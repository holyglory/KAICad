using System.Text.RegularExpressions;

namespace KiCad.Automation.Tests;

internal sealed record MacRetainedPackageInput(string RunId, string Attempt, string Commit, string Sha256)
{
    public string ArtifactName => $"native-macos-arm64-{Commit}-{Attempt}";
    public string ArchiveName => $"unqualified-macos-{Commit}.tar.gz";

    public static MacRetainedPackageInput Parse(string value)
    {
        if (value.Length > 160 || !Regex.IsMatch(value,
                @"\A[1-9][0-9]{0,19}/[1-9][0-9]{0,5}/[0-9a-f]{40}/[0-9a-f]{64}\z",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Require the exact Mac build run/attempt/source commit/archive SHA-256.");
        string[] parts = value.Split('/');
        return new(parts[0], parts[1], parts[2], parts[3]);
    }
}
