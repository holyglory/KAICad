using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

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
