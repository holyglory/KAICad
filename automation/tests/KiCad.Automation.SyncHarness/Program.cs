using System.ComponentModel;
using System.Text.Json;
using Kiapi.Common;
using KiCad.Automation.Model;
using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Test-only host. No production CLI switch or default MCP registration enables
// the unqualified high-level tool. All actual execution uses the service handler.
string state = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_STATE_DIRECTORY")
    ?? throw new InvalidOperationException("An isolated state directory is required.");
if (!Path.IsPathFullyQualified(state)) throw new InvalidOperationException("The state directory must be absolute.");
var pause = new PauseGate(Environment.GetEnvironmentVariable("KICAD_SYNC_HARNESS_PAUSE_STAGE") ?? "none",
    Environment.GetEnvironmentVariable("KICAD_SYNC_HARNESS_PAUSE_MARKER"));
if (args is ["--refinement-input-file", var inputFile])
{
    using var inputJson = JsonDocument.Parse(await File.ReadAllBytesAsync(inputFile)); var request = inputJson.RootElement;
    var input = request.GetProperty("input").Deserialize<DiagramRefinementInput>()!;
    var result = await RefinementInputFiles.RecordCoreAsync(request.GetProperty("repositoryRoot").GetString()!,
        request.GetProperty("designPath").GetString()!, request.GetProperty("documentId").GetGuid(),
        request.GetProperty("sourceToken").GetString()!, input, CancellationToken.None, state, pause.WaitAsync);
    Console.WriteLine(JsonSerializer.Serialize(new { result.Added, result.Snapshot.ContentSha256 }));
    return;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(pause);
builder.Services.AddSingleton<INativeTransport>(new PausingTransport(pause));
builder.Services.AddSingleton(provider => new InstanceRegistry(provider.GetRequiredService<INativeTransport>(), state));
builder.Services.AddSingleton<AutomaticDesignRegistry>();
builder.Services.AddMcpServer().WithStdioServerTransport()
    .WithTools<InstanceTools>().WithTools<RecoveryTools>().WithTools<SchematicViewTools>()
    .WithTools<CheckedSchematicTools>().WithTools<QualificationSynchronizationTools>()
    .WithTools<AutomaticDesignTools>();
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class QualificationSynchronizationTools(InstanceRegistry registry, PauseGate pause)
{
    [McpServerTool(Name = "kicad_design_sync_apply", ReadOnly = false),
     Description("Test-only registration of the production synchronization handler.")]
    public async Task<CallToolResult> Apply(string instanceId, string recoveryPath, string designPath,
        string expectedRevisionToken, string operationId, CancellationToken cancellationToken)
    {
        var result = await new RecoveryTools(registry).ApplySynchronization(instanceId, recoveryPath, designPath,
            expectedRevisionToken, operationId, cancellationToken, pause.WaitAsync);
        if (!(result.IsError ?? false)) await pause.WaitAsync("completed", cancellationToken);
        return result;
    }
}

public sealed class PauseGate
{
    private readonly string stage;
    private readonly string? marker;
    private int fired;
    public PauseGate(string stage, string? marker)
    {
        if (stage is not ("none" or "native-edit" or "native-save" or "completed" or "publication-staged"
            or "publication-replaced" or "baseline-committed" or "receipt-archived" or "retained-archived"
            or "layout-prepared" or "layout-resolved" or "input-prepared" or "input-replacing" or "input-replaced" or "input-published"))
            throw new ArgumentException("Unknown interruption stage.", nameof(stage));
        if (stage != "none" && (marker is null || !Path.IsPathFullyQualified(marker)))
            throw new ArgumentException("An absolute test-owned marker path is required.", nameof(marker));
        this.stage = stage; this.marker = marker;
    }
    public async Task WaitAsync(string reached, CancellationToken token)
    {
        if (reached != stage || Interlocked.CompareExchange(ref fired, 1, 0) != 0) return;
        string temporary = marker! + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { stage = reached }), token);
            await stream.FlushAsync(token); stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, marker!, overwrite: false);
        // The parent kills only this owned host. Native editors are separate
        // processes and remain alive; no cooperative cleanup establishes proof.
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}

internal sealed class PausingTransport(PauseGate pause) : INativeTransport
{
    private readonly NngTransport native = new();
    public async Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        byte[] response = await native.ExchangeAsync(endpoint, request, timeout, cancellationToken);
        var message = ApiRequest.Parser.ParseFrom(request).Message;
        if (message.Is(CheckedSchematicBatch.Descriptor)) await pause.WaitAsync("native-edit", cancellationToken);
        if (message.Is(CheckedSaveDocument.Descriptor)) await pause.WaitAsync("native-save", cancellationToken);
        return response;
    }
}
