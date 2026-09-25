using System.Runtime.InteropServices;

namespace KiCad.Automation.Native;

public interface INativeTransport
{
    Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
                               CancellationToken cancellationToken = default);
}

/// <summary>Native NNG REQ/REP transport. No shell, HTTP service or Python bridge.</summary>
public sealed class NngTransport : INativeTransport
{
    /// <summary>Checks the actual native C binding without listening or dialing.</summary>
    public static string ProbeLibrary()
    {
        Nng.Check(Nng.nng_req0_open(out var request));
        try { Nng.Check(Nng.nng_setopt_ms(request, "recv-timeout", 1000)); }
        finally { Nng.Check(Nng.nng_close(request)); }
        Nng.Check(Nng.nng_sub0_open(out var subscription));
        try { Nng.Check(Nng.nng_setopt_ms(subscription, "recv-timeout", 1000)); }
        finally { Nng.Check(Nng.nng_close(subscription)); }
        return Marshal.PtrToStringUTF8(Nng.nng_version())
            ?? throw new InvalidDataException("NNG did not report its library version.");
    }

    public Task<byte[]> ExchangeAsync(string endpoint, byte[] request, TimeSpan timeout,
                                      CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return Task.Run(() => Exchange(endpoint, request, (int)Math.Ceiling(timeout.TotalMilliseconds),
                                       cancellationToken), cancellationToken);
    }

    public static void ValidateEndpoint(string endpoint)
    {
        // SA-02: execution and KiCad are co-located. Never silently use TCP.
        bool unixPath = endpoint.StartsWith("ipc:///", StringComparison.Ordinal) && endpoint.Length > 7;
        bool windowsPath = OperatingSystem.IsWindows() && endpoint.Length > 9
            && endpoint.StartsWith("ipc://", StringComparison.Ordinal)
            && char.IsAsciiLetter(endpoint[6]) && endpoint[7] == ':' && endpoint[8] is '/' or '\\';
        if ((!unixPath && !windowsPath) || endpoint.IndexOf('\0') >= 0)
            throw new ArgumentException("An explicit absolute local ipc:// endpoint is required.", nameof(endpoint));
    }

    private static byte[] Exchange(string endpoint, byte[] request, int timeoutMs,
                                   CancellationToken cancellationToken)
    {
        Nng.Check(Nng.nng_req0_open(out Nng.Socket socket));
        using var handle = new SocketLifetime(socket);
        using CancellationTokenRegistration registration = cancellationToken.Register(handle.Dispose);
        bool delivered = false;
        try
        {
            Nng.Check(Nng.nng_setopt_ms(socket, "send-timeout", timeoutMs));
            Nng.Check(Nng.nng_setopt_ms(socket, "recv-timeout", timeoutMs));
            // Nonblocking connection lets the timeout/cancellation cover an unavailable peer.
            Nng.Check(Nng.nng_dial(socket, endpoint, IntPtr.Zero, 2));
            cancellationToken.ThrowIfCancellationRequested();
            // A REQ send completes only once a connected peer pipe takes the request. Until then
            // closing this socket discards it, so the peer can never receive it later.
            Nng.Check(Nng.nng_send(socket, request, (nuint)request.Length, 0));
            delivered = true;
            nuint length = 0;
            Nng.Check(Nng.nng_recv(socket, out IntPtr data, ref length, 1));
            try
            {
                byte[] result = new byte[checked((int)length)];
                Marshal.Copy(data, result, 0, result.Length);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally { Nng.nng_free(data, length); }
        }
        catch (NngException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (NngException error) when (delivered)
        {
            throw new NngException(error.ErrorCode, error.Message, requestDelivered: true);
        }
    }

    private sealed class SocketLifetime(Nng.Socket socket) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                _ = Nng.nng_close(socket);
        }
    }
}

public sealed class NngException(int errorCode, string message, bool requestDelivered = false) : IOException(message)
{
    public int ErrorCode { get; } = errorCode;

    /// <summary>
    /// True when the request had reached the peer's connection before this failure, for example a
    /// reply timeout: the peer may still act on it. False means the peer never received it.
    /// </summary>
    public bool RequestDelivered { get; } = requestDelivered;
}

internal static class Nng
{
    static Nng()
    {
        // This override belongs to the trusted process launcher, never a design
        // document. It lets a Mac runtime use the matching app-bundle dylib
        // without changing an already signed native bundle or loader globals.
        string? library = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_NNG_LIBRARY");
        if (library is null) return;
        if (!Path.IsPathFullyQualified(library) || !File.Exists(library))
            throw new InvalidDataException("KICAD_AUTOMATION_NNG_LIBRARY must name an existing absolute native library.");
        // Every P/Invoke can be bound lazily, long after the first call. Keep
        // one process-lifetime handle instead of reopening a possibly removed
        // installation pathname for each newly used function.
        IntPtr handle = NativeLibrary.Load(library);
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Nng).Assembly, (name, _, _) =>
                name == "nng" ? handle : IntPtr.Zero);
        }
        catch { NativeLibrary.Free(handle); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Socket { public uint Id; }

    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr nng_version();

    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_req0_open(out Socket socket);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_rep0_open(out Socket socket);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_listen(Socket socket, [MarshalAs(UnmanagedType.LPUTF8Str)] string address, IntPtr listener, int flags);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_setopt_size(Socket socket, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint value);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_sub0_open(out Socket socket);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_setopt(Socket socket, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                        IntPtr value, nuint size);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_close(Socket socket);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_setopt_ms(Socket socket, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_dial(Socket socket, [MarshalAs(UnmanagedType.LPUTF8Str)] string address, IntPtr dialer, int flags);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_send(Socket socket, byte[] data, nuint size, int flags);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int nng_recv(Socket socket, out IntPtr data, ref nuint size, int flags);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void nng_free(IntPtr data, nuint size);
    [DllImport("nng", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nng_strerror(int error);

    internal static void Check(int result)
    {
        if (result != 0)
            throw new NngException(result, Marshal.PtrToStringUTF8(nng_strerror(result)) ?? $"NNG error {result}");
    }
}
