using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using KiCad.Automation.Model;
using Kiapi.Common.Types;

namespace KiCad.Automation.Mcp;

// MCP binds ordinary writable collections, then this boundary builds the protobuf
// message. Binding Google's get-only RepeatedField directly loses the sheet path.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulationDocumentInput : SimulationDocumentInputObject
{
    private SimulationDocumentType type;
    private SimulationProjectInput? project;
    private SimulationSheetPathInput? sheetPath;
    private SimulationLibraryIdentifierInput? libId;
    private string? boardFilename;
    private int? identifierCase;

    [Description("Native document type. Use DOCTYPE_SCHEMATIC; numeric 1 remains accepted for existing clients.")]
    public SimulationDocumentType Type { get => type; set => type = BindOnce(value); }
    public SimulationProjectInput? Project { get => project; set => project = BindOnce(value); }
    public SimulationSheetPathInput? SheetPath { get => sheetPath; set => sheetPath = BindOnce(value); }
    public SimulationLibraryIdentifierInput? LibId { get => libId; set => libId = BindOnce(value); }
    public string? BoardFilename { get => boardFilename; set => boardFilename = BindOnce(value); }

    [Description("Optional discriminator emitted by native .NET document serialization; it must agree with the sheet identifier.")]
    public int? IdentifierCase { get => identifierCase; set => identifierCase = BindOnce(value); }

    internal static DocumentSpecifier ToNative(SimulationDocumentInput? input)
    {
        if (input?.Project is null || string.IsNullOrWhiteSpace(input.Project.Name)
            || string.IsNullOrWhiteSpace(input.Project.Path)
            || !Path.IsPathFullyQualified(input.Project.Path))
            throw Invalid("An explicit project name and absolute project directory are required.");

        if (!Enum.IsDefined(input.Type))
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
            if (input.Type != SimulationDocumentType.Schematic)
                throw Invalid("A sheet-instance path requires a schematic document type.");
            if (sheet.Path is null || sheet.Path.Count == 0
                || sheet.Path.Any(id => id is null || !Guid.TryParseExact(id.Value, "D", out var uuid) || uuid == Guid.Empty))
                throw Invalid("An exact native sheet-instance path with nonempty UUIDs is required.");

            target.SheetPath = new() { PathHumanReadable = sheet.PathHumanReadable ?? "" };
            target.SheetPath.Path.Add(sheet.Path.Select(id => new KIID { Value = id!.Value! }));
        }
        else if (input.LibId is { } library)
            target.LibId = new() { LibraryNickname = library.LibraryNickname ?? "", EntryName = library.EntryName ?? "" };
        else
            target.BoardFilename = input.BoardFilename!;

        if (input.IdentifierCase is { } discriminator && discriminator != (int)target.IdentifierCase)
            throw Invalid("The native document discriminator contradicts its identifier.");

        DocumentStateTools.ValidateTarget(target);
        if (target.Type != DocumentType.DoctypeSchematic)
            throw new AutomationException("unsupported_simulation_document", "Simulation requires a schematic document.");
        return target;
    }

    private static AutomationException Invalid(string message) => new("invalid_document_target", message);
}

[JsonConverter(typeof(JsonStringEnumConverter<SimulationDocumentType>))]
public enum SimulationDocumentType
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
public sealed class SimulationProjectInput : SimulationDocumentInputObject
{
    private string? name;
    private string? path;
    public string? Name { get => name; set => name = BindOnce(value); }
    public string? Path { get => path; set => path = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulationSheetPathInput : SimulationDocumentInputObject
{
    private List<SimulationUuidInput?>? path;
    private string? pathHumanReadable;

    [Description("Complete ordered native UUID path, including the root and every child-sheet instance.")]
    public List<SimulationUuidInput?>? Path { get => path; set => path = BindOnce(value); }
    public string? PathHumanReadable { get => pathHumanReadable; set => pathHumanReadable = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulationUuidInput : SimulationDocumentInputObject
{
    private string? value;
    public string? Value { get => value; set => this.value = BindOnce(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimulationLibraryIdentifierInput : SimulationDocumentInputObject
{
    private string? libraryNickname;
    private string? entryName;
    public string? LibraryNickname { get => libraryNickname; set => libraryNickname = BindOnce(value); }
    public string? EntryName { get => entryName; set => entryName = BindOnce(value); }
}

public abstract class SimulationDocumentInputObject
{
    private readonly HashSet<string> boundMembers = new(StringComparer.Ordinal);

    protected T BindOnce<T>(T value, [CallerMemberName] string member = "")
    {
        if (!boundMembers.Add(member))
            throw new JsonException("A native document identity member cannot be supplied more than once: " + member);
        return value;
    }
}
