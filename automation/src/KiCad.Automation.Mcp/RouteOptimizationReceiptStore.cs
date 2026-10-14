using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Mcp;

internal sealed record RouteOptimizationReceiptRecord(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("processEpoch")] string ProcessEpoch,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("resultJson")] string ResultJson)
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
            fingerprint, result.GetRawText());
        record.Validate();
        Directory.CreateDirectory(directory);
        string path = PathFor(instanceId, operationId);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(record, Json));
            File.Move(temp, path, true);
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

    private string PathFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".json");

    private string LockFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".lock");
}
