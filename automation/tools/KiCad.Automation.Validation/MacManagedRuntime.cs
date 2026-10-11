using System.Runtime.InteropServices;
using System.Text.Json;

namespace KiCad.Automation.Validation;

public sealed record MacManagedRuntimeEvidence(string TargetArchitecture, IReadOnlyList<NativeBinaryEvidence> Binaries,
    string NngRelativePath, string Framework, string NngVersion);

public static class MacManagedRuntime
{
    public static string RuntimeIdentifier(string architecture) => architecture switch
    {
        "arm64" => "osx-arm64", "x64" => "osx-x64",
        _ => throw new ArgumentException("Select arm64 or x64 for the Mac runtime.")
    };

    public static string FindBundledNng(string bundle)
    {
        if (!Path.IsPathFullyQualified(bundle)) throw new ArgumentException("Use an absolute native app bundle path.");
        string frameworks = Path.Combine(bundle, "Contents", "Frameworks");
        if (!Directory.Exists(frameworks)) throw new DirectoryNotFoundException("The native app Frameworks directory is missing.");
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(frameworks, "libnng*.dylib", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            string resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
            string relative = Path.GetRelativePath(frameworks, resolved);
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative)
                || !File.Exists(resolved))
                throw new InvalidDataException("The bundled NNG library is missing or points outside the native bundle.");
            candidates.Add(resolved);
        }
        if (candidates.Count != 1)
            throw new InvalidDataException("The native bundle must contain one unambiguous shared NNG library for the MCP runtime.");
        return candidates.Single();
    }

    public static string FindBundledZstd(string bundle)
    {
        if (!Path.IsPathFullyQualified(bundle)) throw new ArgumentException("Use an absolute native app bundle path.");
        string frameworks = Path.Combine(bundle, "Contents", "Frameworks");
        if (!Directory.Exists(frameworks)) throw new DirectoryNotFoundException("The native app Frameworks directory is missing.");
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(frameworks, "libzstd*.dylib", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            string resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
            string relative = Path.GetRelativePath(frameworks, resolved);
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative)
                || !File.Exists(resolved))
                throw new InvalidDataException("The bundled zstd library is missing or points outside the native bundle.");
            candidates.Add(resolved);
        }
        if (candidates.Count != 1)
            throw new InvalidDataException("The native bundle must contain one unambiguous shared zstd library for the MCP runtime.");
        return candidates.Single();
    }

    public static (string Framework, string NngVersion) ReadProbe(string json, string architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("status").GetString() != "runtime_available"
            || root.GetProperty("nativeEditorContacted").GetBoolean() || root.GetProperty("crossPlatformReady").GetBoolean()
            || !Enum.TryParse<Architecture>(root.GetProperty("processArchitecture").GetString(), out var process))
            throw new InvalidDataException("The compiled runtime probe did not report a valid local runtime check.");
        MacArchitecture.RequireExecutionTarget(architecture, process);
        string framework = root.GetProperty("framework").GetString() ?? "";
        string nng = root.GetProperty("nngVersion").GetString() ?? "";
        if (!framework.StartsWith(".NET 10.", StringComparison.Ordinal) || nng.Length is < 1 or > 128)
            throw new InvalidDataException("The Mac runtime requires .NET 10 and an actual NNG version result.");
        return (framework, nng);
    }

    public static void ValidateEvidence(MacManagedRuntimeEvidence runtime, string architecture)
    {
        if (runtime.TargetArchitecture != architecture || runtime.Binaries is null || runtime.Binaries.Any(b => b is null)
            || !runtime.Binaries.Select(b => b.Name).Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "kicad-mcp", "libcoreclr.dylib", "libhostpolicy.dylib", "libnng.dylib" }))
            throw new InvalidDataException("The Mac runtime receipt must identify the matching host, runtime and NNG binaries.");
        if (runtime.NngRelativePath is null || !runtime.NngRelativePath.StartsWith("Contents/Frameworks/", StringComparison.Ordinal)
            || runtime.NngRelativePath.Split('/').Any(part => part is ".." or "." or "")
            || runtime.Framework is null || !runtime.Framework.StartsWith(".NET 10.", StringComparison.Ordinal)
            || runtime.NngVersion is null || runtime.NngVersion.Length is < 1 or > 128)
            throw new InvalidDataException("The Mac runtime receipt contains invalid dependency identities.");
        foreach (var binary in runtime.Binaries)
        {
            MacArchitecture.RequireBinaryTarget(architecture, binary.Architectures);
            if (binary.Sha256?.Length != 64 || binary.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new InvalidDataException("A Mac runtime binary checksum is invalid.");
        }
    }
}
