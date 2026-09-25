using System.Threading.Channels;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

internal sealed class AutomaticDesignDriver : IAutomaticDesignDriver
{
    private readonly NativeClient client;
    private readonly string designPath;
    private readonly Guid instanceId;
    private readonly NativeEventSubscription native;
    private readonly DesignFileSubscription file;
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<AutomaticDesignInput> inputs = Channel.CreateBounded<AutomaticDesignInput>(
        new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly IReadOnlyList<FileStream> leases;
    private readonly BlockOwnershipTarget? blocks;
    private readonly Task nativeReader;
    private readonly Task fileReader;
    private int disposed;
    public DesignRecoveryStore Store { get; }
    public AutomationSession? Session { get; }

    private AutomaticDesignDriver(DesignRecoveryStore store, NativeClient client, string designPath, Guid instanceId,
        AutomationSession session, NativeEventSubscription native, DesignFileSubscription file, IReadOnlyList<FileStream> leases,
        BlockOwnershipTarget? blocks)
    {
        Store = store; this.client = client; this.designPath = designPath; this.instanceId = instanceId; Session = session;
        this.native = native; this.file = file; this.leases = leases; this.blocks = blocks;
        nativeReader = Task.Run(ReadNativeAsync); fileReader = Task.Run(ReadFileAsync);
    }

    internal static Task<AutomaticDesignDriver> CreateAsync(DesignRecoveryStore store, NativeClient client,
        string designPath, string expectedRecoveryRevision, CancellationToken token)
        => CreateAsync(store, client, designPath, expectedRecoveryRevision, null, token);

    internal static async Task<AutomaticDesignDriver> CreateAsync(DesignRecoveryStore store, NativeClient client,
        string designPath, string expectedRecoveryRevision, BlockOwnershipTarget? blocks, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(designPath)) throw Error("invalid_automatic_sync_path", "Provide an absolute design path.");
        designPath = Path.GetFullPath(designPath);
        if (blocks is not null)
        {
            if (!Path.IsPathFullyQualified(blocks.BlockGraphPath) || blocks.DesignId == Guid.Empty)
                throw Error("invalid_automatic_sync_path", "Provide the absolute block graph path and the design's exact identity together.");
            blocks = blocks with { BlockGraphPath = Path.GetFullPath(blocks.BlockGraphPath) };
            var separate = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (separate.Equals(blocks.BlockGraphPath, designPath) || separate.Equals(blocks.BlockGraphPath, store.StatePath))
                throw Error("invalid_automatic_sync_path", "The block graph, design and recovery files must remain separate.");
        }
        var saved = store.Read() ?? throw Error("missing_design_recovery", "Initialize the exact design recovery record first.");
        if (saved.RevisionToken != expectedRecoveryRevision) throw Error("design_recovery_changed", "Reload the recovery record before starting automatic synchronization.");
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != saved.State.InstanceId.ToString("D")) throw Error("recovery_instance_mismatch", "The native connection belongs to another instance.");
        var source = new NativeEventSubscription(session);
        DesignFileSubscription? file = null; var leases = new List<FileStream>();
        try
        {
            file = new(designPath);
            var observed = await client.InvokeAsync<ReadCheckedSchematicState, CheckedSchematicState>(new()
                { Document = saved.State.Baseline.Schematic.Document.Clone(), ProcessEpoch = client.Epoch }, token);
            if (observed.State?.Document is not { } document || !Equals(document, saved.State.Baseline.Schematic.Document)
                || observed.State.ProcessEpoch != client.Epoch || !Path.IsPathFullyQualified(document.Project.Path))
                throw Error("invalid_automatic_sync_target", "Native discovery did not confirm the exact project and document.");
            string history = Path.Combine(document.Project.Path, ".kicad-sync-history");
            Directory.CreateDirectory(history);
            if ((File.GetAttributes(history) & FileAttributes.ReparsePoint) != 0)
                throw Error("invalid_history_directory", "Automatic ownership requires an ordinary project history directory.");
            string ignore = Path.Combine(history, ".gitignore");
            try { using var output = new FileStream(ignore, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write("*\n"u8); output.Flush(true); }
            catch (IOException) when (File.Exists(ignore)) { }
            string root = document.SheetPath.Path[0].Value;
            if (!Guid.TryParseExact(root, "D", out var rootId) || rootId == Guid.Empty)
                throw Error("invalid_automatic_sync_target", "A persistent native root identity is required.");
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (comparison.Equals(store.StatePath, designPath)) throw Error("invalid_automatic_sync_path", "Design and recovery files must remain separate.");
            foreach (string path in new[] { store.StatePath + ".automatic.lock", designPath + ".automatic.lock",
                         Path.Combine(history, "native-" + rootId.ToString("N") + ".automatic.lock") }.Distinct(comparison).Order(comparison))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw Error("invalid_automatic_sync_lock", "Automatic ownership lock paths cannot be redirected.");
                try { leases.Add(new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                catch (IOException) { throw Error("automatic_sync_ownership_conflict", "Another automatic worker owns this design, document or recovery record."); }
            }
            // The block graph must read and bind this design's circuit before the worker owns anything.
            if (blocks is not null) _ = await BlockOwnershipSynchronization.PlanAsync(blocks.BlockGraphPath, blocks.DesignId, saved.State.Baseline, token);
            if (store.Read()?.RevisionToken != saved.RevisionToken) throw Error("design_recovery_changed", "Recovery changed while acquiring automatic ownership.");
            return new(store, client, designPath, saved.State.InstanceId, session.Clone(), source, file, leases, blocks);
        }
        catch { foreach (var lease in leases) lease.Dispose(); file?.Dispose(); source.Dispose(); throw; }
    }

    public Task<AutomaticDesignInput> ReceiveAsync(CancellationToken token) => inputs.Reader.ReadAsync(token).AsTask();

    public async Task<StoredDesignRecovery> RefreshAsync(AutomaticDesignInput input, CancellationToken token)
    {
        var before = Read();
        if (before.State.HasPendingWork) return before;
        byte[] bytes = await File.ReadAllBytesAsync(designPath, token);
        token.ThrowIfCancellationRequested();
        var saved = bytes.AsSpan().SequenceEqual(before.State.DesiredFileBytes) ? before
            : Store.Save(before.State with { DesiredFileBytes = bytes, HierarchyResolution = null }, before.RevisionToken);
        return await DesignRecoveryInspector.RefreshAsync(Store, client, saved.RevisionToken, token,
            input.MinimumRevision, includeElectrical: true);
    }

    public Task<SchematicSynchronizationExecution> ApplyAsync(StoredDesignRecovery saved, Guid operationId,
        string requestRevisionToken, CancellationToken token) =>
        SchematicSynchronizationExecutor.ApplyAsync(Store, client, designPath, requestRevisionToken, operationId, token);

    public async Task<BlockOwnershipResult?> SynchronizeBlocksAsync(StoredDesignRecovery saved, CancellationToken token) =>
        blocks is null ? null : await BlockOwnershipSynchronization.SynchronizeAsync(blocks.BlockGraphPath, blocks.DesignId,
            saved.State.Baseline, BlockOwnershipSynchronization.NativeOrigin("Bind components placed in KiCad to the block of their sheet"),
            token: token);

    private async Task ReadNativeAsync()
    {
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var delivery = await native.ReceiveAsync(deadline.Token);
                if (delivery.Disposition == NativeEventDisposition.Duplicate) continue;
                if (delivery.Disposition == NativeEventDisposition.Heartbeat)
                {
                    // The serial owner ignores these unless it is waiting for a busy
                    // editor. Ordinary heartbeats never request a design snapshot.
                    await inputs.Writer.WriteAsync(new(AutomaticDesignSignal.Heartbeat), stopping.Token);
                    continue;
                }
                var saved = Read(); var commit = delivery.Event.SchematicCommit;
                if (commit is not null)
                {
                    var root = saved.State.Baseline.Schematic.Document;
                    if (commit.Document?.Project is null || !Equals(commit.Document.Project, root.Project)
                        || commit.Document.SheetPath is null || commit.Document.SheetPath.Path.Count == 0
                        || !commit.Document.SheetPath.Path[0].Equals(root.SheetPath.Path[0])) continue;
                    if (commit.Revision.Epoch != saved.State.NativeRevision.Epoch)
                        throw Error("native_document_changed", "The native document epoch changed; reattach before synchronizing.");
                    if (delivery.Disposition == NativeEventDisposition.Change && !saved.State.HasPendingWork
                        && commit.Revision.Sequence <= saved.State.NativeRevision.Sequence) continue;
                }
                await inputs.Writer.WriteAsync(new(delivery.Disposition == NativeEventDisposition.Change
                    ? AutomaticDesignSignal.Native : AutomaticDesignSignal.Recovery,
                    commit?.Revision is { } revision ? new(revision.Epoch, revision.Sequence) : null), stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        { await Failure(error is AutomationException known ? known.Code : error is OperationCanceledException ? "native_event_silence" : "native_event_failed", error.Message); }
    }

    private async Task ReadFileAsync()
    {
        try
        {
            while (true)
            {
                var change = await file.ReceiveAsync(stopping.Token);
                if (change.Reasons.HasFlag(DesignFileChangeReason.ObservationStopped))
                { await Failure(change.ErrorCode ?? "sync_watch_stopped", "Reattach the file watcher before applying more changes."); return; }
                await inputs.Writer.WriteAsync(new(AutomaticDesignSignal.File
                    | (change.Reasons.HasFlag(DesignFileChangeReason.RecoveryRequired) ? AutomaticDesignSignal.Recovery : 0)), stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error) { await Failure("automatic_file_observation_failed", error.Message); }
    }

    private async Task Failure(string code, string message)
    {
        try { await inputs.Writer.WriteAsync(new(AutomaticDesignSignal.Recovery, ReattachRequired: true,
            ErrorCode: code, ErrorMessage: message), stopping.Token); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private StoredDesignRecovery Read()
    {
        var saved = Store.Read() ?? throw Error("missing_design_recovery", "The automatic recovery record disappeared.");
        if (saved.State.InstanceId != instanceId) throw Error("recovery_instance_mismatch", "The recovery record now belongs to another instance.");
        return saved;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel(); native.Dispose(); file.Dispose();
        await Task.WhenAll(nativeReader, fileReader); inputs.Writer.TryComplete();
        foreach (var lease in leases) lease.Dispose(); stopping.Dispose();
    }

    private static AutomationException Error(string code, string message) => new(code, message);
}
