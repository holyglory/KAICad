using System.Text.Json;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbDrcJobs(NativeClient client, DocumentSpecifier board,
        string evidence, CancellationToken token)
    {
        string statePath = Directory.CreateTempSubdirectory("kicad-drc-job-mcp-").FullName;
        try
        {
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            static PcbDrcJobState Parse(JsonElement reply) =>
                SchematicJson.Parser.Parse<PcbDrcJobState>(reply.GetProperty("content").EnumerateArray()
                    .Single(item => item.GetProperty("type").GetString() == "text")
                    .GetProperty("text").GetString()!);
            async Task<JsonElement> Call(StdioMcpFixture mcp, string name, object arguments)
            {
                var reply = await mcp.Tool(name, arguments);
                Assert.IsFalse(reply.TryGetProperty("isError", out var error) && error.GetBoolean(),
                    reply.GetRawText());
                return reply;
            }
            async Task<PcbDrcJobState> Wait(StdioMcpFixture mcp, string documentJson, string jobId,
                string epoch)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(45));
                int delay = 25;
                while (true)
                {
                    var state = Parse(await Call(mcp, "kicad_pcb_drc_job", new
                        { instanceId, documentJson, jobId, processEpoch = epoch }));
                    Assert.AreEqual(client.Epoch, state.ProcessEpoch);
                    Assert.IsTrue((int)state.Status is >= 1 and <= 7);
                    if ((int)state.Status is not (1 or 2)) return state;
                    Assert.AreEqual(0, state.Findings.Count, "Running jobs must not expose partial findings.");
                    await Task.Delay(delay, limit.Token);
                    delay = Math.Min(delay * 2, 250);
                }
            }

            string documentJson = SchematicJson.Formatter.Format(board);
            await using var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, "drc-job-mcp.stderr.log"), token);
            await Call(mcp, "kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });

            var before = await ObserveLifecycleState(client, board, token);
            string operationId = Guid.NewGuid().ToString("D");
            var started = Parse(await Call(mcp, "kicad_pcb_drc_start", new
                { instanceId, documentJson, operationId, refillZones = false,
                  reportAllTrackErrors = true, testFootprints = false }));
            Assert.AreEqual(board, started.Document);
            Assert.AreEqual(operationId, started.OperationId);
            Assert.AreEqual(client.Epoch, started.ProcessEpoch);
            Assert.IsNotNull(started.CheckedRevision);
            var finished = await Wait(mcp, documentJson, started.JobId, client.Epoch);
            Assert.IsTrue((int)finished.Status is 3 or 4 or 5 or 6 or 7);
            if ((int)finished.Status == 3)
            {
                Assert.IsTrue(finished.ResultsFresh);
                Assert.AreEqual(before.Revision, finished.CheckedRevision);
                Assert.IsTrue(finished.Progress >= 1);
            }
            else Assert.IsFalse(finished.ResultsFresh);

            // Idempotent start returns the existing job rather than running it twice.
            var repeated = Parse(await Call(mcp, "kicad_pcb_drc_start", new
                { instanceId, documentJson, operationId, refillZones = false,
                  reportAllTrackErrors = true, testFootprints = false }));
            Assert.AreEqual(finished, repeated);

            string cancelOperation = Guid.NewGuid().ToString("D");
            var cancellable = Parse(await Call(mcp, "kicad_pcb_drc_start", new
                { instanceId, documentJson, operationId = cancelOperation, refillZones = false,
                  reportAllTrackErrors = true, testFootprints = true }));
            var cancelled = Parse(await Call(mcp, "kicad_pcb_drc_cancel", new
                { instanceId, documentJson, jobId = cancellable.JobId, processEpoch = client.Epoch }));
            Assert.IsTrue((int)cancelled.Status is 3 or 4 or 5 or 6 or 7);
            if ((int)cancelled.Status == 4) Assert.IsFalse(cancelled.ResultsFresh);
            var terminalCancel = await Wait(mcp, documentJson, cancellable.JobId, client.Epoch);
            Assert.AreEqual(cancelled.Status, terminalCancel.Status);
            Assert.IsFalse(terminalCancel.ResultsFresh);

            var wrongTarget = board.Clone(); wrongTarget.BoardFilename = "not-open.kicad_pcb";
            var wrong = await mcp.Tool("kicad_pcb_drc_job", new
                { instanceId, documentJson = SchematicJson.Formatter.Format(wrongTarget),
                  jobId = started.JobId, processEpoch = client.Epoch });
            Assert.IsTrue(wrong.GetProperty("isError").GetBoolean());
            var staleEpoch = await mcp.Tool("kicad_pcb_drc_job", new
                { instanceId, documentJson, jobId = started.JobId, processEpoch = Guid.NewGuid().ToString("D") });
            Assert.IsTrue(staleEpoch.GetProperty("isError").GetBoolean());
            Assert.AreEqual(before, await ObserveLifecycleState(client, board, token),
                "DRC jobs must not mutate the live PCB.");
        }
        finally { Directory.Delete(statePath, true); }
    }
}
