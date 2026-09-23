using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public enum SchematicRebuildKind { NotApplicable = 0, Admitted = 1, Rejected = 2 }

public sealed record SchematicRebuildClassification(SchematicRebuildKind Kind, IReadOnlyList<Guid> SheetInstanceIds,
    string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>The model sheet instances an admitted rebuild regenerates natively from
/// saved XML. The synchronization seam only carries it from planner to executor.</summary>
public sealed record SchematicRebuildIntent(int Version, IReadOnlyList<Guid> SheetInstanceIds)
{
    public const int CurrentVersion = 1;
}

/// <summary>Entry points the synchronization seam calls to generate missing native
/// sheets and rebuild deleted native files from saved XML without loss. Lane 2C
/// owns this file.</summary>
public static class SchematicRebuild
{
    /// <summary>Decide whether a saved XML revision needs native sheets generated or
    /// rebuilt. Until lane 2C delivers, nothing is admitted, so every plan keeps its
    /// existing path and error code.</summary>
    public static SchematicRebuildClassification Classify(DesignRecoveryState state, SchematicDesign desired,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(desired);
        token.ThrowIfCancellationRequested();
        return new(SchematicRebuildKind.NotApplicable, []);
    }

    public static SchematicSynchronizationPlan Prepare(DesignRecoveryState state, SchematicDesign desired,
        SchematicHierarchyMergeResult hierarchy, SchematicRebuildClassification shape,
        List<HierarchyCoverageGap> gaps, CancellationToken token = default) => throw Unavailable();

    /// <summary>Whether a journaled native mutation is a rebuild rather than a connected move.</summary>
    internal static bool IsRebuild(ApplySchematicItemBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return false;
    }

    /// <summary>Build the checked rebuild batch plus the exact design it plans to publish.</summary>
    internal static Task<SchematicPreparedRealization> RealizeAsync(NativeClient client, DesignRecoveryState state,
        SchematicSynchronizationPlan plan, CheckedSchematicState checkpoint, CancellationToken token = default)
        => throw Unavailable();

    /// <summary>Check a rebuild receipt before generic handling.</summary>
    internal static void CheckReceipt(DesignRecoveryStore store, StoredDesignRecovery saved,
        CheckedSchematicBatchReceipt receipt) => throw Unavailable();

    /// <summary>Resolve a committed rebuild against the planned design.</summary>
    internal static SchematicDesign Resolve(SchematicDesign planned, DesignRecoveryState state,
        SchematicElectricalState native, ApplySchematicItemBatch batch, CheckedSchematicBatchReceipt receipt,
        CancellationToken token = default) => throw Unavailable();

    private static AutomationException Unavailable() => new("schematic_rebuild_unavailable",
        "Rebuilding native sheets from XML is not available in this build.");
}
