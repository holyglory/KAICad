using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Durable MCP-side admission and terminal evidence for one PCB artwork
/// operation. The native checked transaction remains authoritative for board
/// state; this record prevents a second dispatch after an MCP restart and
/// preserves an explicit unknown outcome when the native reply is lost.
/// </summary>
internal sealed record PcbArtworkReceiptRecord(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("processEpoch")] string ProcessEpoch,
    [property: JsonPropertyName("requestFingerprintSha256")] string RequestFingerprintSha256,
    [property: JsonPropertyName("archiveRelativePath")] string ArchiveRelativePath,
    [property: JsonPropertyName("archiveSha256")] string ArchiveSha256,
    [property: JsonPropertyName("archiveBytes")] long ArchiveBytes,
    [property: JsonPropertyName("archiveReceiptId")] string ArchiveReceiptId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("nativeOutcome")] string NativeOutcome,
    [property: JsonPropertyName("archiveReadbackVerified")] bool ArchiveReadbackVerified,
    [property: JsonPropertyName("isError")] bool IsError,
    [property: JsonPropertyName("resultJson")] string ResultJson,
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("errorMessage")] string ErrorMessage)
{
    internal const int CurrentVersion = 1;
    internal const string Admitted = "admitted";
    internal const string Unknown = "unknown";
    internal const string Terminal = "terminal";

    internal void Validate()
    {
        if (Version != CurrentVersion || !CanonicalGuid(InstanceId)
            || !CanonicalGuid(OperationId) || !CanonicalGuid(ProcessEpoch)
            || !Sha256(RequestFingerprintSha256) || !Sha256(ArchiveSha256)
            || !Sha256(ArchiveReceiptId) || ArchiveBytes < 0
            || !RelativeArchivePath(ArchiveRelativePath)
            || Outcome is not (Admitted or Unknown or Terminal)
            || string.IsNullOrWhiteSpace(NativeOutcome))
            throw Invalid("The PCB artwork receipt identity or archive binding is invalid.");

        if (Outcome == Admitted && (ResultJson.Length != 0 || IsError
            || ErrorCode.Length != 0 || ErrorMessage.Length != 0
            || NativeOutcome != Admitted))
            throw Invalid("An admitted PCB artwork receipt cannot contain a terminal result.");

        if (Outcome == Unknown && (ResultJson.Length != 0 || !IsError
            || string.IsNullOrWhiteSpace(ErrorCode) || string.IsNullOrWhiteSpace(ErrorMessage)
            || NativeOutcome != "indeterminate"))
            throw Invalid("An indeterminate PCB artwork receipt requires an error and no terminal result.");

        if (Outcome == Terminal && (ResultJson.Length == 0 || string.IsNullOrWhiteSpace(NativeOutcome)))
            throw Invalid("A terminal PCB artwork receipt requires a native outcome and result.");

        if (ResultJson.Length != 0)
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(ResultJson);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    throw Invalid("The PCB artwork result must be a JSON object.");
            }
            catch (JsonException error)
            {
                throw Invalid("The PCB artwork result is malformed: " + error.Message);
            }
        }
    }

    internal static AutomationException Invalid(string message) =>
        new("invalid_pcb_artwork_receipt", message);

    internal static bool CanonicalGuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        && value == id.ToString("D");

    internal static bool Sha256(string? value) => value is not null
        && value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    internal static bool RelativeArchivePath(string? value) => !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathFullyQualified(value) && value.IndexOf('\0') < 0
        && value.Split('/', '\\').All(part => part is not ("" or "." or ".."));
}

internal sealed record PcbArtworkReceiptReservation(PcbArtworkReceiptRecord Receipt, bool Created);

/// <summary>Atomic, cross-process MCP state for PCB artwork operations.</summary>
internal sealed class PcbArtworkReceiptStore
{
    private const int MaximumReceiptBytes = 4 * 1024 * 1024;
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    internal PcbArtworkReceiptStore(string stateDirectory)
    {
        if (!Path.IsPathFullyQualified(stateDirectory))
            throw new AutomationException("invalid_pcb_artwork_receipt", "The artwork receipt state directory must be absolute.");
        directory = Path.Combine(Path.GetFullPath(stateDirectory), "pcb-artwork-receipts");
    }

    internal PcbArtworkReceiptReservation Reserve(string instanceId, string processEpoch,
        string operationId, string requestFingerprintSha256, ArtworkSourceArchive.Receipt archive)
    {
        ValidateIdentity(instanceId, processEpoch, operationId, requestFingerprintSha256, archive);
        RequireDirectory(true);
        string path = PathFor(instanceId, operationId);
        using var lease = Acquire(LockFor(instanceId, operationId));
        PcbArtworkReceiptRecord? existing = ReadCurrent(path, instanceId, operationId);
        if (existing is not null)
        {
            VerifyBinding(existing, instanceId, processEpoch, operationId, requestFingerprintSha256, archive);
            return new(existing, false);
        }

        var admitted = NewRecord(instanceId, processEpoch, operationId,
            requestFingerprintSha256, archive, Admitted, Admitted, true, false,
            string.Empty, string.Empty, string.Empty);
        WriteAtomic(path, admitted, overwrite: false);
        return new(admitted, true);
    }

    internal PcbArtworkReceiptRecord? Read(string instanceId, string operationId)
    {
        ValidateKey(instanceId, operationId);
        RequireDirectory(false);
        string path = PathFor(instanceId, operationId);
        try
        {
            return File.Exists(path) ? ReadFile(path, instanceId, operationId) : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw PcbArtworkReceiptRecord.Invalid(error.Message); }
        catch (IOException error) { throw new AutomationException("pcb_artwork_receipt_unavailable", error.Message); }
        catch (UnauthorizedAccessException error) { throw new AutomationException("pcb_artwork_receipt_unavailable", error.Message); }
    }

    internal void WriteTerminal(string instanceId, string processEpoch, string operationId,
        string requestFingerprintSha256, ArtworkSourceArchive.Receipt archive,
        string nativeOutcome, bool archiveReadbackVerified, CallToolResult result)
    {
        if (result.StructuredContent is null)
            throw PcbArtworkReceiptRecord.Invalid("A terminal PCB artwork result must contain structured JSON.");
        ValidateIdentity(instanceId, processEpoch, operationId, requestFingerprintSha256, archive);
        if (string.IsNullOrWhiteSpace(nativeOutcome))
            throw PcbArtworkReceiptRecord.Invalid("A terminal PCB artwork result must identify its native outcome.");
        string resultJson = result.StructuredContent.Value.GetRawText();
        var terminal = NewRecord(instanceId, processEpoch, operationId, requestFingerprintSha256,
            archive, Terminal, nativeOutcome, archiveReadbackVerified, result.IsError ?? false,
            resultJson, string.Empty, string.Empty);
        terminal.Validate();
        WriteTerminalRecord(terminal);
    }

    internal void WriteUnknown(string instanceId, string processEpoch, string operationId,
        string requestFingerprintSha256, ArtworkSourceArchive.Receipt archive,
        string errorCode, string errorMessage)
    {
        ValidateIdentity(instanceId, processEpoch, operationId, requestFingerprintSha256, archive);
        if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(errorMessage))
            throw PcbArtworkReceiptRecord.Invalid("An indeterminate PCB artwork receipt requires an error code and message.");
        var unknown = NewRecord(instanceId, processEpoch, operationId, requestFingerprintSha256,
            archive, Unknown, "indeterminate", false, true, string.Empty, errorCode, errorMessage);
        unknown.Validate();
        WriteTerminalRecord(unknown);
    }

    /// <summary>Drop only a reservation proven not to have reached native.</summary>
    internal void RemoveAdmission(string instanceId, string processEpoch, string operationId,
        string requestFingerprintSha256, ArtworkSourceArchive.Receipt archive)
    {
        ValidateIdentity(instanceId, processEpoch, operationId, requestFingerprintSha256, archive);
        RequireDirectory(false);
        string path = PathFor(instanceId, operationId);
        if (!File.Exists(path)) return;
        using var lease = Acquire(LockFor(instanceId, operationId));
        PcbArtworkReceiptRecord? existing = ReadCurrent(path, instanceId, operationId);
        if (existing is null) return;
        VerifyBinding(existing, instanceId, processEpoch, operationId, requestFingerprintSha256, archive);
        if (existing.Outcome == PcbArtworkReceiptRecord.Admitted) File.Delete(path);
    }

    internal static string Fingerprint(string canonicalRequest) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

    internal static CallToolResult ToResult(PcbArtworkReceiptRecord record)
    {
        record.Validate();
        if (record.Outcome != PcbArtworkReceiptRecord.Terminal)
            throw new AutomationException("operation_outcome_unknown", record.ErrorMessage.Length == 0
                ? "The PCB artwork operation has no terminal native receipt; inspect the board before retrying."
                : record.ErrorMessage);
        JsonElement structured = JsonDocument.Parse(record.ResultJson).RootElement.Clone();
        return new CallToolResult
        {
            IsError = record.IsError,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private void WriteTerminalRecord(PcbArtworkReceiptRecord next)
    {
        RequireDirectory(true);
        string path = PathFor(next.InstanceId, next.OperationId);
        using var lease = Acquire(LockFor(next.InstanceId, next.OperationId));
        PcbArtworkReceiptRecord? existing = ReadCurrent(path, next.InstanceId, next.OperationId);
        if (existing is not null)
        {
            VerifyBinding(existing, next.InstanceId, next.ProcessEpoch, next.OperationId,
                next.RequestFingerprintSha256, Archive(next));
            if (existing.Outcome == PcbArtworkReceiptRecord.Terminal)
            {
                if (Equivalent(existing, next)) return;
                throw new AutomationException("pcb_artwork_receipt_conflict",
                    "The PCB artwork operation already has a different terminal receipt.");
            }
            if (existing.Outcome == PcbArtworkReceiptRecord.Unknown
                && next.Outcome != PcbArtworkReceiptRecord.Terminal)
                return;
        }
        WriteAtomic(path, next, overwrite: existing is not null);
    }

    private PcbArtworkReceiptRecord? ReadCurrent(string path, string instanceId, string operationId) =>
        File.Exists(path) ? ReadFile(path, instanceId, operationId) : null;

    private PcbArtworkReceiptRecord ReadFile(string path, string instanceId, string operationId)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw PcbArtworkReceiptRecord.Invalid("PCB artwork receipts require ordinary files.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumReceiptBytes)
            throw PcbArtworkReceiptRecord.Invalid("The PCB artwork receipt is empty or too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        ValidateObject(parsed.RootElement);
        PcbArtworkReceiptRecord result = JsonSerializer.Deserialize<PcbArtworkReceiptRecord>(bytes, Json)
            ?? throw PcbArtworkReceiptRecord.Invalid("The PCB artwork receipt is empty.");
        result.Validate();
        if (result.InstanceId != instanceId || result.OperationId != operationId)
            throw PcbArtworkReceiptRecord.Invalid("The receipt filename and operation identity differ.");
        return result;
    }

    private static void VerifyBinding(PcbArtworkReceiptRecord record, string instanceId,
        string processEpoch, string operationId, string fingerprint,
        ArtworkSourceArchive.Receipt archive)
    {
        if (record.InstanceId != instanceId || record.OperationId != operationId
            || record.ProcessEpoch != processEpoch || record.RequestFingerprintSha256 != fingerprint
            || record.ArchiveRelativePath != archive.RelativePath
            || record.ArchiveSha256 != archive.Sha256 || record.ArchiveBytes != archive.Bytes
            || record.ArchiveReceiptId != archive.ReceiptId)
            throw new AutomationException("operation_reused",
                "The PCB artwork operation UUID was already used for another process epoch, request or source archive.");
    }

    private static bool Equivalent(PcbArtworkReceiptRecord first, PcbArtworkReceiptRecord second) =>
        first with { } == second;

    private static PcbArtworkReceiptRecord NewRecord(string instanceId, string processEpoch,
        string operationId, string fingerprint, ArtworkSourceArchive.Receipt archive,
        string outcome, string nativeOutcome, bool archiveReadbackVerified, bool isError,
        string resultJson, string errorCode, string errorMessage) => new(
            PcbArtworkReceiptRecord.CurrentVersion, instanceId, operationId, processEpoch,
            fingerprint, archive.RelativePath, archive.Sha256, archive.Bytes, archive.ReceiptId,
            outcome, nativeOutcome, archiveReadbackVerified, isError, resultJson, errorCode, errorMessage);

    private static ArtworkSourceArchive.Receipt Archive(PcbArtworkReceiptRecord record) =>
        new(record.ArchiveRelativePath, record.ArchiveSha256, record.ArchiveBytes,
            Persisted: true, ExactBytesVerified: true, AtomicWrite: false);

    private void WriteAtomic(string path, PcbArtworkReceiptRecord record, bool overwrite)
    {
        record.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > MaximumReceiptBytes)
            throw PcbArtworkReceiptRecord.Invalid("The PCB artwork receipt is too large.");
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
        catch (IOException error) { throw new AutomationException("pcb_artwork_receipt_persistence_failed", error.Message); }
        catch (UnauthorizedAccessException error) { throw new AutomationException("pcb_artwork_receipt_persistence_failed", error.Message); }
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
        catch (IOException error) { throw new AutomationException("pcb_artwork_receipt_busy", error.Message); }
        catch (UnauthorizedAccessException error) { throw new AutomationException("pcb_artwork_receipt_busy", error.Message); }
    }

    private void RequireDirectory(bool create)
    {
        if (create) Directory.CreateDirectory(directory);
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw PcbArtworkReceiptRecord.Invalid("Use the existing owned PCB artwork receipt directory.");
    }

    private string PathFor(string instanceId, string operationId) => Path.Combine(directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId))) + "-" + operationId + ".json");

    private string LockFor(string instanceId, string operationId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".lock";

    private static void ValidateKey(string instanceId, string operationId)
    {
        if (!PcbArtworkReceiptRecord.CanonicalGuid(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId must be a canonical UUID.");
        if (!PcbArtworkReceiptRecord.CanonicalGuid(operationId))
            throw new AutomationException("invalid_operation_id", "operationId must be a canonical UUID.");
    }

    private static void ValidateIdentity(string instanceId, string processEpoch, string operationId,
        string fingerprint, ArtworkSourceArchive.Receipt archive)
    {
        ValidateKey(instanceId, operationId);
        if (!PcbArtworkReceiptRecord.CanonicalGuid(processEpoch))
            throw new AutomationException("invalid_process_epoch", "processEpoch must be a canonical UUID.");
        if (!PcbArtworkReceiptRecord.Sha256(fingerprint))
            throw PcbArtworkReceiptRecord.Invalid("The PCB artwork request fingerprint is invalid.");
        if (archive is null || !archive.Persisted || !archive.ExactBytesVerified
            || !PcbArtworkReceiptRecord.RelativeArchivePath(archive.RelativePath)
            || !PcbArtworkReceiptRecord.Sha256(archive.Sha256)
            || !PcbArtworkReceiptRecord.Sha256(archive.ReceiptId) || archive.Bytes < 0)
            throw PcbArtworkReceiptRecord.Invalid("The PCB artwork archive binding is incomplete.");
    }

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw PcbArtworkReceiptRecord.Invalid("PCB artwork receipts require an object.");
        var stack = new Stack<JsonElement>();
        stack.Push(element);
        while (stack.TryPop(out JsonElement current))
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in current.EnumerateObject())
                    if (!names.Add(property.Name))
                        throw PcbArtworkReceiptRecord.Invalid("Duplicate PCB artwork receipt properties.");
                    else stack.Push(property.Value);
            }
            else if (current.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in current.EnumerateArray()) stack.Push(item);
        }
    }
}
