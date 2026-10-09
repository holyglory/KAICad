using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

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
    [property: JsonRequired] JsonElement Result)
{
    internal void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(InstanceId)
            || !CanonicalGuid(OperationId) || !CanonicalGuid(ProcessEpoch)
            || !Sha256(Fingerprint) || Result.ValueKind != JsonValueKind.Object)
            throw Invalid("The schematic export receipt identity or result is invalid.");
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
            File.Move(temporary, path, overwrite: false);
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
