using System.Security.Cryptography;
using System.Text;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Managed admission contract for the future detached PCB manufacturing
/// worker.  The current native manager still exposes only owner-thread
/// adapters; this record describes the evidence a worker must receive before
/// it may consume a serialized board/project snapshot.
///
/// This is deliberately a transport-independent contract.  It does not turn
/// lifecycle paths or a board hash into a detached snapshot, and it does not
/// advertise worker support until the native adapter returns this evidence.
/// </summary>
internal sealed record PcbManufacturingDependencyBaseline(
    string Path,
    string Sha256,
    ulong Bytes,
    bool Exists = true,
    bool Known = true);

internal sealed record PcbManufacturingDetachedSnapshot(
    DocumentSpecifier Document,
    string SerializedBoard,
    string SerializedProject,
    string SnapshotSha256,
    IReadOnlyList<PcbManufacturingDependencyBaseline> DependencyBaselines,
    bool BoardSerializationComplete,
    bool ProjectSerializationComplete,
    bool DependencyCaptureComplete,
    bool DiskBaselineChecked);

internal static class PcbManufacturingSnapshotContract
{
    private const int MaxSerializedBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Validate the complete immutable input required by a detached exporter.
    /// Every path and byte baseline is checked before the worker can be
    /// admitted.  The lifecycle observation remains a separate owner-thread
    /// check and must be re-read before publication.
    /// </summary>
    internal static void Validate(
        PcbManufacturingDetachedSnapshot snapshot,
        DocumentSpecifier expectedDocument)
    {
        if (snapshot is null || snapshot.Document is null || expectedDocument is null)
            throw Invalid("A detached manufacturing snapshot is required.");

        if (!snapshot.Document.Equals(expectedDocument))
            throw Invalid("The detached manufacturing snapshot does not identify the requested PCB document.");

        DocumentSpecifier document = snapshot.Document;
        if (document.Type != DocumentType.DoctypePcb || document.Project is null
            || string.IsNullOrWhiteSpace(document.Project.Name)
            || string.IsNullOrWhiteSpace(document.Project.Path)
            || string.IsNullOrWhiteSpace(document.BoardFilename)
            || document.Project.Name.IndexOf('\0') >= 0
            || document.Project.Path.IndexOf('\0') >= 0
            || document.BoardFilename.IndexOf('\0') >= 0
            || Path.GetFileName(document.BoardFilename) != document.BoardFilename)
            throw Invalid("A detached manufacturing snapshot requires one project-bound PCB document.");

        if (!snapshot.BoardSerializationComplete || !snapshot.ProjectSerializationComplete
            || !snapshot.DependencyCaptureComplete || !snapshot.DiskBaselineChecked)
            throw Invalid("The detached manufacturing snapshot is incomplete.");

        ValidateSerializedBoard(snapshot.SerializedBoard);
        ValidateSerializedProject(snapshot.SerializedProject);
        if (!IsSha256(snapshot.SnapshotSha256))
            throw Invalid("The detached manufacturing snapshot needs one canonical SHA-256 digest.");

        if (snapshot.DependencyBaselines is null || snapshot.DependencyBaselines.Count == 0)
            throw Invalid("The detached manufacturing snapshot needs complete dependency baselines.");

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var baselines = new Dictionary<string, PcbManufacturingDependencyBaseline>(pathComparer);
        foreach (PcbManufacturingDependencyBaseline baseline in snapshot.DependencyBaselines)
        {
            if (baseline is null || !baseline.Known)
                throw Invalid("Every detached manufacturing dependency needs a known baseline.");

            string path = CanonicalPath(baseline.Path);
            if (!baselines.TryAdd(path, baseline))
                throw Invalid("Detached manufacturing dependencies must have unique canonical paths.");

            if (baseline.Exists)
            {
                if (!IsSha256(baseline.Sha256) || baseline.Bytes == 0)
                    throw Invalid("Every existing detached manufacturing dependency needs a complete hash and size.");
            }
            else if (!string.IsNullOrEmpty(baseline.Sha256) || baseline.Bytes != 0)
            {
                throw Invalid("An absent detached manufacturing dependency cannot carry a partial baseline.");
            }
        }

        string boardPath = CanonicalPath(Path.Combine(document.Project.Path, document.BoardFilename));
        string projectPath = CanonicalPath(Path.Combine(document.Project.Path, document.Project.Name + ".kicad_pro"));
        if (!baselines.TryGetValue(boardPath, out PcbManufacturingDependencyBaseline? board)
            || !board.Exists || !baselines.TryGetValue(projectPath, out PcbManufacturingDependencyBaseline? project)
            || !project.Exists)
            throw Invalid("Detached manufacturing dependencies must include existing board and project baselines.");

        if (!string.Equals(snapshot.SnapshotSha256, ComputeSnapshotSha256(snapshot), StringComparison.Ordinal))
            throw Invalid("The detached manufacturing snapshot digest does not match its serialized content and dependencies.");
    }

    internal static string ComputeSnapshotSha256(PcbManufacturingDetachedSnapshot snapshot)
    {
        if (snapshot is null) throw Invalid("A detached manufacturing snapshot is required.");

        var canonical = new StringBuilder();
        Append(canonical, snapshot.SerializedBoard);
        Append(canonical, snapshot.SerializedProject);
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (PcbManufacturingDependencyBaseline baseline in (snapshot.DependencyBaselines ?? [])
                     .OrderBy(item => CanonicalPath(item.Path), pathComparer))
        {
            Append(canonical, CanonicalPath(baseline.Path));
            Append(canonical, baseline.Sha256);
            Append(canonical, baseline.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, baseline.Exists ? "1" : "0");
            Append(canonical, baseline.Known ? "1" : "0");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void ValidateSerializedBoard(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0
            || value.Length > MaxSerializedBytes || !value.TrimStart().StartsWith("(kicad_pcb", StringComparison.Ordinal))
            throw Invalid("The detached manufacturing snapshot needs one complete serialized PCB board.");
    }

    private static void ValidateSerializedProject(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0
            || value.Length > MaxSerializedBytes || !value.TrimStart().StartsWith('{'))
            throw Invalid("The detached manufacturing snapshot needs one complete serialized project.");
    }

    private static string CanonicalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0
            || !Path.IsPathFullyQualified(value))
            throw Invalid("Detached manufacturing dependency paths must be absolute and NUL-free.");
        try { return Path.GetFullPath(value); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw Invalid("Detached manufacturing dependency paths must be valid absolute paths."); }
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(value.Length).Append(':').Append(value).Append('\n');
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static AutomationException Invalid(string message) =>
        new("pcb_export_snapshot_incomplete", message);
}
