using System.Collections.Immutable;
using System.Xml;

namespace KiCad.Automation.Model;

public enum RequirementRevisionActor { User, Agent, Import }

public sealed record RequirementRevisionOrigin(RequirementRevisionActor ActorKind, string Actor,
    DateTimeOffset RecordedAt, string Summary, ImmutableArray<SourceReference> Sources,
    ImmutableArray<Guid> InputIds)
{
    public void Validate()
    {
        if (!Enum.IsDefined(ActorKind) || string.IsNullOrWhiteSpace(Actor) || Summary is null
            || RecordedAt == DateTimeOffset.MinValue || RecordedAt.Offset != TimeSpan.Zero
            || Sources.IsDefault || InputIds.IsDefault || InputIds.Any(id => id == Guid.Empty)
            || InputIds.Distinct().Count() != InputIds.Length)
            throw Invalid("Revision origin needs an actor, an explicit UTC time and distinct input references.");
        Text(Actor); Text(Summary);
        foreach (var source in Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.DocumentId) || string.IsNullOrWhiteSpace(source.Revision)
                || source.Page is <= 0) throw Invalid("A revision source requires its document and revision.");
            Text(source.DocumentId); Text(source.Revision);
            if (source.Table is { } table) Text(table);
            if (source.PartVariant is { } variant) Text(variant);
        }
    }
    private static void Text(string text)
    {
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw Invalid("Revision origin text cannot be preserved in XML."); }
    }
    private static AutomationException Invalid(string message) => new("invalid_requirement_origin", message);
}

public sealed record RequirementFieldRestoration(DiagramRequirementField Field, Guid SourceRevisionId);

public sealed record DiagramRequirementRevision(Guid Id, Guid? ParentId, DiagramRequirements Requirements,
    RequirementRevisionOrigin Origin, ImmutableArray<RequirementFieldRestoration> Restorations);

/// <summary>An immutable draft. Browsing and restoring cannot publish history.
/// The baseline is kept even when another actor has saved a newer revision.</summary>
public sealed record DiagramRequirementDraft(DiagramRequirementSnapshot Baseline, DiagramRequirements Requirements,
    ImmutableDictionary<DiagramRequirementField, Guid> RestoredFields)
{
    public DiagramRequirementDraft Edit(DiagramRequirementField field, string text)
    {
        if (Baseline is null || Requirements is null || RestoredFields is null)
            throw new AutomationException("invalid_requirement_draft", "The draft needs its exact baseline and current requirements.");
        Baseline.Validate(); Requirements.Validate();
        return this with { Requirements = Requirements.With(field, text), RestoredFields = RestoredFields.Remove(field) };
    }
}

public sealed record DiagramRequirementCommit(DiagramRequirementHistory History,
    DiagramRequirementRevision Revision, bool Changed);

/// <summary>Immutable per-owner, per-design-state history. This domain object
/// never writes files, activates a design or sends an agent request. A caller
/// must publish the returned history through the guarded save transaction.</summary>
public sealed class DiagramRequirementHistory
{
    public DiagramRequirementScope Scope { get; }
    public ImmutableArray<DiagramRequirementRevision> Revisions { get; }
    public DiagramRequirementRevision Current => Revisions[^1];
    private readonly ImmutableDictionary<Guid, DiagramRequirementRevision> _index;

    public DiagramRequirementHistory(DiagramRequirementScope scope,
        IEnumerable<DiagramRequirementRevision> revisions)
    {
        if (scope is null || revisions is null) throw Invalid("Provide a scope and its immutable revisions.");
        scope.Validate(); Scope = scope;
        Revisions = revisions.ToImmutableArray();
        if (Revisions.IsEmpty) throw Invalid("A saved requirement history needs an initial revision.");
        var index = ImmutableDictionary.CreateBuilder<Guid, DiagramRequirementRevision>();
        Guid? parent = null;
        foreach (var revision in Revisions)
        {
            if (revision is null || revision.Id == Guid.Empty || revision.ParentId != parent
                || revision.Requirements is null || revision.Origin is null || revision.Restorations.IsDefault
                || index.ContainsKey(revision.Id)) throw Invalid("Revision identities and parent links must form an exact ordered history.");
            revision.Requirements.Validate(); revision.Origin.Validate();
            var fields = new HashSet<DiagramRequirementField>();
            foreach (var restore in revision.Restorations)
            {
                if (restore is null || !Enum.IsDefined(restore.Field) || !fields.Add(restore.Field)
                    || !index.TryGetValue(restore.SourceRevisionId, out var source)
                    || revision.Requirements.Get(restore.Field) != source.Requirements.Get(restore.Field))
                    throw Invalid("A field restoration must identify an earlier exact value in the same history.");
            }
            index.Add(revision.Id, revision); parent = revision.Id;
        }
        _index = index.ToImmutable();
    }

    public DiagramRequirementSnapshot Inspect(Guid revisionId)
    {
        var revision = RequireRevision(revisionId);
        return new(Scope, revision.Id, revision.Requirements);
    }

    public DiagramRequirementDraft StartDraft() => new(Inspect(Current.Id), Current.Requirements,
        ImmutableDictionary<DiagramRequirementField, Guid>.Empty);

    /// <summary>Check the complete saved prefix, including provenance. Reloaded immutable
    /// arrays compare by content, not by their allocation identity.</summary>
    public bool Retains(DiagramRequirementHistory saved) => saved is not null && Scope == saved.Scope
        && Revisions.Length >= saved.Revisions.Length && saved.Revisions.Select((r, i) => Same(r, Revisions[i])).All(x => x);

    private static bool Same(DiagramRequirementRevision a, DiagramRequirementRevision b) =>
        a.Id == b.Id && a.ParentId == b.ParentId && a.Requirements == b.Requirements
        && SameOrigin(a.Origin, b.Origin)
        && a.Restorations.SequenceEqual(b.Restorations);

    internal static bool SameOrigin(RequirementRevisionOrigin a, RequirementRevisionOrigin b) =>
        a.ActorKind == b.ActorKind && a.Actor == b.Actor && a.RecordedAt == b.RecordedAt && a.Summary == b.Summary
        && a.Sources.SequenceEqual(b.Sources) && a.InputIds.SequenceEqual(b.InputIds);

    public ImmutableArray<DiagramRequirementRevision> FieldHistory(DiagramRequirementField field)
    {
        _ = Current.Requirements.Get(field);
        var changes = ImmutableArray.CreateBuilder<DiagramRequirementRevision>();
        string? previous = null;
        foreach (var revision in Revisions)
        {
            string value = revision.Requirements.Get(field);
            if (value != previous) changes.Add(revision);
            previous = value;
        }
        return changes.ToImmutable().Reverse().ToImmutableArray();
    }

    public DiagramRequirementDraft RestoreField(DiagramRequirementDraft draft, Guid sourceRevisionId,
        DiagramRequirementField field)
    {
        ValidateDraft(draft);
        string text = RequireRevision(sourceRevisionId).Requirements.Get(field);
        if (draft.Requirements.Get(field) == text) return draft;
        return draft with { Requirements = draft.Requirements.With(field, text),
            RestoredFields = draft.RestoredFields.SetItem(field, sourceRevisionId) };
    }

    public DiagramRequirementMerge PrepareMerge(DiagramRequirementDraft draft)
    {
        ValidateDraft(draft);
        return DiagramRequirementMerge.Prepare(draft.Baseline, draft.Requirements, Inspect(Current.Id));
    }

    public DiagramRequirementCommit Commit(Guid expectedSavedRevision, DiagramRequirementDraft draft,
        Guid revisionId, RequirementRevisionOrigin origin,
        IEnumerable<DiagramRequirementResolution>? resolutions = null)
    {
        if (expectedSavedRevision != Current.Id)
            throw new AutomationException("stale_requirement_history", "The saved requirement revision changed. Keep the draft and compare again.");
        if (revisionId == Guid.Empty || _index.ContainsKey(revisionId) || origin is null)
            throw Invalid("A new revision requires an unused identity and its actual origin.");
        origin.Validate();
        var merged = PrepareMerge(draft).Inspect(resolutions);
        if (!merged.CanSave)
            throw new AutomationException("requirement_conflict", "Resolve competing requirement text before saving; both versions are retained.");
        var text = merged.Candidate!;
        if (text == Current.Requirements) return new(this, Current, false);
        var restorations = draft.RestoredFields.OrderBy(p => p.Key)
            .Where(p => text.Get(p.Key) == RequireRevision(p.Value).Requirements.Get(p.Key))
            .Select(p => new RequirementFieldRestoration(p.Key, p.Value)).ToImmutableArray();
        var revision = new DiagramRequirementRevision(revisionId, Current.Id, text, origin, restorations);
        return new(new(Scope, Revisions.Add(revision)), revision, true);
    }

    private void ValidateDraft(DiagramRequirementDraft draft)
    {
        if (draft is null || draft.Baseline is null || draft.Requirements is null || draft.RestoredFields is null)
            throw Invalid("A draft needs its exact baseline, current text and restoration references.");
        draft.Baseline.Validate(); draft.Requirements.Validate();
        if (draft.Baseline.Scope != Scope || Inspect(draft.Baseline.RevisionId) != draft.Baseline)
            throw Invalid("The draft baseline does not belong to this exact owner and history.");
        foreach (var field in draft.RestoredFields)
            if (draft.Requirements.Get(field.Key) != RequireRevision(field.Value).Requirements.Get(field.Key))
                throw Invalid("The draft restoration no longer matches its referenced field value.");
    }

    private DiagramRequirementRevision RequireRevision(Guid id) => _index.TryGetValue(id, out var revision)
        ? revision : throw Invalid("The exact requirement revision is unavailable in this history.");

    private static AutomationException Invalid(string message) => new("invalid_requirement_history", message);
}
