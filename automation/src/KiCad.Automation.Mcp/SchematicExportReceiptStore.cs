using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// The exact schematic export request retained with a durable MCP receipt.
/// The native process owns the export, but this envelope lets a replacement
/// process verify the original request without looking up or replaying native
/// state that no longer exists.
/// </summary>
internal sealed record SchematicExportReceiptRequest(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("processEpoch")] string ProcessEpoch,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("jobKind")] string JobKind,
    [property: JsonPropertyName("requestJson")] string RequestJson,
    [property: JsonPropertyName("expectedStateJson")] string ExpectedStateJson)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal string Fingerprint => ComputeFingerprint(
        InstanceId, JobKind, RequestJson, ExpectedStateJson);

    internal string Serialize() => JsonSerializer.Serialize(this, Json);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(InstanceId)
            || !CanonicalGuid(ProcessEpoch) || !CanonicalGuid(OperationId)
            || JobKind is not ("svg" or "dxf" or "pdf" or "ps" or "netlist" or "bom")
            || string.IsNullOrWhiteSpace(RequestJson)
            || string.IsNullOrWhiteSpace(ExpectedStateJson))
            throw Invalid("The schematic export request envelope is incomplete.");
    }

    internal static SchematicExportReceiptRequest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw Invalid("The original schematic export request is required for process-restart recovery.");
        try
        {
            var request = JsonSerializer.Deserialize<SchematicExportReceiptRequest>(json, Json)
                ?? throw Invalid("The schematic export request envelope is empty.");
            request.Validate();
            return request;
        }
        catch (AutomationException) { throw; }
        catch (JsonException error)
        { throw Invalid("The schematic export request envelope is invalid: " + error.Message); }
    }

    internal static string ComputeFingerprint(string instanceId, string jobKind,
        string requestJson, string expectedStateJson) => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                instanceId + "\n" + jobKind + "\n" + requestJson + "\n" + expectedStateJson)));

    private static bool CanonicalGuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && value == id.ToString("D");

    private static AutomationException Invalid(string message) =>
        new("invalid_schematic_export_receipt", message);
}

/// <summary>
/// Durable MCP-side receipts for completed schematic manufacturing exports.
/// The native job remains authoritative; this store only preserves the exact
/// successful MCP result so an identical operation can be observed after the
/// MCP process is restarted.
/// </summary>
internal sealed record SchematicExportReceiptRecord(
    [property: JsonRequired] int Version,
    [property: JsonRequired] string InstanceId,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string Fingerprint,
    [property: JsonRequired] string ProcessEpoch,
    [property: JsonRequired] JsonElement Result,
    string RequestJson = "",
    string ExpectedStateJson = "",
    string JobKind = "",
    string Outcome = "completed",
    string ErrorCode = "",
    string ErrorMessage = "")
{
    internal const string Admitted = "admitted";
    internal const string Unknown = "unknown";
    internal const string Completed = "completed";

    internal void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(InstanceId)
            || !CanonicalGuid(OperationId) || !CanonicalGuid(ProcessEpoch)
            || !Sha256(Fingerprint) || Result.ValueKind != JsonValueKind.Object
            || Outcome is not (Admitted or Unknown or Completed))
            throw Invalid("The schematic export receipt identity or result is invalid.");

        bool hasRequest = RequestJson.Length != 0 || ExpectedStateJson.Length != 0 || JobKind.Length != 0;
        if (hasRequest)
        {
            if (RequestJson.Length == 0 || ExpectedStateJson.Length == 0 || JobKind.Length == 0)
                throw Invalid("The schematic export receipt retains only a partial original request.");
            var request = new SchematicExportReceiptRequest(
                InstanceId, ProcessEpoch, OperationId, JobKind, RequestJson, ExpectedStateJson);
            request.Validate();
            if (!string.Equals(request.Fingerprint, Fingerprint, StringComparison.Ordinal))
                throw Invalid("The schematic export receipt fingerprint does not match its original request.");
        }

        if (Outcome == Unknown && (string.IsNullOrWhiteSpace(ErrorCode)
            || string.IsNullOrWhiteSpace(ErrorMessage)))
            throw Invalid("An unknown schematic export outcome requires an error code and message.");
        if (Outcome == Admitted && !string.IsNullOrWhiteSpace(ErrorMessage)
            && string.IsNullOrWhiteSpace(ErrorCode))
            throw Invalid("An admitted schematic export outcome cannot have an unclassified error.");
    }

    private static bool CanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && value == id.ToString("D");

    private static bool Sha256(string value) => value.Length == 64
        && value.All(char.IsAsciiHexDigitLower);

    internal static AutomationException Invalid(string message) =>
        new("invalid_schematic_export_receipt", message);
}

/// <summary>Atomic, reconnectable MCP state for schematic export receipts.</summary>
internal sealed class SchematicExportReceiptStore
{
    private const int MaximumReceiptBytes = 64 * 1024 * 1024;
    private readonly string root;
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal SchematicExportReceiptStore(string stateDirectory)
    {
        root = Path.GetFullPath(stateDirectory);
        directory = Path.Combine(root, "schematic-export-receipts");
    }

    internal SchematicExportReceiptRecord? Read(string instanceId, string operationId)
    {
        ValidateIdentity(instanceId, operationId);
        string path = PathFor(instanceId, operationId);
        try
        {
            RequireDirectory(false);
            if (File.Exists(path)) return ReadFile(path, instanceId, operationId);
            return ReadLegacy(instanceId, operationId);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (JsonException error)
        { throw SchematicExportReceiptRecord.Invalid(error.Message); }
        catch (IOException error)
        { throw new AutomationException("schematic_export_receipt_unavailable", error.Message); }
        catch (UnauthorizedAccessException error)
        { throw new AutomationException("schematic_export_receipt_unavailable", error.Message); }
    }

    internal void Write(SchematicExportReceiptRecord receipt)
    {
        receipt.Validate();
        ValidateIdentity(receipt.InstanceId, receipt.OperationId);
        RequireDirectory(true);
        string path = PathFor(receipt.InstanceId, receipt.OperationId);
        using var lease = Acquire(Path.GetFileName(path) + ".lock");
        var prior = File.Exists(path) ? ReadFile(path, receipt.InstanceId, receipt.OperationId) : null;
        if (prior is not null)
        {
            if (prior.Fingerprint != receipt.Fingerprint || prior.ProcessEpoch != receipt.ProcessEpoch
                || !JsonSerializer.SerializeToUtf8Bytes(prior.Result, Json).AsSpan()
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(receipt.Result, Json)))
                throw new AutomationException("schematic_export_receipt_conflict",
                    "The schematic export operation is already bound to another result.");
            return;
        }
        WriteAtomic(path, receipt, overwrite: false);
    }

    internal void WriteAdmitted(SchematicExportReceiptRequest request)
    {
        request.Validate();
        ValidateIdentity(request.InstanceId, request.OperationId);
        JsonElement result = JsonSerializer.SerializeToElement(new
        {
            instanceId = request.InstanceId,
            processEpoch = request.ProcessEpoch,
            operationId = request.OperationId,
            job = request.JobKind,
            outcome = SchematicExportReceiptRecord.Admitted,
            nativeReceiptAvailable = false,
            retrySafety = "unknown-native-outcome",
            retryDispatched = false,
            replayed = false
        }, Json);
        var admitted = new SchematicExportReceiptRecord(1, request.InstanceId,
            request.OperationId, request.Fingerprint, request.ProcessEpoch, result,
            request.RequestJson, request.ExpectedStateJson, request.JobKind, SchematicExportReceiptRecord.Admitted,
            "operation_admitted", "The export was admitted before native dispatch; inspect the native process before retrying.");
        WriteTransition(admitted);
    }

    internal void WriteUnknown(SchematicExportReceiptRequest request,
        string errorCode, string errorMessage)
    {
        request.Validate();
        if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(errorMessage))
            throw SchematicExportReceiptRecord.Invalid("An unknown schematic export outcome requires an error code and message.");
        JsonElement result = JsonSerializer.SerializeToElement(new
        {
            instanceId = request.InstanceId,
            processEpoch = request.ProcessEpoch,
            operationId = request.OperationId,
            job = request.JobKind,
            outcome = SchematicExportReceiptRecord.Unknown,
            nativeReceiptAvailable = false,
            retrySafety = "unknown-native-outcome",
            retryDispatched = false,
            replayed = false,
            errorCode,
            errorMessage
        }, Json);
        var unknown = new SchematicExportReceiptRecord(1, request.InstanceId,
            request.OperationId, request.Fingerprint, request.ProcessEpoch, result,
            request.RequestJson, request.ExpectedStateJson, request.JobKind, SchematicExportReceiptRecord.Unknown,
            errorCode, errorMessage);
        WriteTransition(unknown);
    }

    internal void WriteTerminal(SchematicExportReceiptRequest request, JsonElement result)
    {
        request.Validate();
        var terminal = new SchematicExportReceiptRecord(1, request.InstanceId,
            request.OperationId, request.Fingerprint, request.ProcessEpoch, result.Clone(),
            request.RequestJson, request.ExpectedStateJson, request.JobKind, SchematicExportReceiptRecord.Completed);
        WriteTransition(terminal);
    }

    internal void RemoveAdmission(SchematicExportReceiptRequest request)
    {
        request.Validate();
        string path = PathFor(request.InstanceId, request.OperationId);
        if (!File.Exists(path)) return;
        using var lease = Acquire(Path.GetFileName(path) + ".lock");
        var prior = ReadFile(path, request.InstanceId, request.OperationId);
        if (prior is null) return;
        VerifyBinding(prior, request);
        if (prior.Outcome == SchematicExportReceiptRecord.Admitted) File.Delete(path);
    }

    internal SchematicExportReceiptRecord? ReadBound(string instanceId,
        SchematicExportReceiptRequest request)
    {
        request.Validate();
        ValidateIdentity(instanceId, request.OperationId);
        if (!string.Equals(instanceId, request.InstanceId, StringComparison.Ordinal))
            throw new AutomationException("operation_id_reused", "The schematic export request belongs to another MCP instance.");
        var record = Read(instanceId, request.OperationId);
        if (record is not null) VerifyBinding(record, request);
        return record;
    }

    private SchematicExportReceiptRecord? ReadFile(string path, string instanceId, string operationId)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw SchematicExportReceiptRecord.Invalid("Schematic export receipts require ordinary files.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumReceiptBytes)
            throw SchematicExportReceiptRecord.Invalid("The schematic export receipt is empty or too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using var parsed = JsonDocument.Parse(bytes);
        ValidateObject(parsed.RootElement);
        var receipt = JsonSerializer.Deserialize<SchematicExportReceiptRecord>(bytes, Json)
            ?? throw SchematicExportReceiptRecord.Invalid("The schematic export receipt is empty.");
        receipt.Validate();
        if (receipt.InstanceId != instanceId || receipt.OperationId != operationId)
            throw SchematicExportReceiptRecord.Invalid("The receipt filename and operation identity differ.");
        return receipt;
    }

    // Read the pre-store single-file format once so a running installation can
    // reconnect to receipts written before this store was introduced.
    private SchematicExportReceiptRecord? ReadLegacy(string instanceId, string operationId)
    {
        string path = Path.Combine(root, "schematic-export-receipts.json");
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw SchematicExportReceiptRecord.Invalid("The legacy schematic export receipt must be an ordinary file.");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes.Length > MaximumReceiptBytes)
            throw SchematicExportReceiptRecord.Invalid("The legacy schematic export receipt is empty or too large.");
        using var parsed = JsonDocument.Parse(bytes);
        ValidateObject(parsed.RootElement);
        var legacy = JsonSerializer.Deserialize<Dictionary<string, LegacyReceipt>>(bytes, Json);
        string key = instanceId + "\n" + operationId;
        if (legacy is null || !legacy.TryGetValue(key, out var value)) return null;
        if (!CanonicalGuid(value.ProcessEpoch) || !Sha256(value.Fingerprint)
            || value.Result.ValueKind != JsonValueKind.Object)
            throw SchematicExportReceiptRecord.Invalid("The legacy schematic export receipt is invalid.");
        return new(1, instanceId, operationId, value.Fingerprint, value.ProcessEpoch, value.Result.Clone());
    }

    private void WriteTransition(SchematicExportReceiptRecord receipt)
    {
        receipt.Validate();
        ValidateIdentity(receipt.InstanceId, receipt.OperationId);
        RequireDirectory(true);
        string path = PathFor(receipt.InstanceId, receipt.OperationId);
        using var lease = Acquire(Path.GetFileName(path) + ".lock");
        var prior = File.Exists(path) ? ReadFile(path, receipt.InstanceId, receipt.OperationId) : null;
        if (prior is not null)
        {
            VerifyBinding(prior, receipt);
            if (prior.Outcome == SchematicExportReceiptRecord.Completed)
            {
                if (receipt.Outcome == SchematicExportReceiptRecord.Completed && SameJson(prior.Result, receipt.Result)) return;
                if (receipt.Outcome == SchematicExportReceiptRecord.Unknown) return;
                throw new AutomationException("schematic_export_receipt_conflict",
                    "The schematic export operation already has a terminal result.");
            }
            if (prior.Outcome == SchematicExportReceiptRecord.Unknown && receipt.Outcome != SchematicExportReceiptRecord.Unknown) return;
        }
        WriteAtomic(path, receipt, overwrite: prior is not null);
    }

    private static void VerifyBinding(SchematicExportReceiptRecord record,
        SchematicExportReceiptRequest request)
    {
        if (record.InstanceId != request.InstanceId || record.OperationId != request.OperationId
            || record.Fingerprint != request.Fingerprint || record.ProcessEpoch != request.ProcessEpoch)
            throw new AutomationException("operation_id_reused",
                "The schematic export request does not match the durable receipt identity.");
        if (string.IsNullOrWhiteSpace(record.RequestJson)
            || string.IsNullOrWhiteSpace(record.ExpectedStateJson) || string.IsNullOrWhiteSpace(record.JobKind))
            throw new AutomationException("operation_id_reused",
                "The durable schematic export receipt does not retain its original request envelope.");
        var retained = new SchematicExportReceiptRequest(record.InstanceId, record.ProcessEpoch,
            record.OperationId, record.JobKind, record.RequestJson, record.ExpectedStateJson);
        retained.Validate();
        if (!string.Equals(retained.Serialize(), request.Serialize(), StringComparison.Ordinal))
            throw new AutomationException("operation_id_reused",
                "The original schematic export request differs from the durable receipt request.");
    }

    private static void VerifyBinding(SchematicExportReceiptRecord prior,
        SchematicExportReceiptRecord next)
    {
        if (prior.InstanceId != next.InstanceId || prior.OperationId != next.OperationId
            || prior.Fingerprint != next.Fingerprint || prior.ProcessEpoch != next.ProcessEpoch)
            throw new AutomationException("schematic_export_receipt_conflict",
                "The schematic export operation is already bound to another request.");
        if (!string.IsNullOrWhiteSpace(prior.RequestJson) || !string.IsNullOrWhiteSpace(next.RequestJson))
        {
            if (prior.RequestJson != next.RequestJson || prior.ExpectedStateJson != next.ExpectedStateJson
                || prior.JobKind != next.JobKind)
                throw new AutomationException("schematic_export_receipt_conflict",
                    "The schematic export operation is already bound to another request.");
        }
    }

    private static bool SameJson(JsonElement first, JsonElement second) =>
        JsonSerializer.SerializeToUtf8Bytes(first, Json).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second, Json));

    private void WriteAtomic(string path, SchematicExportReceiptRecord receipt, bool overwrite)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        if (bytes.Length > MaximumReceiptBytes)
            throw SchematicExportReceiptRecord.Invalid("The schematic export receipt is too large.");
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

    private FileStream Acquire(string name)
    {
        string path = Path.Combine(directory, name);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        { throw new AutomationException("schematic_export_receipt_busy", error.Message); }
    }

    private void RequireDirectory(bool create)
    {
        if (create) Directory.CreateDirectory(directory);
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw SchematicExportReceiptRecord.Invalid("Use the existing owned schematic export receipt directory.");
    }

    private string PathFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instanceId)))
        + "-" + operationId + ".json");

    private static void ValidateIdentity(string instanceId, string operationId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required.");
        if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty
            || operationId != id.ToString("D"))
            throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID.");
    }

    private static bool CanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && value == id.ToString("D");

    private static bool Sha256(string value) => value.Length == 64
        && value.All(char.IsAsciiHexDigitLower);

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw SchematicExportReceiptRecord.Invalid("Schematic export receipts require an object.");
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
                        throw SchematicExportReceiptRecord.Invalid("Duplicate schematic export receipt properties.");
                    stack.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray()) stack.Push(item);
            }
        }
    }

    private sealed record LegacyReceipt(string Fingerprint, string ProcessEpoch, JsonElement Result);
}
