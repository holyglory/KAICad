using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
