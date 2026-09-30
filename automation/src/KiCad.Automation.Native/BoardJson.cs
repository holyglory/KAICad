using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kiapi.Board.Types;

namespace KiCad.Automation.Native;

/// <summary>Typed board JSON, including board items carried in protobuf Any.</summary>
public static class BoardJson
{
    private static readonly TypeRegistry Types = TypeRegistry.FromFiles(BoardTypesReflection.Descriptor);
    public static JsonFormatter Formatter { get; } = new(JsonFormatter.Settings.Default.WithTypeRegistry(Types));
    public static JsonParser Parser { get; } = new(JsonParser.Settings.Default.WithTypeRegistry(Types));
}
