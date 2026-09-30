using System.Text.Json;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

/// <summary>Real compiled MCP/native journey; its engineering bindings stay explicitly unresolved.</summary>
internal sealed class NativeRecoveryIntakeProbe : IAsyncDisposable
{
    private readonly StdioMcpFixture mcp;
    private readonly NativeClient native;
    private readonly DocumentSpecifier document;
    private readonly DesignRecoveryStore recovery;
    private readonly string instanceId, intakeId, baselineXml, recoveryPath;
    private readonly CancellationToken token;
    private ulong sequence;
    private FileStream? persistenceLock;

    private NativeRecoveryIntakeProbe(StdioMcpFixture mcp, NativeClient native, DocumentSpecifier document,
        DesignRecoveryStore recovery, string recoveryPath, string instanceId, string intakeId, string baselineXml, CancellationToken token)
    {
        this.mcp = mcp; this.native = native; this.document = document; this.recovery = recovery;
        this.recoveryPath = recoveryPath;
        this.instanceId = instanceId; this.intakeId = intakeId; this.baselineXml = baselineXml; this.token = token;
    }

    public static async Task<NativeRecoveryIntakeProbe> StartAsync(NativeClient native, DocumentSpecifier document,
        string evidence, string instanceId, CancellationToken token)
    {
        var snapshot = await native.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
            new() { Document = document }, token);
        var definition = Guid.NewGuid();
        var engineering = new EngineeringDesign(new(Guid.NewGuid(), [], [new(definition, "Native intake fixture", [])],
            [new(Guid.NewGuid(), definition, null)], [], [], []), new(Guid.NewGuid(), [], [], [], []), [], []);
        var baseline = new SchematicDesign(engineering, snapshot.Data, [], []);
        string path = Path.Combine(evidence, instanceId + "-native-intake-recovery.json");
        var recovery = new DesignRecoveryStore(path);
        recovery.Save(new(Guid.NewGuid(), Guid.Parse(instanceId), new(snapshot.Revision.Epoch, snapshot.Revision.Sequence),
            snapshot.TrackingComplete, baseline, [0xff, 0x3c], snapshot.Data, []), null);
        var mcp = await StdioMcpFixture.StartAsync(Path.Combine(evidence, instanceId + "-intake-mcp-state"),
            Path.Combine(evidence, instanceId + "-intake-mcp.stderr.log"), token);
        try
        {
            Success(await mcp.Tool("kicad_instance_attach", new { endpoint = native.Endpoint, expectedInstanceId = instanceId }));
            var started = Success(await mcp.Tool("kicad_design_native_intake_start", new { instanceId, recoveryPath = path }));
            string intakeId = started.GetProperty("intakeId").GetString()!;
            var probe = new NativeRecoveryIntakeProbe(mcp, native, document, recovery, path, instanceId, intakeId,
                SchematicDesignXml.Write(baseline, []), token);
            await probe.WaitForRevision(snapshot.Revision.Sequence);
            var listed = Success(await mcp.Tool("kicad_design_native_intake_list", new { instanceId }));
            Assert.AreEqual(intakeId, listed.GetProperty("sessions")[0].GetProperty("intakeId").GetString());
            var duplicate = await mcp.Tool("kicad_design_native_intake_start", new { instanceId, recoveryPath = path });
            Assert.AreEqual("native_intake_ownership_conflict", duplicate.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            var wrong = await mcp.Tool("kicad_design_native_intake_stop", new { instanceId = Guid.NewGuid().ToString("D"), intakeId });
            Assert.AreEqual("unknown_native_intake", wrong.GetProperty("structuredContent").GetProperty("errorCode").GetString());
            return probe;
        }
        catch { await mcp.DisposeAsync(); throw; }
    }

    public async Task WaitForRevision(ulong revision)
    {
        do
        {
            var status = Success(await mcp.Tool("kicad_design_native_intake_wait", new { instanceId, intakeId, afterSequence = sequence }));
            sequence = status.GetProperty("sequence").GetUInt64();
            if (persistenceLock is not null && status.GetProperty("phase").GetString() == "Paused")
            {
                Assert.AreEqual("design_recovery_io", status.GetProperty("errorCode").GetString());
                Assert.IsTrue(recovery.Read()!.State.NativeRevision.Sequence < revision);
                persistenceLock.Dispose(); persistenceLock = null;
                var stale = await mcp.Tool("kicad_design_native_intake_resume", new { instanceId, intakeId, expectedSequence = sequence - 1 });
                Assert.AreEqual("native_intake_changed", stale.GetProperty("structuredContent").GetProperty("errorCode").GetString());
                Success(await mcp.Tool("kicad_design_native_intake_resume", new { instanceId, intakeId, expectedSequence = sequence }));
                continue;
            }
            Assert.AreEqual("Watching", status.GetProperty("phase").GetString(), status.GetRawText());
        } while (recovery.Read()!.State.NativeRevision.Sequence < revision);
        var saved = recovery.Read()!;
        Assert.AreEqual(revision, saved.State.NativeRevision.Sequence);
        var live = await native.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(new() { Document = document }, token);
        Assert.IsTrue(saved.State.Observed.Equals(live.Data), "The continuously saved observation must match the native hierarchy.");
        var electrical = await native.InvokeAsync<ReadSchematicElectricalState, SchematicElectricalState>(new() { Document = document }, token);
        Assert.AreEqual(saved.State.ObservedElectrical, electrical);
        Assert.IsNull(saved.State.BaselineElectrical, "Observation must not initialize an unverified electrical baseline.");
        Assert.AreEqual(baselineXml, SchematicDesignXml.Write(saved.State.Baseline, saved.State.KnowledgeLibraries));
        CollectionAssert.AreEqual(new byte[] { 0xff, 0x3c }, saved.State.DesiredFileBytes);
        Assert.IsNull(saved.State.PendingMutation);
    }

    public void BlockNextPersistence() => persistenceLock = new FileStream(recoveryPath + ".lock",
        FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    public async Task VerifyStopAndMcpDisconnect()
    {
        var before = await native.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token);
        var stopped = Success(await mcp.Tool("kicad_design_native_intake_stop", new { instanceId, intakeId }));
        Assert.AreEqual("Stopped", stopped.GetProperty("phase").GetString());
        Assert.AreEqual(0, Success(await mcp.Tool("kicad_design_native_intake_list", new { instanceId })).GetProperty("sessions").GetArrayLength());
        string savedToken = recovery.Read()!.RevisionToken;
        var restarted = Success(await mcp.Tool("kicad_design_native_intake_start", new { instanceId, recoveryPath }));
        string replacementId = restarted.GetProperty("intakeId").GetString()!;
        Success(await mcp.Tool("kicad_design_native_intake_wait", new { instanceId, intakeId = replacementId, afterSequence = 0 }));
        // Disconnect with an active observer, not only after manual stop.
        await mcp.DisposeAsync();
        Assert.IsFalse(mcp.ForcedTermination, "MCP must await its native observer on normal input disconnect, not require forced termination.");
        Assert.AreEqual(before, await native.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(new() { Document = document }, token));
        Assert.AreEqual(savedToken, recovery.Read()!.RevisionToken);
    }

    private static JsonElement Success(JsonElement result)
    {
        Assert.IsFalse(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.GetRawText());
        return result.GetProperty("structuredContent");
    }
    public async ValueTask DisposeAsync()
    {
        persistenceLock?.Dispose(); persistenceLock = null;
        await mcp.DisposeAsync();
    }
}
