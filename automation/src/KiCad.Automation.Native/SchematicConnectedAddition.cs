using KiCad.Automation.Model;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Native;

public enum SchematicConnectedAdditionKind { NotApplicable = 0, Admitted = 1, Rejected = 2 }

public sealed record SchematicConnectedAdditionClassification(SchematicConnectedAdditionKind Kind,
    IReadOnlyList<Guid> AddedComponentIds, IReadOnlyList<Guid> ChangedNetIds,
    string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>What a saved XML revision changes in the circuit, counting only drawn endpoints
/// (cn1-wiring-intent.md §4.1 step 3). <paramref name="Added"/> holds, per net, the drawn pins
/// that net gains, and <paramref name="DrawnMembers"/> how many drawn pins each of those nets then
/// has. <paramref name="Lost"/> is true when some baseline net no longer holds one of its drawn pins
/// (a deleted net, a removed pin or a pin moved to another net); the first such pin, in net and pin
/// order, is kept for the refusal message.</summary>
internal sealed record SchematicConnectedAdditionDelta(IReadOnlyDictionary<Guid, IReadOnlyList<PinEndpoint>> Added,
    IReadOnlyDictionary<Guid, int> DrawnMembers, bool Lost, PinEndpoint? FirstLost, Guid? FirstLostNet,
    IReadOnlyList<Guid> AddedComponentIds);

/// <summary>Entry points the synchronization seam calls for XML revisions that add
/// connections (cn1-wiring-intent.md §4 and §9). Lane 2A owns this file.</summary>
public static class SchematicConnectedAddition
{
    /// <summary>The handshake capability native advertises only when every CN-1 native piece
    /// exists: the atomic connectivity assertion, item-candidate measurement, pin power facts and
    /// incomplete-pin reasons (cn1-wiring-intent.md §8.3).</summary>
    public const string NativeCapability = "schematic.connection-realization.v1";

    private static readonly SchematicConnectedAdditionClassification NotApplicable =
        new(SchematicConnectedAdditionKind.NotApplicable, [], []);

    /// <summary>Classify a saved XML revision for the planning seam. Planning reads only the saved
    /// recovery record and has no native session, so it cannot show that the editor advertises
    /// <see cref="NativeCapability"/>. This overload therefore never admits or rejects anything:
    /// every plan keeps its existing path and error code until a caller that holds the handshake
    /// uses <see cref="Classify(DesignRecoveryState, SchematicDesign, AutomationSession?, CancellationToken)"/>.</summary>
    public static SchematicConnectedAdditionClassification Classify(DesignRecoveryState state,
        SchematicDesign desired, CancellationToken token = default) => Classify(state, desired, session: null, token);

    /// <summary>Decide whether a saved XML revision only adds connections over drawn pins, with or
    /// without supported component additions (cn1-wiring-intent.md §4.1). The result is
    /// <see cref="SchematicConnectedAdditionKind.NotApplicable"/> unless <paramref name="session"/> is the
    /// handshake of the recorded instance and advertises <see cref="NativeCapability"/>; an editor that
    /// cannot realize and prove connections keeps today's behaviour. Pure: reads no files or editor
    /// and never throws for an unsupported shape, which stays on the general path.</summary>
    public static SchematicConnectedAdditionClassification Classify(DesignRecoveryState state,
        SchematicDesign desired, AutomationSession? session, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(desired);
        token.ThrowIfCancellationRequested();
        if (!Advertises(session, state.InstanceId)) return NotApplicable;

        var baseline = state.Baseline.Engineering.Circuit;
        var wanted = desired.Engineering.Circuit;
        // Steps 1-2: the same circuit, with every existing owner, part, sheet and binding unchanged.
        if (baseline.Id != wanted.Id || !SameShape(state, desired, token)) return NotApplicable;
        // Step 3: the delta over drawn endpoints, evaluated in the desired circuit.
        if (Delta(baseline, wanted) is not { } delta) return NotApplicable;
        token.ThrowIfCancellationRequested();
        bool creates = delta.AddedComponentIds.Count != 0;
        // Step 4: renames, requirement-only edits and regroupings that need no native change.
        if (!creates && !delta.Lost && delta.DrawnMembers.Values.All(count => count < 2)) return NotApplicable;
        bool stable = Stable(state, token);
        // Step 6: XML never disconnects. On a stable editor that is a refusal; otherwise the
        // general three-way path may merge a matching native disconnection.
        if (delta.Lost)
            return stable
                ? new(SchematicConnectedAdditionKind.Rejected, [], [], SchematicConnectionErrors.XmlDisconnectionUnsupported,
                    DisconnectionMessage(wanted, baseline, delta))
                : NotApplicable;
        // Step 7: the editor already shows the requested connections.
        if (!creates && !stable && Realized(state, desired, token)) return NotApplicable;
        // Step 8.
        return new(SchematicConnectedAdditionKind.Admitted, delta.AddedComponentIds, delta.Added.Keys.Order().ToArray());
    }

    /// <summary>Whether <paramref name="session"/> is the handshake of <paramref name="instanceId"/> and
    /// advertises <see cref="NativeCapability"/>. Any other session is no evidence for this instance.</summary>
    public static bool Advertises(AutomationSession? session, Guid instanceId) =>
        session is not null && instanceId != Guid.Empty && session.InstanceId == instanceId.ToString("D")
        && session.Capabilities.Contains(NativeCapability);

    /// <summary>The additive delta over drawn endpoints (§4.1 step 3), or <c>null</c> when an endpoint
    /// does not resolve to a declared part pin, which leaves the revision to the general path.</summary>
    internal static SchematicConnectedAdditionDelta? Delta(Circuit baseline, Circuit desired)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(desired);
        if (Drawing.Create(desired) is not { } drawing) return null;
        var before = new Dictionary<Guid, HashSet<PinEndpoint>>();
        foreach (var net in baseline.Nets)
            if (!before.TryAdd(net.Id, net.Pins.ToHashSet())) return null;
        var added = new SortedDictionary<Guid, IReadOnlyList<PinEndpoint>>();
        var members = new SortedDictionary<Guid, int>();
        var after = new Dictionary<Guid, HashSet<PinEndpoint>>();
        foreach (var net in desired.Nets)
        {
            if (!after.TryAdd(net.Id, net.Pins.ToHashSet())) return null;
            var old = before.GetValueOrDefault(net.Id) ?? [];
            var gained = new List<PinEndpoint>();
            int drawnPins = 0;
            foreach (var pin in net.Pins)
            {
                if (drawing.IsDrawn(pin) is not { } drawn) return null;
                if (!drawn) continue;
                drawnPins++;
                if (!old.Contains(pin)) gained.Add(pin);
            }
            if (gained.Count == 0) continue;
            added.Add(net.Id, gained.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal).ToArray());
            members.Add(net.Id, drawnPins);
        }
        PinEndpoint? firstLost = null; Guid? firstLostNet = null;
        foreach (var net in baseline.Nets.OrderBy(n => n.Id))
        {
            var kept = after.GetValueOrDefault(net.Id);
            foreach (var pin in net.Pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal))
            {
                if (drawing.IsDrawn(pin) is not { } drawn) return null;
                if (drawn && kept?.Contains(pin) != true) { firstLost = pin; firstLostNet = net.Id; break; }
            }
            if (firstLost is not null) break;
        }
        var existing = baseline.Components.Select(c => c.Id).ToHashSet();
        var created = desired.Components.Select(c => c.Id).Where(id => !existing.Contains(id)).Distinct().Order().ToArray();
        return new(added, members, firstLost is not null, firstLost, firstLostNet, created);
    }

    // §4.1 step 2. Existing components, occurrences, sheet-definition components, parts, sheet
    // instances, bindings and native hierarchy are unchanged; new parts are validated by creation.
    private static bool SameShape(DesignRecoveryState state, SchematicDesign desired, CancellationToken token)
    {
        var baseline = state.Baseline.Engineering.Circuit;
        var wanted = desired.Engineering.Circuit;
        var components = new Dictionary<Guid, ComponentInstance>();
        if (wanted.Components.Any(c => !components.TryAdd(c.Id, c))) return false;
        if (baseline.Components.Any(c => !components.TryGetValue(c.Id, out var next) || !c.Equals(next))) return false;
        var symbols = new Dictionary<Guid, SymbolOccurrence>();
        if (wanted.Symbols.Any(s => !symbols.TryAdd(s.Id, s))) return false;
        if (baseline.Symbols.Any(s => !symbols.TryGetValue(s.Id, out var next) || !s.Equals(next))) return false;
        var sheets = new Dictionary<Guid, SheetDefinition>();
        if (wanted.Sheets.Any(s => !sheets.TryAdd(s.Id, s))) return false;
        if (baseline.Sheets.Any(sheet => !sheets.TryGetValue(sheet.Id, out var next)
            || sheet.Components.Any(definition => !next.Components.Any(c => c.Id == definition.Id && c.Equals(definition)))))
            return false;
        if (!baseline.Sheets.Select(s => s.Id).ToHashSet().SetEquals(sheets.Keys)) return false;
        var parts = new Dictionary<Guid, PartDefinition>();
        if (wanted.Parts.Any(p => !parts.TryAdd(p.Id, p))) return false;
        if (baseline.Parts.Any(part => !parts.TryGetValue(part.Id, out var next)
            || SchematicNativeCreationProjection.NormalizePart(part) != SchematicNativeCreationProjection.NormalizePart(next)))
            return false;
        if (!baseline.SheetInstances.OrderBy(s => s.Id).SequenceEqual(wanted.SheetInstances.OrderBy(s => s.Id))) return false;
        if (SchematicNetReconciliation.Bindings(state.Baseline) != SchematicNetReconciliation.Bindings(desired)) return false;
        try { return SchematicHierarchyDelta.Plan(state.Baseline.Schematic, desired.Schematic, token).Count == 0; }
        catch (AutomationException) { return false; }
    }

    // §4.1 step 5: no pending choice, and the editor still shows the baseline hierarchy and pin partition.
    private static bool Stable(DesignRecoveryState state, CancellationToken token)
    {
        if (state.HierarchyResolution is not null || state.OwnershipResolution is not null) return false;
        try
        {
            if (SchematicHierarchyDelta.Plan(state.Baseline.Schematic, state.Observed, token).Count != 0) return false;
            _ = SchematicElectricalCheckpoints.Require(state);
            var observed = SchematicElectricalComparison.Compare(state.Baseline, state.ObservedElectrical!, state.KnowledgeLibraries, token);
            return observed.PinBindingsComplete && observed.ConnectivityEquivalent;
        }
        catch (AutomationException) { return false; }
    }

    // §4.1 step 7: the observed pin partition already equals the desired circuit.
    private static bool Realized(DesignRecoveryState state, SchematicDesign desired, CancellationToken token)
    {
        if (state.ObservedElectrical is null) return false;
        try
        {
            var observed = SchematicElectricalComparison.Compare(state.Baseline with { Engineering = desired.Engineering },
                state.ObservedElectrical, state.KnowledgeLibraries, token);
            return observed.PinBindingsComplete && observed.ConnectivityEquivalent;
        }
        catch (AutomationException) { return false; }
    }

    private static string DisconnectionMessage(Circuit desired, Circuit baseline, SchematicConnectedAdditionDelta delta)
    {
        var pin = delta.FirstLost!;
        string reference = desired.Components.FirstOrDefault(c => c.Id == pin.ComponentId)?.Reference ?? pin.ComponentId.ToString("D");
        string net = baseline.Nets.First(n => n.Id == delta.FirstLostNet).Name;
        return $"The saved XML removes pin {reference}.{pin.Pin} from net '{net}'. XML can add connections but cannot "
            + "disconnect or rewire existing ones; make that change in the schematic editor, or restore the connection in the XML.";
    }

    /// <summary>Which pins are drawn in one circuit (cn1-wiring-intent.md §2, "Drawn endpoint"): a pin
    /// of unit u is drawn when its component has an occurrence of unit u; a common pin (unit 0) is
    /// drawn when the component has any occurrence.</summary>
    private sealed class Drawing
    {
        // Component -> (pin number -> declared unit), and component -> units that have an occurrence.
        private readonly Dictionary<Guid, IReadOnlyDictionary<string, int>> pins = [];
        private readonly Dictionary<Guid, HashSet<int>> units = [];

        public static Drawing? Create(Circuit circuit)
        {
            var result = new Drawing();
            var partPins = new Dictionary<Guid, IReadOnlyDictionary<string, int>>();
            foreach (var part in circuit.Parts)
            {
                var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
                if (part.Pins.Any(pin => !numbers.TryAdd(pin.Number, pin.Unit)) || !partPins.TryAdd(part.Id, numbers)) return null;
            }
            var definitions = new Dictionary<Guid, ComponentDefinition>();
            if (circuit.Sheets.SelectMany(s => s.Components).Any(d => !definitions.TryAdd(d.Id, d))) return null;
            foreach (var component in circuit.Components)
            {
                if (!definitions.TryGetValue(component.DefinitionId, out var definition)
                    || !partPins.TryGetValue(definition.PartId, out var numbers) || !result.pins.TryAdd(component.Id, numbers))
                    return null;
                result.units.Add(component.Id, []);
            }
            foreach (var symbol in circuit.Symbols)
            {
                if (!result.units.TryGetValue(symbol.ComponentId, out var drawn)) return null;
                drawn.Add(symbol.Unit);
            }
            return result;
        }

        /// <summary>Whether <paramref name="pin"/> is drawn, or <c>null</c> when it names no declared part pin.</summary>
        public bool? IsDrawn(PinEndpoint pin)
        {
            if (!pins.TryGetValue(pin.ComponentId, out var numbers) || !numbers.TryGetValue(pin.Pin, out int unit)) return null;
            var drawn = units[pin.ComponentId];
            return unit == 0 ? drawn.Count != 0 : drawn.Contains(unit);
        }
    }

    /// <summary>Whether the pending layout recorded in <paramref name="state"/> was produced
    /// by a connection-realization plan rather than a connected move or a rebuild. The executor
    /// routes by the lane recorded in the layout intent; this predicate only validates
    /// that record and must never claim a layout another lane recorded.</summary>
    internal static bool IsRealization(DesignRecoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return false;
    }

    /// <summary>Check the <paramref name="session"/> handshake for the realization capability,
    /// measure the checkpoint natively and return the operations, ending with the connectivity
    /// assertion, plus the exact design they plan to publish (§9.1 steps 2-3). The executor
    /// builds, validates and journals the batch envelope.</summary>
    internal static Task<SchematicPreparedRealization> RealizeAsync(NativeClient client, AutomationSession session,
        DesignRecoveryState state, SchematicSynchronizationPlan plan, CheckedSchematicState checkpoint, CancellationToken token = default)
        => throw Unavailable();

    /// <summary>Check a realization receipt before generic handling (§9.2): abandon a
    /// batch its own assertion rejected, and refuse a completion without verification.</summary>
    internal static void CheckReceipt(DesignRecoveryStore store, StoredDesignRecovery saved,
        CheckedSchematicBatchReceipt receipt) => throw Unavailable();

    /// <summary>Resolve a committed realization against the planned design (§9.4).</summary>
    internal static SchematicDesign Resolve(SchematicDesign planned, DesignRecoveryState state,
        SchematicElectricalState native, ApplySchematicItemBatch batch, CheckedSchematicBatchReceipt receipt,
        CancellationToken token = default) => throw Unavailable();

    internal static AutomationException Unavailable() => new(SchematicConnectionErrors.ConnectedAdditionUnavailable,
        "Connected XML additions are not available in this build.");
}

/// <summary>Prepare an admitted connected addition (cn1-wiring-intent.md §4.4).</summary>
public static class SchematicConnectedAdditionPlanner
{
    /// <summary>Run the creation guards in <c>PrepareCreation</c> order with its error codes, build the
    /// pre-realization candidate and check its bindings. A refusal is a plan with no candidate, exactly
    /// as the synchronization planner reports creation refusals. The connection intent (§4.4 step 4,
    /// §5) is not delivered in this build, so an addition that passes every guard still stops with
    /// <c>connected_addition_unavailable</c> and nothing reaches the editor.</summary>
    public static SchematicSynchronizationPlan Prepare(DesignRecoveryState state, SchematicDesign desired,
        SchematicHierarchyMergeResult hierarchy, SchematicConnectedAdditionClassification shape,
        List<HierarchyCoverageGap> gaps, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(gaps);
        if (shape.Kind != SchematicConnectedAdditionKind.Admitted)
            throw new ArgumentException("Only an admitted connected addition can be prepared.", nameof(shape));
        token.ThrowIfCancellationRequested();
        try
        {
            // Step 1: the creation guards, in PrepareCreation order and with its codes.
            var checkpoints = SchematicElectricalCheckpoints.Require(state);
            gaps.AddRange(hierarchy.CoverageGaps);
            if (!hierarchy.CanApply || hierarchy.Merged is null)
                return Failure(hierarchy.ErrorCode ?? "design_sync_conflict",
                    hierarchy.ErrorMessage ?? "Resolve the reported hierarchy conflict before adding XML connections.");
            if (SchematicNetReconciliation.Bindings(state.Baseline) != SchematicNetReconciliation.Bindings(desired))
                return Failure(SchematicConnectionErrors.CreationBindingsChanged,
                    "Preserve existing bindings until new connections have been realized.");
            if (state.HierarchyResolution is not null || state.OwnershipResolution is not null
                || SchematicHierarchyDelta.Plan(state.Baseline.Schematic, state.Observed, token).Count != 0
                || SchematicHierarchyDelta.Plan(state.Baseline.Schematic, desired.Schematic, token).Count != 0)
                return Failure(SchematicConnectionErrors.CreationRequiresStableNativeHierarchy,
                    "XML connections cannot overwrite concurrent native hierarchy or layout edits.");
            var before = SchematicElectricalComparison.Compare(state.Baseline, checkpoints.Baseline, state.KnowledgeLibraries, token);
            if (!before.PinBindingsComplete || !before.ConnectivityEquivalent)
                return Failure(SchematicConnectionErrors.UnalignedElectricalBaseline, "The saved baseline must agree with its native pin partition.");
            var observed = SchematicElectricalComparison.Compare(state.Baseline, checkpoints.Observed, state.KnowledgeLibraries, token);
            if (!observed.PinBindingsComplete || !observed.ConnectivityEquivalent)
                return Failure(SchematicConnectionErrors.CreationRequiresStableConnectivity,
                    "Reconcile current native connectivity before adding XML connections.");

            // Step 2: the pre-realization candidate. Created symbols keep their exact creation identities.
            var candidate = shape.AddedComponentIds.Count != 0
                ? SchematicNativeCreationProjection.Project(state.Baseline, desired, state.KnowledgeLibraries, token, allowConnected: true).Candidate
                : state.Baseline with { Engineering = desired.Engineering, PartSymbols = desired.PartSymbols };

            // Step 3.
            var bindings = SchematicDesignBindings.Inspect(candidate, state.KnowledgeLibraries, token);
            gaps.AddRange(bindings.CoverageGaps);
            if (!bindings.IdentitiesResolved)
                return Failure(SchematicConnectionErrors.CreatedBindingInvalid, "The generated native identities do not resolve exactly.", bindings.Issues);

            // Step 4 needs the connection intent (§5), which this build does not contain.
            var unavailable = SchematicConnectedAddition.Unavailable();
            return Failure(unavailable.Code, unavailable.Message);
        }
        catch (AutomationException error) { return Failure(error.Code, error.Message); }

        SchematicSynchronizationPlan Failure(string code, string message, IReadOnlyList<SchematicBindingIssue>? issues = null) =>
            new(null, null, [], hierarchy, null, null, issues ?? [], [], null, gaps.Distinct().ToArray(), true, code, message);
    }
}
