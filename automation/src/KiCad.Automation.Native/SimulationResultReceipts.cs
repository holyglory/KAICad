using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;

namespace KiCad.Automation.Native;

public enum SimulationOperationStage { Prepared, Dispatched, Accepted, Terminal }

public sealed record SimulationOperationReceipt(int Version, string InstanceId, string ProcessEpoch,
    string OperationId, string DocumentPayload, string? RequestedDeckSha256, string DeckSource,
    bool RequestFingerprintKnown, SimulationOperationStage Stage, string? JobId)
{
    [JsonIgnore] public DocumentSpecifier Document => DocumentSpecifier.Parser.ParseFrom(Convert.FromBase64String(DocumentPayload));

    public static SimulationOperationReceipt Prepare(string instanceId, string processEpoch, DocumentSpecifier document,
        Guid operationId, string? deckSha256, string source) => new(1, instanceId, processEpoch, operationId.ToString("D"),
            Convert.ToBase64String(document.ToByteArray()), deckSha256, source, true, SimulationOperationStage.Prepared, null);

    public static SimulationOperationReceipt Observe(string instanceId, SimulationJobState result) => new(1,
        instanceId, result.ProcessEpoch, result.OperationId, Convert.ToBase64String(result.Document.ToByteArray()),
        null, "native-job-observed", false, SimulationOperationStage.Accepted, result.JobId);
}

public sealed record SimulationTerminalReceipt(int Version, SimulationOperationReceipt Operation,
    string JobPayload, string DependencyPayload, string Sha256)
{
    [JsonIgnore] public SimulationJobState Job => SimulationJobState.Parser.ParseFrom(Convert.FromBase64String(JobPayload));
    [JsonIgnore] public NativeCapabilitySimulationDependencies Dependencies =>
        NativeCapabilitySimulationDependencies.Parser.ParseFrom(Convert.FromBase64String(DependencyPayload));

    public static SimulationTerminalReceipt Capture(SimulationOperationReceipt operation, SimulationJobState job,
        NativeCapabilitySimulationDependencies dependencies)
    {
        var result = new SimulationTerminalReceipt(1, operation with { Stage = SimulationOperationStage.Terminal },
            Convert.ToBase64String(job.ToByteArray()), Convert.ToBase64String(dependencies.ToByteArray()), "");
        return result with { Sha256 = SimulationResultReceipts.Digest(result) };
    }
}

/// <summary>Private same-host receipts; these never resume a simulator or adopt a replacement process.</summary>
public sealed class SimulationResultReceipts(string stateDirectory)
{
    internal const int MaximumReceiptBytes = 32 * 1024 * 1024;
    private const int MaximumDocumentCharacters = 1024 * 1024;
    private readonly string directory = Path.Combine(Path.GetFullPath(stateDirectory), "simulation-results");
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record CurrentOperation(int Version, string InstanceId, string ProcessEpoch, string OperationId);

    public IDisposable AcquireSession(string instanceId)
    {
        RequireIdentity(instanceId, "instance"); RequireDirectory(true);
        // SA-02/04: coordinate the existing private local state, without adding
        // another account boundary. Keep lock files in place across releases.
        return Lock(InstanceKey(instanceId) + ".session.lock");
    }

    public SimulationOperationReceipt? ReadOperation(string instanceId, Guid operationId)
    {
        RequireIdentity(instanceId, "instance"); RequireId(operationId);
        var result = Read<SimulationOperationReceipt>(OperationPath(instanceId, operationId));
        if (result is null) return null;
        Validate(result);
        if (result.InstanceId != instanceId || result.OperationId != operationId.ToString("D"))
            throw Invalid("Simulation receipt filename and operation identity differ.");
        return result;
    }

    public SimulationOperationReceipt? ReadCurrent(string instanceId, string processEpoch)
    {
        var current = Read<CurrentOperation>(CurrentPath(instanceId, processEpoch));
        if (current is null) return null;
        if (current.Version != 1 || current.InstanceId != instanceId || current.ProcessEpoch != processEpoch
            || !CanonicalId(current.OperationId)) throw Invalid("The current simulation operation identity is invalid.");
        var operation = ReadOperation(instanceId, Guid.Parse(current.OperationId));
        if (operation is null || operation.ProcessEpoch != processEpoch)
            throw Invalid("The current simulation operation has no matching durable receipt.");
        return operation;
    }

    public void MakeCurrent(SimulationOperationReceipt operation)
    {
        Validate(operation);
        var previous = ReadCurrent(operation.InstanceId, operation.ProcessEpoch);
        if (previous is not null && previous.OperationId != operation.OperationId
            && previous.Stage != SimulationOperationStage.Prepared
            && ReadTerminal(previous) is null)
            throw Unresolved(previous.OperationId);
        WriteAtomic(CurrentPath(operation.InstanceId, operation.ProcessEpoch),
            JsonSerializer.SerializeToUtf8Bytes(new CurrentOperation(1, operation.InstanceId,
                operation.ProcessEpoch, operation.OperationId), Json));
    }

    public void WriteOperation(SimulationOperationReceipt operation)
    {
        Validate(operation); RequireDirectory(true);
        string path = OperationPath(operation.InstanceId, Guid.Parse(operation.OperationId));
        using var lease = Lock(Path.GetFileName(path) + ".receipt.lock");
        var previous = ReadOperation(operation.InstanceId, Guid.Parse(operation.OperationId));
        if (previous is not null)
        {
            if (!SameFingerprint(previous, operation) || previous.JobId is not null && previous.JobId != operation.JobId
                || (int)operation.Stage < (int)previous.Stage || (int)operation.Stage > (int)previous.Stage + 1)
                throw Conflict("The simulation operation fingerprint, job identity or recorded stage cannot be rewritten.");
            if (previous == operation) return;
            if (previous.Stage == operation.Stage)
                throw Conflict("A recorded simulation operation stage cannot change.");
        }
        else if (operation.Stage != SimulationOperationStage.Prepared
            && !(operation.Stage == SimulationOperationStage.Accepted && !operation.RequestFingerprintKnown))
            throw Conflict("Record the simulation fingerprint before dispatch.");
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(operation, Json));
    }

    public SimulationTerminalReceipt? ReadTerminal(string instanceId, string processEpoch, Guid jobId)
    {
        RequireIdentity(instanceId, "instance"); RequireIdentity(processEpoch, "process epoch"); RequireId(jobId);
        var result = Read<SimulationTerminalReceipt>(ResultPath(instanceId, processEpoch, jobId));
        if (result is null) return null;
        Validate(result);
        if (result.Operation.InstanceId != instanceId || result.Operation.ProcessEpoch != processEpoch
            || result.Operation.JobId != jobId.ToString("D")) throw Invalid("Simulation archive filename and original identity differ.");
        return result;
    }

    public SimulationTerminalReceipt? ReadTerminal(SimulationOperationReceipt operation)
    {
        Validate(operation);
        if (operation.JobId is null) return null;
        var result = ReadTerminal(operation.InstanceId, operation.ProcessEpoch, Guid.Parse(operation.JobId));
        if (result is not null && (!SameFingerprint(operation, result.Operation) || operation.JobId != result.Operation.JobId))
            throw Invalid("The archived result does not match its original operation fingerprint.");
        return result;
    }

    public void WriteTerminal(SimulationTerminalReceipt receipt)
    {
        Validate(receipt); RequireDirectory(true);
        var operation = ReadOperation(receipt.Operation.InstanceId, Guid.Parse(receipt.Operation.OperationId));
        if (operation is null || operation.JobId != receipt.Operation.JobId || !SameFingerprint(operation, receipt.Operation))
            throw Conflict("A terminal result needs its matching accepted operation receipt.");
        string path = ResultPath(operation.InstanceId, operation.ProcessEpoch, Guid.Parse(operation.JobId!));
        using var lease = Lock(Path.GetFileName(path) + ".receipt.lock");
        var previous = ReadTerminal(operation);
        if (previous is not null && previous != receipt)
            throw Conflict("An archived terminal simulation result and its provenance are immutable.");
        if (previous is null) WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
        // Commit the complete result first. If this bookkeeping update is
        // interrupted, ReadTerminal still recovers the exact durable receipt.
        WriteOperation(receipt.Operation);
    }

    public bool HasEarlierEpoch(string instanceId, string processEpoch, Guid jobId)
    {
        RequireIdentity(instanceId, "instance"); RequireIdentity(processEpoch, "process epoch"); RequireId(jobId);
        if (!Directory.Exists(directory)) return false;
        RequireDirectory(false);
        foreach (string path in Directory.EnumerateFiles(directory, InstanceKey(instanceId) + "-*-job-" + jobId.ToString("N") + ".json"))
        {
            var result = Read<SimulationTerminalReceipt>(path);
            if (result is null) continue;
            Validate(result);
            if (result.Operation.InstanceId == instanceId && result.Operation.ProcessEpoch != processEpoch
                && result.Operation.JobId == jobId.ToString("D")) return true;
        }
        return false;
    }

    public static bool SameFingerprint(SimulationOperationReceipt left, SimulationOperationReceipt right) =>
        left with { Stage = right.Stage, JobId = right.JobId } == right;

    internal static string Digest(SimulationTerminalReceipt receipt) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { receipt.Version, receipt.Operation, receipt.JobPayload, receipt.DependencyPayload }, Json)));

    public static AutomationException Unresolved(string operationId) => new("simulation_operation_unresolved",
        "Simulation operation " + operationId + " has no confirmed terminal receipt. Inspect its recorded job when available; it will not be dispatched again automatically.");

    private static void Validate(SimulationOperationReceipt operation)
    {
        if (operation.Version != 1 || string.IsNullOrWhiteSpace(operation.InstanceId) || string.IsNullOrWhiteSpace(operation.ProcessEpoch)
            || !CanonicalId(operation.OperationId) || !Enum.IsDefined(operation.Stage)) throw Invalid("Invalid simulation operation identity or version.");
        if (operation.DocumentPayload is null || operation.DocumentPayload.Length > MaximumDocumentCharacters)
            throw Invalid("The simulation document payload is missing or too large.");
        try
        {
            var document = operation.Document;
            if ((int)document.Type != 1 || document.Project is null || document.SheetPath is null
                || document.SheetPath.Path.Count == 0) throw Invalid("The simulation receipt lacks its schematic document.");
        }
        catch (Exception error) when (error is FormatException or InvalidProtocolBufferException or ArgumentNullException)
        { throw Invalid("Invalid simulation document payload: " + error.Message); }
        bool jobRequired = operation.Stage is SimulationOperationStage.Accepted or SimulationOperationStage.Terminal;
        if (jobRequired ? !CanonicalId(operation.JobId) : operation.JobId is not null)
            throw Invalid("The simulation operation stage and job identity differ.");
        bool validProvenance = operation.RequestFingerprintKnown
            ? operation.DeckSource == "inline-caller-deck" ? Hash(operation.RequestedDeckSha256)
                : operation.DeckSource == "native-schematic-generated" && operation.RequestedDeckSha256 is null
            : operation.DeckSource == "native-job-observed" && operation.RequestedDeckSha256 is null && jobRequired;
        if (!validProvenance)
            throw Invalid("The simulation request provenance is invalid.");
    }

    private static void Validate(SimulationTerminalReceipt receipt)
    {
        if (receipt.Version != 1 || receipt.Operation is null) throw Invalid("Invalid simulation archive version or operation.");
        if (receipt.JobPayload is null || receipt.DependencyPayload is null
            || receipt.JobPayload.Length > MaximumReceiptBytes || receipt.DependencyPayload.Length > MaximumReceiptBytes)
            throw Invalid("Simulation archive payloads are missing or too large.");
        // Check the immutable content digest before allocating protobuf objects.
        if (receipt.Operation.Stage != SimulationOperationStage.Terminal || !Hash(receipt.Sha256) || Digest(receipt) != receipt.Sha256)
            throw Invalid("The terminal simulation archive failed its content check.");
        Validate(receipt.Operation);
        try
        {
            var job = receipt.Job; var dependencies = receipt.Dependencies; var operation = receipt.Operation;
            if (job.Status is not (SimulationJobStatus.SimjsCompleted or SimulationJobStatus.SimjsCancelled or SimulationJobStatus.SimjsFailed)
                || !job.WorkerFinished || job.JobId != operation.JobId || job.OperationId != operation.OperationId
                || job.ProcessEpoch != operation.ProcessEpoch || job.Document is null || !job.Document.Equals(operation.Document)
                || dependencies.JobId != job.JobId || dependencies.OperationId != job.OperationId
                || dependencies.ProcessEpoch != job.ProcessEpoch || dependencies.Document is null || !dependencies.Document.Equals(job.Document))
                throw Invalid("The archive is not a terminal native result for its original identity.");
        }
        catch (Exception error) when (error is FormatException or InvalidProtocolBufferException or ArgumentNullException)
        { throw Invalid("Invalid simulation archive payload: " + error.Message); }
    }

    private T? Read<T>(string path) where T : class
    {
        try
        {
            RequireDirectory(false);
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw Invalid("Simulation receipts require ordinary files.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaximumReceiptBytes)
                throw Invalid("The simulation receipt is empty or exceeds the archive size limit.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            using var parsed = JsonDocument.Parse(bytes);
            ValidateProperties(parsed.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid("The simulation receipt is empty.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw Invalid("Invalid simulation receipt JSON: " + error.Message); }
    }

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("Simulation receipts require an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw Invalid("Duplicate simulation receipt properties.");
            if (property.Value.ValueKind == JsonValueKind.Object) ValidateProperties(property.Value);
        }
    }

    private void WriteAtomic(string path, byte[] bytes)
    {
        if (bytes.Length > MaximumReceiptBytes)
            throw Invalid("The simulation receipt exceeds the archive size limit; no result was truncated.");
        RequireDirectory(true);
        if (File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw Invalid("Simulation receipts require ordinary files.");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private FileStream Lock(string filename)
    {
        string path = Path.Combine(directory, filename);
        if (File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw Invalid("Simulation receipt locks require ordinary files.");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new AutomationException("simulation_receipt_busy", error.Message); }
    }

    private void RequireDirectory(bool create)
    {
        if (create) Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw Invalid("Use the existing owned simulation receipt directory.");
    }

    private string OperationPath(string instanceId, Guid operationId) => Path.Combine(directory,
        InstanceKey(instanceId) + "-operation-" + operationId.ToString("N") + ".json");
    private string CurrentPath(string instanceId, string epoch) => Path.Combine(directory, SessionKey(instanceId, epoch) + "-current.json");
    private string ResultPath(string instanceId, string epoch, Guid jobId) => Path.Combine(directory,
        SessionKey(instanceId, epoch) + "-job-" + jobId.ToString("N") + ".json");
    private static string InstanceKey(string instanceId) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(instanceId)));
    private static string SessionKey(string instanceId, string epoch) => InstanceKey(instanceId) + "-" +
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(epoch)));
    private static bool CanonicalId(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void RequireId(Guid value) { if (value == Guid.Empty) throw Invalid("Provide a nonempty simulation identity."); }
    private static void RequireIdentity(string value, string kind) { if (string.IsNullOrWhiteSpace(value)) throw Invalid("Provide the original simulation " + kind + "."); }
    private static AutomationException Invalid(string message) => new("invalid_simulation_receipt", message);
    private static AutomationException Conflict(string message) => new("simulation_receipt_conflict", message);
}
