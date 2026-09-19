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
            foreach (string text in new[] { "Keep power paths short.", "Keep high-current paths away from sensing." })
            {
                var draft = graph.StartDraft(psu);
                draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, text) };
                graph = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot, psu], draft, Guid.NewGuid(), Guid.NewGuid(),
                    [Guid.NewGuid()], RecursiveBlockFixture.Origin(text.Contains("sensing", StringComparison.Ordinal) ? "AI agent" : "Fixture user")).Graph;
                psu = graph.Inspect(graph.SelectedRoot).Children[0];
            }
            var page = DiagramFieldHistoryQuery.Block(graph, psu, DiagramRequirementField.Routing);
            string input = Path.Combine(temporary, "field-history.pb");
            await File.WriteAllBytesAsync(input, RecursiveBlockCodec.Encode(page).ToByteArray());
            var start = new ProcessStartInfo("xvfb-run")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = temporary };
            foreach (string arg in new[] { "-a", "-s", "-screen 0 1280x1024x24 -nolisten tcp", executable,
                "--run_test=DiagramFieldHistory", "--log_level=test_suite" }) start.ArgumentList.Add(arg);
            start.Environment["KICAD_FIELD_HISTORY_INPUT"] = input;
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
            foreach (string check in new[] { "cancelled_without_restore", "reopen_cleared_restore", "scope_isolation", "compact_controls_visible" })
                Assert.IsTrue(result.GetProperty(check).GetBoolean(), check);
            foreach (string capture in new[] { "01-current.png", "02-earlier-text.png", "03-compact.png" })
                Assert.IsTrue(new FileInfo(Path.Combine(evidence, capture)).Length > 1000, "A rendered capture is required: " + capture);
            var history = graph.RequirementHistories.Single(h => h.Scope.DesignStateId == psu.StateId);
            var working = graph.StartDraft(psu).Requirements.Edit(DiagramRequirementField.General, "Independent unsaved text.");
            var restoredDraft = history.RestoreField(working, restored, DiagramRequirementField.Routing);
            Assert.AreEqual("Independent unsaved text.", restoredDraft.Requirements.General);
            Assert.AreEqual("Keep power paths short.", restoredDraft.Requirements.Routing);
            Assert.AreEqual("Keep high-current paths away from sensing.", graph.Requirements(psu).Requirements.Routing);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "automation", "KiCad.Automation.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The compiled test must run from its KiCad worktree.");
    }
}
