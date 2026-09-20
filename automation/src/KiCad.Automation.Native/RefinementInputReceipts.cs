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
        if (Version != 1 || InputId == Guid.Empty || !Path.IsPathFullyQualified(DesignPath)
            || Path.GetFullPath(DesignPath) != DesignPath || !RefinementPublicationIntent.Digest(BeforeSha256)
            || !RefinementPublicationIntent.Digest(AfterSha256) || BeforeSha256 == AfterSha256
            || !Enum.IsDefined(Stage)) throw Invalid("The original-input receipt has incomplete identity or hashes.");
        DiagramRefinementInput input;
        try { input = DiagramRefinementInputXml.Read(InputXml); }
        catch (AutomationException error) { throw Invalid("The receipt does not contain a valid retained input: " + error.Message); }
        if (input.Id != InputId || input.SourceSha256 != BeforeSha256)
            throw Invalid("The receipt input does not match its operation identity or source preimage.");
        if (Stage == RefinementPublicationStage.Prepared)
        {
            if (StagedPath is not null || RetainedPath is not null || ConfirmedAt is not null) throw Invalid("A prepared receipt cannot claim replacement paths or confirmation.");
            return;
        }
        if (string.IsNullOrEmpty(StagedPath) || string.IsNullOrEmpty(RetainedPath)
            || !Path.IsPathFullyQualified(StagedPath) || !Path.IsPathFullyQualified(RetainedPath)
            || Path.GetDirectoryName(StagedPath) != Path.GetDirectoryName(DesignPath)
            || (RetainedPath != StagedPath && RetainedPath != StagedPath + ".previous")
            || (Stage == RefinementPublicationStage.Published
                ? ConfirmedAt is null || ConfirmedAt.Value.Offset != TimeSpan.Zero
                : ConfirmedAt is not null)) throw Invalid("Replacement receipts must retain exact paths and only publish after verified confirmation.");
    }

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
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, inputId.ToString("N") + ".json"));
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
        receipt.Validate(); Directory.CreateDirectory(directory);
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
        }
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (existing is null) File.Move(temporary, path, overwrite: false);
            else File.Replace(temporary, path, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
