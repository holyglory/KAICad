using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Structured request for the deterministic single-layer route planner.  The
/// legacy MCP form accepts the same values as three JSON strings; this DTO
/// keeps the model-visible contract typed while leaving the planner's native
/// protobuf and receipt boundary unchanged.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRoutePlanRequestInput : NativeDocumentInputObject
{
    private string netName = "";
    private string netClassName = "";
    private PcbRoutePointInput? start;
    private PcbRoutePointInput? end;
    private string layer = "";
    private long widthNm;
    private long clearanceNm;
    private long gridNm = 1_000_000;
    private PcbRouteBoundsInput? bounds;
    private int maxNodes = 250_000;
    private int maxObstacleReport = 512;
    private bool includeFootprintEnvelopes = true;
    private bool includeRipUpCandidate;

    [Description("Exact native PCB net name to route.")]
    public string NetName { get => netName; set => netName = BindOnce(value); }

    [Description("Optional effective netclass name or constituent to check before planning.")]
    public string NetClassName { get => netClassName; set => netClassName = BindOnce(value); }

    public PcbRoutePointInput? Start { get => start; set => start = BindOnce(value); }
    public PcbRoutePointInput? End { get => end; set => end = BindOnce(value); }

    [Description("Copper layer name such as F.Cu, In1.Cu or B.Cu.")]
    public string Layer { get => layer; set => layer = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long WidthNm { get => widthNm; set => widthNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long ClearanceNm { get => clearanceNm; set => clearanceNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long GridNm { get => gridNm; set => gridNm = BindOnce(value); }

    public PcbRouteBoundsInput? Bounds { get => bounds; set => bounds = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int MaxNodes { get => maxNodes; set => maxNodes = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int MaxObstacleReport { get => maxObstacleReport; set => maxObstacleReport = BindOnce(value); }

    public bool IncludeFootprintEnvelopes { get => includeFootprintEnvelopes; set => includeFootprintEnvelopes = BindOnce(value); }
    public bool IncludeRipUpCandidate { get => includeRipUpCandidate; set => includeRipUpCandidate = BindOnce(value); }

    internal string ToRequestJson()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRoutePointInput : NativeDocumentInputObject
{
    private long xNm;
    private long yNm;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long XNm { get => xNm; set => xNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long YNm { get => yNm; set => yNm = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRouteBoundsInput : NativeDocumentInputObject
{
    private long minXNm;
    private long minYNm;
    private long maxXNm;
    private long maxYNm;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MinXNm { get => minXNm; set => minXNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MinYNm { get => minYNm; set => minYNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MaxXNm { get => maxXNm; set => maxXNm = BindOnce(value); }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long MaxYNm { get => maxYNm; set => maxYNm = BindOnce(value); }
}

/// <summary>
/// Single structured admission boundary for deterministic headless routing.
/// The legacy planner accepts three independent JSON strings for compatibility;
/// this input binds one PCB document, one complete lifecycle checkpoint, one
/// caller-owned operation identity and one typed route request together.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PcbRoutePlanTypedInput : NativeDocumentInputObject
{
    private NativeDocumentInput? document;
    private DocumentLifecycleInput? expectedState;
    private string? operationId;
    private PcbRoutePlanRequestInput? request;

    public NativeDocumentInput? Document { get => document; set => document = BindOnce(value); }
    public DocumentLifecycleInput? ExpectedState { get => expectedState; set => expectedState = BindOnce(value); }
    public string? OperationId { get => operationId; set => operationId = BindOnce(value); }
    public PcbRoutePlanRequestInput? Request { get => request; set => request = BindOnce(value); }

    internal (string Document, string ExpectedState, string Request, string OperationId) ToPayload()
    {
        if (Document is null || ExpectedState is null || Request is null)
            throw Invalid("document, expectedState, operationId and request are required.");
        if (!Guid.TryParseExact(OperationId, "D", out Guid operation) || operation == Guid.Empty
            || !string.Equals(OperationId, operation.ToString("D"), StringComparison.Ordinal))
            throw Invalid("operationId must be a canonical lowercase UUID.");

        DocumentSpecifier target;
        DocumentLifecycleState state;
        try
        {
            target = NativeDocumentInput.ToNative(Document);
            state = DocumentLifecycleInput.ToNative(ExpectedState);
        }
        catch (AutomationException error)
        {
            throw Invalid(error.Message);
        }

        if (target.Type != DocumentType.DoctypePcb)
            throw Invalid("document must identify a PCB.");
        if (state.Scope != DocumentLifecycleScope.DlsPcb || !state.Document.Equals(target))
            throw Invalid("expectedState must be a complete observation of the same PCB.");
        if (!Guid.TryParseExact(state.ProcessEpoch, "D", out Guid epoch) || epoch == Guid.Empty
            || !string.Equals(state.ProcessEpoch, epoch.ToString("D"), StringComparison.Ordinal))
            throw Invalid("expectedState.processEpoch must be a canonical lowercase UUID.");
        if (state.Revision is null || !Guid.TryParseExact(state.Revision.Epoch, "D", out Guid revisionEpoch)
            || revisionEpoch == Guid.Empty
            || !string.Equals(state.Revision.Epoch, revisionEpoch.ToString("D"), StringComparison.Ordinal))
            throw Invalid("expectedState.revision.epoch must be a canonical lowercase UUID.");

        return (SchematicJson.Formatter.Format(target), SchematicJson.Formatter.Format(state),
            Request.ToRequestJson(), OperationId!);
    }

    private static AutomationException Invalid(string message)
        => new("invalid_route_plan_typed_input", message);
}
