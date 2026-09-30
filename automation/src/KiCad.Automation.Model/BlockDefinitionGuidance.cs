using System.Collections.Immutable;

namespace KiCad.Automation.Model;

public enum BlockClassAvailability { Available, MissingLibrary, MissingRevision, MissingClass, InvalidLibrary }
public sealed record BlockClassGuidance(KnowledgeClassReference Reference, BlockClassAvailability Availability,
    string? ClassName, GuidanceResolution? Guidance, string? ErrorCode, string? Reason);
public sealed record BlockDefinitionGuidanceResult(DefinitionChoiceState State, ImmutableArray<BlockClassGuidance> Choices)
{
    public BlockClassGuidance? Selected => State == DefinitionChoiceState.Selected ? Choices.Single() : null;
}

/// <summary>Exact, read-only class resolution. Missing or invalid libraries stay
/// explicit; candidate libraries never get combined into an invented selected part.</summary>
public static class BlockDefinitionGuidance
{
    public static BlockDefinitionGuidanceResult Resolve(BlockDefinition definition,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(libraries); definition.Validate();
        var available = new Dictionary<(Guid Id, string Revision), ComponentKnowledgeLibrary>();
        foreach (var library in libraries)
            if (library is null || library.Id == Guid.Empty || string.IsNullOrWhiteSpace(library.Revision)
                || !available.TryAdd((library.Id, library.Revision), library))
                throw new AutomationException("ambiguous_definition_library", "Supply one exact library for each identity and revision.");
        var choice = definition.KnowledgeClass ?? DefinitionChoice<KnowledgeClassReference>.Unspecified;
        var results = ImmutableArray.CreateBuilder<BlockClassGuidance>();
        foreach (var reference in choice.Values)
        {
            if (!available.TryGetValue((reference.LibraryId, reference.LibraryRevision), out var library))
            {
                bool sameLibrary = available.Keys.Any(k => k.Id == reference.LibraryId);
                results.Add(new(reference, sameLibrary ? BlockClassAvailability.MissingRevision : BlockClassAvailability.MissingLibrary,
                    null, null, sameLibrary ? "library_revision_mismatch" : "missing_knowledge_library",
                    "The exact declared library revision is unavailable; no class or values were inferred."));
                continue;
            }
            try
            {
                ComponentGuidance.Validate(library);
                var type = library.Classes.SingleOrDefault(c => c.Id == reference.ClassId);
                if (type is null)
                { results.Add(new(reference, BlockClassAvailability.MissingClass, null, null, "unknown_class", "The exact class is absent from the declared library revision.")); continue; }
                results.Add(new(reference, BlockClassAvailability.Available, type.Name,
                    ComponentGuidance.ResolveClass(library, reference.ClassId), null, null));
            }
            catch (AutomationException error)
            { results.Add(new(reference, BlockClassAvailability.InvalidLibrary, null, null, error.Code, error.Message)); }
        }
        return new(choice.State, results.ToImmutable());
    }
}
