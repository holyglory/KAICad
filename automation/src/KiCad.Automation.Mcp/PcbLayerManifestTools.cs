using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
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

[McpServerToolType]
public sealed class PcbLayerManifestTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_layer_manifest", ReadOnly = true),
     Description("Read a revision-bound machine-readable PCB layer manifest. requestJson contains optional layers[] names such as F.Cu, F.SilkS, Dwgs.User or their BoardLayer enum names, plus includeEmpty; returned layer names retain their BoardLayer enum spelling. The tool returns enabled-layer metadata plus native object counts per layer and type without rendering or mutating the board. This is inspection metadata, not DRC, geometry rasterization or manufacturing proof.")]
    [KiCadCapability("pcb-rendering", "compiled-mcp plus native-board-api", "exact PCB lifecycle state, enabled layers, per-layer typed object counts"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Read(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_layer_manifest_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_layer_manifest_target", "Layer manifests require an explicit PCB.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_layer_manifest_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document))
            throw new AutomationException("invalid_pcb_layer_manifest_state", "expectedStateJson must identify the same PCB.");
        ManifestRequest request = JsonSerializer.Deserialize<ManifestRequest>(requestJson, JsonOptions) ?? new();
        if (request.Layers is null || request.Layers.Length > 128 || request.Layers.Any(string.IsNullOrWhiteSpace))
            throw new AutomationException("invalid_pcb_layer_manifest", "layers must contain at most 128 nonempty names.");
        HashSet<BoardLayer>? requested = request.Layers.Length == 0
            ? null : request.Layers.Select(ParseLayer).ToHashSet();
        NativeClient client = registry.Client(instanceId); AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch) throw new AutomationException("pcb_instance_changed", "The process epoch changed before the layer manifest.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, cancellationToken);
        if (!before.Equals(expected)) throw new AutomationException("pcb_state_changed", "The board changed before the layer manifest.");
        BoardEnabledLayersResponse enabled = await client.InvokeAsync<GetBoardEnabledLayers, BoardEnabledLayersResponse>(new() { Board = document }, cancellationToken);
        GetItemsResponse items = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia,
                KiCadObjectType.KotPcbPad, KiCadObjectType.KotPcbFootprint, KiCadObjectType.KotPcbShape, KiCadObjectType.KotPcbZone }
        }, cancellationToken);
        var counts = new Dictionary<BoardLayer, Dictionary<string, int>>();
        var objectIdsByLayer = new Dictionary<BoardLayer, HashSet<string>>();
        var compositeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var unassignedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nativeIdentityValues = new List<string?>();
        string Identity(KIID? identity)
        {
            string? value = identity?.Value;
            nativeIdentityValues.Add(value);
            return value ?? string.Empty;
        }
        void Add(BoardLayer layer, string type, string objectId)
        {
            if (requested is not null && !requested.Contains(layer)) return;
            if (!counts.TryGetValue(layer, out var types)) counts[layer] = types = new(StringComparer.Ordinal);
            types[type] = types.GetValueOrDefault(type) + 1;
            if (!objectIdsByLayer.TryGetValue(layer, out var objectIds))
                objectIdsByLayer[layer] = objectIds = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(objectId)) objectIds.Add(objectId);
        }
        void AddMembership(IEnumerable<BoardLayer> layers, string type, string objectId)
        {
            BoardLayer[] distinctLayers = layers.Distinct().ToArray();
            if (distinctLayers.Length == 0)
            {
                unassignedCounts[type] = unassignedCounts.GetValueOrDefault(type) + 1;
                return;
            }
            if (distinctLayers.Length > 1)
                compositeCounts[type] = compositeCounts.GetValueOrDefault(type) + 1;
            foreach (BoardLayer layer in distinctLayers) Add(layer, type, objectId);
        }
        foreach (Any packed in items.Items)
        {
            if (packed.Is(Track.Descriptor))
            {
                Track item = packed.Unpack<Track>();
                AddMembership([item.Layer], "track", Identity(item.Id));
            }
            else if (packed.Is(Arc.Descriptor))
            {
                Arc item = packed.Unpack<Arc>();
                AddMembership([item.Layer], "arc", Identity(item.Id));
            }
            else if (packed.Is(Via.Descriptor))
            {
                Via item = packed.Unpack<Via>();
                AddMembership(item.PadStack?.Layers.AsEnumerable() ?? Enumerable.Empty<BoardLayer>(), "via", Identity(item.Id));
            }
            else if (packed.Is(Pad.Descriptor))
            {
                Pad item = packed.Unpack<Pad>();
                AddMembership(item.PadStack?.Layers.AsEnumerable() ?? Enumerable.Empty<BoardLayer>(), "pad", Identity(item.Id));
            }
            else if (packed.Is(FootprintInstance.Descriptor))
            {
                FootprintInstance item = packed.Unpack<FootprintInstance>();
                AddMembership([item.Layer], "footprint", Identity(item.Id));
            }
            else if (packed.Is(BoardGraphicShape.Descriptor))
            {
                BoardGraphicShape item = packed.Unpack<BoardGraphicShape>();
                AddMembership([item.Layer], "shape", Identity(item.Id));
            }
            else if (packed.Is(Zone.Descriptor))
            {
                Zone item = packed.Unpack<Zone>();
                AddMembership(item.Layers, "zone", Identity(item.Id));
            }
        }
        ValidateNativeIdentities(nativeIdentityValues);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(new() { Document = document }, cancellationToken);
        if (!after.Equals(expected)) throw new AutomationException("pcb_layer_manifest_changed", "The board changed while reading the layer manifest.");
        var enabledLayerNames = enabled.Layers.Select(layer => layer.ToString()).ToArray();
        var layerRows = enabled.Layers.Select((layer, index) => new { layer, index })
            .Where(layer => requested is null || requested.Contains(layer.layer))
            .Where(layer => request.IncludeEmpty || counts.ContainsKey(layer.layer))
            .Select(layer =>
            {
                Dictionary<string, int> objectCounts = counts.GetValueOrDefault(layer.layer) ?? new(StringComparer.Ordinal);
                int membershipCount = objectCounts.Values.Sum();
                int distinctObjectCount = objectIdsByLayer.GetValueOrDefault(layer.layer)?.Count ?? 0;
                return new
                {
                    layer = layer.layer.ToString(),
                    order = layer.index,
                    kind = LayerKind(layer.layer),
                    objectCounts,
                    membershipCount,
                    distinctObjectCount,
                    hasObjects = membershipCount != 0
                };
            })
            .ToArray();
        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            enabledLayers = enabledLayerNames, copperLayerCount = enabled.CopperLayerCount, layers = layerRows,
            compositeObjects = new { byType = compositeCounts, total = compositeCounts.Values.Sum() },
            unassignedObjects = new { byType = unassignedCounts, total = unassignedCounts.Values.Sum() },
            totalObjects = items.Items.Count, layerMemberships = layerRows.Sum(layer => layer.membershipCount),
            nativeQuery = true, boardMutation = false,
            drcValidated = false, vectorGeometry = false
        });
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    internal static void ValidateNativeIdentities(IEnumerable<string?> identities)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? value in identities)
        {
            if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed == Guid.Empty
                || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal)
                || !seen.Add(value!))
                throw new AutomationException("pcb_layer_manifest_identity",
                    "Every layer-manifest object must carry one distinct canonical native UUID.");
        }
    }

    private sealed class ManifestRequest { public string[] Layers { get; set; } = []; public bool IncludeEmpty { get; set; } = true; }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    internal static BoardLayer ParseLayer(string value)
    {
        string normalized = value.Trim().Replace(".", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        string enumName = normalized.StartsWith("Bl", StringComparison.OrdinalIgnoreCase) ? normalized : "Bl" + normalized;
        if (System.Enum.TryParse<BoardLayer>(enumName, true, out var layer)
            && System.Enum.IsDefined(layer) && layer is not BoardLayer.BlUnknown and not BoardLayer.BlUndefined and not BoardLayer.BlUnselected)
            return layer;
        throw new AutomationException("invalid_pcb_layer_manifest", $"Unknown board layer '{value}'. Use a board layer name such as F.Cu or its BoardLayer enum name.");
    }

    private static string LayerKind(BoardLayer layer) => layer switch
    {
        _ when MultilayerRoutePlanner.IsCopperLayer(layer) => "copper",
        BoardLayer.BlFAdhes or BoardLayer.BlBAdhes or BoardLayer.BlFPaste or BoardLayer.BlBPaste
            or BoardLayer.BlFSilkS or BoardLayer.BlBSilkS or BoardLayer.BlFMask or BoardLayer.BlBMask
            or BoardLayer.BlFCrtYd or BoardLayer.BlBCrtYd or BoardLayer.BlFFab or BoardLayer.BlBFab => "technical",
        BoardLayer.BlEdgeCuts or BoardLayer.BlMargin => "board",
        BoardLayer.BlDwgsUser or BoardLayer.BlCmtsUser or BoardLayer.BlEco1User or BoardLayer.BlEco2User => "user",
        _ when layer.ToString().StartsWith("BlUser", StringComparison.Ordinal) => "user",
        _ => "other"
    };
    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    { try { return SchematicJson.Parser.Parse<T>(json); } catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException) { throw new AutomationException(code, error.Message); } }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException)
        { JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = error is AutomationException known ? known.Code : "pcb_layer_manifest_failed", errorMessage = error.Message }); return new CallToolResult { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] }; }
    }
}
