namespace KiCad.Automation.Model;

/// <summary>One design's electrical and architectural intent plus local component guidance.
/// Library definitions remain declared repository dependencies. Native schematic representation
/// has a separate owner; this model alone does not reconstruct a KiCad schematic.</summary>
public sealed record EngineeringDesign(Circuit Circuit, StructuralDiagram Structure,
    IReadOnlyList<HardwareLibrary> KnowledgeLibraries, IReadOnlyList<ComponentKnowledgeBinding> ComponentBindings,
    IReadOnlyList<UnresolvedGuidanceBinding>? UnresolvedGuidanceBindings = null)
{
    public bool HasUnresolvedComponentReferences => Structure.UnresolvedComponentReferences is { Count: > 0 }
        || UnresolvedGuidanceBindings is { Count: > 0 };

    public IReadOnlyDictionary<Guid, GuidanceResolution> Validate(IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        Circuit.Validate();
        Structure.Validate(Circuit);
        var unresolvedGuidance = ComponentReferenceValidation.Guidance(this);
        var available = IndexLibraries(libraries);
        var declared = new Dictionary<Guid, HardwareLibrary>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in KnowledgeLibraries)
        {
            HardwareRepository.ValidatePath(reference.Path);
            if (reference.Id == Guid.Empty || string.IsNullOrWhiteSpace(reference.Revision)
                || !declared.TryAdd(reference.Id, reference) || !paths.Add(reference.Path))
                throw Invalid("Knowledge libraries require distinct identities and repository paths with explicit revisions.");
            if (!available.TryGetValue(reference.Id, out var library))
                throw new AutomationException("missing_knowledge_library", "Supply the declared knowledge library: " + reference.Id);
            if (library.Revision != reference.Revision)
                throw new AutomationException("library_revision_mismatch", "Supply the declared revision for knowledge library: " + reference.Id);
            ComponentGuidance.Validate(library);
        }
        var resolved = new Dictionary<Guid, GuidanceResolution>();
        var owners = new HashSet<Guid>();
        var localStatements = new HashSet<Guid>(Structure.Statements.Select(s => s.Id));
        foreach (var binding in ComponentBindings)
        {
            if (!declared.TryGetValue(binding.LibraryId, out var declaration))
                throw Invalid("Instance guidance must refer to a declared knowledge library.");
            if (declaration.Revision != binding.LibraryRevision)
                throw new AutomationException("library_revision_mismatch", "Instance guidance and its library declaration require the same revision.");
            if (!owners.Add(binding.ComponentInstanceId))
                throw Invalid("A component instance must have one explicit knowledge-class binding.");
            foreach (var statement in binding.Guidance)
                if (!localStatements.Add(statement.Id))
                    throw Invalid("Design-owned statement identities cannot be shared by unrelated owners.");
            if (unresolvedGuidance.Contains(binding.ComponentInstanceId))
                _ = ComponentGuidance.Resolve(available[binding.LibraryId], binding);
            else
                resolved.Add(binding.ComponentInstanceId, ComponentGuidance.Resolve(Circuit, available[binding.LibraryId], binding));
        }
        // Conflicting statements are retained, not discarded or promoted to validated requirements.
        return resolved;
    }

    internal static Dictionary<Guid, ComponentKnowledgeLibrary> IndexLibraries(IReadOnlyCollection<ComponentKnowledgeLibrary> libraries)
    {
        var result = new Dictionary<Guid, ComponentKnowledgeLibrary>();
        foreach (var library in libraries)
            if (library.Id == Guid.Empty || !result.TryAdd(library.Id, library))
                throw Invalid("Supply one exact revision for each knowledge library identity.");
        return result;
    }

    internal static AutomationException Invalid(string message) => new("invalid_engineering_design", message);
}
