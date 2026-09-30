using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

internal static class SchematicSheetMappingOperations
{
    internal static IReadOnlyList<SchematicItemOperation> Add(SchematicHierarchyData before, SchematicHierarchyData after,
        SchematicSheetInstanceChoices choices, IReadOnlyList<SchematicItemOperation> operations)
    {
        var changes = SchematicNativeSheetChanges.Compare(before, after, choices);
        if (changes.ErrorCode is not null) throw new AutomationException(changes.ErrorCode, changes.ErrorMessage!);
        static string PathKey(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
        var oldScreens = before.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
        var newScreens = after.Instances.ToDictionary(SchematicNativeSheetChanges.Key, StringComparer.Ordinal);
        var creates = operations.Where(o => o.Create?.Is(SheetSymbol.Descriptor) == true)
            .Select(o => (Operation: o, Sheet: o.Create.Unpack<SheetSymbol>(), Parent: o.TargetDocument ?? after.Document)).ToArray();
        var mapped = new List<SchematicItemOperation>();
        var usedDestinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var removal in operations.Where(o => o.Remove is not null))
        {
            var source = removal.TargetDocument ?? before.Document;
            var owner = oldScreens[PathKey(source)];
            var sheet = owner.Items.Where(i => i.Is(SheetSymbol.Descriptor))
                .Select(i => i.Unpack<SheetSymbol>()).SingleOrDefault(s => s.Id.Equals(removal.Remove));
            if (sheet is null) continue;
            string[] Sources() => oldScreens.Where(p => p.Value.Metadata.ScreenId.Equals(owner.Metadata.ScreenId))
                .Select(p => p.Key + "/" + sheet.Id.Value).Order(StringComparer.Ordinal).ToArray();
            var sources = Sources();
            string[] Destinations(DocumentSpecifier parent, SheetSymbol created)
            {
                var target = newScreens[PathKey(parent)];
                return newScreens.Where(p => p.Value.Metadata.ScreenId.Equals(target.Metadata.ScreenId))
                    .Select(p => p.Key + "/" + created.Id.Value).Order(StringComparer.Ordinal).ToArray();
            }
            var matches = creates.Where(c => c.Sheet.ChildScreenId.Equals(sheet.ChildScreenId)).Where(c =>
            {
                var paths = Destinations(c.Parent, c.Sheet);
                return sources.Any(p => changes.Moved.TryGetValue(p, out string? moved) && paths.Contains(moved, StringComparer.Ordinal))
                    || c.Sheet.Id.Equals(sheet.Id) && sources.All(p => changes.Removed.Contains(p, StringComparer.Ordinal))
                        && paths.All(p => changes.Inserted.Contains(p, StringComparer.Ordinal));
            }).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) throw Invalid("A physical sheet reference needs one explicitly resolved destination.");
            var destination = matches[0]; var destinations = Destinations(destination.Parent, destination.Sheet);
            // Preserve the established operation stream for an ordinary unique move.
            if (sources.Length == 1 && destinations.Length == 1 && sheet.Id.Equals(destination.Sheet.Id)
                && changes.Moved.TryGetValue(sources[0], out string? unique) && unique == destinations[0]) continue;
            string destinationKey = newScreens[PathKey(destination.Parent)].Metadata.ScreenId.Value + "#" + destination.Sheet.Id.Value;
            if (!usedDestinations.Add(destinationKey)) throw Invalid("Several physical references cannot claim the same destination.");
            var declaration = new SchematicSheetInstancePaths { SourceDocument = source.Clone(), SourceSheetId = sheet.Id.Clone(),
                DestinationDocument = destination.Parent.Clone(), DestinationSheetId = destination.Sheet.Id.Clone() };
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in sources)
            {
                if (changes.Moved.TryGetValue(path, out string? target) && destinations.Contains(target, StringComparer.Ordinal))
                {
                    declaration.Moves.Add(new SchematicSheetInstanceMove { Before = NativePath(path), After = NativePath(target) });
                    assigned.Add(target);
                }
                else if (changes.Removed.Contains(path, StringComparer.Ordinal)) declaration.Retired.Add(NativePath(path));
                else throw Invalid("Every instance of the moved physical reference needs an explicit surviving or retired identity.");
            }
            foreach (string path in destinations.Where(p => !assigned.Contains(p)))
                if (changes.Inserted.Contains(path, StringComparer.Ordinal)) declaration.Added.Add(NativePath(path));
                else throw Invalid("Every resulting instance needs an explicit existing or new identity.");
            mapped.Add(new() { SetSheetInstancePaths = declaration });
        }
        return mapped.Count == 0 ? operations : [.. mapped, .. operations];
    }

    private static SheetPath NativePath(string value)
    {
        var path = new SheetPath(); path.Path.Add(value.Split('/').Select(p => new KIID { Value = p })); return path;
    }
    private static AutomationException Invalid(string message) => new(SchematicNativeSheetChanges.MoveAnswerInvalid, message);
}
