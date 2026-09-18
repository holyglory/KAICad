using System.Text;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Explicit usable page and reserved artwork/title-block areas. The
/// physical screen UUID, not a filename or displayed sheet, owns these limits.</summary>
public sealed record SchematicLayoutRegion(Guid ScreenId, PresentationBounds UsableBounds,
    IReadOnlyList<InitialLayoutObstacle> Reservations);

public sealed record SchematicInitialLayoutResult(SchematicDesign? DesiredDesign, string? DesiredXml,
    InitialLayoutCandidate Layout, LayoutRefinement? Refinement, IReadOnlyList<string> Limitations)
{
    public bool CanPropose => DesiredDesign is not null && Layout.CanPropose;
    public bool RequiresNativeConnectivityValidation => true;
    public bool RequiresVisualReview => true;
}

/// <summary>Read-only preparation for known-part, unconnected additions. Uses
/// detached native measurements, never dummy symbol sizes or native insertion.
/// A result is a proposed XML edit, not an accepted layout or a sync commit.</summary>
public static class SchematicInitialLayoutPlanner
{
    public static async Task<SchematicInitialLayoutResult> ProposeAsync(NativeClient client,
        DesignRecoveryState state, InitialLayoutPolicy policy, IReadOnlyList<SchematicLayoutRegion> regions,
        string userInstructions, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != state.InstanceId.ToString("D"))
            throw Error("layout_instance_mismatch", "Measure only the native instance recorded by this design.");
        return await ProposeMeasuredAsync(state, policy, regions, userInstructions,
            (request, cancellation) => client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, cancellation), token);
    }

    internal static async Task<SchematicInitialLayoutResult> ProposeMeasuredAsync(DesignRecoveryState state,
        InitialLayoutPolicy policy, IReadOnlyList<SchematicLayoutRegion> regions, string userInstructions,
        Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = SchematicElectricalCheckpoints.Require(state);
        var desired = DesignRecoveryStore.ReadDesired(state);
        if (!SchematicNativeCreationProjection.IsSupportedAddition(state.Baseline, desired.Engineering))
            throw Error("unsupported_layout_creation", "Initial placement currently requires additions of existing part definitions without rewiring or ownership changes.");
        var oldIds = state.Baseline.Engineering.Circuit.Symbols.Select(s => s.Id).ToHashSet();
        var added = desired.Engineering.Circuit.Symbols.Where(s => !oldIds.Contains(s.Id)).ToArray();
        if (!added.Any(s => s.Placement is null))
            throw Error("no_missing_placement", "All new symbols already have positions; use scoped visual refinement to adjust them.");
        var components = desired.Engineering.Circuit.Components.ToDictionary(c => c.Id);
        var sheets = state.Baseline.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document), StringComparer.Ordinal);
        var paths = state.Baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var groups = added.GroupBy(s =>
        {
            var component = components[s.ComponentId];
            string path = paths[s.EffectiveSheetInstanceId(component)];
            return (Screen: Id(sheets[path].Metadata.ScreenId), component.DefinitionId, s.Unit);
        }).ToArray();
        var seed = new Dictionary<Guid, SymbolPlacement>();
        var fixedOccurrences = new HashSet<Guid>();
        foreach (var group in groups)
        {
            var specified = group.Where(s => s.Placement is not null).ToArray();
            var placement = specified.FirstOrDefault()?.Placement ?? new(0, 0, 0, false, false, false);
            if (specified.Any(s => !SchematicOrientation.Equivalent(s.Placement, placement)))
                throw Error("shared_symbol_placement_conflict", "Repeated instances of one physical symbol request different positions or orientations.");
            foreach (var occurrence in group)
            {
                seed.Add(occurrence.Id, placement);
                if (specified.Length != 0) fixedOccurrences.Add(occurrence.Id);
            }
        }
        var seeded = desired with { Engineering = desired.Engineering with { Circuit = desired.Engineering.Circuit with
        { Symbols = desired.Engineering.Circuit.Symbols.Select(s => seed.TryGetValue(s.Id, out var p) ? s with { Placement = p } : s).ToArray() } } };
        // Reuse synchronization's exact checkpoint, binding, hierarchy and
        // electrical guards before asking the editor to measure anything.
        var plan = SchematicSynchronizationPlanner.Plan(state with
        { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(seeded, state.KnowledgeLibraries)) }, token);
        if (!plan.CanPrepare)
            throw Error(plan.ErrorCode ?? "invalid_layout_baseline", plan.ErrorMessage ?? "Resolve the current synchronization conflict first.");
        var prepared = plan.Candidate!;
        var bindings = prepared.SymbolBindings.Where(b => seed.ContainsKey(b.SymbolOccurrenceId)).ToDictionary(b => b.SymbolOccurrenceId);
        var newNative = bindings.Values.Select(b => b.NativeObjectId).ToHashSet();
        var byScreen = new Dictionary<Guid, List<SchematicPlacementGeometry>>();
        var limits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in prepared.Schematic.Instances.OrderBy(s => Path(s.Metadata.Document), StringComparer.Ordinal))
        {
            var prototypes = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Where(s => newNative.Contains(Id(s.Id)))
                .OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
            if (prototypes.Length == 0) continue;
            var expectedObstacles = SchematicItemDelta.Index(sheets[Path(screen.Metadata.Document)].Items)
                .Where(pair => pair.Value is not Group).Select(pair => pair.Key).ToHashSet();
            SchematicPlacementGeometry? first = null;
            foreach (var chunk in prototypes.Chunk(256))
            {
                token.ThrowIfCancellationRequested();
                var request = new MeasureSchematicPlacement { Document = screen.Metadata.Document.Clone(),
                    ExpectedRevision = new() { Epoch = state.NativeRevision.Epoch, Sequence = state.NativeRevision.Sequence } };
                request.Candidates.Add(chunk.Select(s => s.Clone()));
                var measured = await measure(request, token);
                ValidateMeasurement(request, measured, Id(screen.Metadata.ScreenId), expectedObstacles);
                if (first is not null && (!first.PageBounds.Equals(measured.PageBounds)
                    || !first.Obstacles.OrderBy(o => o.Id.Value, StringComparer.Ordinal).SequenceEqual(measured.Obstacles.OrderBy(o => o.Id.Value, StringComparer.Ordinal))))
                    throw Error("inconsistent_layout_measurement", "The same native revision returned different page or obstacle geometry.");
                first ??= measured;
                Guid physical = Id(measured.ScreenId);
                if (!byScreen.TryGetValue(physical, out var observations)) byScreen.Add(physical, observations = []);
                observations.Add(measured.Clone());
                limits.UnionWith(measured.Limitations);
            }
        }
        token.ThrowIfCancellationRequested();
        if (regions.Select(r => r.ScreenId).Distinct().Count() != regions.Count
            || !regions.Select(r => r.ScreenId).ToHashSet().SetEquals(byScreen.Keys))
            throw Error("layout_page_regions_required", "Supply exactly one explicit usable page region for each affected physical screen, including title-block reservations.");
        var pages = new List<InitialLayoutSheet>();
        var bodies = new List<InitialLayoutBody>();
        foreach (var (physical, measurements) in byScreen)
        {
            var region = regions.Single(r => r.ScreenId == physical);
            var page = Bounds(measurements[0].PageBounds);
            if (measurements.Any(m => Bounds(m.PageBounds) != page) || !page.Contains(region.UsableBounds))
                throw Error("invalid_layout_page_region", "The usable region must fit the measured page for every repeated sheet instance.");
            var obstacles = measurements.SelectMany(m => m.Obstacles).GroupBy(o => Id(o.Id))
                .Select(g => new InitialLayoutObstacle(g.Key, Union(g.Select(o => Bounds(o.Bounds))))).ToArray();
            pages.Add(new(physical, region.UsableBounds, [.. obstacles, .. region.Reservations]));
            foreach (var geometry in measurements.SelectMany(m => m.Candidates).GroupBy(c => Id(c.Id)))
            {
                Guid[] occurrences = bindings.Values.Where(b => b.NativeObjectId == geometry.Key)
                    .Select(b => b.SymbolOccurrenceId).Order().ToArray();
                var relative = Union(geometry.Select(g => Relative(Bounds(g.Bounds), g.Anchor)));
                var anchor = seed[occurrences[0]];
                var memberComponents = added.Where(s => occurrences.Contains(s.Id)).Select(s => s.ComponentId).ToHashSet();
                var blockIds = desired.Engineering.Structure.Blocks.Where(b => b.ComponentIds.Any(memberComponents.Contains))
                    .Select(b => b.Id).Distinct().ToArray();
                // Distinct explicit block memberships can disagree for one
                // repeated physical symbol. Do not invent a preferred owner.
                if (blockIds.Length > 1)
                    throw Error("ambiguous_layout_group", "One physical symbol belongs to multiple structural blocks; resolve its placement grouping explicitly.");
                bodies.Add(new(geometry.Key, physical, occurrences, relative,
                    fixedOccurrences.Contains(occurrences[0]) ? new(Coordinates.MillimetersToNanometers(anchor.XMillimeters),
                        Coordinates.MillimetersToNanometers(anchor.YMillimeters)) : null,
                    blockIds.Length == 1 ? blockIds[0] : null));
            }
        }
        var layout = InitialSchematicLayout.Propose(pages, bodies, policy, token);
        if (!layout.CanPropose) return new(null, null, layout, null, limits.Order(StringComparer.Ordinal).ToArray());
        var placements = layout.Placements!.SelectMany(p => p.SymbolOccurrences.Select(id => (Id: id,
            Placement: seed[id] with { XMillimeters = Coordinates.NanometersToMillimeters(p.Anchor.XNm),
                YMillimeters = Coordinates.NanometersToMillimeters(p.Anchor.YNm) }))).ToDictionary(p => p.Id, p => p.Placement);
        var circuit = desired.Engineering.Circuit with { Symbols = desired.Engineering.Circuit.Symbols
            .Select(s => placements.TryGetValue(s.Id, out var p) ? s with { Placement = p } : s).ToArray() };
        var result = desired with { Engineering = desired.Engineering with { Circuit = circuit } };
        // Leave existing native bindings unchanged in the desired XML. The
        // ordinary synchronizer owns creation and publication of new bindings.
        _ = SchematicNativeCreationProjection.Project(state.Baseline, result.Engineering, state.KnowledgeLibraries, token);
        string xml = SchematicDesignXml.Write(result, state.KnowledgeLibraries);
        var refinement = LayoutRefinement.ForAddedSymbols(state.Baseline.Engineering.Circuit, circuit, state.NativeRevision, userInstructions);
        token.ThrowIfCancellationRequested();
        return new(result, xml, layout, refinement, limits.Order(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateMeasurement(MeasureSchematicPlacement request, SchematicPlacementGeometry measured,
        Guid screen, HashSet<Guid> expectedObstacles)
    {
        if (measured is null || !Equals(request.Document, measured.Document) || !Equals(request.ExpectedRevision, measured.Revision)
            || Id(measured.ScreenId) != screen)
            throw Error("stale_layout_measurement", "Placement measurements must identify the exact requested sheet and native revision.");
        _ = Bounds(measured.PageBounds);
        if (measured.Obstacles.Select(o => Id(o.Id)).Distinct().Count() != measured.Obstacles.Count
            || !expectedObstacles.SetEquals(measured.Obstacles.Select(o => Id(o.Id)))
            || measured.Candidates.Select(c => Id(c.Id)).Distinct().Count() != measured.Candidates.Count
            || !request.Candidates.Select(c => Id(c.Id)).ToHashSet().SetEquals(measured.Candidates.Select(c => Id(c.Id))))
            throw Error("incomplete_layout_measurement", "Native geometry must include every exact requested candidate and existing obstacle once.");
        foreach (var obstacle in measured.Obstacles) _ = Bounds(obstacle.Bounds);
        foreach (var candidate in measured.Candidates)
        {
            if (!Equals(candidate.Anchor, request.Candidates.Single(c => c.Id.Equals(candidate.Id)).Position))
                throw Error("invalid_layout_anchor", "The measured candidate anchor differs from its requested placement.");
            _ = Bounds(candidate.Bounds);
        }
    }

    private static Guid Id(KIID? value) => value is not null && Guid.TryParseExact(value.Value, "D", out var id)
        && id != Guid.Empty && value.Value == id.ToString("D") ? id
        : throw Error("invalid_layout_identity", "Native measurements require canonical nonempty UUIDs.");

    private static PresentationBounds Bounds(Box2? box)
    {
        if (box?.Position is null || box.Size is null || box.Size.XNm < 0 || box.Size.YNm < 0)
            throw Error("invalid_layout_geometry", "Native geometry requires nonnegative dimensions and an explicit position.");
        try { return new(box.Position.XNm, box.Position.YNm,
            checked(box.Position.XNm + box.Size.XNm), checked(box.Position.YNm + box.Size.YNm)); }
        catch (OverflowException) { throw Error("invalid_layout_geometry", "Native geometry exceeds the supported coordinate range."); }
    }

    private static PresentationBounds Relative(PresentationBounds bounds, Vector2 anchor)
    {
        try { return new(checked(bounds.LeftNm - anchor.XNm), checked(bounds.TopNm - anchor.YNm),
            checked(bounds.RightNm - anchor.XNm), checked(bounds.BottomNm - anchor.YNm)); }
        catch (OverflowException) { throw Error("invalid_layout_geometry", "Relative geometry exceeds the supported coordinate range."); }
    }

    private static PresentationBounds Union(IEnumerable<PresentationBounds> boxes)
    {
        var all = boxes.ToArray();
        return new(all.Min(b => b.LeftNm), all.Min(b => b.TopNm), all.Max(b => b.RightNm), all.Max(b => b.BottomNm));
    }
    private static string Path(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(id => id.Value));
    private static AutomationException Error(string code, string message) => new(code, message);
}
