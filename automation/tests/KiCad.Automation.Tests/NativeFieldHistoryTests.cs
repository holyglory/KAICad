using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
[TestCategory("NativeFieldHistory")]
public sealed class NativeFieldHistoryTests
{
    [TestMethod]
    [DataRow("Adwaita", "light")]
    [DataRow("Adwaita:dark", "dark")]
    public async Task RenderedHistoryReturnsOnlyTheChosenFieldRevision(string gtkTheme, string theme)
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("This native display journey qualifies Linux only.");
        string root = FindRoot();
        string executable = Path.Combine(root, "automation", "artifacts", "native", "qa", "tests", "common", "qa_diagram_field_history");
        Assert.IsTrue(File.Exists(executable), "Build the native GUI-test executable before this journey.");
        string evidence = NativeEvidenceDirectory.Begin(Path.Combine(root, "automation", "artifacts", "native-field-history", theme));
        string temporary = Directory.CreateTempSubdirectory("kicad-history-gui-").FullName;
        try
        {
            var fixture = RecursiveBlockFixture.Create(); var graph = fixture.Graph; var psu = fixture.Selected["PSU"];
            // The first text is saved in the PSU's initial implementation. An agent then duplicates that implementation,
            // rewrites the text in the duplicate and chooses it: the duplicate's field history continues the initial one's,
            // so the dialog lists (and can restore) the text saved before the switch (ledger p390b40bed99e0ab2).
            var draft = graph.StartDraft(psu);
            draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Keep power paths short.") };
            graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, psu], draft, Guid.NewGuid(), Guid.NewGuid(),
                [Guid.NewGuid()], RecursiveBlockFixture.Origin("Fixture user")).Graph;
            var initialPsu = graph.Inspect(graph.SelectedRoot).Children[0];
            Guid duplicate = Guid.NewGuid(), duplicateRevision = Guid.NewGuid();
            graph = graph.ForkImplementation(initialPsu, duplicate, duplicateRevision, Guid.NewGuid(), "Sensing-aware supply",
                RecursiveBlockFixture.Origin("AI agent"));
            var agentDraft = graph.StartDraft(new(initialPsu.BlockId, duplicate, duplicateRevision));
            agentDraft = agentDraft with { Requirements = agentDraft.Requirements.Edit(DiagramRequirementField.Routing, "Keep high-current paths away from sensing.") };
            graph = graph.SaveImplementationDraft(graph.SelectedRoot, [graph.SelectedRoot, initialPsu], agentDraft, Guid.NewGuid(), Guid.NewGuid(),
                [Guid.NewGuid()], RecursiveBlockFixture.Origin("AI agent")).Graph;
            psu = graph.Inspect(graph.SelectedRoot).Children[0];
            Assert.AreEqual(duplicate, psu.StateId);
            var page = DiagramFieldHistoryQuery.Block(graph, psu, DiagramRequirementField.Routing);
            CollectionAssert.AreEqual(new[] { "Keep high-current paths away from sensing.", "Keep power paths short.", "" },
                page.Entries.Select(e => e.Text).ToArray());
            CollectionAssert.AreEqual(new[] { duplicate, initialPsu.StateId, initialPsu.StateId }, page.Entries.Select(e =>
                graph.Revisions.Single(r => r.Selection.RevisionId == e.ContextRevisionId).Selection.StateId).ToArray());
            string input = Path.Combine(temporary, "field-history.pb");
            await File.WriteAllBytesAsync(input, RecursiveBlockCodec.Encode(page).ToByteArray());
            // The rendered dialog labels its rows with the editor's own row builder, from the diagram the page was read from.
            string graphInput = Path.Combine(temporary, "field-history-graph.pb");
            await File.WriteAllBytesAsync(graphInput, RecursiveBlockCodec.Encode(graph).ToByteArray());
            var longGraph = RecursiveBlockFixture.RefineRoot(fixture.Graph, DiagramRequirementField.General, 205);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "history-graph.pb"), RecursiveBlockCodec.Encode(longGraph).ToByteArray());
            for (int offset = 0; offset < 206; offset += 200)
                await File.WriteAllBytesAsync(Path.Combine(temporary, $"history-page-{offset}.pb"), RecursiveBlockCodec.Encode(
                    DiagramFieldHistoryQuery.Block(longGraph, longGraph.SelectedRoot, DiagramRequirementField.General, offset, 200)).ToByteArray());
            for (int offset = 0; offset < 2; ++offset)
                await File.WriteAllBytesAsync(Path.Combine(temporary, $"diagram-page-{offset}.pb"), RecursiveBlockCodec.Encode(
                    DiagramHistoryQuery.Read(longGraph, longGraph.SelectedRoot, offset, 1)).ToByteArray());
            var preceding = longGraph.History(longGraph.SelectedRoot.StateId)[1].Selection;
            await File.WriteAllBytesAsync(Path.Combine(temporary, "diagram-comparison.pb"), RecursiveBlockCodec.Encode(
                DiagramHistoryQuery.Compare(longGraph, longGraph.SelectedRoot, preceding)).ToByteArray());
            var start = new ProcessStartInfo("xvfb-run")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = temporary };
            foreach (string arg in new[] { "-a", "-s", "-screen 0 1280x1024x24 -nolisten tcp", executable,
                "--run_test=DiagramFieldHistory", "--log_level=test_suite" }) start.ArgumentList.Add(arg);
            start.Environment["KICAD_FIELD_HISTORY_INPUT"] = input;
            start.Environment["KICAD_FIELD_HISTORY_GRAPH"] = graphInput;
            start.Environment["KICAD_FIELD_HISTORY_PAGES"] = temporary;
            start.Environment["KICAD_FIELD_HISTORY_EVIDENCE"] = evidence;
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(temporary, "config");
            start.Environment["KICAD_CONFIG_HOME"] = Path.Combine(temporary, "config", "kicad");
            start.Environment["KICAD_RUN_FROM_BUILD_DIR"] = "1";
            start.Environment["GTK_THEME"] = gtkTheme;
            start.Environment["GSETTINGS_BACKEND"] = "memory";
            start.Environment["LC_ALL"] = "C.UTF-8";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await File.WriteAllTextAsync(Path.Combine(evidence, "native.stdout.log"), await stdout);
                await File.WriteAllTextAsync(Path.Combine(evidence, "native.stderr.log"), await stderr);
            }
            Assert.AreEqual(0, process.ExitCode, "The rendered native assertions failed; inspect retained native logs.");
            string receiptPath = Path.Combine(evidence, "interaction.json");
            Assert.IsTrue(File.Exists(receiptPath), "A skipped or unexecuted native case is not GUI evidence.");
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            var result = receipt.RootElement;
            Assert.AreEqual(page.Scope.DocumentId.ToString("D"), result.GetProperty("document_id").GetString());
            Assert.AreEqual(page.Scope.OwnerId.ToString("D"), result.GetProperty("owner_id").GetString());
            Assert.AreEqual(page.ContextRevisionId.ToString("D"), result.GetProperty("context_revision_id").GetString());
            Guid restored = Guid.ParseExact(result.GetProperty("restore_requirement_revision_id").GetString()!, "D");
            Assert.AreEqual(page.Entries[1].RequirementRevisionId, restored);
            foreach (string check in new[] { "cancelled_without_restore", "reopen_cleared_restore", "scope_isolation", "compact_controls_visible",
                "compact_author_visible" })
                Assert.IsTrue(result.GetProperty(check).GetBoolean(), check);
            // Both texts below the saved one were saved in the implementation the chosen one was made from; each row names it,
            // so its version 2 cannot be mistaken for version 2 of the chosen implementation. The selected row's heading
            // also names its author, which the narrow list can cut off.
            string initialName = graph.States.Single(s => s.Id == initialPsu.StateId).Name;
            Assert.AreEqual("Initial approach", initialName);
            string[] wholeRows = [.. result.GetProperty("row_labels").EnumerateArray().Select(r => r.GetString()!)];
            CollectionAssert.AreEqual(new[] { "v2 · Saved · AI agent", $"{initialName} · v2 · Fixture user", $"{initialName} · v1 · Fixture user" }, wholeRows);
            // Mockup audit M1-3 (review of e62ac6dce7): in the list every row shows its version and author whole, as the approved
            // rows read "v2 · User"; a row too wide for the list shortens only the name of the earlier implementation it was
            // saved in ("Initi… · v2 · Fixture user"). The native case measures each row as drawn against its cell in GTK, at the
            // default and the compact size, so GTK's own "…" cuts nothing of it.
            string[] names = ["", initialName, initialName];
            foreach (string key in new[] { "shown_rows", "compact_shown_rows" })
            {
                string[] shown = [.. result.GetProperty(key).EnumerateArray().Select(r => r.GetString()!)];
                Assert.HasCount(wholeRows.Length, shown, $"{theme}: {key} lists every row.");
                for (int row = 0; row < shown.Length; ++row)
                    Assert.IsTrue(IsShownHistoryRow(wholeRows[row], names[row], shown[row]),
                        $"{theme}: {key} row {row} reads '{shown[row]}': '{wholeRows[row]}' whole, or with only '{names[row]}' shortened.");
                Assert.AreNotEqual(wholeRows[1], shown[1], $"{theme}: {key}: the earlier implementation's rows are too wide for the list whole.");
            }
            Assert.IsTrue(result.GetProperty("rows_fit").GetBoolean(), theme + ": every history row as drawn fits the list, so GTK cuts none of it.");
            Assert.IsTrue(result.GetProperty("compact_rows_fit").GetBoolean(), theme + ": every history row as drawn fits the list in the compact dialog.");
            Assert.AreEqual($"{initialName} · v2 · Fixture user — Selected text", result.GetProperty("selected_heading").GetString());
            // In the compact dialog the heading is too narrow for the whole text: only the implementation's name is shortened, and
            // the version and author stay whole (the native case also checks that the shown heading fits its column).
            string compactHeading = result.GetProperty("compact_heading").GetString()!;
            StringAssert.EndsWith(compactHeading, "v2 · Fixture user — Selected text");
            Assert.AreNotEqual($"{initialName} · v2 · Fixture user — Selected text", compactHeading, "The compact heading shortens the implementation's name.");
            Assert.AreEqual($"Use {initialName} · v2 text in draft", result.GetProperty("restore_label").GetString());
            // An "&" in an implementation's name shows on the Use button as written, not as a keyboard mnemonic.
            Assert.AreEqual("Use C&K approach · v2 text in draft", result.GetProperty("ampersand_restore_label").GetString());
            // Mockup audit M1-3 to M1-5, measured in the rendered dialog (02-earlier-text.png): a row too wide for the list even
            // with its implementation's name shortened ends in "…" instead of being cut off mid-word; "Use … text in draft" is
            // filled with the accent like the editor's Save (3:1 from the dialog, its label 4.5:1 on the fill), as the approved
            // mockup shows it; and both compared texts start 8 pixels or more inside their boxes, as the editor's boxes do
            // (design QA P2-9).
            Assert.IsTrue(result.GetProperty("rows_ellipsize").GetBoolean(), theme + ": a row too wide even for its version and author ends in \"…\".");
            Assert.IsTrue(result.GetProperty("restore_fill_on_dialog").GetDouble() >= 3.0,
                $"{theme}: the Use button's fill stands {result.GetProperty("restore_fill_on_dialog").GetDouble():F2}:1 from the dialog, 3:1 or more.");
            Assert.IsTrue(result.GetProperty("restore_label_on_fill").GetDouble() >= 4.5,
                $"{theme}: the Use button's label reads {result.GetProperty("restore_label_on_fill").GetDouble():F2}:1 on its fill, 4.5:1 or more.");
            foreach (string inset in new[] { "selected_text_inset", "saved_text_inset" })
                Assert.IsTrue(result.GetProperty(inset).GetInt32() >= 8, $"{theme}: {inset} is {result.GetProperty(inset).GetInt32()} pixels, 8 or more.");
            foreach (string capture in new[] { "01-current.png", "02-earlier-text.png", "03-compact.png", "04-conflict-unresolved.png", "05-conflict-resolved.png" })
                Assert.IsTrue(new FileInfo(Path.Combine(evidence, capture)).Length > 1000, "A rendered capture is required: " + capture);
            byte[] currentCapture = await File.ReadAllBytesAsync(Path.Combine(evidence, "01-current.png"));
            byte[] earlierCapture = await File.ReadAllBytesAsync(Path.Combine(evidence, "02-earlier-text.png"));
            Assert.IsFalse(currentCapture.SequenceEqual(earlierCapture),
                "Changing the selected revision must produce a new rendered frame, not a stale screenshot.");
            var history = graph.RequirementHistories.Single(h => h.Scope.DesignStateId == psu.StateId);
            var working = graph.StartDraft(psu).Requirements.Edit(DiagramRequirementField.General, "Independent unsaved text.");
            var restoredDraft = history.RestoreField(working, restored, DiagramRequirementField.Routing);
            Assert.AreEqual("Independent unsaved text.", restoredDraft.Requirements.General);
            Assert.AreEqual("Keep power paths short.", restoredDraft.Requirements.Routing);
            Assert.AreEqual("Keep high-current paths away from sensing.", graph.Requirements(psu).Requirements.Routing);
            // Saving the restored text written before the switch is a new revision of the chosen implementation that
            // names the earlier implementation's revision it came from; it survives the diagram file.
            var restoring = graph.StartDraft(psu) with { Requirements = restoredDraft };
            var restoredGraph = RecursiveBlockGraphXml.Read(RecursiveBlockGraphXml.Write(graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, psu],
                restoring, Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], RecursiveBlockFixture.Origin("Fixture user")).Graph));
            var restoredHistory = restoredGraph.RequirementHistories.Single(h => h.Scope.DesignStateId == duplicate);
            Assert.AreEqual(history.Current.Id, restoredHistory.Current.ParentId);
            Assert.AreEqual(new RequirementFieldRestoration(DiagramRequirementField.Routing, restored), restoredHistory.Current.Restorations.Single());
            var afterRestore = DiagramFieldHistoryQuery.Block(restoredGraph, restoredGraph.Inspect(restoredGraph.SelectedRoot).Children[0],
                DiagramRequirementField.Routing);
            CollectionAssert.AreEqual(new[] { restoredHistory.Current.Id }.Concat(page.Entries.Select(e => e.RequirementRevisionId)).ToArray(),
                afterRestore.Entries.Select(e => e.RequirementRevisionId).ToArray());
            Assert.AreEqual("Keep power paths short.", afterRestore.SavedText);
            using var conflict = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "conflict-interaction.json")));
            Assert.IsTrue(conflict.RootElement.GetProperty("no_default_choice").GetBoolean());
            Assert.IsTrue(conflict.RootElement.GetProperty("partial_resolution_blocked").GetBoolean());
            Assert.AreEqual("Keep both edges accessible.", conflict.RootElement.GetProperty("routing_text").GetString());
            Assert.AreEqual("Prefer fixed mounting.", conflict.RootElement.GetProperty("general_text").GetString());
            // Mockup audit M1-6: the words in which the draft and the latest saved text differ are marked in both, tinted (the
            // tint drawn, its text 7:1 or more on it) and underlined. The approved conflict mockup marks the phrases "top edge"
            // and "bottom edge"; the editor marks the words that differ, "top" and "bottom" (a P3 difference in design-qa.md).
            // Mockup audit M1-4 and M1-5: Save resolved version is filled with the accent once a choice is made for every field,
            // and the texts start 8 pixels or more inside their boxes.
            var conflictResult = conflict.RootElement;
            CollectionAssert.AreEqual(new[] { "top" }, conflictResult.GetProperty("draft_marked").EnumerateArray().Select(w => w.GetString()).ToArray());
            CollectionAssert.AreEqual(new[] { "bottom" }, conflictResult.GetProperty("saved_marked").EnumerateArray().Select(w => w.GetString()).ToArray());
            foreach (string side in new[] { "draft", "saved" })
            {
                Assert.IsTrue(conflictResult.GetProperty(side + "_mark_pixels").GetInt32() >= 40,
                    $"{theme}: the {side} text's mark is drawn ({conflictResult.GetProperty(side + "_mark_pixels").GetInt32()} pixels in its tint).");
                Assert.IsTrue(conflictResult.GetProperty(side + "_mark_text_on_fill").GetDouble() >= 4.5,
                    $"{theme}: the marked {side} word reads {conflictResult.GetProperty(side + "_mark_text_on_fill").GetDouble():F2}:1 on its tint, 4.5:1 or more.");
            }
            foreach (string inset in new[] { "base_text_inset", "draft_text_inset", "saved_text_inset" })
                Assert.IsTrue(conflictResult.GetProperty(inset).GetInt32() >= 8, $"{theme}: conflict {inset} is {conflictResult.GetProperty(inset).GetInt32()} pixels, 8 or more.");
            Assert.IsTrue(conflictResult.GetProperty("save_fill_on_dialog").GetDouble() >= 3.0,
                $"{theme}: Save resolved version's fill stands {conflictResult.GetProperty("save_fill_on_dialog").GetDouble():F2}:1 from the dialog, 3:1 or more.");
            Assert.IsTrue(conflictResult.GetProperty("save_label_on_fill").GetDouble() >= 4.5,
                $"{theme}: Save resolved version's label reads {conflictResult.GetProperty("save_label_on_fill").GetDouble():F2}:1 on its fill, 4.5:1 or more.");
            using var paging = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "paging-interaction.json")));
            foreach (string check in new[] { "selection_preserved", "failure_preserved_rows", "malformed_page_rejected", "cancelled_load_without_restore" })
                Assert.IsTrue(paging.RootElement.GetProperty(check).GetBoolean(), check);
            Assert.AreEqual(206, paging.RootElement.GetProperty("loaded_count").GetInt32());
            Assert.AreEqual(fixture.Graph.Requirements(fixture.Graph.SelectedRoot).RevisionId.ToString("D"),
                paging.RootElement.GetProperty("oldest_revision_id").GetString());
            foreach (string capture in new[] { "06-history-loading.png", "07-history-load-error.png", "08-history-oldest.png" })
                Assert.IsTrue(new FileInfo(Path.Combine(evidence, capture)).Length > 1000, capture);
            using var wholePanel = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "diagram-panel-interaction.json")));
            foreach (string check in new[] { "inspection_read_only", "comparison_target_rejected", "preview_explicit", "return_explicit", "restore_explicit", "cancelled" })
                Assert.IsTrue(wholePanel.RootElement.GetProperty(check).GetBoolean(), check);
            // Mockup audit M1-4 and M1-5 in the whole-diagram history panel (09-diagram-history-panel.png): Restore as draft is
            // filled with the accent, as the approved history mockup shows it, and the comparison text keeps clear of its border.
            Assert.IsTrue(wholePanel.RootElement.GetProperty("restore_fill_on_panel").GetDouble() >= 3.0,
                $"{theme}: Restore as draft's fill stands {wholePanel.RootElement.GetProperty("restore_fill_on_panel").GetDouble():F2}:1 from the panel, 3:1 or more.");
            Assert.IsTrue(wholePanel.RootElement.GetProperty("restore_label_on_fill").GetDouble() >= 4.5,
                $"{theme}: Restore as draft's label reads {wholePanel.RootElement.GetProperty("restore_label_on_fill").GetDouble():F2}:1 on its fill, 4.5:1 or more.");
            Assert.IsTrue(wholePanel.RootElement.GetProperty("details_inset").GetInt32() >= 8,
                $"{theme}: the comparison text starts {wholePanel.RootElement.GetProperty("details_inset").GetInt32()} pixels inside its box, 8 or more.");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    /// <summary>Whether <paramref name="shown"/> is how the field-history list may show the row <paramref name="whole"/>
    /// (mockup audit M1-3): whole, or with only the leading name of the earlier implementation it was saved in,
    /// <paramref name="name"/>, shortened with "…" or replaced by "…" or left out, so the version and author after it are
    /// always whole.</summary>
    internal static bool IsShownHistoryRow(string whole, string name, string shown)
    {
        if (shown == whole) return true;
        if (name.Length == 0 || !whole.StartsWith(name + " · ", StringComparison.Ordinal)) return false;
        string rest = whole[(name.Length + " · ".Length)..];
        if (shown == rest || shown == "… · " + rest) return true;
        if (!shown.EndsWith("… · " + rest, StringComparison.Ordinal)) return false;
        string kept = shown[..^("… · " + rest).Length];
        return kept.Length > 0 && kept.Length < name.Length && name.StartsWith(kept, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "automation", "KiCad.Automation.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The compiled test must run from its KiCad worktree.");
    }
}
