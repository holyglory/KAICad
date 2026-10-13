using System.ComponentModel;
using System.Globalization;
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
     Description("Create a deterministic proposal-only Track candidate for length tuning. requestJson requires netName, copper layer, widthNm, targetAdditionalLengthNm, pitchNm, maxSegments and an explicit corridor, plus either legacy same-axis start/end anchors or an axis-aligned centerline array whose first and last points are the anchors. Returns exact Track CreateItems geometry and achieved length; it does not inspect or claim DRC, impedance, differential-pair or manufacturability and never mutates the board."),
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
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch)
            || string.IsNullOrWhiteSpace(expected.NativeIdentity)
            || string.IsNullOrWhiteSpace(expected.StateSha256)
            || string.IsNullOrWhiteSpace(expected.Revision.Epoch))
            throw new AutomationException("invalid_route_tune_state", "The expected observation must identify the same PCB, process epoch, native identity, state digest and revision.");
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
        List<Vector2> reference = ReferencePath(request);
        long baseLength = PathLength(reference);
        var points = BuildPath(request);
        long achieved = checked(PathLength(points) - baseLength);
        string planId = BuildPlanId(document, expected, request, layer, reference);
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
                track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.tune_segment", Value = i.ToString(CultureInfo.InvariantCulture) });
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
            baseLengthNm = baseLength, achievedAdditionalLengthNm = achieved,
            requestedAdditionalLengthNm = request.TargetAdditionalLengthNm,
            toleranceNm = Tolerance(request.TargetAdditionalLengthNm),
            withinTolerance = Math.Abs(achieved - request.TargetAdditionalLengthNm) <= Tolerance(request.TargetAdditionalLengthNm),
            segmentCount = candidate.Items.Count, drcValidated = false, nativeCommit = false,
            obstacleModel = "explicit-corridor-only", completeObstacleModel = false,
            limitations = new[] { "proposal_only", "corridor_must_be_verified", "drc_not_validated", "impedance_not_validated" }
        };
        JsonElement structured = JsonSerializer.SerializeToElement(result, JsonOptions);
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    internal sealed class TuneRequest
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

    internal sealed class Bounds { public long MinXNm { get; set; } public long MinYNm { get; set; } public long MaxXNm { get; set; } public long MaxYNm { get; set; } }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    internal static void Validate(TuneRequest request)
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
        ParseLayer(request.Layer);
        long margin = CorridorMargin(request.WidthNm, request.ClearanceNm);
        if (!TryInset(corridor.MinXNm, corridor.MaxXNm, margin, out long minX, out long maxX)
            || !TryInset(corridor.MinYNm, corridor.MaxYNm, margin, out long minY, out long maxY))
            throw new AutomationException("invalid_route_tune", "the corridor is too small for the requested width and clearance.");

        if (request.Centerline is { } centerline)
        {
            ValidateCenterline(centerline, request.Start, request.End,
                corridor.MinXNm, corridor.MinYNm, corridor.MaxXNm, corridor.MaxYNm);
            ValidateSimplePath(centerline, minX, minY, maxX, maxY);
            if (centerline.Count - 1 > request.MaxSegments)
                throw new AutomationException("invalid_route_tune", "maxSegments is too small for the supplied centerline.");
            request.Start ??= centerline[0].Clone();
            request.End ??= centerline[^1].Clone();
            return;
        }

        bool horizontal = request.Start!.YNm == request.End!.YNm, vertical = request.Start.XNm == request.End.XNm;
        if (!horizontal && !vertical) throw new AutomationException("invalid_route_tune", "start and end must share one axis.");
        var legacy = new[] { request.Start!, request.End! };
        ValidateSimplePath(legacy, minX, minY, maxX, maxY);
        if (request.TargetAdditionalLengthNm > 0 && request.PitchNm > long.MaxValue / 2)
            throw new AutomationException("invalid_route_tune", "pitch is too large for bounded route coordinates.");
    }

    internal static void ValidateCenterline(IReadOnlyList<Vector2> centerline, Vector2? start, Vector2? end,
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

    internal static List<Vector2> BuildPath(TuneRequest request)
    {
        List<Vector2> reference = ReferencePath(request);
        if (request.TargetAdditionalLengthNm == 0)
            return reference;

        long target = RoundToEven(request.TargetAdditionalLengthNm);
        (long minX, long minY, long maxX, long maxY) = SafeBounds(request);
        for (int segmentIndex = 0; segmentIndex < reference.Count - 1; segmentIndex++)
        {
            Vector2 start = reference[segmentIndex], end = reference[segmentIndex + 1];
            bool horizontal = start.YNm == end.YNm;
            for (int side = 1; side >= -1; side -= 2)
            {
                List<Vector2>? candidate;
                try
                {
                    if (!TryBuildSegmentMeander(reference, segmentIndex, target, horizontal, side,
                            minX, minY, maxX, maxY, request.MaxSegments, out candidate))
                        continue;
                }
                catch (OverflowException)
                {
                    continue;
                }
                if (candidate.Count - 1 > request.MaxSegments)
                    continue;
                try
                {
                    ValidateSimplePath(candidate, minX, minY, maxX, maxY);
                    long achieved = checked(PathLength(candidate) - PathLength(reference));
                    if (achieved == target)
                        return candidate;
                }
                catch (AutomationException) { }
                catch (OverflowException) { }
            }
        }

        throw new AutomationException("invalid_route_tune", "No feasible non-overlapping tuning geometry fits the reference path, corridor and maxSegments.");
    }

    internal static long PathLength(IReadOnlyList<Vector2> points)
    {
        try
        {
            long total = 0;
            for (int index = 1; index < points.Count; index++)
                total = checked(total + Distance(points[index - 1], points[index]));
            return total;
        }
        catch (OverflowException)
        {
            throw new AutomationException("invalid_route_tune", "route length exceeds the bounded coordinate range.");
        }
    }

    private static List<Vector2> ReferencePath(TuneRequest request)
        => request.Centerline is { } centerline
            ? centerline.Select(point => point.Clone()).ToList()
            : [request.Start!.Clone(), request.End!.Clone()];

    private static long CorridorMargin(long widthNm, long clearanceNm)
    {
        long halfWidth = widthNm / 2 + widthNm % 2;
        if (clearanceNm > long.MaxValue - halfWidth)
            throw new AutomationException("invalid_route_tune", "width and clearance exceed the bounded coordinate range.");
        return halfWidth + clearanceNm;
    }

    private static bool TryInset(long min, long max, long margin, out long safeMin, out long safeMax)
    {
        safeMin = safeMax = 0;
        if (margin < 0 || min >= max || min > long.MaxValue - margin || max < long.MinValue + margin)
            return false;
        safeMin = min + margin;
        safeMax = max - margin;
        return safeMin < safeMax;
    }

    private static (long minX, long minY, long maxX, long maxY) SafeBounds(TuneRequest request)
    {
        long margin = CorridorMargin(request.WidthNm, request.ClearanceNm);
        if (!TryInset(request.Corridor!.MinXNm, request.Corridor.MaxXNm, margin, out long minX, out long maxX)
            || !TryInset(request.Corridor.MinYNm, request.Corridor.MaxYNm, margin, out long minY, out long maxY))
            throw new AutomationException("invalid_route_tune", "the corridor is too small for the requested width and clearance.");
        return (minX, minY, maxX, maxY);
    }

    private static long RoundToEven(long value)
    {
        if ((value & 1) == 0)
            return value;
        return value == long.MaxValue ? value - 1 : value + 1;
    }

    private static long Tolerance(long target) => (target & 1) == 0 ? 0 : 1;

    private static bool TryBuildSegmentMeander(IReadOnlyList<Vector2> reference, int segmentIndex, long target,
        bool horizontal, int side, long minX, long minY, long maxX, long maxY, int maxSegments,
        out List<Vector2>? candidate)
    {
        candidate = null;
        Vector2 start = reference[segmentIndex], end = reference[segmentIndex + 1];
        long segmentLength;
        try { segmentLength = Distance(start, end); }
        catch (AutomationException) { return false; }
        if (segmentLength < 3)
            return false;

        long baseline = horizontal ? start.YNm : start.XNm;
        long lateralMin = horizontal ? minY : minX;
        long lateralMax = horizontal ? maxY : maxX;
        long available;
        if (side > 0)
        {
            if (!TryPositiveDifference(baseline, lateralMax, out available))
                return false;
        }
        else if (!TryPositiveDifference(lateralMin, baseline, out available))
            return false;
        if (available <= 0)
            return false;

        long halfTarget = target / 2;
        long laneDistance = halfTarget <= available ? halfTarget : available;
        if (laneDistance <= 0)
            return false;
        long pairExtra = checked(laneDistance * 2);
        long fullPairs = target / pairExtra;
        long remainder = target % pairExtra;
        long transitionCount = checked(fullPairs * 2 + (remainder == 0 ? 0 : 2));
        if (transitionCount > maxSegments || transitionCount > int.MaxValue)
            return false;
        if (transitionCount + 1 > segmentLength)
            return false;
        long outputSegments = checked(reference.Count - 2 + transitionCount * 2 + 1);
        if (outputSegments > maxSegments)
            return false;

        var lanes = new List<long>(checked((int)transitionCount));
        long lane = checked(side > 0 ? baseline + laneDistance : baseline - laneDistance);
        for (long pair = 0; pair < fullPairs; pair++)
        {
            lanes.Add(lane);
            lanes.Add(baseline);
        }
        if (remainder != 0)
        {
            long partial = remainder / 2;
            long partialLane = checked(side > 0 ? baseline + partial : baseline - partial);
            lanes.Add(partialLane);
            lanes.Add(baseline);
        }

        var local = new List<Vector2>(checked(lanes.Count * 2 + 2)) { start.Clone() };
        long previousOffset = 0;
        for (int transition = 0; transition < lanes.Count; transition++)
        {
            long offset = FractionOffset(segmentLength, transition + 1, lanes.Count + 1);
            if (offset <= previousOffset || offset >= segmentLength)
                return false;
            long axis = MoveAlong(start, end, horizontal, offset);
            long currentLane = transition == 0 ? baseline : lanes[transition - 1];
            local.Add(horizontal
                ? new Vector2 { XNm = axis, YNm = currentLane }
                : new Vector2 { XNm = currentLane, YNm = axis });
            local.Add(horizontal
                ? new Vector2 { XNm = axis, YNm = lanes[transition] }
                : new Vector2 { XNm = lanes[transition], YNm = axis });
            previousOffset = offset;
        }
        local.Add(end.Clone());

        var combined = new List<Vector2>(reference.Count + local.Count - 2);
        for (int index = 0; index < reference.Count - 1; index++)
        {
            if (index == segmentIndex)
            {
                if (combined.Count == 0)
                    combined.AddRange(local.Select(point => point.Clone()));
                else
                    combined.AddRange(local.Skip(1).Select(point => point.Clone()));
            }
            else
            {
                if (combined.Count == 0)
                    combined.Add(reference[index].Clone());
                combined.Add(reference[index + 1].Clone());
            }
        }
        candidate = combined;
        return true;
    }

    private static long FractionOffset(long length, int numerator, int denominator)
    {
        long quotient = length / denominator;
        long remainder = length % denominator;
        return checked(quotient * numerator + remainder * numerator / denominator);
    }

    private static long MoveAlong(Vector2 start, Vector2 end, bool horizontal, long offset)
    {
        if (horizontal)
        {
            if (end.XNm >= start.XNm)
                return checked(start.XNm + offset);
            return checked(start.XNm - offset);
        }
        if (end.YNm >= start.YNm)
            return checked(start.YNm + offset);
        return checked(start.YNm - offset);
    }

    private static void ValidateSimplePath(IReadOnlyList<Vector2> points, long minX, long minY, long maxX, long maxY)
    {
        if (points.Count < 2)
            throw new AutomationException("invalid_route_tune", "route must contain at least two points.");
        for (int index = 0; index < points.Count; index++)
        {
            if (!Inside(points[index], minX, minY, maxX, maxY))
                throw new AutomationException("invalid_route_tune", "route points must remain inside the width and clearance margin.");
            if (index == 0)
                continue;
            Vector2 previous = points[index - 1], current = points[index];
            if (previous.XNm == current.XNm && previous.YNm == current.YNm)
                throw new AutomationException("invalid_route_tune", "route segments must be nonzero.");
            if (previous.XNm != current.XNm && previous.YNm != current.YNm)
                throw new AutomationException("invalid_route_tune", "route segments must be axis-aligned.");
        }

        for (int left = 0; left < points.Count - 1; left++)
        for (int right = left + 1; right < points.Count - 1; right++)
        {
            if (!SegmentsIntersect(points[left], points[left + 1], points[right], points[right + 1]))
                continue;
            if (right == left + 1 && OnlySharedJunction(points[left], points[left + 1], points[right], points[right + 1]))
                continue;
            throw new AutomationException("invalid_route_tune", "route geometry self-crosses or overlaps another segment.");
        }
    }

    private static bool OnlySharedJunction(Vector2 firstStart, Vector2 firstEnd, Vector2 secondStart, Vector2 secondEnd)
    {
        if (!firstEnd.Equals(secondStart))
            return false;
        if (firstStart.YNm == firstEnd.YNm && secondStart.YNm == secondEnd.YNm)
        {
            long overlapStart = Math.Max(Math.Min(firstStart.XNm, firstEnd.XNm), Math.Min(secondStart.XNm, secondEnd.XNm));
            long overlapEnd = Math.Min(Math.Max(firstStart.XNm, firstEnd.XNm), Math.Max(secondStart.XNm, secondEnd.XNm));
            return firstStart.YNm != secondStart.YNm || overlapStart == overlapEnd;
        }
        if (firstStart.XNm == firstEnd.XNm && secondStart.XNm == secondEnd.XNm)
        {
            long overlapStart = Math.Max(Math.Min(firstStart.YNm, firstEnd.YNm), Math.Min(secondStart.YNm, secondEnd.YNm));
            long overlapEnd = Math.Min(Math.Max(firstStart.YNm, firstEnd.YNm), Math.Max(secondStart.YNm, secondEnd.YNm));
            return firstStart.XNm != secondStart.XNm || overlapStart == overlapEnd;
        }
        return true;
    }

    private static bool SegmentsIntersect(Vector2 firstStart, Vector2 firstEnd, Vector2 secondStart, Vector2 secondEnd)
    {
        bool firstHorizontal = firstStart.YNm == firstEnd.YNm;
        bool secondHorizontal = secondStart.YNm == secondEnd.YNm;
        if (firstHorizontal && secondHorizontal)
            return firstStart.YNm == secondStart.YNm
                && RangesOverlap(firstStart.XNm, firstEnd.XNm, secondStart.XNm, secondEnd.XNm);
        if (!firstHorizontal && !secondHorizontal)
            return firstStart.XNm == secondStart.XNm
                && RangesOverlap(firstStart.YNm, firstEnd.YNm, secondStart.YNm, secondEnd.YNm);
        Vector2 horizontalStart = firstHorizontal ? firstStart : secondStart;
        Vector2 horizontalEnd = firstHorizontal ? firstEnd : secondEnd;
        Vector2 verticalStart = firstHorizontal ? secondStart : firstStart;
        Vector2 verticalEnd = firstHorizontal ? secondEnd : firstEnd;
        return Between(verticalStart.XNm, horizontalStart.XNm, horizontalEnd.XNm)
            && Between(horizontalStart.YNm, verticalStart.YNm, verticalEnd.YNm);
    }

    private static bool RangesOverlap(long firstStart, long firstEnd, long secondStart, long secondEnd)
        => Math.Max(Math.Min(firstStart, firstEnd), Math.Min(secondStart, secondEnd))
            <= Math.Min(Math.Max(firstStart, firstEnd), Math.Max(secondStart, secondEnd));

    private static bool Between(long value, long first, long second)
        => value >= Math.Min(first, second) && value <= Math.Max(first, second);

    private static bool TryPositiveDifference(long lower, long upper, out long difference)
    {
        difference = 0;
        if (upper < lower)
            return false;
        if (lower < 0 && upper > long.MaxValue + lower)
            return false;
        difference = upper - lower;
        return true;
    }

    private static bool Inside(Vector2 point, long minX, long minY, long maxX, long maxY)
        => point.XNm >= minX && point.XNm <= maxX && point.YNm >= minY && point.YNm <= maxY;

    private static long Distance(Vector2 left, Vector2 right)
    {
        double dx = (double) right.XNm - left.XNm;
        double dy = (double) right.YNm - left.YNm;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(distance) || distance > long.MaxValue - 0.5)
            throw new AutomationException("invalid_route_tune", "route coordinates exceed the bounded length range.");
        return checked((long) Math.Round(distance, MidpointRounding.AwayFromZero));
    }

    private static bool Inside(Vector2 point, Bounds bounds)
        => Inside(point, bounds.MinXNm, bounds.MinYNm, bounds.MaxXNm, bounds.MaxYNm);

    internal static string BuildPlanId(DocumentSpecifier document, DocumentLifecycleState expected,
        TuneRequest request, BoardLayer layer, IReadOnlyList<Vector2> reference)
    {
        var normalizedRequest = new
        {
            netName = request.NetName,
            layer = layer.ToString(),
            reference,
            widthNm = request.WidthNm,
            clearanceNm = request.ClearanceNm,
            targetAdditionalLengthNm = request.TargetAdditionalLengthNm,
            pitchNm = request.PitchNm,
            maxSegments = request.MaxSegments,
            corridor = request.Corridor
        };
        var identity = new
        {
            document = SchematicJson.Formatter.Format(document),
            processEpoch = expected.ProcessEpoch,
            nativeIdentity = expected.NativeIdentity,
            stateSha256 = expected.StateSha256,
            revisionEpoch = expected.Revision.Epoch,
            revisionSequence = expected.Revision.Sequence,
            request = normalizedRequest
        };
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity, JsonOptions))));
    }
    private static BoardLayer ParseLayer(string value)
    {
        string normalized = value.Replace(".", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        string enumName = normalized.StartsWith("Bl", StringComparison.OrdinalIgnoreCase) ? normalized : "Bl" + normalized;
        if (!System.Enum.TryParse<BoardLayer>(enumName, true, out var layer))
            throw new AutomationException("invalid_route_tune", "layer must name a copper BoardLayer value such as F.Cu or B.Cu.");
        int numeric = (int)layer;
        if (numeric < (int)BoardLayer.BlFCu || numeric > (int)BoardLayer.BlBCu)
            throw new AutomationException("invalid_route_tune", "layer must name a copper BoardLayer value such as F.Cu or B.Cu.");
        return layer;
    }
    private static string GuidFrom(string seed, int index) => GuidFromBytes(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed + ":" + index)));
    private static string GuidFromBytes(byte[] bytes) => new Guid(bytes.AsSpan(0, 16)).ToString("D");
    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    { try { return SchematicJson.Parser.Parse<T>(json); } catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException) { throw new AutomationException(code, error.Message); } }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    { try { return await action(); } catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException or InvalidProtocolBufferException or InvalidJsonException or JsonException) { var structured = JsonSerializer.SerializeToElement(new { errorCode = error is AutomationException known ? known.Code : "pcb_route_tune_failed", errorMessage = error.Message }); return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] }; } }
}
