using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Schematic.Commands;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyElectricalState(NativeClient client, DocumentSpecifier document,
        ElectricalFixture fixture, string evidence, string instanceId, CancellationToken token)
    {
        var request = new ReadSchematicElectricalState { Document = document };
        var before = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token);
        var view = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        var state = await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token);
        Assert.AreEqual(before.Revision, state.Hierarchy.Revision);
        Assert.AreEqual(document, state.Hierarchy.Data.Document);
        Assert.IsFalse(state.Hierarchy.TrackingComplete);
        var model = ProbeElectricalModel(state);
        var comparison = SchematicElectricalComparison.Compare(model, state, []);
        Assert.IsTrue(comparison.PinBindingsComplete, string.Join(',', comparison.Issues.Select(i => i.Code)));
        Assert.IsTrue(comparison.ConnectivityEquivalent);
        var net = state.Nets.Single(n => n.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinA));
        Assert.IsTrue(net.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.PinB));
        Assert.IsTrue(net.Sheets.SelectMany(s => s.Items).Any(id => id.Value == fixture.Wire));
        request.ExpectedRevision = state.Hierarchy.Revision.Clone();
        Assert.AreEqual(state, await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token));
        var stale = request.Clone(); stale.ExpectedRevision.Sequence++;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(stale, token))).Status);
        var wrong = request.Clone(); wrong.Document.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(wrong, token));
        var future = request.Clone(); future.SchemaVersion = 99;
        Assert.AreEqual(3, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(future, token))).Status);

        var commit = await client.InvokeAsync<BeginCommit, BeginCommitResponse>(new() { Header = new() { Document = document } }, token);
        Assert.AreEqual(7, (await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
            client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token))).Status);
        await client.InvokeAsync<EndCommit, EndCommitResponse>(new() { Header = new() { Document = document }, Id = commit.Id, Action = (CommitAction)2 }, token);
        Assert.AreEqual(state, await client.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(request, token));
        Assert.AreEqual(before, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token));
        var afterView = await client.InvokeAsync<CaptureSchematicPreview, SchematicPreview>(new() { Document = document }, token);
        Assert.AreEqual(view.Viewport, afterView.Viewport);
        Assert.AreEqual(view.Document, afterView.Document);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-electrical-state.json"), SchematicJson.Formatter.Format(state), token);

        await using var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-electrical-mcp"),
            Path.Combine(evidence, instanceId + "-electrical-mcp.stderr.log"), token);
        RequireToolSuccess(await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId }));
        var result = await mcp.Tool("kicad_schematic_electrical_state", new { instanceId, requestJson = SchematicJson.Formatter.Format(request) });
        RequireToolSuccess(result);
        var actual = SchematicJson.Parser.Parse<SchematicElectricalState>(result.GetProperty("structuredContent").GetProperty("state").GetRawText());
        Assert.AreEqual(state, actual);
        Assert.AreEqual(SchematicDataXml.Write(state.Hierarchy.Data), result.GetProperty("structuredContent").GetProperty("xml").GetString());
        var compared = await mcp.Tool("kicad_design_connectivity_compare", new
        {
            designXml = SchematicDesignXml.Write(model, []), electricalStateJson = SchematicJson.Formatter.Format(actual),
            knowledgeLibraryXml = Array.Empty<string>()
        });
        RequireToolSuccess(compared);
        Assert.IsTrue(compared.GetProperty("structuredContent").GetProperty("connectivityEquivalent").GetBoolean());
        Assert.IsTrue((await mcp.Tool("kicad_schematic_electrical_state", new
            { instanceId, requestJson = SchematicJson.Formatter.Format(stale) })).GetProperty("isError").GetBoolean());

        string recoveryPath = Path.Combine(evidence, instanceId + "-electrical-baseline-recovery.json");
        var recovery = new DesignRecoveryStore(recoveryPath);
        var recoveryState = new DesignRecoveryState(Guid.NewGuid(), Guid.Parse(instanceId),
            new(state.Hierarchy.Revision.Epoch, state.Hierarchy.Revision.Sequence), state.Hierarchy.TrackingComplete,
            model, [0xff, 0x3c], state.Hierarchy.Data.Clone(), []);
        var savedRecovery = recovery.Save(recoveryState, null);
        async Task<System.Text.Json.JsonElement> Initialize(string expected, string target) =>
            await mcp.Tool("kicad_design_electrical_baseline_initialize", new { instanceId = target, recoveryPath, expectedRevisionToken = expected });
        Assert.IsTrue((await Initialize("stale", instanceId)).GetProperty("isError").GetBoolean());
        Assert.IsTrue((await Initialize(savedRecovery.RevisionToken, Guid.NewGuid().ToString("D"))).GetProperty("isError").GetBoolean());
        using (var held = new FileStream(recoveryPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.IsTrue((await Initialize(savedRecovery.RevisionToken, instanceId)).GetProperty("isError").GetBoolean());
        Assert.AreEqual(savedRecovery.RevisionToken, recovery.Read()!.RevisionToken);
        var wrongCircuit = model with { Engineering = model.Engineering with { Circuit = model.Engineering.Circuit with { Nets = [] } } };
        var wrongRecovery = recovery.Save(recoveryState with { Baseline = wrongCircuit }, savedRecovery.RevisionToken);
        Assert.IsTrue((await Initialize(wrongRecovery.RevisionToken, instanceId)).GetProperty("isError").GetBoolean());
        savedRecovery = recovery.Save(recoveryState, wrongRecovery.RevisionToken);
        var initialized = await Initialize(savedRecovery.RevisionToken, instanceId); RequireToolSuccess(initialized);
        Assert.AreEqual(state, recovery.Read()!.State.BaselineElectrical);
        Assert.AreEqual(state, recovery.Read()!.State.ObservedElectrical);
        CollectionAssert.AreEqual(recoveryState.DesiredFileBytes, recovery.Read()!.State.DesiredFileBytes);
        Assert.IsTrue((await Initialize(recovery.Read()!.RevisionToken, instanceId)).GetProperty("isError").GetBoolean());
        Assert.AreEqual(before, await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token));
    }

    private static SchematicDesign ProbeElectricalModel(SchematicElectricalState state)
    {
        // This known fixture has two one-pin probes on the root and empty
        // repeated child sheets. Its expected connection is declared here,
        // never inferred from the native net memberships being verified.
        var hierarchy = state.Hierarchy.Data;
        var root = hierarchy.Instances.Single(s => s.Metadata.Document.Equals(hierarchy.Document));
        var symbols = root.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
        Assert.AreEqual(2, symbols.Length);
        var definitionIds = hierarchy.Instances.Select(s => s.Metadata.ScreenId.Value).Distinct()
            .ToDictionary(id => id, _ => Guid.NewGuid(), StringComparer.Ordinal);
        string Key(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(p => p.Value));
        var instanceIds = hierarchy.Instances.ToDictionary(Key, _ => Guid.NewGuid(), StringComparer.Ordinal);
        Guid part = Guid.NewGuid();
        var componentDefinitions = symbols.Select(s => new ComponentDefinition(Guid.NewGuid(), part, s.ValueField.Text.Text_)).ToArray();
        var components = symbols.Select((s, i) => new ComponentInstance(Guid.NewGuid(), componentDefinitions[i].Id,
            instanceIds[Key(root)], s.ReferenceField.Text.Text_)).ToArray();
        var occurrences = components.Select(c => new SymbolOccurrence(Guid.NewGuid(), c.Id, 1, null)).ToArray();
        var circuit = new Circuit(Guid.NewGuid(), [new(part, "Fixture probe", 1, [new("1", "1", 1)])],
            definitionIds.Select(d => new SheetDefinition(d.Value, d.Key,
                d.Key == root.Metadata.ScreenId.Value ? componentDefinitions : [])).ToArray(),
            hierarchy.Instances.Select(s => new KiCad.Automation.Model.SheetInstance(instanceIds[Key(s)], definitionIds[s.Metadata.ScreenId.Value],
                Key(s).Contains('/') ? instanceIds[Key(s)[..Key(s).LastIndexOf('/')]] : null)).ToArray(), components,
            [new(Guid.NewGuid(), "Expected probe link", components.Select(c => new PinEndpoint(c.Id, "1")).ToArray())], occurrences);
        var engineering = new EngineeringDesign(circuit, new(Guid.NewGuid(), [], [], [], []), [], []);
        return new(engineering, hierarchy.Clone(), hierarchy.Instances.Select(s => new SchematicSheetBinding(instanceIds[Key(s)],
            s.Metadata.Document.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray())).ToArray(),
            symbols.Select((s, i) => new SchematicSymbolBinding(occurrences[i].Id, Guid.Parse(s.Id.Value))).ToArray());
    }
}
