using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public sealed record WindowsRequalifiedPreviewRequest(string NativeCandidate, string Qualification, string Repository,
    string Commit, string QualificationRunId, string Version, string Previous, string Output);

/// <summary>Admit unchanged diagnostic native bytes only after a clean, bound native
/// editor rerun. The original failed receipt is never rewritten or relabelled.</summary>
public static class WindowsRequalifiedPreview
{
    public static async Task<object> RunAsync(WindowsRequalifiedPreviewRequest request, CancellationToken token)
    {
        Evidence.RequireCommit(request.Commit);
        foreach (string directory in new[] { request.NativeCandidate, request.Qualification, request.Repository, request.Previous, request.Output })
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use absolute paths for requalification inputs and output.");
        if (request.QualificationRunId.Length == 0 || !request.QualificationRunId.All(char.IsAsciiDigit)
            || request.Version.Length is < 1 or > 128 || !request.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("Use an explicit native qualification run and a valid preview version.");
        if (Directory.Exists(request.Output) || File.Exists(request.Output)) throw new IOException("Preserve the existing output.");
        byte[] originalBytes = await HostedPreviewStaging.Metadata(Path.Combine(request.NativeCandidate, "receipt.json"), token);
        using var original = JsonDocument.Parse(originalBytes);
        string evidence = Path.Combine(request.Qualification, "evidence");
        using var inputs = JsonDocument.Parse(await HostedPreviewStaging.Metadata(Path.Combine(evidence, "windows-retained-package/inputs.json"), token));
        using var result = JsonDocument.Parse(await HostedPreviewStaging.Metadata(Path.Combine(evidence, "windows-retained-package/result.json"), token));
        var binding = ValidateProvenance(original.RootElement, inputs.RootElement, result.RootElement, request.Commit);
        string[] runs = Directory.GetFiles(Path.Combine(request.Qualification, "retained"), "*.trx", SearchOption.TopDirectoryOnly);
        if (runs.Length != 1) throw new InvalidDataException("Require one complete outer retained-package test receipt.");
        byte[] trxBytes = await HostedPreviewStaging.Metadata(runs[0], token);
        RequirePassingOuterTest(trxBytes);
        if (binding.RequiresManagedRecovery)
        {
            string[] managedRuns = Directory.GetFiles(Path.Combine(evidence, "windows-retained-package/managed-run"), "*.trx");
            if (managedRuns.Length != 1) throw new InvalidDataException("Require one exact managed-contract recovery receipt.");
            RequirePassingManagedTests(await HostedPreviewStaging.Metadata(managedRuns[0], token));
        }
        await HostedPreviewStaging.VerifyWindowsEditorResult(Path.Combine(evidence, "windows-installed-editor/result.json"), token);
        string archive = Path.Combine(request.NativeCandidate, "diagnostics", "unqualified-windows-" + request.Commit + ".zip");
        await HostedPreviewStaging.VerifyFile(archive, binding.Bytes, binding.Sha256, token);
        // A dedicated exact-commit worktree keeps current development and its
        // private config out of the source archive without rewriting history.
        await LinuxPackage.RequireSourceAsync(request.Repository, request.Commit, token);
        var upstream = await UpstreamProvenance.RequireAsync(request.Repository, request.Commit, token);
        string parent = Path.GetDirectoryName(request.Output)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The staging parent must already exist.");
        string scratch = Directory.CreateDirectory(Path.Combine(parent, ".requalified-source-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string prefix = "kicad-codex-" + request.Commit + "-";
            string source = Path.Combine(scratch, prefix + "source.tar.gz");
            await Git(["archive", "--format=tar.gz", "--output", source, request.Commit]);
            await using var sourceStream = File.OpenRead(source);
            string sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(sourceStream, token));
            long sourceBytes = sourceStream.Length; await sourceStream.DisposeAsync();
            var incoming = new[]
            {
                new HostedPreviewStaging.PublicPreviewArtifact(new DownloadArtifact(prefix + "windows-x64.zip", "win-x64",
                    request.Version, request.Commit, sourceHash, binding.Bytes, binding.Sha256), archive),
                new HostedPreviewStaging.PublicPreviewArtifact(new DownloadArtifact(prefix + "source.tar.gz", "source",
                    request.Version, request.Commit, sourceHash, sourceBytes, sourceHash), source)
            };
            var staged = await HostedPreviewStaging.StagePublicAsync(request.Previous, request.Output, incoming, null, token);
            return new
            {
                Status = "staged_requalified", Directory = request.Output, SourceCommit = request.Commit, Upstream = upstream,
                OriginalBuildRunId = binding.RunId, request.QualificationRunId, binding.HarnessCommit,
                OriginalReceiptSha256 = Convert.ToHexStringLower(SHA256.HashData(originalBytes)),
                QualificationTrxSha256 = Convert.ToHexStringLower(SHA256.HashData(trxBytes)),
                ArchiveSha256 = binding.Sha256, SourceArchiveSha256 = sourceHash,
                ArtifactCount = staged.Count, CatalogueSha256 = staged.Hash, OriginalFailurePreserved = true, QualifyingDelivery = false
            };
        }
        finally { Directory.Delete(scratch, true); }

        async Task Git(string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = request.Repository, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            await stdout; string error = await stderr;
            if (process.ExitCode != 0) throw new IOException("Exact source export failed: " + error);
        }
    }

    internal sealed record Binding(string RunId, string HarnessCommit, long Bytes, string Sha256, bool RequiresManagedRecovery);
    internal static Binding ValidateProvenance(JsonElement original, JsonElement inputs, JsonElement result, string commit)
    {
        try
        {
            if (original.GetProperty("SchemaVersion").GetInt32() != 1 || original.GetProperty("Status").GetString() != "failed" || original.GetProperty("SourceCommit").GetString() != commit
                || original.GetProperty("Platform").GetString() != "windows" || original.GetProperty("Architecture").GetString() != "x64")
                throw new InvalidDataException("Wrong original native build identity.");
            var steps = original.GetProperty("Steps").EnumerateArray().ToArray();
            foreach (string name in new[] { "source-commit", "pinned-ancestry", "native-build", "native-tests", "native-install", "installed-native-commit", "managed-runtime" })
                if (steps.Count(step => step.GetProperty("Name").GetString() == name && step.GetProperty("ExitCode").GetInt32() == 0) != 1)
                    throw new InvalidDataException("Missing native prerequisite: " + name);
            var failures = steps.Where(step => step.GetProperty("ExitCode").GetInt32() != 0).ToArray();
            if (failures.Length != 1 || failures[0].GetProperty("Name").GetString() is not ("installed-editor-journey" or "managed-contracts"))
                throw new InvalidDataException("Only managed-contract or installed-editor qualification failures can use this path.");
            bool managedRecovery = failures[0].GetProperty("Name").GetString() == "managed-contracts";
            if (managedRecovery)
            {
                if (!result.GetProperty("managedContractsPassed").GetBoolean() || !result.GetProperty("managedRuntimeSourceUnchanged").GetBoolean())
                    throw new InvalidDataException("Managed failure recovery needs current tests over unchanged runtime source.");
            }
            else if (steps.Count(step => step.GetProperty("Name").GetString() == "managed-contracts" && step.GetProperty("ExitCode").GetInt32() == 0) != 1)
                throw new InvalidDataException("Missing successful managed-contract prerequisite.");
            var artifact = original.GetProperty("DiagnosticArtifacts").EnumerateArray().Single(item =>
                item.GetProperty("Path").GetString() == "unqualified-windows-" + commit + ".zip");
            string hash = artifact.GetProperty("Sha256").GetString()!;
            long bytes = artifact.GetProperty("Bytes").GetInt64();
            string run = original.GetProperty("RunId").GetString()!;
            if (string.IsNullOrEmpty(run) || !run.All(char.IsAsciiDigit) || bytes <= 0
                || hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower)) throw new InvalidDataException("Invalid original artifact identity.");
            if (inputs.GetProperty("sourceCommit").GetString() != commit || result.GetProperty("sourceCommit").GetString() != commit
                || inputs.GetProperty("archiveSha256").GetString() != hash || result.GetProperty("archiveSha256").GetString() != hash
                || inputs.GetProperty("archiveBytes").GetInt64() != bytes || inputs.GetProperty("originalRunId").GetString() != run
                || inputs.GetProperty("rebuiltNativeCode").GetBoolean() || result.GetProperty("rebuiltNativeCode").GetBoolean()
                || result.GetProperty("schemaVersion").GetInt32() != 1 || result.GetProperty("status").GetString() != "passed"
                || !result.GetProperty("exactRetainedPayload").GetBoolean() || !result.GetProperty("nativeEditorJourneyPassed").GetBoolean()
                || !result.GetProperty("originalReceiptUnchanged").GetBoolean())
                throw new InvalidDataException("Native rerun evidence does not bind the original exact payload.");
            string harness = inputs.GetProperty("harnessCommit").GetString()!; Evidence.RequireCommit(harness);
            return new(run, harness, bytes, hash, managedRecovery);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or JsonException or ArgumentException)
        { throw new InvalidDataException("Incomplete requalification provenance.", error); }
    }

    internal static void RequirePassingOuterTest(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var document = XDocument.Load(reader); XNamespace ns = document.Root!.Name.Namespace;
        var tests = document.Descendants(ns + "UnitTestResult").ToArray();
        var counters = document.Descendants(ns + "Counters").SingleOrDefault();
        if (tests.Length != 1 || (string?)tests[0].Attribute("testName") != "OperateTheExactRetainedNativePackageWithoutRebuildingIt"
            || (string?)tests[0].Attribute("outcome") != "Passed" || counters is null
            || (string?)counters.Attribute("total") != "1" || (string?)counters.Attribute("passed") != "1"
            || (string?)counters.Attribute("executed") != "1"
            || new[] { "failed", "error", "timeout", "aborted", "inconclusive", "notExecuted" }.Any(name => (string?)counters.Attribute(name) != "0"))
            throw new InvalidDataException("The complete retained-package test, including cleanup, did not pass.");
    }

    internal static void RequirePassingManagedTests(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var document = XDocument.Load(reader); XNamespace ns = document.Root!.Name.Namespace;
        var tests = document.Descendants(ns + "UnitTestResult").ToArray();
        var counters = document.Descendants(ns + "Counters").Single();
        if (tests.Length == 0 || tests.Any(t => (string?)t.Attribute("outcome") != "Passed")
            || (string?)counters.Attribute("total") != tests.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || (string?)counters.Attribute("passed") != tests.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || (string?)counters.Attribute("executed") != tests.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || new[] { "failed", "error", "timeout", "aborted", "inconclusive", "notExecuted" }
                .Any(name => (string?)counters.Attribute(name) != "0"))
            throw new InvalidDataException("Managed recovery requires complete passing tests, without skips or cleanup failures.");
        foreach (string required in new[] { "HostedDeliveryTests", "RuntimeInfoTests", "NngTransportTests", "NativeIpcEndpointTests" })
        {
            var ids = document.Descendants(ns + "UnitTest").Where(t => t.Descendants(ns + "TestMethod")
                .Any(m => ((string?)m.Attribute("className"))?.Split(',')[0].EndsWith("." + required, StringComparison.Ordinal) == true))
                .Select(t => (string?)t.Attribute("id")).ToHashSet();
            if (!tests.Any(t => ids.Contains((string?)t.Attribute("testId"))))
                throw new InvalidDataException("Managed recovery omitted required class " + required);
        }
    }
}
