using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyNativeSimulation(NativeClient client, DocumentSpecifier document,
        string evidence, string instanceId, CancellationToken token)
    {
        const string netlist = "Automation divider\nV1 in 0 5\nR1 in out 1k\nR2 out 0 1k\n.op\n.end\n";
        string operation = Guid.NewGuid().ToString("D");
        var started = await client.InvokeAsync<StartSimulationJob, SimulationJobState>(new()
        { Document = document, OperationId = operation, ProcessEpoch = client.Epoch, Netlist = netlist }, token);
        Assert.AreEqual(SimulationJobStatus.SimjsRunning, started.Status);
        Assert.IsFalse(started.WorkerFinished);
        SimulationJobState current = started;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while( current.Status == SimulationJobStatus.SimjsRunning )
        {
            await Task.Delay(100, deadline.Token);
            current = await client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
            { Document = document, JobId = started.JobId, ProcessEpoch = client.Epoch }, deadline.Token);
        }
        Assert.AreEqual(SimulationJobStatus.SimjsCompleted, current.Status, current.ErrorMessage);
        Assert.IsTrue(current.WorkerFinished);
        Assert.IsTrue(current.Vectors.Any(v => v.Name.Contains("out", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(current.Vectors.SelectMany(v => v.Values).Any(value => Math.Abs(value - 2.5) < 0.01));
        var replay = await client.InvokeAsync<StartSimulationJob, SimulationJobState>(new()
        { Document = document, OperationId = operation, ProcessEpoch = client.Epoch, Netlist = netlist }, token);
        Assert.AreEqual(started.JobId, replay.JobId);
        var wrongDocument = document.Clone(); wrongDocument.SheetPath.Path[0].Value = Guid.NewGuid().ToString("D");
        await Assert.ThrowsExactlyAsync<NativeApiException>(() => client.InvokeAsync<ReadSimulationJob, SimulationJobState>(new()
        { Document = wrongDocument, JobId = started.JobId, ProcessEpoch = client.Epoch }, token));

        const string longNetlist = "Long transient\nV1 in 0 PULSE(0 5 0 1n 1n 1m 2m)\nR1 in out 1k\nC1 out 0 1u\n.tran 1u 1000m\n.end\n";
        var longRun = await client.InvokeAsync<StartSimulationJob, SimulationJobState>(new()
        { Document = document, OperationId = Guid.NewGuid().ToString("D"), ProcessEpoch = client.Epoch, Netlist = longNetlist }, token);
        var cancelled = await client.InvokeAsync<CancelSimulationJob, SimulationJobState>(new()
        { Document = document, JobId = longRun.JobId, ProcessEpoch = client.Epoch }, token);
        Assert.AreEqual(SimulationJobStatus.SimjsCancelled, cancelled.Status);
        Assert.IsTrue(cancelled.CancellationRequested);
        Assert.IsFalse(cancelled.Vectors.Count > 0);
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-simulation.json"),
            System.Text.Json.JsonSerializer.Serialize(current), token);
    }
}
