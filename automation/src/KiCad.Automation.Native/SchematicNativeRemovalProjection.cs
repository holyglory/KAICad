using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal sealed record SchematicNativeRemovalResult(SchematicDesign? BindingCandidate,
    IReadOnlyList<Guid> RemovedOccurrences, IReadOnlyList<ComponentReferenceChange> ComponentChanges,
    IReadOnlyList<NetIdentityChange> RetiredNets, IReadOnlyList<SchematicBindingIssue> Issues,
    IReadOnlyList<HierarchyCoverageGap> CoverageGaps, string? ErrorCode = null, string? ErrorMessage = null)
{
    /// <summary>Design sheet instances KiCad no longer shows, with everything on them (ledger p5f6d5d0ca242d628).</summary>
    public IReadOnlyList<Guid> RemovedSheetInstances { get; init; } = [];
    /// <summary>Design sheet instances KiCad shows at another place: under another parent, or through another sheet symbol.
    /// Their descendants keep their parents and are only shown at other paths.</summary>
    public IReadOnlyList<Guid> MovedSheetInstances { get; init; } = [];
    /// <summary>Moves exact identities cannot decide; set with <see cref="SchematicNativeSheetChanges.MoveAmbiguous"/>.</summary>
    public IReadOnlyList<SchematicSheetResolutionRequest> SheetRequests { get; init; } = [];
    /// <summary>Only the addition compositor may use this partial owner view; all
    /// listed native instances still require complete binding validation.</summary>
    public IReadOnlyList<string> UnboundInsertedSheetPaths { get; init; } = [];
    public bool SheetsChanged => RemovedSheetInstances.Count != 0 || MovedSheetInstances.Count != 0;
}

/// <summary>Project exact native symbol and sheet deletions, and sheet moves, onto engineering ownership.
/// The result resolves bindings only: its properties and provisional net groups
/// still require three-way reconciliation against the original checkpoints.
/// A sheet KiCad no longer shows takes its components and the units drawn on it along; a component whose own sheet goes
/// while a unit of it stays on another sheet is refused. A sheet shown at another place keeps its design identity, its
/// components and their bindings; only its binding and parent follow KiCad (ledger p5f6d5d0ca242d628). Sheets KiCad shows
/// that the design does not know are new owners: <c>electrical_ownership_changed</c>, for the addition projection.</summary>
internal static class SchematicNativeRemovalProjection
{
    internal const string SplitComponent = "native_sheet_removal_splits_component";

    internal static SchematicNativeRemovalResult Project(SchematicDesign baseline, SchematicHierarchyData observed,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default,
        SchematicSheetInstanceChoices? sheetChoices = null) => ProjectCore(baseline, observed, libraries, token, sheetChoices, false);

    internal static SchematicNativeRemovalResult ProjectExisting(SchematicDesign baseline, SchematicHierarchyData observed,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token,
        SchematicSheetInstanceChoices? sheetChoices) => ProjectCore(baseline, observed, libraries, token, sheetChoices, true);

    private static SchematicNativeRemovalResult ProjectCore(SchematicDesign baseline, SchematicHierarchyData observed,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token,
        SchematicSheetInstanceChoices? sheetChoices, bool projectExisting)
    {
        token.ThrowIfCancellationRequested();
        var gaps = new List<HierarchyCoverageGap>();
        SchematicNativeRemovalResult Failure(string code, string? message = null,
            IReadOnlyList<SchematicBindingIssue>? issues = null) => new(null, [], [], [], issues ?? [],
                gaps.Distinct().ToArray(), code, message);
        try
        {
            var original = SchematicDesignBindings.Inspect(baseline, libraries, token);
            gaps.AddRange(original.CoverageGaps);
            if (!original.IdentitiesResolved) return Failure("unresolved_design_bindings", issues: original.Issues);
            var topology = SchematicHierarchyTopology.Inspect(observed, token);
            gaps.AddRange(topology.CoverageGaps);
            if (!topology.IsValid) return Failure("invalid_native_hierarchy", "Resolve the reported native hierarchy before projecting deletions.");
            var sheets = SchematicNativeSheetChanges.Compare(baseline.Schematic, observed, sheetChoices);
            if (sheets.ErrorCode is not null)
                return Failure(sheets.ErrorCode, sheets.ErrorMessage) with { SheetRequests = Requests(sheets, baseline) };
            if (sheets.Inserted.Count != 0 && !projectExisting)
                return Failure("electrical_ownership_changed", "Sheets inserted in KiCad require explicit ownership reconciliation.");
            var screens = observed.Instances.ToDictionary(SchematicNativeSheetChanges.Key);

            var circuit = baseline.Engineering.Circuit;
            // Where KiCad shows each design sheet now; null when it no longer does.
            var now = sheets.Rebind(baseline.SheetBindings);
            var removedSheets = now.Where(p => p.Value is null).Select(p => p.Key).ToHashSet();
            var byPath = now.Where(p => p.Value is not null).ToDictionary(p => p.Value!, p => p.Key, StringComparer.Ordinal);
            if (now.Values.Where(p => p is not null).Any(p => SchematicNativeSheetChanges.Parent(p!).Length != 0
                && !byPath.ContainsKey(SchematicNativeSheetChanges.Parent(p!))))
                return Failure("native_sheet_parent_requires_owner", "Resolve the new parent's ownership before projecting its existing children.");
            var instances = circuit.SheetInstances.Where(s => !removedSheets.Contains(s.Id)).Select(s =>
            {
                string path = now[s.Id]!, parent = SchematicNativeSheetChanges.Parent(path);
                return s with { ParentId = parent.Length == 0 ? null : byPath[parent] };
            }).ToArray();
            var before = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
            // Moved at their own level: another parent, or another sheet symbol; descendants only follow.
            var moved = instances.Where(s => now[s.Id] != before[s.Id]
                && (SchematicNativeSheetChanges.Parent(now[s.Id]!) != (sheets.Now(SchematicNativeSheetChanges.Parent(before[s.Id])) ?? "")
                    || SchematicNativeSheetChanges.Last(now[s.Id]!) != SchematicNativeSheetChanges.Last(before[s.Id])))
                .Select(s => s.Id).ToArray();

            var nativeIds = screens.ToDictionary(pair => pair.Key, pair => pair.Value.Items
                .Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value)
                .ToHashSet(StringComparer.Ordinal));
            var components = circuit.Components.ToDictionary(c => c.Id);
            var bindings = baseline.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId);
            var removed = circuit.Symbols.Where(s => now[s.EffectiveSheetInstanceId(components[s.ComponentId])] is not { } path
                || !nativeIds[path].Contains(bindings[s.Id].NativeObjectId.ToString("D"))).Select(s => s.Id).ToHashSet();
            // Restrict only a private comparison copy. The real baseline and
            // electrical checkpoints remain untouched and must be verified later.
            var survivingNative = baseline.Schematic.Clone();
            for (int index = survivingNative.Instances.Count - 1; index >= 0; --index)
            {
                token.ThrowIfCancellationRequested();
                var screen = survivingNative.Instances[index];
                if (sheets.Now(SchematicNativeSheetChanges.Key(screen)) is not { } path) { survivingNative.Instances.RemoveAt(index); continue; }
                screen.Metadata.Document.SheetPath.Path.Clear();
                screen.Metadata.Document.SheetPath.Path.Add(path.Split('/').Select(id => new Kiapi.Common.Types.KIID { Value = id }));
                var live = nativeIds[path];
                for (int i = screen.Items.Count - 1; i >= 0; --i)
                    if (screen.Items[i].Is(SchematicSymbolInstance.Descriptor)
                        && !live.Contains(screen.Items[i].Unpack<SchematicSymbolInstance>().Id.Value)) screen.Items.RemoveAt(i);
            }
            var observedOwners = observed;
            if (projectExisting && sheets.Inserted.Count != 0)
            {
                // This copy is used only to compare old symbol ownership, never
                // as a hierarchy, native observation or electrical checkpoint.
                observedOwners = observed.Clone();
                for (int i = observedOwners.Instances.Count - 1; i >= 0; --i)
                    if (sheets.Inserted.Contains(SchematicNativeSheetChanges.Key(observedOwners.Instances[i]), StringComparer.Ordinal))
                        observedOwners.Instances.RemoveAt(i);
            }
            if (SchematicNetReconciliation.NativeOwners(survivingNative) != SchematicNetReconciliation.NativeOwners(observedOwners))
                return Failure("electrical_ownership_changed", "New symbols, unit changes or changed library pin identities require explicit ownership reconciliation.");

            var remainingSymbols = circuit.Symbols.Where(s => !removed.Contains(s.Id)).ToArray();
            var hadDrawing = circuit.Symbols.Select(s => s.ComponentId).ToHashSet();
            var hasDrawing = remainingSymbols.Select(s => s.ComponentId).ToHashSet();
            // A component goes with the sheet it sits on; one that keeps a unit on another sheet cannot follow either.
            var homeless = circuit.Components.Where(c => removedSheets.Contains(c.SheetInstanceId)).Select(c => c.Id).ToHashSet();
            if (circuit.Components.FirstOrDefault(c => homeless.Contains(c.Id) && hasDrawing.Contains(c.Id)) is { } split)
                return Failure(SplitComponent, $"KiCad no longer shows the sheet component {split.Reference} sits on, but still shows a unit of it on "
                    + "another sheet. Nothing was published. Undo the sheet removal in KiCad, then remove or move that unit first.");
            var retired = circuit.Components.Where(c => homeless.Contains(c.Id) || hadDrawing.Contains(c.Id) && !hasDrawing.Contains(c.Id))
                .Select(c => c.Id).ToHashSet();
            var remainingComponents = circuit.Components.Where(c => !retired.Contains(c.Id)).ToArray();
            var retiredDefinitions = circuit.Components.Where(c => retired.Contains(c.Id)).Select(c => c.DefinitionId)
                .Except(remainingComponents.Select(c => c.DefinitionId)).ToHashSet();
            // A sheet definition goes with the last sheet that showed it.
            var hiddenDefinitions = circuit.SheetInstances.Where(s => removedSheets.Contains(s.Id)).Select(s => s.DefinitionId)
                .Except(instances.Select(s => s.DefinitionId)).ToHashSet();
            var nets = circuit.Nets.Select(n => (Original: n, Pins: n.Pins.Where(p => !retired.Contains(p.ComponentId)).ToArray()))
                .Where(n => n.Pins.Length > 0 || n.Original.Pins.Count == 0)
                .Select(n => n.Original with { Pins = n.Pins }).ToArray();
            var next = circuit with { Symbols = remainingSymbols, Components = remainingComponents, Nets = nets, SheetInstances = instances,
                Sheets = circuit.Sheets.Where(s => !hiddenDefinitions.Contains(s.Id))
                    .Select(s => s with { Components = s.Components.Where(c => !retiredDefinitions.Contains(c.Id)).ToArray() }).ToArray() };
            try { next.Validate(); }
            catch (AutomationException error)
            {
                return Failure("component_ownership_resolution_required", error.Message);
            }
            var changes = retired.Order().Select(id => new ComponentReferenceChange(new(id), ComponentReferenceChangeKind.Removed,
                homeless.Contains(id) ? "KiCad no longer shows the sheet this component sits on."
                    : "The last native symbol occurrence of this component was removed.", [])).ToArray();
            var liveNets = nets.Select(n => n.Id).ToHashSet();
            var netChanges = circuit.Nets.Where(n => !liveNets.Contains(n.Id)).Select(n => new NetIdentityChange(n.Id,
                NetBindingChangeKind.Removed, "Every component pin realizing this net was removed.", [])).ToArray();
            var engineering = ComponentReferenceRetention.Retain(baseline.Engineering, next, changes, libraries, netChanges);
            var candidate = baseline with { Engineering = engineering, Schematic = observed.Clone(),
                SheetBindings = baseline.SheetBindings.Where(b => !removedSheets.Contains(b.SheetInstanceId))
                    .Select(b => b with { NativePath = [.. now[b.SheetInstanceId]!.Split('/').Select(Guid.Parse)] }).ToArray(),
                SymbolBindings = baseline.SymbolBindings.Where(b => !removed.Contains(b.SymbolOccurrenceId)).ToArray() };
            var report = SchematicDesignBindings.Inspect(candidate, libraries, token);
            gaps.AddRange(report.CoverageGaps);
            var remainingIssues = projectExisting ? report.Issues.Where(i =>
                !(i.Code is "unmapped_native_sheet" or "unmapped_native_symbol"
                    && i.NativePath is not null && sheets.Inserted.Contains(i.NativePath, StringComparer.Ordinal))).ToArray() : report.Issues;
            if (remainingIssues.Count != 0) return Failure("unresolved_design_bindings", issues: remainingIssues);
            return new(candidate, removed.Order().ToArray(), changes, netChanges, [], gaps.Distinct().ToArray())
            {
                RemovedSheetInstances = [.. circuit.SheetInstances.Where(s => removedSheets.Contains(s.Id)).Select(s => s.Id)],
                MovedSheetInstances = moved,
                UnboundInsertedSheetPaths = projectExisting ? sheets.Inserted : []
            };
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
    }

    /// <summary>The design sheets KiCad showed at the former paths of each undecidable move.</summary>
    internal static IReadOnlyList<SchematicSheetResolutionRequest> Requests(SchematicNativeSheetChanges changes, SchematicDesign baseline)
    {
        var byPath = baseline.SheetBindings.ToDictionary(b => SchematicDesignBindings.PathKey(b.NativePath), b => b.SheetInstanceId, StringComparer.Ordinal);
        return [.. changes.Requests.Select(r => r with { FormerSheetInstanceIds = [.. r.FormerPaths.Where(byPath.ContainsKey).Select(p => byPath[p])] })];
    }
}
