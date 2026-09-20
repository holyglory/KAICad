namespace KiCad.Automation.Model;

public enum ImplementationChangeKind { Rename, Archive, Restore }

/// <summary>Append-only management history. Removing an implementation from choices
/// never deletes the revisions referenced by a historical root or source fork.</summary>
public sealed record ImplementationChange(Guid Id, Guid StateId, ImplementationChangeKind Kind,
    string BeforeName, string AfterName, bool BeforeArchived, bool AfterArchived, RequirementRevisionOrigin Origin);
