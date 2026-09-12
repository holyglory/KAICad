using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

/// <summary>Plans identity-based item changes without executing them. Not an
/// automatic synchronization service or a complete revision-admission guard.</summary>
public static class SchematicItemDelta
{
    // Pinned fork eeschema/sch_file_versions.h. Native journey asserts this
    // value against the actual build; upgrading needs explicit coverage work.
    public const uint SupportedWriterFormatVersion = 20260912;
    private static readonly MessageDescriptor[] ItemTypes =
    [
        SchematicLine.Descriptor, Junction.Descriptor, NoConnectMarker.Descriptor, BusEntry.Descriptor,
        SchematicText.Descriptor, SchematicTextBox.Descriptor, SchematicGraphicShape.Descriptor,
        SchematicImage.Descriptor, LocalLabel.Descriptor, GlobalLabel.Descriptor, HierarchicalLabel.Descriptor,
        DirectiveLabel.Descriptor, Group.Descriptor, SchematicRuleArea.Descriptor, SheetSymbol.Descriptor,
        SchematicSymbolInstance.Descriptor, SchematicTable.Descriptor
    ];

    public static IReadOnlyList<SchematicItemOperation> Plan(SchematicScreenData current, SchematicScreenData desired)
        => PlanWithNewSheets(current, desired, new HashSet<Guid>());

    internal static IReadOnlyList<SchematicItemOperation> PlanWithNewSheets(SchematicScreenData current,
        SchematicScreenData desired, IReadOnlySet<Guid> newSheetIds, SchematicMetadata? projectionBaseline = null)
    {
        if (current.Metadata?.Document?.SheetPath is not { } path || path.Path.Count == 0
            || current.Metadata.ScreenId is null)
            throw Invalid("An explicit current screen and sheet-instance target is required.");
        Identity(current.Metadata.ScreenId);
        foreach (var id in path.Path) Identity(id);
        ValidateFormat(current.Metadata);
        ValidateFormat(desired.Metadata);
        var cacheChange = CacheOperation(current, desired);
        var operations = MetadataOperations(current.Metadata, desired.Metadata);
        if (current.UnrepresentedItems.Count != 0 || desired.UnrepresentedItems.Count != 0)
            throw Invalid("Unrepresented screen objects must be supported before planning item changes.");
        // Also reject unknown transport fields instead of normalizing away
        // native information while constructing the edit list.
        SchematicDataXml.Write(current);
        SchematicDataXml.Write(desired);
        SchematicFieldTextModes.Validate(current);
        SchematicFieldTextModes.Validate(desired);
        var before = Index(current.Items);
        var after = Index(desired.Items);
        foreach (var definition in current.CachedSymbols.Concat(desired.CachedSymbols).Select(c => c.Definition)
                     .Concat(before.Values.Concat(after.Values).OfType<SchematicSymbolInstance>().Select(s => s.Definition)))
        {
            if (definition?.DemorganBodyStyles == true
                && (definition.BodyStyle.Count != 2 || definition.BodyStyle[0].Name != "Standard"
                    || definition.BodyStyle[1].Name != "Alternate"))
                throw Invalid("De Morgan definitions require the exact Standard/Alternate style pair; custom names cannot be silently discarded.");
        }
        foreach (var item in before.Values) SchematicVariantProjection.ValidateAndStrip(item, current.Metadata, projectionBaseline);
        foreach (var item in after.Values) SchematicVariantProjection.ValidateAndStrip(item, desired.Metadata, projectionBaseline ?? current.Metadata);
        if (after.Values.OfType<SchematicGraphicShape>().Any(shape => shape.Shape?.Segment is not null))
            throw Invalid("Schematic graphic segments must use SchematicLine with type SLT_GRAPHIC; the native writer cannot save a segment-shaped SchematicGraphicShape.");
        SchematicGroupGraph.Validate(before);
        SchematicGroupGraph.Validate(after);
        var createdGroups = new SortedDictionary<Guid, Group>();
        var removedGroups = new SortedDictionary<Guid, Group>();
        var updatedGroups = new SortedDictionary<Guid, Group>();
        var detachments = new List<SchematicItemOperation>();
        foreach (Guid id in before.Keys.Union(after.Keys).Order())
        {
            before.TryGetValue(id, out var oldItem);
            after.TryGetValue(id, out var newItem);
            if (oldItem?.Equals(newItem) == true) continue;
            if (oldItem is not null && newItem is not null && oldItem.Descriptor != newItem.Descriptor)
                throw Invalid("An existing native identity cannot change object type.");
            if (oldItem is SheetSymbol || newItem is SheetSymbol)
                ValidateSheetDelta(oldItem as SheetSymbol, newItem as SheetSymbol, before, path, newSheetIds);
            if (oldItem is Group || newItem is Group)
            {
                if (oldItem is Group removedGroup && newItem is null)
                {
                    removedGroups.Add(id, removedGroup);
                    continue;
                }
                if (oldItem is null && newItem is Group createdGroup)
                {
                    if (createdGroup.Items.Count == 0)
                        throw Invalid("Empty groups cannot be reconstructed because the native writer omits them.");
                    createdGroups.Add(id, createdGroup);
                    continue;
                }
                if (oldItem is Group oldGroup && newItem is Group newGroup
                    && !oldGroup.Items.Select(member => member.Value).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(newGroup.Items.Select(member => member.Value)))
                {
                    if (newGroup.Items.Count == 0)
                        throw Invalid("Final empty groups are not persisted by the native writer; remove the group instead.");
                    var detached = oldGroup.Clone();
                    var retained = oldGroup.Items.Where(member => newGroup.Items.Contains(member)).ToArray();
                    detached.Items.Clear(); detached.Items.Add(retained);
                    if (!detached.Equals(oldGroup)) detachments.Add(new() { Update = Any.Pack(detached) });
                    updatedGroups.Add(id, newGroup);
                    continue;
                }
            }
            if (newItem is null)
                operations.Add(new() { Remove = new() { Value = id.ToString("D") } });
            else if (oldItem is null)
                operations.Add(new() { Create = Any.Pack(newItem) });
            else
                operations.Add(new() { Update = Any.Pack(newItem) });
        }
        // Remove parents before child groups, and all groups before their
        // potentially deleted members. Surviving objects are not deleted.
        var removals = new List<SchematicItemOperation>();
        while (removedGroups.Count != 0)
        {
            var children = removedGroups.Values.SelectMany(group => group.Items).Select(member => Guid.Parse(member.Value)).ToHashSet();
            var ready = removedGroups.Where(pair => !children.Contains(pair.Key)).ToArray();
            if (ready.Length == 0) throw Invalid("Group removal dependencies contain a cycle.");
            foreach (var (id, _) in ready)
            {
                removals.Add(new() { Remove = new() { Value = id.ToString("D") } });
                removedGroups.Remove(id);
            }
        }
        operations.InsertRange(0, removals);
        operations.InsertRange(0, detachments);
        // Screen items exist before groups; nested groups exist before parents.
        // Validate already excluded cycles, so each pass must consume a leaf.
        while (createdGroups.Count != 0)
        {
            var ready = createdGroups.Where(pair => pair.Value.Items.All(member =>
                !createdGroups.ContainsKey(Guid.Parse(member.Value)))).ToArray();
            if (ready.Length == 0) throw Invalid("Group creation dependencies contain a cycle.");
            foreach (var (id, group) in ready)
            {
                operations.Add(new() { Create = Any.Pack(group) });
                createdGroups.Remove(id);
            }
        }
        while (updatedGroups.Count != 0)
        {
            var ready = updatedGroups.Where(pair => pair.Value.Items.All(member =>
                !updatedGroups.ContainsKey(Guid.Parse(member.Value)))).ToArray();
            if (ready.Length == 0) throw Invalid("Group update dependencies contain a cycle.");
            foreach (var (id, group) in ready)
            {
                operations.Add(new() { Update = Any.Pack(group) });
                updatedGroups.Remove(id);
            }
        }
        // An explicit cache owns unused definitions too. When placed symbols
        // change, keep the requested cache even if its records are unchanged;
        // native per-item cache maintenance must not invent or prune entries.
        bool changesSymbols = before.Keys.Union(after.Keys).Any(id =>
            (before.GetValueOrDefault(id) is SchematicSymbolInstance
                || after.GetValueOrDefault(id) is SchematicSymbolInstance)
            && !Equals(before.GetValueOrDefault(id), after.GetValueOrDefault(id)));
        if (cacheChange is not null || changesSymbols)
        {
            var state = new SchematicLibraryCacheState { ScreenId = current.Metadata.ScreenId.Clone() };
            state.Definitions.Add(desired.CachedSymbols.OrderBy(c => c.CacheKey, StringComparer.Ordinal)
                .Select(c => c.Clone()));
            operations.Add(new SchematicItemOperation { ReplaceLibraryCache = state });
        }
        return operations;
    }

    private static void ValidateSheetDelta(SheetSymbol? before, SheetSymbol? after,
        IReadOnlyDictionary<Guid, IMessage> current, SheetPath target, IReadOnlySet<Guid> newSheetIds)
    {
        var sheet = after ?? before!;
        if (sheet.ChildScreenId is null)
            throw Invalid("Sheet edits require the persistent identity of the referenced child screen.");
        Identity(sheet.ChildScreenId);
        if (sheet.Path is null || !sheet.Path.Equals(target))
            throw Invalid("The sheet symbol must belong to the explicitly targeted parent sheet instance.");
        if (string.IsNullOrWhiteSpace(sheet.FilenameField?.Text?.Text_))
            throw Invalid("A sheet symbol requires a declared child schematic filename.");
        if (before is not null && after is not null)
        {
            if (!Equals(before.ChildScreenId, after.ChildScreenId)
                || before.FilenameField?.Text?.Text_ != after.FilenameField?.Text?.Text_)
                throw Invalid("Relinking a sheet to different child content requires a document-level hierarchy delta.");
        }
        else if (before is null && after is not null)
        {
            // A single-screen snapshot contains references, not child contents.
            // It can add an instance of a known child, but must never invent an
            // empty replacement for an unrepresented child design.
            if (!newSheetIds.Contains(Identity(after.Id)) && !current.Values.OfType<SheetSymbol>().Any(known =>
                    Equals(known.ChildScreenId, after.ChildScreenId)
                    && known.FilenameField?.Text?.Text_ == after.FilenameField.Text.Text_))
                throw Invalid("Creating a new child schematic requires its contents in a document-level hierarchy delta.");
        }
    }

    private static void ValidateFormat(SchematicMetadata metadata)
    {
        if (metadata is null) throw Invalid("Missing schematic metadata.");
        if (metadata.LoadedNativeFormatVersion > SupportedWriterFormatVersion
            || metadata.WriterNativeFormatVersion > SupportedWriterFormatVersion)
            throw new AutomationException("unsupported_native_format", "The schematic format is newer than this pinned integration supports.");
        if (metadata.WriterNativeFormatVersion != 0 && metadata.LoadedNativeFormatVersion > metadata.WriterNativeFormatVersion)
            throw new AutomationException("invalid_native_format", "The loaded format is newer than the declared native writer.");
        // Zero is unknown provenance, not compatibility proof. Legacy DTO-only
        // plans remain inspectable; no plan authorizes live native mutation.
    }

    private static SchematicItemOperation? CacheOperation(SchematicScreenData current, SchematicScreenData desired)
    {
        static Dictionary<string, SchematicCachedSymbol> IndexCache(SchematicScreenData screen)
        {
            var result = new Dictionary<string, SchematicCachedSymbol>(StringComparer.Ordinal);
            foreach (var entry in screen.CachedSymbols)
            {
                if (string.IsNullOrEmpty(entry.CacheKey) || entry.CacheKey.Contains('\0')
                    || entry.Definition is null || !result.TryAdd(entry.CacheKey, entry))
                    throw Invalid("Cached symbol definitions require unique nonempty exact keys and a definition.");
            }
            return result;
        }
        var before = IndexCache(current);
        var after = IndexCache(desired);
        if (before.Count != after.Count || before.Any(entry =>
                !after.TryGetValue(entry.Key, out var value) || !entry.Value.Equals(value)))
        {
            var state = new SchematicLibraryCacheState { ScreenId = current.Metadata.ScreenId.Clone() };
            state.Definitions.Add(after.Values.OrderBy(c => c.CacheKey, StringComparer.Ordinal).Select(c => c.Clone()));
            return new SchematicItemOperation { ReplaceLibraryCache = state };
        }
        return null;
    }

    private static List<SchematicItemOperation> MetadataOperations(SchematicMetadata current, SchematicMetadata desired)
    {
        if (desired is null) throw Invalid("Missing desired sheet metadata.");
        var remainder = desired.Clone();
        remainder.EmbeddedFiles = current.EmbeddedFiles?.Clone();
        remainder.EmbeddedFonts = current.EmbeddedFonts;
        remainder.RootInstance = current.RootInstance?.Clone();
        remainder.TitleBlock = current.TitleBlock?.Clone();
        remainder.Page = current.Page?.Clone();
        remainder.BusAliases.Clear(); remainder.BusAliases.Add(current.BusAliases);
        remainder.TextVariables.Clear(); remainder.TextVariables.Add(current.TextVariables);
        remainder.NetChains.Clear(); remainder.NetChains.Add(current.NetChains);
        remainder.VariantDescriptions.Clear(); remainder.VariantDescriptions.Add(current.VariantDescriptions);
        remainder.DrawingRatios = current.DrawingRatios?.Clone();
        remainder.Formatting = current.Formatting?.Clone();
        remainder.Annotation = current.Annotation?.Clone();
        remainder.ErcSettings = current.ErcSettings?.Clone();
        remainder.NetChainClasses = current.NetChainClasses?.Clone();
        if (!current.Equals(remainder))
            throw Invalid("Unsupported settings or document identity changed; those changes cannot be discarded.");
        var operations = new List<SchematicItemOperation>();
        if (current.Annotation is not null || desired.Annotation is not null)
        {
            var before = current.Annotation ?? throw Invalid("The native peer did not capture annotation policy.");
            var after = desired.Annotation ?? throw Invalid("Annotation policy cannot be removed or inferred from defaults.");
            SchematicAnnotation.Validate(before);
            SchematicAnnotation.Validate(after);
            if (!before.Equals(after)) operations.Add(new() { SetAnnotation = after.Clone() });
        }
        if (current.NetChainClasses is not null || desired.NetChainClasses is not null)
        {
            var before = current.NetChainClasses ?? throw Invalid("The native peer did not capture the net-chain class registry.");
            var after = desired.NetChainClasses ?? throw Invalid("The class registry cannot be removed or inferred from defaults.");
            if (!SchematicNetChainClasses.Same(before, after))
                operations.Add(new() { ReplaceNetChainClasses = SchematicNetChainClasses.Normalize(after) });
        }
        if (current.ErcSettings is not null || desired.ErcSettings is not null)
        {
            var beforeErc = current.ErcSettings ?? throw Invalid("The native peer did not capture an ERC policy catalogue.");
            var afterErc = desired.ErcSettings ?? throw Invalid("ERC settings cannot be removed or inferred from defaults.");
            var rules = beforeErc.RuleSeverities.Select(rule => rule.RuleType).ToHashSet();
            var before = SchematicErcSettingsValidation.Normalize(beforeErc, rules);
            var after = SchematicErcSettingsValidation.Normalize(afterErc, rules);
            if (!before.Equals(after)) operations.Add(new() { SetErcSettings = after });
        }
        if (!Equals(current.Formatting, desired.Formatting))
        {
            var formatting = desired.Formatting ?? throw Invalid("Project formatting cannot be removed or inferred from application defaults.");
            SchematicFormatting.Validate(formatting);
            operations.Add(new() { SetFormatting = formatting.Clone() });
        }
        if (!Equals(current.DrawingRatios, desired.DrawingRatios))
        {
            var ratios = desired.DrawingRatios ?? throw Invalid("Drawing ratios cannot be removed or inferred from local defaults.");
            double[] values = [ratios.DashLengthRatio, ratios.GapLengthRatio, ratios.TextOffsetRatio,
                ratios.LabelSizeRatio, ratios.OverbarHeightRatio];
            if (values.Any(value => !double.IsFinite(value) || value < 0)
                || ratios.DashLengthRatio + ratios.GapLengthRatio <= 0
                || ratios.TextOffsetRatio > 2 || ratios.LabelSizeRatio > 2)
                throw Invalid("Drawing ratios must be finite and nonnegative, dash plus gap positive, text/label ratios at most 2.");
            operations.Add(new() { SetDrawingRatios = ratios.Clone() });
        }
        if (!current.VariantDescriptions.Equals(desired.VariantDescriptions))
        {
            if (desired.VariantDescriptions.Any(v => v.Key.Length == 0 || v.Key.Trim() != v.Key
                || v.Key.Contains('\0') || v.Value.Contains('\0')))
                throw Invalid("Variant names must be nonempty and trimmed; names/descriptions cannot contain NUL.");
            var registry = new SchematicVariantRegistryState(); registry.Descriptions.Add(desired.VariantDescriptions);
            operations.Add(new() { ReplaceVariantRegistry = registry });
        }
        if (!current.NetChains.Equals(desired.NetChains))
        {
            var state = new SchematicNetChainState();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chain in desired.NetChains)
            {
                bool NoNul(string text) => !text.Contains('\0');
                if (chain.Name.Length == 0 || chain.Name.IndexOfAny(['"', '\'', '(', ')', ' ', '\t', '\r', '\n', '\0']) >= 0
                    || !names.Add(chain.Name))
                    throw Invalid("Net chain names must be valid and unique.");
                var prior = current.NetChains.SingleOrDefault(c => c.Name == chain.Name);
                if (chain.Committed != (prior?.Committed ?? false))
                    throw Invalid("Net chain committed membership is computed, not writable.");
                if (!NoNul(chain.From?.Reference ?? "") || !NoNul(chain.From?.Pin ?? "")
                    || !NoNul(chain.To?.Reference ?? "") || !NoNul(chain.To?.Pin ?? "") || !NoNul(chain.NetClass))
                    throw Invalid("Net chain fields cannot contain NUL.");
                if (chain.Color is { } color && new[] { color.R, color.G, color.B, color.A }
                    .Any(v => !double.IsFinite(v) || v < 0 || v > 1))
                    throw Invalid("Net chain color channels must be finite values from zero to one.");
                if (chain.MemberNets.Any(n => n.Length == 0 || !NoNul(n) || n.StartsWith("__SG_", StringComparison.Ordinal))
                    || chain.MemberNets.Distinct(StringComparer.Ordinal).Count() != chain.MemberNets.Count)
                    throw Invalid("Net chain member nets must be unique nonempty persisted names.");
                if (prior?.Exclusions is { } priorExclusions && (priorExclusions.NetNames.Count != 0 || priorExclusions.Pins.Count != 0)
                    && chain.Exclusions is null)
                    throw Invalid("Removal restrictions cannot be cleared by an older or incomplete representation.");
                if (chain.Exclusions is { } exclusions && (exclusions.NetNames.Any(n => n.Length == 0 || !NoNul(n)
                    || n.StartsWith("__SG_", StringComparison.Ordinal) || chain.MemberNets.Contains(n))
                    || exclusions.NetNames.Distinct(StringComparer.Ordinal).Count() != exclusions.NetNames.Count))
                    throw Invalid("Excluded net names must be unique persisted names disjoint from retained members.");
                if (chain.Exclusions is { } anchored)
                {
                    bool ExactId(string? text) => Guid.TryParseExact(text, "D", out var id)
                        && id != Guid.Empty && id.ToString("D") == text;
                    var identities = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var anchor in anchored.Pins)
                        if (anchor.Pin is null || !ExactId(anchor.Pin.Value) || anchor.Path is null || anchor.Path.Path.Count == 0
                            || anchor.Path.Path.Any(id => !ExactId(id.Value))
                            || !identities.Add(string.Join('/', anchor.Path.Path.Select(p => p.Value).Append(anchor.Pin.Value))))
                            throw Invalid("Excluded pins require unique exact UUIDs and nonempty sheet paths.");
                }
                SchematicDataXml.Write(chain);
                var declaration = chain.Clone(); declaration.Committed = false;
                state.Definitions.Add(declaration);
            }
            operations.Add(new() { ReplaceNetChains = state });
        }
        if (!current.TextVariables.Equals(desired.TextVariables))
        {
            if (desired.TextVariables.Any(v => v.Key.Length == 0 || v.Key.Contains('\0') || v.Value.Contains('\0')))
                throw Invalid("Text variable names must be nonempty and names/values cannot contain NUL.");
            var variables = new SchematicTextVariableState(); variables.Variables.Add(desired.TextVariables);
            operations.Add(new() { ReplaceTextVariables = variables });
        }
        if (!current.BusAliases.Equals(desired.BusAliases))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alias in desired.BusAliases)
            {
                bool Valid(string value) => value.Length != 0 && value == value.Trim() && !value.Contains('\0');
                if (!Valid(alias.Name) || alias.Members.Any(member => !Valid(member)) || !seen.Add(alias.Name))
                    throw Invalid("Bus aliases require trimmed nonempty names/members and unique names so project saving preserves every definition.");
                SchematicDataXml.Write(alias); // Reject unrepresented fields before native mutation.
            }
            var state = new SchematicBusAliasState(); state.Aliases.Add(desired.BusAliases.Select(a => a.Clone()));
            operations.Add(new() { ReplaceBusAliases = state });
        }
        if (!Equals(current.TitleBlock, desired.TitleBlock))
        {
            if (current.TitleBlock is null || desired.TitleBlock is null)
                throw Invalid("Title changes require explicit records; use an empty record to clear the title block.");
            operations.Add(new() { SetTitleBlock = desired.TitleBlock.Clone() });
        }
        if (!Equals(current.EmbeddedFiles, desired.EmbeddedFiles) || current.EmbeddedFonts != desired.EmbeddedFonts)
        {
            if (current.EmbeddedFiles is null || desired.EmbeddedFiles is null)
                throw Invalid("Asset replacement requires explicit observed and desired file collections.");
            operations.Add(new() { ReplaceEmbeddedFiles = new()
                { Files = desired.EmbeddedFiles.Clone(), EmbeddedFonts = desired.EmbeddedFonts } });
        }
        // A new drawing sheet may be supplied by this batch's embedded assets.
        if (!Equals(current.Page, desired.Page))
        {
            if (current.Page is null || desired.Page is null)
                throw Invalid("Page changes require explicit observed and desired settings.");
            operations.Add(new() { SetPageSettings = desired.Page.Clone() });
        }
        if (!Equals(current.RootInstance, desired.RootInstance))
        {
            if (current.RootInstance is null || desired.RootInstance is null)
                throw Invalid("Root-page changes require explicit records; use an empty record to remove its page number.");
            operations.Add(new() { SetRootInstance = desired.RootInstance.Clone() });
        }
        return operations;
    }

    internal static Dictionary<Guid, IMessage> Index(IEnumerable<Any> items)
    {
        var result = new Dictionary<Guid, IMessage>();
        foreach (var item in items)
        {
            var descriptor = ItemTypes.SingleOrDefault(d => item.TypeUrl == "type.googleapis.com/" + d.FullName)
                ?? throw Invalid("Unsupported top-level schematic item type: " + item.TypeUrl);
            IMessage decoded;
            try { decoded = descriptor.Parser.ParseFrom(item.Value); }
            catch (InvalidProtocolBufferException error) { throw Invalid("Invalid native item: " + error.Message); }
            var labelText = decoded switch
            {
                LocalLabel label => label.Text,
                GlobalLabel label => label.Text,
                HierarchicalLabel label => label.Text,
                DirectiveLabel label => label.Text,
                _ => null
            };
            if (labelText?.Attributes?.Multiline == true)
                throw Invalid("Schematic labels must be single-line; multiline label state cannot survive native undo.");
            if (decoded is SchematicSymbolInstance symbol && symbol.DefinitionPinNameOffset is { } offset
                && (offset.ValueNm % 100 != 0 || offset.ValueNm < (long)int.MinValue * 100
                    || offset.ValueNm > (long)int.MaxValue * 100))
                throw Invalid("Owned symbol pin-name offsets must be exactly representable on the native 100 nm grid.");
            var field = descriptor.FindFieldByName("id");
            var id = Identity(field?.Accessor.GetValue(decoded) as KIID);
            if (!result.TryAdd(id, decoded)) throw Invalid("Duplicate native object identity.");
        }
        return result;
    }

    private static Guid Identity(KIID? id) => Guid.TryParseExact(id?.Value, "D", out var value)
        && value != Guid.Empty && id.Value == value.ToString("D") ? value
        : throw Invalid("Canonical non-empty native identities are required.");
    private static AutomationException Invalid(string message) => new("unsupported_schematic_delta", message);
}
