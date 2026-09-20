namespace KiCad.Automation.Model;

public sealed record ComponentKnowledgeAuthoringResult(ComponentKnowledgeLibrary Library,
    GuidanceStatement Added, IReadOnlyList<QuantityIssue> QuantityIssues);

/// <summary>Authoring boundary for source-backed reusable component guidance.
/// Numeric facts remain classified and sourced; this never extracts or invents
/// values from document prose and never creates a native symbol/footprint.</summary>
public static class ComponentKnowledgeAuthoring
{
    public static ComponentKnowledgeAuthoringResult AddClassGuidance(ComponentKnowledgeLibrary library,
        Guid classId, string newRevision, GuidanceStatement statement)
    {
        ComponentGuidance.Validate(library);
        if (classId == Guid.Empty || string.IsNullOrWhiteSpace(newRevision) || newRevision == library.Revision)
            throw Invalid("A guidance edit needs an exact class and a new library revision.");
        if (library.Classes.SingleOrDefault(c => c.Id == classId) is not { } owner)
            throw Invalid("The guidance class does not exist in the selected library revision.");
        if (statement is null || statement.Id == Guid.Empty || library.Classes.SelectMany(c => c.Guidance).Any(s => s.Id == statement.Id))
            throw Invalid("The authored guidance statement needs a fresh identity.");
        if (statement.Quantity is not null && statement.Sources.Count == 0)
            throw Invalid("Numeric component facts require at least one exact source document/revision reference.");
        if (statement.Quantity?.Kind is ParameterKind.Unclassified)
            throw Invalid("Numeric component facts must be classified as nominal, operating limit, absolute maximum or measurement.");
        ComponentGuidance.ValidateStatement(statement, library.Classes.SelectMany(c => c.Guidance).Select(s => s.Id)
            .Concat(library.Classes.Select(c => c.Id)).Append(library.Id).ToHashSet());
        var result = library with { Revision = newRevision, Classes = library.Classes.Select(c =>
            c.Id == classId ? c with { Guidance = c.Guidance.Append(statement).ToArray() } : c).ToArray() };
        ComponentGuidance.Validate(result);
        var issues = statement.Quantity?.Inspect(statement.Id).ToArray() ?? [];
        return new(result, statement, issues);
    }

    private static AutomationException Invalid(string message) => new("invalid_guidance_authoring", message);
}
