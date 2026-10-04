using Google.Protobuf;
using Google.Protobuf.Reflection;
using Kiapi.Schematic.Types;
using KiCad.Automation.Protocol;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;

namespace KiCad.Automation.Native;

/// <summary>Typed schematic JSON, including native items carried in protobuf Any.</summary>
public static class SchematicJson
{
    private static readonly TypeRegistry Types = TypeRegistry.FromFiles(
        SchematicText.Descriptor.File, CheckedPcbRouteCommit.Descriptor.File,
        ReadCheckedPcbRouteReceipt.Descriptor.File, CheckedPcbRouteReceipt.Descriptor.File,
        CheckedPcbItemTransaction.Descriptor.File,
        ReadCheckedPcbItemTransactionReceipt.Descriptor.File,
        CheckedPcbItemTransactionReceipt.Descriptor.File,
        CreateItems.Descriptor.File, Track.Descriptor.File, Arc.Descriptor.File, Via.Descriptor.File,
        Footprint.Descriptor.File, Pad.Descriptor.File, BoardGraphicShape.Descriptor.File,
        ReferenceImage.Descriptor.File);
    public static JsonFormatter Formatter { get; } = new(JsonFormatter.Settings.Default.WithTypeRegistry(Types));
    public static JsonParser Parser { get; } = new(JsonParser.Settings.Default.WithTypeRegistry(Types));
}
