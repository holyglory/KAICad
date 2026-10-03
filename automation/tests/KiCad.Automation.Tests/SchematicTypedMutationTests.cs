using Kiapi.Schematic.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Native;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class SchematicTypedMutationTests
{
    private static readonly string Id = "12345678-1234-4234-8234-123456789012";

    [TestMethod]
    public void BuildsTypedConnectionItemsWithoutAnyPayloadAuthoring()
    {
        var wire = (SchematicLine)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Wire, Id,
            1_000, 2_000, 4_000, 2_000, null);
        Assert.AreEqual(Id, wire.Id.Value);
        Assert.AreEqual(SchematicLineType.SltWire, wire.Type);
        Assert.AreEqual(1_000, wire.Start.XNm);
        Assert.AreEqual(4_000, wire.End.XNm);

        var bus = (SchematicLine)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Bus, Id,
            1_000, 2_000, 4_000, 2_000, null);
        Assert.AreEqual(SchematicLineType.SltBus, bus.Type);

        var junction = (Junction)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Junction, Id,
            1_000, 2_000, null, null, null);
        Assert.AreEqual(2_000, junction.Position.YNm);

        var label = (LocalLabel)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.LocalLabel, Id,
            1_000, 2_000, null, null, "DATA");
        Assert.AreEqual("DATA", label.Text.Text_);
        Assert.AreEqual(SchematicLabelSpinStyle.SlssRight, label.SpinStyle);

        var note = (SchematicText)SchematicTypedMutationTools.BuildItem(SchematicConnectionItemKind.Text, Id,
            1_000, 2_000, null, null, "note");
        Assert.AreEqual("note", note.Text.Text_);
        Assert.AreEqual(1_000, note.Text.Position.XNm);
    }

    [TestMethod]
    public void RejectsMissingEndpointsTextAndNonNativeCoordinates()
    {
        var missing = Assert.ThrowsExactly<AutomationException>(() => SchematicTypedMutationTools.BuildItem(
            SchematicConnectionItemKind.Wire, Id, 1_000, 2_000, null, null, null));
        Assert.AreEqual("missing_connection_endpoint", missing.Code);

        var badCoordinate = Assert.ThrowsExactly<AutomationException>(() => SchematicTypedMutationTools.BuildItem(
            SchematicConnectionItemKind.Junction, Id, 1_001, 2_000, null, null, null));
        Assert.AreEqual("invalid_coordinate", badCoordinate.Code);

        var badId = Assert.ThrowsExactly<AutomationException>(() => SchematicTypedMutationTools.BuildItem(
            SchematicConnectionItemKind.Junction, "not-a-uuid", 1_000, 2_000, null, null, null));
        Assert.AreEqual("invalid_identity", badId.Code);
    }
}
