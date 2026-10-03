using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

[McpServerToolType]
public sealed class PcbFootprintArtworkTools
{
    [McpServerTool(Name = "kicad_pcb_footprint_svg_plan", ReadOnly = true),
     Description("Prepare a deterministic generated-footprint artwork candidate from strict SVG segments. footprintJson must be a complete FootprintInstance with an identified library definition; SVG geometry is converted to integer nanometres and appended as non-copper footprint graphics with source SHA-256, asset ID and sourceKind provenance. The result is proposal-only: pads, pin numbering, electrical connectivity, courtyard validity, DRC and manufacturing rules remain separate checks; persist through kicad_pcb_footprint_place_apply."),
     KiCadCapability("pcb-placement", "compiled-mcp plus deterministic-svg-parser", "strict SVG footprint graphics, deterministic provenance and complete placement candidate"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public CallToolResult Plan(string documentJson, string expectedStateJson, string footprintJson, string svg,
        string sourceSha256, string sourceKind, string assetId, string layer,
        string originXNm, string originYNm, string nanometersPerSvgUnit,
        CancellationToken cancellationToken, long strokeWidthNm = 100_000)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSpecifier document = Parse<DocumentSpecifier>(documentJson, "invalid_pcb_footprint_artwork_target");
            DocumentStateTools.ValidateTarget(document);
            if (document.Type != DocumentType.DoctypePcb) throw new AutomationException("invalid_pcb_footprint_artwork_target", "The target must be an explicit PCB document.");
            DocumentLifecycleState expected = Parse<DocumentLifecycleState>(expectedStateJson, "invalid_pcb_footprint_artwork_state");
            if (expected.Scope != DocumentLifecycleScope.DlsPcb || !expected.Document.Equals(document) || string.IsNullOrWhiteSpace(expected.ProcessEpoch))
                throw new AutomationException("invalid_pcb_footprint_artwork_state", "expectedStateJson must identify the same PCB and native epoch.");
            FootprintInstance footprint = Parse<FootprintInstance>(footprintJson, "invalid_pcb_footprint_artwork_footprint");
            if (footprint.Id is null || !Guid.TryParseExact(footprint.Id.Value, "D", out var footprintId) || footprintId == Guid.Empty
                || footprint.Definition?.Id is null || string.IsNullOrWhiteSpace(footprint.Definition.Id.LibraryNickname)
                || string.IsNullOrWhiteSpace(footprint.Definition.Id.EntryName))
                throw new AutomationException("invalid_pcb_footprint_artwork_footprint", "footprintJson must identify a canonical instance and library definition.");
            if (string.IsNullOrWhiteSpace(assetId) || !Guid.TryParseExact(assetId, "D", out var asset) || asset == Guid.Empty)
                throw new AutomationException("invalid_pcb_footprint_artwork_id", "assetId must be a canonical UUID.");
            if (sourceKind is not ("imagegen" or "human" or "generated")) throw new AutomationException("invalid_pcb_footprint_artwork_source", "sourceKind must be imagegen, human or generated.");
            if (sourceSha256.Length != 64 || sourceSha256.Any(c => !Uri.IsHexDigit(c))) throw new AutomationException("invalid_pcb_footprint_artwork_source", "sourceSha256 must be an exact SHA-256 digest.");
            string sourceSvg = svg ?? "";
            string actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceSvg)));
            if (!string.Equals(actual, sourceSha256, StringComparison.OrdinalIgnoreCase)) throw new AutomationException("pcb_footprint_artwork_source_changed", "SVG bytes do not match sourceSha256.");
            BoardLayer boardLayer = ParseLayer(layer); ValidateLayer(boardLayer);
            decimal ox = Decimal(originXNm, "originXNm"), oy = Decimal(originYNm, "originYNm"), scale = Decimal(nanometersPerSvgUnit, "nanometersPerSvgUnit");
            if (scale <= 0 || strokeWidthNm <= 0) throw new AutomationException("invalid_pcb_footprint_artwork", "Scale and stroke width must be positive.");
            PcbSvgGuide parsed = PcbSvgGuideParser.Parse(sourceSvg, 20_000);
            FootprintInstance resultFootprint = footprint.Clone();
            string planId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(footprintJson + "\n" + actual + "\n" + assetId + "\n" + layer)));
            for (int index = 0; index < parsed.Segments.Length; index++)
            {
                var segment = parsed.Segments[index];
                var shape = new BoardGraphicShape
                {
                    Id = new() { Value = DeterministicId(planId, index) }, Layer = boardLayer,
                    Shape = new GraphicShape { Attributes = new() { Stroke = new() { Width = new() { ValueNm = strokeWidthNm } } }, Segment = new()
                    { Start = Point(segment.StartX, segment.StartY, ox, oy, scale), End = Point(segment.EndX, segment.EndY, ox, oy, scale) } }
                };
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.footprint_artwork.id", Value = assetId });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.footprint_artwork.plan", Value = planId });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.footprint_artwork.source_sha256", Value = actual });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.footprint_artwork.source_kind", Value = sourceKind });
                shape.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.footprint_artwork.role", Value = "authoritative-vector" });
                resultFootprint.Definition.Items.Add(Any.Pack(shape));
            }
            CreateItems candidate = new() { Header = new() { Document = document } }; candidate.Items.Add(Any.Pack(resultFootprint));
            return Data(new
            {
                planId, document, processEpoch = expected.ProcessEpoch, footprintId = footprint.Id.Value,
                libraryNickname = footprint.Definition.Id.LibraryNickname, entryName = footprint.Definition.Id.EntryName,
                sourceSha256 = actual, sourceKind, assetId, layer = boardLayer.ToString(), segmentCount = parsed.Segments.Length,
                footprintJson = BoardJson.Formatter.Format(resultFootprint), candidateRequestJson = BoardJson.Formatter.Format(candidate),
                authoritativeVector = true, proposalOnly = true, nativeCommit = false,
                padsValidated = false, electricalValidation = false, drcValidated = false, manufacturingValidation = false
            });
        }
        catch (Exception error) when (error is AutomationException or InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException or OverflowException)
        { return Data(new { errorCode = error is AutomationException known ? known.Code : "invalid_pcb_footprint_artwork", errorMessage = error.Message, proposalOnly = true, nativeCommit = false }, true); }
    }

    private static BoardLayer ParseLayer(string value) => value switch
    {
        "F.SilkS" => BoardLayer.BlFSilkS, "B.SilkS" => BoardLayer.BlBSilkS,
        "Dwgs.User" => BoardLayer.BlDwgsUser, "Cmts.User" => BoardLayer.BlCmtsUser,
        "Eco1.User" => BoardLayer.BlEco1User, "Eco2.User" => BoardLayer.BlEco2User,
        _ when System.Enum.TryParse<BoardLayer>(value, true, out var layer) => layer,
        _ => throw new AutomationException("invalid_pcb_footprint_artwork_layer", "Use a concrete non-copper layer.")
    };
    private static void ValidateLayer(BoardLayer layer) { if (layer is BoardLayer.BlUnknown or BoardLayer.BlUndefined || (int)layer is >= 3 and <= 34) throw new AutomationException("invalid_pcb_footprint_artwork_layer", "Generated footprint artwork must use a non-copper layer."); }
    private static Vector2 Point(decimal x, decimal y, decimal ox, decimal oy, decimal scale) { decimal px = ox + x * scale, py = oy + y * scale; if (decimal.Truncate(px) != px || decimal.Truncate(py) != py || px < long.MinValue || px > long.MaxValue || py < long.MinValue || py > long.MaxValue) throw new AutomationException("invalid_pcb_footprint_artwork_coordinates", "SVG coordinates must convert exactly to integer nanometres."); return new() { XNm = (long)px, YNm = (long)py }; }
    private static decimal Decimal(string value, string name) => decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new AutomationException("invalid_pcb_footprint_artwork", $"{name} must be a finite decimal.");
    private static string DeterministicId(string plan, int index) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(plan + ":" + index)).AsSpan(0, 16)).ToString("D");
    private static T Parse<T>(string json, string code) where T : class, Google.Protobuf.IMessage<T>, new() { try { return SchematicJson.Parser.Parse<T>(json); } catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException) { throw new AutomationException(code, error.Message); } }
    private static CallToolResult Data(object value, bool error = false) { JsonElement data = JsonSerializer.SerializeToElement(value); return new() { IsError = error, StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] }; }
}
