using Google.Protobuf.WellKnownTypes;
using Kiapi.Board.Types;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbViaMinimizationTests
{
    [TestMethod]
    public void CombinesContiguousSamePositionViaSpans()
    {
        var document = new DocumentSpecifier { Type = DocumentType.DoctypePcb, BoardFilename = "via.kicad_pcb", Project = new() { Name = "via", Path = "/tmp/via" } };
        var candidate = new CreateItems { Header = new() { Document = document } };
        candidate.Items.Add(Any.Pack(Via("00000000-0000-0000-0000-000000000001", BoardLayer.BlFCu, BoardLayer.BlIn1Cu)));
        candidate.Items.Add(Any.Pack(Via("00000000-0000-0000-0000-000000000002", BoardLayer.BlIn1Cu, BoardLayer.BlBCu)));
        var plan = PcbViaMinimizationTools.BuildPlan(candidate, new());
        Assert.AreEqual(1, plan.MergedCount);
        Assert.AreEqual(2, plan.RemovedItemIds.Count);
        Assert.AreEqual(1, plan.CreatedViaIds.Count);
        Via merged = plan.Create.Items.Single().Unpack<Via>();
        CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlBCu },
            merged.PadStack.Layers.ToArray());
        CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlBCu },
            merged.PadStack.CopperLayers.Select(layer => layer.Layer).ToArray());
    }

    [TestMethod]
    public void CollapsesThreeSpanChainIntoOneReplacementAndDeletesOnlyOriginals()
    {
        var document = Document();
        var candidate = new CreateItems { Header = new() { Document = document } };
        string[] ids =
        {
            "00000000-0000-0000-0000-000000000101",
            "00000000-0000-0000-0000-000000000102",
            "00000000-0000-0000-0000-000000000103"
        };
        candidate.Items.Add(Any.Pack(Via(ids[0], BoardLayer.BlFCu, BoardLayer.BlIn1Cu)));
        candidate.Items.Add(Any.Pack(Via(ids[1], BoardLayer.BlIn1Cu, BoardLayer.BlIn2Cu)));
        candidate.Items.Add(Any.Pack(Via(ids[2], BoardLayer.BlIn2Cu, BoardLayer.BlBCu)));

        var plan = PcbViaMinimizationTools.BuildPlan(candidate, new());

        Assert.AreEqual(2, plan.MergedCount);
        CollectionAssert.AreEquivalent(ids, plan.RemovedItemIds.ToArray());
        Assert.AreEqual(1, plan.Create.Items.Count);
        Assert.AreEqual(1, plan.CreatedViaIds.Count);
        Assert.IsFalse(plan.RemovedItemIds.Contains(plan.CreatedViaIds[0]), "A generated intermediate ID must never be deleted from the board.");
        Assert.AreEqual(1, plan.ViaCountAfter);
        Via merged = plan.Create.Items.Single().Unpack<Via>();
        StringAssert.Contains(merged.CustomProperties.Single(property => property.Key == "kicad.ai.via_minimize_sources").Value, ids[0]);
        StringAssert.Contains(merged.CustomProperties.Single(property => property.Key == "kicad.ai.via_minimize_sources").Value, ids[1]);
        StringAssert.Contains(merged.CustomProperties.Single(property => property.Key == "kicad.ai.via_minimize_sources").Value, ids[2]);
        CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlIn2Cu, BoardLayer.BlBCu },
            merged.PadStack.Layers.ToArray());
        CollectionAssert.AreEqual(new[] { BoardLayer.BlFCu, BoardLayer.BlIn1Cu, BoardLayer.BlIn2Cu, BoardLayer.BlBCu },
            merged.PadStack.CopperLayers.Select(layer => layer.Layer).ToArray());
    }

    [TestMethod]
    public void LeavesDifferentDrillProfilesUntouched()
    {
        var candidate = new CreateItems { Header = new() { Document = Document() } };
        candidate.Items.Add(Any.Pack(Via("00000000-0000-0000-0000-000000000111", BoardLayer.BlFCu, BoardLayer.BlIn1Cu, 300_000)));
        candidate.Items.Add(Any.Pack(Via("00000000-0000-0000-0000-000000000112", BoardLayer.BlIn1Cu, BoardLayer.BlBCu, 350_000)));

        var plan = PcbViaMinimizationTools.BuildPlan(candidate, new());

        Assert.AreEqual(0, plan.MergedCount);
        Assert.AreEqual(0, plan.Delete.ItemIds.Count);
        Assert.AreEqual(0, plan.Create.Items.Count);
        Assert.AreEqual(2, plan.ViaCountAfter);
    }

    [TestMethod]
    public void LeavesDifferentJunctionPadDefinitionsUntouched()
    {
        var candidate = new CreateItems { Header = new() { Document = Document() } };
        candidate.Items.Add(Any.Pack(Via("00000000-0000-0000-0000-000000000121", BoardLayer.BlFCu, BoardLayer.BlIn1Cu)));
        Via incompatible = Via("00000000-0000-0000-0000-000000000122", BoardLayer.BlIn1Cu, BoardLayer.BlBCu);
        incompatible.PadStack.CopperLayers.Single(layer => layer.Layer == BoardLayer.BlIn1Cu).Shape = PadStackShape.PssRectangle;
        candidate.Items.Add(Any.Pack(incompatible));

        var plan = PcbViaMinimizationTools.BuildPlan(candidate, new());

        Assert.AreEqual(0, plan.MergedCount);
        Assert.AreEqual(0, plan.Delete.ItemIds.Count);
        Assert.AreEqual(0, plan.Create.Items.Count);
    }

    [TestMethod]
    public void RejectsMalformedSelectedVia()
    {
        var candidate = new CreateItems { Header = new() { Document = Document() } };
        Via malformed = Via("00000000-0000-0000-0000-000000000131", BoardLayer.BlFCu, BoardLayer.BlIn1Cu);
        malformed.Id.Value = "";
        candidate.Items.Add(Any.Pack(malformed));

        var error = Assert.ThrowsExactly<AutomationException>(() => PcbViaMinimizationTools.BuildPlan(candidate, new()));

        Assert.AreEqual("invalid_pcb_via_minimize_candidate", error.Code);
    }

    private static Via Via(string id, BoardLayer start, BoardLayer end, long drillDiameter = 300_000) => new()
    {
        Id = new() { Value = id }, Position = new() { XNm = 1_000_000, YNm = 2_000_000 }, Net = new() { Name = "N" },
        PadStack = new() { Layers = { start, end }, Drill = new() { StartLayer = start, EndLayer = end, Diameter = new() { XNm = drillDiameter, YNm = drillDiameter } },
            CopperLayers = { new PadStackLayer { Layer = start }, new PadStackLayer { Layer = end } } }
    };

    private static DocumentSpecifier Document() => new()
    {
        Type = DocumentType.DoctypePcb, BoardFilename = "via.kicad_pcb", Project = new() { Name = "via", Path = "/tmp/via" }
    };
}
