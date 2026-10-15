using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// The MCP-side part of a PCB DRC operation receipt.  KiCad owns the actual
/// checker and its job state; this journal only preserves the exact admission
/// identity and the last state that the MCP verified.  In particular, an
/// Unknown record never authorizes another native dispatch.
/// </summary>
internal enum PcbDrcReceiptOutcome
{
    Prepared,
    Unknown,
    Retryable,
    Completed,
    Stale
}

internal sealed record PcbDrcReceiptRecord(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("jobId")] string? JobId,
    [property: JsonPropertyName("processEpoch")] string ProcessEpoch,
    [property: JsonPropertyName("documentJson")] string DocumentJson,
    [property: JsonPropertyName("revisionJson")] string RevisionJson,
    [property: JsonPropertyName("candidateJson")] string CandidateJson,
    [property: JsonPropertyName("deleteJson")] string DeleteJson,
    [property: JsonPropertyName("groupChangesJson")] string GroupChangesJson,
    [property: JsonPropertyName("requestFingerprintSha256")] string RequestFingerprintSha256,
    [property: JsonPropertyName("candidateHash")] string CandidateHash,
    [property: JsonPropertyName("outcome")] PcbDrcReceiptOutcome Outcome,
    [property: JsonPropertyName("nativeDispatchKnown")] bool NativeDispatchKnown,
    [property: JsonPropertyName("nativeDispatch")] bool NativeDispatch,
    [property: JsonPropertyName("resultJson")] string? ResultJson,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc)
{
    internal const int CurrentVersion = 1;
    internal const int MaximumReceiptBytes = 16 * 1024 * 1024;
    internal const int MaximumPayloadCharacters = 8 * 1024 * 1024;

    internal void Validate()
    {
        if (Version != CurrentVersion || !Canonical(InstanceId) || !Canonical(OperationId)
            || !Canonical(ProcessEpoch) || !Digest(RequestFingerprintSha256)
            || CandidateJson.Length > MaximumPayloadCharacters || DeleteJson.Length > MaximumPayloadCharacters
            || GroupChangesJson.Length > MaximumPayloadCharacters || DocumentJson.Length == 0
            || RevisionJson.Length == 0 || DocumentJson.Length > MaximumPayloadCharacters
            || RevisionJson.Length > MaximumPayloadCharacters || !Enum.IsDefined(Outcome))
            throw Invalid("The PCB DRC receipt identity or payload bounds are invalid.");

        if (JobId is not null && !Canonical(JobId))
            throw Invalid("The PCB DRC receipt job identity is invalid.");
        if (CandidateJson.Length == 0 ? CandidateHash.Length != 0 : !Digest(CandidateHash))
            throw Invalid("The PCB DRC candidate hash does not match its presence.");
        if (ResultJson is { Length: > MaximumPayloadCharacters })
            throw Invalid("The PCB DRC result is too large.");
        if (ResultJson is not null)
        {
            try
            {
                using var parsed = JsonDocument.Parse(ResultJson);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    throw Invalid("The PCB DRC result must be a JSON object.");
                ValidateProperties(parsed.RootElement);
            }
            catch (JsonException error) { throw Invalid("The PCB DRC result is not valid JSON: " + error.Message); }
        }

        switch (Outcome)
        {
            case PcbDrcReceiptOutcome.Prepared:
                if (ResultJson is not null || ErrorCode is not null || ErrorMessage is not null
                    || NativeDispatchKnown || NativeDispatch)
                    throw Invalid("A prepared PCB DRC receipt cannot contain a terminal result or dispatch evidence.");
                break;
            case PcbDrcReceiptOutcome.Unknown:
                if (ResultJson is not null || string.IsNullOrWhiteSpace(ErrorCode)
                    || string.IsNullOrWhiteSpace(ErrorMessage) || (NativeDispatchKnown && !NativeDispatch))
                    throw Invalid("An unknown PCB DRC receipt requires an error and must not prove native non-dispatch.");
                break;
            case PcbDrcReceiptOutcome.Retryable:
                if (ResultJson is not null || string.IsNullOrWhiteSpace(ErrorCode)
                    || string.IsNullOrWhiteSpace(ErrorMessage) || !NativeDispatchKnown || NativeDispatch)
                    throw Invalid("A retryable PCB DRC receipt must prove that native dispatch did not occur.");
                break;
            case PcbDrcReceiptOutcome.Completed:
                if (ResultJson is null || ErrorCode is not null || ErrorMessage is not null
                    || !NativeDispatchKnown || !NativeDispatch || JobId is null)
                    throw Invalid("A completed PCB DRC receipt requires native dispatch, a job and a result.");
                break;
            case PcbDrcReceiptOutcome.Stale:
                if (ResultJson is null || string.IsNullOrWhiteSpace(ErrorCode)
                    || string.IsNullOrWhiteSpace(ErrorMessage) || !NativeDispatchKnown || !NativeDispatch
                    || JobId is null)
                    throw Invalid("A stale PCB DRC receipt requires the dispatched job, result and reason.");
                break;
        }
    }

    internal static AutomationException Invalid(string message) =>
        new("drc_receipt_corrupt", message);

    internal static bool Canonical(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && value == id.ToString("D");

    internal static bool Digest(string? value) => value is not null && value.Length == 64
        && value.All(char.IsAsciiHexDigitLower);

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Duplicate PCB DRC receipt properties.");
                ValidateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray()) ValidateProperties(value);
        }
    }
}

internal sealed record PcbDrcReceiptReservation(PcbDrcReceiptRecord Receipt, bool Created);

/// <summary>Atomic, reconnectable MCP state for PCB DRC operation admissions.</summary>
internal sealed class PcbDrcReceiptStore(string stateDirectory)
{
    private readonly string directory = Path.Combine(Path.GetFullPath(stateDirectory), "pcb-drc-receipts");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal FileStream Acquire(string instanceId, string operationId)
    {
        ValidateKey(instanceId, operationId);
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(LockPath(instanceId, operationId), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) { throw new AutomationException("drc_receipt_busy", error.Message); }
        catch (UnauthorizedAccessException error) { throw new AutomationException("drc_receipt_busy", error.Message); }
    }

    internal PcbDrcReceiptRecord? Read(string instanceId, string operationId)
    {
        ValidateKey(instanceId, operationId);
        string path = ReceiptPath(instanceId, operationId);
        if (!File.Exists(path)) return null;
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw PcbDrcReceiptRecord.Invalid("PCB DRC receipts require ordinary files.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > PcbDrcReceiptRecord.MaximumReceiptBytes)
                throw PcbDrcReceiptRecord.Invalid("The PCB DRC receipt is empty or too large.");
            using var parsed = JsonDocument.Parse(bytes);
            ValidateProperties(parsed.RootElement);
            var result = JsonSerializer.Deserialize<PcbDrcReceiptRecord>(bytes, Json)
                ?? throw PcbDrcReceiptRecord.Invalid("The PCB DRC receipt is empty.");
            result.Validate();
            if (result.InstanceId != instanceId || result.OperationId != operationId)
                throw PcbDrcReceiptRecord.Invalid("The PCB DRC receipt filename and identity differ.");
            return result;
        }
        catch (JsonException error) { throw PcbDrcReceiptRecord.Invalid(error.Message); }
        catch (IOException error) { throw new AutomationException("drc_receipt_unavailable", error.Message); }
        catch (UnauthorizedAccessException error) { throw new AutomationException("drc_receipt_unavailable", error.Message); }
    }

    internal PcbDrcReceiptRecord? ReadByJob(string instanceId, string processEpoch, string jobId)
    {
        if (!PcbDrcReceiptRecord.Canonical(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId must be a canonical UUID.");
        if (!PcbDrcReceiptRecord.Canonical(processEpoch) || !PcbDrcReceiptRecord.Canonical(jobId))
            throw new AutomationException("invalid_drc_receipt", "The PCB DRC process epoch and job ID must be canonical UUIDs.");
        if (!Directory.Exists(directory)) return null;
        PcbDrcReceiptRecord? match = null;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            string operationId = Path.GetFileNameWithoutExtension(path).Split('-', 2).LastOrDefault() ?? "";
            if (!PcbDrcReceiptRecord.Canonical(operationId)) continue;
            var record = Read(instanceId, operationId);
            if (record?.ProcessEpoch == processEpoch && record.JobId == jobId)
            {
                if (match is not null && match.OperationId != record.OperationId)
                    throw PcbDrcReceiptRecord.Invalid("More than one PCB DRC receipt names the same job.");
                match = record;
            }
        }
        return match;
    }

    internal PcbDrcReceiptReservation Reserve(string instanceId, string operationId,
        string processEpoch, string documentJson, string revisionJson, string candidateJson,
        string deleteJson, string groupChangesJson, string requestFingerprintSha256, string candidateHash)
    {
        using var lease = Acquire(instanceId, operationId);
        return ReserveHeld(instanceId, operationId, processEpoch, documentJson, revisionJson,
            candidateJson, deleteJson, groupChangesJson, requestFingerprintSha256, candidateHash);
    }

    internal PcbDrcReceiptReservation ReserveHeld(string instanceId, string operationId,
        string processEpoch, string documentJson, string revisionJson, string candidateJson,
        string deleteJson, string groupChangesJson, string requestFingerprintSha256, string candidateHash)
    {
        ValidateIdentity(instanceId, operationId, processEpoch, documentJson, revisionJson,
            candidateJson, deleteJson, groupChangesJson, requestFingerprintSha256, candidateHash);
        var existing = Read(instanceId, operationId);
        if (existing is not null)
        {
            if (existing.ProcessEpoch != processEpoch)
                throw new AutomationException("stale_process_epoch", "The PCB DRC operation belongs to another native process epoch.");
            if (existing.Outcome == PcbDrcReceiptOutcome.Retryable)
            {
                // A retryable receipt proves native dispatch did not occur,
                // so the caller may correct a stale revision or other request
                // bytes while retaining the same idempotency identity. The
                // replacement admission becomes the new exact request that
                // future retries must match.
                var prepared = existing with
                {
                    DocumentJson = documentJson,
                    RevisionJson = revisionJson,
                    CandidateJson = candidateJson,
                    DeleteJson = deleteJson,
                    GroupChangesJson = groupChangesJson,
                    RequestFingerprintSha256 = requestFingerprintSha256,
                    CandidateHash = candidateHash,
                    Outcome = PcbDrcReceiptOutcome.Prepared,
                    NativeDispatchKnown = false,
                    NativeDispatch = false,
                    ErrorCode = null,
                    ErrorMessage = null,
                    ResultJson = null,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                Write(prepared, existing);
                return new(prepared, true);
            }
            if (!SameRequest(existing, instanceId, operationId, processEpoch, documentJson, revisionJson,
                    candidateJson, deleteJson, groupChangesJson, requestFingerprintSha256, candidateHash))
                throw new AutomationException("drc_operation_reused", "The PCB DRC operation ID is already bound to different request bytes.");
            if (existing.Outcome == PcbDrcReceiptOutcome.Completed)
                return new(existing, false);
            if (existing.Outcome == PcbDrcReceiptOutcome.Stale)
                throw new AutomationException("drc_operation_stale", "The PCB DRC operation is permanently stale; start a new operation.");
            throw new AutomationException("operation_outcome_unknown", "The PCB DRC operation has no confirmed native outcome; inspect its receipt before retrying.");
        }

        var created = new PcbDrcReceiptRecord(PcbDrcReceiptRecord.CurrentVersion, instanceId, operationId,
            null, processEpoch, documentJson, revisionJson, candidateJson, deleteJson, groupChangesJson,
            requestFingerprintSha256, candidateHash, PcbDrcReceiptOutcome.Prepared, false, false, null, null, null,
            DateTimeOffset.UtcNow);
        Write(created, null);
        return new(created, true);
    }

    internal PcbDrcReceiptRecord MarkUnknown(string instanceId, string operationId,
        bool? nativeDispatch, string errorCode, string errorMessage)
    {
        using var lease = Acquire(instanceId, operationId);
        return MarkUnknownHeld(instanceId, operationId, nativeDispatch, errorCode, errorMessage);
    }

    internal PcbDrcReceiptRecord MarkUnknownHeld(string instanceId, string operationId,
        bool? nativeDispatch, string errorCode, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(errorMessage))
            throw new AutomationException("invalid_drc_receipt", "An unknown PCB DRC outcome requires an error code and message.");
        var prior = Read(instanceId, operationId)
            ?? throw new AutomationException("drc_receipt_missing", "The PCB DRC admission receipt is missing.");
        if (prior.Outcome is PcbDrcReceiptOutcome.Completed or PcbDrcReceiptOutcome.Stale)
            return prior;
        var outcome = nativeDispatch == false ? PcbDrcReceiptOutcome.Retryable : PcbDrcReceiptOutcome.Unknown;
        var current = prior with
        {
            Outcome = outcome,
            NativeDispatchKnown = nativeDispatch.HasValue,
            NativeDispatch = nativeDispatch == true,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ResultJson = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        Write(current, prior);
        return current;
    }

    internal PcbDrcReceiptRecord MarkCompleted(string instanceId, string operationId,
        string processEpoch, string jobId, string resultJson)
    {
        using var lease = Acquire(instanceId, operationId);
        return MarkCompletedHeld(instanceId, operationId, processEpoch, jobId, resultJson);
    }

    internal PcbDrcReceiptRecord MarkCompletedHeld(string instanceId, string operationId,
        string processEpoch, string jobId, string resultJson)
    {
        if (!PcbDrcReceiptRecord.Canonical(processEpoch) || !PcbDrcReceiptRecord.Canonical(jobId)
            || string.IsNullOrWhiteSpace(resultJson))
            throw new AutomationException("invalid_drc_receipt", "A completed PCB DRC receipt needs canonical process, job and result identities.");
        var prior = Read(instanceId, operationId)
            ?? throw new AutomationException("drc_receipt_missing", "The PCB DRC admission receipt is missing.");
        if (prior.ProcessEpoch != processEpoch)
            throw new AutomationException("stale_process_epoch", "The PCB DRC receipt belongs to another native process epoch.");
        if (prior.JobId is not null && prior.JobId != jobId)
            throw new AutomationException("drc_receipt_conflict", "The PCB DRC job identity changed for the same operation.");
        if (prior.Outcome == PcbDrcReceiptOutcome.Stale)
            return prior;
        if (prior.Outcome == PcbDrcReceiptOutcome.Completed)
        {
            if (prior.ResultJson != resultJson || prior.JobId != jobId)
                throw new AutomationException("drc_receipt_conflict", "The PCB DRC terminal receipt is immutable.");
            return prior;
        }
        var current = prior with
        {
            JobId = jobId,
            Outcome = PcbDrcReceiptOutcome.Completed,
            NativeDispatchKnown = true,
            NativeDispatch = true,
            ResultJson = resultJson,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        Write(current, prior);
        return current;
    }

    internal PcbDrcReceiptRecord MarkStale(string instanceId, string operationId,
        string processEpoch, string jobId, string resultJson, string errorCode, string errorMessage)
    {
        using var lease = Acquire(instanceId, operationId);
        return MarkStaleHeld(instanceId, operationId, processEpoch, jobId, resultJson, errorCode, errorMessage);
    }

    internal PcbDrcReceiptRecord MarkStaleHeld(string instanceId, string operationId,
        string processEpoch, string jobId, string resultJson, string errorCode, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(errorMessage))
            throw new AutomationException("invalid_drc_receipt", "A stale PCB DRC receipt requires a reason.");
        var prior = Read(instanceId, operationId)
            ?? throw new AutomationException("drc_receipt_missing", "The PCB DRC admission receipt is missing.");
        if (prior.ProcessEpoch != processEpoch)
            throw new AutomationException("stale_process_epoch", "The PCB DRC receipt belongs to another native process epoch.");
        if (prior.JobId is not null && prior.JobId != jobId)
            throw new AutomationException("drc_receipt_conflict", "The PCB DRC job identity changed for the same operation.");
        if (prior.Outcome == PcbDrcReceiptOutcome.Stale && prior.JobId == jobId
            && prior.ResultJson == resultJson && prior.ErrorCode == errorCode && prior.ErrorMessage == errorMessage)
            return prior;
        var current = prior with
        {
            JobId = jobId,
            Outcome = PcbDrcReceiptOutcome.Stale,
            NativeDispatchKnown = true,
            NativeDispatch = true,
            ResultJson = resultJson,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        Write(current, prior);
        return current;
    }

    internal static bool SameRequest(PcbDrcReceiptRecord record, string instanceId, string operationId,
        string processEpoch, string documentJson, string revisionJson, string candidateJson,
        string deleteJson, string groupChangesJson, string requestFingerprintSha256, string candidateHash) =>
        record.InstanceId == instanceId && record.OperationId == operationId && record.ProcessEpoch == processEpoch
        && record.DocumentJson == documentJson && record.RevisionJson == revisionJson
        && record.CandidateJson == candidateJson && record.DeleteJson == deleteJson
        && record.GroupChangesJson == groupChangesJson
        && record.RequestFingerprintSha256 == requestFingerprintSha256 && record.CandidateHash == candidateHash;

    internal static string Fingerprint(string instanceId, string operationId, string processEpoch,
        string documentJson, string revisionJson, string candidateJson, string deleteJson,
        string groupChangesJson, string? expectedSchematicJson, bool refillZones,
        bool reportAllTrackErrors, bool testFootprints, bool allowDuplicateSheetNames) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", [
            "kicad-pcb-drc-receipt-v1", instanceId, operationId, processEpoch, documentJson,
            revisionJson, candidateJson, deleteJson, groupChangesJson, expectedSchematicJson ?? "",
            refillZones ? "1" : "0", reportAllTrackErrors ? "1" : "0", testFootprints ? "1" : "0",
            allowDuplicateSheetNames ? "1" : "0"]))) );

    private void Write(PcbDrcReceiptRecord record, PcbDrcReceiptRecord? previous)
    {
        record.Validate();
        Directory.CreateDirectory(directory);
        string path = ReceiptPath(record.InstanceId, record.OperationId);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
                if (bytes.Length > PcbDrcReceiptRecord.MaximumReceiptBytes)
                    throw PcbDrcReceiptRecord.Invalid("The PCB DRC receipt is too large.");
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: previous is not null);
            temporary = string.Empty;
        }
        catch (IOException error) { throw new AutomationException("drc_receipt_persistence_failed", error.Message); }
        catch (UnauthorizedAccessException error) { throw new AutomationException("drc_receipt_persistence_failed", error.Message); }
        finally
        {
            if (temporary.Length != 0)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private string ReceiptPath(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId))) + "-" + operationId + ".json");

    private string LockPath(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".lock");

    private static void ValidateKey(string instanceId, string operationId)
    {
        if (!PcbDrcReceiptRecord.Canonical(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId must be a canonical UUID.");
        if (!PcbDrcReceiptRecord.Canonical(operationId))
            throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID.");
    }

    private static void ValidateIdentity(string instanceId, string operationId, string processEpoch,
        string documentJson, string revisionJson, string candidateJson, string deleteJson,
        string groupChangesJson, string requestFingerprintSha256, string candidateHash)
    {
        ValidateKey(instanceId, operationId);
        if (!PcbDrcReceiptRecord.Canonical(processEpoch) || string.IsNullOrWhiteSpace(documentJson)
            || string.IsNullOrWhiteSpace(revisionJson) || documentJson.Length > PcbDrcReceiptRecord.MaximumPayloadCharacters
            || revisionJson.Length > PcbDrcReceiptRecord.MaximumPayloadCharacters
            || candidateJson.Length > PcbDrcReceiptRecord.MaximumPayloadCharacters
            || deleteJson.Length > PcbDrcReceiptRecord.MaximumPayloadCharacters
            || groupChangesJson.Length > PcbDrcReceiptRecord.MaximumPayloadCharacters
            || !PcbDrcReceiptRecord.Digest(requestFingerprintSha256)
            || (candidateJson.Length != 0 && !PcbDrcReceiptRecord.Digest(candidateHash)))
            throw new AutomationException("invalid_drc_receipt", "The PCB DRC receipt request identity is incomplete or too large.");
        if (candidateJson.Length == 0 && candidateHash.Length != 0)
            throw new AutomationException("invalid_drc_receipt", "A missing PCB DRC candidate cannot carry a candidate hash.");
    }

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw PcbDrcReceiptRecord.Invalid("PCB DRC receipts require an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw PcbDrcReceiptRecord.Invalid("Duplicate PCB DRC receipt properties.");
            if (property.Value.ValueKind == JsonValueKind.Object) ValidateProperties(property.Value);
            else if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) ValidateProperties(item);
        }
    }
}
