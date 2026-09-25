using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyMcpReattachment(ProcessStartInfo start, string instanceId,
        DocumentSpecifier document, SchematicScreenDataSnapshot expected, SchematicSaveState dirty,
        CancellationToken token)
    {
        using Process process = Process.Start(start)!;
        Task<string> diagnostics = process.StandardError.ReadToEndAsync(token);
        int nextId = 0;
        try
        {
            await Request("initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "native-reattachment-journey", version = "1" }
            });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var saved = await Call("kicad_instance_saved_sessions", new { });
            using var records = JsonDocument.Parse(saved.GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.AreEqual(instanceId, records.RootElement[0].GetProperty("instanceId").GetString());
            await Call("kicad_instance_reattach", new { instanceId });
            await VerifyInstanceCapabilityCatalogue();
            string documentJson = JsonFormatter.Default.Format(document);
            var state = await Call("kicad_schematic_save_state", new { instanceId, documentJson });
            Assert.AreEqual(dirty, SchematicJson.Parser.Parse<SchematicSaveState>(state.GetProperty("structuredContent").GetProperty("state").GetRawText()));
            var snapshot = await Call("kicad_schematic_data", new { instanceId, documentJson });
            Assert.AreEqual(expected, SchematicJson.Parser.Parse<SchematicScreenDataSnapshot>(snapshot.GetProperty("structuredContent").GetProperty("snapshot").GetRawText()),
                "Reconnecting must preserve unsaved in-memory objects and their revision, not reload disk contents.");
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            Assert.AreEqual(0, process.ExitCode, await diagnostics);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }

        // The reattached server reports exactly what is registered: its service tools equal tools/list,
        // its native features and requests equal a handshake read directly from KiCad, and that
        // handshake's handled requests equal the request types this process really dispatches with its
        // schematic editor open.
        async Task VerifyInstanceCapabilityCatalogue()
        {
            var listed = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = (await Request("tools/list", cursor is null ? new { } : (object)new { cursor })).GetProperty("result");
                listed.AddRange(page.GetProperty("tools").EnumerateArray().Select(t => t.Clone()));
                cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            } while (cursor is not null);
            string state = start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"]!;
            var record = JsonSerializer.Deserialize<InstanceRecord>(await File.ReadAllTextAsync(Path.Combine(state, instanceId + ".json"), token))!;
            var direct = new NativeClient(new NngTransport(), record.Endpoint, record.Epoch);
            var session = await direct.HandshakeAsync(token);
            string[] features = NativeFeatureContracts.Verify(session);

            var attached = JsonSerializer.Deserialize<InstanceView[]>((await Call("kicad_instances_list", new { }))
                .GetProperty("content")[0].GetProperty("text").GetString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.AreEqual(instanceId, attached.Single().InstanceId);
            var catalogue = (await Call("kicad_instance_capabilities", new { instanceId })).GetProperty("structuredContent");
            string[] serviceNames = CapabilityCatalogAssertions.VerifyServiceCatalogue(catalogue, listed);
            Assert.AreEqual(instanceId, catalogue.GetProperty("instanceId").GetString());
            Assert.AreEqual(session.Epoch, catalogue.GetProperty("epoch").GetString());
            CollectionAssert.AreEqual(features, catalogue.GetProperty("nativeFeatures").EnumerateArray().Select(n => n.GetString()!).ToArray(),
                "The MCP catalogue must carry the native feature contracts unchanged.");
            Assert.AreEqual(CapabilityCatalog.HandledRequestCoverage, catalogue.GetProperty("nativeRequestCoverage").GetString());
            var native = catalogue.GetProperty("nativeRequests").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(session.HandledRequests.ToArray(), native.Select(n => n.GetProperty("name").GetString()!).ToArray(),
                "The MCP catalogue must carry the native handled requests unchanged.");
            Assert.IsTrue(native.All(n => n.GetProperty("availability").GetString() == "handler-registered"));
            Assert.IsFalse(catalogue.TryGetProperty("nativeCapabilities", out _), "Catalogue v2 names features and requests separately.");
            var service = (await Call("kicad_service_capabilities", new { })).GetProperty("structuredContent");
            Assert.AreEqual(catalogue.GetProperty("serviceCapabilities").GetRawText(), service.GetProperty("serviceCapabilities").GetRawText());
            Assert.AreEqual(catalogue.GetProperty("limitations").GetRawText(), service.GetProperty("limitations").GetRawText());
            var inspected = (await Call("kicad_instance_inspect", new { instanceId })).GetProperty("structuredContent");
            CollectionAssert.AreEqual(features, inspected.GetProperty("nativeCapabilities").EnumerateArray()
                .Select(n => n.GetString()!).ToArray(), "Inspection keeps nativeCapabilities as the feature contracts.");
            Assert.AreEqual(CapabilityCatalog.HandledRequestCoverage, inspected.GetProperty("nativeRequestCoverage").GetString());
            CollectionAssert.AreEqual(session.HandledRequests.ToArray(), inspected.GetProperty("nativeRequests").EnumerateArray()
                .Select(n => n.GetProperty("name").GetString()!).ToArray());

            string artifacts = Path.Combine(FindRoot(), "automation", "artifacts", "native-session-current");
            string[] advertised = await NativeCapabilityProbe.VerifyHandshakeAsync(direct,
                Directory.Exists(artifacts) ? Path.Combine(artifacts, instanceId + "-editor-capabilities.json") : null, token);
            // With the schematic editor open, its handler and both automation controllers are dispatched.
            // Opening an editor adds no close-all request to the desktop manager.
            foreach (string type in new[] { GetAutomationSession.Descriptor.FullName, GetVersion.Descriptor.FullName,
                         ReadSchematicScreenData.Descriptor.FullName, CheckedSchematicBatch.Descriptor.FullName, CheckedSaveDocument.Descriptor.FullName })
                CollectionAssert.Contains(advertised, type);
            CollectionAssert.DoesNotContain(advertised, CloseAllDocuments.Descriptor.FullName,
                "The desktop manager has no close-all callback, so it must not dispatch or list CloseAllDocuments.");
            var documents = SchematicJson.Parser.Parse<GetOpenDocumentsResponse>((await Call("kicad_documents_list",
                new { instanceId, kind = "schematic" })).GetProperty("content")[0].GetProperty("text").GetString()!);
            CollectionAssert.Contains(documents.Documents.ToArray(), document);
            Console.WriteLine($"Capability catalogue {instanceId}: {serviceNames.Length} registered tools, {features.Length} native features, {advertised.Length} native request types.");
        }

        async Task<JsonElement> Call(string name, object arguments)
        {
            var response = await Request("tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
            return result;
        }

        async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                Assert.IsNotNull(line, "Restarted MCP exited before responding.");
                using var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                    return response.RootElement.Clone();
            }
        }
    }

    // Decision n39ac0ccc5c9270f2 / ledgers pbfcccd896f17cf27 and p186c0db05be2a146 against a real KiCad and the compiled MCP
    // server, now that KiCad advertises schematic.connection-realization.v1 (CN-1 §8.3). The saved revision only connects the
    // drawn, unconnected pins of two probes, as the net PROBE_LINK (declared, never inferred). The kicad_design_sync_plan
    // preview classifies it with the handshake the server recorded when it attached or reattached this instance: the
    // realization plan (CN-1 §4.2), with no publishable XML and no native operations, because only the realization itself
    // measures KiCad. With no attached instance, or only a saved registration after a restart, it is the general plan. The
    // automatic worker and apply each take their own live handshake and realize the revision on this editor (CN-1 §9.1-9.2):
    // one checked KiCad commit whose connectivity assertion KiCad verified, a stub and a local label for each probe pin, KiCad
    // showing both pins in one net named PROBE_LINK, and exactly that drawing published to the XML. Both start from the same
    // saved sheet and draw exactly the same items. The advertised case with a test double stays in
    // McpProcessTests.SyncPreviewClassifiesWithTheAttachedInstancesHandshakeOverStdio.
    private static async Task VerifyRecordedHandshakePlanning(NativeClient client, string registryState, string schematic,
        string evidence, CancellationToken token)
    {
        var root = (await Ready(() => client.OpenRootSchematicAsync(schematic, token))).Document;
        await Ready(() => client.InvokeAsync<SaveDocument, Empty>(new() { Document = root.Clone() }, token));
        var live = await client.HandshakeAsync(token);
        string instanceId = live.InstanceId;
        NativeFeatureContracts.Verify(live);
        Assert.IsTrue(live.Capabilities.Contains(SchematicConnectedAddition.NativeCapability),
            "This KiCad serves every CN-1 §8.3 piece, so it must advertise " + SchematicConnectedAddition.NativeCapability + ": "
            + string.Join(",", live.Capabilities));
        byte[] savedSheet = await File.ReadAllBytesAsync(schematic, token);
        Task<CheckedSchematicState> Capture() => Ready(() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token));
        var initial = await Capture();
        Assert.IsTrue(CheckedSchematicContract.FileCoverage(initial.State),
            "The saved root must have known, unchanged file baselines so apply can reach its classification: " + SchematicJson.Formatter.Format(initial.State));
        Assert.IsFalse(initial.State.NativeContentDirty);
        // The baseline declares no connection; the saved revision joins both probe pins as PROBE_LINK.
        var probe = ProbeElectricalModel(initial.Electrical);
        var link = probe.Engineering.Circuit.Nets.Single() with { Name = "PROBE_LINK" };
        var unconnected = probe.Engineering with { Circuit = probe.Engineering.Circuit with { Nets = [] } };
        var connected = probe.Engineering with { Circuit = probe.Engineering.Circuit with { Nets = [link] } };
        // A record of this editor at one captured state, with the saved revision as its desired XML.
        DesignRecoveryState Revision(CheckedSchematicState at)
        {
            var data = at.Electrical.Hierarchy.Data;
            var design = probe with { Engineering = connected, Schematic = data.Clone() };
            return new(Guid.NewGuid(), Guid.Parse(instanceId), new(at.State.Revision.Epoch, at.State.Revision.Sequence),
                at.Electrical.Hierarchy.TrackingComplete, design with { Engineering = unconnected },
                Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [])), data.Clone(), [],
                BaselineElectrical: at.Electrical.Clone(), ObservedElectrical: at.Electrical.Clone());
        }
        var reference = Revision(initial);
        var classified = SchematicConnectedAddition.Classify(reference, DesignRecoveryStore.ReadDesired(reference), live, token);
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, classified.Kind, classified.ErrorCode + ": " + classified.ErrorMessage);
        CollectionAssert.AreEqual(new[] { link.Id }, classified.ChangedNetIds.ToArray());
        // Without a handshake the planner keeps the general path; this KiCad's own handshake plans the realization.
        var today = SchematicSynchronizationPlanner.Plan(reference, token);
        Assert.IsTrue(today.CanPrepare, today.ErrorCode + ": " + today.ErrorMessage);
        Assert.IsFalse(today.NativeConnectionRealizationRequired);
        Assert.IsNotNull(today.CandidateXml, "The general path previews its candidate XML.");
        var realizing = SchematicSynchronizationPlanner.Plan(reference, live, token);
        Assert.IsTrue(realizing.CanPrepare, realizing.ErrorCode + ": " + realizing.ErrorMessage);
        Assert.IsTrue(realizing.NativeConnectionRealizationRequired, "This KiCad's handshake plans the connection's realization (CN-1 §4.3).");
        Assert.IsNull(realizing.CandidateXml, "A realization plan has no publishable preview (CN-1 §4.2).");
        Assert.IsEmpty(realizing.NativeOperations, "Only the realization measures KiCad and produces native operations.");
        var island = realizing.Connections!.Screens.Single().Islands.Single();
        Assert.AreEqual("PROBE_LINK", island.LabelText);
        Assert.HasCount(2, island.Members.Where(m => m.RequiresStub).ToArray(), "Both probe pins need a stub.");

        string folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(registryState)!, "handshake-planning")).FullName;
        (string Recovery, string Design, string Token) Record(string name, DesignRecoveryState state)
        {
            string recovery = Path.Combine(folder, name + ".recovery.json"), design = Path.Combine(folder, name + ".design.xml");
            File.WriteAllBytes(design, state.DesiredFileBytes);
            return (recovery, design, new DesignRecoveryStore(recovery).Save(state, null).RevisionToken);
        }
        async Task<JsonElement> Preview(StdioMcpFixture mcp, (string Recovery, string Design, string Token) target) =>
            (await mcp.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = target.Recovery, expectedRevisionToken = target.Token }))
                .GetProperty("structuredContent").Clone();
        var preview = Record("preview", reference);
        JsonElement unattached, afterAttach, afterRestart, afterReattach;
        Drawing worker, applied;
        string? workerCode;
        await using (var mcp = await StdioMcpFixture.StartAsync(registryState, Path.Combine(evidence, "handshake-planning-attach.stderr.log"), token))
        {
            unattached = await Preview(mcp, preview);
            RequirePlan(today, unattached, "Without an attached instance the preview is the general plan.");
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            afterAttach = await Preview(mcp, preview);
            RequirePlan(realizing, afterAttach, "The handshake recorded at attach advertises realization, so the preview is the realization plan.");
            Assert.AreNotEqual(unattached.GetRawText(), afterAttach.GetRawText());

            // The automatic worker takes its own handshake, plans and realizes the revision.
            var automatic = Record("worker", reference);
            var started = await mcp.Tool("kicad_design_automatic_sync_start", new { instanceId, recoveryPath = automatic.Recovery,
                designPath = automatic.Design, expectedRecoveryRevision = automatic.Token });
            RequireToolSuccess(started);
            string sessionId = started.GetProperty("structuredContent").GetProperty("sessionId").GetString()!;
            var status = started.GetProperty("structuredContent").GetProperty("status").Clone();
            while (status.GetProperty("phase").GetString() is not ("Paused" or "Watching" or "Stopped" or "InvalidDesign"))
            {
                var next = await mcp.Tool("kicad_design_automatic_sync_wait", new { instanceId, sessionId,
                    afterSequence = status.GetProperty("sequence").GetUInt64() });
                RequireToolSuccess(next); status = next.GetProperty("structuredContent").GetProperty("status").Clone();
            }
            RequireToolSuccess(await mcp.Tool("kicad_design_automatic_sync_stop", new { instanceId, sessionId }));
            workerCode = status.GetProperty("errorCode").GetString();
            Assert.AreEqual("Watching", status.GetProperty("phase").GetString(), "The worker must realize the revision: " + status.GetRawText());
            Assert.AreNotEqual(JsonValueKind.Null, status.GetProperty("operationId").ValueKind, status.GetRawText());
            worker = await Drawn("worker", automatic, initial, status.GetProperty("operationId").GetGuid());

            // Apply starts from the same saved sheet: the worker's drawing is taken back by reloading that sheet.
            await File.WriteAllBytesAsync(schematic, savedSheet, token);
            await Ready(() => client.InvokeAsync<RevertDocument, Empty>(new() { Document = root.Clone() }, token));
            var restored = await Capture();
            // Loading the file KiCad wrote changes the sheet's loaded-format provenance, not any object on it.
            var probesAlone = initial.Electrical.Hierarchy.Data.Clone();
            foreach (var screen in probesAlone.Instances)
                screen.Metadata.LoadedNativeFormatVersion = restored.Electrical.Hierarchy.Data.Instances
                    .Single(s => s.Metadata.Document.Equals(screen.Metadata.Document)).Metadata.LoadedNativeFormatVersion;
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(probesAlone, restored.Electrical.Hierarchy.Data, token),
                "Reloading the saved sheet restores the unconnected probes.");
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(restored.Electrical.Hierarchy.Data, probesAlone, token));
            Assert.IsFalse(restored.State.NativeContentDirty);
            var direct = Record("apply", Revision(restored));
            Guid applyOperation = Guid.NewGuid();
            var apply = await mcp.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = direct.Recovery,
                designPath = direct.Design, expectedRevisionToken = direct.Token, operationId = applyOperation.ToString("D") });
            RequireToolSuccess(apply);
            var result = apply.GetProperty("structuredContent");
            Assert.IsTrue(result.GetProperty("nativeMutationCommitted").GetBoolean(), apply.GetRawText());
            Assert.IsTrue(result.GetProperty("synchronizationCommitted").GetBoolean(), apply.GetRawText());
            Assert.IsTrue(result.GetProperty("nativeReceipt").GetProperty("result").GetProperty("connectivityAssertionVerified").GetBoolean(),
                "KiCad verified the connectivity assertion of apply's commit: " + apply.GetRawText());
            applied = await Drawn("apply", direct, restored, applyOperation);
            CollectionAssert.AreEqual(worker.Items.ToArray(), applied.Items.ToArray(), "The worker and apply draw exactly the same items from the same saved sheet.");
        }
        // A restarted server holds only the saved registration, which is not a handshake, until it reattaches.
        await using (var mcp = await StdioMcpFixture.StartAsync(registryState, Path.Combine(evidence, "handshake-planning-reattach.stderr.log"), token))
        {
            afterRestart = await Preview(mcp, preview);
            Assert.AreEqual(unattached.GetRawText(), afterRestart.GetRawText(),
                "A saved registration is not a recorded handshake: the preview is the general plan.");
            RequireToolSuccess(await mcp.Tool("kicad_instance_reattach", new { instanceId }));
            afterReattach = await Preview(mcp, preview);
            Assert.AreEqual(afterAttach.GetRawText(), afterReattach.GetRawText(),
                "The handshake recorded at reattach advertises realization again.");
        }
        Assert.AreEqual(preview.Token, new DesignRecoveryStore(preview.Recovery).Read()!.RevisionToken, "The preview writes nothing.");
        static string Sha256(JsonElement plan) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(plan.GetRawText())));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-handshake-planning.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeCapabilities = live.Capabilities.ToArray(), classification = classified.Kind.ToString(),
            todayCandidateXmlSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(today.CandidateXml!))),
            previewSha256 = new { withoutAttachment = Sha256(unattached), afterAttach = Sha256(afterAttach),
                afterRestart = Sha256(afterRestart), afterReattach = Sha256(afterReattach) },
            realizationPlan = new { label = island.LabelText, stubs = island.Members.Count(m => m.RequiresStub),
                expectedGroups = realizing.Connections!.ExpectedGroups.Count },
            workerErrorCode = workerCode, worker, apply = applied
        }), token);
        Console.WriteLine($"Recorded handshake planning {instanceId}: the preview is the realization plan; the worker and apply each drew "
            + $"{applied.Items.Count} items in one verified KiCad commit.");

        // What one realization left in KiCad and its record: the committed receipt with KiCad's verified assertion, exactly
        // two stubs and two PROBE_LINK labels, both probe pins in one native net named PROBE_LINK, and that drawing published.
        async Task<Drawing> Drawn(string name, (string Recovery, string Design, string Token) target, CheckedSchematicState before, Guid operation)
        {
            var after = await Capture();
            Assert.IsFalse(after.State.NativeContentDirty, name + ": the realization is saved.");
            var record = new DesignRecoveryStore(target.Recovery).Read()!.State;
            Assert.IsFalse(record.HasPendingWork, name);
            // A worker that saw its own commit afterwards may have archived this receipt behind a later settled one.
            var synchronization = record.LastSynchronization?.OperationId == operation ? record.LastSynchronization
                : new DesignSynchronizationReceipts(target.Recovery).Read(operation) ?? throw new AssertFailedException(name + ": no receipt for " + operation);
            var receipt = CheckedSchematicBatchReceipt.Parser.ParseFrom(synchronization.NativeReceipt
                ?? throw new AssertFailedException(name + ": the synchronization committed no native batch."));
            Assert.AreEqual(CheckedSchematicBatchStatus.CsbsCompleted, receipt.Status, name);
            Assert.IsTrue(receipt.Result.ConnectivityAssertionVerified, name + ": KiCad verified the connectivity assertion.");
            byte[] xml = await File.ReadAllBytesAsync(target.Design, token);
            CollectionAssert.AreEqual(record.DesiredFileBytes, xml, name + ": the record holds the published XML.");
            var published = SchematicDesignXml.Read(Encoding.UTF8.GetString(xml), []);
            Assert.AreEqual(CircuitXml.Write(connected.Circuit), CircuitXml.Write(published.Engineering.Circuit), name + ": the circuit is the saved revision's.");
            Assert.IsEmpty(SchematicHierarchyDelta.Plan(after.Electrical.Hierarchy.Data, published.Schematic, token), name + ": the XML holds KiCad's drawing.");
            var comparison = SchematicElectricalComparison.Compare(published, after.Electrical, [], token);
            Assert.IsTrue(comparison.PinBindingsComplete && comparison.ConnectivityEquivalent, name + ": KiCad shows exactly the XML nets.");
            var joined = comparison.PinPartitions!.Single(p => p.Pins.Count == 2);
            CollectionAssert.AreEquivalent(link.Pins.ToArray(), joined.Pins.ToArray(), name);
            Assert.AreEqual("PROBE_LINK", joined.NativeName![(joined.NativeName!.LastIndexOf('/') + 1)..], name + ": the label names the net.");
            var items = NewItems(before.Electrical.Hierarchy.Data, after.Electrical.Hierarchy.Data);
            Assert.HasCount(4, items, name + ": " + string.Join("; ", items));
            Assert.AreEqual(2, items.Count(i => i.StartsWith(SchematicLine.Descriptor.FullName, StringComparison.Ordinal)), name);
            Assert.AreEqual(2, items.Count(i => i.StartsWith(LocalLabel.Descriptor.FullName, StringComparison.Ordinal) && i.Contains("\"PROBE_LINK\"", StringComparison.Ordinal)), name);
            return new(items, after.State.StateSha256, receipt.OperationId, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(xml)));
        }

        // An editor that has just opened can answer busy or not ready for a moment.
        async Task<T> Ready<T>(Func<Task<T>> request)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { return await request(); }
                catch (NativeApiException error) when (error.Status is 4 or 7 && !deadline.IsCancellationRequested)
                { await Task.Delay(100, deadline.Token); }
            }
        }
    }

    private sealed record Drawing(IReadOnlyList<string> Items, string NativeStateSha256, string OperationId, string PublishedXmlSha256);

    // Items KiCad holds after that it did not hold before, per sheet, each written without the identities it was given, so that
    // two drawings of the same connections compare equal whatever identities their revisions gave them.
    private static IReadOnlyList<string> NewItems(SchematicHierarchyData before, SchematicHierarchyData after)
    {
        string known = SchematicJson.Formatter.Format(before);
        var result = new List<string>();
        foreach (var screen in after.Instances)
        {
            string path = string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));
            var old = before.Instances.SingleOrDefault(s => s.Metadata.Document.Equals(screen.Metadata.Document));
            var existing = old is null ? [] : SchematicItemDelta.Index(old.Items).Keys.ToHashSet();
            foreach (var (id, item) in SchematicItemDelta.Index(screen.Items).Where(p => !existing.Contains(p.Key)))
            {
                string json = System.Text.RegularExpressions.Regex.Replace(SchematicJson.Formatter.Format(item),
                    "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", match => known.Contains(match.Value, StringComparison.Ordinal) ? match.Value : "new");
                result.Add(item.Descriptor.FullName + " " + path + " " + json);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    // The public preview reports exactly the planner's own plan for the same record and handshake.
    private static void RequirePlan(SchematicSynchronizationPlan expected, JsonElement preview, string because)
    {
        Assert.AreEqual(expected.CanPrepare, preview.GetProperty("canPrepare").GetBoolean(), because + " " + preview.GetRawText());
        Assert.AreEqual(expected.ErrorCode, preview.GetProperty("errorCode").GetString(), because + " " + preview.GetRawText());
        Assert.AreEqual(expected.CandidateXml, preview.GetProperty("candidateDesignXml").GetString(), because);
        CollectionAssert.AreEqual(expected.NativeOperations.Select(o => SchematicJson.Formatter.Format(o)).ToArray(),
            preview.GetProperty("nativeOperationsJson").EnumerateArray().Select(o => o.GetString()).ToArray(), because);
        Assert.AreEqual(expected.NativeConnectivityValidationRequired, preview.GetProperty("nativeConnectivityValidationRequired").GetBoolean(), because);
    }

    // Two probes on the declared project root whose single pins are drawn and not connected to anything.
    internal static string UnconnectedProbePair(string rootId, string projectName)
    {
        string Probe(string reference, string x) => $$"""
              (symbol (lib_id "Automation:Probe") (at {{x}} 76.2 0) (unit 1)
                (in_bom yes) (on_board yes) (dnp no) (uuid {{Guid.NewGuid():D}})
                (property "Reference" "{{reference}}" (at {{x}} 71.12 0) (effects (font (size 1.27 1.27))))
                (property "Value" "Probe" (at {{x}} 69.85 0) (effects (font (size 1.27 1.27))))
                (pin "1" (uuid {{Guid.NewGuid():D}}))
                (instances (project "{{projectName}}" (path "/{{rootId}}" (reference "{{reference}}") (unit 1)))))
            """;
        return $$"""
            (kicad_sch (version 20250114) (generator eeschema) (uuid {{Guid.NewGuid():D}}) (paper "A4")
              (lib_symbols (symbol "Automation:Probe"
                (pin_names (offset 0) hide) (in_bom yes) (on_board yes)
                (property "Reference" "TP" (at 0 3 0) (effects (font (size 1.27 1.27))))
                (property "Value" "Probe" (at 0 5 0) (effects (font (size 1.27 1.27))))
                (symbol "Probe_0_1" (circle (center 0 2.54) (radius 0.75) (stroke (width 0) (type default)) (fill (type none))))
                (symbol "Probe_1_1" (pin passive line (at 0 0 90) (length 2.54)
                  (name "1" (effects (font (size 1.27 1.27))))
                  (number "1" (effects (font (size 1.27 1.27))))))))
            {{Probe("TP1", "76.2")}}
            {{Probe("TP2", "127")}}
              (sheet_instances (path "/" (page "1"))))
            """;
    }

}
