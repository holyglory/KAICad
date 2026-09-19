using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

internal sealed record SchematicNativeCreationResult(SchematicDesign Candidate,
    IReadOnlyList<SchematicItemOperation> Operations, IReadOnlyList<Guid> CreatedOccurrences);

/// <summary>
/// Creates the native representation for a narrow, unambiguous XML-first
/// addition. The new component must use an exact part/unit template or an explicitly
/// declared standalone symbol definition, have
/// explicit placement, and have no new net membership. Connectivity-changing
/// creation remains a separate ownership operation and is never inferred here.
/// </summary>
internal static class SchematicNativeCreationProjection
{
    internal static bool IsSupportedAddition(SchematicDesign baseline, EngineeringDesign desired)
    {
        var oldCircuit = baseline.Engineering.Circuit;
        var newCircuit = desired.Circuit;
        var oldComponents = oldCircuit.Components.ToDictionary(c => c.Id);
        var newComponents = newCircuit.Components.ToDictionary(c => c.Id);
        bool added = newComponents.Keys.Except(oldComponents.Keys).Any();
        bool removed = oldComponents.Keys.Except(newComponents.Keys).Any();
        return added && !removed && oldCircuit.Id == newCircuit.Id
            && oldCircuit.Parts.All(part => newCircuit.Parts.SingleOrDefault(p => p.Id == part.Id) is { } next
                && NormalizePart(part) == NormalizePart(next))
            && oldCircuit.SheetInstances.OrderBy(instance => instance.Id).SequenceEqual(newCircuit.SheetInstances.OrderBy(instance => instance.Id))
            && oldCircuit.Nets.OrderBy(net => net.Id).Select(NormalizeNet)
                .SequenceEqual(newCircuit.Nets.OrderBy(net => net.Id).Select(NormalizeNet))
            && oldCircuit.Components.All(c => newComponents.TryGetValue(c.Id, out var current) && c.Equals(current))
            && oldCircuit.Symbols.All(s => newCircuit.Symbols.Any(current => current.Id == s.Id && current.Equals(s)))
            && oldCircuit.Sheets.All(sheet => newCircuit.Sheets.SingleOrDefault(current => current.Id == sheet.Id) is { } current
                && sheet.Components.All(component => current.Components.Any(next => next.Id == component.Id && next.Equals(component))));

        static string NormalizeNet(CircuitNet net) => JsonSerializer.Serialize(new
        {
            net.Id,
            net.Name,
            Pins = net.Pins.OrderBy(pin => pin.ComponentId).ThenBy(pin => pin.Pin, StringComparer.Ordinal).ToArray()
        });

        static string NormalizePart(PartDefinition part) => JsonSerializer.Serialize(new
        {
            part.Id,
            part.Name,
            part.Units,
            Pins = part.Pins.OrderBy(pin => pin.Number, StringComparer.Ordinal)
                .ThenBy(pin => pin.Unit).Select(pin => new { pin.Number, pin.Name, pin.Unit }).ToArray()
        });
    }

    internal static SchematicNativeCreationResult Project(SchematicDesign baseline, EngineeringDesign desired,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default)
        => Project(baseline, baseline with { Engineering = desired }, libraries, token);

    internal static SchematicNativeCreationResult Project(SchematicDesign baseline, SchematicDesign desiredDesign,
        IReadOnlyCollection<ComponentKnowledgeLibrary> libraries, CancellationToken token = default)
    {
        var desired = desiredDesign.Engineering;
        token.ThrowIfCancellationRequested();
        baseline.Engineering.Validate(libraries);
        desired.Validate(libraries);
        SchematicPartSymbols.Validate(desiredDesign, token);
        var declared = (desiredDesign.PartSymbols ?? []).ToDictionary(s => s.PartId);
        var baselineReport = SchematicDesignBindings.Inspect(baseline, libraries, token);
        if (!baselineReport.IdentitiesResolved)
            throw Invalid("unresolved_design_bindings", "The existing design must have exact native bindings before creating a component.");

        var oldCircuit = baseline.Engineering.Circuit;
        var newCircuit = desired.Circuit;
        var oldParts = oldCircuit.Parts.ToDictionary(p => p.Id);
        var newParts = newCircuit.Parts.ToDictionary(p => p.Id);
        if (oldParts.Any(pair => !newParts.TryGetValue(pair.Key, out var next) || !SamePart(pair.Value, next)))
            throw Invalid("part_definition_change_requires_resolution", "Preserve existing part definitions while creating new components.");
        if (newParts.Keys.Except(oldParts.Keys).Any(id => !declared.ContainsKey(id)))
            throw Invalid("new_part_requires_library_definition", "Creating a new part requires an explicit native library definition.");
        if (!oldCircuit.SheetInstances.OrderBy(instance => instance.Id).SequenceEqual(newCircuit.SheetInstances.OrderBy(instance => instance.Id))
            || !oldCircuit.Sheets.Select(s => s.Id).ToHashSet().SetEquals(newCircuit.Sheets.Select(s => s.Id)))
            throw Invalid("sheet_ownership_change_requires_resolution", "Sheet insertion, removal or reparenting requires explicit ownership reconciliation.");

        var oldSheets = oldCircuit.Sheets.ToDictionary(s => s.Id);
        var newSheets = newCircuit.Sheets.ToDictionary(s => s.Id);
        var addedDefinitions = new HashSet<Guid>();
        foreach (var oldSheet in oldCircuit.Sheets)
        {
            if (!newSheets.TryGetValue(oldSheet.Id, out var newSheet))
                throw Invalid("sheet_ownership_change_requires_resolution", "An existing sheet definition disappeared.");
            var oldDefinitions = oldSheet.Components.ToDictionary(c => c.Id);
            var newDefinitions = newSheet.Components.ToDictionary(c => c.Id);
            foreach (var (id, definition) in oldDefinitions)
                if (!newDefinitions.TryGetValue(id, out var current) || !definition.Equals(current))
                    throw Invalid("component_rebinding_requires_resolution", "Changing an existing component definition is not inferred during creation.");
            foreach (var definition in newSheet.Components.Where(c => !oldDefinitions.ContainsKey(c.Id)))
                if (!addedDefinitions.Add(definition.Id))
                    throw Invalid("duplicate_component_definition", "A new component definition must have one exact owning sheet.");
        }

        var oldComponents = oldCircuit.Components.ToDictionary(c => c.Id);
        var newComponents = newCircuit.Components.ToDictionary(c => c.Id);
        var addedComponents = newCircuit.Components.Where(c => !oldComponents.ContainsKey(c.Id)).ToArray();
        foreach (var oldComponent in oldCircuit.Components)
            if (!newComponents.TryGetValue(oldComponent.Id, out var current) || !oldComponent.Equals(current))
                throw Invalid("component_rebinding_requires_resolution", "Changing an existing component owner or reference requires explicit ownership reconciliation.");
        if (addedComponents.Length == 0)
            throw Invalid("no_component_creation", "The requested design contains no new component instance.");
        foreach (var component in addedComponents)
        {
            if (!addedDefinitions.Contains(component.DefinitionId))
                throw Invalid("component_definition_required", "A new component must introduce an exact component definition.");
            if (!newCircuit.SheetInstances.Any(s => s.Id == component.SheetInstanceId))
                throw Invalid("unknown_component_sheet", "A new component must target an existing sheet instance.");
            if (newCircuit.Nets.Any(n => n.Pins.Any(pin => pin.ComponentId == component.Id)))
                throw Invalid("created_component_connectivity_requires_resolution", "A connected component requires an explicit native wiring operation.");
        }

        var oldSymbols = oldCircuit.Symbols.ToDictionary(s => s.Id);
        var newSymbols = newCircuit.Symbols.ToDictionary(s => s.Id);
        foreach (var oldSymbol in oldCircuit.Symbols)
            if (!newSymbols.TryGetValue(oldSymbol.Id, out var current) || !oldSymbol.Equals(current))
                throw Invalid("symbol_rebinding_requires_resolution", "Changing an existing symbol occurrence is not inferred during creation.");
        var addedSymbols = newCircuit.Symbols.Where(s => !oldSymbols.ContainsKey(s.Id)).ToArray();
        var addedComponentIds = addedComponents.Select(c => c.Id).ToHashSet();
        if (addedSymbols.Any(s => !addedComponentIds.Contains(s.ComponentId)))
            throw Invalid("symbol_owner_requires_resolution", "A new symbol occurrence must belong to a new component instance.");
        foreach (var component in addedComponents)
        {
            var definition = newSheets.Values.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId);
            var part = newParts[definition.PartId];
            var occurrences = addedSymbols.Where(s => s.ComponentId == component.Id).ToArray();
            if (occurrences.Select(s => s.Unit).Distinct().Count() != occurrences.Length
                || !occurrences.Select(s => s.Unit).ToHashSet().SetEquals(Enumerable.Range(1, part.Units)))
                throw Invalid("component_units_incomplete", "A created component must provide exactly one native symbol occurrence for every declared unit.");
        }

        var existingPaths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId,
            b => SchematicDesignBindings.PathKey(b.NativePath));
        var nativeIds = baseline.Schematic.Instances.SelectMany(s => s.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>().Id.Value))
            .ToHashSet(StringComparer.Ordinal);
        var bindings = baseline.SymbolBindings.ToList();
        var augmented = baseline.Schematic.Clone();
        var screens = augmented.Instances.ToDictionary(s => Path(s.Metadata.Document), StringComparer.Ordinal);
        var created = new List<Guid>();
        var groupedSymbols = addedSymbols.GroupBy(occurrence =>
        {
            var component = newComponents[occurrence.ComponentId];
            var definition = newSheets.Values.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId);
            string path = existingPaths[occurrence.EffectiveSheetInstanceId(component)];
            string physicalScreen = screens[path].Metadata.ScreenId.Value;
            return (PhysicalScreen: physicalScreen, Definition: definition.Id, Unit: occurrence.Unit);
        }).OrderBy(g => g.Key.PhysicalScreen, StringComparer.Ordinal).ThenBy(g => g.Key.Definition).ThenBy(g => g.Key.Unit).ToArray();
        foreach (var group in groupedSymbols)
        {
            token.ThrowIfCancellationRequested();
            var occurrences = group.OrderBy(s => s.Id).ToArray();
            var representative = occurrences[0];
            if (occurrences.Any(s => s.Placement is null))
                throw Invalid("created_symbol_placement_required", "Coordinate-free creation needs layout resolution before a native symbol can be inserted.");
            if (occurrences.Skip(1).Any(s => !SchematicOrientation.Equivalent(s.Placement, representative.Placement)))
                throw Invalid("shared_symbol_placement_conflict", "Repeated instances of one physical symbol must request the same placement.");
            string nativeId = StablePhysicalId(group.Key.PhysicalScreen, group.Key.Definition, group.Key.Unit);
            if (nativeIds.Contains(nativeId)) throw Invalid("created_native_identity_collision", "The deterministic native identity is already in use.");
            var representativeComponent = newComponents[representative.ComponentId];
            var representativeDefinition = newSheets.Values.SelectMany(s => s.Components).Single(c => c.Id == representativeComponent.DefinitionId);
            var part = newParts[representativeDefinition.PartId];
            var declaration = declared.GetValueOrDefault(part.Id);
            var template = declaration is null
                ? FindTemplate(baseline, oldCircuit, oldComponents, oldSheets, representative, part, screens, existingPaths, token)
                : (Symbol: InstantiateDeclaration(declaration), Path: "");
            var createdSymbols = new List<SchematicSymbolInstance>();
            var targetScreens = new List<SchematicScreenData>();
            foreach (var occurrence in occurrences)
            {
                token.ThrowIfCancellationRequested();
                var component = newComponents[occurrence.ComponentId];
                var definition = newSheets.Values.SelectMany(s => s.Components).Single(c => c.Id == component.DefinitionId);
                Guid sheetInstance = occurrence.EffectiveSheetInstanceId(component);
                if (!existingPaths.TryGetValue(sheetInstance, out var path) || !screens.TryGetValue(path, out var target))
                    throw Invalid("missing_native_sheet", "A created symbol must target a bound existing sheet instance.");
                var symbol = CreateSymbol(template.Symbol, target, occurrence, component, definition.Value, part, nativeIds, nativeId, token);
                if (declaration is null) CopyLibraryCache(screens[template.Path], target, template.Symbol);
                else CopyLibraryCache(declaration.Symbol, target);
                target.Items.Add(Any.Pack(symbol));
                createdSymbols.Add(symbol); targetScreens.Add(target);
                bindings.Add(new(occurrence.Id, Guid.Parse(symbol.Id.Value)));
                created.Add(occurrence.Id);
            }
            var records = new SymbolSheetRecords();
            foreach (var occurrence in occurrences.OrderBy(s => existingPaths[s.EffectiveSheetInstanceId(newComponents[s.ComponentId])], StringComparer.Ordinal))
            {
                var component = newComponents[occurrence.ComponentId];
                var path = existingPaths[occurrence.EffectiveSheetInstanceId(component)];
                var target = screens[path];
                var record = new SymbolSheetRecord { ProjectName = target.Metadata.Document.Project.Name,
                    Reference = component.Reference, Unit = occurrence.Unit, Variants = template.Symbol.Variants?.Clone() ?? new() };
                record.Path.Add(target.Metadata.Document.SheetPath.Path.Select(p => p.Clone()));
                records.Records.Add(record);
            }
            for (int index = 0; index < createdSymbols.Count; index++)
            {
                createdSymbols[index].InstanceRecords = records.Clone();
                int itemIndex = targetScreens[index].Items.ToList().FindIndex(item =>
                    item.Is(SchematicSymbolInstance.Descriptor)
                    && item.Unpack<SchematicSymbolInstance>().Id.Value == nativeId);
                if (itemIndex < 0) throw Invalid("created_native_identity_missing", "The generated symbol was not retained in its target screen.");
                targetScreens[index].Items[itemIndex] = Any.Pack(createdSymbols[index]);
            }
            nativeIds.Add(nativeId);
        }

        var candidate = baseline with { Engineering = desired, Schematic = augmented, PartSymbols = desiredDesign.PartSymbols,
            SymbolBindings = bindings.OrderBy(b => b.SymbolOccurrenceId).ToArray() };
        var report = SchematicDesignBindings.Inspect(candidate, libraries, token);
        if (!report.IdentitiesResolved)
            throw Invalid("created_binding_invalid", "The generated native identities did not resolve exactly: "
                + string.Join(",", report.Issues.Select(issue => issue.Code)));
        var operations = SchematicHierarchyDelta.Plan(baseline.Schematic, candidate.Schematic, token);
        return new(candidate, operations, created.Order().ToArray());
    }

    private static (SchematicSymbolInstance Symbol, string Path) FindTemplate(SchematicDesign baseline,
        Circuit circuit, IReadOnlyDictionary<Guid, ComponentInstance> components,
        IReadOnlyDictionary<Guid, SheetDefinition> sheets,
        SymbolOccurrence wanted, PartDefinition part, IReadOnlyDictionary<string, SchematicScreenData> screens,
        IReadOnlyDictionary<Guid, string> paths, CancellationToken token)
    {
        var candidates = new List<(SchematicSymbolInstance Symbol, string Path)>();
        foreach (var existing in circuit.Symbols.Where(s => s.Unit == wanted.Unit).OrderBy(s => s.Id))
        {
            token.ThrowIfCancellationRequested();
            var owner = components[existing.ComponentId];
            var definition = sheets.Values.SelectMany(s => s.Components).Single(c => c.Id == owner.DefinitionId);
            if (definition.PartId != part.Id || !paths.TryGetValue(existing.EffectiveSheetInstanceId(owner), out var path)
                || !screens.TryGetValue(path, out var screen)) continue;
            var binding = baseline.SymbolBindings.Single(b => b.SymbolOccurrenceId == existing.Id);
            var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
                .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == binding.NativeObjectId.ToString("D"));
            ValidateTemplate(symbol, part, existing.Unit);
            candidates.Add((symbol, path));
        }
        if (candidates.Count == 0)
            throw Invalid("missing_symbol_template", "No existing exact part/unit native symbol can be used to create this component.");
        var first = candidates[0];
        if (candidates.Skip(1).Any(c => !NormalizedDefinition(c.Symbol.Definition!).Equals(NormalizedDefinition(first.Symbol.Definition!))))
            throw Invalid("ambiguous_symbol_template", "Multiple native symbol definitions match the part/unit; select an explicit library identity.");
        return first;
    }

    private static SchematicSymbolInstance CreateSymbol(SchematicSymbolInstance template, SchematicScreenData target,
        SymbolOccurrence occurrence, ComponentInstance component, string value, PartDefinition part,
        IReadOnlySet<string> usedIds, string nativeId, CancellationToken token)
    {
        ValidateTemplate(template, part, occurrence.Unit);
        var result = template.Clone();
        result.Id = new() { Value = nativeId };
        if (usedIds.Contains(result.Id.Value)) throw Invalid("created_native_identity_collision", "The deterministic native identity is already in use.");
        result.Path = target.Metadata.Document.SheetPath.Clone();
        result.Unit = new() { Unit = occurrence.Unit };
        result.Position = new()
        {
            XNm = Coordinates.MillimetersToNanometers(occurrence.Placement!.XMillimeters),
            YNm = Coordinates.MillimetersToNanometers(occurrence.Placement.YMillimeters)
        };
        result.Transform = new()
        {
            Orientation = (SchematicSymbolOrientation)(occurrence.Placement.RotationDegrees / 90 + 1),
            MirrorX = occurrence.Placement.MirrorX,
            MirrorY = occurrence.Placement.MirrorY
        };
        result.Locked = occurrence.Placement.Locked ? LockedState.LsLocked : LockedState.LsUnlocked;
        long dx = result.Position.XNm - template.Position.XNm;
        long dy = result.Position.YNm - template.Position.YNm;
        foreach (var field in Fields(result))
            if (field.Text?.Position is { } position) { position.XNm += dx; position.YNm += dy; }
        if (result.ReferenceField?.Text is null || result.ValueField?.Text is null)
            throw Invalid("incomplete_symbol_fields", "A created symbol requires complete reference and value fields.");
        result.ReferenceField.Text.Text_ = component.Reference;
        result.ValueField.Text.Text_ = value;
        if (result.InstanceRecords is null)
            throw Invalid("incomplete_symbol_instance_records", "A created symbol requires complete instance records.");
        var records = result.InstanceRecords.Clone();
        var matching = records.Records.Where(r => r.Path.SequenceEqual(target.Metadata.Document.SheetPath.Path)).ToArray();
        if (matching.Length > 1)
            throw Invalid("ambiguous_symbol_instance_record", "A created symbol needs one exact instance record for its target sheet.");
        var record = matching.Length == 1 ? matching[0] : new SymbolSheetRecord
        {
            ProjectName = target.Metadata.Document.Project.Name,
            Reference = component.Reference,
            Unit = occurrence.Unit,
            Variants = result.Variants?.Clone() ?? new()
        };
        if (matching.Length == 0) record.Path.Add(target.Metadata.Document.SheetPath.Path.Select(p => p.Clone()));
        record.Reference = component.Reference;
        record.Unit = occurrence.Unit;
        if (string.IsNullOrWhiteSpace(record.ProjectName)) record.ProjectName = target.Metadata.Document.Project.Name;
        if (matching.Length == 0) records.Records.Add(record);
        result.InstanceRecords = records;
        int pinOrdinal = 0;
        foreach (var child in result.Definition.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true))
        {
            token.ThrowIfCancellationRequested();
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null)
                pin.Id = new() { Value = StablePinId(nativeId, pin.LibraryPinId?.Value, pin.Number, pinOrdinal++) };
            child.Item = Any.Pack(pin);
        }
        OrderDefinitionPins(result.Definition, libraryIds: false);
        return result;
    }

    private static void ValidateTemplate(SchematicSymbolInstance symbol, PartDefinition part, int unit)
    {
        if (symbol.Definition is null || !symbol.SeparatePinIdentities || symbol.Position is null
            || symbol.Transform is null || symbol.InstanceRecords is null)
            throw Invalid("incomplete_symbol_template", "The existing part/unit symbol lacks persistent native definition, pin or placement state.");
        if (symbol.Definition.UnitCount < part.Units)
            throw Invalid("symbol_unit_mismatch", "The native symbol template does not contain every declared unit.");
        var pins = symbol.Definition.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true)
            .Select(c => (Child: c, Pin: c.Item.Unpack<SchematicPin>()))
            .Where(p => p.Child.Unit is null || p.Child.Unit.Unit == 0 || p.Child.Unit.Unit == unit)
            .Where(p => p.Pin.LibraryPinId is not null).Select(p => p.Pin.Number).ToHashSet(StringComparer.Ordinal);
        var required = part.Pins.Where(p => p.Unit == 0 || p.Unit == unit).Select(p => p.Number).ToHashSet(StringComparer.Ordinal);
        if (!pins.SetEquals(required)) throw Invalid("symbol_pin_template_mismatch", "The native template pins do not exactly cover the declared part pins.");
    }

    private static SchematicSymbol NormalizedDefinition(SchematicSymbol definition)
    {
        var result = definition.Clone();
        foreach (var child in result.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true))
        {
            var pin = child.Item.Unpack<SchematicPin>();
            if (pin.LibraryPinId is not null) pin.Id = null;
            child.Item = Any.Pack(pin);
        }
        return result;
    }

    private static IEnumerable<SchematicField> Fields(SchematicSymbolInstance symbol) => new[]
    {
        symbol.ReferenceField, symbol.ValueField, symbol.FootprintField, symbol.DatasheetField, symbol.DescriptionField
    }.Concat(symbol.UserFields).Where(field => field is not null)!;

    private static SchematicSymbolInstance InstantiateDeclaration(SchematicPartSymbol source)
    {
        var definition = source.Symbol.Definition;
        if (new[] { definition.ReferenceField, definition.ValueField, definition.FootprintField,
                definition.DatasheetField, definition.DescriptionField }.Any(f => f?.Text?.Position is null))
            throw Invalid("incomplete_symbol_fields", "A declared symbol requires all five standard fields with explicit local positions.");
        var result = new SchematicSymbolInstance
        {
            Definition = definition.Clone(), LibraryId = source.LibraryId.Clone(), LibName = source.Symbol.CacheKey,
            Position = new(), Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 },
            Locked = LockedState.LsUnlocked,
            BodyStyle = definition.BodyStyle.Count > 1 ? new() { Style = source.BodyStyle } : null,
            Passthrough = SchematicPassthroughMode.SpmDefault,
            SeparatePinIdentities = true, InstanceRecords = new(), Variants = new(),
            ShowPinNames = source.Symbol.ShowPinNames, ShowPinNumbers = source.Symbol.ShowPinNumbers,
            PinNameOffset = source.Symbol.PinNameOffset.Clone(), DefinitionPinNameOffset = source.Symbol.PinNameOffset.Clone(),
            Attributes = definition.Attributes?.Clone() ?? new(),
            ReferenceField = definition.ReferenceField.Clone(), ValueField = definition.ValueField.Clone(),
            FootprintField = definition.FootprintField.Clone(), DatasheetField = definition.DatasheetField.Clone(),
            DescriptionField = definition.DescriptionField.Clone()
        };
        foreach (var child in result.Definition.Items)
        {
            if (child.Item.Is(SchematicField.Descriptor)) result.UserFields.Add(child.Item.Unpack<SchematicField>());
            if (!child.Item.Is(SchematicPin.Descriptor)) continue;
            var pin = child.Item.Unpack<SchematicPin>();
            int style = child.BodyStyle?.Style ?? 0;
            if (style == 0 || style == source.BodyStyle)
            {
                // The declaration retains its owned UUID. CreateSymbol assigns the distinct
                // deterministic placed UUID; inactive styles remain library-owned only.
                pin.LibraryPinId = pin.Id.Clone();
                child.Item = Any.Pack(pin);
            }
        }
        // Native cache enumeration is not an identity. Assign deterministic placed
        // IDs in owned-pin order, then match native PackSymbol's placed-pin order.
        OrderNativeDefinitionChildren(result.Definition);
        OrderDefinitionPins(result.Definition, libraryIds: true);
        return result;
    }

    private static void OrderDefinitionPins(SchematicSymbol definition, bool libraryIds)
    {
        var others = definition.Items.Where(c => !c.Item.Is(SchematicPin.Descriptor)).ToArray();
        var pins = definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)).OrderBy(c =>
        {
            var pin = c.Item.Unpack<SchematicPin>();
            return libraryIds ? pin.LibraryPinId?.Value ?? pin.Id.Value : pin.Id.Value;
        }, StringComparer.Ordinal).ToArray();
        definition.Items.Clear(); definition.Items.Add(others); definition.Items.Add(pins);
    }

    private static void OrderNativeDefinitionChildren(SchematicSymbol definition)
    {
        // LIB_ITEMS_CONTAINER is MULTIVECTOR<SCH_ITEM, SCH_SHAPE_T, SCH_PIN_T>.
        // Match that pinned type-bucket traversal without changing the order within
        // a bucket or mutating the independently retained declaration.
        foreach (var child in definition.Items)
        {
            child.Unit ??= new();
            child.BodyStyle ??= new();
        }
        var ordered = definition.Items.OrderBy(child => child.Item switch
        {
            var item when item.Is(SchematicGraphicShape.Descriptor) => 0,
            var item when item.Is(SchematicField.Descriptor) => 1,
            var item when item.Is(SchematicText.Descriptor) => 2,
            var item when item.Is(SchematicTextBox.Descriptor) => 3,
            var item when item.Is(SchematicPin.Descriptor) => 4,
            _ => throw Invalid("unsupported_symbol_child", "The declared symbol contains an unsupported native library child.")
        }).ToArray();
        definition.Items.Clear(); definition.Items.Add(ordered);
    }

    private static void CopyLibraryCache(SchematicScreenData source, SchematicScreenData target, SchematicSymbolInstance symbol)
    {
        var library = symbol.LibraryId ?? symbol.Definition.Id;
        string key = symbol.LibName.Length != 0 ? symbol.LibName : library is null ? ""
            : (library.LibraryNickname.Length == 0 ? "" : library.LibraryNickname + ":") + library.EntryName;
        var entry = source.CachedSymbols.SingleOrDefault(c => c.CacheKey == key);
        if (entry is null) return; // Legacy, explicitly incomplete DTOs remain inspectable, not live proof.
        CopyLibraryCache(entry, target);
    }

    private static void CopyLibraryCache(SchematicCachedSymbol entry, SchematicScreenData target)
    {
        entry = entry.Clone();
        OrderNativeDefinitionChildren(entry.Definition);
        var existing = target.CachedSymbols.SingleOrDefault(c => c.CacheKey == entry.CacheKey);
        if (existing is not null && !SchematicLibraryCacheEquivalence.Equal(existing, entry))
            throw Invalid("created_symbol_cache_conflict", "The target screen has a different definition for the selected library key.");
        if (existing is null) target.CachedSymbols.Add(entry.Clone());
    }

    private static string StablePhysicalId(string physicalScreen, Guid definition, int unit)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-created-symbol-v2\n" + physicalScreen
            + "\n" + definition.ToString("D") + "\n" + unit));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50); digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private static string StablePinId(string nativeId, string? libraryPin, string number, int ordinal)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-created-pin-v2\n" + nativeId
            + "\n" + (libraryPin ?? "") + "\n" + number + "\n" + ordinal));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50); digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    private static string Path(DocumentSpecifier document) => string.Join('/', document.SheetPath.Path.Select(p => p.Value));
    private static bool SamePart(PartDefinition left, PartDefinition right) => left.Id == right.Id
        && left.Name == right.Name && left.Units == right.Units
        && left.Pins.OrderBy(pin => pin.Number, StringComparer.Ordinal).ThenBy(pin => pin.Unit)
            .SequenceEqual(right.Pins.OrderBy(pin => pin.Number, StringComparer.Ordinal).ThenBy(pin => pin.Unit));
    private static AutomationException Invalid(string code, string message) => new(code, message);
}
