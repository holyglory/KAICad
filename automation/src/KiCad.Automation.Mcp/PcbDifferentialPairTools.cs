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

    [McpServerTool(Name = "kicad_pcb_diff_pair_apply"),
     Description("Apply the complete Track-only differential-pair candidate from kicad_pcb_diff_pair_plan through the checked route-replacement transaction. requestJson names positiveNet and negativeNet; the candidate must contain at least two Tracks, distinct canonical IDs, explicit positive widths and endpoints, exactly those two net names, and one copper layer. A completed detached DRC receipt is required, and native undo, operation replay and indeterminate outcomes remain authoritative."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "exact differential-pair candidate, lifecycle checkpoint, detached DRC receipt, checked native transaction and operation UUID"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Apply(string instanceId, string deleteRequestJson, string createRequestJson,
        string expectedStateJson, string operationId, string requestJson, string drcJobStateJson,
        CancellationToken cancellationToken) => Execute(async () =>
    {
        if (string.IsNullOrWhiteSpace(drcJobStateJson))
            throw new AutomationException("diff_pair_apply_requires_drc",
                "Differential-pair apply requires a completed detached DRC receipt for the exact replacement geometry.");
        PairApplyRequest pair = JsonSerializer.Deserialize<PairApplyRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_diff_pair_apply", "requestJson must name positiveNet and negativeNet.");
        Validate(pair);
        CreateItems create = Parse<CreateItems>(createRequestJson, "invalid_diff_pair_apply_create");
        ValidateApplyCandidate(pair.PositiveNet, pair.NegativeNet, create);
        return await new PcbRouteOptimizationTools(registry).Apply(
            instanceId, deleteRequestJson, createRequestJson, expectedStateJson, operationId,
            cancellationToken, drcJobStateJson);
    });

    [McpServerTool(Name = "kicad_pcb_diff_pair_receipt", ReadOnly = true),
     Description("Read the durable receipt for a differential-pair apply after a lost reply or native process replacement. Supply expectedRequestJson from the original route apply when the epoch changed; the tool validates exact request identity, never resubmits geometry and never performs a native receipt lookup during durable recovery."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "operation UUID and process-owned differential-pair receipt"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Receipt(string instanceId, string expectedProcessEpoch, string operationId,
        CancellationToken cancellationToken, string expectedRequestJson = "")
        => new PcbRouteOptimizationTools(registry).Receipt(
            instanceId, expectedProcessEpoch, operationId, cancellationToken, expectedRequestJson);

    [McpServerTool(Name = "kicad_pcb_diff_pair_validate", ReadOnly = true),
     Description("Validate one matched connected straight-track differential-pair route per net at one lifecycle checkpoint. requestJson contains positiveNet, negativeNet, widthNm, minimumGapNm, optional targetGapNm, maxSkewNm, toleranceNm, parallelTolerance and netClassName. The validator reports polyline lengths, skew, copper edge gap, endpoint correspondence, layer agreement and per-segment parallelism. Arcs, vias, disconnected or branched routes, and unknown geometry are explicit incomplete results; the tool never mutates the board or claims DRC, impedance or manufacturing validity."),
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
        catch (Exception error) when (error is JsonException or ArgumentNullException)
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
            supportedScope = summary.SupportedScope
                ? summary.MatchedSegmentCount == 1 ? "single_straight_track_per_net" : "matched_multi_segment_straight_track_route_per_net"
                : "unsupported",
            validationStatus = summary.SupportedScope ? "complete" : "incomplete",
            widthNm = request.WidthNm, minimumGapNm = request.MinimumGapNm, targetGapNm = request.TargetGapNm,
            requiredMinimumCenterlineSpacingNm = checked(request.WidthNm + request.MinimumGapNm),
            maxSkewNm = request.MaxSkewNm, toleranceNm = request.ToleranceNm,
            parallelTolerance = request.ParallelTolerance,
            positive = new
            {
                itemCount = summary.Positive.ItemCount, trackCount = summary.Positive.TrackCount, arcCount = summary.Positive.ArcCount,
                viaCount = summary.Positive.ViaCount, lengthNm = summary.Positive.LengthNm is { } length ? Round(length) : (double?)null,
                partialStraightLengthNm = summary.Positive.PartialStraightLengthNm is { } partial ? Round(partial) : (double?)null,
                layers = summary.Positive.Layers
            },
            negative = new
            {
                itemCount = summary.Negative.ItemCount, trackCount = summary.Negative.TrackCount, arcCount = summary.Negative.ArcCount,
                viaCount = summary.Negative.ViaCount, lengthNm = summary.Negative.LengthNm is { } negativeLength ? Round(negativeLength) : (double?)null,
                partialStraightLengthNm = summary.Negative.PartialStraightLengthNm is { } negativePartial ? Round(negativePartial) : (double?)null,
                layers = summary.Negative.Layers
            },
            matchedSegmentCount = summary.MatchedSegmentCount,
            measuredSkewNm = summary.SkewNm is { } skew ? Round(skew) : (double?)null,
            partialStraightSkewNm = summary.PartialStraightSkewNm is { } partialSkew ? Round(partialSkew) : (double?)null,
            centerlineSpacingNm = summary.CenterlineSpacingNm is { } centerlineSpacing ? Round(centerlineSpacing) : (double?)null,
            copperEdgeGapNm = summary.CopperEdgeGapNm is { } copperGap ? Round(copperGap) : (double?)null,
            longitudinalOverlapNm = summary.LongitudinalOverlapNm is { } overlap ? Round(overlap) : (double?)null,
            longitudinalEndpointDeltaNm = summary.LongitudinalEndpointDeltaNm is { } endpointDelta ? Round(endpointDelta) : (double?)null,
            maximumParallelError = summary.SupportedScope ? summary.MaximumParallelError : (double?)null,
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
        public long MinimumGapNm { get; set; }
        public long? TargetGapNm { get; set; }
        public double? MaxSkewNm { get; set; }
        public double ToleranceNm { get; set; }
        public double ParallelTolerance { get; set; } = 1e-6;
    }

    private sealed class PairApplyRequest
    {
        public string PositiveNet { get; set; } = "";
        public string NegativeNet { get; set; } = "";
    }

    internal sealed record PairValidationIssue(string Code, string Message, string? PositiveItemId = null, string? NegativeItemId = null);
    internal sealed record PairRouteSummary(string NetName, int ItemCount, int TrackCount, int ArcCount, int ViaCount,
        double? LengthNm, double? PartialStraightLengthNm, string[] Layers);
    internal sealed record PairValidationSummary(PairRouteSummary Positive, PairRouteSummary Negative,
        int MatchedSegmentCount, double? SkewNm, double? PartialStraightSkewNm, double? CenterlineSpacingNm,
        double? CopperEdgeGapNm, double? LongitudinalOverlapNm, double? LongitudinalEndpointDeltaNm,
        double MaximumParallelError, bool RouteFound, bool SupportedScope, PairValidationIssue[] Violations)
    {
        public bool Compliant => RouteFound && SupportedScope && Violations.Length == 0;
    }
    private sealed record PairSegment(string Id, string NetName, string Kind, string Layer, Vector2 Start,
        Vector2 End, long WidthNm, double LengthNm);
    private sealed record PairRoute(PairSegment[] Segments, Vector2[] Points, double LengthNm,
        bool Connected, bool SimplePath, bool UniformLayer)
    {
        public bool Supported => Segments.Length != 0 && Connected && SimplePath && UniformLayer;
    }
    private readonly record struct PointKey(long XNm, long YNm);

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
        if (request.WidthNm <= 0 || request.MinimumGapNm < 0 || request.WidthNm > long.MaxValue - request.MinimumGapNm
            || request.TargetGapNm is < 0 || request.TargetGapNm is { } targetGap && targetGap < request.MinimumGapNm)
                    throw new AutomationException("invalid_diff_pair_validation", "widthNm must be positive, minimumGapNm must be nonnegative, and targetGapNm cannot be below minimumGapNm.");
        if (!double.IsFinite(request.ToleranceNm) || request.ToleranceNm < 0
            || request.MaxSkewNm is { } maxSkew && (!double.IsFinite(maxSkew) || maxSkew < 0)
            || !double.IsFinite(request.ParallelTolerance) || request.ParallelTolerance < 0 || request.ParallelTolerance > 0.25)
            throw new AutomationException("invalid_diff_pair_validation", "tolerance, maxSkewNm and parallelTolerance must be finite and nonnegative; parallelTolerance cannot exceed 0.25.");
    }

    private static void Validate(PairApplyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PositiveNet) || string.IsNullOrWhiteSpace(request.NegativeNet)
            || request.PositiveNet.Length > 512 || request.NegativeNet.Length > 512
            || request.PositiveNet == request.NegativeNet)
            throw new AutomationException("invalid_diff_pair_apply", "positiveNet and negativeNet must be distinct nonempty names of at most 512 characters.");
    }

    internal static void ValidateApplyCandidate(string positiveNet, string negativeNet, CreateItems create)
    {
        if (create.Items.Count < 2 || create.Items.Any(item => item is null || !item.Is(BoardTrack.Descriptor)))
            throw new AutomationException("invalid_diff_pair_apply_create",
                "A differential-pair apply candidate must contain at least two Track objects and no other object types.");

        BoardTrack[] tracks = create.Items.Select(item => item.Unpack<BoardTrack>()).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (BoardTrack track in tracks)
        {
            if (track.Id is null || !CanonicalUuid(track.Id.Value) || !ids.Add(track.Id.Value)
                || track.Net is null || string.IsNullOrWhiteSpace(track.Net.Name)
                || track.Start is null || track.End is null || track.Start.Equals(track.End)
                || track.Width is null || track.Width.ValueNm <= 0)
                throw new AutomationException("invalid_diff_pair_apply_create",
                    "Each differential-pair Track needs a distinct canonical ID, explicit nonzero endpoints and positive width.");
            if (!MultilayerRoutePlanner.IsCopperLayer(track.Layer))
                throw new AutomationException("invalid_diff_pair_apply_create",
                    "Differential-pair Tracks must use the same copper layer.");
        }

        var netNames = tracks.Select(track => track.Net.Name).ToHashSet(StringComparer.Ordinal);
        if (netNames.Count != 2 || !netNames.SetEquals(new[] { positiveNet, negativeNet }))
            throw new AutomationException("invalid_diff_pair_apply_create",
                "The candidate must contain exactly the requested positive and negative net names.");

        BoardLayer layer = tracks[0].Layer;
        if (tracks.Any(track => track.Layer != layer))
            throw new AutomationException("invalid_diff_pair_apply_create",
                "All differential-pair Tracks must use the same copper layer.");
    }

    private static bool CanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    internal static PairValidationSummary ValidateGeometry(PairValidationRequest request, IEnumerable<ProtoAny> packedItems)
    {
        if (packedItems is null)
            throw new AutomationException("invalid_diff_pair_validation_geometry", "The native geometry collection is required.");
        var positive = new List<PairSegment>();
        var negative = new List<PairSegment>();
        int positiveVias = 0, negativeVias = 0;
        int positiveArcs = 0, negativeArcs = 0, unknownItems = 0;
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProtoAny? packed in packedItems)
        {
            if (packed is null)
            {
                unknownItems++;
                continue;
            }
            if (!packed.Is(BoardTrack.Descriptor) && !packed.Is(Arc.Descriptor) && !packed.Is(Via.Descriptor))
            {
                unknownItems++;
                continue;
            }
            if (packed.Is(Via.Descriptor))
            {
                Via via = Unpack<Via>(packed, "via");
                ValidateIdentity("via", via.Id, via.Net);
                if (via.Net.Name == request.PositiveNet)
                {
                    positiveVias++;
                    AddIdentity(via.Id.Value, selectedIds);
                }
                else if (via.Net.Name == request.NegativeNet)
                {
                    negativeVias++;
                    AddIdentity(via.Id.Value, selectedIds);
                }
                continue;
            }
            if (packed.Is(Arc.Descriptor))
            {
                Arc arc = Unpack<Arc>(packed, "arc");
                ValidateIdentity("arc", arc.Id, arc.Net);
                if (arc.Net.Name == request.PositiveNet)
                {
                    positiveArcs++;
                    AddIdentity(arc.Id.Value, selectedIds);
                }
                else if (arc.Net.Name == request.NegativeNet)
                {
                    negativeArcs++;
                    AddIdentity(arc.Id.Value, selectedIds);
                }
                continue;
            }
            PairSegment? segment = Segment(packed);
            if (segment is null) continue;
            if (segment.NetName == request.PositiveNet)
            {
                AddIdentity(segment.Id, selectedIds);
                positive.Add(segment);
            }
            else if (segment.NetName == request.NegativeNet)
            {
                AddIdentity(segment.Id, selectedIds);
                negative.Add(segment);
            }
        }

        PairRoute positiveRoute = BuildRoute(positive);
        PairRoute negativeRoute = BuildRoute(negative);
        PairSegment[] orderedPositive = positiveRoute.Segments;
        PairSegment[] orderedNegative = negativeRoute.Segments;
        var issues = new List<PairValidationIssue>();
        if (orderedPositive.Length == 0)
            issues.Add(new("positive_route_missing", "No straight Track geometry was found for the positive differential-pair net."));
        if (orderedNegative.Length == 0)
            issues.Add(new("negative_route_missing", "No straight Track geometry was found for the negative differential-pair net."));
        if (positiveArcs != 0 || negativeArcs != 0)
            issues.Add(new("arc_not_supported", "Arc geometry is outside the supported straight-track route validation scope."));
        if (positiveVias != 0 || negativeVias != 0)
            issues.Add(new("via_not_supported", "Via geometry is outside the supported straight-track route validation scope."));
        if (unknownItems != 0)
            issues.Add(new("unknown_geometry", $"The native response contained {unknownItems} unsupported or unreadable geometry item(s)."));
        if (orderedPositive.Length != 0 && !positiveRoute.Supported)
            issues.Add(new("single_segment_scope", "The positive straight-track geometry must form one connected, non-branching path on one copper layer."));
        if (orderedNegative.Length != 0 && !negativeRoute.Supported)
            issues.Add(new("single_segment_scope", "The negative straight-track geometry must form one connected, non-branching path on one copper layer."));

        double positivePartialLength = orderedPositive.Sum(segment => segment.LengthNm);
        double negativePartialLength = orderedNegative.Sum(segment => segment.LengthNm);
        double? positiveLength = positiveRoute.Supported && positiveArcs == 0 && positiveVias == 0 && unknownItems == 0
            ? positiveRoute.LengthNm : null;
        double? negativeLength = negativeRoute.Supported && negativeArcs == 0 && negativeVias == 0 && unknownItems == 0
            ? negativeRoute.LengthNm : null;
        double? skew = positiveLength is { } positiveFull && negativeLength is { } negativeFull
            ? Math.Abs(positiveFull - negativeFull) : null;
        double partialSkew = Math.Abs(positivePartialLength - negativePartialLength);
        if (skew is { } fullSkew && request.MaxSkewNm is { } maxSkew && fullSkew > maxSkew + request.ToleranceNm)
            issues.Add(new("skew_exceeded", $"Measured differential-pair skew {Round(fullSkew)} nm exceeds the requested bound {Round(maxSkew)} nm.", null, null));

        bool supported = positiveRoute.Supported && negativeRoute.Supported
            && positiveArcs == 0 && negativeArcs == 0 && positiveVias == 0 && negativeVias == 0 && unknownItems == 0;
        int matched = 0;
        double? centerlineSpacing = null, copperEdgeGap = null, longitudinalOverlap = null, longitudinalEndpointDelta = null;
        double maximumParallelError = 0;
        if (supported && orderedPositive.Length != orderedNegative.Length)
        {
            issues.Add(new("segment_count_mismatch", $"The paired routes contain {orderedPositive.Length} and {orderedNegative.Length} straight Track segments and cannot be matched deterministically."));
            supported = false;
        }
        if (supported)
        {
            PairRoute orientedNegative = OrientPairRoute(positiveRoute, negativeRoute);
            matched = orderedPositive.Length;
            double overlapTotal = 0;
            double endpointDeltaMaximum = 0;
            double spacingMinimum = double.PositiveInfinity;
            double edgeGapMinimum = double.PositiveInfinity;
            for (int index = 0; index < orderedPositive.Length; index++)
            {
                PairSegment left = orderedPositive[index], right = orientedNegative.Segments[index];
                if (!string.Equals(left.Layer, right.Layer, StringComparison.Ordinal))
                    issues.Add(new("layer_mismatch", $"Paired tracks use different copper layers ({left.Layer} and {right.Layer}).", left.Id, right.Id));
                if (Math.Abs((double)left.WidthNm - request.WidthNm) > request.ToleranceNm)
                    issues.Add(new("positive_width_mismatch", $"The positive track width {left.WidthNm} nm differs from requested width {request.WidthNm} nm.", left.Id, null));
                if (Math.Abs((double)right.WidthNm - request.WidthNm) > request.ToleranceNm)
                    issues.Add(new("negative_width_mismatch", $"The negative track width {right.WidthNm} nm differs from requested width {request.WidthNm} nm.", null, right.Id));
                if (Math.Abs((double)left.WidthNm - right.WidthNm) > request.ToleranceNm)
                    issues.Add(new("width_mismatch", $"Paired track widths differ by {Round(Math.Abs((double)left.WidthNm - right.WidthNm))} nm.", left.Id, right.Id));
                if (left.LengthNm <= request.ToleranceNm || right.LengthNm <= request.ToleranceNm)
                    issues.Add(new("zero_length", "Differential-pair geometry contains a zero-length or tolerance-sized track.", left.Id, right.Id));

                double parallelError = ParallelError(left, right);
                maximumParallelError = Math.Max(maximumParallelError, parallelError);
                if (parallelError > request.ParallelTolerance)
                    issues.Add(new("non_parallel", $"Paired tracks are not parallel within tolerance {request.ParallelTolerance:R}.", left.Id, right.Id));

                (double overlap, double endpointDelta) = LongitudinalCorrespondence(left, right);
                overlapTotal += overlap;
                endpointDeltaMaximum = Math.Max(endpointDeltaMaximum, endpointDelta);

                double spacing = SegmentDistance(left.Start, left.End, right.Start, right.End);
                double edgeGap = spacing - ((double)left.WidthNm + right.WidthNm) / 2;
                spacingMinimum = Math.Min(spacingMinimum, spacing);
                edgeGapMinimum = Math.Min(edgeGapMinimum, edgeGap);
                if (edgeGap + request.ToleranceNm < request.MinimumGapNm)
                    issues.Add(new("copper_gap_below_minimum", $"Measured copper edge gap {Round(edgeGap)} nm is below the minimum {request.MinimumGapNm} nm.", left.Id, right.Id));
                if (request.TargetGapNm is { } targetGap && Math.Abs(edgeGap - targetGap) > request.ToleranceNm)
                    issues.Add(new("copper_gap_target_mismatch", $"Measured copper edge gap {Round(edgeGap)} nm differs from target {targetGap} nm.", left.Id, right.Id));
            }
            longitudinalOverlap = overlapTotal;
            longitudinalEndpointDelta = endpointDeltaMaximum;
            centerlineSpacing = spacingMinimum;
            copperEdgeGap = edgeGapMinimum;
            if (overlapTotal <= request.ToleranceNm)
                issues.Add(new("longitudinal_no_overlap", "Paired tracks have no shared longitudinal span and cannot be treated as coupled.", orderedPositive[0].Id, orientedNegative.Segments[0].Id));
            if (endpointDeltaMaximum > request.ToleranceNm)
                issues.Add(new("longitudinal_endpoint_mismatch", $"Paired track endpoints differ longitudinally by {Round(endpointDeltaMaximum)} nm.", orderedPositive[0].Id, orientedNegative.Segments[0].Id));
        }

        PairRouteSummary positiveSummary = new(request.PositiveNet, orderedPositive.Length + positiveVias + positiveArcs,
            orderedPositive.Length, positiveArcs, positiveVias, positiveLength, positivePartialLength,
            orderedPositive.Select(segment => segment.Layer).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        PairRouteSummary negativeSummary = new(request.NegativeNet, orderedNegative.Length + negativeVias + negativeArcs,
            orderedNegative.Length, negativeArcs, negativeVias, negativeLength, negativePartialLength,
            orderedNegative.Select(segment => segment.Layer).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        PairValidationIssue[] violations = issues
            .DistinctBy(issue => (issue.Code, issue.PositiveItemId, issue.NegativeItemId), EqualityComparer<(string Code, string? PositiveItemId, string? NegativeItemId)>.Default)
            .ToArray();
        return new(positiveSummary, negativeSummary, matched, skew, partialSkew, centerlineSpacing,
            copperEdgeGap, longitudinalOverlap, longitudinalEndpointDelta, maximumParallelError,
            orderedPositive.Length != 0 && orderedNegative.Length != 0, supported, violations);
    }

    private static PairSegment? Segment(ProtoAny packed)
    {
        if (packed.Is(BoardTrack.Descriptor))
        {
            BoardTrack item = Unpack<BoardTrack>(packed, "track");
            ValidateIdentity("track", item.Id, item.Net);
            if (item.Start is null || item.End is null || item.Width is null)
                throw new AutomationException("invalid_diff_pair_validation_geometry", "A native Track is missing endpoints or width.");
            if (item.Layer is BoardLayer.BlUnknown or BoardLayer.BlUndefined)
                throw new AutomationException("invalid_diff_pair_validation_geometry", "A native Track has an unknown copper layer.");
            if (item.Width.ValueNm <= 0)
                throw new AutomationException("invalid_diff_pair_validation_geometry", "A native Track has a nonpositive width.");
            return new(item.Id.Value, item.Net.Name, "track", item.Layer.ToString(), item.Start.Clone(), item.End.Clone(), item.Width.ValueNm,
                Distance(item.Start, item.End));
        }
        return null;
    }

    private static PairRoute BuildRoute(IReadOnlyList<PairSegment> source)
    {
        PairSegment[] segments = source.OrderBy(segment => segment.Id, StringComparer.Ordinal).ToArray();
        if (segments.Length == 0)
            return new(Array.Empty<PairSegment>(), Array.Empty<Vector2>(), 0, false, false, false);

        double length = segments.Sum(segment => segment.LengthNm);
        var adjacency = new Dictionary<PointKey, List<int>>();
        for (int index = 0; index < segments.Length; index++)
        {
            PairSegment segment = segments[index];
            if (segment.Start.Equals(segment.End))
                return new(segments, Array.Empty<Vector2>(), length, false, false, false);
            AddEndpoint(adjacency, Point(segment.Start), index);
            AddEndpoint(adjacency, Point(segment.End), index);
        }

        var visited = new bool[segments.Length];
        var pending = new Queue<int>();
        pending.Enqueue(0);
        int visitedCount = 0;
        while (pending.Count != 0)
        {
            int edgeIndex = pending.Dequeue();
            if (visited[edgeIndex])
                continue;
            visited[edgeIndex] = true;
            visitedCount++;
            PairSegment edge = segments[edgeIndex];
            foreach (PointKey endpoint in new[] { Point(edge.Start), Point(edge.End) })
            {
                foreach (int adjacent in adjacency[endpoint])
                {
                    if (!visited[adjacent])
                        pending.Enqueue(adjacent);
                }
            }
        }

        bool connected = visitedCount == segments.Length;
        bool degreesValid = adjacency.Values.All(edges => edges.Count <= 2);
        PointKey[] endpoints = adjacency.Where(entry => entry.Value.Count == 1)
            .Select(entry => entry.Key)
            .OrderBy(point => point.XNm)
            .ThenBy(point => point.YNm)
            .ToArray();
        bool simplePath = connected && degreesValid && endpoints.Length == 2;
        if (!simplePath)
        {
            bool uniform = segments.Select(segment => segment.Layer).Distinct(StringComparer.Ordinal).Count() == 1;
            return new(segments, Array.Empty<Vector2>(), length, connected, false, uniform);
        }

        var ordered = new List<PairSegment>(segments.Length);
        var points = new List<Vector2>(segments.Length + 1) { FromPoint(endpoints[0]) };
        var used = new bool[segments.Length];
        PointKey current = endpoints[0];
        int previousEdge = -1;
        while (ordered.Count < segments.Length)
        {
            int[] candidates = adjacency[current].Where(index => index != previousEdge && !used[index]).ToArray();
            if (candidates.Length != 1)
            {
                bool uniform = segments.Select(segment => segment.Layer).Distinct(StringComparer.Ordinal).Count() == 1;
                return new(segments, Array.Empty<Vector2>(), length, connected, false, uniform);
            }

            int edgeIndex = candidates[0];
            used[edgeIndex] = true;
            PairSegment edge = segments[edgeIndex];
            PairSegment oriented = Point(edge.Start) == current
                ? edge
                : edge with { Start = edge.End.Clone(), End = edge.Start.Clone() };
            ordered.Add(oriented);
            points.Add(oriented.End.Clone());
            previousEdge = edgeIndex;
            current = Point(oriented.End);
        }

        bool allUsed = used.All(value => value);
        bool uniformLayer = ordered.Select(segment => segment.Layer).Distinct(StringComparer.Ordinal).Count() == 1;
        return new(ordered.ToArray(), points.ToArray(), length, connected, allUsed, uniformLayer);
    }

    private static PairRoute OrientPairRoute(PairRoute reference, PairRoute candidate)
    {
        if (candidate.Points.Length < 2 || reference.Points.Length < 2)
            return candidate;
        double direct = Distance(reference.Points[0], candidate.Points[0])
            + Distance(reference.Points[^1], candidate.Points[^1]);
        double reverse = Distance(reference.Points[0], candidate.Points[^1])
            + Distance(reference.Points[^1], candidate.Points[0]);
        return reverse < direct ? ReverseRoute(candidate) : candidate;
    }

    private static PairRoute ReverseRoute(PairRoute route)
    {
        PairSegment[] segments = route.Segments.Reverse()
            .Select(segment => segment with { Start = segment.End.Clone(), End = segment.Start.Clone() })
            .ToArray();
        Vector2[] points = route.Points.Reverse().Select(point => point.Clone()).ToArray();
        return route with { Segments = segments, Points = points };
    }

    private static void AddEndpoint(Dictionary<PointKey, List<int>> adjacency, PointKey point, int edgeIndex)
    {
        if (!adjacency.TryGetValue(point, out List<int>? edges))
        {
            edges = new List<int>();
            adjacency.Add(point, edges);
        }
        edges.Add(edgeIndex);
    }

    private static PointKey Point(Vector2 point) => new(point.XNm, point.YNm);

    private static Vector2 FromPoint(PointKey point) => new() { XNm = point.XNm, YNm = point.YNm };

    private static T Unpack<T>(ProtoAny packed, string kind) where T : class, IMessage<T>, new()
    {
        try
        {
            return packed.Unpack<T>();
        }
        catch (InvalidProtocolBufferException error)
        {
            throw new AutomationException("invalid_diff_pair_validation_geometry", $"The native {kind} payload could not be decoded: {error.Message}");
        }
    }

    private static void ValidateIdentity(string kind, KIID? id, Net? net)
    {
        if (id is null || net is null || string.IsNullOrWhiteSpace(id.Value)
            || !Guid.TryParseExact(id.Value, "D", out var parsed) || parsed == Guid.Empty || string.IsNullOrWhiteSpace(net.Name))
            throw new AutomationException("invalid_diff_pair_validation_geometry", $"A native {kind} is missing a canonical identity or net name.");
    }

    private static void AddIdentity(string id, HashSet<string> selectedIds)
    {
        if (!selectedIds.Add(id))
            throw new AutomationException("invalid_diff_pair_validation_geometry", $"The native differential-pair geometry repeats item identity '{id}'.");
    }

    private static double ParallelError(PairSegment left, PairSegment right)
    {
        double leftX = (double)left.End.XNm - left.Start.XNm, leftY = (double)left.End.YNm - left.Start.YNm;
        double rightX = (double)right.End.XNm - right.Start.XNm, rightY = (double)right.End.YNm - right.Start.YNm;
        double denominator = Math.Sqrt((leftX * leftX + leftY * leftY) * (rightX * rightX + rightY * rightY));
        return denominator <= 0 ? 1 : Math.Abs(leftX * rightY - leftY * rightX) / denominator;
    }

    private static (double Overlap, double EndpointDelta) LongitudinalCorrespondence(PairSegment left, PairSegment right)
    {
        double dx = (double)left.End.XNm - left.Start.XNm, dy = (double)left.End.YNm - left.Start.YNm;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return (0, double.PositiveInfinity);
        double ux = dx / length, uy = dy / length;
        double leftStart = 0, leftEnd = length;
        double rightStart = ((double)right.Start.XNm - left.Start.XNm) * ux + ((double)right.Start.YNm - left.Start.YNm) * uy;
        double rightEnd = ((double)right.End.XNm - left.Start.XNm) * ux + ((double)right.End.YNm - left.Start.YNm) * uy;
        double rightMin = Math.Min(rightStart, rightEnd), rightMax = Math.Max(rightStart, rightEnd);
        double overlap = Math.Max(0, Math.Min(leftEnd, rightMax) - Math.Max(leftStart, rightMin));
        double endpointDelta = Math.Max(Math.Abs(leftStart - rightMin), Math.Abs(leftEnd - rightMax));
        return (overlap, endpointDelta);
    }

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
        ((double)second.XNm - first.XNm) * ((double)point.YNm - first.YNm)
        - ((double)second.YNm - first.YNm) * ((double)point.XNm - first.XNm);

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
