using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using Kiapi.Board.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbRoutingTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_route_preview", ReadOnly = true),
     Description("Run KiCad's native push-and-shove single-track router on an explicit start item and waypoint sequence, then return typed Track/Arc/Via candidates without committing copper. The preview is revision-bound and read-only; use candidate validation and detached DRC before any separate mutation. Active native routing sessions are rejected rather than hijacked.")]
    public Task<CallToolResult> Preview(string instanceId, string requestJson, string expectedStateJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        var request = SchematicJson.Parser.Parse<StartPcbRoutePreview>(requestJson);
        if (request.Document is null || request.Document.Type != DocumentType.DoctypePcb
            || request.ExpectedRevision is null || string.IsNullOrWhiteSpace(request.ProcessEpoch))
            throw new AutomationException("invalid_route_preview", "Supply an explicit PCB, process epoch and expected revision.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (!expected.Document.Equals(request.Document) || expected.Scope != DocumentLifecycleScope.DlsPcb
            || expected.ProcessEpoch != request.ProcessEpoch)
            throw new AutomationException("invalid_route_preview", "The preview request and lifecycle checkpoint target different native state.");
        var result = await registry.Client(instanceId).InvokeAsync<StartPcbRoutePreview, PcbRoutePreviewState>(request, cancellationToken);
        if (!result.Completed || result.NativeCommit || !result.PreviewOnly)
            throw new AutomationException(string.IsNullOrWhiteSpace(result.ErrorCode) ? "route_preview_failed" : result.ErrorCode,
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Native routing preview did not complete." : result.ErrorMessage);
        var candidate = new CreateItems { Header = new() { Document = request.Document } };
        candidate.Items.Add(result.RouteItems);
        var after = await registry.Client(instanceId).InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = request.Document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_preview_changed", "The board changed during native routing preview; discard the candidates.");
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = result.ProcessEpoch,
            preview = JsonSerializer.Deserialize<JsonElement>(SchematicJson.Formatter.Format(result)),
            candidateRequestJson = BoardJson.Formatter.Format(candidate),
            beforeState = SchematicJson.Formatter.Format(expected), afterState = SchematicJson.Formatter.Format(after),
            nativeRouter = true, nativeCommit = false, drcValidated = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    [McpServerTool(Name = "kicad_pcb_route_geometry", ReadOnly = true),
     Description("Measure exact native PCB trace, arc and via geometry at one revision checkpoint. Returns net-grouped track length, arc length, layer usage, via transitions and object identities. An optional netName restricts the result. This is numerical routing feedback for placement, high-speed tuning and candidate comparison; it does not certify DRC, impedance, RF or electromagnetic performance and does not mutate the board.")]
    public Task<CallToolResult> Measure(string instanceId, string documentJson, string expectedStateJson,
        CancellationToken cancellationToken, string? netName = null) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = SchematicJson.Parser.Parse<DocumentSpecifier>(documentJson);
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_route_target", "Route geometry requires an explicit PCB document.");
        var expected = SchematicJson.Parser.Parse<DocumentLifecycleState>(expectedStateJson);
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_route_state", "The expected observation must identify the same PCB and native process epoch.");
        if (netName is not null && string.IsNullOrWhiteSpace(netName))
            throw new AutomationException("invalid_route_net", "If supplied, netName must not be empty.");

        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before measuring route geometry.");
        var before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before route geometry could be measured.");
        var response = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
        }, cancellationToken);

        var items = new List<object>();
        var groups = new Dictionary<string, RouteGroup>(StringComparer.Ordinal);
        foreach (var packed in response.Items)
        {
            string itemNet; double lengthNm; string layer; string id;
            string[] viaLayers = [];
            if (packed.Is(Track.Descriptor))
            {
                var track = packed.Unpack<Track>(); itemNet = track.Net.Name; id = track.Id.Value;
                lengthNm = Distance(track.Start.XNm, track.Start.YNm, track.End.XNm, track.End.YNm);
                layer = track.Layer.ToString();
            }
            else if (packed.Is(Arc.Descriptor))
            {
                var arc = packed.Unpack<Arc>(); itemNet = arc.Net.Name; id = arc.Id.Value;
                lengthNm = ArcLength(arc); layer = arc.Layer.ToString();
            }
            else if (packed.Is(Via.Descriptor))
            {
                var via = packed.Unpack<Via>(); itemNet = via.Net.Name; id = via.Id.Value;
                lengthNm = 0; layer = "via";
                viaLayers = via.PadStack?.Layers.Select(value => value.ToString()).ToArray() ?? [];
            }
            else continue;
            if (netName is not null && !string.Equals(itemNet, netName, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(id) || !double.IsFinite(lengthNm))
                throw new AutomationException("invalid_route_geometry", "Native route geometry contained an invalid item identity or length.");
            string safeNet = itemNet ?? "";
            if (!groups.TryGetValue(safeNet, out var group)) groups[safeNet] = group = new(safeNet);
            group.ItemCount++;
            group.TrackLengthNm += lengthNm;
            if (layer == "via") group.ViaCount++;
            else group.Layers.Add(layer);
            items.Add(new { id, netName = safeNet, layer, lengthNm = Math.Round(lengthNm, MidpointRounding.AwayFromZero), viaLayers });
        }
        var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_observation_changed", "The board changed while route geometry was measured; discard the result and retry.");
        var data = JsonSerializer.SerializeToElement(new
        {
            instanceId, processEpoch = session.Epoch, document, netFilter = netName,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            routeItems = items, nets = groups.Values.OrderBy(group => group.NetName, StringComparer.Ordinal)
                .Select(group => new { netName = group.NetName, itemCount = group.ItemCount,
                    trackLengthNm = Math.Round(group.TrackLengthNm, MidpointRounding.AwayFromZero),
                    viaCount = group.ViaCount, layers = group.Layers.Order(StringComparer.Ordinal).ToArray() }),
            measuredGeometry = true, drcValidated = false, impedanceValidated = false, nativeCommit = false
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
    });

    private sealed class RouteGroup(string netName)
    {
        public string NetName { get; } = netName;
        public int ItemCount { get; set; }
        public int ViaCount { get; set; }
        public double TrackLengthNm { get; set; }
        public HashSet<string> Layers { get; } = new(StringComparer.Ordinal);
    }

    private static double Distance(long startX, long startY, long endX, long endY)
    {
        double dx = (double)endX - startX, dy = (double)endY - startY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double ArcLength(Arc arc)
    {
        double ax = arc.Start.XNm, ay = arc.Start.YNm, bx = arc.Mid.XNm, by = arc.Mid.YNm,
            cx = arc.End.XNm, cy = arc.End.YNm;
        double determinant = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(determinant) < 1e-9)
            return Distance(arc.Start.XNm, arc.Start.YNm, arc.Mid.XNm, arc.Mid.YNm)
                + Distance(arc.Mid.XNm, arc.Mid.YNm, arc.End.XNm, arc.End.YNm);
        double aa = ax * ax + ay * ay, bb = bx * bx + by * by, cc = cx * cx + cy * cy;
        double centerX = (aa * (by - cy) + bb * (cy - ay) + cc * (ay - by)) / determinant;
        double centerY = (aa * (cx - bx) + bb * (ax - cx) + cc * (bx - ax)) / determinant;
        double radius = Distance((long)centerX, (long)centerY, arc.Start.XNm, arc.Start.YNm);
        double start = Math.Atan2(ay - centerY, ax - centerX);
        double middle = Math.Atan2(by - centerY, bx - centerX);
        double end = Math.Atan2(cy - centerY, cx - centerX);
        double ccwStartEnd = PositiveAngle(end - start), ccwStartMiddle = PositiveAngle(middle - start);
        double sweep = ccwStartMiddle <= ccwStartEnd + 1e-10 ? ccwStartEnd : 2 * Math.PI - ccwStartEnd;
        return radius * sweep;
    }

    private static double PositiveAngle(double angle)
    {
        angle %= 2 * Math.PI;
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidProtocolBufferException or InvalidJsonException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            {
                errorCode = error is AutomationException known ? known.Code : "pcb_route_geometry_failed",
                errorMessage = error.Message
            });
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = structured.GetRawText() }], StructuredContent = structured };
        }
    }
}
