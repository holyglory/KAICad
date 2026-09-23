using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public sealed record PresentationRepairTarget(Guid ObjectId, Guid? OwnerId, string? FieldName);
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

    public static async Task<NativePresentationCheck> CheckAsync(NativeClient client, DocumentSpecifier document,
        PresentationPolicy policy, CancellationToken cancellationToken = default)
    {
        var facts = await client.InvokeAsync<ReadSchematicPresentationFacts, SchematicPresentationFacts>(
            new() { Document = document }, cancellationToken);
        if (!facts.Document.Equals(document) || facts.Document.SheetPath is null || facts.Document.SheetPath.Path.Count == 0)
            throw Invalid("Native presentation facts identify another or missing sheet.");
        var objects = new List<PresentationObject>();
        var references = new List<Guid>();
        var targets = new List<PresentationRepairTarget>();
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
            Guid id = Identity(fact.Id);
            objects.Add(new(id, kind, Bounds(fact.Bounds), fact.Visible,
                fact.HasTextHeightNm ? fact.TextHeightNm / 1_000_000m : null, fact.Text,
                TextBounds: fact.TextBounds is null ? null : Bounds(fact.TextBounds)));
            if (kind == PresentationObjectKind.ReferenceDesignator) references.Add(id);
            targets.Add(new(id, fact.OwnerId is null ? null : Identity(fact.OwnerId),
                string.IsNullOrEmpty(fact.FieldName) ? null : fact.FieldName));
        }
        var sheet = new PresentationSheet(facts.Document.SheetPath.Path.Select(Identity).ToArray(),
            Bounds(facts.PageBounds), objects, references,
            facts.Wires.Select(w => new PresentationWire(Identity(w.Id), w.SignalKey,
                new(w.Start.XNm, w.Start.YNm), new(w.End.XNm, w.End.YNm))).ToArray(),
            facts.Junctions.Select(p => new PresentationPoint(p.XNm, p.YNm)).ToArray());
        var revision = new KiCad.Automation.Model.DocumentRevision(facts.Revision.Epoch, facts.Revision.Sequence);
        // The native extraction deliberately reports missing capabilities.
        var snapshot = new PresentationSnapshot(Identity(facts.Document.SheetPath.Path[0]), revision,
            facts.CoverageComplete && facts.Limitations.Count == 0, [sheet]);
        return new(PresentationVerifier.Verify(snapshot, policy), targets, facts.Limitations.ToArray());
    }

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
