using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ElectricalPinType = Kiapi.Common.Types.ElectricalPinType;
using KIID = Kiapi.Common.Types.KIID;
using LockedState = Kiapi.Common.Types.LockedState;

namespace KiCad.Automation.Tests;

// CN-1 classification, preparation guards and connection intent (cn1-wiring-intent.md §4.1, §4.4 and §5).
// These are unit tests of isolated pure logic: no editor can exercise an admitted connected addition until
// native advertises schematic.connection-realization.v1, which it does not yet, and nothing can apply the
// realizer's wires before lane 2C's native assertion exists. The rendered NativeXmlComponentCreation journey checks
// the same gate, builds intents from real handshakes and captured states and realizes them against the editor's own
// measurements without applying them; the existing plan tests here pin the unchanged general-path results.
public sealed partial class SchematicSynchronizationPlanTests
{
    [TestMethod]
    public void ConnectionsAddedOverDrawnPinsAreAdmittedOnlyWhenTheEditorAdvertisesRealization()
    {
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit; var vcc = circuit.Nets.Single();
        Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id, signal = Guid.NewGuid();
        // Preparation (§5.6): U1 and U2 are the two instances of one repeated channel sheet and share its symbols,
        // so only a connection drawn identically on both instances can be realized; every other one is refused.
        foreach (var (problem, edit, changed, prepared) in new (string, Func<Circuit, Circuit>, Guid[], string?)[]
        {
            ("a new net joins drawn unit-1 pins on both repeated sheets",
                c => c with { Nets = [.. c.Nets, new(signal, "SIG", [new(u1, "1"), new(u2, "1")])] }, [signal], null),
            ("an existing net gains a drawn pin", c => c with { Nets = [vcc with { Pins = [.. vcc.Pins, new(u1, "1")] }] }, [vcc.Id],
                SchematicConnectionErrors.ConnectedRepeatedScreenDivergent),
            ("a rename in the same revision", c => c with { Nets = [vcc with { Name = "VDD", Pins = [.. vcc.Pins, new(u2, "7")] }] }, [vcc.Id],
                SchematicConnectionErrors.ConnectedRepeatedScreenDivergent),
            ("a new net whose pins are all new to the circuit's nets",
                c => c with { Nets = [.. c.Nets, new(signal, "SIG", [new(u1, "7"), new(u2, "1"), new(u2, "7")])] }, [signal],
                SchematicConnectionErrors.ConnectedRepeatedScreenDivergent)
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
            var realized = SchematicSynchronizationPlanner.Plan(saved, ConnectedSession(saved.InstanceId, realization: true));
            if (prepared is not null)
            {
                SchematicConnectionIntentBuilderTests.RequireRefusal(saved, prepared, "is used 2 times", problem);
                continue;
            }
            var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(realized);
            var net = intent.Nets.Single();
            Assert.AreEqual(ConnectionScope.Local, net.Scope, problem);
            // One shared hierarchical label on the channel sheet, and one sheet pin per channel on the root.
            var channel = intent.Screens.Single(s => s.InstancePathKeys.Count == 2);
            var shared = channel.Islands.Single();
            Assert.AreEqual(channel.InstancePathKeys[0], shared.SheetPathKey, "The representative instance is the ordinal-first path.");
            Assert.AreEqual("SIG", shared.LabelText); Assert.IsNotNull(shared.UplinkSheetSymbolId);
            Assert.IsTrue(shared.Members.Single().RequiresStub);
            var root = intent.Screens.Single(s => s.InstancePathKeys.Count == 1).Islands.Single();
            Assert.IsEmpty(root.Members); Assert.HasCount(2, root.ChildSheetSymbolIds);
            CollectionAssert.AreEquivalent(root.ChildSheetSymbolIds.ToArray(), intent.Ports.Select(p => p.SheetSymbolId).ToArray());
            Assert.IsTrue(intent.Ports.All(p => p.PortText == "SIG" && !p.SheetPinExists && !p.UplinkLabelExists));
            var group = intent.ExpectedGroups.Single();
            Assert.HasCount(2, group);
            Assert.AreEqual(group[0].PlacedPinId, group[1].PlacedPinId, "Both instances share the one physical pin.");
            CollectionAssert.AreEqual(channel.InstancePathKeys.ToArray(), group.Select(k => k.SheetPathKey).ToArray());
        }
    }

    [TestMethod]
    public void ARealizationPlanIsMeasuredOnlyByAnEditorThatAdvertisesRealization()
    {
        // The executor hands a realization plan to the lane entry point (§9.1 steps 2-3). Without the capability for
        // the recorded instance it is refused with native_capability_missing before anything is measured, journaled
        // or sent. With it, the entry point measures the exact checkpoint first: a measurement that answers for
        // another revision stops it with realization_measurement_stale, still before any native change.
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
        var (saved, _) = ConnectedRevision(state, c => c with { Nets = [.. c.Nets,
            new(Guid.NewGuid(), "SIG", [new(circuit.Components[0].Id, "1"), new(circuit.Components[1].Id, "1")])] });
        var plan = SchematicSynchronizationPlanner.Plan(saved, ConnectedSession(saved.InstanceId, realization: true));
        Assert.IsNotNull(plan.Connections, plan.ErrorCode + " " + plan.ErrorMessage);
        foreach (var (problem, session) in new (string, AutomationSession)[]
        {
            ("today's editor", ConnectedSession(saved.InstanceId, realization: false)),
            ("another instance's handshake", ConnectedSession(Guid.NewGuid(), realization: true))
        })
        {
            var error = Assert.ThrowsExactly<AutomationException>(() => { _ = SchematicConnectedAddition.RealizeAsync((NativeClient)null!, session, saved, plan, null!); }, problem);
            Assert.AreEqual(SchematicConnectionErrors.NativeCapabilityMissing, error.Code, problem + ": " + error.Message);
            StringAssert.Contains(error.Message, "nothing was changed", problem);
        }
        var checkpoint = SchematicConnectionRealizerTests.Checkpoint(saved);
        var requests = new List<MeasureSchematicPlacement>();
        var stale = Assert.ThrowsExactly<AutomationException>(() => SchematicConnectedAddition.RealizeAsync((request, _) =>
        {
            requests.Add(request.Clone());
            var reply = new SchematicPlacementGeometry { Document = request.Document.Clone(), Revision = request.ExpectedRevision.Clone() };
            reply.Revision.Sequence++;
            return Task.FromResult(reply);
        }, ConnectedSession(saved.InstanceId, realization: true), saved, plan, checkpoint).GetAwaiter().GetResult());
        Assert.AreEqual(SchematicConnectionErrors.RealizationMeasurementStale, stale.Code, stale.Message);
        Assert.HasCount(1, requests, "The realizer measures before it decides anything.");
        var measuredRevision = requests[0].ExpectedRevision;
        Assert.AreEqual(checkpoint.State.Revision, measuredRevision);
    }

    [TestMethod]
    public void RepeatedSheetsRealizeOnlyConnectionsThatMatchOnEveryInstance()
    {
        // U1 and U2 share one repeated channel sheet. Each instance joins its own OUT_A and OUT_B pins. With the same
        // name after the last '/', both instances show the same labels and the plan realizes both nets; with
        // different names the one shared drawing cannot carry both, and with only one instance connected the other
        // instance would show the connection too.
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
        Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id;
        CircuitNet Net(string name, Guid component) => new(Guid.NewGuid(), name, [new(component, "1"), new(component, "7")]);
        var (same, _) = ConnectedRevision(state, c => c with { Nets = [.. c.Nets, Net("/CH1/OUT", u1), Net("/CH2/OUT", u2)] });
        var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(SchematicSynchronizationPlanner.Plan(same,
            ConnectedSession(same.InstanceId, realization: true)));
        Assert.HasCount(2, intent.Nets);
        Assert.IsEmpty(intent.Ports, "Each net stays on its own instance.");
        var channel = intent.Screens.Single();
        Assert.HasCount(2, channel.InstancePathKeys);
        var island = channel.Islands.Single();
        Assert.AreEqual("OUT", island.LabelText);
        Assert.HasCount(2, island.Members.Where(m => m.RequiresStub).ToArray());
        Assert.HasCount(2, intent.ExpectedGroups);
        Assert.IsTrue(intent.ExpectedGroups.All(g => g.Select(k => k.SheetPathKey).Distinct().Count() == 1));

        var (different, _) = ConnectedRevision(state, c => c with { Nets = [.. c.Nets, Net("/CH1/OUT", u1), Net("/CH2/RET", u2)] });
        SchematicConnectionIntentBuilderTests.RequireRefusal(different, SchematicConnectionErrors.ConnectedLabelTextDivergent, "'OUT', 'RET'");
        var (single, _) = ConnectedRevision(state, c => c with { Nets = [.. c.Nets, Net("/CH1/OUT", u1)] });
        SchematicConnectionIntentBuilderTests.RequireRefusal(single, SchematicConnectionErrors.ConnectedRepeatedScreenDivergent, "is used 2 times");
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

        // Every creation guard runs first and the connected projection succeeds. The probe then joins one channel's
        // new pin with an existing pin of one channel only, which the shared channel drawing cannot show on just
        // one instance, so the intent refuses it before anything reaches the editor.
        var ready = PrepareConnected(saved, desired, admitted);
        Assert.AreEqual(SchematicConnectionErrors.ConnectedRepeatedScreenDivergent, ready.ErrorCode, ready.ErrorMessage);
        Assert.IsFalse(ready.CanPrepare); Assert.IsNull(ready.Candidate); Assert.IsNull(ready.CandidateXml); Assert.IsEmpty(ready.NativeOperations);
        Assert.IsNull(ready.Connections);
        // The two created channel components share one physical symbol, so joining their pins is the same on both
        // instances: the plan carries the intent, the created symbols and no publishable XML.
        Guid pair = Guid.NewGuid();
        var (pairSaved, pairDesired) = ConnectedRevision(state, _ => creation.Circuit with
            { Nets = [.. creation.Circuit.Nets, new(pair, "PROBE_PAIR", [new(created[0], "1"), new(created[1], "1")])] });
        var pairShape = ClassifyConnected(pairSaved, pairDesired);
        Assert.AreEqual(SchematicConnectedAdditionKind.Admitted, pairShape.Kind);
        var pairPlan = PrepareConnected(pairSaved, pairDesired, pairShape);
        var pairIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(pairPlan);
        CollectionAssert.AreEqual(pairPlan.Candidate!.SymbolBindings.Where(b => pairDesired.Engineering.Circuit.Symbols
                .Any(s => s.Id == b.SymbolOccurrenceId && created.Contains(s.ComponentId))).Select(b => b.NativeObjectId).Distinct().Order().ToArray(),
            pairIntent.CreatedSymbolIds.ToArray());
        Assert.HasCount(2, pairIntent.CreatedSymbolIds, "One shared symbol per unit on the repeated channel sheet.");
        Assert.HasCount(2, pairIntent.Ports);
        var pairGroup = pairIntent.ExpectedGroups.Single(g => g.Count == 2);
        Assert.AreEqual(pairGroup[0].PlacedPinId, pairGroup[1].PlacedPinId);
        Assert.IsTrue(pairIntent.ExpectedGroups.Where(g => g != pairGroup).All(g => g.Count == 1),
            "Every other created pin, on each instance, must stay alone.");
        Assert.HasCount(8, pairIntent.ExpectedGroups.SelectMany(g => g).ToArray(), "Two units with two pins each, on two instances.");
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
        // A saved sheet holding a symbol whose library definition KiCad cannot resolve (captured, as KiCad captures it,
        // with no definition of its own) cannot be matched with the design, so the refusal names that symbol and what to
        // restore. The live creation journey proves the same on a real editor; this pins the wording's precision: a
        // baseline that disagrees for any other reason keeps the general reason and names no symbol.
        var (unresolved, unresolvedDesired) = WithUnresolvedSymbol(saved, desired, "X9");
        var named = PrepareConnected(unresolved, unresolvedDesired, admitted);
        Assert.AreEqual(SchematicConnectionErrors.UnalignedElectricalBaseline, named.ErrorCode, named.ErrorMessage);
        Assert.AreEqual("The saved baseline must agree with its native pin partition. KiCad cannot resolve the library definition of symbol "
            + "X9 ('Unavailable:Unresolved'), so its pins cannot be matched with the design and no XML connection can be added. Restore the "
            + "missing library, or rescue or replace that symbol in the schematic editor, then save the XML again.", named.ErrorMessage);
        Assert.IsNull(named.Candidate); Assert.IsNull(named.Connections);
        // The whole planner reaches the same refusal from the saved XML, which carries such a symbol unchanged.
        var unresolvedSaved = unresolved with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(unresolvedDesired, unresolved.KnowledgeLibraries)) };
        var planned = SchematicSynchronizationPlanner.Plan(unresolvedSaved, ConnectedSession(unresolvedSaved.InstanceId, realization: true));
        Assert.AreEqual(SchematicConnectionErrors.UnalignedElectricalBaseline, planned.ErrorCode, planned.ErrorMessage);
        Assert.AreEqual(named.ErrorMessage, planned.ErrorMessage);
        var general = PrepareConnected(saved with { BaselineElectrical = foreign }, desired, admitted);
        Assert.AreEqual("The saved baseline must agree with its native pin partition.", general.ErrorMessage, "No unresolved symbol is named.");

        // Hierarchy merge refusals pass through with the merge's own result, as PrepareCreation reports them
        // (§4.4 step 1): competing XML and native edits of one sheet are a conflict, and an invalid XML
        // hierarchy keeps the merge's error code. Neither yields a candidate.
        var duplicated = desired.Schematic.Clone(); duplicated.Instances.Add(duplicated.Instances[^1].Clone());
        foreach (var (problem, input, wanted, code) in new (string, DesignRecoveryState, SchematicDesign, string)[]
        {
            ("competing XML and native hierarchy edits", Observe(saved, observedTitle.Clone()), desired with { Schematic = titled }, "design_sync_conflict"),
            ("an invalid XML hierarchy", saved, desired with { Schematic = duplicated }, "invalid_merge_hierarchy")
        })
        {
            var merge = SchematicHierarchyMerge.Plan(input.Baseline.Schematic, wanted.Schematic, input.Observed);
            Assert.IsFalse(merge.CanApply, problem);
            var refused = PrepareConnected(input, wanted, admitted);
            Assert.AreEqual(code, refused.ErrorCode, problem + ": " + refused.ErrorMessage);
            Assert.AreEqual(merge.ErrorCode ?? "design_sync_conflict", refused.ErrorCode, problem);
            CollectionAssert.AreEqual(merge.Conflicts.Select(c => (c.InstancePath, c.Reason)).ToArray(),
                refused.Hierarchy!.Conflicts.Select(c => (c.InstancePath, c.Reason)).ToArray(), problem);
            Assert.IsNull(refused.Candidate, problem); Assert.IsNull(refused.CandidateXml, problem); Assert.IsEmpty(refused.NativeOperations, problem);
        }

        // §4.4 step 3: the candidate must resolve every binding. A classification that no longer lists the
        // revision's created components builds no symbols for them, so their occurrences stay unbound: the plan
        // stops with created_binding_invalid and reports exactly those occurrences, never a partial candidate.
        var unbound = PrepareConnected(saved, desired, admitted with { AddedComponentIds = [] });
        Assert.AreEqual(SchematicConnectionErrors.CreatedBindingInvalid, unbound.ErrorCode, unbound.ErrorMessage);
        Assert.IsNull(unbound.Candidate); Assert.IsNull(unbound.CandidateXml); Assert.IsEmpty(unbound.NativeOperations);
        CollectionAssert.AreEquivalent(desired.Engineering.Circuit.Symbols.Where(s => created.Contains(s.ComponentId)).Select(s => (Guid?)s.Id).ToArray(),
            unbound.BindingIssues.Where(i => i.Code == "unmapped_model_symbol").Select(i => i.ModelId).ToArray());
        Assert.IsTrue(unbound.BindingIssues.All(i => i.Code == "unmapped_model_symbol"), string.Join(",", unbound.BindingIssues.Select(i => i.Code)));
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
        var createdPlan = PrepareConnected(fromSheets, createAndConnect, admitted);
        Assert.IsNotNull(createdPlan.Connections, "The eleven placements must pass creation with their connections allowed: "
            + createdPlan.ErrorCode + " " + createdPlan.ErrorMessage);
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
        var connectPlan = PrepareConnected(unconnected, Connected(placed, complete.Nets), connectOnly);
        Assert.IsNotNull(connectPlan.Connections, connectPlan.ErrorCode + " " + connectPlan.ErrorMessage);

        // The realized complete circuit: removing the memory write-protect pin from GND is a disconnection,
        // while tying the unresolved memory SDA pin to a free processor GPIO is an addition.
        // The editor state carries the fixture's hierarchical labels, sheet pins, local labels and wires.
        var (realized, connectedState) = PsuCpuWired(Connected(placed, complete.Nets));
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

    [TestMethod]
    public void PsuCpuCompleteStagePlansEveryFixtureNetAcrossItsFourSheets()
    {
        // The frozen fixture's eleven nets (psu-cpu-fixture-and-ownership.md §1.4.2) with the real electrical types
        // and visibility of its eight library symbols: every member is an ordinary signal, every net is local, and
        // the planned hierarchical labels and sheet pins are exactly the expected native ones (§1.6.3).
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        var typed = components with { PartSymbols = WithLibraryPinTypes(components.PartSymbols!) };
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit;

        // S1 sheets to Complete in one revision: eleven created placements and every net.
        var fromSheets = PsuCpuConnectedState(sheets);
        var createAndConnect = fromSheets with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(WithPsuNets(typed, complete.Nets), [])) };
        var session = ConnectedSession(createAndConnect.InstanceId, realization: true);
        var plan = SchematicSynchronizationPlanner.Plan(createAndConnect, session);
        var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
        RequirePsuCpuIntent(intent, plan.Candidate!, created: true);
        Assert.IsNull(SchematicSynchronizationPlanner.Plan(createAndConnect).Connections, "Without the capability nothing is planned for wiring.");
        var again = SchematicSynchronizationPlanner.Plan(createAndConnect, session).Connections!;
        Assert.AreEqual(JsonSerializer.Serialize(SchematicConnectionIntentBuilder.Summary(intent)), JsonSerializer.Serialize(SchematicConnectionIntentBuilder.Summary(again)),
            "The same saved revision must plan the same intent.");
        CollectionAssert.AreEqual(intent.ExpectedGroups.SelectMany(g => g).ToArray(), again.ExpectedGroups.SelectMany(g => g).ToArray());

        // Components already created, then XML connects them: the same nets, ports and labels, no created symbols.
        var placed = SchematicNativeCreationProjection.Project(sheets, typed, []).Candidate;
        var unconnected = PsuCpuConnectedState(placed);
        var connect = unconnected with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(WithPsuNets(placed, complete.Nets), [])) };
        var connectPlan = SchematicSynchronizationPlanner.Plan(connect, ConnectedSession(connect.InstanceId, realization: true));
        RequirePsuCpuIntent(SchematicConnectionIntentBuilderTests.RequireRealizationPlan(connectPlan), connectPlan.Candidate!, created: false);
    }

    [TestMethod]
    public void PsuCpuHiddenPowerPinsNameTheirNetGloballyOrAreRefused()
    {
        // Must-catch cases on the same fixture: a hidden power input joins the global net of its own name (§5.3).
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit;
        var u6 = complete.Components.Single(c => c.Reference == "U6").Id;
        SchematicSynchronizationPlan PlanWith(IReadOnlyList<CircuitNet> nets, params (string CacheKey, string Number)[] hidden)
        {
            var typed = components with { PartSymbols = WithLibraryPinTypes(components.PartSymbols!, hidden) };
            var state = PsuCpuConnectedState(sheets);
            var saved = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(WithPsuNets(typed, nets), [])) };
            return SchematicSynchronizationPlanner.Plan(saved, ConnectedSession(saved.InstanceId, realization: true));
        }

        // The memory's VCC pin hidden: RAIL_B is named VCC everywhere and needs no hierarchical crossing.
        var railB = complete.Nets.Single(n => n.Name == "RAIL_B");
        var global = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(PlanWith(complete.Nets, ("pic_programmer:24C16", "8")));
        var net = global.Nets.Single(n => n.NetId == railB.Id);
        Assert.AreEqual(ConnectionScope.Global, net.Scope); Assert.AreEqual("VCC", net.GlobalName);
        Assert.IsTrue(global.Nets.Where(n => n.NetId != railB.Id).All(n => n.Scope == ConnectionScope.Local));
        Assert.IsFalse(global.Ports.Any(p => p.NetId == railB.Id));
        Assert.HasCount(10, global.Ports);
        var railIslands = global.Screens.SelectMany(s => s.Islands).Where(i => i.NetId == railB.Id).ToArray();
        var railIsland = railIslands.Single();
        Assert.AreEqual("VCC", railIsland.LabelText); Assert.AreEqual(ConnectionScope.Global, railIsland.Scope);
        Assert.IsNull(railIsland.UplinkSheetSymbolId); Assert.IsEmpty(railIsland.ChildSheetSymbolIds);
        Assert.HasCount(3, railIsland.Members.Where(m => m.RequiresStub).ToArray(), "U2.1, U2.4 and U3.8 get global labels on PSU.");
        Assert.IsFalse(global.Screens.SelectMany(s => s.Islands).Any(i => i.Members.Any(m => m.Pin.Endpoint == new PinEndpoint(u6, "8"))),
            "The memory's hidden VCC pin needs nothing drawn, so its CPU island is not realized.");
        Assert.IsTrue(global.ExpectedGroups.Any(g => g.Count == railB.Pins.Count), "RAIL_B still becomes one native net.");

        // The telemetry MCU's VDD and the processor's VDDIO hidden: RAIL_A would join two global names.
        var twoNames = PlanWith(complete.Nets, ("MCU_ST_STM32C0:STM32C011J_4-6_Mx", "2"), ("Library:F28P659DK8PTPQ1", "3"));
        Assert.AreEqual(SchematicConnectionErrors.ConnectedImplicitPowerConflict, twoNames.ErrorCode, twoNames.ErrorMessage);
        StringAssert.Contains(twoNames.ErrorMessage!, "'VDD'"); StringAssert.Contains(twoNames.ErrorMessage!, "'VDDIO'");
        Assert.IsNull(twoNames.Candidate); Assert.IsNull(twoNames.Connections);

        // The ADC's and the memory's GND pins hidden, with the memory's left out of GND: KiCad would still join it.
        var gnd = complete.Nets.Single(n => n.Name == "GND");
        var withoutMemory = complete.Nets.Select(n => n.Id == gnd.Id ? n with { Pins = n.Pins.Where(p => p != new PinEndpoint(u6, "4")).ToArray() } : n).ToArray();
        var lone = PlanWith(withoutMemory, ("Battery_Management:LTC2959", "10"), ("pic_programmer:24C16", "4"));
        Assert.AreEqual(SchematicConnectionErrors.ConnectedImplicitPowerConflict, lone.ErrorCode, lone.ErrorMessage);
        StringAssert.Contains(lone.ErrorMessage!, "U6.4");
        // False-positive guard: the same two hidden GND pins inside GND name it globally and are planned.
        var named = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(PlanWith(complete.Nets, ("Battery_Management:LTC2959", "10"), ("pic_programmer:24C16", "4")));
        Assert.AreEqual("GND", named.Nets.Single(n => n.NetId == gnd.Id).GlobalName);
        Assert.IsFalse(named.Ports.Any(p => p.NetId == gnd.Id), "A global GND needs no sheet pins.");
    }

    [TestMethod]
    public void PsuCpuStackedRegulatorPinsAreOneConnectionAndNeverSplitAcrossNets()
    {
        // Decision kicad-stacked-pins-one-node-20260924 on the frozen fixture's exact definition geometry: the LP3982 (U2)
        // draws pins 1 and 4 at one point, which KiCad always joins. Isolated planning and comparison logic; the rendered
        // PSU/CPU creation journey proves the same rules against the editor's own captured symbols.
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        var typed = components with { PartSymbols = WithLibraryPinTypes(components.PartSymbols!) };
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit;
        var references = complete.Components.ToDictionary(c => c.Reference, c => c.Id);
        PinEndpoint Pin(string reference, string number) => new(references[reference], number);
        Guid railB = complete.Nets.Single(n => n.Name == "RAIL_B").Id, fault = complete.Nets.Single(n => n.Name == "LDO_FAULT").Id;
        // U2.4 moved from RAIL_B to LDO_FAULT while its stacked partner U2.1 stays on RAIL_B.
        CircuitNet[] split = [.. complete.Nets.Select(n => n.Id == railB ? n with { Pins = [.. n.Pins.Where(p => p != Pin("U2", "4"))] }
            : n.Id == fault ? n with { Pins = [.. n.Pins, Pin("U2", "4")] } : n)];
        // U2.4 left out of every net: KiCad still joins it to RAIL_B through U2.1, which is no conflict.
        CircuitNet[] partial = [.. complete.Nets.Select(n => n.Id == railB ? n with { Pins = [.. n.Pins.Where(p => p != Pin("U2", "4"))] } : n)];
        SchematicSynchronizationPlan Planned(DesignRecoveryState state, SchematicDesign desired)
        {
            var saved = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(desired, [])) };
            return SchematicSynchronizationPlanner.Plan(saved, ConnectedSession(saved.InstanceId, realization: true));
        }
        void Refused(SchematicSynchronizationPlan plan, string path)
        {
            Assert.AreEqual(SchematicConnectionErrors.StackedPinsOnDifferentNets, plan.ErrorCode, path + ": " + plan.ErrorMessage);
            StringAssert.Contains(plan.ErrorMessage!, "U2.1 in net 'RAIL_B' and U2.4 in net 'LDO_FAULT'", path);
            Assert.IsNull(plan.Candidate, path); Assert.IsNull(plan.CandidateXml, path); Assert.IsNull(plan.Connections, path);
            Assert.IsEmpty(plan.NativeOperations, path + ": nothing may reach the editor.");
        }

        // The creation projection, which both creation and connected creation use, refuses the split before placing anything;
        // plain creation still refuses any new connection first, with its own code.
        var projected = Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(sheets, WithPsuNets(typed, split), [], allowConnected: true));
        Assert.AreEqual(SchematicConnectionErrors.StackedPinsOnDifferentNets, projected.Code);
        Assert.AreEqual("created_component_connectivity_requires_resolution",
            Assert.ThrowsExactly<AutomationException>(() => SchematicNativeCreationProjection.Project(sheets, WithPsuNets(typed, split), [])).Code);
        // Connected creation from the S1 sheets, and connections added to components the editor already shows.
        var fromSheets = PsuCpuConnectedState(sheets);
        Refused(Planned(fromSheets, WithPsuNets(typed, split)), "create and connect");
        var placed = SchematicNativeCreationProjection.Project(sheets, typed, []).Candidate;
        var unconnected = PsuCpuConnectedState(placed);
        Refused(Planned(unconnected, WithPsuNets(placed, split)), "connect only");

        // False positives: listing only one of the stacked pins is planned in both paths, and KiCad's join is predicted:
        // the unlisted partner belongs to RAIL_B's expected native group.
        foreach (var (state, design, created) in new[] { (fromSheets, typed, true), (unconnected, placed, false) })
        {
            var plan = Planned(state, WithPsuNets(design, partial));
            var intent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(plan);
            var keys = SchematicConnectionIntentBuilderTests.Keys(plan.Candidate!, (Pin("U2", "1").ComponentId, "1"), (Pin("U2", "4").ComponentId, "4"));
            var group = intent.ExpectedGroups.Single(g => g.Contains(keys[0]));
            Assert.IsTrue(group.Contains(keys[1]), (created ? "created" : "existing") + " U2.4 joins RAIL_B's group through U2.1.");
            Assert.AreEqual(complete.Nets.Single(n => n.Id == railB).Pins.Count, group.Count);
            Assert.AreEqual(created ? 222 : 41, intent.ExpectedGroups.Sum(g => g.Count));
        }
        // Both stacked pins on RAIL_B (the frozen Complete stage) is planned, and two pins of different symbols that share
        // one local position (LTC2959 U3.1 on DCDC_OUT and STM32 U4.2 on RAIL_A, both on PSU) are never one connection.
        SchematicConnectionIntentBuilderTests.RequireRealizationPlan(Planned(fromSheets, WithPsuNets(typed, complete.Nets)));

        // Comparison: the created, unconnected components as KiCad shows them, with U2.1 and U2.4 joined.
        var observed = unconnected.ObservedElectrical!;
        var result = SchematicElectricalComparison.Compare(placed, observed, []);
        Assert.IsTrue(result.PinBindingsComplete && result.ConnectivityEquivalent, string.Join("; ", result.Differences.Select(d => d.Kind)));
        CollectionAssert.AreEquivalent(new[] { Pin("U2", "1"), Pin("U2", "4") }, result.PinPartitions!.Single(p => p.Pins.Count > 1).Pins.ToArray());
        Assert.AreEqual(220, result.PinPartitions!.Count(p => p.Pins.Count == 1),
            "Every other pin stays alone, including processor U5, whose four units draw pins at the same local points.");
        // Must-catch: an editor that showed the stacked pins apart is not KiCad's drawing of this symbol.
        var apart = observed.Clone(); apart.Nets.Clear();
        var separated = SchematicElectricalComparison.Compare(placed, apart, []);
        Assert.IsTrue(separated.PinBindingsComplete); Assert.IsFalse(separated.ConnectivityEquivalent);
        var difference = separated.Differences.Single();
        Assert.AreEqual("stacked_pins_split", difference.Kind);
        CollectionAssert.AreEqual(new[] { Pin("U2", "1"), Pin("U2", "4") }, difference.Pins.ToArray());
        // Must-catch: U3.1 and U4.2 sit at the same local point of two different symbols; if they touched in the editor,
        // that is a real join the XML does not have, never a stacked pair.
        var screen = placed.Schematic.Instances.Single(s => s.Items.Any(i => i.Is(SchematicSymbolInstance.Descriptor)
            && i.Unpack<SchematicSymbolInstance>().ReferenceField.Text.Text_ == "U3"));
        SchematicPin PlacedPin(string reference, string number) => screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor))
            .Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.ReferenceField.Text.Text_ == reference)
            .Definition.Items.Select(c => c.Item.Unpack<SchematicPin>()).Single(p => p.Number == number && p.LibraryPinId is not null);
        Assert.AreEqual(PlacedPin("U3", "1").Position, PlacedPin("U4", "2").Position, "The premise: one local point in two symbols.");
        var touching = observed.Clone();
        var contact = new SchematicNet { Name = "Net-(U3-VDD-Pad1)" };
        contact.Sheets.Add(new SchematicNetSheetContents { Path = screen.Metadata.Document.SheetPath.Clone(), Items = { PlacedPin("U3", "1").Id.Clone(), PlacedPin("U4", "2").Id.Clone() } });
        touching.Nets.Add(contact);
        var joined = SchematicElectricalComparison.Compare(placed, touching, []);
        Assert.IsTrue(joined.PinBindingsComplete); Assert.IsFalse(joined.ConnectivityEquivalent);
        CollectionAssert.AreEquivalent(new[] { Pin("U3", "1"), Pin("U4", "2") }, joined.Differences.Single(d => d.Kind == "native_net_join").Pins.ToArray());
        Assert.IsTrue(SchematicElectricalComparison.Compare(WithPsuNets(placed, [new(Guid.NewGuid(), "SUPPLY", [Pin("U3", "1"), Pin("U4", "2")])]), touching, [])
            .ConnectivityEquivalent, "The same touch is exactly what an XML net joining the two pins describes.");
    }

    [TestMethod]
    public void StackedPinsJoinOnlyWithinOnePlacedUnit()
    {
        // Isolated comparison and planning rules on a two-unit part whose units share pin number 8 (common to both units) and
        // draw pin 1 (unit 1) and pin 7 (unit 2) at the same local point: nothing stacks across units or symbols.
        var f = SchematicElectricalComparisonTests.Fixture();
        void Place(int unit, string number, long y)
        {
            foreach (var data in new[] { f.Design.Schematic, f.State.Hierarchy.Data })
            foreach (var screen in data.Instances)
                for (int index = 0; index < screen.Items.Count; ++index)
                {
                    if (!screen.Items[index].Is(SchematicSymbolInstance.Descriptor)) continue;
                    var symbol = screen.Items[index].Unpack<SchematicSymbolInstance>();
                    if (symbol.Unit.Unit != unit) continue;
                    foreach (var child in symbol.Definition.Items)
                    {
                        var pin = child.Item.Unpack<SchematicPin>();
                        if (pin.Number != number) continue;
                        pin.Position = new() { XNm = 0, YNm = y }; child.Item = Any.Pack(pin);
                    }
                    screen.Items[index] = Any.Pack(symbol);
                }
        }
        Place(1, "1", 0); Place(1, "8", 5_080_000); Place(2, "7", 0); Place(2, "8", 5_080_000);
        var circuit = f.Design.Engineering.Circuit;
        Guid u1 = circuit.Components[0].Id, u2 = circuit.Components[1].Id, vcc = circuit.Nets[0].Id;
        // False positive: units drawn alike share local points and pin 8, and every pin keeps its own connection.
        var alike = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(alike.PinBindingsComplete && alike.ConnectivityEquivalent, string.Join("; ", alike.Differences.Select(d => d.Kind)));
        CollectionAssert.AreEquivalent(new[] { new PinEndpoint(u1, "8"), new PinEndpoint(u2, "8") }, alike.PinPartitions!.Single(p => p.Pins.Count > 1).Pins.ToArray());
        SchematicPlacedPins.RequireStackedPinsOnOneNet(f.Design with { Engineering = f.Design.Engineering with { Circuit = circuit with
            { Nets = [.. circuit.Nets, new(Guid.NewGuid(), "OUT", [new(u1, "1"), new(u2, "1")]), new(Guid.NewGuid(), "OUT_B", [new(u1, "7"), new(u2, "7")])] } } });

        // Unit 1 now draws pin 1 on its common pin 8: KiCad joins U1.1 and U2.1 into VCC, and an editor that did not is refused.
        Place(1, "1", 5_080_000);
        var apart = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsFalse(apart.ConnectivityEquivalent);
        Assert.IsTrue(apart.Differences.All(d => d.Kind == "stacked_pins_split" && d.ModelNetIds.SequenceEqual([vcc])), string.Join("; ", apart.Differences.Select(d => d.Kind)));
        foreach (var sheet in f.State.Nets[0].Sheets)
        {
            var screen = f.State.Hierarchy.Data.Instances.Single(s => s.Metadata.Document.SheetPath.Equals(sheet.Path));
            foreach (var symbol in screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()))
                foreach (var pin in symbol.Definition.Items.Select(c => c.Item.Unpack<SchematicPin>()).Where(p => p.Number == "1"))
                    sheet.Items.Add(pin.Id.Clone());
        }
        var together = SchematicElectricalComparison.Compare(f.Design, f.State, [f.Library]);
        Assert.IsTrue(together.PinBindingsComplete && together.ConnectivityEquivalent, "The stacked pin is one node with the VCC pin it touches.");
        // Must-catch: the model putting the stacked pin on another net is a join KiCad cannot avoid, and planning refuses it.
        var other = f.Design with { Engineering = f.Design.Engineering with { Circuit = circuit with
            { Nets = [.. circuit.Nets, new(Guid.NewGuid(), "OUT", [new(u1, "1"), new(u2, "1")])] } } };
        var conflict = SchematicElectricalComparison.Compare(other, f.State, [f.Library]);
        Assert.IsFalse(conflict.ConnectivityEquivalent);
        Assert.IsTrue(conflict.Differences.Any(d => d.Kind == "native_net_join" && d.ModelNetIds.Contains(vcc)));
        Assert.AreEqual(SchematicConnectionErrors.StackedPinsOnDifferentNets,
            Assert.ThrowsExactly<AutomationException>(() => SchematicPlacedPins.RequireStackedPinsOnOneNet(other)).Code);
        SchematicPlacedPins.RequireStackedPinsOnOneNet(f.Design with { Engineering = f.Design.Engineering with { Circuit = circuit with
            { Nets = [circuit.Nets[0] with { Pins = [.. circuit.Nets[0].Pins, new(u1, "1"), new(u2, "1")] }] } } });

        // Unit 2 now also draws pin 7 on the common pin 8. Pin 8 is one physical pin, so KiCad joins pin 1 (stacked on it in
        // unit 1) and pin 7 (stacked on it in unit 2) through it: each component has one node 1+7+8, and planning checks
        // exactly the nodes the comparison uses.
        Place(2, "7", 5_080_000);
        // VCC keeps its identity (the structural diagram realizes it) but loses pin 8, so pin 8 is in no net unless listed.
        SchematicDesign WithNets(params CircuitNet[] nets) => f.Design with { Engineering = f.Design.Engineering with { Circuit = circuit with
            { Nets = [circuit.Nets[0] with { Pins = [] }, .. nets] } } };
        CircuitNet Both(string name, params string[] numbers) => new(Guid.NewGuid(), name, [.. numbers.SelectMany(n => new PinEndpoint[] { new(u1, n), new(u2, n) })]);
        var shared = WithNets(Both("OUT_A", "1"), Both("OUT_B", "7"));
        var nodes = SchematicElectricalComparison.StackedPinNodes(shared);
        Assert.AreEqual(2, nodes.Count);
        Assert.IsTrue(nodes.All(n => n.Select(p => p.ComponentId).Distinct().Count() == 1 && n.Select(p => p.Pin).SequenceEqual(["1", "7", "8"])),
            string.Join("; ", nodes.Select(n => string.Join(",", n.Select(p => p.Pin)))));
        CollectionAssert.AreEquivalent(new[] { u1, u2 }, nodes.Select(n => n[0].ComponentId).ToArray());
        var compared = SchematicElectricalComparison.Compare(shared, f.State, [f.Library]);
        Assert.IsTrue(compared.PinBindingsComplete);
        CollectionAssert.AreEqual(nodes.SelectMany(n => n).ToArray(), compared.StackedPins!.SelectMany(n => n).ToArray(),
            "The comparison joins the same nodes that planning checks.");
        // The premise: on its own, each unit stacks just one of the listed pins on the unlisted pin 8, so a check per unit
        // passes this XML; KiCad would then need pin 8 in both nets, which only fails after the editor has changed.
        var unitScreen = f.Design.Schematic.Instances.First(s => s.Items.Any(i => i.Is(SchematicSymbolInstance.Descriptor)));
        foreach (var unit in new[] { 1, 2 })
        {
            var symbol = unitScreen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Unit.Unit == unit);
            var stack = SchematicElectricalComparison.StackedDefinitionPins(symbol, unit).Single();
            CollectionAssert.AreEquivalent(new[] { unit == 1 ? "1" : "7", "8" }, stack.Select(p => p.Number).ToArray());
        }
        // Must-catch: pins 1 and 7 on different nets through the shared pin 8 are refused before anything changes.
        var refused = Assert.ThrowsExactly<AutomationException>(() => SchematicPlacedPins.RequireStackedPinsOnOneNet(shared));
        Assert.AreEqual(SchematicConnectionErrors.StackedPinsOnDifferentNets, refused.Code);
        StringAssert.Matches(refused.Message, new System.Text.RegularExpressions.Regex(
            @"Pins (U[12])\.1, \1\.7, \1\.8 of \1 \(Dual amplifier\) are always one connection in KiCad, because its symbol draws them at the same point, "
            + @"directly or through a pin its units share, but the XML puts \1\.1 in net 'OUT_A' and \1\.7 in net 'OUT_B'\."));
        // False positives: pin 1 alone in a net (7 and 8 left out), or the whole node in one net, is no conflict.
        SchematicPlacedPins.RequireStackedPinsOnOneNet(WithNets(Both("OUT_A", "1")));
        SchematicPlacedPins.RequireStackedPinsOnOneNet(WithNets(Both("OUT", "1", "7")));
        SchematicPlacedPins.RequireStackedPinsOnOneNet(WithNets(Both("SUPPLY", "1", "7", "8")));
    }

    [TestMethod]
    public void PsuCpuWiredCrossingsAreReusedOrJoinedWhenXmlAddsPins()
    {
        // The fixture's Complete stage once the editor shows it wired: every net joined with its hierarchical labels,
        // sheet pins and local labels from expected-native.json. XML revisions that add pins must reuse those items,
        // join an existing connection only where no matching label reaches the new drawing, and plan new nets
        // (the memory SDA link) from scratch.
        var (sheets, components) = SchematicNativeCreationProjectionTests.PsuCpuComponents();
        var typed = components with { PartSymbols = WithLibraryPinTypes(components.PartSymbols!) };
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit;
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Complete);
        var realized = WithPsuNets(SchematicNativeCreationProjection.Project(sheets, typed, []).Candidate, complete.Nets);
        var (wired, state) = PsuCpuWired(realized);
        Assert.IsTrue(SchematicElectricalComparison.Compare(wired, state.ObservedElectrical!, []).ConnectivityEquivalent);
        var circuit = wired.Engineering.Circuit;
        var used = complete.Nets.SelectMany(n => n.Pins).ToHashSet();
        Guid IdOf(string reference) => circuit.Components.Single(c => c.Reference == reference).Id;
        PinEndpoint Free(string reference, int unit)
        {
            var component = circuit.Components.Single(c => c.Reference == reference);
            var part = circuit.Parts.Single(p => p.Id == circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == component.DefinitionId).PartId);
            return new(component.Id, part.Pins.Where(p => p.Unit == unit && !used.Contains(new(component.Id, p.Number)))
                .OrderBy(p => p.Number, StringComparer.Ordinal).First().Number);
        }
        Guid InstanceOf(string key) => expected.Sheets.Single(s => s.Key == key).ModelSheetInstance;
        Guid SheetSymbolOf(string key) => expected.Sheets.Single(s => s.Key == key).NativeSheetSymbol!.Value;
        Guid ScreenOf(string key) => expected.Sheets.Single(s => s.Key == key).NativeScreen;
        string PathOf(string key) => SchematicDesignBindings.PathKey(wired.SheetBindings.Single(b => b.SheetInstanceId == InstanceOf(key)).NativePath);
        Guid[] AnchorOn(DesignRecoveryState at, string net, string key) => [.. at.ObservedElectrical!.Nets.Single(n => n.Name == "/" + net).Sheets
            .Where(s => string.Join('/', s.Path.Path.Select(p => p.Value)) == PathOf(key)).SelectMany(s => s.Items).Select(i => Guid.Parse(i.Value))];
        SchematicConnectionIntent PlanWith(DesignRecoveryState at, SchematicDesign design, string net, PinEndpoint pin)
        {
            var nets = complete.Nets.Select(n => n.Name == net ? n with { Pins = [.. n.Pins, pin] } : n).ToArray();
            var saved = at with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(WithPsuNets(design, nets), [])) };
            return SchematicConnectionIntentBuilderTests.RequireRealizationPlan(
                SchematicSynchronizationPlanner.Plan(saved, ConnectedSession(saved.InstanceId, realization: true)));
        }
        PinEndpoint[] Ordered(params PinEndpoint[] pins) => [.. pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal)];
        PinEndpoint PinOf(string reference, string number) => new(IdOf(reference), number);
        var gpio = Free("U5", 2);

        // Same-sheet reuse: a free processor GPIO joins MEM_SCL on CPU, where the local label MEM_SCL already names it.
        var memScl = PlanWith(state, wired, "MEM_SCL", gpio);
        Assert.AreEqual(ConnectionScope.Local, memScl.Nets.Single().Scope);
        CollectionAssert.AreEqual(new[] { gpio }, memScl.Nets.Single().AddedPins.ToArray());
        Assert.IsEmpty(memScl.Ports, "MEM_SCL stays on CPU.");
        var screen = memScl.Screens.Single();
        Assert.AreEqual(ScreenOf("CPU"), screen.ScreenId);
        var island = screen.Islands.Single();
        Assert.AreEqual("MEM_SCL", island.LabelText);
        Assert.IsTrue(island.AnchorHasMatchingDriver); Assert.IsFalse(island.JoinRequired); Assert.IsEmpty(island.JoinCandidates);
        Assert.IsNull(island.UplinkSheetSymbolId); Assert.IsEmpty(island.ChildSheetSymbolIds);
        CollectionAssert.AreEqual(new[] { gpio }, island.Members.Where(m => m.RequiresStub).Select(m => m.Pin.Endpoint).ToArray());
        Assert.IsTrue(island.Members.All(m => m.Role == ConnectionMemberRole.Signal));
        CollectionAssert.AreEquivalent(AnchorOn(state, "MEM_SCL", "CPU"), island.AnchorItemIds.ToArray());
        Assert.HasCount(4, island.AnchorItemIds, "Two pins, the local label and the wire.");
        SchematicConnectionIntentBuilderTests.RequireGroups(memScl, [SchematicConnectionIntentBuilderTests.Keys(wired,
            (IdOf("U5"), "161"), (IdOf("U6"), "6"), (gpio.ComponentId, gpio.Pin))]);

        // Cross-sheet reuse: the same GPIO joins TELEM_MCU_TO_CPU, which already crosses PSU -> ROOT -> CPU through
        // hierarchical labels and sheet pins. Both crossings are reused and only the new pin is drawn on CPU.
        var telemetry = PlanWith(state, wired, "TELEM_MCU_TO_CPU", gpio);
        Assert.HasCount(2, telemetry.Ports);
        foreach (var (port, child) in telemetry.Ports.Zip(new[] { "PSU", "CPU" }.OrderBy(PathOf, StringComparer.Ordinal)))
        {
            Assert.AreEqual(InstanceOf(child), port.ChildSheetInstanceId); Assert.AreEqual(InstanceOf("ROOT"), port.ParentSheetInstanceId);
            Assert.AreEqual(SheetSymbolOf(child), port.SheetSymbolId); Assert.AreEqual("TELEM_MCU_TO_CPU", port.PortText);
            Assert.IsTrue(port.SheetPinExists, child); Assert.IsTrue(port.UplinkLabelExists, child);
        }
        var cpu = telemetry.Screens.Single().Islands.Single();
        Assert.AreEqual(ScreenOf("CPU"), cpu.ScreenId, "Nothing is drawn on ROOT or PSU.");
        Assert.AreEqual("TELEM_MCU_TO_CPU", cpu.LabelText);
        Assert.IsNull(cpu.UplinkSheetSymbolId); Assert.IsEmpty(cpu.ChildSheetSymbolIds);
        Assert.IsTrue(cpu.AnchorHasMatchingDriver); Assert.IsFalse(cpu.JoinRequired);
        CollectionAssert.AreEqual(new[] { gpio }, cpu.Members.Where(m => m.RequiresStub).Select(m => m.Pin.Endpoint).ToArray());
        Assert.IsTrue(cpu.Members.Single(m => m.Pin.Endpoint == PinOf("U5", "74")).AlreadyConnected);
        CollectionAssert.AreEquivalent(AnchorOn(state, "TELEM_MCU_TO_CPU", "CPU"), cpu.AnchorItemIds.ToArray());
        SchematicConnectionIntentBuilderTests.RequireGroups(telemetry, [SchematicConnectionIntentBuilderTests.Keys(wired,
            (IdOf("U4"), "8"), (IdOf("U5"), "74"), (gpio.ComponentId, gpio.Pin))]);

        // Two-level reuse: a free unit-4 supply pin joins GND on CPU_POWER, crossing CPU_POWER -> CPU -> ROOT -> PSU.
        var supply = Free("U5", 4);
        var ground = PlanWith(state, wired, "GND", supply);
        Assert.HasCount(3, ground.Ports);
        Assert.IsTrue(ground.Ports.All(p => p.SheetPinExists && p.UplinkLabelExists && p.PortText == "GND"));
        CollectionAssert.AreEquivalent(new[] { InstanceOf("PSU"), InstanceOf("CPU"), InstanceOf("CPU_POWER") }, ground.Ports.Select(p => p.ChildSheetInstanceId).ToArray());
        Assert.AreEqual(InstanceOf("CPU"), ground.Ports.Single(p => p.ChildSheetInstanceId == InstanceOf("CPU_POWER")).ParentSheetInstanceId);
        var power = ground.Screens.Single().Islands.Single();
        Assert.AreEqual(ScreenOf("CPU_POWER"), power.ScreenId);
        Assert.IsTrue(power.AnchorHasMatchingDriver); Assert.IsFalse(power.JoinRequired);
        Assert.IsNull(power.UplinkSheetSymbolId); Assert.IsEmpty(power.ChildSheetSymbolIds);
        CollectionAssert.AreEqual(new[] { supply }, power.Members.Where(m => m.RequiresStub).Select(m => m.Pin.Endpoint).ToArray());
        Assert.AreEqual(ConnectionMemberRole.Signal, power.Members.Single(m => m.Pin.Endpoint == supply).Role, "A visible supply input is an ordinary pin.");

        // Same-sheet join: without the MEM_SCL local label the wire alone holds U5.161 and U6.6, so the new label must
        // also reach them.
        var (unlabelled, unlabelledState) = PsuCpuWired(realized, "MEM_SCL");
        var joined = PlanWith(unlabelledState, unlabelled, "MEM_SCL", gpio).Screens.Single().Islands.Single();
        Assert.AreEqual("MEM_SCL", joined.LabelText, "Without a label the net's own name labels it.");
        Assert.IsFalse(joined.AnchorHasMatchingDriver); Assert.IsTrue(joined.JoinRequired);
        CollectionAssert.AreEqual(Ordered(PinOf("U5", "161"), PinOf("U6", "6")), joined.JoinCandidates.Select(p => p.Endpoint).ToArray());
        Assert.IsTrue(joined.JoinCandidates.All(p => joined.Members.Single(m => m.Pin == p).Role == ConnectionMemberRole.Signal));
        CollectionAssert.AreEqual(new[] { gpio }, joined.Members.Where(m => m.RequiresStub).Select(m => m.Pin.Endpoint).ToArray());
        Assert.HasCount(3, joined.AnchorItemIds, "Two pins and the wire.");

        // Uplink-only join: a free PSU pin joins MEM_SCL. CPU needs a new hierarchical label but has no new pin, so
        // the label joins the existing MEM_SCL pins even though the local label matches; both crossings are new.
        var psuPin = Free("U4", 1);
        var uplink = PlanWith(state, wired, "MEM_SCL", psuPin);
        Assert.HasCount(2, uplink.Ports);
        Assert.IsTrue(uplink.Ports.All(p => !p.SheetPinExists && !p.UplinkLabelExists && p.PortText == "MEM_SCL"
            && p.ParentSheetInstanceId == InstanceOf("ROOT")));
        var islands = uplink.Screens.SelectMany(s => s.Islands).ToDictionary(i => i.SheetInstanceId);
        Assert.HasCount(3, islands);
        var cpuIsland = islands[InstanceOf("CPU")];
        Assert.AreEqual(SheetSymbolOf("CPU"), cpuIsland.UplinkSheetSymbolId);
        Assert.IsFalse(cpuIsland.Members.Any(m => m.RequiresStub)); Assert.IsEmpty(cpuIsland.ChildSheetSymbolIds);
        Assert.IsTrue(cpuIsland.AnchorHasMatchingDriver); Assert.IsTrue(cpuIsland.JoinRequired);
        CollectionAssert.AreEqual(Ordered(PinOf("U5", "161"), PinOf("U6", "6")), cpuIsland.JoinCandidates.Select(p => p.Endpoint).ToArray());
        var psuIsland = islands[InstanceOf("PSU")];
        Assert.AreEqual(SheetSymbolOf("PSU"), psuIsland.UplinkSheetSymbolId); Assert.IsFalse(psuIsland.JoinRequired);
        CollectionAssert.AreEqual(new[] { psuPin }, psuIsland.Members.Where(m => m.RequiresStub).Select(m => m.Pin.Endpoint).ToArray());
        var rootIsland = islands[InstanceOf("ROOT")];
        Assert.IsEmpty(rootIsland.Members); Assert.AreEqual("MEM_SCL", rootIsland.LabelText);
        CollectionAssert.AreEqual(new[] { SheetSymbolOf("PSU"), SheetSymbolOf("CPU") }.Order().ToArray(), rootIsland.ChildSheetSymbolIds.ToArray());
        SchematicConnectionIntentBuilderTests.RequireGroups(uplink, [SchematicConnectionIntentBuilderTests.Keys(wired,
            (IdOf("U5"), "161"), (IdOf("U6"), "6"), (psuPin.ComponentId, psuPin.Pin))]);

        // The memory SDA link the fixture leaves open (§0 rule 2): a new net on CPU with two new stubs and no crossing.
        var sda = new CircuitNet(Guid.NewGuid(), "MEM_SDA", [gpio, PinOf("U6", "5")]);
        var sdaSaved = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(WithPsuNets(wired, [.. complete.Nets, sda]), [])) };
        var sdaIntent = SchematicConnectionIntentBuilderTests.RequireRealizationPlan(
            SchematicSynchronizationPlanner.Plan(sdaSaved, ConnectedSession(sdaSaved.InstanceId, realization: true)));
        var sdaNet = sdaIntent.Nets.Single();
        Assert.AreEqual(sda.Id, sdaNet.NetId); Assert.AreEqual(ConnectionScope.Local, sdaNet.Scope);
        CollectionAssert.AreEqual(Ordered(gpio, PinOf("U6", "5")), sdaNet.AddedPins.ToArray());
        Assert.IsEmpty(sdaIntent.Ports);
        var sdaIsland = sdaIntent.Screens.Single().Islands.Single();
        Assert.AreEqual(ScreenOf("CPU"), sdaIsland.ScreenId); Assert.AreEqual("MEM_SDA", sdaIsland.LabelText);
        Assert.IsEmpty(sdaIsland.AnchorItemIds); Assert.IsFalse(sdaIsland.AnchorHasMatchingDriver); Assert.IsFalse(sdaIsland.JoinRequired);
        Assert.IsTrue(sdaIsland.Members.All(m => m.RequiresStub && !m.AlreadyConnected && m.Role == ConnectionMemberRole.Signal));
        CollectionAssert.AreEqual(Ordered(gpio, PinOf("U6", "5")), sdaIsland.Members.Select(m => m.Pin.Endpoint).ToArray());
        SchematicConnectionIntentBuilderTests.RequireGroups(sdaIntent, [SchematicConnectionIntentBuilderTests.Keys(wired,
            (gpio.ComponentId, gpio.Pin), (IdOf("U6"), "5"))]);
    }

    /// <summary>The connection intent for the fixture's Complete stage (psu-cpu-fixture-and-ownership.md §1.6.3),
    /// checked against the frozen expected-native.json: eleven local nets of ordinary signal pins, the exact
    /// hierarchical labels per sheet and sheet pins per sheet symbol, required and allowed label texts, twelve new
    /// crossings, and the expected native groups computed independently from the candidate's own bindings.</summary>
    internal static void RequirePsuCpuIntent(SchematicConnectionIntent intent, SchematicDesign candidate, bool created)
    {
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Complete);
        var complete = PsuCpuFixture.Engineering(PsuCpuStage.Complete).Circuit;
        CollectionAssert.AreEqual(complete.Nets.Select(n => n.Id).Order().ToArray(), intent.Nets.Select(n => n.NetId).ToArray());
        foreach (var net in intent.Nets)
        {
            var model = complete.Nets.Single(n => n.Id == net.NetId);
            Assert.AreEqual(model.Name, net.Name);
            Assert.AreEqual(ConnectionScope.Local, net.Scope, net.Name); Assert.IsNull(net.GlobalName, net.Name);
            CollectionAssert.AreEquivalent(model.Pins.ToArray(), net.AddedPins.ToArray(), net.Name);
        }
        var sheetOfPath = expected.Sheets.ToDictionary(s => SchematicDesignBindings.PathKey(candidate.SheetBindings
            .Single(b => b.SheetInstanceId == s.ModelSheetInstance).NativePath), s => s.Key, StringComparer.Ordinal);
        Assert.HasCount(4, intent.Screens, "Root carries the crossings; PSU, CPU and CPU_POWER carry the pins.");
        Assert.IsTrue(intent.Screens.All(s => s.InstancePathKeys.Count == 1));
        var islands = intent.Screens.SelectMany(s => s.Islands).ToArray();
        var members = islands.SelectMany(i => i.Members).ToArray();
        Assert.HasCount(41, members);
        Assert.IsTrue(members.All(m => m.Role == ConnectionMemberRole.Signal && m.PowerName is null && !m.AlreadyConnected && m.RequiresStub),
            "Every fixture pin is an ordinary signal, including its visible power inputs and hidden non-power pins.");
        Assert.IsTrue(members.All(m => m.Pin.CreatedSymbol == created));
        Assert.IsTrue(islands.All(i => i.Scope == ConnectionScope.Local && i.AnchorItemIds.Count == 0 && !i.JoinRequired && !i.AnchorHasMatchingDriver));
        foreach (var sheet in expected.Sheets)
        {
            var here = islands.Where(i => sheetOfPath[i.SheetPathKey] == sheet.Key).ToArray();
            CollectionAssert.AreEqual(expected.HierarchicalLabels[sheet.Key].ToArray(),
                here.Where(i => i.UplinkSheetSymbolId is not null).Select(i => i.LabelText).Order(StringComparer.Ordinal).ToArray(), "hierarchical labels on " + sheet.Key);
            Assert.IsTrue(here.All(i => i.UplinkSheetSymbolId is null || i.UplinkSheetSymbolId == sheet.NativeSheetSymbol), sheet.Key);
            CollectionAssert.IsSubsetOf((expected.RequiredLocalLabelNames.GetValueOrDefault(sheet.Key) ?? []).ToArray(),
                here.Where(i => i.UplinkSheetSymbolId is null && i.Members.Any(m => m.RequiresStub)).Select(i => i.LabelText).ToArray(), "local labels on " + sheet.Key);
            CollectionAssert.IsSubsetOf(here.Select(i => i.LabelText).Distinct(StringComparer.Ordinal).ToArray(),
                expected.AllowedLabelNames[sheet.Key].ToArray(), "allowed labels on " + sheet.Key);
            if (sheet.Parent is null) Assert.IsTrue(here.All(i => i.Members.Count == 0 && i.ChildSheetSymbolIds.Count != 0), "The root only carries crossings.");
        }
        var sheetPins = islands.SelectMany(i => i.ChildSheetSymbolIds.Select(k => (Sheet: expected.Sheets.Single(s => s.NativeSheetSymbol == k).Key,
                Text: intent.Ports.Single(p => p.NetId == i.NetId && p.SheetSymbolId == k).PortText)))
            .GroupBy(x => x.Sheet, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Text).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var (key, names) in expected.SheetPins)
            CollectionAssert.AreEqual(names.ToArray(), sheetPins.GetValueOrDefault(key) ?? [], "sheet pins on " + key);
        Assert.HasCount(12, intent.Ports);
        Assert.IsTrue(intent.Ports.All(p => !p.SheetPinExists && !p.UplinkLabelExists && p.PortText == complete.Nets.Single(n => n.Id == p.NetId).Name));

        var groups = complete.Nets.Select(n => SchematicConnectionIntentBuilderTests.Keys(candidate, [.. n.Pins.Select(p => (p.ComponentId, p.Pin))])).ToList();
        if (created)
        {
            var connected = groups.SelectMany(g => g).ToHashSet();
            var circuit = candidate.Engineering.Circuit;
            foreach (var component in circuit.Components)
            {
                var part = circuit.Parts.Single(p => p.Id == circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == component.DefinitionId).PartId);
                foreach (var pin in part.Pins)
                    foreach (var key in SchematicConnectionIntentBuilderTests.Keys(candidate, (component.Id, pin.Number)).Where(k => !connected.Contains(k)))
                        groups.Add([key]);
            }
            Assert.HasCount(11, intent.CreatedSymbolIds, "Eleven unit placements (errata 2026-09-23).");
        }
        else Assert.IsEmpty(intent.CreatedSymbolIds);
        SchematicConnectionIntentBuilderTests.RequireGroups(intent, groups);
        Assert.AreEqual(created ? 222 : 41, intent.ExpectedGroups.Sum(g => g.Count));
    }

    /// <summary>The fixture's part declarations with each pin's electrical type and visibility taken from the frozen
    /// lib_symbols list, optionally hiding the named (cache key, pin number) pins.</summary>
    internal static IReadOnlyList<SchematicPartSymbol> WithLibraryPinTypes(IReadOnlyList<SchematicPartSymbol> declarations,
        params (string CacheKey, string Number)[] hidden)
    {
        var facts = new Dictionary<(string CacheKey, int Unit, int Style, string Number), (ElectricalPinType Type, bool Hidden)>();
        var list = PsuCpuSexpr.Parse(PsuCpuFixture.ReadText("lib_symbols.kicad_sexpr"));
        foreach (var symbol in list.Children("symbol"))
            foreach (var body in symbol.Children("symbol"))
            {
                var name = body.Value(1).Split('_');
                int unit = int.Parse(name[^2], System.Globalization.CultureInfo.InvariantCulture), style = int.Parse(name[^1], System.Globalization.CultureInfo.InvariantCulture);
                foreach (var pin in body.Children("pin"))
                {
                    bool hide = pin.Children("hide").Any(h => h.Items!.Count < 2 || h.Value(1) == "yes") || pin.Items!.Any(i => i is { Atom: "hide", Quoted: false });
                    facts.Add((symbol.Value(1), unit, style, pin.Child("number").Value(1)), (pin.Value(1) switch
                    {
                        "input" => ElectricalPinType.EptInput, "output" => ElectricalPinType.EptOutput, "bidirectional" => ElectricalPinType.EptBidirectional,
                        "tri_state" => ElectricalPinType.EptTristate, "passive" => ElectricalPinType.EptPassive, "free" => ElectricalPinType.EptFree,
                        "unspecified" => ElectricalPinType.EptUnspecified, "power_in" => ElectricalPinType.EptPowerInput, "power_out" => ElectricalPinType.EptPowerOutput,
                        "open_collector" => ElectricalPinType.EptOpenCollector, "open_emitter" => ElectricalPinType.EptOpenEmitter,
                        "no_connect" => ElectricalPinType.EptNoConnect, var other => throw new AssertFailedException("Unknown pin type " + other)
                    }, hide));
                }
            }
        return [.. declarations.Select(declaration =>
        {
            var symbol = declaration.Symbol.Clone();
            foreach (var child in symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)))
            {
                var pin = child.Item.Unpack<SchematicPin>();
                var (type, hide) = facts[(symbol.CacheKey, child.Unit?.Unit ?? 0, child.BodyStyle?.Style ?? 0, pin.Number)];
                pin.ElectricalType = type;
                pin.Visible = !hide && !hidden.Contains((symbol.CacheKey, pin.Number));
                child.Item = Any.Pack(pin);
            }
            return declaration with { Symbol = symbol };
        })];
    }

    private static SchematicDesign WithPsuNets(SchematicDesign design, IReadOnlyList<CircuitNet> nets) =>
        design with { Engineering = design.Engineering with { Circuit = design.Engineering.Circuit with { Nets = nets } } };

    private static (DesignRecoveryState Saved, SchematicDesign Desired) ConnectedRevision(DesignRecoveryState state, Func<Circuit, Circuit> edit)
    {
        var design = WithCircuit(state.Baseline, edit);
        var saved = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
        return (saved, DesignRecoveryStore.ReadDesired(saved));
    }

    // The saved record and the desired design with one more symbol on the root sheet whose library definition KiCad cannot
    // resolve: captured, as KiCad captures such a symbol, with its library identity and fields but no definition of its
    // own (no unit and no pins), on the saved, observed and desired sheets and in both electrical checkpoints. The design
    // does not own it.
    private static (DesignRecoveryState State, SchematicDesign Desired) WithUnresolvedSymbol(DesignRecoveryState state, SchematicDesign desired,
        string reference)
    {
        var library = new Kiapi.Common.Types.LibraryIdentifier { LibraryNickname = "Unavailable", EntryName = "Unresolved" };
        void Add(SchematicHierarchyData data)
        {
            var root = data.Instances.Single(s => s.Metadata.Document.SheetPath.Path.Count == 1);
            root.Items.Add(Any.Pack(new SchematicSymbolInstance
            {
                Id = new() { Value = "7e57f1c5-0000-4000-8000-00000000c0de" }, Path = root.Metadata.Document.SheetPath.Clone(),
                Position = new() { XNm = 266_700_000, YNm = 25_400_000 }, LibraryId = library.Clone(), Definition = new() { Id = library.Clone() },
                Unit = new() { Unit = 1 }, SeparatePinIdentities = true,
                ReferenceField = new() { Name = "Reference", Text = new() { Text_ = reference, Attributes = new() { Multiline = true } } },
                ValueField = new() { Name = "Value", Text = new() { Text_ = "Unresolved", Attributes = new() { Multiline = true } } }
            }));
        }
        var baseline = state.Baseline.Schematic.Clone(); Add(baseline);
        var observed = state.Observed.Clone(); Add(observed);
        var baselineElectrical = state.BaselineElectrical!.Clone(); Add(baselineElectrical.Hierarchy.Data);
        var observedElectrical = state.ObservedElectrical!.Clone(); Add(observedElectrical.Hierarchy.Data);
        var wanted = desired.Schematic.Clone(); Add(wanted);
        return (state with { Baseline = state.Baseline with { Schematic = baseline }, Observed = observed,
            BaselineElectrical = baselineElectrical, ObservedElectrical = observedElectrical }, desired with { Schematic = wanted });
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
    /// partition is built from the baseline's own nets through its exact placed-pin identities, plus the listed
    /// labels, sheet pins and wires of each net (by net name), which must already be on the baseline's screens.</summary>
    private static DesignRecoveryState PsuCpuConnectedState(SchematicDesign baseline,
        IReadOnlyDictionary<string, List<(string Path, KIID Id)>>? decorations = null)
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
            foreach (var (path, id) in decorations?.GetValueOrDefault(net.Name) ?? [])
            {
                if (!sheets.TryGetValue(path, out var contents))
                    sheets.Add(path, contents = new() { Path = screens[path].Metadata.Document.SheetPath.Clone() });
                contents.Items.Add(id.Clone());
            }
            if (sheets.Count == 0) continue;
            var native = new SchematicNet { Name = "/" + net.Name };
            native.Sheets.Add(sheets.Values);
            electrical.Nets.Add(native);
        }
        // Like KiCad, this editor joins the pins one placed symbol draws at one point (the LP3982's pins 1 and 4):
        // an unconnected sibling joins its group's net, and a group with no connected pin is a net of its own.
        foreach (var occurrence in circuit.Symbols.OrderBy(s => s.Id))
        {
            string path = paths[occurrence.EffectiveSheetInstanceId(components[occurrence.ComponentId])];
            var screen = screens[path];
            var symbol = screen.Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>())
                .Single(s => s.Id.Value == natives[occurrence.Id]);
            var placed = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor)
                    && !(c.Unit?.Unit is > 0 && c.Unit.Unit != occurrence.Unit) && !(c.BodyStyle?.Style is > 0 && c.BodyStyle.Style != (symbol.BodyStyle?.Style ?? 1)))
                .Select(c => c.Item.Unpack<SchematicPin>())
                .Where(p => p.LibraryPinId is not null && p.Position is not null && p.ElectricalType != ElectricalPinType.EptNoConnect);
            foreach (var stack in placed.GroupBy(p => (p.Position.XNm, p.Position.YNm)).Where(g => g.Count() > 1))
            {
                var ids = stack.Select(p => p.Id.Value).ToHashSet(StringComparer.Ordinal);
                var joined = electrical.Nets.FirstOrDefault(n => n.Sheets.Any(s => string.Join('/', s.Path.Path.Select(p => p.Value)) == path
                    && s.Items.Any(i => ids.Contains(i.Value))));
                if (joined is null)
                {
                    joined = new SchematicNet { Name = "Net-(" + components[occurrence.ComponentId].Reference + "-Pad" + stack.First().Number + ")" };
                    electrical.Nets.Add(joined);
                }
                var contents = joined.Sheets.FirstOrDefault(s => string.Join('/', s.Path.Path.Select(p => p.Value)) == path);
                if (contents is null) joined.Sheets.Add(contents = new() { Path = screen.Metadata.Document.SheetPath.Clone() });
                foreach (var pin in stack.Where(p => !contents.Items.Any(i => i.Value == p.Id.Value)))
                    contents.Items.Add(pin.Id.Clone());
            }
        }
        return new(Guid.NewGuid(), Guid.NewGuid(), new("psu-cpu-epoch", 7), false, baseline,
            Encoding.UTF8.GetBytes(SchematicDesignXml.Write(baseline, [])), baseline.Schematic.Clone(), [],
            BaselineElectrical: electrical, ObservedElectrical: electrical.Clone());
    }

    /// <summary>The fixture's Complete stage as the editor shows it once wired (psu-cpu-fixture-and-ownership.md
    /// §1.6.3): <paramref name="realized"/>'s screens gain the hierarchical labels, sheet pins and required local
    /// labels of expected-native.json (except <paramref name="withoutLocalLabels"/>) plus one wire per net and sheet,
    /// and the native partition joins each net's placed pins with exactly those items.</summary>
    private static (SchematicDesign Design, DesignRecoveryState State) PsuCpuWired(SchematicDesign realized, params string[] withoutLocalLabels)
    {
        var expected = PsuCpuFixture.ExpectedNative(PsuCpuStage.Complete);
        var schematic = realized.Schematic.Clone();
        var circuit = realized.Engineering.Circuit;
        string PathOf(Guid instance) => SchematicDesignBindings.PathKey(realized.SheetBindings.Single(b => b.SheetInstanceId == instance).NativePath);
        string KeyPath(string key) => PathOf(expected.Sheets.Single(s => s.Key == key).ModelSheetInstance);
        SchematicScreenData Screen(string path) => schematic.Instances.Single(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == path);
        var items = new Dictionary<string, List<(string Path, KIID Id)>>(StringComparer.Ordinal);
        long column = 0;
        Kiapi.Common.Types.Vector2 Point() => new() { XNm = 12_700_000 + 2_540_000 * column++, YNm = 190_500_000 };
        static Kiapi.Common.Types.Text Caption(string text) => new() { Text_ = text, Attributes = new() { Multiline = false } };
        KIID Note(string net, string path)
        {
            var id = new KIID { Value = Guid.NewGuid().ToString("D") };
            if (!items.TryGetValue(net, out var list)) items.Add(net, list = []);
            list.Add((path, id));
            return id;
        }
        foreach (var (sheet, texts) in expected.HierarchicalLabels)
            foreach (var text in texts)
                Screen(KeyPath(sheet)).Items.Add(Any.Pack(new HierarchicalLabel { Id = Note(text, KeyPath(sheet)).Clone(), Position = Point(), Text = Caption(text),
                    SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked }));
        foreach (var (sheet, texts) in expected.SheetPins)
        {
            var own = expected.Sheets.Single(s => s.Key == sheet);
            string parent = KeyPath(own.Parent!);
            var screen = Screen(parent);
            int index = screen.Items.ToList().FindIndex(i => i.Is(SheetSymbol.Descriptor) && i.Unpack<SheetSymbol>().Id.Value == own.NativeSheetSymbol!.Value.ToString("D"));
            var symbol = screen.Items[index].Unpack<SheetSymbol>();
            foreach (var text in texts)
                symbol.Pins.Add(new SheetPin { Id = Note(text, parent).Clone(), Position = Point(), Text = Caption(text), Side = SheetSide.ShsLeft,
                    SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked });
            screen.Items[index] = Any.Pack(symbol);
        }
        foreach (var (sheet, texts) in expected.RequiredLocalLabelNames)
            foreach (var text in texts.Where(t => !withoutLocalLabels.Contains(t, StringComparer.Ordinal)))
                Screen(KeyPath(sheet)).Items.Add(Any.Pack(new LocalLabel { Id = Note(text, KeyPath(sheet)).Clone(), Position = Point(), Text = Caption(text),
                    SpinStyle = SchematicLabelSpinStyle.SlssRight, Locked = LockedState.LsUnlocked }));
        // One wire per net and sheet joins that sheet's pins and labels of the net.
        var components = circuit.Components.ToDictionary(c => c.Id);
        foreach (var net in circuit.Nets.OrderBy(n => n.Id))
        {
            var paths = new SortedSet<string>(items.GetValueOrDefault(net.Name)?.Select(i => i.Path) ?? [], StringComparer.Ordinal);
            foreach (var endpoint in net.Pins)
            {
                var component = components[endpoint.ComponentId];
                var part = circuit.Parts.Single(p => p.Id == circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == component.DefinitionId).PartId);
                int unit = part.Pins.Single(p => p.Number == endpoint.Pin).Unit;
                foreach (var occurrence in circuit.Symbols.Where(o => o.ComponentId == component.Id && (unit == 0 || o.Unit == unit)))
                    paths.Add(PathOf(occurrence.EffectiveSheetInstanceId(component)));
            }
            foreach (var path in paths)
                Screen(path).Items.Add(Any.Pack(new SchematicLine { Id = Note(net.Name, path).Clone(), Start = Point(), End = Point(),
                    Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked }));
        }
        var design = realized with { Schematic = schematic };
        return (design, PsuCpuConnectedState(design, items));
    }
}
