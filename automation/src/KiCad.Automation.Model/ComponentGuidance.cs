namespace KiCad.Automation.Model;

public enum GuidanceStrength { Information, Preference, Requirement }

/// <summary>A named engineering statement. Text is never evaluated as code or automatically
/// converted to a numerical requirement. Interpretation and measurements have separate owners.</summary>
public sealed record GuidanceStatement(Guid Id, string Key, string Category, string Text,
    GuidanceStrength Strength, string Applicability, IReadOnlyList<SourceReference> Sources,
    VerificationState Verification = VerificationState.Unverified, Guid? Replaces = null,
    string? ExceptionRationale = null, GuidanceQuantity? Quantity = null);

// Library revision is explicit. Resolving an instance never silently upgrades it.
public sealed record ComponentClass(Guid Id, string Name, Guid? BaseClassId,
    IReadOnlyList<GuidanceStatement> Guidance);
public sealed record ComponentKnowledgeLibrary(Guid Id, string Revision, IReadOnlyList<ComponentClass> Classes);
public sealed record ComponentKnowledgeBinding(Guid ComponentInstanceId, Guid LibraryId,
    string LibraryRevision, Guid ClassId, IReadOnlyList<GuidanceStatement> Guidance);
public sealed record ResolvedGuidance(GuidanceStatement Statement, Guid OwnerId, bool IsInstance);
public sealed record GuidanceReplacement(ResolvedGuidance Original, ResolvedGuidance Replacement);
public sealed record GuidanceConflict(string Key, string Applicability, IReadOnlyList<Guid> StatementIds);
public sealed record GuidanceResolution(IReadOnlyList<ResolvedGuidance> Effective,
    IReadOnlyList<GuidanceReplacement> Replacements, IReadOnlyList<GuidanceConflict> Conflicts,
    IReadOnlyList<QuantityIssue>? QuantityIssues = null);

public static class ComponentGuidance
{
    public static void Validate(ComponentKnowledgeLibrary library)
    {
        var ids = new HashSet<Guid>();
        AddId(library.Id, ids);
        Required(library.Revision, "Library revision");
        foreach (var type in library.Classes)
        {
            AddId(type.Id, ids);
            Required(type.Name, "Class name");
            foreach (var statement in type.Guidance) ValidateStatement(statement, ids);
        }
        var classes = library.Classes.ToDictionary(c => c.Id);
        foreach (var type in library.Classes)
        {
            // Validate every class, including classes not currently instantiated.
            var chain = Chain(classes, type.Id);
            ResolveChain(chain, []);
        }
    }

    public static GuidanceResolution Resolve(ComponentKnowledgeLibrary library, ComponentKnowledgeBinding binding)
    {
        Validate(library);
        if (binding.LibraryId != library.Id || binding.LibraryRevision != library.Revision)
            throw Invalid("library_revision_mismatch", "Resolve against the exact library identity and revision selected by the instance.");
        if (binding.ComponentInstanceId == Guid.Empty)
            throw Invalid("invalid_guidance", "A component instance identity is required.");
        var ids = library.Classes.SelectMany(c => c.Guidance).Select(s => s.Id)
            .Concat(library.Classes.Select(c => c.Id)).Append(library.Id).ToHashSet();
        AddId(binding.ComponentInstanceId, ids);
        foreach (var statement in binding.Guidance) ValidateStatement(statement, ids);
        var chain = Chain(library.Classes.ToDictionary(c => c.Id), binding.ClassId);
        return ResolveChain(chain, binding.Guidance.Select(s => new ResolvedGuidance(s, binding.ComponentInstanceId, true)));
    }

    public static GuidanceResolution Resolve(Circuit circuit, ComponentKnowledgeLibrary library,
                                            ComponentKnowledgeBinding binding)
    {
        circuit.Validate();
        if (!circuit.Components.Any(c => c.Id == binding.ComponentInstanceId))
            throw Invalid("unknown_component", "Guidance must target an explicit component instance in this circuit.");
        return Resolve(library, binding);
    }

    private static IReadOnlyList<ComponentClass> Chain(IReadOnlyDictionary<Guid, ComponentClass> classes, Guid leaf)
    {
        var result = new List<ComponentClass>();
        var visited = new HashSet<Guid>();
        Guid? current = leaf;
        while (current is Guid id)
        {
            if (!visited.Add(id)) throw Invalid("class_cycle", "Component class inheritance contains a cycle.");
            if (!classes.TryGetValue(id, out var type)) throw Invalid("unknown_class", "A component class or its base does not exist.");
            result.Add(type);
            current = type.BaseClassId;
        }
        result.Reverse();
        return result;
    }

    private static GuidanceResolution ResolveChain(IReadOnlyList<ComponentClass> chain,
                                                    IEnumerable<ResolvedGuidance> instanceStatements)
    {
        var effective = new Dictionary<Guid, ResolvedGuidance>();
        var replacements = new List<GuidanceReplacement>();
        // Each owner is a batch: sibling statements cannot override each other by
        // file order. Only explicitly inherited statements may be replaced.
        var batches = chain.Select(c => c.Guidance.Select(s => new ResolvedGuidance(s, c.Id, false)))
            .Append(instanceStatements);
        foreach (var batch in batches)
        {
            var rows = batch.OrderBy(r => r.Statement.Id).ToArray();
            var replaced = new HashSet<Guid>();
            foreach (var row in rows)
            {
                GuidanceStatement statement = row.Statement;
                if (statement.Replaces is not Guid target) continue;
                if (!replaced.Add(target) || !effective.TryGetValue(target, out var original))
                    throw Invalid("invalid_override", "Replacement must identify one active inherited statement, exactly once.");
                if (original.Statement.Key != statement.Key || original.Statement.Applicability != statement.Applicability
                    || original.Statement.Category != statement.Category)
                    throw Invalid("invalid_override", "Replacement cannot change the named property's category or applicability.");
                if (original.Statement.Strength == GuidanceStrength.Requirement
                    && (string.IsNullOrWhiteSpace(statement.ExceptionRationale) || statement.Sources.Count == 0))
                    throw Invalid("requirement_exception_required", "Replacing a requirement needs a recorded rationale and source; the original is retained.");
                replacements.Add(new(original, row));
            }
            foreach (Guid target in replaced) effective.Remove(target);
            foreach (var row in rows) effective.Add(row.Statement.Id, row);
        }
        ResolvedGuidance[] ordered = effective.Values.OrderBy(r => r.Statement.Id).ToArray();
        // This detects competing assignments to a named property, not semantic
        // contradictions in arbitrary prose. Those require explicit interpretation.
        var conflicts = ordered.GroupBy(r => (r.Statement.Key, r.Statement.Applicability))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Key, StringComparer.Ordinal).ThenBy(g => g.Key.Applicability, StringComparer.Ordinal)
            .Select(g => new GuidanceConflict(g.Key.Key, g.Key.Applicability, g.Select(r => r.Statement.Id).ToArray())).ToArray();
        var quantities = ordered.SelectMany(r => r.Statement.Quantity?.Inspect(r.Statement.Id) ?? []).ToArray();
        return new(ordered, replacements, conflicts, quantities);
    }

    internal static void ValidateStatement(GuidanceStatement statement, HashSet<Guid> ids)
    {
        AddId(statement.Id, ids);
        Required(statement.Key, "Guidance key");
        Required(statement.Category, "Guidance category");
        Required(statement.Text, "Guidance text");
        statement.Quantity?.ValidateShape();
        if (!Enum.IsDefined(statement.Strength) || !Enum.IsDefined(statement.Verification))
            throw Invalid("invalid_guidance", "Unknown guidance strength or verification state.");
        foreach (var source in statement.Sources)
        {
            Required(source.DocumentId, "Source document identity");
            Required(source.Revision, "Source revision");
            if (source.Page is <= 0) throw Invalid("invalid_guidance", "Source page numbers start at one.");
        }
        if (statement.Replaces == Guid.Empty || statement.Replaces == statement.Id)
            throw Invalid("invalid_override", "Replacement needs a different non-empty statement identity.");
        if (statement.ExceptionRationale is not null && statement.Replaces is null)
            throw Invalid("invalid_override", "An exception rationale must identify the statement being replaced.");
    }

    private static void AddId(Guid id, HashSet<Guid> ids)
    {
        if (id == Guid.Empty || !ids.Add(id)) throw Invalid("invalid_guidance", "Knowledge identities must be unique and non-empty.");
    }
    private static void Required(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Invalid("invalid_guidance", field + " is required.");
    }
    private static AutomationException Invalid(string code, string text) => new(code, text);
}
