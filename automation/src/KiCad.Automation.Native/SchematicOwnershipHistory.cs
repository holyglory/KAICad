using System.Text;
using System.Text.Json;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal sealed record SchematicOwnershipHistory(DesignSynchronizationReceipt Receipt, SchematicDesign Design, bool Latest);

internal static class SchematicOwnershipHistoryReader
{
    /// <summary>The design was never synchronized, so no retained XML can know an earlier owner of anything KiCad shows.</summary>
    internal const string NeverSynchronized = "missing_native_ownership_history";
    /// <summary>Synchronizations happened, but no retained XML is content-verified for this circuit and document: an
    /// earlier owner may exist that cannot be read, so nothing may be treated as new.</summary>
    internal const string Unverified = "unverified_native_ownership_history";

    internal static async Task<IReadOnlyList<SchematicOwnershipHistory>> ReadAsync(DesignRecoveryStore store,
        DesignRecoveryState state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var latest = state.LastSynchronization
            ?? throw new AutomationException(NeverSynchronized, "No completed synchronization identifies this design's retained history.");
        var receipts = new DesignSynchronizationReceipts(store.StatePath).ReadAll().ToDictionary(r => r.OperationId);
        if (receipts.TryGetValue(latest.OperationId, out var copy)
            && JsonSerializer.Serialize(copy) != JsonSerializer.Serialize(latest))
            throw new AutomationException("sync_receipt_conflict", "The current and archived synchronization receipts disagree.");
        receipts[latest.OperationId] = latest;
        var result = new List<SchematicOwnershipHistory>();
        foreach (var receipt in receipts.Values.Where(r => r.InstanceId == state.InstanceId && r.DesignPath == latest.DesignPath
                     && r.Version == 2 && r.PreviousXmlPath is not null).OrderBy(r => r.OperationId))
        {
            token.ThrowIfCancellationRequested();
            if (receipt.NativeDocumentEpoch == state.NativeRevision.Epoch && receipt.NativeRevisionSequence > state.NativeRevision.Sequence)
                throw new AutomationException("native_ownership_history_ahead", "Retained history follows this captured native revision; refresh the observation before restoring owners.");
            byte[] bytes = await RetainedXmlHistory.ReadVerifiedAsync(receipt, token);
            var design = SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(bytes), state.KnowledgeLibraries);
            if (design.Engineering.Circuit.Id == state.Baseline.Engineering.Circuit.Id
                && Equals(design.Schematic.Document, state.Baseline.Schematic.Document))
                result.Add(new(receipt, design, receipt.OperationId == latest.OperationId));
        }
        if (result.Count == 0)
            throw new AutomationException(Unverified, "No content-verified retained XML belongs to this circuit; legacy paths alone cannot restore identities.");
        return result;
    }
}
