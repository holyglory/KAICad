using System.Text.Json;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task SaveCheckedThroughMcp(NativeClient client, DocumentSpecifier document,
        string evidence, CancellationToken token, bool verifyReconnect = true)
    {
        string statePath = Directory.CreateTempSubdirectory("kicad-save-mcp-").FullName;
        try
        {
            var before = await ObserveLifecycleState(client, document, token);
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            Assert.AreEqual(client.Epoch, before.ProcessEpoch);
            string operation = Guid.NewGuid().ToString("D");
            string expectedJson = SchematicJson.Formatter.Format(before);
            LifecycleOperationResult Parse(JsonElement reply) => SchematicJson.Parser.Parse<LifecycleOperationResult>(
                reply.GetProperty("content").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "text")
                    .GetProperty("text").GetString()!);
            LifecycleOperationResult saved;
            await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, "checked-save-" + operation + ".stderr.log"), token))
            {
                var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                var stale = before.Clone(); stale.Revision.Sequence++;
                var rejected = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = SchematicJson.Formatter.Format(stale), operationId = Guid.NewGuid().ToString("D") });
                Assert.IsTrue(rejected.GetProperty("isError").GetBoolean());
                Assert.AreEqual(LifecycleOperationStatus.LosRejected, Parse(rejected).Status);
                Assert.AreEqual(before, await ObserveLifecycleState(client, document, token));

                if (before.NativeContentDirty || before.CleanCheckpointSha256 != before.StateSha256)
                {
                    var dirtyClose = await mcp.Tool("kicad_document_close", new
                        { instanceId, expectedStateJson = expectedJson, operationId = Guid.NewGuid().ToString("D") });
                    Assert.IsTrue(dirtyClose.GetProperty("isError").GetBoolean());
                    Assert.AreEqual(LifecycleOperationStatus.LosRejected, Parse(dirtyClose).Status);
                    Assert.AreEqual(before, await ObserveLifecycleState(client, document, token),
                        "Refused close must preserve both native state and dirty edits.");
                }

                var reply = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = expectedJson, operationId = operation });
                Assert.IsFalse(reply.TryGetProperty("isError", out error) && error.GetBoolean(), reply.GetRawText());
                saved = Parse(reply);
                Assert.AreEqual(LifecycleOperationStatus.LosSaved, saved.Status);
                Assert.AreEqual(document, saved.Document);
                Assert.IsFalse(saved.ObservedState.NativeContentDirty);
                var persisted = saved.ObservedState.NativeFiles.ToDictionary(path => path,
                    path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
                var repeated = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = expectedJson, operationId = operation });
                Assert.AreEqual(saved, Parse(repeated));
                foreach (var (path, expected) in persisted)
                {
                    CollectionAssert.AreEqual(expected.Bytes, File.ReadAllBytes(path));
                    Assert.AreEqual(expected.Written, File.GetLastWriteTimeUtc(path), "Replaying a save receipt must not write again.");
                }
                var mismatched = before.Clone(); mismatched.Revision.Sequence++;
                var conflict = await mcp.Tool("kicad_document_save", new
                    { instanceId, expectedStateJson = SchematicJson.Formatter.Format(mismatched), operationId = operation });
                Assert.IsTrue(conflict.GetProperty("isError").GetBoolean());
                Assert.AreEqual(saved.ObservedState, await ObserveLifecycleState(client, document, token));
            }
            // Repeated middle steps may omit only this process-reconnection proof.
            // All per-edit persistence, stale/dirty refusal and replay assertions above remain.
            if (!verifyReconnect) return;
            // A new MCP process must query the existing native receipt without saving/reopening.
            await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, "checked-save-reconnect-" + operation + ".stderr.log"), token))
            {
                var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                var receipt = await mcp.Tool("kicad_document_operation", new
                    { instanceId, documentJson = SchematicJson.Formatter.Format(document), operationId = operation, processEpoch = client.Epoch });
                Assert.IsFalse(receipt.TryGetProperty("isError", out error) && error.GetBoolean());
                Assert.AreEqual(saved, Parse(receipt));
                Assert.AreEqual(saved.ObservedState, await ObserveLifecycleState(client, document, token));
            }
        }
        finally { Directory.Delete(statePath, true); }
    }
}
