using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Native;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

internal sealed record KeepoutCachedReceipt(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("callerExpectedFingerprint")] string CallerExpectedFingerprint,
    [property: JsonPropertyName("requestedZoneFingerprint")] string RequestedZoneFingerprint,
    [property: JsonPropertyName("requestJson")] string RequestJson)
{
    internal const int CurrentVersion = 1;
}

internal sealed class KeepoutCheckedRequestStore
{
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal KeepoutCheckedRequestStore(string stateDirectory)
    {
        if (!Path.IsPathFullyQualified(stateDirectory))
            throw new AutomationException("invalid_pcb_keepout_receipt", "The keepout receipt state directory must be absolute.");
        directory = Path.Combine(Path.GetFullPath(stateDirectory), "pcb-keepout-requests");
    }

    internal KeepoutCachedCheckedRequest? Read(string instanceId, string operationId)
    {
        string path = PathFor(instanceId, operationId);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = JsonSerializer.Deserialize<KeepoutCachedReceipt>(stream, Json)
                ?? throw Invalid("The keepout request receipt is empty.");
            if (record.Version != KeepoutCachedReceipt.CurrentVersion
                || record.InstanceId != instanceId || record.OperationId != operationId)
                throw Invalid("The keepout request receipt identity is invalid.");
            var request = SchematicJson.Parser.Parse<CheckedPcbItemTransaction>(record.RequestJson);
            string actual = Fingerprint(request);
            if (!string.Equals(actual, record.Fingerprint, StringComparison.Ordinal))
                throw Invalid("The keepout request receipt fingerprint is invalid.");
            var operation = request.Operations.Single();
            var mutation = operation.ActionCase switch
            {
                PcbItemOperation.ActionOneofCase.Create => PcbKeepoutAuthoringTools.KeepoutMutation.Create,
                PcbItemOperation.ActionOneofCase.Update => PcbKeepoutAuthoringTools.KeepoutMutation.Update,
                PcbItemOperation.ActionOneofCase.Delete => PcbKeepoutAuthoringTools.KeepoutMutation.Delete,
                _ => throw Invalid("The keepout request receipt operation is unsupported.")
            };
            string target = mutation == PcbKeepoutAuthoringTools.KeepoutMutation.Delete
                ? operation.Delete.ItemIds.Single().Value
                : mutation == PcbKeepoutAuthoringTools.KeepoutMutation.Update
                    ? operation.Update.Items.Single().Unpack<Kiapi.Board.Types.Zone>().Id.Value
                    : operation.Create.Items.Single().Unpack<Kiapi.Board.Types.Zone>().Id.Value;
            return new(mutation, target, record.CallerExpectedFingerprint, record.RequestedZoneFingerprint, request);
        }
        catch (AutomationException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw new AutomationException("pcb_keepout_receipt_unavailable", error.Message); }
    }

    internal void Write(string instanceId, string operationId, KeepoutCachedCheckedRequest request)
    {
        Directory.CreateDirectory(directory);
        string requestJson = SchematicJson.Formatter.Format(request.Request);
        var record = new KeepoutCachedReceipt(KeepoutCachedReceipt.CurrentVersion, instanceId, operationId,
            Fingerprint(request.Request), request.CallerExpectedFingerprint, request.RequestedZoneFingerprint, requestJson);
        string path = PathFor(instanceId, operationId);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, record, Json);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
        }
    }

    private string PathFor(string instanceId, string operationId) =>
        Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + "\n" + operationId))) + ".json");

    private static string Fingerprint(CheckedPcbItemTransaction request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SchematicJson.Formatter.Format(request))));

    private static AutomationException Invalid(string message) =>
        new("invalid_pcb_keepout_receipt", message);
}
