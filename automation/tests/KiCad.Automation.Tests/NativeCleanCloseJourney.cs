using System.Text.Json;
using Kiapi.Common.Commands;
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
                        if (pass == 1)
                        {
                            // Read-only files: a save is refused before anything is written and names every
                            // file, and the editor stays exactly as it was, so the close below still succeeds.
                            var refusedSave = await mcp.Tool("kicad_document_save", new { instanceId,
                                expectedStateJson = SchematicJson.Formatter.Format(current), operationId = Guid.NewGuid().ToString("D") });
                            Assert.IsTrue(refusedSave.GetProperty("isError").GetBoolean(), refusedSave.GetRawText());
                            var failed = Parse(refusedSave);
                            Assert.AreEqual(LifecycleOperationStatus.LosFailed, failed.Status);
                            Assert.AreEqual("file_not_writable", failed.ErrorCode, failed.ErrorMessage);
                            CollectionAssert.AreEquivalent(current.NativeFiles.ToArray(), failed.BlockedFiles.ToArray(), failed.ErrorMessage);
                            Assert.IsEmpty(failed.WrittenFiles, failed.ErrorMessage);
                            Assert.AreEqual(current, failed.ObservedState);
                            Assert.AreEqual(current, await ObserveLifecycleState(client, document, token));
                            Console.WriteLine($"Read-only {kind} save refused for {instanceId}: {failed.ErrorMessage}");
                        }
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

    // An agent cancels a close while KiCad is not answering. KiCad never receives it: the editor
    // stays open with the same saved document, no file is written, and the receipt query says so.
    private static async Task VerifyCancelledCloseKeepsEditor(CancellableMcpClient mcp, NativeClient client,
        DocumentSpecifier document, int processId, string evidence, string instanceId, CancellationToken token)
    {
        var clean = await ObserveLifecycleState(client, document, token);
        Assert.IsFalse(clean.NativeContentDirty);
        Assert.AreEqual(clean.StateSha256, clean.CleanCheckpointSha256, "A close needs a verified clean checkpoint.");
        var files = clean.NativeFiles.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
        string operation = Guid.NewGuid().ToString("D");
        var reply = await CancelWhileKiCadIsStopped(mcp, processId, "kicad_document_close",
            new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(clean), operationId = operation }, token);
        await OperationNotReceived(mcp, client, document, instanceId, operation, token);
        Assert.AreEqual(clean, await ObserveLifecycleState(client, document, token), "The editor must stay open and unchanged.");
        var open = await client.InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(new() { Type = document.Type }, token);
        CollectionAssert.Contains(open.Documents.ToArray(), document, "The cancelled close must leave the document open.");
        foreach (var (path, before) in files)
        {
            CollectionAssert.AreEqual(before.Bytes, await File.ReadAllBytesAsync(path, token), path + " changed on disk.");
            Assert.AreEqual(before.Written, File.GetLastWriteTimeUtc(path), path + " was written by a cancelled close.");
        }
        NotReportedAsSuccess(reply, "cancelled close");
        await File.WriteAllTextAsync(Path.Combine(evidence, instanceId + "-cancelled-close.json"), SchematicJson.Formatter.Format(clean), token);
        Console.WriteLine($"Cancelled close of {instanceId} left the editor open; KiCad never received operation {operation}.");
    }
}
