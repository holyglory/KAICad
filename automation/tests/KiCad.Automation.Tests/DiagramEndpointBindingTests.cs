using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class DiagramEndpointBindingTests
{
    private static DiagramPinTarget Pin(string pin = "D11") => new(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], pin);

    [TestMethod]
    public void ExactMemoryPinAndUnresolvedCompatibleProcessorPinRemainDifferentStates()
    {
        var memory = DiagramEndpointBinding.Unknown(Guid.NewGuid(), "Memory data role").Choose(Pin("D15"));
        var processor = new DiagramEndpointBinding(DiagramEndpointKind.Compatible, Guid.NewGuid(), null,
            "Choose a compatible processor data pin during refinement.",
            new("Data", "", ["Bidirectional digital I/O"], [new("user-brief", "rev2", null, null, null)]), [], null);
        memory.Validate(); processor.Validate();
        Assert.AreEqual(DiagramEndpointKind.Pin, memory.Kind);
        Assert.AreEqual("D15", memory.Pin!.Pin);
        Assert.AreEqual(DiagramEndpointKind.Compatible, processor.Kind);
        Assert.IsNull(processor.Pin);
        Assert.AreEqual("", processor.Selector!.Protocol); // No protocol or voltage was invented.
    }

    [TestMethod]
    public void ChoosingAndUnbindingPreserveSourceIntentAndCompatibilitySelector()
    {
        var pin = Pin(); var second = pin with { Pin = "D12" };
        var endpoint = new DiagramEndpointBinding(DiagramEndpointKind.Candidates, Guid.NewGuid(), Guid.NewGuid(),
            "Route this member within the memory interface.", new("Data", "", [], []), [pin, second], null);
        var selected = endpoint.Choose(pin);
        Assert.AreSame(endpoint.Selector, selected.Selector);
        Assert.AreEqual(endpoint.Intent, selected.Intent);
        Assert.HasCount(2, selected.Candidates);
        var removed = selected.Unbind([second]);
        Assert.AreEqual(DiagramEndpointKind.Candidates, removed.Kind); Assert.IsNull(removed.Pin);
        Assert.AreSame(endpoint.Selector, removed.Selector);
        var unresolved = selected.Unbind([]);
        Assert.AreEqual(DiagramEndpointKind.Compatible, unresolved.Kind); Assert.IsNull(unresolved.Pin);
        Assert.AreEqual(endpoint.InterfaceId, unresolved.InterfaceId);
        Assert.ThrowsExactly<AutomationException>(() => endpoint.Choose(Pin("unlisted")));
    }

    [TestMethod]
    public void RepeatedSheetPathsAndSeparateBoardsArePartOfExactPinIdentity()
    {
        var pin = Pin();
        var same = pin with { SheetInstancePath = [.. pin.SheetInstancePath] };
        Assert.IsTrue(pin.SamePin(same));
        Assert.IsFalse(pin.SamePin(pin with { SheetInstancePath = [Guid.NewGuid(), Guid.NewGuid()] }));
        Assert.IsFalse(pin.SamePin(pin with { DesignId = Guid.NewGuid() }));
        var endpoint = DiagramEndpointBinding.Unknown(Guid.NewGuid()) with { Kind = DiagramEndpointKind.Candidates, Candidates = [pin, same] };
        Assert.ThrowsExactly<AutomationException>(endpoint.Validate);
        (endpoint with { Candidates = [pin, pin with { SheetInstancePath = [Guid.NewGuid()] }] }).Validate();
    }

    [TestMethod]
    public void InvalidContradictoryAndUnknownFieldStatesCannotMasqueradeAsResolvedPins()
    {
        var unknown = DiagramEndpointBinding.Unknown(Guid.NewGuid()); unknown.Validate();
        Assert.ThrowsExactly<AutomationException>((unknown with { Kind = DiagramEndpointKind.Pin }).Validate);
        Assert.ThrowsExactly<AutomationException>((unknown with { Kind = DiagramEndpointKind.Candidates }).Validate);
        Assert.ThrowsExactly<AutomationException>((unknown with { Kind = DiagramEndpointKind.Compatible }).Validate);
        Assert.ThrowsExactly<AutomationException>((unknown with { Kind = DiagramEndpointKind.Interface }).Validate);
        Assert.ThrowsExactly<AutomationException>((unknown with { Pin = Pin() }).Validate);
        Assert.ThrowsExactly<AutomationException>((unknown with { InterfaceId = Guid.Empty }).Validate);
        Assert.ThrowsExactly<AutomationException>((unknown with { Intent = "bad\0text" }).Validate);
        Assert.ThrowsExactly<AutomationException>((unknown with { Candidates = default }).Validate);
        Assert.ThrowsExactly<AutomationException>(() => unknown.Choose(Pin() with { SheetInstancePath = [] }));
        Assert.ThrowsExactly<AutomationException>(() => unknown.Choose(Pin() with { Pin = " " }));
        var selector = new DiagramPinSelector("Data", "I2C", ["SDA", "SDA"], []);
        Assert.ThrowsExactly<AutomationException>(selector.Validate);
    }
}
