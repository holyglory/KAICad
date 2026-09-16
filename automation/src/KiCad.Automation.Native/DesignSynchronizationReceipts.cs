using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>One immutable application-operation receipt per ID. The current
/// recovery record owns the latest receipt until archiving succeeds. This is
/// runtime recovery data, not a development planning/completion ledger.</summary>
internal sealed class DesignSynchronizationReceipts(string statePath)
{
    private readonly string directory = Path.GetFullPath(statePath) + ".operations";
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal DesignSynchronizationReceipt? Read(Guid id)
    {
        if (id == Guid.Empty) throw new AutomationException("invalid_operation_id", "A nonempty synchronization operation ID is required.");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(Path.Combine(directory, id.ToString("N") + ".json")); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || parsed.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    != parsed.RootElement.EnumerateObject().Count())
                throw new AutomationException("invalid_sync_receipt", "Duplicate or invalid synchronization receipt fields.");
            var result = JsonSerializer.Deserialize<DesignSynchronizationReceipt>(bytes, Json)
                ?? throw new AutomationException("invalid_sync_receipt", "Missing synchronization receipt.");
            result.Validate();
            if (result.OperationId != id) throw new AutomationException("invalid_sync_receipt", "The receipt filename identifies another operation.");
            return result;
        }
        catch (JsonException error) { throw new AutomationException("invalid_sync_receipt", error.Message); }
    }

    internal void Archive(DesignSynchronizationReceipt receipt)
    {
        receipt.Validate();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, receipt.OperationId.ToString("N") + ".json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        var existing = Read(receipt.OperationId);
        if (existing is not null)
        {
            if (!JsonSerializer.SerializeToUtf8Bytes(existing, Json).AsSpan().SequenceEqual(bytes))
                throw new AutomationException("sync_receipt_conflict", "A different result already occupies this operation ID; neither version was replaced.");
            return;
        }
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal IReadOnlyList<DesignSynchronizationReceipt> ReadAll()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(directory); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new AutomationException("invalid_sync_history", "Receipt history requires its ordinary owned directory.");
        var result = new List<DesignSynchronizationReceipt>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file.Name);
            if (!Guid.TryParseExact(name, "N", out Guid id) || id == Guid.Empty || name != id.ToString("N")
                || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AutomationException("invalid_sync_history", "Receipt history contains an unrecognized identity or redirected file.");
            result.Add(Read(id) ?? throw new AutomationException("sync_history_changed", "Receipt history changed during inspection."));
        }
        return result;
    }

    internal IDisposable Acquire()
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new AutomationException("synchronization_busy", error.Message); }
    }
}
