using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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

/// <summary>
/// Emits exact native item envelopes together with a caller supplied image transform.
/// Rendering jobs and this observation intentionally remain separate: a caller can pair the
/// returned transform with an SVG or PNG only when it uses the same viewport and dimensions.
/// </summary>
[McpServerToolType]
public sealed class PcbGeometryManifestTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_render_geometry", ReadOnly = true),
     Description("Read a revision-bound PCB geometry manifest for independent layer inspection. requestJson supplies an explicit nanometre viewport, output width/height, optional layers[] and objectTypes[] filters, and includeGeometry; each native item's exact bounding box is paired with deterministic pixel bounds and optional control geometry. The transform uses image coordinates with y increasing downward. This is observation metadata and does not produce pixels, validate DRC, or mutate the board."),
     KiCadCapability("pcb-rendering", "compiled-mcp plus native-board-api", "exact PCB lifecycle state, native item bounding boxes, deterministic coordinate transform"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Read(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_render_geometry_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_pcb_render_geometry_target", "Geometry manifests require an explicit PCB document.");

        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_render_geometry_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_pcb_render_geometry_state", "expectedStateJson must identify the same PCB and native process epoch.");

        GeometryRequest request = JsonSerializer.Deserialize<GeometryRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_pcb_render_geometry", "requestJson must be a JSON object.");
        Validate(request);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The process epoch changed before the geometry manifest.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before the geometry manifest; inspect a fresh lifecycle checkpoint.");

        GetItemsResponse response = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ =
            {
                KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia,
                KiCadObjectType.KotPcbPad, KiCadObjectType.KotPcbFootprint, KiCadObjectType.KotPcbShape,
                KiCadObjectType.KotPcbZone
            }
        }, cancellationToken);
        if (response.Items.Count > request.MaxObjects)
            throw new AutomationException("pcb_render_geometry_oversized", $"The native query returned {response.Items.Count} items; narrow the layer or object-type filters.");

        HashSet<string>? requestedLayers = request.Layers.Length == 0
            ? null : request.Layers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? requestedTypes = request.ObjectTypes.Length == 0
            ? null : request.ObjectTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Any[] selected = response.Items.Where(item => TryDescribe(item, out string type, out string[] layers)
            && (requestedTypes is null || requestedTypes.Contains(type))
            && (requestedLayers is null || layers.Any(requestedLayers.Contains))).ToArray();
        if (selected.Length > request.MaxObjects)
            throw new AutomationException("pcb_render_geometry_oversized", "The filtered geometry manifest exceeds maxObjects.");

        var ids = selected.Select(GetId).ToArray();
        ValidateNativeIdentities(ids);
        Dictionary<string, Box2> boxes = new(StringComparer.Ordinal);
        if (ids.Length != 0)
        {
            GetBoundingBoxResponse nativeBoxes = await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(new()
            {
                Header = new() { Document = document }, Items = { ids.Select(id => id.Clone()) }, Mode = BoundingBoxMode.BbmItemOnly
            }, cancellationToken);
            if (nativeBoxes.Items.Count != nativeBoxes.Boxes.Count
                || !nativeBoxes.Items.All(id => !string.IsNullOrWhiteSpace(id.Value)))
                throw new AutomationException("pcb_render_geometry_bounds_incomplete", "KiCad returned an incomplete native bounding-box response.");
            for (int index = 0; index < nativeBoxes.Items.Count; index++)
                boxes[nativeBoxes.Items[index].Value] = nativeBoxes.Boxes[index];
        }

        var items = new List<object>(selected.Length);
        foreach (Any packed in selected)
        {
            if (!TryDescribe(packed, out string type, out string[] layers)) continue;
            KIID id = GetId(packed);
            if (string.IsNullOrWhiteSpace(id.Value) || !boxes.TryGetValue(id.Value, out Box2? box))
                throw new AutomationException("pcb_render_geometry_bounds_incomplete", $"KiCad returned no native bounds for {type} '{id.Value}'.");
            (long minX, long minY, long maxX, long maxY) = Normalize(box);
            object? anchor = TryAnchor(packed, out Vector2 point)
                ? PixelPoint(point, request.Viewport!, request.Width, request.Height)
                : null;
            var row = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id.Value,
                ["objectType"] = type,
                ["layers"] = layers,
                ["netName"] = TryNetName(packed),
                ["boundsNm"] = new { minXNm = minX, minYNm = minY, maxXNm = maxX, maxYNm = maxY },
                ["pixelBounds"] = PixelBounds(minX, minY, maxX, maxY, request.Viewport!, request.Width, request.Height),
                ["boundsSource"] = "native-get-bounding-box",
                ["boundsExact"] = true
            };
            if (anchor is not null) row["anchorPixel"] = anchor;
            if (request.IncludeGeometry) row["geometry"] = Geometry(packed);
            items.Add(row);
        }

        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("pcb_render_geometry_changed", "The board changed while reading geometry; discard the result and inspect again.");

        var structured = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            coordinateTransform = new
            {
                units = "nm", width = request.Width, height = request.Height,
                viewport = new { minXNm = request.Viewport!.MinXNm, minYNm = request.Viewport.MinYNm,
                    maxXNm = request.Viewport.MaxXNm, maxYNm = request.Viewport.MaxYNm },
                yAxis = "down", formula = "x=(xNm-minXNm)*width/(maxXNm-minXNm); y=(maxYNm-yNm)*height/(maxYNm-minYNm)"
            },
            items, itemCount = items.Count, nativeQuery = true, nativeBounds = true,
            boardMutation = false, rasterImage = false, drcValidated = false
        }, JsonOptions);
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    private sealed class GeometryRequest
    {
        public Viewport? Viewport { get; set; }
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
        public string[] Layers { get; set; } = [];
        public string[] ObjectTypes { get; set; } = [];
        public bool IncludeGeometry { get; set; } = true;
        public int MaxObjects { get; set; } = 8192;
    }

    private sealed class Viewport
    {
        public long MinXNm { get; set; }
        public long MinYNm { get; set; }
        public long MaxXNm { get; set; }
        public long MaxYNm { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true };

    private static void Validate(GeometryRequest request)
    {
        if (request.Viewport is null || request.Viewport.MinXNm >= request.Viewport.MaxXNm || request.Viewport.MinYNm >= request.Viewport.MaxYNm)
            throw new AutomationException("invalid_pcb_render_geometry", "viewport must contain positive min/max nanometre extents.");
        if (request.Width is < 1 or > 8192 || request.Height is < 1 or > 8192)
            throw new AutomationException("invalid_pcb_render_geometry", "width and height must be between 1 and 8192 pixels.");
        if (request.Layers.Length > 128 || request.Layers.Any(string.IsNullOrWhiteSpace)
            || request.ObjectTypes.Length > 7 || request.ObjectTypes.Any(string.IsNullOrWhiteSpace))
            throw new AutomationException("invalid_pcb_render_geometry", "layers may contain at most 128 names and objectTypes at most 7 names.");
        string[] known = ["track", "arc", "via", "pad", "footprint", "shape", "zone"];
        if (request.ObjectTypes.Any(value => !known.Contains(value, StringComparer.OrdinalIgnoreCase)))
            throw new AutomationException("invalid_pcb_render_geometry", "objectTypes must use track, arc, via, pad, footprint, shape or zone.");
        if (request.MaxObjects is < 1 or > 8192)
            throw new AutomationException("invalid_pcb_render_geometry", "maxObjects must be between 1 and 8192.");
    }

    private static bool TryDescribe(Any packed, out string type, out string[] layers)
    {
        if (packed.Is(Track.Descriptor)) { Track item = packed.Unpack<Track>(); type = "track"; layers = [item.Layer.ToString()]; return true; }
        if (packed.Is(Arc.Descriptor)) { Arc item = packed.Unpack<Arc>(); type = "arc"; layers = [item.Layer.ToString()]; return true; }
        if (packed.Is(Via.Descriptor)) { Via item = packed.Unpack<Via>(); type = "via"; layers = item.PadStack?.Layers.Select(layer => layer.ToString()).ToArray() ?? []; return true; }
        if (packed.Is(Pad.Descriptor)) { Pad item = packed.Unpack<Pad>(); type = "pad"; layers = item.PadStack?.Layers.Select(layer => layer.ToString()).ToArray() ?? []; return true; }
        if (packed.Is(FootprintInstance.Descriptor)) { FootprintInstance item = packed.Unpack<FootprintInstance>(); type = "footprint"; layers = [item.Layer.ToString()]; return true; }
        if (packed.Is(BoardGraphicShape.Descriptor)) { BoardGraphicShape item = packed.Unpack<BoardGraphicShape>(); type = "shape"; layers = [item.Layer.ToString()]; return true; }
        if (packed.Is(Zone.Descriptor)) { Zone item = packed.Unpack<Zone>(); type = "zone"; layers = item.Layers.Select(layer => layer.ToString()).ToArray(); return true; }
        type = ""; layers = [];
        return false;
    }

    private static KIID GetId(Any packed) => packed.Is(Track.Descriptor) ? packed.Unpack<Track>().Id
        : packed.Is(Arc.Descriptor) ? packed.Unpack<Arc>().Id
        : packed.Is(Via.Descriptor) ? packed.Unpack<Via>().Id
        : packed.Is(Pad.Descriptor) ? packed.Unpack<Pad>().Id
        : packed.Is(FootprintInstance.Descriptor) ? packed.Unpack<FootprintInstance>().Id
        : packed.Is(BoardGraphicShape.Descriptor) ? packed.Unpack<BoardGraphicShape>().Id
        : packed.Unpack<Zone>().Id;

    internal static void ValidateNativeIdentities(IEnumerable<KIID?> identities)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (KIID? identity in identities)
        {
            string? value = identity?.Value;
            if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed == Guid.Empty
                || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal)
                || !seen.Add(value!))
                throw new AutomationException("pcb_render_geometry_bounds_incomplete",
                    "Every geometry item must carry one distinct canonical native UUID.");
        }
    }

    private static string? TryNetName(Any packed) => packed.Is(Track.Descriptor) ? packed.Unpack<Track>().Net?.Name
        : packed.Is(Arc.Descriptor) ? packed.Unpack<Arc>().Net?.Name
        : packed.Is(Via.Descriptor) ? packed.Unpack<Via>().Net?.Name
        : packed.Is(Pad.Descriptor) ? packed.Unpack<Pad>().Net?.Name
        : packed.Is(BoardGraphicShape.Descriptor) ? packed.Unpack<BoardGraphicShape>().Net?.Name
        : packed.Is(Zone.Descriptor) ? packed.Unpack<Zone>().CopperSettings?.Net?.Name : null;

    private static bool TryAnchor(Any packed, out Vector2 point)
    {
        if (packed.Is(Track.Descriptor)) { point = packed.Unpack<Track>().Start; return true; }
        if (packed.Is(Arc.Descriptor)) { point = packed.Unpack<Arc>().Start; return true; }
        if (packed.Is(Via.Descriptor)) { point = packed.Unpack<Via>().Position; return true; }
        if (packed.Is(FootprintInstance.Descriptor)) { point = packed.Unpack<FootprintInstance>().Position; return true; }
        point = new(); return false;
    }

    internal static object Geometry(Any packed)
    {
        if (packed.Is(Track.Descriptor))
        {
            Track item = packed.Unpack<Track>();
            return SegmentGeometry(item.Start, item.End);
        }
        if (packed.Is(Arc.Descriptor))
        {
            Arc item = packed.Unpack<Arc>();
            return new { kind = "arc", start = Point(item.Start), mid = Point(item.Mid), end = Point(item.End) };
        }
        if (packed.Is(Via.Descriptor))
        {
            Via item = packed.Unpack<Via>();
            return new { kind = "via", position = Point(item.Position), padStack = PadStackGeometry(item.PadStack) };
        }
        if (packed.Is(FootprintInstance.Descriptor))
        {
            FootprintInstance item = packed.Unpack<FootprintInstance>();
            return new { kind = "footprint-origin", position = Point(item.Position), layer = item.Layer.ToString(), childCount = item.Definition?.Items.Count ?? 0 };
        }
        if (packed.Is(Pad.Descriptor))
        {
            Pad item = packed.Unpack<Pad>();
            return new { kind = "pad", position = Point(item.Position), number = item.Number,
                parent = item.Parent?.Value, padStack = PadStackGeometry(item.PadStack) };
        }
        if (packed.Is(BoardGraphicShape.Descriptor))
        {
            BoardGraphicShape item = packed.Unpack<BoardGraphicShape>();
            return new { kind = "graphic-shape", layer = item.Layer.ToString(), geometry = GraphicGeometry(item.Shape) };
        }
        Zone zone = packed.Unpack<Zone>();
        return new { kind = "zone", type = zone.Type.ToString(), layers = zone.Layers.Select(layer => layer.ToString()).ToArray(),
            outline = PolySetGeometry(zone.Outline), filledPolygonCount = zone.FilledPolygons.Count };
    }

    private static object? PadStackGeometry(PadStack? stack)
    {
        if (stack is null) return null;
        return new
        {
            type = stack.Type.ToString(), angle = stack.Angle?.ValueDegrees,
            layers = stack.Layers.Select(layer => layer.ToString()).ToArray(),
            drill = DrillGeometry(stack.Drill),
            copperLayers = stack.CopperLayers.Select(layer => new
            {
                layer = layer.Layer.ToString(), shape = layer.Shape.ToString(),
                size = Point(layer.Size), offset = Point(layer.Offset),
                cornerRoundingRatio = layer.CornerRoundingRatio, chamferRatio = layer.ChamferRatio
            }).ToArray()
        };
    }

    private static object? DrillGeometry(DrillProperties? drill) => drill is null ? null : new
    {
        startLayer = drill.StartLayer.ToString(), endLayer = drill.EndLayer.ToString(),
        diameter = Point(drill.Diameter), shape = drill.Shape.ToString()
    };

    private static object PolySetGeometry(PolySet? set) => new
    {
        polygons = (set?.Polygons ?? []).Select(polygon => new
        {
            outline = PolyLineGeometry(polygon.Outline),
            holes = polygon.Holes.Select(PolyLineGeometry).ToArray()
        }).ToArray()
    };

    private static object PolyLineGeometry(PolyLine? line) => new
    {
        closed = line?.Closed ?? false,
        nodes = (line?.Nodes ?? []).Select(NodeGeometry).ToArray()
    };

    private static object NodeGeometry(PolyLineNode node) => node.GeometryCase switch
    {
        PolyLineNode.GeometryOneofCase.Point => new { kind = "point", point = Point(node.Point) },
        PolyLineNode.GeometryOneofCase.Arc => new { kind = "arc", start = Point(node.Arc.Start), mid = Point(node.Arc.Mid), end = Point(node.Arc.End) },
        _ => new { kind = "unknown", point = (object?)null }
    };

    private static object? GraphicGeometry(GraphicShape? shape)
    {
        if (shape is null) return null;
        return shape.GeometryCase switch
        {
            GraphicShape.GeometryOneofCase.Segment => new { kind = "segment", start = Point(shape.Segment.Start), end = Point(shape.Segment.End) },
            GraphicShape.GeometryOneofCase.Rectangle => new { kind = "rectangle", topLeft = Point(shape.Rectangle.TopLeft), bottomRight = Point(shape.Rectangle.BottomRight), cornerRadiusNm = shape.Rectangle.CornerRadius?.ValueNm },
            GraphicShape.GeometryOneofCase.Arc => new { kind = "arc", start = Point(shape.Arc.Start), mid = Point(shape.Arc.Mid), end = Point(shape.Arc.End) },
            GraphicShape.GeometryOneofCase.Circle => new { kind = "circle", center = Point(shape.Circle.Center), radiusPoint = Point(shape.Circle.RadiusPoint) },
            GraphicShape.GeometryOneofCase.Polygon => new { kind = "polygon", geometry = PolySetGeometry(shape.Polygon) },
            GraphicShape.GeometryOneofCase.Bezier => new { kind = "bezier", start = Point(shape.Bezier.Start), control1 = Point(shape.Bezier.Control1), control2 = Point(shape.Bezier.Control2), end = Point(shape.Bezier.End) },
            GraphicShape.GeometryOneofCase.Ellipse => new { kind = "ellipse", center = Point(shape.Ellipse.Center), majorRadiusNm = shape.Ellipse.MajorRadius?.ValueNm, minorRadiusNm = shape.Ellipse.MinorRadius?.ValueNm, rotationDegrees = shape.Ellipse.Rotation?.ValueDegrees },
            GraphicShape.GeometryOneofCase.EllipseArc => new { kind = "ellipse-arc", center = Point(shape.EllipseArc.Center), majorRadiusNm = shape.EllipseArc.MajorRadius?.ValueNm, minorRadiusNm = shape.EllipseArc.MinorRadius?.ValueNm, rotationDegrees = shape.EllipseArc.Rotation?.ValueDegrees, startAngleDegrees = shape.EllipseArc.StartAngle?.ValueDegrees, endAngleDegrees = shape.EllipseArc.EndAngle?.ValueDegrees },
            _ => new { kind = "none" }
        };
    }

    private static object SegmentGeometry(Vector2 start, Vector2 end) => new { kind = "segment", start = Point(start), end = Point(end) };
    private static object? Point(Vector2? value) => value is null ? null : new { xNm = value.XNm, yNm = value.YNm };

    private static (long MinX, long MinY, long MaxX, long MaxY) Normalize(Box2 box)
    {
        long x1 = box.Position?.XNm ?? 0, y1 = box.Position?.YNm ?? 0;
        long x2 = checked(x1 + (box.Size?.XNm ?? 0)), y2 = checked(y1 + (box.Size?.YNm ?? 0));
        return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    private static object PixelPoint(Vector2 point, Viewport viewport, int width, int height)
        => new { x = PixelX(point.XNm, viewport, width), y = PixelY(point.YNm, viewport, height), xNm = point.XNm, yNm = point.YNm };

    private static object PixelBounds(long minX, long minY, long maxX, long maxY, Viewport viewport, int width, int height)
    {
        double left = PixelX(minX, viewport, width), right = PixelX(maxX, viewport, width);
        double top = PixelY(maxY, viewport, height), bottom = PixelY(minY, viewport, height);
        return new { left, top, right, bottom, width = right - left, height = bottom - top,
            intersectsViewport = maxX >= viewport.MinXNm && minX <= viewport.MaxXNm
                && maxY >= viewport.MinYNm && minY <= viewport.MaxYNm };
    }

    private static double PixelX(long value, Viewport viewport, int width)
        => ((double)value - viewport.MinXNm) * width / ((double)viewport.MaxXNm - viewport.MinXNm);
    private static double PixelY(long value, Viewport viewport, int height)
        => ((double)viewport.MaxYNm - value) * height / ((double)viewport.MaxYNm - viewport.MinYNm);

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException(code, error.Message); }
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or IOException or OverflowException or InvalidProtocolBufferException or InvalidJsonException or JsonException)
        {
            JsonElement structured = JsonSerializer.SerializeToElement(new
            { errorCode = error is AutomationException known ? known.Code : "pcb_render_geometry_failed", errorMessage = error.Message });
            return new CallToolResult { IsError = true, StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}
