using System.ComponentModel;
using System.Collections.Immutable;
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
    Environment.GetEnvironmentVariable("KICAD_SYNC_HARNESS_PAUSE_MARKER"),
    Environment.GetEnvironmentVariable("KICAD_SYNC_HARNESS_PAUSE_RELEASE"));
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
if (args is ["--block-proposal-file", var proposalFile])
{
    using var proposalJson = JsonDocument.Parse(await File.ReadAllBytesAsync(proposalFile)); var request = proposalJson.RootElement;
    var proposal = BlockProposalFiles.Normalize(request.GetProperty("proposal").Deserialize<BlockProposal>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
    var result = await BlockProposalFiles.PublishAsync(request.GetProperty("repositoryRoot").GetString()!,
        request.GetProperty("designPath").GetString()!, request.GetProperty("documentId").GetGuid(),
        request.GetProperty("sourceToken").GetString()!, proposal, state, CancellationToken.None,
        request.GetProperty("operationId").GetGuid(), pause.WaitAsync);
    Console.WriteLine(JsonSerializer.Serialize(new { result.Added, result.Snapshot.ContentSha256 }));
    return;
}
if (args is ["--block-proposal-select-file", var selectionFile])
{
    using var selectionJson = JsonDocument.Parse(await File.ReadAllBytesAsync(selectionFile)); var request = selectionJson.RootElement;
    var result = await BlockProposalFiles.SelectAsync(request.GetProperty("repositoryRoot").GetString()!,
        request.GetProperty("designPath").GetString()!, request.GetProperty("documentId").GetGuid(), request.GetProperty("proposalId").GetGuid(),
        request.GetProperty("sourceToken").GetString()!, request.GetProperty("expectedRoot").Deserialize<BlockSelection>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
        request.GetProperty("currentPath").Deserialize<ImmutableArray<BlockSelection>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        request.GetProperty("ancestorIds").Deserialize<ImmutableArray<Guid>>(),
        request.GetProperty("origin").Deserialize<RequirementRevisionOrigin>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
        CancellationToken.None, state, request.GetProperty("operationId").GetGuid(), pause.WaitAsync);
    Console.WriteLine(JsonSerializer.Serialize(new { result.ContentSha256 }));
    return;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(pause);
builder.Services.AddSingleton<IExecutionCheckpoint>(pause);
builder.Services.AddSingleton<INativeTransport>(new PausingTransport(pause));
builder.Services.AddSingleton(provider => new InstanceRegistry(provider.GetRequiredService<INativeTransport>(), state));
// As in the production server: the synchronization preview classifies with the handshake each instance gave when it
// was attached, without contacting KiCad, so it classifies a saved revision exactly as apply does (decision
// n39ac0ccc5c9270f2).
builder.Services.AddSingleton<IAttachedHandshakes>(provider =>
    new AttachedHandshakes(provider.GetRequiredService<InstanceRegistry>().AttachedHandshake));
builder.Services.AddSingleton<AutomaticDesignRegistry>();
builder.Services.AddMcpServer().WithStdioServerTransport()
    .WithTools<InstanceTools>().WithTools<RecoveryTools>().WithTools<SchematicViewTools>()
    .WithTools<RecoveryObservationTools>()
    .WithTools<CheckedSchematicTools>()
    .WithTools<AutomaticDesignTools>();
await builder.Build().RunAsync();

public sealed class PauseGate : IExecutionCheckpoint
{
    private readonly string stage;
    private readonly string? marker;
    private readonly string? release;
    private int fired;
    /// <param name="release">Absent: the paused call waits until the parent kills this host. Present: the parent
    /// observes the paused state, then creates this file and the same call continues.</param>
    public PauseGate(string stage, string? marker, string? release = null)
    {
        if (stage is not ("none" or "native-edit" or "native-save" or "completed" or "publication-staged"
            or "publication-replaced" or "baseline-committed" or "receipt-archived" or "retained-archived"
            or "layout-prepared" or "layout-resolved" or "input-prepared" or "input-replacing" or "input-replaced" or "input-published"
            or "proposal-prepared" or "proposal-replacing" or "proposal-replaced" or "proposal-published"
            or "proposal-selection-prepared" or "proposal-selection-replacing" or "proposal-selection-replaced" or "proposal-selection-published"))
            throw new ArgumentException("Unknown interruption stage.", nameof(stage));
        if (stage != "none" && (marker is null || !Path.IsPathFullyQualified(marker)))
            throw new ArgumentException("An absolute test-owned marker path is required.", nameof(marker));
        if (release is not null && (stage == "none" || !Path.IsPathFullyQualified(release)))
            throw new ArgumentException("A release file needs a pause stage and an absolute test-owned path.", nameof(release));
        this.stage = stage; this.marker = marker; this.release = release;
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
        if (release is null)
        {
            // The parent kills only this owned host. Native editors are separate
            // processes and remain alive; no cooperative cleanup establishes proof.
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return;
        }
        // An observing parent reads the state at this exact point, then lets the same call finish.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(release)!, Path.GetFileName(release));
        watcher.Created += (_, _) => released.TrySetResult();
        watcher.Renamed += (_, _) => released.TrySetResult();
        watcher.Error += (_, error) => released.TrySetException(error.GetException());
        watcher.EnableRaisingEvents = true;
        if (File.Exists(release)) released.TrySetResult();
        await released.Task.WaitAsync(token);
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
