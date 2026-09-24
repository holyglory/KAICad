using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifySynchronizationExecution(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token, bool transformsOnly = false)
    {
        if (!transformsOnly)
            await VerifyConnectivityAssertion(client, document, processId, display, evidence, instanceId, token);
        string designPath = Path.Combine(evidence, instanceId + "-sync-design.xml");
        string recordPath = Path.Combine(evidence, instanceId + "-sync-recovery.json");
        var store = new DesignRecoveryStore(recordPath);
        async Task<CheckedSchematicState> Capture() => await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        var initial = await Capture(); var model = ProbeElectricalModel(initial.Electrical);
        byte[] original = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(model, []));
        await File.WriteAllBytesAsync(designPath, original, token);
        var saved = store.Save(new(Guid.NewGuid(), Guid.Parse(instanceId),
            new(initial.State.Revision.Epoch, initial.State.Revision.Sequence), initial.Electrical.Hierarchy.TrackingComplete,
            model, original, initial.Electrical.Hierarchy.Data.Clone(), [],
            BaselineElectrical: initial.Electrical.Clone(), ObservedElectrical: initial.Electrical.Clone()), null);

        Console.WriteLine($"Native synchronization {instanceId}: establish a known synchronized baseline");
        var initialized = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        Assert.IsTrue(initialized.SynchronizationCommitted); Assert.IsFalse(initialized.NativeMutationCommitted);
        saved = store.Read()!;
        Console.WriteLine($"Native synchronization {instanceId}: unchanged reapplication");
        var noOp = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        Assert.IsTrue(noOp.SynchronizationCommitted); Assert.IsFalse(noOp.NativeMutationCommitted);
        Assert.AreEqual(saved.RevisionToken, noOp.RecoveryRevisionToken);

        if (transformsOnly)
        {
            await VerifySynchronizationLayout(client, document, store, designPath, processId, display, evidence, instanceId, token, transforms: true);
            await VerifySynchronizationLocks(client, document, store, designPath, processId, display, evidence, instanceId, token);
            return;
        }

        Console.WriteLine($"Native synchronization {instanceId}: native edit to XML");
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
            { Document = document.Clone(), TitleBlock = new() { Title = "Native edit synchronized to XML" } }, token);
        saved = await DesignRecoveryInspector.RefreshAsync(store, client, saved.RevisionToken, token, includeElectrical: true);
        Guid reverseOperationId = Guid.NewGuid(); string reverseRequestToken = saved.RevisionToken;
        var reverse = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, reverseOperationId, token);
        Assert.IsTrue(reverse.SynchronizationCommitted); Assert.IsFalse(reverse.NativeMutationCommitted);
        Assert.IsTrue(reverse.NativeFilesSaved);
        Assert.IsNotNull(reverse.PreviousXmlPath);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(reverse.PreviousXmlPath, token));
        await RequireAgreement("Native edit synchronized to XML");

        foreach (var (descriptor, title) in new[]
        {
            (CheckedSchematicBatch.Descriptor, "Recovered lost edit reply"),
            (CheckedSaveDocument.Descriptor, "Recovered lost save reply")
        })
        {
            Console.WriteLine($"Native synchronization {instanceId}: {title}");
            saved = store.Read()!;
            var desired = saved.State.Baseline with { Schematic = saved.State.Baseline.Schematic.Clone() };
            desired.Schematic.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.TitleBlock.Title = title;
            byte[] bytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, []));
            await File.WriteAllBytesAsync(designPath, bytes, token);
            saved = store.Save(saved.State with { DesiredFileBytes = bytes }, saved.RevisionToken);
            var drop = new LostSynchronizationReply(descriptor);
            Guid operationId = Guid.NewGuid();
            var interruptedClient = new NativeClient(drop, client.Endpoint, client.Epoch);
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, interruptedClient, designPath, saved.RevisionToken, operationId, token));
            Assert.IsTrue(drop.Dropped, "The failure must occur only after the real KiCad response was received.");
            var pending = new DesignRecoveryStore(recordPath).Read()!;
            Assert.IsNotNull(pending.State.PendingPublication); Assert.IsNotNull(pending.State.PendingMutation);
            string nativeOperation = pending.State.PendingMutation.OperationId;
            Guid publicationOperation = pending.State.PendingPublication.OperationId;
            await File.WriteAllBytesAsync(designPath, "<newer-unobserved-xml/>"u8.ToArray(), token);
            var auditing = new SynchronizationRequestAudit();
            var auditClient = new NativeClient(auditing, client.Endpoint, client.Epoch);
            var changedXml = await Assert.ThrowsExactlyAsync<KiCad.Automation.Model.AutomationException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, auditClient, designPath, saved.RevisionToken, operationId, token));
            Assert.AreEqual("publication_target_changed", changedXml.Code);
            Assert.AreEqual(0, auditing.Mutations, "File-intake lag cannot authorize replaying a native mutation against newer XML.");
            Assert.AreEqual(pending.RevisionToken, store.Read()!.RevisionToken);
            await File.WriteAllBytesAsync(designPath, bytes, token);
            var wrongPath = await Assert.ThrowsExactlyAsync<KiCad.Automation.Model.AutomationException>(() =>
                SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath + ".other.xml", saved.RevisionToken, operationId, token));
            Assert.AreEqual("publication_target_mismatch", wrongPath.Code);
            var completed = await SchematicSynchronizationExecutor.ApplyAsync(new(recordPath), client, designPath, saved.RevisionToken, operationId, token);
            Assert.IsTrue(completed.SynchronizationCommitted); Assert.IsTrue(completed.NativeMutationCommitted);
            Assert.AreEqual(nativeOperation, completed.NativeReceipt!.OperationId);
            Assert.AreEqual(publicationOperation, completed.PublicationId);
            Assert.IsFalse(store.Read()!.State.HasPendingWork);
            await RequireAgreement(title);
            var replay = await SchematicSynchronizationExecutor.ApplyAsync(new(recordPath), client, designPath, saved.RevisionToken, operationId, token);
            Assert.IsTrue(replay.Replayed); Assert.AreEqual(completed.NativeRevision, replay.NativeRevision);
            Assert.AreEqual(completed.NativeReceipt, replay.NativeReceipt);

            byte[] recoveryBefore = await File.ReadAllBytesAsync(recordPath, token);
            DateTime xmlTime = File.GetLastWriteTimeUtc(designPath);
            var repeated = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, completed.RecoveryRevisionToken, Guid.NewGuid(), token);
            Assert.IsFalse(repeated.NativeMutationCommitted); Assert.IsFalse(repeated.NativeFilesSaved);
            Assert.AreEqual(completed.RecoveryRevisionToken, repeated.RecoveryRevisionToken);
            CollectionAssert.AreEqual(recoveryBefore, await File.ReadAllBytesAsync(recordPath, token));
            Assert.AreEqual(xmlTime, File.GetLastWriteTimeUtc(designPath));
        }

        Console.WriteLine($"Native synchronization {instanceId}: rendered keyboard undo to XML");
        await FocusedSchematicShortcut(client, document, processId, display, "z", token);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while ((await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, timeout.Token)).Title
                != "Recovered lost edit reply") await Task.Delay(50, timeout.Token);
        }
        saved = await DesignRecoveryInspector.RefreshAsync(store, client, store.Read()!.RevisionToken, token, includeElectrical: true);
        var undo = await SchematicSynchronizationExecutor.ApplyAsync(store, client, designPath, saved.RevisionToken, Guid.NewGuid(), token);
        Assert.IsFalse(undo.NativeMutationCommitted); await RequireAgreement("Recovered lost edit reply");
        byte[] beforeHistoricalReplay = await File.ReadAllBytesAsync(recordPath, token);
        var historicalAudit = new SynchronizationRequestAudit();
        var historical = await SchematicSynchronizationExecutor.ApplyAsync(store,
            new NativeClient(historicalAudit, client.Endpoint, client.Epoch), designPath, reverseRequestToken, reverseOperationId, token);
        Assert.IsTrue(historical.Replayed); Assert.AreEqual(reverse.NativeRevision, historical.NativeRevision);
        Assert.AreEqual(0, historicalAudit.Requests, "Historical completion lookup must not contact or edit KiCad.");
        CollectionAssert.AreEqual(beforeHistoricalReplay, await File.ReadAllBytesAsync(recordPath, token));
        await VerifySynchronizationServiceRestart(client, document, store, designPath, evidence, instanceId, token);
        await VerifySynchronizationProperties(client, document, store, designPath, processId, display, evidence, instanceId, token);
        await VerifySynchronizationLayout(client, document, store, designPath, processId, display, evidence, instanceId, token);
        var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
        Assert.AreEqual(image.Snapshot.Revision, image.Preview.Revision);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-sync-execution.png"), image.Preview.Png.ToByteArray(), token);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-sync-execution-result.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeProjects = 1, xmlAndNativeAgreement = true, lostEditReplyRecovered = true,
            lostSaveReplyRecovered = true, keyboardUndoSynchronized = true, unchangedReapplication = true,
            historicalReceiptReplayed = true, unobservedXmlChangeBlockedNativeReplay = true,
            highLevelMcpQualified = false, automaticEventLoopQualified = false, crossPlatformReady = false
        }), token);

        async Task RequireAgreement(string title)
        {
            var actual = await Capture();
            var xml = SchematicDesignXml.Read(await File.ReadAllTextAsync(designPath, token), []);
            Assert.AreEqual(title, xml.Schematic.Instances.Single(s => s.Metadata.Document.Equals(document)).Metadata.TitleBlock.Title);
            Assert.AreEqual(0, SchematicHierarchyDelta.Plan(actual.Electrical.Hierarchy.Data, xml.Schematic, token).Count);
            var connectivity = SchematicElectricalComparison.Compare(xml, actual.Electrical, [], token);
            Assert.IsTrue(connectivity.PinBindingsComplete); Assert.IsTrue(connectivity.ConnectivityEquivalent);
        }
    }

    // CN-1 §8 through the real MCP STDIO tool: one checked batch creates probe symbols and label
    // stubs and asserts the exact pin partition that must result. A wrong result commits nothing
    // (no revision, journal entry or undo step); the right one is one rendered undo step.
    private static async Task VerifyConnectivityAssertion(NativeClient client, DocumentSpecifier document,
        int processId, string display, string evidence, string instanceId, CancellationToken token)
    {
        Console.WriteLine($"Native synchronization {instanceId}: asserted connectivity through MCP");
        await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-assertion-mcp"),
            Path.Combine(evidence, instanceId + "-assertion-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        async Task<CheckedSchematicState> Observe()
        {
            var observed = await mcp.Tool("kicad_schematic_checked_state", new { instanceId, documentJson = SchematicJson.Formatter.Format(document) });
            RequireToolSuccess(observed);
            return SchematicJson.Parser.Parse<CheckedSchematicState>(observed.GetProperty("structuredContent").GetRawText());
        }
        async Task<(CheckedSchematicBatchReceipt Receipt, bool IsError)> Apply(CheckedSchematicBatch request)
        {
            var applied = await mcp.Tool("kicad_schematic_apply_checked_batch", new { instanceId, requestJson = SchematicJson.Formatter.Format(request) });
            var data = applied.GetProperty("structuredContent");
            Assert.IsTrue(data.TryGetProperty("receipt", out var receipt), applied.GetRawText());
            return (SchematicJson.Parser.Parse<CheckedSchematicBatchReceipt>(receipt.GetRawText()),
                applied.TryGetProperty("isError", out var error) && error.GetBoolean());
        }
        Task<SchematicChangeJournal> Journal() => client.InvokeAsync<ReadSchematicChangeJournal, SchematicChangeJournal>(new() { Document = document }, token);
        Task<DocumentLifecycleState> Lifecycle() => client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, token);
        async Task History(string key)
        {
            var before = (await Lifecycle()).Revision;
            await FocusedSchematicShortcut(client, document, processId, display, key, token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, limit.Token)).Revision.Equals(before))
                await Task.Delay(50, limit.Token);
        }
        string rootPath = "/" + string.Join('/', document.SheetPath.Path.Select(id => id.Value));
        string Key(string pin) => rootPath + "#" + pin;
        // Native pin groups exactly as the assertion measures them: placed pins per loaded sheet
        // instance, joined by native nets, every other pin on its own.
        static IReadOnlyList<SortedSet<string>> Groups(CheckedSchematicState state)
        {
            var placed = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var screen in state.Electrical.Hierarchy.Data.Instances)
            {
                string path = "/" + string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(id => id.Value));
                foreach (var symbol in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()))
                foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
                {
                    if ((child.Unit?.Unit is > 0 && child.Unit.Unit != symbol.Unit.Unit)
                        || (child.BodyStyle?.Style is > 0 && child.BodyStyle.Style != (symbol.BodyStyle?.Style ?? 1))) continue;
                    var pin = child.Item.Unpack<SchematicPin>();
                    if (pin.LibraryPinId is not null) placed.Add(path + "#" + pin.Id.Value);
                }
            }
            var groups = new List<SortedSet<string>>(); var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var net in state.Electrical.Nets)
            {
                var members = new SortedSet<string>(net.Sheets.SelectMany(sheet => sheet.Items.Select(id =>
                    "/" + string.Join('/', sheet.Path.Path.Select(p => p.Value)) + "#" + id.Value)).Where(placed.Contains), StringComparer.Ordinal);
                if (members.Count == 0) continue;
                foreach (string pin in members) Assert.IsTrue(assigned.Add(pin), "A native pin belongs to exactly one net.");
                groups.Add(members);
            }
            groups.AddRange(placed.Where(pin => !assigned.Contains(pin)).Select(pin => new SortedSet<string>([pin], StringComparer.Ordinal)));
            return groups.OrderBy(g => g.First(), StringComparer.Ordinal).ToArray();
        }
        static string[] Canonical(IEnumerable<SortedSet<string>> groups) =>
            groups.Select(g => string.Join(',', g)).Order(StringComparer.Ordinal).ToArray();

        var titleBefore = await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token);
        // A marker edit on top of the undo history: a refused batch must leave it on top.
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
            { Document = document, TitleBlock = new() { Title = "Connectivity assertion marker" } }, token);
        var marked = await Observe();
        var rootScreen = marked.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document));
        var template = rootScreen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
            .OrderBy(s => s.Position.XNm).First();
        string templatePin = template.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).Select(c => c.Item.Unpack<SchematicPin>())
            .Single(p => p.LibraryPinId is not null).Id.Value;
        string existingNet = rootScreen.Items.Where(i => i.Is(LocalLabel.Descriptor)).Select(i => i.Unpack<LocalLabel>()).Single().Text.Text_;
        var baseline = Groups(marked);
        var joined = baseline.Single(g => g.Contains(Key(templatePin)));
        Assert.IsGreaterThan(1, joined.Count, "The fixture's labelled net must already join native pins.");

        KIID Id() => new() { Value = Guid.NewGuid().ToString("D") };
        Vector2 Point(long x, long y) => new() { XNm = x, YNm = y };
        Text Label(string value) => new() { Text_ = value, Attributes = new() { Size = Point(1270000, 1270000), Multiline = false } };
        var probes = new List<(SchematicSymbolInstance Symbol, string Pin)>();
        foreach (var (x, reference) in new[] { (60000000L, "TP701"), (90000000L, "TP702"), (120000000L, "TP703") })
        {
            var probe = template.Clone(); probe.Id = Id(); probe.Position = Point(x, 140000000);
            probe.Path = document.SheetPath.Clone(); probe.Variants = new(); probe.InstanceRecords = new();
            probe.ReferenceField.Text.Text_ = reference;
            probe.ReferenceField.Text.Position = Point(x + 3000000, 138000000);
            probe.ValueField.Text.Position = Point(x + 3000000, 142000000);
            var record = new SymbolSheetRecord { ProjectName = document.Project.Name, Reference = reference, Unit = probe.Unit.Unit, Variants = new() };
            record.Path.Add(document.SheetPath.Path.Select(id => id.Clone()));
            probe.InstanceRecords.Records.Add(record);
            string pinId = "";
            foreach (var child in probe.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                if (pin.LibraryPinId is null) continue;
                pin.Id = Id(); child.Item = Any.Pack(pin); pinId = pin.Id.Value;
            }
            Assert.AreNotEqual("", pinId);
            probes.Add((probe, pinId));
        }
        string newNet = "CN1_ASSERTED_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        // Label stubs placed exactly on each probe's pin anchor (its position): the first probe
        // joins the existing labelled net, the other two form a new net.
        var labels = new[] { (Probe: 0, Text: existingNet), (Probe: 1, Text: newNet), (Probe: 2, Text: newNet) }
            .Select(stub => new LocalLabel { Id = Id(), Position = probes[stub.Probe].Symbol.Position.Clone(), Text = Label(stub.Text),
                SpinStyle = SchematicLabelSpinStyle.SlssRight, Locked = LockedState.LsUnlocked }).ToArray();
        // Each expected pin is its own loaded sheet-instance path plus its placed pin UUID.
        SchematicNetChainPinAnchor Anchor(string key)
        {
            var anchor = new SchematicNetChainPinAnchor { Path = new(), Pin = new() { Value = key[(key.IndexOf('#') + 1)..] } };
            anchor.Path.Path.Add(key[..key.IndexOf('#')].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(id => new KIID { Value = id }));
            return anchor;
        }
        var assertion = new SchematicConnectivityAssertion { Version = 1 };
        var first = new SchematicPinGroup(); first.Pins.Add(joined.Select(Anchor));
        first.Pins.Add(Anchor(Key(probes[0].Pin)));
        var second = new SchematicPinGroup(); second.Pins.Add(Anchor(Key(probes[2].Pin))); second.Pins.Add(Anchor(Key(probes[1].Pin)));
        assertion.ExpectedGroups.Add(second); assertion.ExpectedGroups.Add(first);
        CheckedSchematicBatch Request(CheckedSchematicState state, IEnumerable<LocalLabel> stubs, bool assertionFirst = false)
        {
            var batch = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = state.State.Revision.Epoch,
                ExpectedRevision = state.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Apply XML connections" };
            var operations = probes.Select(p => new SchematicItemOperation { Create = Any.Pack(p.Symbol) })
                .Concat(stubs.Select(label => new SchematicItemOperation { Create = Any.Pack(label) })).ToList();
            var check = new SchematicItemOperation { AssertConnectivity = assertion.Clone() };
            if (assertionFirst) operations.Insert(0, check); else operations.Add(check);
            batch.Operations.Add(operations);
            return new() { Batch = batch, ExpectedState = state.State.Clone() };
        }
        var journal = await Journal();

        // Fail closed: a misplaced assertion is refused before any operation is applied.
        var (misplaced, misplacedError) = await Apply(Request(marked, labels, assertionFirst: true));
        Assert.IsTrue(misplacedError);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, misplaced.Status);
        Assert.AreEqual("native_batch_rejected", misplaced.ErrorCode);
        StringAssert.StartsWith(misplaced.ErrorMessage, "Atomic operation 0 rejected: A connectivity assertion must be the last");
        Assert.AreEqual(marked, await Observe());

        // Must-catch: the last probe has no stub, so the asserted new net would be split.
        var (split, splitError) = await Apply(Request(marked, labels.Take(2)));
        Assert.IsTrue(splitError, "A refused post-condition is not success.");
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, split.Status, split.ErrorMessage);
        Assert.AreEqual(SchematicConnectionErrors.ConnectivityPostconditionFailed, split.ErrorCode);
        StringAssert.StartsWith(split.ErrorMessage, "connectivity_postcondition_failed: expected=2 mismatches=3 first=unexpected_split:");
        Assert.IsTrue(split.ErrorMessage.Contains(Key(probes[1].Pin), StringComparison.Ordinal)
            || split.ErrorMessage.Contains(Key(probes[2].Pin), StringComparison.Ordinal), split.ErrorMessage);
        Assert.IsLessThanOrEqualTo(2048, split.ErrorMessage.Length);
        Assert.IsNull(split.Result);
        Assert.AreEqual(marked.State, split.ObservedBefore); Assert.AreEqual(marked.State, split.ObservedAfter);
        Assert.AreEqual(marked, await Observe(), "A refused realization leaves every native object, revision and digest unchanged.");
        var afterRefusal = await Journal();
        Assert.AreEqual(journal.DocumentEpoch, afterRefusal.DocumentEpoch);
        Assert.AreEqual(journal.Sequence, afterRefusal.Sequence, "A refused realization records no journal entry.");
        // The marker is still the newest undo step: the refusal added none.
        await History("z");
        Assert.AreEqual(titleBefore.Title, (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token)).Title);
        await History("y");
        Assert.AreEqual("Connectivity assertion marker", (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token)).Title);

        // The exact result commits once, with the native proof, as one undo step.
        var ready = await Observe();
        CollectionAssert.AreEqual(Canonical(baseline), Canonical(Groups(ready)));
        var passingRequest = Request(ready, labels);
        var (passed, passedError) = await Apply(passingRequest);
        Assert.IsFalse(passedError, passed.ErrorMessage);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, passed.Status, passed.ErrorMessage);
        Assert.IsTrue(passed.Result.ConnectivityAssertionVerified);
        Assert.IsGreaterThan(ready.State.Revision.Sequence, passed.Result.Revision.Sequence);
        var realized = await Observe();
        Assert.AreEqual(passed.ObservedAfter, realized.State);
        var expectedGroups = baseline.Where(g => !g.Contains(Key(templatePin)))
            .Append(new SortedSet<string>(joined.Append(Key(probes[0].Pin)), StringComparer.Ordinal))
            .Append(new SortedSet<string>([Key(probes[1].Pin), Key(probes[2].Pin)], StringComparer.Ordinal));
        CollectionAssert.AreEqual(Canonical(expectedGroups), Canonical(Groups(realized)));
        var realizedScreen = realized.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document));
        foreach (var id in probes.Select(p => p.Symbol.Id.Value).Concat(labels.Select(l => l.Id.Value)))
            Assert.IsTrue(realizedScreen.Items.Any(item => ItemId(item) == id), "Created item " + id + " must be on the root screen.");
        var image = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-connectivity-assertion.png"), image.Preview.Png.ToByteArray(), token);

        await History("z");
        var undone = await Observe();
        Assert.IsTrue(ready.Electrical.Hierarchy.Data.Equals(undone.Electrical.Hierarchy.Data),
            "One native undo removes every created symbol and label stub of the realization.");
        CollectionAssert.AreEqual(Canonical(baseline), Canonical(Groups(undone)));
        Assert.AreEqual("Connectivity assertion marker", (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token)).Title);
        await History("y");
        var redone = await Observe();
        Assert.IsTrue(realized.Electrical.Hierarchy.Data.Equals(redone.Electrical.Hierarchy.Data), "Redo restores the whole realization.");
        CollectionAssert.AreEqual(Canonical(expectedGroups), Canonical(Groups(redone)));
        await History("z"); await History("z");
        Assert.AreEqual(titleBefore.Title, (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token)).Title);
        CollectionAssert.AreEqual(Canonical(baseline), Canonical(Groups(await Observe())));

        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-connectivity-assertion.json"), JsonSerializer.Serialize(new
        {
            instanceId, request = JsonDocument.Parse(SchematicJson.Formatter.Format(passingRequest)).RootElement,
            refused = JsonDocument.Parse(SchematicJson.Formatter.Format(split)).RootElement,
            misplaced = JsonDocument.Parse(SchematicJson.Formatter.Format(misplaced)).RootElement,
            completed = JsonDocument.Parse(SchematicJson.Formatter.Format(passed)).RootElement,
            before = Canonical(baseline), after = Canonical(Groups(realized)),
            refusalLeftNoRevisionJournalOrUndo = true, oneUndoStep = true, capabilityAdvertised = false
        }), token);

        static string ItemId(Any item) => item.Is(SchematicSymbolInstance.Descriptor) ? item.Unpack<SchematicSymbolInstance>().Id.Value
            : item.Is(LocalLabel.Descriptor) ? item.Unpack<LocalLabel>().Id.Value : "";
    }

    private sealed class LostSynchronizationReply(MessageDescriptor descriptor) : INativeTransport
    {
        private readonly NngTransport transport = new();
        internal bool Dropped { get; private set; }
        public async Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            byte[] response = await transport.ExchangeAsync(endpoint, request, timeout, cancellationToken);
            if (!Dropped && ApiRequest.Parser.ParseFrom(request).Message.Is(descriptor))
            {
                Dropped = true;
                throw new IOException("Injected reply loss after the real native response");
            }
            return response;
        }
    }

    private sealed class SynchronizationRequestAudit : INativeTransport
    {
        private readonly NngTransport transport = new();
        internal int Requests { get; private set; }
        internal int Mutations { get; private set; }
        public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Requests++;
            var message = ApiRequest.Parser.ParseFrom(request).Message;
            if (message.Is(CheckedSchematicBatch.Descriptor) || message.Is(CheckedSaveDocument.Descriptor)) Mutations++;
            return transport.ExchangeAsync(endpoint, request, timeout, cancellationToken);
        }
    }
}
