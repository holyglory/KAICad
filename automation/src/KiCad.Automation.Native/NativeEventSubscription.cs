using System.Runtime.InteropServices;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>One explicitly discovered local subscriber. Closing it only closes
/// this observation channel, never the editor or a dirty document.</summary>
public interface INativeEventSource : IDisposable
{
    Task<NativeEventDelivery> ReceiveAsync(CancellationToken token);
}

public sealed class NativeEventSubscription : INativeEventSource
{
    private readonly Nng.Socket socket;
    private readonly NativeEventCursor cursor;
    private readonly SemaphoreSlim reader = new(1, 1);
    private int disposed;

    public NativeEventSubscription(AutomationSession session, ulong? afterSequence = null)
    {
        NngTransport.ValidateEndpoint(session.EventEndpoint);
        cursor = new(session, afterSequence);
        Nng.Check(Nng.nng_sub0_open(out socket));
        try
        {
            // Empty binary topic, not a NUL-terminated string subscription.
            Nng.Check(Nng.nng_setopt(socket, "sub:subscribe", IntPtr.Zero, 0));
            Nng.Check(Nng.nng_dial(socket, session.EventEndpoint, IntPtr.Zero, 2));
        }
        catch { Dispose(); throw; }
    }

    public async Task<NativeEventDelivery> ReceiveAsync(CancellationToken token)
    {
        await reader.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            // Cancellation permanently closes this subscription and wakes nng_recv.
            // A new subscription must rediscover/verify its process and stream epoch.
            using var cancellation = token.Register(Dispose);
            return await Task.Run(() =>
            {
                try
                {
                    nuint length = 0;
                    Nng.Check(Nng.nng_recv(socket, out IntPtr bytes, ref length, 1));
                    try
                    {
                        byte[] packet = new byte[checked((int)length)];
                        Marshal.Copy(bytes, packet, 0, packet.Length);
                        token.ThrowIfCancellationRequested();
                        return cursor.Observe(AutomationEvent.Parser.ParseFrom(packet));
                    }
                    finally { Nng.nng_free(bytes, length); }
                }
                catch (NngException) when (token.IsCancellationRequested)
                { throw new OperationCanceledException(token); }
            }, token);
        }
        finally { reader.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) _ = Nng.nng_close(socket);
    }
}
