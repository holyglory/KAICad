using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>Managed structural validation for generated or imported footprint candidates.</summary>
[McpServerToolType]
public sealed class PcbFootprintValidationTools
{
    private const int MaximumItems = 16_384;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    internal static IReadOnlyList<string> ValidateCompleteCandidate(FootprintInstance footprint,
        CancellationToken cancellationToken)
    {
        var issues = new List<ValidationIssue>();
        ValidateFootprint(footprint, issues, cancellationToken);
        return issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal)
            .Select(issue => issue.Code + ":" + issue.Path)
            .ToArray();
    }

    [McpServerTool(Name = "kicad_pcb_footprint_validate", ReadOnly = true),
     Description("Validate one complete FootprintInstance protobuf-JSON candidate without contacting KiCad. The check covers explicit instance/library identity, bounded Any-packed child content, unique pad identities and numbers, pad-stack layers and positive pad sizes, graphic identities/layers, 3D model filenames/transforms and supported footprint child types. It returns deterministic issues and a canonical content SHA-256; it does not run board DRC, resolve a library, or claim manufacturing validity."),
     KiCadCapability("pcb-placement", "compiled-mcp", "managed footprint identity, pads, padstacks, graphics, 3D model transforms and canonical content SHA-256"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public CallToolResult Validate(string footprintJson, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(footprintJson))
                throw new AutomationException("invalid_pcb_footprint", "footprintJson is required.");
            if (Encoding.UTF8.GetByteCount(footprintJson) > MaximumPayloadBytes)
                throw new AutomationException("pcb_footprint_too_large", $"footprintJson exceeds the {MaximumPayloadBytes} byte limit.");
            FootprintInstance footprint = Parse(footprintJson);
            string canonical = BoardJson.Formatter.Format(footprint);
            var issues = new List<ValidationIssue>();
            ValidateFootprint(footprint, issues, cancellationToken);
            ValidationIssue[] ordered = issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Message, StringComparer.Ordinal).ToArray();
            return Data(new
            {
                valid = ordered.Length == 0,
                issues = ordered,
                contentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
                canonicalBytes = Encoding.UTF8.GetByteCount(canonical),
                itemCount = footprint.Definition?.Items.Count ?? 0,
                padCount = footprint.Definition?.Items.Count(item => item.Is(Pad.Descriptor)) ?? 0,
                graphicCount = footprint.Definition?.Items.Count(item => item.Is(BoardGraphicShape.Descriptor)) ?? 0,
                nativeContacted = false, nativeMutation = false
            });
        }
        catch (Exception error) when (error is AutomationException or InvalidProtocolBufferException or InvalidJsonException)
        {
            string code = error is AutomationException known ? known.Code : "invalid_pcb_footprint";
            return Data(new { errorCode = code, errorMessage = error.Message, valid = false,
                issues = Array.Empty<ValidationIssue>(), nativeContacted = false, nativeMutation = false }, true);
        }
    }

    [McpServerTool(Name = "kicad_schematic_footprint_assignment_validate", ReadOnly = true),
     Description("Validate one typed schematic-to-PCB footprint assignment without contacting KiCad. symbolJson is a complete SchematicSymbolInstance carrying its Footprint field; footprintJson is a complete FootprintInstance carrying its library identity and optional symbol path. The check compares the exact library:name assignment, verifies shared sheet-path identity when both sides declare it, and includes the footprint structural issues from kicad_pcb_footprint_validate. It does not resolve library aliases, infer assignments, compare electrical pin maps, run board DRC, mutate either document or prove persistence."),
     KiCadCapability("pcb-placement", "compiled-mcp", "typed schematic Footprint field, PCB footprint library identity and shared sheet-path assignment validation"),
     KiCadVerification(KiCadVerificationLevel.McpProcess, "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public CallToolResult ValidateAssignment(string symbolJson, string footprintJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(symbolJson) || string.IsNullOrWhiteSpace(footprintJson))
                throw new AutomationException("invalid_footprint_assignment", "symbolJson and footprintJson are required.");
            if (Encoding.UTF8.GetByteCount(symbolJson) > MaximumPayloadBytes
                || Encoding.UTF8.GetByteCount(footprintJson) > MaximumPayloadBytes)
                throw new AutomationException("footprint_assignment_too_large", $"symbolJson and footprintJson must each stay within {MaximumPayloadBytes} bytes.");

            SchematicSymbolInstance symbol = ParseSymbol(symbolJson);
            FootprintInstance footprint = Parse(footprintJson);
            var issues = new List<ValidationIssue>();
            if (!Canonical(symbol.Id?.Value))
                Issue(issues, "missing_symbol_instance_id", "symbol.id", "The schematic symbol instance needs a canonical UUID.");

            string assignment = symbol.FootprintField?.Text?.Text_?.Trim() ?? "";
            LibraryIdentifier? footprintId = footprint.Definition?.Id;
            if (string.IsNullOrWhiteSpace(assignment))
                Issue(issues, "missing_footprint_assignment", "symbol.footprintField", "The schematic symbol has no explicit library:name footprint assignment.");
            if (footprintId is null || string.IsNullOrWhiteSpace(footprintId.LibraryNickname)
                || string.IsNullOrWhiteSpace(footprintId.EntryName)
                || footprintId.LibraryNickname.Contains('\0') || footprintId.EntryName.Contains('\0'))
                Issue(issues, "missing_footprint_identity", "footprint.definition.id", "The PCB footprint needs an explicit library nickname and entry name.");
            else if (!string.Equals(assignment,
                         footprintId.LibraryNickname + ":" + footprintId.EntryName, StringComparison.Ordinal))
                Issue(issues, "footprint_assignment_mismatch", "symbol.footprintField",
                    $"The schematic assignment '{assignment}' does not equal the PCB footprint identity '{footprintId.LibraryNickname}:{footprintId.EntryName}'.");

            if (HasPath(symbol.Path) && HasPath(footprint.SymbolPath)
                && !symbol.Path.Equals(footprint.SymbolPath))
                Issue(issues, "footprint_assignment_path_mismatch", "footprint.symbolPath",
                    "The schematic symbol and PCB footprint declare different hierarchical sheet paths.");

            ValidateFootprint(footprint, issues, cancellationToken);
            ValidationIssue[] ordered = issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Message, StringComparer.Ordinal).ToArray();
            string? footprintIdentity = footprintId is null ? null : footprintId.LibraryNickname + ":" + footprintId.EntryName;
            return Data(new
            {
                valid = ordered.Length == 0,
                issues = ordered,
                symbolInstanceId = symbol.Id?.Value,
                assignment,
                footprintIdentity,
                assignmentMatched = ordered.All(issue => issue.Code is not "missing_footprint_assignment"
                    and not "missing_footprint_identity" and not "footprint_assignment_mismatch"
                    and not "footprint_assignment_path_mismatch"),
                footprintContentSha256 = Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes(BoardJson.Formatter.Format(footprint)))),
                nativeContacted = false,
                nativeMutation = false,
                pinMappingChecked = false,
                persistenceVerified = false
            });
        }
        catch (Exception error) when (error is AutomationException or InvalidProtocolBufferException or InvalidJsonException)
        {
            string code = error is AutomationException known ? known.Code : "invalid_footprint_assignment";
            return Data(new { errorCode = code, errorMessage = error.Message, valid = false,
                assignmentMatched = false, nativeContacted = false, nativeMutation = false,
                pinMappingChecked = false, persistenceVerified = false }, true);
        }
    }

    private static void ValidateFootprint(FootprintInstance footprint, List<ValidationIssue> issues, CancellationToken token)
    {
        if (!Canonical(footprint.Id?.Value)) Issue(issues, "missing_instance_id", "id", "The footprint instance needs a canonical UUID.");
        Footprint? definition = footprint.Definition;
        if (definition?.Id is null || string.IsNullOrWhiteSpace(definition.Id.LibraryNickname)
            || string.IsNullOrWhiteSpace(definition.Id.EntryName) || definition.Id.LibraryNickname.Contains('\0') || definition.Id.EntryName.Contains('\0'))
            Issue(issues, "missing_library_id", "definition.id", "The footprint definition needs a library nickname and entry name.");
        if (definition is null) return;
        if (definition.Items.Count > MaximumItems) Issue(issues, "too_many_items", "definition.items", $"A footprint may contain at most {MaximumItems} child items.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var padNumbers = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < definition.Items.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            Any item = definition.Items[index];
            string path = $"definition.items[{index}]";
            if (item is null || string.IsNullOrWhiteSpace(item.TypeUrl) || item.Value.Length == 0)
            {
                Issue(issues, "missing_child_type", path, "Every footprint child needs a typed, nonempty protobuf Any value.");
                continue;
            }
            if (!Supported(item))
            {
                Issue(issues, "unsupported_child_type", path, "The candidate contains an unsupported footprint child type.");
                continue;
            }
            string? id = ChildId(item);
            if (id is not null && (!Canonical(id) || !ids.Add(id)))
                Issue(issues, "invalid_child_id", path + ".id", "Footprint child identities must be distinct canonical UUIDs.");
            if (item.Is(Pad.Descriptor)) ValidatePad(item.Unpack<Pad>(), path, padNumbers, issues);
            else if (item.Is(Footprint3DModel.Descriptor)) ValidateModel(item.Unpack<Footprint3DModel>(), path, issues);
            else if (item.Is(BoardGraphicShape.Descriptor))
            {
                BoardGraphicShape shape = item.Unpack<BoardGraphicShape>();
                if (shape.Layer is BoardLayer.BlUnknown or BoardLayer.BlUndefined)
                    Issue(issues, "invalid_graphic_layer", path + ".layer", "Footprint graphics need an explicit board layer.");
                if (shape.Shape is null || shape.Shape.GeometryCase == GraphicShape.GeometryOneofCase.None)
                    Issue(issues, "missing_graphic_geometry", path + ".shape", "Footprint graphics need explicit vector geometry.");
            }
        }
    }

    private static void ValidateModel(Footprint3DModel model, string path, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(model.Filename) || model.Filename.Length > 4096 || model.Filename.Contains('\0'))
            Issue(issues, "invalid_3d_model_filename", path + ".filename", "A 3D model needs a bounded, NUL-free filename.");
        if (!Finite(model.Scale.XNm) || !Finite(model.Scale.YNm) || !Finite(model.Scale.ZNm)
            || model.Scale.XNm == 0 || model.Scale.YNm == 0 || model.Scale.ZNm == 0)
            Issue(issues, "invalid_3d_model_scale", path + ".scale", "3D model scale values must be finite and nonzero.");
        if (!Finite(model.Rotation.XNm) || !Finite(model.Rotation.YNm) || !Finite(model.Rotation.ZNm)
            || !Finite(model.Offset.XNm) || !Finite(model.Offset.YNm) || !Finite(model.Offset.ZNm))
            Issue(issues, "invalid_3d_model_transform", path, "3D model rotation and offset values must be finite.");
        if (!double.IsFinite(model.Opacity) || model.Opacity < 0 || model.Opacity > 1)
            Issue(issues, "invalid_3d_model_opacity", path + ".opacity", "3D model opacity must be finite and between 0 and 1.");
    }

    private static void ValidatePad(Pad pad, string path, HashSet<string> numbers, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(pad.Number) || pad.Number.Length > 128 || !numbers.Add(pad.Number))
            Issue(issues, "invalid_pad_number", path + ".number", "Pad numbers must be nonempty, bounded and unique within a footprint.");
        if (pad.PadStack is null || pad.PadStack.Layers.Count < 1 || pad.PadStack.CopperLayers.Count < 1)
        {
            Issue(issues, "invalid_padstack", path + ".padStack", "Each pad needs at least one pad-stack layer and copper shape.");
            return;
        }
        for (int index = 0; index < pad.PadStack.CopperLayers.Count; index++)
        {
            PadStackLayer layer = pad.PadStack.CopperLayers[index];
            if (layer.Shape == PadStackShape.PssUnknown || layer.Size is null || layer.Size.XNm <= 0 || layer.Size.YNm <= 0)
                Issue(issues, "invalid_pad_shape", $"{path}.padStack.copperLayers[{index}]", "Pad copper shapes need a known shape and positive X/Y dimensions.");
        }
    }

    private static bool Supported(Any item) => item.Is(Pad.Descriptor) || item.Is(BoardGraphicShape.Descriptor)
        || item.Is(BoardText.Descriptor) || item.Is(BoardTextBox.Descriptor) || item.Is(Barcode.Descriptor)
        || item.Is(Dimension.Descriptor) || item.Is(Kiapi.Board.Types.Group.Descriptor) || item.Is(ReferenceImage.Descriptor)
        || item.Is(Table.Descriptor) || item.Is(Constraint.Descriptor) || item.Is(Footprint3DModel.Descriptor);

    private static string? ChildId(Any item) => item.Is(Pad.Descriptor) ? item.Unpack<Pad>().Id?.Value
        : item.Is(BoardGraphicShape.Descriptor) ? item.Unpack<BoardGraphicShape>().Id?.Value
        : item.Is(BoardText.Descriptor) ? item.Unpack<BoardText>().Id?.Value
        : item.Is(BoardTextBox.Descriptor) ? item.Unpack<BoardTextBox>().Id?.Value
        : item.Is(Barcode.Descriptor) ? item.Unpack<Barcode>().Id?.Value
        : item.Is(Dimension.Descriptor) ? item.Unpack<Dimension>().Id?.Value
        : item.Is(Kiapi.Board.Types.Group.Descriptor) ? item.Unpack<Kiapi.Board.Types.Group>().Id?.Value
        : item.Is(ReferenceImage.Descriptor) ? item.Unpack<ReferenceImage>().Id?.Value
        : item.Is(Table.Descriptor) ? item.Unpack<Table>().Id?.Value
        : item.Is(Constraint.Descriptor) ? item.Unpack<Constraint>().Id?.Value : null;

    private static bool Finite(double value) => double.IsFinite(value);
    private static bool HasPath(SheetPath? path) => path is not null && path.Path.Count > 0;
    private static bool Canonical(string? value) => value is not null && Guid.TryParseExact(value, "D", out Guid id)
        && id != Guid.Empty && value == id.ToString("D");
    private static void Issue(List<ValidationIssue> issues, string code, string path, string message) => issues.Add(new(code, path, message));
    private static FootprintInstance Parse(string json)
    {
        try { return BoardJson.Parser.Parse<FootprintInstance>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_pcb_footprint", "footprintJson is not valid FootprintInstance protobuf JSON: " + error.Message); }
    }
    private static SchematicSymbolInstance ParseSymbol(string json)
    {
        try { return SchematicJson.Parser.Parse<SchematicSymbolInstance>(json); }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidJsonException or InvalidOperationException)
        { throw new AutomationException("invalid_footprint_assignment", "symbolJson is not valid SchematicSymbolInstance protobuf JSON: " + error.Message); }
    }
    private static CallToolResult Data(object value, bool error = false)
    {
        JsonElement data = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { IsError = error, StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }
    private sealed record ValidationIssue(string Code, string Path, string Message);
}
