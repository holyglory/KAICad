using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Distribution;
using KiCad.Automation.Native;

namespace KiCad.Automation.Mcp;

public sealed record WindowsUpdateHandoffRequest(string InstallationRoot, string ExpectedSelectionId, string ManifestSha256,
    Guid OperationId, WindowsProcessIdentity OldProcess, string ProjectPath, Guid InstanceId, string SocketPath, bool SoftwareRendering,
    UpdateOrigin? Origin = null);
public sealed record WindowsUpdateHandoffState(string Status, string JournalDirectory, string? SelectionId = null,
    int? ProcessId = null, string? NativeEpoch = null, string? Error = null, string? Endpoint = null,
    WindowsProcessIdentity? ProcessIdentity = null);
public sealed record WindowsUpdateHandoffIntent(int SchemaVersion, WindowsUpdateHandoffRequest Request,
    VerifiedWindowsVersion PreviousVersion, DateTimeOffset PreparedAtUtc, IReadOnlyDictionary<string, string?> LaunchEnvironment);

/// <summary>Explicit Update handoff. Never closes or signals the old editor.
/// Persisted launch uncertainty requires reconciliation, not another launch.</summary>
public static class WindowsUpdateHandoff
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    public static Task<WindowsUpdateHandoffState> ExecuteAsync(WindowsUpdateHandoffRequest request,
        Func<WindowsUpdateHandoffState, Task> report, CancellationToken token = default,
        IReadOnlyDictionary<string, string?>? launchEnvironment = null) => ExecuteAsync(request, report, launchEnvironment, null, token);

    internal static async Task<WindowsUpdateHandoffState> ExecuteAsync(WindowsUpdateHandoffRequest request,
        Func<WindowsUpdateHandoffState, Task> report, IReadOnlyDictionary<string, string?>? launchEnvironment,
        Action<string, ProcessStartInfo>? beforeLaunch, CancellationToken token,
        Func<string, Process, Task>? afterLaunch = null)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InstallationRoot));
        string journal = Path.Combine(root, "handovers", request.OperationId.ToString("D"));
        string requestPath = Path.Combine(journal, "request.json");
        if (Directory.Exists(journal))
        {
            if (!File.Exists(requestPath)) throw new InvalidDataException("An incomplete Windows handoff needs inspection.");
            if (await ReadRequest(requestPath, token) != request) throw new InvalidDataException("The Windows restart operation belongs to a different request.");
            return new("reconciliation_required", journal, Error: "Inspect the existing handoff before retrying it.");
        }
        // Only validate existing registered data. A failed preflight cannot select a version or close a process.
        _ = await WindowsVerifiedVersions.InspectActivationAsync(root, request.ExpectedSelectionId, request.ManifestSha256, token);
        using var old = WindowsObservedProcess.Open(request.OldProcess);
        var previous = await WindowsVerifiedVersions.InspectExecutableAsync(root, request.OldProcess.Executable, token);
        await UpdateOrigin.VerifyAsync(request.Origin, request.InstanceId, request.ProjectPath, token);
        if (!old.IsAlive) throw new InvalidDataException("The original editor exited before the handoff was prepared.");
        var environment = UpdateLaunchEnvironment.Capture(launchEnvironment);
        Directory.CreateDirectory(journal);
        using var ownership = new FileStream(Path.Combine(journal, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(requestPath))
        {
            if (await ReadRequest(requestPath, token) != request) throw new InvalidDataException("The Windows restart operation belongs to a different request.");
            return new("reconciliation_required", journal, Error: "Inspect the existing handoff before retrying it.");
        }
        await SaveAsync(requestPath, request, token, overwrite: false);
        await SaveAsync(Path.Combine(journal, "intent.json"), new WindowsUpdateHandoffIntent(2, request, previous, DateTimeOffset.UtcNow, environment), token, overwrite: false);
        var state = new WindowsUpdateHandoffState("waiting_for_exit", journal, request.ExpectedSelectionId);
        await Record(state, token); await report(state);
        try { await old.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (old.IsAlive)
            {
                state = state with { Status = "cancelled_before_activation" };
                await Record(state, CancellationToken.None); return state;
            }
        }
        SelectedWindowsVersion selected;
        try
        {
            await Record(new("activating", journal, request.ExpectedSelectionId), token);
            selected = await WindowsVerifiedVersions.ActivateAsync(root, request.ExpectedSelectionId, request.ManifestSha256, request.OperationId, token);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException or OperationCanceledException)
        {
            state = new("activation_failed", journal, Error: failure.Message);
            await SaveAsync(Path.Combine(journal, "activation-failure.json"), state, CancellationToken.None);
            await Record(state, CancellationToken.None);
            return await RestorePrevious();
        }
        await Record(new("launching", journal, selected.SelectionId), CancellationToken.None);
        var replacement = await Launch(selected.Version, selected.SelectionId, "candidate", request.SocketPath);
        if (replacement.Status != "launch_failed") return replacement;
        await SaveAsync(Path.Combine(journal, "candidate-failure.json"), replacement, CancellationToken.None);
        try
        {
            await Record(new("rolling_back", journal, selected.SelectionId, Error: replacement.Error), CancellationToken.None);
            await WindowsVerifiedVersions.RollbackAsync(root, selected.SelectionId, request.OperationId, Guid.NewGuid(), CancellationToken.None);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException)
        {
            // Another selection may have superseded this one. Preserve it, but
            // still recover the exact editor we closed if its retained bytes verify.
            await SaveAsync(Path.Combine(journal, "rollback-failure.json"), new { error = failure.Message }, CancellationToken.None);
        }
        return await RestorePrevious();

        async Task<WindowsUpdateHandoffState> RestorePrevious()
        {
            try
            {
                var retained = await WindowsVerifiedVersions.InspectExecutableAsync(root, previous.NativeExecutable, CancellationToken.None);
                if (retained != previous) throw new InvalidDataException("The previous Windows editor changed before recovery.");
                string current = WindowsVerifiedVersions.InspectSelectionId(root);
                await Record(new("restoring", journal, current), CancellationToken.None);
                return await Launch(retained, current, "restored", request.SocketPath + ".r");
            }
            catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException)
            {
                var uncertain = new WindowsUpdateHandoffState("reconciliation_required", journal, Error: failure.Message);
                await Record(uncertain, CancellationToken.None); return uncertain;
            }
        }
        Task<WindowsUpdateHandoffState> Launch(VerifiedWindowsVersion version, string selection, string label, string socket) =>
            WindowsUpdateLauncher.LaunchAsync(new(request, version, journal, selection, label, socket), Record, token, environment, beforeLaunch, afterLaunch);
        Task Record(WindowsUpdateHandoffState value, CancellationToken cancellation) => SaveAsync(Path.Combine(journal, "state.json"), value, cancellation);
    }

    internal static void Validate(WindowsUpdateHandoffRequest request)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows update handoff requires Windows.");
        request.Origin?.Validate();
        if (!Path.IsPathFullyQualified(request.InstallationRoot) || !Directory.Exists(request.InstallationRoot)
            || request.ProjectPath is null || (request.ProjectPath.Length != 0 && (!Path.IsPathFullyQualified(request.ProjectPath)
                || Path.GetExtension(request.ProjectPath) != ".kicad_pro" || !File.Exists(request.ProjectPath)))
            || !Guid.TryParseExact(request.ExpectedSelectionId, "D", out var selection) || selection == Guid.Empty
            || request.ManifestSha256 is not { Length: 64 } || !request.ManifestSha256.All(char.IsAsciiHexDigitLower)
            || !Path.IsPathFullyQualified(request.SocketPath) || !Directory.Exists(Path.GetDirectoryName(request.SocketPath))
            || Encoding.UTF8.GetByteCount(request.SocketPath) > 120 || request.OperationId == Guid.Empty || request.InstanceId == Guid.Empty
            || request.OldProcess is null)
            throw new ArgumentException("Use a registered Windows installation, exact live process, explicit project or empty manager, and a new short local pipe name.");
        request.OldProcess.Validate(); NngTransport.ValidateEndpoint(NativeIpcEndpoint.FromSocketPath(request.SocketPath));
    }

    private static async Task<WindowsUpdateHandoffRequest?> ReadRequest(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 65536) throw new InvalidDataException("Invalid Windows handoff record size.");
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        return ParseRequest(bytes);
    }

    // Persisted requests are part of the recovery identity. Reject unknown or
    // repeated fields instead of allowing a stale local journal to masquerade
    // as the current request during a retry.
    internal static WindowsUpdateHandoffRequest ParseRequest(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 65536) throw new InvalidDataException("Invalid Windows handoff record size.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        CheckProperties(document.RootElement);
        return JsonSerializer.Deserialize<WindowsUpdateHandoffRequest>(bytes, Json)
            ?? throw new InvalidDataException("Missing Windows handoff request.");
    }

    private static void CheckProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Repeated Windows handoff property.");
                CheckProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray()) CheckProperties(element);
        }
    }
    internal static async Task SaveAsync<T>(string path, T value, CancellationToken token, bool overwrite = true)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 65536) throw new InvalidDataException("Windows handoff record exceeds its size limit.");
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); File.Move(pending, path, overwrite);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
