using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;

namespace KiCad.Automation.Native;

/// <summary>Protobuf JSON for board commands and Any-packed board objects. The
/// registry is explicit so a track/via/pad payload cannot be silently simplified
/// because it was not registered with the parser.</summary>
public static class BoardJson
{
    private static readonly TypeRegistry Types = TypeRegistry.FromFiles(
        CreateItems.Descriptor.File, Track.Descriptor.File, Arc.Descriptor.File, Via.Descriptor.File,
        Footprint.Descriptor.File, Pad.Descriptor.File, ReferenceImage.Descriptor.File,
        BoardGraphicShape.Descriptor.File, BoardText.Descriptor.File);
    public static JsonFormatter Formatter { get; } = new(JsonFormatter.Settings.Default.WithTypeRegistry(Types));
    public static JsonParser Parser { get; } = new(JsonParser.Settings.Default.WithTypeRegistry(Types));
}
