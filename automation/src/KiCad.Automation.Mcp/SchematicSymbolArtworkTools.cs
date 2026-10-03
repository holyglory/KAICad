using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Strict SVG-to-symbol vector proposals with provenance.</summary>
[McpServerToolType]
public sealed class SchematicSymbolArtworkTools
{
    [McpServerTool(Name = "kicad_symbol_svg_plan", ReadOnly = true),
     Description("Convert a strict SVG asset into a deterministic SchematicSymbol graphic proposal. The result contains complete library identity, integer-nanometre vector geometry, source SHA-256, asset/source-kind provenance and a machine-readable definition JSON. It is proposal-only: pins, electrical types, fields, library validation and native persistence remain separate checks.")]
    [KiCadCapability("symbol-library", "compiled-mcp plus deterministic-svg-parser", "strict SVG symbol graphics, provenance and deterministic SchematicSymbol proposal")]
    public CallToolResult Plan(string libraryNickname, string symbolName, string svg,
        string sourceSha256, string sourceKind, string assetId, string originXNm = "0",
        string originYNm = "0", string nanometersPerSvgUnit = "1", long strokeWidthNm = 100_000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(libraryNickname) || string.IsNullOrWhiteSpace(symbolName)
                || libraryNickname.Contains('\0') || symbolName.Contains('\0'))
                throw new AutomationException("invalid_symbol_artwork_identity", "libraryNickname and symbolName are required and NUL-free.");
            if (!Guid.TryParseExact(assetId, "D", out var asset) || asset == Guid.Empty)
                throw new AutomationException("invalid_symbol_artwork_id", "assetId must be a canonical UUID.");
            if (sourceKind is not ("imagegen" or "human" or "generated"))
                throw new AutomationException("invalid_symbol_artwork_source", "sourceKind must be imagegen, human or generated.");
            string sourceSvg = svg ?? "";
            byte[] sourceBytes = Encoding.UTF8.GetBytes(sourceSvg);
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
            if (sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit)
                || !string.Equals(actualHash, sourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new AutomationException("symbol_artwork_source_changed", "SVG bytes do not match sourceSha256.");
            if (strokeWidthNm <= 0) throw new AutomationException("invalid_symbol_artwork", "strokeWidthNm must be positive.");
            decimal ox = Decimal(originXNm, "originXNm"), oy = Decimal(originYNm, "originYNm"), scale = Decimal(nanometersPerSvgUnit, "nanometersPerSvgUnit");
            if (scale <= 0) throw new AutomationException("invalid_symbol_artwork", "nanometersPerSvgUnit must be positive.");
            PcbSvgGuide parsed = PcbSvgGuideParser.Parse(sourceSvg, 20_000);
            string planId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                libraryNickname + "\n" + symbolName + "\n" + actualHash + "\n" + assetId)));
            SchematicSymbol definition = new()
            {
                Id = new() { LibraryNickname = libraryNickname, EntryName = symbolName },
                Type = SchematicSymbolType.SstNormal, UnitCount = 1, PinsUseLocalCoordinates = true
            };
            definition.BodyStyle.Add(new SchematicBodyStyle { Name = "Standard" });
            for (int index = 0; index < parsed.Segments.Length; index++)
            {
                var segment = parsed.Segments[index];
                var graphic = new SchematicGraphicShape
                {
                    Id = new() { Value = DeterministicId(planId, index) },
                    Shape = new GraphicShape
                    {
                        Attributes = new() { Stroke = new() { Width = new() { ValueNm = strokeWidthNm } } },
                        Bezier = StraightBezier(segment, ox, oy, scale)
                    }
                };
                graphic.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.id", Value = assetId });
                graphic.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.plan", Value = planId });
                graphic.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.source_sha256", Value = actualHash });
                graphic.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.source_kind", Value = sourceKind });
                graphic.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.role", Value = "authoritative-vector" });
                definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(graphic) });
            }
            // Native .kicad_sym shape writers currently preserve the vector geometry but do
            // not serialize SCH_SHAPE custom properties. Keep the per-shape properties for
            // the in-memory proposal and carry the same provenance in a private user field,
            // whose native writer/parser already round-trip custom properties.
            var manifest = JsonSerializer.Serialize(new
            {
                planId,
                assetId,
                sourceKind,
                sourceSha256 = actualHash,
                role = "authoritative-vector",
                segmentCount = parsed.Segments.Length,
                graphicIdScheme = "sha256(planId + ':' + zeroBasedSegmentIndex)[0..16] as UUID"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var metadataField = new SchematicField
            {
                Name = "kicad.ai.symbol_artwork_manifest",
                Text = new Text
                {
                    Text_ = manifest,
                    Attributes = new TextAttributes
                    {
                        Multiline = true,
                        Size = new Vector2 { XNm = 1_000_000, YNm = 1_000_000 }
                    }
                },
                Visible = false,
                ShowName = false,
                AllowAutoPlace = false,
                IsPrivate = true
            };
            metadataField.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.source_sha256", Value = actualHash });
            metadataField.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.plan", Value = planId });
            metadataField.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.asset_id", Value = assetId });
            metadataField.CustomProperties.Add(new CustomProperty { Key = "kicad.ai.symbol_artwork.source_kind", Value = sourceKind });
            definition.Items.Add(new SchematicSymbolChild { Item = Any.Pack(metadataField), IsPrivate = true });
            JsonElement data = JsonSerializer.SerializeToElement(new
            {
                planId, libraryNickname, symbolName, assetId, sourceKind, sourceSha256 = actualHash,
                segmentCount = parsed.Segments.Length, definitionJson = SchematicJson.Formatter.Format(definition),
                authoritativeVector = true, provenanceCarrier = "native-persisted-private-field", proposalOnly = true, nativeMutation = false,
                pinsValidated = false, electricalValidation = false, persistenceVerified = false
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new CallToolResult { StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] };
        }
        catch (Exception error) when (error is AutomationException or InvalidOperationException or InvalidDataException or FormatException or OverflowException)
        {
            JsonElement data = JsonSerializer.SerializeToElement(new
            { errorCode = error is AutomationException known ? known.Code : "invalid_symbol_artwork", errorMessage = error.Message, proposalOnly = true, nativeMutation = false });
            return new CallToolResult { IsError = true, StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] };
        }
    }

    private static Vector2 Point(decimal x, decimal y, decimal ox, decimal oy, decimal scale)
    {
        decimal px = ox + x * scale, py = oy + y * scale;
        if (decimal.Truncate(px) != px || decimal.Truncate(py) != py || px < long.MinValue || px > long.MaxValue || py < long.MinValue || py > long.MaxValue)
            throw new AutomationException("invalid_symbol_artwork_coordinates", "SVG coordinates must convert exactly to integer nanometres.");
        return new Vector2 { XNm = (long)px, YNm = (long)py };
    }

    private static GraphicBezierAttributes StraightBezier(PcbSvgGuideSegment segment, decimal ox, decimal oy, decimal scale)
    {
        Vector2 start = Point(segment.StartX, segment.StartY, ox, oy, scale);
        Vector2 end = Point(segment.EndX, segment.EndY, ox, oy, scale);
        return new GraphicBezierAttributes
        {
            Start = start,
            Control1 = new Vector2 { XNm = start.XNm + (end.XNm - start.XNm) / 3, YNm = start.YNm + (end.YNm - start.YNm) / 3 },
            Control2 = new Vector2 { XNm = start.XNm + (end.XNm - start.XNm) * 2 / 3, YNm = start.YNm + (end.YNm - start.YNm) * 2 / 3 },
            End = end
        };
    }
    private static decimal Decimal(string value, string name) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
        ? parsed : throw new AutomationException("invalid_symbol_artwork", $"{name} must be a finite decimal.");
    private static string DeterministicId(string plan, int index) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(plan + ":" + index)).AsSpan(0, 16)).ToString("D");
}
