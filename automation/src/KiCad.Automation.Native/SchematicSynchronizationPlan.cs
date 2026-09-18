using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record SchematicSynchronizationPlan(SchematicDesign? Candidate, string? CandidateXml,
    IReadOnlyList<SchematicItemOperation> NativeOperations,
    SchematicHierarchyMergeResult? Hierarchy, SchematicNetReconciliationResult? Electrical,
    SchematicModelProjectionResult? Properties, IReadOnlyList<SchematicBindingIssue> BindingIssues,
    IReadOnlyList<SchematicBindingDifference> ProjectionDifferences,
    SchematicElectricalComparisonResult? ObservedConnectivity, IReadOnlyList<HierarchyCoverageGap> CoverageGaps,
    bool NativeConnectivityValidationRequired, string? ErrorCode = null, string? ErrorMessage = null,
    bool NativeLayoutResolutionRequired = false)
{
    // Preparation from saved observations is not live mutation admission or permission
    // to publish XML. The executor must revalidate native state and resulting connectivity.
    public bool CanPrepare => Candidate is not null && ErrorCode is null;
}

/// <summary>Compose exact-identity reconcilers into one recoverable design candidate.
/// Reads no files or native editor, writes nothing and never advances a baseline.</summary>
public static class SchematicSynchronizationPlanner
{
    public static SchematicSynchronizationPlan Plan(DesignRecoveryState state, CancellationToken token = default)
        => Prepare(state, false, token);

    internal static SchematicSynchronizationPlan PlanForExecution(DesignRecoveryState state, CancellationToken token = default)
        => Prepare(state, true, token);

    public static Task<SchematicSynchronizationPlan> PlanWithHistoryAsync(DesignRecoveryStore store, StoredDesignRecovery saved,
        CancellationToken token = default) => PrepareWithHistoryAsync(store, saved, false, token);

    internal static Task<SchematicSynchronizationPlan> PlanForExecutionWithHistoryAsync(DesignRecoveryStore store, StoredDesignRecovery saved,
        CancellationToken token = default) => PrepareWithHistoryAsync(store, saved, true, token);

    private static async Task<SchematicSynchronizationPlan> PrepareWithHistoryAsync(DesignRecoveryStore store,
        StoredDesignRecovery saved, bool allowConnectedLayout, CancellationToken token)
    {
        var plan = Prepare(saved.State, allowConnectedLayout, token);
        if (saved.State.HasPendingWork) return plan;
        bool selected = saved.State.OwnershipResolution is not null;
        bool sameOwners = SchematicNetReconciliation.NativeOwners(saved.State.Baseline.Schematic) == SchematicNetReconciliation.NativeOwners(saved.State.Observed);
        if (!selected && (plan.CanPrepare || plan.ErrorCode != "electrical_ownership_changed" || sameOwners))
            return plan;
        try
        {
            if (selected && sameOwners)
                throw new AutomationException("native_owner_resolution_stale", "The selected restoration no longer applies; clear it or inspect the current owners.");
            var history = await SchematicOwnershipHistoryReader.ReadAsync(store, saved.State, token);
            if (selected) _ = SchematicNativeRestorationProjection.Project(saved.State, history, token);
            return Prepare(saved.State, allowConnectedLayout, token, history);
        }
        catch (Exception error) when (error is AutomationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return plan with { Candidate = null, CandidateXml = null, NativeOperations = [],
                ErrorCode = error is AutomationException automation ? automation.Code : "native_ownership_history_io",
                ErrorMessage = error.Message };
        }
    }

    private static SchematicSynchronizationPlan Prepare(DesignRecoveryState state, bool allowConnectedLayout, CancellationToken token,
        IReadOnlyList<SchematicOwnershipHistory>? history = null)
    {
        token.ThrowIfCancellationRequested();
        SchematicHierarchyMergeResult? hierarchy = null;
        SchematicNetReconciliationResult? electrical = null;
        SchematicModelProjectionResult? properties = null;
        var gaps = new List<HierarchyCoverageGap>();
        try
        {
            if (state.HasPendingWork)
                return Failure("pending_recovery_requires_reconciliation", "Inspect the saved native operation before preparing another synchronization.");
            var desired = DesignRecoveryStore.ReadDesired(state);
            hierarchy = state.HierarchyResolution is { } choices
                ? SchematicHierarchyMerge.Resolve(state.Baseline.Schematic, desired.Schematic, state.Observed,
                    choices.SnapshotToken, choices.Choices, token)
                : SchematicHierarchyMerge.Plan(state.Baseline.Schematic, desired.Schematic, state.Observed, token);
            if (SchematicNativeCreationProjection.IsSupportedAddition(state.Baseline, desired.Engineering))
                return PrepareCreation(state, desired, hierarchy, gaps, token);
            electrical = SchematicNetReconciliation.Plan(state, history, token);
            gaps.AddRange(hierarchy.CoverageGaps); gaps.AddRange(electrical.CoverageGaps);
            // Retain both independent diagnostics. Neither successful half is a full design.
            if (!hierarchy.CanApply || electrical.Candidate is null)
                return Failure(hierarchy.ErrorCode ?? electrical.ErrorCode ?? "design_sync_conflict",
                    hierarchy.ErrorMessage ?? electrical.ErrorMessage ?? "Resolve the reported hierarchy or electrical conflicts first.");

            bool nativeRemovals = electrical.RemovedSymbolOccurrences is { Count: > 0 };
            properties = electrical.Restoration is { } restoration
                ? SchematicModelProjection.ReconcileAfterRestoration(state.Baseline, electrical.Candidate,
                    hierarchy.Merged!, restoration, state.KnowledgeLibraries, token)
                : nativeRemovals
                ? SchematicModelProjection.ReconcileAfterRemovals(state.Baseline, electrical.Candidate,
                    hierarchy.Merged!, state.KnowledgeLibraries, token)
                : SchematicModelProjection.Reconcile(state.Baseline, electrical.Candidate,
                    hierarchy.Merged!, state.KnowledgeLibraries, token);
            gaps.AddRange(properties.CoverageGaps);
            if (properties.Candidate is null)
                return Failure(properties.ErrorCode ?? "design_property_conflict", properties.ErrorMessage ?? "Resolve the reported native/model property conflicts first.");
            var survivingSymbols = properties.Candidate.Circuit.Symbols.Select(s => s.Id).ToHashSet();
            var candidate = desired with { Engineering = properties.Candidate,
                Schematic = PreserveEnumeration(hierarchy.Merged!, desired.Schematic),
                SymbolBindings = electrical.Restoration is not null ? electrical.Restoration.BindingCandidate.SymbolBindings
                    : nativeRemovals ? desired.SymbolBindings.Where(b => survivingSymbols.Contains(b.SymbolOccurrenceId)).ToArray()
                    : desired.SymbolBindings };
            candidate = electrical.Restoration is not null
                ? SchematicPropertyProjection.ProjectAfterRestoration(state.Baseline, candidate, electrical.Restoration, state.KnowledgeLibraries, token)
                : SchematicPropertyProjection.Project(state.Baseline, candidate, state.KnowledgeLibraries, token);
            var bindings = SchematicDesignBindings.Inspect(candidate, state.KnowledgeLibraries, token);
            gaps.AddRange(bindings.CoverageGaps);
            if (!bindings.IdentitiesResolved)
                return Failure("unresolved_design_bindings", "Reconcile component, sheet and pin ownership before publishing this design.", bindings.Issues);

            var differences = bindings.Differences.ToList();
            foreach (var occurrence in candidate.Engineering.Circuit.Symbols.Where(x => x.Placement is not null))
            {
                token.ThrowIfCancellationRequested();
                var component = candidate.Engineering.Circuit.Components.Single(x => x.Id == occurrence.ComponentId);
                var sheet = candidate.SheetBindings.Single(x => x.SheetInstanceId == occurrence.EffectiveSheetInstanceId(component));
                string path = SchematicDesignBindings.PathKey(sheet.NativePath);
                var screen = candidate.Schematic.Instances.Single(x => string.Join('/', x.Metadata.Document.SheetPath.Path.Select(id => id.Value)) == path);
                var binding = candidate.SymbolBindings.Single(x => x.SymbolOccurrenceId == occurrence.Id);
                var symbol = screen.Items.Where(x => x.Is(SchematicSymbolInstance.Descriptor))
                    .Select(x => x.Unpack<SchematicSymbolInstance>()).Single(x => x.Id.Value == binding.NativeObjectId.ToString("D"));
                var placement = SchematicModelProjection.Placement(symbol);
                if (!SchematicOrientation.Equivalent(placement, occurrence.Placement))
                    differences.Add(new(occurrence.Id, "placement", JsonSerializer.Serialize(occurrence.Placement), JsonSerializer.Serialize(placement)));
            }
            IReadOnlyList<SchematicItemOperation> moves = [];
            if (differences.Count != 0 && allowConnectedLayout && differences.All(d => d.Field == "placement"))
            {
                var placement = SchematicPlacementPlan.Plan(state.Baseline, candidate.Engineering, candidate.Schematic,
                    state.KnowledgeLibraries, token);
                if (placement.ReconciledModel is null || placement.Issues.Count != 0 || placement.Conflicts.Count != 0)
                    return Failure(placement.Issues.FirstOrDefault()?.Code ?? "layout_conflict",
                        placement.Issues.FirstOrDefault()?.Message ?? "Resolve conflicting placement before moving connected objects.");
                moves = placement.Operations;
                if (moves.Count == 0) return Failure("unresolved_layout", "The requested placement has no matching native connected move.");
            }
            else if (differences.Count != 0)
                return Failure("native_projection_required", "Project the requested engineering properties into the native schematic before preparing a consistent design.",
                    bindings.Issues, differences);

            var connectivity = SchematicElectricalComparison.Compare(candidate, state.ObservedElectrical!, state.KnowledgeLibraries, token);
            gaps.AddRange(connectivity.CoverageGaps);
            if (!connectivity.PinBindingsComplete)
                return Failure("unresolved_electrical_bindings", "The candidate no longer resolves the captured native pins.");
            string xml = SchematicDesignXml.Write(candidate, state.KnowledgeLibraries);
            var decoded = SchematicDesignXml.Read(xml, state.KnowledgeLibraries);
            if (SchematicDesignXml.Write(decoded, state.KnowledgeLibraries) != xml)
                return Failure("inconsistent_design_serialization", "The combined candidate must round-trip without information loss.");
            token.ThrowIfCancellationRequested();
            var operations = SchematicHierarchyDelta.Plan(state.Observed, candidate.Schematic, token).Concat(moves).ToArray();
            return new(candidate, xml, operations.Select(x => x.Clone()).ToArray(), hierarchy, electrical,
                properties, [], [], connectivity, gaps.Distinct().ToArray(),
                operations.Length != 0 || !connectivity.ConnectivityEquivalent, NativeLayoutResolutionRequired: moves.Count != 0);
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }

        SchematicSynchronizationPlan PrepareCreation(DesignRecoveryState current, SchematicDesign desiredDesign,
            SchematicHierarchyMergeResult mergedHierarchy, List<HierarchyCoverageGap> currentGaps, CancellationToken cancellation)
        {
            var checkpoints = SchematicElectricalCheckpoints.Require(current);
            currentGaps.AddRange(mergedHierarchy.CoverageGaps);
            if (!mergedHierarchy.CanApply || mergedHierarchy.Merged is null)
                return Failure(mergedHierarchy.ErrorCode ?? "design_sync_conflict",
                    mergedHierarchy.ErrorMessage ?? "Resolve the reported hierarchy conflict before creating a component.");
            if (SchematicNetReconciliation.Bindings(current.Baseline) != SchematicNetReconciliation.Bindings(desiredDesign))
                return Failure("creation_bindings_changed", "Preserve existing bindings until new symbol identities have been prepared.");
            if (SchematicHierarchyDelta.Plan(current.Baseline.Schematic, current.Observed, cancellation).Count != 0
                || SchematicHierarchyDelta.Plan(current.Baseline.Schematic, desiredDesign.Schematic, cancellation).Count != 0)
                return Failure("creation_requires_stable_native_hierarchy",
                    "XML component creation cannot overwrite concurrent native hierarchy or layout edits.");
            var before = SchematicElectricalComparison.Compare(current.Baseline, checkpoints.Baseline, current.KnowledgeLibraries, cancellation);
            if (!before.PinBindingsComplete || !before.ConnectivityEquivalent)
                return Failure("unaligned_electrical_baseline", "The saved baseline must agree with its native pin partition.");
            var observed = SchematicElectricalComparison.Compare(current.Baseline, checkpoints.Observed, current.KnowledgeLibraries, cancellation);
            if (!observed.PinBindingsComplete || !observed.ConnectivityEquivalent)
                return Failure("creation_requires_stable_connectivity", "Reconcile current native connectivity before creating components.");
            var creation = SchematicNativeCreationProjection.Project(current.Baseline, desiredDesign.Engineering,
                current.KnowledgeLibraries, cancellation);
            var bindings = SchematicDesignBindings.Inspect(creation.Candidate, current.KnowledgeLibraries, cancellation);
            currentGaps.AddRange(bindings.CoverageGaps);
            if (!bindings.IdentitiesResolved)
                return Failure("created_binding_invalid", "The generated native identities do not resolve exactly.", bindings.Issues);
            string xml = SchematicDesignXml.Write(creation.Candidate, current.KnowledgeLibraries);
            var decoded = SchematicDesignXml.Read(xml, current.KnowledgeLibraries);
            if (SchematicDesignXml.Write(decoded, current.KnowledgeLibraries) != xml)
                return Failure("inconsistent_design_serialization", "The created candidate must round-trip without information loss.");
            var operations = SchematicHierarchyDelta.Plan(current.Observed, creation.Candidate.Schematic, cancellation).ToArray();
            return new(creation.Candidate, xml, operations.Select(operation => operation.Clone()).ToArray(),
                mergedHierarchy, null, null, [], [], null, currentGaps.Distinct().ToArray(),
                true);
        }

        SchematicSynchronizationPlan Failure(string code, string? message,
            IReadOnlyList<SchematicBindingIssue>? bindings = null, IReadOnlyList<SchematicBindingDifference>? differences = null) =>
            new(null, null, [], hierarchy, electrical, properties, bindings ?? [], differences ?? [], null,
                gaps.Distinct().ToArray(), true, code, message);
    }

    private static SchematicHierarchyData PreserveEnumeration(SchematicHierarchyData merged, SchematicHierarchyData desired)
    {
        // Hierarchy reconciliation sorts paths for deterministic conflict processing.
        // Do not rewrite an otherwise unchanged XML document merely to impose that
        // internal traversal order. Retain desired ordering for surviving exact IDs;
        // newly observed entities follow the deterministic merged order.
        static string PathKey(SchematicScreenData screen) => string.Join('/', screen.Metadata.Document.SheetPath.Path.Select(id => id.Value));
        var result = merged.Clone();
        var byPath = result.Instances.ToDictionary(PathKey, StringComparer.Ordinal);
        var paths = desired.Instances.Select(PathKey).Concat(result.Instances.Select(PathKey))
            .Distinct(StringComparer.Ordinal).Where(byPath.ContainsKey).ToArray();
        result.Instances.Clear(); result.Instances.Add(paths.Select(path => byPath[path]));
        var desiredByPath = desired.Instances.ToDictionary(PathKey, StringComparer.Ordinal);
        foreach (var screen in result.Instances)
        {
            if (!desiredByPath.TryGetValue(PathKey(screen), out var previous)) continue;
            var mergedIds = new List<Guid>(); var desiredIds = new List<Guid>();
            _ = SchematicItemDelta.Index(screen.Items, mergedIds);
            _ = SchematicItemDelta.Index(previous.Items, desiredIds);
            var byId = mergedIds.Zip(screen.Items).ToDictionary(pair => pair.First, pair => pair.Second);
            var items = desiredIds.Concat(mergedIds).Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
            screen.Items.Clear(); screen.Items.Add(items);
        }
        return result;
    }
}
