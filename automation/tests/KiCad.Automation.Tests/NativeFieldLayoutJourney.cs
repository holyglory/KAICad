using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyCreatedFieldLayout(NativeClient client, DocumentSpecifier root, DesignRecoveryStore store,
        string designPath, IReadOnlyCollection<Guid> createdComponents, string evidence, string instanceId,
        int processId, string display, CancellationToken token)
    {
        var saved = store.Read()!; var baseline = saved.State.Baseline;
        var added = baseline.Engineering.Circuit.Symbols.Where(s => createdComponents.Contains(s.ComponentId)).ToArray();
        var bindings = baseline.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
        var nativeSymbols = SchematicModelProjection.NativeSymbols(baseline, baseline.Schematic);
        var changes = new List<SchematicFieldPlacement>();
        // Fixture-specific arrangement proposal. Text stays visible and retains
        // content/font settings; actual post-commit bounds verify the result.
        foreach (var occurrence in added.DistinctBy(s => bindings[s.Id]))
        {
            var symbol = nativeSymbols[occurrence.Id];
            var owner = baseline.Schematic.Instances.Single(s => s.Metadata.Document.SheetPath.Equals(symbol.Path));
            var measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
            { Document = owner.Metadata.Document.Clone(), ExpectedRevision = new() { Epoch = saved.State.NativeRevision.Epoch,
                Sequence = saved.State.NativeRevision.Sequence } }, token);
            var envelope = measured.Obstacles.Single(o => o.Id.Value == symbol.Id.Value).Bounds;
            long x = ((envelope.Position.XNm + envelope.Size.XNm + 2_540_000 + 99) / 100) * 100;
            long y = symbol.Position.YNm;
            var fields = new List<(SchematicFieldSlot Slot, int? Index, SchematicField Field)>
            {
                (SchematicFieldSlot.Reference, null, symbol.ReferenceField), (SchematicFieldSlot.Value, null, symbol.ValueField),
                (SchematicFieldSlot.Footprint, null, symbol.FootprintField), (SchematicFieldSlot.Datasheet, null, symbol.DatasheetField),
                (SchematicFieldSlot.Description, null, symbol.DescriptionField)
            };
            fields.AddRange(symbol.UserFields.Select((field, index) => (SchematicFieldSlot.User, (int?)index, field)));
            foreach (var (slot, index, field) in fields.Where(f => f.Field is { Visible: true, Text: not null }
                && !string.IsNullOrWhiteSpace(f.Field.Text.Text_)))
            {
                long height = Math.Max(field.Text.Attributes.Size.YNm, 1_270_000);
                long lines = field.Text.Text_.Count(c => c == '\n') + 1;
                changes.Add(new(new(occurrence.Id, slot, field.Name, index), x, y, 0, HorizontalAlignment.HaLeft, VerticalAlignment.VaCenter));
                y += ((height * lines * 2 + 1_270_000 + 99) / 100) * 100;
            }
        }
        Assert.IsNotEmpty(changes);
        var before = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token);
        string beforeXml = await File.ReadAllTextAsync(designPath, token);
        await using var host = await StdioMcpFixture.StartAsync(SyncHarnessProcessTests.StartInfo(),
            Path.Combine(evidence, instanceId + "-fields-host"), Path.Combine(evidence, instanceId + "-fields-host.log"), token);
        RequireToolSuccess(await host.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var args = new { instanceId, recoveryPath = store.StatePath, expectedRevisionToken = saved.RevisionToken, changes = changes.ToArray() };
        var stale = await host.Tool("kicad_design_propose_field_layout", args with { expectedRevisionToken = "stale" });
        Assert.IsTrue(stale.GetProperty("isError").GetBoolean());
        var proposed = await host.Tool("kicad_design_propose_field_layout", args); RequireToolSuccess(proposed);
        Assert.AreEqual(before, await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = root.Clone(), ProcessEpoch = client.Epoch }, token));
        Assert.AreEqual(beforeXml, await File.ReadAllTextAsync(designPath, token));
        var proposal = proposed.GetProperty("structuredContent");
        string desiredXml = proposal.GetProperty("desiredXml").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-fields-proposal.json"), proposal.GetRawText(), token);
        await File.WriteAllTextAsync(designPath, desiredXml, new UTF8Encoding(false), token);
        saved = store.Save(saved.State with { DesiredFileBytes = Encoding.UTF8.GetBytes(desiredXml) }, saved.RevisionToken);
        var operation = Guid.NewGuid();
        var applied = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = store.StatePath,
            designPath, expectedRevisionToken = saved.RevisionToken, operationId = operation.ToString("D") });
        RequireToolSuccess(applied);
        var after = store.Read()!.State.Baseline;
        Assert.AreEqual(EngineeringDesignXml.Write(baseline.Engineering, []), EngineeringDesignXml.Write(after.Engineering, []));
        await CheckRendered();
        await using (var automatic = await AutomaticDesignSynchronization.StartAsync(store, client, designPath, store.Read()!.RevisionToken, token))
        {
            await Watching(after.Schematic);
            await FocusedSchematicShortcut(client, root, processId, display, "z", token);
            await Watching(baseline.Schematic);
            await FocusedSchematicShortcut(client, root, processId, display, "y", token);
            await Watching(after.Schematic);

            async Task Watching(SchematicHierarchyData expected)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(20));
                var status = automatic.Inspect();
                while (status.Phase != AutomaticDesignPhase.Watching
                    || !SamePersisted(expected, store.Read()!.State.Baseline.Schematic))
                {
                    Assert.AreNotEqual(AutomaticDesignPhase.Paused, status.Phase, status.ErrorMessage);
                    status = await automatic.WaitAsync(status.Sequence, wait.Token);
                }
                var actual = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, wait.Token), []);
                var forward = SchematicHierarchyDelta.Plan(expected, actual.Schematic, wait.Token);
                var reverse = SchematicHierarchyDelta.Plan(actual.Schematic, expected, wait.Token);
                if (forward.Count != 0 || reverse.Count != 0)
                {
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-fields-expected.xml"), SchematicDataXml.Write(expected), wait.Token);
                    await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-fields-actual.xml"), SchematicDataXml.Write(actual.Schematic), wait.Token);
                }
                Assert.AreEqual(0, forward.Count + reverse.Count,
                    "Field synchronization changed persisted objects; inspect the retained expected/actual XML, not protobuf enumeration order.");
            }
        }
        var noOp = SchematicFieldLayoutPlanner.Propose(store.Read()!.State, changes, token);
        Assert.IsEmpty(noOp.Operations);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-fields-proof.json"), JsonSerializer.Serialize(new
        { instanceId, operation, fieldCount = changes.Count, publicMcpProposal = true, exactTextAndCircuitPreserved = true,
            renderedFieldBoundsSeparated = true, noPageClipping = true, automaticUndoRedoXml = true, unchangedReplay = true,
            fullReadabilityQualified = false, manualFieldDragVerified = false, crossPlatformReady = false }), token);

        async Task CheckRendered()
        {
            foreach (var sheet in after.Schematic.Instances)
            {
                await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = sheet.Metadata.Document.Clone() }, token);
                var facts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(new()
                    { Document = sheet.Metadata.Document.Clone() }, token);
                var affected = after.SymbolBindings.Where(b => added.Any(s => s.Id == b.SymbolOccurrenceId))
                    .Select(b => b.NativeObjectId.ToString("D")).ToHashSet(StringComparer.Ordinal);
                var fields = facts.Objects.Where(o => o.OwnerId is not null && affected.Contains(o.OwnerId.Value)
                    && o.Visible && o.Text.Length > 0 && o.Kind is SchematicPresentationObject.Types.Kind.Text or SchematicPresentationObject.Types.Kind.ReferenceDesignator).ToArray();
                Assert.IsNotEmpty(fields);
                foreach (var field in fields)
                {
                    Assert.IsTrue(Contains(facts.PageBounds, field.Bounds), field.FieldName + " extends beyond the page.");
                    foreach (var other in fields.Where(o => o.OwnerId.Equals(field.OwnerId) && o.Id.Value != field.Id.Value))
                        Assert.IsFalse(Overlaps(field.Bounds, other.Bounds), "Visible fields overlap: " + field.FieldName + " / " + other.FieldName);
                }
                await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-fields-facts-" + sheet.Metadata.Document.SheetPath.Path[^1].Value + ".json"),
                    SchematicJson.Formatter.Format(facts), token);
                var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = sheet.Metadata.Document.Clone() }, token);
                await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-fields-render-" + sheet.Metadata.Document.SheetPath.Path[^1].Value + ".png"), image.Preview.Png.ToByteArray(), token);
            }
            await client.InvokeAsync<ActivateSchematicSheet, DocumentSpecifier>(new() { Document = root.Clone() }, token);
        }
        static bool Contains(Box2 page, Box2 field) => field.Position.XNm >= page.Position.XNm && field.Position.YNm >= page.Position.YNm
            && field.Position.XNm + field.Size.XNm <= page.Position.XNm + page.Size.XNm && field.Position.YNm + field.Size.YNm <= page.Position.YNm + page.Size.YNm;
        static bool Overlaps(Box2 a, Box2 b) => a.Position.XNm < b.Position.XNm + b.Size.XNm && b.Position.XNm < a.Position.XNm + a.Size.XNm
            && a.Position.YNm < b.Position.YNm + b.Size.YNm && b.Position.YNm < a.Position.YNm + a.Size.YNm;
        bool SamePersisted(SchematicHierarchyData expected, SchematicHierarchyData actual) =>
            SchematicHierarchyDelta.Plan(expected, actual, token).Count == 0 && SchematicHierarchyDelta.Plan(actual, expected, token).Count == 0;
    }
}
