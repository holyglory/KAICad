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

/// <summary>Revision-bound route length and skew analysis for agents.</summary>
[McpServerToolType]
public sealed class PcbRouteConstraintTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_route_constraints", ReadOnly = true),
     Description("Analyze exact native route lengths and skew for explicitly named PCB nets at one lifecycle checkpoint. requestJson contains nets[], optional targetLengthNm, maxSkewNm, minLengthNm, maxLengthNm and toleranceNm. Returns deterministic measured lengths, violations and a non-mutating tuning deficit; it does not invent meanders, validate DRC/impedance or commit geometry."),
     KiCadCapability("pcb-routing", "compiled-mcp plus native-board-api", "explicit PCB lifecycle state, native Track/Arc/Via geometry, length/skew constraints"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> Analyze(string instanceId, string documentJson, string expectedStateJson,
        string requestJson, CancellationToken cancellationToken) => Execute(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_route_constraint_target");
        DocumentStateTools.ValidateTarget(document);
        if (document.Type != DocumentType.DoctypePcb)
            throw new AutomationException("invalid_route_constraint_target", "Route constraints require an explicit PCB document.");
        DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_route_constraint_state");
        if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document)
            || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
            throw new AutomationException("invalid_route_constraint_state", "The expected observation must identify the same PCB and native process epoch.");
        ConstraintRequest request = JsonSerializer.Deserialize<ConstraintRequest>(requestJson, JsonOptions)
            ?? throw new AutomationException("invalid_route_constraints", "requestJson must be a JSON object.");
        Validate(request);

        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        if (session.Epoch != expected.ProcessEpoch)
            throw new AutomationException("pcb_instance_changed", "The native process epoch changed before route analysis.");
        DocumentLifecycleState before = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!before.Equals(expected))
            throw new AutomationException("pcb_state_changed", "The PCB changed before route analysis; refresh the lifecycle checkpoint.");
        GetItemsResponse response = await client.InvokeAsync<GetItems, GetItemsResponse>(new()
        {
            Header = new() { Document = document },
            Types_ = { KiCadObjectType.KotPcbTrace, KiCadObjectType.KotPcbArc, KiCadObjectType.KotPcbVia }
        }, cancellationToken);
        var lengths = request.Nets.ToDictionary(net => net, _ => 0.0, StringComparer.Ordinal);
        var layers = request.Nets.ToDictionary(net => net, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (Any packed in response.Items)
        {
            string net; double length; string layer;
            if (packed.Is(Track.Descriptor))
            {
                Track item = packed.Unpack<Track>(); net = item.Net.Name; layer = item.Layer.ToString();
                length = Distance(item.Start, item.End);
            }
            else if (packed.Is(Arc.Descriptor))
            {
                Arc item = packed.Unpack<Arc>(); net = item.Net.Name; layer = item.Layer.ToString();
                length = Distance(item.Start, item.Mid) + Distance(item.Mid, item.End);
            }
            else if (packed.Is(Via.Descriptor))
            {
                Via item = packed.Unpack<Via>(); net = item.Net.Name; layer = "via"; length = 0;
            }
            else continue;
            if (!lengths.ContainsKey(net)) continue;
            lengths[net] += length;
            if (layer != "via") layers[net].Add(layer);
        }
        DocumentLifecycleState after = await client.InvokeAsync<ReadDocumentLifecycleState, DocumentLifecycleState>(
            new() { Document = document }, cancellationToken);
        if (!after.Equals(expected))
            throw new AutomationException("route_constraint_changed", "The board changed while route constraints were measured; discard the result.");

        double min = lengths.Values.Min(), max = lengths.Values.Max();
        double skew = max - min;
        var metrics = lengths.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
        {
            netName = pair.Key,
            lengthNm = Math.Round(pair.Value, MidpointRounding.AwayFromZero),
            layers = layers[pair.Key].Order(StringComparer.Ordinal).ToArray(),
            targetDeltaNm = request.TargetLengthNm is { } target
                ? Math.Max(0, target - pair.Value) : 0
        }).ToArray();
        bool targetViolation = request.TargetLengthNm is { } targetLength
            && lengths.Values.Any(value => Math.Abs(value - targetLength) > request.ToleranceNm);
        bool skewViolation = request.MaxSkewNm is { } maxSkew && skew > maxSkew + request.ToleranceNm;
        bool rangeViolation = lengths.Values.Any(value => request.MinLengthNm is { } minLength && value < minLength - request.ToleranceNm
            || request.MaxLengthNm is { } maxLength && value > maxLength + request.ToleranceNm);
        return Result(new
        {
            instanceId, processEpoch = session.Epoch, document,
            beforeState = SchematicJson.Formatter.Format(before), afterState = SchematicJson.Formatter.Format(after),
            metrics, measuredSkewNm = Math.Round(skew, MidpointRounding.AwayFromZero),
            targetViolation, skewViolation, rangeViolation,
            compliant = !targetViolation && !skewViolation && !rangeViolation,
            tuningRequired = targetViolation || skewViolation || rangeViolation,
            autoTune = false, drcValidated = false, nativeCommit = false,
            limitations = new[] { "measurement_only", "no_meander_geometry", "drc_not_validated", "impedance_not_validated" }
        });
    });

    private sealed class ConstraintRequest
    {
        public string[] Nets { get; set; } = [];
        public double? TargetLengthNm { get; set; }
        public double? MaxSkewNm { get; set; }
        public double? MinLengthNm { get; set; }
        public double? MaxLengthNm { get; set; }
        public double ToleranceNm { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true };

    private static void Validate(ConstraintRequest request)
    {
        if (request.Nets is null || request.Nets.Length is < 1 or > 64
            || request.Nets.Any(net => string.IsNullOrWhiteSpace(net) || net.Length > 512)
            || request.Nets.Distinct(StringComparer.Ordinal).Count() != request.Nets.Length)
            throw new AutomationException("invalid_route_constraints", "nets must contain 1..64 unique nonempty net names.");
        if (request.ToleranceNm < 0 || request.TargetLengthNm is < 0 || request.MaxSkewNm is < 0
            || request.MinLengthNm is < 0 || request.MaxLengthNm is < 0
            || request.MinLengthNm is { } min && request.MaxLengthNm is { } max && min > max)
            throw new AutomationException("invalid_route_constraints", "Constraint distances must be nonnegative and minLengthNm cannot exceed maxLengthNm.");
    }

    private static double Distance(Vector2 left, Vector2 right)
    {
        double dx = (double)right.XNm - left.XNm, dy = (double)right.YNm - left.YNm;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static T Parse<T>(string json, string code) where T : class, IMessage<T>, new()
    {
        try { return SchematicJson.Parser.Parse<T>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException)
        { throw new AutomationException(code, "Invalid protobuf JSON: " + error.Message); }
    }

    private static CallToolResult Result(object value)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(value, JsonOptions);
        return new() { StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is AutomationException or NativeApiException or NngException
            or IOException or InvalidProtocolBufferException or InvalidJsonException or JsonException)
        {
            var structured = JsonSerializer.SerializeToElement(new
            { errorCode = error is AutomationException known ? known.Code : "pcb_route_constraints_failed", errorMessage = error.Message });
            return new() { IsError = true, StructuredContent = structured, Content = [new TextContentBlock { Text = structured.GetRawText() }] };
        }
    }
}
