using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Admission and receipt invariants for the typed PCB manufacturing envelope.
///
/// The native exporter owns the files and publication transaction.  This boundary
/// makes sure a reply is still about the requested board and operation before it is
/// exposed to an MCP caller, and that a completed reply carries a complete published
/// manifest rather than a partial or staged result.
/// </summary>
internal static class PcbManufacturingContract
{
    /// <summary>
    /// The typed recovery manager currently owns only the four synchronous
    /// owner-thread adapters exposed by its native contract. Keep the other
    /// board jobs out of this path until each one can consume the manager's
    /// immutable serialized snapshot without resolving editor/KIWAY state.
    /// </summary>
    public static void ValidateTypedRecoveryJob(RunPcbManufacturingExport request)
    {
        if (request is null)
            throw Invalid("A manufacturing export request is required.");

        if (request.JobCase is RunPcbManufacturingExport.JobOneofCase.ExportSvg
            or RunPcbManufacturingExport.JobOneofCase.ExportGerbers
            or RunPcbManufacturingExport.JobOneofCase.ExportOdb
            or RunPcbManufacturingExport.JobOneofCase.ExportIpc2581)
            return;

        string job = request.JobCase switch
        {
            RunPcbManufacturingExport.JobOneofCase.ExportStats => "board statistics",
            RunPcbManufacturingExport.JobOneofCase.Export3D => "3D",
            RunPcbManufacturingExport.JobOneofCase.ExportRender => "render",
            RunPcbManufacturingExport.JobOneofCase.ExportDxf => "DXF",
            RunPcbManufacturingExport.JobOneofCase.ExportPdf => "PDF",
            RunPcbManufacturingExport.JobOneofCase.ExportPs => "PostScript",
            RunPcbManufacturingExport.JobOneofCase.ExportDrill => "drill",
            RunPcbManufacturingExport.JobOneofCase.ExportPosition => "position",
            RunPcbManufacturingExport.JobOneofCase.ExportGencad => "GenCAD",
            RunPcbManufacturingExport.JobOneofCase.ExportIpcD356 => "IPC-D-356",
            _ => "the requested job"
        };

        string detail = request.JobCase == RunPcbManufacturingExport.JobOneofCase.ExportStats
            ? "The native stats job resolves its board through PCBNEW_JOBS_HANDLER and KIWAY::ProcessJob, and has no serialized-snapshot input."
            : "The native job has no serialized-snapshot adapter.";

        throw new AutomationException("pcb_export_detached_unsupported",
            $"Typed recovery cannot dispatch {job} from the manager worker boundary. {detail} "
            + "Use the existing synchronous kicad_pcb_export tool or wait for a native detached adapter.");
    }

    public static void ValidateRequest(RunPcbManufacturingExport request)
    {
        if (request is null)
            throw Invalid("A manufacturing export request is required.");
        CanonicalUuid(request.OperationId, "operation ID");
        CanonicalUuid(request.ProcessEpoch, "process epoch");

        try { DocumentStateTools.ValidateTarget(request.Document); }
        catch (AutomationException error) { throw Invalid(error.Message); }

        if (request.Document!.Type != DocumentType.DoctypePcb)
            throw Invalid("A manufacturing export requires an explicit PCB document.");
        if (request.ExpectedState is null
            || request.ExpectedState.Scope != DocumentLifecycleScope.DlsPcb
            || request.ExpectedState.Document is null
            || !request.ExpectedState.Document.Equals(request.Document)
            || request.ExpectedState.ProcessEpoch != request.ProcessEpoch
            || !Sha256(request.ExpectedState.StateSha256))
            throw Invalid("The manufacturing export must carry a complete PCB lifecycle observation for the same process epoch.");
        if (string.IsNullOrWhiteSpace(request.OutputDirectory)
            || !Path.IsPathFullyQualified(request.OutputDirectory))
            throw Invalid("The manufacturing output directory must be absolute.");
        if (request.JobCase == RunPcbManufacturingExport.JobOneofCase.None)
            throw Invalid("Exactly one concrete manufacturing job is required.");
    }

    public static void ValidateReceipt(PcbManufacturingExportReceipt receipt,
        DocumentSpecifier document, DocumentLifecycleState? expectedState,
        string operationId, string processEpoch, string? expectedRequestFingerprintSha256 = null)
    {
        if (receipt is null)
            throw Invalid("The native manufacturing receipt is missing.");
        CanonicalUuid(operationId, "operation ID");
        CanonicalUuid(processEpoch, "process epoch");
        if (receipt.OperationId != operationId || receipt.ProcessEpoch != processEpoch
            || receipt.Document is null || !receipt.Document.Equals(document))
            throw Invalid("The native manufacturing receipt does not match the requested PCB, process epoch or operation.");
        if (!Sha256(receipt.RequestFingerprintSha256) || string.IsNullOrWhiteSpace(receipt.JobKind))
            throw Invalid("The native manufacturing receipt lacks request and job identity.");
        if (expectedRequestFingerprintSha256 is not null
            && (!Sha256(expectedRequestFingerprintSha256)
                || !string.Equals(receipt.RequestFingerprintSha256, expectedRequestFingerprintSha256,
                    StringComparison.Ordinal)))
            throw Invalid("The native manufacturing receipt fingerprint does not match the submitted request.");
        if (!Enum.IsDefined(receipt.Status) || receipt.Status == PcbManufacturingExportStatus.PcbMfgExportStatusUnspecified
            || !Enum.IsDefined(receipt.DispatchStage) || receipt.DispatchStage == PcbManufacturingDispatchStage.PcbMfgDispatchUnspecified
            || !Enum.IsDefined(receipt.PublicationState) || receipt.PublicationState == PcbManufacturingPublicationState.PcbMfgPublicationUnspecified)
            throw Invalid("The native manufacturing receipt has an unknown lifecycle status.");

        bool terminalStatus = receipt.Status is PcbManufacturingExportStatus.PcbMfgExportCompleted
            or PcbManufacturingExportStatus.PcbMfgExportFailed
            or PcbManufacturingExportStatus.PcbMfgExportIndeterminate
            or PcbManufacturingExportStatus.PcbMfgExportUnsubmitted;
        if (receipt.Terminal != terminalStatus)
            throw Invalid("The native manufacturing terminal flag does not match its status.");
        if (!receipt.Terminal && receipt.DispatchStage is PcbManufacturingDispatchStage.PcbMfgDispatchTerminal
            or PcbManufacturingDispatchStage.PcbMfgDispatchPublicationFailed)
            throw Invalid("A nonterminal manufacturing receipt cannot report a terminal dispatch stage.");
        if (receipt.Terminal && (receipt.Status is PcbManufacturingExportStatus.PcbMfgExportAdmitted
            or PcbManufacturingExportStatus.PcbMfgExportRunning))
            throw Invalid("An admitted or running manufacturing receipt cannot be terminal.");

        if (receipt.ExpectedState is null || receipt.ObservedBefore is null
            || receipt.ExpectedState.Document is null || receipt.ObservedBefore.Document is null
            || !receipt.ExpectedState.Document.Equals(document)
            || !receipt.ObservedBefore.Document.Equals(document)
            || receipt.ExpectedState.Scope != DocumentLifecycleScope.DlsPcb
            || receipt.ObservedBefore.Scope != DocumentLifecycleScope.DlsPcb
            || !Sha256(receipt.ExpectedState.StateSha256)
            || !Sha256(receipt.ObservedBefore.StateSha256)
            || receipt.ExpectedState.ProcessEpoch != processEpoch
            || receipt.ObservedBefore.ProcessEpoch != processEpoch
            || !receipt.ExpectedState.Equals(receipt.ObservedBefore))
            throw Invalid("The native manufacturing receipt lacks the requested lifecycle observation.");
        if (expectedState is not null && (!receipt.ExpectedState.Equals(expectedState)
            || !receipt.ObservedBefore.Equals(expectedState)))
            throw Invalid("The native manufacturing receipt changed the lifecycle observation before export.");

        if (!string.IsNullOrWhiteSpace(receipt.OutputDirectory)
            && !Path.IsPathFullyQualified(receipt.OutputDirectory))
            throw Invalid("The native manufacturing output directory must be absolute.");
        if (!string.IsNullOrWhiteSpace(receipt.StagingDirectory)
            && !Path.IsPathFullyQualified(receipt.StagingDirectory))
            throw Invalid("The native manufacturing staging directory must be absolute.");

        ValidateArtifacts(receipt);
        if (receipt.Status == PcbManufacturingExportStatus.PcbMfgExportCompleted)
        {
            if (!receipt.Terminal || receipt.DispatchStage != PcbManufacturingDispatchStage.PcbMfgDispatchTerminal
                || receipt.PublicationState != PcbManufacturingPublicationState.PcbMfgPublicationPublished
                || !receipt.OutputManifestComplete || !Sha256(receipt.OutputManifestSha256)
                || receipt.Artifacts.Count == 0 || receipt.ObservedAfter is null
                || !receipt.ObservedAfter.Equals(receipt.ExpectedState)
                || (expectedState is not null && !receipt.ObservedAfter.Equals(expectedState)))
                throw Invalid("A completed manufacturing export must carry a published, complete manifest and unchanged post-export state.");
            if (!string.IsNullOrWhiteSpace(receipt.ErrorCode) || !string.IsNullOrWhiteSpace(receipt.ErrorMessage))
                throw Invalid("A completed manufacturing export cannot carry an error.");
        }
        else
        {
            if (receipt.PublicationState == PcbManufacturingPublicationState.PcbMfgPublicationPublished)
                throw Invalid("Only a completed manufacturing export may report published output.");
            if (receipt.Terminal && (string.IsNullOrWhiteSpace(receipt.ErrorCode)
                || string.IsNullOrWhiteSpace(receipt.ErrorMessage)))
                throw Invalid("A terminal unsuccessful manufacturing export must retain an error code and message.");
        }
    }

    private static void ValidateArtifacts(PcbManufacturingExportReceipt receipt)
    {
        if (receipt.Artifacts.Count > 4096)
            throw Invalid("The native manufacturing manifest contains too many artifacts.");
        if (receipt.Artifacts.Count == 0) return;
        string requestedRoot = receipt.Status == PcbManufacturingExportStatus.PcbMfgExportCompleted
            ? receipt.OutputDirectory
            : (string.IsNullOrWhiteSpace(receipt.StagingDirectory) ? receipt.OutputDirectory : receipt.StagingDirectory);
        if (string.IsNullOrWhiteSpace(requestedRoot) || !Path.IsPathFullyQualified(requestedRoot))
            throw Invalid("Manufacturing artifacts require an absolute published or staging directory.");

        string root;
        try { root = Path.GetFullPath(requestedRoot); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw Invalid("The native manufacturing output directory is not a valid path."); }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var relativePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (PcbManufacturingArtifact artifact in receipt.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Path) || !Path.IsPathFullyQualified(artifact.Path)
                || string.IsNullOrWhiteSpace(artifact.RelativePath) || !Sha256(artifact.Sha256)
                || !Enum.IsDefined(artifact.Ownership)
                || artifact.Ownership == PcbManufacturingArtifactOwnership.PcbMfgArtifactOwnershipUnspecified)
                throw Invalid("Every manufacturing artifact needs a unique absolute path, relative path, hash and ownership.");
            string relative = artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(relative))
                throw Invalid("A manufacturing artifact relative path must remain inside the output directory.");
            string path, reported, relativeFromRoot;
            try
            {
                path = Path.GetFullPath(Path.Combine(root, relative));
                reported = Path.GetFullPath(artifact.Path);
                relativeFromRoot = Path.GetRelativePath(root, path);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            { throw Invalid("A manufacturing artifact path is not valid."); }
            if (relativeFromRoot is "." or ".."
                || relativeFromRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !PathEquals(path, reported)
                || !paths.Add(reported) || !relativePaths.Add(artifact.RelativePath))
                throw Invalid("Manufacturing artifacts must have distinct paths contained by the published output directory.");
        }
    }

    private static void CanonicalUuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty
            || !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
            throw Invalid($"The manufacturing {name} must be a nonempty canonical UUID.");
    }

    private static bool Sha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static AutomationException Invalid(string message) => new("invalid_pcb_export_receipt", message);
}
