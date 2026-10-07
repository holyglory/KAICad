using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

// Outcome p079fb6adb164b14c remains open until native routing/DRC proof is integrated.
[TestClass]
public sealed class PcbKeepoutAuthoringContractTests
{
    private const string KeepoutId = "11111111-1111-1111-1111-111111111111";
    private const string BoardId = "55555555-5555-5555-5555-555555555555";

    [TestMethod]
    public void KeepoutContractBuildsRuleAreaWithAllRestrictionsAndPhysicalLayers()
    {
        var input = new PcbKeepoutAuthoringInput
        {
            Id = KeepoutId,
            Layers = ["F.Cu", "F.Mask", "User.Drawings"],
            KeepoutCopper = true,
            KeepoutVias = true,
            KeepoutTracks = true,
            KeepoutPads = true,
            KeepoutFootprints = true,
            Polygons = [new PcbKeepoutPolygonInput
            {
                Outline =
                [new() { XNm = 0, YNm = 0 }, new() { XNm = 20_000, YNm = 0 },
                 new() { XNm = 20_000, YNm = 20_000 }, new() { XNm = 0, YNm = 20_000 }],
                Holes = [[new() { XNm = 5_000, YNm = 5_000 }, new() { XNm = 10_000, YNm = 5_000 },
                          new() { XNm = 10_000, YNm = 10_000 }]]
            }]
        };

        Zone native = input.ToNative();
        PcbKeepoutAuthoringContract.ValidateCreate(native);
        Assert.AreEqual(ZoneType.ZtRuleArea, native.Type);
        Assert.IsNotNull(native.RuleAreaSettings);
        Assert.IsTrue(native.RuleAreaSettings.KeepoutCopper);
        Assert.IsTrue(native.RuleAreaSettings.KeepoutVias);
        Assert.IsTrue(native.RuleAreaSettings.KeepoutTracks);
        Assert.IsTrue(native.RuleAreaSettings.KeepoutPads);
        Assert.IsTrue(native.RuleAreaSettings.KeepoutFootprints);
        CollectionAssert.AreEquivalent(
            new[] { BoardLayer.BlFCu, BoardLayer.BlFMask, BoardLayer.BlDwgsUser }, native.Layers.ToArray());
        PcbKeepoutAuthoringContract.ValidateOperationId("22222222-2222-2222-2222-222222222222");
        Assert.ThrowsExactly<AutomationException>(() =>
            PcbKeepoutAuthoringContract.ValidateOperationId("a2222222-2222-2222-2222-222222222222".ToUpperInvariant()));
    }

    [TestMethod]
    public void KeepoutContractRejectsNoRestrictionsCopperAndDerivedOrPlacementState()
    {
        var noRestrictions = Keepout();
        noRestrictions.RuleAreaSettings!.KeepoutTracks = false;
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(noRestrictions));

        var copper = Keepout();
        copper.RuleAreaSettings = null;
        copper.CopperSettings = new CopperZoneSettings { FillMode = ZoneFillMode.ZfmSolid, Net = new() };
        copper.Type = ZoneType.ZtCopper;
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(copper));

        var filled = Keepout();
        filled.Filled = true;
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(filled));

        var parented = Keepout();
        parented.Parent = new KIID { Value = BoardId };
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(parented));

        var placement = Keepout();
        placement.RuleAreaSettings!.PlacementEnabled = true;
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(placement));

        var placementSourceType = Keepout();
        placementSourceType.RuleAreaSettings!.PlacementSourceType = PlacementRuleSourceType.PrstSheetName;
        var placementError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbKeepoutAuthoringContract.ValidateCreate(placementSourceType));
        Assert.AreEqual("unsupported_pcb_keepout_placement", placementError.Code);

        var malformedPlacementType = Keepout();
        malformedPlacementType.RuleAreaSettings!.PlacementSourceType = (PlacementRuleSourceType)999;
        var placementTypeError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbKeepoutAuthoringContract.ValidateCreate(malformedPlacementType));
        Assert.AreEqual("invalid_pcb_keepout_settings", placementTypeError.Code);

        var derivedFill = Keepout();
        derivedFill.FilledPolygons.Add(new ZoneFilledPolygons { Layer = BoardLayer.BlFCu, Shapes = new PolySet() });
        var fillError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbKeepoutAuthoringContract.ValidateCreate(derivedFill));
        Assert.AreEqual("unsupported_pcb_keepout_fill", fillError.Code);
    }

    [TestMethod]
    public void KeepoutContractRejectsInvalidLayersGeometryAndArcs()
    {
        var duplicateLayer = Keepout();
        duplicateLayer.Layers.Add(BoardLayer.BlFCu);
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(duplicateLayer));

        var unknownLayer = Keepout();
        unknownLayer.Layers[0] = BoardLayer.BlUnknown;
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(unknownLayer));

        var duplicatePoint = Keepout();
        duplicatePoint.Outline.Polygons[0].Outline.Nodes[2].Point =
            duplicatePoint.Outline.Polygons[0].Outline.Nodes[1].Point.Clone();
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(duplicatePoint));

        var arc = Keepout();
        arc.Outline.Polygons[0].Outline.Nodes[1].Arc = new ArcStartMidEnd
        {
            Start = new() { XNm = 10_000, YNm = 0 }, Mid = new() { XNm = 15_000, YNm = 5_000 },
            End = new() { XNm = 20_000, YNm = 0 }
        };
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateCreate(arc));
    }

    [TestMethod]
    public void KeepoutContractPinsPcbLifecycleAndRejectsForeignReadbackParent()
    {
        var document = new DocumentSpecifier
        {
            Type = DocumentType.DoctypePcb,
            BoardFilename = "board.kicad_pcb",
            Project = new() { Name = "project", Path = "/tmp/project" }
        };
        var state = new DocumentLifecycleState
        {
            Document = document,
            Scope = DocumentLifecycleScope.DlsPcb,
            ProcessEpoch = "a4444444-4444-4444-4444-444444444444",
            NativeIdentity = BoardId,
            StateSha256 = new string('a', 64),
            Revision = new() { Epoch = BoardId, Sequence = 4 }
        };
        PcbKeepoutAuthoringContract.ValidateDocument(document);
        PcbKeepoutAuthoringContract.ValidateState(document, state);

        var uppercaseEpoch = state.Clone();
        uppercaseEpoch.ProcessEpoch = state.ProcessEpoch.ToUpperInvariant();
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateState(document, uppercaseEpoch));
        var wrongRevisionEpoch = state.Clone();
        wrongRevisionEpoch.Revision.Epoch = "66666666-6666-6666-6666-666666666666";
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateState(document, wrongRevisionEpoch));

        var expected = Keepout();
        var actual = expected.Clone();
        actual.Parent = new KIID { Value = BoardId };
        PcbKeepoutAuthoringContract.ValidateReadback(expected, actual, BoardId);
        actual.Parent.Value = "66666666-6666-6666-6666-666666666666";
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateReadback(expected, actual, BoardId));

        var placement = expected.Clone();
        placement.RuleAreaSettings!.PlacementSource = "Sheet A";
        Assert.ThrowsExactly<AutomationException>(() => PcbKeepoutAuthoringContract.ValidateReadback(expected, placement, BoardId));

        var placementType = expected.Clone();
        placementType.RuleAreaSettings!.PlacementSourceType = PlacementRuleSourceType.PrstGroup;
        var placementReadbackError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbKeepoutAuthoringContract.ValidateReadback(expected, placementType, BoardId));
        Assert.AreEqual("pcb_keepout_readback_mismatch", placementReadbackError.Code);
    }

    [TestMethod]
    public void KeepoutContractRequiresCanonicalPayloadAndDeleteIdentity()
    {
        var uppercaseInput = new PcbKeepoutAuthoringInput
        {
            Id = "a1111111-1111-1111-1111-111111111111".ToUpperInvariant(),
            Layers = ["F.Cu"],
            Polygons = [new PcbKeepoutPolygonInput
            {
                Outline = [new() { XNm = 0, YNm = 0 }, new() { XNm = 20_000, YNm = 0 }, new() { XNm = 0, YNm = 20_000 }]
            }],
            KeepoutTracks = true
        };
        var inputError = Assert.ThrowsExactly<AutomationException>(() => uppercaseInput.ToNative());
        Assert.AreEqual("invalid_pcb_keepout_identity", inputError.Code);

        Assert.ThrowsExactly<AutomationException>(() =>
            PcbKeepoutAuthoringContract.ValidateDeleteId("a1111111-1111-1111-1111-111111111111".ToUpperInvariant()));
        Assert.ThrowsExactly<AutomationException>(() =>
            PcbKeepoutAuthoringContract.ValidateDeleteId(Guid.Empty.ToString("D")));
    }

    private static Zone Keepout()
    {
        var zone = new Zone
        {
            Id = new() { Value = KeepoutId },
            Type = ZoneType.ZtRuleArea,
            Name = "MCP keepout",
            RuleAreaSettings = new RuleAreaSettings { KeepoutTracks = true },
            Border = new() { Style = ZoneBorderStyle.ZbsSolid, Pitch = new() { ValueNm = 0 } },
            Outline = new PolySet
            {
                Polygons =
                {
                    new PolygonWithHoles
                    {
                        Outline = new PolyLine
                        {
                            Closed = true,
                            Nodes =
                            {
                                new PolyLineNode { Point = new() { XNm = 0, YNm = 0 } },
                                new PolyLineNode { Point = new() { XNm = 20_000, YNm = 0 } },
                                new PolyLineNode { Point = new() { XNm = 20_000, YNm = 20_000 } },
                                new PolyLineNode { Point = new() { XNm = 0, YNm = 20_000 } }
                            }
                        }
                    }
                }
            }
        };
        zone.Layers.Add(BoardLayer.BlFCu);
        return zone;
    }
}
