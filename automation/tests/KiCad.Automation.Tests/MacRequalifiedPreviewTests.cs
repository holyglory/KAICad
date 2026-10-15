using System.Text;
using System.Text.Json;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class MacRequalifiedPreviewTests
{
    [TestMethod]
    public void ProvenanceRequiresExactFailedArmReceiptAndCurrentRecoveryProof()
    {
        const string commit = "e40d567a4350591dc991cd670f07adeea55444c5";
        const string hash = "24f570075d40b52dff3813a0b2e3fcc1982461083441ad9d5bec4fc3271ef295";
        JsonElement inputs = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, sourceCommit = commit, archiveSha256 = hash, archiveBytes = 338812628L,
            originalRunId = "38639617642", rebuiltNativeCode = false
        });
        JsonElement result = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, status = "passed", sourceCommit = commit, archiveSha256 = hash,
            exactRetainedPayload = true, currentManagedRecoveryPassed = true, rebuiltNativeCode = false,
            originalReceiptUnchanged = true, nativeEditorJourneyAvailable = false, crashUndoClosed = false
        });
        MacRequalifiedPreview.ValidateQualification(inputs, result, commit, hash, 338812628L, "38639617642");

        foreach (string invalid in new[] { "archiveBytes", "sourceCommit", "archiveSha256" })
        {
            string property = invalid == "archiveBytes" ? "\"archiveBytes\":338812628"
                : invalid == "sourceCommit" ? "\"sourceCommit\":\"" + commit + "\""
                : "\"archiveSha256\":\"" + hash + "\"";
            string replacement = invalid == "archiveBytes" ? "\"archiveBytes\":1"
                : invalid == "sourceCommit" ? "\"sourceCommit\":\"" + new string('f', 40) + "\""
                : "\"archiveSha256\":\"" + new string('a', 64) + "\"";
            JsonElement changed = JsonDocument.Parse(inputs.GetRawText().Replace(property, replacement, StringComparison.Ordinal)).RootElement.Clone();
            Assert.ThrowsExactly<InvalidDataException>(() =>
                MacRequalifiedPreview.ValidateQualification(changed, result, commit, hash, 338812628L, "38639617642"));
        }

        JsonElement unavailable = JsonDocument.Parse(result.GetRawText().Replace("\"crashUndoClosed\":false", "\"crashUndoClosed\":true", StringComparison.Ordinal)).RootElement.Clone();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            MacRequalifiedPreview.ValidateQualification(inputs, unavailable, commit, hash, 338812628L, "38639617642"));
    }

    [TestMethod]
    public void PassingReceiptRejectsSkippedOrDifferentRequiredTests()
    {
        string passing = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="PcbExportRecoveryTests.CancellationRefusalReconcilesLiveTerminalReceipt" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """;
        MacRequalifiedPreview.RequirePassingTests(Encoding.UTF8.GetBytes(passing), "CancellationRefusalReconcilesLiveTerminalReceipt");
        Assert.ThrowsExactly<InvalidDataException>(() => MacRequalifiedPreview.RequirePassingTests(
            Encoding.UTF8.GetBytes(passing.Replace("CancellationRefusalReconcilesLiveTerminalReceipt", "Other", StringComparison.Ordinal)),
            "CancellationRefusalReconcilesLiveTerminalReceipt"));
        Assert.ThrowsExactly<InvalidDataException>(() => MacRequalifiedPreview.RequirePassingTests(
            Encoding.UTF8.GetBytes(passing.Replace("passed=\"1\"", "passed=\"0\"", StringComparison.Ordinal))));
    }
}
