using System.Collections.Immutable;
using System.Xml;

namespace KiCad.Automation.Model;

public enum DefinitionChoiceState { Unspecified, Unknown, Candidates, Selected }
public enum BlockDefinitionFacet { Purpose, Type, Manufacturer, Family, Model, OrderablePart, Package }

public sealed record KnowledgeClassReference(Guid LibraryId, string LibraryRevision, Guid ClassId);

/// <summary>A choice is not a materialized component or a proof of compatibility.
/// Candidate values never become selected merely because only one remains.</summary>
public sealed record DefinitionChoice<T>(DefinitionChoiceState State, ImmutableArray<T> Values,
    GuidanceStrength Strength, string Applicability, ImmutableArray<SourceReference> Sources,
    VerificationState Verification = VerificationState.Unverified, string? UnknownReason = null)
{
    public static DefinitionChoice<T> Unspecified { get; } = new(DefinitionChoiceState.Unspecified, [],
        GuidanceStrength.Information, "", [], VerificationState.Unknown);

    internal void Validate(Action<T> validateValue)
    {
        if (!Enum.IsDefined(State) || !Enum.IsDefined(Strength) || !Enum.IsDefined(Verification)
            || Values.IsDefault || Sources.IsDefault || Applicability is null
            || Values.Distinct().Count() != Values.Length)
            throw BlockDefinition.Invalid("Definition choices require supported states, distinct values and explicit provenance collections.");
        if (State is DefinitionChoiceState.Unspecified or DefinitionChoiceState.Unknown && !Values.IsEmpty
            || State == DefinitionChoiceState.Candidates && Values.IsEmpty
            || State == DefinitionChoiceState.Selected && Values.Length != 1
            || State == DefinitionChoiceState.Unknown && string.IsNullOrWhiteSpace(UnknownReason)
            || State != DefinitionChoiceState.Unknown && UnknownReason is not null)
            throw BlockDefinition.Invalid("Keep unknown reasons separate from candidate or selected values; a selection names exactly one value.");
        BlockDefinition.Text(Applicability); if (UnknownReason is not null) BlockDefinition.Text(UnknownReason);
        foreach (var value in Values) validateValue(value);
        foreach (var source in Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.DocumentId) || string.IsNullOrWhiteSpace(source.Revision) || source.Page is <= 0)
                throw BlockDefinition.Invalid("Definition sources require exact document/revision references and positive page numbers when given.");
            BlockDefinition.Text(source.DocumentId); BlockDefinition.Text(source.Revision);
            if (source.Table is { } table) BlockDefinition.Text(table);
            if (source.PartVariant is { } variant) BlockDefinition.Text(variant);
        }
    }

    public bool SameContents(DefinitionChoice<T> other) => other is not null && State == other.State
        && Values.SequenceEqual(other.Values) && Strength == other.Strength && Applicability == other.Applicability
        && Sources.SequenceEqual(other.Sources) && Verification == other.Verification && UnknownReason == other.UnknownReason;
}

/// <summary>Independent definition facets; no linear completion stage, invented part,
/// electrical pins or implied package mapping. Native realization has another owner.</summary>
public sealed record BlockDefinition(
    DefinitionChoice<string>? Purpose = null,
    DefinitionChoice<string>? Type = null,
    DefinitionChoice<string>? Manufacturer = null,
    DefinitionChoice<string>? Family = null,
    DefinitionChoice<string>? Model = null,
    DefinitionChoice<string>? OrderablePart = null,
    DefinitionChoice<string>? Package = null,
    DefinitionChoice<KnowledgeClassReference>? KnowledgeClass = null)
{
    public static BlockDefinition Empty { get; } = new();

    public DefinitionChoice<string> Get(BlockDefinitionFacet facet) => (facet switch
    {
        BlockDefinitionFacet.Purpose => Purpose, BlockDefinitionFacet.Type => Type,
        BlockDefinitionFacet.Manufacturer => Manufacturer, BlockDefinitionFacet.Family => Family,
        BlockDefinitionFacet.Model => Model, BlockDefinitionFacet.OrderablePart => OrderablePart,
        BlockDefinitionFacet.Package => Package, _ => throw Invalid("Choose a supported definition facet.")
    }) ?? DefinitionChoice<string>.Unspecified;

    public BlockDefinition With(BlockDefinitionFacet facet, DefinitionChoice<string> choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var result = facet switch
        {
            BlockDefinitionFacet.Purpose => this with { Purpose = choice }, BlockDefinitionFacet.Type => this with { Type = choice },
            BlockDefinitionFacet.Manufacturer => this with { Manufacturer = choice }, BlockDefinitionFacet.Family => this with { Family = choice },
            BlockDefinitionFacet.Model => this with { Model = choice }, BlockDefinitionFacet.OrderablePart => this with { OrderablePart = choice },
            BlockDefinitionFacet.Package => this with { Package = choice }, _ => throw Invalid("Choose a supported definition facet.")
        };
        result.Validate(); return result;
    }

    public void Validate()
    {
        foreach (var facet in Enum.GetValues<BlockDefinitionFacet>()) Get(facet).Validate(value =>
        { if (string.IsNullOrWhiteSpace(value)) throw Invalid("A definition value cannot be empty; leave it unspecified or record why it is unknown."); Text(value); });
        (KnowledgeClass ?? DefinitionChoice<KnowledgeClassReference>.Unspecified).Validate(value =>
        {
            if (value is null || value.LibraryId == Guid.Empty || value.ClassId == Guid.Empty || string.IsNullOrWhiteSpace(value.LibraryRevision))
                throw Invalid("A knowledge-class choice needs exact library/class identities and a pinned library revision.");
            Text(value.LibraryRevision);
        });
    }

    public bool SameContents(BlockDefinition other) => other is not null
        && Enum.GetValues<BlockDefinitionFacet>().All(f => Get(f).SameContents(other.Get(f)))
        && (KnowledgeClass ?? DefinitionChoice<KnowledgeClassReference>.Unspecified)
            .SameContents(other.KnowledgeClass ?? DefinitionChoice<KnowledgeClassReference>.Unspecified);

    internal static void Text(string value)
    {
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException) { throw Invalid("Definition text must be preservable in XML."); }
    }
    internal static AutomationException Invalid(string message) => new("invalid_block_definition", message);
}
