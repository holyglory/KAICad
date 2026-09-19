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
    [DataRow("future-version")]
    [DataRow("wrong-target")]
    [DataRow("noncanonical-identity")]
    [DataRow("missing-origin")]
    [DataRow("missing-time")]
    [DataRow("unsupported-precision")]
    [DataRow("invalid-seconds")]
    [DataRow("negative-nanos")]
    [DataRow("unnormalized-nanos")]
    [DataRow("unknown-actor")]
    [DataRow("missing-fields")]
    [DataRow("empty-parent")]
    [DataRow("unknown-root-field")]
    [DataRow("unknown-nested-field")]
    public void UnknownFieldsBadTargetsMissingFieldsAndUnsupportedPrecisionAreRejected(string scenario)
    {
        var data = RecursiveBlockCodec.Encode(RecursiveBlockFixture.Create().Graph);
        switch (scenario)
        {
            case "future-version": data.SchemaVersion = 2; break;
            case "wrong-target": data.SelectedRoot.BlockId = Guid.NewGuid().ToString("D"); break;
            case "noncanonical-identity": data.SelectedRoot.StateId = data.SelectedRoot.StateId.ToUpperInvariant(); break;
            case "missing-origin": data.Revisions[0].Origin = null; break;
            case "missing-time": data.Revisions[0].Origin.RecordedAt = null; break;
            case "unsupported-precision": data.Revisions[0].Origin.RecordedAt.Nanos = 1; break;
            case "invalid-seconds": data.Revisions[0].Origin.RecordedAt.Seconds = long.MaxValue; break;
            case "negative-nanos": data.Revisions[0].Origin.RecordedAt.Nanos = -1; break;
            case "unnormalized-nanos": data.Revisions[0].Origin.RecordedAt.Nanos = 1000000000; break;
            case "unknown-actor": data.Revisions[0].Origin.Kind = P.DiagramActorKind.DakUnknown; break;
            case "missing-fields": data.RequirementHistories[0].Revisions[0].Fields = null; break;
            case "empty-parent": data.RequirementHistories[0].Revisions[0].ParentId = ""; break;
            case "unknown-root-field":
                data = P.RecursiveBlockGraphData.Parser.ParseFrom(data.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray()); break;
            case "unknown-nested-field":
                data.Revisions[0].Selection = P.BlockSelectionData.Parser.ParseFrom(
                    data.Revisions[0].Selection.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray()); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(data));
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
        Assert.IsNotNull(endpoint.Selector);
        Assert.AreEqual(endpoint.Selector.Role, chosen.Selector!.Role);
        var unknown = RecursiveBlockCodec.Encode(endpoint); unknown.Kind = P.DiagramEndpointKind.DekUnknown;
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(unknown));
        var invalidPin = RecursiveBlockCodec.Encode(endpoint.Choose(pin)); invalidPin.Pin.SheetInstancePath.Clear();
        Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(invalidPin));
    }
}
