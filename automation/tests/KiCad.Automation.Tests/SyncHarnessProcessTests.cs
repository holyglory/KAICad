using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Protocol = KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SyncHarnessProcessTests
{
    internal static ProcessStartInfo StartInfo(string stage = "none", string? marker = null)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("The compiled source harness requires its automation checkout.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(Path.Combine(root.FullName, "tests", "KiCad.Automation.SyncHarness", "bin", configuration,
            "net10.0", "KiCad.Automation.SyncHarness.dll"));
        start.Environment["KICAD_SYNC_HARNESS_PAUSE_STAGE"] = stage;
        if (marker is null) start.Environment.Remove("KICAD_SYNC_HARNESS_PAUSE_MARKER");
        else start.Environment["KICAD_SYNC_HARNESS_PAUSE_MARKER"] = marker;
        return start;
    }

    internal static ProcessStartInfo ProductionStartInfo()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KiCad.Automation.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("The compiled automation checkout is required.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "KiCad.Automation.Mcp", "bin", configuration, "net10.0", "kicad-mcp.dll"));
        return start;
    }

    [TestMethod]
    public async Task QualificationHostUsesRealStdioAndTheServiceValidationBoundary()
    {
        string root = Directory.CreateTempSubdirectory("sync-host-contract-").FullName;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var host = await StdioMcpFixture.StartAsync(StartInfo(), Path.Combine(root, "state"), Path.Combine(root, "stderr.log"), timeout.Token);
            var tools = await host.ListTools(null);
            Assert.IsTrue(tools.GetProperty("tools").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "kicad_design_sync_apply"));
            var result = await host.Tool("kicad_design_sync_apply", new
            {
                instanceId = Guid.NewGuid().ToString("D"), recoveryPath = Path.Combine(root, "missing.json"),
                designPath = Path.Combine(root, "design.xml"), expectedRevisionToken = "missing", operationId = Guid.NewGuid().ToString("D")
            });
            Assert.IsTrue(result.GetProperty("isError").GetBoolean());
            Assert.AreEqual("missing_design_recovery", result.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            Assert.IsFalse(File.Exists(Path.Combine(root, "design.xml")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "missing.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    // Ledger pbea3100a7414c90d (decision n39ac0ccc5c9270f2). The test sync host, which the native interruption journeys
    // drive, records each instance's handshake at attach exactly as the production server does, so its
    // kicad_design_sync_plan preview classifies a saved revision the way its own kicad_design_sync_apply does. The saved
    // revision only joins two drawn pins. No KiCad build advertises schematic.connection-realization.v1 yet (CN-1 §8.3),
    // so a scripted editor on the real NNG transport stands in for one, and the check holds whichever way a real KiCad
    // answers. Must-catch: a host without the recorded handshakes previews today's plan while its apply realizes the
    // connection, so the attached, advertising preview below fails.
    [TestMethod]
    public async Task HostPreviewClassifiesASavedRevisionLikeItsApply()
    {
        string root = Directory.CreateTempSubdirectory("sync-host-handshake-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var state = ConnectionOnlyRevision(project);
            string instanceId = state.InstanceId.ToString("D");
            using var editor = new ScriptedNngEditor(root, state, Path.Combine(project, "fixture.kicad_pro"));
            string handshake = Protocol.GetAutomationSession.Descriptor.FullName, capture = Protocol.ReadCheckedSchematicState.Descriptor.FullName,
                measure = Protocol.MeasureSchematicPlacement.Descriptor.FullName;
            var today = SchematicSynchronizationPlanner.Plan(state);
            var realizing = SchematicSynchronizationPlanner.Plan(state, editor.Session(advertises: true));
            Assert.IsTrue(today.CanPrepare, today.ErrorMessage);
            Assert.IsFalse(today.NativeConnectionRealizationRequired);
            Assert.IsTrue(realizing.NativeConnectionRealizationRequired, realizing.ErrorCode + ": " + realizing.ErrorMessage);

            int records = 0;
            (string Recovery, string Design, string Token) Record()
            {
                string folder = Directory.CreateDirectory(Path.Combine(root, "records", (++records).ToString())).FullName;
                string recovery = Path.Combine(folder, "recovery.json"), design = Path.Combine(folder, "design.xml");
                File.WriteAllBytes(design, state.DesiredFileBytes);
                return (recovery, design, new DesignRecoveryStore(recovery).Save(state, null).RevisionToken);
            }
            await using var host = await StdioMcpFixture.StartAsync(StartInfo(), Path.Combine(root, "host-state"),
                Path.Combine(root, "host.stderr.log"), timeout.Token);
            var preview = Record();
            async Task<JsonElement> Preview()
            {
                var result = await host.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = preview.Recovery,
                    expectedRevisionToken = preview.Token });
                Assert.IsFalse(result.TryGetProperty("isError", out var failed) && failed.GetBoolean(), result.GetRawText());
                return result.GetProperty("structuredContent");
            }
            async Task<(string? Code, string[] Requests, DesignRecoveryState Record)> Apply()
            {
                var target = Record(); int from = editor.Requests.Length;
                var result = await host.Tool("kicad_design_sync_apply", new { instanceId, recoveryPath = target.Recovery,
                    designPath = target.Design, expectedRevisionToken = target.Token, operationId = Guid.NewGuid().ToString("D") });
                Assert.IsTrue(result.GetProperty("isError").GetBoolean(), "The scripted editor cannot complete an application: " + result.GetRawText());
                return (result.GetProperty("structuredContent").GetProperty("errorCode").GetString(), editor.Requests[from..],
                    new DesignRecoveryStore(target.Recovery).Read()!.State);
            }
            // Whether apply went on to measure the checkpoint for a realization (CN-1 §9.1) or took the general path.
            bool Realized(string[] requests) => requests.Contains(measure);

            // Without an attached instance the preview is today's plan, and nothing contacts the editor.
            McpProcessTests.RequirePreviewPlan(today, await Preview());
            Assert.IsEmpty(editor.Requests);

            // Attached to an editor that advertises realization: the preview is the realization plan and names the
            // planned connections, and apply realizes the same revision.
            editor.Advertises = true;
            var attached = await host.Tool("kicad_instance_attach", new { endpoint = editor.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachFailed) && attachFailed.GetBoolean(), attached.GetRawText());
            CollectionAssert.AreEqual(new[] { handshake }, editor.Requests, "Attaching reads one handshake.");
            var recorded = await Preview();
            McpProcessTests.RequirePreviewPlan(realizing, recorded);
            Assert.HasCount(1, editor.Requests, "The preview never contacts KiCad.");
            // The published values for this revision, independent of the planner's records: the new net SIG is local,
            // its one shared channel pin gets the only stub, both channel instances cross to the root through a new
            // sheet pin each, and apply must prove one native group of the two instances' pin.
            var summary = recorded.GetProperty("connectionIntent");
            var signal = summary.GetProperty("nets").EnumerateArray().Single();
            Assert.AreEqual("SIG", signal.GetProperty("name").GetString(), summary.GetRawText());
            Assert.AreEqual("Local", signal.GetProperty("scope").GetString(), summary.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, signal.GetProperty("globalName").ValueKind, summary.GetRawText());
            Assert.AreEqual(2, signal.GetProperty("addedPins").GetArrayLength(), summary.GetRawText());
            var islands = summary.GetProperty("screens").EnumerateArray().SelectMany(s => s.GetProperty("islands").EnumerateArray()).ToArray();
            Assert.IsNotEmpty(islands, summary.GetRawText());
            foreach (var island in islands)
            {
                Assert.AreEqual("Local", island.GetProperty("scope").GetString(), summary.GetRawText());
                Assert.AreEqual("SIG", island.GetProperty("labelText").GetString(), "A new net is labelled with its own name: " + summary.GetRawText());
                Assert.IsFalse(island.GetProperty("joinRequired").GetBoolean(), "A new net has no existing wiring to join: " + summary.GetRawText());
            }
            var members = islands.SelectMany(i => i.GetProperty("members").EnumerateArray()).ToArray();
            var stubbed = members.Single(m => m.GetProperty("requiresStub").GetBoolean());
            Assert.HasCount(1, members, "Both channel instances share one drawn pin: " + summary.GetRawText());
            Assert.AreEqual("Signal", stubbed.GetProperty("role").GetString(), summary.GetRawText());
            Assert.IsFalse(stubbed.GetProperty("createdSymbol").GetBoolean(), summary.GetRawText());
            Assert.IsFalse(stubbed.GetProperty("alreadyConnected").GetBoolean(), summary.GetRawText());
            Assert.AreEqual(JsonValueKind.Null, stubbed.GetProperty("powerName").ValueKind, summary.GetRawText());
            var ports = summary.GetProperty("ports").EnumerateArray().ToArray();
            Assert.HasCount(2, ports, summary.GetRawText());
            Assert.IsTrue(ports.All(p => p.GetProperty("portText").GetString() == "SIG" && !p.GetProperty("sheetPinExists").GetBoolean()
                && !p.GetProperty("uplinkLabelExists").GetBoolean()), summary.GetRawText());
            Assert.AreEqual(1, summary.GetProperty("expectedGroupCount").GetInt32(), summary.GetRawText());
            Assert.AreEqual(2, summary.GetProperty("expectedPinCount").GetInt32(), summary.GetRawText());
            Assert.AreEqual(0, summary.GetProperty("createdSymbolIds").GetArrayLength(), "Nothing is created: " + summary.GetRawText());
            var realized = await Apply();
            CollectionAssert.AreEqual(new[] { handshake, capture }, realized.Requests.Take(2).ToArray(), string.Join(", ", realized.Requests));
            Assert.IsGreaterThan(2, realized.Requests.Length, "After the capture the realization measures the checkpoint: " + string.Join(", ", realized.Requests));
            Assert.IsTrue(realized.Requests.Skip(2).All(r => r == measure),
                "After the capture the realization only measures the checkpoint: " + string.Join(", ", realized.Requests));
            Assert.AreEqual(SchematicConnectionErrors.RealizationMeasurementUnsupported, realized.Code, "Apply stops at the refused measurement.");
            Assert.IsFalse(realized.Record.HasPendingWork, "Nothing is journaled before the editor has been measured.");
            Assert.IsTrue(recorded.GetProperty("connectionRealizationRequired").GetBoolean());
            Assert.IsTrue(Realized(realized.Requests), "The preview classifies like apply: both realize the connection.");

            // Reattaching to the editor once it stops advertising refreshes the record: the preview is today's plan again,
            // and apply takes today's general path, journaling the XML publication and refusing it because the editor
            // does not show the new connection.
            editor.Advertises = false;
            var reattached = await host.Tool("kicad_instance_reattach", new { instanceId });
            Assert.IsFalse(reattached.TryGetProperty("isError", out var reattachFailed) && reattachFailed.GetBoolean(), reattached.GetRawText());
            var refreshed = await Preview();
            McpProcessTests.RequirePreviewPlan(today, refreshed);
            var general = await Apply();
            CollectionAssert.AreEqual(new[] { handshake, capture, capture }, general.Requests, string.Join(", ", general.Requests));
            Assert.AreEqual("native_sync_connectivity_mismatch", general.Code);
            Assert.IsNotNull(general.Record.PendingPublication, "Apply journals the general path's XML publication.");
            Assert.IsNull(general.Record.PendingLayout, "Apply must not plan a connection realization.");
            Assert.IsNull(general.Record.PendingMutation, "Apply has no native batch to send.");
            Assert.IsFalse(refreshed.GetProperty("connectionRealizationRequired").GetBoolean());
            Assert.IsFalse(Realized(general.Requests), "The preview classifies like apply: neither realizes the connection.");

            CollectionAssert.DoesNotContain(editor.Requests, Protocol.CheckedSchematicBatch.Descriptor.FullName, "Nothing is drawn.");
            CollectionAssert.DoesNotContain(editor.Requests, Protocol.CheckedSaveDocument.Descriptor.FullName, "Nothing is saved.");
            Assert.AreEqual(preview.Token, new DesignRecoveryStore(preview.Recovery).Read()!.RevisionToken, "The preview writes nothing.");
            Console.WriteLine($"Test sync host: advertised preview realizes, apply {realized.Code} after [{string.Join(", ", realized.Requests)}]; "
                + $"unadvertised preview is today's plan, apply {general.Code} after [{string.Join(", ", general.Requests)}].");
        }
        finally { Directory.Delete(root, true); }
    }

    // Ledger pe84e513e13928d94 (CN-1 §9.5 and the §15 "Power" example) through the compiled production server over STDIO.
    // Saved XML that adds a part together with its wiring is realized in KiCad too, so the preview names those planned
    // connections as well: the new IC U9 joins the global power net VCC, which already holds the power symbol #PWR1 and
    // the hidden VCC pin of the existing IC U8. This pins the published values the connection-only revision cannot
    // reach: a global name, the power-carrier and hidden-power roles, members on a created symbol and the created symbol
    // itself. The planner's bench (SchematicConnectionIntentBuilderTests) builds the revision; no KiCad build advertises
    // schematic.connection-realization.v1 yet (CN-1 §8.3), so a scripted editor on the real NNG transport gives the
    // handshake recorded at attach. Must-catch: a summary that drops or renames a field, or publishes another scope or
    // role name than CN-1 §5.9, fails here.
    [TestMethod]
    public async Task PreviewNamesTheGlobalConnectionsOfAPartTheXmlAddsOverStdio()
    {
        string root = Directory.CreateTempSubdirectory("sync-power-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var bench = new SchematicConnectionIntentBuilderTests.Bench();
            Guid resistor = bench.Part("R", SchematicConnectionIntentBuilderTests.Passive("1"), SchematicConnectionIntentBuilderTests.Passive("2"));
            Guid power = bench.Part("VCC", Kiapi.Schematic.Types.SchematicSymbolType.SstGlobalPower,
                new SchematicConnectionIntentBuilderTests.BenchPin("1", "VCC", 1, Kiapi.Common.Types.ElectricalPinType.EptPowerInput, false));
            Guid ic = bench.Part("IC", new SchematicConnectionIntentBuilderTests.BenchPin("1", "OUT", 1, Kiapi.Common.Types.ElectricalPinType.EptOutput),
                new SchematicConnectionIntentBuilderTests.BenchPin("2", "VCC", 1, Kiapi.Common.Types.ElectricalPinType.EptPowerInput, false),
                new SchematicConnectionIntentBuilderTests.BenchPin("3", "IN", 1, Kiapi.Common.Types.ElectricalPinType.EptInput));
            Guid pwr = bench.Component(power, "#PWR1", value: "VCC"), u8 = bench.Component(ic, "U8"), r2 = bench.Component(resistor, "R2");
            var vcc = new CircuitNet(Guid.NewGuid(), "VCC", [new(pwr, "1"), new(u8, "2")]);
            var baseline = bench.State([vcc]);
            var (created, u9) = bench.Create(baseline.Baseline, ic, "U9", SchematicConnectionIntentBuilderTests.BenchSheet.Root);
            var saved = SchematicConnectionIntentBuilderTests.Revise(baseline, _ => SchematicConnectionIntentBuilderTests.WithNets(created,
                vcc with { Pins = [.. vcc.Pins, new(u9, "2"), new(u9, "1"), new(r2, "1")] })).Saved;

            // The expected plans come from the record exactly as the server reads it.
            string recovery = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "record")).FullName, "recovery.json");
            string token = new DesignRecoveryStore(recovery).Save(saved, null).RevisionToken;
            var state = new DesignRecoveryStore(recovery).Read()!.State;
            string instanceId = state.InstanceId.ToString("D");
            using var editor = new ScriptedNngEditor(root, state, Path.Combine(root, "project", "bench.kicad_pro"));
            var today = SchematicSynchronizationPlanner.Plan(state);
            var realizing = SchematicSynchronizationPlanner.Plan(state, editor.Session(advertises: true));
            Assert.IsNull(today.Connections, "Without the capability nothing is planned for wiring.");
            var planned = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(realizing);
            var candidate = realizing.Candidate!;
            Guid[] u9Symbols = [.. candidate.SymbolBindings.Where(b => candidate.Engineering.Circuit.Symbols
                .Any(s => s.Id == b.SymbolOccurrenceId && s.ComponentId == u9)).Select(b => b.NativeObjectId).Distinct().Order()];
            CollectionAssert.AreEqual(u9Symbols, planned.CreatedSymbolIds.ToArray(), "The plan creates exactly U9's symbol.");

            await using var server = await StdioMcpFixture.StartAsync(ProductionStartInfo(), Path.Combine(root, "server-state"),
                Path.Combine(root, "server.stderr.log"), timeout.Token);
            async Task<JsonElement> Preview() => (await server.Tool("kicad_design_sync_plan", new { instanceId, recoveryPath = recovery,
                expectedRevisionToken = token })).GetProperty("structuredContent");

            // Unattached, the preview is today's plan and names no connections.
            var unattached = await Preview();
            McpProcessTests.RequirePreviewPlan(today, unattached);
            Assert.IsFalse(unattached.GetProperty("connectionRealizationRequired").GetBoolean(), unattached.GetRawText());

            // Attached to an editor that advertises realization, the preview is the realization plan.
            editor.Advertises = true;
            var attached = await server.Tool("kicad_instance_attach", new { endpoint = editor.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var attachFailed) && attachFailed.GetBoolean(), attached.GetRawText());
            var shown = await Preview();
            McpProcessTests.RequirePreviewPlan(realizing, shown);
            CollectionAssert.AreEqual(new[] { Protocol.GetAutomationSession.Descriptor.FullName }, editor.Requests,
                "Attaching reads one handshake and the preview never contacts KiCad.");

            // The published values, independent of the planner's records.
            var summary = shown.GetProperty("connectionIntent");
            string json = summary.GetRawText();
            var net = summary.GetProperty("nets").EnumerateArray().Single();
            Assert.AreEqual("VCC", net.GetProperty("name").GetString(), json);
            Assert.AreEqual("Global", net.GetProperty("scope").GetString(), json);
            Assert.AreEqual("VCC", net.GetProperty("globalName").GetString(), json);
            CollectionAssert.AreEquivalent(new[] { (u9, "2"), (u9, "1"), (r2, "1") }, net.GetProperty("addedPins").EnumerateArray()
                .Select(p => (p.GetProperty("componentId").GetGuid(), p.GetProperty("pin").GetString()!)).ToArray(), json);
            Assert.AreEqual(0, summary.GetProperty("ports").GetArrayLength(), "A global name needs no sheet crossing: " + json);
            var island = summary.GetProperty("screens").EnumerateArray().Single().GetProperty("islands").EnumerateArray().Single();
            Assert.AreEqual("Global", island.GetProperty("scope").GetString(), json);
            Assert.AreEqual("VCC", island.GetProperty("labelText").GetString(), json);
            Assert.IsTrue(island.GetProperty("anchorHasMatchingDriver").GetBoolean(), "The power symbol already carries VCC: " + json);
            Assert.IsFalse(island.GetProperty("joinRequired").GetBoolean(), json);
            var members = island.GetProperty("members").EnumerateArray().ToArray();
            Assert.HasCount(5, members, json);
            var createdIds = summary.GetProperty("createdSymbolIds").EnumerateArray().Select(p => p.GetGuid()).ToArray();
            CollectionAssert.AreEqual(u9Symbols, createdIds, json);
            foreach (var (component, pin, role, onCreated, connected, stub, name) in new (Guid, string, string, bool, bool, bool, string?)[]
            {
                (pwr, "1", "PowerCarrier", false, true, false, "VCC"),
                (u8, "2", "ImplicitPower", false, true, false, "VCC"),
                (u9, "2", "ImplicitPower", true, false, false, "VCC"),
                (u9, "1", "Signal", true, false, true, null),
                (r2, "1", "Signal", false, false, true, null)
            })
            {
                string what = (component == pwr ? "#PWR1" : component == u8 ? "U8" : component == u9 ? "U9" : "R2") + "." + pin;
                var member = members.Single(m => m.GetProperty("componentId").GetGuid() == component && m.GetProperty("pin").GetString() == pin);
                Assert.AreEqual(role, member.GetProperty("role").GetString(), what + ": " + json);
                Assert.AreEqual(onCreated, member.GetProperty("createdSymbol").GetBoolean(), what + ": " + json);
                Assert.AreEqual(onCreated, createdIds.Contains(member.GetProperty("symbolId").GetGuid()), what + ": " + json);
                Assert.AreEqual(connected, member.GetProperty("alreadyConnected").GetBoolean(), what + ": " + json);
                Assert.AreEqual(stub, member.GetProperty("requiresStub").GetBoolean(), "Only signal pins get stubs; hidden power pins never do: " + what);
                Assert.AreEqual(name, member.GetProperty("powerName").GetString(), what + ": " + json);
            }
            Assert.AreEqual(2, summary.GetProperty("expectedGroupCount").GetInt32(), "VCC, and U9's unconnected input alone: " + json);
            Assert.AreEqual(6, summary.GetProperty("expectedPinCount").GetInt32(), json);
            Assert.AreEqual(token, new DesignRecoveryStore(recovery).Read()!.RevisionToken, "The preview writes nothing.");
            Console.WriteLine($"Production preview of a part the XML adds with its wiring: net VCC Global VCC, {members.Length} members, "
                + $"{members.Count(m => m.GetProperty("requiresStub").GetBoolean())} stubs, created symbols [{string.Join(", ", createdIds)}].");
        }
        finally { Directory.Delete(root, true); }
    }

    // The planning fixture with one saved XML revision that only joins two drawn pins (as in
    // AutomaticDesignSynchronizationTests), moved into a real project folder where an automatic worker keeps its
    // history. The editor checkpoint carries one exact native revision, and every sheet reports the connection
    // grid realization draws on (CN-1 §6.1). McpProcessTests keeps a private copy until the integration owner
    // deletes it and calls this one as SyncHarnessProcessTests.ConnectionOnlyRevision.
    internal static DesignRecoveryState ConnectionOnlyRevision(string projectDirectory)
    {
        var state = SchematicSynchronizationPlanTests.Fixture();
        var schematic = state.Baseline.Schematic.Clone();
        schematic.Document.Project.Path = projectDirectory;
        foreach (var screen in schematic.Instances)
        {
            if (screen.Metadata.Document?.Project is { } owner) owner.Path = projectDirectory;
            screen.Metadata.Formatting = SchematicFormattingTests.Formatting();
        }
        var revision = new Protocol.DocumentRevision { Epoch = Guid.NewGuid().ToString("D"), Sequence = state.NativeRevision.Sequence };
        var baselineElectrical = state.BaselineElectrical!.Clone();
        baselineElectrical.Hierarchy.Data = schematic.Clone(); baselineElectrical.Hierarchy.Revision = revision.Clone();
        var observedElectrical = state.ObservedElectrical!.Clone();
        observedElectrical.Hierarchy.Data = schematic.Clone(); observedElectrical.Hierarchy.Revision = revision.Clone();
        state = state with { Baseline = state.Baseline with { Schematic = schematic }, Observed = schematic.Clone(),
            NativeRevision = new(revision.Epoch, revision.Sequence), BaselineElectrical = baselineElectrical, ObservedElectrical = observedElectrical };
        var circuit = state.Baseline.Engineering.Circuit;
        var design = state.Baseline with { Engineering = state.Baseline.Engineering with { Circuit = circuit with { Nets =
            [.. circuit.Nets, new CircuitNet(Guid.NewGuid(), "SIG", [new(circuit.Components[0].Id, "1"), new(circuit.Components[1].Id, "1")])] } } };
        return state with { DesiredFileBytes = System.Text.Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
    }

    internal static async Task WaitForMarkerAsync(string path, CancellationToken token)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        watcher.Created += (_, _) => { if (File.Exists(path)) ready.TrySetResult(); };
        watcher.Changed += (_, _) => { if (File.Exists(path)) ready.TrySetResult(); };
        watcher.Renamed += (_, _) => { if (File.Exists(path)) ready.TrySetResult(); };
        watcher.Error += (_, error) => ready.TrySetException(error.GetException());
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path)) ready.TrySetResult();
        await ready.Task.WaitAsync(token);
    }
}

/// <summary>An editor on the real NNG request/reply transport. It answers its handshake, the checked checkpoint
/// capture and the electrical observation of one saved state, refuses every other request, and records each
/// request by message name. It stands in only for a KiCad that advertises schematic.connection-realization.v1,
/// which no native build does yet (CN-1 §8.3). McpProcessTests keeps a private nested copy until the integration
/// owner deletes it; its unqualified uses then resolve to this class.</summary>
internal sealed class ScriptedNngEditor : IDisposable
{
    private readonly Nng.Socket socket;
    private readonly Task serving;
    private readonly object gate = new();
    private readonly List<string> requests = [];
    private readonly string instanceId, projectPath, eventEndpoint, epoch = Guid.NewGuid().ToString("D");
    private readonly Protocol.CheckedSchematicState checkpoint;
    private readonly Protocol.SchematicElectricalState electrical;
    private volatile bool advertises;
    internal string Endpoint { get; }
    internal bool Advertises { get => advertises; set => advertises = value; }
    internal string[] Requests { get { lock (gate) return [.. requests]; } }

    internal ScriptedNngEditor(string directory, DesignRecoveryState state, string projectPath)
    {
        instanceId = state.InstanceId.ToString("D"); this.projectPath = projectPath;
        Endpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "editor.sock"));
        // Nothing publishes here: an automatic worker's event subscription waits, which is all these checks need.
        eventEndpoint = NativeIpcEndpoint.FromSocketPath(Path.Combine(directory, "events.sock"));
        electrical = state.ObservedElectrical!.Clone();
        checkpoint = new Protocol.CheckedSchematicState
        {
            Electrical = electrical.Clone(),
            State = new() { Document = state.Baseline.Schematic.Document.Clone(), ProcessEpoch = epoch,
                NativeIdentity = Guid.NewGuid().ToString("D"), Revision = electrical.Hierarchy.Revision.Clone(),
                Scope = Protocol.DocumentLifecycleScope.DlsSchematicHierarchy, ProjectSettingsIncluded = true,
                StateSha256 = new string('a', 64) }
        };
        string file = Path.Combine(Path.GetDirectoryName(projectPath)!, "fixture.kicad_sch");
        checkpoint.State.NativeFiles.Add(file);
        checkpoint.State.FileBaselines.Add(new Protocol.NativeFileBaselineState { Path = file, BaselinePath = file,
            BaselineKnown = true, BaselineExists = true, BaselineSha256 = new string('b', 64), BaselineBytes = 1,
            CurrentKnown = true, CurrentExists = true, CurrentSha256 = new string('b', 64), CurrentBytes = 1,
            Status = Protocol.NativeFileBaselineStatus.NfbsUnchanged });
        Nng.Check(Nng.nng_rep0_open(out socket));
        try
        {
            Nng.Check(Nng.nng_setopt_size(socket, "recv-size-max", 8 * 1024 * 1024));
            Nng.Check(Nng.nng_listen(socket, Endpoint, IntPtr.Zero, 0));
        }
        catch { Nng.nng_close(socket); throw; }
        serving = Task.Factory.StartNew(Serve, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal Protocol.AutomationSession Session(bool advertises)
    {
        var session = new Protocol.AutomationSession { ProtocolVersion = 1, InstanceId = instanceId, ProjectPath = projectPath,
            Epoch = epoch, EventEndpoint = eventEndpoint, EventEpoch = epoch };
        session.Capabilities.Add("session.info");
        if (advertises) session.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
        return session;
    }

    private void Serve()
    {
        // Closing the socket ends the blocked receive.
        while (true)
        {
            nuint size = 0;
            if (Nng.nng_recv(socket, out IntPtr buffer, ref size, 1) != 0) return;
            byte[] request = new byte[checked((int)size)];
            try { System.Runtime.InteropServices.Marshal.Copy(buffer, request, 0, request.Length); }
            finally { Nng.nng_free(buffer, size); }
            byte[] reply = Answer(request);
            if (Nng.nng_send(socket, reply, (nuint)reply.Length, 0) != 0) return;
        }
    }

    private byte[] Answer(byte[] bytes)
    {
        var message = Kiapi.Common.ApiRequest.Parser.ParseFrom(bytes).Message;
        lock (gate) requests.Add(message.TypeUrl[(message.TypeUrl.LastIndexOf('/') + 1)..]);
        Google.Protobuf.IMessage? reply = message.Is(Protocol.GetAutomationSession.Descriptor) ? Session(advertises)
            : message.Is(Protocol.ReadCheckedSchematicState.Descriptor) ? checkpoint.Clone()
            : message.Is(Protocol.ReadSchematicElectricalState.Descriptor) ? electrical.Clone() : null;
        var response = new Kiapi.Common.ApiResponse
        {
            Header = new() { KicadToken = epoch },
            Status = new() { Status = (Kiapi.Common.ApiStatusCode)(reply is null ? 3 : 1),
                ErrorMessage = reply is null ? "Refused by the scripted editor" : "" }
        };
        if (reply is not null) response.Message = Any.Pack(reply);
        return Google.Protobuf.MessageExtensions.ToByteArray(response);
    }

    public void Dispose()
    {
        Nng.nng_close(socket);
        try { serving.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }
}
