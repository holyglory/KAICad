using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Structured admission boundary for all copper-zone operations.  The legacy
/// zone tools remain the execution and receipt authority; this wrapper binds
/// their document, lifecycle checkpoint, operation identity and task input in
/// one typed request.
/// </summary>
[McpServerToolType]
public sealed class PcbZoneTypedTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_zone_mutation_typed"),
     Description("Read or mutate one native copper zone from one structured input. input carries an explicit PCB document, complete lifecycle observation, operation kind, canonical operation identity and either a typed zone or zoneId. Supported mutations are read, create, update and delete. Legacy protobuf-JSON zone tools remain available, while this boundary rejects incomplete or stale identity data before invoking them."),
     KiCadCapability("pcb-zones", "compiled-mcp plus-native-board-api", "typed PCB document, complete lifecycle checkpoint, operation kind, zone input and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "PcbZoneTypedMcpProcessTests.TypedZoneMutationPublishesStructuredSchemaAndRejectsIncompleteInput", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> Mutate(string instanceId, PcbZoneMutationInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            if (input is null)
                throw Invalid("input is required.");
            var payload = input.ToPayload();
            var legacy = new PcbZoneAuthoringTools(registry);
            return payload.Mutation switch
            {
                PcbZoneMutationKind.Read => await legacy.Read(instanceId, payload.Document, payload.ZoneId!, payload.ExpectedState, cancellationToken),
                PcbZoneMutationKind.Create => await legacy.Create(instanceId, payload.Document, payload.Zone!, payload.ExpectedState, payload.OperationId!, cancellationToken),
                PcbZoneMutationKind.Update => await legacy.Update(instanceId, payload.Document, payload.Zone!, payload.ExpectedState, payload.OperationId!, cancellationToken),
                PcbZoneMutationKind.Delete => await legacy.Delete(instanceId, payload.Document, payload.ZoneId!, payload.ExpectedState, payload.OperationId!, cancellationToken),
                _ => throw Invalid("mutation must be read, create, update or delete.")
            };
        }
        catch (AutomationException error)
        {
            return Error(error.Code, error.Message);
        }
        catch (JsonException error)
        {
            return Error("invalid_pcb_zone_typed_input", error.Message);
        }
    }

    private static AutomationException Invalid(string message)
        => new("invalid_pcb_zone_typed_input", message);

    private static CallToolResult Error(string code, string message)
    {
        var json = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = message });
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }]
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbZoneMutationInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private DocumentLifecycleInput? expectedState;
    private PcbZoneMutationKind mutation;
    private string? operationId;
    private string? zoneId;
    private PcbZoneAuthoringInput? zone;

    [Description("The exact PCB document being inspected or changed.")]
    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    [Description("The complete lifecycle observation captured immediately before the operation.")]
    public DocumentLifecycleInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }
    public PcbZoneMutationKind Mutation { get => mutation; set => mutation = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public string? ZoneId { get => zoneId; set => zoneId = BindOnce(value); }
    public PcbZoneAuthoringInput? Zone { get => zone; set => zone = BindOnce(value); }

    internal (string Document, string ExpectedState, PcbZoneMutationKind Mutation,
        string? OperationId, string? ZoneId, PcbZoneAuthoringInput? Zone) ToPayload()
    {
        if (Document is null || ExpectedState is null)
            throw Invalid("document and expectedState are required.");
        if (!Enum.IsDefined(Mutation))
            throw Invalid("mutation must be read, create, update or delete.");

        DocumentSpecifier document;
        DocumentLifecycleState state;
        try
        {
            document = NativeDocumentInput.ToNative(Document);
            state = DocumentLifecycleInput.ToNative(ExpectedState);
        }
        catch (AutomationException error)
        {
            throw Invalid(error.Message);
        }
        if (document.Type != DocumentType.DoctypePcb || state.Scope != DocumentLifecycleScope.DlsPcb
            || !state.Document.Equals(document))
            throw Invalid("document and expectedState must identify the same PCB.");

        string? canonicalOperation = null;
        if (Mutation is PcbZoneMutationKind.Create or PcbZoneMutationKind.Update or PcbZoneMutationKind.Delete)
        {
            canonicalOperation = Canonical(OperationId, "operationId");
        }

        string? canonicalZone = null;
        if (Mutation is PcbZoneMutationKind.Read or PcbZoneMutationKind.Delete)
            canonicalZone = Canonical(ZoneId, "zoneId");
        if (Mutation is PcbZoneMutationKind.Create or PcbZoneMutationKind.Update)
        {
            if (Zone is null)
                throw Invalid("zone is required for create and update.");
            if (Mutation == PcbZoneMutationKind.Update && !string.Equals(Zone.Id, Canonical(Zone.Id, "zone.id"), StringComparison.Ordinal))
                throw Invalid("zone.id must be a canonical lowercase UUID for update.");
        }
        if (Mutation is PcbZoneMutationKind.Read or PcbZoneMutationKind.Delete && Zone is not null)
            throw Invalid("zone is not accepted for read or delete.");
        if (Mutation is PcbZoneMutationKind.Create or PcbZoneMutationKind.Update && !string.IsNullOrWhiteSpace(ZoneId))
            throw Invalid("zoneId is not accepted for create or update.");

        return (SchematicJson.Formatter.Format(document), SchematicJson.Formatter.Format(state),
            Mutation, canonicalOperation, canonicalZone, Zone);
    }

    private static string Canonical(string? value, string field)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty
            || !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
            throw Invalid($"{field} must be a canonical lowercase UUID.");
        return value!;
    }

    private static AutomationException Invalid(string message)
        => new("invalid_pcb_zone_typed_input", message);
}

[JsonConverter(typeof(JsonStringEnumConverter<PcbZoneMutationKind>))]
public enum PcbZoneMutationKind
{
    [JsonStringEnumMemberName("read")] Read = 1,
    [JsonStringEnumMemberName("create")] Create = 2,
    [JsonStringEnumMemberName("update")] Update = 3,
    [JsonStringEnumMemberName("delete")] Delete = 4
}
