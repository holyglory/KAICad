using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record PlacementPlanIssue(string Code, Guid? SymbolOccurrenceId, string Message);
public sealed record SchematicPlacementPlanResult(EngineeringDesign? ReconciledModel,
    IReadOnlyList<SchematicItemOperation> Operations, IReadOnlyList<PlacementPlanIssue> Issues,
    IReadOnlyList<SchematicProjectionConflict> Conflicts, IReadOnlyList<SchematicBindingIssue> BindingIssues,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, bool UnprojectedSnapshotChanges);

/// <summary>Pure forward translation planning after conflict-preserving reverse reconciliation.
/// Operations contain exact sheet/symbol identities but no live admission or retry token.
/// Native execution must revalidate revision, preserve connectivity, and capture the actual
/// resulting wire geometry before updating a synchronized checkpoint.</summary>
public static class SchematicPlacementPlan
{
    public static SchematicPlacementPlanResult Plan(SchematicDesign baseline, EngineeringDesign desired,
        SchematicHierarchyData observed, IReadOnlyCollection<ComponentKnowledgeLibrary> libraries,
        CancellationToken cancellationToken = default)
    {
        var projection = SchematicModelProjection.Reconcile(baseline, desired, observed, libraries, cancellationToken);
        var issues = new List<PlacementPlanIssue>();
        var operations = new List<SchematicItemOperation>();
        var transforms = new List<SchematicItemOperation>();
        var unlocks = new List<SchematicItemOperation>();
        var locks = new List<SchematicItemOperation>();
        SchematicPlacementPlanResult Result() => new(projection.Candidate,
            issues.Count == 0 && projection.Candidate is not null ? unlocks.Concat(transforms).Concat(operations).Concat(locks).ToArray() : [], issues,
            projection.Conflicts, projection.BindingIssues, projection.CoverageGaps, projection.UnprojectedSnapshotChanges);
        if (projection.Candidate is null)
        {
            if (projection.ErrorCode is not null)
                issues.Add(new(projection.ErrorCode, null, projection.ErrorMessage ?? "Reconcile model/native ownership before planning placement."));
            return Result();
        }
        var candidate = projection.Candidate;
        var native = SchematicModelProjection.NativeSymbols(baseline, observed);
        var oldNative = SchematicModelProjection.NativeSymbols(baseline, baseline.Schematic);
        var originalSymbols = baseline.Engineering.Circuit.Symbols.ToDictionary(s => s.Id);
        var components = candidate.Circuit.Components.ToDictionary(c => c.Id);
        var paths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId,
            b => SchematicDesignBindings.PathKey(b.NativePath));
        var screens = observed.Instances.ToDictionary(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(id => id.Value)));
        var bindings = baseline.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId);
        var shared = new Dictionary<string, List<(Guid Id, DocumentSpecifier Document, Guid NativeId,
            SymbolPlacement Current, SymbolPlacement Desired)>>();

        foreach (var symbol in candidate.Circuit.Symbols.OrderBy(s => s.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var current = SchematicModelProjection.Placement(native[symbol.Id]);
                var previous = SchematicModelProjection.Placement(oldNative[symbol.Id]);
                var original = originalSymbols[symbol.Id].Placement;
                if (original is not null && !SchematicOrientation.Equivalent(original, previous))
                    issues.Add(new("unaligned_placement_baseline", symbol.Id, "The saved model placement does not match its native baseline."));
                // Missing coordinates do not request deletion or movement. A later layout
                // refinement can supply them without fabricating a native placement now.
                var wanted = symbol.Placement ?? current;
                if (symbol.Unit != native[symbol.Id].Unit.Unit)
                    issues.Add(new("unit_change_requires_electrical_update", symbol.Id, "Apply and verify the symbol-unit change before placement."));
                if (current.Locked && wanted.Locked && !SchematicOrientation.Equivalent(current, wanted))
                    issues.Add(new("locked_symbol", symbol.Id, "Preserve the locked native symbol placement."));
                foreach (decimal coordinate in new[] { current.XMillimeters, current.YMillimeters,
                    wanted.XMillimeters, wanted.YMillimeters })
                {
                    long units = Coordinates.MillimetersToSchematicUnits(coordinate);
                    if (units < int.MinValue || units > int.MaxValue)
                        throw new AutomationException("native_coordinate_range", "Placement exceeds the native schematic coordinate range.");
                }
                var screen = screens[paths[symbol.EffectiveSheetInstanceId(components[symbol.ComponentId])]];
                string key = screen.Metadata.ScreenId.Value + "#" + bindings[symbol.Id];
                if (!shared.TryGetValue(key, out var owners)) shared.Add(key, owners = []);
                owners.Add((symbol.Id, screen.Metadata.Document, bindings[symbol.Id], current, wanted));
            }
            catch (Exception error) when (error is AutomationException or OverflowException)
            {
                issues.Add(new(error is AutomationException automation ? automation.Code : "native_coordinate_range",
                    symbol.Id, error.Message));
            }
        }
        foreach (var owners in shared.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = owners.OrderBy(o => string.Join('/', o.Document.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal).First();
            if (owners.Any(o => !SchematicOrientation.Equivalent(o.Desired, first.Desired)))
            {
                foreach (var owner in owners)
                    issues.Add(new("shared_placement_conflict", owner.Id, "Repeated instances share one native symbol geometry but request different placements."));
                continue;
            }
            try
            {
                if (first.Current.Locked != first.Desired.Locked)
                {
                    var change = new SchematicSymbolLocks { Locked = first.Desired.Locked ? LockedState.LsLocked : LockedState.LsUnlocked };
                    change.Symbols.Add(new KIID { Value = first.NativeId.ToString("D") });
                    (first.Desired.Locked ? locks : unlocks).Add(new SchematicItemOperation
                        { TargetDocument = first.Document.Clone(), SetSymbolLocks = change });
                }
                foreach (var kind in SchematicOrientation.Plan(first.Current, first.Desired))
                {
                    var transform = new SchematicConnectedSymbolTransform { Kind = kind, Pivot = new()
                    {
                        XNm = Coordinates.MillimetersToNanometers(first.Current.XMillimeters),
                        YNm = Coordinates.MillimetersToNanometers(first.Current.YMillimeters)
                    } };
                    transform.Symbols.Add(new KIID { Value = first.NativeId.ToString("D") });
                    transforms.Add(new SchematicItemOperation { TargetDocument = first.Document.Clone(), TransformConnectedSymbols = transform });
                }
                long x = Coordinates.MillimetersToNanometers(first.Desired.XMillimeters - first.Current.XMillimeters);
                long y = Coordinates.MillimetersToNanometers(first.Desired.YMillimeters - first.Current.YMillimeters);
                if (x / 100 < int.MinValue || x / 100 > int.MaxValue || y / 100 < int.MinValue || y / 100 > int.MaxValue)
                    throw new AutomationException("native_displacement_range", "The connected movement exceeds native displacement range.");
                if (x == 0 && y == 0) continue;
                // Move a rigid group together so native drag sees both ends of its internal wires.
                var operation = operations.FirstOrDefault(o => o.TargetDocument.Equals(first.Document)
                    && o.MoveConnectedSymbols.Delta.XNm == x && o.MoveConnectedSymbols.Delta.YNm == y);
                if (operation is null)
                {
                    operation = new() { TargetDocument = first.Document.Clone(),
                        MoveConnectedSymbols = new() { Delta = new() { XNm = x, YNm = y } } };
                    operations.Add(operation);
                }
                operation.MoveConnectedSymbols.Symbols.Add(new KIID { Value = first.NativeId.ToString("D") });
            }
            catch (Exception error) when (error is AutomationException or OverflowException)
            {
                issues.Add(new(error is AutomationException automation ? automation.Code : "native_displacement_range", first.Id, error.Message));
            }
        }
        return Result();
    }
}
