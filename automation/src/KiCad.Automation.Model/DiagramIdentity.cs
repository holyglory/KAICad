using System.Security.Cryptography;
using System.Text;

namespace KiCad.Automation.Model;

/// <summary>Deterministic identities for objects created by one diagram operation (contract rbg-v2
/// G7): the same operation and name always give the same identity, so a retried create or
/// conversion recognises its own result instead of inventing a second one.</summary>
public static class DiagramIdentity
{
    /// <summary>UUIDv5 (RFC 9562 section 5.5): SHA-1 over the namespace bytes in big-endian order
    /// followed by the UTF-8 name, then version 5 and the RFC variant.</summary>
    public static Guid Derive(Guid operationId, string name)
    {
        if (operationId == Guid.Empty) throw new AutomationException("invalid_diagram_identity", "A derived identity needs an exact operation identity.");
        ArgumentException.ThrowIfNullOrEmpty(name);
        byte[] space = operationId.ToByteArray(bigEndian: true);
        byte[] text = Encoding.UTF8.GetBytes(name);
        byte[] input = new byte[space.Length + text.Length];
        space.CopyTo(input, 0); text.CopyTo(input, space.Length);
#pragma warning disable CA5350 // RFC 9562 defines version 5 identities with SHA-1; this is not a security use.
        byte[] hash = SHA1.HashData(input);
#pragma warning restore CA5350
        byte[] bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
