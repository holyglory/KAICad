using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Explicitly adopt a checked observation from a reloaded document.
/// Never carries pending requests into a new session or advances the baseline.</summary>
public static class DesignRecoveryReattachment
{
    public static async Task<StoredDesignRecovery> ReattachAsync(DesignRecoveryStore store, NativeClient client,
        string expectedRecoveryRevision, string expectedDocumentEpoch, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(expectedDocumentEpoch, "D", out var epoch) || epoch == Guid.Empty
            || expectedDocumentEpoch != epoch.ToString("D"))
            throw Error("invalid_document_epoch", "Provide the exact newly observed native document epoch.");
        var saved = store.Read() ?? throw Error("missing_design_recovery", "Initialize design recovery before reattachment.");
        if (saved.RevisionToken != expectedRecoveryRevision)
            throw Error("design_recovery_changed", "Reload the current recovery record before reattachment.");
        if (saved.State.HasPendingWork)
            throw Error("pending_recovery_requires_reconciliation", "Resolve the old session's pending operation before adopting another session.");
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != saved.State.InstanceId.ToString("D"))
            throw Error("recovery_instance_mismatch", "Reattach only the native instance recorded by this design.");
        var document = saved.State.Baseline.Schematic.Document;
        var observed = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
            { Document = document.Clone(), ProcessEpoch = client.Epoch }, token);
        CheckedSchematicContract.ValidateObservation(observed, document, client.Epoch);
        var revision = observed.State.Revision;
        if (revision.Epoch != expectedDocumentEpoch
            || (revision.Epoch == saved.State.NativeRevision.Epoch && revision.Sequence < saved.State.NativeRevision.Sequence))
            throw Error("invalid_recovery_revision", "The native document session changed again or its revision regressed.");
        // Validate the captured representation; keep coverage limitations explicit.
        _ = SchematicDataXml.Read(SchematicDataXml.Write(observed.Electrical.Hierarchy.Data));
        token.ThrowIfCancellationRequested();
        return store.Save(saved.State with
        {
            Observed = observed.Electrical.Hierarchy.Data.Clone(), ObservedElectrical = observed.Electrical.Clone(),
            NativeRevision = new(revision.Epoch, revision.Sequence), TrackingComplete = observed.Electrical.Hierarchy.TrackingComplete,
            HierarchyResolution = null, OwnershipResolution = null
        }, expectedRecoveryRevision);
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}
