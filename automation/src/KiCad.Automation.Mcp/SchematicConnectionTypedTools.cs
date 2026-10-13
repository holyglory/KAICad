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

/// <summary>Structured admission boundary for checked schematic wire and label operations.</summary>
[McpServerToolType]
public sealed class SchematicConnectionTypedTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_connection_mutation_typed"),
     Description("Read or mutate one checked schematic wire, bus, junction, bus entry, label or text item from one structured input. input binds an explicit root schematic document, the complete checked schematic observation returned by kicad_schematic_checked_state (lifecycle state plus electrical hierarchy), mutation kind, item kind and identity, operation UUID and optional typed coordinates/text. The existing connection-item handlers remain the execution and receipt authority; this boundary rejects incomplete or stale identities before invoking them."),
     KiCadCapability("schematic-design", "compiled-mcp plus-native-api", "typed root document, complete checked state with electrical hierarchy, connection kind, item UUID and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicConnectionTypedMcpProcessTests.TypedConnectionMutationPublishesStructuredSchemaAndRejectsIncompleteInput", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> Mutate(string instanceId, SchematicConnectionMutationInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            if (input is null)
                throw Invalid("input is required.");
            var payload = input.ToPayload();
            var legacy = new SchematicTypedMutationTools(registry);
            return payload.Mutation switch
            {
                SchematicConnectionMutationKind.Read => await legacy.ReadConnectionItem(instanceId, payload.Document,
                    payload.ItemId, payload.ExpectedState, cancellationToken, payload.ExpectedKind),
                SchematicConnectionMutationKind.Create => await legacy.CreateConnectionItem(instanceId, payload.Document,
                    payload.ExpectedState, payload.Kind, payload.ItemId, payload.XNm!.Value, payload.YNm!.Value,
                    payload.OperationId!, cancellationToken, payload.EndXNm, payload.EndYNm, payload.Text),
                SchematicConnectionMutationKind.Update => await legacy.UpdateConnectionItem(instanceId, payload.Document,
                    payload.ItemId, payload.ExpectedState, payload.OperationId!, cancellationToken, payload.XNm,
                    payload.YNm, payload.EndXNm, payload.EndYNm, payload.Text, payload.ExpectedKind),
                SchematicConnectionMutationKind.Delete => await legacy.DeleteConnectionItem(instanceId, payload.Document,
                    payload.ItemId, payload.ExpectedState, payload.OperationId!, cancellationToken),
                _ => throw Invalid("mutation must be read, create, update or delete.")
            };
        }
        catch (AutomationException error)
        {
            return Error(error.Code, error.Message);
        }
        catch (JsonException error)
        {
            return Error("invalid_schematic_connection_typed_input", error.Message);
        }
    }

    private static AutomationException Invalid(string message)
        => new("invalid_schematic_connection_typed_input", message);

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
public sealed class SchematicConnectionMutationInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private CheckedSchematicObservationInput? expectedState;
    private SchematicConnectionMutationKind mutation;
    private SchematicConnectionItemKind? kind;
    private string? itemId;
    private string? operationId;
    private long? xNm;
    private long? yNm;
    private long? endXNm;
    private long? endYNm;
    private string? text;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    [Description("The complete checked schematic observation: state and electrical hierarchy from one kicad_schematic_checked_state response. All hierarchy items, repeated fields and Any-packed payloads are retained.")]
    public CheckedSchematicObservationInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }
    public SchematicConnectionMutationKind Mutation { get => mutation; set => mutation = BindOnce(value); }
    public SchematicConnectionItemKind? Kind { get => kind; set => kind = BindOnce(value); }
    public string? ItemId { get => itemId; set => itemId = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long? XNm { get => xNm; set => xNm = BindOnce(value); }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long? YNm { get => yNm; set => yNm = BindOnce(value); }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long? EndXNm { get => endXNm; set => endXNm = BindOnce(value); }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long? EndYNm { get => endYNm; set => endYNm = BindOnce(value); }
    public string? Text { get => text; set => text = BindOnce(value); }

    internal (string Document, string ExpectedState, SchematicConnectionMutationKind Mutation,
        SchematicConnectionItemKind Kind, SchematicConnectionItemKind? ExpectedKind, string ItemId,
        string? OperationId, long? XNm, long? YNm, long? EndXNm, long? EndYNm, string? Text) ToPayload()
    {
        if (Document is null || ExpectedState is null)
            throw Invalid("document and expectedState are required.");
        if (!Enum.IsDefined(Mutation) || (Mutation != SchematicConnectionMutationKind.Delete && Kind is null))
            throw Invalid("mutation and kind must be defined.");
        if (!Guid.TryParseExact(ItemId, "D", out Guid item) || item == Guid.Empty
            || !string.Equals(ItemId, item.ToString("D"), StringComparison.Ordinal))
            throw Invalid("itemId must be a canonical lowercase UUID.");
        DocumentSpecifier document;
        CheckedSchematicState state;
        try
        {
            document = NativeDocumentInput.ToNative(Document);
            state = ExpectedState.ToNative();
            CheckedSchematicContract.ValidateObservation(state, document, state.State.ProcessEpoch);
        }
        catch (AutomationException error)
        {
            throw Invalid(error.Message);
        }
        if (document.Type != DocumentType.DoctypeSchematic || state.State.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
            || !state.State.Document.Equals(document) || !state.Electrical.Hierarchy.Data.Document.Equals(document))
            throw Invalid("document and expectedState must identify the same schematic root.");
        if (Mutation is SchematicConnectionMutationKind.Create or SchematicConnectionMutationKind.Update or SchematicConnectionMutationKind.Delete)
        {
            if (!Guid.TryParseExact(OperationId, "D", out Guid operation) || operation == Guid.Empty
                || !string.Equals(OperationId, operation.ToString("D"), StringComparison.Ordinal))
                throw Invalid("operationId must be a canonical lowercase UUID.");
        }
        if (Mutation == SchematicConnectionMutationKind.Create && (XNm is null || YNm is null))
            throw Invalid("create requires xNm and yNm.");
        if (Mutation is SchematicConnectionMutationKind.Create or SchematicConnectionMutationKind.Update)
        {
            if ((XNm is null) != (YNm is null) || (EndXNm is null) != (EndYNm is null))
                throw Invalid("coordinate pairs must be supplied together.");
            if (new[] { XNm, YNm, EndXNm, EndYNm }.Where(value => value.HasValue).Any(value => value!.Value % 100 != 0))
                throw Invalid("coordinates must lie on 100 nm boundaries.");
        }
        SchematicConnectionItemKind parsedKind = Kind ?? SchematicConnectionItemKind.Wire;
        return (SchematicJson.Formatter.Format(document), ExpectedState.ToJson(), Mutation, parsedKind,
            Kind, ItemId!,
            Mutation == SchematicConnectionMutationKind.Read ? null : OperationId,
            XNm, YNm, EndXNm, EndYNm, Text);
    }

    private static AutomationException Invalid(string message)
        => new("invalid_schematic_connection_typed_input", message);
}

[JsonConverter(typeof(JsonStringEnumConverter<SchematicConnectionMutationKind>))]
public enum SchematicConnectionMutationKind
{
    [JsonStringEnumMemberName("read")] Read = 1,
    [JsonStringEnumMemberName("create")] Create = 2,
    [JsonStringEnumMemberName("update")] Update = 3,
    [JsonStringEnumMemberName("delete")] Delete = 4
}
