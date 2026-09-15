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
    string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>Pure three-way electrical-model reconciliation over stable exact
/// component/sheet/pin ownership. Does not apply native edits or advance recovery.</summary>
public static class SchematicNetReconciliation
{
    public static SchematicNetReconciliationResult Plan(DesignRecoveryState state, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (state.OriginId == Guid.Empty || state.InstanceId == Guid.Empty)
                throw Failure("invalid_electrical_recovery", "An exact recovery origin and instance are required.");
            if (state.HasPendingWork) throw Failure("pending_recovery_requires_reconciliation", "Reconcile the exact pending operation first.");
            var baseline = state.BaselineElectrical ?? throw Failure("missing_electrical_baseline", "Initialize the matched electrical baseline first.");
            var observed = state.ObservedElectrical ?? throw Failure("missing_electrical_observation", "Capture current matching electrical state first.");
            if (!Equals(baseline.Hierarchy?.Data, state.Baseline.Schematic) || !Equals(observed.Hierarchy?.Data, state.Observed)
                || observed.Hierarchy?.Revision?.Epoch != state.NativeRevision.Epoch
                || observed.Hierarchy.Revision.Sequence != state.NativeRevision.Sequence
                || observed.Hierarchy.TrackingComplete != state.TrackingComplete)
                throw Failure("invalid_electrical_recovery", "Electrical checkpoints must match their exact recovery owners and revisions.");
            if (baseline.Hierarchy.Revision.Epoch == state.NativeRevision.Epoch
                && baseline.Hierarchy.Revision.Sequence > state.NativeRevision.Sequence)
                throw Failure("invalid_electrical_recovery", "The baseline cannot follow the current observation.");
            var desiredDocument = SchematicDesignXml.Read(new UTF8Encoding(false, true).GetString(state.DesiredFileBytes), state.KnowledgeLibraries);
            var desired = desiredDocument.Engineering;
            if (Topology(state.Baseline.Engineering.Circuit) != Topology(desired.Circuit)
                || Bindings(state.Baseline) != Bindings(desiredDocument)
                || NativeOwners(state.Baseline.Schematic) != NativeOwners(state.Observed))
                throw Failure("electrical_ownership_changed", "Reconcile changed component, sheet, unit or pin ownership before merging nets.");
            var before = SchematicElectricalComparison.Compare(state.Baseline, baseline, state.KnowledgeLibraries, token);
            if (!before.PinBindingsComplete || !before.ConnectivityEquivalent)
                return new(null, [], [], before.Issues, before.CoverageGaps, "unaligned_electrical_baseline", "The saved baseline must agree with its native pin partition.");
            var current = SchematicElectricalComparison.Compare(state.Baseline, observed, state.KnowledgeLibraries, token);
            var gaps = before.CoverageGaps.Concat(current.CoverageGaps).Distinct().ToArray();
            if (!current.PinBindingsComplete) return new(null, [], [], current.Issues, gaps, "unresolved_electrical_bindings");
            var universe = before.PinPartitions!.SelectMany(g => g.Pins).ToArray();
            var merged = PinPartitionMerge.Plan(Partition(state.Baseline.Engineering.Circuit, universe),
                Partition(desired.Circuit, universe), current.PinPartitions!.Select(p => p.Pins).ToArray(), token);
            if (merged.Groups is null) return new(null, merged.Conflicts, [], [], gaps);

            // Preserve explicit XML identities and semantic names for every
            // exact surviving group. Native label names are not identity keys.
            var desiredByGroup = desired.Circuit.Nets.Where(n => n.Pins.Count > 0).ToDictionary(n => Key(n.Pins), StringComparer.Ordinal);
            var baselineGroups = Partition(state.Baseline.Engineering.Circuit, universe).Select(Key).ToHashSet(StringComparer.Ordinal);
            var nativeByGroup = current.PinPartitions!.ToDictionary(p => Key(p.Pins), StringComparer.Ordinal);
            var desiredIds = desired.Circuit.Nets.Select(n => n.Id).ToHashSet();
            var desiredPins = desired.Circuit.Nets.SelectMany(n => n.Pins).ToHashSet();
            var resultById = new Dictionary<Guid, CircuitNet>();
            foreach (var empty in desired.Circuit.Nets.Where(n => n.Pins.Count == 0)) resultById.Add(empty.Id, empty);
            foreach (var group in merged.Groups)
            {
                token.ThrowIfCancellationRequested(); string key = Key(group);
                if (desiredByGroup.TryGetValue(key, out var explicitNet)) { resultById.Add(explicitNet.Id, explicitNet); continue; }
                // An unchanged implicit unconnected pin must not acquire a new
                // net merely because it appeared in a native observation.
                if (group.Count == 1 && baselineGroups.Contains(key)
                    && !desiredPins.Contains(group[0])) continue;
                Guid id = GeneratedIdentity(state.OriginId, desired.Circuit.Id, group);
                string name = nativeByGroup.TryGetValue(key, out var nativeGroup) && !string.IsNullOrWhiteSpace(nativeGroup.NativeName)
                    ? nativeGroup.NativeName : "NET-" + id.ToString("N")[..12];
                if (desiredIds.Contains(id) || !resultById.TryAdd(id, new(id, name!, group.ToArray())))
                    throw Failure("generated_net_identity_conflict", "A generated net identity conflicts with an explicit design entity.");
            }
            var ordered = desired.Circuit.Nets.Where(n => resultById.ContainsKey(n.Id)).Select(n => resultById[n.Id])
                .Concat(resultById.Values.Where(n => !desiredIds.Contains(n.Id)).OrderBy(n => n.Id)).ToArray();
            var circuit = desired.Circuit with { Nets = ordered }; circuit.Validate();
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
            var candidate = desired with { Circuit = circuit,
                Structure = desired.Structure.RetainUnresolvedNets(desired.Circuit, circuit, changes) };
            candidate.Validate(state.KnowledgeLibraries);
            return new(candidate, [], changes, [], gaps);
        }
        catch (DecoderFallbackException error) { return new(null, [], [], [], [], "invalid_desired_design", error.Message); }
        catch (AutomationException error) { return new(null, [], [], [], [], error.Code, error.Message); }
    }

    private static IReadOnlyList<IReadOnlyList<PinEndpoint>> Partition(Circuit circuit, IReadOnlyList<PinEndpoint> universe)
    {
        var assigned = circuit.Nets.SelectMany(n => n.Pins).ToHashSet();
        return circuit.Nets.Where(n => n.Pins.Count > 0).Select(n => n.Pins)
            .Concat(universe.Where(p => !assigned.Contains(p)).Select(p => (IReadOnlyList<PinEndpoint>)new[] { p })).ToArray();
    }

    private static string Key(IEnumerable<PinEndpoint> pins) => JsonSerializer.Serialize(pins
        .OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).Select(p => new { p.ComponentId, p.Pin }));

    private static Guid GeneratedIdentity(Guid origin, Guid circuit, IEnumerable<PinEndpoint> pins)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes("kicad-net-reconciliation-v1\n" + origin.ToString("D") + "\n" + circuit.ToString("D") + "\n" + Key(pins)));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    private static string Topology(Circuit circuit) => JsonSerializer.Serialize(new
    {
        circuit.Id,
        parts = circuit.Parts.OrderBy(p => p.Id).Select(p => new { p.Id, p.Units,
            pins = p.Pins.OrderBy(p => p.Number, StringComparer.Ordinal).Select(p => new { p.Number, p.Unit }) }),
        sheets = circuit.Sheets.OrderBy(s => s.Id).Select(s => new { s.Id,
            components = s.Components.OrderBy(c => c.Id).Select(c => new { c.Id, c.PartId }) }),
        instances = circuit.SheetInstances.OrderBy(s => s.Id),
        components = circuit.Components.OrderBy(c => c.Id).Select(c => new { c.Id, c.DefinitionId, c.SheetInstanceId }),
        symbols = circuit.Symbols.OrderBy(s => s.Id).Select(s => new { s.Id, s.ComponentId, s.Unit })
    });

    private static string Bindings(SchematicDesign design) => JsonSerializer.Serialize(new
    {
        sheets = design.SheetBindings.OrderBy(b => b.SheetInstanceId).Select(b => new { b.SheetInstanceId, b.NativePath }),
        symbols = design.SymbolBindings.OrderBy(b => b.SymbolOccurrenceId)
    });

    private static string NativeOwners(SchematicHierarchyData hierarchy) => JsonSerializer.Serialize(hierarchy.Instances
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
