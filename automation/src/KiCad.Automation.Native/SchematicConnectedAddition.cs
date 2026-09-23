using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public enum SchematicConnectedAdditionKind { NotApplicable = 0, Admitted = 1, Rejected = 2 }

public sealed record SchematicConnectedAdditionClassification(SchematicConnectedAdditionKind Kind,
    IReadOnlyList<Guid> AddedComponentIds, IReadOnlyList<Guid> ChangedNetIds,
    string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>Entry points the synchronization seam calls for XML revisions that add
/// connections (cn1-wiring-intent.md §4 and §9). Lane 2A owns this file.</summary>
public static class SchematicConnectedAddition
{
    /// <summary>Decide whether a saved XML revision is an additive connection change.
    /// Until lane 2A delivers §4.1, nothing is admitted, so every plan keeps its
    /// existing path and error code.</summary>
    public static SchematicConnectedAdditionClassification Classify(DesignRecoveryState state,
        SchematicDesign desired, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(desired);
        token.ThrowIfCancellationRequested();
        return new(SchematicConnectedAdditionKind.NotApplicable, [], []);
    }

    /// <summary>Whether the pending layout recorded in <paramref name="state"/> was produced
    /// by a connection-realization plan rather than a connected move or a rebuild. The executor
    /// routes by the lane recorded in the layout intent; this predicate only validates
    /// that record and must never claim a layout another lane recorded.</summary>
    internal static bool IsRealization(DesignRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return false;
    }

    /// <summary>Check the <paramref name="session"/> handshake for the realization capability,
    /// measure the checkpoint natively and return the operations, ending with the connectivity
    /// assertion, plus the exact design they plan to publish (§9.1 steps 2-3). The executor
    /// builds, validates and journals the batch envelope.</summary>
    internal static Task<SchematicPreparedRealization> RealizeAsync(NativeClient client, AutomationSession session,
        DesignRecoveryState state, SchematicSynchronizationPlan plan, CheckedSchematicState checkpoint, CancellationToken token = default)
        => throw Unavailable();

    /// <summary>Check a realization receipt before generic handling (§9.2): abandon a
    /// batch its own assertion rejected, and refuse a completion without verification.</summary>
    internal static void CheckReceipt(DesignRecoveryStore store, StoredDesignRecovery saved,
        CheckedSchematicBatchReceipt receipt) => throw Unavailable();

    /// <summary>Resolve a committed realization against the planned design (§9.4).</summary>
    internal static SchematicDesign Resolve(SchematicDesign planned, DesignRecoveryState state,
        SchematicElectricalState native, ApplySchematicItemBatch batch, CheckedSchematicBatchReceipt receipt,
        CancellationToken token = default) => throw Unavailable();

    internal static AutomationException Unavailable() => new(SchematicConnectionErrors.ConnectedAdditionUnavailable,
        "Connected XML additions are not available in this build.");
}

/// <summary>Prepare an admitted connected addition (cn1-wiring-intent.md §4.4).</summary>
public static class SchematicConnectedAdditionPlanner
{
    public static SchematicSynchronizationPlan Prepare(DesignRecoveryState state, SchematicDesign desired,
        SchematicHierarchyMergeResult hierarchy, SchematicConnectedAdditionClassification shape,
        List<HierarchyCoverageGap> gaps, CancellationToken token = default)
        => throw SchematicConnectedAddition.Unavailable();
}
