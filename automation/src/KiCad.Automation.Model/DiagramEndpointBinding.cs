using System.Collections.Immutable;
using System.Xml;

namespace KiCad.Automation.Model;

public enum DiagramEndpointKind { Unresolved, Interface, Compatible, Candidates, Pin }

/// <summary>Exact electrical identity across child designs and repeated sheets.
/// A chosen target is not, by itself, proof of electrical compatibility.</summary>
public sealed record DiagramPinTarget(Guid DesignId, Guid ComponentId,
    ImmutableArray<Guid> SheetInstancePath, string Pin)
{
    public void Validate()
    {
        if (DesignId == Guid.Empty || ComponentId == Guid.Empty || SheetInstancePath.IsDefaultOrEmpty
            || SheetInstancePath.Any(id => id == Guid.Empty) || string.IsNullOrWhiteSpace(Pin))
            throw DiagramEndpointBinding.Invalid("An exact pin requires a design, component occurrence, full sheet-instance path and pin identifier.");
        DiagramEndpointBinding.Text(Pin);
    }

    public bool SamePin(DiagramPinTarget other) => other is not null && DesignId == other.DesignId
        && ComponentId == other.ComponentId && Pin == other.Pin && SheetInstancePath.SequenceEqual(other.SheetInstancePath);
}

/// <summary>Compatibility intent survives pin selection. Its strings express requirements;
/// no selector is silently interpreted as a verified component capability.</summary>
public sealed record DiagramPinSelector(string Role, string Protocol,
    ImmutableArray<string> RequiredFunctions, ImmutableArray<SourceReference> Sources)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Role) || Protocol is null || RequiredFunctions.IsDefault || Sources.IsDefault
            || RequiredFunctions.Any(string.IsNullOrWhiteSpace)
            || RequiredFunctions.Distinct(StringComparer.Ordinal).Count() != RequiredFunctions.Length)
            throw DiagramEndpointBinding.Invalid("A compatibility selector needs a role and distinct required functions; protocol may remain unspecified.");
        DiagramEndpointBinding.Text(Role); DiagramEndpointBinding.Text(Protocol);
        foreach (string function in RequiredFunctions) DiagramEndpointBinding.Text(function);
        foreach (var source in Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.DocumentId) || string.IsNullOrWhiteSpace(source.Revision) || source.Page is <= 0)
                throw DiagramEndpointBinding.Invalid("Selector sources require exact document revisions and positive page numbers when supplied.");
            DiagramEndpointBinding.Text(source.DocumentId); DiagramEndpointBinding.Text(source.Revision);
            if (source.Table is { } table) DiagramEndpointBinding.Text(table);
            if (source.PartVariant is { } variant) DiagramEndpointBinding.Text(variant);
        }
    }
}

/// <summary>Each end of a connection can refine independently. BlockId addresses a
/// direct child or the current diagram boundary; InterfaceId is optional until known.</summary>
public sealed record DiagramEndpointBinding(DiagramEndpointKind Kind, Guid BlockId, Guid? InterfaceId,
    string Intent, DiagramPinSelector? Selector, ImmutableArray<DiagramPinTarget> Candidates, DiagramPinTarget? Pin)
{
    public static DiagramEndpointBinding Unknown(Guid blockId, string intent = "") =>
        new(DiagramEndpointKind.Unresolved, blockId, null, intent, null, [], null);

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || BlockId == Guid.Empty || InterfaceId == Guid.Empty || Intent is null || Candidates.IsDefault)
            throw Invalid("An endpoint needs a supported definition state and exact block identity; unknowns must remain explicit.");
        Text(Intent); Selector?.Validate(); Pin?.Validate();
        for (int i = 0; i < Candidates.Length; ++i)
        {
            var candidate = Candidates[i];
            if (candidate is null) throw Invalid("A pin candidate cannot be null.");
            candidate.Validate();
            for (int j = 0; j < i; ++j)
                if (candidate.SamePin(Candidates[j])) throw Invalid("Pin candidates must be distinct exact identities, including repeated-sheet paths.");
        }
        bool valid = Kind switch
        {
            DiagramEndpointKind.Unresolved => InterfaceId is null && Selector is null && Candidates.IsEmpty && Pin is null,
            DiagramEndpointKind.Interface => InterfaceId is not null && Selector is null && Candidates.IsEmpty && Pin is null,
            DiagramEndpointKind.Compatible => Selector is not null && Candidates.IsEmpty && Pin is null,
            DiagramEndpointKind.Candidates => !Candidates.IsEmpty && Pin is null,
            DiagramEndpointKind.Pin => Pin is not null && (Candidates.IsEmpty || Candidates.Any(c => c.SamePin(Pin))),
            _ => false
        };
        if (!valid) throw Invalid("Endpoint fields disagree with its definition state; do not disguise an unresolved or competing pin as a chosen assignment.");
    }

    /// <summary>Records an explicit choice while retaining its intent, selector and candidates.
    /// The caller's separate electrical check determines whether it can become a native net.</summary>
    public DiagramEndpointBinding Choose(DiagramPinTarget pin)
    {
        Validate();
        var chosen = this with { Kind = DiagramEndpointKind.Pin, Pin = pin };
        chosen.Validate(); return chosen;
    }

    /// <summary>A pin swap or removed component can invalidate a choice without erasing
    /// the requirements that led to it. Supplied candidates are possibilities, not guesses.</summary>
    public DiagramEndpointBinding Unbind(ImmutableArray<DiagramPinTarget> candidates)
    {
        Validate();
        var kind = !candidates.IsDefaultOrEmpty ? DiagramEndpointKind.Candidates : Selector is not null
            ? DiagramEndpointKind.Compatible : InterfaceId is not null ? DiagramEndpointKind.Interface : DiagramEndpointKind.Unresolved;
        var result = this with { Kind = kind, Pin = null, Candidates = candidates };
        result.Validate(); return result;
    }

    internal static void Text(string text)
    {
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw Invalid("Endpoint text cannot be preserved in XML."); }
    }
    internal static AutomationException Invalid(string message) => new("invalid_diagram_endpoint", message);
}
