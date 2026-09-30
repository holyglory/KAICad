using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed record RoutePlanRequest(string DocumentJson, string HeaderJson, string NetName,
    long StartXNm, long StartYNm, long EndXNm, long EndYNm, string Layer, long WidthNm,
    long ClearanceNm, IReadOnlyList<RouteObstacle> Obstacles);

[McpServerToolType]
public sealed class RoutingTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_pcb_route_plan", ReadOnly = true, UseStructuredContent = true),
     Description("Plan a deterministic orthogonal single-layer route from explicit endpoints and obstacle rectangles. The result is reproducible and includes exact segments and cost; it is a proposal until committed and must still be checked by native DRC. Differential pairs, layer changes, tuning and push-and-shove are reported as unsupported until their dedicated planners are enabled.")]
    public CallToolResult Plan(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = JsonSerializer.Deserialize<RoutePlanRequest>(requestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new AutomationException("invalid_route_request", "A route plan request is required.");
            if (!System.Enum.TryParse<BoardLayer>(request.Layer, true, out var layer))
                throw new AutomationException("invalid_layer", "Use an exact BoardLayer enum name for route planning.");
            var route = RoutePlanner.Plan(new(request.StartXNm, request.StartYNm), new(request.EndXNm, request.EndYNm),
                request.WidthNm, request.ClearanceNm, request.Obstacles);
            var result = new
            {
                documentJson = request.DocumentJson, headerJson = request.HeaderJson, request.NetName,
                layer = layer.ToString(), widthNm = request.WidthNm, clearanceNm = request.ClearanceNm,
                strategy = route.Strategy, totalLengthNm = route.TotalLengthNm,
                segments = route.Segments.Select(s => new { start = new { xNm = s.Start.XNm, yNm = s.Start.YNm }, end = new { xNm = s.End.XNm, yNm = s.End.YNm } }),
                supportsDifferentialPair = false, supportsLayerChanges = false, supportsTuning = false,
                nativeDrcRequired = true
            };
            var json = JsonSerializer.SerializeToElement(result);
            return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
        catch (JsonException error) { return Failure("invalid_route_request", error.Message); }
    }

    [McpServerTool(Name = "kicad_pcb_route_commit"),
     Description("Commit a previously returned single-layer route plan as native Track items. The plan must retain the exact board document and item header. The native response is the source of truth; inspect the resulting items and run DRC before claiming routing success.")]
    public Task<CallToolResult> Commit(string instanceId, string routeJson, CancellationToken cancellationToken) =>
        Execute(async () =>
        {
            using var parsed = JsonDocument.Parse(routeJson);
            var root = parsed.RootElement;
            string documentJson = root.GetProperty("documentJson").GetString()!;
            string headerJson = root.GetProperty("headerJson").GetString()!;
            string netName = root.GetProperty("netName").GetString()!;
            var layer = System.Enum.Parse<BoardLayer>(root.GetProperty("layer").GetString()!, true);
            long widthNm = root.GetProperty("widthNm").GetInt64();
            var request = new CreateItems { Header = JsonParser.Default.Parse<ItemHeader>(headerJson) };
            foreach (var segment in root.GetProperty("segments").EnumerateArray())
            {
                var start = segment.GetProperty("start"); var end = segment.GetProperty("end");
                var track = new Track
                {
                    Id = new KIID { Value = Guid.NewGuid().ToString("D") },
                    Start = new Vector2 { XNm = start.GetProperty("xNm").GetInt64(), YNm = start.GetProperty("yNm").GetInt64() },
                    End = new Vector2 { XNm = end.GetProperty("xNm").GetInt64(), YNm = end.GetProperty("yNm").GetInt64() },
                    Width = new Distance { ValueNm = widthNm }, Layer = layer, Net = new Net { Name = netName }
                };
                request.Items.Add(Any.Pack(track));
            }
            var response = await registry.Client(instanceId).InvokeAsync<CreateItems, CreateItemsResponse>(request, cancellationToken);
            return BoardJson.Formatter.Format(response);
        });

    private static CallToolResult Failure(string code, string message)
    {
        var json = JsonSerializer.SerializeToElement(new { code, message });
        return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }

    private static async Task<CallToolResult> Execute(Func<Task<string>> action)
    {
        try { string value = await action(); return new() { Content = [new TextContentBlock { Text = value }] }; }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }
        catch (NativeApiException error) { return Failure("native_status_" + error.Status, error.Message); }
    }
}
