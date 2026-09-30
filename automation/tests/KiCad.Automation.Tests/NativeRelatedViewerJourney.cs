using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbViewerCloseRefusal(NativeClient client, DocumentSpecifier board,
        DocumentSpecifier schematic, int processId, string display, string evidence, CancellationToken token)
    {
        const string viewer = "3D Viewer";
        async Task WaitForViewer(bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, viewer) != visible)
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 250); }
            if (visible) await NativeSetupUi.StableGeometry(display, processId, limit.Token, viewer);
        }
        await SaveCheckedThroughMcp(client, board, evidence, token);
        var before = await ObserveLifecycleState(client, board, token);
        var other = await ObserveLifecycleState(client, schematic, token);
        var files = before.NativeFiles.ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
        NativeKeyboard.SchematicShortcut(display, processId, "3", "PCB Editor", false, true, altKey: true);
        await WaitForViewer(true);
        await NativeKeyboard.CaptureAsync(display, Path.Combine(evidence, processId + "-related-3d-viewer.png"), token);

        string statePath = Directory.CreateTempSubdirectory("kicad-viewer-close-mcp-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, processId + "-viewer-close.stderr.log"), token);
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
            var current = await ObserveLifecycleState(client, board, token);
            Assert.AreEqual(before, current, "Opening a viewer is not a design edit.");
            string operation = Guid.NewGuid().ToString("D");
            var request = new { instanceId, expectedStateJson = SchematicJson.Formatter.Format(current), operationId = operation };
            var refused = await mcp.Tool("kicad_document_close", request);
            Assert.IsTrue(refused.GetProperty("isError").GetBoolean());
            var result = SchematicJson.Parser.Parse<LifecycleOperationResult>(refused.GetProperty("content").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            Assert.AreEqual(LifecycleOperationStatus.LosFailed, result.Status);
            Assert.AreEqual("native_close_failed", result.ErrorCode);
            StringAssert.Contains(result.ErrorMessage, "3D viewer");
            var replay = await mcp.Tool("kicad_document_close", request);
            Assert.AreEqual(refused.GetRawText(), replay.GetRawText(), "Retrying a refused close replays its native receipt.");
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, viewer));
            Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "PCB Editor"));
            Assert.AreEqual(current, await ObserveLifecycleState(client, board, token));
            Assert.AreEqual(other, await ObserveLifecycleState(client, schematic, token));
        }
        finally { Directory.Delete(statePath, true); }
        foreach (var (path, expected) in files)
        {
            CollectionAssert.AreEqual(expected.Bytes, File.ReadAllBytes(path));
            Assert.AreEqual(expected.Written, File.GetLastWriteTimeUtc(path));
        }
        // Explicit user close through the viewer's File menu; never force-close
        // the PCB or kill a process to clear the guard. The next journey proves
        // normal clean PCB close/reopen after this dependency is removed.
        NativeKeyboard.SchematicShortcut(display, processId, "f", viewer, false, false, altKey: true);
        await NativeSetupUi.WaitForPopup(display, processId, true, token, viewer);
        NativeKeyboard.SchematicShortcut(display, processId, "End", viewer, false, false);
        NativeKeyboard.SchematicShortcut(display, processId, "Return", viewer, false, false);
        await WaitForViewer(false);
        Assert.IsTrue(NativeKeyboard.HasWindow(display, processId, "PCB Editor"));
        Assert.AreEqual(other, await ObserveLifecycleState(client, schematic, token));
    }
}
