using System.Text;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicNetReconciliationTests
{
    internal static DesignRecoveryState Fixture()
    {
        var f = SchematicElectricalComparisonTests.Fixture();
        var statement = f.Design.Engineering.Structure.Statements[0] with
        { TargetId = f.Design.Engineering.Circuit.Nets[0].Id, Text = " Preserve supply requirements\r\nwithout guessing.",
            Sources = [new("datasheet", "B", 9, "limits", "industrial")] };
        var design = f.Design with { Engineering = f.Design.Engineering with
        { Structure = f.Design.Engineering.Structure with { Statements = [statement, f.Design.Engineering.Structure.Statements[1]] } } };
        return new(Guid.NewGuid(), Guid.NewGuid(), new(f.State.Hierarchy.Revision.Epoch, f.State.Hierarchy.Revision.Sequence), false,
            design, Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [f.Library])), f.State.Hierarchy.Data.Clone(), [f.Library],
            BaselineElectrical: f.State.Clone(), ObservedElectrical: f.State.Clone());
    }

    internal static DesignRecoveryState Desired(DesignRecoveryState state, EngineeringDesign desired) => state with
    { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(state.Baseline with { Engineering = desired }, state.KnowledgeLibraries)) };

    internal static DesignRecoveryState NativeGroups(DesignRecoveryState state, params PinEndpoint[][] groups)
    {
        var native = state.ObservedElectrical!.Clone(); native.Hierarchy.Revision.Sequence++; native.Nets.Clear();
        foreach (var group in groups)
        {
            var net = new SchematicNet { Name = "Observed " + native.Nets.Count };
            var sheets = new Dictionary<string, SchematicNetSheetContents>();
            foreach (var endpoint in group)
            {
                var component = state.Baseline.Engineering.Circuit.Components.Single(c => c.Id == endpoint.ComponentId);
                var binding = state.Baseline.SheetBindings.Single(s => s.SheetInstanceId == component.SheetInstanceId);
                string path = string.Join('/', binding.NativePath.Select(id => id.ToString("D")));
                var screen = native.Hierarchy.Data.Instances.Single(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == path);
                if (!sheets.TryGetValue(path, out var contents)) sheets.Add(path, contents = new() { Path = screen.Metadata.Document.SheetPath.Clone() });
                foreach (var occurrence in state.Baseline.Engineering.Circuit.Symbols.Where(s => s.ComponentId == component.Id))
                {
                    string id = state.Baseline.SymbolBindings.Single(s => s.SymbolOccurrenceId == occurrence.Id).NativeObjectId.ToString("D");
                    var symbol = screen.Items.Where(p => p.Is(SchematicSymbolInstance.Descriptor)).Select(p => p.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == id);
                    foreach (var pin in symbol.Definition.Items.Select(c => c.Item.Unpack<SchematicPin>()).Where(p => p.Number == endpoint.Pin))
                        contents.Items.Add(pin.Id.Clone());
                }
            }
            net.Sheets.Add(sheets.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Value)); native.Nets.Add(net);
        }
        return state with { Observed = native.Hierarchy.Data.Clone(), ObservedElectrical = native,
            NativeRevision = new(native.Hierarchy.Revision.Epoch, native.Hierarchy.Revision.Sequence) };
    }

    // The PSU/CPU fixture's Components stage as the editor holds it once created, saved, reloaded and reattached (the
    // settled plan of the rendered PSU/CPU creation journey): every part placed from the frozen definitions, no net in the
    // XML, and the native snapshot as KiCad shows it, where the LP3982 U2 joins the pins 1 and 4 it draws at one point
    // (decision kicad-stacked-pins-one-node-20260924). Pins in <paramref name="wired"/> are also connected to that native
    // net since the baseline, as a wire drawn in KiCad would connect them.
    private static (DesignRecoveryState State, Func<string, string, PinEndpoint> Pin) PsuCpuSettled(params (string Reference, string Number)[] wired)
    {
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        var typed = components with { PartSymbols = SchematicSynchronizationPlanTests.WithLibraryPinTypes(components.PartSymbols!) };
        var placed = SchematicNativeCreationProjection.Project(sheets, typed, []).Candidate;
        var circuit = placed.Engineering.Circuit;
        var owners = circuit.Components.ToDictionary(c => c.Id);
        var references = circuit.Components.ToDictionary(c => c.Reference, c => c.Id);
        PinEndpoint Pin(string reference, string number) => new(references[reference], number);
        SchematicNet Net(IEnumerable<(string Reference, string Number)> pins)
        {
            var net = new SchematicNet { Name = "Net-(U2-OUT-Pad1)" };
            var contents = new SortedDictionary<string, SchematicNetSheetContents>(StringComparer.Ordinal);
            foreach (var (reference, number) in pins)
            foreach (var occurrence in circuit.Symbols.Where(s => s.ComponentId == references[reference]))
            {
                var path = placed.SheetBindings.Single(b => b.SheetInstanceId == occurrence.EffectiveSheetInstanceId(owners[occurrence.ComponentId])).NativePath;
                string key = string.Join('/', path.Select(id => id.ToString("D")));
                var screen = placed.Schematic.Instances.Single(i => string.Join('/', i.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == key);
                string native = placed.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrence.Id).NativeObjectId.ToString("D");
                var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == native);
                foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)
                    && !(c.Unit?.Unit is > 0 && c.Unit.Unit != occurrence.Unit)))
                {
                    var pin = child.Item.Unpack<SchematicPin>();
                    if (pin.LibraryPinId is null || pin.Number != number) continue;
                    if (!contents.TryGetValue(key, out var sheet)) contents.Add(key, sheet = new() { Path = screen.Metadata.Document.SheetPath.Clone() });
                    sheet.Items.Add(pin.Id.Clone());
                }
            }
            net.Sheets.Add(contents.Values);
            return net;
        }
        (string, string)[] stacked = [("U2", "1"), ("U2", "4")];
        var baseline = new SchematicElectricalState { Hierarchy = new() { Data = placed.Schematic.Clone(),
            Revision = new() { Epoch = "psu-cpu-settled", Sequence = 7 }, TrackingComplete = false } };
        baseline.Nets.Add(Net(stacked));
        var observed = baseline.Clone(); observed.Hierarchy.Revision.Sequence = 8;
        observed.Nets.Clear(); observed.Nets.Add(Net([.. stacked, .. wired]));
        return (new(Guid.NewGuid(), Guid.NewGuid(), new("psu-cpu-settled", 8), false, placed,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(placed, [])), observed.Hierarchy.Data.Clone(), [],
            BaselineElectrical: baseline, ObservedElectrical: observed), Pin);
    }

    // After save, reload and reattach, the native observation shows KiCad's join of U2's stacked pins although the XML leaves
    // them unconnected. That join is not a native edit: the plan keeps the published XML exactly and adds no generated net
    // (ledger p1c0985fabea9cbbe; before, it added "Net-(U2-OUT-Pad1)" holding exactly U2.1 and U2.4).
    [TestMethod]
    public void KiCadsJoinOfStackedPinsAddsNoNet()
    {
        var (state, pin) = PsuCpuSettled();
        var before = SchematicElectricalComparison.Compare(state.Baseline, state.BaselineElectrical!, state.KnowledgeLibraries);
        Assert.IsTrue(before.PinBindingsComplete && before.ConnectivityEquivalent, "KiCad's join is exactly what the XML describes.");
        CollectionAssert.AreEquivalent(new[] { pin("U2", "1"), pin("U2", "4") }, before.StackedPins!.Single().ToArray());
        var result = SchematicNetReconciliation.Plan(state);
        Assert.IsNotNull(result.Candidate, result.ErrorCode + ": " + result.ErrorMessage);
        Assert.IsEmpty(result.Candidate.Circuit.Nets, "KiCad's join of the stacked pins must not become a generated net.");
        Assert.AreEqual(EngineeringDesignXml.Write(state.Baseline.Engineering, []), EngineeringDesignXml.Write(result.Candidate, []));
        Assert.IsEmpty(result.NetChanges); Assert.IsEmpty(result.Conflicts);
        Assert.IsTrue(SchematicElectricalComparison.Compare(state.Baseline with { Engineering = result.Candidate }, state.ObservedElectrical!, [])
            .ConnectivityEquivalent);
    }

    // An XML net that names only one of the stacked pins keeps its identity and exactly its own pins: KiCad's join brings the
    // other stacked pin into that net natively, and the net is found by the pins KiCad joins to it rather than reissued
    // under a generated identity. Here the XML connects U2.1 to U3.1 and the editor shows the same connection.
    [TestMethod]
    public void XmlNetOnOneStackedPinKeepsItsIdentityAndPins()
    {
        var (state, pin) = PsuCpuSettled(("U3", "1"));
        var circuit = state.Baseline.Engineering.Circuit;
        var named = new CircuitNet(Guid.NewGuid(), "LDO_SENSE", [pin("U2", "1"), pin("U3", "1")]);
        var desired = state.Baseline.Engineering with { Circuit = circuit with { Nets = [named] } };
        state = Desired(state, desired);
        Assert.IsTrue(SchematicElectricalComparison.Compare(state.Baseline, state.BaselineElectrical!, []).ConnectivityEquivalent);
        var result = SchematicNetReconciliation.Plan(state);
        Assert.IsNotNull(result.Candidate, result.ErrorCode + ": " + result.ErrorMessage);
        Assert.AreEqual(EngineeringDesignXml.Write(desired, []), EngineeringDesignXml.Write(result.Candidate, []));
        CollectionAssert.AreEquivalent(new[] { pin("U2", "1"), pin("U3", "1") }, result.Candidate.Circuit.Nets.Single().Pins.ToArray());
        Assert.IsEmpty(result.NetChanges); Assert.IsEmpty(result.Conflicts);
        Assert.IsTrue(SchematicElectricalComparison.Compare(state.Baseline with { Engineering = result.Candidate }, state.ObservedElectrical!, [])
            .ConnectivityEquivalent, "The kept net is exactly what the editor shows once KiCad joins U2.4 to it.");
    }

    // Precision: a real native connection of the stacked pins to another pin is a native edit, reconciled as one generated
    // net holding all three pins under KiCad's own net name, as for any native join.
    [TestMethod]
    public void RealNativeConnectionOfStackedPinsIsStillReconciled()
    {
        var (state, pin) = PsuCpuSettled(("U3", "1"));
        var result = SchematicNetReconciliation.Plan(state);
        Assert.IsNotNull(result.Candidate, result.ErrorCode + ": " + result.ErrorMessage);
        var added = result.Candidate.Circuit.Nets.Single();
        CollectionAssert.AreEquivalent(new[] { pin("U2", "1"), pin("U2", "4"), pin("U3", "1") }, added.Pins.ToArray());
        Assert.AreEqual("Net-(U2-OUT-Pad1)", added.Name);
        Assert.IsEmpty(result.NetChanges); Assert.IsEmpty(result.Conflicts);
    }

    [TestMethod]
    public void NoChangePreservesExactDesiredIdentityAndSemanticNames()
    {
        var state = Fixture();
        var desired = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
        { Nets = state.Baseline.Engineering.Circuit.Nets.Select(n => n with { Name = "Desired semantic label" }).ToArray() } };
        state = Desired(state, desired);
        var result = SchematicNetReconciliation.Plan(state);
        Assert.IsNotNull(result.Candidate, result.ErrorMessage);
        Assert.AreEqual(EngineeringDesignXml.Write(desired, state.KnowledgeLibraries), EngineeringDesignXml.Write(result.Candidate, state.KnowledgeLibraries));
        Assert.IsEmpty(result.NetChanges); Assert.IsEmpty(result.Conflicts);
    }

    [TestMethod]
    public void NativeSplitCreatesStableNewIdsAndRetainsRequirementsUnresolved()
    {
        var state = Fixture(); var old = state.Baseline.Engineering.Circuit.Nets.Single();
        state = NativeGroups(state, [old.Pins[0]], [old.Pins[1]]);
        string input = SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries);
        var result = SchematicNetReconciliation.Plan(state); Assert.IsNotNull(result.Candidate, result.ErrorMessage);
        Assert.AreEqual(2, result.Candidate.Circuit.Nets.Count);
        Assert.IsFalse(result.Candidate.Circuit.Nets.Any(n => n.Id == old.Id));
        Assert.AreEqual(NetBindingChangeKind.Split, result.NetChanges.Single().Change);
        Assert.AreEqual(2, result.Candidate.Structure.UnresolvedNetBindings!.Count);
        foreach (var expected in state.Baseline.Engineering.Structure.Statements)
        {
            var actual = result.Candidate.Structure.Statements.Single(s => s.Id == expected.Id);
            Assert.AreEqual(expected.TargetId, actual.TargetId);
            Assert.AreEqual(expected.Role, actual.Role); Assert.AreEqual(expected.Strength, actual.Strength);
            Assert.AreEqual(expected.Text, actual.Text); Assert.AreEqual(expected.Connection, actual.Connection);
            CollectionAssert.AreEquivalent(expected.DerivedFrom.ToArray(), actual.DerivedFrom.ToArray());
            CollectionAssert.AreEquivalent(expected.Sources.ToArray(), actual.Sources.ToArray());
        }
        Assert.IsTrue(SchematicElectricalComparison.Compare(state.Baseline with { Engineering = result.Candidate }, state.ObservedElectrical!, state.KnowledgeLibraries).ConnectivityEquivalent);
        string candidate = EngineeringDesignXml.Write(result.Candidate, state.KnowledgeLibraries);
        Assert.AreEqual(candidate, EngineeringDesignXml.Write(SchematicNetReconciliation.Plan(state).Candidate!, state.KnowledgeLibraries));
        var reversed = state.ObservedElectrical!.Clone(); var nets = reversed.Nets.Reverse().ToArray(); reversed.Nets.Clear(); reversed.Nets.Add(nets);
        Assert.AreEqual(candidate, EngineeringDesignXml.Write(SchematicNetReconciliation.Plan(state with { ObservedElectrical = reversed }).Candidate!, state.KnowledgeLibraries));
        Assert.AreEqual(input, SchematicDesignXml.Write(state.Baseline, state.KnowledgeLibraries));
    }

    [TestMethod]
    public void NativeJoinAndIndependentXmlJoinBothSurvive()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
        var a = circuit.Components[0].Id; var b = circuit.Components[1].Id;
        var desiredNet = new CircuitNet(Guid.NewGuid(), "XML connection", [new(a, "1"), new(b, "1")]);
        state = Desired(state, state.Baseline.Engineering with { Circuit = circuit with { Nets = [.. circuit.Nets, desiredNet] } });
        state = NativeGroups(state, circuit.Nets[0].Pins.ToArray(), [new(a, "7"), new(b, "7")]);
        var result = SchematicNetReconciliation.Plan(state); Assert.IsNotNull(result.Candidate, result.ErrorMessage);
        Assert.AreEqual(3, result.Candidate.Circuit.Nets.Count);
        var preserved = result.Candidate.Circuit.Nets.Single(n => n.Id == desiredNet.Id);
        Assert.AreEqual(desiredNet.Name, preserved.Name);
        CollectionAssert.AreEquivalent(desiredNet.Pins.ToArray(), preserved.Pins.ToArray());
        Assert.IsTrue(result.Candidate.Circuit.Nets.Any(n => n.Pins.Contains(new(a, "7")) && n.Pins.Contains(new(b, "7"))));
        Assert.IsEmpty(result.NetChanges);
    }

    [TestMethod]
    public void ConvergentExplicitXmlSplitKeepsItsSelectedIds()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var old = circuit.Nets[0];
        var left = old with { Pins = [old.Pins[0]], Name = "Chosen left" };
        var right = new CircuitNet(Guid.NewGuid(), "Chosen right", [old.Pins[1]]);
        state = Desired(state, state.Baseline.Engineering with { Circuit = circuit with { Nets = [left, right] } });
        state = NativeGroups(state, [old.Pins[0]], [old.Pins[1]]);
        var result = SchematicNetReconciliation.Plan(state); Assert.IsNotNull(result.Candidate, result.ErrorMessage);
        CollectionAssert.AreEquivalent(new[] { left.Id, right.Id }, result.Candidate.Circuit.Nets.Select(n => n.Id).ToArray());
        Assert.IsFalse(result.Candidate.Structure.HasUnresolvedNetBindings);
        Assert.IsEmpty(result.NetChanges);
    }

    [TestMethod]
    public void OverlappingSplitAndJoinReturnsNoPartialCandidate()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var old = circuit.Nets[0];
        state = Desired(state, state.Baseline.Engineering with { Circuit = circuit with
        { Nets = [old with { Pins = [.. old.Pins, new(circuit.Components[0].Id, "1")] }] } });
        state = NativeGroups(state, [old.Pins[0]], [old.Pins[1]]);
        byte[] wanted = state.DesiredFileBytes.ToArray();
        var result = SchematicNetReconciliation.Plan(state);
        Assert.IsNull(result.Candidate); Assert.IsNotEmpty(result.Conflicts); Assert.IsEmpty(result.NetChanges);
        CollectionAssert.AreEqual(wanted, state.DesiredFileBytes);
    }

    [TestMethod]
    public void MissingBaselinePendingMutationChangedOwnershipAndInvalidXmlStayUnresolved()
    {
        var state = Fixture();
        Assert.AreEqual("missing_electrical_baseline", SchematicNetReconciliation.Plan(state with { BaselineElectrical = null }).ErrorCode);
        Assert.AreEqual("missing_electrical_observation", SchematicNetReconciliation.Plan(state with { ObservedElectrical = null }).ErrorCode);
        Assert.AreEqual("pending_recovery_requires_reconciliation", SchematicNetReconciliation.Plan(state with { PendingMutation = DesignRecoveryStoreTests.Mutation(state) }).ErrorCode);
        Assert.AreEqual("invalid_desired_design", SchematicNetReconciliation.Plan(state with { DesiredFileBytes = [0xff] }).ErrorCode);
        var desired = state.Baseline.Engineering with { Circuit = state.Baseline.Engineering.Circuit with
        { Symbols = state.Baseline.Engineering.Circuit.Symbols.Skip(1).ToArray() } };
        Assert.AreEqual("electrical_ownership_changed", SchematicNetReconciliation.Plan(Desired(state, desired)).ErrorCode);
        var changed = state.ObservedElectrical!.Clone(); var symbol = changed.Hierarchy.Data.Instances[1].Items.First(p => p.Is(SchematicSymbolInstance.Descriptor));
        var decoded = symbol.Unpack<SchematicSymbolInstance>(); decoded.Definition.UnitCount++;
        int index = changed.Hierarchy.Data.Instances[1].Items.IndexOf(symbol); changed.Hierarchy.Data.Instances[1].Items[index] = Any.Pack(decoded);
        Assert.AreEqual("electrical_ownership_changed", SchematicNetReconciliation.Plan(state with
        { Observed = changed.Hierarchy.Data.Clone(), ObservedElectrical = changed }).ErrorCode);
        Assert.ThrowsExactly<OperationCanceledException>(() => SchematicNetReconciliation.Plan(state, new(true)));
    }

    [TestMethod]
    public void GeneratedIdCannotSilentlyReuseARetiredExplicitNetIdentity()
    {
        var state = Fixture(); var old = state.Baseline.Engineering.Circuit.Nets.Single();
        state = NativeGroups(state, [old.Pins[0]], [old.Pins[1]]);
        var initial = SchematicNetReconciliation.Plan(state); Assert.IsNotNull(initial.Candidate);
        var collision = initial.Candidate.Circuit.Nets[0].Id;
        var desired = state.Baseline.Engineering with
        {
            Circuit = state.Baseline.Engineering.Circuit with { Nets = [old with { Id = collision }] },
            Structure = new(Guid.NewGuid(), [], [], [], [])
        };
        var result = SchematicNetReconciliation.Plan(Desired(state, desired));
        Assert.IsNull(result.Candidate);
        Assert.AreEqual("generated_net_identity_conflict", result.ErrorCode);
    }

    [TestMethod]
    public void FurtherChangesUpdateOnlyExplicitCandidatePossibilities()
    {
        var f = UnresolvedNetBindingTests.Split();
        var next = f.After with { Nets = [new(Guid.NewGuid(), "Merged again", f.Before.Nets[0].Pins)] };
        var result = f.Pending.RetainUnresolvedNets(f.After, next, f.After.Nets.Select(n =>
            new NetIdentityChange(n.Id, NetBindingChangeKind.Merged, "Candidate merged", [next.Nets[0].Id])).ToArray());
        Assert.AreEqual(f.Pending.Statements[0], result.Statements[0]);
        Assert.AreEqual(f.Pending.UnresolvedNetBindings![0].FormerNetId, result.UnresolvedNetBindings![0].FormerNetId);
        Assert.IsTrue(result.UnresolvedNetBindings.All(b => b.CandidateNetIds.SequenceEqual([next.Nets[0].Id])));
        Assert.IsTrue(result.HasUnresolvedNetBindings);
    }
}
