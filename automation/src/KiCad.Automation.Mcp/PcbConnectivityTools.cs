using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Commands;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Task-oriented, read-only PCB connectivity queries.
///
/// The native board API returns Any-packed objects because it is also used for
/// round-tripping edits. This wrapper deliberately exposes only the stable,
/// connectivity fields needed by a planner. An unexpected native object or an
/// invalid identity is rejected instead of being passed through as an opaque
/// payload.
/// </summary>
[McpServerToolType]
public sealed class PcbConnectivityTools(InstanceRegistry registry)
{
    private static readonly KiCadObjectType[] ConnectivityTypes =
    [
        KiCadObjectType.KotPcbPad,
        KiCadObjectType.KotPcbTrace,
        KiCadObjectType.KotPcbArc,
        KiCadObjectType.KotPcbVia,
        KiCadObjectType.KotPcbZone
    ];

    [McpServerTool(Name = "kicad_pcb_connectivity_query", ReadOnly = true),
     Description("Read the native copper items on one explicitly named PCB net at one exact lifecycle checkpoint. documentJson identifies the open board and expectedStateJson must be the matching kicad_document_state observation. The result contains deterministic typed pad, track, arc, via and zone summaries with canonical identities; it never passes through native Any payloads, infers DRC or changes the board."),
     KiCadCapability("pcb-connectivity", "compiled-mcp plus native-board-api", "explicit PCB document, process epoch, lifecycle state, exact net name"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Query(string instanceId, string documentJson, string netName,
        string expectedStateJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = ParseDocument(documentJson);
        ValidateNetName(netName);
        DocumentLifecycleState expected = ParseState(expectedStateJson, document);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (!string.Equals(session.Epoch, expected.ProcessEpoch, StringComparison.Ordinal))
            throw new AutomationException("pcb_instance_changed", "The expected PCB state belongs to another native process epoch.");

        DocumentLifecycleState before = await ReadState(client, document, cancellationToken);
        RequireState(before, expected, "The PCB changed before the connectivity query; inspect a fresh lifecycle state and retry.");

        var request = new GetItemsByNet
        {
            Header = new ItemHeader { Document = document }
        };
        request.Types_.Add(ConnectivityTypes);
        request.Nets.Add(new Net { Name = netName });
        GetItemsResponse native = await client.InvokeAsync<GetItemsByNet, GetItemsResponse>(request, cancellationToken);
        PcbConnectivityItem[] items = Normalize(native.Items);

        DocumentLifecycleState after = await ReadState(client, document, cancellationToken);
        RequireState(after, expected, "The board changed while reading connectivity; discard this snapshot.");

        return Data(new
        {
            instanceId,
            processEpoch = session.Epoch,
            document,
            query = "net",
            netName,
            beforeState = SchematicJson.Formatter.Format(before),
            afterState = SchematicJson.Formatter.Format(after),
            nativeStatus = native.Status.ToString(),
            itemCount = items.Length,
            items,
            nativeQuery = true,
            boardMutation = false,
            drcValidated = false
        });
    });

    /// <summary>
    /// Converts a native response into the bounded typed surface returned by
    /// the task-oriented query. Kept internal so managed contract tests can
    /// exercise ordering and fail-closed behavior without a native process.
    /// </summary>
    internal static PcbConnectivityItem[] Normalize(IEnumerable<Any> packedItems)
    {
        var items = packedItems.Select(NormalizeItem).ToArray();
        var duplicate = items.GroupBy(item => (item.Kind, item.Id))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new AutomationException("duplicate_pcb_connectivity_item", $"Native connectivity returned duplicate {duplicate.Key.Kind} identity {duplicate.Key.Id}.");

        return items.OrderBy(item => ItemKindOrder(item.Kind))
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static PcbConnectivityItem NormalizeItem(Any packed)
    {
        if (packed.Is(Pad.Descriptor))
        {
            Pad item = packed.Unpack<Pad>();
            return new PcbConnectivityItem(
                "pad", CanonicalId(item.Id, "pad"), ParentId(item.Parent), NetName(item.Net),
                null, item.Number, null);
        }

        if (packed.Is(Track.Descriptor))
        {
            Track item = packed.Unpack<Track>();
            return new PcbConnectivityItem(
                "track", CanonicalId(item.Id, "track"), ParentId(item.Parent), NetName(item.Net),
                LayerName(item.Layer), null, null);
        }

        if (packed.Is(Arc.Descriptor))
        {
            Arc item = packed.Unpack<Arc>();
            return new PcbConnectivityItem(
                "arc", CanonicalId(item.Id, "arc"), ParentId(item.Parent), NetName(item.Net),
                LayerName(item.Layer), null, null);
        }

        if (packed.Is(Via.Descriptor))
        {
            Via item = packed.Unpack<Via>();
            return new PcbConnectivityItem(
                "via", CanonicalId(item.Id, "via"), ParentId(item.Parent), NetName(item.Net),
                null, null, Layers(item.PadStack?.Layers));
        }

        if (packed.Is(Zone.Descriptor))
        {
            Zone item = packed.Unpack<Zone>();
            string? zoneNet = item.SettingsCase == Zone.SettingsOneofCase.CopperSettings
                ? NetName(item.CopperSettings.Net) : null;
            return new PcbConnectivityItem(
                "zone", CanonicalId(item.Id, "zone"), ParentId(item.Parent), zoneNet,
                null, null, Layers(item.Layers));
        }

        throw new AutomationException("unsupported_pcb_connectivity_item",
            $"The native connectivity query returned an unsupported item type '{packed.TypeUrl}'.");
    }

    private static int ItemKindOrder(string kind) => kind switch
    {
        "pad" => 0,
        "track" => 1,
        "arc" => 2,
        "via" => 3,
        "zone" => 4,
        _ => throw new AutomationException("unsupported_pcb_connectivity_item", $"Unsupported connectivity item kind '{kind}'.")
    };

    private static string CanonicalId(KIID? value, string kind)
    {
        string id = value?.Value ?? "";
        if (!Guid.TryParseExact(id, "D", out Guid parsed) || parsed == Guid.Empty)
            throw new AutomationException("invalid_pcb_connectivity_item_identity",
                $"Native {kind} connectivity item did not carry a canonical KIID.");
        return parsed.ToString("D");
    }

    private static string? ParentId(KIID? value)
    {
        string id = value?.Value ?? "";
        if (id.Length == 0)
            return null;
        if (!Guid.TryParseExact(id, "D", out Guid parsed) || parsed == Guid.Empty)
            throw new AutomationException("invalid_pcb_connectivity_parent_identity",
                "Native connectivity item carried a non-canonical parent KIID.");
        return parsed.ToString("D");
    }

    private static string? NetName(Net? value) =>
        string.IsNullOrEmpty(value?.Name) ? null : value.Name;

    private static string? LayerName(BoardLayer layer) =>
        System.Enum.IsDefined(layer) ? layer.ToString() : null;

    private static string[]? Layers(IEnumerable<BoardLayer>? values)
    {
        if (values is null)
            return null;
        BoardLayer[] layers = values.ToArray();
        if (layers.Length == 0)
            return null;
        if (layers.Any(layer => !System.Enum.IsDefined(layer)))
            return null;
        return layers.Select(layer => layer.ToString()).Order(StringComparer.Ordinal).ToArray();
    }

    private static DocumentSpecifier ParseDocument(string documentJson)
    {
        try
        {
            DocumentSpecifier document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
            DocumentStateTools.ValidateTarget(document);
            if (document.Type != DocumentType.DoctypePcb)
                throw new AutomationException("invalid_pcb_connectivity_target", "The query target must be an explicit PCB document.");
            return document;
        }
        catch (AutomationException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        {
            throw new AutomationException("invalid_pcb_connectivity_target", "documentJson is not a valid PCB document target.");
        }
    }

    private static DocumentLifecycleState ParseState(string stateJson, DocumentSpecifier document)
    {
        try
        {
            DocumentLifecycleState state = SchematicJson.Parser.Parse<DocumentLifecycleState>(stateJson);
            if (state.Scope != DocumentLifecycleScope.DlsPcb || !state.Document.Equals(document)
                || string.IsNullOrWhiteSpace(state.ProcessEpoch))
                throw new AutomationException("invalid_pcb_connectivity_state", "expectedStateJson must identify the same PCB and native process epoch.");
            return state;
        }
        catch (AutomationException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        {
            throw new AutomationException("invalid_pcb_connectivity_state", "expectedStateJson is not a matching PCB lifecycle observation.");
        }
    }

    private static async Task<DocumentLifecycleState> ReadState(NativeClient client, DocumentSpecifier document,
        CancellationToken cancellationToken) => await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);

    private static void RequireState(DocumentLifecycleState actual, DocumentLifecycleState expected, string message)
    {
        if (!actual.Equals(expected))
            throw new AutomationException("pcb_connectivity_state_changed", message);
    }

    private static void ValidateNetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            throw new AutomationException("invalid_pcb_connectivity_net", "netName must be a non-empty PCB net name of at most 1024 characters.");
    }

    private static CallToolResult Data(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private static Task<CallToolResult> Execute(Func<Task<CallToolResult>> action) => ExecuteCore(action);

    private static async Task<CallToolResult> ExecuteCore(Func<Task<CallToolResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or ArgumentException)
        {
            string code = error is AutomationException known ? known.Code : "pcb_connectivity_query_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(
                new { errorCode = code, errorMessage = error.Message, query = "net" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }]
            };
        }
    }
}

/// <summary>Stable fields returned for one native PCB connectivity item.</summary>
internal sealed record PcbConnectivityItem(
    string Kind,
    string Id,
    string? ParentId,
    string? NetName,
    string? Layer,
    string? PadNumber,
    string[]? Layers);
