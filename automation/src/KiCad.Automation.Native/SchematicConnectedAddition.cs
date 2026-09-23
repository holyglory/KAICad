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

    /// <summary>Whether a journaled native mutation is a connection realization
    /// (its batch carries the connectivity assertion) rather than a connected move.</summary>
    internal static bool IsRealization(ApplySchematicItemBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return false;
    }

    /// <summary>Measure the checkpoint natively and build the one checked realization
    /// batch plus the exact design it plans to publish (§9.1 steps 2-4).</summary>
    internal static Task<SchematicPreparedRealization> RealizeAsync(NativeClient client, DesignRecoveryState state,
        SchematicSynchronizationPlan plan, CheckedSchematicState checkpoint, CancellationToken token = default)
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
