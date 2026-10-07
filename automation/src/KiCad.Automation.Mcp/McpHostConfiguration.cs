using Microsoft.Extensions.Hosting;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Builds the ordinary .NET host while disabling only the default appsettings
/// reload watchers. Product document and intake watchers are registered by
/// their own services and remain unaffected.
/// </summary>
internal static class McpHostConfiguration
{
    private const string ReloadConfigOnChangePrefix = "--hostBuilder:reloadConfigOnChange=";

    internal static HostApplicationBuilder CreateBuilder(string[] args) =>
        Host.CreateApplicationBuilder(WithReloadWatchersDisabled(args));

    internal static string[] WithReloadWatchersDisabled(string[] args)
    {
        if (args is null || args.Length == 0)
            return [ReloadConfigOnChangePrefix + "false"];

        // Keep all caller configuration and precedence intact. The final
        // reserved host switch wins over an accidental caller override while
        // leaving state-directory and other application arguments untouched.
        return [.. args, ReloadConfigOnChangePrefix + "false"];
    }
}
