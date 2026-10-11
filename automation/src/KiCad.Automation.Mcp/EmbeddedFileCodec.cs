using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

// Uses the same libzstd entry point and compression level as KiCad's native
// EMBEDDED_FILES::CompressAndEncode (ZSTD_compress(..., 15)). The SHA-256
// checksum is a supported legacy checksum form and is normalized by the
// native reader. Keeping compression in the native zstd library avoids a
// second managed implementation whose bytes could differ on save/reload.
internal static class EmbeddedFileCodec
{
    private const int MaximumSourceBytes = 32 * 1024 * 1024;

    internal static EmbeddedFile FromBytes(string name, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
            throw new AutomationException("invalid_raster_trace_embedded_name", "Embedded source names must be one safe filename.");
        if (bytes.Length == 0 || bytes.Length > MaximumSourceBytes)
            throw new AutomationException("raster_trace_source_oversized", "Embedded raster source bytes are empty or exceed 32 MiB.");

        return new EmbeddedFile
        {
            Name = name,
            Type = EmbeddedFileType.EftOther,
            Data = ByteString.CopyFrom(Convert.ToBase64String(CompressNative(bytes)), System.Text.Encoding.UTF8),
            // KiCad's native reader compares legacy SHA-256 checksums as the
            // exact lowercase hex string produced by picosha2. Keep the
            // managed envelope byte-for-byte compatible with that decoder.
            DataHash = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
    }

    internal static string Name(string sourceSha256) => "raster_trace_" + sourceSha256 + ".png";

    private static byte[] CompressNative(byte[] source)
    {
        ZstdApi api;
        try { api = ZstdApi.Instance.Value; }
        catch (Exception error) when (error is AutomationException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        { throw new AutomationException("raster_trace_embedded_codec_unavailable", "The native zstd library is unavailable for exact embedded-source encoding."); }
        nuint bound = api.CompressBound((nuint)source.Length);
        if (bound == 0 || bound > int.MaxValue)
            throw new AutomationException("raster_trace_embedded_codec_failed", "Native zstd returned an invalid compression bound.");
        IntPtr sourcePtr = Marshal.AllocHGlobal(source.Length);
        IntPtr destinationPtr = Marshal.AllocHGlobal((int)bound);
        try
        {
            Marshal.Copy(source, 0, sourcePtr, source.Length);
            nuint compressed = api.Compress(destinationPtr, bound, sourcePtr, (nuint)source.Length, 15);
            if (api.IsError(compressed) || compressed > bound || compressed > int.MaxValue)
                throw new AutomationException("raster_trace_embedded_codec_failed", "Native zstd could not compress the exact source bytes.");
            byte[] output = new byte[(int)compressed];
            Marshal.Copy(destinationPtr, output, 0, output.Length);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(sourcePtr);
            Marshal.FreeHGlobal(destinationPtr);
        }
    }

    private sealed class ZstdApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nuint CompressBoundDelegate(nuint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nuint CompressDelegate(IntPtr destination, nuint capacity, IntPtr source, nuint size, int level);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint IsErrorDelegate(nuint code);

        private readonly IntPtr library;
        private readonly CompressBoundDelegate compressBound;
        private readonly CompressDelegate compress;
        private readonly IsErrorDelegate isError;

        private ZstdApi()
        {
            string[] names = OperatingSystem.IsWindows() ? ["libzstd.dll", "zstd.dll"]
                : OperatingSystem.IsMacOS() ? ["libzstd.dylib", "libzstd.1.dylib",
                    "/opt/homebrew/opt/zstd/lib/libzstd.1.dylib", "/usr/local/opt/zstd/lib/libzstd.1.dylib",
                    "/opt/homebrew/lib/libzstd.1.dylib", "/usr/local/lib/libzstd.1.dylib"]
                : ["libzstd.so.1", "libzstd.so"];
            string? configured = Environment.GetEnvironmentVariable("KICAD_AUTOMATION_ZSTD_LIBRARY");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                    throw new AutomationException("raster_trace_embedded_codec_unavailable", "KICAD_AUTOMATION_ZSTD_LIBRARY must be an existing absolute library path.");
                names = [configured, .. names];
            }
            foreach (string name in names)
                if (NativeLibrary.TryLoad(name, out library)) break;
            if (library == IntPtr.Zero)
                throw new AutomationException("raster_trace_embedded_codec_unavailable", "The native zstd library is unavailable for exact embedded-source encoding.");
            compressBound = Load<CompressBoundDelegate>("ZSTD_compressBound");
            compress = Load<CompressDelegate>("ZSTD_compress");
            isError = Load<IsErrorDelegate>("ZSTD_isError");
        }

        internal static Lazy<ZstdApi> Instance { get; } = new(() => new ZstdApi());
        internal nuint CompressBound(nuint size) => compressBound(size);
        internal nuint Compress(IntPtr destination, nuint capacity, IntPtr source, nuint size, int level)
            => compress(destination, capacity, source, size, level);
        internal bool IsError(nuint code) => isError(code) != 0;

        private T Load<T>(string name) where T : Delegate
        {
            IntPtr symbol = NativeLibrary.GetExport(library, name);
            return Marshal.GetDelegateForFunctionPointer<T>(symbol);
        }
    }
}
