using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Explicit usable page and reserved artwork/title-block areas. The
/// physical screen UUID, not a filename or displayed sheet, owns these limits.</summary>
public sealed record SchematicLayoutRegion(Guid ScreenId, PresentationBounds UsableBounds,
    IReadOnlyList<InitialLayoutObstacle> Reservations);

/// <summary>Where a new symbol that connects to already placed pins was aimed (cn1-wiring-intent.md §10):
/// the grid point that puts the mean of its connected pins on the mean of the partner pins it connects to
/// on the same sheet. <paramref name="PartnerPins"/> are those partners' placed pin identities.</summary>
public sealed record InitialLayoutPreferredAnchor(Guid BodyId, Guid ScreenId, IReadOnlyList<Guid> SymbolOccurrences,
    PresentationPoint Anchor, IReadOnlyList<Guid> PartnerPins);

/// <summary>A new power symbol paired with the pin it will sit on (cn1-wiring-intent.md §10): turned by
/// <paramref name="RotationDegrees"/> so that its pin faces the partner pin, and placed so that its pin lands
/// on the end of that pin's connection stub. When <paramref name="Attached"/> is false the pairing was
/// dropped for <paramref name="DroppedReason"/> (<c>collision</c>, <c>page_overflow</c> or
/// <c>no_matching_rotation</c>) and the symbol keeps its free placement.</summary>
public sealed record InitialLayoutPowerAttachment(Guid CarrierBodyId, Guid ScreenId, IReadOnlyList<Guid> CarrierOccurrences,
    Guid CarrierComponentId, PinEndpoint Partner, Guid PartnerSymbolId, Guid PartnerPlacedPinId, int RotationDegrees,
    PresentationPoint? Anchor, bool Attached, string? DroppedReason);

public sealed record SchematicInitialLayoutResult(SchematicDesign? DesiredDesign, string? DesiredXml,
    InitialLayoutCandidate Layout, LayoutRefinement? Refinement, IReadOnlyList<string> Limitations,
    IReadOnlyList<InitialLayoutPreferredAnchor> PreferredAnchors, IReadOnlyList<InitialLayoutPowerAttachment> PowerAttachments)
{
    public bool CanPropose => DesiredDesign is not null && Layout.CanPropose;
    public bool RequiresNativeConnectivityValidation => true;
    public bool RequiresVisualReview => true;
}

/// <summary>Read-only preparation for known-part additions. Uses detached native measurements,
/// never dummy symbol sizes or native insertion. A result is a proposed XML edit, not an accepted
/// layout or a sync commit. Unconnected additions are placed exactly as before. Additions that come
/// with XML connections (cn1-wiring-intent.md §10) are admitted only for an editor that reports it can
/// draw and verify them; their symbols go near the pins they connect to, keep room for every
/// connection stub and label they and their partners will receive, and new power symbols sit on
/// the end of their partner's stub.</summary>
public static class SchematicInitialLayoutPlanner
{
    private const string UnsupportedMessage = "Initial placement supports component additions without rewiring or changes to existing owners and parts.";

    public static async Task<SchematicInitialLayoutResult> ProposeAsync(NativeClient client,
        DesignRecoveryState state, InitialLayoutPolicy policy, IReadOnlyList<SchematicLayoutRegion> regions,
        string userInstructions, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != state.InstanceId.ToString("D"))
            throw Error("layout_instance_mismatch", "Measure only the native instance recorded by this design.");
        return await ProposeMeasuredAsync(state, policy, regions, userInstructions,
            (request, cancellation) => client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(request, cancellation),
            session, token);
    }

    internal static Task<SchematicInitialLayoutResult> ProposeMeasuredAsync(DesignRecoveryState state,
        InitialLayoutPolicy policy, IReadOnlyList<SchematicLayoutRegion> regions, string userInstructions,
        Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        CancellationToken token = default) => ProposeMeasuredAsync(state, policy, regions, userInstructions, measure, null, token);

    /// <param name="session">The handshake of the recorded instance. Only a session that advertises
    /// <see cref="SchematicConnectedAddition.NativeCapability"/> admits additions with connections.</param>
    internal static async Task<SchematicInitialLayoutResult> ProposeMeasuredAsync(DesignRecoveryState state,
        InitialLayoutPolicy policy, IReadOnlyList<SchematicLayoutRegion> regions, string userInstructions,
        Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        AutomationSession? session, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = SchematicElectricalCheckpoints.Require(state);
        var desired = DesignRecoveryStore.ReadDesired(state);
        // §10 admission: a plain supported addition, or a connected addition the classification admits.
        bool connected = false;
        if (!SchematicNativeCreationProjection.IsSupportedAddition(state.Baseline, desired.Engineering))
        {
            var shape = SchematicConnectedAddition.Classify(state, desired, session, token);
            if (shape.Kind != SchematicConnectedAdditionKind.Admitted)
                throw Error("unsupported_layout_creation", SchematicConnectedAddition.Advertises(session, state.InstanceId) ? UnsupportedMessage
                    : UnsupportedMessage + " Additions that also connect pins need an editor that reports it can draw and verify XML connections; this one does not.");
            connected = true;
        }
        var oldIds = state.Baseline.Engineering.Circuit.Symbols.Select(s => s.Id).ToHashSet();
        var added = desired.Engineering.Circuit.Symbols.Where(s => !oldIds.Contains(s.Id)).ToArray();
        if (!added.Any(s => s.Placement is null))
            throw Error("no_missing_placement", "All new symbols already have positions; use scoped visual refinement to adjust them.");
        var sheets = state.Baseline.Schematic.Instances.ToDictionary(s => Path(s.Metadata.Document), StringComparer.Ordinal);
        // Identities need every occurrence of each definition. Creation always supplies them; the only other
        // shape here, a new unit of an existing component, is refused by creation itself, so report exactly
        // the synchronization plan's code for this record instead of computing any identity.
        if (SchematicNativeCreationProjection.OmittedOccurrences(desired.Engineering.Circuit, added).Count != 0)
        {
            var refused = SchematicSynchronizationPlanner.Plan(state, session, token);
            throw Error(refused.ErrorCode ?? "unsupported_layout_creation",
                refused.ErrorMessage ?? "Initial placement requires component additions without changes to existing owners and parts.");
        }
        // Seed exactly the physical symbols creation will make, including units placed on
        // another sheet than their component; an inconsistent placement is rejected here.
        var groups = SchematicNativeCreationProjection.PhysicalSymbols(state.Baseline, desired.Engineering.Circuit, added, token);
        var seed = new Dictionary<Guid, SymbolPlacement>();
        var fixedOccurrences = new HashSet<Guid>();
        foreach (var group in groups)
        {
            var specified = group.Occurrences.Where(s => s.Placement is not null).ToArray();
            var placement = specified.FirstOrDefault()?.Placement ?? new(0, 0, 0, false, false, false);
            if (specified.Any(s => !SchematicOrientation.Equivalent(s.Placement, placement)))
                throw Error("shared_symbol_placement_conflict", "Repeated instances of one physical symbol request different positions or orientations.");
            foreach (var occurrence in group.Occurrences)
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
        { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(seeded, state.KnowledgeLibraries)) }, session, token);
        if (!plan.CanPrepare)
            throw Error(plan.ErrorCode ?? "invalid_layout_baseline", plan.ErrorMessage ?? "Resolve the current synchronization conflict first.");
        // The seeded plan supplies the connection intent (§10); an unconnected addition has none.
        if (connected != plan.Connections is not null)
            throw Error("unsupported_layout_creation", UnsupportedMessage);
        var prepared = plan.Candidate!;
        var bindings = prepared.SymbolBindings.Where(b => seed.ContainsKey(b.SymbolOccurrenceId)).ToDictionary(b => b.SymbolOccurrenceId);
        var newNative = bindings.Values.Select(b => b.NativeObjectId).ToHashSet();
        var byScreen = new Dictionary<Guid, List<SchematicPlacementGeometry>>();
        var byPath = new Dictionary<string, List<SchematicPlacementGeometry>>(StringComparer.Ordinal);
        var prototypesByPath = new Dictionary<string, SchematicSymbolInstance[]>(StringComparer.Ordinal);
        var obstaclesByPath = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        var limits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in prepared.Schematic.Instances.OrderBy(s => Path(s.Metadata.Document), StringComparer.Ordinal))
        {
            var prototypes = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Where(s => newNative.Contains(Id(s.Id)))
                .OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToArray();
            if (prototypes.Length == 0) continue;
            string path = Path(screen.Metadata.Document);
            var expectedObstacles = SchematicItemDelta.Index(sheets[path].Items)
                .Where(pair => pair.Value is not Group).Select(pair => pair.Key).ToHashSet();
            prototypesByPath.Add(path, prototypes);
            obstaclesByPath.Add(path, expectedObstacles);
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
                if (!byPath.TryGetValue(path, out var pathObservations)) byPath.Add(path, pathObservations = []);
                pathObservations.Add(measured.Clone());
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
        ConnectedLayout? connection = null;
        if (plan.Connections is { } intent)
        {
            connection = new ConnectedLayout(intent, state, policy, byPath, prototypesByPath, obstaclesByPath, added, measure, token);
            await connection.PrepareAsync(pages, bodies);
            limits.UnionWith(connection.Limitations);
        }
        var layout = InitialSchematicLayout.Propose(pages, bodies, policy, token);
        var preferredAnchors = connection?.PreferredAnchors(bodies) ?? [];
        if (!layout.CanPropose) return new(null, null, layout, null, limits.Order(StringComparer.Ordinal).ToArray(), preferredAnchors, []);
        var rotations = new Dictionary<Guid, int>();
        IReadOnlyList<InitialLayoutPowerAttachment> attachments = [];
        if (connection is not null)
            (layout, attachments) = connection.AttachPowerSymbols(layout, pages, bodies, rotations);
        var placements = layout.Placements!.SelectMany(p => p.SymbolOccurrences.Select(id => (Id: id,
            Placement: seed[id] with { XMillimeters = Coordinates.NanometersToMillimeters(p.Anchor.XNm),
                YMillimeters = Coordinates.NanometersToMillimeters(p.Anchor.YNm),
                RotationDegrees = rotations.TryGetValue(p.BodyId, out int rotation) ? rotation : seed[id].RotationDegrees }))).ToDictionary(p => p.Id, p => p.Placement);
        var circuit = desired.Engineering.Circuit with { Symbols = desired.Engineering.Circuit.Symbols
            .Select(s => placements.TryGetValue(s.Id, out var p) ? s with { Placement = p } : s).ToArray() };
        var result = desired with { Engineering = desired.Engineering with { Circuit = circuit } };
        // Leave existing native bindings unchanged in the desired XML. The
        // ordinary synchronizer owns creation and publication of new bindings.
        _ = SchematicNativeCreationProjection.Project(state.Baseline, result, state.KnowledgeLibraries, token, allowConnected: connected);
        string xml = SchematicDesignXml.Write(result, state.KnowledgeLibraries);
        var refinement = LayoutRefinement.ForAddedSymbols(state.Baseline.Engineering.Circuit, circuit, state.NativeRevision, userInstructions);
        token.ThrowIfCancellationRequested();
        return new(result, xml, layout, refinement, limits.Order(StringComparer.Ordinal).ToArray(), preferredAnchors, attachments);
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
            || !request.Candidates.Select(c => Id(c.Id)).ToHashSet().SetEquals(measured.Candidates.Select(c => Id(c.Id)))
            || measured.ItemCandidates.Count != request.ItemCandidates.Count)
            throw Error("incomplete_layout_measurement", "Native geometry must include every exact requested candidate and existing obstacle once.");
        foreach (var obstacle in measured.Obstacles) _ = Bounds(obstacle.Bounds);
        foreach (var candidate in measured.Candidates)
        {
            if (!Equals(candidate.Anchor, request.Candidates.Single(c => c.Id.Equals(candidate.Id)).Position))
                throw Error("invalid_layout_anchor", "The measured candidate anchor differs from its requested placement.");
            _ = Bounds(candidate.Bounds);
        }
        // Label prototypes answer in request order, at their requested position, without pins.
        for (int i = 0; i < request.ItemCandidates.Count; i++)
        {
            var (id, item) = SchematicItemDelta.Index([request.ItemCandidates[i]]).Single();
            var position = item.Descriptor.FindFieldByName("position")?.Accessor.GetValue(item) as Vector2;
            var reply = measured.ItemCandidates[i];
            if (Id(reply.Id) != id || reply.SymbolPins is not null)
                throw Error("incomplete_layout_measurement", "Native label measurements must answer each requested label prototype once, in order.");
            if (!Equals(reply.Anchor, position))
                throw Error("invalid_layout_anchor", "The measured label anchor differs from its requested position.");
            _ = Bounds(reply.Bounds);
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

    /// <summary>The connection-aware part of a proposal (cn1-wiring-intent.md §10), built from the seeded plan's
    /// connection intent and the editor's own measurements at the recorded revision. It aims each new symbol at
    /// the pins it connects to, reserves room for every stub and label that realization will draw beside new and
    /// existing pins, and afterwards seats new power symbols on the end of their partner pin's stub.</summary>
    private sealed class ConnectedLayout(SchematicConnectionIntent intent, DesignRecoveryState state,
        InitialLayoutPolicy layoutPolicy, Dictionary<string, List<SchematicPlacementGeometry>> byPath,
        Dictionary<string, SchematicSymbolInstance[]> prototypesByPath, Dictionary<string, HashSet<Guid>> obstaclesByPath,
        SymbolOccurrence[] added,
        Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure, CancellationToken token)
    {
        private static readonly int[] Rotations = [0, 90, 180, 270];
        private readonly SortedSet<string> limitations = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, ScreenWork> work = [];
        private SchematicConnectionPolicy policy = null!;

        public IReadOnlyCollection<string> Limitations => limitations;

        /// <summary>Measure what connected placement needs and fold it into <paramref name="pages"/> and
        /// <paramref name="bodies"/>: preferred anchors, reserved bounds and keep-out areas.</summary>
        public async Task PrepareAsync(List<InitialLayoutSheet> pages, List<InitialLayoutBody> bodies)
        {
            // Stub lengths and label sizes come from the project's own connection grid and text size (§6.1).
            policy = SchematicConnectionPolicy.FromSnapshot(state.Observed);
            var screenIds = pages.Select(p => p.Id).ToHashSet();
            foreach (var record in intent.Screens.Where(s => screenIds.Contains(s.ScreenId)).OrderBy(s => s.ScreenId))
            {
                token.ThrowIfCancellationRequested();
                var screen = new ScreenWork(record, bodies.Where(b => b.SheetId == record.ScreenId).ToDictionary(b => b.Id));
                foreach (var path in record.InstancePathKeys)
                    if (!byPath.TryGetValue(path, out var observed) || !prototypesByPath.ContainsKey(path))
                        throw Error("incomplete_layout_measurement", "Every instance of a sheet that receives new connected symbols must be measured.");
                    else screen.Views.Add(new View(path, observed));
                foreach (var view in screen.Views)
                    if (view.Measurements.Any(m => !m.PinGeometryAvailable))
                        throw Error("incomplete_layout_measurement", "This KiCad does not report exact pin geometry, which placing connected symbols needs.");
                work.Add(record.ScreenId, screen);
                Collect(screen);
                await MeasureAsync(screen);
                Reserve(screen);
            }
            for (int i = 0; i < pages.Count; i++)
                if (work.TryGetValue(pages[i].Id, out var screen) && screen.KeepOuts.Count != 0)
                    pages[i] = pages[i] with { Obstacles = [.. pages[i].Obstacles, .. screen.KeepOuts] };
            for (int i = 0; i < bodies.Count; i++)
                if (work.TryGetValue(bodies[i].SheetId, out var screen) && screen.Bodies.TryGetValue(bodies[i].Id, out var updated))
                    bodies[i] = updated;
        }

        public IReadOnlyList<InitialLayoutPreferredAnchor> PreferredAnchors(IReadOnlyList<InitialLayoutBody> bodies) =>
        [
            .. bodies.Where(b => b.PreferredAnchor is not null).OrderBy(b => b.SheetId).ThenBy(b => b.Id).Select(b =>
                new InitialLayoutPreferredAnchor(b.Id, b.SheetId, b.SymbolOccurrences.Order().ToArray(), b.PreferredAnchor!.Value,
                    work[b.SheetId].Partners.TryGetValue(b.Id, out var partners) ? partners.Order().ToArray() : []))
        ];

        // ---- which pins matter, and where they are ----

        private void Collect(ScreenWork screen)
        {
            var representative = screen.Views[0];
            foreach (var island in screen.Record.Islands.OrderBy(i => i.NetId).ThenBy(i => i.SheetPathKey, StringComparer.Ordinal))
            {
                if (island.SheetPathKey != representative.Path)
                    throw Error("inconsistent_layout_measurement", "A connection island is not on its sheet's representative instance.");
                var kinds = Kinds(island);
                // Partners: drawn pins of symbols whose position is already known. Hidden power pins join by name only.
                var partners = island.Members.Where(m => m.Role != ConnectionMemberRole.ImplicitPower && !IsFree(screen, m.Pin.SymbolId))
                    .Select(m => (m.Pin, At: Absolute(screen, m.Pin))).ToArray();
                foreach (var member in island.Members.Where(m => m.Role != ConnectionMemberRole.ImplicitPower && IsFree(screen, m.Pin.SymbolId)))
                {
                    if (partners.Length == 0) continue;
                    var own = Offset(screen, member.Pin);
                    if (!screen.Aims.TryGetValue(member.Pin.SymbolId, out var aim)) screen.Aims.Add(member.Pin.SymbolId, aim = new());
                    aim.Own.Add(own);
                    aim.Partners.AddRange(partners.Select(p => p.At));
                    aim.PartnerPins.UnionWith(partners.Select(p => p.Pin.PlacedPinId));
                }
                foreach (var member in island.Members.Where(m => m.RequiresStub))
                    screen.Stubs.Add(Stub(screen, member.Pin, kinds, island.LabelText));
                // Realization names an unlabelled existing connection at its first join candidate that has room.
                if (island.JoinRequired && island.JoinCandidates.Count != 0)
                    screen.Stubs.Add(Stub(screen, island.JoinCandidates[0], kinds, island.LabelText));
                foreach (var sheetId in island.ChildSheetSymbolIds.Order())
                    screen.Strips.Add(new(sheetId, representative.ObstacleBounds(sheetId), kinds, island.LabelText));
                // New coordinate-free single-pin global power symbols that have a same-net stub pin to sit on.
                if (island.Members.Any(m => m.RequiresStub))
                    foreach (var member in island.Members.Where(m => m.Role == ConnectionMemberRole.PowerCarrier && IsFree(screen, m.Pin.SymbolId)))
                        if (SinglePinGlobalCarrier(representative, member.Pin.SymbolId))
                            screen.Carriers.TryAdd(member.Pin.SymbolId, (island, member));
            }
        }

        private StubWork Stub(ScreenWork screen, ConnectionPlacedPin pin, IReadOnlyList<ConnectionLabelKind> kinds, string text)
        {
            var anchor = screen.Views[0].Pin(pin);
            var outward = SchematicConnectionGeometry.Outward(anchor);
            foreach (var view in screen.Views.Skip(1))
            {
                var other = view.Pin(pin);
                if (other.BodyDirectionX != anchor.BodyDirectionX || other.BodyDirectionY != anchor.BodyDirectionY)
                    throw Error("inconsistent_layout_measurement", "A connected pin faces different ways on instances of one repeated sheet.");
            }
            return new(pin, IsFree(screen, pin.SymbolId), new(anchor.Position.XNm, anchor.Position.YNm), outward, kinds, text);
        }

        private bool IsFree(ScreenWork screen, Guid symbol) => screen.Bodies.TryGetValue(symbol, out var body) && body.FixedAnchor is null;

        // The absolute position of a pin whose symbol is existing or explicitly placed, identical on every instance.
        private PresentationPoint Absolute(ScreenWork screen, ConnectionPlacedPin pin)
        {
            var at = screen.Views[0].Pin(pin).Position;
            if (screen.Views.Skip(1).Any(v => !Equals(v.Pin(pin).Position, at)))
                throw Error("inconsistent_layout_measurement", "A connected pin is drawn at different positions on instances of one repeated sheet.");
            return new(at.XNm, at.YNm);
        }

        // A free new symbol's pin relative to its measured anchor, identical on every instance.
        private PresentationPoint Offset(ScreenWork screen, ConnectionPlacedPin pin)
        {
            PresentationPoint? first = null;
            foreach (var view in screen.Views)
            {
                var at = view.Pin(pin).Position; var anchor = view.CandidateAnchor(pin.SymbolId);
                var offset = new PresentationPoint(at.XNm - anchor.XNm, at.YNm - anchor.YNm);
                if (first is not null && first != offset)
                    throw Error("inconsistent_layout_measurement", "A new symbol's pins are measured differently on instances of one repeated sheet.");
                first = offset;
            }
            return first!.Value;
        }

        private bool SinglePinGlobalCarrier(View view, Guid symbol)
        {
            var prototype = prototypesByPath[view.Path].Single(s => Id(s.Id) == symbol);
            return prototype.Definition?.Type == SchematicSymbolType.SstGlobalPower
                && SchematicPlacedPins.Active(prototype, prototype.Unit?.Unit ?? 1).Count() == 1;
        }

        private static IReadOnlyList<ConnectionLabelKind> Kinds(ConnectionIsland island) => island.Scope == ConnectionScope.Global
            ? [ConnectionLabelKind.Global]
            : island.UplinkSheetSymbolId is null ? [ConnectionLabelKind.Local] : [ConnectionLabelKind.Local, ConnectionLabelKind.Hierarchical];

        // ---- round 2: label prototypes and rotated power-symbol probes ----

        private async Task MeasureAsync(ScreenWork screen)
        {
            long first = SchematicConnectionPolicy.StubMultiples[0] * policy.GridNm;
            var positions = new Dictionary<Combo, PresentationPoint>();
            foreach (var stub in screen.Stubs)
                foreach (var kind in stub.Kinds)
                    positions.TryAdd(new(kind, stub.Text, SchematicConnectionGeometry.Spin(stub.Outward)), Step(stub.At, stub.Outward, first));
            foreach (var strip in screen.Strips)
                foreach (var (outward, x) in new[] { ((-1, 0), strip.Sheet.LeftNm), ((1, 0), strip.Sheet.RightNm) })
                    foreach (var kind in strip.Kinds)
                        positions.TryAdd(new(kind, strip.Text, SchematicConnectionGeometry.Spin(outward)),
                            Step(new(x, strip.Sheet.TopNm + policy.SheetPinPitchNm), outward, first));
            var labels = positions.Keys.OrderBy(c => c.Kind).ThenBy(c => c.Text, StringComparer.Ordinal).ThenBy(c => c.Spin).ToArray();
            var ids = labels.ToDictionary(c => c, c => SchematicConnectionIdentity.Probe(intent.NativeRevision, screen.Record.ScreenId,
                SchematicConnectionRealizer.Descriptor(c.Kind), c.Text, c.Spin, c.Shape));
            var probes = new List<(Guid Carrier, int Rotation, Guid Probe)>();
            foreach (var carrier in screen.Carriers.Keys.Order())
                foreach (int rotation in Rotations)
                    probes.Add((carrier, rotation, SchematicConnectionIdentity.Probe(intent.NativeRevision, screen.Record.ScreenId,
                        SchematicSymbolInstance.Descriptor, "", SchematicLabelSpinStyle.SlssUnknown, SchematicLabelShape.SlshUnknown, carrier, rotation)));
            if (labels.Length == 0 && probes.Count == 0) return;
            var items = labels.Select(c => (Label: (Combo?)c, Probe: ((Guid Carrier, int Rotation, Guid Probe)?)null))
                .Concat(probes.Select(p => (Label: (Combo?)null, Probe: ((Guid Carrier, int Rotation, Guid Probe)?)p))).ToArray();
            foreach (var view in screen.Views)
            {
                var prototypes = prototypesByPath[view.Path].ToDictionary(s => Id(s.Id));
                foreach (var chunk in items.Chunk(SchematicConnectionRealizer.MaxMeasuredCandidates))
                {
                    token.ThrowIfCancellationRequested();
                    // Round 1 already proved this document is exactly the requested instance path.
                    var request = new MeasureSchematicPlacement { Document = view.Measurements[0].Document.Clone(),
                        ExpectedRevision = new() { Epoch = state.NativeRevision.Epoch, Sequence = state.NativeRevision.Sequence } };
                    foreach (var (label, probe) in chunk)
                        if (label is { } combo)
                            request.ItemCandidates.Add(Any.Pack(SchematicConnectionRealizer.LabelPayload(combo.Kind, ids[combo],
                                new() { XNm = positions[combo].XNm, YNm = positions[combo].YNm }, combo.Text, combo.Spin, policy)));
                        else if (probe is { } p)
                        {
                            var symbol = prototypes[p.Carrier].Clone();
                            symbol.Id = new() { Value = p.Probe.ToString("D") };
                            symbol.Transform = new() { Orientation = (SchematicSymbolOrientation)(p.Rotation / 90 + 1), MirrorX = false, MirrorY = false };
                            request.Candidates.Add(symbol);
                        }
                    var measured = await measure(request, token);
                    ValidateMeasurement(request, measured, screen.Record.ScreenId, obstaclesByPath[view.Path]);
                    limitations.UnionWith(measured.Limitations);
                    int index = 0;
                    foreach (var (label, probe) in chunk)
                    {
                        if (label is not { } combo) continue;
                        var reply = measured.ItemCandidates[index++];
                        var envelope = Relative(Bounds(reply.Bounds), reply.Anchor);
                        screen.Envelopes[combo] = screen.Envelopes.TryGetValue(combo, out var prior) ? Union([prior, envelope]) : envelope;
                    }
                    foreach (var (_, probe) in chunk)
                    {
                        if (probe is not { } p) continue;
                        var reply = measured.Candidates.Single(c => Id(c.Id) == p.Probe);
                        var pins = reply.SymbolPins;
                        if (pins is null || !pins.Complete || pins.Pins.Count != 1)
                            throw Error("incomplete_layout_measurement", "KiCad does not report the single pin of new power symbol "
                                + p.Carrier.ToString("D") + " when it is turned, so it cannot be seated on its partner pin.");
                        var bounds = Relative(Bounds(reply.Bounds), reply.Anchor);
                        var pin = pins.Pins[0];
                        var geometry = new ProbeGeometry(bounds, new(pin.Position.XNm - reply.Anchor.XNm, pin.Position.YNm - reply.Anchor.YNm),
                            (pin.BodyDirectionX, pin.BodyDirectionY));
                        var key = (p.Carrier, p.Rotation);
                        if (screen.Probes.TryGetValue(key, out var earlier))
                        {
                            if (earlier.Pin != geometry.Pin || earlier.BodyDirection != geometry.BodyDirection)
                                throw Error("inconsistent_layout_measurement", "A turned power symbol is measured differently on instances of one repeated sheet.");
                            geometry = geometry with { Bounds = Union([earlier.Bounds, geometry.Bounds]) };
                        }
                        screen.Probes[key] = geometry;
                    }
                }
            }
        }

        // ---- reservations, keep-outs and preferred anchors ----

        private void Reserve(ScreenWork screen)
        {
            long length = SchematicConnectionPolicy.StubMultiples[0] * policy.GridNm;
            var zones = new Dictionary<Guid, List<PresentationBounds>>();
            foreach (var stub in screen.Stubs)
            {
                var end = Step(stub.At, stub.Outward, length);
                var spin = SchematicConnectionGeometry.Spin(stub.Outward);
                // The stub and the clearance realization keeps around its end, and every label that may end it.
                var zone = Union([Inflate(Span(stub.At, end), policy.ClearanceNm), Inflate(Span(end, end), policy.ClearanceNm),
                    .. stub.Kinds.Select(kind => Translate(screen.Envelopes[new(kind, stub.Text, spin)], end))]);
                screen.Zones[stub.Pin.PlacedPinId] = zone;
                if (screen.Bodies.TryGetValue(stub.Pin.SymbolId, out var body))
                {
                    // A new symbol reserves the zone relative to its anchor: measured at its seed, or its explicit position.
                    var anchor = stub.Free ? screen.Views[0].CandidateAnchor(stub.Pin.SymbolId) : ToVector(body.FixedAnchor!.Value);
                    if (!zones.TryGetValue(body.Id, out var list)) zones.Add(body.Id, list = []);
                    list.Add(Relative(zone, anchor));
                }
                else if (screen.KeepOuts.All(k => k.Id != stub.Pin.PlacedPinId))
                    screen.KeepOuts.Add(new(stub.Pin.PlacedPinId, zone));
            }
            foreach (var strip in screen.Strips)
            {
                foreach (var (outward, side) in new[] { ((-1, 0), "left"), ((1, 0), "right") })
                {
                    var spin = SchematicConnectionGeometry.Spin(outward);
                    long depth = strip.Kinds.Max(kind => outward.Item1 < 0 ? -screen.Envelopes[new(kind, strip.Text, spin)].LeftNm
                        : screen.Envelopes[new(kind, strip.Text, spin)].RightNm);
                    long reach = checked(length + Math.Max(0, depth) + policy.ClearanceNm);
                    var band = outward.Item1 < 0
                        ? new PresentationBounds(checked(strip.Sheet.LeftNm - reach), strip.Sheet.TopNm, strip.Sheet.LeftNm, strip.Sheet.BottomNm)
                        : new PresentationBounds(strip.Sheet.RightNm, strip.Sheet.TopNm, checked(strip.Sheet.RightNm + reach), strip.Sheet.BottomNm);
                    var id = KeepOutId(screen.Record.ScreenId, strip.SheetId, side);
                    int existing = screen.KeepOuts.FindIndex(k => k.Id == id);
                    if (existing < 0) screen.KeepOuts.Add(new(id, band));
                    else screen.KeepOuts[existing] = new(id, Union([screen.KeepOuts[existing].Bounds, band]));
                }
            }
            foreach (var (id, body) in screen.Bodies.ToArray())
            {
                PresentationPoint? preferred = null;
                if (body.FixedAnchor is null && screen.Aims.TryGetValue(id, out var aim) && aim.Partners.Count != 0)
                {
                    // §10: snap to grid (mean partner anchor − mean own member-pin offset).
                    decimal x = aim.Partners.Average(p => (decimal)p.XNm) - aim.Own.Average(p => (decimal)p.XNm);
                    decimal y = aim.Partners.Average(p => (decimal)p.YNm) - aim.Own.Average(p => (decimal)p.YNm);
                    preferred = new(Snap(x), Snap(y));
                    screen.Partners[id] = aim.PartnerPins;
                }
                var reserved = zones.TryGetValue(id, out var list) ? Union([body.RelativeBounds, .. list]) : null;
                if (preferred is not null || reserved is not null)
                    screen.Bodies[id] = body with { PreferredAnchor = preferred, ReservedRelativeBounds = reserved };
            }
        }

        private long Snap(decimal value)
        {
            long grid = layoutPolicy.GridNm;
            try { return checked((long)(decimal.Round(value / grid, MidpointRounding.AwayFromZero) * grid)); }
            catch (OverflowException) { throw Error("invalid_layout_geometry", "A preferred position exceeds the supported coordinate range."); }
        }

        // ---- power symbols seated on their partner pin's stub (§10 power attachment) ----

        public (InitialLayoutCandidate Layout, IReadOnlyList<InitialLayoutPowerAttachment> Attachments) AttachPowerSymbols(
            InitialLayoutCandidate layout, IReadOnlyList<InitialLayoutSheet> pages, IReadOnlyList<InitialLayoutBody> bodies,
            Dictionary<Guid, int> rotations)
        {
            var placements = layout.Placements!.ToDictionary(p => (p.SheetId, p.BodyId));
            var attachments = new List<InitialLayoutPowerAttachment>();
            long length = SchematicConnectionPolicy.StubMultiples[0] * policy.GridNm;
            var components = added.ToDictionary(s => s.Id, s => s.ComponentId);
            foreach (var (screenId, screen) in work.OrderBy(w => w.Key))
            {
                if (screen.Carriers.Count == 0) continue;
                var page = pages.Single(p => p.Id == screenId);
                var available = new PresentationBounds(page.AvailableBounds.LeftNm + layoutPolicy.PageInsetNm, page.AvailableBounds.TopNm + layoutPolicy.PageInsetNm,
                    page.AvailableBounds.RightNm - layoutPolicy.PageInsetNm, page.AvailableBounds.BottomNm - layoutPolicy.PageInsetNm);
                var paired = new HashSet<Guid>();
                var seated = new List<(Guid Body, PresentationBounds Bounds)>();
                var carriers = screen.Carriers.Select(c => (Body: c.Key, c.Value.Island, c.Value.Member,
                        Component: components[bodies.Single(b => b.SheetId == screenId && b.Id == c.Key).SymbolOccurrences[0]]))
                    .OrderBy(c => c.Component).ThenBy(c => c.Body).ToArray();
                foreach (var carrier in carriers)
                {
                    token.ThrowIfCancellationRequested();
                    // Greedy pairing: the first same-net stub pin, in member order, that no earlier power symbol took.
                    var partner = carrier.Island.Members.FirstOrDefault(m => m.RequiresStub && m.Role == ConnectionMemberRole.Signal
                        && !paired.Contains(m.Pin.PlacedPinId));
                    if (partner is null) continue;
                    var body = bodies.Single(b => b.SheetId == screenId && b.Id == carrier.Body);
                    var anchor = screen.Views[0].Pin(partner.Pin);
                    var outward = SchematicConnectionGeometry.Outward(anchor);
                    var at = PartnerAt(screen, partner.Pin, placements);
                    InitialLayoutPowerAttachment Report(int rotation, PresentationPoint? where, bool attached, string? reason) =>
                        new(carrier.Body, screenId, body.SymbolOccurrences.Order().ToArray(), carrier.Component, partner.Pin.Endpoint,
                            partner.Pin.SymbolId, partner.Pin.PlacedPinId, rotation, where, attached, reason);
                    // The smallest turn whose pin points from the stub end back into the power symbol along the stub.
                    int? turn = Rotations.Cast<int?>().FirstOrDefault(r => screen.Probes[(carrier.Body, r!.Value)].BodyDirection == outward);
                    if (turn is not { } rotation)
                    {
                        attachments.Add(Report(0, null, false, "no_matching_rotation"));
                        continue;
                    }
                    var probe = screen.Probes[(carrier.Body, rotation)];
                    var end = Step(at, outward, length);
                    var seat = new PresentationPoint(checked(end.XNm - probe.Pin.XNm), checked(end.YNm - probe.Pin.YNm));
                    var bounds = Translate(probe.Bounds, seat);
                    string? refusal = !available.Contains(bounds) ? "page_overflow"
                        : Collides(screen, page, placements, seated, carrier.Body, partner.Pin, bounds) ? "collision" : null;
                    attachments.Add(Report(rotation, seat, refusal is null, refusal));
                    if (refusal is not null) continue;
                    paired.Add(partner.Pin.PlacedPinId);
                    seated.Add((carrier.Body, bounds));
                    rotations[carrier.Body] = rotation;
                    var old = placements[(screenId, carrier.Body)];
                    placements[(screenId, carrier.Body)] = old with { Anchor = seat, Bounds = bounds, Fixed = false };
                }
            }
            if (attachments.Count == 0) return (layout, attachments);
            return (layout with { Placements = placements.Values.OrderBy(p => p.SheetId).ThenBy(p => p.BodyId).ToArray() }, attachments);
        }

        // Where the partner pin ends up: measured for existing and explicitly placed symbols, or moved with its placed body.
        private PresentationPoint PartnerAt(ScreenWork screen, ConnectionPlacedPin pin,
            Dictionary<(Guid, Guid), InitialLayoutPlacement> placements)
        {
            if (!IsFree(screen, pin.SymbolId)) return Absolute(screen, pin);
            var offset = Offset(screen, pin);
            var placed = placements[(screen.Record.ScreenId, pin.SymbolId)].Anchor;
            return new(checked(placed.XNm + offset.XNm), checked(placed.YNm + offset.YNm));
        }

        // A seated power symbol keeps the layout clearance from everything except the symbol it attaches to, which it may
        // touch but not overlap, together with the stubs and labels of that symbol's other pins; it takes the place of
        // the label its partner pin would otherwise receive.
        private bool Collides(ScreenWork screen, InitialLayoutSheet page, Dictionary<(Guid, Guid), InitialLayoutPlacement> placements,
            List<(Guid Body, PresentationBounds Bounds)> seated, Guid carrier, ConnectionPlacedPin partner, PresentationBounds bounds)
        {
            long gap = layoutPolicy.ClearanceNm;
            var partnerZones = screen.Stubs.Where(s => s.Pin.SymbolId == partner.SymbolId && s.Pin.PlacedPinId != partner.PlacedPinId)
                .Select(s => screen.Zones[s.Pin.PlacedPinId]).ToArray();
            bool existingPartner = !screen.Bodies.ContainsKey(partner.SymbolId);
            var partnerPins = screen.Stubs.Where(s => s.Pin.SymbolId == partner.SymbolId).Select(s => s.Pin.PlacedPinId).ToHashSet();
            foreach (var obstacle in page.Obstacles)
            {
                if (obstacle.Id == partner.PlacedPinId) continue;
                bool open = existingPartner && (obstacle.Id == partner.SymbolId || partnerPins.Contains(obstacle.Id));
                if (open ? Overlaps(bounds, obstacle.Bounds) : Near(bounds, obstacle.Bounds, gap)) return true;
            }
            foreach (var ((sheet, body), placement) in placements)
            {
                if (sheet != screen.Record.ScreenId || body == carrier) continue;
                if (body == partner.SymbolId)
                {
                    // The partner's own envelope and its other pins' stubs and labels, at its placed position.
                    var real = Translate(screen.Bodies[body].RelativeBounds, placement.Anchor);
                    var shift = new PresentationPoint(placement.Anchor.XNm - Anchor(screen, body).XNm, placement.Anchor.YNm - Anchor(screen, body).YNm);
                    if (Overlaps(bounds, real) || partnerZones.Any(z => Overlaps(bounds, Translate(z, shift)))) return true;
                    continue;
                }
                if (Near(bounds, placement.Bounds, gap)) return true;
            }
            return seated.Any(s => Near(bounds, s.Bounds, gap));
        }

        // The position a new body's zones were computed at: its seed measurement, or its explicit position.
        private PresentationPoint Anchor(ScreenWork screen, Guid body)
        {
            if (screen.Bodies[body].FixedAnchor is { } fixedAnchor) return fixedAnchor;
            var seed = screen.Views[0].CandidateAnchor(body);
            return new(seed.XNm, seed.YNm);
        }

        private static Guid KeepOutId(Guid screen, Guid sheet, string side)
        {
            byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-initial-layout-keep-out-v1\n" + screen.ToString("D") + "\n"
                + sheet.ToString("D") + "\n" + side));
            b[6] = (byte)((b[6] & 0x0F) | 0x80); b[8] = (byte)((b[8] & 0x3F) | 0x80);
            return new Guid(b.AsSpan(0, 16), bigEndian: true);
        }

        private static PresentationPoint Step(PresentationPoint at, (int Dx, int Dy) outward, long length) =>
            new(checked(at.XNm + outward.Dx * length), checked(at.YNm + outward.Dy * length));
        private static PresentationBounds Span(PresentationPoint a, PresentationPoint b) =>
            new(Math.Min(a.XNm, b.XNm), Math.Min(a.YNm, b.YNm), Math.Max(a.XNm, b.XNm), Math.Max(a.YNm, b.YNm));
        private static PresentationBounds Inflate(PresentationBounds b, long by) =>
            new(checked(b.LeftNm - by), checked(b.TopNm - by), checked(b.RightNm + by), checked(b.BottomNm + by));
        private static PresentationBounds Translate(PresentationBounds b, PresentationPoint p) =>
            new(checked(b.LeftNm + p.XNm), checked(b.TopNm + p.YNm), checked(b.RightNm + p.XNm), checked(b.BottomNm + p.YNm));
        private static Vector2 ToVector(PresentationPoint p) => new() { XNm = p.XNm, YNm = p.YNm };
        private static bool Overlaps(PresentationBounds a, PresentationBounds b) =>
            a.LeftNm < b.RightNm && b.LeftNm < a.RightNm && a.TopNm < b.BottomNm && b.TopNm < a.BottomNm;
        private static bool Near(PresentationBounds a, PresentationBounds b, long gap) =>
            (decimal)a.LeftNm - gap < b.RightNm && (decimal)a.RightNm + gap > b.LeftNm
            && (decimal)a.TopNm - gap < b.BottomNm && (decimal)a.BottomNm + gap > b.TopNm;

        private sealed record Combo(ConnectionLabelKind Kind, string Text, SchematicLabelSpinStyle Spin)
        {
            public SchematicLabelShape Shape => Kind == ConnectionLabelKind.Local ? SchematicLabelShape.SlshUnknown : SchematicLabelShape.SlshPassive;
        }

        private sealed record StubWork(ConnectionPlacedPin Pin, bool Free, PresentationPoint At, (int Dx, int Dy) Outward,
            IReadOnlyList<ConnectionLabelKind> Kinds, string Text);

        private sealed record StripWork(Guid SheetId, PresentationBounds Sheet, IReadOnlyList<ConnectionLabelKind> Kinds, string Text);

        private sealed record ProbeGeometry(PresentationBounds Bounds, PresentationPoint Pin, (int X, int Y) BodyDirection);

        private sealed class Aim
        {
            public List<PresentationPoint> Own { get; } = [];
            public List<PresentationPoint> Partners { get; } = [];
            public HashSet<Guid> PartnerPins { get; } = [];
        }

        private sealed class View(string path, List<SchematicPlacementGeometry> measurements)
        {
            public string Path { get; } = path;
            public List<SchematicPlacementGeometry> Measurements { get; } = measurements;

            public SchematicPinAnchor Pin(ConnectionPlacedPin pin)
            {
                var symbol = Measurements.SelectMany(m => m.Candidates.Concat(m.Obstacles)).FirstOrDefault(s => s.Id?.Value == pin.SymbolId.ToString("D"))
                    ?? throw Error("incomplete_layout_measurement", "KiCad did not measure the symbol of connected pin "
                        + pin.Endpoint.Pin + " on sheet " + Path + ".");
                if (symbol.SymbolPins is not { Complete: true } pins)
                    throw Error("incomplete_layout_measurement", "KiCad cannot report exact pin positions for the symbol of connected pin "
                        + pin.Endpoint.Pin + " on sheet " + Path + ", so its connections cannot be placed.");
                return pins.Pins.FirstOrDefault(p => p.Id?.Value == pin.PlacedPinId.ToString("D"))
                    ?? throw Error("incomplete_layout_measurement", "KiCad does not report connected pin " + pin.Endpoint.Pin + " on sheet " + Path + ".");
            }

            public Vector2 CandidateAnchor(Guid symbol) =>
                Measurements.SelectMany(m => m.Candidates).First(c => c.Id.Value == symbol.ToString("D")).Anchor;

            public PresentationBounds ObstacleBounds(Guid item) =>
                Bounds(Measurements.SelectMany(m => m.Obstacles).FirstOrDefault(o => o.Id.Value == item.ToString("D"))?.Bounds
                    ?? throw Error("incomplete_layout_measurement", "KiCad did not measure sheet symbol " + item.ToString("D") + " on sheet " + Path + "."));
        }

        private sealed class ScreenWork(ConnectionScreen record, Dictionary<Guid, InitialLayoutBody> bodies)
        {
            public ConnectionScreen Record { get; } = record;
            public Dictionary<Guid, InitialLayoutBody> Bodies { get; } = bodies;
            public List<View> Views { get; } = [];
            public Dictionary<Guid, Aim> Aims { get; } = [];
            public Dictionary<Guid, HashSet<Guid>> Partners { get; } = [];
            public List<StubWork> Stubs { get; } = [];
            public List<StripWork> Strips { get; } = [];
            public Dictionary<Guid, (ConnectionIsland Island, ConnectionMember Member)> Carriers { get; } = [];
            public Dictionary<Combo, PresentationBounds> Envelopes { get; } = [];
            public Dictionary<(Guid Carrier, int Rotation), ProbeGeometry> Probes { get; } = [];
            public Dictionary<Guid, PresentationBounds> Zones { get; } = [];
            public List<InitialLayoutObstacle> KeepOuts { get; } = [];
        }
    }
}
