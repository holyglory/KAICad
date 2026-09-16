using System.Text.Json.Serialization;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Historical result of one exact synchronization request. This is not
/// a live observation, and replaying it never edits either representation.</summary>
public sealed record DesignSynchronizationReceipt(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] string DesignPath,
    [property: JsonRequired] string RequestedRecoveryRevisionToken,
    [property: JsonRequired] string DesignFileSha256,
    [property: JsonRequired] string NativeProcessEpoch,
    [property: JsonRequired] string NativeDocumentEpoch,
    [property: JsonRequired] ulong NativeRevisionSequence,
    [property: JsonRequired] bool NativeMutationCommitted,
    [property: JsonRequired] bool NativeFilesSaved,
    [property: JsonRequired] byte[]? NativeReceipt,
    [property: JsonRequired] string? PreviousXmlPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousXmlSha256 = null)
{
    internal void Validate()
    {
        if (Version is not (1 or 2) || OperationId == Guid.Empty || InstanceId == Guid.Empty
            || !Path.IsPathFullyQualified(DesignPath) || Path.GetFullPath(DesignPath) != DesignPath
            || !Digest(RequestedRecoveryRevisionToken) || !Digest(DesignFileSha256)
            || !Uuid(NativeProcessEpoch) || !Uuid(NativeDocumentEpoch)
            || NativeMutationCommitted != (NativeReceipt is not null)
            || (PreviousXmlPath is not null && !Path.IsPathFullyQualified(PreviousXmlPath)))
            throw Invalid("Synchronization receipt has incomplete identity or result fields.");
        if ((Version == 1 && PreviousXmlSha256 is not null)
            || (Version == 2 && ((PreviousXmlPath is null) != (PreviousXmlSha256 is null)
                || (PreviousXmlSha256 is not null && !Digest(PreviousXmlSha256)))))
            throw Invalid("Versioned retained XML requires an exact prior-content digest paired with its path.");
        if (NativeReceipt is not null)
        {
            try
            {
                var receipt = CheckedSchematicBatchReceipt.Parser.ParseFrom(NativeReceipt);
                if (receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted || !receipt.ExpectedRequestVerified
                    || receipt.ProcessEpoch != NativeProcessEpoch || receipt.Result?.Revision?.Epoch != NativeDocumentEpoch
                    || receipt.Result.Revision.Sequence > NativeRevisionSequence || !Uuid(receipt.OperationId))
                    throw Invalid("The retained native result does not match the completed synchronization.");
            }
            catch (InvalidProtocolBufferException error) { throw Invalid(error.Message); }
        }
    }

    internal void RequireRequest(Guid instance, string path, string token)
    {
        if (InstanceId != instance || DesignPath != path || RequestedRecoveryRevisionToken != token)
            throw new AutomationException("sync_operation_id_conflict", "This operation ID already belongs to a different synchronization request.");
    }

    internal SchematicSynchronizationExecution Result(string currentRecoveryToken, bool replayed)
    {
        var retained = RetainedXmlHistory.Inspect(this);
        return new(currentRecoveryToken, DesignFileSha256, new(NativeDocumentEpoch, NativeRevisionSequence),
            NativeMutationCommitted, NativeFilesSaved, true,
            NativeReceipt is null ? null : CheckedSchematicBatchReceipt.Parser.ParseFrom(NativeReceipt),
            OperationId, retained.Path, replayed, retained);
    }

    private static bool Digest(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static bool Uuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
    private static AutomationException Invalid(string message) => new("invalid_sync_receipt", message);
}
