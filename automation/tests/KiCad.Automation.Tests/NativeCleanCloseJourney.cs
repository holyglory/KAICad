using System.Text.Json;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Wired into the rendered graph only when the close export passes its native gates.
    private static async Task VerifyCleanPcbClose(NativeClient client, DocumentSpecifier board,
        DocumentSpecifier schematic, string evidence, CancellationToken token)
    {
        string statePath = Directory.CreateTempSubdirectory("kicad-close-mcp-").FullName;
        try
        {
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            await SaveCheckedThroughMcp(client, board, evidence, token);
            for (int pass = 0; pass < 2; pass++)
            {
                var current = await ObserveLifecycleState(client, board, token);
                Assert.IsFalse(current.NativeContentDirty);
                Assert.AreEqual(current.StateSha256, current.CleanCheckpointSha256);
                var other = await ObserveLifecycleState(client, schematic, token);
                var files = current.NativeFiles.ToDictionary(path => path,
                    path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
                var modes = new Dictionary<string, UnixFileMode>();
                if (pass == 1)
                {
                    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux readonly fixture.");
                    foreach (string path in current.NativeFiles)
                    {
                        var mode = File.GetUnixFileMode(path); modes.Add(path, mode);
                        File.SetUnixFileMode(path, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
                    }
                }
                try
                {
                    string operation = Guid.NewGuid().ToString("D");
                    LifecycleOperationResult Parse(JsonElement reply) => SchematicJson.Parser.Parse<LifecycleOperationResult>(
                        reply.GetProperty("content").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "text")
                            .GetProperty("text").GetString()!);
                    LifecycleOperationResult result;
                    await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                        Path.Combine(evidence, "clean-close-" + operation + ".stderr.log"), token))
                    {
                        var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                        Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                        var stale = current.Clone(); stale.Revision.Sequence++;
                        var refused = await mcp.Tool("kicad_document_close", new
                            { instanceId, expectedStateJson = SchematicJson.Formatter.Format(stale), operationId = Guid.NewGuid().ToString("D") });
                        Assert.IsTrue(refused.GetProperty("isError").GetBoolean());
                        Assert.AreEqual(LifecycleOperationStatus.LosRejected, Parse(refused).Status);
                        Assert.AreEqual(current, await ObserveLifecycleState(client, board, token));
                        var request = new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(current), operationId = operation };
                        var reply = await mcp.Tool("kicad_document_close", request);
                        Assert.IsFalse(reply.TryGetProperty("isError", out error) && error.GetBoolean(), reply.GetRawText());
                        result = Parse(reply);
                        Assert.AreEqual(LifecycleOperationStatus.LosClosed, result.Status);
                        Assert.AreEqual(result, Parse(await mcp.Tool("kicad_document_close", request)));
                        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                            client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = board }, token));
                        Assert.AreEqual(other, await ObserveLifecycleState(client, schematic, token));
                    }
                    await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                        Path.Combine(evidence, "clean-close-reconnect-" + operation + ".stderr.log"), token))
                    {
                        var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                        Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                        var receipt = await mcp.Tool("kicad_document_operation", new
                            { instanceId, documentJson = SchematicJson.Formatter.Format(board), operationId = operation, processEpoch = client.Epoch });
                        Assert.IsFalse(receipt.TryGetProperty("isError", out error) && error.GetBoolean());
                        Assert.AreEqual(result, Parse(receipt));
                    }
                    foreach (var (path, expected) in files)
                    {
                        CollectionAssert.AreEqual(expected.Bytes, await File.ReadAllBytesAsync(path, token));
                        Assert.AreEqual(expected.Written, File.GetLastWriteTimeUtc(path), "Clean close must not write design files.");
                    }
                }
                finally
                {
                    if (OperatingSystem.IsLinux()) foreach (var (path, mode) in modes) File.SetUnixFileMode(path, mode);
                }
                // Reopen through the actual manager so a clean loaded checkpoint is
                // established without another save. The second close is readonly.
                string boardPath = Path.Combine(board.Project.Path, board.BoardFilename);
                var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, boardPath, evidence, token,
                    toolName: "kicad_pcb_open");
                Assert.AreEqual(board, opened.Document);
            }
        }
        finally { Directory.Delete(statePath, true); }
    }
}
