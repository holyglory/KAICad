using Google.Protobuf;
using Kiapi.Schematic.Types;

namespace KiCad.Automation.Native;

/// <summary>Native library copying/saving sorts its child containers. Their enumeration
/// is not a persistent edit. Compare the complete child-message multiset, including
/// exact IDs, every property and duplicate count; never match by number or position.</summary>
internal static class SchematicLibraryCacheEquivalence
{
    internal static bool Equal(SchematicCachedSymbol? left, SchematicCachedSymbol? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left.Equals(right)) return true;
        if (left.Definition is null || right.Definition is null) return false;
        var a = left.Clone(); var b = right.Clone();
        a.Definition.Items.Clear(); b.Definition.Items.Clear();
        return a.Equals(b) && left.Definition.Items.Select(Key).Order(StringComparer.Ordinal)
            .SequenceEqual(right.Definition.Items.Select(Key).Order(StringComparer.Ordinal));

        static string Key(SchematicSymbolChild child) => child.ToByteString().ToBase64();
    }

    internal static bool Choose(SchematicCachedSymbol? baseline, SchematicCachedSymbol? xml,
        SchematicCachedSymbol? native, out SchematicCachedSymbol? result)
    {
        if (Equal(xml, native) || Equal(baseline, xml)) result = native;
        else if (Equal(baseline, native)) result = xml;
        else { result = null; return false; }
        return true;
    }
}
