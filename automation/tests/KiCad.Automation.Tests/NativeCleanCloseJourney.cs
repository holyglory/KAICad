using System.Text.Json;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    // Native contracts precede this real MCP/editor journey in the governed graph.
    private static async Task VerifyCleanPcbClose(NativeClient client, DocumentSpecifier board,
        DocumentSpecifier schematic, string evidence, CancellationToken token)
    {
        await VerifyCleanDocumentClose(client, board, schematic, openTool: "kicad_pcb_open", evidence, token);
        await VerifyCleanDocumentClose(client, schematic, board, openTool: "kicad_schematic_open", evidence, token);
    }

    private static async Task VerifyCleanDocumentClose(NativeClient client, DocumentSpecifier document,
        DocumentSpecifier otherDocument, string openTool, string evidence, CancellationToken token)
    {
        string statePath = Directory.CreateTempSubdirectory("kicad-close-mcp-").FullName;
        try
        {
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            // The handshake follows the open editors: closing this one removes its request types,
            // the other editor keeps its own, and reopening restores exactly the same list.
            bool pcb = (int)document.Type == 3;
            string kind = pcb ? "pcb" : "schematic";
            string closedEditorType = pcb ? ReadPcbDrcState.Descriptor.FullName : ReadSchematicScreenData.Descriptor.FullName;
            string otherEditorType = pcb ? ReadSchematicScreenData.Descriptor.FullName : ReadPcbDrcState.Descriptor.FullName;
            await SaveCheckedThroughMcp(client, document, evidence, token);
            for (int pass = 0; pass < 2; pass++)
            {
                var current = await ObserveLifecycleState(client, document, token);
                Assert.IsFalse(current.NativeContentDirty);
                Assert.AreEqual(current.StateSha256, current.CleanCheckpointSha256);
                var other = await ObserveLifecycleState(client, otherDocument, token);
                var files = current.NativeFiles.ToDictionary(path => path,
                    path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
                var modes = new Dictionary<string, UnixFileMode>();
                string[] reopenedTypes = [];
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
                    // Both editors are open here. The first pass proves the list against dispatch.
                    string[] bothOpen = pass == 0
                        ? await NativeCapabilityProbe.VerifyHandshakeAsync(client,
                            Path.Combine(evidence, $"{instanceId}-before-{kind}-close-capabilities.json"), token)
                        : (await client.HandshakeAsync(token)).Capabilities.ToArray();
                    CollectionAssert.Contains(bothOpen, closedEditorType);
                    CollectionAssert.Contains(bothOpen, otherEditorType);
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
                        Assert.AreEqual(current, await ObserveLifecycleState(client, document, token));
                        var request = new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(current), operationId = operation };
                        var reply = await mcp.Tool("kicad_document_close", request);
                        Assert.IsFalse(reply.TryGetProperty("isError", out error) && error.GetBoolean(), reply.GetRawText());
                        result = Parse(reply);
                        Assert.AreEqual(LifecycleOperationStatus.LosClosed, result.Status);
                        Assert.AreEqual(result, Parse(await mcp.Tool("kicad_document_close", request)));
                        await Assert.ThrowsExactlyAsync<NativeApiException>(() =>
                            client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, token));
                        Assert.AreEqual(other, await ObserveLifecycleState(client, otherDocument, token));
                    }
                    string[] afterClose = pass == 0
                        ? await NativeCapabilityProbe.VerifyHandshakeAsync(client,
                            Path.Combine(evidence, $"{instanceId}-after-{kind}-close-capabilities.json"), token)
                        : (await client.HandshakeAsync(token)).Capabilities.ToArray();
                    CollectionAssert.IsSubsetOf(afterClose, bothOpen, "Closing an editor must not add request types.");
                    CollectionAssert.DoesNotContain(afterClose, closedEditorType, "The closed editor's request types must leave the handshake.");
                    CollectionAssert.Contains(afterClose, otherEditorType, "The editor that stays open keeps its request types.");
                    Console.WriteLine($"Closing the {kind} editor of {instanceId} removed {bothOpen.Length - afterClose.Length} of {bothOpen.Length} native request types.");
                    reopenedTypes = bothOpen;
                    await using (var mcp = await StdioMcpFixture.StartAsync(statePath,
                        Path.Combine(evidence, "clean-close-reconnect-" + operation + ".stderr.log"), token))
                    {
                        var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
                        Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
                        var receipt = await mcp.Tool("kicad_document_operation", new
                            { instanceId, documentJson = SchematicJson.Formatter.Format(document), operationId = operation, processEpoch = client.Epoch });
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
                string filename = (int)document.Type == 3 ? document.BoardFilename : document.Project.Name + ".kicad_sch";
                string documentPath = current.NativeFiles.Single(path => Path.GetFileName(path) == filename);
                var opened = await CreateRootThroughMcp(client.Endpoint, instanceId, documentPath, evidence, token, toolName: openTool);
                Assert.AreEqual(document, opened.Document);
                CollectionAssert.AreEqual(reopenedTypes, (await client.HandshakeAsync(token)).Capabilities.ToArray(),
                    "Reopening the editor must restore exactly the request types it had before closing.");
            }
        }
        finally { Directory.Delete(statePath, true); }
    }
}
