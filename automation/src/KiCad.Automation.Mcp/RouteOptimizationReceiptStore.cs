using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using KiCad.Automation.Model;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Mcp;

internal sealed record RouteOptimizationReceiptRecord(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("processEpoch")] string ProcessEpoch,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("resultJson")] string ResultJson,
    [property: JsonPropertyName("replayObserved")] bool ReplayObserved = false)
{
    internal void Validate()
    {
        if (!Canonical(InstanceId) || !Canonical(ProcessEpoch) || !Canonical(OperationId)
            || !Digest(Fingerprint) || string.IsNullOrWhiteSpace(ResultJson))
            throw new AutomationException("invalid_route_receipt", "The route receipt identity is invalid.");
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(ResultJson);
            JsonElement payload = parsed.RootElement;
            if (payload.ValueKind != JsonValueKind.Object
                || !Matches(payload, "instanceId", InstanceId)
                || !Matches(payload, "processEpoch", ProcessEpoch)
                || !Matches(payload, "operationId", OperationId))
                throw new AutomationException("invalid_route_receipt",
                    "The route receipt result identity does not match its envelope.");
        }
        catch (JsonException error) { throw new AutomationException("invalid_route_receipt", error.Message); }
    }

    private static bool Matches(JsonElement payload, string property, string expected) =>
        payload.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool Canonical(string value) => Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty && value == id.ToString("D");
    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
}

internal sealed class RouteOptimizationReceiptStore
{
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal RouteOptimizationReceiptStore(string stateDirectory)
    {
        if (!Path.IsPathFullyQualified(stateDirectory))
            throw new AutomationException("invalid_route_receipt", "Route receipt state directory must be absolute.");
        directory = Path.Combine(Path.GetFullPath(stateDirectory), "pcb-route-receipts");
    }

    /// <summary>
    /// Acquires the durable per-operation admission lease. The lease must span
    /// the receipt read, native dispatch and terminal write so independent MCP
    /// processes cannot both admit the same operation before either writes a
    /// receipt. Lock files are retained as stable inodes; only the lease is
    /// released when the returned stream is disposed.
    /// </summary>
    internal FileStream Acquire(string instanceId, string operationId)
    {
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(LockFor(instanceId, operationId), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new AutomationException("route_receipt_busy", error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new AutomationException("route_receipt_busy", error.Message);
        }
    }

    internal RouteOptimizationReceiptRecord? Read(string instanceId, string operationId)
    {
        string path = PathFor(instanceId, operationId);
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<RouteOptimizationReceiptRecord>(File.ReadAllBytes(path), Json)
                ?? throw new AutomationException("invalid_route_receipt", "The route receipt is empty.");
            record.Validate();
            if (record.InstanceId != instanceId || record.OperationId != operationId)
                throw new AutomationException("invalid_route_receipt", "The route receipt filename does not match its identity.");
            return record;
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new AutomationException("route_receipt_unavailable", error.Message); }
    }

    internal void Write(string instanceId, string processEpoch, string operationId,
        string fingerprint, JsonElement result)
    {
        var record = new RouteOptimizationReceiptRecord(instanceId, processEpoch, operationId,
            fingerprint, result.GetRawText(), ReplayObserved: false);
        record.Validate();
        Directory.CreateDirectory(directory);
        WriteRecord(record, overwrite: true);
    }

    /// <summary>
    /// Retains the immutable terminal result while recording that an exact
    /// retry has been served from MCP state.  The caller holds the operation
    /// lease for the read/mark/response sequence.
    /// </summary>
    internal RouteOptimizationReceiptRecord MarkReplayed(RouteOptimizationReceiptRecord record)
    {
        record.Validate();
        if (record.ReplayObserved) return record;
        var replayed = record with { ReplayObserved = true };
        replayed.Validate();
        WriteRecord(replayed, overwrite: true);
        return replayed;
    }

    private void WriteRecord(RouteOptimizationReceiptRecord record, bool overwrite)
    {
        Directory.CreateDirectory(directory);
        string path = PathFor(record.InstanceId, record.OperationId);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(record, Json));
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    internal static CallToolResult Result(RouteOptimizationReceiptRecord record)
    {
        record.Validate();
        JsonElement payload = JsonDocument.Parse(record.ResultJson).RootElement.Clone();
        return new CallToolResult { StructuredContent = payload,
            Content = [new TextContentBlock { Text = payload.GetRawText() }] };
    }

    /// <summary>
    /// Projects an exact retry from the immutable terminal result.  Only the
    /// replay markers change; all state, geometry and receipt evidence remain
    /// the original native outcome.
    /// </summary>
    internal static CallToolResult ReplayResult(RouteOptimizationReceiptRecord record)
    {
        record.Validate();
        JsonObject payload = JsonNode.Parse(record.ResultJson)?.AsObject()
            ?? throw new AutomationException("invalid_route_receipt", "The route receipt result must be a JSON object.");
        payload["replayed"] = true;
        MarkReceipt(payload["receipt"]);
        MarkReceipt(payload["nativeReceipt"]);
        if (payload["checkedRoute"] is JsonObject checkedRoute)
        {
            MarkReceipt(checkedRoute["receipt"]);
            MarkReceipt(checkedRoute["nativeReceipt"]);
        }
        JsonElement projected = JsonSerializer.SerializeToElement(payload, Json);
        return new CallToolResult { StructuredContent = projected,
            Content = [new TextContentBlock { Text = projected.GetRawText() }] };
    }

    private static void MarkReceipt(JsonNode? value)
    {
        if (value is JsonObject receipt) receipt["replayed"] = true;
    }

    private string PathFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".json");

    private string LockFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".lock");
}
