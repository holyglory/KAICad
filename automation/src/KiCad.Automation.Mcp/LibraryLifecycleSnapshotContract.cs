using System.Collections.Immutable;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Preliminary managed mirror of the native read-only symbol/footprint-library snapshot boundary.
///
/// This contract is intentionally not a tool response and does not add a protobuf message.  It
/// gives the future native read path one fail-closed validation point while symbol-editor,
/// import/rescue and remote writers remain outside complete lifecycle tracking.
/// </summary>
internal sealed record LibraryLifecycleDependencyBaseline(
    string Path,
    string Sha256,
    ulong Bytes,
    bool Exists = true,
    bool Known = true,
    string Cursor = "");

internal sealed record LibraryLifecycleSnapshot(
    DocumentSpecifier Document,
    string TablePath,
    string TableScope,
    string Nickname,
    string RawUri,
    string ExpandedUri,
    string Provider,
    string DetachedDefinitionJson,
    IReadOnlyList<LibraryLifecycleDependencyBaseline> DependencyBaselines,
    bool ForcedRefresh,
    bool ProviderObservable,
    bool ContentSnapshotComplete,
    bool DiskBaselineChecked,
    bool CompleteChangeTracking,
    bool Postverify);

internal static class LibraryLifecycleSnapshotContract
{
    internal static void ValidateReadOnly(
        LibraryLifecycleSnapshot snapshot,
        DocumentSpecifier expectedDocument,
        string expectedProjectName,
        string expectedProjectPath)
    {
        if (snapshot is null || snapshot.Document is null || expectedDocument is null)
            throw new AutomationException("library_snapshot_incomplete", "A project-bound library snapshot is required.");

        if (!snapshot.Document.Equals(expectedDocument))
            throw new AutomationException("library_document_mismatch",
                "The library snapshot does not identify the exact requested project and library document.");

        DocumentSpecifier document = snapshot.Document;
        bool isSymbol = document.Type == DocumentType.DoctypeSymbol;
        bool isFootprint = document.Type == DocumentType.DoctypeFootprint;
        if ((!isSymbol && !isFootprint) || document.Project is null || document.LibId is null
            || string.IsNullOrWhiteSpace(document.Project.Name) || string.IsNullOrWhiteSpace(document.Project.Path)
            || string.IsNullOrWhiteSpace(document.LibId.LibraryNickname) || string.IsNullOrWhiteSpace(document.LibId.EntryName)
            || document.Project.Name.IndexOf('\0') >= 0 || document.Project.Path.IndexOf('\0') >= 0
            || document.LibId.LibraryNickname.IndexOf('\0') >= 0 || document.LibId.EntryName.IndexOf('\0') >= 0)
            throw new AutomationException("invalid_library_document",
                "A library snapshot requires a project-bound symbol or footprint DocumentSpecifier and library entry.");

        if (string.IsNullOrWhiteSpace(expectedProjectName) || expectedProjectName.IndexOf('\0') >= 0
            || !string.Equals(document.Project.Name, expectedProjectName, StringComparison.Ordinal)
            || !SameAbsolutePath(document.Project.Path, expectedProjectPath))
            throw new AutomationException("library_project_mismatch",
                "The requested library project is not the project served by this native owner.");

        if (string.IsNullOrWhiteSpace(snapshot.Nickname)
            || !string.Equals(snapshot.Nickname, document.LibId.LibraryNickname, StringComparison.Ordinal)
            || snapshot.TableScope is not ("PROJECT" or "GLOBAL")
            || !IsAbsolute(snapshot.TablePath) || string.IsNullOrWhiteSpace(snapshot.RawUri)
            || snapshot.RawUri.IndexOf('\0') >= 0
            || (isSymbol && !IsSymbolFile(snapshot.ExpandedUri))
            || (isFootprint && (!IsFootprintDirectoryUri(snapshot.RawUri)
                                || !IsFootprintFile(snapshot.ExpandedUri))))
            throw new AutomationException("library_table_row_mismatch",
                "The library table row must include the exact scope, table path and raw/expanded URI.");

        if (string.IsNullOrWhiteSpace(snapshot.Provider))
            throw new AutomationException("library_provider_unknown", "The symbol-library provider was not identified.");
        bool providerSupported = string.Equals(snapshot.Provider, "KiCad", StringComparison.Ordinal)
            || (isSymbol && string.Equals(snapshot.Provider, "kicad-symbol-file", StringComparison.Ordinal))
            || (isFootprint && string.Equals(snapshot.Provider, "kicad-footprint-file", StringComparison.Ordinal));
        if (!providerSupported)
            throw new AutomationException("library_provider_unsupported",
                "The library provider is outside the read-only lifecycle slice.");

        if (!snapshot.ForcedRefresh || !snapshot.ProviderObservable || !snapshot.ContentSnapshotComplete
            || !snapshot.DiskBaselineChecked || string.IsNullOrWhiteSpace(snapshot.DetachedDefinitionJson)
            || snapshot.DependencyBaselines is null || snapshot.DependencyBaselines.Count == 0)
            throw new AutomationException("library_snapshot_incomplete",
                "A safe library snapshot requires a forced refresh, detached definition, observable provider and complete dependency evidence.");

        var paths = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        bool hasTableBaseline = false;
        bool hasLibraryBaseline = false;
        foreach (LibraryLifecycleDependencyBaseline baseline in snapshot.DependencyBaselines)
        {
            string? normalizedPath = baseline is null ? null : NormalizeAbsolutePath(baseline.Path);
            if (baseline is null || !baseline.Known || normalizedPath is null || !paths.Add(normalizedPath))
                throw new AutomationException("library_baseline_incomplete",
                    "Every library dependency needs one complete, unique absolute file baseline.");

            bool isTable = SameAbsolutePath(baseline.Path, snapshot.TablePath);
            bool isLibrary = SameAbsolutePath(baseline.Path, snapshot.ExpandedUri);
            if ((isTable || isLibrary) && !baseline.Exists)
                throw new AutomationException("library_baseline_incomplete",
                    "The table and expanded library URI each need an existing content baseline.");

            hasTableBaseline |= isTable && baseline.Exists;
            hasLibraryBaseline |= isLibrary && baseline.Exists;

            if (!string.IsNullOrEmpty(baseline.Cursor) || (baseline.Exists && !IsSha256(baseline.Sha256))
                || (!baseline.Exists && (!string.IsNullOrEmpty(baseline.Sha256) || baseline.Bytes != 0)))
                throw new AutomationException("library_baseline_incomplete",
                    "A partial hash or cursor cannot be used as a symbol-library file baseline.");
        }

        if (!hasTableBaseline || !hasLibraryBaseline)
            throw new AutomationException("library_baseline_incomplete",
                "The table and expanded library URI each need an existing content baseline.");

        // The current owner coverage deliberately remains incomplete.  A true value would claim
        // coverage for GUI editor, import/rescue and remote writers that do not share this owner.
        if (snapshot.CompleteChangeTracking || snapshot.Postverify)
            throw new AutomationException("library_snapshot_incomplete",
                "The read-only foundation cannot claim complete mutation tracking or postverify.");
    }

    internal static void AdmitCheckedMutation(
        LibraryLifecycleSnapshot snapshot,
        DocumentSpecifier expectedDocument,
        string expectedProjectName,
        string expectedProjectPath)
    {
        ValidateReadOnly(snapshot, expectedDocument, expectedProjectName, expectedProjectPath);
        throw new AutomationException("library_mutation_unavailable",
            "Checked library mutation is unavailable until every native writer has shared tracking and rollback proof.");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static bool IsAbsolute(string value) => !string.IsNullOrWhiteSpace(value)
        && value.IndexOf('\0') < 0 && Path.IsPathFullyQualified(value);

    private static bool IsSymbolFile(string value)
    {
        if (!IsAbsolute(value) || HasTrailingSeparator(value)) return false;

        string normalized = NormalizePath(value);
        if (!string.Equals(Path.GetExtension(normalized), ".kicad_sym", StringComparison.OrdinalIgnoreCase))
            return false;

        for (string? parent = Path.GetDirectoryName(normalized);
             !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (string.Equals(Path.GetExtension(Path.GetFileName(parent)), ".kicad_sym",
                StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool IsFootprintFile(string value)
    {
        if (!IsAbsolute(value) || HasTrailingSeparator(value)) return false;

        string normalized = NormalizePath(value);
        if (!string.Equals(Path.GetExtension(normalized), ".kicad_mod", StringComparison.OrdinalIgnoreCase))
            return false;

        for (string? parent = Path.GetDirectoryName(normalized);
             !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (string.Equals(Path.GetExtension(Path.GetFileName(parent)), ".kicad_mod",
                StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool IsFootprintDirectoryUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
            return false;

        string normalized = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.EndsWith(".pretty", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTrailingSeparator(string value) =>
        value.EndsWith(Path.DirectorySeparatorChar) || value.EndsWith(Path.AltDirectorySeparatorChar);

    private static bool SameAbsolutePath(string left, string right) =>
        IsAbsolute(left) && IsAbsolute(right) && string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.Ordinal);

    private static string? NormalizeAbsolutePath(string value) => IsAbsolute(value) ? NormalizePath(value) : null;

    private static string NormalizePath(string value) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
}
