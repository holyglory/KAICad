using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

// Null object state means deletion/absence, not an omitted conflict version.
public sealed record SchematicItemConflict(Guid? ObjectId, string Reason,
    Any? Baseline, Any? Xml, Any? Native, string? CacheKey = null);
public enum SchematicConflictChoice { Xml, Native, Baseline }
public sealed record SchematicItemMergeResult(SchematicScreenData? Merged,
    IReadOnlyList<SchematicItemOperation> NativeOperations,
    IReadOnlyList<SchematicItemConflict> Conflicts, string? UnsupportedChange = null)
{
    public bool CanApply => Merged is not null && Conflicts.Count == 0 && UnsupportedChange is null;
}

/// <summary>Three-way merge against the last synchronized
/// state. Pure planning: no file writes, live edits, or conflict auto-resolution.</summary>
public static class SchematicItemMerge
{
    /// <summary>Resolve explicit object conflicts in the supplied snapshot only.
    /// The caller must retain that snapshot and revalidate the live revision
    /// before applying the resulting operations.</summary>
    public static SchematicItemMergeResult Resolve(SchematicScreenData baseline,
        SchematicScreenData xml, SchematicScreenData native,
        IReadOnlyDictionary<Guid, SchematicConflictChoice> choices,
        IReadOnlyDictionary<string, SchematicConflictChoice>? netChainChoices = null,
        IReadOnlyDictionary<string, SchematicConflictChoice>? variantChoices = null,
        IReadOnlyDictionary<string, SchematicConflictChoice>? cacheChoices = null)
    {
        foreach (var state in new[] { baseline, xml, native })
            SchematicItemDelta.PlanWithNewSheets(state, state, new HashSet<Guid>(),
                ReferenceEquals(state, xml) ? baseline.Metadata : null);
        var adjustedBaseline = baseline.Clone();
        var adjustedXml = xml.Clone();
        foreach (var (key, choice) in cacheChoices ?? new Dictionary<string, SchematicConflictChoice>())
        {
            SchematicCachedSymbol? Find(SchematicScreenData state) => state.CachedSymbols.SingleOrDefault(c => c.CacheKey == key);
            var b = Find(baseline); var x = Find(xml); var n = Find(native);
            if (!System.Enum.IsDefined(choice) || Choose(b, x, n, out _))
                throw new AutomationException("invalid_sync_resolution", "Choose an existing conflicting cache definition by its exact key.");
            void Set(SchematicScreenData state, SchematicCachedSymbol? value)
            {
                var entries = state.CachedSymbols.Where(c => c.CacheKey != key).Select(c => c.Clone()).ToList();
                if (value is not null) entries.Add(value.Clone());
                state.CachedSymbols.Clear();
                state.CachedSymbols.Add(entries.OrderBy(c => c.CacheKey, StringComparer.Ordinal));
            }
            Set(adjustedBaseline, n);
            Set(adjustedXml, choice switch
            {
                SchematicConflictChoice.Xml => x,
                SchematicConflictChoice.Native => n,
                _ => b
            });
        }
        foreach (var (name, choice) in variantChoices ?? new Dictionary<string, SchematicConflictChoice>())
        {
            string? Find(SchematicMetadata metadata) => metadata.VariantDescriptions.TryGetValue(name, out var value) ? value : null;
            var b = Find(baseline.Metadata); var x = Find(xml.Metadata); var n = Find(native.Metadata);
            if (!System.Enum.IsDefined(choice) || Choose(b, x, n, out _))
                throw new AutomationException("invalid_sync_resolution", "Choose an existing conflicting project variant by its exact name.");
            void Set(SchematicMetadata metadata, string? value)
            {
                metadata.VariantDescriptions.Remove(name);
                if (value is not null) metadata.VariantDescriptions.Add(name, value);
            }
            Set(adjustedBaseline.Metadata, n);
            Set(adjustedXml.Metadata, choice switch
            {
                SchematicConflictChoice.Xml => x,
                SchematicConflictChoice.Native => n,
                _ => b
            });
        }
        if (variantChoices?.Count > 0)
        {
            SchematicVariantProjection.Reproject(adjustedBaseline);
            SchematicVariantProjection.Reproject(adjustedXml);
        }
        foreach (var (name, choice) in netChainChoices ?? new Dictionary<string, SchematicConflictChoice>())
        {
            if (!System.Enum.IsDefined(choice)) throw InvalidChainResolution();
            SchematicNetChainDefinition? Find(SchematicMetadata metadata)
            {
                var matches = metadata.NetChains.Where(c => c.Name == name).ToArray();
                if (matches.Length > 1) throw InvalidChainResolution();
                return matches.SingleOrDefault();
            }
            var b = Find(baseline.Metadata); var x = Find(xml.Metadata); var n = Find(native.Metadata);
            SchematicNetChainDefinition? Declaration(SchematicNetChainDefinition? value)
            {
                if (value is null) return null;
                var copy = value.Clone(); copy.Committed = false; return copy;
            }
            if (x is not null && x.Committed != (b?.Committed ?? false)) throw InvalidChainResolution();
            if (Choose(Declaration(b), Declaration(x), Declaration(n), out _)) throw InvalidChainResolution();
            void Set(SchematicMetadata metadata, SchematicNetChainDefinition? value)
            {
                var retained = metadata.NetChains.Where(c => c.Name != name).Select(c => c.Clone()).ToList();
                if (value is not null)
                {
                    var selected = value.Clone(); selected.Committed = n?.Committed ?? false;
                    retained.Add(selected);
                }
                metadata.NetChains.Clear(); metadata.NetChains.Add(retained.OrderBy(c => c.Name, StringComparer.Ordinal));
            }
            Set(adjustedBaseline.Metadata, n);
            Set(adjustedXml.Metadata, choice switch
            {
                SchematicConflictChoice.Xml => x,
                SchematicConflictChoice.Native => n,
                _ => b
            });
        }
        var pending = Plan(adjustedBaseline, adjustedXml, native);
        var objectConflicts = pending;
        if (choices.Count != 0 && pending.Conflicts.Any(c => c.Reason == "metadata_changed"))
        {
            // Inspect object conflicts independently; unresolved metadata must
            // not force a choice order. This common metadata is used only for
            // enumeration, never as the final merge or a replacement source.
            var objectsBefore = adjustedBaseline.Clone(); objectsBefore.Metadata = native.Metadata.Clone();
            var objectsXml = adjustedXml.Clone(); objectsXml.Metadata = native.Metadata.Clone();
            SchematicVariantProjection.Reproject(objectsBefore);
            SchematicVariantProjection.Reproject(objectsXml);
            objectConflicts = Plan(objectsBefore, objectsXml, native);
        }
        var selectable = objectConflicts.Conflicts.Where(c => c.ObjectId.HasValue
            && c.Reason is "competing_edit" or "competing_creation" or "delete_modify")
            .ToDictionary(c => c.ObjectId!.Value);
        foreach (var (id, choice) in choices)
            if (!selectable.ContainsKey(id) || !System.Enum.IsDefined(choice))
                throw new AutomationException("invalid_sync_resolution",
                    "Choose an existing editable object conflict from the current snapshot.");

        foreach (var (id, choice) in choices)
        {
            var conflict = selectable[id];
            // Rebase only this explicit decision against the observed native
            // version. Other objects retain their original three-way history.
            Replace(adjustedBaseline, id, conflict.Native);
            Replace(adjustedXml, id, choice switch
            {
                SchematicConflictChoice.Xml => conflict.Xml,
                SchematicConflictChoice.Native => conflict.Native,
                _ => conflict.Baseline
            });
        }
        if (choices.Count > 0)
        {
            // Object conflict snapshots may use common metadata solely for
            // enumeration. Rebind their redundant descriptions to each real
            // working registry; never carry the enumeration registry forward.
            SchematicVariantProjection.Reproject(adjustedBaseline);
            SchematicVariantProjection.Reproject(adjustedXml);
        }
        return Plan(adjustedBaseline, adjustedXml, native);
    }

    private static AutomationException InvalidChainResolution() => new("invalid_sync_resolution",
        "Choose an existing competing net-chain declaration by its exact name; computed membership is not writable.");

    private static void Replace(SchematicScreenData state, Guid id, Any? replacement)
    {
        var indexed = SchematicItemDelta.Index(state.Items);
        indexed.Remove(id);
        state.Items.Clear();
        foreach (var item in indexed.OrderBy(pair => pair.Key)) state.Items.Add(Any.Pack(item.Value));
        if (replacement is not null) state.Items.Add(replacement.Clone());
    }

    public static SchematicItemMergeResult Plan(SchematicScreenData baseline,
        SchematicScreenData xml, SchematicScreenData native) => PlanWithNewSheets(baseline, xml, native, new HashSet<Guid>());

    internal static SchematicItemMergeResult PlanWithNewSheets(SchematicScreenData baseline,
        SchematicScreenData xml, SchematicScreenData native, IReadOnlySet<Guid> newSheetIds)
    {
        // Validate even the unchanged states. Unsupported/unknown content must
        // not be interpreted as absent simply because no edit was requested.
        foreach (var state in new[] { baseline, xml, native })
            SchematicItemDelta.PlanWithNewSheets(state, state, new HashSet<Guid>(),
                ReferenceEquals(state, xml) ? baseline.Metadata : null);
        // A native-only or convergent target change is not a metadata edit.
        // Repeated sheets can share a screen UUID but have different paths.
        foreach (var version in new[] { xml.Metadata, native.Metadata })
            if (!baseline.Metadata.ScreenId.Equals(version.ScreenId)
                || !baseline.Metadata.Document.Equals(version.Document))
                return new(null, [], [new(null, "target_changed", Any.Pack(baseline.Metadata),
                    Any.Pack(xml.Metadata), Any.Pack(native.Metadata))]);
        var chosenMetadata = MergeMetadata(baseline.Metadata, xml.Metadata, native.Metadata);
        if (chosenMetadata is null)
            return new(null, [], [new(null, "metadata_changed", Any.Pack(baseline.Metadata),
                Any.Pack(xml.Metadata), Any.Pack(native.Metadata))]);

        var metadataCandidate = native.Clone(); metadataCandidate.Metadata = chosenMetadata.Clone();
        try { SchematicItemDelta.Plan(native, metadataCandidate); }
        catch (AutomationException error) when (error.Code == "unsupported_schematic_delta")
        {
            return new(null, [], [new(null, "metadata_changed", Any.Pack(baseline.Metadata),
                Any.Pack(xml.Metadata), Any.Pack(native.Metadata))], error.Message);
        }

        var before = SchematicItemDelta.Index(baseline.Items);
        var desired = SchematicItemDelta.Index(xml.Items);
        var current = SchematicItemDelta.Index(native.Items);
        var conflicts = new List<SchematicItemConflict>();
        var merged = native.Clone(); merged.Items.Clear();
        merged.Metadata = chosenMetadata.Clone();
        merged.CachedSymbols.Clear();
        var cacheBefore = baseline.CachedSymbols.ToDictionary(c => c.CacheKey, StringComparer.Ordinal);
        var cacheXml = xml.CachedSymbols.ToDictionary(c => c.CacheKey, StringComparer.Ordinal);
        var cacheNative = native.CachedSymbols.ToDictionary(c => c.CacheKey, StringComparer.Ordinal);
        foreach (var key in cacheBefore.Keys.Union(cacheXml.Keys).Union(cacheNative.Keys).Order(StringComparer.Ordinal))
        {
            var b = cacheBefore.GetValueOrDefault(key); var x = cacheXml.GetValueOrDefault(key);
            var n = cacheNative.GetValueOrDefault(key);
            if (!Choose(b, x, n, out var selected))
                conflicts.Add(new(null, "cache_definition_changed", Pack(b), Pack(x), Pack(n), key));
            else if (selected is not null) merged.CachedSymbols.Add(selected.Clone());
        }
        foreach (Guid id in before.Keys.Union(desired.Keys).Union(current.Keys).Order())
        {
            before.TryGetValue(id, out var original);
            desired.TryGetValue(id, out var xmlItem);
            current.TryGetValue(id, out var nativeItem);
            if (original is not null && ((xmlItem is not null && xmlItem.Descriptor != original.Descriptor)
                || (nativeItem is not null && nativeItem.Descriptor != original.Descriptor)))
            {
                conflicts.Add(new(id, "object_type_changed", Pack(original), Pack(xmlItem), Pack(nativeItem)));
                continue;
            }
            IMessage? chosen;
            if (SchematicVariantProjection.Equivalent(xmlItem, nativeItem)) chosen = xmlItem;
            else if (SchematicVariantProjection.Equivalent(original, xmlItem)) chosen = nativeItem;
            else if (SchematicVariantProjection.Equivalent(original, nativeItem)) chosen = xmlItem;
            else if (original is SchematicText originalNote && xmlItem is SchematicText xmlNote
                && nativeItem is SchematicText nativeNote
                && MergeNote(originalNote, xmlNote, nativeNote) is { } note) chosen = note;
            else
            {
                conflicts.Add(new(id, original is null ? "competing_creation"
                    : xmlItem is null || nativeItem is null ? "delete_modify" : "competing_edit",
                    Pack(original), Pack(xmlItem), Pack(nativeItem)));
                continue;
            }
            if (chosen is not null) merged.Items.Add(Any.Pack(SchematicVariantProjection.Reproject(chosen, chosenMetadata)));
        }
        // Never return a partially applicable candidate when any object conflicts.
        if (conflicts.Count != 0) return new(null, [], conflicts);
        try { return new(merged, SchematicItemDelta.PlanWithNewSheets(native, merged, newSheetIds), []); }
        catch (AutomationException error) when (error.Code == "unsupported_schematic_delta")
        {
            return new(null, [], [], error.Message);
        }
    }

    private static Any? Pack(IMessage? item) => item is null ? null : Any.Pack(item);

    private static SchematicText? MergeNote(SchematicText baseline, SchematicText xml, SchematicText native)
    {
        if (baseline.Text is null || xml.Text is null || native.Text is null) return null;
        // Wording and its link are one semantic unit. Position plus typography
        // stay together: merging individual coordinates or angle/alignment can
        // invent a placement neither editor chose. The remaining object fields
        // (including locks and custom properties) are a third ownership unit.
        SchematicText Remainder(SchematicText value)
        {
            var copy = value.Clone(); copy.Text = null; return copy;
        }
        Kiapi.Common.Types.Text Presentation(SchematicText value)
        {
            var copy = value.Text.Clone(); copy.Text_ = ""; copy.Hyperlink = ""; return copy;
        }
        if (!Choose((baseline.Text.Text_, baseline.Text.Hyperlink),
                (xml.Text.Text_, xml.Text.Hyperlink), (native.Text.Text_, native.Text.Hyperlink), out var content)
            || !Choose(Presentation(baseline), Presentation(xml), Presentation(native), out var presentation)
            || !Choose(Remainder(baseline), Remainder(xml), Remainder(native), out var remainder)) return null;
        var result = remainder.Clone(); result.Text = presentation.Clone();
        result.Text.Text_ = content.Item1; result.Text.Hyperlink = content.Item2;
        return result;
    }

    private static SchematicMetadata? MergeMetadata(SchematicMetadata baseline,
        SchematicMetadata xml, SchematicMetadata native)
    {
        // Assets plus their font setting are one ownership unit. Root-page state
        // is independent. Variables merge by exact key; bus aliases form another
        // ownership unit. Other settings stay grouped until their native deltas
        // and cross-field invariants are supported.
        SchematicMetadata Remainder(SchematicMetadata metadata)
        {
            var copy = metadata.Clone(); copy.EmbeddedFiles = null;
            copy.EmbeddedFonts = false; copy.RootInstance = null; copy.TitleBlock = null; copy.Page = null;
            copy.TextVariables.Clear(); copy.BusAliases.Clear(); copy.NetChains.Clear();
            copy.VariantDescriptions.Clear(); copy.DrawingRatios = null; copy.Formatting = null; copy.ErcSettings = null;
            copy.NetChainClasses = null; copy.Annotation = null; return copy;
        }
        var assetsBefore = (baseline.EmbeddedFiles, baseline.EmbeddedFonts);
        var assetsXml = (xml.EmbeddedFiles, xml.EmbeddedFonts);
        var assetsNative = (native.EmbeddedFiles, native.EmbeddedFonts);
        var variables = MergeTextVariables(baseline.TextVariables, xml.TextVariables, native.TextVariables);
        var chains = MergeNetChains(baseline.NetChains, xml.NetChains, native.NetChains);
        var variants = MergeTextVariables(baseline.VariantDescriptions, xml.VariantDescriptions, native.VariantDescriptions);
        if (variables is null || chains is null || variants is null) return null;
        if (!Choose(Remainder(baseline), Remainder(xml), Remainder(native), out var rest)
            || !Choose(baseline.RootInstance, xml.RootInstance, native.RootInstance, out var root)
            || !Choose(baseline.TitleBlock, xml.TitleBlock, native.TitleBlock, out var title)
            || !Choose(baseline.Page, xml.Page, native.Page, out var page)
            || !Choose(baseline.DrawingRatios, xml.DrawingRatios, native.DrawingRatios, out var drawing)
            || !MergeFormatting(baseline.Formatting, xml.Formatting, native.Formatting, out var formatting)
            || !MergeAnnotation(baseline.Annotation, xml.Annotation, native.Annotation, out var annotation)
            || !MergeErc(baseline.ErcSettings, xml.ErcSettings, native.ErcSettings, out var erc)
            || !SchematicNetChainClasses.Merge(baseline.NetChainClasses, xml.NetChainClasses, native.NetChainClasses, out var chainClasses)
            || !Choose(baseline.BusAliases, xml.BusAliases, native.BusAliases, out var aliases)
            || !Choose(assetsBefore, assetsXml, assetsNative, out var assets)) return null;
        var result = rest.Clone(); result.RootInstance = root?.Clone();
        result.TitleBlock = title?.Clone();
        result.Page = page?.Clone();
        result.DrawingRatios = drawing?.Clone();
        result.Formatting = formatting?.Clone();
        result.Annotation = annotation?.Clone();
        result.ErcSettings = erc?.Clone();
        result.NetChainClasses = chainClasses?.Clone();
        result.EmbeddedFiles = assets.Item1?.Clone(); result.EmbeddedFonts = assets.Item2;
        result.TextVariables.Add(variables);
        result.BusAliases.Add(aliases.Select(alias => alias.Clone()));
        result.NetChains.Add(chains);
        result.VariantDescriptions.Add(variants);
        return result;
    }

    private static RepeatedField<SchematicNetChainDefinition>? MergeNetChains(
        RepeatedField<SchematicNetChainDefinition> baseline,
        RepeatedField<SchematicNetChainDefinition> xml,
        RepeatedField<SchematicNetChainDefinition> native)
    {
        // Names are the exact persisted keys, not a similarity-based match.
        // A rename is deletion plus creation; competing declarations remain
        // atomic because their endpoints and member nets describe one intent.
        Dictionary<string, SchematicNetChainDefinition>? Index(RepeatedField<SchematicNetChainDefinition> values)
        {
            var indexed = new Dictionary<string, SchematicNetChainDefinition>(StringComparer.Ordinal);
            foreach (var value in values)
                if (!indexed.TryAdd(value.Name, value)) return null;
            return indexed;
        }
        SchematicNetChainDefinition? Declaration(SchematicNetChainDefinition? value)
        {
            if (value is null) return null;
            var copy = value.Clone(); copy.Committed = false; return copy;
        }
        var before = Index(baseline); var fromXml = Index(xml); var fromNative = Index(native);
        if (before is null || fromXml is null || fromNative is null) return null;
        var result = new RepeatedField<SchematicNetChainDefinition>();
        foreach (string name in before.Keys.Concat(fromXml.Keys).Concat(fromNative.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(name, out var b); fromXml.TryGetValue(name, out var x); fromNative.TryGetValue(name, out var n);
            // XML cannot change the previous observation. A newer native
            // membership observation, however, must not create a false conflict.
            if (x is not null && x.Committed != (b?.Committed ?? false)) return null;
            if (!Choose(Declaration(b), Declaration(x), Declaration(n), out var chosen)) return null;
            if (chosen is null) continue;
            var merged = chosen.Clone(); merged.Committed = n?.Committed ?? false;
            result.Add(merged);
        }
        return result;
    }

    private static MapField<string, string>? MergeTextVariables(MapField<string, string> baseline,
        MapField<string, string> xml, MapField<string, string> native)
    {
        var result = new MapField<string, string>();
        foreach (string key in baseline.Keys.Concat(xml.Keys).Concat(native.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            baseline.TryGetValue(key, out var before); xml.TryGetValue(key, out var fromXml); native.TryGetValue(key, out var fromNative);
            // Absence is different from a present variable with an empty value.
            if (!Choose(before, fromXml, fromNative, out var chosen)) return null;
            if (chosen is not null) result.Add(key, chosen);
        }
        return result;
    }

    private static bool MergeAnnotation(SchematicAnnotationSettings? baseline,
        SchematicAnnotationSettings? xml, SchematicAnnotationSettings? native,
        out SchematicAnnotationSettings? result)
    {
        if (Choose(baseline, xml, native, out result)) return true;
        if (baseline is null || xml is null || native is null) return false;
        var merged = new SchematicAnnotationSettings();
        foreach (var field in SchematicAnnotationSettings.Descriptor.Fields.InFieldNumberOrder())
        {
            if (!Choose(field.Accessor.GetValue(baseline), field.Accessor.GetValue(xml),
                field.Accessor.GetValue(native), out var value)) return false;
            field.Accessor.SetValue(merged, value);
        }
        result = merged;
        return true;
    }

    private static bool MergeFormatting(SchematicFormattingSettings? baseline,
        SchematicFormattingSettings? xml, SchematicFormattingSettings? native,
        out SchematicFormattingSettings? result)
    {
        result = null;
        var fields = SchematicFormattingSettings.Descriptor.Fields.InFieldNumberOrder();
        // Plan validates all three complete snapshots before reaching this
        // field merge, including unchanged versions with unsupported content.
        if (Choose(baseline, xml, native, out result)) return true;
        if (baseline is null || xml is null || native is null) return false;
        var merged = new SchematicFormattingSettings();
        foreach (var field in fields)
        {
            if (!Choose(field.Accessor.GetValue(baseline), field.Accessor.GetValue(xml),
                field.Accessor.GetValue(native), out var value)) return false;
            field.Accessor.SetValue(merged, value);
        }
        result = merged;
        return true;
    }

    private static bool MergeErc(SchematicErcSettings? baseline, SchematicErcSettings? xml,
        SchematicErcSettings? native, out SchematicErcSettings? result)
    {
        result = null;
        if (baseline is null || xml is null || native is null)
            return Choose(baseline, xml, native, out result);
        var rules = baseline.RuleSeverities.Select(rule => rule.RuleType).ToHashSet();
        var before = SchematicErcSettingsValidation.Normalize(baseline, rules);
        var requested = SchematicErcSettingsValidation.Normalize(xml, rules);
        var observed = SchematicErcSettingsValidation.Normalize(native, rules);
        // Canonical comparison must not reorder a chosen native/XML snapshot:
        // unchanged synchronization must preserve its exact representation.
        if (requested.Equals(observed) || before.Equals(requested)) { result = native.Clone(); return true; }
        if (before.Equals(observed)) { result = xml.Clone(); return true; }
        var merged = new SchematicErcSettings();
        bool Entries<T>(IEnumerable<T> b, IEnumerable<T> x, IEnumerable<T> n, Func<T, string> key, Action<T> add)
            where T : class, IMessage<T>
        {
            var old = b.ToDictionary(key, StringComparer.Ordinal);
            var desired = x.ToDictionary(key, StringComparer.Ordinal);
            var live = n.ToDictionary(key, StringComparer.Ordinal);
            foreach (string id in n.Select(key).Concat(x.Select(key)).Concat(b.Select(key)).Distinct(StringComparer.Ordinal))
            {
                old.TryGetValue(id, out var bv); desired.TryGetValue(id, out var xv); live.TryGetValue(id, out var nv);
                if (!Choose(bv, xv, nv, out var value)) return false;
                if (value is not null) add(value.Clone());
            }
            return true;
        }
        if (!Entries(baseline.RuleSeverities, xml.RuleSeverities, native.RuleSeverities,
                rule => rule.RuleType.ToString(), merged.RuleSeverities.Add)
            || !Entries(baseline.PinMap, xml.PinMap, native.PinMap,
                cell => cell.First + "/" + cell.Second, merged.PinMap.Add)
            || !Entries(baseline.Exclusions, xml.Exclusions, native.Exclusions,
                exclusion => exclusion.Marker.ToByteString().ToBase64(), merged.Exclusions.Add)) return false;
        SchematicErcSettingsValidation.Validate(merged, rules);
        result = merged;
        return true;
    }

    private static bool Choose<T>(T baseline, T xml, T native, out T result)
    {
        if (EqualityComparer<T>.Default.Equals(xml, native)) result = xml;
        else if (EqualityComparer<T>.Default.Equals(baseline, xml)) result = native;
        else if (EqualityComparer<T>.Default.Equals(baseline, native)) result = xml;
        else { result = default!; return false; }
        return true;
    }
}
