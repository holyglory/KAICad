using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Plans one batch across loaded sheets and explicitly supported new
/// screen contents. Does not execute, admit revisions or establish complete synchronization.</summary>
public static class SchematicHierarchyDelta
{
    public static IReadOnlyList<SchematicItemOperation> Plan(SchematicHierarchyData current,
        SchematicHierarchyData desired, CancellationToken cancellationToken = default)
    {
        foreach (var data in new[] { current, desired })
        {
            if (!SchematicHierarchyTopology.Inspect(data, cancellationToken).IsValid)
                throw Invalid("Both hierarchy snapshots must have valid instance/reference topology.");
            var assets = data.Instances[0].Metadata;
            if (data.Instances.Any(s => !Equals(s.Metadata.EmbeddedFiles, assets.EmbeddedFiles)
                || s.Metadata.EmbeddedFonts != assets.EmbeddedFonts || !s.Metadata.BusAliases.Equals(assets.BusAliases)
                || !s.Metadata.TextVariables.Equals(assets.TextVariables) || !s.Metadata.NetChains.Equals(assets.NetChains)
                || !s.Metadata.VariantDescriptions.Equals(assets.VariantDescriptions)
                || !Equals(s.Metadata.DrawingRatios, assets.DrawingRatios)
                || !Equals(s.Metadata.Formatting, assets.Formatting)
                || !Equals(s.Metadata.Annotation, assets.Annotation)
                || !SchematicNetChainClasses.Same(s.Metadata.NetChainClasses, assets.NetChainClasses)
                || !SchematicErcSettingsValidation.Same(s.Metadata.ErcSettings, assets.ErcSettings)))
                throw Invalid("Schematic-wide assets, bus aliases, text variables and net chains must agree across all sheet instances.");
        }
        if (!current.Document.Equals(desired.Document))
            throw Invalid("The hierarchy document cannot change during an update.");
        var descriptions = current.Instances[0].Metadata.VariantDescriptions;
        var desiredDescriptions = desired.Instances[0].Metadata.VariantDescriptions;
        foreach (var (name, description) in VariantDescriptions(desired))
            if (description != (descriptions.TryGetValue(name, out var existing) ? existing : "")
                && description != (desiredDescriptions.TryGetValue(name, out var requested) ? requested : ""))
                throw Invalid("Variant description projections must match the current or requested project registry.");
        string Key(SchematicScreenData screen) => string.Join("/", screen.Metadata.Document.SheetPath.Path.Select(id => id.Value));
        var before = current.Instances.ToDictionary(Key, StringComparer.Ordinal);
        var after = desired.Instances.ToDictionary(Key, StringComparer.Ordinal);
        // Removed subtrees disappear through their surviving parent's sheet
        // reference operation. Do not also delete objects inside detached screens:
        // native undo owns those screens and other instances may still use them.
        var addedPaths = after.Keys.Where(path => !before.ContainsKey(path)).ToHashSet(StringComparer.Ordinal);
        var existingScreens = before.Values.Select(s => s.Metadata.ScreenId.Value).ToHashSet(StringComparer.Ordinal);
        var addedSharedPaths = addedPaths.Where(path => existingScreens.Contains(after[path].Metadata.ScreenId.Value)).ToHashSet(StringComparer.Ordinal);
        var reparented = new Dictionary<string, SchematicScreenData>(StringComparer.Ordinal);
        foreach (var path in addedSharedPaths)
        {
            var representative = after.Values.FirstOrDefault(s => before.ContainsKey(Key(s))
                && s.Metadata.ScreenId.Equals(after[path].Metadata.ScreenId));
            if (representative is null)
            {
                var prior = before.Values.OrderBy(Key, StringComparer.Ordinal).First(s => s.Metadata.ScreenId.Equals(after[path].Metadata.ScreenId));
                // Validate every desired instance projection before retargeting
                // the old physical snapshot for delta calculation.
                PhysicalScreen(after[path]);
                reparented.Add(path, Retarget(prior, after[path]));
                continue;
            }
            if (!PhysicalScreen(representative).Equals(PhysicalScreen(after[path])))
                throw Invalid("A new instance must agree with the complete shared-screen contents and placement records.");
        }
        var newSheetIds = addedPaths.Select(path => Guid.Parse(after[path].Metadata.Document.SheetPath.Path[^1].Value)).ToHashSet();

        var sharedPlans = new Dictionary<string, IReadOnlyList<SchematicItemOperation>>(StringComparer.Ordinal);
        var result = new List<SchematicItemOperation>();
        bool aliasesEmitted = false;
        bool variablesEmitted = false;
        bool chainsEmitted = false;
        bool chainClassesEmitted = false;
        bool variantsEmitted = false;
        bool drawingRatiosEmitted = false;
        bool formattingEmitted = false;
        bool annotationEmitted = false;
        bool ercEmitted = false;
        foreach (var path in after.Keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (addedSharedPaths.Contains(path) && !reparented.ContainsKey(path)) continue;
            bool isNew = addedPaths.Contains(path) && !reparented.ContainsKey(path);
            var screen = reparented.TryGetValue(path, out var moved) ? moved
                : isNew ? EmptyNewScreen(after[path], current.Instances[0].Metadata) : before[path];
            var operations = SchematicItemDelta.PlanWithNewSheets(screen, after[path], newSheetIds).ToList();
            if (isNew)
            {
                if (after[path].Metadata.TitleBlock is { } title)
                    operations.Insert(0, new() { SetTitleBlock = title.Clone() });
                if (after[path].Metadata.Page is { } page)
                    operations.Insert(0, new() { SetPageSettings = page.Clone() });
            }
            foreach (var operation in operations)
            {
                var payload = operation.Create ?? operation.Update;
                if (payload?.Is(SchematicSymbolInstance.Descriptor) == true
                    && !Equals(payload.Unpack<SchematicSymbolInstance>().Path, screen.Metadata.Document.SheetPath))
                    throw Invalid("A symbol edit must identify the exact sheet instance from which it was planned.");
            }
            string screenId = screen.Metadata.ScreenId.Value;
            if (sharedPlans.TryGetValue(screenId, out var prior))
            {
                // Reference and selected unit are projections of the complete
                // placement records. Validate those projections before ignoring
                // their differences when comparing physical-screen edits.
                if (!prior.Select(PhysicalOperation).SequenceEqual(operations.Select(PhysicalOperation)))
                    throw Invalid("Repeated instances propose different edits to the same physical screen.");
                continue;
            }
            sharedPlans.Add(screenId, operations);
            foreach (var operation in operations)
            {
                if (operation.SetDrawingRatios is not null)
                {
                    if (drawingRatiosEmitted) continue;
                    drawingRatiosEmitted = true;
                }
                if (operation.SetFormatting is not null)
                {
                    if (formattingEmitted) continue;
                    formattingEmitted = true;
                }
                if (operation.SetAnnotation is not null)
                {
                    if (annotationEmitted) continue;
                    annotationEmitted = true;
                }
                if (operation.SetErcSettings is not null)
                {
                    if (ercEmitted) continue;
                    ercEmitted = true;
                }
                if (operation.ReplaceVariantRegistry is not null)
                {
                    if (variantsEmitted) continue;
                    variantsEmitted = true;
                }
                if (operation.ReplaceBusAliases is not null)
                {
                    if (aliasesEmitted) continue;
                    aliasesEmitted = true;
                }
                if (operation.ReplaceTextVariables is not null)
                {
                    if (variablesEmitted) continue;
                    variablesEmitted = true;
                }
                if (operation.ReplaceNetChains is not null)
                {
                    if (chainsEmitted) continue;
                    chainsEmitted = true;
                }
                if (operation.ReplaceNetChainClasses is not null)
                {
                    if (chainClassesEmitted) continue;
                    chainClassesEmitted = true;
                }
                var targeted = operation.Clone();
                targeted.TargetDocument = screen.Metadata.Document.Clone();
                result.Add(targeted);
            }
        }
        return result;
    }

    private static SchematicScreenData Retarget(SchematicScreenData source, SchematicScreenData desired)
    {
        var result = source.Clone(); result.Metadata.Document = desired.Metadata.Document.Clone();
        for (int i = 0; i < result.Items.Count; ++i)
        {
            var item = result.Items[i];
            if (item.Is(SchematicSymbolInstance.Descriptor))
            {
                var symbol = item.Unpack<SchematicSymbolInstance>(); symbol.Path = desired.Metadata.Document.SheetPath.Clone();
                result.Items[i] = Any.Pack(symbol);
            }
            else if (item.Is(SheetSymbol.Descriptor))
            {
                var sheet = item.Unpack<SheetSymbol>(); sheet.Path = desired.Metadata.Document.SheetPath.Clone();
                result.Items[i] = Any.Pack(sheet);
            }
        }
        return result;
    }

    internal static SchematicScreenData PhysicalScreen(SchematicScreenData source)
    {
        var result = source.Clone();
        result.Metadata.Document.SheetPath = null;
        result.Items.Clear();
        foreach (var (_, item) in SchematicItemDelta.Index(source.Items).OrderBy(pair => pair.Key))
        {
            var path = item switch { SchematicSymbolInstance symbol => symbol.Path, SheetSymbol sheet => sheet.Path, _ => null };
            if ((item is SchematicSymbolInstance || item is SheetSymbol) && !Equals(path, source.Metadata.Document.SheetPath))
                throw Invalid("Shared object projections must identify their exact sheet instance.");
            result.Items.Add(PhysicalOperation(new() { Update = Any.Pack(item) }).Update);
        }
        return result;
    }

    private static SchematicScreenData EmptyNewScreen(SchematicScreenData desired, SchematicMetadata project)
    {
        var metadata = desired.Metadata;
        if (desired.UnrepresentedItems.Count != 0 || metadata.UnrepresentedState.Count != 0
            || metadata.RootInstance?.HasPageNumber == true
            || metadata.LoadedNativeFormatVersion != 0)
            throw Invalid("New screens require explicit supported contents, no unrepresented state, no root-page record and no loaded-file provenance.");
        if (!Equals(metadata.EmbeddedFiles, project.EmbeddedFiles) || metadata.EmbeddedFonts != project.EmbeddedFonts)
            throw Invalid("New-screen assets must match the existing schematic.");
        // Metadata which is explicitly supported is initialized with native
        // operations above, not inferred from the editor's current defaults.
        return new SchematicScreenData { Metadata = metadata.Clone() };
    }

    private static SchematicItemOperation PhysicalOperation(SchematicItemOperation operation)
    {
        var result = operation.Clone();
        var payload = result.Create ?? result.Update;
        if (payload?.Is(SheetSymbol.Descriptor) == true)
        {
            var sheet = payload.Unpack<SheetSymbol>();
            if (sheet.Path is null || sheet.InstanceRecords is null)
                throw Invalid("Shared sheet references require complete placement records.");
            var sheetPlacements = sheet.InstanceRecords.Records.Where(r => r.Path.SequenceEqual(sheet.Path.Path)).ToArray();
            var variants = sheet.Variants?.Clone() ?? new SheetVariants();
            foreach (var variant in variants.Variants) variant.ClearDescription();
            if (sheetPlacements.Length != 1 || sheetPlacements[0].PageNumber != sheet.PageNumber
                || !variants.Equals(sheetPlacements[0].Variants ?? new SheetVariants()))
                throw Invalid("A shared sheet's page and variants must agree with its placement record.");
            sheet.Path = null; sheet.PageNumber = ""; sheet.Variants = null;
            if (result.Create is not null) result.Create = Any.Pack(sheet);
            else result.Update = Any.Pack(sheet);
            return result;
        }
        if (payload?.Is(SchematicSymbolInstance.Descriptor) != true) return result;
        var symbol = payload.Unpack<SchematicSymbolInstance>();
        if (symbol.Path is null || symbol.InstanceRecords is null)
            throw Invalid("Shared symbol edits require the complete placement records and observation path.");
        var placements = symbol.InstanceRecords.Records.Where(record => record.Path.SequenceEqual(symbol.Path.Path)).ToArray();
        if (placements.Length != 1 || symbol.ReferenceField?.Text is null || symbol.Unit is null
            || placements[0].Reference != symbol.ReferenceField.Text.Text_ || placements[0].Unit != symbol.Unit.Unit)
            throw Invalid("A shared symbol's projected reference/unit must agree with its exact placement record.");
        var projectedVariants = symbol.Variants?.Clone() ?? new SchematicSymbolVariants();
        foreach (var variant in projectedVariants.Variants) variant.ClearDescription();
        if (!projectedVariants.Equals(placements[0].Variants ?? new SchematicSymbolVariants()))
            throw Invalid("A shared symbol's projected variants must agree with its exact placement record.");
        symbol.Path = null;
        symbol.ReferenceField.Text.Text_ = "";
        symbol.Unit = null;
        symbol.Variants = null;
        // All placement records, variant information, geometry and other fields
        // remain in the comparison; only proven redundant projections are removed.
        if (result.Create is not null) result.Create = Any.Pack(symbol);
        else result.Update = Any.Pack(symbol);
        return result;
    }

    private static Dictionary<string, string> VariantDescriptions(SchematicHierarchyData data)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in data.Instances.SelectMany(screen => screen.Items))
        {
            IEnumerable<(string Name, string Description)> variants = [];
            if (item.Is(SchematicSymbolInstance.Descriptor))
                variants = item.Unpack<SchematicSymbolInstance>().Variants?.Variants.Where(v => v.HasDescription)
                    .Select(v => (v.Name, v.Description)) ?? [];
            else if (item.Is(SheetSymbol.Descriptor))
                variants = item.Unpack<SheetSymbol>().Variants?.Variants.Where(v => v.HasDescription)
                    .Select(v => (v.Name, v.Description)) ?? [];
            foreach (var variant in variants)
            {
                if (descriptions.TryGetValue(variant.Name, out var prior) && prior != variant.Description)
                    throw Invalid("Schematic-wide variant descriptions disagree between symbol projections.");
                descriptions[variant.Name] = variant.Description;
            }
        }
        return descriptions;
    }

    private static AutomationException Invalid(string message) => new("unsupported_hierarchy_delta", message);
}
