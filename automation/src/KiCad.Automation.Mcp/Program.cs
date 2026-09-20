using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.FirstOrDefault() is "--structural-file" or "--diagram-file")
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        Environment.ExitCode = args[0] == "--diagram-file"
            ? await RecursiveFileCommand.RunAsync(Console.In, Console.Out, cancellation.Token)
            : await StructuralFileCommand.RunAsync(Console.In, Console.Out, cancellation.Token);
    }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}

if (args.FirstOrDefault() == "--launch-installed")
{
    // A forwarded STDIO MCP session is not a finite 15-minute update job.
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try { Environment.ExitCode = await WindowsLaunchCommand.RunAsync(args, Console.Error, cancellation.Token); }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}

if (args.FirstOrDefault() is "--prepare-update" or "--check-update" or "--install-package" or "--restart-update" or "--inspect-update" or "--recover-update" or "--runtime-info")
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        Environment.ExitCode = args[0] switch
        {
            "--install-package" => await LinuxInstallCommand.RunAsync(args, Console.Out, cancellation.Token),
            "--restart-update" => OperatingSystem.IsWindows()
                ? await WindowsRestartCommand.RunAsync(args, Console.Out, cancellation.Token)
                : OperatingSystem.IsMacOS()
                ? await MacRestartCommand.RunAsync(args, Console.Out, cancellation.Token)
                : await LinuxRestartCommand.RunAsync(args, Console.Out, cancellation.Token),
            "--inspect-update" => OperatingSystem.IsWindows()
                ? await WindowsUpdateInspectionCommand.RunAsync(args, Console.Out, cancellation.Token)
                : OperatingSystem.IsMacOS()
                ? await MacUpdateInspectionCommand.RunAsync(args, Console.Out, cancellation.Token)
                : await LinuxUpdateInspectionCommand.RunAsync(args, Console.Out, cancellation.Token),
            "--recover-update" => OperatingSystem.IsWindows()
                ? await WindowsUpdateRecoveryCommand.RunAsync(args, Console.Out, cancellation.Token)
                : OperatingSystem.IsMacOS()
                ? await MacUpdateRecoveryCommand.RunAsync(args, Console.Out, cancellation.Token)
                : await LinuxUpdateRecoveryCommand.RunAsync(args, Console.Out, cancellation.Token),
            "--runtime-info" => await RuntimeInfoCommand.RunAsync(args, Console.Out, cancellation.Token),
            _ => await UpdatePreparationCommand.RunAsync(args, Console.Out, cancellation.Token)
        };
    }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
string stateDirectory = builder.Configuration["state-directory"]
    ?? Environment.GetEnvironmentVariable("KICAD_AUTOMATION_STATE_DIRECTORY")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kicad-automation");
builder.Services.AddSingleton<INativeTransport, NngTransport>();
builder.Services.AddSingleton<FileIntakeRegistry>();
builder.Services.AddSingleton<NativeIntakeRegistry>();
builder.Services.AddSingleton<AutomaticDesignRegistry>();
builder.Services.AddSingleton(provider => new InstanceRegistry(provider.GetRequiredService<INativeTransport>(), stateDirectory));
builder.Services.AddMcpServer(options =>
{
    options.ServerInstructions = "Operate only explicitly identified KiCad instances. Listed registrations are not proof a process is still live: inspect before use. Do not edit native design files behind a live editor. Mutations require explicit targets and the documented revision/retry identities. After a lost mutation reply, inspect its receipt or retry identical arguments with the same operation ID. Do not infer routing, complete revision tracking or XML reconstruction support from a successful connection.";
})
    .WithStdioServerTransport()
    .WithTools<InstanceTools>()
    .WithTools<InstanceUpdateTools>()
    .WithTools<EventTools>()
    .WithTools<SchematicViewTools>()
    .WithTools<StructuralEditorTools>()
    .WithTools<RecursiveEditorTools>()
    .WithTools<SchematicMutationTools>()
    .WithTools<CheckedSchematicTools>()
    .WithTools<SchematicXmlTools>()
    .WithTools<DocumentTools>()
    .WithTools<DocumentStateTools>()
    .WithTools<DocumentLifecycleTools>()
    .WithTools<PcbDrcTools>()
    .WithTools<KnowledgeTools>()
    .WithTools<PlacementTools>()
    .WithTools<RecoveryTools>()
    .WithTools<FileIntakeTools>()
    .WithTools<NativeIntakeTools>()
    .WithTools<AutomaticDesignTools>()
    .WithTools<SimulationTools>();
await builder.Build().RunAsync();
