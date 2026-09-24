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
        // CN-1 §8.3: the capability stays unadvertised until lane 2A's measurement pieces land.
        // Measure it through the same MCP server and the native handshake instead of assuming it.
        var inspected = await mcp.Tool("kicad_instance_inspect", new { instanceId });
        RequireToolSuccess(inspected);
        string[] nativeCapabilities = inspected.GetProperty("structuredContent").EnumerateObject()
            .Single(property => string.Equals(property.Name, "nativeCapabilities", StringComparison.OrdinalIgnoreCase))
            .Value.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual((await client.HandshakeAsync(token)).Capabilities.Order(StringComparer.Ordinal).ToArray(), nativeCapabilities,
            "MCP must report exactly the capabilities the native handshake advertises.");
        bool capabilityAdvertised = nativeCapabilities.Contains(SchematicConnectedAddition.NativeCapability, StringComparer.Ordinal);
        Assert.IsFalse(capabilityAdvertised, "Connection realization must not be advertised before every CN-1 §8.3 piece is present.");
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
        async Task<string> Title() => (await client.InvokeAsync<GetTitleBlockInfo, TitleBlockInfo>(new() { Document = document }, token)).Title;
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
        // A new probe symbol placed on one physical screen, with one placement record per sheet
        // instance of that screen; the first placement is the projection this copy describes.
        (SchematicSymbolInstance Symbol, string Pin) NewProbe(long x, long y, params (DocumentSpecifier Owner, string Reference)[] placements)
        {
            var probe = template.Clone(); probe.Id = Id(); probe.Position = Point(x, y);
            probe.Path = placements[0].Owner.SheetPath.Clone(); probe.Variants = new(); probe.InstanceRecords = new();
            probe.ReferenceField.Text.Text_ = placements[0].Reference;
            probe.ReferenceField.Text.Position = Point(x + 3000000, y - 2000000);
            probe.ValueField.Text.Position = Point(x + 3000000, y + 2000000);
            foreach (var (owner, reference) in placements)
            {
                var record = new SymbolSheetRecord { ProjectName = document.Project.Name, Reference = reference, Unit = probe.Unit.Unit, Variants = new() };
                record.Path.Add(owner.SheetPath.Path.Select(id => id.Clone()));
                probe.InstanceRecords.Records.Add(record);
            }
            string pinId = "";
            foreach (var child in probe.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                if (pin.LibraryPinId is null) continue;
                pin.Id = Id(); child.Item = Any.Pack(pin); pinId = pin.Id.Value;
            }
            Assert.AreNotEqual("", pinId);
            return (probe, pinId);
        }
        var probes = new[] { (60000000L, "TP701"), (90000000L, "TP702"), (120000000L, "TP703") }
            .Select(probe => NewProbe(probe.Item1, 140000000, (document, probe.Item2))).ToList();
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
        var afterSplit = await Observe();
        Assert.AreEqual(marked, afterSplit, "A refused realization leaves every native object, revision and digest unchanged.");
        var afterRefusal = await Journal();
        Assert.AreEqual(journal.DocumentEpoch, afterRefusal.DocumentEpoch);
        Assert.AreEqual(journal.Sequence, afterRefusal.Sequence, "A refused realization records no journal entry.");
        // The marker is still the newest undo step: the refusal added none.
        await History("z");
        string refusalUndoTitle = await Title();
        Assert.AreEqual(titleBefore.Title, refusalUndoTitle);
        await History("y");
        string refusalRedoTitle = await Title();
        Assert.AreEqual("Connectivity assertion marker", refusalRedoTitle);

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
        string undoneTitle = await Title();
        Assert.AreEqual("Connectivity assertion marker", undoneTitle);
        await History("y");
        var redone = await Observe();
        Assert.IsTrue(realized.Electrical.Hierarchy.Data.Equals(redone.Electrical.Hierarchy.Data), "Redo restores the whole realization.");
        CollectionAssert.AreEqual(Canonical(expectedGroups), Canonical(Groups(redone)));
        await History("z"); await History("z");
        string restoredTitle = await Title();
        Assert.AreEqual(titleBefore.Title, restoredTitle);
        var restored = await Observe();
        CollectionAssert.AreEqual(Canonical(baseline), Canonical(Groups(restored)));

        // CN-1 §6.8 across the fixture's shared child screen (Channel A and B), planned by the
        // production hierarchy delta: both existing sheet symbols gain a SheetPin by update (the
        // rollback path that must forget connections before Revert frees the replaced pins), and a
        // GlobalLabel and a HierarchicalLabel sit on one screen loaded as two sheet instances, so
        // every child pin has one key per instance. The untouched labelled net must stay whole.
        await client.InvokeAsync<SetTitleBlockInfo, Empty>(new()
            { Document = document, TitleBlock = new() { Title = "Hierarchical assertion marker" } }, token);
        var shared = await Observe();
        var sharedData = shared.Electrical.Hierarchy.Data;
        var sharedRoot = sharedData.Instances.Single(s => s.Metadata.Document.Equals(document));
        var channels = sharedRoot.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>())
            .OrderBy(sheet => sheet.Position.YNm).ToArray();
        Assert.HasCount(2, channels, "The fixture places Channel A and Channel B on the root.");
        Assert.AreEqual(channels[0].ChildScreenId, channels[1].ChildScreenId, "Both channels must load one shared child screen.");
        var channelDocuments = channels.Select(sheet => sharedData.Instances.Single(s =>
            s.Metadata.Document.SheetPath.Path.Count == document.SheetPath.Path.Count + 1
            && s.Metadata.Document.SheetPath.Path.Take(document.SheetPath.Path.Count).SequenceEqual(document.SheetPath.Path)
            && s.Metadata.Document.SheetPath.Path[^1].Equals(sheet.Id)).Metadata.Document).ToArray();
        static string PathKey(DocumentSpecifier owner) => "/" + string.Join('/', owner.SheetPath.Path.Select(id => id.Value));
        static SchematicSymbolInstance InstanceCopy(SchematicSymbolInstance physical, DocumentSpecifier owner)
        {
            var copy = physical.Clone(); copy.Path = owner.SheetPath.Clone();
            copy.ReferenceField.Text.Text_ = physical.InstanceRecords.Records.Single(r => r.Path.SequenceEqual(owner.SheetPath.Path)).Reference;
            return copy;
        }
        string tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string globalName = "CN1_GLOBAL_" + tag, portName = "CN1_PORT_" + tag;
        string[] channelNets = ["CN1_PORT_A_" + tag, "CN1_PORT_B_" + tag];
        var rootGlobal = NewProbe(60000000, 120000000, (document, "TP811"));
        var rootPorts = new[] { NewProbe(90000000, 120000000, (document, "TP812")), NewProbe(120000000, 120000000, (document, "TP813")) };
        var stray = NewProbe(30000000, 120000000, (document, "TP814"));
        var childGlobal = NewProbe(60000000, 100000000, (channelDocuments[0], "TP821"), (channelDocuments[1], "TP921"));
        var childPort = NewProbe(100000000, 100000000, (channelDocuments[0], "TP822"), (channelDocuments[1], "TP922"));
        LocalLabel Local(Vector2 at, string text) => new() { Id = Id(), Position = at.Clone(), Text = Label(text),
            SpinStyle = SchematicLabelSpinStyle.SlssRight, Locked = LockedState.LsUnlocked };
        // §6.6 payloads: GlobalLabel and HierarchicalLabel stubs leave intersheet_refs_field unset for native to create.
        GlobalLabel Global(Vector2 at) => new() { Id = Id(), Position = at.Clone(), Text = Label(globalName),
            SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked };
        var rootGlobalLabel = Global(rootGlobal.Symbol.Position);
        var childGlobalLabel = Global(childGlobal.Symbol.Position);
        var childPortLabel = new HierarchicalLabel { Id = Id(), Position = childPort.Symbol.Position.Clone(), Text = Label(portName),
            SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked };
        var rootPortLabels = rootPorts.Select((probe, i) => Local(probe.Symbol.Position, channelNets[i])).ToArray();
        var strayLabel = Local(stray.Symbol.Position, existingNet);
        // §6.5: a left-side sheet pin on each channel, an outward stub wire and the channel's label at its end.
        var sheetPins = channels.Select(sheet => new SheetPin { Id = Id(), Position = Point(sheet.Position.XNm, sheet.Position.YNm + 5080000),
            Text = Label(portName), SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive,
            Side = SheetSide.ShsLeft, Locked = LockedState.LsUnlocked }).ToArray();
        var wireTemplate = sharedRoot.Items.Where(i => i.Is(SchematicLine.Descriptor)).Select(i => i.Unpack<SchematicLine>())
            .First(line => line.Type == SchematicLineType.SltWire);
        var pinWires = sheetPins.Select(pin =>
        {
            var stub = wireTemplate.Clone(); stub.Id = Id(); stub.Start = pin.Position.Clone();
            stub.End = Point(pin.Position.XNm - 7620000, pin.Position.YNm); return stub;
        }).ToArray();
        var pinLabels = pinWires.Select((stub, i) => Local(stub.End, channelNets[i])).ToArray();
        // New screens own their cache definitions: the child screen receives the probe's root entry.
        var libraryLink = template.LibraryId ?? template.Definition.Id;
        string cacheKey = template.LibName.Length != 0 ? template.LibName
            : (libraryLink.LibraryNickname.Length == 0 ? "" : libraryLink.LibraryNickname + ":") + libraryLink.EntryName;
        var probeCache = sharedRoot.CachedSymbols.Single(c => c.CacheKey == cacheKey);
        SchematicHierarchyData Desired(bool strayOnExistingNet)
        {
            var desired = sharedData.Clone();
            var root = desired.Instances.Single(s => s.Metadata.Document.Equals(document));
            root.Items.Add(Any.Pack(rootGlobal.Symbol)); root.Items.Add(Any.Pack(rootGlobalLabel));
            for (int i = 0; i < channels.Length; i++)
            {
                root.Items.Add(Any.Pack(rootPorts[i].Symbol)); root.Items.Add(Any.Pack(rootPortLabels[i]));
                int index = root.Items.ToList().FindIndex(item => item.Is(SheetSymbol.Descriptor) && item.Unpack<SheetSymbol>().Id.Equals(channels[i].Id));
                var sheet = root.Items[index].Unpack<SheetSymbol>(); sheet.Pins.Add(sheetPins[i].Clone()); root.Items[index] = Any.Pack(sheet);
                root.Items.Add(Any.Pack(pinWires[i])); root.Items.Add(Any.Pack(pinLabels[i]));
                var screen = desired.Instances.Single(s => s.Metadata.Document.Equals(channelDocuments[i]));
                if (!screen.CachedSymbols.Any(c => c.CacheKey == cacheKey)) screen.CachedSymbols.Add(probeCache.Clone());
                screen.Items.Add(Any.Pack(InstanceCopy(childGlobal.Symbol, channelDocuments[i]))); screen.Items.Add(Any.Pack(childGlobalLabel));
                screen.Items.Add(Any.Pack(InstanceCopy(childPort.Symbol, channelDocuments[i]))); screen.Items.Add(Any.Pack(childPortLabel));
            }
            if (strayOnExistingNet) { root.Items.Add(Any.Pack(stray.Symbol)); root.Items.Add(Any.Pack(strayLabel)); }
            return desired;
        }
        SchematicPinGroup PinGroup(params string[] keys) { var group = new SchematicPinGroup(); group.Pins.Add(keys.Select(Anchor)); return group; }
        SchematicConnectivityAssertion Expect(params SchematicPinGroup[] groups)
        { var expectation = new SchematicConnectivityAssertion { Version = 1 }; expectation.ExpectedGroups.Add(groups); return expectation; }
        CheckedSchematicBatch SharedRequest(SchematicHierarchyData desired, SchematicConnectivityAssertion expectation, bool createSheet = false)
        {
            var batch = new ApplySchematicItemBatch { Document = document.Clone(), DocumentEpoch = shared.State.Revision.Epoch,
                ExpectedRevision = shared.State.Revision.Clone(), OperationId = Guid.NewGuid().ToString("D"), Description = "Apply XML connections" };
            if (createSheet)
            {
                var sheet = channels[0].Clone(); sheet.Id = Id(); sheet.Pins.Clear();
                batch.Operations.Add(new SchematicItemOperation { TargetDocument = document.Clone(), Create = Any.Pack(sheet) });
            }
            batch.Operations.Add(SchematicHierarchyDelta.Plan(sharedData, desired, token));
            batch.Operations.Add(new SchematicItemOperation { AssertConnectivity = expectation.Clone() });
            return new() { Batch = batch, ExpectedState = shared.State.Clone() };
        }
        string At(DocumentSpecifier owner, string pin) => PathKey(owner) + "#" + pin;
        string rootGlobalKey = At(document, rootGlobal.Pin), aGlobalKey = At(channelDocuments[0], childGlobal.Pin), bGlobalKey = At(channelDocuments[1], childGlobal.Pin);
        string[] portKeys = [At(channelDocuments[0], childPort.Pin), At(channelDocuments[1], childPort.Pin)];
        string[] rootPortKeys = rootPorts.Select(probe => At(document, probe.Pin)).ToArray();
        Assert.AreEqual(rootPath, PathKey(document));
        // Equivalent grouping in a different order: groups and members deliberately unsorted.
        var hierarchicalExpectation = Expect(PinGroup(portKeys[1], rootPortKeys[1]), PinGroup(bGlobalKey, rootGlobalKey, aGlobalKey),
            PinGroup(rootPortKeys[0], portKeys[0]));
        // The same batch planned as if a shared screen were one sheet instance: the global join across instances is missed.
        var perInstanceExpectation = Expect(PinGroup(rootGlobalKey, aGlobalKey), PinGroup(bGlobalKey),
            PinGroup(rootPortKeys[0], portKeys[0]), PinGroup(rootPortKeys[1], portKeys[1]));
        var sharedBaseline = Groups(shared);
        var untouched = sharedBaseline.Single(g => g.Contains(Key(templatePin)));
        var correct = Desired(false);
        var planned = SchematicHierarchyDelta.Plan(sharedData, correct, token);
        Assert.AreEqual(2, planned.Count(o => o.Update?.Is(SheetSymbol.Descriptor) == true), "Both channel sheet symbols gain their pin by update.");
        Assert.AreEqual(4, planned.Count(o => o.Create is not null && !o.TargetDocument.Equals(document)),
            "Each shared-screen item is created once for both sheet instances.");
        Assert.IsTrue(planned.All(o => o.Create is not null || o.Update is not null || o.ReplaceLibraryCache is not null));
        var sharedJournal = await Journal();
        async Task<(bool Unchanged, object Evidence)> RequireRefusal(CheckedSchematicBatchReceipt refusal, bool isError, string code, string message)
        {
            Assert.IsTrue(isError, "A refused post-condition is not success.");
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsRejected, refusal.Status, refusal.ErrorMessage);
            Assert.AreEqual(code, refusal.ErrorCode, refusal.ErrorMessage);
            Assert.AreEqual(message, refusal.ErrorMessage);
            Assert.IsNull(refusal.Result);
            Assert.AreEqual(shared.State, refusal.ObservedBefore); Assert.AreEqual(shared.State, refusal.ObservedAfter);
            // KiCad still answers, and every native object, revision and digest is unchanged.
            var observed = await Observe();
            Assert.AreEqual(shared, observed, "A refused hierarchical realization leaves the document untouched.");
            var journalAfter = await Journal();
            Assert.AreEqual(sharedJournal.DocumentEpoch, journalAfter.DocumentEpoch);
            Assert.AreEqual(sharedJournal.Sequence, journalAfter.Sequence, "A refused realization records no journal entry.");
            bool documentUnchanged = shared.Equals(observed);
            bool journalUnchanged = sharedJournal.DocumentEpoch == journalAfter.DocumentEpoch && sharedJournal.Sequence == journalAfter.Sequence;
            return (documentUnchanged && journalUnchanged, new { code, documentUnchanged, journalUnchanged,
                journalSequenceBefore = sharedJournal.Sequence, journalSequenceAfter = journalAfter.Sequence });
        }

        // Fail closed: sheet creation cannot share a batch with an assertion (CN-1 §1), refused before staging.
        var (sheetCreation, sheetCreationError) = await Apply(SharedRequest(correct, hierarchicalExpectation, createSheet: true));
        var sheetCreationCheck = await RequireRefusal(sheetCreation, sheetCreationError, "native_batch_rejected",
            "Atomic operation 0 rejected: A connectivity assertion cannot evaluate a batch that creates sheets");
        // Must-catch: one key per sheet instance. The GlobalLabel on the shared screen joins both
        // instances of the child probe, so treating them as one instance is an unexpected join.
        var (perInstance, perInstanceError) = await Apply(SharedRequest(correct, perInstanceExpectation));
        var perInstanceCheck = await RequireRefusal(perInstance, perInstanceError, SchematicConnectionErrors.ConnectivityPostconditionFailed,
            $"connectivity_postcondition_failed: expected=4 mismatches=3 first=unexpected_join:{bGlobalKey}");
        // Must-catch: a stray stub joins the untouched labelled net the assertion does not mention.
        var (strayJoin, strayJoinError) = await Apply(SharedRequest(Desired(true), hierarchicalExpectation));
        var strayJoinCheck = await RequireRefusal(strayJoin, strayJoinError, SchematicConnectionErrors.ConnectivityPostconditionFailed,
            "connectivity_postcondition_failed: expected=3 mismatches=2 first=unaffected_group_changed:"
            + string.Join(',', untouched.Order(StringComparer.Ordinal)));
        // The marker is still the newest undo step: none of the refusals added one.
        await History("z");
        string sharedRefusalUndoTitle = await Title();
        Assert.AreEqual(titleBefore.Title, sharedRefusalUndoTitle);
        await History("y");
        string sharedRefusalRedoTitle = await Title();
        Assert.AreEqual("Hierarchical assertion marker", sharedRefusalRedoTitle);

        // The exact hierarchical result commits once, with the native proof, as one undo step.
        var sharedReady = await Observe();
        Assert.IsTrue(sharedData.Equals(sharedReady.Electrical.Hierarchy.Data), "Undo and redo of the marker restore the same hierarchy.");
        CollectionAssert.AreEqual(Canonical(sharedBaseline), Canonical(Groups(sharedReady)));
        shared = sharedReady;
        var hierarchicalRequest = SharedRequest(correct, hierarchicalExpectation);
        var (connected, connectedError) = await Apply(hierarchicalRequest);
        Assert.IsFalse(connectedError, connected.ErrorMessage);
        Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, connected.Status, connected.ErrorMessage);
        Assert.IsTrue(connected.Result.ConnectivityAssertionVerified);
        var hierarchicalRealized = await Observe();
        Assert.AreEqual(connected.ObservedAfter, hierarchicalRealized.State);
        var hierarchicalGroups = sharedBaseline
            .Append(new SortedSet<string>([rootGlobalKey, aGlobalKey, bGlobalKey], StringComparer.Ordinal))
            .Concat(portKeys.Select((port, i) => new SortedSet<string>([port, rootPortKeys[i]], StringComparer.Ordinal)));
        CollectionAssert.AreEqual(Canonical(hierarchicalGroups), Canonical(Groups(hierarchicalRealized)),
            "Native groups equal the untouched net plus exactly the asserted groups.");
        var realizedRoot = hierarchicalRealized.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(document));
        foreach (var (sheet, pin) in channels.Zip(sheetPins))
            Assert.AreEqual(pin.Id, realizedRoot.Items.Where(i => i.Is(SheetSymbol.Descriptor)).Select(i => i.Unpack<SheetSymbol>())
                .Single(s => s.Id.Equals(sheet.Id)).Pins.Single().Id, "Each channel sheet symbol gained exactly its sheet pin.");
        foreach (var owner in channelDocuments)
        {
            var screen = hierarchicalRealized.Electrical.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.Equals(owner));
            var childLabel = screen.Items.Where(i => i.Is(GlobalLabel.Descriptor)).Select(i => i.Unpack<GlobalLabel>()).Single(l => l.Id.Equals(childGlobalLabel.Id));
            // §6.6: native creates the hidden intersheet reference field every global label owns.
            Assert.AreEqual("${INTERSHEET_REFS}", childLabel.IntersheetRefsField?.Text.Text_, "Native must create the intersheet reference field.");
            Assert.IsFalse(childLabel.IntersheetRefsField!.Visible);
            Assert.IsTrue(screen.Items.Any(i => i.Is(HierarchicalLabel.Descriptor) && i.Unpack<HierarchicalLabel>().Id.Equals(childPortLabel.Id)));
        }
        var hierarchyImage = await client.InvokeAsync<CaptureSchematicObservation, SchematicObservation>(new() { Document = document }, token);
        await File.WriteAllBytesAsync(Path.Combine(evidence, instanceId + "-connectivity-assertion-hierarchy.png"), hierarchyImage.Preview.Png.ToByteArray(), token);

        await History("z");
        var hierarchicalUndone = await Observe();
        Assert.IsTrue(shared.Electrical.Hierarchy.Data.Equals(hierarchicalUndone.Electrical.Hierarchy.Data),
            "One native undo restores the exact pre-batch hierarchy, sheet symbols and child caches included.");
        CollectionAssert.AreEqual(Canonical(sharedBaseline), Canonical(Groups(hierarchicalUndone)));
        string hierarchicalUndoneTitle = await Title();
        Assert.AreEqual("Hierarchical assertion marker", hierarchicalUndoneTitle);
        await History("y");
        var hierarchicalRedone = await Observe();
        Assert.IsTrue(hierarchicalRealized.Electrical.Hierarchy.Data.Equals(hierarchicalRedone.Electrical.Hierarchy.Data),
            "Redo re-applies the whole hierarchical realization.");
        CollectionAssert.AreEqual(Canonical(hierarchicalGroups), Canonical(Groups(hierarchicalRedone)));
        await History("z"); await History("z");
        string hierarchicalRestoredTitle = await Title();
        Assert.AreEqual(titleBefore.Title, hierarchicalRestoredTitle);
        var hierarchicalRestored = await Observe();
        CollectionAssert.AreEqual(Canonical(baseline), Canonical(Groups(hierarchicalRestored)));

        static JsonElement Json(IMessage message) => JsonDocument.Parse(SchematicJson.Formatter.Format(message)).RootElement;
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-connectivity-assertion.json"), JsonSerializer.Serialize(new
        {
            instanceId, request = Json(passingRequest), refused = Json(split), misplaced = Json(misplaced), completed = Json(passed),
            before = Canonical(baseline), after = Canonical(Groups(realized)),
            // Each value below is measured in this run: the refused batch left the document and its change journal
            // unchanged and added no undo step (one undo still removes the marker title); one undo removed the whole
            // realization (the hierarchy equals the pre-batch one and the marker is still set); redo re-applied it;
            // undoing the marker restored the original title.
            refusal = new { documentUnchanged = marked.Equals(afterSplit),
                journalSequenceBefore = journal.Sequence, journalSequenceAfter = afterRefusal.Sequence,
                journalUnchanged = journal.DocumentEpoch == afterRefusal.DocumentEpoch && journal.Sequence == afterRefusal.Sequence,
                titleAfterUndo = refusalUndoTitle, titleAfterRedo = refusalRedoTitle },
            refusalLeftNoRevisionJournalOrUndo = marked.Equals(afterSplit)
                && journal.DocumentEpoch == afterRefusal.DocumentEpoch && journal.Sequence == afterRefusal.Sequence
                && refusalUndoTitle == titleBefore.Title && refusalRedoTitle == "Connectivity assertion marker",
            oneUndoStep = ready.Electrical.Hierarchy.Data.Equals(undone.Electrical.Hierarchy.Data)
                && Canonical(baseline).SequenceEqual(Canonical(Groups(undone))) && undoneTitle == "Connectivity assertion marker",
            redoReapplied = realized.Electrical.Hierarchy.Data.Equals(redone.Electrical.Hierarchy.Data)
                && Canonical(expectedGroups).SequenceEqual(Canonical(Groups(redone))),
            titleRestored = restoredTitle == titleBefore.Title && Canonical(baseline).SequenceEqual(Canonical(Groups(restored))),
            hierarchical = new
            {
                instanceKeys = channelDocuments.Select(PathKey).ToArray(), request = Json(hierarchicalRequest),
                sheetCreationRefused = Json(sheetCreation), perInstanceRefused = Json(perInstance), strayJoinRefused = Json(strayJoin),
                completed = Json(connected), before = Canonical(sharedBaseline), after = Canonical(Groups(hierarchicalRealized)),
                refusals = new[] { sheetCreationCheck.Evidence, perInstanceCheck.Evidence, strayJoinCheck.Evidence },
                refusalsLeftNoRevisionJournalOrUndo = sheetCreationCheck.Unchanged && perInstanceCheck.Unchanged && strayJoinCheck.Unchanged
                    && sharedRefusalUndoTitle == titleBefore.Title && sharedRefusalRedoTitle == "Hierarchical assertion marker",
                oneUndoStep = shared.Electrical.Hierarchy.Data.Equals(hierarchicalUndone.Electrical.Hierarchy.Data)
                    && Canonical(sharedBaseline).SequenceEqual(Canonical(Groups(hierarchicalUndone)))
                    && hierarchicalUndoneTitle == "Hierarchical assertion marker",
                redoReapplied = hierarchicalRealized.Electrical.Hierarchy.Data.Equals(hierarchicalRedone.Electrical.Hierarchy.Data)
                    && Canonical(hierarchicalGroups).SequenceEqual(Canonical(Groups(hierarchicalRedone))),
                titleRestored = hierarchicalRestoredTitle == titleBefore.Title
                    && Canonical(baseline).SequenceEqual(Canonical(Groups(hierarchicalRestored)))
            },
            nativeCapabilities, capabilityAdvertised
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
