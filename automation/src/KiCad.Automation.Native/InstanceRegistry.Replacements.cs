using System.Text.Json;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record InstanceReplacementResult(InstanceRecord Instance, bool Reused);

public sealed partial class InstanceRegistry
{
    private sealed record ReplacementIntent(int SchemaVersion, Guid OperationId, InstanceRecord Previous, InstanceRecord Replacement);

    public async Task<InstanceRecord> PreviousForVerifiedReplacementAsync(string instanceId, Guid operationId,
        string expectedEpoch, CancellationToken token = default)
    {
        var current = await ReadSavedAsync(instanceId, token);
        if (current.Epoch == expectedEpoch) return current;
        string path = Path.Combine(directory, "replacements", operationId.ToString("D") + ".json");
        if (!File.Exists(path)) throw new AutomationException("instance_changed", "The saved registration no longer has the expected original epoch.");
        var intent = await ReadReplacementAsync(path, token);
        if (intent.OperationId != operationId || intent.Previous.InstanceId != instanceId || intent.Previous.Epoch != expectedEpoch
            || !SameIdentity(current, intent.Replacement))
            throw new AutomationException("instance_changed", "The replacement receipt does not connect the expected original and current registrations.");
        return intent.Previous;
    }

    /// <summary>The service must first verify the private native handoff, old
    /// process exit and live replacement kernel identity. This method performs
    /// the registry CAS and another exact native handshake, not that authority check.</summary>
    public Task<InstanceReplacementResult> AdoptVerifiedReplacementAsync(InstanceRecord previous,
        string endpoint, string epoch, int processId, Guid operationId, CancellationToken token = default) =>
        AdoptVerifiedReplacementAsync(previous, endpoint, epoch, processId, operationId, token, null);

    internal async Task<InstanceReplacementResult> AdoptVerifiedReplacementAsync(InstanceRecord previous,
        string endpoint, string epoch, int processId, Guid operationId, CancellationToken token, Action? beforePublish)
    {
        if (!Guid.TryParseExact(previous.InstanceId, "D", out var id) || id == Guid.Empty
            || string.IsNullOrWhiteSpace(previous.Epoch) || !Path.IsPathFullyQualified(previous.ProjectPath)
            || string.IsNullOrWhiteSpace(epoch) || epoch == previous.Epoch || processId <= 0 || operationId == Guid.Empty)
            throw new AutomationException("invalid_replacement", "Use the exact previous registration and a verified replacement with a new process epoch.");
        NngTransport.ValidateEndpoint(endpoint); NngTransport.ValidateEndpoint(previous.Endpoint);
        // Do not hold the metadata gate while talking to a native process.
        var client = new NativeClient(transport, endpoint, epoch);
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != previous.InstanceId || session.ProjectPath != previous.ProjectPath || session.Epoch != epoch)
            throw new AutomationException("instance_changed", "The replacement does not match the registered instance and project.");
        var replacement = new InstanceRecord(previous.InstanceId, previous.ProjectPath, endpoint, epoch, processId, DateTimeOffset.UtcNow);

        await changes.WaitAsync(token);
        try
        {
            using var lease = await MetadataLease(token);
            var current = await ReadSavedAsync(previous.InstanceId, token);
            string receipts = Path.Combine(directory, "replacements");
            string receipt = Path.Combine(receipts, operationId.ToString("D") + ".json");
            ReplacementIntent intent;
            if (File.Exists(receipt))
            {
                intent = await ReadReplacementAsync(receipt, token);
                if (intent.SchemaVersion != 1 || intent.OperationId != operationId || intent.Previous is null || intent.Replacement is null
                    || !SameIdentity(intent.Previous, previous) || !SameIdentity(intent.Replacement, replacement))
                    throw new AutomationException("operation_reused", "The replacement operation has different identities.");
            }
            else intent = new(1, operationId, previous, replacement);

            if (SameIdentity(current, intent.Replacement))
            {
                if (!File.Exists(receipt)) throw new AutomationException("instance_changed", "This operation did not adopt the current registration.");
                if (connections.TryGetValue(current.InstanceId, out var held)
                    && !SameIdentity(held.Record, previous) && !SameIdentity(held.Record, current))
                    throw new AutomationException("instance_changed", "The attached registration contradicts the replacement receipt.");
                connections[current.InstanceId] = new(current, client, session.Clone());
                return new(current, true);
            }
            if (!SameIdentity(current, previous) || (connections.TryGetValue(previous.InstanceId, out var attached)
                && !SameIdentity(attached.Record, previous)))
                throw new AutomationException("instance_changed", "The previous registration changed; inspect before adopting the replacement.");
            if (connections.Values.Any(value => value.Record.ProjectPath == previous.ProjectPath && value.Record.InstanceId != previous.InstanceId))
                throw new AutomationException("project_owned", "Another attached instance owns the project's registration.");
            if (!File.Exists(receipt))
            {
                Directory.CreateDirectory(receipts);
                string pending = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await JsonSerializer.SerializeAsync(stream, intent, cancellationToken: token);
                        await stream.FlushAsync(token);
                    }
                    token.ThrowIfCancellationRequested(); File.Move(pending, receipt);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
            var connection = new Connection(intent.Replacement, client, session.Clone());
            beforePublish?.Invoke();
            await WriteRecordAsync(intent.Replacement, token);
            connections[previous.InstanceId] = connection;
            RetireLaunch(previous.InstanceId);
            return new(intent.Replacement, false);
        }
        finally { changes.Release(); }
    }

    private static bool SameIdentity(InstanceRecord first, InstanceRecord second) =>
        first.InstanceId == second.InstanceId && first.ProjectPath == second.ProjectPath && first.Endpoint == second.Endpoint
        && first.Epoch == second.Epoch && first.ProcessId == second.ProcessId;

    private static async Task<ReplacementIntent> ReadReplacementAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length is < 1 or > 1048576) throw new AutomationException("invalid_registry", "The replacement receipt has an invalid size.");
            var intent = await JsonSerializer.DeserializeAsync<ReplacementIntent>(stream, cancellationToken: token);
            if (intent is null || intent.SchemaVersion != 1 || intent.OperationId == Guid.Empty || intent.Previous is null || intent.Replacement is null)
                throw new AutomationException("invalid_registry", "The replacement receipt is invalid.");
            return intent;
        }
        catch (JsonException) { throw new AutomationException("invalid_registry", "The replacement receipt is invalid."); }
    }

    private async Task<FileStream> MetadataLease(CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "metadata.lock");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        int delay = 25;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new AutomationException("invalid_registry", "The registry metadata lease cannot be redirected.");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 35 or 32 or 33)
            { await Task.Delay(delay, deadline.Token); delay = Math.Min(delay * 2, 500); }
        }
    }

    private async Task WriteRecordAsync(InstanceRecord record, CancellationToken token)
    {
        string destination = Path.Combine(directory, record.InstanceId + ".json");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
