using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Durable MCP-side evidence for one typed PCB manufacturing export. This is
/// recovery state, not a replacement for KiCad's native receipt: a cached
/// receipt is always labelled as such by the caller before it is exposed.
/// </summary>
internal sealed record PcbExportRecoveryRecord(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string InstanceId,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string ProcessEpoch,
    [property: JsonRequired] string DocumentJson,
    [property: JsonRequired] string RequestFingerprintSha256,
    [property: JsonRequired] string OutputDirectory,
    [property: JsonRequired] string StagingDirectory,
    [property: JsonRequired] string Status,
    [property: JsonRequired] bool NativeDispatchKnown,
    [property: JsonRequired] bool NativeDispatch,
    [property: JsonRequired] bool PublicationMayHaveHappened,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReceiptJson,
    [property: JsonRequired] DateTimeOffset UpdatedAtUtc)
{
    internal void Validate()
    {
        if (Version != 1 || !CanonicalGuid(InstanceId) || !CanonicalGuid(OperationId)
            || !CanonicalGuid(ProcessEpoch) || !Digest(RequestFingerprintSha256)
            || !PathIsCanonical(OutputDirectory)
            || !PathIsCanonical(StagingDirectory)
            || !string.Equals(StagingDirectory,
                PcbExportRecoveryPolicy.StagingDirectory(OutputDirectory, OperationId),
                StringComparison.Ordinal)
            || !PcbExportRecoveryPolicy.Statuses.Contains(Status)
            || UpdatedAtUtc == DateTimeOffset.MinValue || UpdatedAtUtc.Offset != TimeSpan.Zero)
            throw Invalid("The durable PCB export recovery identity or state is invalid.");

        try
        {
            var document = SchematicJson.Parser.Parse<Kiapi.Common.Types.DocumentSpecifier>(DocumentJson);
            if (document.Type != Kiapi.Common.Types.DocumentType.DoctypePcb)
                throw Invalid("The durable PCB export recovery document is not a PCB.");
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw Invalid("The durable PCB export recovery document is invalid: " + error.Message); }

        if (ReceiptJson is null) return;
        try
        {
            var receipt = SchematicJson.Parser.Parse<PcbManufacturingExportReceipt>(ReceiptJson);
            if (!string.Equals(receipt.OperationId, OperationId, StringComparison.Ordinal)
                || !string.Equals(receipt.ProcessEpoch, ProcessEpoch, StringComparison.Ordinal)
                || !string.Equals(receipt.RequestFingerprintSha256, RequestFingerprintSha256,
                    StringComparison.Ordinal))
                throw Invalid("The durable PCB export receipt does not match its operation, process epoch or request fingerprint.");
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw Invalid("The durable PCB export receipt is invalid: " + error.Message); }
    }

    private static bool CanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");

    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static bool PathIsCanonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try { return string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    internal static AutomationException Invalid(string message) => new("invalid_pcb_export_recovery", message);
}

internal sealed record PcbExportRecoveryDecision(
    string Status,
    bool RetryAllowed,
    bool PublicationMayHaveHappened,
    bool OutputExists,
    bool StagingExists,
    bool NativeReceiptFound,
    string Reason);

internal static class PcbExportRecoveryPolicy
{
    internal const string RecoveryUnavailable = "pcb_export_recovery_unavailable";
    internal const string Pending = "pending";
    internal const string Completed = "completed";
    internal const string Indeterminate = "indeterminate";
    internal const string Retryable = "retryable";
    internal const string Cancelled = "cancelled";

    // These are the stable agent-facing outcome names.  The native receipt
    // status remains the authoritative KiCad enum; this small vocabulary lets
    // callers distinguish a request that was handed to KiCad from one whose
    // result is still unknown without interpreting transport details.
    internal const string Submitted = "submitted";
    internal const string Unknown = "unknown";
    internal const string Failed = "failed";
    internal const string OutcomeCompleted = "completed";

    internal static IReadOnlySet<string> Statuses { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Pending, Completed, Indeterminate, Retryable, Cancelled };

    internal static string Outcome(PcbManufacturingExportReceipt? receipt,
        bool nativeDispatch, bool cancellationRequested = false)
    {
        if (receipt is not null)
        {
            return receipt.Status switch
            {
                PcbManufacturingExportStatus.PcbMfgExportCompleted => OutcomeCompleted,
                PcbManufacturingExportStatus.PcbMfgExportFailed => Failed,
                PcbManufacturingExportStatus.PcbMfgExportIndeterminate => Unknown,
                PcbManufacturingExportStatus.PcbMfgExportAdmitted
                    or PcbManufacturingExportStatus.PcbMfgExportRunning => Submitted,
                // Native uses UNSUBMITTED for admission failures as well as
                // a caller cancellation.  Without an explicit cancellation
                // marker it is a failed admission, never a successful cancel.
                PcbManufacturingExportStatus.PcbMfgExportUnsubmitted => Failed,
                _ => Unknown
            };
        }

        if (cancellationRequested && !nativeDispatch)
            return Cancelled;

        return nativeDispatch ? Unknown : Submitted;
    }

    /// <summary>
    /// Maps durable MCP state to the same bounded outcome vocabulary used by
    /// live native receipts. A missing native receipt never becomes a success
    /// claim; callers expose this as MCP state with no redispatch permission
    /// unless the durable record proves a pre-dispatch retryable result.
    /// </summary>
    internal static string DurableOutcome(PcbExportRecoveryRecord record,
        PcbManufacturingExportReceipt? receipt)
    {
        if (receipt is not null)
            return record.Status == Completed ? OutcomeCompleted : Outcome(receipt, receipt.NativeDispatch);
        return record.Status switch
        {
            Cancelled => Cancelled,
            Retryable => Failed,
            Completed => Unknown,
            _ => Unknown
        };
    }

    internal static string StagingDirectory(string outputDirectory, string operationId)
    {
        string output = Path.GetFullPath(outputDirectory);
        string? parent = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(parent))
            throw new AutomationException("invalid_pcb_export_path", "The manufacturing output directory has no parent directory.");
        return Path.Combine(parent, Path.GetFileName(output) + ".kicad-staging-" + operationId);
    }

    internal static string Fingerprint(string operationId, string processEpoch, string documentJson,
        string outputDirectory, IMessage request)
    {
        // KiCad's native receipt uses this exact protobuf payload digest.
        // The instance, document and output paths are retained separately in
        // the durable record and are checked before a replay.
        _ = operationId; _ = processEpoch; _ = documentJson; _ = outputDirectory;
        return Convert.ToHexStringLower(SHA256.HashData(request.ToByteArray()));
    }

    internal static PcbExportRecoveryDecision Reconcile(PcbExportRecoveryRecord? prior,
        bool nativeReceiptFound, PcbManufacturingExportReceipt? receipt,
        bool outputExists, bool outputVerified, bool stagingExists)
    {
        if (prior is null)
            return new("untracked", false, outputExists || stagingExists, outputExists, stagingExists,
                nativeReceiptFound, "No durable export operation record exists for this operation ID.");

        if (receipt is not null)
        {
            if (receipt.OperationId != prior.OperationId
                || receipt.ProcessEpoch != prior.ProcessEpoch
                || receipt.RequestFingerprintSha256 != prior.RequestFingerprintSha256)
                return new(Indeterminate, false, true, outputExists, stagingExists, nativeReceiptFound,
                    "The native receipt belongs to a different export operation identity; retry is not authorized.");

            bool completed = receipt.Status == PcbManufacturingExportStatus.PcbMfgExportCompleted
                && receipt.Terminal
                && receipt.PublicationState == PcbManufacturingPublicationState.PcbMfgPublicationPublished
                && receipt.OutputManifestComplete && receipt.Artifacts.Count != 0
                && receipt.OperationId == prior.OperationId
                && receipt.ProcessEpoch == prior.ProcessEpoch
                && receipt.RequestFingerprintSha256 == prior.RequestFingerprintSha256;
            if (completed && outputExists && outputVerified)
                return new(Completed, false, true, true, stagingExists, true,
                    "The native receipt and verified published output agree.");

            bool retryableFailure = receipt.Status is PcbManufacturingExportStatus.PcbMfgExportFailed
                or PcbManufacturingExportStatus.PcbMfgExportUnsubmitted;
            if (retryableFailure && receipt.Artifacts.Count == 0 && !outputExists && !stagingExists)
                return new(Retryable, true, false, false, false, true,
                    "The native receipt is terminal and no owned output or staging path exists.");
        }

        if (prior.Status == Completed && outputExists && outputVerified)
            return new(Completed, false, true, true, stagingExists, nativeReceiptFound,
                "The durable completed receipt still matches the verified published output.");

        if (prior.Status == Cancelled && !outputExists && !stagingExists)
            return new(Cancelled, true, false, false, false, nativeReceiptFound,
                "The export was cancelled before native dispatch and left no owned output or staging path.");

        if (outputExists || stagingExists)
            return new(Indeterminate, false, true, outputExists, stagingExists, nativeReceiptFound,
                "An owned output or staging path exists, so publication cannot be ruled out.");

        if (prior.Status == Retryable)
            return new(Retryable, true, false, false, false, nativeReceiptFound,
                "The previous terminal result left no owned output or staging path.");

        if (prior.NativeDispatchKnown && !prior.NativeDispatch && prior.Status != Pending)
            return new(Retryable, true, false, false, false, nativeReceiptFound,
                "The durable record proves that native dispatch did not occur.");

        return new(Indeterminate, false, prior.PublicationMayHaveHappened || prior.NativeDispatch,
            false, false, nativeReceiptFound,
            "The native outcome is not proven; inspect the native process or submit a new operation after review.");
    }

    internal static bool OutputMatches(PcbManufacturingExportReceipt receipt)
    {
        if (receipt.Artifacts.Count == 0 || !receipt.OutputManifestComplete) return false;
        foreach (var artifact in receipt.Artifacts)
        {
            try
            {
                if (!File.Exists(artifact.Path)
                    || (File.GetAttributes(artifact.Path) & FileAttributes.ReparsePoint) != 0)
                    return false;
                var info = new FileInfo(artifact.Path);
                if (info.Length != checked((long)artifact.Bytes)) return false;
                using var stream = File.OpenRead(artifact.Path);
                string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (!string.Equals(hash, artifact.Sha256, StringComparison.Ordinal)) return false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or OverflowException)
            { return false; }
        }
        return true;
    }

    internal static void ValidateReceiptBinding(PcbExportRecoveryRecord record,
        string instanceId, string processEpoch, string documentJson)
    {
        if (record.InstanceId != instanceId || record.ProcessEpoch != processEpoch
            || record.DocumentJson != documentJson)
            throw new AutomationException("export_recovery_identity_mismatch",
                "The durable export record belongs to another instance, process epoch or PCB document.");
    }

    internal static void ValidateReceiptPaths(PcbExportRecoveryRecord record,
        PcbManufacturingExportReceipt receipt)
    {
        if (!string.IsNullOrWhiteSpace(receipt.OutputDirectory)
            && !PathEqualsCanonical(record.OutputDirectory, receipt.OutputDirectory))
            throw new AutomationException("export_recovery_identity_mismatch",
                "The native manufacturing receipt output directory does not match the durable export operation.");
        if (!string.IsNullOrWhiteSpace(receipt.StagingDirectory)
            && !PathEqualsCanonical(record.StagingDirectory, receipt.StagingDirectory))
            throw new AutomationException("export_recovery_identity_mismatch",
                "The native manufacturing receipt staging directory does not match the durable export operation.");
        if (receipt.Status == PcbManufacturingExportStatus.PcbMfgExportCompleted
            && (string.IsNullOrWhiteSpace(receipt.OutputDirectory)
                || !PathEqualsCanonical(record.OutputDirectory, receipt.OutputDirectory)))
            throw new AutomationException("export_recovery_identity_mismatch",
                "A completed native manufacturing receipt must identify the durable published output directory.");
    }

    private static bool PathEqualsCanonical(string expected, string actual)
    {
        if (!Path.IsPathFullyQualified(actual)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(expected), Path.GetFullPath(actual),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Atomic, process-reconnectable MCP state for typed manufacturing exports.</summary>
internal sealed class PcbExportRecoveryStore(string stateDirectory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };
    private readonly string directory = Path.Combine(Path.GetFullPath(stateDirectory),
        "pcb-manufacturing-export-recovery");

    internal PcbExportRecoveryRecord? Read(string operationId)
    {
        ValidateOperationId(operationId);
        string path = PathFor(operationId);
        try
        {
            RequireDirectory(false);
            RequireRegular(path);
            byte[] bytes = File.ReadAllBytes(path);
            using var parsed = JsonDocument.Parse(bytes);
            var names = parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw PcbExportRecoveryRecord.Invalid("Duplicate properties are not valid recovery state.");
            var result = JsonSerializer.Deserialize<PcbExportRecoveryRecord>(bytes, Json)
                ?? throw PcbExportRecoveryRecord.Invalid("The durable recovery record is empty.");
            result.Validate();
            if (result.OperationId != operationId)
                throw PcbExportRecoveryRecord.Invalid("The recovery filename and operation ID differ.");
            return result;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw PcbExportRecoveryRecord.Invalid(error.Message); }
        catch (IOException error) when (error is not FileNotFoundException)
        { throw new AutomationException("export_recovery_unavailable", "The durable export recovery record could not be read: " + error.Message); }
    }

    internal PcbExportRecoveryRecord Begin(string instanceId, string operationId, string processEpoch,
        string documentJson, string requestFingerprintSha256, string outputDirectory)
    {
        ValidateIdentity(instanceId, operationId, processEpoch, documentJson, requestFingerprintSha256, outputDirectory);
        Directory.CreateDirectory(directory);
        using var lease = Acquire(operationId);
        PcbExportRecoveryRecord? prior = Read(operationId);
        string output = Path.GetFullPath(outputDirectory);
        string staging = PcbExportRecoveryPolicy.StagingDirectory(output, operationId);
        if (prior is not null)
        {
            if (!SameBinding(prior, instanceId, operationId, processEpoch, documentJson,
                    requestFingerprintSha256, output, staging))
                throw new AutomationException("export_operation_reused",
                    "The operation ID is already bound to different PCB export arguments.");
            if (prior.Status == PcbExportRecoveryPolicy.Indeterminate
                || prior.Status == PcbExportRecoveryPolicy.Pending)
                throw new AutomationException("export_status_required",
                    "The prior export outcome is not confirmed; call kicad_pcb_export_reconcile before retrying.");
            // A terminal native receipt is replayable by the native process. Keep
            // its durable record unchanged until that exact replay returns.
            if (prior.ReceiptJson is not null) return prior;
            if (prior.Status is not (PcbExportRecoveryPolicy.Retryable or PcbExportRecoveryPolicy.Cancelled))
                throw new AutomationException("export_recovery_invalid",
                    "The durable export record cannot be submitted again.");
        }

        var current = new PcbExportRecoveryRecord(1, instanceId, operationId, processEpoch, documentJson,
            requestFingerprintSha256, output, staging, PcbExportRecoveryPolicy.Pending,
            false, false, false, null, DateTimeOffset.UtcNow);
        Write(current, prior);
        return current;
    }

    internal PcbExportRecoveryRecord MarkReceipt(string operationId, PcbManufacturingExportReceipt receipt,
        bool outputExists, bool outputVerified, bool stagingExists)
    {
        return Update(operationId, prior =>
        {
            // A native receipt is replayable only for the exact operation-owned
            // paths that were admitted with this durable request.  Operation,
            // epoch and request-fingerprint checks alone do not protect the
            // publication boundary when a malformed or stale peer reports a
            // different output location.
            PcbExportRecoveryPolicy.ValidateReceiptPaths(prior, receipt);
            var decision = PcbExportRecoveryPolicy.Reconcile(prior, true, receipt, outputExists,
                outputVerified, stagingExists);
            return prior with
            {
                Status = decision.Status,
                NativeDispatchKnown = true,
                NativeDispatch = receipt.NativeDispatch,
                PublicationMayHaveHappened = decision.PublicationMayHaveHappened,
                ReceiptJson = SchematicJson.Formatter.Format(receipt)
            };
        });
    }

    internal PcbExportRecoveryRecord MarkUnknown(string operationId, bool? nativeDispatch)
    {
        return Update(operationId, prior =>
        {
            // A replay of an already terminal native receipt cannot make that
            // earlier outcome unknown. The second caller only lost its reply.
            if (prior.ReceiptJson is not null) return prior;
            bool outputExists = Directory.Exists(prior.OutputDirectory);
            bool stagingExists = Directory.Exists(prior.StagingDirectory);
            bool known = nativeDispatch.HasValue;
            bool dispatched = nativeDispatch == true;
            string status = !outputExists && !stagingExists && nativeDispatch == false
                ? PcbExportRecoveryPolicy.Retryable : PcbExportRecoveryPolicy.Indeterminate;
            return prior with
            {
                Status = status,
                NativeDispatchKnown = known,
                NativeDispatch = dispatched,
                PublicationMayHaveHappened = outputExists || stagingExists || dispatched
            };
        });
    }

    internal PcbExportRecoveryRecord MarkCancelled(string operationId)
    {
        return Update(operationId, prior =>
        {
            // A cancellation is only recorded by the MCP before invoking NNG.
            // If a receipt or owned path already exists, preserving that
            // evidence is safer than relabelling a dispatched operation.
            if (prior.ReceiptJson is not null || Directory.Exists(prior.OutputDirectory)
                || Directory.Exists(prior.StagingDirectory))
                return prior;

            return prior with
            {
                Status = PcbExportRecoveryPolicy.Cancelled,
                NativeDispatchKnown = true,
                NativeDispatch = false,
                PublicationMayHaveHappened = false
            };
        });
    }

    internal PcbExportRecoveryRecord ApplyDecision(string operationId, PcbExportRecoveryDecision decision)
    {
        return Update(operationId, prior => prior with
        {
            Status = decision.Status,
            PublicationMayHaveHappened = decision.PublicationMayHaveHappened
        });
    }

    internal static PcbManufacturingExportReceipt ParseReceipt(PcbExportRecoveryRecord record)
    {
        if (record.ReceiptJson is null)
            throw new AutomationException("export_recovery_missing_receipt", "The durable export record has no cached native receipt.");
        try
        {
            var receipt = SchematicJson.Parser.Parse<PcbManufacturingExportReceipt>(record.ReceiptJson);
            PcbManufacturingContract.ValidateReceipt(receipt,
                SchematicJson.Parser.Parse<Kiapi.Common.Types.DocumentSpecifier>(record.DocumentJson), null,
                record.OperationId, record.ProcessEpoch, record.RequestFingerprintSha256);
            return receipt;
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or AutomationException)
        { throw new AutomationException("invalid_pcb_export_recovery", "The cached native receipt is invalid: " + error.Message); }
    }

    private PcbExportRecoveryRecord Update(string operationId,
        Func<PcbExportRecoveryRecord, PcbExportRecoveryRecord> update)
    {
        Directory.CreateDirectory(directory);
        using var lease = Acquire(operationId);
        PcbExportRecoveryRecord prior = Read(operationId)
            ?? throw new AutomationException("export_recovery_missing", "The durable export recovery record is missing.");
        PcbExportRecoveryRecord current = update(prior) with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        Write(current, prior);
        return current;
    }

    private void Write(PcbExportRecoveryRecord record, PcbExportRecoveryRecord? previous)
    {
        record.Validate();
        Directory.CreateDirectory(directory);
        string path = PathFor(record.OperationId);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(JsonSerializer.SerializeToUtf8Bytes(record, Json));
                output.Flush(flushToDisk: true);
            }
            if (previous is null) File.Move(temporary, path, overwrite: false);
            else File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream Acquire(string operationId)
    {
        string path = PathFor(operationId) + ".lock";
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        { throw new AutomationException("export_recovery_busy", "The export recovery record is busy: " + error.Message); }
    }

    private string PathFor(string operationId) => Path.Combine(directory, operationId + ".json");

    private static bool SameBinding(PcbExportRecoveryRecord prior, string instanceId, string operationId,
        string processEpoch, string documentJson, string fingerprint, string output, string staging) =>
        prior.InstanceId == instanceId && prior.OperationId == operationId && prior.ProcessEpoch == processEpoch
        && prior.DocumentJson == documentJson && prior.RequestFingerprintSha256 == fingerprint
        && prior.OutputDirectory == output && prior.StagingDirectory == staging;

    private static void ValidateIdentity(string instanceId, string operationId, string processEpoch,
        string documentJson, string fingerprint, string outputDirectory)
    {
        if (!Guid.TryParseExact(instanceId, "D", out var instance) || instance == Guid.Empty
            || instanceId != instance.ToString("D"))
            throw new AutomationException("invalid_instance_id", "Provide a nonempty canonical instance UUID.");
        if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty
            || operationId != operation.ToString("D"))
            throw new AutomationException("invalid_operation_id", "Provide a nonempty canonical operation UUID.");
        if (!Guid.TryParseExact(processEpoch, "D", out var epoch) || epoch == Guid.Empty
            || processEpoch != epoch.ToString("D"))
            throw new AutomationException("invalid_process_epoch", "Provide the canonical native process epoch.");
        if (string.IsNullOrWhiteSpace(documentJson) || fingerprint.Length != 64
            || !fingerprint.All(char.IsAsciiHexDigitLower) || !Path.IsPathFullyQualified(outputDirectory))
            throw new AutomationException("invalid_pcb_export_recovery", "The durable export identity is incomplete.");
    }

    private void RequireDirectory(bool create)
    {
        if (create) Directory.CreateDirectory(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw PcbExportRecoveryRecord.Invalid("Recovery state must use an ordinary directory.");
    }

    private static void RequireRegular(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw PcbExportRecoveryRecord.Invalid("Recovery state must use an ordinary file.");
    }

    private static void ValidateOperationId(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty || operationId != id.ToString("D"))
            throw new AutomationException("invalid_operation_id", "Provide a nonempty canonical operation UUID.");
    }
}
