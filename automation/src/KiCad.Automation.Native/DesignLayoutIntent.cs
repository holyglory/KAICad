using System.Text.Json.Serialization;

namespace KiCad.Automation.Native;

/// <summary>An exact requested design before native connected movement has
/// resolved wire geometry. These bytes are not a publishable final candidate.</summary>
public sealed record DesignLayoutIntent(
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string DesignPath,
    [property: JsonRequired] byte[] ExpectedFileBytes,
    [property: JsonRequired] byte[] PlannedDesignFileBytes,
    [property: JsonRequired] string RequestedRecoveryRevisionToken)
{
    public static DesignLayoutIntent Create(string designPath, byte[] expected, byte[] planned,
        Guid operationId, string requestedRecoveryRevisionToken)
    {
        if (!Path.IsPathFullyQualified(designPath)) throw new ArgumentException("An absolute XML destination is required.", nameof(designPath));
        return new(operationId, Path.GetFullPath(designPath), expected.ToArray(), planned.ToArray(), requestedRecoveryRevisionToken);
    }
}
