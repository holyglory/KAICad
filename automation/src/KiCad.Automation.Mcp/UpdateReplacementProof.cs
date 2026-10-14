namespace KiCad.Automation.Mcp;

/// <summary>Point-in-time old-to-new identity read from a verified native
/// handoff. Never accepted directly as input to an MCP tool.</summary>
public sealed record UpdateReplacementProof(Guid OperationId, Guid InstanceId, string ProjectPath,
    UpdateOrigin Origin, int OriginalProcessId, int ReplacementProcessId, string Endpoint, string Epoch)
{
    internal static UpdateReplacementProof? FromInspection(UpdateOrigin? origin, string originalStatus,
        Guid operation, Guid instance, string project, int oldPid, int newPid, string endpoint, string epoch)
    {
        if (origin is null || originalStatus is not ("exited" or "identity_changed")) return null;
        origin.Validate();
        // An empty project identifies KiCad's explicit update-manager session.
        // It is a valid native origin and must remain reconnectable after the
        // manager itself is replaced; ordinary project sessions still require
        // an absolute .kicad_pro path.
        if (epoch == origin.Epoch || string.IsNullOrWhiteSpace(epoch) || oldPid <= 0 || newPid <= 0
            || operation == Guid.Empty || instance == Guid.Empty
            || project.Length != 0 && !Path.IsPathFullyQualified(project)) return null;
        return new(operation, instance, project, origin, oldPid, newPid, endpoint, epoch);
    }
}
