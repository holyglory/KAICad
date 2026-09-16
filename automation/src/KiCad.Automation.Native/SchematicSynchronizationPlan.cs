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

    private static SchematicSynchronizationPlan Prepare(DesignRecoveryState state, bool allowConnectedLayout, CancellationToken token)
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
            electrical = SchematicNetReconciliation.Plan(state, token);
            gaps.AddRange(hierarchy.CoverageGaps); gaps.AddRange(electrical.CoverageGaps);
            // Retain both independent diagnostics. Neither successful half is a full design.
            if (!hierarchy.CanApply || electrical.Candidate is null)
                return Failure(hierarchy.ErrorCode ?? electrical.ErrorCode ?? "design_sync_conflict",
                    hierarchy.ErrorMessage ?? electrical.ErrorMessage ?? "Resolve the reported hierarchy or electrical conflicts first.");

            properties = SchematicModelProjection.Reconcile(state.Baseline, electrical.Candidate,
                hierarchy.Merged!, state.KnowledgeLibraries, token);
            gaps.AddRange(properties.CoverageGaps);
            if (properties.Candidate is null)
                return Failure(properties.ErrorCode ?? "design_property_conflict", properties.ErrorMessage ?? "Resolve the reported native/model property conflicts first.");
            var candidate = desired with { Engineering = properties.Candidate,
                Schematic = PreserveEnumeration(hierarchy.Merged!, desired.Schematic) };
            candidate = SchematicPropertyProjection.Project(state.Baseline, candidate, state.KnowledgeLibraries, token);
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
