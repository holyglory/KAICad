using System.Diagnostics;
using System.Text.Json;
using KiCad.Automation.Distribution;

namespace KiCad.Automation.Validation;

public static class HostedValidatorIsolation
{
    internal static void RequireOutside(string repository, string executableDirectory)
    {
        if (!Path.IsPathFullyQualified(repository) || !Path.IsPathFullyQualified(executableDirectory))
            throw new ArgumentException("Validator isolation requires absolute source and runtime directories.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        string runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (runtime.Equals(root, comparison) || runtime.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Publish the running validator outside the source checkout before rebuilding managed projects.");
    }

    // Native Windows execution demonstrates that a forced rebuild cannot try
    // to replace assemblies loaded by this process. Output remains cold evidence.
    public static async Task VerifyAsync(string repository, string output, CancellationToken token)
    {
        RequireOutside(repository, AppContext.BaseDirectory);
        RequireOutside(repository, Path.GetDirectoryName(typeof(UpdateDownloader).Assembly.Location)!);
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("Use a new absolute evidence directory.");
        string project = Path.Combine(repository, "automation/tools/KiCad.Automation.Validation/KiCad.Automation.Validation.csproj");
        if (!File.Exists(project)) throw new FileNotFoundException("Validator source project is missing.", project);
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(output);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "build", project, "--configuration", "Release", "--no-restore", "--target:Rebuild" })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Could not start the isolated rebuild probe.");
        await using var stdout = File.Create(Path.Combine(output, "rebuild.stdout.log"));
        await using var stderr = File.Create(Path.Combine(output, "rebuild.stderr.log"));
        Task readOut = child.StandardOutput.BaseStream.CopyToAsync(stdout);
        Task readErr = child.StandardError.BaseStream.CopyToAsync(stderr);
        try { await child.WaitForExitAsync(token); }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(); await Task.WhenAll(readOut, readErr);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, status = child.ExitCode == 0 ? "passed" : "failed", child.ExitCode,
            operatingSystem = Environment.OSVersion.Platform.ToString(), isolatedRuntime = true,
            loadedDependencyOutsideSource = true, forcedManagedRebuild = true }), token);
        if (child.ExitCode != 0) throw new IOException("Isolated validator rebuild failed; inspect retained rebuild logs.");
    }
}
