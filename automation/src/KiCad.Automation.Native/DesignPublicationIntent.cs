using System.Text.Json.Serialization;

namespace KiCad.Automation.Native;

public enum DesignPublicationPhase { Prepared, Staged, Attempting, Published, Converged }

/// <summary>The exact file operation belongs to the existing design recovery
/// record. Retained paths are not guessed from unrelated temporary files.</summary>
public sealed record DesignPublicationIntent(
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string DesignPath,
    [property: JsonRequired] string StagedPath,
    [property: JsonRequired] string PreviousPath,
    [property: JsonRequired] byte[] ExpectedFileBytes,
    [property: JsonRequired] byte[] CandidateFileBytes,
    [property: JsonRequired] DesignPublicationPhase Phase,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedRecoveryRevisionToken = null)
{
    public static DesignPublicationIntent Create(string designPath, byte[] expected, byte[] candidate,
        Guid? operationId = null, string? requestedRecoveryRevisionToken = null)
    {
        if (!Path.IsPathFullyQualified(designPath)) throw new ArgumentException("An absolute XML destination is required.", nameof(designPath));
        Guid operation = operationId ?? Guid.NewGuid();
        string target = Path.GetFullPath(designPath), staged = target + ".sync-" + operation.ToString("N");
        return new(operation, target, staged, PreservingFileReplacement.PreviousPath(staged),
            expected.ToArray(), candidate.ToArray(), DesignPublicationPhase.Prepared, requestedRecoveryRevisionToken);
    }
}
