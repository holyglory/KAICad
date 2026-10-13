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

/// <summary>Deterministic proposal-only length tuning inside an explicit corridor.</summary>
[McpServerToolType]
public sealed class PcbRouteTuningTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_route_tune_plan", ReadOnly = true),
     Description("Create a deterministic proposal-only serpentine Track candidate for length tuning. requestJson requires netName, copper layer, same-axis start/end anchors, widthNm, targetAdditionalLengthNm, pitchNm, maxSegments and an explicit corridor. Returns exact Track CreateItems geometry and achieved additional length; it does not inspect or claim DRC, impedance, differential-pair or manufacturability and never mutates the board."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "explicit PCB lifecycle state, tuning corridor, deterministic meander proposal")]
    public Task<CallToolResult> Plan(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_route_tune_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_route_tune_target", "Route tuning requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_route_tune_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_route_tune_state", "The expected observation must identify the same PCB and native process epoch.");
        TuneRequest request = JsonSerializer.Deserialize<TuneRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_route_tune", "requestJson must be a JSON object.");
        Validate(request);
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before tuning planning.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before tuning planning; refresh the lifecycle checkpoint.");

        BoardLayer layer = ParseLayer(request.Layer);
        var points = BuildPath(request);
        long achieved = PathLength(points) - Distance(request.Start!, request.End!);
        string planId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request, expected.StateSha256 }))));
        var candidate = new CreateItems { Header = new() { Document = document } };
        int index = 0;
        for (int i = 1; i < points.Count; i++)
        {
            Track track = new()
            {
                Id = new() { Value = GuidFrom(planId, index++) }, Start = points[i - 1], End = points[i],
                Width = new() { ValueNm = request.WidthNm }, Layer = layer,
                Net = new() { Name = request.NetName }
            };
            track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.tune_plan_id", Value = planId });
            track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.tune_segment", Value = i.ToString() });
            candidate.Items.Add(Any.Pack(track));
        }
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_tune_changed", "The board changed while tuning was planned; discard the proposal.");
        var result = new
        {
            instanceId, processEpoch = session.Epoch, document, planId,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            routePoints = points, candidateRequestJson = BoardJson.Formatter.Format(candidate),
            baseLengthNm = Distance(request.Start!, request.End!), achievedAdditionalLengthNm = achieved,
            requestedAdditionalLengthNm = request.TargetAdditionalLengthNm,
            withinTolerance = Math.Abs(achieved - request.TargetAdditionalLengthNm) <= request.PitchNm,
            segmentCount = candidate.Items.Count, drcValidated = false, nativeCommit = false,
            obstacleModel = "explicit-corridor-only", completeObstacleModel = false,
            limitations = new[] { "proposal_only", "corridor_must_be_verified", "drc_not_validated", "impedance_not_validated" }
        };
        JsonElement structured = JsonSerializer.SerializeToElement(result, JsonOptions);
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    private sealed class TuneRequest
    {
        public string NetName { get; set; } = "";
        public string Layer { get; set; } = "";
        public Vector2? Start { get; set; }
        public Vector2? End { get; set; }
        public List<Vector2>? Centerline { get; set; }
        public long WidthNm { get; set; }
        public long ClearanceNm { get; set; }
        public long TargetAdditionalLengthNm { get; set; }
        public long PitchNm { get; set; }
        public int MaxSegments { get; set; } = 64;
        public Bounds? Corridor { get; set; }
    }

    private sealed class Bounds { public long MinXNm { get; set; } public long MinYNm { get; set; } public long MaxXNm { get; set; } public long MaxYNm { get; set; } }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private static void Validate(TuneRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NetName) || request.NetName.Length > 512
            || request.Corridor is null
            || (request.Centerline is null && (request.Start is null || request.End is null)))
            throw new AutomationException("invalid_route_tune", "netName, corridor and either start/end or centerline are required.");
        if (request.WidthNm <= 0 || request.ClearanceNm < 0 || request.PitchNm <= 0 || request.TargetAdditionalLengthNm < 0
            || request.MaxSegments is < 3 or > 512)
            throw new AutomationException("invalid_route_tune", "width, pitch and maxSegments must be positive and target additional length cannot be negative.");

        Bounds corridor = request.Corridor;
        if (corridor.MinXNm >= corridor.MaxXNm || corridor.MinYNm >= corridor.MaxYNm)
            throw new AutomationException("invalid_route_tune", "corridor must have positive dimensions.");

        if (request.Centerline is { } centerline)
        {
            ValidateCenterline(centerline, request.Start, request.End,
                corridor.MinXNm, corridor.MinYNm, corridor.MaxXNm, corridor.MaxYNm);
            if (centerline.Count > request.MaxSegments)
                throw new AutomationException("invalid_route_tune", "maxSegments is too small for the supplied centerline.");
            request.Start ??= centerline[0].Clone();
            request.End ??= centerline[^1].Clone();
            return;
        }

        bool horizontal = request.Start!.YNm == request.End!.YNm, vertical = request.Start.XNm == request.End.XNm;
        if (!horizontal && !vertical) throw new AutomationException("invalid_route_tune", "start and end must share one axis.");
        if (request.TargetAdditionalLengthNm > 0 && (corridor.MaxYNm - corridor.MinYNm < request.PitchNm * 2
            || corridor.MaxXNm - corridor.MinXNm < request.PitchNm * 2))
            throw new AutomationException("invalid_route_tune", "corridor is too narrow for the requested pitch.");
        if (!Inside(request.Start, corridor) || !Inside(request.End, corridor))
            throw new AutomationException("invalid_route_tune", "anchors must lie inside the explicit corridor.");
    }

    private static void ValidateCenterline(IReadOnlyList<Vector2> centerline, Vector2? start, Vector2? end,
        long minXNm, long minYNm, long maxXNm, long maxYNm)
    {
        if (centerline.Count < 2)
            throw new AutomationException("invalid_route_tune", "centerline must contain at least two points.");
        if (start is not null && !start.Equals(centerline[0])
            || end is not null && !end.Equals(centerline[^1]))
            throw new AutomationException("invalid_route_tune", "start/end must match the supplied centerline endpoints.");
        for (int index = 0; index < centerline.Count; index++)
        {
            Vector2 point = centerline[index];
            if (point.XNm < minXNm || point.XNm > maxXNm || point.YNm < minYNm || point.YNm > maxYNm)
                throw new AutomationException("invalid_route_tune", "centerline points must lie inside the explicit corridor.");
            if (index == 0)
                continue;
            Vector2 previous = centerline[index - 1];
            if (previous.XNm == point.XNm && previous.YNm == point.YNm)
                throw new AutomationException("invalid_route_tune", "centerline segments must be nonzero.");
            if (previous.XNm != point.XNm && previous.YNm != point.YNm)
                throw new AutomationException("invalid_route_tune", "centerline segments must be axis-aligned.");
        }
    }

    private static List<Vector2> BuildPath(TuneRequest request)
    {
        if (request.Centerline is { } centerline)
            return centerline.Select(point => point.Clone()).ToList();

        bool horizontal = request.Start!.YNm == request.End!.YNm;
        int loops = request.TargetAdditionalLengthNm == 0 ? 0
            : Math.Max(1, (int)Math.Ceiling(request.TargetAdditionalLengthNm / (double)(2 * request.PitchNm)));
        if (2 + loops * 2 > request.MaxSegments)
            throw new AutomationException("invalid_route_tune", "maxSegments is too small for the requested tuning length.");
        var points = new List<Vector2> { request.Start.Clone() };
        for (int i = 0; i < loops; i++)
        {
            double fraction = (i + 1.0) / (loops + 1.0);
            long axis = horizontal
                ? request.Start.XNm + (long)Math.Round((request.End.XNm - request.Start.XNm) * fraction)
                : request.Start.YNm + (long)Math.Round((request.End.YNm - request.Start.YNm) * fraction);
            long offset = (i % 2 == 0 ? request.PitchNm : -request.PitchNm);
            points.Add(horizontal ? new Vector2 { XNm = axis, YNm = request.Start.YNm + offset }
                                   : new Vector2 { XNm = request.Start.XNm + offset, YNm = axis });
        }
        points.Add(request.End.Clone());
        if (points.Any(point => !Inside(point, request.Corridor!)))
            throw new AutomationException("invalid_route_tune", "The requested pitch leaves the explicit tuning corridor.");
        return points;
    }

    internal static long PathLength(IReadOnlyList<Vector2> points)
    {
        long total = 0;
        for (int index = 1; index < points.Count; index++) total = checked(total + Distance(points[index - 1], points[index]));
        return total;
    }
    private static long Distance(Vector2 left, Vector2 right)
    {
        double dx = (double) right.XNm - left.XNm;
        double dy = (double) right.YNm - left.YNm;
        return checked((long) Math.Round(Math.Sqrt(dx * dx + dy * dy), MidpointRounding.AwayFromZero));
    }
    private static bool Inside(Vector2 point, Bounds bounds) => point.XNm >= bounds.MinXNm && point.XNm <= bounds.MaxXNm && point.YNm >= bounds.MinYNm && point.YNm <= bounds.MaxYNm;
    private static BoardLayer ParseLayer(string value)
    {
        string normalized = value.Replace(".", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        string enumName = normalized.StartsWith("Bl", StringComparison.OrdinalIgnoreCase) ? normalized : "Bl" + normalized;
        return System.Enum.TryParse<BoardLayer>(enumName, true, out var layer)
            && layer is not BoardLayer.BlUnknown and not BoardLayer.BlUndefined
            ? layer : throw new AutomationException("invalid_route_tune", "layer must name a copper BoardLayer value such as F.Cu or B.Cu.");
    }
    private static string GuidFrom(string seed, int index) => GuidFromBytes(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed + ":" + index)));
    private static string GuidFromBytes(byte[] bytes) => new Guid(bytes.AsSpan(0, 16)).ToString("D");
    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    { try { return SchematicJson.Parser.Parse<T>(json); } catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException) { throw new AutomationException(code, error.Message); } }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    { try { return await action(); } catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException or InvalidProtocolBufferException or InvalidJsonException or JsonException) { var structured = JsonSerializer.SerializeToElement(new { errorCode = error is AutomationException known ? known.Code : "pcb_route_tune_failed", errorMessage = error.Message }); return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] }; } }
}
