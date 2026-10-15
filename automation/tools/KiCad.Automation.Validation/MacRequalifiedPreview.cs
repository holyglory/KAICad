using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public sealed record MacRequalifiedPreviewRequest(string NativeCandidate, string Qualification, string Repository,
    string Commit, string QualificationRunId, string Version, string Previous, string Output);

/// <summary>
/// Binds a fresh macOS ARM test-only receipt to an unchanged diagnostic package.
/// This can create a private preliminary catalogue, but never turns an unsigned
/// or previously failed package into qualifying delivery evidence.
/// </summary>
public static class MacRequalifiedPreview
{
    public static async Task<object> RunAsync(MacRequalifiedPreviewRequest request, CancellationToken token)
    {
        Evidence.RequireCommit(request.Commit);
        foreach (string directory in new[] { request.NativeCandidate, request.Qualification, request.Repository, request.Previous, request.Output })
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use absolute paths for Mac requalification inputs and output.");
        if (request.QualificationRunId.Length == 0 || !request.QualificationRunId.All(char.IsAsciiDigit)
            || !ulong.TryParse(request.QualificationRunId, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || request.Version.Length is < 1 or > 128 || !request.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("Use an explicit qualification run and a valid preview version.");
        if (Directory.Exists(request.Output) || File.Exists(request.Output)) throw new IOException("Preserve the existing output.");

        byte[] originalBytes = await HostedPreviewStaging.Metadata(Path.Combine(request.NativeCandidate, "receipt.json"), token);
        using JsonDocument original = JsonDocument.Parse(originalBytes);
        string archiveName = "unqualified-macos-" + request.Commit + ".tar.gz";
        JsonElement originalRoot = original.RootElement;
        ValidateOriginal(originalRoot, request.Commit, archiveName);
        string archive = Path.Combine(request.NativeCandidate, "diagnostics", archiveName);
        var diagnostic = originalRoot.GetProperty("DiagnosticArtifacts").EnumerateArray()
            .Single(item => item.GetProperty("Path").GetString() == archiveName);
        long archiveBytes = diagnostic.GetProperty("Bytes").GetInt64();
        string archiveSha = diagnostic.GetProperty("Sha256").GetString()!;
        await HostedPreviewStaging.VerifyFile(archive, archiveBytes, archiveSha, token);

        string macEvidence = Path.Combine(request.NativeCandidate, "mac", "evidence");
        RequirePassingTests(await HostedPreviewStaging.Metadata(Path.Combine(macEvidence, "native-tests.xml"), token),
            "qa_pcbnew_pns_detached_update");

        string qualificationEvidence = Path.Combine(request.Qualification, "mac-retained-package");
        using JsonDocument inputs = JsonDocument.Parse(await HostedPreviewStaging.Metadata(Path.Combine(qualificationEvidence, "inputs.json"), token));
        using JsonDocument result = JsonDocument.Parse(await HostedPreviewStaging.Metadata(Path.Combine(qualificationEvidence, "result.json"), token));
        string originalRunId = originalRoot.GetProperty("RunId").GetString()!;
        ValidateQualification(inputs.RootElement, result.RootElement, request.Commit, archiveSha, archiveBytes, originalRunId);
        string[] managedRuns = Directory.GetFiles(Path.Combine(qualificationEvidence, "managed-run"), "*.trx", SearchOption.TopDirectoryOnly);
        if (managedRuns.Length != 1) throw new InvalidDataException("Require one complete managed recovery receipt.");
        byte[] managedTrx = await HostedPreviewStaging.Metadata(managedRuns[0], token);
        RequirePassingTests(managedTrx, "CancellationRefusalReconcilesLiveTerminalReceipt");
        byte[] qualifiedOriginal = await HostedPreviewStaging.Metadata(Path.Combine(request.Qualification, "original-receipt.json"), token);
        if (!qualifiedOriginal.AsSpan().SequenceEqual(originalBytes))
            throw new InvalidDataException("The retained qualification changed the original build receipt.");

        string parent = Path.GetDirectoryName(request.Output)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The staging parent must already exist.");
        string scratch = Directory.CreateDirectory(Path.Combine(parent, ".requalified-mac-source-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(scratch, "kicad-codex-" + request.Commit + "-source.tar.gz");
            await RunGitArchive(request.Repository, request.Commit, source, token);
            long sourceBytes = new FileInfo(source).Length;
            string sourceHash;
            await using (var sourceStream = File.OpenRead(source))
                sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(sourceStream, token));
            string packageName = "kicad-codex-" + request.Commit + "-macos-arm64.tar.gz";
            var incoming = new[]
            {
                new HostedPreviewStaging.PublicPreviewArtifact(
                    new DownloadArtifact(packageName, "osx-arm64", request.Version, request.Commit,
                        sourceHash, archiveBytes, archiveSha), archive),
                new HostedPreviewStaging.PublicPreviewArtifact(
                    new DownloadArtifact(Path.GetFileName(source), "source", request.Version, request.Commit,
                        sourceHash, sourceBytes, sourceHash), source)
            };
            var staged = await HostedPreviewStaging.StagePublicAsync(request.Previous, request.Output, incoming, null, token);
            return new
            {
                Status = "staged_requalified_preliminary", Directory = request.Output,
                SourceCommit = request.Commit, Platform = "osx-arm64", QualificationRunId = request.QualificationRunId,
                OriginalReceiptSha256 = Convert.ToHexStringLower(SHA256.HashData(originalBytes)),
                QualificationTrxSha256 = Convert.ToHexStringLower(SHA256.HashData(managedTrx)),
                ArchiveSha256 = archiveSha, SourceArchiveSha256 = sourceHash,
                ArtifactCount = staged.Count, CatalogueSha256 = staged.Hash,
                OriginalFailurePreserved = true, RebuiltNativeCode = false,
                NativeEditorJourneyAvailable = false, CrashUndoClosed = false,
                QualifyingDelivery = false
            };
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal static void ValidateOriginal(JsonElement original, string commit, string archiveName)
    {
        if (original.GetProperty("SchemaVersion").GetInt32() != 1
            || original.GetProperty("Status").GetString() != "failed"
            || original.GetProperty("SourceCommit").GetString() != commit
            || original.GetProperty("Platform").GetString() != "macos"
            || original.GetProperty("Architecture").GetString() != "arm64")
            throw new InvalidDataException("Wrong original Mac ARM build identity.");
        foreach (string name in new[] { "source-commit", "pinned-ancestry", "official-dependencies" })
            if (original.GetProperty("Steps").EnumerateArray().Count(step =>
                    step.GetProperty("Name").GetString() == name && step.GetProperty("ExitCode").GetInt32() == 0) != 1)
                throw new InvalidDataException("Missing successful original prerequisite: " + name);
        var artifact = original.GetProperty("DiagnosticArtifacts").EnumerateArray()
            .SingleOrDefault(item => item.GetProperty("Path").GetString() == archiveName);
        if (artifact.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || artifact.GetProperty("Bytes").GetInt64() <= 0
            || !IsSha256(artifact.GetProperty("Sha256").GetString() ?? ""))
            throw new InvalidDataException("Missing or invalid original Mac diagnostic archive.");
    }

    internal static void ValidateQualification(JsonElement inputs, JsonElement result, string commit,
        string archiveSha, long archiveBytes, string originalRunId)
    {
        if (inputs.GetProperty("schemaVersion").GetInt32() != 1
            || inputs.GetProperty("sourceCommit").GetString() != commit
            || inputs.GetProperty("archiveSha256").GetString() != archiveSha
            || inputs.GetProperty("archiveBytes").GetInt64() != archiveBytes
            || inputs.GetProperty("originalRunId").GetString() != originalRunId
            || inputs.GetProperty("rebuiltNativeCode").GetBoolean())
            throw new InvalidDataException("Retained Mac qualification inputs do not bind the original archive.");
        if (result.GetProperty("schemaVersion").GetInt32() != 1
            || result.GetProperty("status").GetString() != "passed"
            || result.GetProperty("sourceCommit").GetString() != commit
            || result.GetProperty("archiveSha256").GetString() != archiveSha
            || !result.GetProperty("exactRetainedPayload").GetBoolean()
            || !result.GetProperty("currentManagedRecoveryPassed").GetBoolean()
            || result.GetProperty("rebuiltNativeCode").GetBoolean()
            || !result.GetProperty("originalReceiptUnchanged").GetBoolean()
            || result.GetProperty("nativeEditorJourneyAvailable").GetBoolean()
            || result.GetProperty("crashUndoClosed").GetBoolean())
            throw new InvalidDataException("Retained Mac qualification result overclaims package or crash coverage.");
    }

    internal static void RequirePassingTests(byte[] bytes, string? requiredName = null)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        XDocument document = XDocument.Load(reader);
        XNamespace ns = document.Root?.Name.Namespace ?? throw new InvalidDataException("A test receipt has no root.");
        var tests = document.Descendants(ns + "UnitTestResult").ToArray();
        var counters = document.Descendants(ns + "Counters").SingleOrDefault()
            ?? throw new InvalidDataException("A test receipt has no counters.");
        if (tests.Length == 0 || tests.Any(test => (string?)test.Attribute("outcome") != "Passed")
            || (string?)counters.Attribute("total") != tests.Length.ToString(CultureInfo.InvariantCulture)
            || (string?)counters.Attribute("passed") != tests.Length.ToString(CultureInfo.InvariantCulture)
            || (string?)counters.Attribute("executed") != tests.Length.ToString(CultureInfo.InvariantCulture)
            || new[] { "failed", "error", "timeout", "aborted", "inconclusive", "notExecuted" }
                .Any(name => (string?)counters.Attribute(name) != "0"))
            throw new InvalidDataException("The retained Mac test receipt is incomplete or failed.");
        if (requiredName is not null && !tests.Any(test => ((string?)test.Attribute("testName"))?.EndsWith(requiredName, StringComparison.Ordinal) == true))
            throw new InvalidDataException("The retained Mac test receipt omitted " + requiredName + ".");
    }

    private static async Task RunGitArchive(string repository, string commit, string destination, CancellationToken token)
    {
        await RunProcess("git", repository, ["archive", "--format=tar.gz", "--prefix=kicad-source/", "--output=" + destination, commit], token);
    }

    private static async Task RunProcess(string executable, string workingDirectory, string[] arguments, CancellationToken token)
    {
        var start = new System.Diagnostics.ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not start " + executable);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token), stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string error = await stderr;
        if (process.ExitCode != 0) throw new IOException(executable + " exited " + process.ExitCode + ": " + error);
        _ = await stdout;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
}
