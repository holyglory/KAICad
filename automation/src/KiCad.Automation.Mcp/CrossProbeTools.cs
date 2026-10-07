using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Typed schematic cross-probe operations.  The native handlers change editor
/// selection/highlight state only; the lifecycle checkpoint proves that they do
/// not modify schematic design data.
/// </summary>
[McpServerToolType]
public sealed class CrossProbeTools(InstanceRegistry registry)
{
    private static readonly SemaphoreSlim ReceiptGate = new(1, 1);
    private static readonly Dictionary<string, StoredReceipt> Receipts = new(StringComparer.Ordinal);

    [McpServerTool(Name = "kicad_schematic_sync_selection", ReadOnly = false),
     Description("Synchronize typed schematic selections in the attached KiCad editor. document identifies an explicit schematic hierarchy; selection carries a focus item and footprint, pad or sheet-path selections; expectedStateJson must be the exact lifecycle checkpoint including process epoch and document revision; operationId enables replay without resubmitting a completed GUI action. This changes editor selection only and never claims a schematic design mutation."),
     KiCadCapability("schematic-operations", "compiled-mcp plus native-gui-api", "typed schematic selection, explicit document, process epoch, lifecycle revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicCrossProbeMcpProcessTests.TypedSchematicCrossProbeToolsAreDiscoverableAndRefuseMalformedTargets")]
    public Task<CallToolResult> SyncSelection(string instanceId, NativeDocumentInput document,
        SchematicCrossProbeSelectionInput selection, string expectedStateJson, Guid operationId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier target = ParseTarget(document);
        Kiapi.Common.Commands.SyncSelection request = selection?.ToNative()
            ?? throw new AutomationException("invalid_schematic_cross_probe_request", "selection is required.");
        string requestJson = SchematicJson.Formatter.Format(request);
        string targetJson = SchematicJson.Formatter.Format(target);
        return await WithReceipt(instanceId, target, operationId, "sync-selection", requestJson,
            expectedStateJson, () => DispatchSelection(instanceId, target, request,
                expectedStateJson, operationId, cancellationToken));
    });

    [McpServerTool(Name = "kicad_schematic_highlight_nets", ReadOnly = false),
     Description("Highlight typed net names in the attached KiCad schematic editor. document identifies an explicit schematic hierarchy; highlight carries a bounded nonempty net-name list; expectedStateJson must be the exact lifecycle checkpoint including process epoch and document revision; operationId enables replay without resubmitting a completed GUI action. This changes editor highlighting only and never claims a schematic design mutation."),
     KiCadCapability("schematic-operations", "compiled-mcp plus native-gui-api", "typed schematic net names, explicit document, process epoch, lifecycle revision and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicCrossProbeMcpProcessTests.TypedSchematicCrossProbeToolsAreDiscoverableAndRefuseMalformedTargets")]
    public Task<CallToolResult> HighlightNets(string instanceId, NativeDocumentInput document,
        SchematicCrossProbeHighlightInput highlight, string expectedStateJson, Guid operationId,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier target = ParseTarget(document);
        Kiapi.Common.Commands.HighlightNets request = highlight?.ToNative()
            ?? throw new AutomationException("invalid_schematic_cross_probe_request", "highlight is required.");
        string requestJson = SchematicJson.Formatter.Format(request);
        return await WithReceipt(instanceId, target, operationId, "highlight-nets", requestJson,
            expectedStateJson, () => DispatchHighlight(instanceId, target, request,
                expectedStateJson, operationId, cancellationToken));
    });

    [McpServerTool(Name = "kicad_schematic_cross_probe_receipt", ReadOnly = true),
     Description("Read a process-owned schematic cross-probe receipt without resubmitting the GUI action. The receipt is bound to the exact instance, process epoch and schematic document supplied at submission; an absent receipt is reported honestly and does not imply that an unknown native request was safe to retry."),
     KiCadCapability("schematic-operations", "mcp-process-receipt", "operation UUID, explicit schematic document, process epoch and receipt identity"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "SchematicCrossProbeMcpProcessTests.TypedSchematicCrossProbeToolsAreDiscoverableAndRefuseMalformedTargets")]
    public Task<CallToolResult> Receipt(string instanceId, NativeDocumentInput document,
        string expectedProcessEpoch, Guid operationId, CancellationToken cancellationToken) => Execute(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier target = ParseTarget(document);
        ValidateEpoch(expectedProcessEpoch, "invalid_process_epoch");
        ValidateOperationId(operationId);
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new AutomationException("invalid_instance_id", "instanceId is required to inspect a schematic cross-probe receipt.");

        ReceiptGate.Wait(cancellationToken);
        try
        {
            if (!Receipts.TryGetValue(ReceiptKey(instanceId, operationId), out StoredReceipt? stored))
                return Task.FromResult(Data(new
                {
                    instanceId, processEpoch = expectedProcessEpoch, operationId = operationId.ToString("D"),
                    document = target, found = false, receiptScope = "mcp-process",
                    nativeReceiptAvailable = false,
                    receiptObservation = "not-found",
                    retrySafety = "unknown-native-outcome"
                }));

            if (!string.Equals(stored.InstanceId, instanceId, StringComparison.Ordinal))
                throw new AutomationException("wrong_instance_receipt",
                    "The schematic cross-probe receipt belongs to a different MCP instance.");
            if (!string.Equals(stored.ProcessEpoch, expectedProcessEpoch, StringComparison.Ordinal))
                throw new AutomationException("stale_process_epoch",
                    "The schematic cross-probe receipt belongs to a different native process epoch.");
            if (!stored.Document.Equals(target))
                throw new AutomationException("wrong_document_receipt",
                    "The schematic cross-probe receipt belongs to a different document.");

            return Task.FromResult(Data(new
            {
                instanceId, processEpoch = expectedProcessEpoch, operationId = operationId.ToString("D"),
                document = target, found = true, receiptScope = "mcp-process",
                nativeReceiptAvailable = false, receiptObservation = "process-cache",
                receipt = stored.Result.StructuredContent ?? JsonSerializer.SerializeToElement(new { })
            }));
        }
        finally { ReceiptGate.Release(); }
    });

    private async Task<CallToolResult> DispatchSelection(string instanceId, DocumentSpecifier document,
        Kiapi.Common.Commands.SyncSelection request, string expectedStateJson, Guid operationId,
        CancellationToken cancellationToken)
    {
        (NativeClient client, AutomationSession session, DocumentLifecycleState before) =
            await Admit(instanceId, document, expectedStateJson, "selection", cancellationToken);
        SyncSelectionResponse response;
        try
        {
            response = await client.InvokeAsync<Kiapi.Common.Commands.SyncSelection,
                SyncSelectionResponse>(request, cancellationToken);
        }
        catch (NngException error)
        {
            throw new AutomationException(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                error.RequestDelivered
                    ? $"KiCad received schematic cross-probe selection {operationId:D} but did not answer; inspect the editor before retrying."
                    : $"KiCad did not receive schematic cross-probe selection {operationId:D}.");
        }

        DocumentLifecycleState after = await ReadAfter(client, document, "selection", operationId,
            cancellationToken);
        RequireSame(before, after, "selection");
        bool accepted = response.Status == CrossProbeStatus.CpsOk;
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operationId = operationId.ToString("D"),
            operation = "sync-selection", document,
            beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after),
            response = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response)),
            nativeGuiOnly = true, designMutation = false, selectionChanged = accepted,
            operationReceipt = true, receiptScope = "mcp-process", nativeReceiptAvailable = false,
            operationOutcome = accepted ? "completed" : "rejected",
            retrySafety = "same-operation-id-replays-receipt"
        }, !accepted);
    }

    private async Task<CallToolResult> DispatchHighlight(string instanceId, DocumentSpecifier document,
        Kiapi.Common.Commands.HighlightNets request, string expectedStateJson, Guid operationId,
        CancellationToken cancellationToken)
    {
        (NativeClient client, AutomationSession session, DocumentLifecycleState before) =
            await Admit(instanceId, document, expectedStateJson, "net highlight", cancellationToken);
        HighlightNetsResponse response;
        try
        {
            response = await client.InvokeAsync<Kiapi.Common.Commands.HighlightNets,
                HighlightNetsResponse>(request, cancellationToken);
        }
        catch (NngException error)
        {
            throw new AutomationException(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                error.RequestDelivered
                    ? $"KiCad received schematic net highlight {operationId:D} but did not answer; inspect the editor before retrying."
                    : $"KiCad did not receive schematic net highlight {operationId:D}.");
        }

        DocumentLifecycleState after = await ReadAfter(client, document, "net highlight", operationId,
            cancellationToken);
        RequireSame(before, after, "net highlight");
        bool accepted = response.Status == CrossProbeStatus.CpsOk;
        return Data(new
        {
            instanceId, processEpoch = session.Epoch, operationId = operationId.ToString("D"),
            operation = "highlight-nets", document,
            beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after),
            response = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(response)),
            nativeGuiOnly = true, designMutation = false, highlightChanged = accepted,
            operationReceipt = true, receiptScope = "mcp-process", nativeReceiptAvailable = false,
            operationOutcome = accepted ? "completed" : "rejected",
            retrySafety = "same-operation-id-replays-receipt"
        }, !accepted);
    }

    private async Task<(NativeClient Client, AutomationSession Session, DocumentLifecycleState Before)> Admit(
        string instanceId, DocumentSpecifier document, string expectedStateJson, string operation,
        CancellationToken cancellationToken)
    {
        DocumentLifecycleState expected = ParseState(expectedStateJson);
        ValidateExpected(document, expected);
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (!string.Equals(session.Epoch, expected.ProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("schematic_cross_probe_instance_changed",
                "The expected schematic state belongs to another native process epoch.");
        DocumentLifecycleState before;
        try
        {
            before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
        }
        catch (NngException error)
        {
            throw new AutomationException(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                $"KiCad could not provide the schematic lifecycle state before {operation}: {error.Message}");
        }
        ValidateObserved(document, before);
        if (!before.Equals(expected))
            throw new AutomationException("schematic_cross_probe_state_changed",
                $"The schematic changed before {operation}; inspect a fresh lifecycle state and retry.");
        return (client, session, before);
    }

    private static async Task<DocumentLifecycleState> ReadAfter(NativeClient client, DocumentSpecifier document,
        string operation, Guid operationId, CancellationToken cancellationToken)
    {
        try
        {
            return await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
                new() { Document = document }, cancellationToken);
        }
        catch (NngException error)
        {
            throw new AutomationException(error.RequestDelivered ? "operation_outcome_unknown" : "native_not_reached",
                error.RequestDelivered
                    ? $"KiCad accepted schematic cross-probe {operation} {operationId:D} but lifecycle verification was lost; inspect the editor before retrying."
                    : $"KiCad did not receive lifecycle verification for schematic cross-probe {operation} {operationId:D}.");
        }
    }

    private static void RequireSame(DocumentLifecycleState before, DocumentLifecycleState after, string operation)
    {
        ValidateObserved(before.Document, after);
        if (!before.Equals(after))
            throw new AutomationException("schematic_cross_probe_state_changed",
                $"The schematic lifecycle changed while {operation} ran; discard the GUI result.");
    }

    private static DocumentSpecifier ParseTarget(NativeDocumentInput input)
    {
        try
        {
            DocumentSpecifier target = NativeDocumentInput.ToNative(input);
            if (target.Type != DocumentType.DoctypeSchematic)
                throw new AutomationException("invalid_schematic_cross_probe_target",
                    "The cross-probe target must be an explicit schematic document.");
            DocumentStateTools.ValidateTarget(target);
            return target;
        }
        catch (AutomationException error) when (error.Code is "invalid_document_target" or "unsupported_document_type")
        {
            throw new AutomationException("invalid_schematic_cross_probe_target", error.Message);
        }
    }

    private static DocumentLifecycleState ParseState(string json)
    {
        try { return SchematicJson.Parser.Parse<DocumentLifecycleState>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException
            or InvalidOperationException or ArgumentException)
        { throw new AutomationException("invalid_schematic_cross_probe_state", "expectedStateJson is invalid protobuf JSON: " + error.Message); }
    }

    private static void ValidateExpected(DocumentSpecifier document, DocumentLifecycleState expected)
    {
        if (expected.Document is null || !expected.Document.Equals(document)
            || expected.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
            || !CanonicalUuid(expected.ProcessEpoch) || expected.Revision is null
            || !CanonicalUuid(expected.Revision.Epoch))
            throw new AutomationException("invalid_schematic_cross_probe_state",
                "expectedStateJson must identify the same schematic, process epoch and document revision.");
        ValidateStateIdentity(expected, "invalid_schematic_cross_probe_state");
    }

    private static void ValidateObserved(DocumentSpecifier? document, DocumentLifecycleState state)
    {
        if (document is null || state.Document is null || !state.Document.Equals(document)
            || state.Scope != DocumentLifecycleScope.DlsSchematicHierarchy
            || !CanonicalUuid(state.ProcessEpoch) || state.Revision is null
            || !CanonicalUuid(state.Revision.Epoch))
            throw new AutomationException("invalid_schematic_cross_probe_state",
                "Native lifecycle state did not provide the requested schematic, process epoch or revision.");
        ValidateStateIdentity(state, "invalid_schematic_cross_probe_state");
    }

    private static void ValidateStateIdentity(DocumentLifecycleState state, string code)
    {
        if (!CanonicalUuid(state.NativeIdentity) || state.StateSha256.Length != 64
            || !state.StateSha256.All(char.IsAsciiHexDigitLower))
            throw new AutomationException(code,
                "The schematic lifecycle checkpoint must include a native identity and lowercase SHA-256 state digest.");
    }

    private static async Task<CallToolResult> WithReceipt(string instanceId, DocumentSpecifier document,
        Guid operationId, string operation, string requestJson, string expectedStateJson,
        Func<Task<CallToolResult>> action)
    {
        ValidateOperationId(operationId);
        string operationKey = ReceiptKey(instanceId, operationId);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            instanceId + "\n" + operation + "\n" + SchematicJson.Formatter.Format(document)
            + "\n" + requestJson + "\n" + expectedStateJson)));
        await ReceiptGate.WaitAsync();
        try
        {
            if (Receipts.TryGetValue(operationKey, out StoredReceipt? existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AutomationException("operation_id_reused",
                        "The operation ID is already bound to a different schematic cross-probe request.");
                return existing.Result;
            }

            CallToolResult result = await action();
            string epoch = result.StructuredContent?.TryGetProperty("processEpoch", out JsonElement process)
                == true ? process.GetString() ?? "" : "";
            Receipts[operationKey] = new(fingerprint, result, instanceId, epoch, document.Clone());
            return result;
        }
        finally { ReceiptGate.Release(); }
    }

    private static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new AutomationException("invalid_operation_id", "Provide a nonempty operation UUID for schematic cross-probe.");
    }

    private static void ValidateEpoch(string value, string code)
    {
        if (!CanonicalUuid(value))
            throw new AutomationException(code, "Provide a canonical native process epoch UUID.");
    }

    private static bool CanonicalUuid(string? value) =>
        Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    private static string ReceiptKey(string instanceId, Guid operationId) =>
        instanceId + "\n" + operationId.ToString("D");

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException
            or ArgumentException or JsonException)
        {
            string code = error is AutomationException known ? known.Code
                : error is NativeApiException native ? "native_status_" + native.Status
                : error is NngException ? "native_transport_unavailable"
                : "schematic_cross_probe_failed";
            return Error(code, error.Message);
        }
    }

    private static CallToolResult Data(object value, bool error = false)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new CallToolResult
        {
            IsError = error,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private static CallToolResult Error(string code, string message) => Data(new { errorCode = code,
        errorMessage = message, mutationSubmitted = false }, true);

    private sealed record StoredReceipt(string Fingerprint, CallToolResult Result, string InstanceId,
        string ProcessEpoch, DocumentSpecifier Document);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicCrossProbeSelectionInput : NativeDocumentInputObject
{
    private SchematicCrossProbeContext context;
    private SchematicCrossProbeMode mode;
    private SchematicCrossProbeSelectionItemInput? focusItem;
    private List<SchematicCrossProbeSelectionItemInput?>? items;

    [Description("Whether this is an implicit selection-sync event or an explicit user-requested sync.")]
    public SchematicCrossProbeContext Context { get => context; set => context = BindOnce(value); }
    [Description("Whether to synchronize selected items only or selected items and their nets.")]
    public SchematicCrossProbeMode Mode { get => mode; set => mode = BindOnce(value); }
    public SchematicCrossProbeSelectionItemInput? FocusItem { get => focusItem; set => focusItem = BindOnce(value); }
    [Description("Selections to synchronize. An intentionally empty list clears the native schematic selection.")]
    public List<SchematicCrossProbeSelectionItemInput?>? Items { get => items; set => items = BindOnce(value); }

    internal Kiapi.Common.Commands.SyncSelection ToNative()
    {
        if (Context is not (SchematicCrossProbeContext.Implicit or SchematicCrossProbeContext.Explicit))
            throw new AutomationException("invalid_schematic_cross_probe_request", "context must be SSC_IMPLICIT or SSC_EXPLICIT.");
        if (Mode is not (SchematicCrossProbeMode.ItemsOnly or SchematicCrossProbeMode.ItemsAndNets))
            throw new AutomationException("invalid_schematic_cross_probe_request", "mode must be SSM_ITEMS_ONLY or SSM_ITEMS_AND_NETS.");
        if (Items is { Count: > 4096 })
            throw new AutomationException("invalid_schematic_cross_probe_request", "items must contain at most 4096 selections.");

        var result = new Kiapi.Common.Commands.SyncSelection
        {
            Context = Context == SchematicCrossProbeContext.Implicit
                ? SyncSelectionContext.SscImplicit : SyncSelectionContext.SscExplicit,
            Mode = Mode == SchematicCrossProbeMode.ItemsOnly
                ? SyncSelectionMode.SsmItemsOnly : SyncSelectionMode.SsmItemsAndNets
        };
        if (FocusItem is not null)
            result.FocusItem = FocusItem.ToNative("focusItem");
        if (Items is not null)
            foreach ((SchematicCrossProbeSelectionItemInput? item, int index) in Items.Select((item, index) => (item, index)))
            {
                if (item is null)
                    throw new AutomationException("invalid_schematic_cross_probe_request", $"items[{index}] is required.");
                result.Items.Add(item.ToNative($"items[{index}]"));
            }
        return result;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<SchematicCrossProbeContext>))]
public enum SchematicCrossProbeContext
{
    [JsonStringEnumMemberName("SSC_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("SSC_IMPLICIT")] Implicit = 1,
    [JsonStringEnumMemberName("SSC_EXPLICIT")] Explicit = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<SchematicCrossProbeMode>))]
public enum SchematicCrossProbeMode
{
    [JsonStringEnumMemberName("SSM_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("SSM_ITEMS_ONLY")] ItemsOnly = 1,
    [JsonStringEnumMemberName("SSM_ITEMS_AND_NETS")] ItemsAndNets = 2
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicCrossProbeSelectionItemInput : NativeDocumentInputObject
{
    private SchematicCrossProbeFootprintInput? footprint;
    private SchematicCrossProbePadInput? pad;
    private List<string?>? sheetPath;

    public SchematicCrossProbeFootprintInput? Footprint { get => footprint; set => footprint = BindOnce(value); }
    public SchematicCrossProbePadInput? Pad { get => pad; set => pad = BindOnce(value); }
    [Description("Canonical UUIDs from root to the selected schematic sheet instance.")]
    public List<string?>? SheetPath { get => sheetPath; set => sheetPath = BindOnce(value); }

    internal SelectionSpec ToNative(string field)
    {
        int selected = (Footprint is null ? 0 : 1) + (Pad is null ? 0 : 1) + (SheetPath is null ? 0 : 1);
        if (selected != 1)
            throw new AutomationException("invalid_schematic_cross_probe_request", $"{field} must name exactly one footprint, pad or sheet path.");
        if (Footprint is not null)
        {
            if (string.IsNullOrWhiteSpace(Footprint.Reference))
                throw new AutomationException("invalid_schematic_cross_probe_request", $"{field}.footprint.reference is required.");
            return new SelectionSpec { Footprint = new FootprintSelectionSpec { Reference = Footprint.Reference } };
        }
        if (Pad is not null)
        {
            if (string.IsNullOrWhiteSpace(Pad.Reference) || string.IsNullOrWhiteSpace(Pad.Number))
                throw new AutomationException("invalid_schematic_cross_probe_request", $"{field}.pad.reference and number are required.");
            return new SelectionSpec { Pad = new PadSelectionSpec { Reference = Pad.Reference, Number = Pad.Number } };
        }
        if (SheetPath is null || SheetPath.Count == 0 || SheetPath.Any(id => !CanonicalUuid(id)))
            throw new AutomationException("invalid_schematic_cross_probe_request", $"{field}.sheetPath must contain canonical nonempty UUIDs.");
        var spec = new SelectionSpec { SheetPath = new() };
        spec.SheetPath.Path.Add(SheetPath.Select(id => new KIID { Value = id! }));
        return spec;
    }

    private static bool CanonicalUuid(string? value) =>
        Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicCrossProbeFootprintInput : NativeDocumentInputObject
{
    private string? reference;
    public string? Reference { get => reference; set => reference = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicCrossProbePadInput : NativeDocumentInputObject
{
    private string? reference;
    private string? number;
    public string? Reference { get => reference; set => reference = BindOnce(value); }
    public string? Number { get => number; set => number = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SchematicCrossProbeHighlightInput : NativeDocumentInputObject
{
    private List<string?>? netNames;

    [Description("Names of schematic nets to highlight. Use an empty list to clear highlighting.")]
    public List<string?>? NetNames { get => netNames; set => netNames = BindOnce(value); }

    internal Kiapi.Common.Commands.HighlightNets ToNative()
    {
        if (NetNames is { Count: > 4096 } || NetNames?.Any(string.IsNullOrWhiteSpace) == true)
            throw new AutomationException("invalid_schematic_cross_probe_request", "netNames must contain at most 4096 nonempty names.");
        return new Kiapi.Common.Commands.HighlightNets { NetName = { NetNames?.Where(name => name is not null).Select(name => name!) ?? [] } };
    }
}
