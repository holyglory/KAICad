using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Model = KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyManualMoveRoundTrip(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, int processId, string display, string evidence, string instanceId,
        CancellationToken token)
    {
        var header = new ItemHeader { Document = document };
        async Task<SchematicScreenDataSnapshot> Snapshot(CancellationToken local) =>
            await client.InvokeAsync<ReadSchematicScreenData, SchematicScreenDataSnapshot>(new() { Document = document }, local);
        async Task<SchematicNetlistResponse> Nets() =>
            await client.InvokeAsync<GetSchematicNetlist, SchematicNetlistResponse>(new() { Document = document }, token);
        string NetOf(SchematicNetlistResponse nets, string pin) =>
            nets.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == pin)).Name;
        string[] Topology(SchematicNetlistResponse nets) => nets.Nets.SelectMany(n => n.Sheets
            .SelectMany(s => s.Items.Select(id => n.Name + "\n" + SchematicJson.Formatter.Format(s.Path) + "\n" + id.Value)))
            .Order(StringComparer.Ordinal).ToArray();
        SchematicSymbolInstance Symbol(SchematicScreenDataSnapshot snapshot) => snapshot.Data.Items
            .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .Single(i => i.Id.Value == fixture.Symbol);

        var before = await Snapshot(token);
        var netsBefore = await Nets();
        var electricalBefore = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document }, token);
        var model = ProbeElectricalModel(electricalBefore);
        var requirement = new Model.EngineeringStatement(Guid.NewGuid(), model.Engineering.Circuit.Nets.Single().Id,
            Model.EngineeringStatementRole.Intent, Model.GuidanceStrength.Requirement,
            "Keep the two fixture probes electrically connected.", null, [], []);
        model = model with { Engineering = model.Engineering with
            { Structure = model.Engineering.Structure with { Statements = [requirement] } } };
        var recovery = new DesignRecoveryStore(Path.Combine(evidence, instanceId + "-manual-electrical-recovery.json"));
        var recoveryState = recovery.Save(new(Guid.NewGuid(), Guid.Parse(instanceId),
            new(electricalBefore.Hierarchy.Revision.Epoch, electricalBefore.Hierarchy.Revision.Sequence), electricalBefore.Hierarchy.TrackingComplete,
            model, [0xff], electricalBefore.Hierarchy.Data.Clone(), []), null);
        var initialized = await DesignRecoveryInspector.InitializeElectricalBaselineAsync(recovery, client, recoveryState.RevisionToken, token);
        await using var intake = await DesignNativeIntakeSession.CreateAsync(recovery, client, token);
        ulong intakeSequence = 0;
        // Attach the event stream before moving, so the first stream-recovery
        // capture is not deliberately raced against an uncommitted GUI edit.
        DesignNativeIntakeStatus attached;
        do
        {
            attached = await intake.WaitAsync(intakeSequence, token); intakeSequence = attached.Sequence;
            Assert.AreEqual(DesignNativeIntakePhase.Watching, attached.Phase, attached.ErrorMessage);
        } while (attached.Observation?.Delivery is null);
        async Task ObserveSaved(ulong revision)
        {
            while (recovery.Read()!.State.NativeRevision.Sequence < revision)
            {
                var status = await intake.WaitAsync(intakeSequence, token); intakeSequence = status.Sequence;
                Assert.AreEqual(DesignNativeIntakePhase.Watching, status.Phase, status.ErrorMessage);
            }
            Assert.AreEqual(initialized.State.BaselineElectrical, recovery.Read()!.State.BaselineElectrical);
            CollectionAssert.AreEqual(new byte[] { 0xff }, recovery.Read()!.State.DesiredFileBytes);
        }
        async Task VerifyNetPlan(bool split)
        {
            var saved = recovery.Read()!;
            // The separate planning input has valid desired XML. The live
            // intake fixture intentionally retains invalid bytes for recovery.
            var input = saved.State with { DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(
                SchematicDesignXml.Write(model, [])) };
            var plan = SchematicNetReconciliation.Plan(input, token);
            Assert.IsNotNull(plan.Candidate, plan.ErrorCode + ": " + plan.ErrorMessage);
            Assert.IsEmpty(plan.Conflicts);
            Assert.IsTrue(SchematicElectricalComparison.Compare(model with { Engineering = plan.Candidate },
                saved.State.ObservedElectrical!, []).ConnectivityEquivalent);
            var preserved = plan.Candidate.Structure.Statements.Single(s => s.Id == requirement.Id);
            Assert.AreEqual(requirement.Text, preserved.Text);
            Assert.AreEqual(requirement.TargetId, preserved.TargetId);
            if (split)
            {
                Assert.AreEqual(2, plan.Candidate.Circuit.Nets.Count);
                Assert.AreEqual(Model.NetBindingChangeKind.Split, plan.NetChanges.Single().Change);
                var unresolved = plan.Candidate.Structure.UnresolvedNetBindings!.Single();
                Assert.AreEqual(requirement.Id, unresolved.OwnerId);
                Assert.AreEqual(requirement.TargetId, unresolved.FormerNetId);
                Assert.AreEqual(2, unresolved.CandidateNetIds.Count);
            }
            else
            {
                Assert.IsEmpty(plan.NetChanges);
                Assert.IsFalse(plan.Candidate.Structure.HasUnresolvedNetBindings);
                Assert.AreEqual(Model.EngineeringDesignXml.Write(model.Engineering, []),
                    Model.EngineeringDesignXml.Write(plan.Candidate, []));
            }
            Assert.AreEqual(saved.RevisionToken, recovery.Read()!.RevisionToken);
            CollectionAssert.AreEqual(new byte[] { 0xff }, recovery.Read()!.State.DesiredFileBytes);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + (split ? "-manual-split-plan.xml" : "-manual-undo-plan.xml")),
                Model.EngineeringDesignXml.Write(plan.Candidate, []), token);
            var combined = SchematicSynchronizationPlanner.Plan(input, token);
            Assert.IsTrue(combined.CanPrepare, combined.ErrorCode + ": " + combined.ErrorMessage);
            Assert.IsEmpty(combined.NativeOperations, "A pure native edit already exists in the observed schematic.");
            Assert.IsTrue(combined.ObservedConnectivity!.ConnectivityEquivalent);
            Assert.IsFalse(combined.NativeConnectivityValidationRequired);
            Assert.IsNotNull(combined.CandidateXml);
            var fullDesign = SchematicDesignXml.Read(combined.CandidateXml, []);
            Assert.AreEqual(saved.State.Observed, fullDesign.Schematic);
            Assert.AreEqual(requirement.Text, fullDesign.Engineering.Structure.Statements.Single(s => s.Id == requirement.Id).Text);
            Assert.AreEqual(split, fullDesign.Engineering.Structure.HasUnresolvedNetBindings);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + (split ? "-manual-split-design.xml" : "-manual-undo-design.xml")),
                combined.CandidateXml, token);
            Assert.AreEqual(saved.RevisionToken, recovery.Read()!.RevisionToken);
            CollectionAssert.AreEqual(new byte[] { 0xff }, recovery.Read()!.State.DesiredFileBytes);
        }
        Assert.AreEqual(NetOf(netsBefore, fixture.PinA), NetOf(netsBefore, fixture.PinB));
        var select = new AddToSelection { Header = header };
        select.Items.Add(new KIID { Value = fixture.Symbol });
        await client.InvokeAsync<AddToSelection, SelectionResponse>(select, token);
        NativeKeyboard.SchematicShortcut(display, processId, "m", controlKey: false, focusCanvas: false);
        try
        {
            using var begin = CancellationTokenSource.CreateLinkedTokenSource(token);
            begin.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try { await Snapshot(begin.Token); }
                catch (NativeApiException error) when (error.Status == 7)
                { StringAssert.Contains(error.Message, "current schematic edit"); break; }
                await Task.Delay(100, begin.Token);
            }
            // Commit through a real canvas click. M is deliberately a move,
            // not a connected drag: this electrical change must be observable.
            int pointerX = 620, pointerY = 460;
            // XSync confirms X-server delivery, not that the editor processed
            // motion. This legacy raw item query is only an input-progress
            // probe; it is deliberately NOT a matched committed observation.
            var query = new GetItemsById { Header = header };
            query.Items.Add(new KIID { Value = fixture.Symbol });
            using var motion = CancellationTokenSource.CreateLinkedTokenSource(token);
            motion.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                // SetCursorPosition deliberately suppresses the next warp
                // motion. Send a short physical path, as a mouse actually
                // produces, rather than one teleport followed by a click.
                NativeKeyboard.SchematicShortcut(display, processId, "motion", controlKey: false,
                    clickFromLeft: pointerX, clickFromTop: pointerY);
                await Task.Delay(100, motion.Token);
                var live = (await client.InvokeAsync<GetItemsById, GetItemsResponse>(query, motion.Token))
                    .Items.Single().Unpack<SchematicSymbolInstance>();
                if (!live.Position.Equals(Symbol(before).Position)) break;
                pointerX = pointerX == 640 ? 620 : pointerX + 10;
                pointerY = pointerY == 480 ? 460 : pointerY + 10;
            }
            NativeKeyboard.SchematicShortcut(display, processId, "click", controlKey: false,
                clickFromLeft: pointerX, clickFromTop: pointerY);
            var moved = await Ready();
            Assert.AreNotEqual(Symbol(before).Position, Symbol(moved).Position);
            Assert.AreEqual(before.Revision.Epoch, moved.Revision.Epoch);
            Assert.IsTrue(moved.Revision.Sequence > before.Revision.Sequence);
            var netsAfter = await Nets();
            Assert.AreNotEqual(NetOf(netsAfter, fixture.PinA), NetOf(netsAfter, fixture.PinB),
                "Moving the symbol away from its stationary wire must expose the resulting disconnection.");
            var electrical = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new()
                { Document = document, ExpectedRevision = moved.Revision }, token);
            Assert.AreEqual(moved.Revision, electrical.Hierarchy.Revision);
            Assert.AreEqual(moved.Data, electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document)));
            bool Connected(SchematicElectricalState state) => state.Nets.Any(n =>
                n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinA)
                && n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinB));
            Assert.IsFalse(Connected(electrical), "The same-revision electrical read must include the real manual disconnection.");
            await ObserveSaved(electrical.Hierarchy.Revision.Sequence);
            Assert.AreEqual(electrical, recovery.Read()!.State.ObservedElectrical);
            var difference = SchematicElectricalComparison.Compare(model, electrical, []);
            Assert.IsTrue(difference.PinBindingsComplete, string.Join(',', difference.Issues.Select(i => i.Code)));
            Assert.IsFalse(difference.ConnectivityEquivalent);
            Assert.IsTrue(difference.Differences.Any(d => d.Kind == "model_net_split"));
            await VerifyNetPlan(split: true);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-electrical-state.json"),
                SchematicJson.Formatter.Format(electrical), token);
            var journal = await client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new()
            {
                Document = document, DocumentEpoch = before.Revision.Epoch, AfterSequence = before.Revision.Sequence
            }, token);
            Assert.IsTrue(journal.Changes.Any(c => c.Kind == SchematicChange.Types.Kind.Commit));
            Assert.IsTrue(journal.Changes.All(c => c.OriginId.Length == 0 && c.OperationId.Length == 0),
                "A manual edit must not inherit a preceding automation operation's attribution.");
            string xml = SchematicDataXml.Write(moved.Data);
            Assert.AreEqual(moved.Data, SchematicDataXml.Read(xml));
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-move.xml"), xml, token);
            await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-manual-move-netlist.json"),
                SchematicJson.Formatter.Format(netsAfter), token);
            var preview = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
            await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-manual-move.png"), preview.Png.ToByteArray(), token);

            NativeKeyboard.SchematicShortcut(display, processId, "z");
            using var undo = CancellationTokenSource.CreateLinkedTokenSource(token);
            undo.CancelAfter(TimeSpan.FromSeconds(5));
            SchematicScreenDataSnapshot restored;
            do
            {
                restored = await Snapshot(undo.Token);
                if (restored.Revision.Sequence == moved.Revision.Sequence) await Task.Delay(100, undo.Token);
            } while (restored.Revision.Sequence == moved.Revision.Sequence);
            Assert.AreEqual(before.Data, restored.Data, "Native undo must restore the manually moved symbol and all supported data.");
            CollectionAssert.AreEqual(Topology(netsBefore), Topology(await Nets()));
            var restoredElectrical = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new()
                { Document = document, ExpectedRevision = restored.Revision }, token);
            Assert.AreEqual(restored.Revision, restoredElectrical.Hierarchy.Revision);
            Assert.IsTrue(Connected(restoredElectrical));
            await ObserveSaved(restoredElectrical.Hierarchy.Revision.Sequence);
            Assert.AreEqual(restoredElectrical, recovery.Read()!.State.ObservedElectrical);
            Assert.IsTrue(SchematicElectricalComparison.Compare(model, restoredElectrical, []).ConnectivityEquivalent);
            await VerifyNetPlan(split: false);
        }
        catch
        {
            try { await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, instanceId + "-manual-move-failed.png"), CancellationToken.None); }
            catch (Exception error) { Console.Error.WriteLine("Manual-move failure capture unavailable: " + error.Message); }
            throw;
        }
        finally
        {
            NativeKeyboard.SchematicShortcut(display, processId, "Escape", controlKey: false, focusCanvas: false);
        }
        await client.InvokeAsync<ClearSelection, Empty>(new() { Header = header }, token);

        async Task<SchematicScreenDataSnapshot> Ready()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try
                {
                    var snapshot = await Snapshot(deadline.Token);
                    if (snapshot.Revision.Sequence != before.Revision.Sequence) return snapshot;
                }
                catch (NativeApiException error) when (error.Status == 7) { }
                await Task.Delay(100, deadline.Token);
            }
        }
    }
}
