using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Durable MCP-side terminal receipts for checked PCB footprint placement.
/// The native checked transaction remains authoritative for board state; this
/// store keeps an exact successful result available after an MCP reconnect and
/// prevents a matching operation ID from dispatching a second create request.
/// Native process receipts and native-process restart recovery remain separate.
/// </summary>
internal sealed record PcbFootprintPlacementReceiptRecord(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("processEpoch")] string ProcessEpoch,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("requestFingerprintSha256")] string RequestFingerprintSha256,
    [property: JsonPropertyName("isError")] bool IsError,
    [property: JsonPropertyName("resultJson")] string ResultJson)
{
    internal const int CurrentVersion = 1;
    internal const int MaximumReceiptBytes = 4 * 1024 * 1024;

    internal void Validate()
    {
        if (Version != CurrentVersion || !CanonicalGuid(InstanceId)
            || !CanonicalGuid(ProcessEpoch) || !CanonicalGuid(OperationId)
            || !Sha256(RequestFingerprintSha256) || string.IsNullOrWhiteSpace(ResultJson))
            throw Invalid("The PCB footprint-placement receipt identity is invalid.");

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(ResultJson);
            JsonElement payload = parsed.RootElement;
            if (payload.ValueKind != JsonValueKind.Object
                || !Matches(payload, "instanceId", InstanceId)
                || (!Matches(payload, "processId", ProcessEpoch)
                    && !Matches(payload, "processEpoch", ProcessEpoch))
                || !Matches(payload, "operationId", OperationId))
                throw Invalid("The PCB footprint-placement result identity does not match its receipt.");
        }
        catch (JsonException error)
        {
            throw Invalid("The PCB footprint-placement result is malformed: " + error.Message);
        }
    }

    internal static AutomationException Invalid(string message) =>
        new("invalid_pcb_footprint_receipt", message);

    internal static bool CanonicalGuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        && value == id.ToString("D");

    internal static bool Sha256(string? value) => value is not null
        && value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static bool Matches(JsonElement payload, string property, string expected) =>
        payload.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
}

/// <summary>Atomic, cross-process MCP state for footprint placement operations.</summary>
internal sealed class PcbFootprintPlacementReceiptStore
{
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal PcbFootprintPlacementReceiptStore(string stateDirectory)
    {
        if (!Path.IsPathFullyQualified(stateDirectory))
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "The PCB footprint-placement receipt state directory must be absolute.");
        directory = Path.Combine(Path.GetFullPath(stateDirectory), "pcb-footprint-placement-receipts");
    }

    /// <summary>
    /// Holds a per-operation file lease across receipt read, native dispatch and
    /// terminal write. Independent MCP processes therefore cannot both admit an
    /// exact operation before either one records its result.
    /// </summary>
    internal FileStream Acquire(string instanceId, string operationId)
    {
        ValidateKey(instanceId, operationId);
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(LockFor(instanceId, operationId), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new AutomationException("pcb_footprint_receipt_busy", error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new AutomationException("pcb_footprint_receipt_busy", error.Message);
        }
    }

    internal PcbFootprintPlacementReceiptRecord? Read(string instanceId, string operationId)
    {
        ValidateKey(instanceId, operationId);
        if (!Directory.Exists(directory)) return null;
        string path = PathFor(instanceId, operationId);
        if (!File.Exists(path)) return null;
        try
        {
            return ReadFile(path, instanceId, operationId);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException error)
        {
            throw PcbFootprintPlacementReceiptRecord.Invalid(error.Message);
        }
        catch (IOException error)
        {
            throw new AutomationException("pcb_footprint_receipt_unavailable", error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new AutomationException("pcb_footprint_receipt_unavailable", error.Message);
        }
    }

    internal void Write(string instanceId, string processEpoch, string operationId,
        string requestFingerprintSha256, CallToolResult result)
    {
        using FileStream lease = Acquire(instanceId, operationId);
        WriteHeld(instanceId, processEpoch, operationId, requestFingerprintSha256, result);
    }

    /// <summary>Writes a terminal result while the caller holds Acquire's lease.</summary>
    internal void WriteHeld(string instanceId, string processEpoch, string operationId,
        string requestFingerprintSha256, CallToolResult result)
    {
        ValidateIdentity(instanceId, processEpoch, operationId, requestFingerprintSha256);
        if (result.StructuredContent is not JsonElement structured)
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "A terminal PCB footprint-placement result must contain structured JSON.");

        var record = new PcbFootprintPlacementReceiptRecord(
            PcbFootprintPlacementReceiptRecord.CurrentVersion, instanceId, processEpoch,
            operationId, requestFingerprintSha256, result.IsError ?? false, structured.GetRawText());
        record.Validate();
        Directory.CreateDirectory(directory);
        string path = PathFor(instanceId, operationId);
        PcbFootprintPlacementReceiptRecord? existing = ReadCurrent(path, instanceId, operationId);
        if (existing is not null)
        {
            VerifyBinding(existing, record);
            if (!Equivalent(existing, record))
                throw new AutomationException("pcb_footprint_receipt_conflict",
                    "The PCB footprint-placement operation already has a different terminal receipt.");
            return;
        }
        WriteAtomic(path, record, overwrite: false);
    }

    internal static string Fingerprint(string canonicalRequest) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

    internal static CallToolResult Result(PcbFootprintPlacementReceiptRecord record)
    {
        record.Validate();
        JsonElement structured = ParseResult(record.ResultJson);
        return new CallToolResult
        {
            IsError = record.IsError,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    internal static CallToolResult ReplayResult(PcbFootprintPlacementReceiptRecord record)
    {
        record.Validate();
        JsonObject payload = JsonNode.Parse(record.ResultJson)?.AsObject()
            ?? throw PcbFootprintPlacementReceiptRecord.Invalid(
                "The PCB footprint-placement result must be a JSON object.");
        payload["replayed"] = true;
        if (payload["receipt"] is JsonObject receipt) receipt["replayed"] = true;
        if (payload["nativeReceipt"] is JsonObject nativeReceipt) nativeReceipt["replayed"] = true;
        JsonElement projected = JsonSerializer.SerializeToElement(payload, Json);
        return new CallToolResult
        {
            IsError = record.IsError,
            StructuredContent = projected,
            Content = [new TextContentBlock { Text = projected.GetRawText() }]
        };
    }

    private PcbFootprintPlacementReceiptRecord? ReadCurrent(string path,
        string instanceId, string operationId) => File.Exists(path)
            ? ReadFile(path, instanceId, operationId) : null;

    private PcbFootprintPlacementReceiptRecord ReadFile(string path,
        string instanceId, string operationId)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "PCB footprint-placement receipts require ordinary files.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > PcbFootprintPlacementReceiptRecord.MaximumReceiptBytes)
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "The PCB footprint-placement receipt is empty or too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        ValidateObject(parsed.RootElement);
        PcbFootprintPlacementReceiptRecord result = JsonSerializer.Deserialize<PcbFootprintPlacementReceiptRecord>(bytes, Json)
            ?? throw PcbFootprintPlacementReceiptRecord.Invalid(
                "The PCB footprint-placement receipt is empty.");
        result.Validate();
        if (result.InstanceId != instanceId || result.OperationId != operationId)
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "The receipt filename and operation identity differ.");
        return result;
    }

    private static void VerifyBinding(PcbFootprintPlacementReceiptRecord existing,
        PcbFootprintPlacementReceiptRecord expected)
    {
        if (existing.InstanceId != expected.InstanceId || existing.OperationId != expected.OperationId
            || existing.ProcessEpoch != expected.ProcessEpoch
            || existing.RequestFingerprintSha256 != expected.RequestFingerprintSha256)
            throw new AutomationException("operation_id_reused",
                "The PCB footprint-placement operation UUID was already used for another request or native process.");
    }

    private static bool Equivalent(PcbFootprintPlacementReceiptRecord first,
        PcbFootprintPlacementReceiptRecord second) => first == second;

    private static JsonElement ParseResult(string resultJson) =>
        JsonDocument.Parse(resultJson).RootElement.Clone();

    private static void WriteAtomic(string path, PcbFootprintPlacementReceiptRecord record,
        bool overwrite)
    {
        record.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > PcbFootprintPlacementReceiptRecord.MaximumReceiptBytes)
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "The PCB footprint-placement receipt is too large.");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
            temporary = string.Empty;
        }
        catch (IOException error)
        {
            throw new AutomationException("pcb_footprint_receipt_persistence_failed", error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new AutomationException("pcb_footprint_receipt_persistence_failed", error.Message);
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

    private string PathFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".json");

    private string LockFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".lock");

    private static void ValidateKey(string instanceId, string operationId)
    {
        if (!PcbFootprintPlacementReceiptRecord.CanonicalGuid(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId must be a canonical UUID.");
        if (!PcbFootprintPlacementReceiptRecord.CanonicalGuid(operationId))
            throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID.");
    }

    private static void ValidateIdentity(string instanceId, string processEpoch,
        string operationId, string requestFingerprintSha256)
    {
        ValidateKey(instanceId, operationId);
        if (!PcbFootprintPlacementReceiptRecord.CanonicalGuid(processEpoch))
            throw new AutomationException("invalid_process_epoch", "processEpoch must be a canonical UUID.");
        if (!PcbFootprintPlacementReceiptRecord.Sha256(requestFingerprintSha256))
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "The PCB footprint-placement request fingerprint is invalid.");
    }

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw PcbFootprintPlacementReceiptRecord.Invalid(
                "PCB footprint-placement receipts require an object.");
        var stack = new Stack<JsonElement>();
        stack.Push(element);
        while (stack.TryPop(out JsonElement current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in current.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw PcbFootprintPlacementReceiptRecord.Invalid(
                            "Duplicate PCB footprint-placement receipt properties.");
                    stack.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in current.EnumerateArray()) stack.Push(item);
            }
        }
    }
}
