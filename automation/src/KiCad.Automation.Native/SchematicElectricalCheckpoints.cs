using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

internal static class SchematicElectricalCheckpoints
{
    internal static (SchematicElectricalState Baseline, SchematicElectricalState Observed) Require(DesignRecoveryState state)
    {
        if (state.OriginId == Guid.Empty || state.InstanceId == Guid.Empty)
            throw Error("invalid_electrical_recovery", "An exact recovery origin and instance are required.");
        if (state.HasPendingWork)
            throw Error("pending_recovery_requires_reconciliation", "Reconcile the exact pending operation first.");
        var baseline = state.BaselineElectrical ?? throw Error("missing_electrical_baseline", "Initialize the matched electrical baseline first.");
        var observed = state.ObservedElectrical ?? throw Error("missing_electrical_observation", "Capture current matching electrical state first.");
        if (baseline.Hierarchy?.Revision is null || observed.Hierarchy?.Revision is null
            || string.IsNullOrWhiteSpace(baseline.Hierarchy.Revision.Epoch)
            || string.IsNullOrWhiteSpace(observed.Hierarchy.Revision.Epoch)
            || !Equals(baseline.Hierarchy.Data, state.Baseline.Schematic) || !Equals(observed.Hierarchy.Data, state.Observed)
            || observed.Hierarchy.Revision.Epoch != state.NativeRevision.Epoch
            || observed.Hierarchy.Revision.Sequence != state.NativeRevision.Sequence
            || observed.Hierarchy.TrackingComplete != state.TrackingComplete)
            throw Error("invalid_electrical_recovery", "Electrical checkpoints must match their exact recovery owners and revisions.");
        if (baseline.Hierarchy.Revision.Epoch == state.NativeRevision.Epoch
            && baseline.Hierarchy.Revision.Sequence > state.NativeRevision.Sequence)
            throw Error("invalid_electrical_recovery", "The baseline cannot follow the current observation.");
        return (baseline, observed);
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}
