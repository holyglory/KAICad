using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record RefinementInputPublicationReceipt(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid InputId,
    [property: JsonRequired] string DesignPath,
    [property: JsonRequired] string InputXml,
    [property: JsonRequired] string BeforeSha256,
    [property: JsonRequired] string AfterSha256,
    [property: JsonRequired] RefinementPublicationStage Stage,
    [property: JsonRequired] string? StagedPath,
    [property: JsonRequired] string? RetainedPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ConfirmedAt = null)
{
    internal void Validate()
    {
        try
        {
            var proof = Intent(); proof.Validate();
            if (proof.Input.Id != InputId) throw Invalid("The receipt input does not match its operation identity.");
        }
        catch (Exception error) when (error is AutomationException or ArgumentException)
        { throw Invalid(error.Message); }
    }

    internal RefinementPublicationIntent Intent() => new(Version, DesignPath, DiagramRefinementInputXml.Read(InputXml),
        BeforeSha256, AfterSha256, Stage, StagedPath, RetainedPath, ConfirmedAt);

    private static AutomationException Invalid(string message) => new("invalid_refinement_receipt", message);
}

/// <summary>Durable process-owned receipts for original-input publication. This is
/// runtime recovery state, not a completion ledger and not a second design model.</summary>
public sealed class RefinementInputReceipts(string stateDirectory)
{
    private readonly string directory = Path.Combine(Path.GetFullPath(stateDirectory), "refinement-input-operations");
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public RefinementInputPublicationReceipt? Read(Guid inputId)
    {
        if (inputId == Guid.Empty) throw new AutomationException("invalid_operation_id", "A nonempty original-input identity is required.");
        try
        {
            RequireDirectory(create: false);
            string path = Path.Combine(directory, inputId.ToString("N") + ".json"); RequireRegular(path);
            byte[] bytes = File.ReadAllBytes(path);
            using var parsed = JsonDocument.Parse(bytes);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || parsed.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    != parsed.RootElement.EnumerateObject().Count())
                throw new AutomationException("invalid_refinement_receipt", "Duplicate or invalid publication receipt fields.");
            var result = JsonSerializer.Deserialize<RefinementInputPublicationReceipt>(bytes, Json)
                ?? throw new AutomationException("invalid_refinement_receipt", "Missing original-input receipt.");
            result.Validate();
            if (result.InputId != inputId) throw new AutomationException("invalid_refinement_receipt", "Receipt filename and input identity differ.");
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException error) { throw new AutomationException("invalid_refinement_receipt", error.Message); }
    }

    public void Write(RefinementInputPublicationReceipt receipt)
    {
        receipt.Validate(); RequireDirectory(create: true);
        using var writer = Lock(receipt.InputId, ".receipt.lock");
        string path = Path.Combine(directory, receipt.InputId.ToString("N") + ".json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        var existing = Read(receipt.InputId);
        if (existing is not null)
        {
            if (existing.DesignPath != receipt.DesignPath || existing.InputXml != receipt.InputXml
                || existing.BeforeSha256 != receipt.BeforeSha256 || existing.AfterSha256 != receipt.AfterSha256
                || (int)receipt.Stage < (int)existing.Stage)
                throw new AutomationException("refinement_receipt_conflict", "A different or older publication result already occupies this input identity.");
            if (bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(existing, Json))) return;
            if ((int)receipt.Stage != (int)existing.Stage + 1
                || (existing.Stage == RefinementPublicationStage.Replacing
                    && (receipt.StagedPath != existing.StagedPath || receipt.RetainedPath != existing.RetainedPath)))
                throw new AutomationException("refinement_receipt_conflict", "A receipt transition cannot skip a phase, rewrite paths or change a recorded acknowledgement.");
        }
        else if (receipt.Stage != RefinementPublicationStage.Prepared)
            throw new AutomationException("refinement_receipt_conflict", "Record the original prepared intent before replacement or publication.");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            if (existing is null) File.Move(temporary, path, overwrite: false);
            else File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IDisposable Acquire(Guid inputId) => Lock(inputId, ".operation.lock");

    private IDisposable Lock(Guid inputId, string suffix)
    {
        if (inputId == Guid.Empty) throw new AutomationException("invalid_operation_id", "Identify the exact original input.");
        RequireDirectory(create: true); string path = Path.Combine(directory, inputId.ToString("N") + suffix);
        if (File.Exists(path)) RequireRegular(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new AutomationException("refinement_publication_busy", error.Message); }
    }

    private void RequireDirectory(bool create)
    {
        if (create) Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new AutomationException("invalid_refinement_receipt_path", "Receipt storage must use its ordinary owned directory.");
    }
    private static void RequireRegular(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new AutomationException("invalid_refinement_receipt_path", "Receipt state requires an ordinary file, not a redirected path.");
    }
}
