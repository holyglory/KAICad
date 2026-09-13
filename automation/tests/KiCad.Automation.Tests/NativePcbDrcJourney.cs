using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

public sealed partial class NativeSessionTests
{
    private static async Task VerifyPcbDrcExclusions(NativeClient client, DocumentSpecifier board,
        int processId, string display, string evidence, CancellationToken token)
    {
        const string dialog = "Design Rules Checker";
        const string commentDialog = "Exclusion Comment";
        const string comment = "fixture reviewed exclusion";
        string project = Path.Combine(board.Project.Path, board.Project.Name + ".kicad_pro");
        void Key(string key, string window = dialog, bool control = false) =>
            NativeKeyboard.SchematicShortcut(display, processId, key, window, control, false);
        Task Picture(string phase) => NativeKeyboard.CaptureAsync(display,
            Path.Combine(evidence, processId + "-drc-" + phase + ".png"), token);
        async Task Window(string title, bool visible)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            int delay = 25;
            while (NativeKeyboard.HasWindow(display, processId, title) != visible)
            { await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 200); }
            if (visible) await NativeSetupUi.StableGeometry(display, processId, limit.Token, title);
        }
        async Task Open()
        {
            NativeKeyboard.SchematicShortcut(display, processId, "i", "PCB Editor", false, false, altKey: true);
            await NativeSetupUi.WaitForPopup(display, processId, true, token, "PCB Editor");
            Key("Home", "PCB Editor"); Key("Down", "PCB Editor"); Key("Down", "PCB Editor");
            Key("Return", "PCB Editor"); await Window(dialog, true);
        }
        async Task<PcbDrcState> Read() => await client.InvokeAsync<ReadPcbDrcState, PcbDrcState>(
            new() { Document = board }, token);
        async Task<PcbDrcState> WaitFor(Func<PcbDrcState, bool> predicate)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            int delay = 25;
            while (true)
            {
                var state = await client.InvokeAsync<ReadPcbDrcState, PcbDrcState>(
                    new() { Document = board }, limit.Token);
                Assert.AreEqual(board, state.Document);
                Assert.AreEqual(client.Epoch, state.ProcessEpoch);
                Assert.IsFalse(state.ResultsFreshnessKnown, "A marker inventory is not a fresh DRC receipt.");
                if (!state.Running && state.MarkerSnapshotComplete && predicate(state)) return state;
                await Task.Delay(delay, limit.Token); delay = Math.Min(delay * 2, 250);
            }
        }
        async Task Menu(bool withComment)
        {
            // Real first violation row, not the PCB marker/property setter.
            NativeKeyboard.SchematicShortcut(display, processId, "right-click", dialog, false, false,
                clickFromLeft: 180, clickFromTop: 110);
            await NativeSetupUi.WaitForPopup(display, processId, true, token, dialog);
            await Picture("context");
            Key("Home"); if (withComment) Key("Down"); Key("Return");
            if (withComment) await Window(commentDialog, true);
            else await NativeSetupUi.WaitForPopup(display, processId, false, token, dialog);
        }
        static JsonNode WithoutExclusions(byte[] bytes)
        {
            var json = JsonNode.Parse(bytes)!;
            json["board"]!["design_settings"]!["drc_exclusions"] = new JsonArray();
            return json;
        }

        await Open();
        await Picture("before-run");
        Assert.AreEqual(0, (await Read()).Findings.Count, "This fixture has not yet run DRC.");
        NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
            clickFromRight: 60, clickFromBottom: 45);
        var initial = await WaitFor(state => state.Findings.Count > 0);
        await Picture("results");
        Assert.IsTrue(initial.Findings.All(finding => !finding.Excluded));
        // Show exclusions too, so the same real row remains available for edit/remove.
        NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
            clickFromLeft: 440, clickFromBottom: 85);
        var before = await ObserveLifecycleState(client, board, token);
        byte[] diskBefore = await File.ReadAllBytesAsync(project, token);
        await Menu(true); await Picture("comment-cancel");
        Key("Escape", commentDialog); await Window(commentDialog, false);
        Assert.AreEqual(before, await ObserveLifecycleState(client, board, token));
        Assert.AreEqual(initial, await Read());
        CollectionAssert.AreEqual(diskBefore, await File.ReadAllBytesAsync(project, token));

        await Menu(true);
        Key("a", commentDialog, true);
        foreach (char character in comment) Key(character.ToString(), commentDialog);
        // The comment editor is multiline: activate the real OK button.
        NativeKeyboard.SchematicShortcut(display, processId, "click", commentDialog, false, false,
            clickFromRight: 60, clickFromBottom: 25);
        await Window(commentDialog, false);
        var changed = await WaitFor(state => state.Findings.Any(finding => finding.Excluded && finding.Comment == comment));
        var excluded = changed.Findings.Single(finding => finding.Excluded);
        foreach (var finding in initial.Findings.Where(finding => finding.NativeId != excluded.NativeId))
            Assert.AreEqual(finding, changed.Findings.Single(current => current.NativeId == finding.NativeId));
        Assert.AreEqual(initial.Findings.Single(finding => finding.NativeId == excluded.NativeId).Marker, excluded.Marker);
        Assert.AreNotEqual(before.StateSha256, (await ObserveLifecycleState(client, board, token)).StateSha256);
        CollectionAssert.AreEqual(diskBefore, await File.ReadAllBytesAsync(project, token));
        await Picture("excluded");
        Key("Escape"); await Window(dialog, false);
        var rejected = await client.InvokeAsync<CheckedSaveDocument, LifecycleOperationResult>(new()
            { Document = board, ExpectedState = before, OperationId = Guid.NewGuid().ToString("D") }, token);
        Assert.AreEqual(LifecycleOperationStatus.LosRejected, rejected.Status);

        // Check the same inventory across an actual STDIO MCP process.
        string statePath = Directory.CreateTempSubdirectory("kicad-drc-mcp-").FullName;
        try
        {
            await using var mcp = await StdioMcpFixture.StartAsync(statePath,
                Path.Combine(evidence, processId + "-drc-mcp.stderr.log"), token);
            string instanceId = (await client.HandshakeAsync(token)).InstanceId;
            var attached = await mcp.Tool("kicad_instance_attach", new { endpoint = client.Endpoint, expectedInstanceId = instanceId });
            Assert.IsFalse(attached.TryGetProperty("isError", out var error) && error.GetBoolean());
            var reply = await mcp.Tool("kicad_pcb_drc_state", new { instanceId, documentJson = SchematicJson.Formatter.Format(board) });
            Assert.IsFalse(reply.TryGetProperty("isError", out error) && error.GetBoolean(), reply.GetRawText());
            var observed = SchematicJson.Parser.Parse<PcbDrcState>(reply.GetProperty("content").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!);
            Assert.AreEqual(await Read(), observed);
        }
        finally { Directory.Delete(statePath, true); }

        await SaveCheckedThroughMcp(client, board, evidence, token);
        byte[] persisted = await File.ReadAllBytesAsync(project, token);
        Assert.IsTrue(JsonNode.DeepEquals(WithoutExclusions(diskBefore), WithoutExclusions(persisted)),
            "Excluding one violation must preserve unrelated project settings.");
        var declared = JsonNode.Parse(persisted)!["board"]!["design_settings"]!["drc_exclusions"]!.AsArray();
        Assert.AreEqual(1, declared.Count);
        Assert.AreEqual(comment, declared[0]!["comment"]!.GetValue<string>());
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        var reloaded = await Read();
        var restored = reloaded.Findings.Single(finding => finding.Excluded && finding.Comment == comment);
        var expectedMarker = excluded.Marker.Clone();
        // The actual project record must explicitly identify the parent board;
        // never infer an owner from coordinates or an unresolved old UUID.
        var persistedItems = declared[0]!["marker"]!["items"]!.AsArray();
        Assert.AreEqual(expectedMarker.Items.Count, persistedItems.Count);
        for (int index = 0; index < expectedMarker.Items.Count; ++index)
            if (persistedItems[index]!["value"]!.GetValue<string>() == Guid.Empty.ToString("D"))
            {
                Assert.AreEqual(changed.Revision.Epoch, expectedMarker.Items[index].Value);
                expectedMarker.Items[index].Value = reloaded.Revision.Epoch;
            }
        Assert.AreEqual(expectedMarker, restored.Marker, "Persisted exclusion must restore the exact violation, not a nearby one.");
        await Open(); await Picture("reloaded");
        // A real rerun must match the persisted board-scoped exclusion too.
        NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
            clickFromRight: 60, clickFromBottom: 45);
        var repeated = await WaitFor(state => state.Findings.Count == initial.Findings.Count);
        Assert.AreEqual(expectedMarker, repeated.Findings.Single(finding => finding.Excluded && finding.Comment == comment).Marker);
        await Picture("rerun");
        await Menu(false);
        await WaitFor(state => state.Findings.All(finding => !finding.Excluded));
        await Picture("removed");
        Key("Escape"); await Window(dialog, false);
        await SaveCheckedThroughMcp(client, board, evidence, token);
        var cleared = JsonNode.Parse(await File.ReadAllBytesAsync(project, token))!;
        Assert.AreEqual(0, cleared["board"]!["design_settings"]!["drc_exclusions"]!.AsArray().Count);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        Assert.IsTrue((await Read()).Findings.All(finding => !finding.Excluded));
    }
}
