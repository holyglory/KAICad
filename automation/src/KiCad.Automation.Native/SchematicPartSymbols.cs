using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>An explicitly selected, self-contained symbol definition for an electrical part.
/// The library link, cache key and definition identity have distinct owners. This is not a
/// placed symbol, an implicit library lookup, or evidence of native mutation admission.</summary>
public sealed record SchematicPartSymbol(Guid PartId, LibraryIdentifier LibraryId,
    SchematicCachedSymbol Symbol, int BodyStyle = 1);

public static class SchematicPartSymbols
{
    /// <summary>Check declaration identity and electrical coverage without reading a library,
    /// allocating placement identities, changing a definition or modifying a schematic.</summary>
    public static void Validate(SchematicDesign design, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        design.Engineering.Circuit.Validate();
        var parts = design.Engineering.Circuit.Parts.ToDictionary(p => p.Id);
        var declared = new HashSet<Guid>();
        foreach (var source in design.PartSymbols ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (source.PartId == Guid.Empty || !declared.Add(source.PartId)
                || !parts.TryGetValue(source.PartId, out var part))
                throw Invalid("Declare exactly one symbol for each selected, existing electrical part identity.");
            RequireLibraryId(source.LibraryId);
            if (source.Symbol?.Definition is not { } definition || string.IsNullOrWhiteSpace(source.Symbol.CacheKey)
                || source.Symbol.CacheKey.Contains('\0') || source.Symbol.CacheKey.Contains("{slash}", StringComparison.Ordinal)
                || source.Symbol.PinNameOffset is not { } offset || offset.ValueNm % 100 != 0
                || offset.ValueNm < (long)int.MinValue * 100 || offset.ValueNm > (long)int.MaxValue * 100)
                throw Invalid("A declared symbol requires a cache key, a complete definition and native-grid pin-name spacing.");
            RequireLibraryId(definition.Id);
            if (!definition.PinsUseLocalCoordinates || definition.UnitCount != part.Units
                || source.BodyStyle < 1 || source.BodyStyle > definition.BodyStyle.Count)
                throw Invalid("Declare every part unit, local definition coordinates and one existing body style.");
            ValidateFields(definition);
            var ids = new HashSet<Guid>();
            var pins = new List<(string Number, string Name, int Unit)>();
            foreach (var child in definition.Items)
            {
                token.ThrowIfCancellationRequested();
                if (child.Item is null)
                    throw Invalid("Every symbol definition child requires an explicit typed native item.");
                int unit = child.Unit?.Unit ?? 0;
                int style = child.BodyStyle?.Style ?? 0;
                if (unit < 0 || unit > part.Units || style < 0 || style > definition.BodyStyle.Count)
                    throw Invalid("A definition child refers to a nonexistent unit or body style.");
                if (child.Item?.Is(SchematicPin.Descriptor) != true) continue;
                var pin = child.Item.Unpack<SchematicPin>();
                if (pin.Name.Contains(' ') || pin.Number.Contains(' '))
                    throw Invalid("Native pin names and numbers cannot contain spaces; declare their exact KiCad identifiers.");
                if (!Guid.TryParseExact(pin.Id?.Value, "D", out var id) || id == Guid.Empty
                    || pin.Id!.Value != id.ToString("D") || !ids.Add(id)
                    || pin.LibraryPinId is not null || pin.HasActiveAlternate)
                    throw Invalid("Definition pins require distinct owned UUIDs, not placement IDs or selected instance alternates.");
                if (style == 0 || style == source.BodyStyle)
                    pins.Add((pin.Number, pin.Name, unit));
            }
            var expected = part.Pins.Select(p => (p.Number, p.Name, p.Unit)).ToHashSet();
            // Stacked native pins can share a number, but retain distinct UUIDs. The electrical
            // part names one logical pin per number/unit; require exact coverage of that set.
            if (!expected.SetEquals(pins))
                throw Invalid("The selected symbol body must exactly cover the part pin numbers, names and units.");
            // The typed codec rejects future fields and unrepresentable Any payloads instead
            // of silently simplifying a definition, including its embedded assets.
            _ = SchematicDataXml.Write(source.Symbol);
        }
    }

    private static void ValidateFields(SchematicSymbol definition)
    {
        string[] standardNames = ["Reference", "Value", "Footprint", "Datasheet", "Description"];
        SchematicField?[] standard = [definition.ReferenceField, definition.ValueField, definition.FootprintField,
            definition.DatasheetField, definition.DescriptionField];
        for (int i = 0; i < standard.Length; i++)
        {
            if (standard[i] is not { } field || field.Name != standardNames[i] || field.IsPrivate)
                throw Invalid("Standard symbol fields require their canonical names and cannot be private.");
            ValidateField(field);
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in definition.Items.Where(c => c.Item?.Is(SchematicField.Descriptor) == true))
        {
            var field = child.Item.Unpack<SchematicField>();
            if (string.IsNullOrEmpty(field.Name) || !names.Add(field.Name)
                || standardNames.Contains(field.Name, StringComparer.OrdinalIgnoreCase)
                || field.Name is "ki_keywords" or "ki_description" or "ki_fp_filters" or "ki_locked")
                throw Invalid("User-field names must be distinct and must not collide with standard fields or reserved library metadata.");
            if ((child.Unit?.Unit ?? 0) != 0 || (child.BodyStyle?.Style ?? 0) != 0
                || child.IsPrivate != field.IsPrivate)
                throw Invalid("Library user fields belong to the whole symbol and require consistent privacy flags.");
            ValidateField(field);
        }

        static void ValidateField(SchematicField field)
        {
            if (field.Text?.Position is null || field.Text.Attributes?.Multiline != true)
                throw Invalid("Symbol fields require explicit local positions and native multiline text mode.");
            if (field.CustomProperties.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != field.CustomProperties.Count)
                throw Invalid("Field custom-property keys must not collide case-insensitively in the native property map.");
        }
    }

    private static void RequireLibraryId(LibraryIdentifier? id)
    {
        if (id is null || string.IsNullOrWhiteSpace(id.EntryName)
            || id.EntryName.Contains(':') || id.LibraryNickname.Contains(':')
            || id.EntryName.IndexOfAny(['\r', '\n', '\0']) >= 0
            || id.LibraryNickname.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw Invalid("Supply an explicit native library identifier without ambiguous separators.");
        if (!id.Equals(new LibraryIdentifier { LibraryNickname = id.LibraryNickname, EntryName = id.EntryName }))
            throw Invalid("The library identifier contains fields unsupported by this XML version.");
    }

    private static AutomationException Invalid(string message) => new("invalid_part_symbol", message);
}
