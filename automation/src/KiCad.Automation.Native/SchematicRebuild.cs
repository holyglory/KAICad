using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

// Provisional until lane 2C confirms; changes go through a seam request
// (decision kicad-phase2-contract-errata-20260923, item 2). This covers every
// rebuild seam shape in this file: the classification, SchematicRebuildIntent,
// the entry-point signatures and the schematic_rebuild_unavailable code, plus the
// plan's Rebuild parameter and the planner's design_sync_conflict fallback.

public enum SchematicRebuildKind { NotApplicable = 0, Admitted = 1, Rejected = 2 }

public sealed record SchematicRebuildClassification(SchematicRebuildKind Kind, IReadOnlyList<Guid> SheetInstanceIds,
    string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>The model sheet instances an admitted rebuild regenerates natively from
/// saved XML. The synchronization seam only carries it from planner to executor.
/// Provisional until lane 2C confirms; changes go through a seam request.</summary>
public sealed record SchematicRebuildIntent(int Version, IReadOnlyList<Guid> SheetInstanceIds)
{
    public const int CurrentVersion = 1;
}

/// <summary>Entry points the synchronization seam calls to generate missing native
/// sheets and rebuild deleted native files from saved XML without loss. Lane 2C
/// owns this file. Provisional until lane 2C confirms; changes go through a seam request.</summary>
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

    /// <summary>Whether the pending layout recorded in <paramref name="state"/> was produced
    /// by a rebuild plan rather than a connected move or a connection realization. Decide
    /// from the recorded plan, not from batch contents; the executor asks this lane first,
    /// so a rebuild batch that also asserts connectivity is resolved here.</summary>
    internal static bool IsRebuild(DesignRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return false;
    }

    /// <summary>Return the rebuild operations for this checkpoint plus the exact design they
    /// plan to publish, after checking the <paramref name="session"/> handshake for the native
    /// support they need. The executor builds, validates and journals the batch envelope.</summary>
    internal static Task<SchematicPreparedRealization> RealizeAsync(NativeClient client, AutomationSession session,
        DesignRecoveryState state, SchematicSynchronizationPlan plan, CheckedSchematicState checkpoint, CancellationToken token = default)
        => throw Unavailable();

    /// <summary>Check a rebuild receipt before generic handling.</summary>
    internal static void CheckReceipt(DesignRecoveryStore store, StoredDesignRecovery saved,
        CheckedSchematicBatchReceipt receipt) => throw Unavailable();

    /// <summary>Resolve a committed rebuild against the planned design.</summary>
    internal static SchematicDesign Resolve(SchematicDesign planned, DesignRecoveryState state,
        SchematicElectricalState native, ApplySchematicItemBatch batch, CheckedSchematicBatchReceipt receipt,
        CancellationToken token = default) => throw Unavailable();

    // Provisional until lane 2C confirms; changes go through a seam request.
    private static AutomationException Unavailable() => new("schematic_rebuild_unavailable",
        "Rebuilding native sheets from XML is not available in this build.");
}
