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
/// Deterministic multilayer route proposals.  The planner is deliberately read-only:
/// it produces the exact native CreateItems payload and leaves DRC and mutation to the
/// checked route path.  The algorithm uses a bounded 4-way grid and explicit via
/// definitions, so a replay receives the same geometry for the same board state.
/// </summary>
[McpServerToolType]
public sealed class PcbMultilayerRoutingTools(InstanceRegistry registry)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    [McpServerTool(Name = "kicad_pcb_route_plan_multilayer", ReadOnly = true),
     Description("Build a deterministic read-only multilayer PCB route proposal from one checked board observation. requestJson contains netName, optional netClassName, start/end {xNm,yNm}, startLayer/endLayer, ordered enabled copper layers[], widthNm, clearanceNm, gridNm, explicit bounds, maxNodes, maxVias, and an explicit vias[] list with type/startLayer/endLayer/diameterNm/drillNm/costNm. The planner validates native layers and netclass, models per-layer copper obstacles and via-span clearances, and returns exact Track/Via CreateItems protobuf JSON plus full before/after lifecycle state. Proposals are never claimed DRC-clean and are never committed by this tool.")]
    [KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "explicit PCB lifecycle state, enabled copper layers, deterministic multilayer proposal"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Plan(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_route_plan_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_route_plan_target", "Multilayer route planning requires an explicit PCB document.");

        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_route_plan_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_route_plan_state", "The expected observation must identify the same PCB and native process epoch.");

        MultilayerRoutePlanner.MultilayerRouteRequest request = ParseRequest(requestJson);
        var client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before route planning.");

        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before route planning; refresh the lifecycle checkpoint.");

        BoardEnabledLayersResponse enabledResponse = await client.InvokeAsync<GetBoardEnabledLayers, BoardEnabledLayersResponse>(
            new() { Board = document }, cancellationToken);
        ImmutableArray<BoardLayer> enabledCopper = enabledResponse.Layers
            .Where(MultilayerRoutePlanner.IsCopperLayer)
            .Distinct()
            .OrderBy(layer => (int)layer)
            .ToImmutableArray();
        if (enabledCopper.Length == 0)
            throw new AutomationException("route_no_enabled_copper", "The native board has no enabled copper layers.");

        MultilayerRoutePlanner.ValidatedRequest validated = MultilayerRoutePlanner.Validate(request, enabledCopper);

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

        GetItemsResponse items = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ =
            {
                KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia,
                KiCadObjectType.KotPcbPad, KiCadObjectType.KotPcbZone, KiCadObjectType.KotPcbShape
            }
        }, cancellationToken);

        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_plan_changed", "The board changed while obstacles were being read; discard the proposal.");

        MultilayerRoutePlanner.PlanResult plan = MultilayerRoutePlanner.Plan(
            validated, items.Items, enabledCopper, document, expected);
        var result = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["processEpoch"] = session.Epoch,
            ["document"] = document,
            ["planId"] = plan.PlanId,
            ["beforeState"] = SchematicJson.Formatter.Format(before),
            ["afterState"] = SchematicJson.Formatter.Format(after),
            ["routeFound"] = plan.RouteFound,
            ["failureCode"] = plan.FailureCode,
            ["exploredNodes"] = plan.ExploredNodes,
            ["obstacleCount"] = plan.ObstacleCount,
            ["obstacleReport"] = plan.ObstacleReport,
            ["enabledCopperLayers"] = enabledCopper.Select(layer => MultilayerRoutePlanner.LayerName(layer)).ToArray(),
            ["netValidated"] = true,
            ["effectiveNetClass"] = effectiveNetClass.Name,
            ["obstacleModel"] = "conservative-axis-aligned",
            ["completeObstacleModel"] = false,
            ["drcValidated"] = false,
            ["nativeCommit"] = false,
            ["deterministicReplay"] = true,
            ["limitations"] = new[] { "proposal_only", "drc_not_validated", "manufacturing_constraints_not_inferred" }
        };

        if (!plan.RouteFound)
        {
            result["candidateRequestJson"] = null;
            result["routePoints"] = Array.Empty<object>();
            result["layersUsed"] = Array.Empty<string>();
            result["trackCount"] = 0;
            result["viaCount"] = 0;
            result["pathLengthNm"] = 0L;
            result["viaCostNm"] = 0L;
            result["totalCostNm"] = 0L;
            result["bendCount"] = 0;
            return Result(result);
        }

        result["candidateRequestJson"] = BoardJson.Formatter.Format(plan.Candidate!);
        result["routePoints"] = plan.Path!.Value.ToArray().Select(point => new { xNm = point.XNm, yNm = point.YNm, layer = MultilayerRoutePlanner.LayerName(point.Layer) }).ToArray();
        result["layersUsed"] = plan.LayersUsed.Select(layer => MultilayerRoutePlanner.LayerName(layer)).ToArray();
        result["trackCount"] = plan.TrackCount;
        result["viaCount"] = plan.ViaCount;
        result["pathLengthNm"] = plan.PathLengthNm;
        result["viaCostNm"] = plan.ViaCostNm;
        result["totalCostNm"] = plan.TotalCostNm;
        result["bendCount"] = plan.BendCount;
        return Result(result);
    });

    private static MultilayerRoutePlanner.MultilayerRouteRequest ParseRequest(string value)
        => JsonSerializer.Deserialize<MultilayerRoutePlanner.MultilayerRouteRequest>(value, JsonOptions)
           ?? throw new AutomationException("invalid_multilayer_route_plan", "requestJson must be a JSON object.");

    private static T Parse<T>(string value, string code)
        where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(value); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static bool MatchesNetClass(Kiapi.Common.Project.NetClass effective, string requested)
        => string.Equals(effective.Name, requested, StringComparison.Ordinal)
           || effective.Constituents.Contains(requested, StringComparer.Ordinal);

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
            string code = error is AutomationException known ? known.Code : "pcb_multilayer_route_plan_failed";
            JsonElement structured = JsonSerializer.SerializeToElement(new { errorCode = code, errorMessage = error.Message }, JsonOptions);
            return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}

internal static class MultilayerRoutePlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
    private static readonly GridStep[] Directions =
    [
        new(1, 0), new(0, 1), new(-1, 0), new(0, -1)
    ];

    internal sealed class ValidatedRequest
    {
        public required string NetName { get; init; }
        public string NetClassName { get; init; } = "";
        public required MlPoint Start { get; init; }
        public required MlPoint End { get; init; }
        public required BoardLayer StartLayer { get; init; }
        public required BoardLayer EndLayer { get; init; }
        public required ImmutableArray<BoardLayer> Layers { get; init; }
        public required long WidthNm { get; init; }
        public required long ClearanceNm { get; init; }
        public required long GridNm { get; init; }
        public required MlBounds Bounds { get; init; }
        public required int MaxNodes { get; init; }
        public required int MaxVias { get; init; }
        public required ImmutableArray<MlViaSpec> Vias { get; init; }
    }

    internal sealed class PlanResult
    {
        public bool RouteFound { get; init; }
        public string? FailureCode { get; init; }
        public string PlanId { get; init; } = "";
        public int ExploredNodes { get; init; }
        public int ObstacleCount { get; init; }
        public object[] ObstacleReport { get; init; } = [];
        public CreateItems? Candidate { get; init; }
        public ImmutableArray<MlPathPoint>? Path { get; init; }
        public ImmutableArray<BoardLayer> LayersUsed { get; init; } = [];
        public int TrackCount { get; init; }
        public int ViaCount { get; init; }
        public long PathLengthNm { get; init; }
        public long ViaCostNm { get; init; }
        public long TotalCostNm { get; init; }
        public int BendCount { get; init; }
    }

    internal sealed class MultilayerRouteRequest
    {
        public string NetName { get; set; } = "";
        public string NetClassName { get; set; } = "";
        public MlPoint? Start { get; set; }
        public MlPoint? End { get; set; }
        public string StartLayer { get; set; } = "";
        public string EndLayer { get; set; } = "";
        public string[] Layers { get; set; } = [];
        public long WidthNm { get; set; }
        public long ClearanceNm { get; set; }
        public long GridNm { get; set; } = 1_000_000;
        public MlBounds? Bounds { get; set; }
        public int MaxNodes { get; set; } = 250_000;
        public int MaxVias { get; set; } = 8;
        public MlViaSpecInput[] Vias { get; set; } = [];
    }

    internal sealed class MlViaSpecInput
    {
        public string Type { get; set; } = "";
        public string StartLayer { get; set; } = "";
        public string EndLayer { get; set; } = "";
        public long DiameterNm { get; set; }
        public long DrillNm { get; set; }
        public long CostNm { get; set; }
    }

    internal sealed class MlPoint
    {
        public long XNm { get; set; }
        public long YNm { get; set; }
    }

    internal sealed class MlBounds
    {
        public long MinXNm { get; set; }
        public long MinYNm { get; set; }
        public long MaxXNm { get; set; }
        public long MaxYNm { get; set; }
    }

    internal readonly record struct MlViaSpec(ViaType Type, BoardLayer StartLayer, BoardLayer EndLayer,
        long DiameterNm, long DrillNm, long CostNm, string TypeName);
    internal readonly record struct MlPathPoint(long XNm, long YNm, BoardLayer Layer);
    internal readonly record struct MlBoundsValue(long MinXNm, long MinYNm, long MaxXNm, long MaxYNm);
    internal readonly record struct MlObstacle(string Kind, string Id, string NetName, BoardLayer Layer,
        MlBoundsValue Bounds, string Reason);
    private readonly record struct GridStep(int X, int Y);
    private readonly record struct GridNode(int X, int Y, int Layer, int ViaCount);
    private readonly record struct GridCell(int X, int Y, int Layer);
    private readonly record struct SearchResult(ImmutableArray<GridNode>? Path, int ExploredNodes, long Cost);

    internal static ValidatedRequest Validate(MultilayerRouteRequest request, ImmutableArray<BoardLayer> enabledCopper)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.NetName) || request.NetName.Length > 512
            || request.NetClassName is null || request.NetClassName.Length > 512 || request.Layers is null)
            throw new AutomationException("invalid_multilayer_route_plan", "netName is required and net names/classes must be at most 512 characters.");
        if (request.Start is null || request.End is null || request.Bounds is null)
            throw new AutomationException("invalid_multilayer_route_plan", "start, end and explicit bounds are required.");
        if (string.IsNullOrWhiteSpace(request.StartLayer) || string.IsNullOrWhiteSpace(request.EndLayer))
            throw new AutomationException("invalid_multilayer_route_plan", "startLayer and endLayer are required.");
        if (request.WidthNm <= 0 || request.ClearanceNm < 0 || request.GridNm <= 0
            || request.WidthNm > long.MaxValue - request.ClearanceNm)
            throw new AutomationException("invalid_multilayer_route_plan", "widthNm and gridNm must be positive; clearanceNm cannot be negative.");
        if (request.Bounds.MinXNm >= request.Bounds.MaxXNm || request.Bounds.MinYNm >= request.Bounds.MaxYNm)
            throw new AutomationException("invalid_multilayer_route_plan", "bounds must have positive width and height.");
        if (!Inside(request.Start, request.Bounds) || !Inside(request.End, request.Bounds))
            throw new AutomationException("invalid_multilayer_route_plan", "start and end must lie within the explicit bounds.");
        if ((request.Start.XNm - request.Bounds.MinXNm) % request.GridNm != 0
            || (request.Start.YNm - request.Bounds.MinYNm) % request.GridNm != 0
            || (request.End.XNm - request.Bounds.MinXNm) % request.GridNm != 0
            || (request.End.YNm - request.Bounds.MinYNm) % request.GridNm != 0)
            throw new AutomationException("invalid_multilayer_route_plan", "start and end must lie on the requested grid.");
        if (request.Start.XNm == request.End.XNm && request.Start.YNm == request.End.YNm
            && string.Equals(request.StartLayer, request.EndLayer, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("invalid_multilayer_route_plan", "start and end must differ by point or layer.");
        if (request.MaxNodes is < 1 or > 2_000_000 || request.MaxVias is < 0 or > 128)
            throw new AutomationException("invalid_multilayer_route_plan", "maxNodes must be 1..2,000,000 and maxVias must be 0..128.");

        long width = checked((request.Bounds.MaxXNm - request.Bounds.MinXNm) / request.GridNm + 1);
        long height = checked((request.Bounds.MaxYNm - request.Bounds.MinYNm) / request.GridNm + 1);
        if (width <= 0 || height <= 0 || width > 4_000 || height > 4_000
            || checked(width * height * Math.Max(1, request.Layers.Length)) > request.MaxNodes * 4L)
            throw new AutomationException("invalid_multilayer_route_plan", "The explicit bounds and layers produce too many search cells.");

        if (!TryCopperLayer(request.StartLayer, out BoardLayer startLayer)
            || !TryCopperLayer(request.EndLayer, out BoardLayer endLayer))
            throw new AutomationException("invalid_multilayer_route_plan", "startLayer and endLayer must name copper layers.");
        ImmutableArray<BoardLayer> layers = ParseLayers(request.Layers, enabledCopper);
        if (!layers.Contains(startLayer) || !layers.Contains(endLayer))
            throw new AutomationException("invalid_multilayer_route_plan", "startLayer and endLayer must be in the ordered layers list.");

        var vias = ImmutableArray.CreateBuilder<MlViaSpec>();
        foreach (MlViaSpecInput input in request.Vias ?? [])
        {
            if (input is null || !TryParseViaType(input.Type, out ViaType type)
                || !TryCopperLayer(input.StartLayer, out BoardLayer viaStart)
                || !TryCopperLayer(input.EndLayer, out BoardLayer viaEnd))
                throw new AutomationException("invalid_multilayer_via", "Each via needs a supported type and copper start/end layers.");
            if (!enabledCopper.Contains(viaStart) || !enabledCopper.Contains(viaEnd)
                || viaStart == viaEnd || input.DiameterNm <= 0 || input.DrillNm <= 0 || input.DrillNm > input.DiameterNm
                || input.CostNm < 0)
                throw new AutomationException("invalid_multilayer_via", "Via layers must be distinct enabled copper layers; drill/diameter/cost are invalid.");
            ValidateViaSpan(type, viaStart, viaEnd, enabledCopper);
            if (vias.Any(existing => (existing.StartLayer == viaStart && existing.EndLayer == viaEnd)
                || (existing.StartLayer == viaEnd && existing.EndLayer == viaStart)))
                throw new AutomationException("invalid_multilayer_via", "Only one explicit via definition per layer pair is currently supported.");
            vias.Add(new(type, viaStart, viaEnd, input.DiameterNm, input.DrillNm, input.CostNm, input.Type));
        }

        return new ValidatedRequest
        {
            NetName = request.NetName,
            NetClassName = request.NetClassName,
            Start = request.Start,
            End = request.End,
            StartLayer = startLayer,
            EndLayer = endLayer,
            Layers = layers,
            WidthNm = request.WidthNm,
            ClearanceNm = request.ClearanceNm,
            GridNm = request.GridNm,
            Bounds = request.Bounds,
            MaxNodes = request.MaxNodes,
            MaxVias = request.MaxVias,
            Vias = vias.ToImmutable()
        };
    }

    internal static PlanResult Plan(ValidatedRequest request, IEnumerable<ProtoAny> packedItems,
        ImmutableArray<BoardLayer> enabledCopper, DocumentSpecifier document, DocumentLifecycleState state)
    {
        ImmutableArray<MlObstacle> obstacles = BuildObstacles(packedItems, request, enabledCopper);
        string planId = PlanIdentity(request, state, obstacles);
        object[] report = obstacles.Take(512).Select(obstacle => (object)new
        {
            kind = obstacle.Kind,
            id = obstacle.Id,
            netName = obstacle.NetName,
            layer = LayerName(obstacle.Layer),
            bounds = new { minXNm = obstacle.Bounds.MinXNm, minYNm = obstacle.Bounds.MinYNm, maxXNm = obstacle.Bounds.MaxXNm, maxYNm = obstacle.Bounds.MaxYNm },
            reason = obstacle.Reason
        }).ToArray();

        var grid = new MlGrid(request.Bounds, request.GridNm, request.Layers, enabledCopper);
        var blocked = BuildBlocked(grid, obstacles, request);
        GridNode startBase = grid.Node(request.Start), goalBase = grid.Node(request.End);
        GridNode start = new(startBase.X, startBase.Y, grid.LayerIndex(request.StartLayer), 0);
        GridNode goal = new(goalBase.X, goalBase.Y, grid.LayerIndex(request.EndLayer), 0);
        if (blocked.Contains(new(start.X, start.Y, start.Layer)) || blocked.Contains(new(goal.X, goal.Y, goal.Layer)))
            return Failure(planId, "endpoint_blocked", 0, obstacles.Length, report);

        SearchResult search = Search(grid, blocked, start, goal, request, obstacles);
        if (search.Path is null)
            return Failure(planId, "no_route", search.ExploredNodes, obstacles.Length, report);

        ImmutableArray<MlPathPoint> path = search.Path.Value.Select(node => new MlPathPoint(
            grid.Point(node.X, node.Y).XNm, grid.Point(node.X, node.Y).YNm, request.Layers[node.Layer])).ToImmutableArray();
        CreateItems candidate = BuildCandidate(document, request, path, planId, enabledCopper);
        int bends = CountBends(path);
        int vias = path.Zip(path.Skip(1), (a, b) => (a, b)).Count(pair => pair.a.Layer != pair.b.Layer);
        long pathLength = 0, viaCost = 0;
        var usedLayers = ImmutableArray.CreateBuilder<BoardLayer>();
        foreach (MlPathPoint point in path)
            if (!usedLayers.Contains(point.Layer)) usedLayers.Add(point.Layer);
        for (int index = 1; index < path.Length; index++)
        {
            MlPathPoint previous = path[index - 1], current = path[index];
            if (previous.Layer == current.Layer) pathLength = checked(pathLength + Distance(previous, current));
            else
            {
                MlViaSpec spec = request.Vias.First(v => (v.StartLayer == previous.Layer && v.EndLayer == current.Layer)
                    || (v.StartLayer == current.Layer && v.EndLayer == previous.Layer));
                viaCost = checked(viaCost + spec.CostNm);
            }
        }
        return new PlanResult
        {
            RouteFound = true, PlanId = planId, ExploredNodes = search.ExploredNodes,
            ObstacleCount = obstacles.Length, ObstacleReport = report, Candidate = candidate,
            Path = path, LayersUsed = usedLayers.ToImmutable(), TrackCount = candidate.Items.Count(item => item.Is(Track.Descriptor)),
            ViaCount = vias, PathLengthNm = pathLength, ViaCostNm = viaCost, TotalCostNm = checked(pathLength + viaCost), BendCount = bends
        };
    }

    private static PlanResult Failure(string planId, string failure, int explored, int obstacleCount, object[] report)
        => new() { RouteFound = false, FailureCode = failure, PlanId = planId, ExploredNodes = explored,
            ObstacleCount = obstacleCount, ObstacleReport = report };

    private static ImmutableHashSet<GridCell> BuildBlocked(MlGrid grid, ImmutableArray<MlObstacle> obstacles, ValidatedRequest request)
    {
        var blocked = ImmutableHashSet.CreateBuilder<GridCell>();
        foreach (MlObstacle obstacle in obstacles)
        {
            int layer = grid.LayerIndex(obstacle.Layer);
            if (layer < 0) continue;
            foreach (GridNode node in grid.CellsIntersecting(obstacle.Bounds, layer))
                blocked.Add(new(node.X, node.Y, node.Layer));
        }
        return blocked.ToImmutable();
    }

    private static SearchResult Search(MlGrid grid, ImmutableHashSet<GridCell> blocked, GridNode start,
        GridNode goal, ValidatedRequest request, ImmutableArray<MlObstacle> obstacles)
    {
        var queue = new PriorityQueue<GridNode, (long Score, int X, int Y, int Layer, int ViaCount, int Sequence)>();
        var distance = new Dictionary<GridNode, long> { [start] = 0 };
        var parent = new Dictionary<GridNode, GridNode>();
        int sequence = 0, explored = 0;
        queue.Enqueue(start, (Heuristic(grid, start, goal, request), start.X, start.Y, start.Layer, start.ViaCount, sequence++));
        while (queue.TryDequeue(out GridNode current, out _))
        {
            if (++explored > request.MaxNodes) break;
            if (current.X == goal.X && current.Y == goal.Y && current.Layer == goal.Layer)
            {
                var path = new List<GridNode> { current };
                while (parent.TryGetValue(path[^1], out GridNode previous)) path.Add(previous);
                path.Reverse();
                return new(path.ToImmutableArray(), explored, distance[current]);
            }
            foreach (GridStep direction in Directions)
            {
                GridNode next = current with { X = current.X + direction.X, Y = current.Y + direction.Y };
                if (!grid.Contains(next.X, next.Y) || blocked.Contains(new(next.X, next.Y, next.Layer))
                    || !grid.EdgeClear(current, next, request, blocked)) continue;
                long nextDistance = checked(distance[current] + request.GridNm);
                if (distance.TryGetValue(next, out long known) && known <= nextDistance) continue;
                distance[next] = nextDistance; parent[next] = current;
                queue.Enqueue(next, (checked(nextDistance + Heuristic(grid, next, goal, request)), next.X, next.Y, next.Layer, next.ViaCount, sequence++));
            }
            foreach (MlViaSpec via in request.Vias)
            {
                if (current.ViaCount >= request.MaxVias) continue;
                BoardLayer currentLayer = request.Layers[current.Layer];
                BoardLayer? targetLayer = via.StartLayer == currentLayer ? via.EndLayer
                    : via.EndLayer == currentLayer ? via.StartLayer : null;
                if (targetLayer is null) continue;
                int targetIndex = grid.LayerIndex(targetLayer.Value);
                if (targetIndex < 0 || blocked.Contains(new(current.X, current.Y, targetIndex))
                    || !grid.ViaClear(current, targetLayer.Value, via, request, obstacles)) continue;
                GridNode next = new(current.X, current.Y, targetIndex, current.ViaCount + 1);
                long nextDistance = checked(distance[current] + via.CostNm);
                if (distance.TryGetValue(next, out long known) && known <= nextDistance) continue;
                distance[next] = nextDistance; parent[next] = current;
                queue.Enqueue(next, (checked(nextDistance + Heuristic(grid, next, goal, request)), next.X, next.Y, next.Layer, next.ViaCount, sequence++));
            }
        }
        return new(null, explored, 0);
    }

    private static long Heuristic(MlGrid grid, GridNode from, GridNode to, ValidatedRequest request)
        => checked((long)(Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y)) * request.GridNm);

    private static CreateItems BuildCandidate(DocumentSpecifier document, ValidatedRequest request,
        ImmutableArray<MlPathPoint> path, string planId, ImmutableArray<BoardLayer> enabledCopper)
    {
        var candidate = new CreateItems { Header = new() { Document = document } };
        int itemIndex = 0, segmentStart = 0;
        while (segmentStart < path.Length - 1)
        {
            int next = segmentStart + 1;
            while (next < path.Length && path[next].Layer == path[segmentStart].Layer) next++;
            int segmentEnd = next - 1;
            for (int index = segmentStart + 1; index <= segmentEnd; index++)
            {
                bool end = index == segmentEnd;
                bool turn = !end && index + 1 <= segmentEnd
                    && (path[index].XNm - path[index - 1].XNm) * (path[index + 1].YNm - path[index].YNm)
                    != (path[index].YNm - path[index - 1].YNm) * (path[index + 1].XNm - path[index].XNm);
                if (!end && !turn) continue;
                MlPathPoint from = path[segmentStart], to = path[end ? index : index];
                if (from.XNm != to.XNm || from.YNm != to.YNm)
                {
                    Track track = new()
                    {
                        Id = new() { Value = DeterministicId(planId, itemIndex++) },
                        Start = new() { XNm = from.XNm, YNm = from.YNm }, End = new() { XNm = to.XNm, YNm = to.YNm },
                        Width = new() { ValueNm = request.WidthNm }, Layer = from.Layer, Net = new() { Name = request.NetName }
                    };
                    track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_plan_id", Value = planId });
                    track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_plan_segment", Value = itemIndex.ToString(CultureInfo.InvariantCulture) });
                    candidate.Items.Add(ProtoAny.Pack(track));
                }
                segmentStart = index;
            }
            if (segmentEnd < path.Length - 1)
            {
                MlPathPoint point = path[segmentEnd];
                MlPathPoint nextPoint = path[segmentEnd + 1];
                MlViaSpec spec = request.Vias.First(v => (v.StartLayer == point.Layer && v.EndLayer == nextPoint.Layer)
                    || (v.StartLayer == nextPoint.Layer && v.EndLayer == point.Layer));
                int low = Math.Min((int)spec.StartLayer, (int)spec.EndLayer);
                int high = Math.Max((int)spec.StartLayer, (int)spec.EndLayer);
                Via via = new()
                {
                    Id = new() { Value = DeterministicId(planId, itemIndex++) }, Position = new() { XNm = point.XNm, YNm = point.YNm },
                    Type = spec.Type, Net = new() { Name = request.NetName }, PadStack = new() { Type = PadStackType.PstNormal }
                };
                for (int layer = low; layer <= high; layer++)
                {
                    BoardLayer boardLayer = (BoardLayer)layer;
                    if (!enabledCopper.Contains(boardLayer)) continue;
                    via.PadStack.Layers.Add(boardLayer);
                    via.PadStack.CopperLayers.Add(new PadStackLayer { Layer = boardLayer, Shape = PadStackShape.PssCircle,
                        Size = new() { XNm = spec.DiameterNm, YNm = spec.DiameterNm } });
                }
                via.PadStack.Drill = new DrillProperties
                {
                    StartLayer = spec.StartLayer, EndLayer = spec.EndLayer,
                    Diameter = new() { XNm = spec.DrillNm, YNm = spec.DrillNm }, Shape = DrillShape.DsCircle
                };
                via.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.route_plan_id", Value = planId });
                candidate.Items.Add(ProtoAny.Pack(via));
            }
            segmentStart = segmentEnd + 1;
        }
        return candidate;
    }

    private static int CountBends(ImmutableArray<MlPathPoint> path)
    {
        int count = 0;
        for (int index = 1; index + 1 < path.Length; index++)
        {
            if (path[index - 1].Layer != path[index].Layer || path[index].Layer != path[index + 1].Layer) continue;
            long ax = path[index].XNm - path[index - 1].XNm, ay = path[index].YNm - path[index - 1].YNm;
            long bx = path[index + 1].XNm - path[index].XNm, by = path[index + 1].YNm - path[index].YNm;
            if (ax * by != ay * bx) count++;
        }
        return count;
    }

    private static ImmutableArray<MlObstacle> BuildObstacles(IEnumerable<ProtoAny> packedItems,
        ValidatedRequest request, ImmutableArray<BoardLayer> enabledCopper)
    {
        var result = ImmutableArray.CreateBuilder<MlObstacle>();
        foreach (ProtoAny packed in packedItems)
        {
            if (packed.Is(Track.Descriptor))
            {
                Track track = packed.Unpack<Track>();
                if (!enabledCopper.Contains(track.Layer) || SameNet(track.Net, request.NetName)) continue;
                result.Add(new("track", track.Id.Value, track.Net.Name, track.Layer,
                    SegmentBounds(track.Start, track.End, Margin(request, track.Width.ValueNm)), "different-net track"));
            }
            else if (packed.Is(Arc.Descriptor))
            {
                Arc arc = packed.Unpack<Arc>();
                if (!enabledCopper.Contains(arc.Layer) || SameNet(arc.Net, request.NetName)) continue;
                result.Add(new("arc", arc.Id.Value, arc.Net.Name, arc.Layer,
                    PointBounds([arc.Start, arc.Mid, arc.End], Margin(request, arc.Width.ValueNm)), "different-net arc"));
            }
            else if (packed.Is(Via.Descriptor))
            {
                Via via = packed.Unpack<Via>();
                long radius = Math.Max(PadRadius(via.PadStack), via.PadStack?.Drill?.Diameter?.XNm / 2 ?? 0);
                foreach (BoardLayer layer in enabledCopper.Where(layer => LayersContain(via.PadStack?.Layers, layer)))
                    if (!SameNet(via.Net, request.NetName))
                        result.Add(new("via", via.Id.Value, via.Net.Name, layer,
                            PointBounds([via.Position], checked(radius + request.ClearanceNm + request.WidthNm / 2)), "different-net via span"));
            }
            else if (packed.Is(Pad.Descriptor))
            {
                Pad pad = packed.Unpack<Pad>();
                if (SameNet(pad.Net, request.NetName)) continue;
                long radius = PadRadius(pad.PadStack);
                foreach (BoardLayer layer in enabledCopper.Where(layer => LayersContain(pad.PadStack?.Layers, layer)))
                    result.Add(new("pad", pad.Id.Value, pad.Net.Name, layer,
                        PointBounds([pad.Position], checked(radius + request.ClearanceNm + request.WidthNm / 2)), "different-net pad"));
            }
            else if (packed.Is(Zone.Descriptor))
            {
                Zone zone = packed.Unpack<Zone>();
                if (zone.Outline is null || zone.Outline.Polygons.Count == 0) continue;
                string net = zone.CopperSettings?.Net?.Name ?? "";
                bool keepout = zone.Type == ZoneType.ZtRuleArea;
                if (!keepout && SameNet(new Net { Name = net }, request.NetName)) continue;
                long margin = checked((zone.CopperSettings?.Clearance?.ValueNm ?? 0) + request.ClearanceNm + request.WidthNm / 2);
                MlBoundsValue bounds = PolySetBounds(zone.Outline!, margin);
                foreach (BoardLayer layer in enabledCopper.Where(layer => LayersContain(zone.Layers, layer)))
                    result.Add(new("zone", zone.Id.Value, net, layer, bounds, keepout ? "rule-area keepout" : "different-net copper zone"));
            }
            else if (packed.Is(BoardGraphicShape.Descriptor))
            {
                BoardGraphicShape shape = packed.Unpack<BoardGraphicShape>();
                if (!enabledCopper.Contains(shape.Layer) || SameNet(shape.Net, request.NetName)) continue;
                if (TryGraphicBounds(shape.Shape, Margin(request, shape.Shape.Attributes?.Stroke?.Width?.ValueNm ?? 0), out MlBoundsValue bounds))
                    result.Add(new("shape", shape.Id.Value, shape.Net.Name, shape.Layer, bounds, "copper graphic"));
            }
        }
        return result.Where(obstacle => Intersects(obstacle.Bounds, request.Bounds))
            .OrderBy(obstacle => obstacle.Layer).ThenBy(obstacle => obstacle.Kind, StringComparer.Ordinal)
            .ThenBy(obstacle => obstacle.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private sealed class MlGrid
    {
        private readonly MlBounds _bounds;
        private readonly long _size;
        private readonly ImmutableArray<BoardLayer> _layers;
        private readonly ImmutableArray<BoardLayer> _physicalLayers;
        public int Width { get; }
        public int Height { get; }
        public MlGrid(MlBounds bounds, long size, ImmutableArray<BoardLayer> layers, ImmutableArray<BoardLayer> physicalLayers)
        { _bounds = bounds; _size = size; _layers = layers; _physicalLayers = physicalLayers; Width = checked((int)((bounds.MaxXNm - bounds.MinXNm) / size + 1)); Height = checked((int)((bounds.MaxYNm - bounds.MinYNm) / size + 1)); }
        public GridNode Node(MlPoint point) => new(checked((int)((point.XNm - _bounds.MinXNm) / _size)), checked((int)((point.YNm - _bounds.MinYNm) / _size)), 0, 0);
        public Vector2 Point(int x, int y) => new() { XNm = checked(_bounds.MinXNm + (long)x * _size), YNm = checked(_bounds.MinYNm + (long)y * _size) };
        public int LayerIndex(BoardLayer layer) => _layers.IndexOf(layer);
        public bool Contains(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;
        public IEnumerable<GridNode> CellsIntersecting(MlBoundsValue obstacle, int layer)
        {
            long minX = Math.Max(obstacle.MinXNm, _bounds.MinXNm), maxX = Math.Min(obstacle.MaxXNm, _bounds.MaxXNm);
            long minY = Math.Max(obstacle.MinYNm, _bounds.MinYNm), maxY = Math.Min(obstacle.MaxYNm, _bounds.MaxYNm);
            if (minX > maxX || minY > maxY) yield break;
            int lowX = Math.Clamp(checked((int)((minX - _bounds.MinXNm) / _size)), 0, Width - 1);
            int highX = Math.Clamp(checked((int)((maxX - _bounds.MinXNm) / _size)), 0, Width - 1);
            int lowY = Math.Clamp(checked((int)((minY - _bounds.MinYNm) / _size)), 0, Height - 1);
            int highY = Math.Clamp(checked((int)((maxY - _bounds.MinYNm) / _size)), 0, Height - 1);
            for (int x = lowX; x <= highX; x++) for (int y = lowY; y <= highY; y++) yield return new(x, y, layer, 0);
        }
        public bool EdgeClear(GridNode from, GridNode to, ValidatedRequest request, ImmutableHashSet<GridCell> blocked)
        {
            if (from.Layer != to.Layer) return false;
            int minX = Math.Min(from.X, to.X), maxX = Math.Max(from.X, to.X), minY = Math.Min(from.Y, to.Y), maxY = Math.Max(from.Y, to.Y);
            if (from.X == to.X)
                for (int y = minY; y <= maxY; y++) if (blocked.Contains(new(from.X, y, from.Layer))) return false;
            else
                for (int x = minX; x <= maxX; x++) if (blocked.Contains(new(x, from.Y, from.Layer))) return false;
            return true;
        }
        public bool ViaClear(GridNode current, BoardLayer targetLayer, MlViaSpec via, ValidatedRequest request,
            ImmutableArray<MlObstacle> obstacles)
        {
            long viaRadius = Math.Max(via.DiameterNm, via.DrillNm) / 2;
            int startPhysical = _physicalLayers.IndexOf(via.StartLayer);
            int endPhysical = _physicalLayers.IndexOf(via.EndLayer);
            int low = Math.Min(startPhysical, endPhysical), high = Math.Max(startPhysical, endPhysical);
            if (startPhysical < 0 || endPhysical < 0) return false;
            for (int layer = low; layer <= high; layer++)
            {
                BoardLayer boardLayer = _physicalLayers[layer];
                foreach (MlObstacle obstacle in obstacles.Where(obstacle => obstacle.Layer == boardLayer))
                {
                    // Track/pad obstacles already include the requested track
                    // width and clearance.  Expand them by the difference when
                    // this via is wider than that track; this is the via-size
                    // keepout check for every physical layer in the span.
                    long extra = Math.Max(0, viaRadius - request.WidthNm / 2);
                    MlBoundsValue expanded = new(
                        checked(obstacle.Bounds.MinXNm - extra), checked(obstacle.Bounds.MinYNm - extra),
                        checked(obstacle.Bounds.MaxXNm + extra), checked(obstacle.Bounds.MaxYNm + extra));
                    Vector2 point = Point(current.X, current.Y);
                    if (point.XNm >= expanded.MinXNm && point.XNm <= expanded.MaxXNm
                        && point.YNm >= expanded.MinYNm && point.YNm <= expanded.MaxYNm)
                        return false;
                }
            }
            return true;
        }
    }

    private static ImmutableArray<BoardLayer> ParseLayers(IEnumerable<string>? values, ImmutableArray<BoardLayer> enabled)
    {
        if (values is null) throw new AutomationException("invalid_multilayer_route_plan", "layers is required.");
        var result = ImmutableArray.CreateBuilder<BoardLayer>();
        foreach (string value in values)
        {
            if (!TryCopperLayer(value, out BoardLayer layer) || !enabled.Contains(layer) || result.Contains(layer))
                throw new AutomationException("invalid_multilayer_route_plan", "layers must be a unique ordered subset of enabled copper layers.");
            if (result.Count != 0 && (int)result[^1] >= (int)layer)
                throw new AutomationException("invalid_multilayer_route_plan", "layers must follow the native F.Cu-to-B.Cu copper ordering.");
            result.Add(layer);
        }
        if (result.Count == 0) throw new AutomationException("invalid_multilayer_route_plan", "layers must contain at least one enabled copper layer.");
        return result.ToImmutable();
    }

    private static void ValidateViaSpan(ViaType type, BoardLayer start, BoardLayer end, ImmutableArray<BoardLayer> enabled)
    {
        int first = enabled.IndexOf(start), last = enabled.IndexOf(end);
        if (first < 0 || last < 0 || first == last) throw new AutomationException("invalid_multilayer_via", "Via span is not in the enabled copper stack.");
        int low = Math.Min(first, last), high = Math.Max(first, last);
        bool outerStart = first == 0 || first == enabled.Length - 1, outerEnd = last == 0 || last == enabled.Length - 1;
        switch (type)
        {
            case ViaType.VtThrough when first != 0 || last != enabled.Length - 1:
                throw new AutomationException("invalid_multilayer_via", "A through via must span the first enabled copper layer to the last enabled copper layer.");
            case ViaType.VtBlind when outerStart == outerEnd:
                throw new AutomationException("invalid_multilayer_via", "A blind via must connect one outer layer to an inner layer.");
            case ViaType.VtBuried when outerStart || outerEnd:
                throw new AutomationException("invalid_multilayer_via", "A buried via must start and end on inner copper layers.");
            case ViaType.VtMicro when high - low != 1 || (!outerStart && !outerEnd):
                throw new AutomationException("invalid_multilayer_via", "A microvia must span adjacent layers from an outer layer.");
        }
    }

    private static bool TryParseViaType(string value, out ViaType type)
    {
        if (string.IsNullOrWhiteSpace(value)) { type = ViaType.VtUnknown; return false; }
        type = value.Trim().ToLowerInvariant() switch
        {
            "through" or "vt_through" or "v t through" => ViaType.VtThrough,
            "blind_buried" or "blind-buried" or "vt_blind_buried" => ViaType.VtBlindBuried,
            "micro" or "microvia" or "vt_micro" => ViaType.VtMicro,
            "blind" or "vt_blind" => ViaType.VtBlind,
            "buried" or "vt_buried" => ViaType.VtBuried,
            _ => ViaType.VtUnknown
        };
        return type != ViaType.VtUnknown;
    }

    internal static bool IsCopperLayer(BoardLayer layer) => (int)layer is >= (int)BoardLayer.BlFCu and <= (int)BoardLayer.BlBCu;

    internal static string LayerName(BoardLayer layer) => layer switch
    {
        BoardLayer.BlFCu => "F.Cu", BoardLayer.BlBCu => "B.Cu",
        _ when IsCopperLayer(layer) => "In" + ((int)layer - (int)BoardLayer.BlFCu) + ".Cu",
        _ => layer.ToString()
    };

    private static bool TryCopperLayer(string value, out BoardLayer layer)
    {
        if (string.IsNullOrWhiteSpace(value)) { layer = BoardLayer.BlUnknown; return false; }
        string normalized = value.Trim().Replace("_", ".", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (normalized.Equals("F.Cu", StringComparison.OrdinalIgnoreCase)) { layer = BoardLayer.BlFCu; return true; }
        if (normalized.Equals("B.Cu", StringComparison.OrdinalIgnoreCase)) { layer = BoardLayer.BlBCu; return true; }
        if (normalized.StartsWith("In", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(".Cu", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(normalized[2..^3], NumberStyles.None, CultureInfo.InvariantCulture, out int inner)
            && inner is >= 1 and <= 30)
        { layer = (BoardLayer)((int)BoardLayer.BlFCu + inner); return true; }
        layer = BoardLayer.BlUnknown;
        return false;
    }

    private static string PlanIdentity(ValidatedRequest request, DocumentLifecycleState state, ImmutableArray<MlObstacle> obstacles)
    {
        string canonical = JsonSerializer.Serialize(request, JsonOptions) + "\n" + state.StateSha256 + "\n"
            + string.Join("\n", obstacles.Select(o => $"{o.Kind}|{o.Id}|{o.Layer}|{o.Bounds.MinXNm},{o.Bounds.MinYNm},{o.Bounds.MaxXNm},{o.Bounds.MaxYNm}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string DeterministicId(string planId, int index)
        => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{planId}\n{index}"))[..16], bigEndian: true).ToString("D");
    private static long Margin(ValidatedRequest request, long itemWidth)
        => checked(Math.Max(0, itemWidth) / 2 + request.WidthNm / 2 + request.ClearanceNm);
    private static bool SameNet(Net? net, string name) => net is not null && net.Name == name;
    private static bool LayersContain(IEnumerable<BoardLayer>? layers, BoardLayer target) => layers is null || !layers.Any() || layers.Contains(target);
    private static bool Inside(MlPoint point, MlBounds bounds) => point.XNm >= bounds.MinXNm && point.XNm <= bounds.MaxXNm && point.YNm >= bounds.MinYNm && point.YNm <= bounds.MaxYNm;
    private static bool Intersects(MlBoundsValue a, MlBounds b) => a.MaxXNm >= b.MinXNm && a.MinXNm <= b.MaxXNm && a.MaxYNm >= b.MinYNm && a.MinYNm <= b.MaxYNm;
    private static long Distance(MlPathPoint a, MlPathPoint b) => Math.Abs(a.XNm - b.XNm) + Math.Abs(a.YNm - b.YNm);
    private static MlBoundsValue SegmentBounds(Vector2 a, Vector2 b, long margin) => new(Math.Min(a.XNm, b.XNm) - margin, Math.Min(a.YNm, b.YNm) - margin, Math.Max(a.XNm, b.XNm) + margin, Math.Max(a.YNm, b.YNm) + margin);
    private static MlBoundsValue PointBounds(IReadOnlyList<Vector2> points, long margin) => new(points.Min(p => p.XNm) - margin, points.Min(p => p.YNm) - margin, points.Max(p => p.XNm) + margin, points.Max(p => p.YNm) + margin);
    private static MlBoundsValue PolySetBounds(PolySet poly, long margin)
    {
        var points = poly.Polygons.SelectMany(p => p.Outline.Nodes).Where(n => n.GeometryCase == PolyLineNode.GeometryOneofCase.Point).Select(n => n.Point).ToArray();
        return points.Length == 0 ? new(0, 0, 0, 0) : PointBounds(points, margin);
    }
    private static long PadRadius(PadStack? stack)
    {
        if (stack is null) return 0;
        long radius = 0;
        foreach (PadStackLayer layer in stack.CopperLayers)
            radius = Math.Max(radius, Math.Max(Math.Abs(layer.Size.XNm), Math.Abs(layer.Size.YNm)) / 2 + Math.Max(Math.Abs(layer.Offset.XNm), Math.Abs(layer.Offset.YNm)));
        return radius;
    }
    private static bool TryGraphicBounds(GraphicShape shape, long margin, out MlBoundsValue bounds)
    {
        switch (shape.GeometryCase)
        {
            case GraphicShape.GeometryOneofCase.Segment: bounds = SegmentBounds(shape.Segment.Start, shape.Segment.End, margin); return true;
            case GraphicShape.GeometryOneofCase.Rectangle:
                bounds = new(Math.Min(shape.Rectangle.TopLeft.XNm, shape.Rectangle.BottomRight.XNm) - margin, Math.Min(shape.Rectangle.TopLeft.YNm, shape.Rectangle.BottomRight.YNm) - margin, Math.Max(shape.Rectangle.TopLeft.XNm, shape.Rectangle.BottomRight.XNm) + margin, Math.Max(shape.Rectangle.TopLeft.YNm, shape.Rectangle.BottomRight.YNm) + margin); return true;
            case GraphicShape.GeometryOneofCase.Circle:
                long radius = Math.Max(Math.Abs(shape.Circle.RadiusPoint.XNm - shape.Circle.Center.XNm), Math.Abs(shape.Circle.RadiusPoint.YNm - shape.Circle.Center.YNm));
                bounds = PointBounds([shape.Circle.Center], checked(radius + margin)); return true;
            case GraphicShape.GeometryOneofCase.Arc: bounds = PointBounds([shape.Arc.Start, shape.Arc.Mid, shape.Arc.End], margin); return true;
            case GraphicShape.GeometryOneofCase.Polygon: bounds = PolySetBounds(shape.Polygon, margin); return bounds.MinXNm != bounds.MaxXNm || bounds.MinYNm != bounds.MaxYNm;
            default: bounds = default; return false;
        }
    }
}
