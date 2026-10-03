using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using ProtoAny = Google.Protobuf.WellKnownTypes.Any;
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
/// Deterministic, read-only routing for agents that need an explainable proposal before
/// invoking KiCad's interactive router.  The native router remains the authority for the
/// final geometry and DRC.  This planner deliberately requires a board window and a grid;
/// it never guesses a board outline or silently turns an incomplete native snapshot into a
/// manufacturing claim.
/// </summary>
[McpServerToolType]
public sealed class PcbHeadlessRoutingTools(InstanceRegistry registry)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly GridNode[] Directions =
    [
        new(1, 0), new(0, 1), new(-1, 0), new(0, -1)
    ];

    [McpServerTool(Name = "kicad_pcb_route_plan", ReadOnly = true),
     Description("Build a deterministic, read-only single-layer PCB route proposal from a checked board observation. requestJson is a JSON object with netName, optional netClassName, start/end {xNm,yNm}, copper layer, widthNm, clearanceNm, gridNm, explicit bounds {minXNm,minYNm,maxXNm,maxYNm}, and optional maxNodes/maxObstacleReport/includeFootprintEnvelopes/includeRipUpCandidate. The planner validates the native net and effective netclass, reads native tracks, arcs, vias, pads, zones, copper graphics and conservative footprint envelopes, expands axis-aligned obstacles, and runs a fixed-order 4-way A* search. When includeRipUpCandidate is true, it also returns an exact DeleteItems candidate for existing same-net copper so the route can be rerouted transactionally after DRC review. It never commits copper and never claims DRC, impedance, differential-pair, length or manufacturing correctness. Multi-layer changes and native push-and-shove remain separate operations.")]
    [KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "explicit PCB lifecycle state, board obstacle snapshot, deterministic grid proposal"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Plan(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_route_plan_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_route_plan_target", "Route planning requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_route_plan_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_route_plan_state", "The expected observation must identify the same PCB and native process epoch.");
        RoutePlanRequest request = ParseRequest(requestJson);
        ValidateRequest(request);
        RoutePoint startPoint = request.Start!;
        RoutePoint endPoint = request.End!;
        RouteBounds routeBounds = request.Bounds!;

        var client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before route planning.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before route planning; refresh the lifecycle checkpoint.");

        // Validate the requested net and its effective netclass against the same
        // lifecycle checkpoint used for the obstacle snapshot.  A planner must
        // never emit a candidate for a typo, an orphaned net, or a class that is
        // not present on the board.
        NetsResponse nets = await client.InvokeAsync<GetNets, NetsResponse>(new() { Board = document }, cancellationToken);
        Net? targetNet = nets.Nets.FirstOrDefault(net => string.Equals(net.Name, request.NetName, StringComparison.Ordinal));
        if (targetNet is null)
            throw new AutomationException("route_net_not_found", $"The PCB has no net named '{request.NetName}'.");

        GetNetClassForNets netClassQuery = new();
        netClassQuery.Net.Add(targetNet.Clone());
        NetClassForNetsResponse netClasses = await client.InvokeAsync<GetNetClassForNets, NetClassForNetsResponse>(netClassQuery, cancellationToken);
        if (!netClasses.Classes.TryGetValue(request.NetName, out var effectiveNetClass))
            throw new AutomationException("route_net_class_not_found", $"The native PCB returned no effective netclass for '{request.NetName}'.");
        if (!string.IsNullOrWhiteSpace(request.NetClassName)
            && !MatchesNetClass(effectiveNetClass, request.NetClassName))
            throw new AutomationException("route_net_class_mismatch",
                $"Net '{request.NetName}' uses effective netclass '{effectiveNetClass.Name}', not '{request.NetClassName}'.");

        GetItemsResponse response = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ =
            {
                KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia,
                KiCadObjectType.KotPcbPad, KiCadObjectType.KotPcbZone, KiCadObjectType.KotPcbShape,
                KiCadObjectType.KotPcbFootprint
            }
        }, cancellationToken);
        DeleteItems ripUp = request.IncludeRipUpCandidate
            ? BuildRipUpCandidate(document, request.NetName, response.Items)
            : new DeleteItems { Header = new() { Document = document } };
        var obstacles = BuildObstacles(response.Items, request);
        int footprintObstacleCount = 0;
        if (request.IncludeFootprintEnvelopes)
        {
            GetBoundingBoxResponse footprintBoxes = await ReadFootprintBounds(client, document, response.Items, cancellationToken);
            var footprintObstacles = BuildFootprintObstacles(response.Items, footprintBoxes, request);
            footprintObstacleCount = footprintObstacles.Length;
            obstacles = obstacles.AddRange(footprintObstacles)
                .OrderBy(obstacle => obstacle.Kind, StringComparer.Ordinal)
                .ThenBy(obstacle => obstacle.Id, StringComparer.Ordinal).ToImmutableArray();
        }
        var after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_plan_changed", "The board changed while obstacles were being read; discard the proposal.");

        var grid = new Grid(routeBounds, request.GridNm);
        var blocked = grid.Blocked(obstacles);
        GridNode start = grid.Node(startPoint);
        GridNode goal = grid.Node(endPoint);
        object[] reportedObstacles = ReportObstacles(obstacles, request.MaxObstacleReport);
        if (blocked.Contains(start) || blocked.Contains(goal))
            return Result(new
            {
                instanceId, processEpoch = session.Epoch, document,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                routeFound = false, failureCode = "endpoint_blocked", exploredNodes = 0,
                obstacleCount = obstacles.Length, obstacleReport = (object)reportedObstacles,
                footprintObstacleCount, netValidated = true, effectiveNetClass = effectiveNetClass.Name,
                ripUpItemIds = ripUp.ItemIds.Select(item => item.Value).ToArray(),
                ripUpCandidateRequestJson = request.IncludeRipUpCandidate ? BoardJson.Formatter.Format(ripUp) : null,
                obstacleModel = "conservative-axis-aligned", completeObstacleModel = false,
                drcValidated = false, nativeCommit = false
            });

        SearchResult search = Search(grid, blocked, start, goal, request.MaxNodes);
        if (search.Path is null)
            return Result(new
            {
                instanceId, processEpoch = session.Epoch, document,
                beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
                routeFound = false, failureCode = "no_route", exploredNodes = search.ExploredNodes,
                obstacleCount = obstacles.Length, obstacleReport = (object)reportedObstacles,
                footprintObstacleCount, netValidated = true, effectiveNetClass = effectiveNetClass.Name,
                ripUpItemIds = ripUp.ItemIds.Select(item => item.Value).ToArray(),
                ripUpCandidateRequestJson = request.IncludeRipUpCandidate ? BoardJson.Formatter.Format(ripUp) : null,
                obstacleModel = "conservative-axis-aligned", completeObstacleModel = false,
                drcValidated = false, nativeCommit = false
            });

        string planId = PlanIdentity(request, expected, obstacles);
        CreateItems candidate = BuildCandidate(document, request, grid, search.Path.Value, planId);
        var points = search.Path.Value.Select(node => grid.Point(node)).ToArray();
        int bends = points.Length <= 2 ? 0 : points.Zip(points.Skip(1), (left, right) => (left, right))
            .Zip(points.Skip(2), (pair, next) => (pair.left, pair.right, next))
            .Count(turn => (turn.right.XNm - turn.left.XNm) * (turn.next.YNm - turn.right.YNm)
                         != (turn.right.YNm - turn.left.YNm) * (turn.next.XNm - turn.right.XNm));
        long pathLength = checked((long)(points.Length - 1) * request.GridNm);
        return Result(new
        {
            instanceId, processEpoch = session.Epoch, document, planId,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            routeFound = true, exploredNodes = search.ExploredNodes, routeNodeCount = points.Length,
            pathLengthNm = pathLength, bendCount = bends,
            routePoints = points.Select(point => new { xNm = point.XNm, yNm = point.YNm }),
            candidateItems = candidate.Items.Count,
            candidateRequestJson = BoardJson.Formatter.Format(candidate),
            ripUpItemIds = ripUp.ItemIds.Select(item => item.Value).ToArray(),
            ripUpCandidateRequestJson = request.IncludeRipUpCandidate ? BoardJson.Formatter.Format(ripUp) : null,
            obstacleCount = obstacles.Length, footprintObstacleCount, obstacleReport = (object)reportedObstacles,
            netValidated = true, effectiveNetClass = effectiveNetClass.Name,
            obstacleModel = "conservative-axis-aligned", completeObstacleModel = false,
            drcValidated = false, nativeCommit = false, deterministicReplay = true
        });
    });

    private static RoutePlanRequest ParseRequest(string value)
        => JsonSerializer.Deserialize<RoutePlanRequest>(value, JsonOptions)
           ?? throw new AutomationException("invalid_route_plan", "requestJson must be a JSON object.");

    private static T Parse<T>(string value, string code)
        where T : class, IMessage<T>, new()
    {
        try
        {
            return SchematicJson.Parser.Parse<T>(value);
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message);
        }
    }

    private static void ValidateRequest(RoutePlanRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.NetName) || request.NetName.Length > 512
            || request.NetClassName is null || request.NetClassName.Length > 512)
            throw new AutomationException("invalid_route_plan", "netName is required and netName/netClassName must be at most 512 characters.");
        if (request.Start is null || request.End is null || request.Bounds is null)
            throw new AutomationException("invalid_route_plan", "start, end and explicit bounds are required.");
        if (request.WidthNm <= 0 || request.ClearanceNm < 0 || request.GridNm <= 0)
            throw new AutomationException("invalid_route_plan", "widthNm and gridNm must be positive; clearanceNm cannot be negative.");
        if (request.GridNm > 10_000_000 || request.WidthNm > long.MaxValue - request.ClearanceNm)
            throw new AutomationException("invalid_route_plan", "Route dimensions exceed the bounded planner range.");
        if (request.Bounds.MinXNm >= request.Bounds.MaxXNm || request.Bounds.MinYNm >= request.Bounds.MaxYNm)
            throw new AutomationException("invalid_route_plan", "bounds must have positive width and height.");
        if (!Inside(request.Start, request.Bounds) || !Inside(request.End, request.Bounds))
            throw new AutomationException("invalid_route_plan", "start and end must lie within the explicit bounds.");
        if ((request.Start.XNm - request.Bounds.MinXNm) % request.GridNm != 0
            || (request.Start.YNm - request.Bounds.MinYNm) % request.GridNm != 0
            || (request.End.XNm - request.Bounds.MinXNm) % request.GridNm != 0
            || (request.End.YNm - request.Bounds.MinYNm) % request.GridNm != 0)
            throw new AutomationException("invalid_route_plan", "start and end must lie on the requested grid.");
        if (request.Start.XNm == request.End.XNm && request.Start.YNm == request.End.YNm)
            throw new AutomationException("invalid_route_plan", "start and end must be different points.");
        if (!TryCopperLayer(request.Layer, out _))
            throw new AutomationException("invalid_route_plan", "layer must name a copper layer such as F.Cu, In1.Cu or B.Cu.");
        if (request.MaxNodes is < 1 or > 2_000_000)
            throw new AutomationException("invalid_route_plan", "maxNodes must be between 1 and 2,000,000.");
        long width = checked((request.Bounds.MaxXNm - request.Bounds.MinXNm) / request.GridNm + 1);
        long height = checked((request.Bounds.MaxYNm - request.Bounds.MinYNm) / request.GridNm + 1);
        if (width <= 0 || height <= 0 || width > 4_000 || height > 4_000 || width * height > request.MaxNodes)
            throw new AutomationException("invalid_route_plan", "The explicit bounds and grid produce too many search cells.");
        request.MaxObstacleReport = Math.Clamp(request.MaxObstacleReport, 0, 4096);
    }

    private static bool MatchesNetClass(Kiapi.Common.Project.NetClass effective, string requested)
        => string.Equals(effective.Name, requested, StringComparison.Ordinal)
           || effective.Constituents.Contains(requested, StringComparer.Ordinal);

    private static async Task<GetBoundingBoxResponse> ReadFootprintBounds(
        NativeClient client, DocumentSpecifier document, IEnumerable<ProtoAny> packedItems,
        CancellationToken cancellationToken)
    {
        var ids = packedItems
            .Where(item => item.Is(FootprintInstance.Descriptor))
            .Select(item => item.Unpack<FootprintInstance>().Id)
            .Where(id => !string.IsNullOrWhiteSpace(id.Value))
            .ToArray();
        if (ids.Length == 0)
            return new GetBoundingBoxResponse();

        GetBoundingBox query = new()
        {
            Header = new() { Document = document },
            Mode = BoundingBoxMode.BbmItemOnly
        };
        query.Items.Add(ids.Select(id => id.Clone()));
        return await client.InvokeAsync<GetBoundingBox, GetBoundingBoxResponse>(query, cancellationToken);
    }

    private static ImmutableArray<Obstacle> BuildFootprintObstacles(
        IEnumerable<ProtoAny> packedItems, GetBoundingBoxResponse boxes, RoutePlanRequest request)
    {
        ProtoAny[] items = packedItems.ToArray();
        var footprints = items
            .Where(item => item.Is(FootprintInstance.Descriptor))
            .Select(item => item.Unpack<FootprintInstance>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Id.Value))
            .ToDictionary(item => item.Id.Value, StringComparer.Ordinal);
        var targetNetFootprints = items
            .Where(item => item.Is(Pad.Descriptor))
            .Select(item => item.Unpack<Pad>())
            .Where(pad => SameNet(pad.Net, request.NetName) && !string.IsNullOrWhiteSpace(pad.Parent?.Value))
            .Select(pad => pad.Parent.Value)
            .ToHashSet(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<Obstacle>();
        long margin = checked(request.ClearanceNm + request.WidthNm / 2);
        if (boxes.Items.Count != boxes.Boxes.Count
            || !footprints.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(boxes.Items.Select(id => id.Value)))
            throw new AutomationException("route_footprint_bounds_incomplete", "The native footprint envelope observation omitted one or more requested footprints.");
        int count = boxes.Items.Count;
        for (int index = 0; index < count; ++index)
        {
            string id = boxes.Items[index].Value;
            if (!footprints.TryGetValue(id, out FootprintInstance? footprint))
                continue;
            BoundsValue bounds = BoxBounds(boxes.Boxes[index], margin);
            if (bounds.MinXNm == bounds.MaxXNm && bounds.MinYNm == bounds.MaxYNm)
                continue;
            if (!Intersects(bounds, request.Bounds!))
                continue;

            // The endpoint is normally a pad inside its own footprint.  Keep
            // that component's envelope available when it owns the requested
            // net; the separate native pad obstacles still protect other nets.
            // Other footprints remain conservative full-envelope obstacles,
            // covering courtyards/body geometry that is not represented by a
            // copper primitive in the board snapshot.
            if (OwnsRouteEndpoint(footprint, bounds, request, targetNetFootprints))
                continue;

            result.Add(new("footprint", id, "", BoardLayer.BlUnknown, bounds,
                "conservative footprint envelope including courtyard/body"));
        }
        return result.ToImmutable();
    }

    private static bool OwnsRouteEndpoint(FootprintInstance footprint, BoundsValue bounds, RoutePlanRequest request,
        IReadOnlySet<string> targetNetFootprints)
    {
        bool endpointInside(RoutePoint point) => Inside(point, new RouteBounds
        {
            MinXNm = bounds.MinXNm, MinYNm = bounds.MinYNm,
            MaxXNm = bounds.MaxXNm, MaxYNm = bounds.MaxYNm
        });
        if (!endpointInside(request.Start!) && !endpointInside(request.End!))
            return false;
        return targetNetFootprints.Contains(footprint.Id.Value)
            || footprint.Definition?.Items.Any(item => item.Is(Pad.Descriptor)
                && SameNet(item.Unpack<Pad>().Net, request.NetName)) == true;
    }

    private static bool Inside(RoutePoint point, RouteBounds bounds)
        => point.XNm >= bounds.MinXNm && point.XNm <= bounds.MaxXNm
        && point.YNm >= bounds.MinYNm && point.YNm <= bounds.MaxYNm;

    private static ImmutableArray<Obstacle> BuildObstacles(IEnumerable<ProtoAny> packedItems, RoutePlanRequest request)
    {
        TryCopperLayer(request.Layer, out BoardLayer layer);
        var result = ImmutableArray.CreateBuilder<Obstacle>();
        foreach (ProtoAny packed in packedItems)
        {
            if (packed.Is(Track.Descriptor))
            {
                Track track = packed.Unpack<Track>();
                if (!SameLayer(track.Layer, layer) || SameNet(track.Net, request.NetName)) continue;
                long margin = Margin(request, track.Width.ValueNm);
                result.Add(new("track", track.Id.Value, track.Net.Name, track.Layer, SegmentBounds(track.Start, track.End, margin), "different-net track"));
            }
            else if (packed.Is(Arc.Descriptor))
            {
                Arc arc = packed.Unpack<Arc>();
                if (!SameLayer(arc.Layer, layer) || SameNet(arc.Net, request.NetName)) continue;
                long margin = Margin(request, arc.Width.ValueNm);
                result.Add(new("arc", arc.Id.Value, arc.Net.Name, arc.Layer, PointBounds([arc.Start, arc.Mid, arc.End], margin), "different-net arc"));
            }
            else if (packed.Is(Via.Descriptor))
            {
                Via via = packed.Unpack<Via>();
                if (!LayersContain(via.PadStack?.Layers, layer) || SameNet(via.Net, request.NetName)) continue;
                long radius = Math.Max(PadRadius(via.PadStack), via.PadStack?.Drill?.Diameter?.XNm / 2 ?? 0);
                result.Add(new("via", via.Id.Value, via.Net.Name, layer, PointBounds([via.Position], checked(radius + request.ClearanceNm + request.WidthNm / 2)), "different-net via"));
            }
            else if (packed.Is(Pad.Descriptor))
            {
                Pad pad = packed.Unpack<Pad>();
                if (!LayersContain(pad.PadStack?.Layers, layer) || SameNet(pad.Net, request.NetName)) continue;
                long radius = PadRadius(pad.PadStack);
                result.Add(new("pad", pad.Id.Value, pad.Net.Name, layer, PointBounds([pad.Position], checked(radius + request.ClearanceNm + request.WidthNm / 2)), "different-net pad"));
            }
            else if (packed.Is(Zone.Descriptor))
            {
                Zone zone = packed.Unpack<Zone>();
                if (!LayersContain(zone.Layers, layer) || zone.Outline?.Polygons.Count == 0) continue;
                string net = zone.CopperSettings?.Net?.Name ?? "";
                bool keepout = zone.Type == ZoneType.ZtRuleArea;
                if (!keepout && SameNet(new Net { Name = net }, request.NetName)) continue;
                long extra = zone.CopperSettings?.Clearance?.ValueNm ?? 0;
                result.Add(new("zone", zone.Id.Value, net, layer, PolySetBounds(zone.Outline!, checked(extra + request.ClearanceNm + request.WidthNm / 2)),
                    keepout ? "rule-area keepout" : "different-net copper zone"));
            }
            else if (packed.Is(BoardGraphicShape.Descriptor))
            {
                BoardGraphicShape shape = packed.Unpack<BoardGraphicShape>();
                if (!SameLayer(shape.Layer, layer) || SameNet(shape.Net, request.NetName)) continue;
                if (TryGraphicBounds(shape.Shape, Margin(request, shape.Shape.Attributes?.Stroke?.Width?.ValueNm ?? 0), out BoundsValue bounds))
                    result.Add(new("shape", shape.Id.Value, shape.Net.Name, shape.Layer, bounds, "copper graphic"));
            }
        }
        return result.Where(o => Intersects(o.Bounds, request.Bounds!)).OrderBy(o => o.Kind, StringComparer.Ordinal)
            .ThenBy(o => o.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static object[] ReportObstacles(ImmutableArray<Obstacle> obstacles, int max)
        => obstacles.Take(max).Select(o => (object)new
        {
            kind = o.Kind, id = o.Id, netName = o.NetName, layer = o.Layer.ToString(),
            bounds = new { minXNm = o.Bounds.MinXNm, minYNm = o.Bounds.MinYNm, maxXNm = o.Bounds.MaxXNm, maxYNm = o.Bounds.MaxYNm },
            reason = o.Reason
        }).ToArray();

    private static SearchResult Search(Grid grid, HashSet<GridNode> blocked, GridNode start, GridNode goal, int maxNodes)
    {
        var queue = new PriorityQueue<GridNode, (int Score, int Sequence)>();
        var distance = new Dictionary<GridNode, int> { [start] = 0 };
        var parent = new Dictionary<GridNode, GridNode>();
        int sequence = 0, explored = 0;
        queue.Enqueue(start, (Heuristic(start, goal), sequence++));
        while (queue.TryDequeue(out GridNode current, out _))
        {
            if (++explored > maxNodes) break;
            if (current == goal)
            {
                var path = new List<GridNode> { current };
                while (parent.TryGetValue(path[^1], out GridNode previous)) path.Add(previous);
                path.Reverse();
                return new(path.ToImmutableArray(), explored);
            }
            foreach (GridNode direction in Directions)
            {
                GridNode next = new(current.X + direction.X, current.Y + direction.Y);
                if (!grid.Contains(next) || blocked.Contains(next)) continue;
                int nextDistance = distance[current] + 1;
                if (distance.TryGetValue(next, out int known) && known <= nextDistance) continue;
                distance[next] = nextDistance; parent[next] = current;
                queue.Enqueue(next, (nextDistance + Heuristic(next, goal), sequence++));
            }
        }
        return new(null, explored);
    }

    private static CreateItems BuildCandidate(DocumentSpecifier document, RoutePlanRequest request,
        Grid grid, ImmutableArray<GridNode> path, string planId)
    {
        TryCopperLayer(request.Layer, out BoardLayer layer);
        var points = path.Select(node => grid.Point(node)).ToArray();
        var candidate = new CreateItems { Header = new() { Document = document } };
        int segment = 0, start = 0;
        for (int index = 1; index <= points.Length; index++)
        {
            bool atEnd = index == points.Length;
            bool turn = !atEnd && index + 1 < points.Length
                && (points[index].XNm - points[index - 1].XNm) * (points[index + 1].YNm - points[index].YNm)
                 != (points[index].YNm - points[index - 1].YNm) * (points[index + 1].XNm - points[index].XNm);
            if (!atEnd && !turn) continue;
            Kiapi.Common.Types.Vector2 from = points[start], to = points[atEnd ? index - 1 : index];
            string id = DeterministicId(planId, segment++);
            var track = new Track { Id = new() { Value = id }, Start = new() { XNm = from.XNm, YNm = from.YNm },
                End = new() { XNm = to.XNm, YNm = to.YNm }, Width = new() { ValueNm = request.WidthNm },
                Layer = layer, Net = new() { Name = request.NetName } };
            track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_plan_id", Value = planId });
            track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_plan_segment", Value = segment.ToString(CultureInfo.InvariantCulture) });
            candidate.Items.Add(ProtoAny.Pack(track));
            start = atEnd ? index - 1 : index;
        }
        return candidate;
    }

    private static DeleteItems BuildRipUpCandidate(DocumentSpecifier document, string netName,
        IEnumerable<ProtoAny> items)
    {
        var candidate = new DeleteItems { Header = new() { Document = document } };
        foreach (ProtoAny packed in items)
        {
            string? id = packed.Is(Track.Descriptor) && SameNet(packed.Unpack<Track>().Net, netName)
                ? packed.Unpack<Track>().Id.Value
                : packed.Is(Arc.Descriptor) && SameNet(packed.Unpack<Arc>().Net, netName)
                    ? packed.Unpack<Arc>().Id.Value
                    : packed.Is(Via.Descriptor) && SameNet(packed.Unpack<Via>().Net, netName)
                        ? packed.Unpack<Via>().Id.Value : null;
            if (!string.IsNullOrWhiteSpace(id) && Guid.TryParseExact(id, "D", out var identity) && identity != Guid.Empty)
                candidate.ItemIds.Add(new KIID { Value = id });
        }
        return candidate;
    }

    private static string PlanIdentity(RoutePlanRequest request, DocumentLifecycleState state, ImmutableArray<Obstacle> obstacles)
    {
        string canonical = JsonSerializer.Serialize(request, JsonOptions) + "\n" + state.StateSha256 + "\n"
            + string.Join("\n", obstacles.Select(o => $"{o.Kind}|{o.Id}|{o.Layer}|{o.Bounds.MinXNm},{o.Bounds.MinYNm},{o.Bounds.MaxXNm},{o.Bounds.MaxYNm}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static long Margin(RoutePlanRequest request, long itemWidth)
        => checked(Math.Max(0, itemWidth) / 2 + request.WidthNm / 2 + request.ClearanceNm);

    private static long PadRadius(PadStack? stack)
    {
        if (stack is null) return 0;
        long radius = 0;
        foreach (PadStackLayer layer in stack.CopperLayers)
            radius = Math.Max(radius, Math.Max(Math.Abs(layer.Size.XNm), Math.Abs(layer.Size.YNm)) / 2
                + Math.Max(Math.Abs(layer.Offset.XNm), Math.Abs(layer.Offset.YNm)));
        return radius;
    }

    private static BoundsValue SegmentBounds(Vector2 start, Vector2 end, long margin)
        => new(Math.Min(start.XNm, end.XNm) - margin, Math.Min(start.YNm, end.YNm) - margin,
            Math.Max(start.XNm, end.XNm) + margin, Math.Max(start.YNm, end.YNm) + margin);

    private static BoundsValue PointBounds(IReadOnlyList<Vector2> points, long margin)
        => new(points.Min(p => p.XNm) - margin, points.Min(p => p.YNm) - margin,
            points.Max(p => p.XNm) + margin, points.Max(p => p.YNm) + margin);

    private static BoundsValue BoxBounds(Box2 box, long margin)
    {
        long minX = Math.Min(box.Position.XNm, checked(box.Position.XNm + box.Size.XNm));
        long maxX = Math.Max(box.Position.XNm, checked(box.Position.XNm + box.Size.XNm));
        long minY = Math.Min(box.Position.YNm, checked(box.Position.YNm + box.Size.YNm));
        long maxY = Math.Max(box.Position.YNm, checked(box.Position.YNm + box.Size.YNm));
        return new(checked(minX - margin), checked(minY - margin), checked(maxX + margin), checked(maxY + margin));
    }

    private static BoundsValue PolySetBounds(PolySet poly, long margin)
    {
        var points = poly.Polygons.SelectMany(p => p.Outline.Nodes)
            .Where(n => n.GeometryCase == PolyLineNode.GeometryOneofCase.Point).Select(n => n.Point).ToArray();
        return points.Length == 0 ? new(0, 0, 0, 0) : PointBounds(points, margin);
    }

    private static bool TryGraphicBounds(GraphicShape shape, long margin, out BoundsValue bounds)
    {
        switch (shape.GeometryCase)
        {
            case GraphicShape.GeometryOneofCase.Segment:
                bounds = SegmentBounds(shape.Segment.Start, shape.Segment.End, margin); return true;
            case GraphicShape.GeometryOneofCase.Rectangle:
                bounds = new(Math.Min(shape.Rectangle.TopLeft.XNm, shape.Rectangle.BottomRight.XNm) - margin,
                    Math.Min(shape.Rectangle.TopLeft.YNm, shape.Rectangle.BottomRight.YNm) - margin,
                    Math.Max(shape.Rectangle.TopLeft.XNm, shape.Rectangle.BottomRight.XNm) + margin,
                    Math.Max(shape.Rectangle.TopLeft.YNm, shape.Rectangle.BottomRight.YNm) + margin); return true;
            case GraphicShape.GeometryOneofCase.Circle:
                long radius = Distance(shape.Circle.Center, shape.Circle.RadiusPoint);
                bounds = PointBounds([shape.Circle.Center], checked(radius + margin)); return true;
            case GraphicShape.GeometryOneofCase.Arc:
                bounds = PointBounds([shape.Arc.Start, shape.Arc.Mid, shape.Arc.End], margin); return true;
            case GraphicShape.GeometryOneofCase.Polygon:
                bounds = PolySetBounds(shape.Polygon, margin); return bounds.MinXNm != bounds.MaxXNm || bounds.MinYNm != bounds.MaxYNm;
            default:
                bounds = default; return false;
        }
    }

    private static long Distance(Vector2 a, Vector2 b)
    {
        double dx = (double)a.XNm - b.XNm, dy = (double)a.YNm - b.YNm;
        return checked((long)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy)));
    }

    private static bool Intersects(BoundsValue a, RouteBounds b)
        => a.MaxXNm >= b.MinXNm && a.MinXNm <= b.MaxXNm && a.MaxYNm >= b.MinYNm && a.MinYNm <= b.MaxYNm;

    private static bool SameNet(Net? net, string name) => net is not null && net.Name == name;

    private static bool SameLayer(BoardLayer left, BoardLayer right) => left == right;

    private static bool LayersContain(IEnumerable<BoardLayer>? layers, BoardLayer target)
        => layers is null || !layers.Any() || layers.Contains(target);

    private static bool TryCopperLayer(string value, out BoardLayer layer)
    {
        layer = value switch
        {
            "F.Cu" or "F_Cu" or "BL_F_Cu" => BoardLayer.BlFCu,
            "B.Cu" or "B_Cu" or "BL_B_Cu" => BoardLayer.BlBCu,
            _ when System.Enum.TryParse(value, true, out BoardLayer parsed) => parsed,
            _ => BoardLayer.BlUnknown
        };
        return (int)layer is >= 3 and <= 34;
    }

    private static int Heuristic(GridNode from, GridNode to) => Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y);

    private static string DeterministicId(string planId, int index)
        => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{planId}\n{index}"))[..16], bigEndian: true).ToString("D");

    private static CallToolResult Result(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value, JsonOptions);
        return new() { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or InvalidProtocolBufferException or InvalidJsonException or JsonException or IOException or OverflowException)
        {
            string code = error is AutomationException known ? known.Code : "pcb_route_plan_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message }, JsonOptions);
            return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }

    private sealed class RoutePlanRequest
    {
        public string NetName { get; set; } = "";
        public string NetClassName { get; set; } = "";
        public RoutePoint? Start { get; set; }
        public RoutePoint? End { get; set; }
        public string Layer { get; set; } = "";
        public long WidthNm { get; set; }
        public long ClearanceNm { get; set; }
        public long GridNm { get; set; } = 1_000_000;
        public RouteBounds? Bounds { get; set; }
        public int MaxNodes { get; set; } = 250_000;
        public int MaxObstacleReport { get; set; } = 512;
        public bool IncludeFootprintEnvelopes { get; set; } = true;
        public bool IncludeRipUpCandidate { get; set; }
    }

    private sealed class RoutePoint
    {
        public long XNm { get; set; }
        public long YNm { get; set; }
    }

    private sealed class RouteBounds
    {
        public long MinXNm { get; set; }
        public long MinYNm { get; set; }
        public long MaxXNm { get; set; }
        public long MaxYNm { get; set; }
    }

    private readonly record struct GridNode(int X, int Y);
    private readonly record struct BoundsValue(long MinXNm, long MinYNm, long MaxXNm, long MaxYNm);
    private readonly record struct Obstacle(string Kind, string Id, string NetName, BoardLayer Layer, BoundsValue Bounds, string Reason);
    private readonly record struct SearchResult(ImmutableArray<GridNode>? Path, int ExploredNodes);

    private sealed class Grid(RouteBounds bounds, long size)
    {
        public int Width { get; } = checked((int)((bounds.MaxXNm - bounds.MinXNm) / size + 1));
        public int Height { get; } = checked((int)((bounds.MaxYNm - bounds.MinYNm) / size + 1));
        public GridNode Node(RoutePoint point) => new(checked((int)((point.XNm - bounds.MinXNm) / size)), checked((int)((point.YNm - bounds.MinYNm) / size)));
        public Vector2 Point(GridNode node) => new() { XNm = checked(bounds.MinXNm + (long)node.X * size), YNm = checked(bounds.MinYNm + (long)node.Y * size) };
        public bool Contains(GridNode node) => node.X >= 0 && node.X < Width && node.Y >= 0 && node.Y < Height;
        public HashSet<GridNode> Blocked(ImmutableArray<Obstacle> obstacles)
        {
            var blocked = new HashSet<GridNode>();
            foreach (Obstacle obstacle in obstacles)
            {
                long minX = Math.Max(obstacle.Bounds.MinXNm, bounds.MinXNm), maxX = Math.Min(obstacle.Bounds.MaxXNm, bounds.MaxXNm);
                long minY = Math.Max(obstacle.Bounds.MinYNm, bounds.MinYNm), maxY = Math.Min(obstacle.Bounds.MaxYNm, bounds.MaxYNm);
                if (minX > maxX || minY > maxY) continue;
                int lowX = checked((int)((minX - bounds.MinXNm) / size)), highX = checked((int)((maxX - bounds.MinXNm) / size));
                int lowY = checked((int)((minY - bounds.MinYNm) / size)), highY = checked((int)((maxY - bounds.MinYNm) / size));
                for (int x = lowX; x <= highX; ++x)
                    for (int y = lowY; y <= highY; ++y) blocked.Add(new(x, y));
            }
            return blocked;
        }
    }
}
