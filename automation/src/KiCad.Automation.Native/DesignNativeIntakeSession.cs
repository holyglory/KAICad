using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public enum DesignNativeIntakePhase { Starting, Watching, Paused, Stopped }
public sealed record DesignNativeIntakeStatus(ulong Sequence, DesignNativeIntakePhase Phase,
    DesignRecoveryNativeObservation? Observation, string? ErrorCode, string? ErrorMessage);

/// <summary>Continuous event-to-recovery intake, not XML write-back or native
/// mutation. Disposing this task never closes its editor.</summary>
public sealed class DesignNativeIntakeSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly DesignRecoveryNativeObserver observer;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private readonly TimeSpan silenceLimit;
    private TaskCompletionSource changed = Signal();
    private TaskCompletionSource? resume;
    private DesignNativeIntakeStatus status = new(0, DesignNativeIntakePhase.Starting, null, null, null);

    internal DesignNativeIntakeSession(DesignRecoveryNativeObserver observer, TimeSpan? silenceLimit = null)
    {
        this.observer = observer; this.silenceLimit = silenceLimit ?? TimeSpan.FromSeconds(30);
        if (this.silenceLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(silenceLimit));
        worker = Task.Run(RunAsync);
    }

    public static async Task<DesignNativeIntakeSession> CreateAsync(DesignRecoveryStore store, NativeClient client,
        CancellationToken token = default) => new(await DesignRecoveryNativeObserver.CreateAsync(store, client, token));

    public DesignNativeIntakeStatus Inspect() { lock (gate) return status; }
    public async Task<DesignNativeIntakeStatus> WaitAsync(ulong afterSequence, CancellationToken token = default)
    {
        while (true)
        {
            Task next;
            lock (gate)
            {
                if (afterSequence > status.Sequence)
                    throw new AutomationException("invalid_native_intake_cursor", "The cursor is ahead of this native intake.");
                if (status.Sequence > afterSequence || status.Phase == DesignNativeIntakePhase.Stopped) return status;
                next = changed.Task;
            }
            await next.WaitAsync(token);
        }
    }

    public void Resume(ulong expectedSequence)
    {
        lock (gate)
        {
            if (status.Sequence != expectedSequence || status.Phase != DesignNativeIntakePhase.Paused || resume is null)
                throw new AutomationException("native_intake_changed", "Resume only the current paused native intake.");
            if (status.Observation?.ReattachRequired == true)
                throw new AutomationException("native_intake_reattach_required", "Create a new verified native intake after reconnecting.");
            resume.TrySetResult();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var observation = await observer.ReceiveAsync(stopping.Token, silenceLimit);
                if (observation.RecoveryRevisionToken is not null)
                {
                    lock (gate) Publish(DesignNativeIntakePhase.Watching, observation, null, null);
                    continue;
                }
                Task restart;
                lock (gate)
                {
                    resume = Signal(); restart = resume.Task;
                    Publish(DesignNativeIntakePhase.Paused, observation, observation.ErrorCode, observation.ErrorMessage);
                }
                await restart.WaitAsync(stopping.Token);
                lock (gate) resume = null;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate) Publish(DesignNativeIntakePhase.Stopped, status.Observation,
                error is AutomationException known ? known.Code : "native_intake_failed", error.Message);
        }
        finally
        {
            observer.Dispose();
            lock (gate)
                if (status.Phase != DesignNativeIntakePhase.Stopped)
                    Publish(DesignNativeIntakePhase.Stopped, status.Observation, null, null);
        }
    }

    private void Publish(DesignNativeIntakePhase phase, DesignRecoveryNativeObservation? observation, string? code, string? message)
    {
        status = new(checked(status.Sequence + 1), phase, observation, code, message);
        var signal = changed; changed = Signal(); signal.TrySetResult();
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask DisposeAsync() { stopping.Cancel(); observer.Dispose(); await worker; }
}
