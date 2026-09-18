using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class LayoutRefinementTests
{
    private static readonly DocumentRevision Revision = new("fixture-epoch", 3);
    private static SymbolPlacement Placement(int index) => new(index * 12.7m, 25.4m, 0, false, false, false);
    [TestMethod]
    public void AdditionOnlyRefinementProtectsExistingSymbolsEvenWithoutXmlCoordinates()
    {
        var current = CircuitXmlTests.Fixture().WithoutPlacement();
        var previous = current with { Symbols = current.Symbols.SkipLast(1).ToArray() };
        var added = current.Symbols.Last();
        var request = LayoutRefinement.ForAddedSymbols(previous, current, Revision, "Preserve existing native layout.");
        CollectionAssert.AreEqual(new[] { added.Id }, request.AffectedSymbols.ToArray());
        var result = request.ApplyCandidate(current, Revision, [new(added.Id, Placement(9))]);
        CollectionAssert.AreEqual(previous.Symbols.ToArray(), result.Symbols.SkipLast(1).ToArray());
        Assert.ThrowsExactly<AutomationException>(() => request.ApplyCandidate(current, Revision,
            [new(current.Symbols[0].Id, Placement(1)), new(added.Id, Placement(9))]));
        Assert.ThrowsExactly<AutomationException>(() => LayoutRefinement.ForAddedSymbols(current, current, Revision, ""));
        Assert.ThrowsExactly<AutomationException>(() => LayoutRefinement.ForAddedSymbols(previous, current,
            Revision with { Epoch = "" }, ""));
        Assert.ThrowsExactly<AutomationException>(() => LayoutRefinement.ForAddedSymbols(previous,
            current with { Symbols = current.Symbols.Select((s, i) => i == 0 ? s with { Placement = Placement(1) } : s).ToArray() }, Revision, ""));
    }

    [TestMethod]
    public void InitialCandidatePreservesCircuitAndRemainsSeparateFromOriginal()
    {
        var circuit = CircuitXmlTests.Fixture().WithoutPlacement();
        var request = LayoutRefinement.Request(null, circuit, Revision, "Signal flow left to right.")!;
        Assert.AreEqual(LayoutRefinementReason.InitialGeneration, request.Reason);
        Assert.AreEqual("Signal flow left to right.", request.UserInstructions);
        var result = request.ApplyCandidate(circuit, Revision,
            circuit.Symbols.Select((s, i) => new SymbolLayoutChange(s.Id, Placement(i))).ToArray());
        Assert.IsTrue(circuit.Symbols.All(s => s.Placement is null));
        Assert.IsTrue(result.Symbols.All(s => s.Placement is not null));
        Assert.AreEqual(CircuitXml.Write(circuit), CircuitXml.Write(result.WithoutPlacement()));
        Assert.IsNull(LayoutRefinement.Request(result, result, Revision, ""));
    }

    [TestMethod]
    public void AddedUnitRequestsLocalLayoutAndPreservesUnlockedManualPlacement()
    {
        var fixture = CircuitXmlTests.Fixture();
        var current = fixture with { Symbols = fixture.Symbols.Select((s, i) => s with { Placement = Placement(i) }).ToArray() };
        var previous = current with { Symbols = current.Symbols.SkipLast(1).ToArray() };
        var added = current.Symbols.Last();
        var request = LayoutRefinement.Request(previous, current, Revision, "Keep the existing groups.")!;
        Assert.AreEqual(LayoutRefinementReason.AddedSymbols, request.Reason);
        CollectionAssert.AreEqual(new[] { added.Id }, request.AffectedSymbols.ToArray());
        var result = request.ApplyCandidate(current, Revision, [new(added.Id, Placement(8))]);
        CollectionAssert.AreEqual(current.Symbols.SkipLast(1).ToArray(), result.Symbols.SkipLast(1).ToArray());
        Assert.ThrowsExactly<AutomationException>(() => request.ApplyCandidate(current, Revision,
            [new(current.Symbols[0].Id, Placement(9))]));
    }

    [TestMethod]
    public void StaleEpochRevisionOrModelRejectsTheWholeCandidate()
    {
        var circuit = CircuitXmlTests.Fixture().WithoutPlacement();
        var request = LayoutRefinement.Request(null, circuit, Revision, "")!;
        foreach (var stale in new[] { Revision with { Sequence = 4 }, Revision with { Epoch = "restarted" } })
            Assert.AreEqual("stale_revision", Assert.ThrowsExactly<AutomationException>(() =>
                request.ApplyCandidate(circuit, stale, [])).Code);
        var changed = circuit with { Symbols = circuit.Symbols.Select((s, i) => s with { Placement = Placement(i) }).ToArray() };
        Assert.AreEqual("stale_revision", Assert.ThrowsExactly<AutomationException>(() =>
            request.ApplyCandidate(changed, Revision, [])).Code);
        Assert.IsTrue(circuit.Symbols.All(s => s.Placement is null));
    }

    [TestMethod]
    public void LockedIncompleteAndDuplicateCandidatesFailWithoutPartialEdits()
    {
        var circuit = CircuitXmlTests.Fixture();
        var locked = circuit.Symbols[0];
        var request = LayoutRefinement.Request(null, circuit, Revision, "", [locked.Id])!;
        Assert.ThrowsExactly<AutomationException>(() => request.ApplyCandidate(circuit, Revision, [new(locked.Id, Placement(9))]));
        Assert.ThrowsExactly<AutomationException>(() => request.ApplyCandidate(circuit, Revision, []));
        var editable = circuit.Symbols[1];
        Assert.ThrowsExactly<AutomationException>(() => request.ApplyCandidate(circuit, Revision,
            [new(editable.Id, Placement(1)), new(editable.Id, Placement(2))]));
        Assert.ThrowsExactly<AutomationException>(() => request.ApplyCandidate(circuit, Revision,
            [new(editable.Id, Placement(1) with { Locked = true })]));
        Assert.AreEqual(locked, circuit.Symbols[0]);
        Assert.IsNull(circuit.Symbols[1].Placement);
    }

    [TestMethod]
    public void PinReassignmentRefinesOnlyAffectedComponentUnits()
    {
        var fixture = CircuitXmlTests.Fixture();
        var placed = fixture with { Symbols = fixture.Symbols.Select((s, i) => s with { Placement = Placement(i) }).ToArray() };
        Guid component = placed.Components[0].Id;
        var signal = new CircuitNet(Guid.NewGuid(), "OUTPUT", [new(component, "1")]);
        var previous = placed with { Nets = [.. placed.Nets, signal] };
        var current = previous with { Nets = [.. placed.Nets, signal with { Pins = [new(component, "7")] }] };
        var request = LayoutRefinement.Request(previous, current, Revision, "Preserve the other channel.")!;
        Assert.AreEqual(LayoutRefinementReason.ConnectivityChanged, request.Reason);
        CollectionAssert.AreEquivalent(current.Symbols.Where(s => s.ComponentId == component).Select(s => s.Id).ToArray(),
            request.AffectedSymbols.ToArray());
        var moved = current.Symbols.First(s => s.ComponentId == component);
        var result = request.ApplyCandidate(current, Revision, [new(moved.Id, Placement(9))]);
        CollectionAssert.AreEqual(current.Symbols.Where(s => s.ComponentId != component).ToArray(),
            result.Symbols.Where(s => s.ComponentId != component).ToArray());
        Assert.AreEqual(CircuitXml.Write(current.WithoutPlacement()), CircuitXml.Write(result.WithoutPlacement()));
        var removed = LayoutRefinement.Request(previous, placed, Revision, "")!;
        CollectionAssert.AreEquivalent(request.AffectedSymbols.ToArray(), removed.AffectedSymbols.ToArray());
        var added = LayoutRefinement.Request(placed, previous, Revision, "")!;
        CollectionAssert.AreEquivalent(request.AffectedSymbols.ToArray(), added.AffectedSymbols.ToArray());
    }

    [TestMethod]
    public void ReorderedNetEndpointsDoNotRequestLayout()
    {
        var fixture = CircuitXmlTests.Fixture();
        var placed = fixture with { Symbols = fixture.Symbols.Select((s, i) => s with { Placement = Placement(i) }).ToArray() };
        var reordered = placed with
        {
            Nets = placed.Nets.Reverse().Select(n => n with { Pins = n.Pins.Reverse().ToArray() }).ToArray(),
            Symbols = placed.Symbols.Reverse().ToArray()
        };
        Assert.IsNull(LayoutRefinement.Request(placed, reordered, Revision, ""));
    }

    [TestMethod]
    public void NetSplitIncludesFormerEndpointsButCannotOverridePlacementLocks()
    {
        var fixture = CircuitXmlTests.Fixture();
        var previous = fixture with
        {
            Symbols = fixture.Symbols.Select((s, i) => s with { Placement = Placement(i) with { Locked = i == 0 } }).ToArray()
        };
        var net = previous.Nets.Single();
        var current = previous with
        {
            Nets = [net with { Pins = [net.Pins[0]] }, new(Guid.NewGuid(), "SPLIT", [net.Pins[1]])]
        };
        var request = LayoutRefinement.Request(previous, current, Revision, "")!;
        CollectionAssert.AreEquivalent(current.Symbols.Select(s => s.Id).ToArray(), request.AffectedSymbols.ToArray());
        var locked = current.Symbols[0];
        Assert.AreEqual("invalid_layout", Assert.ThrowsExactly<AutomationException>(() =>
            request.ApplyCandidate(current, Revision, [new(locked.Id, Placement(8))])).Code);
        var unchanged = request.ApplyCandidate(current, Revision, []);
        Assert.AreEqual(CircuitXml.Write(current), CircuitXml.Write(unchanged));
    }
}
