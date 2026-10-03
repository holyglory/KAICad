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
        int contextNumber = 0;
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
            await Picture("context-" + ++contextNumber);
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

        // Check the same inventory across an actual STDIO MCP process. The same agent reads every detached check of this
        // step and of the rendered-editor steps it runs below.
        await using var agent = await PcbDrcAgent.StartAsync(client, Path.Combine(evidence, processId + "-drc-mcp.stderr.log"), token);
        {
            var inventoryBefore = await Read();
            var wrongBoard = board.Clone(); wrongBoard.BoardFilename = "not-the-open-board.kicad_pcb";
            var wrongTarget = await agent.Mcp.Tool("kicad_pcb_drc_state", new
                { instanceId = agent.InstanceId, documentJson = SchematicJson.Formatter.Format(wrongBoard) });
            Assert.IsTrue(wrongTarget.GetProperty("isError").GetBoolean());
            Assert.AreEqual(inventoryBefore, await Read(), "A wrong-target query must not change the actual board.");
            var reply = await agent.Mcp.Tool("kicad_pcb_drc_state", new
                { instanceId = agent.InstanceId, documentJson = SchematicJson.Formatter.Format(board) });
            Assert.IsFalse(PcbDrcAgent.Failed(reply), reply.GetRawText());
            var observed = SchematicJson.Parser.Parse<PcbDrcState>(PcbDrcAgent.Text(reply));
            Assert.AreEqual(await Read(), observed);
        }

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
        var beforeFilter = await ObserveLifecycleState(client, board, token);
        // Fresh findings can have a different order. Show only exclusions so
        // the action targets the saved finding, not the first new warning.
        NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
            clickFromLeft: 165, clickFromBottom: 85); // Errors off.
        NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
            clickFromLeft: 290, clickFromBottom: 85); // Warnings off; Exclusions stays on.
        Assert.AreEqual(beforeFilter, await ObserveLifecycleState(client, board, token));
        // A real rerun must match the persisted board-scoped exclusion too.
        NativeKeyboard.SchematicShortcut(display, processId, "click", dialog, false, false,
            clickFromRight: 60, clickFromBottom: 45);
        var repeated = await WaitFor(state => state.Findings.Count == initial.Findings.Count);
        Assert.AreEqual(expectedMarker, repeated.Findings.Single(finding => finding.Excluded && finding.Comment == comment).Marker);
        await Picture("rerun");

        // A detached check captures the exclusion with its comment. Editing only the comment in this rendered dialog makes
        // that check stale for an agent reading it through the STDIO MCP server; KiCad records the edit as a change of the
        // board, so the reason is document_changed. A new check carries the edited comment and is complete and fresh.
        const string editedComment = "fixture edited exclusion";
        static string Violation(Kiapi.Board.DrcMarker marker) => marker.ErrorType + "|" + string.Join(",", marker.Items.Select(item => item.Value));
        Task JobEvidence(string name, PcbDrcJobState job) => File.WriteAllTextAsync(
            Path.Combine(evidence, processId + "-" + name), SchematicJson.Formatter.Format(job), token);
        var (_, commented) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
        await JobEvidence("pcb-drc-job-exclusion.json", commented);
        AssertPcbDrcJobCurrent(commented, "A check of the board with a commented exclusion must be complete and fresh.");
        var captured = commented.Findings.Single(finding => finding.Excluded);
        Assert.AreEqual(comment, captured.Comment, "The check must capture the exclusion's comment.");
        Assert.AreEqual(Violation(expectedMarker), Violation(captured.Marker), "The check must exclude the same violation as the editor.");
        await Menu(true); // Edit exclusion comment... is the second action of an excluded violation.
        await Picture("comment-edit");
        Key("a", commentDialog, true);
        foreach (char character in editedComment) Key(character.ToString(), commentDialog);
        NativeKeyboard.SchematicShortcut(display, processId, "click", commentDialog, false, false,
            clickFromRight: 60, clickFromBottom: 25);
        await Window(commentDialog, false);
        await WaitFor(state => state.Findings.Any(finding => finding.Excluded && finding.Comment == editedComment));
        await Picture("comment-edited");
        var stale = await agent.Read(board, commented, token);
        await JobEvidence("pcb-drc-job-exclusion-stale.json", stale);
        AssertPcbDrcJobStale(stale, "document_changed",
            "Editing the exclusion comment in the rendered dialog must make the check stale for an agent reading it over MCP.");
        var (_, recommented) = await RunPcbDrcJob(client, board, await ObserveLifecycleState(client, board, token), token, agent: agent);
        await JobEvidence("pcb-drc-job-exclusion-edited.json", recommented);
        AssertPcbDrcJobCurrent(recommented, "A new check after the comment edit must be complete and fresh.");
        var recaptured = recommented.Findings.Single(finding => finding.Excluded);
        Assert.AreEqual(editedComment, recaptured.Comment, "The new check must capture the edited comment.");
        Assert.AreEqual(Violation(expectedMarker), Violation(recaptured.Marker));

        await Menu(false);
        await WaitFor(state => state.Findings.All(finding => !finding.Excluded));
        await Picture("removed");
        Key("Escape"); await Window(dialog, false);
        await SaveCheckedThroughMcp(client, board, evidence, token);
        var cleared = JsonNode.Parse(await File.ReadAllBytesAsync(project, token))!;
        Assert.AreEqual(0, cleared["board"]!["design_settings"]!["drc_exclusions"]!.AsArray().Count);
        await client.InvokeAsync<RevertDocument, Empty>(new() { Document = board }, token);
        Assert.IsTrue((await Read()).Findings.All(finding => !finding.Excluded));
        // A detached DRC job's window-activation checkpoint needs this step's rendered editor windows.
        await VerifyPcbDrcJobActivation(client, board, processId, display, evidence, agent, token);
        // The router settings reach a check only through the rendered editor's Interactive Router Settings dialog.
        await VerifyPcbDrcJobRouterChange(client, board, processId, display, evidence, agent, token);
        // The board's current variant reaches a check only through the rendered editor's variant choice.
        await VerifyPcbDrcJobVariantChange(client, board, processId, display, evidence, agent, token);
        // A footprint library the rendered editor loaded with the project, edited in place.
        await VerifyPcbDrcJobLibraryEdit(client, board, processId, display, evidence, agent, token);
        // Checks in this project and its sibling KiCad at once, driven by one agent over MCP; it needs the rendered
        // PCB editor of this step for the person's keyboard edit.
        await VerifyPcbDrcTwoProjects(client, board, processId, display, evidence, token);
    }
}
