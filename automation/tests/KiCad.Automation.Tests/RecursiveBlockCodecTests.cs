using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class RecursiveBlockCodecTests
{
    [TestMethod]
    public void SharedMessagesPreserveImmutableHistoryAndRestoreSourceAcrossBinaryRoundTrip()
    {
        var graph = RecursiveBlockFixture.Create().Graph;
        var draft = graph.StartDraft(graph.SelectedRoot);
        draft = draft with { Requirements = draft.Requirements.Edit(DiagramRequirementField.Routing, "Exact text\r\n  Ω🙂"), Name = "Changed system" };
        var origin = RecursiveBlockFixture.Origin("Agent client") with
        { RecordedAt = RecursiveBlockFixture.Origin().RecordedAt.AddTicks(13), Sources = [new("datasheet", "rev3", 4, "table2", "variantA")], InputIds = [Guid.NewGuid()] };
        var changed = graph.SaveDraft(graph.SelectedRoot, [graph.SelectedRoot], draft, Guid.NewGuid(), Guid.NewGuid(), [], origin).Graph;
        var restoredDraft = changed.RestoreAsDraft(changed.StartDraft(changed.SelectedRoot), graph.SelectedRoot);
        var restored = changed.SaveDraft(changed.SelectedRoot, [changed.SelectedRoot], restoredDraft, Guid.NewGuid(), Guid.NewGuid(), [], origin).Graph;
        var data = RecursiveBlockCodec.Encode(restored);
        var decoded = RecursiveBlockCodec.Decode(P.RecursiveBlockGraphData.Parser.ParseFrom(data.ToByteArray()));
        Assert.AreEqual(RecursiveBlockGraphXml.Write(restored), RecursiveBlockGraphXml.Write(decoded));
        Assert.AreEqual(graph.SelectedRoot, decoded.Inspect(decoded.SelectedRoot).RestoredFrom);
        Assert.AreEqual(origin.RecordedAt, decoded.Inspect(decoded.SelectedRoot).Origin.RecordedAt);
        Assert.AreEqual("Exact text\r\n  Ω🙂", decoded.Requirements(changed.SelectedRoot).Requirements.Routing);
    }

    [TestMethod]
    public void UnknownFieldsBadTargetsMissingFieldsAndUnsupportedPrecisionAreRejected()
    {
        var data = RecursiveBlockCodec.Encode(RecursiveBlockFixture.Create().Graph);
        void Reject(Action<P.RecursiveBlockGraphData> change)
        {
            var copy = data.Clone(); change(copy); Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(copy));
        }
        Reject(d => d.SchemaVersion = 2);
        Reject(d => d.SelectedRoot.BlockId = Guid.NewGuid().ToString("D"));
        Reject(d => d.SelectedRoot.StateId = d.SelectedRoot.StateId.ToUpperInvariant());
        Reject(d => d.Revisions[0].Origin = null);
        Reject(d => d.Revisions[0].Origin.RecordedAt = null);
        Reject(d => d.Revisions[0].Origin.RecordedAt.Nanos = 1);
        Reject(d => d.Revisions[0].Origin.RecordedAt.Seconds = long.MaxValue);
        Reject(d => d.Revisions[0].Origin.Kind = P.DiagramActorKind.DakUnknown);
        Reject(d => d.RequirementHistories[0].Revisions[0].Fields = null);
        Reject(d => d.RequirementHistories[0].Revisions[0].ParentId = "");
        var unknown = P.RecursiveBlockGraphData.Parser.ParseFrom(data.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray());
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(unknown));
        Reject(d => d.Revisions[0].Selection = P.BlockSelectionData.Parser.ParseFrom(
            d.Revisions[0].Selection.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray()));
    }

    [TestMethod]
    public void PartialAndExactEndpointsRetainIndependentStatesSelectorsAndRepeatedSheetIdentity()
    {
        var pin = new DiagramPinTarget(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], "D15");
        var endpoint = new DiagramEndpointBinding(DiagramEndpointKind.Candidates, Guid.NewGuid(), Guid.NewGuid(), "Memory data role",
            new("Data", "", ["Bidirectional"], [new("requirements", "rev2", null, null, null)]), [pin], null);
        var candidate = RecursiveBlockCodec.Decode(P.DiagramEndpointBindingData.Parser.ParseFrom(RecursiveBlockCodec.Encode(endpoint).ToByteArray()));
        Assert.AreEqual(DiagramEndpointKind.Candidates, candidate.Kind); Assert.IsNull(candidate.Pin);
        Assert.AreEqual("", candidate.Selector!.Protocol);
        Assert.IsTrue(pin.SamePin(candidate.Candidates[0]));
        var chosen = RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(endpoint.Choose(pin)));
        Assert.AreEqual(DiagramEndpointKind.Pin, chosen.Kind); Assert.IsTrue(pin.SamePin(chosen.Pin!));
        Assert.AreEqual(endpoint.Selector.Role, chosen.Selector!.Role);
        var unknown = RecursiveBlockCodec.Encode(endpoint); unknown.Kind = P.DiagramEndpointKind.DekUnknown;
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(unknown));
        var invalidPin = RecursiveBlockCodec.Encode(endpoint.Choose(pin)); invalidPin.Pin.SheetInstancePath.Clear();
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(invalidPin));
    }
}
