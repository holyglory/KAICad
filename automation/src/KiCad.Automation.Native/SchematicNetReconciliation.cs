using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

public sealed record SchematicNetReconciliationResult(EngineeringDesign? Candidate,
    IReadOnlyList<PinPartitionConflict> Conflicts, IReadOnlyList<NetIdentityChange> NetChanges,
    IReadOnlyList<ElectricalBindingIssue> BindingIssues, IReadOnlyList<HierarchyCoverageGap> CoverageGaps,
    string? ErrorCode = null, string? ErrorMessage = null,
    IReadOnlyList<Guid>? RemovedSymbolOccurrences = null,
    IReadOnlyList<ComponentReferenceChange>? ComponentChanges = null,
    IReadOnlyList<Guid>? RestoredSymbolOccurrences = null, IReadOnlyList<Guid>? RestoredNetIds = null)
{
    internal SchematicNativeRestorationResult? Restoration { get; init; }
}

/// <summary>Pure three-way electrical-model reconciliation over stable exact
/// component/sheet/pin ownership. Does not apply native edits or advance recovery.</summary>
public static class SchematicNetReconciliation
{
    public static SchematicNetReconciliationResult Plan(DesignRecoveryState state, CancellationToken token = default)
        => PlanCore(state, token, null);

    internal static SchematicNetReconciliationResult Plan(DesignRecoveryState state,
        IReadOnlyList<SchematicOwnershipHistory>? history, CancellationToken token) => PlanCore(state, token, history);

    private static SchematicNetReconciliationResult PlanCore(DesignRecoveryState state, CancellationToken token,
        IReadOnlyList<SchematicOwnershipHistory>? history)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var (baseline, observed) = SchematicElectricalCheckpoints.Require(state);
            var desiredDocument = SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(state.DesiredFileBytes), state.KnowledgeLibraries);
            var desired = desiredDocument.Engineering;
            if (Topology(state.Baseline.Engineering.Circuit) != Topology(desired.Circuit)
                || Bindings(state.Baseline) != Bindings(desiredDocument))
                throw Failure("electrical_ownership_changed", "Reconcile changed component, sheet, unit or pin ownership before merging nets.");
            SchematicNativeRemovalResult? removal = null;
            SchematicNativeRestorationResult? restoration = null;
            if (NativeOwners(state.Baseline.Schematic) != NativeOwners(state.Observed))
            {
                removal = SchematicNativeRemovalProjection.Project(state.Baseline, state.Observed, state.KnowledgeLibraries, token);
                if (removal.BindingCandidate is null)
                {
                    if (removal.ErrorCode != "electrical_ownership_changed" || history is null)
                        return new(null, [], [], [], removal.CoverageGaps, removal.ErrorCode, removal.ErrorMessage);
                    restoration = SchematicNativeRestorationProjection.Project(state, history, token);
                    removal = null;
                }
                // Circuit/layout edits need their own three-way owner resolution.
                // Independent instruction changes are retained in the candidate.
                if (CircuitXml.Write(desired.Circuit) != CircuitXml.Write(state.Baseline.Engineering.Circuit))
                    throw Failure("ownership_change_with_xml_edits", "Resolve concurrent circuit or layout XML edits and native ownership changes before applying either version.");
            }
            var before = SchematicElectricalComparison.Compare(state.Baseline, baseline, state.KnowledgeLibraries, token);
            if (!before.PinBindingsComplete || !before.ConnectivityEquivalent)
                return new(null, [], [], before.Issues, before.CoverageGaps, "unaligned_electrical_baseline", "The saved baseline must agree with its native pin partition.");
            var current = SchematicElectricalComparison.Compare(restoration?.BindingCandidate ?? removal?.BindingCandidate ?? state.Baseline,
                observed, state.KnowledgeLibraries, token);
            var gaps = before.CoverageGaps.Concat(current.CoverageGaps).Distinct().ToArray();
            if (!current.PinBindingsComplete) return new(null, [], [], current.Issues, gaps, "unresolved_electrical_bindings");
            var universe = before.PinPartitions!.SelectMany(g => g.Pins).ToArray();
            // KiCad always joins the pins one placed symbol's own definition stacks at one point (decision
            // kicad-stacked-pins-one-node-20260924), so the model partitions are compared as KiCad shows them: the
            // baseline with the stacking its native baseline showed, the desired XML with the stacking KiCad shows now.
            // KiCad's own join is then no native edit and never becomes a generated net.
            IReadOnlyList<IReadOnlyList<PinEndpoint>> stackedBefore = before.StackedPins ?? [], stackedNow = current.StackedPins ?? [];
            var baselinePartition = Partition(state.Baseline.Engineering.Circuit, universe, stackedBefore);
            var desiredPartition = Partition(desired.Circuit, universe, stackedNow);
            var merged = PinPartitionEvolution.Plan(baselinePartition.Groups, desiredPartition.Groups,
                current.PinPartitions!.Select(p => p.Pins).ToArray(), token);
            if (merged.Groups is null) return new(null, merged.Conflicts, [], [], gaps);

            // Preserve explicit XML identities and semantic names for every
            // exact surviving group. Native label names are not identity keys.
            // A net is found by its own pins or by its pins with the unconnected
            // pins stacked on them, which KiCad joins to it.
            var desiredByGroup = desiredPartition.Nets;
            // Only its single pins are looked up. Stacking only joins groups, so the partition as KiCad shows it has no
            // single pin the plain partition lacks.
            var baselineGroups = Partition(state.Baseline.Engineering.Circuit, universe, []).Groups
                .Select(Key).ToHashSet(StringComparer.Ordinal);
            var stackedNodes = stackedNow.Select(Key).ToHashSet(StringComparer.Ordinal);
            var nativeByGroup = current.PinPartitions!.ToDictionary(p => Key(p.Pins), StringComparer.Ordinal);
            var desiredIds = desired.Circuit.Nets.Select(n => n.Id).ToHashSet();
            var desiredPins = desired.Circuit.Nets.SelectMany(n => n.Pins).ToHashSet();
            var resultById = new Dictionary<Guid, CircuitNet>();
            var historicalByGroup = restoration is null ? null
                : Partition(restoration.History.Design.Engineering.Circuit, universe, stackedNow).Nets;
            var restoredNets = new HashSet<Guid>();
            var historicalImplicit = new HashSet<PinEndpoint>();
            if (restoration is not null)
            {
                var past = restoration.History.Design.Engineering.Circuit;
                var parts = past.Parts.ToDictionary(p => p.Id);
                var definitions = past.Sheets.SelectMany(s => s.Components).ToDictionary(c => c.Id);
                historicalImplicit = past.Components.SelectMany(c => parts[definitions[c.DefinitionId].PartId].Pins
                    .Select(p => new PinEndpoint(c.Id, p.Number))).Except(past.Nets.SelectMany(n => n.Pins)).ToHashSet();
            }
            foreach (var empty in desired.Circuit.Nets.Where(n => n.Pins.Count == 0)) resultById.Add(empty.Id, empty);
            foreach (var group in merged.Groups)
            {
                token.ThrowIfCancellationRequested(); string key = Key(group);
                if (desiredByGroup.TryGetValue(key, out var explicitNet)) { resultById.Add(explicitNet.Id, explicitNet); continue; }
                if (historicalByGroup is not null && historicalByGroup.TryGetValue(key, out var historical)
                    && !desiredIds.Contains(historical.Id))
                {
                    resultById.Add(historical.Id, historical); restoredNets.Add(historical.Id); continue;
                }
                // An unchanged implicit unconnected pin must not acquire a new
                // net merely because it appeared in a native observation.
                if (group.Count == 1 && (baselineGroups.Contains(key) || historicalImplicit.Contains(group[0]))
                    && !desiredPins.Contains(group[0])) continue;
                // Nor do unconnected pins that KiCad joins only because one symbol
                // stacks them: exactly a stacked node, none of it named by the XML.
                // Any other group comes from a native edit and becomes a net as KiCad
                // shows it, its stacked pins included, like every native group.
                if (stackedNodes.Contains(key) && !group.Any(desiredPins.Contains)) continue;
                Guid id = GeneratedIdentity(state.OriginId, desired.Circuit.Id, group);
                string name = nativeByGroup.TryGetValue(key, out var nativeGroup) && !string.IsNullOrWhiteSpace(nativeGroup.NativeName)
                    ? nativeGroup.NativeName : "NET-" + id.ToString("N")[..12];
                if (desiredIds.Contains(id) || !resultById.TryAdd(id, new(id, name!, group.ToArray())))
                    throw Failure("generated_net_identity_conflict", "A generated net identity conflicts with an explicit design entity.");
            }
            var ordered = desired.Circuit.Nets.Where(n => resultById.ContainsKey(n.Id)).Select(n => resultById[n.Id])
                .Concat(resultById.Values.Where(n => !desiredIds.Contains(n.Id)).OrderBy(n => n.Id)).ToArray();
            var circuit = (restoration?.BindingCandidate.Engineering.Circuit ?? removal?.BindingCandidate?.Engineering.Circuit ?? desired.Circuit)
                with { Nets = ordered }; circuit.Validate();
            var finalByPin = ordered.SelectMany(net => net.Pins.Select(pin => (pin, net))).ToDictionary(x => x.pin, x => x.net);
            var changes = new List<NetIdentityChange>();
            foreach (var retired in desired.Circuit.Nets.Where(n => !resultById.ContainsKey(n.Id)))
            {
                var candidates = retired.Pins.Where(finalByPin.ContainsKey).Select(p => finalByPin[p]).DistinctBy(n => n.Id).ToArray();
                var kind = candidates.Length > 1 ? NetBindingChangeKind.Split
                    : candidates.Length == 0 ? NetBindingChangeKind.Removed
                    : candidates[0].Pins.Count > retired.Pins.Count ? NetBindingChangeKind.Merged : NetBindingChangeKind.Reidentified;
                changes.Add(new(retired.Id, kind, $"Native connectivity changed net '{retired.Name}'; its requirement binding needs explicit resolution.",
                    candidates.Select(n => n.Id).Order().ToArray()));
            }
            var candidate = ComponentReferenceRetention.Retain(desired, circuit, removal?.ComponentChanges ?? [], state.KnowledgeLibraries, changes);
            if (restoration is not null)
                candidate = SchematicNativeRestorationProjection.ResolveRetained(candidate, restoration, restoredNets, state.KnowledgeLibraries);
            candidate.Validate(state.KnowledgeLibraries);
            return new(candidate, [], changes, [], gaps, RemovedSymbolOccurrences: removal?.RemovedOccurrences,
                ComponentChanges: removal?.ComponentChanges, RestoredSymbolOccurrences: restoration?.RestoredOccurrences,
                RestoredNetIds: restoration is null ? null : restoredNets.Order().ToArray()) { Restoration = restoration };
        }
        catch (DecoderFallbackException error) { return new(null, [], [], [], [], "invalid_desired_design", error.Message); }
        catch (AutomationException error) { return new(null, [], [], [], [], error.Code, error.Message); }
    }

    /// <summary>The circuit's pin partition over <paramref name="universe"/> as KiCad shows it: each net with the
    /// unconnected pins stacked on its pins, and each group of unconnected stacked pins as one node (decision
    /// kicad-stacked-pins-one-node-20260924). A stacked node the circuit spreads over two nets stays split: KiCad's join is
    /// then a real difference, which the comparison and planning report. <c>Nets</c> finds every net by its own pins and
    /// by its joined pins.</summary>
    internal static (IReadOnlyList<IReadOnlyList<PinEndpoint>> Groups, Dictionary<string, CircuitNet> Nets) Partition(
        Circuit circuit, IReadOnlyList<PinEndpoint> universe, IReadOnlyList<IReadOnlyList<PinEndpoint>> stacked)
    {
        var assigned = circuit.Nets.SelectMany(n => n.Pins).ToHashSet();
        var groups = circuit.Nets.Where(n => n.Pins.Count > 0).Select(n => (Pins: n.Pins, Net: (CircuitNet?)n))
            .Concat(universe.Where(p => !assigned.Contains(p)).Distinct()
                .Select(p => (Pins: (IReadOnlyList<PinEndpoint>)new[] { p }, Net: (CircuitNet?)null)))
            .ToArray();
        var owner = new Dictionary<PinEndpoint, int>();
        for (int group = 0; group < groups.Length; ++group)
            foreach (var pin in groups[group].Pins) owner.TryAdd(pin, group);
        var parent = Enumerable.Range(0, groups.Length).ToArray();
        int Root(int group) { while (parent[group] != group) group = parent[group] = parent[parent[group]]; return group; }
        foreach (var node in stacked)
        {
            var joined = node.Where(owner.ContainsKey).Select(p => Root(owner[p])).Distinct().ToArray();
            // Nets already in the joined groups, not only the ones the node touches directly.
            if (joined.Length < 2 || Enumerable.Range(0, groups.Length)
                    .Count(g => groups[g].Net is not null && joined.Contains(Root(g))) > 1)
                continue;
            foreach (int root in joined.Skip(1)) parent[root] = joined[0];
        }
        var result = new List<IReadOnlyList<PinEndpoint>>();
        var nets = new Dictionary<string, CircuitNet>(StringComparer.Ordinal);
        foreach (var component in Enumerable.Range(0, groups.Length).GroupBy(Root))
        {
            IReadOnlyList<PinEndpoint> pins = [.. component.SelectMany(g => groups[g].Pins)];
            result.Add(pins);
            if (component.Select(g => groups[g].Net).OfType<CircuitNet>().SingleOrDefault() is { } net)
            {
                nets.TryAdd(Key(net.Pins), net);
                nets.TryAdd(Key(pins), net);
            }
        }
        return (result, nets);
    }

    internal static string Key(IEnumerable<PinEndpoint> pins) => JsonSerializer.Serialize(pins
        .OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).Select(p => new { p.ComponentId, p.Pin }));

    private static Guid GeneratedIdentity(Guid origin, Guid circuit, IEnumerable<PinEndpoint> pins)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-net-reconciliation-v1\n" + origin.ToString("D") + "\n" + circuit.ToString("D") + "\n" + Key(pins)));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    internal static string Topology(Circuit circuit)
    {
        var owners = circuit.Components.ToDictionary(c => c.Id);
        return JsonSerializer.Serialize(new
        {
        circuit.Id,
        parts = circuit.Parts.OrderBy(p => p.Id).Select(p => new { p.Id, p.Units,
            pins = p.Pins.OrderBy(p => p.Number, StringComparer.Ordinal).Select(p => new { p.Number, p.Unit }) }),
        sheets = circuit.Sheets.OrderBy(s => s.Id).Select(s => new { s.Id,
            components = s.Components.OrderBy(c => c.Id).Select(c => new { c.Id, c.PartId }) }),
        instances = circuit.SheetInstances.OrderBy(s => s.Id),
        components = circuit.Components.OrderBy(c => c.Id).Select(c => new { c.Id, c.DefinitionId, c.SheetInstanceId }),
        symbols = circuit.Symbols.OrderBy(s => s.Id).Select(s => new { s.Id, s.ComponentId, s.Unit,
            Sheet = s.EffectiveSheetInstanceId(owners[s.ComponentId]) })
        });
    }

    internal static string Bindings(SchematicDesign design) => JsonSerializer.Serialize(new
    {
        sheets = design.SheetBindings.OrderBy(b => b.SheetInstanceId).Select(b => new { b.SheetInstanceId, b.NativePath }),
        symbols = design.SymbolBindings.OrderBy(b => b.SymbolOccurrenceId)
    });

    internal static string NativeOwners(SchematicHierarchyData hierarchy) => JsonSerializer.Serialize(hierarchy.Instances
        .OrderBy(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)), StringComparer.Ordinal)
        .Select(s => new
        {
            screen = s.Metadata.ScreenId?.Value,
            path = s.Metadata.Document.SheetPath.Path.Select(p => p.Value),
            symbols = s.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .OrderBy(s => s.Id.Value, StringComparer.Ordinal).Select(symbol => new
                {
                    id = symbol.Id.Value, unit = symbol.Unit?.Unit,
                    library = (symbol.LibraryId ?? symbol.Definition?.Id)?.ToString(),
                    units = symbol.Definition?.UnitCount,
                    pins = symbol.Definition?.Items.Where(c => c.Item?.Is(SchematicPin.Descriptor) == true)
                        .Select(c => c.Item.Unpack<SchematicPin>()).Where(p => p.LibraryPinId is not null)
                        .OrderBy(p => p.Id?.Value, StringComparer.Ordinal).Select(p => new
                        { id = p.Id?.Value, library = p.LibraryPinId.Value, p.Number, p.Name, p.ActiveAlternate, p.ElectricalType })
                })
        }));

    private static AutomationException Failure(string code, string message) => new(code, message);
}
