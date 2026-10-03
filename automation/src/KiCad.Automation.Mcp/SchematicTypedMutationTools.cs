using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.ComponentModel;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Small, typed constructors for the most common schematic connection edits.</summary>
public enum SchematicConnectionItemKind
{
    Wire = 1,
    Bus = 2,
    Junction = 3,
    BusEntry = 4,
    LocalLabel = 5,
    GlobalLabel = 6,
    HierarchicalLabel = 7,
    Text = 8
}

/// <summary>
/// Task-oriented schematic creation over the checked batch protocol. The generic checked batch
/// remains the escape hatch for object types that are not covered here; these methods keep the
/// common wire/label edits typed so an agent does not have to manufacture protobuf Any payloads.
/// </summary>
[McpServerToolType]
public sealed class SchematicTypedMutationTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_schematic_create_connection_item", ReadOnly = false, UseStructuredContent = true),
     Description("Create one explicitly typed schematic connection item (wire, bus, junction, bus entry, local/global/hierarchical label, or text) as one checked native undo step. Supply the exact root sheet documentJson and the complete expectedStateJson returned by kicad_schematic_checked_state, a new canonical itemId and operationId, coordinates in nanometres on 100 nm boundaries, and text for labels/text. Wire and bus require endXNm/endYNm; a bus entry uses those values as its size vector. The native batch is admitted only when process epoch, serialized state, revision and file baselines still match. Reuse the same operationId and identical arguments after a lost reply; a rejected or indeterminate result is not success. The edit is not saved to disk; use kicad_document_save after observing the resulting state."),
     KiCadCapability("schematic-design", "compiled-mcp plus native-api", "explicit root document, complete expected lifecycle state, process epoch, revision, item UUID and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> CreateConnectionItem(string instanceId, string documentJson,
        string expectedStateJson, SchematicConnectionItemKind kind, string itemId,
        long xNm, long yNm, string operationId, CancellationToken cancellationToken,
        long? endXNm = null, long? endYNm = null, string? text = null)
        => Execute(instanceId, documentJson, expectedStateJson, kind, itemId, xNm, yNm,
            endXNm, endYNm, text, operationId, cancellationToken);

    private async Task<CallToolResult> Execute(string instanceId, string documentJson,
        string expectedStateJson, SchematicConnectionItemKind kind, string itemId,
        long xNm, long yNm, long? endXNm, long? endYNm, string? text, string operationId,
        CancellationToken cancellationToken)
    {
        bool submitted = false;
        CheckedSchematicBatch? request = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_checked_document");
            try { DocumentStateTools.ValidateTarget(document); }
            catch (AutomationException error)
            { throw new AutomationException("invalid_checked_document", error.Message); }
            DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_checked_state");
            ValidateIdentity(itemId, "itemId");
            ValidateIdentity(operationId, "operationId");
            ValidateCoordinate(xNm, nameof(xNm));
            ValidateCoordinate(yNm, nameof(yNm));
            if (kind is SchematicConnectionItemKind.Wire or SchematicConnectionItemKind.Bus or SchematicConnectionItemKind.BusEntry)
            {
                if (endXNm is null || endYNm is null)
                    throw new AutomationException("missing_connection_endpoint", "Wire, bus and bus-entry creation require endXNm and endYNm.");
                ValidateCoordinate(endXNm.Value, nameof(endXNm));
                ValidateCoordinate(endYNm.Value, nameof(endYNm));
            }
            if (kind is SchematicConnectionItemKind.LocalLabel or SchematicConnectionItemKind.GlobalLabel
                or SchematicConnectionItemKind.HierarchicalLabel or SchematicConnectionItemKind.Text)
                if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
                    throw new AutomationException("invalid_connection_text", "Labels and text require 1..4096 characters.");

            IMessage item = BuildItem(kind, itemId, xNm, yNm, endXNm, endYNm, text);
            var batch = new ApplySchematicItemBatch
            {
                Document = document.Clone(),
                OperationId = operationId,
                DocumentEpoch = expected.Revision?.Epoch ?? string.Empty,
                ExpectedRevision = expected.Revision?.Clone() ?? new(),
                Description = "Create typed schematic " + kind.ToString().ToLowerInvariant()
            };
            batch.Operations.Add(new SchematicItemOperation { Create = Any.Pack(item) });
            request = new CheckedSchematicBatch { Batch = batch, ExpectedState = expected.Clone() };

            var client = registry.Client(instanceId);
            CheckedSchematicTools.ValidateRequest(request, client.Epoch);
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            var receipt = await client.InvokeAsync<CheckedSchematicBatch, CheckedSchematicBatchReceipt>(request, cancellationToken);
            CheckedSchematicTools.ValidateResult(request, receipt, inspect: false);
            string json = SchematicJson.Formatter.Format(receipt);
            var structured = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                instanceId, kind = kind.ToString(), itemId, operationId,
                receipt = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json),
                mutationSubmitted = submitted, designFilesSaved = false
            });
            return new() { IsError = receipt.Status != CheckedSchematicBatchStatus.CsbsCompleted,
                StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or NativeApiException
            or InvalidProtocolBufferException or InvalidJsonException or NngException)
        {
            string code = error switch
            {
                AutomationException known => known.Code,
                NativeApiException native => "native_status_" + native.Status,
                NngException transport => "transport_status_" + transport.ErrorCode,
                _ => "invalid_checked_schematic_item"
            };
            var structured = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                instanceId, kind = kind.ToString(), itemId, operationId,
                mutationSubmitted = submitted,
                outcome = submitted ? "not_confirmed" : "not_submitted",
                errorCode = code, errorMessage = error.Message
            });
            return new() { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    internal static IMessage BuildItem(SchematicConnectionItemKind kind, string itemId,
        long xNm, long yNm, long? endXNm, long? endYNm, string? text)
    {
        ValidateIdentity(itemId, "itemId");
        ValidateCoordinate(xNm, nameof(xNm));
        ValidateCoordinate(yNm, nameof(yNm));
        var id = new KIID { Value = itemId };
        var position = new Vector2 { XNm = xNm, YNm = yNm };
        var unlocked = LockedState.LsUnlocked;
        return kind switch
        {
            SchematicConnectionItemKind.Wire => new SchematicLine
            {
                Id = id, Start = position, End = Endpoint(endXNm, endYNm),
                Type = SchematicLineType.SltWire, Locked = unlocked
            },
            SchematicConnectionItemKind.Bus => new SchematicLine
            {
                Id = id, Start = position, End = Endpoint(endXNm, endYNm),
                Type = SchematicLineType.SltBus, Locked = unlocked
            },
            SchematicConnectionItemKind.Junction => new Junction
            {
                Id = id, Position = position, Locked = unlocked
            },
            SchematicConnectionItemKind.BusEntry => new BusEntry
            {
                Id = id, Position = position, Size = Endpoint(endXNm, endYNm),
                Type = BusEntryType.BetWireToBus, Locked = unlocked
            },
            SchematicConnectionItemKind.LocalLabel => new LocalLabel
            {
                Id = id, Position = position, Text = Caption(text!),
                SpinStyle = SchematicLabelSpinStyle.SlssRight, Locked = unlocked
            },
            SchematicConnectionItemKind.GlobalLabel => new GlobalLabel
            {
                Id = id, Position = position, Text = Caption(text!),
                SpinStyle = SchematicLabelSpinStyle.SlssRight,
                Shape = SchematicLabelShape.SlshPassive, Locked = unlocked
            },
            SchematicConnectionItemKind.HierarchicalLabel => new HierarchicalLabel
            {
                Id = id, Position = position, Text = Caption(text!),
                SpinStyle = SchematicLabelSpinStyle.SlssRight,
                Shape = SchematicLabelShape.SlshPassive, Locked = unlocked
            },
            SchematicConnectionItemKind.Text => new SchematicText
            {
                Id = id, Text = new Text { Position = position, Text_ = text! }, Locked = unlocked
            },
            _ => throw new AutomationException("invalid_connection_kind", "Choose a supported schematic connection item kind.")
        };
    }

    private static Text Caption(string text) => new() { Text_ = text };

    private static Vector2 Endpoint(long? xNm, long? yNm)
    {
        if (xNm is null || yNm is null)
            throw new AutomationException("missing_connection_endpoint", "This item requires endXNm and endYNm.");
        ValidateCoordinate(xNm.Value, nameof(xNm));
        ValidateCoordinate(yNm.Value, nameof(yNm));
        return new() { XNm = xNm.Value, YNm = yNm.Value };
    }

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static void ValidateIdentity(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new AutomationException("invalid_identity", name + " must be a canonical nonempty UUID.");
    }

    private static void ValidateCoordinate(long value, string name)
    {
        if (value % 100 != 0)
            throw new AutomationException("invalid_coordinate", name + " must be a multiple of 100 nm.");
    }
}
