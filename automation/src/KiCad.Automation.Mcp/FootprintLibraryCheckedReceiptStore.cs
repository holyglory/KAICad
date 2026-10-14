using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Durable MCP-side admission and terminal evidence for one checked footprint
/// library write.  The native editor remains authoritative for the write, but
/// this journal keeps an exact request identity when the MCP process is
/// replaced or the native reply is lost.  An admitted or unknown record is
/// never permission to submit the operation again.
/// </summary>
internal sealed record FootprintLibraryCheckedReceiptRecord(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string InstanceId,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string ProcessEpoch,
    [property: JsonRequired] string RequestFingerprintSha256,
    [property: JsonRequired] string RequestJson,
    [property: JsonRequired] string Outcome,
    [property: JsonRequired] string ResponseJson,
    [property: JsonRequired] string ErrorCode,
    [property: JsonRequired] string ErrorMessage)
{
    internal const int CurrentVersion = 1;
    internal const string Admitted = "admitted";
    internal const string Unknown = "unknown";
    internal const string Terminal = "terminal";

    internal void Validate()
    {
        if (Version != CurrentVersion || !CanonicalGuid(InstanceId)
            || !CanonicalGuid(OperationId) || !CanonicalGuid(ProcessEpoch)
            || !Sha256(RequestFingerprintSha256)
            || string.IsNullOrWhiteSpace(RequestJson)
            || Outcome is not (Admitted or Unknown or Terminal)
            || (Outcome == Terminal && string.IsNullOrWhiteSpace(ResponseJson))
            || (Outcome != Terminal && !string.IsNullOrWhiteSpace(ResponseJson)))
            throw Invalid("The footprint-library receipt identity or outcome is invalid.");

        try
        {
            var request = SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedWrite>(RequestJson);
            if (!CanonicalGuid(request.OperationId) || request.OperationId != OperationId
                || !CanonicalGuid(request.ProcessEpoch) || request.ProcessEpoch != ProcessEpoch
                || request.Document is null || request.ExpectedState is null
                || request.ExpectedSnapshotSha256 != RequestFingerprintStateDigest(request))
                throw Invalid("The durable footprint request does not retain its exact operation identity or state digest.");

            if (FootprintRequestFingerprint(request) != RequestFingerprintSha256)
                throw Invalid("The durable footprint request fingerprint does not match its protobuf request.");

            if (Outcome == Terminal)
            {
                var response = SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedWriteResponse>(ResponseJson);
                if (response.OperationId != OperationId || response.ProcessEpoch != ProcessEpoch
                    || response.RequestFingerprintSha256 != RequestFingerprintSha256
                    || response.Request is null || !response.Request.Equals(request))
                    throw Invalid("The durable footprint response does not retain the exact request identity.");
            }
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw Invalid("The durable footprint protobuf receipt is malformed: " + error.Message); }
    }

    internal static AutomationException Invalid(string message) =>
        new("invalid_footprint_receipt", message);

    internal static bool CanonicalGuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && value == id.ToString("D");

    internal static bool Sha256(string? value) => value is not null
        && value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static string RequestFingerprintStateDigest(
        NativeCapabilityFootprintLibraryCheckedWrite request)
    {
        var state = request.ExpectedState.Clone();
        string digest = state.StateSha256;
        state.StateSha256 = string.Empty;
        // The native contract hashes the state after clearing its digest.  Reuse
        // the managed implementation indirectly through the request validator's
        // public digest helper to keep this journal bound to the same bytes.
        return FootprintStateDigest(state, digest);
    }

    private static string FootprintStateDigest(
        NativeCapabilityFootprintLibraryCheckedState state, string expected)
    {
        string actual = FootprintLibraryCheckedContract.ComputeStateDigest(state);
        return actual == expected ? expected : string.Empty;
    }

    private static string FootprintRequestFingerprint(
        NativeCapabilityFootprintLibraryCheckedWrite request) =>
        FootprintLibraryCheckedContract.ComputeFingerprint(request);
}

internal sealed class FootprintLibraryCheckedReceiptStore
{
    private const int MaximumReceiptBytes = 64 * 1024 * 1024;
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal FootprintLibraryCheckedReceiptStore(string stateDirectory)
    {
        directory = Path.Combine(Path.GetFullPath(stateDirectory),
            "footprint-library-checked-receipts");
    }

    internal FootprintLibraryCheckedReceiptReservation Reserve(
        string instanceId, NativeCapabilityFootprintLibraryCheckedWrite request)
    {
        string fingerprint = FootprintLibraryCheckedContract.ComputeFingerprint(request);
        ValidateKey(instanceId, request, fingerprint);
        Directory.CreateDirectory(directory);
        string path = PathFor(instanceId, request.OperationId);
        using var lease = Acquire(instanceId, request.OperationId);
        var existing = ReadCurrent(path, instanceId, request.OperationId);
        if (existing is not null)
        {
            VerifyBinding(existing, instanceId, request, fingerprint);
            return new(existing, false);
        }

        var admitted = new FootprintLibraryCheckedReceiptRecord(
            FootprintLibraryCheckedReceiptRecord.CurrentVersion, instanceId,
            request.OperationId, request.ProcessEpoch, fingerprint,
            SchematicJson.Formatter.Format(request),
            FootprintLibraryCheckedReceiptRecord.Admitted, string.Empty,
            string.Empty, string.Empty);
        WriteAtomic(path, admitted, overwrite: false);
        return new(admitted, true);
    }

    internal FootprintLibraryCheckedReceiptRecord? Read(string instanceId,
        string operationId)
    {
        if (!FootprintLibraryCheckedReceiptRecord.CanonicalGuid(instanceId)
            || !FootprintLibraryCheckedReceiptRecord.CanonicalGuid(operationId))
            throw new AutomationException("invalid_footprint_receipt", "Receipt lookup requires canonical instance and operation UUIDs.");
        string path = PathFor(instanceId, operationId);
        try
        {
            if (!File.Exists(path)) return null;
            return ReadFile(path, instanceId, operationId);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw FootprintLibraryCheckedReceiptRecord.Invalid(error.Message); }
        catch (IOException error)
        { throw new AutomationException("footprint_receipt_unavailable", error.Message); }
        catch (UnauthorizedAccessException error)
        { throw new AutomationException("footprint_receipt_unavailable", error.Message); }
    }

    internal void WriteTerminal(string instanceId,
        NativeCapabilityFootprintLibraryCheckedWrite request,
        NativeCapabilityFootprintLibraryCheckedWriteResponse response)
    {
        string fingerprint = FootprintLibraryCheckedContract.ComputeFingerprint(request);
        ValidateKey(instanceId, request, fingerprint);
        FootprintLibraryCheckedContract.ValidateResponse(response, request);
        Directory.CreateDirectory(directory);
        string path = PathFor(instanceId, request.OperationId);
        using var lease = Acquire(instanceId, request.OperationId);
        var existing = ReadCurrent(path, instanceId, request.OperationId);
        if (existing is not null)
        {
            VerifyBinding(existing, instanceId, request, fingerprint);
            if (existing.Outcome == FootprintLibraryCheckedReceiptRecord.Terminal)
            {
                var prior = ParseResponse(existing.ResponseJson);
                if (!prior.Equals(response))
                    throw new AutomationException("footprint_receipt_conflict", "The checked footprint operation already has a different terminal response.");
                return;
            }
        }

        var terminal = (existing ?? new FootprintLibraryCheckedReceiptRecord(
            FootprintLibraryCheckedReceiptRecord.CurrentVersion, instanceId,
            request.OperationId, request.ProcessEpoch, fingerprint,
            SchematicJson.Formatter.Format(request),
            FootprintLibraryCheckedReceiptRecord.Admitted, string.Empty,
            string.Empty, string.Empty)) with
        {
            Outcome = FootprintLibraryCheckedReceiptRecord.Terminal,
            ResponseJson = SchematicJson.Formatter.Format(response),
            ErrorCode = response.ErrorCode,
            ErrorMessage = response.ErrorMessage
        };
        WriteAtomic(path, terminal, overwrite: existing is not null);
    }

    internal void WriteUnknown(string instanceId,
        NativeCapabilityFootprintLibraryCheckedWrite request,
        string errorCode, string errorMessage)
    {
        string fingerprint = FootprintLibraryCheckedContract.ComputeFingerprint(request);
        ValidateKey(instanceId, request, fingerprint);
        if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(errorMessage))
            throw new AutomationException("invalid_footprint_receipt", "An indeterminate checked footprint receipt requires an error code and message.");
        Directory.CreateDirectory(directory);
        string path = PathFor(instanceId, request.OperationId);
        using var lease = Acquire(instanceId, request.OperationId);
        var existing = ReadCurrent(path, instanceId, request.OperationId);
        if (existing is not null)
        {
            VerifyBinding(existing, instanceId, request, fingerprint);
            if (existing.Outcome == FootprintLibraryCheckedReceiptRecord.Terminal) return;
        }
        var unknown = (existing ?? new FootprintLibraryCheckedReceiptRecord(
            FootprintLibraryCheckedReceiptRecord.CurrentVersion, instanceId,
            request.OperationId, request.ProcessEpoch, fingerprint,
            SchematicJson.Formatter.Format(request),
            FootprintLibraryCheckedReceiptRecord.Admitted, string.Empty,
            string.Empty, string.Empty)) with
        {
            Outcome = FootprintLibraryCheckedReceiptRecord.Unknown,
            ResponseJson = string.Empty,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
        WriteAtomic(path, unknown, overwrite: existing is not null);
    }

    /// <summary>Remove only an admission that is proven not to have reached native.</summary>
    internal void RemoveAdmission(string instanceId,
        NativeCapabilityFootprintLibraryCheckedWrite request)
    {
        string fingerprint = FootprintLibraryCheckedContract.ComputeFingerprint(request);
        ValidateKey(instanceId, request, fingerprint);
        string path = PathFor(instanceId, request.OperationId);
        if (!File.Exists(path)) return;
        using var lease = Acquire(instanceId, request.OperationId);
        var existing = ReadCurrent(path, instanceId, request.OperationId);
        if (existing is null) return;
        VerifyBinding(existing, instanceId, request, fingerprint);
        if (existing.Outcome == FootprintLibraryCheckedReceiptRecord.Admitted)
            File.Delete(path);
    }

    internal static NativeCapabilityFootprintLibraryCheckedWrite ParseRequest(
        FootprintLibraryCheckedReceiptRecord record) =>
        SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedWrite>(record.RequestJson);

    internal static NativeCapabilityFootprintLibraryCheckedWriteResponse ParseResponse(
        string json) => SchematicJson.Parser.Parse<NativeCapabilityFootprintLibraryCheckedWriteResponse>(json);

    private FootprintLibraryCheckedReceiptRecord? ReadCurrent(string path,
        string instanceId, string operationId) =>
        File.Exists(path) ? ReadFile(path, instanceId, operationId) : null;

    private FootprintLibraryCheckedReceiptRecord ReadFile(string path,
        string instanceId, string operationId)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw FootprintLibraryCheckedReceiptRecord.Invalid("Footprint-library receipts require ordinary files.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumReceiptBytes)
            throw FootprintLibraryCheckedReceiptRecord.Invalid("The footprint-library receipt is empty or too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using var parsed = JsonDocument.Parse(bytes);
        ValidateObject(parsed.RootElement);
        var result = JsonSerializer.Deserialize<FootprintLibraryCheckedReceiptRecord>(bytes, Json)
            ?? throw FootprintLibraryCheckedReceiptRecord.Invalid("The footprint-library receipt is empty.");
        result.Validate();
        if (result.InstanceId != instanceId || result.OperationId != operationId)
            throw FootprintLibraryCheckedReceiptRecord.Invalid("The receipt filename and operation identity differ.");
        return result;
    }

    private static void VerifyBinding(FootprintLibraryCheckedReceiptRecord record,
        string instanceId, NativeCapabilityFootprintLibraryCheckedWrite request,
        string fingerprint)
    {
        if (record.InstanceId != instanceId || record.ProcessEpoch != request.ProcessEpoch
            || record.RequestFingerprintSha256 != fingerprint)
            throw new AutomationException("operation_id_conflict", "The operation ID is already bound to another checked footprint request.");
        var prior = ParseRequest(record);
        if (!prior.Equals(request))
            throw new AutomationException("operation_id_conflict", "The operation ID is already bound to another checked footprint request.");
    }

    private static void ValidateKey(string instanceId,
        NativeCapabilityFootprintLibraryCheckedWrite request, string fingerprint)
    {
        if (!FootprintLibraryCheckedReceiptRecord.CanonicalGuid(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId must be a canonical UUID.");
        if (!FootprintLibraryCheckedReceiptRecord.CanonicalGuid(request.OperationId)
            || !FootprintLibraryCheckedReceiptRecord.CanonicalGuid(request.ProcessEpoch))
            throw new AutomationException("invalid_footprint_receipt", "The checked footprint request requires canonical operation and process UUIDs.");
        if (!FootprintLibraryCheckedReceiptRecord.Sha256(fingerprint))
            throw FootprintLibraryCheckedReceiptRecord.Invalid("The checked footprint request fingerprint is invalid.");
    }

    private void WriteAtomic(string path,
        FootprintLibraryCheckedReceiptRecord record, bool overwrite)
    {
        record.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > MaximumReceiptBytes)
            throw FootprintLibraryCheckedReceiptRecord.Invalid("The footprint-library receipt is too large.");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
            temporary = string.Empty;
        }
        catch (IOException error)
        { throw new AutomationException("footprint_receipt_persistence_failed", error.Message); }
        catch (UnauthorizedAccessException error)
        { throw new AutomationException("footprint_receipt_persistence_failed", error.Message); }
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

    private FileStream Acquire(string instanceId, string operationId)
    {
        string path = Path.Combine(directory,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        { throw new AutomationException("footprint_receipt_busy", error.Message); }
        catch (UnauthorizedAccessException error)
        { throw new AutomationException("footprint_receipt_busy", error.Message); }
    }

    private string PathFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId)))
        + "-" + operationId + ".json");

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw FootprintLibraryCheckedReceiptRecord.Invalid("Footprint-library receipts require an object.");
        var stack = new Stack<JsonElement>();
        stack.Push(element);
        while (stack.TryPop(out var current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in current.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw FootprintLibraryCheckedReceiptRecord.Invalid("Duplicate footprint-library receipt properties.");
                    stack.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray()) stack.Push(item);
            }
        }
    }
}

internal sealed record FootprintLibraryCheckedReceiptReservation(
    FootprintLibraryCheckedReceiptRecord Receipt, bool Created);
