using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

// Builds the same base64-encoded Zstandard payload accepted by KiCad's
// EmbeddedFiles loader. Raw Zstd blocks keep this dependency-free and bounded;
// KiCad recompresses the payload on its next save. The SHA-256 checksum is a
// supported legacy checksum form and is normalized by the native reader.
internal static class EmbeddedFileCodec
{
    private const int MaximumSourceBytes = 32 * 1024 * 1024;
    private const int MaximumBlockBytes = 128 * 1024;

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
            Data = ByteString.CopyFrom(Convert.ToBase64String(EncodeRawZstd(bytes)), System.Text.Encoding.UTF8),
            DataHash = Convert.ToHexString(SHA256.HashData(bytes))
        };
    }

    internal static string Name(string sourceSha256) => "raster_trace_" + sourceSha256 + ".png";

    private static byte[] EncodeRawZstd(byte[] source)
    {
        int blockCount = (source.Length + MaximumBlockBytes - 1) / MaximumBlockBytes;
        int capacity = 4 + 1 + sizeof(ulong) + source.Length + blockCount * 3;
        byte[] output = new byte[capacity];
        int offset = 0;
        output[offset++] = 0x28; output[offset++] = 0xB5; output[offset++] = 0x2F; output[offset++] = 0xFD;
        // FCS flag 3 (8-byte content size), single segment, no checksum/dictionary.
        output[offset++] = 0xE0;
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset, sizeof(ulong)), (ulong)source.Length);
        offset += sizeof(ulong);
        int sourceOffset = 0;
        while (sourceOffset < source.Length)
        {
            int count = Math.Min(MaximumBlockBytes, source.Length - sourceOffset);
            bool last = sourceOffset + count == source.Length;
            uint header = ((uint)count << 3) | (last ? 1u : 0u);
            output[offset++] = (byte)header;
            output[offset++] = (byte)(header >> 8);
            output[offset++] = (byte)(header >> 16);
            source.AsSpan(sourceOffset, count).CopyTo(output.AsSpan(offset));
            sourceOffset += count;
            offset += count;
        }
        return output.AsSpan(0, offset).ToArray();
    }
}
