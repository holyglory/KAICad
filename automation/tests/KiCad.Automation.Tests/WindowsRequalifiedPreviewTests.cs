using System.Text;
using System.Text.Json;
using KiCad.Automation.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class WindowsRequalifiedPreviewTests
{
    [TestMethod]
    public void ANewPassingRecordMustBindTheOriginalFailedBuildAndExactBytes()
    {
        string commit = new('a', 40), hash = new('b', 64), harness = new('c', 40);
        var original = JsonSerializer.SerializeToElement(new
        {
            SchemaVersion = 1, Status = "failed", SourceCommit = commit, Platform = "windows", Architecture = "x64", RunId = "123",
            Steps = new[] { "source-commit", "pinned-ancestry", "native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime", "managed-contracts" }
                .Select(name => new { Name = name, ExitCode = 0 }).Append(new { Name = "installed-editor-journey", ExitCode = 1 }).ToArray(),
            DiagnosticArtifacts = new[] { new { Path = "unqualified-windows-" + commit + ".zip", Bytes = 1234L, Sha256 = hash } }
        });
        var inputs = JsonSerializer.SerializeToElement(new { sourceCommit = commit, archiveSha256 = hash, archiveBytes = 1234L,
            originalRunId = "123", rebuiltNativeCode = false, harnessCommit = harness });
        var result = JsonSerializer.SerializeToElement(new { schemaVersion = 1, status = "passed", sourceCommit = commit,
            archiveSha256 = hash, rebuiltNativeCode = false, exactRetainedPayload = true, nativeEditorJourneyPassed = true, originalReceiptUnchanged = true });
        var bound = WindowsRequalifiedPreview.ValidateProvenance(original, inputs, result, commit);
        Assert.AreEqual(hash, bound.Sha256); Assert.AreEqual(harness, bound.HarnessCommit);
        var managedFailure = Replace(original, "\"Name\":\"managed-contracts\",\"ExitCode\":0", "\"Name\":\"managed-contracts\",\"ExitCode\":1");
        var originalNode = System.Text.Json.Nodes.JsonNode.Parse(managedFailure.GetRawText())!;
        var steps = originalNode["Steps"]!.AsArray(); steps.RemoveAt(steps.Count - 1);
        managedFailure = JsonSerializer.SerializeToElement(originalNode);
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(managedFailure, inputs, result, commit));
        var recoveryNode = System.Text.Json.Nodes.JsonNode.Parse(result.GetRawText())!;
        recoveryNode["managedContractsPassed"] = true; recoveryNode["managedRuntimeSourceUnchanged"] = true;
        var recovery = JsonSerializer.SerializeToElement(recoveryNode);
        Assert.IsTrue(WindowsRequalifiedPreview.ValidateProvenance(managedFailure, inputs, recovery, commit).RequiresManagedRecovery);
        foreach (string field in new[] { "managedContractsPassed", "managedRuntimeSourceUnchanged" })
        {
            var bad = recoveryNode.DeepClone(); bad[field] = false;
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(managedFailure, inputs,
                JsonSerializer.SerializeToElement(bad), commit));
        }
        foreach (var invalid in new[] { Replace(result, "passed", "failed"), Replace(result, hash, new string('d', 64)),
            Replace(result, "\"nativeEditorJourneyPassed\":true", "\"nativeEditorJourneyPassed\":false") })
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(original, inputs, invalid, commit));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(Replace(original,
            "\"Name\":\"native-build\",\"ExitCode\":0", "\"Name\":\"native-build\",\"ExitCode\":1"), inputs, result, commit));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(original,
            Replace(inputs, "\"archiveBytes\":1234", "\"archiveBytes\":1235"), result, commit));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.ValidateProvenance(original, inputs, result, new string('e', 40)));
    }

    [TestMethod]
    public void FunctionalResultCannotHideAFailedCleanupOrSkippedOuterTest()
    {
        string xml = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="OperateTheExactRetainedNativePackageWithoutRebuildingIt" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """;
        WindowsRequalifiedPreview.RequirePassingOuterTest(Encoding.UTF8.GetBytes(xml));
        foreach (string invalid in new[] { xml.Replace("outcome=\"Passed\"", "outcome=\"Failed\""),
            xml.Replace("failed=\"0\"", "failed=\"1\""), xml.Replace("executed=\"1\"", "executed=\"0\""),
            xml.Replace("WithoutRebuildingIt", "SomeOtherTest") })
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.RequirePassingOuterTest(Encoding.UTF8.GetBytes(invalid)));
    }

    private static JsonElement Replace(JsonElement value, string before, string after)
    {
        using var result = JsonDocument.Parse(value.GetRawText().Replace(before, after, StringComparison.Ordinal));
        return result.RootElement.Clone();
    }

    [TestMethod]
    public void ManagedRecoveryNeedsEveryRequiredClassAndAllPassingResults()
    {
        string[] classes = ["HostedDeliveryTests", "RuntimeInfoTests", "NngTransportTests", "NativeIpcEndpointTests"];
        string definitions = string.Concat(classes.Select((name, index) =>
            $"<UnitTest id='{index}'><TestMethod className='KiCad.Automation.Tests.{name}' /></UnitTest>"));
        string results = string.Concat(classes.Select((_, index) => $"<UnitTestResult testId='{index}' outcome='Passed' />"));
        string xml = $"<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><TestDefinitions>{definitions}</TestDefinitions>"
            + $"<Results>{results}</Results><Counters total='4' executed='4' passed='4' failed='0' error='0' timeout='0' aborted='0' inconclusive='0' notExecuted='0' /></TestRun>";
        WindowsRequalifiedPreview.RequirePassingManagedTests(Encoding.UTF8.GetBytes(xml));
        foreach (string invalid in new[] { xml.Replace("NngTransportTests", "UnrelatedTests"),
            xml.Replace("outcome='Passed'", "outcome='NotExecuted'"), xml.Replace("passed='4'", "passed='3'"),
            xml.Replace("error='0'", "error='1'"), xml.Replace("testId='3'", "testId='missing'") })
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsRequalifiedPreview.RequirePassingManagedTests(Encoding.UTF8.GetBytes(invalid)));
    }
}
