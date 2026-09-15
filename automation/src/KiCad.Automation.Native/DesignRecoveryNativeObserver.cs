using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Kiapi.Common.Types;

namespace KiCad.Automation.Native;

public enum NativeIntakeReason { InitialSnapshot, CommittedChange, StreamRecovery }
public sealed record DesignRecoveryNativeObservation(NativeIntakeReason Reason, NativeEventDelivery? Delivery,
    string? RecoveryRevisionToken, bool NativeStateChanged, bool ChoicesInvalidated,
    bool ReattachRequired, string? ErrorCode, string? ErrorMessage);

/// <summary>Native events refresh recovery observations only. Baseline, desired
/// bytes, requirements and pending operations remain owned by reconciliation.</summary>
public sealed class DesignRecoveryNativeObserver : IDisposable
{
    private readonly DesignRecoveryStore store;
    private readonly NativeClient client;
    private readonly INativeEventSource events;
    private readonly Guid instanceId;
    private readonly SemaphoreSlim reader = new(1, 1);
    private NativeIntakeReason? pendingReason = NativeIntakeReason.InitialSnapshot;
    private NativeEventDelivery? pendingDelivery;
    private int disposed;

    private DesignRecoveryNativeObserver(DesignRecoveryStore store, NativeClient client, INativeEventSource events, Guid instanceId)
    { this.store = store; this.client = client; this.events = events; this.instanceId = instanceId; }

    public static Task<DesignRecoveryNativeObserver> CreateAsync(DesignRecoveryStore store, NativeClient client,
        CancellationToken token = default) => CreateAsync(store, client, session => new NativeEventSubscription(session), token);

    internal static async Task<DesignRecoveryNativeObserver> CreateAsync(DesignRecoveryStore store, NativeClient client,
        Func<AutomationSession, INativeEventSource> subscribe, CancellationToken token = default)
    {
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "Create recovery state before observing native edits.");
        var session = await client.HandshakeAsync(token);
        if (session.InstanceId != saved.State.InstanceId.ToString("D"))
            throw new AutomationException("recovery_instance_mismatch", "Observe the exact instance recorded by the design.");
        // Verify the discovery contract before the subscription factory, then
        // subscribe before the initial snapshot to cover edits during startup.
        _ = new NativeEventCursor(session);
        token.ThrowIfCancellationRequested();
        var source = subscribe(session);
        try
        {
            token.ThrowIfCancellationRequested();
            return new(store, client, source, saved.State.InstanceId);
        }
        catch { source.Dispose(); throw; }
    }

    public async Task<DesignRecoveryNativeObservation> ReceiveAsync(CancellationToken token = default,
        TimeSpan? eventSilenceLimit = null)
    {
        await reader.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            while (pendingReason is null)
            {
                var delivery = await ReceiveEventAsync(token, eventSilenceLimit);
                if (delivery.Disposition is NativeEventDisposition.Duplicate or NativeEventDisposition.Heartbeat) continue;
                pendingReason = delivery.Disposition == NativeEventDisposition.Change
                    ? NativeIntakeReason.CommittedChange : NativeIntakeReason.StreamRecovery;
                pendingDelivery = delivery;
                var saved = Read();
                if (delivery.Event.SchematicCommit is { } commit)
                {
                    if (delivery.Disposition == NativeEventDisposition.Change
                        && SameHierarchy(commit.Document, saved.State.Baseline.Schematic.Document)
                        && commit.Revision.Epoch == saved.State.NativeRevision.Epoch
                        && !saved.State.HasPendingWork && saved.State.ObservedElectrical is not null
                        && commit.Revision.Sequence <= saved.State.NativeRevision.Sequence)
                    { pendingReason = null; pendingDelivery = null; continue; }
                }
            }

            var reason = pendingReason ?? throw new InvalidOperationException("A native observation requires a pending reason.");

            // File intake may have saved independent desired bytes while a
            // native snapshot was in flight. Retry that CAS conflict only;
            // semantic conflicts and pending mutations still pause intake.
            for (int attempt = 0; ; ++attempt)
            {
                var before = Read();
                // Revalidate retained events after failed reads/writes as well.
                if (pendingDelivery?.Event.SchematicCommit is { } pendingCommit
                    && (!SameHierarchy(pendingCommit.Document, before.State.Baseline.Schematic.Document)
                        || pendingCommit.Revision.Epoch != before.State.NativeRevision.Epoch))
                    return Failure(NativeIntakeReason.StreamRecovery, pendingDelivery, "native_document_changed",
                        "Native document identity changed; reattach and reconcile the saved design.", true);
                try
                {
                    var eventRevision = pendingDelivery?.Event.SchematicCommit?.Revision;
                    var minimum = eventRevision is null ? null
                        : new KiCad.Automation.Model.DocumentRevision(eventRevision.Epoch, eventRevision.Sequence);
                    var refreshed = await DesignRecoveryInspector.RefreshAsync(store, client, before.RevisionToken, token, minimum, includeElectrical: true);
                    bool changed = !before.State.Observed.Equals(refreshed.State.Observed)
                        || before.State.NativeRevision != refreshed.State.NativeRevision
                        || before.State.TrackingComplete != refreshed.State.TrackingComplete;
                    changed |= before.State.ObservedElectrical is not null
                        && !Equals(before.State.ObservedElectrical, refreshed.State.ObservedElectrical);
                    var result = new DesignRecoveryNativeObservation(reason, pendingDelivery,
                        refreshed.RevisionToken, changed,
                        before.State.HierarchyResolution is not null && refreshed.State.HierarchyResolution is null,
                        false, null, null);
                    pendingReason = null; pendingDelivery = null;
                    return result;
                }
                catch (AutomationException error) when (error.Code == "design_recovery_changed" && attempt < 2) { }
            }
        }
        catch (AutomationException error)
        {
            bool reattach = error.Code is "instance_changed" or "event_stream_changed" or "incompatible_event_stream"
                or "invalid_event" or "recovery_instance_mismatch" or "invalid_recovery_revision" or "native_event_silence";
            return Failure(pendingReason ?? NativeIntakeReason.StreamRecovery, pendingDelivery, error.Code, error.Message, reattach);
        }
        catch (NativeApiException error)
        { return Failure(pendingReason ?? NativeIntakeReason.StreamRecovery, pendingDelivery, "native_status_" + error.Status, error.Message, false); }
        catch (NngException error)
        { return Failure(pendingReason ?? NativeIntakeReason.StreamRecovery, pendingDelivery, "native_transport_failure", error.Message, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Failure(pendingReason ?? NativeIntakeReason.StreamRecovery, pendingDelivery, "native_observation_io", error.Message, false); }
        finally { reader.Release(); }
    }

    private StoredDesignRecovery Read()
    {
        var saved = store.Read() ?? throw new AutomationException("missing_design_recovery", "Recovery state is unavailable.");
        if (saved.State.InstanceId != instanceId)
            throw new AutomationException("recovery_instance_mismatch", "The recovery record now belongs to another instance.");
        return saved;
    }

    private async Task<NativeEventDelivery> ReceiveEventAsync(CancellationToken token, TimeSpan? silenceLimit)
    {
        if (silenceLimit is null) return await events.ReceiveAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(silenceLimit.Value);
        try { return await events.ReceiveAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new AutomationException("native_event_silence", "Native events stopped responding; reattach without closing the editor."); }
    }

    private static DesignRecoveryNativeObservation Failure(NativeIntakeReason reason, NativeEventDelivery? delivery,
        string code, string message, bool reattach) => new(reason,
            delivery is null ? null : delivery with { Event = delivery.Event.Clone() },
            null, false, false, reattach, code, message);

    // A hierarchy snapshot owns all sheet-instance paths under its exact root,
    // including newly inserted sheets not present in the previous observation.
    private static bool SameHierarchy(DocumentSpecifier left, DocumentSpecifier right) =>
        left.Type == right.Type && Equals(left.Project, right.Project)
        && left.SheetPath?.Path.Count > 0 && right.SheetPath?.Path.Count > 0
        && left.SheetPath.Path[0].Equals(right.SheetPath.Path[0]);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) events.Dispose();
    }
}
