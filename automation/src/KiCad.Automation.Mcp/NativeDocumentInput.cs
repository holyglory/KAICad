using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiapi.Common.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Mcp;

// MCP binds writable collections here and this boundary builds the protobuf
// document.  Binding Google's get-only repeated fields directly loses the
// caller's sheet path and makes oneof validation too easy to bypass.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NativeDocumentInput : NativeDocumentInputObject
{
    private NativeDocumentType type;
    private NativeProjectInput? project;
    private NativeSheetPathInput? sheetPath;
    private NativeLibraryIdentifierInput? libId;
    private string? boardFilename;
    private int? identifierCase;

    [Description("Native document type. Use DOCTYPE_SCHEMATIC for a schematic hierarchy or DOCTYPE_PCB for a board.")]
    public NativeDocumentType Type { get => type; set => type = BindOnce(value); }
    public NativeProjectInput? Project { get => project; set => project = BindOnce(value); }
    public NativeSheetPathInput? SheetPath { get => sheetPath; set => sheetPath = BindOnce(value); }
    public NativeLibraryIdentifierInput? LibId { get => libId; set => libId = BindOnce(value); }
    public string? BoardFilename { get => boardFilename; set => boardFilename = BindOnce(value); }

    [Description("Optional protobuf oneof discriminator. When supplied it must agree with the selected identifier.")]
    public int? IdentifierCase { get => identifierCase; set => identifierCase = BindOnce(value); }

    internal static DocumentSpecifier ToNative(NativeDocumentInput? input)
    {
        if (input?.Project is null || string.IsNullOrWhiteSpace(input.Project.Name)
            || string.IsNullOrWhiteSpace(input.Project.Path) || !Path.IsPathFullyQualified(input.Project.Path))
            throw Invalid("An explicit project name and absolute project directory are required.");
        if (!Enum.IsDefined(input.Type) || input.Type == NativeDocumentType.Unknown)
            throw new AutomationException("unsupported_document_type", "The native document type is not defined.");

        int identifiers = (input.SheetPath is null ? 0 : 1)
            + (input.LibId is null ? 0 : 1) + (string.IsNullOrEmpty(input.BoardFilename) ? 0 : 1);
        if (identifiers != 1)
            throw Invalid("Provide exactly one native document identifier; sheet, board and library identities cannot be combined.");

        var target = new DocumentSpecifier
        {
            Type = (DocumentType)input.Type,
            Project = new() { Name = input.Project.Name, Path = input.Project.Path }
        };

        if (input.SheetPath is { } sheet)
        {
            if (input.Type != NativeDocumentType.Schematic)
                throw Invalid("A sheet-instance path requires a schematic document type.");
            if (sheet.Path is null || sheet.Path.Count == 0
                || sheet.Path.Any(id => id is null || !Guid.TryParseExact(id.Value, "D", out var uuid) || uuid == Guid.Empty))
                throw Invalid("An exact native sheet-instance path with nonempty UUIDs is required.");
            target.SheetPath = new() { PathHumanReadable = sheet.PathHumanReadable ?? "" };
            target.SheetPath.Path.Add(sheet.Path.Select(id => new KIID { Value = id!.Value! }));
        }
        else if (input.LibId is { } library)
        {
            if (input.Type is not (NativeDocumentType.Symbol or NativeDocumentType.Footprint))
                throw Invalid("A library identifier requires a symbol or footprint document type.");
            target.LibId = new() { LibraryNickname = library.LibraryNickname ?? "", EntryName = library.EntryName ?? "" };
            if (string.IsNullOrWhiteSpace(target.LibId.LibraryNickname) || string.IsNullOrWhiteSpace(target.LibId.EntryName))
                throw Invalid("A library nickname and entry name are required.");
        }
        else
        {
            if (input.Type != NativeDocumentType.Pcb || string.IsNullOrWhiteSpace(input.BoardFilename)
                || Path.GetFileName(input.BoardFilename) != input.BoardFilename
                || Path.GetExtension(input.BoardFilename) != ".kicad_pcb")
                throw Invalid("A board document requires a filename ending in .kicad_pcb.");
            target.BoardFilename = input.BoardFilename;
        }

        if (input.IdentifierCase is { } discriminator && discriminator != (int)target.IdentifierCase)
            throw Invalid("The native document discriminator contradicts its identifier.");
        ValidateTarget(target);
        return target;
    }

    private static void ValidateTarget(DocumentSpecifier target)
    {
        switch (target.Type)
        {
            case DocumentType.DoctypeSchematic when target.SheetPath is { Path.Count: > 0 }
                && target.SheetPath.Path.All(id => id.Value is not null
                    && Guid.TryParseExact(id.Value, "D", out var uuid) && uuid != Guid.Empty):
            case DocumentType.DoctypePcb when !string.IsNullOrWhiteSpace(target.BoardFilename)
                && Path.GetFileName(target.BoardFilename) == target.BoardFilename
                && Path.GetExtension(target.BoardFilename) == ".kicad_pcb":
            case DocumentType.DoctypeSymbol or DocumentType.DoctypeFootprint when target.LibId is not null
                && !string.IsNullOrWhiteSpace(target.LibId.LibraryNickname)
                && !string.IsNullOrWhiteSpace(target.LibId.EntryName):
                return;
            default:
                throw Invalid("The document type and oneof identifier do not form a supported native target.");
        }
    }

    private static AutomationException Invalid(string message) => new("invalid_document_target", message);
}

[JsonConverter(typeof(JsonStringEnumConverter<NativeDocumentType>))]
public enum NativeDocumentType
{
    [JsonStringEnumMemberName("DOCTYPE_UNKNOWN")] Unknown = 0,
    [JsonStringEnumMemberName("DOCTYPE_SCHEMATIC")] Schematic = 1,
    [JsonStringEnumMemberName("DOCTYPE_SYMBOL")] Symbol = 2,
    [JsonStringEnumMemberName("DOCTYPE_PCB")] Pcb = 3,
    [JsonStringEnumMemberName("DOCTYPE_FOOTPRINT")] Footprint = 4,
    [JsonStringEnumMemberName("DOCTYPE_DRAWING_SHEET")] DrawingSheet = 5,
    [JsonStringEnumMemberName("DOCTYPE_PROJECT")] Project = 6
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NativeProjectInput : NativeDocumentInputObject
{
    private string? name;
    private string? path;
    public string? Name { get => name; set => name = BindOnce(value); }
    public string? Path { get => path; set => path = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NativeSheetPathInput : NativeDocumentInputObject
{
    private List<NativeUuidInput?>? path;
    private string? pathHumanReadable;
    public List<NativeUuidInput?>? Path { get => path; set => path = BindOnce(value); }
    public string? PathHumanReadable { get => pathHumanReadable; set => pathHumanReadable = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NativeUuidInput : NativeDocumentInputObject
{
    private string? value;
    public string? Value { get => value; set => this.value = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NativeLibraryIdentifierInput : NativeDocumentInputObject
{
    private string? libraryNickname;
    private string? entryName;
    public string? LibraryNickname { get => libraryNickname; set => libraryNickname = BindOnce(value); }
    public string? EntryName { get => entryName; set => entryName = BindOnce(value); }
}

public abstract class NativeDocumentInputObject
{
    private readonly HashSet<string> boundMembers = new(StringComparer.Ordinal);

    protected T BindOnce<T>(T value, [CallerMemberName] string member = "")
    {
        if (!boundMembers.Add(member))
            throw new JsonException("A native document identity member cannot be supplied more than once: " + member);
        return value;
    }
}
