using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
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

    // Decision n39ac0ccc5c9270f2 / ledger pbfcccd896f17cf27 against a real KiCad and the compiled MCP server. The
    // saved revision only connects the drawn, unconnected pins of two probes: exactly what an editor advertising
    // schematic.connection-realization.v1 would draw, and this KiCad does not advertise it (CN-1 §8.3). The
    // kicad_design_sync_plan preview classifies it with the handshake the server recorded when it attached or
    // reattached this instance, and with no attached instance it is today's plan. The automatic worker and apply
    // take their own live handshakes and classify it the same way: the general path, which journals the XML
    // publication and then refuses it because KiCad does not show the connection. KiCad is never changed. The
    // advertising case needs a test double (McpProcessTests.SyncPreviewClassifiesWithTheAttachedInstancesHandshakeOverStdio).
    private static async Task VerifyRecordedHandshakePlanning(NativeClient client, string registryState, string schematic,
        string evidence, CancellationToken token)
    {
        var live = await client.HandshakeAsync(token);
        string instanceId = live.InstanceId;
        // This step proves the unadvertised case only. The change that makes KiCad advertise the capability (CN-1 §8.3,
        // §16 step 3) must replace it with the native advertised-case proof, so it fails here rather than silently
        // proving less.
        Assert.IsFalse(live.Capabilities.Contains(SchematicConnectedAddition.NativeCapability),
            "KiCad now advertises " + SchematicConnectedAddition.NativeCapability + ": replace this unadvertised-case step with the native "
            + "advertised-case proof (the preview is the realization plan and the worker and apply realize it identically; ledger "
            + "pf1134ca32f913781). Capabilities: " + string.Join(",", live.Capabilities));
        var root = (await Ready(() => client.OpenRootSchematicAsync(schematic, token))).Document;
        await Ready(() => client.InvokeAsync<SaveDocument, Empty>(new() { Document = root.Clone() }, token));
        Task<CheckedSchematicState> Capture() => Ready(() => client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(
            new() { Document = root.Clone(), ProcessEpoch = client.Epoch }, token));
        var initial = await Capture();
        Assert.IsTrue(CheckedSchematicContract.FileCoverage(initial.State),
            "The saved root must have known, unchanged file baselines so apply can reach its classification: " + SchematicJson.Formatter.Format(initial.State));
        // The baseline declares no connection; the saved revision joins both probe pins (declared, never inferred).
        var connected = ProbeElectricalModel(initial.Electrical);
        var baseline = connected with { Engineering = connected.Engineering with { Circuit = connected.Engineering.Circuit with { Nets = [] } } };
        byte[] desired = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(connected, []));
        DesignRecoveryState Revision() => new(Guid.NewGuid(), Guid.Parse(instanceId),
            new(initial.State.Revision.Epoch, initial.State.Revision.Sequence), initial.Electrical.Hierarchy.TrackingComplete,
            baseline, desired, initial.Electrical.Hierarchy.Data.Clone(), [],
            BaselineElectrical: initial.Electrical.Clone(), ObservedElectrical: initial.Electrical.Clone());
        var reference = Revision();
        var wanted = DesignRecoveryStore.ReadDesired(reference);
        var advertised = live.Clone(); advertised.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
        var classifiedIfAdvertised = SchematicConnectedAddition.Classify(reference, wanted, advertised, token).Kind;
        var classified = SchematicConnectedAddition.Classify(reference, wanted, live, token).Kind;
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, classifiedIfAdvertised,
            "The saved revision must only add a connection over drawn pins, which an advertising editor would realize.");
        Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, classified);
        var today = SchematicSynchronizationPlanner.Plan(reference, token);
        Assert.IsTrue(today.CanPrepare, today.ErrorCode + ": " + today.ErrorMessage);
        Assert.IsFalse(today.NativeConnectionRealizationRequired);
        Assert.IsEmpty(today.NativeOperations, "The general path has nothing to draw.");
        Assert.IsTrue(today.NativeConnectivityValidationRequired, "The general path leaves the new connection to native validation.");
        Assert.AreEqual(today.CandidateXml, SchematicSynchronizationPlanner.Plan(reference, live, token).CandidateXml,
            "This KiCad's own handshake keeps today's plan.");

        string folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(registryState)!, "handshake-planning")).FullName;
        (string Recovery, string Design, string Token) Record(string name)
        {
            string recovery = Path.Combine(folder, name + ".recovery.json"), design = Path.Combine(folder, name + ".design.xml");
            File.WriteAllBytes(design, desired);
            return (recovery, design, new DesignRecoveryStore(recovery).Save(Revision(), null).RevisionToken);
        }
        async Task<JsonElement> Preview(StdioMcpFixture mcp, (string Recovery, string Design, string Token) target) =>
            (await mcp.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = target.Recovery, expectedRevisionToken = target.Token }))
                .GetProperty("structuredContent").Clone();
        var preview = Record("preview");
        var before = await Capture();
        JsonElement unattached, afterAttach, afterRestart, afterReattach;
        (string? Code, Guid? Operation) worker;
        string? applied;
        var journaled = new Dictionary<string, object>();
        await using (var mcp = await StdioMcpFixture.StartAsync(registryState, Path.Combine(evidence, "handshake-planning-attach.stderr.log"), token))
        {
            unattached = await Preview(mcp, preview);
            Assert.IsTrue(unattached.GetProperty("canPrepare").GetBoolean(), unattached.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, unattached.GetProperty("errorCode").ValueKind, unattached.GetRawText());
            Assert.AreEqual(today.CandidateXml, unattached.GetProperty("candidateDesignXml").GetString(),
                "Without an attached instance the preview publishes exactly today's candidate.");
            Assert.AreEqual(0, unattached.GetProperty("nativeOperationsJson").GetArrayLength());
            Assert.IsTrue(unattached.GetProperty("nativeConnectivityValidationRequired").GetBoolean());
            RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
            afterAttach = await Preview(mcp, preview);
            Assert.AreEqual(unattached.GetRawText(), afterAttach.GetRawText(),
                "The handshake recorded at attach does not advertise realization, so the preview stays today's plan.");

            // The automatic worker takes its own handshake, plans, and applies the general plan.
            var automatic = Record("worker");
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
            Assert.AreEqual("Paused", status.GetProperty("phase").GetString(), status.GetRawText());
            worker = (status.GetProperty("errorCode").GetString(),
                status.GetProperty("operationId").ValueKind == JsonValueKind.Null ? null : (Guid?)status.GetProperty("operationId").GetGuid());

            // Apply, called directly, takes its own handshake too.
            var direct = Record("apply");
            var apply = await mcp.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = direct.Recovery,
                designPath = direct.Design, expectedRevisionToken = direct.Token, operationId = Guid.NewGuid().ToString("D") });
            Assert.IsTrue(apply.GetProperty("isError").GetBoolean(), apply.GetRawText());
            applied = apply.GetProperty("structuredContent").GetProperty("errorCode").GetString();

            // Both classified the revision as the preview did: the general path journals the XML publication (no
            // realization layout and no native batch) and refuses it because KiCad does not show the new connection.
            foreach (var (name, target, code) in new[] { ("worker", automatic, worker.Code), ("apply", direct, applied) })
            {
                Assert.AreEqual("native_sync_connectivity_mismatch", code, $"The {name} must refuse the general plan's XML publication.");
                var journal = new DesignRecoveryStore(target.Recovery).Read()!.State;
                Assert.IsNotNull(journal.PendingPublication, $"The {name} journals the general path's XML publication.");
                Assert.IsNull(journal.PendingLayout, $"The {name} must not plan a connection realization.");
                Assert.IsNull(journal.PendingMutation, $"The {name} has no native batch to send.");
                CollectionAssert.AreEqual(desired, File.ReadAllBytes(target.Design), $"The {name} must not publish XML.");
                journaled[name] = new { publication = journal.PendingPublication is not null, layout = journal.PendingLayout is not null,
                    mutation = journal.PendingMutation is not null };
            }
            Assert.AreEqual(worker.Operation, new DesignRecoveryStore(automatic.Recovery).Read()!.State.PendingPublication!.OperationId,
                "The worker journaled the operation it paused on.");
        }
        // A restarted server holds only the saved registration, which is not a handshake, until it reattaches.
        await using (var mcp = await StdioMcpFixture.StartAsync(registryState, Path.Combine(evidence, "handshake-planning-reattach.stderr.log"), token))
        {
            afterRestart = await Preview(mcp, preview);
            Assert.AreEqual(unattached.GetRawText(), afterRestart.GetRawText(),
                "A saved registration is not a recorded handshake: the preview is today's plan.");
            RequireToolSuccess(await mcp.Tool("kicad_instance_reattach", new { instanceId }));
            afterReattach = await Preview(mcp, preview);
            Assert.AreEqual(unattached.GetRawText(), afterReattach.GetRawText(),
                "The handshake recorded at reattach does not advertise realization either.");
        }
        var after = await Capture();
        Assert.AreEqual(before.State, after.State, "Planning, the worker and apply must leave KiCad unchanged.");
        Assert.AreEqual(preview.Token, new DesignRecoveryStore(preview.Recovery).Read()!.RevisionToken, "The preview writes nothing.");
        static string Sha256(JsonElement plan) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(plan.GetRawText())));
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-handshake-planning.json"), JsonSerializer.Serialize(new
        {
            instanceId, nativeCapabilities = live.Capabilities.ToArray(),
            classification = classified.ToString(), classificationIfAdvertised = classifiedIfAdvertised.ToString(),
            todayCandidateXmlSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(today.CandidateXml!))),
            previewSha256 = new { withoutAttachment = Sha256(unattached), afterAttach = Sha256(afterAttach),
                afterRestart = Sha256(afterRestart), afterReattach = Sha256(afterReattach) },
            workerErrorCode = worker.Code, applyErrorCode = applied, journaled,
            nativeStateSha256 = new { before = before.State.StateSha256, after = after.State.StateSha256 }
        }), token);
        Console.WriteLine($"Recorded handshake planning {instanceId}: preview, worker and apply all took the general path.");

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
