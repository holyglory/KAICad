using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

/// <summary>The durable states of one document-revert admission.</summary>
internal enum DocumentRevertReceiptOutcome
{
    Admitted,
    Unknown,
    Completed
}

/// <summary>
/// The MCP-side identity and terminal evidence for a document revert.  The native
/// command used by this compatibility path has no process-owned receipt, so the
/// admission record is itself durable recovery evidence and an existing admission
/// is never dispatched a second time.
/// </summary>
internal sealed record DocumentRevertReceiptRecord(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("processEpoch")] string ProcessEpoch,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("outcome")] DocumentRevertReceiptOutcome Outcome,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("message")] string? Message)
{
    internal const int CurrentVersion = 1;

    internal void Validate()
    {
        if (Version != CurrentVersion || !CanonicalGuid(InstanceId) || !CanonicalGuid(OperationId)
            || !CanonicalGuid(ProcessEpoch) || !Sha256(Fingerprint) || !Enum.IsDefined(Outcome))
            throw Invalid("The document-revert receipt identity is invalid.");

        switch (Outcome)
        {
            case DocumentRevertReceiptOutcome.Admitted when Result is not null:
                throw Invalid("An admitted document-revert receipt cannot contain a terminal result.");
            case DocumentRevertReceiptOutcome.Admitted when !string.IsNullOrEmpty(Message):
                throw Invalid("An admitted document-revert receipt cannot contain a terminal message.");
            case DocumentRevertReceiptOutcome.Unknown when Result is not null || string.IsNullOrWhiteSpace(Message):
                throw Invalid("An indeterminate document-revert receipt requires a message and no result.");
            case DocumentRevertReceiptOutcome.Completed when Result is null || !string.IsNullOrEmpty(Message):
                throw Invalid("A completed document-revert receipt requires a result and no terminal message.");
        }

        if (Result is { } result && result.ValueKind != JsonValueKind.Object)
            throw Invalid("The document-revert result must be a JSON object.");
    }

    internal static AutomationException Invalid(string message) =>
        new("revert_receipt_corrupt", message);

    internal static bool CanonicalGuid(string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && value == id.ToString("D");

    internal static bool Sha256(string? value) => value is not null && value.Length == 64
        && value.All(char.IsAsciiHexDigitLower);
}

internal sealed record DocumentRevertReceiptReservation(DocumentRevertReceiptRecord Receipt, bool Created);

/// <summary>
/// Atomic, cross-process MCP state for document-revert admissions and receipts.
/// One file belongs to one instance/operation pair; lock inodes are never removed,
/// and terminal files are replaced only while the same per-operation lease is held.
/// </summary>
internal sealed class DocumentRevertReceiptStore
{
    private const int MaximumReceiptBytes = 16 * 1024 * 1024;
    private readonly string root;
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal DocumentRevertReceiptStore(string stateDirectory)
    {
        root = Path.GetFullPath(stateDirectory);
        directory = Path.Combine(root, "document-revert-receipts");
    }

    internal DocumentRevertReceiptRecord? Read(string instanceId, string operationId)
    {
        ValidateKey(instanceId, operationId);
        RequireDirectory(false);
        string path = PathFor(instanceId, operationId);
        try
        {
            if (File.Exists(path)) return ReadFile(path, instanceId, operationId);

            // Receipts created by the first implementation used one shared JSON map.
            // Read an exact matching entry for a controlled migration, but never treat a
            // malformed or identity-mismatched legacy entry as an absent receipt.
            return ReadLegacy(instanceId, operationId);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException error)
        {
            throw DocumentRevertReceiptRecord.Invalid(error.Message);
        }
        catch (IOException error)
        {
            throw new AutomationException("revert_receipt_unavailable", error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new AutomationException("revert_receipt_unavailable", error.Message);
        }
    }

    internal DocumentRevertReceiptReservation Reserve(string instanceId, string operationId,
        string processEpoch, string fingerprint)
    {
        ValidateIdentity(instanceId, operationId, processEpoch, fingerprint);
        RequireDirectory(true);
        string path = PathFor(instanceId, operationId);
        using var lease = Acquire(LockFor(instanceId, operationId));

        var existing = ReadCurrent(path, instanceId, operationId) ?? ReadLegacy(instanceId, operationId);
        if (existing is not null)
        {
            VerifyRequest(existing, instanceId, operationId, processEpoch, fingerprint);
            return new(existing, false);
        }

        var admitted = new DocumentRevertReceiptRecord(
            DocumentRevertReceiptRecord.CurrentVersion, instanceId, operationId,
            processEpoch, fingerprint, DocumentRevertReceiptOutcome.Admitted, null, null);
        WriteAtomic(path, admitted, overwrite: false);
        return new(admitted, true);
    }

    internal void WriteTerminal(DocumentRevertReceiptRecord receipt)
    {
        receipt.Validate();
        ValidateKey(receipt.InstanceId, receipt.OperationId);
        RequireDirectory(true);
        string path = PathFor(receipt.InstanceId, receipt.OperationId);
        using var lease = Acquire(LockFor(receipt.InstanceId, receipt.OperationId));
        var existing = ReadCurrent(path, receipt.InstanceId, receipt.OperationId) ?? ReadLegacy(receipt.InstanceId, receipt.OperationId);
        if (existing is not null)
        {
            VerifyRequest(existing, receipt.InstanceId, receipt.OperationId, receipt.ProcessEpoch, receipt.Fingerprint);
            if (existing.Outcome == receipt.Outcome && Equivalent(existing, receipt)) return;
            if (existing.Outcome != DocumentRevertReceiptOutcome.Admitted)
                throw new AutomationException("revert_receipt_conflict", "The document-revert operation already has a different terminal receipt.");
        }

        WriteAtomic(path, receipt, overwrite: existing is not null);
    }

    /// <summary>Drop only an admission that is proven not to have reached native.</summary>
    internal void RemoveAdmission(string instanceId, string operationId, string processEpoch, string fingerprint)
    {
        ValidateIdentity(instanceId, operationId, processEpoch, fingerprint);
        RequireDirectory(false);
        string path = PathFor(instanceId, operationId);
        if (!File.Exists(path)) return;
        using var lease = Acquire(LockFor(instanceId, operationId));
        var existing = ReadCurrent(path, instanceId, operationId);
        if (existing is null) return;
        VerifyRequest(existing, instanceId, operationId, processEpoch, fingerprint);
        if (existing.Outcome == DocumentRevertReceiptOutcome.Admitted)
            File.Delete(path);
    }

    internal static string Fingerprint(string instanceId, string operationId, string stateJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            instanceId + "\n" + operationId + "\n" + stateJson)));

    private DocumentRevertReceiptRecord? ReadCurrent(string path, string instanceId, string operationId) =>
        File.Exists(path) ? ReadFile(path, instanceId, operationId) : null;

    private DocumentRevertReceiptRecord? ReadFile(string path, string instanceId, string operationId)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw DocumentRevertReceiptRecord.Invalid("Document-revert receipts require ordinary files.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumReceiptBytes)
            throw DocumentRevertReceiptRecord.Invalid("The document-revert receipt is empty or too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using var parsed = JsonDocument.Parse(bytes);
        ValidateObject(parsed.RootElement);
        var receipt = JsonSerializer.Deserialize<DocumentRevertReceiptRecord>(bytes, Json)
            ?? throw DocumentRevertReceiptRecord.Invalid("The document-revert receipt is empty.");
        receipt.Validate();
        if (receipt.InstanceId != instanceId || receipt.OperationId != operationId)
            throw DocumentRevertReceiptRecord.Invalid("The receipt filename and operation identity differ.");
        return receipt;
    }

    private DocumentRevertReceiptRecord? ReadLegacy(string instanceId, string operationId)
    {
        string path = Path.Combine(root, "document-revert-receipts.json");
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw DocumentRevertReceiptRecord.Invalid("The legacy document-revert receipt must be an ordinary file.");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes.Length > MaximumReceiptBytes)
            throw DocumentRevertReceiptRecord.Invalid("The legacy document-revert receipt is empty or too large.");
        using var parsed = JsonDocument.Parse(bytes);
        ValidateObject(parsed.RootElement);
        var entries = JsonSerializer.Deserialize<Dictionary<string, LegacyReceipt>>(bytes, Json)
            ?? throw DocumentRevertReceiptRecord.Invalid("The legacy document-revert receipt is empty.");
        if (!entries.TryGetValue(operationId, out var value)) return null;
        if (value is null || value.OperationId != operationId)
            throw DocumentRevertReceiptRecord.Invalid("The legacy receipt operation identity does not match its lookup key.");
        var migrated = new DocumentRevertReceiptRecord(value.Version, value.InstanceId, value.OperationId,
            value.ProcessEpoch, value.Fingerprint, value.Outcome, value.Result, value.Message);
        migrated.Validate();
        if (migrated.InstanceId != instanceId)
            throw new AutomationException("revert_receipt_conflict", "The legacy document-revert receipt belongs to another instance.");
        return migrated;
    }

    private static void VerifyRequest(DocumentRevertReceiptRecord receipt, string instanceId,
        string operationId, string processEpoch, string fingerprint)
    {
        if (receipt.InstanceId != instanceId || receipt.OperationId != operationId)
            throw new AutomationException("revert_receipt_conflict", "The document-revert receipt belongs to another operation or instance.");
        if (receipt.ProcessEpoch != processEpoch || receipt.Fingerprint != fingerprint)
            throw new AutomationException("operation_reused", "The revert operation UUID was already used for another process epoch or document observation; choose a new operation UUID.");
    }

    private static bool Equivalent(DocumentRevertReceiptRecord first, DocumentRevertReceiptRecord second)
    {
        if (first.Version != second.Version || first.InstanceId != second.InstanceId
            || first.OperationId != second.OperationId || first.ProcessEpoch != second.ProcessEpoch
            || first.Fingerprint != second.Fingerprint || first.Outcome != second.Outcome
            || first.Message != second.Message)
            return false;
        if (first.Result is null || second.Result is null) return first.Result is null && second.Result is null;
        return Encoding.UTF8.GetBytes(first.Result.Value.GetRawText())
            .AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(second.Result.Value.GetRawText()));
    }

    private void WriteAtomic(string path, DocumentRevertReceiptRecord receipt, bool overwrite)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        if (bytes.Length > MaximumReceiptBytes)
            throw DocumentRevertReceiptRecord.Invalid("The document-revert receipt is too large.");
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
        {
            throw new AutomationException("revert_receipt_persistence_failed", error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new AutomationException("revert_receipt_persistence_failed", error.Message);
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
        { throw new AutomationException("revert_receipt_busy", error.Message); }
        catch (UnauthorizedAccessException error)
        { throw new AutomationException("revert_receipt_busy", error.Message); }
    }

    private void RequireDirectory(bool create)
    {
        if (create) Directory.CreateDirectory(directory);
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw DocumentRevertReceiptRecord.Invalid("Use the existing owned document-revert receipt directory.");
    }

    private string PathFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId))) + "-" + operationId + ".json");

    private string LockFor(string instanceId, string operationId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".lock";

    private static void ValidateKey(string instanceId, string operationId)
    {
        if (!DocumentRevertReceiptRecord.CanonicalGuid(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId must be a canonical UUID.");
        if (!DocumentRevertReceiptRecord.CanonicalGuid(operationId))
            throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID.");
    }

    private static void ValidateIdentity(string instanceId, string operationId, string processEpoch, string fingerprint)
    {
        ValidateKey(instanceId, operationId);
        if (!DocumentRevertReceiptRecord.CanonicalGuid(processEpoch))
            throw new AutomationException("invalid_process_epoch", "processEpoch must be a canonical UUID.");
        if (!DocumentRevertReceiptRecord.Sha256(fingerprint))
            throw DocumentRevertReceiptRecord.Invalid("The document-revert request fingerprint is invalid.");
    }

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw DocumentRevertReceiptRecord.Invalid("Document-revert receipts require an object.");
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
                        throw DocumentRevertReceiptRecord.Invalid("Duplicate document-revert receipt properties.");
                    stack.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray()) stack.Push(item);
            }
        }
    }

    // The legacy record is intentionally strict.  A legacy receipt that cannot be
    // fully promoted is corruption, never permission to redispatch a destructive edit.
    private sealed record LegacyReceipt(int Version, string InstanceId, string OperationId,
        string ProcessEpoch, string Fingerprint, DocumentRevertReceiptOutcome Outcome,
        JsonElement? Result, string? Message);
}
