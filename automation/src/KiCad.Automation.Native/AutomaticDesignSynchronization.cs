using System.Threading.Channels;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum AutomaticDesignPhase { Starting, Watching, Applying, InvalidDesign, Paused, Stopped, WaitingForEditor }
[Flags]
internal enum AutomaticDesignSignal { Initial = 1, File = 2, Native = 4, Recovery = 8, Resume = 16, Heartbeat = 32 }
internal sealed record AutomaticDesignInput(AutomaticDesignSignal Reasons, DocumentRevision? MinimumRevision = null,
    bool ReattachRequired = false, string? ErrorCode = null, string? ErrorMessage = null);
public sealed record AutomaticDesignStatus(ulong Sequence, AutomaticDesignPhase Phase, string? RecoveryRevisionToken,
    Guid? OperationId, bool ReattachRequired, string? ErrorCode, string? ErrorMessage);

internal interface IAutomaticDesignDriver : IAsyncDisposable
{
    DesignRecoveryStore Store { get; }
    /// <summary>The recorded instance's handshake, taken when the driver attached. Planning uses it
    /// to classify exactly as apply does (decision n39ac0ccc5c9270f2); null behaves as today.</summary>
    KiCad.Automation.Protocol.AutomationSession? Session { get; }
    Task<AutomaticDesignInput> ReceiveAsync(CancellationToken token);
    Task<StoredDesignRecovery> RefreshAsync(AutomaticDesignInput input, CancellationToken token);
    Task<SchematicSynchronizationExecution> ApplyAsync(StoredDesignRecovery saved, Guid operationId,
        string requestRevisionToken, CancellationToken token);
}

/// <summary>One serial event-to-application owner. Internal until live service
/// lifecycle, ownership and conflict behavior are qualified.</summary>
public sealed class AutomaticDesignSynchronization : IAsyncDisposable
{
    private readonly IAutomaticDesignDriver driver;
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<byte> ready = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Task intake;
    private readonly Task worker;
    private TaskCompletionSource changed = NewSignal();
    private AutomaticDesignInput? pending;
    private AutomaticDesignStatus status = new(0, AutomaticDesignPhase.Starting, null, null, false, null, null);
    private string? settledRevision;
    private bool resumeRequested;
    private int disposed;

    internal AutomaticDesignSynchronization(IAutomaticDesignDriver driver)
    {
        this.driver = driver;
        Queue(new(AutomaticDesignSignal.Initial));
        intake = Task.Run(IntakeAsync);
        worker = Task.Run(RunAsync);
    }

    public static async Task<AutomaticDesignSynchronization> StartAsync(DesignRecoveryStore store, NativeClient client,
        string designPath, string expectedRecoveryRevision, CancellationToken token = default)
        => new(await AutomaticDesignDriver.CreateAsync(store, client, designPath, expectedRecoveryRevision, token));

    public AutomaticDesignStatus Inspect() { lock (gate) return status; }

    public async Task<AutomaticDesignStatus> WaitAsync(ulong afterSequence, CancellationToken token = default)
    {
        while (true)
        {
            Task next;
            lock (gate)
            {
                if (afterSequence > status.Sequence) throw Error("invalid_automatic_sync_cursor", "The cursor is ahead of this synchronization worker.");
                if (status.Sequence > afterSequence || status.Phase == AutomaticDesignPhase.Stopped) return status;
                next = changed.Task;
            }
            await next.WaitAsync(token);
        }
    }

    public void Resume(ulong expectedSequence)
    {
        lock (gate)
        {
            if (status.Sequence != expectedSequence || status.Phase is not (AutomaticDesignPhase.Paused or AutomaticDesignPhase.InvalidDesign))
                throw Error("automatic_sync_changed", "Resume only the current paused synchronization state.");
            if (status.ReattachRequired)
                throw Error("automatic_sync_reattach_required", "Reattach verified observation channels before resuming.");
            resumeRequested = true;
        }
        Queue(new(AutomaticDesignSignal.Resume | AutomaticDesignSignal.Recovery));
    }

    private void Queue(AutomaticDesignInput input, bool wake = true)
    {
        lock (gate)
        {
            if (stopping.IsCancellationRequested) return;
            if (input.Reasons == AutomaticDesignSignal.Heartbeat && status.Phase != AutomaticDesignPhase.WaitingForEditor)
                return;
            if (pending is null) pending = input;
            else
            {
                var minimum = input.MinimumRevision ?? pending.MinimumRevision;
                bool epochChanged = input.MinimumRevision is { } next && pending.MinimumRevision is { } old && next.Epoch != old.Epoch;
                if (!epochChanged && pending.MinimumRevision is { } previous && minimum is { } latest && previous.Sequence > latest.Sequence)
                    minimum = previous;
                pending = new(pending.Reasons | input.Reasons, minimum,
                    pending.ReattachRequired || input.ReattachRequired || epochChanged,
                    pending.ErrorCode ?? input.ErrorCode ?? (epochChanged ? "native_document_changed" : null),
                    pending.ErrorMessage ?? input.ErrorMessage ?? (epochChanged ? "Native document identity changed; reattach the saved design." : null));
            }
            if (wake) ready.Writer.TryWrite(0);
        }
    }

    private async Task IntakeAsync()
    {
        try
        {
            while (true) Queue(await driver.ReceiveAsync(stopping.Token));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        { Queue(new(AutomaticDesignSignal.Recovery, ReattachRequired: true, ErrorCode: "automatic_sync_observation_failed", ErrorMessage: error.Message)); }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await ready.Reader.WaitToReadAsync(stopping.Token))
            {
                AutomaticDesignInput input;
                lock (gate)
                {
                    if (!ready.Reader.TryRead(out _) || pending is null) continue;
                    input = pending; pending = null;
                    if (!resumeRequested && (status.Phase == AutomaticDesignPhase.Paused
                        || (status.Phase == AutomaticDesignPhase.InvalidDesign && !input.Reasons.HasFlag(AutomaticDesignSignal.File))))
                    {
                        // Preserve notifications while paused, without a retry loop.
                        pending = input;
                        if (input.ErrorCode is not null)
                            Publish(AutomaticDesignPhase.Paused, status.RecoveryRevisionToken, status.OperationId,
                                status.ReattachRequired || input.ReattachRequired, input.ErrorCode, input.ErrorMessage);
                        continue;
                    }
                    resumeRequested = false;
                }
                if (input.ErrorCode is not null)
                { Pause(input.ErrorCode, input.ErrorMessage, input.ReattachRequired); continue; }
                Guid? operation = null;
                try
                {
                    var saved = driver.Store.Read() ?? throw Error("missing_design_recovery", "The synchronization recovery record is unavailable.");
                    if (saved.State.HasPendingWork)
                    {
                        operation = saved.State.PendingPublication?.OperationId ?? saved.State.PendingLayout?.OperationId;
                        string? request = saved.State.PendingPublication?.RequestedRecoveryRevisionToken
                            ?? saved.State.PendingLayout?.RequestedRecoveryRevisionToken;
                        if (operation is null || request is null)
                            throw Error("pending_recovery_requires_reconciliation", "Inspect the retained operation before automatic application can resume.");
                        Publish(AutomaticDesignPhase.Applying, saved.RevisionToken, operation, false, null, null);
                        await driver.ApplyAsync(saved, operation.Value, request, stopping.Token);
                        // A later file/native input may have arrived while recovering.
                        Queue(new(AutomaticDesignSignal.Recovery));
                    }
                    else
                    {
                        saved = await driver.RefreshAsync(input, stopping.Token);
                        try { _ = DesignRecoveryStore.ReadDesired(saved.State); }
                        catch (AutomationException error)
                        {
                            Publish(AutomaticDesignPhase.InvalidDesign, saved.RevisionToken, null, false, error.Code, error.Message);
                            continue;
                        }
                        if (saved.RevisionToken == settledRevision)
                        { Publish(AutomaticDesignPhase.Watching, saved.RevisionToken, Inspect().OperationId, false, null, null); continue; }
                        var plan = await SchematicSynchronizationPlanner.PlanForExecutionWithHistoryAsync(driver.Store, saved,
                            driver.Session, stopping.Token);
                        if (!plan.CanPrepare) throw Error(plan.ErrorCode ?? "design_sync_conflict", plan.ErrorMessage ?? "Resolve the saved design conflicts before resuming.");
                        operation = Guid.NewGuid();
                        Publish(AutomaticDesignPhase.Applying, saved.RevisionToken, operation, false, null, null);
                        await driver.ApplyAsync(saved, operation.Value, saved.RevisionToken, stopping.Token);
                    }
                    var complete = driver.Store.Read() ?? throw Error("missing_design_recovery", "The completed recovery record is unavailable.");
                    if (complete.State.HasPendingWork) throw Error("automatic_sync_incomplete", "The operation remains pending; inspect recovery before resuming.");
                    settledRevision = complete.RevisionToken;
                    Publish(AutomaticDesignPhase.Watching, complete.RevisionToken, operation, false, null, null);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
                catch (NativeApiException error) when (error.Status == 7)
                {
                    // AS_BUSY rejects observation before serializing staged geometry.
                    // Retain this input, but do not schedule an immediate retry. A native
                    // heartbeat also wakes cancellation paths that create no new revision.
                    // The executor's journal retains any already-issued operation identity.
                    Queue(input, wake: false);
                    Publish(AutomaticDesignPhase.WaitingForEditor, Inspect().RecoveryRevisionToken,
                        operation, false, "native_busy", error.Message);
                }
                catch (Exception error)
                {
                    string code = error is AutomationException known ? known.Code : error is NativeApiException native
                        ? "native_status_" + native.Status : error is NngException ? "native_transport_failure" : "automatic_sync_failed";
                    Pause(code, error.Message, code is "instance_changed" or "native_document_changed" or "native_transport_failure", operation);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally { Publish(AutomaticDesignPhase.Stopped, Inspect().RecoveryRevisionToken, Inspect().OperationId, false, null, null); }
    }

    private void Pause(string code, string? message, bool reattach, Guid? operation = null) =>
        Publish(AutomaticDesignPhase.Paused, Inspect().RecoveryRevisionToken, operation, reattach, code, message);

    private void Publish(AutomaticDesignPhase phase, string? revision, Guid? operation, bool reattach, string? code, string? message)
    {
        lock (gate)
        {
            if (status.Phase == phase && status.RecoveryRevisionToken == revision && status.OperationId == operation
                && status.ReattachRequired == reattach && status.ErrorCode == code && status.ErrorMessage == message) return;
            status = new(status.Sequence + 1, phase, revision, operation, reattach, code, message);
            var previous = changed; changed = NewSignal(); previous.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel(); ready.Writer.TryComplete();
        await Task.WhenAll(worker, intake);
        await driver.DisposeAsync(); stopping.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AutomationException Error(string code, string message) => new(code, message);
}
