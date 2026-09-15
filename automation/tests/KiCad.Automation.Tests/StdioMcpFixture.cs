using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KiCad.Automation.Tests;

internal interface IMcpToolClient : IAsyncDisposable
{
    Task<JsonElement> Tool(string name, object arguments);
    Task<JsonElement> ListTools(string? cursor);
}

/// <summary>Compiled black-box test client; owns only its MCP child, never KiCad.</summary>
internal sealed class StdioMcpFixture : IMcpToolClient
{
    private readonly Process process;
    private readonly CancellationToken token;
    private readonly CancellationTokenSource diagnosticsStop = new();
    private readonly Task diagnostics;
    private int nextId;
    private bool disposed;
    public bool ForcedTermination { get; private set; }
    private StdioMcpFixture(Process process, string evidence, CancellationToken token)
    {
        this.process = process; this.token = token;
        diagnostics = Capture();
        async Task Capture()
        {
            await using var output = File.Create(evidence);
            try { await process.StandardError.BaseStream.CopyToAsync(output, diagnosticsStop.Token); }
            catch (OperationCanceledException) when (diagnosticsStop.IsCancellationRequested) { }
        }
    }
    internal static Task<StdioMcpFixture> StartAsync(string state, string evidence, CancellationToken token) =>
        StartAsync(UpdateCommandTests.StartInfo(), state, evidence, token);

    internal static async Task<StdioMcpFixture> StartAsync(ProcessStartInfo start, string state, string evidence, CancellationToken token)
    {
        start.UseShellExecute = false; start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.StandardInputEncoding = new UTF8Encoding(false); start.StandardOutputEncoding = new UTF8Encoding(false);
        start.Environment["KICAD_AUTOMATION_STATE_DIRECTORY"] = state;
        var fixture = new StdioMcpFixture(Process.Start(start)!, evidence, token);
        try
        {
            await fixture.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "kicad-reconnection-fixture", version = "1" } });
            await fixture.process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}".AsMemory(), token);
            await fixture.process.StandardInput.FlushAsync(token);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> Tool(string name, object arguments) =>
        (await Request("tools/call", new { name, arguments })).GetProperty("result").Clone();

    public async Task<JsonElement> ListTools(string? cursor) =>
        (await Request("tools/list", cursor is null ? new { } : (object)new { cursor })).GetProperty("result").Clone();

    internal async Task TerminateAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!process.HasExited) { ForcedTermination = true; process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync();
    }

    private async Task<JsonElement> Request(string method, object parameters)
    {
        int id = ++nextId;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }).AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new IOException("The MCP fixture exited before replying.");
            using var parsed = JsonDocument.Parse(line); var message = parsed.RootElement;
            if (!message.TryGetProperty("id", out var received) || received.ValueKind != JsonValueKind.Number || received.GetInt32() != id) continue;
            if (message.TryGetProperty("error", out var error)) throw new IOException(error.GetRawText());
            return message.Clone();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { ForcedTermination = true; process.Kill(); }
            await process.WaitForExitAsync();
        }
        finally
        {
            diagnosticsStop.Cancel();
            try { await diagnostics.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { process.Dispose(); diagnosticsStop.Dispose(); }
        }
    }
}
