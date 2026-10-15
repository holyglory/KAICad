using System.Text.Json;
using KiCad.Automation.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class UpdateRecoveryTests
{
    [TestMethod]
    public async Task OversizedJournalRecordIsRejectedBeforeCreatingAFile()
    {
        string root = Directory.CreateTempSubdirectory("kicad-journal-limit-").FullName;
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxUpdateHandoff.SaveAsync(
                Path.Combine(root, "intent.json"), new { text = new string('x', 65537) }, CancellationToken.None));
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void RecoveryContextContainsOnlyReviewedDisplayAndProfileKeys()
    {
        var context = UpdateLaunchEnvironment.Capture(new Dictionary<string, string?>
        {
            ["DISPLAY"] = ":fixture", ["KICAD_CONFIG_HOME"] = "/tmp/profile-fixture",
            ["KICAD_AUTOMATION_UPDATE_HELPER"] = "untrusted-version-override", ["PATH"] = "untrusted-path",
            ["SYNTHETIC_SECRET"] = "Synthetic test data, not a credential."
        });
        Assert.HasCount(9, context);
        Assert.AreEqual(":fixture", context["DISPLAY"]);
        Assert.AreEqual("/tmp/profile-fixture", context["KICAD_CONFIG_HOME"]);
        Assert.IsFalse(context.ContainsKey("PATH"));
        Assert.IsFalse(context.ContainsKey("SYNTHETIC_SECRET"));
        Assert.IsFalse(context.ContainsKey("KICAD_AUTOMATION_UPDATE_HELPER"));
        var invalid = context.ToDictionary(pair => pair.Key, pair => pair.Value);
        invalid["PATH"] = "extra";
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateLaunchEnvironment.Validate(invalid));
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateLaunchEnvironment.Validate(null));
    }

    [TestMethod]
    public async Task MissingLockedAndLegacyRecoveryTargetsRemainUntouchedAndCancellationDoesNotStartWork()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux update recovery."); return; }
        string root = Directory.CreateTempSubdirectory("kicad-recovery-input-").FullName;
        Guid operation = Guid.NewGuid(), attempt = Guid.NewGuid();
        try
        {
            Assert.AreEqual("journal_missing", (await LinuxUpdateRecovery.RecoverAsync(root, operation, attempt)).Status);
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
            string journal = Directory.CreateDirectory(Path.Combine(root, "handovers", operation.ToString("D"))).FullName;
            string path = Path.Combine(journal, "operation.lock");
            await File.WriteAllBytesAsync(path, []);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.AreEqual("operation_unavailable", (await LinuxUpdateRecovery.RecoverAsync(root, operation, attempt)).Status);
            Assert.AreEqual("legacy_journal_unverifiable", (await LinuxUpdateRecovery.RecoverAsync(root, operation, attempt)).Status);
            Assert.HasCount(1, Directory.GetFileSystemEntries(journal));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                LinuxUpdateRecovery.RecoverAsync(root, operation, attempt, cancellation.Token));
            Assert.HasCount(1, Directory.GetFileSystemEntries(journal));
            using var output = new StringWriter();
            Assert.AreEqual(1, await LinuxUpdateRecoveryCommand.RunAsync(["--recover-update"], output, CancellationToken.None));
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("not_started", document.RootElement.GetProperty("launchOutcome").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("restored", false, false, 0)]
    [DataRow("original_running", false, false, 0)]
    [DataRow("attempt_already_recorded", false, true, 0)]
    [DataRow("attempt_not_current", false, true, 1)]
    [DataRow("operation_unavailable", false, true, 1)]
    [DataRow("launch_failed", false, false, 1)]
    [DataRow("unexpected", true, false, 0)]
    public void RecoveryCommandExitCodeDoesNotTreatStaleAttemptsAsSuccess(
        string status, bool nativeEditorRestarted, bool reused, int expected)
    {
        var result = new WindowsUpdateRecoveryResult(status, Guid.NewGuid(), Guid.NewGuid(),
            NativeEditorRestarted: nativeEditorRestarted, Reused: reused);
        Assert.AreEqual(expected, WindowsUpdateRecoveryCommand.ExitCode(result));
    }
}
