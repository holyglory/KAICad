using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ProtoAny = Google.Protobuf.WellKnownTypes.Any;
using BoardTrack = Kiapi.Board.Types.Track;
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

/// <summary>Revision-bound, proposal-only differential-pair geometry.</summary>
[McpServerToolType]
public sealed class PcbDifferentialPairTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_diff_pair_plan", ReadOnly = true),
     Description("Build a deterministic, proposal-only differential-pair route from two explicitly named native nets. requestJson contains positiveNet, negativeNet, start/end centerline points, copper layer, widthNm, gapNm, and explicit bounds. Both native nets and their effective netclasses are checked at the same lifecycle state; exact parallel Track candidates are returned with equal geometry. DRC, impedance, skew and manufacturing validity are not claimed and no copper is committed."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "explicit PCB lifecycle state, paired native nets, deterministic matched geometry")]
    public Task<CallToolResult> Plan(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_diff_pair_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_diff_pair_target", "Differential-pair planning requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_diff_pair_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_diff_pair_state", "The expected observation must identify the same PCB and native process epoch.");
        PairRequest request = JsonSerializer.Deserialize<PairRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_diff_pair", "requestJson must be a JSON object.");
        Validate(request);
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before differential-pair planning.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before differential-pair planning; refresh the lifecycle checkpoint.");
        BoardLayer layer = ParseLayer(request.Layer);
        NetsResponse nets = await client.InvokeAsync<GetNets, NetsResponse>(new() { Board = document }, cancellationToken);
        Net positive = FindNet(nets, request.PositiveNet);
        Net negative = FindNet(nets, request.NegativeNet);
        GetNetClassForNets classes = new();
        classes.Net.Add(positive.Clone()); classes.Net.Add(negative.Clone());
        NetClassForNetsResponse classResponse = await client.InvokeAsync<GetNetClassForNets, NetClassForNetsResponse>(classes, cancellationToken);
        if (!classResponse.Classes.TryGetValue(request.PositiveNet, out var positiveClass)
            || !classResponse.Classes.TryGetValue(request.NegativeNet, out var negativeClass))
            throw new AutomationException("diff_pair_net_class_missing", "The native board returned no effective netclass for one paired net.");
        if (!string.IsNullOrWhiteSpace(request.NetClassName)
            && (!string.Equals(positiveClass.Name, request.NetClassName, StringComparison.Ordinal)
                || !string.Equals(negativeClass.Name, request.NetClassName, StringComparison.Ordinal)))
            throw new AutomationException("diff_pair_net_class_mismatch", "Both differential-pair nets must use the requested effective netclass.");

        Vector2[] centerline = Centerline(request);
        (Vector2 positiveStart, Vector2 positiveEnd, Vector2 negativeStart, Vector2 negativeEnd) = OffsetPair(
            centerline[0], centerline[1], checked(request.GapNm + request.WidthNm));
        CreateItems candidate = BuildCandidate(document, layer, request, positiveStart, positiveEnd, negativeStart, negativeEnd);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("diff_pair_changed", "The board changed while the differential pair was planned; discard the proposal.");
        var result = new
        {
            instanceId, processEpoch = session.Epoch, document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            positiveNet = positive.Name, negativeNet = negative.Name,
            positiveNetClass = positiveClass.Name, negativeNetClass = negativeClass.Name,
            layer = layer.ToString(), widthNm = request.WidthNm, gapNm = request.GapNm,
            centerline, candidateRequestJson = BoardJson.Formatter.Format(candidate),
            matchedLengthNm = Distance(centerline[0], centerline[1]),
            skewNm = 0L, routeFound = true, drcValidated = false, nativeCommit = false,
            limitations = new[] { "proposal_only", "drc_not_validated", "impedance_not_validated", "skew_not_validated", "obstacles_not_modeled" }
        };
        JsonElement structured = JsonSerializer.SerializeToElement(result, JsonOptions);
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    [McpServerTool(Name = "kicad_pcb_diff_pair_validate", ReadOnly = true),
     Description("Validate the exact native geometry of two explicitly named PCB nets at one lifecycle checkpoint. requestJson contains positiveNet, negativeNet, widthNm, gapNm, optional maxSkewNm, toleranceNm, parallelTolerance and netClassName. The validator reports deterministic per-net lengths, skew, minimum centerline spacing, layer/topology agreement and parallelism violations; it never mutates the board or claims DRC, impedance or manufacturing validity."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "explicit PCB lifecycle state, paired native route geometry, spacing/parallelism/skew validation")]
    public Task<CallToolResult> ValidatePair(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_diff_pair_validation_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_diff_pair_validation_target", "Differential-pair validation requires an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_diff_pair_validation_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_diff_pair_validation_state", "The expected observation must identify the same PCB and native process epoch.");
        PairValidationRequest request;
        try
        {
            request = JsonSerializer.Deserialize<PairValidationRequest>(requestJson, JsonOptions)
                ?? throw new AutomationException("invalid_diff_pair_validation", "requestJson must be a JSON object.");
        }
        catch (JsonException error)
        {
            throw new AutomationException("invalid_diff_pair_validation", error.Message);
        }
        Validate(request);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before differential-pair validation.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before differential-pair validation; refresh the lifecycle checkpoint.");

        NetsResponse nets = await client.InvokeAsync<GetNets, NetsResponse>(new() { Board = document }, cancellationToken);
        Net positive = FindNet(nets, request.PositiveNet);
        Net negative = FindNet(nets, request.NegativeNet);
        if (!string.IsNullOrWhiteSpace(request.NetClassName))
        {
            GetNetClassForNets classes = new();
            classes.Net.Add(positive.Clone());
            classes.Net.Add(negative.Clone());
            NetClassForNetsResponse classResponse = await client.InvokeAsync<GetNetClassForNets, NetClassForNetsResponse>(classes, cancellationToken);
            if (!classResponse.Classes.TryGetValue(request.PositiveNet, out var positiveClass)
                || !classResponse.Classes.TryGetValue(request.NegativeNet, out var negativeClass)
                || !string.Equals(positiveClass.Name, request.NetClassName, StringComparison.Ordinal)
                || !string.Equals(negativeClass.Name, request.NetClassName, StringComparison.Ordinal))
                throw new AutomationException("diff_pair_net_class_mismatch", "Both differential-pair nets must use the requested effective netclass.");
        }

        GetItemsResponse response = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
        }, cancellationToken);
        PairValidationSummary summary = ValidateGeometry(request, response.Items);
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("diff_pair_validation_changed", "The board changed while the differential pair was validated; discard the result.");

        var result = new
        {
            instanceId, processEpoch = session.Epoch, document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            positiveNet = positive.Name, negativeNet = negative.Name,
            widthNm = request.WidthNm, gapNm = request.GapNm,
            requiredCenterlineSpacingNm = checked(request.WidthNm + request.GapNm),
            maxSkewNm = request.MaxSkewNm, toleranceNm = request.ToleranceNm,
            parallelTolerance = request.ParallelTolerance,
            positive = new
            {
                itemCount = summary.Positive.ItemCount, trackArcCount = summary.Positive.TrackArcCount,
                viaCount = summary.Positive.ViaCount, lengthNm = Round(summary.Positive.LengthNm),
                layers = summary.Positive.Layers
            },
            negative = new
            {
                itemCount = summary.Negative.ItemCount, trackArcCount = summary.Negative.TrackArcCount,
                viaCount = summary.Negative.ViaCount, lengthNm = Round(summary.Negative.LengthNm),
                layers = summary.Negative.Layers
            },
            matchedSegmentCount = summary.MatchedSegmentCount,
            measuredSkewNm = Round(summary.SkewNm),
            minimumCenterlineSpacingNm = summary.MinimumSpacingNm is { } spacing ? Round(spacing) : (double?)null,
            maximumParallelError = summary.MaximumParallelError,
            routeFound = summary.RouteFound,
            compliant = summary.Compliant,
            violations = summary.Violations,
            drcValidated = false, nativeCommit = false,
            limitations = new[] { "measurement_only", "drc_not_validated", "impedance_not_validated", "manufacturing_not_validated" }
        };
        JsonElement structured = JsonSerializer.SerializeToElement(result, JsonOptions);
        return new CallToolResult { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    });

    internal static CreateItems BuildCandidate(DocumentSpecifier document, BoardLayer layer, PairRequest request,
        Vector2 positiveStart, Vector2 positiveEnd, Vector2 negativeStart, Vector2 negativeEnd)
    {
        var result = new CreateItems { Header = new() { Document = document } };
        result.Items.Add(ProtoAny.Pack(Track(document, layer, request.PositiveNet, positiveStart, positiveEnd, "positive", request.WidthNm)));
        result.Items.Add(ProtoAny.Pack(Track(document, layer, request.NegativeNet, negativeStart, negativeEnd, "negative", request.WidthNm)));
        return result;
    }

    private static Track Track(DocumentSpecifier document, BoardLayer layer, string net, Vector2 start, Vector2 end, string role, long widthNm)
    {
        string seed = $"{document.Project.Path}|{document.BoardFilename}|{net}|{role}|{start.XNm}|{start.YNm}|{end.XNm}|{end.YNm}";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        var track = new Track { Id = new() { Value = new Guid(hash.AsSpan(0, 16)).ToString("D") }, Start = start, End = end,
            Width = new() { ValueNm = widthNm }, Layer = layer, Net = new() { Name = net } };
        track.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.diff_pair.role", Value = role });
        return track;
    }

    internal sealed class PairRequest
    {
        public string PositiveNet { get; set; } = ""; public string NegativeNet { get; set; } = "";
        public string NetClassName { get; set; } = ""; public string Layer { get; set; } = "";
        public Vector2? Start { get; set; } public Vector2? End { get; set; }
        public long WidthNm { get; set; } public long GapNm { get; set; }
        public Bounds? Bounds { get; set; }
    }

    internal sealed class PairValidationRequest
    {
        public string PositiveNet { get; set; } = "";
        public string NegativeNet { get; set; } = "";
        public string NetClassName { get; set; } = "";
        public long WidthNm { get; set; }
        public long GapNm { get; set; }
        public double? MaxSkewNm { get; set; }
        public double ToleranceNm { get; set; }
        public double ParallelTolerance { get; set; } = 1e-6;
    }

    internal sealed record PairValidationIssue(string Code, string Message, string? PositiveItemId = null, string? NegativeItemId = null);
    internal sealed record PairRouteSummary(string NetName, int ItemCount, int TrackArcCount, int ViaCount,
        double LengthNm, string[] Layers);
    internal sealed record PairValidationSummary(PairRouteSummary Positive, PairRouteSummary Negative,
        int MatchedSegmentCount, double SkewNm, double? MinimumSpacingNm, double MaximumParallelError,
        bool RouteFound, PairValidationIssue[] Violations)
    {
        public bool Compliant => RouteFound && Violations.Length == 0;
    }
    private sealed record PairSegment(string Id, string NetName, string Kind, string Layer, Vector2 Start,
        Vector2? Mid, Vector2 End, long WidthNm, double LengthNm);

    internal sealed class Bounds { public long MinXNm { get; set; } public long MinYNm { get; set; } public long MaxXNm { get; set; } public long MaxYNm { get; set; } }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private static Net FindNet(NetsResponse response, string name) => response.Nets.FirstOrDefault(net => net.Name == name)
        ?? throw new AutomationException("diff_pair_net_not_found", $"The PCB has no native net named '{name}'.");

    private static void Validate(PairRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PositiveNet) || string.IsNullOrWhiteSpace(request.NegativeNet)
            || request.PositiveNet == request.NegativeNet || request.Start is null || request.End is null || request.Bounds is null)
            throw new AutomationException("invalid_diff_pair", "Two distinct nets, start/end points and bounds are required.");
        if (request.WidthNm <= 0 || request.GapNm <= 0 || request.Bounds.MinXNm >= request.Bounds.MaxXNm || request.Bounds.MinYNm >= request.Bounds.MaxYNm)
            throw new AutomationException("invalid_diff_pair", "Width, gap and bounds must be positive.");
        if (!Inside(request.Start, request.Bounds) || !Inside(request.End, request.Bounds))
            throw new AutomationException("invalid_diff_pair", "Pair anchors must lie inside bounds.");
        if (request.Start.XNm != request.End.XNm && request.Start.YNm != request.End.YNm)
            throw new AutomationException("invalid_diff_pair", "This proposal requires an axis-aligned centerline.");
    }

    private static void Validate(PairValidationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PositiveNet) || string.IsNullOrWhiteSpace(request.NegativeNet)
            || request.PositiveNet.Length > 512 || request.NegativeNet.Length > 512
            || request.PositiveNet == request.NegativeNet)
            throw new AutomationException("invalid_diff_pair_validation", "positiveNet and negativeNet must be distinct nonempty names of at most 512 characters.");
        if (request.NetClassName is null || request.NetClassName.Length > 512)
            throw new AutomationException("invalid_diff_pair_validation", "netClassName must be at most 512 characters.");
        if (request.WidthNm <= 0 || request.GapNm <= 0 || request.WidthNm > long.MaxValue - request.GapNm)
            throw new AutomationException("invalid_diff_pair_validation", "widthNm and gapNm must be positive and must fit the bounded planner range.");
        if (!double.IsFinite(request.ToleranceNm) || request.ToleranceNm < 0
            || request.MaxSkewNm is { } maxSkew && (!double.IsFinite(maxSkew) || maxSkew < 0)
            || !double.IsFinite(request.ParallelTolerance) || request.ParallelTolerance < 0 || request.ParallelTolerance > 0.25)
            throw new AutomationException("invalid_diff_pair_validation", "tolerance, maxSkewNm and parallelTolerance must be finite and nonnegative; parallelTolerance cannot exceed 0.25.");
    }

    internal static PairValidationSummary ValidateGeometry(PairValidationRequest request, IEnumerable<ProtoAny> packedItems)
    {
        var positive = new List<PairSegment>();
        var negative = new List<PairSegment>();
        int positiveVias = 0, negativeVias = 0;
        foreach (ProtoAny packed in packedItems)
        {
            if (packed.Is(Via.Descriptor))
            {
                Via via = packed.Unpack<Via>();
                if (via.Net.Name == request.PositiveNet) positiveVias++;
                else if (via.Net.Name == request.NegativeNet) negativeVias++;
                continue;
            }
            PairSegment? segment = Segment(packed);
            if (segment is null) continue;
            if (segment.NetName == request.PositiveNet) positive.Add(segment);
            else if (segment.NetName == request.NegativeNet) negative.Add(segment);
        }

        PairSegment[] orderedPositive = Order(positive);
        PairSegment[] orderedNegative = Order(negative);
        var issues = new List<PairValidationIssue>();
        if (orderedPositive.Length == 0)
            issues.Add(new("positive_route_missing", "No Track or Arc geometry was found for the positive differential-pair net."));
        if (orderedNegative.Length == 0)
            issues.Add(new("negative_route_missing", "No Track or Arc geometry was found for the negative differential-pair net."));
        if (positiveVias != 0 || negativeVias != 0)
            issues.Add(new("via_not_supported", "Differential-pair validation currently requires matched Track/Arc geometry and reports vias as unsupported.",
                positiveVias == 0 ? null : $"{positiveVias} vias", negativeVias == 0 ? null : $"{negativeVias} vias"));
        if (orderedPositive.Length != orderedNegative.Length)
            issues.Add(new("segment_count_mismatch", "The paired nets do not contain the same number of Track/Arc route segments."));

        double positiveLength = orderedPositive.Sum(segment => segment.LengthNm);
        double negativeLength = orderedNegative.Sum(segment => segment.LengthNm);
        double skew = Math.Abs(positiveLength - negativeLength);
        if (request.MaxSkewNm is { } maxSkew && skew > maxSkew + request.ToleranceNm)
            issues.Add(new("skew_exceeded", $"Measured differential-pair skew {Round(skew)} nm exceeds the requested bound {Round(maxSkew)} nm.", null, null));

        int matched = Math.Min(orderedPositive.Length, orderedNegative.Length);
        double minimumSpacing = double.PositiveInfinity, maximumParallelError = 0;
        for (int index = 0; index < matched; index++)
        {
            PairSegment left = orderedPositive[index], right = orderedNegative[index];
            if (!string.Equals(left.Layer, right.Layer, StringComparison.Ordinal))
                issues.Add(new("layer_mismatch", $"Paired route segments use different copper layers ({left.Layer} and {right.Layer}).", left.Id, right.Id));
            if (!string.Equals(left.Kind, right.Kind, StringComparison.Ordinal))
                issues.Add(new("topology_mismatch", "Paired route segments use different geometry kinds.", left.Id, right.Id));
            if (Math.Abs(left.WidthNm - right.WidthNm) > request.ToleranceNm)
                issues.Add(new("width_mismatch", $"Paired route widths differ by {Round(Math.Abs(left.WidthNm - right.WidthNm))} nm.", left.Id, right.Id));
            if (left.LengthNm <= request.ToleranceNm || right.LengthNm <= request.ToleranceNm)
                issues.Add(new("zero_length", "Differential-pair geometry contains a zero-length or tolerance-sized segment.", left.Id, right.Id));

            double parallelError = ParallelError(left, right);
            maximumParallelError = Math.Max(maximumParallelError, parallelError);
            if (parallelError > request.ParallelTolerance)
                issues.Add(new("non_parallel", $"Paired route segments are not parallel within tolerance {request.ParallelTolerance:R}.", left.Id, right.Id));

            double spacing = PairSpacing(left, right);
            minimumSpacing = Math.Min(minimumSpacing, spacing);
            if (spacing + request.ToleranceNm < request.WidthNm + request.GapNm)
                issues.Add(new("spacing_below_gap", $"Minimum centerline spacing {Round(spacing)} nm is below the required {request.WidthNm + request.GapNm} nm.", left.Id, right.Id));
        }

        PairRouteSummary positiveSummary = new(request.PositiveNet, orderedPositive.Length + positiveVias,
            orderedPositive.Length, positiveVias, positiveLength, orderedPositive.Select(segment => segment.Layer).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        PairRouteSummary negativeSummary = new(request.NegativeNet, orderedNegative.Length + negativeVias,
            orderedNegative.Length, negativeVias, negativeLength, orderedNegative.Select(segment => segment.Layer).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        PairValidationIssue[] violations = issues
            .DistinctBy(issue => (issue.Code, issue.PositiveItemId, issue.NegativeItemId), EqualityComparer<(string Code, string? PositiveItemId, string? NegativeItemId)>.Default)
            .ToArray();
        return new(positiveSummary, negativeSummary, matched, skew,
            double.IsPositiveInfinity(minimumSpacing) ? null : minimumSpacing, maximumParallelError,
            orderedPositive.Length != 0 && orderedNegative.Length != 0, violations);
    }

    private static PairSegment? Segment(ProtoAny packed)
    {
        if (packed.Is(BoardTrack.Descriptor))
        {
            BoardTrack item = packed.Unpack<BoardTrack>();
            return new(item.Id.Value, item.Net.Name, "track", item.Layer.ToString(), item.Start.Clone(), null, item.End.Clone(), item.Width.ValueNm,
                Distance(item.Start, item.End));
        }
        if (packed.Is(Arc.Descriptor))
        {
            Arc item = packed.Unpack<Arc>();
            return new(item.Id.Value, item.Net.Name, "arc", item.Layer.ToString(), item.Start.Clone(), item.Mid.Clone(), item.End.Clone(), item.Width.ValueNm,
                Distance(item.Start, item.Mid) + Distance(item.Mid, item.End));
        }
        return null;
    }

    private static PairSegment[] Order(IEnumerable<PairSegment> segments) => segments
        .OrderBy(segment => segment.Layer, StringComparer.Ordinal)
        .ThenBy(segment => Math.Min(segment.Start.XNm, segment.End.XNm))
        .ThenBy(segment => Math.Min(segment.Start.YNm, segment.End.YNm))
        .ThenBy(segment => Math.Max(segment.Start.XNm, segment.End.XNm))
        .ThenBy(segment => Math.Max(segment.Start.YNm, segment.End.YNm))
        .ThenBy(segment => segment.Kind, StringComparer.Ordinal)
        .ThenBy(segment => segment.Id, StringComparer.Ordinal)
        .ToArray();

    private static double ParallelError(PairSegment left, PairSegment right)
    {
        double leftX = left.End.XNm - left.Start.XNm, leftY = left.End.YNm - left.Start.YNm;
        double rightX = right.End.XNm - right.Start.XNm, rightY = right.End.YNm - right.Start.YNm;
        double denominator = Math.Sqrt((leftX * leftX + leftY * leftY) * (rightX * rightX + rightY * rightY));
        return denominator <= 0 ? 1 : Math.Abs(leftX * rightY - leftY * rightX) / denominator;
    }

    private static double PairSpacing(PairSegment left, PairSegment right)
    {
        (Vector2 Start, Vector2 End)[] leftParts = Parts(left), rightParts = Parts(right);
        double minimum = double.PositiveInfinity;
        foreach (var a in leftParts)
            foreach (var b in rightParts)
                minimum = Math.Min(minimum, SegmentDistance(a.Start, a.End, b.Start, b.End));
        return minimum;
    }

    private static (Vector2 Start, Vector2 End)[] Parts(PairSegment segment) => segment.Mid is null
        ? [(segment.Start, segment.End)]
        : [(segment.Start, segment.Mid), (segment.Mid, segment.End)];

    private static double SegmentDistance(Vector2 firstStart, Vector2 firstEnd, Vector2 secondStart, Vector2 secondEnd)
    {
        if (Intersects(firstStart, firstEnd, secondStart, secondEnd)) return 0;
        return Math.Min(Math.Min(PointSegmentDistance(firstStart, secondStart, secondEnd), PointSegmentDistance(firstEnd, secondStart, secondEnd)),
            Math.Min(PointSegmentDistance(secondStart, firstStart, firstEnd), PointSegmentDistance(secondEnd, firstStart, firstEnd)));
    }

    private static double PointSegmentDistance(Vector2 point, Vector2 start, Vector2 end)
    {
        double dx = end.XNm - start.XNm, dy = end.YNm - start.YNm;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0) return Distance(point, start);
        double t = Math.Clamp(((point.XNm - start.XNm) * dx + (point.YNm - start.YNm) * dy) / lengthSquared, 0, 1);
        double projectedX = start.XNm + dx * t, projectedY = start.YNm + dy * t;
        double deltaX = point.XNm - projectedX, deltaY = point.YNm - projectedY;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static bool Intersects(Vector2 firstStart, Vector2 firstEnd, Vector2 secondStart, Vector2 secondEnd)
    {
        double first = Cross(firstStart, firstEnd, secondStart), second = Cross(firstStart, firstEnd, secondEnd);
        double third = Cross(secondStart, secondEnd, firstStart), fourth = Cross(secondStart, secondEnd, firstEnd);
        const double epsilon = 1e-9;
        if (((first > epsilon && second < -epsilon) || (first < -epsilon && second > epsilon))
            && ((third > epsilon && fourth < -epsilon) || (third < -epsilon && fourth > epsilon))) return true;
        return Math.Abs(first) <= epsilon && OnSegment(firstStart, secondStart, firstEnd)
            || Math.Abs(second) <= epsilon && OnSegment(firstStart, secondEnd, firstEnd)
            || Math.Abs(third) <= epsilon && OnSegment(secondStart, firstStart, secondEnd)
            || Math.Abs(fourth) <= epsilon && OnSegment(secondStart, firstEnd, secondEnd);
    }

    private static double Cross(Vector2 first, Vector2 second, Vector2 point) =>
        (second.XNm - first.XNm) * (double)(point.YNm - first.YNm)
        - (second.YNm - first.YNm) * (double)(point.XNm - first.XNm);

    private static bool OnSegment(Vector2 start, Vector2 point, Vector2 end) =>
        point.XNm >= Math.Min(start.XNm, end.XNm) && point.XNm <= Math.Max(start.XNm, end.XNm)
        && point.YNm >= Math.Min(start.YNm, end.YNm) && point.YNm <= Math.Max(start.YNm, end.YNm);

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    private static Vector2[] Centerline(PairRequest request) => [request.Start!.Clone(), request.End!.Clone()];
    private static (Vector2, Vector2, Vector2, Vector2) OffsetPair(Vector2 start, Vector2 end, long offset)
    {
        bool horizontal = start.YNm == end.YNm;
        if (horizontal) return (new() { XNm = start.XNm, YNm = start.YNm - offset / 2 }, new() { XNm = end.XNm, YNm = end.YNm - offset / 2 },
            new() { XNm = start.XNm, YNm = start.YNm + offset / 2 }, new() { XNm = end.XNm, YNm = end.YNm + offset / 2 });
        return (new() { XNm = start.XNm - offset / 2, YNm = start.YNm }, new() { XNm = end.XNm - offset / 2, YNm = end.YNm },
            new() { XNm = start.XNm + offset / 2, YNm = start.YNm }, new() { XNm = end.XNm + offset / 2, YNm = end.YNm });
    }
    private static bool Inside(Vector2 p, Bounds b) => p.XNm >= b.MinXNm && p.XNm <= b.MaxXNm && p.YNm >= b.MinYNm && p.YNm <= b.MaxYNm;
    private static double Distance(Vector2 a, Vector2 b) => Math.Sqrt(Math.Pow((double)b.XNm - a.XNm, 2) + Math.Pow((double)b.YNm - a.YNm, 2));
    private static BoardLayer ParseLayer(string value) => System.Enum.TryParse<BoardLayer>("Bl" + value.Replace(".", "", StringComparison.Ordinal), true, out var layer)
        && layer is not BoardLayer.BlUnknown and not BoardLayer.BlUndefined ? layer : throw new AutomationException("invalid_diff_pair", "layer must name a copper layer.");
    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    { try { return SchematicJson.Parser.Parse<T>(json); } catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException) { throw new AutomationException(code, error.Message); } }
    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    { try { return await action(); } catch (Exception error) when (error is AutomationException or NativeApiException or NngException or IOException or InvalidProtocolBufferException or InvalidJsonException or JsonException) { var structured = JsonSerializer.SerializeToElement(new { errorCode = error is AutomationException known ? known.Code : "pcb_diff_pair_failed", errorMessage = error.Message }); return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] }; } }
}
