using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Common.Types;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Tests;

// CN-1 connection intent (cn1-wiring-intent.md §5) on small hand-built schematics: the contract's own examples
// (§15), every §13 planning refusal, and the limits. These are unit tests of pure planning logic on purpose: the
// end-to-end journeys (psu-cpu-connected in NativeXmlComponentCreationJourney, and McpReattachmentJourney) build intents
// from a real editor that advertises schematic.connection-realization.v1 and apply them with KiCad's connectivity
// assertion, but a valid fixture reaches only the admitted cases; most §13 refusals and the limits need hand-built states
// that no editor produces on request. SchematicSynchronizationPlanTests.Connected checks the same planner on the frozen
// PSU/CPU fixture and the repeated-sheet fixture.
// Each case goes through the public planner with a session that advertises the capability, so classification,
// the creation guards, the intent and the XML round trip all run; only states no valid saved design can reach
// (a second top-level sheet, pins without their own identities, oversized revisions) call the builder directly.
// Lane 2A created this file with SchematicConnectionIntentBuilder.cs under the cn1-intent integration grant;
// registering both in psu-cpu-fixture-and-ownership.md §2.3 is a seam request to the integration owner.
[TestClass]
public sealed class SchematicConnectionIntentBuilderTests
{
    [TestMethod]
    public void RootAdditionFollowsTheContractExample()
    {
        // §15 "Root addition": R1.1 is in SIG with a local label; TP1.1 is bare. The XML adds R2, puts R2.1 in SIG
        // and makes {R2.2, TP1.1} a new net /OUT.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid tp = bench.Part("TP", Passive("1"));
        Guid r1 = bench.Component(r, "R1"), tp1 = bench.Component(tp, "TP1");
        string wire = bench.Wire(BenchSheet.Root), label = bench.LocalLabel(BenchSheet.Root, "SIG");
        var sig = new CircuitNet(Guid.NewGuid(), "SIG", [new(r1, "1")]);
        var state = bench.State([sig], new() { [sig.Id] = [(BenchSheet.Root, wire), (BenchSheet.Root, label)] });
        var (created, r2) = bench.Create(state.Baseline, r, "R2", BenchSheet.Root);
        var output = new CircuitNet(Guid.NewGuid(), "/OUT", [new(r2, "2"), new(tp1, "1")]);
        var (saved, _) = Revise(state, _ => WithNets(created, sig with { Pins = [.. sig.Pins, new(r2, "1")] }, output));

        var plan = Plan(saved);
        var intent = RequireRealizationPlan(plan);
        Assert.IsNull(SchematicSynchronizationPlanner.Plan(saved).Connections, "Without the capability the plan keeps its existing path.");
        Assert.AreEqual(SchematicConnectionIntent.CurrentVersion, intent.Version);
        Assert.AreEqual(saved.OriginId, intent.OriginId);
        Assert.AreEqual(saved.Baseline.Engineering.Circuit.Id, intent.CircuitId);
        Assert.AreEqual(new KiCad.Automation.Model.DocumentRevision("bench", 7), intent.NativeRevision);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(saved.DesiredFileBytes)), intent.DesiredSha256);
        var candidate = plan.Candidate!;
        Guid r2Symbol = NativeSymbol(candidate, r2);
        CollectionAssert.AreEqual(new[] { r2Symbol }, intent.CreatedSymbolIds.ToArray());
        CollectionAssert.AreEqual(new[] { sig.Id, output.Id }.Order().ToArray(), intent.Nets.Select(n => n.NetId).ToArray());
        Assert.IsTrue(intent.Nets.All(n => n.Scope == ConnectionScope.Local && n.GlobalName is null));
        CollectionAssert.AreEqual(new[] { new PinEndpoint(r2, "1") }, intent.Nets.Single(n => n.NetId == sig.Id).AddedPins.ToArray());
        CollectionAssert.AreEqual(Ordered(new(r2, "2"), new(tp1, "1")), intent.Nets.Single(n => n.NetId == output.Id).AddedPins.ToArray());
        Assert.IsEmpty(intent.Ports);

        var screen = intent.Screens.Single();
        Assert.AreEqual(bench.ScreenId(BenchSheet.Root), screen.ScreenId);
        CollectionAssert.AreEqual(new[] { bench.Path(BenchSheet.Root) }, screen.InstancePathKeys.ToArray());
        var sigIsland = screen.Islands.Single(i => i.NetId == sig.Id);
        Assert.AreEqual("SIG", sigIsland.LabelText, "The existing label names the island (§5.5 precedence 2).");
        Assert.IsTrue(sigIsland.AnchorHasMatchingDriver); Assert.IsFalse(sigIsland.JoinRequired); Assert.IsEmpty(sigIsland.JoinCandidates);
        CollectionAssert.AreEquivalent(new[] { bench.PinId(r1, "1"), Guid.Parse(wire), Guid.Parse(label) }, sigIsland.AnchorItemIds.ToArray());
        var existing = sigIsland.Members.Single(m => m.Pin.Endpoint.ComponentId == r1);
        Assert.IsTrue(existing.AlreadyConnected); Assert.IsFalse(existing.RequiresStub); Assert.IsFalse(existing.Pin.CreatedSymbol);
        var added = sigIsland.Members.Single(m => m.Pin.Endpoint.ComponentId == r2);
        Assert.IsTrue(added.RequiresStub); Assert.IsTrue(added.Pin.CreatedSymbol); Assert.AreEqual(r2Symbol, added.Pin.SymbolId);
        var outIsland = screen.Islands.Single(i => i.NetId == output.Id);
        Assert.AreEqual("OUT", outIsland.LabelText, "A new net is labelled by its name after the last '/'.");
        Assert.IsTrue(outIsland.Members.All(m => m.RequiresStub && m.Role == ConnectionMemberRole.Signal));
        Assert.IsFalse(outIsland.AnchorHasMatchingDriver); Assert.IsFalse(outIsland.JoinRequired); Assert.IsEmpty(outIsland.AnchorItemIds);
        CollectionAssert.AreEqual(screen.Islands.Select(i => i.NetId).Order().ToArray(), screen.Islands.Select(i => i.NetId).ToArray());

        RequireGroups(intent, [Keys(candidate, (r1, "1"), (r2, "1")), Keys(candidate, (r2, "2"), (tp1, "1"))]);
        // Deterministic: the same saved revision plans the same intent.
        Assert.AreEqual(JsonSerializer.Serialize(SchematicConnectionIntentBuilder.Summary(intent)),
            JsonSerializer.Serialize(SchematicConnectionIntentBuilder.Summary(Plan(saved).Connections!)));
        Assert.AreEqual(intent.ExpectedGroups.Count, JsonDocument.Parse(JsonSerializer.Serialize(SchematicConnectionIntentBuilder.Summary(intent)))
            .RootElement.GetProperty("expectedGroupCount").GetInt32());
    }

    [TestMethod]
    public void HierarchyCrossingAddsOneUplinkAndOneSheetPin()
    {
        // §15 "Hierarchy crossing": new U1.3 on the child sheet joins DATA with bare root pin R5.2.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2")), t = bench.Part("T3", Passive("1"), Passive("2"), Passive("3"));
        Guid r5 = bench.Component(r, "R5");
        bench.Component(t, "U9", BenchSheet.Child);
        var state = bench.State([]);
        var (created, u1) = bench.Create(state.Baseline, t, "U1", BenchSheet.Child);
        var data = new CircuitNet(Guid.NewGuid(), "DATA", [new(u1, "3"), new(r5, "2")]);
        var (saved, _) = Revise(state, _ => WithNets(created, data));
        var plan = Plan(saved);
        var intent = RequireRealizationPlan(plan);
        var net = intent.Nets.Single();
        Assert.AreEqual(ConnectionScope.Local, net.Scope);
        CollectionAssert.AreEqual(Ordered(new(u1, "3"), new(r5, "2")), net.AddedPins.ToArray());
        var port = intent.Ports.Single();
        Assert.AreEqual(bench.Instance(BenchSheet.Child), port.ChildSheetInstanceId);
        Assert.AreEqual(bench.Instance(BenchSheet.Root), port.ParentSheetInstanceId);
        Assert.AreEqual(bench.Path(BenchSheet.Child), port.ChildPathKey); Assert.AreEqual(bench.Path(BenchSheet.Root), port.ParentPathKey);
        Assert.AreEqual(bench.ScreenId(BenchSheet.Child), port.ChildScreenId); Assert.AreEqual(bench.ScreenId(BenchSheet.Root), port.ParentScreenId);
        Assert.AreEqual(bench.ChildSheetSymbol, port.SheetSymbolId);
        Assert.AreEqual("DATA", port.PortText); Assert.IsFalse(port.SheetPinExists); Assert.IsFalse(port.UplinkLabelExists);
        CollectionAssert.AreEqual(new[] { bench.ScreenId(BenchSheet.Root), bench.ScreenId(BenchSheet.Child) }.Order().ToArray(),
            intent.Screens.Select(s => s.ScreenId).ToArray());
        var child = Island(intent, BenchSheet.Child, bench);
        Assert.AreEqual(bench.ChildSheetSymbol, child.UplinkSheetSymbolId);
        Assert.IsEmpty(child.ChildSheetSymbolIds);
        Assert.IsTrue(child.Members.Single().RequiresStub);
        var root = Island(intent, BenchSheet.Root, bench);
        Assert.IsNull(root.UplinkSheetSymbolId);
        CollectionAssert.AreEqual(new[] { bench.ChildSheetSymbol }, root.ChildSheetSymbolIds.ToArray());
        Assert.AreEqual("DATA", root.LabelText); Assert.AreEqual("DATA", child.LabelText);
        // U1's other two pins stay alone.
        var candidate = plan.Candidate!;
        RequireGroups(intent, [Keys(candidate, (u1, "3"), (r5, "2")), Keys(candidate, (u1, "1")), Keys(candidate, (u1, "2"))]);
    }

    [TestMethod]
    public void AnExistingCrossingIsReusedInsteadOfDuplicated()
    {
        // False-positive guard: DATA already crosses through sheet pin DATA and hierarchical label DATA, so a new child
        // pin gets only its own stub. No second sheet pin, no uplink, and the root side needs nothing.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid r5 = bench.Component(r, "R5"), r9 = bench.Component(r, "R9", BenchSheet.Child), r6 = bench.Component(r, "R6", BenchSheet.Child);
        var items = new[]
        {
            (BenchSheet.Root, bench.Wire(BenchSheet.Root)), (BenchSheet.Root, bench.LocalLabel(BenchSheet.Root, "DATA")),
            (BenchSheet.Root, bench.SheetPin(BenchSheet.Child, "DATA")),
            (BenchSheet.Child, bench.Wire(BenchSheet.Child)), (BenchSheet.Child, bench.HierarchicalLabel(BenchSheet.Child, "DATA"))
        };
        var data = new CircuitNet(Guid.NewGuid(), "/DATA", [new(r5, "2"), new(r9, "1")]);
        var state = bench.State([data], new() { [data.Id] = items });
        var (saved, _) = Revise(state, d => WithNets(d, data with { Pins = [.. data.Pins, new(r6, "1")] }));
        var intent = RequireRealizationPlan(Plan(saved));
        var port = intent.Ports.Single();
        Assert.IsTrue(port.SheetPinExists); Assert.IsTrue(port.UplinkLabelExists); Assert.AreEqual("DATA", port.PortText);
        var screen = intent.Screens.Single();
        Assert.AreEqual(bench.ScreenId(BenchSheet.Child), screen.ScreenId, "Only the child sheet gets new items.");
        var child = screen.Islands.Single();
        Assert.IsNull(child.UplinkSheetSymbolId); Assert.IsEmpty(child.ChildSheetSymbolIds);
        Assert.IsTrue(child.AnchorHasMatchingDriver); Assert.IsFalse(child.JoinRequired);
        Assert.AreEqual(new PinEndpoint(r6, "1"), child.Members.Single(m => m.RequiresStub).Pin.Endpoint);
        Assert.IsTrue(child.Members.Single(m => m.Pin.Endpoint.ComponentId == r9).AlreadyConnected);
        RequireGroups(intent, [Keys(saved.Baseline, (r5, "2"), (r9, "1"), (r6, "1"))]);
    }

    [TestMethod]
    public void AnUnlabelledExistingConnectionIsJoinedAtItsOwnPins()
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), r3 = bench.Component(r, "R3");
        var link = new CircuitNet(Guid.NewGuid(), "LINK", [new(r1, "1"), new(r2, "1")]);
        var state = bench.State([link], new() { [link.Id] = [(BenchSheet.Root, bench.Wire(BenchSheet.Root))] });
        var (saved, _) = Revise(state, d => WithNets(d, link with { Pins = [.. link.Pins, new(r3, "1")] }));
        var island = RequireRealizationPlan(Plan(saved)).Screens.Single().Islands.Single();
        Assert.AreEqual("LINK", island.LabelText);
        Assert.IsFalse(island.AnchorHasMatchingDriver);
        Assert.IsTrue(island.JoinRequired, "The wire has no label, so the realized label must also reach the existing pins.");
        CollectionAssert.AreEqual(island.Members.Where(m => m.AlreadyConnected).Select(m => m.Pin).ToArray(), island.JoinCandidates.ToArray());
        CollectionAssert.AreEquivalent(new[] { r1, r2 }, island.JoinCandidates.Select(p => p.Endpoint.ComponentId).ToArray());
        CollectionAssert.AreEqual(island.Members.OrderBy(m => m.Pin.Endpoint.ComponentId).ToArray(), island.Members.ToArray());
    }

    [TestMethod]
    public void AGlobalNameGivenToAnUnlabelledConnectionIsJoinedAtItsExistingPins()
    {
        // §5.4 global scope: LINK = {R1.1, R2.1} is joined only by a wire. The XML adds a new global power symbol
        // V5 to LINK, so LINK becomes the global net V5 and the existing connection must receive V5 at exactly one
        // island, at its own already-connected signal pins.
        var (bench, state, link, parts) = GlobalLinkBench(labelled: false);
        var (created, pwr2) = bench.Create(state.Baseline, parts.Power, "#PWR2", BenchSheet.Root, "V5");
        var plan = Plan(Revise(state, _ => WithNets(created, parts.Gnd, link with { Pins = [.. link.Pins, new(pwr2, "1")] })).Saved);
        var intent = RequireRealizationPlan(plan);
        var net = intent.Nets.Single();
        Assert.AreEqual(link.Id, net.NetId);
        Assert.AreEqual(ConnectionScope.Global, net.Scope); Assert.AreEqual("V5", net.GlobalName);
        CollectionAssert.AreEqual(new[] { new PinEndpoint(pwr2, "1") }, net.AddedPins.ToArray());
        Assert.IsEmpty(intent.Ports, "A global name needs no hierarchical port.");
        var island = intent.Screens.Single().Islands.Single();
        Assert.AreEqual(ConnectionScope.Global, island.Scope); Assert.AreEqual("V5", island.LabelText);
        Assert.IsFalse(island.AnchorHasMatchingDriver, "Nothing in the existing connection carries V5.");
        Assert.IsTrue(island.JoinRequired, "V5 must also reach R1.1 and R2.1, or the wire stays a separate net.");
        CollectionAssert.AreEqual(Ordered(new(parts.R1, "1"), new(parts.R2, "1")), island.JoinCandidates.Select(p => p.Endpoint).ToArray(),
            "The join candidates are the already-connected signal pins in member order.");
        CollectionAssert.AreEqual(island.Members.Where(m => m.AlreadyConnected).Select(m => m.Pin).ToArray(), island.JoinCandidates.ToArray());
        var carrier = island.Members.Single(m => m.Pin.Endpoint.ComponentId == pwr2);
        Assert.AreEqual(ConnectionMemberRole.PowerCarrier, carrier.Role); Assert.AreEqual("V5", carrier.PowerName);
        Assert.IsTrue(carrier.Pin.CreatedSymbol); Assert.IsFalse(carrier.AlreadyConnected); Assert.IsFalse(carrier.RequiresStub);
        Assert.IsFalse(island.Members.Any(m => m.RequiresStub), "Only the join carries V5; no pin needs a new stub.");
        Assert.IsNull(island.UplinkSheetSymbolId); Assert.IsEmpty(island.ChildSheetSymbolIds);
        CollectionAssert.AreEquivalent(new[] { bench.PinId(parts.R1, "1"), bench.PinId(parts.R2, "1"), parts.Wire }, island.AnchorItemIds.ToArray());
        RequireGroups(intent, [Keys(plan.Candidate!, (parts.R1, "1"), (parts.R2, "1"), (pwr2, "1"))]);

        // False-positive guard: the same connection already carries V5 through a global label, so neither the new
        // power symbol nor a new signal pin makes it join; only the new signal pin gets a stub.
        var (named, namedState, namedLink, namedParts) = GlobalLinkBench(labelled: true);
        var (namedCreated, pwr3) = named.Create(namedState.Baseline, namedParts.Power, "#PWR2", BenchSheet.Root, "V5");
        var carried = RequireRealizationPlan(Plan(Revise(namedState, _ => WithNets(namedCreated, namedParts.Gnd,
            namedLink with { Pins = [.. namedLink.Pins, new(pwr3, "1")] })).Saved));
        Assert.AreEqual("V5", carried.Nets.Single().GlobalName);
        Assert.IsEmpty(carried.Screens.Single().Islands, "The existing global label already names the connection: nothing is drawn.");
        var withPin = Plan(Revise(namedState, _ => WithNets(namedCreated, namedParts.Gnd,
            namedLink with { Pins = [.. namedLink.Pins, new(pwr3, "1"), new(namedParts.R4, "1")] })).Saved);
        var stubbed = RequireRealizationPlan(withPin).Screens.Single().Islands.Single();
        Assert.AreEqual("V5", stubbed.LabelText);
        Assert.IsTrue(stubbed.AnchorHasMatchingDriver); Assert.IsFalse(stubbed.JoinRequired); Assert.IsEmpty(stubbed.JoinCandidates);
        CollectionAssert.AreEqual(new[] { new PinEndpoint(namedParts.R4, "1") }, stubbed.Members.Where(m => m.RequiresStub).Select(m => m.Pin.Endpoint).ToArray());
        CollectionAssert.Contains(stubbed.AnchorItemIds.ToArray(), namedParts.Label!.Value);
        RequireGroups(RequireRealizationPlan(withPin), [Keys(withPin.Candidate!, (namedParts.R1, "1"), (namedParts.R2, "1"), (namedParts.R4, "1"), (pwr3, "1"))]);
    }

    [TestMethod]
    public void AGlobalNameJoinsTheExistingConnectionAtExactlyOneIsland()
    {
        // LINK already spans the root (R1.1, R2.1) and the child sheet (R9.1) through sheet pin and hierarchical
        // label LINK. A new V5 power symbol on the root makes it global. Only the island of the first
        // already-connected signal pin in member order is joined: R9 has the smallest identity, so the join lands
        // on the child sheet even though the root comes first by path and holds the new symbol.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid power = bench.Part("PWR", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        Guid pwr1 = bench.Component(power, "#PWR1", value: "GND");
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), r3 = bench.Component(r, "R3");
        Guid r9 = bench.Component(r, "R9", BenchSheet.Child, id: Guid.Parse("00000000-0000-4000-8000-000000000001"));
        var gnd = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr1, "1"), new(r3, "1")]);
        var link = new CircuitNet(Guid.NewGuid(), "LINK", [new(r1, "1"), new(r2, "1"), new(r9, "1")]);
        var state = bench.State([gnd, link], new()
        {
            [link.Id] = [(BenchSheet.Root, bench.Wire(BenchSheet.Root)), (BenchSheet.Root, bench.SheetPin(BenchSheet.Child, "LINK")),
                (BenchSheet.Child, bench.HierarchicalLabel(BenchSheet.Child, "LINK")), (BenchSheet.Child, bench.Wire(BenchSheet.Child))]
        });
        var (created, pwr2) = bench.Create(state.Baseline, power, "#PWR2", BenchSheet.Root, "V5");
        var plan = Plan(Revise(state, _ => WithNets(created, gnd, link with { Pins = [.. link.Pins, new(pwr2, "1")] })).Saved);
        var intent = RequireRealizationPlan(plan);
        Assert.AreEqual("V5", intent.Nets.Single().GlobalName);
        Assert.IsEmpty(intent.Ports);
        var joined = intent.Screens.SelectMany(s => s.Islands).Where(i => i.JoinRequired).ToArray();
        Assert.HasCount(1, joined, "Exactly one island gives the existing connection its global name.");
        Assert.AreEqual(bench.Instance(BenchSheet.Child), joined[0].SheetInstanceId);
        CollectionAssert.AreEqual(new[] { new PinEndpoint(r9, "1") }, joined[0].JoinCandidates.Select(p => p.Endpoint).ToArray());
        Assert.AreEqual("V5", joined[0].LabelText);
        Assert.HasCount(1, intent.Screens.SelectMany(s => s.Islands).ToArray(), "The root island needs nothing: its pins already reach the child.");
        CollectionAssert.AreEqual(new[] { bench.ScreenId(BenchSheet.Root), bench.ScreenId(BenchSheet.Child) }.Order().ToArray(),
            intent.Screens.Select(s => s.ScreenId).ToArray(), "The root screen is listed for the created symbol, the child for the join.");
        RequireGroups(intent, [Keys(plan.Candidate!, (r1, "1"), (r2, "1"), (r9, "1"), (pwr2, "1"))]);
    }

    [TestMethod]
    public void AnExistingConnectionReachedOnlyThroughANewUplinkIsJoinedAtItsOwnPins()
    {
        // §5.4 step 5, second clause: DATA exists only on the child sheet, wired and labelled "DATA". The XML adds a
        // bare root pin, so the child island needs a hierarchical label but has no new pin to attach it to. Even
        // with a matching label there, the uplink must be joined at the existing pins.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid r5 = bench.Component(r, "R5"), r9 = bench.Component(r, "R9", BenchSheet.Child), r6 = bench.Component(r, "R6", BenchSheet.Child);
        Guid r7 = bench.Component(r, "R7", BenchSheet.Child);
        string wire = bench.Wire(BenchSheet.Child), label = bench.LocalLabel(BenchSheet.Child, "DATA");
        var data = new CircuitNet(Guid.NewGuid(), "/Child/DATA", [new(r9, "1"), new(r6, "1")]);
        var state = bench.State([data], new() { [data.Id] = [(BenchSheet.Child, wire), (BenchSheet.Child, label)] });
        var (saved, _) = Revise(state, d => WithNets(d, data with { Pins = [.. data.Pins, new(r5, "2")] }));
        var intent = RequireRealizationPlan(Plan(saved));
        var net = intent.Nets.Single();
        Assert.AreEqual(ConnectionScope.Local, net.Scope);
        CollectionAssert.AreEqual(new[] { new PinEndpoint(r5, "2") }, net.AddedPins.ToArray());
        var port = intent.Ports.Single();
        Assert.AreEqual(bench.ChildSheetSymbol, port.SheetSymbolId); Assert.AreEqual("DATA", port.PortText);
        Assert.IsFalse(port.SheetPinExists); Assert.IsFalse(port.UplinkLabelExists);
        var child = Island(intent, BenchSheet.Child, bench);
        Assert.AreEqual("DATA", child.LabelText);
        Assert.IsTrue(child.AnchorHasMatchingDriver, "The local label DATA already names the connection on the child sheet.");
        Assert.AreEqual(bench.ChildSheetSymbol, child.UplinkSheetSymbolId);
        Assert.IsEmpty(child.ChildSheetSymbolIds);
        Assert.IsFalse(child.Members.Any(m => m.RequiresStub));
        Assert.IsTrue(child.JoinRequired, "The new hierarchical label has no new pin to sit on, so it joins the existing ones.");
        CollectionAssert.AreEqual(Ordered(new(r9, "1"), new(r6, "1")), child.JoinCandidates.Select(p => p.Endpoint).ToArray());
        CollectionAssert.AreEqual(child.Members.Select(m => m.Pin).ToArray(), child.JoinCandidates.ToArray());
        CollectionAssert.AreEquivalent(new[] { bench.PinId(r9, "1"), bench.PinId(r6, "1"), Guid.Parse(wire), Guid.Parse(label) }, child.AnchorItemIds.ToArray());
        var root = Island(intent, BenchSheet.Root, bench);
        Assert.AreEqual("DATA", root.LabelText);
        Assert.IsNull(root.UplinkSheetSymbolId);
        CollectionAssert.AreEqual(new[] { bench.ChildSheetSymbol }, root.ChildSheetSymbolIds.ToArray());
        Assert.AreEqual(new PinEndpoint(r5, "2"), root.Members.Single(m => m.RequiresStub).Pin.Endpoint);
        Assert.IsFalse(root.JoinRequired); Assert.IsFalse(root.AnchorHasMatchingDriver); Assert.IsEmpty(root.AnchorItemIds);
        RequireGroups(intent, [Keys(saved.Baseline, (r9, "1"), (r6, "1"), (r5, "2"))]);

        // False-positive guard: when the same revision also adds a bare child pin, the hierarchical label sits on that
        // pin's new stub and the matching local label already names the existing pins, so nothing is joined.
        var (alsoChild, _) = Revise(state, d => WithNets(d, data with { Pins = [.. data.Pins, new(r5, "2"), new(r7, "1")] }));
        var stubbed = Island(RequireRealizationPlan(Plan(alsoChild)), BenchSheet.Child, bench);
        Assert.AreEqual(bench.ChildSheetSymbol, stubbed.UplinkSheetSymbolId);
        Assert.IsFalse(stubbed.JoinRequired); Assert.IsEmpty(stubbed.JoinCandidates);
        CollectionAssert.AreEqual(new[] { new PinEndpoint(r7, "1") }, stubbed.Members.Where(m => m.RequiresStub).Select(m => m.Pin.Endpoint).ToArray());
    }

    private sealed record GlobalLinkParts(Guid Power, Guid R1, Guid R2, Guid R4, CircuitNet Gnd, Guid Wire, Guid? Label);

    /// <summary>LINK = {R1.1, R2.1} joined by a root wire, optionally also carrying a global label V5; GND holds the
    /// template power symbol #PWR1 and R3.1; R4 is bare.</summary>
    private static (Bench Bench, DesignRecoveryState State, CircuitNet Link, GlobalLinkParts Parts) GlobalLinkBench(bool labelled)
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid power = bench.Part("PWR", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        Guid pwr1 = bench.Component(power, "#PWR1", value: "GND");
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), r3 = bench.Component(r, "R3"), r4 = bench.Component(r, "R4");
        var gnd = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr1, "1"), new(r3, "1")]);
        var link = new CircuitNet(Guid.NewGuid(), labelled ? "V5" : "LINK", [new(r1, "1"), new(r2, "1")]);
        string wire = bench.Wire(BenchSheet.Root);
        var items = new List<(BenchSheet Sheet, string Id)> { (BenchSheet.Root, wire) };
        Guid? label = null;
        if (labelled)
        {
            string id = bench.GlobalLabel(BenchSheet.Root, "V5");
            items.Add((BenchSheet.Root, id));
            label = Guid.Parse(id);
        }
        var state = bench.State([gnd, link], new() { [link.Id] = [.. items] });
        return (bench, state, link, new(power, r1, r2, r4, gnd, Guid.Parse(wire), label));
    }

    [TestMethod]
    public void PowerNetsTakeTheirGlobalNameFromPowerSymbolsAndHiddenPowerPins()
    {
        // §15 "Power": VCC holds power symbol #PWR1 and legacy IC U8's hidden VCC pin. The XML adds a new IC U9
        // whose hidden VCC pin joins silently while its signal pin and R2.1 get global labels.
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid power = bench.Part("VCC", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "VCC", 1, ElectricalPinType.EptPowerInput, false));
        Guid ic = bench.Part("IC", new BenchPin("1", "OUT", 1, ElectricalPinType.EptOutput), new BenchPin("2", "VCC", 1, ElectricalPinType.EptPowerInput, false),
            new BenchPin("3", "IN", 1, ElectricalPinType.EptInput));
        Guid pwr = bench.Component(power, "#PWR1", value: "VCC"), u8 = bench.Component(ic, "U8"), r2 = bench.Component(r, "R2"), r3 = bench.Component(r, "R3");
        var vcc = new CircuitNet(Guid.NewGuid(), "VCC", [new(pwr, "1"), new(u8, "2")]);
        var state = bench.State([vcc]);
        var (created, u9) = bench.Create(state.Baseline, ic, "U9", BenchSheet.Root);
        var (saved, _) = Revise(state, _ => WithNets(created, vcc with { Pins = [.. vcc.Pins, new(u9, "2"), new(u9, "1"), new(r2, "1")] }));
        var plan = Plan(saved);
        var intent = RequireRealizationPlan(plan);
        var net = intent.Nets.Single();
        Assert.AreEqual(ConnectionScope.Global, net.Scope); Assert.AreEqual("VCC", net.GlobalName);
        Assert.IsEmpty(intent.Ports, "A global name needs no hierarchical port.");
        var island = intent.Screens.Single().Islands.Single();
        Assert.AreEqual("VCC", island.LabelText); Assert.AreEqual(ConnectionScope.Global, island.Scope);
        Assert.IsTrue(island.AnchorHasMatchingDriver); Assert.IsFalse(island.JoinRequired, "The existing connection already carries VCC.");
        ConnectionMember Member(Guid component, string pin) => island.Members.Single(m => m.Pin.Endpoint == new PinEndpoint(component, pin));
        Assert.AreEqual(ConnectionMemberRole.PowerCarrier, Member(pwr, "1").Role); Assert.AreEqual("VCC", Member(pwr, "1").PowerName);
        Assert.IsTrue(Member(pwr, "1").AlreadyConnected);
        Assert.AreEqual(ConnectionMemberRole.ImplicitPower, Member(u8, "2").Role); Assert.AreEqual("VCC", Member(u8, "2").PowerName);
        Assert.AreEqual(ConnectionMemberRole.ImplicitPower, Member(u9, "2").Role);
        Assert.IsFalse(Member(u9, "2").RequiresStub, "Hidden power pins are never wired (I7).");
        Assert.IsTrue(Member(u9, "1").RequiresStub); Assert.IsTrue(Member(r2, "1").RequiresStub);
        Assert.AreEqual(ConnectionMemberRole.Signal, Member(u9, "1").Role);
        RequireGroups(intent, [Keys(plan.Candidate!, (pwr, "1"), (u8, "2"), (u9, "2"), (u9, "1"), (r2, "1")), Keys(plan.Candidate!, (u9, "3"))]);

        // Putting the hidden VCC pin in another net is refused (§15), and so is leaving it out of every net while
        // VCC exists, because KiCad would join it to VCC anyway.
        var other = new CircuitNet(Guid.NewGuid(), "SIG", [new(u9, "2"), new(r2, "1")]);
        RequireRefusal(Revise(state, _ => WithNets(created, vcc, other)).Saved, SchematicConnectionErrors.ConnectedImplicitPowerConflict, "'VCC'");
        var unrelated = new CircuitNet(Guid.NewGuid(), "SIG", [new(u9, "1"), new(r3, "1")]);
        RequireRefusal(Revise(state, _ => WithNets(created, vcc, unrelated)).Saved, SchematicConnectionErrors.ConnectedImplicitPowerConflict, "U9.2");
        // A template IC's own hidden VCC pin is already a native source of VCC, even when it is in no model net,
        // so a new IC that leaves its hidden VCC pin out of every net is refused as well.
        var lone = new Bench();
        Guid loneIc = lone.Part("IC", new BenchPin("1", "OUT", 1, ElectricalPinType.EptOutput), new BenchPin("2", "VCC", 1, ElectricalPinType.EptPowerInput, false),
            new BenchPin("3", "IN", 1, ElectricalPinType.EptInput));
        Guid loneR = lone.Part("R", Passive("1"), Passive("2"));
        lone.Component(loneIc, "U8"); Guid loneR1 = lone.Component(loneR, "R1");
        var loneState = lone.State([]);
        var (withU9, loneU9) = lone.Create(loneState.Baseline, loneIc, "U9", BenchSheet.Root);
        RequireRefusal(Revise(loneState, _ => WithNets(withU9, new CircuitNet(Guid.NewGuid(), "OUT", [new(loneU9, "1"), new(loneR1, "1")]))).Saved,
            SchematicConnectionErrors.ConnectedImplicitPowerConflict, "U9.2");
        var single = new Bench();
        Guid singleIc = single.Part("IC", new BenchPin("1", "OUT", 1, ElectricalPinType.EptOutput), new BenchPin("2", "VCC", 1, ElectricalPinType.EptPowerInput, false));
        Guid singleR = single.Part("R", Passive("1"), Passive("2"));
        Guid template = single.Component(singleIc, "U8"); Guid singleR1 = single.Component(singleR, "R1");
        var singleNet = new CircuitNet(Guid.NewGuid(), "VCC", [new(template, "2"), new(singleR1, "2")]);
        var singleState = single.State([singleNet]);
        var (withIc, u10) = single.Create(singleState.Baseline, singleIc, "U10", BenchSheet.Root);
        var alone = Plan(Revise(singleState, _ => WithNets(withIc, singleNet, new CircuitNet(Guid.NewGuid(), "OUT", [new(u10, "1"), new(singleR1, "1")]))).Saved);
        Assert.AreEqual(SchematicConnectionErrors.ConnectedImplicitPowerConflict, alone.ErrorCode,
            "U10.2 is named VCC while net VCC exists: it must be placed in that net. " + alone.ErrorMessage);
        // False-positive guard: the same hidden pin placed in VCC is planned, VCC stays global and OUT stays local.
        var placedInVcc = Plan(Revise(singleState, _ => WithNets(withIc, singleNet with { Pins = [.. singleNet.Pins, new(u10, "2")] },
            new CircuitNet(Guid.NewGuid(), "OUT", [new(u10, "1"), new(singleR1, "1")]))).Saved);
        var placed = RequireRealizationPlan(placedInVcc);
        Assert.AreEqual("VCC", placed.Nets.Single(n => n.Name == "VCC").GlobalName);
        Assert.AreEqual(ConnectionScope.Local, placed.Nets.Single(n => n.Name == "OUT").Scope);
    }

    [TestMethod]
    public void EveryPlanningRefusalNamesItsCodeAndChangesNothing()
    {
        foreach (var (problem, saved, code, detail) in RefusalCases())
            RequireRefusal(saved, code, detail, problem);
    }

    [TestMethod]
    public void StatesNoSavedDesignCanReachAreStillRefusedByTheBuilder()
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2");
        var state = bench.State([]);
        var net = new CircuitNet(Guid.NewGuid(), "N", [new(r1, "1"), new(r2, "1")]);
        var candidate = WithNets(state.Baseline, net);
        var shape = new SchematicConnectedAdditionClassification(SchematicConnectedAdditionKind.Admitted, [], [net.Id]);
        Assert.IsNotNull(SchematicConnectionIntentBuilder.Build(state, candidate, shape), "The unmodified case is valid.");

        // More nets than one revision may change (§5.10).
        var many = shape with { ChangedNetIds = [.. Enumerable.Range(0, SchematicConnectionIntentBuilder.MaxChangedNets + 1).Select(_ => Guid.NewGuid())] };
        RequireBuildError(state, candidate, many, SchematicConnectionErrors.ConnectedScopeTooLarge);
        // A classification that disagrees with the revision's own delta.
        RequireBuildError(state, candidate, shape with { ChangedNetIds = [Guid.NewGuid()] }, SchematicConnectionErrors.ConnectedInternalInconsistency);
        // A native symbol without its own pin identities.
        var shared = candidate with { Schematic = candidate.Schematic.Clone() };
        bench.EditSymbol(shared.Schematic, r1, s => s.SeparatePinIdentities = false);
        RequireBuildError(state, shared, shape, SchematicConnectionErrors.ConnectedPinIdentityMissing);
        // Two top-level sheets share no parent sheet, so no hierarchical label reaches both (§5.4 step 1).
        var (twoRoots, other) = bench.WithSecondTopLevelSheet(candidate, r);
        var crossing = new CircuitNet(Guid.NewGuid(), "SPLIT", [new(r1, "2"), new(other, "1")]);
        RequireBuildError(state, WithNets(twoRoots, net, crossing),
            shape with { ChangedNetIds = new[] { net.Id, crossing.Id }.Order().ToArray() }, SchematicConnectionErrors.ConnectedHierarchyUnsupported);
        Assert.ThrowsExactly<ArgumentException>(() => SchematicConnectionIntentBuilder.Build(state, candidate,
            shape with { Kind = SchematicConnectedAdditionKind.NotApplicable }));
    }

    [TestMethod]
    public void StubCountIsLimitedPerRevision()
    {
        // §5.10: at most 4096 placements may need a generated stub. One part with one pin more than that, all in
        // one new net, is refused; the same net one pin short is planned.
        foreach (int count in new[] { SchematicConnectionIntentBuilder.MaxStubPlacements, SchematicConnectionIntentBuilder.MaxStubPlacements + 1 })
        {
            var bench = new Bench();
            Guid big = bench.Part("BIG", [.. Enumerable.Range(1, count).Select(n => Passive(n.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);
            Guid b1 = bench.Component(big, "B1");
            var state = bench.State([]);
            var all = new CircuitNet(Guid.NewGuid(), "ALL", [.. Enumerable.Range(1, count).Select(n => new PinEndpoint(b1, n.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);
            var shape = new SchematicConnectedAdditionClassification(SchematicConnectedAdditionKind.Admitted, [], [all.Id]);
            var candidate = WithNets(state.Baseline, all);
            if (count > SchematicConnectionIntentBuilder.MaxStubPlacements)
                RequireBuildError(state, candidate, shape, SchematicConnectionErrors.ConnectedScopeTooLarge);
            else
                Assert.AreEqual(count, SchematicConnectionIntentBuilder.Build(state, candidate, shape).Screens.Single().Islands.Single().Members.Count(m => m.RequiresStub));
        }
    }

    [TestMethod]
    public void LabelTextFollowsTheContractCharacterRules()
    {
        foreach (string valid in new[] { "SIG", "+3V3", "GND", "a", new string('x', 128), "D-", "Ω_OUT", "😀" })
            Assert.IsTrue(SchematicConnectionIntentBuilder.ValidLabelText(valid), valid);
        foreach (string invalid in new[] { "", "#PWR", "A B", "A\tB", "A{B", "A}B", "A[B", "A]B", "A/B", "A\\B", "A$B", "A~B", "A^B", "A,B", "A\"B",
            new string('x', 129), "A\u0001", "\ud800" })
            Assert.IsFalse(SchematicConnectionIntentBuilder.ValidLabelText(invalid), invalid);
        Assert.AreEqual("OUT", SchematicConnectionIntentBuilder.LocalName("/A/B/OUT"));
        Assert.AreEqual("OUT", SchematicConnectionIntentBuilder.LocalName("OUT"));
        Assert.AreEqual("", SchematicConnectionIntentBuilder.LocalName("/A/"));
    }

    private static IEnumerable<(string Problem, DesignRecoveryState Saved, string Code, string Detail)> RefusalCases()
    {
        {
            // A net pin on a unit that is not drawn (§5.1).
            var bench = new Bench();
            Guid dual = bench.Part("DUAL", new BenchPin("1", "A", 1), new BenchPin("2", "B", 2)), r = bench.Part("R", Passive("1"), Passive("2"));
            Guid u1 = bench.Component(dual, "U1", units: [1]), r1 = bench.Component(r, "R1");
            var state = bench.State([]);
            yield return ("undrawn unit", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "N", [new(u1, "1"), new(u1, "2"), new(r1, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedPinUnresolved, "U1.2");
        }
        {
            // Two placed pins share the number (§5.1).
            var bench = new Bench();
            Guid t = bench.Part("T3", Passive("1"), Passive("2"), Passive("3")), r = bench.Part("R", Passive("1"), Passive("2"));
            Guid a1 = bench.Component(t, "A1", duplicatePin: "1"), r1 = bench.Component(r, "R1");
            yield return ("stacked duplicate pin number", Revise(bench.State([]), d => WithNets(d, new CircuitNet(Guid.NewGuid(), "N", [new(a1, "1"), new(r1, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedPinAmbiguous, "A1");
        }
        {
            // An added pin already driven by a label (§5.2).
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2");
            string wire = bench.Wire(BenchSheet.Root), label = bench.LocalLabel(BenchSheet.Root, "X");
            var state = bench.State([], extra: [[(BenchSheet.Root, bench.PinId(r1, "1").ToString("D")), (BenchSheet.Root, wire), (BenchSheet.Root, label)]]);
            yield return ("driven pin", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "N", [new(r1, "1"), new(r2, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedPinDriverConflict, "label 'X'");
        }
        {
            // A symbol variant that swaps the library symbol (§5.7).
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid r1 = bench.Component(r, "R1", edit: s => s.Variants.Variants.Add(new SchematicSymbolVariant
                { Name = "Alt", SymbolOverride = new() { LibraryNickname = "Bench", EntryName = "Other" } }));
            Guid r2 = bench.Component(r, "R2");
            yield return ("variant symbol override", Revise(bench.State([]), d => WithNets(d, new CircuitNet(Guid.NewGuid(), "N", [new(r1, "1"), new(r2, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedVariantSymbolUnsupported, "'Alt'");
        }
        {
            // An existing connection through a bus entry (§5.4 step 6).
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), r3 = bench.Component(r, "R3");
            var n = new CircuitNet(Guid.NewGuid(), "N", [new(r1, "1"), new(r2, "1")]);
            var state = bench.State([n], new() { [n.Id] = [(BenchSheet.Root, bench.BusEntry(BenchSheet.Root))] });
            yield return ("bus entry", Revise(state, d => WithNets(d, n with { Pins = [.. n.Pins, new(r3, "1")] })).Saved,
                SchematicConnectionErrors.ConnectedBusRealizationUnsupported, "bus");
        }
        foreach (var power in PowerRefusals()) yield return power;
        {
            // A local label with the new net's text belongs to another connection (§5.5).
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), r3 = bench.Component(r, "R3");
            string wire = bench.Wire(BenchSheet.Root), label = bench.LocalLabel(BenchSheet.Root, "SIG");
            var state = bench.State([], extra: [[(BenchSheet.Root, bench.PinId(r3, "1").ToString("D")), (BenchSheet.Root, wire), (BenchSheet.Root, label)]]);
            yield return ("label of another connection", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "SIG", [new(r1, "1"), new(r2, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedNetNameConflict, "'SIG'");
            yield return ("two new nets with one text on one sheet", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "/A/X", [new(r1, "1"), new(r2, "1")]),
                new CircuitNet(Guid.NewGuid(), "/B/X", [new(r1, "2"), new(r2, "2")]))).Saved, SchematicConnectionErrors.ConnectedNetNameConflict, "'X'");
            yield return ("space in the name", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "MY NET", [new(r1, "1"), new(r2, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedLabelTextInvalid, "'MY NET'");
            yield return ("generated name", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "Net-(R1-Pad1)", [new(r1, "1"), new(r2, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedLabelTextInvalid, "explicit name");
            yield return ("reconciliation name", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "NET-0123456789ab", [new(r1, "1"), new(r2, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedLabelTextInvalid, "explicit name");
            yield return ("empty local name", Revise(state, d => WithNets(d, new CircuitNet(Guid.NewGuid(), "/A/", [new(r1, "1"), new(r2, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedLabelTextInvalid, "''");
        }
        {
            // A sheet pin with the port text already belongs to another connection (§5.4 step 2).
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid r1 = bench.Component(r, "R1"), r5 = bench.Component(r, "R5", BenchSheet.Child);
            bench.SheetPin(BenchSheet.Child, "DATA");
            yield return ("sheet pin of another connection", Revise(bench.State([]), d => WithNets(d, new CircuitNet(Guid.NewGuid(), "DATA", [new(r1, "1"), new(r5, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedNetNameConflict, "sheet pin 'DATA'");
            var locked = new Bench();
            Guid lr = locked.Part("R", Passive("1"), Passive("2"));
            Guid l1 = locked.Component(lr, "R1"), l5 = locked.Component(lr, "R5", BenchSheet.Child);
            locked.LockSheetSymbol(BenchSheet.Child);
            yield return ("locked sheet symbol", Revise(locked.State([]), d => WithNets(d, new CircuitNet(Guid.NewGuid(), "DATA", [new(l1, "1"), new(l5, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedLockedSheetSymbol, "locked");
        }
        {
            // Two local power symbols with different values on one island (§5.3).
            var bench = new Bench();
            Guid local = bench.Part("LOCAL", SchematicSymbolType.SstLocalPower, new BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            bench.Component(local, "#LP0", value: "+5V"); Guid r1 = bench.Component(r, "R1");
            var state = bench.State([]);
            var (withA, a) = bench.Create(state.Baseline, local, "#LP1", BenchSheet.Root, "+5V");
            var (withB, b) = bench.Create(withA, local, "#LP2", BenchSheet.Root, "+3V3");
            yield return ("local power names disagree", Revise(state, _ => WithNets(withB, new CircuitNet(Guid.NewGuid(), "N", [new(a, "1"), new(b, "1"), new(r1, "1")]))).Saved,
                SchematicConnectionErrors.ConnectedNetNameConflict, "'+3V3', '+5V'");
        }
        {
            // A crossing whose intermediate sheet has nothing to carry the new hierarchical label (§5.4).
            // N already reaches the grandchild through sheet pins and hierarchical labels "N", but the middle sheet
            // also names it "A_ALIAS", which is its label text, so the root would need a second crossing there.
            var bench = new Bench();
            Guid r = bench.Part("R", Passive("1"), Passive("2"));
            Guid r1 = bench.Component(r, "R1"), g1 = bench.Component(r, "G1", BenchSheet.Grand), g2 = bench.Component(r, "G2", BenchSheet.Grand);
            var items = new[]
            {
                (BenchSheet.Root, bench.Wire(BenchSheet.Root)), (BenchSheet.Root, bench.SheetPin(BenchSheet.Child, "N")),
                (BenchSheet.Child, bench.SheetPin(BenchSheet.Grand, "N")), (BenchSheet.Child, bench.Wire(BenchSheet.Child)),
                (BenchSheet.Child, bench.HierarchicalLabel(BenchSheet.Child, "N")), (BenchSheet.Child, bench.LocalLabel(BenchSheet.Child, "A_ALIAS")),
                (BenchSheet.Grand, bench.HierarchicalLabel(BenchSheet.Grand, "N")), (BenchSheet.Grand, bench.Wire(BenchSheet.Grand))
            };
            var n = new CircuitNet(Guid.NewGuid(), "N", [new(r1, "1"), new(g1, "1")]);
            var state = bench.State([n], new() { [n.Id] = items });
            yield return ("crossing with nothing to attach", Revise(state, d => WithNets(d, n with { Pins = [.. n.Pins, new(g2, "1")] })).Saved,
                SchematicConnectionErrors.ConnectedHierarchyUnsupported, "'Child'");
        }
    }

    private static IEnumerable<(string Problem, DesignRecoveryState Saved, string Code, string Detail)> PowerRefusals()
    {
        var bench = new Bench();
        Guid r = bench.Part("R", Passive("1"), Passive("2"));
        Guid power = bench.Part("PWR", SchematicSymbolType.SstGlobalPower, new BenchPin("1", "~", 1, ElectricalPinType.EptPowerInput, false));
        Guid ic = bench.Part("IC", new BenchPin("1", "OUT", 1, ElectricalPinType.EptOutput), new BenchPin("2", "VCC", 1, ElectricalPinType.EptPowerInput, false));
        Guid pwr1 = bench.Component(power, "#PWR1", value: "GND");
        Guid r1 = bench.Component(r, "R1"), r2 = bench.Component(r, "R2"), r3 = bench.Component(r, "R3");
        Guid u8 = bench.Component(ic, "U8");
        var gnd = new CircuitNet(Guid.NewGuid(), "GND", [new(pwr1, "1"), new(r1, "1")]);
        var vcc = new CircuitNet(Guid.NewGuid(), "VCC", [new(u8, "2"), new(r1, "2")]);
        var state = bench.State([gnd, vcc]);
        var (withVcc, other) = bench.Create(state.Baseline, power, "#PWR2", BenchSheet.Root, "VCC");
        yield return ("two global names in one net", Revise(state, _ => WithNets(withVcc, gnd with { Pins = [.. gnd.Pins, new(other, "1")] }, vcc)).Saved,
            SchematicConnectionErrors.ConnectedGlobalNameConflict, "'VCC'");
        var (withGnd, second) = bench.Create(state.Baseline, power, "#PWR3", BenchSheet.Root, "GND");
        yield return ("a second GND net", Revise(state, _ => WithNets(withGnd, gnd, vcc, new CircuitNet(Guid.NewGuid(), "GND2", [new(second, "1"), new(r2, "1")]))).Saved,
            SchematicConnectionErrors.ConnectedGlobalNameConflict, "already carries it");
        yield return ("an unconnected GND symbol", Revise(state, _ => WithNets(withGnd, gnd, vcc, new CircuitNet(Guid.NewGuid(), "N", [new(r2, "1"), new(r3, "1")]))).Saved,
            SchematicConnectionErrors.ConnectedGlobalNameConflict, "#PWR3.1");
        var (withA, a) = bench.Create(state.Baseline, power, "#PWR4", BenchSheet.Root, "V5");
        var (withB, b) = bench.Create(withA, power, "#PWR5", BenchSheet.Root, "V5");
        yield return ("two nets with one global name", Revise(state, _ => WithNets(withB, gnd, vcc, new CircuitNet(Guid.NewGuid(), "NA", [new(a, "1"), new(r2, "1")]),
            new CircuitNet(Guid.NewGuid(), "NB", [new(b, "1"), new(r3, "1")]))).Saved, SchematicConnectionErrors.ConnectedGlobalNameConflict, "both carry");
        var (withVariable, variable) = bench.Create(state.Baseline, power, "#PWR6", BenchSheet.Root, "${SUPPLY}");
        yield return ("power name with a text variable", Revise(state, _ => WithNets(withVariable, gnd, vcc, new CircuitNet(Guid.NewGuid(), "S", [new(variable, "1"), new(r2, "1")]))).Saved,
            SchematicConnectionErrors.ConnectedPowerNameUnresolved, "${SUPPLY}");
        var (withIc, u9) = bench.Create(state.Baseline, ic, "U9", BenchSheet.Root);
        yield return ("hidden VCC pin in GND", Revise(state, _ => WithNets(withIc, gnd with { Pins = [.. gnd.Pins, new(u9, "2")] }, vcc)).Saved,
            SchematicConnectionErrors.ConnectedImplicitPowerConflict, "'GND'");
    }

    // ---- shared helpers ----

    internal static BenchPin Passive(string number) => new(number, "~");

    internal static (DesignRecoveryState Saved, SchematicDesign Desired) Revise(DesignRecoveryState state, Func<SchematicDesign, SchematicDesign> edit)
    {
        var design = edit(state.Baseline);
        var saved = state with { DesiredFileBytes = Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, state.KnowledgeLibraries)) };
        return (saved, DesignRecoveryStore.ReadDesired(saved));
    }

    internal static SchematicDesign WithNets(SchematicDesign design, params CircuitNet[] nets) =>
        design with { Engineering = design.Engineering with { Circuit = design.Engineering.Circuit with { Nets = nets } } };

    internal static AutomationSession Realizing(DesignRecoveryState state)
    {
        var session = new AutomationSession { InstanceId = state.InstanceId.ToString("D") };
        session.Capabilities.Add("session.info");
        session.Capabilities.Add(SchematicConnectedAddition.NativeCapability);
        return session;
    }

    internal static SchematicSynchronizationPlan Plan(DesignRecoveryState saved) => SchematicSynchronizationPlanner.Plan(saved, Realizing(saved));

    /// <summary>The §4.2 invariant of a realization plan, returning its intent.</summary>
    internal static SchematicConnectionIntent RequireRealizationPlan(SchematicSynchronizationPlan plan)
    {
        Assert.IsNull(plan.ErrorCode, plan.ErrorCode + ": " + plan.ErrorMessage);
        Assert.IsTrue(plan.CanPrepare);
        Assert.IsNotNull(plan.Candidate);
        Assert.IsNull(plan.CandidateXml, "A realization plan has no publishable preview.");
        Assert.IsEmpty(plan.NativeOperations);
        Assert.IsTrue(plan.NativeConnectivityValidationRequired);
        Assert.IsTrue(plan.NativeLayoutResolutionRequired);
        Assert.IsTrue(plan.NativeConnectionRealizationRequired);
        Assert.IsFalse(plan.NativeRebuildRequired);
        return plan.Connections!;
    }

    internal static void RequireRefusal(DesignRecoveryState saved, string code, string detail, string? problem = null)
    {
        var plan = Plan(saved);
        problem ??= code;
        Assert.AreEqual(code, plan.ErrorCode, problem + ": " + plan.ErrorMessage);
        StringAssert.Contains(plan.ErrorMessage!, detail, problem);
        Assert.IsNull(plan.Candidate, problem); Assert.IsNull(plan.CandidateXml, problem);
        Assert.IsEmpty(plan.NativeOperations, problem); Assert.IsNull(plan.Connections, problem);
    }

    private static void RequireBuildError(DesignRecoveryState state, SchematicDesign candidate, SchematicConnectedAdditionClassification shape, string code)
    {
        var error = Assert.ThrowsExactly<AutomationException>(() => SchematicConnectionIntentBuilder.Build(state, candidate, shape));
        Assert.AreEqual(code, error.Code, error.Message);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
    }

    /// <summary>The placed-pin keys of <paramref name="pins"/> in <paramref name="design"/>, resolved independently of
    /// the builder: every occurrence of the pin's unit (or every occurrence for a common pin), through its binding.</summary>
    internal static ConnectionPinKey[] Keys(SchematicDesign design, params (Guid Component, string Number)[] pins)
    {
        var circuit = design.Engineering.Circuit;
        var result = new List<ConnectionPinKey>();
        foreach (var (componentId, number) in pins)
        {
            var component = circuit.Components.Single(c => c.Id == componentId);
            var part = circuit.Parts.Single(p => p.Id == circuit.Sheets.SelectMany(s => s.Components).Single(d => d.Id == component.DefinitionId).PartId);
            int unit = part.Pins.Single(p => p.Number == number).Unit;
            foreach (var occurrence in circuit.Symbols.Where(s => s.ComponentId == componentId && (unit == 0 || s.Unit == unit)))
            {
                string path = SchematicDesignBindings.PathKey(design.SheetBindings.Single(b => b.SheetInstanceId == occurrence.EffectiveSheetInstanceId(component)).NativePath);
                string native = design.SymbolBindings.Single(b => b.SymbolOccurrenceId == occurrence.Id).NativeObjectId.ToString("D");
                var symbol = design.Schematic.Instances.Single(s => string.Join('/', s.Metadata.Document.SheetPath.Path.Select(p => p.Value)) == path)
                    .Items.Where(i => i.Is(SchematicSymbolInstance.Descriptor)).Select(i => i.Unpack<SchematicSymbolInstance>()).Single(s => s.Id.Value == native);
                int style = symbol.BodyStyle?.Style ?? 1;
                var pin = symbol.Definition.Items.Where(c => c.Item.Is(SchematicPin.Descriptor) && (c.Unit is null || c.Unit.Unit == 0 || c.Unit.Unit == occurrence.Unit)
                        && (c.BodyStyle is null || c.BodyStyle.Style == 0 || c.BodyStyle.Style == style))
                    .Select(c => c.Item.Unpack<SchematicPin>()).Single(p => p.Number == number && p.LibraryPinId is not null);
                result.Add(new(path, Guid.Parse(pin.Id.Value)));
            }
        }
        return [.. result];
    }

    /// <summary>The expected groups are exactly <paramref name="groups"/>, each ordered by (path ordinal, placed pin)
    /// and the list ordered by first key.</summary>
    internal static void RequireGroups(SchematicConnectionIntent intent, IReadOnlyList<ConnectionPinKey[]> groups)
    {
        static ConnectionPinKey[] Sorted(IEnumerable<ConnectionPinKey> keys) =>
            [.. keys.OrderBy(k => k.SheetPathKey, StringComparer.Ordinal).ThenBy(k => k.PlacedPinId)];
        var expected = groups.Select(Sorted).OrderBy(g => g[0].SheetPathKey, StringComparer.Ordinal).ThenBy(g => g[0].PlacedPinId).ToArray();
        Assert.HasCount(expected.Length, intent.ExpectedGroups);
        for (int i = 0; i < expected.Length; i++)
            CollectionAssert.AreEqual(expected[i], intent.ExpectedGroups[i].ToArray(), "group " + i);
    }

    private static PinEndpoint[] Ordered(params PinEndpoint[] pins) => [.. pins.OrderBy(p => p.ComponentId).ThenBy(p => p.Pin, StringComparer.Ordinal)];

    private static ConnectionIsland Island(SchematicConnectionIntent intent, BenchSheet sheet, Bench bench) =>
        intent.Screens.Single(s => s.ScreenId == bench.ScreenId(sheet)).Islands.Single();

    private static Guid NativeSymbol(SchematicDesign design, Guid component) =>
        design.SymbolBindings.Single(b => b.SymbolOccurrenceId == design.Engineering.Circuit.Symbols.Single(s => s.ComponentId == component).Id).NativeObjectId;

    /// <summary>The bench's sheets: the root, its child and the child's grandchild; <c>Grand2</c> is a second
    /// grandchild under the same child sheet, present only on a bench built with <c>secondGrand</c>.</summary>
    internal enum BenchSheet { Root, Child, Grand, Grand2 }

    internal sealed record BenchPin(string Number, string Name, int Unit = 1, ElectricalPinType Type = ElectricalPinType.EptPassive, bool Visible = true);

    /// <summary>A small native-backed design: a root sheet, one child and one grandchild (optionally a second grandchild,
    /// drawn by a second sheet symbol on the child sheet), parts with explicit pin types and visibility, components drawn as separate-identity symbols, and a native pin partition built from
    /// the model nets plus explicitly listed labels, wires and sheet pins. Every state it returns is aligned and
    /// stable, so the planner's guards pass and only the connection intent decides the outcome.</summary>
    internal sealed class Bench
    {
        private readonly Guid[] instances = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        private readonly Guid[] definitions = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        private readonly KIID[] screens = [NewId(), NewId(), NewId(), NewId()];
        private readonly KIID rootId = NewId(), childSymbol = NewId(), grandSymbol = NewId(), grand2Symbol = NewId();
        private readonly bool secondGrand;
        private readonly SchematicHierarchyData data;
        private readonly List<PartDefinition> parts = [];
        private readonly Dictionary<Guid, (string Name, SchematicSymbolType Type, BenchPin[] Pins, Dictionary<string, string> LibraryPins)> info = [];
        private readonly List<ComponentDefinition>[] sheetComponents = [[], [], [], []];
        private readonly List<ComponentInstance> components = [];
        private readonly List<SymbolOccurrence> occurrences = [];
        private readonly List<SchematicSymbolBinding> bindings = [];
        private readonly Dictionary<(Guid Component, string Number), List<(BenchSheet Sheet, string Id)>> pins = [];
        private readonly Guid circuit = Guid.NewGuid(), structure = Guid.NewGuid();
        private int column;

        public Bench(bool secondGrand = false)
        {
            this.secondGrand = secondGrand;
            var document = new DocumentSpecifier { Type = DocumentType.DoctypeSchematic, SheetPath = new(), Project = new() { Name = "bench", Path = "/bench" } };
            document.SheetPath.Path.Add(rootId.Clone());
            data = new() { Document = document.Clone() };
            var root = Screen(document, [rootId], 0);
            var child = Screen(document, [rootId, childSymbol], 1);
            Screen(document, [rootId, childSymbol, grandSymbol], 2);
            root.Items.Add(Any.Pack(SheetSymbolFor(childSymbol, 1, root, "Child")));
            child.Items.Add(Any.Pack(SheetSymbolFor(grandSymbol, 2, child, "Grand")));
            if (!secondGrand) return;
            // The second grandchild's sheet symbol sits 10 mm below the first on the child sheet.
            Screen(document, [rootId, childSymbol, grand2Symbol], 3);
            child.Items.Add(Any.Pack(SheetSymbolFor(grand2Symbol, 3, child, "Grand2", 100_000_000)));
        }

        public Guid ChildSheetSymbol => Guid.Parse(childSymbol.Value);
        /// <summary>The sheet symbol, in its parent sheet, that draws <paramref name="sheet"/>.</summary>
        public Guid SheetSymbolOf(BenchSheet sheet) => Guid.Parse(SymbolOf(sheet).Value);
        public Guid Instance(BenchSheet sheet) => instances[(int)sheet];
        public Guid ScreenId(BenchSheet sheet) => Guid.Parse(screens[(int)sheet].Value);
        public string Path(BenchSheet sheet) => string.Join('/', data.Instances[(int)sheet].Metadata.Document.SheetPath.Path.Select(p => p.Value));
        public Guid PinId(Guid component, string number) => Guid.Parse(pins[(component, number)].Single().Id);

        public Guid Part(string name, params BenchPin[] pinList) => Part(name, SchematicSymbolType.SstNormal, pinList);

        public Guid Part(string name, SchematicSymbolType type, params BenchPin[] pinList)
        {
            Guid id = Guid.NewGuid();
            parts.Add(new(id, name, Math.Max(1, pinList.Max(p => p.Unit)), [.. pinList.Select(p => new PartPin(p.Number, p.Name, p.Unit))]));
            info.Add(id, (name, type, pinList, pinList.ToDictionary(p => p.Number, _ => Guid.NewGuid().ToString("D"))));
            return id;
        }

        public Guid Component(Guid part, string reference, BenchSheet sheet = BenchSheet.Root, string? value = null, int[]? units = null,
            string? duplicatePin = null, Action<SchematicSymbolInstance>? edit = null, Guid? id = null)
        {
            var (name, _, _, _) = info[part];
            var definition = new ComponentDefinition(Guid.NewGuid(), part, value ?? name);
            sheetComponents[(int)sheet].Add(definition);
            var component = new ComponentInstance(id ?? Guid.NewGuid(), definition.Id, instances[(int)sheet], reference);
            components.Add(component);
            foreach (int unit in units ?? Enumerable.Range(1, parts.Single(p => p.Id == part).Units).ToArray())
            {
                var occurrence = new SymbolOccurrence(Guid.NewGuid(), component.Id, unit, new SymbolPlacement(20 + 20 * column, 20, 0, false, false, false));
                var symbol = Symbol(part, reference, definition.Value, unit, sheet, 20 + 20 * column++, duplicatePin);
                edit?.Invoke(symbol);
                data.Instances[(int)sheet].Items.Add(Any.Pack(symbol));
                occurrences.Add(occurrence);
                bindings.Add(new(occurrence.Id, Guid.Parse(symbol.Id.Value)));
                foreach (var child in symbol.Definition.Items.Where(c => c.Unit.Unit == 0 || c.Unit.Unit == unit))
                {
                    var pin = child.Item.Unpack<SchematicPin>();
                    if (!pins.TryGetValue((component.Id, pin.Number), out var list)) pins.Add((component.Id, pin.Number), list = []);
                    list.Add((sheet, pin.Id.Value));
                }
            }
            return component.Id;
        }

        /// <summary>Add a coordinate-bearing component of <paramref name="part"/> to <paramref name="design"/> only,
        /// as an XML revision would; creation copies an existing symbol of the same part as its template.</summary>
        public (SchematicDesign Design, Guid Component) Create(SchematicDesign design, Guid part, string reference, BenchSheet sheet, string? value = null)
        {
            var model = design.Engineering.Circuit;
            var definition = new ComponentDefinition(Guid.NewGuid(), part, value ?? info[part].Name);
            var component = new ComponentInstance(Guid.NewGuid(), definition.Id, instances[(int)sheet], reference);
            int x = 100 + 20 * column++;
            var added = Enumerable.Range(1, parts.Single(p => p.Id == part).Units)
                .Select(unit => new SymbolOccurrence(Guid.NewGuid(), component.Id, unit, new SymbolPlacement(x, 40 + 20 * unit, 0, false, false, false))).ToArray();
            return (design with { Engineering = design.Engineering with { Circuit = model with
            {
                Sheets = model.Sheets.Select(s => s.Id == definitions[(int)sheet] ? s with { Components = [.. s.Components, definition] } : s).ToArray(),
                Components = [.. model.Components, component], Symbols = [.. model.Symbols, .. added]
            } } }, component.Id);
        }

        public string Wire(BenchSheet sheet) => Add(sheet, id => new SchematicLine { Id = id, Start = Point(), End = Point(), Type = SchematicLineType.SltWire, Locked = LockedState.LsUnlocked });
        public string BusEntry(BenchSheet sheet) => Add(sheet, id => new BusEntry { Id = id, Position = Point(), Size = Point(), Type = BusEntryType.BetWireToBus, Locked = LockedState.LsUnlocked });
        public string LocalLabel(BenchSheet sheet, string text) => Add(sheet, id => new LocalLabel { Id = id, Position = Point(), Text = Caption(text), SpinStyle = SchematicLabelSpinStyle.SlssRight, Locked = LockedState.LsUnlocked });
        public string GlobalLabel(BenchSheet sheet, string text) => Add(sheet, id => new GlobalLabel { Id = id, Position = Point(), Text = Caption(text),
            SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked });
        public string HierarchicalLabel(BenchSheet sheet, string text) => Add(sheet, id => new HierarchicalLabel { Id = id, Position = Point(), Text = Caption(text),
            SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked });

        /// <summary>A sheet pin on the sheet symbol of <paramref name="sheet"/>, which sits in its parent's screen.</summary>
        public string SheetPin(BenchSheet sheet, string text)
        {
            var id = NewId();
            EditSheetSymbol(sheet, symbol => symbol.Pins.Add(new SheetPin { Id = id, Position = Point(), Text = Caption(text), Side = SheetSide.ShsLeft,
                SpinStyle = SchematicLabelSpinStyle.SlssRight, Shape = SchematicLabelShape.SlshPassive, Locked = LockedState.LsUnlocked }));
            return id.Value;
        }

        public void LockSheetSymbol(BenchSheet sheet) => EditSheetSymbol(sheet, symbol => symbol.Locked = LockedState.LsLocked);

        public void EditSymbol(SchematicHierarchyData schematic, Guid component, Action<SchematicSymbolInstance> edit)
        {
            var natives = bindings.Where(b => occurrences.Any(o => o.Id == b.SymbolOccurrenceId && o.ComponentId == component))
                .Select(b => b.NativeObjectId.ToString("D")).ToHashSet(StringComparer.Ordinal);
            foreach (var screen in schematic.Instances)
                for (int i = 0; i < screen.Items.Count; i++)
                {
                    if (!screen.Items[i].Is(SchematicSymbolInstance.Descriptor)) continue;
                    var symbol = screen.Items[i].Unpack<SchematicSymbolInstance>();
                    if (!natives.Contains(symbol.Id.Value)) continue;
                    edit(symbol); screen.Items[i] = Any.Pack(symbol);
                }
        }

        /// <summary><paramref name="design"/> with an extra top-level sheet instance holding one new component of
        /// <paramref name="part"/>, which no saved design can express but the builder must still refuse cleanly.</summary>
        public (SchematicDesign Design, Guid Component) WithSecondTopLevelSheet(SchematicDesign design, Guid part)
        {
            var model = design.Engineering.Circuit;
            Guid definitionId = Guid.NewGuid(), instanceId = Guid.NewGuid();
            var root = NewId();
            var document = data.Document.Clone(); document.SheetPath.Path.Clear(); document.SheetPath.Path.Add(root.Clone());
            var schematic = design.Schematic.Clone();
            var screen = new SchematicScreenData { Metadata = new() { Document = document, ScreenId = NewId() } };
            schematic.Instances.Add(screen);
            var definition = new ComponentDefinition(Guid.NewGuid(), part, info[part].Name);
            var component = new ComponentInstance(Guid.NewGuid(), definition.Id, instanceId, "Q1");
            var occurrence = new SymbolOccurrence(Guid.NewGuid(), component.Id, 1, new SymbolPlacement(20, 20, 0, false, false, false));
            var symbol = Symbol(part, "Q1", definition.Value, 1, BenchSheet.Root, 20, null);
            symbol.Path = document.SheetPath.Clone();
            screen.Items.Add(Any.Pack(symbol));
            return (design with
            {
                Engineering = design.Engineering with { Circuit = model with
                {
                    Sheets = [.. model.Sheets, new(definitionId, "Other", [definition])],
                    SheetInstances = [.. model.SheetInstances, new(instanceId, definitionId, null)],
                    Components = [.. model.Components, component], Symbols = [.. model.Symbols, occurrence]
                } },
                Schematic = schematic,
                SheetBindings = [.. design.SheetBindings, new(instanceId, [Guid.Parse(root.Value)])],
                SymbolBindings = [.. design.SymbolBindings, new(occurrence.Id, Guid.Parse(symbol.Id.Value))]
            }, component.Id);
        }

        public SchematicDesign Design(IReadOnlyList<CircuitNet> nets)
        {
            SheetDefinition[] sheets = [new(definitions[0], "Root", [.. sheetComponents[0]]), new(definitions[1], "Child", [.. sheetComponents[1]]),
                new(definitions[2], "Grand", [.. sheetComponents[2]]), .. secondGrand ? [new SheetDefinition(definitions[3], "Grand2", [.. sheetComponents[3]])] : Array.Empty<SheetDefinition>()];
            KiCad.Automation.Model.SheetInstance[] sheetInstances = [new(instances[0], definitions[0], null), new(instances[1], definitions[1], instances[0]),
                new(instances[2], definitions[2], instances[1]), .. secondGrand ? [new KiCad.Automation.Model.SheetInstance(instances[3], definitions[3], instances[1])] : Array.Empty<KiCad.Automation.Model.SheetInstance>()];
            var model = new Circuit(circuit, [.. parts], sheets, sheetInstances, [.. components], nets, [.. occurrences]);
            return new(new EngineeringDesign(model, new StructuralDiagram(structure, [], [], [], []), [], []), data.Clone(),
                instances.Take(data.Instances.Count).Select((instance, index) => new SchematicSheetBinding(instance,
                    data.Instances[index].Metadata.Document.SheetPath.Path.Select(p => Guid.Parse(p.Value)).ToArray())).ToArray(),
                [.. bindings]);
        }

        /// <summary>A stable recovery record over this bench: the native partition joins each model net's placed pins
        /// with its listed <paramref name="decorations"/>, and each <paramref name="extra"/> entry is one more native net.</summary>
        public DesignRecoveryState State(IReadOnlyList<CircuitNet> nets, Dictionary<Guid, (BenchSheet Sheet, string Id)[]>? decorations = null,
            (BenchSheet Sheet, string Id)[][]? extra = null)
        {
            var design = Design(nets);
            var electrical = new SchematicElectricalState { Hierarchy = new() { Data = data.Clone(), Revision = new() { Epoch = "bench", Sequence = 7 }, TrackingComplete = false } };
            foreach (var net in nets)
                electrical.Nets.Add(Native("/" + net.Name, net.Pins.SelectMany(p => pins[(p.ComponentId, p.Pin)])
                    .Concat(decorations?.GetValueOrDefault(net.Id) ?? [])));
            foreach (var items in extra ?? []) electrical.Nets.Add(Native("extra", items));
            return new(Guid.NewGuid(), Guid.NewGuid(), new("bench", 7), false, design,
                Encoding.UTF8.GetBytes(SchematicDesignXml.Write(design, [])), data.Clone(), [],
                BaselineElectrical: electrical, ObservedElectrical: electrical.Clone());
        }

        private SchematicNet Native(string name, IEnumerable<(BenchSheet Sheet, string Id)> items)
        {
            var net = new SchematicNet { Name = name };
            foreach (var group in items.GroupBy(i => i.Sheet).OrderBy(g => Path(g.Key), StringComparer.Ordinal))
            {
                var contents = new SchematicNetSheetContents { Path = data.Instances[(int)group.Key].Metadata.Document.SheetPath.Clone() };
                contents.Items.Add(group.Select(i => new KIID { Value = i.Id }));
                net.Sheets.Add(contents);
            }
            return net;
        }

        private SchematicSymbolInstance Symbol(Guid part, string reference, string value, int unit, BenchSheet sheet, int x, string? duplicatePin)
        {
            var (name, type, pinList, libraryPins) = info[part];
            var screen = data.Instances[(int)sheet];
            var symbol = new SchematicSymbolInstance
            {
                Id = NewId(), Path = screen.Metadata.Document.SheetPath.Clone(), Unit = new() { Unit = unit },
                Position = new() { XNm = x * 1_000_000L, YNm = 20_000_000 }, Transform = new() { Orientation = SchematicSymbolOrientation.Sso0 },
                Locked = LockedState.LsUnlocked, ReferenceField = Field(reference), ValueField = Field(value), SeparatePinIdentities = true,
                Variants = new(), InstanceRecords = new(),
                Definition = new SchematicSymbol { Id = new() { LibraryNickname = "Bench", EntryName = name },
                    UnitCount = (uint)parts.Single(p => p.Id == part).Units, Type = type }
            };
            var record = new SymbolSheetRecord { ProjectName = "bench", Reference = reference, Unit = unit, Variants = new() };
            record.Path.Add(screen.Metadata.Document.SheetPath.Path.Select(p => p.Clone()));
            symbol.InstanceRecords.Records.Add(record);
            foreach (var pin in pinList)
            {
                AddPin(pin, libraryPins[pin.Number]);
                if (pin.Number == duplicatePin) AddPin(pin, Guid.NewGuid().ToString("D"));
            }
            return symbol;

            void AddPin(BenchPin pin, string library) => symbol.Definition.Items.Add(new SchematicSymbolChild
            {
                Unit = new() { Unit = pin.Unit },
                Item = Any.Pack(new SchematicPin { Id = NewId(), LibraryPinId = new() { Value = library }, Number = pin.Number, Name = pin.Name,
                    ElectricalType = pin.Type, Visible = pin.Visible })
            });
        }

        private void EditSheetSymbol(BenchSheet sheet, Action<SheetSymbol> edit)
        {
            var parent = data.Instances[sheet == BenchSheet.Grand2 ? (int)BenchSheet.Child : (int)sheet - 1];
            string id = SymbolOf(sheet).Value;
            int index = parent.Items.ToList().FindIndex(i => i.Is(SheetSymbol.Descriptor) && i.Unpack<SheetSymbol>().Id.Value == id);
            var symbol = parent.Items[index].Unpack<SheetSymbol>();
            edit(symbol);
            parent.Items[index] = Any.Pack(symbol);
        }

        private string Add(BenchSheet sheet, Func<KIID, IMessage> item)
        {
            var id = NewId();
            data.Instances[(int)sheet].Items.Add(Any.Pack(item(id)));
            return id.Value;
        }

        private SchematicScreenData Screen(DocumentSpecifier root, KIID[] path, int index)
        {
            var document = root.Clone(); document.SheetPath.Path.Clear(); document.SheetPath.Path.Add(path.Select(p => p.Clone()));
            var screen = new SchematicScreenData { Metadata = new() { Document = document, ScreenId = screens[index].Clone() } };
            data.Instances.Add(screen);
            return screen;
        }

        private KIID SymbolOf(BenchSheet sheet) => sheet switch
        {
            BenchSheet.Child => childSymbol, BenchSheet.Grand => grandSymbol,
            BenchSheet.Grand2 when secondGrand => grand2Symbol,
            _ => throw new ArgumentOutOfRangeException(nameof(sheet), sheet, "This bench has no sheet symbol for that sheet.")
        };

        private SheetSymbol SheetSymbolFor(KIID id, int child, SchematicScreenData parent, string name, long y = 50_000_000) => new()
        {
            Id = id.Clone(), ChildScreenId = screens[child].Clone(), Path = parent.Metadata.Document.SheetPath.Clone(), Locked = LockedState.LsUnlocked,
            NameField = Field(name), FilenameField = Field(name.ToLowerInvariant() + ".kicad_sch"),
            Position = new() { XNm = 150_000_000, YNm = y }, Size = new() { XNm = 40_000_000, YNm = 40_000_000 }
        };

        private Vector2 Point() => new() { XNm = 10_000_000 + 2_540_000L * column++, YNm = 80_000_000 };

        private static SchematicField Field(string text) => new() { Text = new() { Text_ = text, Attributes = new() { Multiline = true } } };
        private static Text Caption(string text) => new() { Text_ = text, Attributes = new() { Multiline = false } };
        private static KIID NewId() => new() { Value = Guid.NewGuid().ToString("D") };
    }
}
