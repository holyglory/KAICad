using System.Collections.Immutable;
using System.Xml;

namespace KiCad.Automation.Model;

/// <summary>The three editable intent fields shared by root blocks, child
/// blocks and connections. Empty text remains unspecified, not a zero value.</summary>
public enum DiagramRequirementField { General, Schematic, Routing }

public sealed record DiagramRequirements(string General, string Schematic, string Routing)
{
    public static DiagramRequirements Empty { get; } = new("", "", "");

    public string Get(DiagramRequirementField field) => field switch
    {
        DiagramRequirementField.General => General,
        DiagramRequirementField.Schematic => Schematic,
        DiagramRequirementField.Routing => Routing,
        _ => throw Invalid("Select an existing requirement field.")
    };

    public DiagramRequirements With(DiagramRequirementField field, string text)
    {
        ValidateText(text);
        return field switch
        {
            DiagramRequirementField.General => this with { General = text },
            DiagramRequirementField.Schematic => this with { Schematic = text },
            DiagramRequirementField.Routing => this with { Routing = text },
            _ => throw Invalid("Select an existing requirement field.")
        };
    }

    public void Validate()
    {
        ValidateText(General); ValidateText(Schematic); ValidateText(Routing);
    }

    private static void ValidateText(string text)
    {
        if (text is null) throw Invalid("Requirement text cannot be null; an empty field is allowed.");
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw Invalid("Requirement text contains characters that XML cannot preserve."); }
    }

    private static AutomationException Invalid(string message) => new("invalid_diagram_requirements", message);
}

/// <summary>Exact scope; equal field text in another implementation is not the
/// same editing target. A selected design-state identity is not a revision.</summary>
public sealed record DiagramRequirementScope(Guid DocumentId, Guid OwnerId, Guid DesignStateId)
{
    public void Validate()
    {
        if (DocumentId == Guid.Empty || OwnerId == Guid.Empty || DesignStateId == Guid.Empty)
            throw new AutomationException("invalid_requirement_scope", "Requirements need an exact document, owner and design state.");
    }
}

public sealed record DiagramRequirementSnapshot(DiagramRequirementScope Scope, Guid RevisionId,
    DiagramRequirements Requirements)
{
    public void Validate()
    {
        if (Scope is null || Requirements is null || RevisionId == Guid.Empty)
            throw new AutomationException("invalid_requirement_snapshot", "A requirement snapshot needs a scope, revision and text.");
        Scope.Validate(); Requirements.Validate();
    }
}

public sealed record DiagramRequirementConflict(DiagramRequirementField Field, string Base,
    string Draft, string Saved);

/// <summary>A field choice is bound to the complete compared input, not just
/// a field name. The draft can change without acquiring a saved revision.</summary>
public sealed record DiagramRequirementResolution(DiagramRequirementScope Scope, Guid BaseRevisionId,
    Guid SavedRevisionId, DiagramRequirements Base, DiagramRequirements Draft, DiagramRequirements Saved,
    DiagramRequirementField Field, string Text);

public sealed record DiagramRequirementMergeResult(DiagramRequirements? Candidate,
    ImmutableArray<DiagramRequirementConflict> Conflicts)
{
    public bool CanSave => Candidate is not null && Conflicts.IsEmpty;
}

/// <summary>Pure three-way field merge. Never infers semantic equivalence or
/// concatenates competing requirements. Publication still requires the normal
/// document revision/epoch check and recoverable save transaction.</summary>
public sealed class DiagramRequirementMerge
{
    private readonly DiagramRequirementSnapshot _base;
    private readonly DiagramRequirementSnapshot _saved;
    private readonly DiagramRequirements _draft;

    private DiagramRequirementMerge(DiagramRequirementSnapshot baseline, DiagramRequirements draft,
        DiagramRequirementSnapshot saved)
    { _base = baseline; _draft = draft; _saved = saved; }

    public static DiagramRequirementMerge Prepare(DiagramRequirementSnapshot baseline,
        DiagramRequirements draft, DiagramRequirementSnapshot saved)
    {
        if (baseline is null || draft is null || saved is null)
            throw Invalid("Provide the base, draft and saved requirements.");
        baseline.Validate(); saved.Validate(); draft.Validate();
        if (baseline.Scope != saved.Scope) throw Invalid("The base and saved requirements have different owners or design states.");
        if (baseline.RevisionId == saved.RevisionId && baseline.Requirements != saved.Requirements)
            throw Invalid("One immutable revision cannot contain two different requirement values.");
        return new(baseline, draft, saved);
    }

    public DiagramRequirementResolution Choose(DiagramRequirementField field, string text)
    {
        _ = _draft.With(field, text); // Validate field and exact text without normalizing it.
        if (!Inspect().Conflicts.Any(c => c.Field == field)) throw Invalid("Resolve only an actual conflicting field.");
        return new(_base.Scope, _base.RevisionId, _saved.RevisionId, _base.Requirements,
            _draft, _saved.Requirements, field, text);
    }

    public DiagramRequirementMergeResult Inspect(IEnumerable<DiagramRequirementResolution>? resolutions = null)
    {
        var choices = new Dictionary<DiagramRequirementField, string>();
        foreach (var resolution in resolutions ?? [])
        {
            if (resolution is null || resolution.Scope != _base.Scope
                || resolution.BaseRevisionId != _base.RevisionId || resolution.SavedRevisionId != _saved.RevisionId
                || resolution.Base != _base.Requirements || resolution.Draft != _draft || resolution.Saved != _saved.Requirements)
                throw new AutomationException("stale_requirement_resolution", "The requirement inputs changed. Preserve the draft and review the new comparison.");
            _ = _draft.With(resolution.Field, resolution.Text);
            if (!choices.TryAdd(resolution.Field, resolution.Text)) throw Invalid("A requirement field has more than one resolution.");
        }

        var conflicts = ImmutableArray.CreateBuilder<DiagramRequirementConflict>();
        var candidate = DiagramRequirements.Empty;
        foreach (var field in Enum.GetValues<DiagramRequirementField>())
        {
            string baseline = _base.Requirements.Get(field), draft = _draft.Get(field), saved = _saved.Requirements.Get(field);
            bool conflicting = draft != baseline && saved != baseline && draft != saved;
            if (conflicting)
            {
                if (choices.Remove(field, out string? choice)) candidate = candidate.With(field, choice);
                else conflicts.Add(new(field, baseline, draft, saved));
            }
            else candidate = candidate.With(field, draft == baseline ? saved : draft);
        }
        if (choices.Count != 0) throw Invalid("A resolution attempted to replace a non-conflicting field.");
        return new(conflicts.Count == 0 ? candidate : null, conflicts.ToImmutable());
    }

    private static AutomationException Invalid(string message) => new("invalid_requirement_merge", message);
}
