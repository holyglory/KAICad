using System.Text.Json.Serialization;

namespace KiCad.Automation.Native;

/// <summary>An exact requested design before native connected movement has
/// resolved wire geometry. These bytes are not a publishable final candidate.
/// <see cref="Lane"/> records which synchronization lane planned the pending batch;
/// absent means an ordinary connected move, so existing records are unchanged.</summary>
public sealed record DesignLayoutIntent(
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string DesignPath,
    [property: JsonRequired] byte[] ExpectedFileBytes,
    [property: JsonRequired] byte[] PlannedDesignFileBytes,
    [property: JsonRequired] string RequestedRecoveryRevisionToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Lane = null)
{
    /// <summary>CN-1 connection realization (lane 2A).</summary>
    public const string ConnectionRealizationLane = "connection-realization";
    /// <summary>Provisional native rebuild seam (lane 2C).</summary>
    public const string RebuildLane = "rebuild";

    public static DesignLayoutIntent Create(string designPath, byte[] expected, byte[] planned,
        Guid operationId, string requestedRecoveryRevisionToken, string? lane = null)
    {
        if (!Path.IsPathFullyQualified(designPath)) throw new ArgumentException("An absolute XML destination is required.", nameof(designPath));
        if (lane is not null and not ConnectionRealizationLane and not RebuildLane)
            throw new ArgumentException("Unknown synchronization lane.", nameof(lane));
        return new(operationId, Path.GetFullPath(designPath), expected.ToArray(), planned.ToArray(), requestedRecoveryRevisionToken, lane);
    }
}
