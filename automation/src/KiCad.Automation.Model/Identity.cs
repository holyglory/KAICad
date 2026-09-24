namespace KiCad.Automation.Model;

/// <summary>A native process epoch and its monotonically increasing document revision.</summary>
public sealed record DocumentRevision(string Epoch, ulong Sequence)
{
    public void RequireSame(DocumentRevision expected)
    {
        if (this != expected)
            throw new AutomationException("stale_revision", "The document changed; obtain a new observation before editing.");
    }
}

/// <summary>Persistent identity is independent of reference designators and placement.</summary>
public sealed record SchematicObjectId(Guid ObjectId, IReadOnlyList<Guid> SheetPath)
{
    public string Key => string.Join('/', SheetPath.Append(ObjectId).Select(id => id.ToString("D")));
}

/// <summary>One object that caused a structured refusal, for example a connection or a realization
/// that still uses a boundary interface (contract rbg-v2 section 4.5, G6).</summary>
public sealed record AutomationErrorDetail(string Kind, Guid? ScopeBlockId, Guid? ObjectId, string Message);

/// <summary>A refusal with a stable code. Optional details name the objects that caused it
/// (contract rbg-v2 G6); they never replace the code.</summary>
public sealed class AutomationException(string code, string message, IReadOnlyList<AutomationErrorDetail>? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<AutomationErrorDetail> Details { get; } = details is null ? [] : [.. details];
}

public enum VerificationState { Unknown, Unverified, Verified, Violated, Unsupported, Conflicting }

public sealed record SourceReference(string DocumentId, string Revision, int? Page, string? Table,
                                    string? PartVariant);

/// <summary>A quantity never implies an operating condition or an evidence-backed result.</summary>
public sealed record EngineeringQuantity(decimal? Value, string Unit, decimal? Tolerance,
                                         string? OperatingCondition, SourceReference? Source,
                                         VerificationState Verification);
