using System.Text;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

// CN-1 classification and preparation guards (cn1-wiring-intent.md §4.1 and §4.4). These are unit tests
// of isolated pure logic: no editor can exercise an admitted connected addition until native advertises
// schematic.connection-realization.v1, which it does not yet. The rendered NativeXmlComponentCreation
// journey checks the same gate against a real handshake and captured state, and the existing plan tests
// here pin the unchanged general-path results.
public sealed partial class SchematicSynchronizationPlanTests
{
    [TestMethod]
    public void ConnectionsAddedOverDrawnPinsAreAdmittedOnlyWhenTheEditorAdvertisesRealization()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var vcc = circuit.Nets.Single();
        Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id, signal = Guid.NewGuid();
        foreach (var (problem, edit, changed) in new (string, Func<Circuit, Circuit>, Guid[])[]
        {
            ("a new net joins drawn unit-1 pins on both repeated sheets",
                c => c with { Nets = [.. c.Nets, new(signal, "SIG", [new(u1, "1"), new(u2, "1")])] }, [signal]),
            ("an existing net gains a drawn pin", c => c with { Nets = [vcc with { Pins = [.. vcc.Pins, new(u1, "1")] }] }, [vcc.Id]),
            ("a rename in the same revision", c => c with { Nets = [vcc with { Name = "VDD", Pins = [.. vcc.Pins, new(u2, "7")] }] }, [vcc.Id]),
            ("a new net whose pins are all new to the circuit's nets",
                c => c with { Nets = [.. c.Nets, new(signal, "SIG", [new(u1, "7"), new(u2, "1"), new(u2, "7")])] }, [signal])
        })
        {
            var (saved, desired) = ConnectedRevision(state, edit);
            var admitted = ClassifyConnected(saved, desired);
            Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, admitted.Kind, problem);
            CollectionAssert.AreEqual(changed, admitted.ChangedNetIds.ToArray(), problem);
            Assert.IsEmpty(admitted.AddedComponentIds, problem);
            Assert.IsNull(admitted.ErrorCode, problem);
            RequireConnectionGate(saved, desired, problem);
            var plan = SchematicSynchronizationPlanner.Plan(saved);
            Assert.IsNull(plan.Connections, problem);
            Assert.IsTrue(plan.CanPrepare, problem + ": " + plan.ErrorCode + " " + plan.ErrorMessage);
            Assert.IsTrue(plan.NativeConnectivityValidationRequired, problem);
            Assert.IsFalse(plan.ObservedConnectivity!.ConnectivityEquivalent, "The general path still defers the unrealized connection: " + problem);
        }
    }

    [TestMethod]
    public void RevisionsThatNeedNoNewNativeConnectionStayOnTheGeneralPath()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var vcc = circuit.Nets.Single();
        Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id;
        CircuitNet Signal() => new(Guid.NewGuid(), "SIG", [new(u1, "1"), new(u2, "1")]);
        Circuit Additive(Circuit c) => c with { Nets = [.. c.Nets, Signal()] };
        foreach (var (problem, edit) in new (string, Func<SchematicDesign, SchematicDesign>)[]
        {
            ("no change", d => d),
            ("a net rename only", d => WithCircuit(d, c => c with { Nets = [vcc with { Name = "VDD" }] })),
            ("a requirement-only edit", d => d with { Engineering = d.Engineering with { Structure = d.Engineering.Structure with
                { Statements = d.Engineering.Structure.Statements.Select(s => s with { Text = s.Text + " Keep it quiet." }).ToArray() } } }),
            ("a new net with a single drawn pin", d => WithCircuit(d, c => c with { Nets = [.. c.Nets, new(Guid.NewGuid(), "LONE", [new(u1, "1")])] })),
            ("another circuit", d => WithCircuit(d, c => Additive(c) with { Id = Guid.NewGuid() })),
            ("a changed component reference", d => WithCircuit(d, c => Additive(c) with
                { Components = c.Components.Select(x => x.Id == u1 ? x with { Reference = "U9" } : x).ToArray() })),
            ("a changed symbol placement", d => WithCircuit(d, c => Additive(c) with
                { Symbols = c.Symbols.Select((s, i) => i == 0 ? s with { Placement = s.Placement! with { XMillimeters = 99 } } : s).ToArray() })),
            ("a changed part pin", d => WithCircuit(d, c => Additive(c) with
                { Parts = c.Parts.Select(p => p with { Pins = p.Pins.Select(x => x.Number == "7" ? x with { Name = "RENAMED" } : x).ToArray() }).ToArray() })),
            ("a changed component definition value", d => WithCircuit(d, c => Additive(c) with
                { Sheets = c.Sheets.Select(s => s with { Components = s.Components.Select(x => x with { Value = "Other value" }).ToArray() }).ToArray() })),
            ("a changed native binding", d => WithCircuit(d, Additive) with
                { SymbolBindings = d.SymbolBindings.Select((b, i) => i == 0 ? b with { NativeObjectId = Guid.NewGuid() } : b).ToArray() }),
            ("a desired native hierarchy change", d =>
            {
                var schematic = d.Schematic.Clone(); schematic.Instances[0].Metadata.TitleBlock.Title = "XML title";
                return WithCircuit(d, Additive) with { Schematic = schematic };
            }),
            ("a reparented sheet instance", d => WithCircuit(d, c =>
            {
                var children = c.SheetInstances.Where(s => s.ParentId is not null).ToArray();
                return Additive(c) with { SheetInstances = c.SheetInstances.Select(s => s.Id == children[1].Id ? s with { ParentId = children[0].Id } : s).ToArray() };
            })),
            ("a new sheet definition", d => WithCircuit(d, c => Additive(c) with { Sheets = [.. c.Sheets, new(Guid.NewGuid(), "Spare", [])] }))
        })
        {
            var design = edit(state.Baseline);
            var saved = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
            var desired = DesignRecoveryStore.ReadDesired(saved);
            var result = ClassifyConnected(saved, desired);
            Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, result.Kind, problem);
            Assert.IsEmpty(result.ChangedNetIds, problem); Assert.IsEmpty(result.AddedComponentIds, problem); Assert.IsNull(result.ErrorCode, problem);
        }

        // Unstable editor that already shows the requested connection: the general path publishes it.
        var (joined, wanted) = ConnectedRevision(state, c => c with { Nets = [.. c.Nets, new(Guid.NewGuid(), "SIG", [new(u1, "1"), new(u2, "1")])] });
        var realized = SchematicNetReconciliationTests.NativeGroups(joined, [.. vcc.Pins], [new(u1, "1"), new(u2, "1")]);
        Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, ClassifyConnected(realized, wanted).Kind, "native already realizes the net");
        // Unstable editor that does not show it: still an addition to realize, and the preparation guards refuse it.
        var split = SchematicNetReconciliationTests.NativeGroups(joined, [vcc.Pins[0]], [vcc.Pins[1]]);
        var shape = ClassifyConnected(split, wanted);
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, shape.Kind, "unstable editor without the connection");
        Assert.AreEqual(SchematicConnectionErrors.CreationRequiresStableConnectivity, PrepareConnected(split, wanted, shape).ErrorCode);
    }

    [TestMethod]
    public void DrawingAnotherUnitOfAnExistingComponentIsNotAConnectedAddition()
    {
        // Must-catch for §4.1 step 2: the saved design draws only unit 1 of U2, and the XML adds U2's unit-2
        // occurrence and connects its pin 7. That pin counts as drawn in the desired circuit, but the change
        // adds an occurrence to an existing owner, which creation refuses (symbol_owner_requires_resolution)
        // and CN-1 does not cover, so it must stay on the general path instead of being admitted.
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
        Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id, signal = Guid.NewGuid();
        var unit2 = circuit.Symbols.Single(s => s.ComponentId == u2 && s.Unit == 2);
        var bindings = state.Baseline.SymbolBindings.Where(b => b.SymbolOccurrenceId != unit2.Id).ToArray();
        var saved = state with { Baseline = WithCircuit(state.Baseline, c => c with { Symbols = c.Symbols.Where(s => s.Id != unit2.Id).ToArray() })
            with { SymbolBindings = bindings } };
        var drawnUnit = WithCircuit(state.Baseline, c => c with { Nets = [.. c.Nets, new(signal, "SIG", [new(u1, "7"), new(u2, "7")])] })
            with { SymbolBindings = bindings };
        Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, ClassifyConnected(saved, drawnUnit).Kind, "a new unit of an existing component");
        // False-positive guard: the same connection over units the saved design already draws is admitted.
        var drawn = WithCircuit(state.Baseline, c => c with { Nets = [.. c.Nets, new(signal, "SIG", [new(u1, "7"), new(u2, "7")])] });
        var admitted = ClassifyConnected(state, drawn);
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, admitted.Kind, admitted.ErrorCode + " " + admitted.ErrorMessage);
        CollectionAssert.AreEqual(new[] { signal }, admitted.ChangedNetIds.ToArray());
    }

    [TestMethod]
    public void XmlDisconnectionIsRefusedOnlyWhileTheEditorStillShowsTheBaseline()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var vcc = circuit.Nets.Single();
        Guid u2 = circuit.Components[1].Id;
        string Reference(PinEndpoint pin) => circuit.Components.Single(c => c.Id == pin.ComponentId).Reference + "." + pin.Pin;
        foreach (var (problem, edit, lost) in new (string, Func<Circuit, Circuit>, PinEndpoint)[]
        {
            ("a drawn pin removed from its net", c => c with { Nets = [vcc with { Pins = vcc.Pins.Where(p => p.ComponentId != u2).ToArray() }] }, new(u2, "8")),
            ("a pin moved to a new net alongside an addition", c => c with { Nets =
                [vcc with { Pins = vcc.Pins.Where(p => p.ComponentId != u2).ToArray() }, new(Guid.NewGuid(), "MOVED", [new(u2, "8"), new(u2, "1")])] }, new(u2, "8"))
        })
        {
            var (saved, desired) = ConnectedRevision(state, edit);
            var rejected = ClassifyConnected(saved, desired);
            Assert.AreEqual(SchematicConnectedAdditionKind.Rejected, rejected.Kind, problem);
            Assert.AreEqual(SchematicConnectionErrors.XmlDisconnectionUnsupported, rejected.ErrorCode, problem);
            StringAssert.Contains(rejected.ErrorMessage!, "pin " + Reference(lost) + " from net 'VCC'", problem);
            Assert.IsEmpty(rejected.ChangedNetIds, problem); Assert.IsEmpty(rejected.AddedComponentIds, problem);
            RequireConnectionGate(saved, desired, problem);
            // Today's general path prepares the XML disconnection and leaves it to native connectivity validation.
            var plan = SchematicSynchronizationPlanner.Plan(saved);
            Assert.IsNull(plan.Connections, problem);
            Assert.IsNull(plan.ErrorCode, problem + ": " + plan.ErrorCode + " " + plan.ErrorMessage);
            Assert.IsTrue(plan.CanPrepare, problem);
            Assert.IsTrue(plan.NativeConnectivityValidationRequired, problem);
            Assert.IsFalse(plan.ObservedConnectivity!.ConnectivityEquivalent, "The general path still defers the unrealized disconnection: " + problem);
            // When the editor no longer shows the baseline, the general path may merge a native disconnection.
            var unstable = SchematicNetReconciliationTests.NativeGroups(saved, [vcc.Pins[0]], [vcc.Pins[1]]);
            Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, ClassifyConnected(unstable, desired).Kind, problem);
        }
    }

    [TestMethod]
    public void ConnectedComponentCreationIsAdmittedAndPreparedThroughTheCreationGuards()
    {
        var state = Fixture(); var baseline = state.Baseline;
        var creation = SchematicNativeCreationProjectionTests.AddComponent(baseline);
        var existing = baseline.Engineering.Circuit.Components.Select(c => c.Id).ToHashSet();
        var created = creation.Circuit.Components.Where(c => !existing.Contains(c.Id)).Select(c => c.Id).Order().ToArray();
        Guid net = Guid.NewGuid(), u1 = baseline.Engineering.Circuit.Components[0].Id;
        var (saved, desired) = ConnectedRevision(state, _ => creation.Circuit with
            { Nets = [.. creation.Circuit.Nets, new(net, "PROBE_OUT", [new(created[0], "1"), new(u1, "1")])] });
        Assert.IsFalse(SchematicNativeCreationProjection.IsSupportedAddition(baseline, desired.Engineering), "connected creation is not the unconnected shape");
        var admitted = ClassifyConnected(saved, desired);
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, admitted.Kind);
        CollectionAssert.AreEqual(created, admitted.AddedComponentIds.ToArray());
        CollectionAssert.AreEqual(new[] { net }, admitted.ChangedNetIds.ToArray());
        RequireConnectionGate(saved, desired, "connected creation");
        var plan = SchematicSynchronizationPlanner.Plan(saved);
        Assert.IsNull(plan.Connections);
        Assert.AreEqual("electrical_ownership_changed", plan.ErrorCode, "The general path keeps refusing connected creation.");

        // Every creation guard runs first; the connected projection then succeeds, and preparation stops
        // only because the connection intent is not in this build.
        var ready = PrepareConnected(saved, desired, admitted);
        Assert.AreEqual(SchematicConnectionErrors.ConnectedAdditionUnavailable, ready.ErrorCode, ready.ErrorMessage);
        Assert.IsFalse(ready.CanPrepare); Assert.IsNull(ready.Candidate); Assert.IsNull(ready.CandidateXml); Assert.IsEmpty(ready.NativeOperations);
        Assert.IsNull(ready.Connections);
        var changed = saved.ObservedElectrical!.Clone(); changed.Nets.Clear();
        var foreign = saved.BaselineElectrical!.Clone(); foreign.Nets.Clear();
        var bindingChange = desired with { SymbolBindings = desired.SymbolBindings.Select((b, i) => i == 0 ? b with { NativeObjectId = Guid.NewGuid() } : b).ToArray() };
        var titled = desired.Schematic.Clone(); titled.Instances[0].Metadata.TitleBlock.Title = "XML title";
        var observedTitle = saved.Observed.Clone(); observedTitle.Instances[0].Metadata.TitleBlock.Title = "Native title";
        var placementFree = desired with { Engineering = desired.Engineering with { Circuit = desired.Engineering.Circuit with
            { Symbols = desired.Engineering.Circuit.Symbols.Select(s => created.Contains(s.ComponentId) ? s with { Placement = null } : s).ToArray() } } };
        foreach (var (problem, input, wanted, code) in new (string, DesignRecoveryState, SchematicDesign, string)[]
        {
            ("missing observation", saved with { ObservedElectrical = null }, desired, "missing_electrical_observation"),
            ("pending hierarchy choice", saved with { HierarchyResolution = new("token", new Dictionary<string, SchematicConflictChoice>(), "epoch", 1) },
                desired, SchematicConnectionErrors.CreationRequiresStableNativeHierarchy),
            ("pending ownership choice", saved with { OwnershipResolution = new(new string('a', 64), Guid.NewGuid(), new string('b', 64)) },
                desired, SchematicConnectionErrors.CreationRequiresStableNativeHierarchy),
            ("changed bindings", saved, bindingChange, SchematicConnectionErrors.CreationBindingsChanged),
            ("desired native hierarchy edit", saved, desired with { Schematic = titled }, SchematicConnectionErrors.CreationRequiresStableNativeHierarchy),
            ("concurrent native hierarchy edit", Observe(saved, observedTitle), desired, SchematicConnectionErrors.CreationRequiresStableNativeHierarchy),
            ("baseline not aligned with its native partition", saved with { BaselineElectrical = foreign }, desired, SchematicConnectionErrors.UnalignedElectricalBaseline),
            ("changed native connectivity", saved with { ObservedElectrical = changed }, desired, SchematicConnectionErrors.CreationRequiresStableConnectivity),
            ("coordinate-free created symbols", saved, placementFree, SchematicConnectionErrors.CreatedSymbolPlacementRequired)
        })
        {
            var refused = PrepareConnected(input, wanted, admitted);
            Assert.AreEqual(code, refused.ErrorCode, problem + ": " + refused.ErrorMessage);
            Assert.IsNull(refused.Candidate, problem); Assert.IsEmpty(refused.NativeOperations, problem);
        }
        Assert.ThrowsExactly<ArgumentException>(() => PrepareConnected(saved, desired, admitted with { Kind = SchematicConnectedAdditionKind.NotApplicable }));
    }

    [TestMethod]
    public void FrozenPsuCpuFixtureIsClassifiedByItsExactNetsAndDrawnUnits()
    {
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit;
        SchematicDesign Connected(SchematicDesign design, IReadOnlyList<CircuitNet> nets) =>
            design with { Engineering = design.Engineering with { Circuit = design.Engineering.Circuit with { Nets = nets } } };
        Assert.AreEqual(41, complete.Nets.Sum(n => n.Pins.Count));

        // S1 sheets to the complete circuit: eight created components and all eleven nets.
        var fromSheets = PsuCpuConnectedState(sheets);
        var createAndConnect = Connected(components, complete.Nets);
        var admitted = ClassifyConnected(fromSheets, createAndConnect);
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, admitted.Kind);
        CollectionAssert.AreEqual(components.Engineering.Circuit.Components.Select(c => c.Id).Order().ToArray(), admitted.AddedComponentIds.ToArray());
        CollectionAssert.AreEqual(complete.Nets.Select(n => n.Id).Order().ToArray(), admitted.ChangedNetIds.ToArray());
        RequireConnectionGate(fromSheets, createAndConnect, "S1 to Complete");
        var withBytes = fromSheets with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(createAndConnect, [])) };
        Assert.AreEqual("electrical_ownership_changed", SchematicSynchronizationPlanner.Plan(withBytes).ErrorCode);
        Assert.AreEqual(SchematicConnectionErrors.ConnectedAdditionUnavailable, PrepareConnected(fromSheets, createAndConnect, admitted).ErrorCode,
            "The eleven placements must pass creation with their connections allowed.");
        Assert.AreEqual(SchematicConnectionErrors.CreatedSymbolPlacementRequired,
            PrepareConnected(fromSheets, Connected(components with { Engineering = PsuCpuFixture.Engineering(PsuCpuStage.Complete) }, complete.Nets), admitted).ErrorCode,
            "The frozen coordinate-free occurrences still need layout before creation.");

        // Components created, then XML connects them: connect-only on a stable editor.
        var placed = SchematicNativeCreationProjection.Project(sheets, components, []).Candidate;
        var unconnected = PsuCpuConnectedState(placed);
        var connectOnly = ClassifyConnected(unconnected, Connected(placed, complete.Nets));
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, connectOnly.Kind);
        Assert.IsEmpty(connectOnly.AddedComponentIds);
        CollectionAssert.AreEqual(complete.Nets.Select(n => n.Id).Order().ToArray(), connectOnly.ChangedNetIds.ToArray());
        Assert.AreEqual(SchematicConnectionErrors.ConnectedAdditionUnavailable, PrepareConnected(unconnected, Connected(placed, complete.Nets), connectOnly).ErrorCode);

        // The realized complete circuit: removing the memory write-protect pin from GND is a disconnection,
        // while tying the unresolved memory SDA pin to a free processor GPIO is an addition.
        var realized = Connected(placed, complete.Nets);
        var connectedState = PsuCpuConnectedState(realized);
        Assert.IsTrue(SchematicElectricalComparison.Compare(realized, connectedState.ObservedElectrical!, []).ConnectivityEquivalent,
            "The synthetic native partition must equal the eleven fixture nets.");
        var u6 = realized.Engineering.Circuit.Components.Single(c => c.Reference == "U6").Id;
        var u5 = realized.Engineering.Circuit.Components.Single(c => c.Reference == "U5");
        var gnd = complete.Nets.Single(n => n.Name == "GND");
        var disconnected = ClassifyConnected(connectedState, Connected(realized, complete.Nets.Select(n => n.Id == gnd.Id
            ? n with { Pins = n.Pins.Where(p => p != new PinEndpoint(u6, "7")).ToArray() } : n).ToArray()));
        Assert.AreEqual(SchematicConnectionErrors.XmlDisconnectionUnsupported, disconnected.ErrorCode);
        StringAssert.Contains(disconnected.ErrorMessage!, "pin U6.7 from net 'GND'");
        // Deleting a whole net disconnects its first drawn pin in (component, pin) order: processor U5 (K07:7) before memory U6.
        var deleted = ClassifyConnected(connectedState, Connected(realized, complete.Nets.Where(n => n.Name != "MEM_SCL").ToArray()));
        Assert.AreEqual(SchematicConnectedAdditionKind.Rejected, deleted.Kind);
        StringAssert.Contains(deleted.ErrorMessage!, "pin U5.161 from net 'MEM_SCL'");
        // Removing a component, even with its connected pins, is not an XML disconnection to refuse: the
        // shape check leaves it to the general path on this stable editor.
        var memory = realized.Engineering.Circuit.Components.Single(c => c.Id == u6);
        var removed = ClassifyConnected(connectedState, WithCircuit(realized, c => c with
        {
            Components = c.Components.Where(x => x.Id != u6).ToArray(), Symbols = c.Symbols.Where(s => s.ComponentId != u6).ToArray(),
            Sheets = c.Sheets.Select(s => s with { Components = s.Components.Where(d => d.Id != memory.DefinitionId).ToArray() }).ToArray(),
            Nets = c.Nets.Select(n => n with { Pins = n.Pins.Where(p => p.ComponentId != u6).ToArray() }).ToArray()
        }));
        Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, removed.Kind, removed.ErrorCode + " " + removed.ErrorMessage);
        Assert.IsNull(removed.ErrorCode);
        var used = complete.Nets.SelectMany(n => n.Pins).ToHashSet();
        var processor = realized.Engineering.Circuit.Parts.Single(p => p.Id == realized.Engineering.Circuit.Sheets
            .SelectMany(s => s.Components).Single(d => d.Id == u5.DefinitionId).PartId);
        var gpio = processor.Pins.Where(p => p.Unit == 2 && !used.Contains(new(u5.Id, p.Number))).OrderBy(p => p.Number, StringComparer.Ordinal).First();
        Guid sda = Guid.NewGuid();
        var memoryData = ClassifyConnected(connectedState, Connected(realized, [.. complete.Nets, new(sda, "MEM_SDA", [new(u5.Id, gpio.Number), new(u6, "5")])]));
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, memoryData.Kind);
        CollectionAssert.AreEqual(new[] { sda }, memoryData.ChangedNetIds.ToArray());
    }

    [TestMethod]
    public void OnlyDrawnPinsCountAsAddedOrLostConnections()
    {
        // Isolated drawn-endpoint rule (cn1-wiring-intent.md §2): a unit pin counts only when its unit has
        // an occurrence, and a common pin when the component has any occurrence.
        var circuit = CircuitXmlTests.Fixture();
        Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id, vcc = circuit.Nets[0].Id;
        var noUnit2 = circuit with { Symbols = circuit.Symbols.Where(s => !(s.ComponentId == u2 && s.Unit == 2)).ToArray() };
        var undrawnU2 = circuit with { Symbols = circuit.Symbols.Where(s => s.ComponentId != u2).ToArray() };
        noUnit2.Validate(); undrawnU2.Validate();
        Circuit Nets(Circuit c, params CircuitNet[] nets) => c with { Nets = nets };

        var unchanged = SchematicConnectedAddition.Delta(noUnit2, noUnit2)!;
        Assert.IsEmpty(unchanged.Added); Assert.IsFalse(unchanged.Lost); Assert.IsEmpty(unchanged.AddedComponentIds);

        Guid probe = Guid.NewGuid();
        var undrawnPin = SchematicConnectedAddition.Delta(noUnit2, Nets(noUnit2, circuit.Nets[0], new CircuitNet(probe, "PROBE", [new(u1, "7"), new(u2, "7")])))!;
        CollectionAssert.AreEqual(new[] { new PinEndpoint(u1, "7") }, undrawnPin.Added[probe].ToArray(), "U2 unit 2 is not drawn");
        Assert.AreEqual(1, undrawnPin.DrawnMembers[probe]);

        var drawnCommon = SchematicConnectedAddition.Delta(noUnit2, Nets(noUnit2, circuit.Nets[0] with { Pins = [.. circuit.Nets[0].Pins, new(u2, "1")] }))!;
        CollectionAssert.AreEqual(new[] { new PinEndpoint(u2, "1") }, drawnCommon.Added[vcc].ToArray());
        Assert.AreEqual(3, drawnCommon.DrawnMembers[vcc], "U2's common pin 8 stays drawn through its unit 1.");

        var removedUndrawn = SchematicConnectedAddition.Delta(undrawnU2, Nets(undrawnU2, circuit.Nets[0] with { Pins = [new(u1, "8")] }))!;
        Assert.IsFalse(removedUndrawn.Lost, "Removing a pin that is not drawn anywhere disconnects nothing natively.");
        var removedDrawn = SchematicConnectedAddition.Delta(noUnit2, Nets(noUnit2, circuit.Nets[0] with { Pins = [new(u1, "8")] }))!;
        Assert.IsTrue(removedDrawn.Lost); Assert.AreEqual(new PinEndpoint(u2, "8"), removedDrawn.FirstLost); Assert.AreEqual(vcc, removedDrawn.FirstLostNet);

        Assert.IsNull(SchematicConnectedAddition.Delta(circuit, Nets(circuit, new CircuitNet(probe, "PROBE", [new(u1, "99")]))), "An unknown pin is left to the general path.");
    }

    private static (DesignRecoveryState Saved, SchematicDesign Desired) ConnectedRevision(DesignRecoveryState state, Func<Circuit, Circuit> edit)
    {
        var design = WithCircuit(state.Baseline, edit);
        var saved = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
        return (saved, DesignRecoveryStore.ReadDesired(saved));
    }

    private static SchematicDesign WithCircuit(SchematicDesign design, Func<Circuit, Circuit> edit) =>
        design with { Engineering = design.Engineering with { Circuit = edit(design.Engineering.Circuit) } };

    private static AutomationSession ConnectedSession(Guid instance, bool realization)
    {
        var session = new AutomationSession { InstanceId = instance.ToString("D") };
        session.Capabilities.Add("session.info"); session.Capabilities.Add("version.read");
        if (realization) session.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
        return session;
    }

    private static SchematicConnectedAdditionClassification ClassifyConnected(DesignRecoveryState state, SchematicDesign desired) =>
        SchematicConnectedAddition.Classify(state, desired, ConnectedSession(state.InstanceId, realization: true));

    // Without the recorded instance's handshake advertising realization, nothing is admitted or refused,
    // and the planning seam (which has no session) sees exactly the freeze behaviour.
    private static void RequireConnectionGate(DesignRecoveryState state, SchematicDesign desired, string problem)
    {
        foreach (var (name, session) in new (string, AutomationSession?)[]
        {
            ("planning seam without a session", null),
            ("today's editor capabilities", ConnectedSession(state.InstanceId, realization: false)),
            ("another instance's handshake", ConnectedSession(Guid.NewGuid(), realization: true))
        })
        {
            var result = SchematicConnectedAddition.Classify(state, desired, session);
            Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, result.Kind, problem + " / " + name);
            Assert.IsEmpty(result.ChangedNetIds); Assert.IsEmpty(result.AddedComponentIds); Assert.IsNull(result.ErrorCode);
        }
        Assert.AreEqual(SchematicConnectedAdditionKind.NotApplicable, SchematicConnectedAddition.Classify(state, desired).Kind, problem);
        Assert.IsFalse(SchematicConnectedAddition.Advertises(ConnectedSession(Guid.Empty, realization: true), Guid.Empty), "An empty instance is no evidence.");
    }

    private static SchematicSynchronizationPlan PrepareConnected(DesignRecoveryState state, SchematicDesign desired,
        SchematicConnectedAdditionClassification shape)
    {
        var hierarchy = SchematicHierarchyMerge.Plan(state.Baseline.Schematic, desired.Schematic, state.Observed);
        return SchematicConnectedAdditionPlanner.Prepare(state, desired, hierarchy, shape, []);
    }

    /// <summary>A stable recovery record whose editor shows <paramref name="baseline"/> exactly: the native
    /// partition is built from the baseline's own nets through its exact placed-pin identities.</summary>
    private static DesignRecoveryState PsuCpuConnectedState(SchematicDesign baseline)
    {
        var electrical = new SchematicElectricalState { Hierarchy = new() { Data = baseline.Schematic.Clone(),
            Revision = new() { Epoch = "psu-cpu-epoch", Sequence = 7 }, TrackingComplete = false } };
        var circuit = baseline.Engineering.Circuit;
        var components = circuit.Components.ToDictionary(c => c.Id);
        var paths = baseline.SheetBindings.ToDictionary(b => b.SheetInstanceId, b => SchematicDesignBindings.PathKey(b.NativePath));
        var natives = baseline.SymbolBindings.ToDictionary(b => b.SymbolOccurrenceId, b => b.NativeObjectId.ToString("D"));
        var screens = baseline.Schematic.Instances.ToDictionary(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)), StringComparer.Ordinal);
        foreach (var net in circuit.Nets.OrderBy(n => n.Id))
        {
            var sheets = new SortedDictionary<string, SchematicNetSheetContents>(StringComparer.Ordinal);
            foreach (var endpoint in net.Pins)
            foreach (var occurrence in circuit.Symbols.Where(s => s.ComponentId == endpoint.ComponentId))
            {
                string path = paths[occurrence.EffectiveSheetInstanceId(components[endpoint.ComponentId])];
                var screen = screens[path];
                var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                    .Single(s => s.Id.Value == natives[occurrence.Id]);
                foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
                {
                    if ((child.Unit?.Unit is > 0 && child.Unit.Unit != occurrence.Unit)
                        || (child.BodyStyle?.Style is > 0 && child.BodyStyle.Style != (symbol.BodyStyle?.Style ?? 1))) continue;
                    var pin = child.Item.Unpack<SchematicPin>();
                    if (pin.LibraryPinId is null || pin.Number != endpoint.Pin) continue;
                    if (!sheets.TryGetValue(path, out var contents))
                        sheets.Add(path, contents = new() { Path = screen.Metadata.Document.SheetPath.Clone() });
                    contents.Items.Add(pin.Id.Clone());
                }
            }
            if (sheets.Count == 0) continue;
            var native = new SchematicNet { Name = "/" + net.Name };
            native.Sheets.Add(sheets.Values);
            electrical.Nets.Add(native);
        }
        return new(Guid.NewGuid(), Guid.NewGuid(), new("psu-cpu-epoch", 7), false, baseline,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, [])), baseline.Schematic.Clone(), [],
            BaselineElectrical: electrical, ObservedElectrical: electrical.Clone());
    }
}
