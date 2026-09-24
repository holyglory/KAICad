using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ElectricalPinType = Kiapi.Common.Types.ElectricalPinType;

namespace KiCad.Automation.Tests;

// CN-1 classification, preparation guards and connection intent (cn1-wiring-intent.md §4.1, §4.4 and §5).
// These are unit tests of isolated pure logic: no editor can exercise an admitted connected addition until
// native advertises schematic.connection-realization.v1, which it does not yet, and nothing can draw the
// planned connections before the realizer and the native assertion exist. The rendered NativeXmlComponentCreation
// and NativePsuCpuComponentCreation journeys check the same gate and build intents from real handshakes and
// captured states without applying them, and the existing plan tests here pin the unchanged general-path results.
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
    public void ARealizationPlanStopsBeforeTheEditorUntilTheRealizerExists()
    {
        // The executor hands a realization plan to the lane entry point (§9.1 steps 2-3). Without the capability for
        // the recorded instance it is refused with native_capability_missing; with it, this build still has no
        // label-stub realizer, so it stops with connected_addition_unavailable. Both happen before any measurement,
        // journal entry or native request, which is why the editor and checkpoint are never touched here.
        var state = Fixture(); var circuit = state.Baseline.Engineering.Circuit;
        var (saved, _) = ConnectedRevision(state, c => c with { Nets = [.. c.Nets,
            new(Guid.NewGuid(), "SIG", [new(circuit.Components[0].Id, "1"), new(circuit.Components[1].Id, "1")])] });
        var plan = SchematicSynchronizationPlanner.Plan(saved, ConnectedSession(saved.InstanceId, realization: true));
        Assert.IsNotNull(plan.Connections, plan.ErrorCode + " " + plan.ErrorMessage);
        foreach (var (problem, session, code) in new (string, AutomationSession, string)[]
        {
            ("today's editor", ConnectedSession(saved.InstanceId, realization: false), SchematicConnectionErrors.NativeCapabilityMissing),
            ("another instance's handshake", ConnectedSession(Guid.NewGuid(), realization: true), SchematicConnectionErrors.NativeCapabilityMissing),
            ("a realizing editor", ConnectedSession(saved.InstanceId, realization: true), SchematicConnectionErrors.ConnectedAdditionUnavailable)
        })
        {
            var error = Assert.ThrowsExactly<AutomationException>(() => { _ = SchematicConnectedAddition.RealizeAsync(null!, session, saved, plan, null!); }, problem);
            Assert.AreEqual(code, error.Code, problem + ": " + error.Message);
            StringAssert.Contains(error.Message, "nothing was changed", problem);
        }
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
