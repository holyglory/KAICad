using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum BlockProposalOperationKind { Publish, Select }
public enum BlockProposalOperationStage { Prepared, Replacing, Published }

public sealed record BlockProposalPublicationReceipt(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid ProposalId,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] BlockProposalOperationKind Kind,
    [property: JsonRequired] string DesignPath,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] string BeforeSha256,
    [property: JsonRequired] string AfterSha256,
    [property: JsonRequired] BlockProposalOperationStage Stage,
    [property: JsonRequired] string? StagedPath,
    [property: JsonRequired] string? RetainedPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ConfirmedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? DocumentId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CandidateXml = null)
{
    internal void Validate()
    {
        if (Version is not (1 or 2) || ProposalId == Guid.Empty || OperationId == Guid.Empty || !Enum.IsDefined(Kind)
            || !Enum.IsDefined(Stage) || !Canonical(DesignPath) || !Digest(RequestSha256)
            || !Digest(BeforeSha256) || !Digest(AfterSha256) || BeforeSha256 == AfterSha256)
            throw Invalid("Proposal receipt identity or hashes are incomplete.");
        if (Version == 1 ? DocumentId is not null || CandidateXml is not null
            : DocumentId is null || DocumentId == Guid.Empty || CandidateXml is null)
            throw Invalid("Version 2 retains the exact candidate and diagram identity for recovery.");
        if (Version == 2)
        {
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CandidateXml!))) != AfterSha256)
                throw Invalid("The retained candidate bytes do not match the operation postimage.");
            var graph = RecursiveBlockGraphXml.Read(CandidateXml!);
            if (graph.DocumentId != DocumentId) throw Invalid("The retained candidate belongs to another diagram.");
            _ = graph.Proposal(ProposalId);
        }
        if (Stage == BlockProposalOperationStage.Prepared)
        {
            if (StagedPath is not null || RetainedPath is not null || ConfirmedAt is not null) throw Invalid("Prepared proposal receipts cannot claim replacement paths or confirmation.");
        }
        else if (!Canonical(StagedPath) || !Canonical(RetainedPath) || !StagedPath!.StartsWith(DesignPath + ".sync-", StringComparison.Ordinal)
            || Path.GetDirectoryName(StagedPath) != Path.GetDirectoryName(DesignPath)
            || (RetainedPath != StagedPath && RetainedPath != StagedPath + ".previous")
            || (Stage == BlockProposalOperationStage.Published
                ? ConfirmedAt is null || ConfirmedAt == DateTimeOffset.MinValue || ConfirmedAt.Value.Offset != TimeSpan.Zero
                : ConfirmedAt is not null)) throw Invalid("Proposal receipt paths and phase confirmation do not match.");
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static bool Canonical(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path; }
        catch (ArgumentException) { return false; }
    }
    internal static AutomationException Invalid(string message) => new("invalid_block_proposal_receipt", message);
}

/// <summary>Durable proposal-operation phases. It records evidence about XML
/// publication, not proposal validity, native activation or completion readiness.</summary>
public sealed class BlockProposalReceipts(string stateDirectory)
{
    private readonly string directory = Path.Combine(Path.GetFullPath(stateDirectory), "block-proposal-operations");
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public BlockProposalPublicationReceipt? Read(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new AutomationException("invalid_operation_id", "A proposal operation needs a nonempty operation identity.");
        try
        {
            RequireDirectory(false);
            string path = Path.Combine(directory, operationId.ToString("N") + ".json");
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new AutomationException("invalid_block_proposal_receipt", "Proposal receipts must be ordinary files.");
            byte[] bytes = File.ReadAllBytes(path);
            using var parsed = JsonDocument.Parse(bytes);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || parsed.RootElement.EnumerateObject().Select(p => p.Name)
                .Distinct(StringComparer.Ordinal).Count() != parsed.RootElement.EnumerateObject().Count())
                throw BlockProposalPublicationReceipt.Invalid("Duplicate or invalid receipt properties.");
            var result = JsonSerializer.Deserialize<BlockProposalPublicationReceipt>(bytes, Json)
                ?? throw new AutomationException("invalid_block_proposal_receipt", "The proposal receipt is empty.");
            result.Validate(); if (result.OperationId != operationId) throw new AutomationException("invalid_block_proposal_receipt", "Receipt filename and operation differ.");
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw new AutomationException("invalid_block_proposal_receipt", error.Message); }
    }

    public void Write(BlockProposalPublicationReceipt receipt)
    {
        receipt.Validate(); RequireDirectory(true);
        using var lease = Lock(receipt.OperationId, ".receipt.lock");
        string path = Path.Combine(directory, receipt.OperationId.ToString("N") + ".json");
        var previous = Read(receipt.OperationId);
        if (previous is not null)
        {
            if (previous.Version != receipt.Version || previous.DocumentId != receipt.DocumentId || previous.CandidateXml != receipt.CandidateXml
                || previous.ProposalId != receipt.ProposalId || previous.Kind != receipt.Kind || previous.DesignPath != receipt.DesignPath
                || previous.RequestSha256 != receipt.RequestSha256 || previous.BeforeSha256 != receipt.BeforeSha256 || previous.AfterSha256 != receipt.AfterSha256
                || (int)receipt.Stage < (int)previous.Stage
                || (int)receipt.Stage != (int)previous.Stage + 1 && receipt.Stage != previous.Stage)
                throw new AutomationException("block_proposal_receipt_conflict", "Proposal receipt identity, payload or phase cannot be rewritten.");
            if (receipt.Stage != previous.Stage && previous.Stage == BlockProposalOperationStage.Replacing
                && (receipt.StagedPath != previous.StagedPath || receipt.RetainedPath != previous.RetainedPath))
                throw new AutomationException("block_proposal_receipt_conflict", "Proposal replacement paths cannot change during recovery.");
            if (receipt.Stage == previous.Stage && JsonSerializer.SerializeToUtf8Bytes(receipt, Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(previous, Json))) return;
            if (receipt.Stage == previous.Stage)
                throw new AutomationException("block_proposal_receipt_conflict", "A recorded phase cannot change its paths or acknowledgement.");
        }
        else if (receipt.Stage != BlockProposalOperationStage.Prepared)
            throw new AutomationException("block_proposal_receipt_conflict", "Record the prepared proposal operation before replacement.");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(receipt, Json)); output.Flush(flushToDisk: true); }
            if (previous is null) File.Move(temporary, path, overwrite: false); else File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IDisposable AcquireOperation(Guid operationId) => Lock(operationId, ".operation.lock");

    private FileStream Lock(Guid operationId, string suffix)
    {
        if (operationId == Guid.Empty) throw new AutomationException("invalid_operation_id", "Identify the exact proposal operation.");
        RequireDirectory(true);
        string path = Path.Combine(directory, operationId.ToString("N") + suffix);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw BlockProposalPublicationReceipt.Invalid("Receipt locks require ordinary paths.");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new AutomationException("block_proposal_receipt_busy", error.Message); }
    }

    private void RequireDirectory(bool create)
    {
        if (create) Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw BlockProposalPublicationReceipt.Invalid("Use the owned ordinary receipt directory.");
    }
}
