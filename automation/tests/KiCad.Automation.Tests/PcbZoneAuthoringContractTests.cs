using Kiapi.Board.Types;
using Kiapi.Common.Types;
using KiCad.Automation.Mcp;
using KiCad.Automation.Model;
using KiCad.Automation.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class PcbZoneAuthoringContractTests
{
    [TestMethod]
    public void CopperZoneContractAcceptsBoundedPointPolygon()
    {
        var zone = Zone("11111111-1111-1111-1111-111111111111");
        PcbZoneAuthoringContract.ValidateCreate(zone);
        PcbZoneAuthoringContract.ValidateOperationId("22222222-2222-2222-2222-222222222222");

        var input = new PcbZoneAuthoringInput
        {
            Id = zone.Id.Value, Layers = ["F.Cu"], NetName = "GND",
            Polygons = [new PcbZonePolygonInput
            {
                Outline =
                [new() { XNm = 0, YNm = 0 }, new() { XNm = 20_000, YNm = 0 },
                 new() { XNm = 20_000, YNm = 20_000 }, new() { XNm = 0, YNm = 20_000 }]
            }]
        };
        PcbZoneAuthoringContract.ValidateCreate(input.ToNative());
    }

    [TestMethod]
    public void CopperZoneContractRejectsUnsupportedTypeAndDerivedState()
    {
        var graphical = Zone("11111111-1111-1111-1111-111111111111");
        graphical.Type = ZoneType.ZtGraphical;
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(graphical));

        var filled = Zone("11111111-1111-1111-1111-111111111111");
        filled.Filled = true;
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(filled));

        var parented = Zone("11111111-1111-1111-1111-111111111111");
        parented.Parent = new KIID { Value = "33333333-3333-3333-3333-333333333333" };
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(parented));
    }

    [TestMethod]
    public void CopperZoneContractRejectsArcsDuplicatesAndZeroArea()
    {
        var arc = Zone("11111111-1111-1111-1111-111111111111");
        arc.Outline.Polygons[0].Outline.Nodes[1].Arc = new ArcStartMidEnd
        {
            Start = new() { XNm = 10_000, YNm = 0 }, Mid = new() { XNm = 15_000, YNm = 5_000 },
            End = new() { XNm = 20_000, YNm = 0 }
        };
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(arc));

        var duplicate = Zone("11111111-1111-1111-1111-111111111111");
        duplicate.Outline.Polygons[0].Outline.Nodes[2].Point = duplicate.Outline.Polygons[0].Outline.Nodes[1].Point.Clone();
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(duplicate));

        var line = Zone("11111111-1111-1111-1111-111111111111");
        line.Outline.Polygons[0].Outline.Nodes[2].Point = new() { XNm = 20_000, YNm = 0 };
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(line));

        var outOfRange = Zone("11111111-1111-1111-1111-111111111111");
        outOfRange.Outline.Polygons[0].Outline.Nodes[0].Point.XNm = (long)int.MaxValue + 1;
        var rangeError = Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(outOfRange));
        Assert.AreEqual("invalid_pcb_zone_coordinate", rangeError.Code);

        var unknownFill = Zone("11111111-1111-1111-1111-111111111111");
        unknownFill.CopperSettings.FillMode = (ZoneFillMode)999;
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(unknownFill));
    }

    [TestMethod]
    public void CopperZoneContractRequiresPcbLifecycleIdentity()
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
            ProcessEpoch = "44444444-4444-4444-4444-444444444444",
            NativeIdentity = "55555555-5555-5555-5555-555555555555",
            StateSha256 = new string('a', 64),
            Revision = new() { Epoch = "55555555-5555-5555-5555-555555555555", Sequence = 4 }
        };
        PcbZoneAuthoringContract.ValidateDocument(document);
        PcbZoneAuthoringContract.ValidateState(document, state);
        var wrongDocument = state.Clone();
        wrongDocument.Document.BoardFilename = "other.kicad_pcb";
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateState(document,
            wrongDocument));
        var wrongRevisionEpoch = state.Clone();
        wrongRevisionEpoch.Revision.Epoch = "66666666-6666-6666-6666-666666666666";
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateState(document,
            wrongRevisionEpoch));
    }

    [TestMethod]
    public void CopperZoneContractRejectsNonCanonicalOperationAndDeleteIds()
    {
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateOperationId("not-a-guid"));
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateOperationId("a2222222-2222-2222-2222-222222222222".ToUpperInvariant()));
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateOperationId(Guid.Empty.ToString("D")));
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateDeleteId("not-a-zone"));
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateDeleteId(Guid.Empty.ToString("D")));
    }

    [TestMethod]
    public void CopperZoneContractAcceptsNativeDerivedReadbackOnlyForOwningBoard()
    {
        const string nativeIdentity = "77777777-7777-7777-7777-777777777777";
        var existing = Zone("11111111-1111-1111-1111-111111111111");
        existing.Parent = new() { Value = nativeIdentity };
        existing.Filled = true;
        PcbZoneAuthoringContract.ValidateExisting(existing, nativeIdentity);

        var foreign = existing.Clone();
        foreign.Parent.Value = "88888888-8888-8888-8888-888888888888";
        var parentError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbZoneAuthoringContract.ValidateExisting(foreign, nativeIdentity));
        Assert.AreEqual("unsupported_pcb_zone_parent", parentError.Code);

        var unowned = existing.Clone();
        unowned.Parent = null;
        unowned.Filled = false;
        PcbZoneAuthoringContract.ValidateUpdate(unowned);
    }

    [TestMethod]
    public void CopperZoneContractRejectsCreateUpdatePayloadsBeforeNativeDispatch()
    {
        var duplicateLayer = Zone("11111111-1111-1111-1111-111111111111");
        duplicateLayer.Layers.Add(BoardLayer.BlFCu);
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(duplicateLayer));

        var negativeClearance = Zone("11111111-1111-1111-1111-111111111111");
        negativeClearance.CopperSettings.Clearance.ValueNm = -1;
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateUpdate(negativeClearance));

        var missingNet = Zone("11111111-1111-1111-1111-111111111111");
        missingNet.CopperSettings.Net = null;
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateCreate(missingNet));

        var nullPoint = new PcbZoneAuthoringInput
        {
            Id = "11111111-1111-1111-1111-111111111111",
            Layers = ["F.Cu"],
            Polygons = [new PcbZonePolygonInput { Outline = [null!] }]
        };
        Assert.ThrowsExactly<AutomationException>(() => nullPoint.ToNative());
    }

    [TestMethod]
    public void CopperZoneReadbackCanonicalizesRingOrderButRejectsArcTopology()
    {
        var expected = Zone("11111111-1111-1111-1111-111111111111");
        var rotated = expected.Clone();
        var nodes = rotated.Outline.Polygons[0].Outline.Nodes.Select(node => node.Clone()).ToArray();
        rotated.Outline.Polygons[0].Outline.Nodes.Clear();
        rotated.Outline.Polygons[0].Outline.Nodes.Add(nodes.Skip(2).Concat(nodes.Take(2)));
        rotated.Parent = new KIID { Value = "77777777-7777-7777-7777-777777777777" };
        PcbZoneAuthoringContract.ValidateReadback(expected, rotated);

        rotated.Outline.Polygons[0].Outline.Nodes[0].Arc = new ArcStartMidEnd
        {
            Start = new() { XNm = 0, YNm = 0 }, Mid = new() { XNm = 1, YNm = 1 }, End = new() { XNm = 2, YNm = 0 }
        };
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateReadback(expected, rotated));

        var foreignParent = expected.Clone();
        foreignParent.Parent = new KIID { Value = "88888888-8888-8888-8888-888888888888" };
        Assert.ThrowsExactly<AutomationException>(() => PcbZoneAuthoringContract.ValidateReadback(
            expected, foreignParent, "77777777-7777-7777-7777-777777777777"));
    }

    [TestMethod]
    public void CopperZoneReadbackRejectsChangedOutlineAndSettings()
    {
        var expected = Zone("11111111-1111-1111-1111-111111111111");

        var changedOutline = expected.Clone();
        changedOutline.Outline.Polygons[0].Outline.Nodes[1].Point.XNm++;
        var outlineError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbZoneAuthoringContract.ValidateReadback(expected, changedOutline));
        Assert.AreEqual("pcb_zone_readback_mismatch", outlineError.Code);

        var changedSettings = expected.Clone();
        changedSettings.CopperSettings.Clearance.ValueNm++;
        var settingsError = Assert.ThrowsExactly<AutomationException>(() =>
            PcbZoneAuthoringContract.ValidateReadback(expected, changedSettings));
        Assert.AreEqual("pcb_zone_readback_mismatch", settingsError.Code);
    }

    private static Zone Zone(string id)
    {
        var zone = new Zone
        {
            Id = new() { Value = id }, Type = ZoneType.ZtCopper,
            CopperSettings = new CopperZoneSettings
            {
                FillMode = ZoneFillMode.ZfmSolid,
                Net = new() { Name = "GND" },
                Clearance = new() { ValueNm = 200 },
                MinThickness = new() { ValueNm = 200 },
                Connection = new() { ZoneConnection = ZoneConnectionStyle.ZcsInherited },
                IslandMode = IslandRemovalMode.IrmAlways
            },
            Border = new() { Style = ZoneBorderStyle.ZbsSolid, Pitch = new() { ValueNm = 0 } }
        };
        zone.Layers.Add(BoardLayer.BlFCu);
        zone.Outline = new PolySet
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
        };
        return zone;
    }
}
