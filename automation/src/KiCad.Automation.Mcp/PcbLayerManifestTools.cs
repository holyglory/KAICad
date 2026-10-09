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
     Description("Read a revision-bound machine-readable PCB layer manifest. requestJson contains optional layers[] names such as F.Cu, F.SilkS, Dwgs.User or their BoardLayer enum names, plus includeEmpty; returned layer names retain their BoardLayer enum spelling. The tool returns enabled-layer metadata, deterministic native object rows with parent IDs and footprint-child rows for typed Pad, BoardGraphicShape, BoardText and Field payloads, plus native object counts per layer and type without rendering or mutating the board. The svgExport field identifies the existing typed kicad_pcb_export SVG route for authoritative layer-subset vector output, including composite or one-file-per-layer pagination, lifecycle binding and output hashes. This manifest itself is inspection metadata, not DRC, geometry rasterization or pixel-rendering proof.")]
    [KiCadCapability("pcb-rendering", "compiled-mcp plus native-board-api plus typed-svg-export", "exact PCB lifecycle state, enabled layers, deterministic object/footprint-child parent mapping and per-layer typed object counts; SVG export uses its own operation ID, process epoch and output manifest"),
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
        var nativeObjects = new List<ManifestObject>();
        var footprints = new List<FootprintInstance>();
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
        void AddMembership(IEnumerable<BoardLayer> layers, string type, string objectId, string? parentId)
        {
            BoardLayer[] distinctLayers = layers.Distinct().ToArray();
            if (distinctLayers.Length == 0)
            {
                unassignedCounts[type] = unassignedCounts.GetValueOrDefault(type) + 1;
            }
            else if (distinctLayers.Length > 1)
                compositeCounts[type] = compositeCounts.GetValueOrDefault(type) + 1;
            foreach (BoardLayer layer in distinctLayers) Add(layer, type, objectId);
            if (requested is null || distinctLayers.Any(requested.Contains))
                nativeObjects.Add(new ManifestObject(objectId, type,
                    distinctLayers.Select(layer => layer.ToString()).ToArray(), parentId));
        }
        foreach (Any packed in items.Items)
        {
            if (packed.Is(Track.Descriptor))
            {
                Track item = packed.Unpack<Track>();
                string id = Identity(item.Id);
                AddMembership([item.Layer], "track", id, ParentIdentity(item.Parent));
            }
            else if (packed.Is(Arc.Descriptor))
            {
                Arc item = packed.Unpack<Arc>();
                string id = Identity(item.Id);
                AddMembership([item.Layer], "arc", id, ParentIdentity(item.Parent));
            }
            else if (packed.Is(Via.Descriptor))
            {
                Via item = packed.Unpack<Via>();
                string id = Identity(item.Id);
                AddMembership(item.PadStack?.Layers.AsEnumerable() ?? Enumerable.Empty<BoardLayer>(), "via", id, ParentIdentity(item.Parent));
            }
            else if (packed.Is(Pad.Descriptor))
            {
                Pad item = packed.Unpack<Pad>();
                string id = Identity(item.Id);
                AddMembership(item.PadStack?.Layers.AsEnumerable() ?? Enumerable.Empty<BoardLayer>(), "pad", id, ParentIdentity(item.Parent));
            }
            else if (packed.Is(FootprintInstance.Descriptor))
            {
                FootprintInstance item = packed.Unpack<FootprintInstance>();
                Identity(item.Id);
                AddMembership([item.Layer], "footprint", item.Id?.Value ?? string.Empty, ParentIdentity(item.Parent));
                footprints.Add(item);
            }
            else if (packed.Is(BoardGraphicShape.Descriptor))
            {
                BoardGraphicShape item = packed.Unpack<BoardGraphicShape>();
                string id = Identity(item.Id);
                AddMembership([item.Layer], "shape", id, ParentIdentity(item.Parent));
            }
            else if (packed.Is(Zone.Descriptor))
            {
                Zone item = packed.Unpack<Zone>();
                string id = Identity(item.Id);
                AddMembership(item.Layers, "zone", id, ParentIdentity(item.Parent));
            }
        }
        ValidateNativeIdentities(nativeIdentityValues);
        IReadOnlyList<ManifestFootprintChildren> footprintChildren =
            BuildFootprintChildren(footprints, items.Items, requested);
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
        var objectRows = OrderObjects(nativeObjects)
            .Select(item => new { id = item.Id, type = item.Type, layers = item.Layers, parentId = item.ParentId })
            .ToArray();
        JsonElement structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            enabledLayers = enabledLayerNames, copperLayerCount = enabled.CopperLayerCount, layers = layerRows,
            compositeObjects = new { byType = compositeCounts, total = compositeCounts.Values.Sum() },
            unassignedObjects = new { byType = unassignedCounts, total = unassignedCounts.Values.Sum() },
            totalObjects = items.Items.Count, layerMemberships = layerRows.Sum(layer => layer.membershipCount),
            objects = objectRows,
            footprintChildren = footprintChildren.Select(group => new
            {
                footprintId = group.FootprintId,
                children = group.Children.Select(child => new
                {
                    id = child.Id, type = child.Type, layers = child.Layers, parentId = child.ParentId
                }).ToArray()
            }).ToArray(),
            svgExport = SvgExportCoverage,
            nativeQuery = true, boardMutation = false,
            drcValidated = false, vectorGeometry = false
        });
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    internal sealed record ManifestObject(string Id, string Type, string[] Layers, string? ParentId);
    internal sealed record ManifestFootprintChild(string Id, string Type, string[] Layers, string? ParentId,
        BoardLayer[] LayerValues);
    internal sealed record ManifestFootprintChildren(string FootprintId, IReadOnlyList<ManifestFootprintChild> Children);

    internal static IReadOnlyList<ManifestObject> OrderObjects(IEnumerable<ManifestObject> objects) => objects
        .OrderBy(item => item.Type, StringComparer.Ordinal)
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyList<ManifestFootprintChildren> BuildFootprintChildren(
        IEnumerable<FootprintInstance> footprints, IEnumerable<Any> nativeItems,
        IReadOnlySet<BoardLayer>? requestedLayers = null)
    {
        var groups = new Dictionary<string, Dictionary<string, ManifestFootprintChild>>(StringComparer.Ordinal);
        foreach (FootprintInstance footprint in footprints)
        {
            string id = ChildIdentity(footprint.Id);
            if (!groups.TryAdd(id, new Dictionary<string, ManifestFootprintChild>(StringComparer.Ordinal)))
                throw new AutomationException("pcb_layer_manifest_identity",
                    $"The layer manifest returned duplicate footprint identity '{id}'.");
        }
        var embeddedIds = groups.ToDictionary(entry => entry.Key,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string footprintId, ManifestFootprintChild child, bool embedded)
        {
            if (!groups.ContainsKey(footprintId))
                throw new AutomationException("pcb_layer_manifest_parent_identity",
                    $"Footprint child '{child.Id}' refers to unknown footprint '{footprintId}'.");
            if (embedded && !embeddedIds[footprintId].Add(child.Id))
                throw new AutomationException("pcb_layer_manifest_identity",
                    $"Footprint '{footprintId}' contains duplicate child identity '{child.Id}'.");
            if (owners.TryGetValue(child.Id, out string? owner) && owner != footprintId)
                throw new AutomationException("pcb_layer_manifest_identity",
                    $"Footprint child identity '{child.Id}' belongs to multiple footprints.");
            owners[child.Id] = footprintId;
            if (child.ParentId is not null && child.ParentId != footprintId)
                throw new AutomationException("pcb_layer_manifest_parent_identity",
                    $"Footprint child '{child.Id}' declares parent '{child.ParentId}' but is nested in '{footprintId}'.");

            if (!groups[footprintId].TryGetValue(child.Id, out ManifestFootprintChild? existing))
            {
                groups[footprintId][child.Id] = child with { ParentId = child.ParentId ?? footprintId };
                return;
            }
            if (!string.Equals(existing.Type, child.Type, StringComparison.Ordinal)
                || !existing.LayerValues.SequenceEqual(child.LayerValues))
                throw new AutomationException("pcb_layer_manifest_identity",
                    $"Footprint child identity '{child.Id}' was returned with conflicting native data.");
            if (existing.ParentId is null && child.ParentId is not null)
                groups[footprintId][child.Id] = existing with { ParentId = child.ParentId };
        }

        foreach ((string footprintId, FootprintInstance footprint) in footprints
            .Select(item => (id: ChildIdentity(item.Id), item)))
        {
            foreach (Any packed in footprint.Definition?.Items ?? Enumerable.Empty<Any>())
            {
                if (!TryDescribeFootprintChild(packed, out ManifestFootprintChild child)) continue;
                Add(footprintId, child, embedded: true);
            }
        }

        // Pads and board graphics are also returned as top-level native objects.  Merge those
        // envelopes when their read-only parent points at a known footprint, but never infer a
        // parent for an object whose protobuf payload does not declare one.
        foreach (Any packed in nativeItems)
        {
            if (!TryDescribeFootprintChild(packed, out ManifestFootprintChild child)
                || child.ParentId is null) continue;
            if (!groups.ContainsKey(child.ParentId))
            {
                if (child.Type is "pad" or "text" or "field")
                    throw new AutomationException("pcb_layer_manifest_parent_identity",
                        $"Footprint child '{child.Id}' refers to unknown footprint '{child.ParentId}'.");
                continue;
            }
            Add(child.ParentId, child, embedded: false);
        }

        return groups
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry =>
            {
                ManifestFootprintChild[] children = entry.Value.Values
                    .Where(child => requestedLayers is null || child.LayerValues.Any(requestedLayers.Contains))
                    .OrderBy(child => child.Type, StringComparer.Ordinal)
                    .ThenBy(child => child.Id, StringComparer.Ordinal)
                    .ToArray();
                return new ManifestFootprintChildren(entry.Key, children);
            })
            .Where(group => requestedLayers is null || group.Children.Count != 0)
            .ToArray();
    }

    private static bool TryDescribeFootprintChild(Any packed, out ManifestFootprintChild child)
    {
        if (packed.Is(Pad.Descriptor))
        {
            Pad item = packed.Unpack<Pad>();
            child = Child(item.Id, "pad", item.PadStack?.Layers ?? Enumerable.Empty<BoardLayer>(), item.Parent);
            return true;
        }
        if (packed.Is(BoardGraphicShape.Descriptor))
        {
            BoardGraphicShape item = packed.Unpack<BoardGraphicShape>();
            child = Child(item.Id, "shape", [item.Layer], item.Parent);
            return true;
        }
        if (packed.Is(BoardText.Descriptor))
        {
            BoardText item = packed.Unpack<BoardText>();
            child = Child(item.Id, "text", [item.Layer], item.Parent);
            return true;
        }
        if (packed.Is(Field.Descriptor))
        {
            Field item = packed.Unpack<Field>();
            if (item.Text is null)
                throw new AutomationException("pcb_layer_manifest_identity",
                    "A footprint field child must carry its native BoardText identity.");
            child = Child(item.Text.Id, "field", [item.Text.Layer], item.Text.Parent);
            return true;
        }
        child = null!;
        return false;
    }

    private static ManifestFootprintChild Child(KIID? id, string type, IEnumerable<BoardLayer> layers, KIID? parent)
    {
        string identity = ChildIdentity(id);
        BoardLayer[] layerValues = layers.Distinct().ToArray();
        if (layerValues.Any(layer => layer is BoardLayer.BlUnknown or BoardLayer.BlUndefined or BoardLayer.BlUnselected))
            throw new AutomationException("pcb_layer_manifest_layer_identity",
                $"Footprint child '{identity}' has an unknown board layer.");
        return new ManifestFootprintChild(identity, type,
            layerValues.Select(layer => layer.ToString()).ToArray(), ParentIdentity(parent), layerValues);
    }

    private static string ChildIdentity(KIID? identity)
    {
        string? value = identity?.Value;
        if (!CanonicalUuid(value))
            throw new AutomationException("pcb_layer_manifest_identity",
                "Every footprint child must carry one canonical native UUID.");
        return value!;
    }

    private static string? ParentIdentity(KIID? identity)
    {
        string? value = identity?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!CanonicalUuid(value))
            throw new AutomationException("pcb_layer_manifest_parent_identity",
                "Every native parent reference must be a canonical UUID.");
        return value;
    }

    private static bool CanonicalUuid(string? value) => value is not null
        && Guid.TryParseExact(value, "D", out Guid parsed) && parsed != Guid.Empty
        && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

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

    // The manifest is an observation tool.  It must not imply that its object
    // counts are an SVG render or that it owns export output.  This compact
    // capability row points callers to the already-qualified typed board-job
    // export and states exactly which layer/pagination and evidence guarantees
    // that route provides.
    internal static IReadOnlyDictionary<string, object?> SvgExportCoverage { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["supported"] = true,
            ["tool"] = "kicad_pcb_export",
            ["job"] = "exportSvg",
            ["layerSubset"] = true,
            ["composite"] = true,
            ["onePagePerLayer"] = true,
            ["oneFilePerLayer"] = true,
            ["lifecycleBound"] = true,
            ["processEpochBound"] = true,
            ["outputHashes"] = true,
            ["boardMutation"] = false,
            ["paginationModes"] = new[]
            {
                "BJPM_ALL_LAYERS_ONE_PAGE",
                "BJPM_EACH_LAYER_OWN_PAGE",
                "BJPM_EACH_LAYER_OWN_FILE"
            },
            ["limitations"] = new[]
            {
                "The layer manifest does not contain SVG geometry or exact rendered pixels.",
                "Invoke kicad_pcb_export with exportSvg for the lifecycle-bound vector artifact."
            }
        };

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
