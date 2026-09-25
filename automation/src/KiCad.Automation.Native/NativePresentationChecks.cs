using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

// SheetPath names the sheet instance the target was measured on (GUIDs joined by '/'); field runtime
// identities are not persistent, so repair a field through its owner and field name on that sheet.
public sealed record PresentationRepairTarget(Guid ObjectId, Guid? OwnerId, string? FieldName, string? SheetPath = null);
public sealed record NativePresentationCheck(PresentationReport Report,
    IReadOnlyList<PresentationRepairTarget> RepairTargets, IReadOnlyList<string> Limitations);

/// <summary>A placement problem among symbols on one sheet: a symbol whose body, pins or visible fields
/// leave the usable region (<see cref="NativePresentationChecks.SymbolOutsideUsableRegion"/>), or two symbols
/// whose bodies overlap (<see cref="NativePresentationChecks.SymbolBodiesOverlap"/>).</summary>
public sealed record SymbolPlacementIssue(string Code, IReadOnlyList<Guid> Symbols);

public static class NativePresentationChecks
{
    public const string SymbolOutsideUsableRegion = "symbol_outside_usable_region";
    public const string SymbolBodiesOverlap = "symbol_bodies_overlap";

    /// <summary>Measure <paramref name="symbols"/> in KiCad on the displayed sheet <paramref name="document"/>
    /// and report every symbol whose body, pins or visible fields leave <paramref name="usable"/> (for example
    /// a page inset and a title-block reserve), and every pair of symbols whose bodies with their pins overlap.
    /// Fields may overhang each other; only bodies must be disjoint (psu-cpu fixture §1.6.3). KiCad measures
    /// only the sheet it displays and refuses any other document, so the caller activates the sheet first.
    /// An empty result means the placement is clean.</summary>
    public static async Task<IReadOnlyList<SymbolPlacementIssue>> CheckSymbolPlacementAsync(NativeClient client,
        DocumentSpecifier document, IReadOnlyList<Guid> symbols, PresentationBounds usable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(usable);
        if (symbols.Count == 0) return [];
        if (symbols.Contains(Guid.Empty) || symbols.Distinct().Count() != symbols.Count)
            throw new ArgumentException("Name each symbol once by its native identity.", nameof(symbols));
        async Task<PresentationBounds[]> Measure(BoundingBoxMode mode)
        {
            var query = new GetBoundingBox { Header = new() { Document = document.Clone() }, Mode = mode };
            query.Items.Add(symbols.Select(id => new KIID { Value = id.ToString("D") }));
            var response = await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(query, cancellationToken);
            if (response.Boxes.Count != symbols.Count || !response.Items.Select(Identity).SequenceEqual(symbols))
                throw Invalid("Native symbol bounds identify other or missing symbols.");
            return [.. response.Boxes.Select(Bounds)];
        }
        // Body and pins only (no fields) for overlap; body, pins and visible fields for the usable region.
        var bodies = await Measure(BoundingBoxMode.BbmItemOnly);
        var withFields = await Measure(BoundingBoxMode.BbmItemAndChildText);
        return SymbolPlacementIssues(symbols, bodies, withFields, usable);
    }

    /// <summary>The geometry of <see cref="CheckSymbolPlacementAsync"/>: bounds are listed in the order of
    /// <paramref name="symbols"/>. Boxes that only touch do not overlap.</summary>
    internal static IReadOnlyList<SymbolPlacementIssue> SymbolPlacementIssues(IReadOnlyList<Guid> symbols,
        IReadOnlyList<PresentationBounds> bodies, IReadOnlyList<PresentationBounds> withFields, PresentationBounds usable)
    {
        if (bodies.Count != symbols.Count || withFields.Count != symbols.Count)
            throw new ArgumentException("Give one body and one field-inclusive box for each symbol.");
        var issues = new List<SymbolPlacementIssue>();
        for (int i = 0; i < symbols.Count; i++)
            if (!usable.Contains(bodies[i]) || !usable.Contains(withFields[i]))
                issues.Add(new(SymbolOutsideUsableRegion, [symbols[i]]));
        for (int i = 0; i < symbols.Count; i++)
            for (int j = i + 1; j < symbols.Count; j++)
            {
                PresentationBounds a = bodies[i], b = bodies[j];
                if (a.LeftNm < b.RightNm && b.LeftNm < a.RightNm && a.TopNm < b.BottomNm && b.TopNm < a.BottomNm)
                    issues.Add(new(SymbolBodiesOverlap, [symbols[i], symbols[j]]));
            }
        return issues;
    }

    /// <summary>Check the sheet KiCad displays with exactly the rules of <see cref="CheckHierarchyAsync"/> for one sheet
    /// instance: KiCad confirms the displayed sheet and its revision, then measures that instance at that revision with its own
    /// references, painted field glyphs, roles and reading directions. So a power or '#' symbol's hidden reference is not
    /// required to show, and overlap and reading-direction rules apply. With <paramref name="expectedRevision"/>, the check is
    /// refused (presentation_revision_changed) unless KiCad holds exactly that document revision, and that refusal comes first:
    /// a stale revision naming a sheet no longer displayed, or a sheet instance deleted since, is refused as stale. The same
    /// refusal is given when the design changes between confirming and measuring, even if the displayed sheet changed too. An
    /// invalid <paramref name="policy"/> or a document naming no sheet instance is refused before KiCad is asked anything.</summary>
    public static async Task<NativePresentationCheck> CheckAsync(NativeClient client, DocumentSpecifier document,
        PresentationPolicy policy, CancellationToken cancellationToken = default, Protocol.DocumentRevision? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(document);
        if (document.SheetPath is null || document.SheetPath.Path.Count == 0)
            throw Invalid("An explicit sheet-instance path naming the displayed sheet is required.");
        PresentationVerifier.RequireValid(policy);
        RequireRevision(expectedRevision);
        // The revision KiCad holds, read through the root sheet: unlike the displayed-sheet read below, this read does not
        // depend on which sheet KiCad displays or on whether the named sheet instance still exists. So a stale revision is
        // refused as stale before the displayed sheet is confirmed, and whether the design moved on during the check is
        // decided even when a person switched sheets meanwhile. The save-state read reports the same document revision as
        // every other read without packing any sheet's content.
        var root = document.Clone();
        root.SheetPath.Path.Clear();
        root.SheetPath.Path.Add(document.SheetPath.Path[0].Clone());
        async Task<Protocol.DocumentRevision> Current()
        {
            var read = await client.InvokeAsync<ReadSchematicSaveState, SchematicSaveState>(
                new() { Document = root.Clone() }, cancellationToken);
            if (read.Revision is null || string.IsNullOrWhiteSpace(read.Revision.Epoch))
                throw Invalid("The native schematic has no identified revision.");
            return read.Revision;
        }
        if (expectedRevision is not null)
        {
            var held = await Current();
            if (!expectedRevision.Equals(held)) throw RevisionChanged(expectedRevision, held);
        }
        // KiCad refuses this read unless the document names the displayed sheet; the facts name the revision it holds.
        async Task<SchematicPresentationFacts> Displayed()
        {
            var read = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
                new() { Document = document }, cancellationToken);
            if (!read.Document.Equals(document) || read.Document.SheetPath is null || read.Document.SheetPath.Path.Count == 0
                || read.Revision is null || string.IsNullOrWhiteSpace(read.Revision.Epoch))
                throw Invalid("Native presentation facts identify another or missing sheet, or no revision.");
            return read;
        }
        var displayed = await Displayed();
        if (expectedRevision is not null && !expectedRevision.Equals(displayed.Revision))
            throw RevisionChanged(expectedRevision, displayed.Revision);
        var revision = displayed.Revision;
        SchematicPlacementGeometry measured;
        try
        {
            measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
            {
                Document = document.Clone(), ExpectedRevision = revision.Clone(), IncludePresentation = true
            }, cancellationToken);
        }
        catch (NativeApiException)
        {
            // As in the hierarchy check: whether the design moved on is decided from the revision KiCad reports now, read
            // through the root sheet so that a person switching sheets meanwhile cannot turn the answer into another refusal.
            var now = await Current();
            if (!now.Equals(revision)) throw RevisionChanged(revision, now);
            throw;
        }
        var facts = measured.Presentation;
        if (facts is null || !facts.Document.Equals(document) || !measured.Revision.Equals(revision) || !facts.Revision.Equals(revision))
            throw Invalid("Native presentation facts identify another sheet or revision, or this KiCad build cannot measure them.");
        var targets = new List<PresentationRepairTarget>();
        var sheet = Sheet(facts, targets);
        var reported = new KiCad.Automation.Model.DocumentRevision(revision.Epoch, revision.Sequence);
        // The native extraction deliberately reports missing capabilities.
        var snapshot = new PresentationSnapshot(Identity(document.SheetPath.Path[0]), reported,
            facts.CoverageComplete && facts.Limitations.Count == 0, [sheet]);
        return new(PresentationVerifier.Verify(snapshot, policy), targets, facts.Limitations.ToArray());
    }

    /// <summary>Check the sheet instance <paramref name="document"/> names and every loaded sheet instance below it
    /// (the whole hierarchy for the root sheet), each measured by KiCad offscreen at its own instance: its own
    /// references, units and field text, painted field glyphs and native nets. Every sheet is measured at one
    /// document revision, which each finding names: the revision KiCad holds when the check starts. When the caller gives
    /// the revision it observed (<paramref name="expectedRevision"/>) and KiCad holds another, or when the design changes
    /// during the check, the check is refused with presentation_revision_changed, and that refusal comes first: a stale
    /// revision naming a sheet instance deleted since is refused as stale. A sheet instance KiCad has not loaded is
    /// otherwise refused with presentation_sheet_not_loaded. An invalid <paramref name="policy"/> is refused before KiCad
    /// is asked anything. Neither the design nor the displayed sheet changes.</summary>
    public static async Task<NativePresentationCheck> CheckHierarchyAsync(NativeClient client, DocumentSpecifier document,
        PresentationPolicy policy, CancellationToken cancellationToken = default, Protocol.DocumentRevision? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(document);
        if (document.SheetPath is null || document.SheetPath.Path.Count == 0)
            throw Invalid("An explicit sheet-instance path is required.");
        PresentationVerifier.RequireValid(policy);
        RequireRevision(expectedRevision);
        // Read the hierarchy from its root sheet, which KiCad always has loaded, so that a request naming a sheet instance
        // KiCad has not loaded is answered as exactly that rather than as a refused read.
        var root = document.Clone();
        root.SheetPath.Path.Clear();
        root.SheetPath.Path.Add(document.SheetPath.Path[0].Clone());
        async Task<SchematicHierarchyDataSnapshot> Hierarchy()
        {
            var read = await client.InvokeAsync<ReadSchematicHierarchyData, SchematicHierarchyDataSnapshot>(
                new() { Document = root.Clone() }, cancellationToken);
            if (read.Revision is null || string.IsNullOrWhiteSpace(read.Revision.Epoch) || read.Data is null)
                throw Invalid("The native hierarchy has no identified revision.");
            return read;
        }
        var hierarchy = await Hierarchy();
        // A revision the caller observed that KiCad no longer holds is refused as exactly that, before anything else is
        // decided from the current hierarchy (such as whether the named sheet instance is loaded).
        if (expectedRevision is not null && !expectedRevision.Equals(hierarchy.Revision))
            throw RevisionChanged(expectedRevision, hierarchy.Revision);
        var revision = hierarchy.Revision;
        var prefix = document.SheetPath.Path.Select(id => id.Value).ToArray();
        var instances = hierarchy.Data.Instances.Select(s => s.Metadata?.Document)
            .Where(d => d?.SheetPath is not null && d.SheetPath.Path.Count >= prefix.Length
                && d.SheetPath.Path.Take(prefix.Length).Select(id => id.Value).SequenceEqual(prefix))
            .Select(d => d!).ToArray();
        if (!instances.Any(d => d.SheetPath.Path.Count == prefix.Length))
            throw new AutomationException("presentation_sheet_not_loaded",
                "KiCad has not loaded the sheet instance " + string.Join('/', prefix) + "; name a sheet instance of the open hierarchy.");
        var sheets = new List<PresentationSheet>();
        var targets = new List<PresentationRepairTarget>();
        var limitations = new List<string>();
        foreach (var instance in instances.OrderBy(d => d.SheetPath.Path.Count)
                     .ThenBy(d => string.Join('/', d.SheetPath.Path.Select(id => id.Value)), StringComparer.Ordinal))
        {
            SchematicPlacementGeometry measured;
            try
            {
                measured = await client.InvokeAsync<MeasureSchematicPlacement, SchematicPlacementGeometry>(new()
                {
                    Document = instance.Clone(), ExpectedRevision = revision.Clone(), IncludePresentation = true
                }, cancellationToken);
            }
            catch (NativeApiException)
            {
                // KiCad measures only the exact revision it holds. Whether the design moved on is decided from the
                // revision KiCad reports now, never from the wording of its refusal; any other refusal stands as it is.
                var now = (await Hierarchy()).Revision;
                if (!now.Equals(revision)) throw RevisionChanged(revision, now);
                throw;
            }
            var facts = measured.Presentation;
            if (facts is null || !facts.Document.Equals(instance) || !measured.Revision.Equals(revision)
                || !facts.Revision.Equals(revision))
                throw Invalid("Native presentation facts identify another sheet or revision, or this KiCad build cannot measure them.");
            sheets.Add(Sheet(facts, targets));
            limitations.AddRange(facts.Limitations.Where(l => !limitations.Contains(l, StringComparer.Ordinal)));
        }
        // Every measurement succeeded at the revision the sheet list was read at, so the list and the facts belong together.
        var reported = new KiCad.Automation.Model.DocumentRevision(revision.Epoch, revision.Sequence);
        var snapshot = new PresentationSnapshot(Identity(document.SheetPath.Path[0]), reported,
            limitations.Count == 0 && sheets.Count > 0, sheets);
        return new(PresentationVerifier.Verify(snapshot, policy), targets, limitations);
    }

    private static void RequireRevision(Protocol.DocumentRevision? expected)
    {
        if (expected is not null && string.IsNullOrWhiteSpace(expected.Epoch))
            throw Invalid("An expected document revision names its document epoch.");
    }

    private static AutomationException RevisionChanged(Protocol.DocumentRevision checkedAt, Protocol.DocumentRevision now) =>
        new("presentation_revision_changed", $"KiCad holds document revision {now.Epoch}:{now.Sequence}, not the revision "
            + $"{checkedAt.Epoch}:{checkedAt.Sequence} the check measures; the design changed, so read it again and check the current revision.");

    // Per-instance facts (MeasureSchematicPlacement with IncludePresentation), which both checks use: every object has a
    // role, painted fields their glyphs and text its reading direction, and KiCad says which references must show.
    // ReadSchematicPresentationFacts, which predates these fields, only confirms the displayed sheet and its revision.
    private static PresentationSheet Sheet(SchematicPresentationFacts facts, List<PresentationRepairTarget> targets)
    {
        if (facts.Document?.SheetPath is null || facts.Document.SheetPath.Path.Count == 0)
            throw Invalid("Native presentation facts identify no sheet instance.");
        var path = facts.Document.SheetPath.Path.Select(Identity).ToArray();
        string key = string.Join('/', path.Select(id => id.ToString("D")));
        var objects = new List<PresentationObject>();
        var references = new List<Guid>();
        foreach (var fact in facts.Objects)
        {
            var kind = fact.Kind switch
            {
                SchematicPresentationObject.Types.Kind.Graphic => PresentationObjectKind.Graphic,
                SchematicPresentationObject.Types.Kind.Image => PresentationObjectKind.Image,
                SchematicPresentationObject.Types.Kind.Text => PresentationObjectKind.Text,
                SchematicPresentationObject.Types.Kind.ReferenceDesignator => PresentationObjectKind.ReferenceDesignator,
                _ => throw Invalid("Unsupported native presentation object kind.")
            };
            var role = fact.PresentationRole switch
            {
                "other" => PresentationRole.Other,
                "symbol" => PresentationRole.Symbol,
                "sheet" => PresentationRole.Sheet,
                "label" => PresentationRole.Label,
                "field" => PresentationRole.Field,
                "sheet_pin" => PresentationRole.SheetPin,
                "text" => PresentationRole.Text,
                _ => throw Invalid($"Unsupported native presentation role '{fact.PresentationRole}'.")
            };
            Guid id = Identity(fact.Id);
            Guid? owner = fact.OwnerId is null ? null : Identity(fact.OwnerId);
            objects.Add(new(id, kind, fact.GlyphBounds is null ? Bounds(fact.Bounds) : Bounds(fact.GlyphBounds), fact.Visible,
                fact.HasTextHeightNm ? fact.TextHeightNm / 1_000_000m : null, fact.Text,
                TextBounds: fact.TextBounds is null ? null : Bounds(fact.TextBounds), Role: role, OwnerId: owner,
                ReadingAngleDegrees: fact.HasReadingAngleDegrees ? Degrees(fact.ReadingAngleDegrees) : null));
            if (kind == PresentationObjectKind.ReferenceDesignator && fact.DesignatorRequired) references.Add(id);
            targets.Add(new(id, owner, string.IsNullOrEmpty(fact.FieldName) ? null : fact.FieldName, key));
        }
        return new(path, Bounds(facts.PageBounds), objects, references,
            facts.Wires.Select(w => new PresentationWire(Identity(w.Id), w.SignalKey,
                new(w.Start.XNm, w.Start.YNm), new(w.End.XNm, w.End.YNm))).ToArray(),
            facts.Junctions.Select(p => new PresentationPoint(p.XNm, p.YNm)).ToArray(),
            string.IsNullOrEmpty(facts.SheetName) ? null : facts.SheetName);
    }

    private static decimal Degrees(double value) => double.IsFinite(value) && Math.Abs(value) <= 3600
        ? Math.Round((decimal)value, 3) : throw Invalid("Native reading direction is not a finite angle.");

    private static PresentationBounds Bounds(Box2? box)
    {
        if (box?.Position is null || box.Size is null || box.Size.XNm < 0 || box.Size.YNm < 0)
            throw Invalid("Native presentation bounds are missing or inverted.");
        return new(box.Position.XNm, box.Position.YNm,
            checked(box.Position.XNm + box.Size.XNm), checked(box.Position.YNm + box.Size.YNm));
    }
    private static Guid Identity(KIID? id) => Guid.TryParseExact(id?.Value, "D", out var value) && value != Guid.Empty
        ? value : throw Invalid("Native presentation identity is missing or invalid.");
    private static AutomationException Invalid(string message) => new("invalid_native_presentation", message);
}
