using System.Xml.Linq;
using KiCad.Automation.Model;

namespace KiCad.Automation.Tests;

[TestClass]
public sealed class StructuralDiagramPresentationTests
{
    [TestMethod]
    public void PurposePropertiesAndPresentationSurviveEngineeringXml()
    {
        var (circuit, diagram) = Fixture();
        string xml = StructuralDiagramXml.Write(diagram, circuit);
        var read = StructuralDiagramXml.Read(xml, circuit);
        Assert.AreEqual(xml, StructuralDiagramXml.Write(read, circuit));
        Assert.AreEqual(diagram.Blocks[0].Purpose, read.Blocks.Single(b => b.Id == diagram.Blocks[0].Id).Purpose);
        Assert.AreEqual(StructuralConnectionDirection.FirstToSecond, read.Connections.Single().Direction);
        var property = read.Properties!.Single();
        Assert.AreEqual(diagram.Properties!.Single().Statement.Text, property.Statement.Text);
        Assert.AreEqual(diagram.Properties!.Single().Statement.Quantity, property.Statement.Quantity);
        Assert.AreEqual(diagram.Properties!.Single().Statement.Sources.Single(), property.Statement.Sources.Single());
        var layout = read.Presentation;
        Assert.IsNotNull(layout);
        CollectionAssert.AreEquivalent(diagram.Presentation!.Blocks.ToArray(), layout.Blocks.ToArray());
        CollectionAssert.AreEquivalent(diagram.Presentation!.Ports.ToArray(), layout.Ports.ToArray());
        CollectionAssert.AreEqual(diagram.Presentation!.Connections[0].Waypoints.ToArray(), layout.Connections[0].Waypoints.ToArray());
        Assert.AreEqual(diagram.Presentation!.Connections[0].Label, layout.Connections[0].Label);
        Assert.IsTrue(layout.Connections[0].Locked);
        var design = new EngineeringDesign(circuit, diagram, [], []);
        string combined = EngineeringDesignXml.Write(design, []);
        Assert.AreEqual(combined, EngineeringDesignXml.Write(EngineeringDesignXml.Read(combined, []), []));
    }

    [TestMethod]
    public void CoordinatesRemainOptionalAndDoNotDefineTheCircuit()
    {
        var (circuit, diagram) = Fixture();
        string electrical = CircuitXml.Write(circuit);
        var coordinateFree = diagram with { Presentation = null };
        var read = StructuralDiagramXml.Read(StructuralDiagramXml.Write(coordinateFree, circuit), circuit);
        Assert.IsNull(read.Presentation);
        Assert.AreEqual(electrical, CircuitXml.Write(circuit));
        Assert.AreEqual(diagram.Connections.Single().Direction, read.Connections.Single().Direction);
        Assert.AreEqual(diagram.Statements.Count, read.Statements.Count);
        Assert.AreEqual(diagram.Properties!.Count, read.Properties!.Count);

        // Use its own circuit, since identities are intentionally independent.
        var old = StructuralDiagramTests.Fixture();
        string oldXml = StructuralDiagramXml.Write(old.Item2, old.Item1);
        var oldRead = StructuralDiagramXml.Read(oldXml, old.Item1);
        Assert.AreEqual(oldXml, StructuralDiagramXml.Write(oldRead, old.Item1));
        Assert.IsNull(oldRead.Properties); Assert.IsNull(oldRead.Presentation);
        Assert.IsTrue(oldRead.Blocks.All(b => b.Purpose.Length == 0));
        Assert.IsTrue(oldRead.Connections.All(c => c.Direction == StructuralConnectionDirection.Unspecified));
    }

    [TestMethod]
    public void OrderingDoesNotChangeSavedPresentation()
    {
        var (circuit, diagram) = Fixture();
        var layout = diagram.Presentation!;
        var reordered = diagram with { Blocks = diagram.Blocks.Reverse().ToArray(),
            Presentation = layout with { Blocks = layout.Blocks.Reverse().ToArray(), Ports = layout.Ports.Reverse().ToArray() } };
        Assert.AreEqual(StructuralDiagramXml.Write(diagram, circuit), StructuralDiagramXml.Write(reordered, circuit));
    }

    [TestMethod]
    public void InvalidOwnersIdentityAliasesAndUnknownQuantitiesAreRejected()
    {
        var (circuit, diagram) = Fixture();
        var property = diagram.Properties!.Single();
        StructuralDiagram[] invalid =
        [
            diagram with { Properties = [property with { OwnerId = Guid.NewGuid() }] },
            diagram with { Properties = [property with { Statement = property.Statement with { Id = circuit.Id } }] },
            diagram with { Properties = [property with { Statement = property.Statement with { Id = diagram.Statements[0].Id } }] },
            diagram with { Properties = [property, property] },
            diagram with { Properties = [property with { Statement = property.Statement with { Replaces = Guid.NewGuid() } }] },
            diagram with { Properties = [property with { Statement = property.Statement with { Quantity = new(ParameterKind.Unclassified, "V") } }] },
            diagram with { Connections = [diagram.Connections[0] with { Direction = (StructuralConnectionDirection)99 }] }
        ];
        foreach (var value in invalid) Assert.ThrowsExactly<AutomationException>(() => value.Validate(circuit));
        // Contradictory evidence is retained for inspection, never normalized.
        (diagram with { Properties = [property with { Statement = property.Statement with
            { Quantity = new(ParameterKind.OperatingLimit, "V", Minimum: 5, Maximum: 3) } }] }).Validate(circuit);
    }

    [TestMethod]
    public void InvalidGeometryNeverRebindsToAnotherObject()
    {
        var (circuit, diagram) = Fixture();
        var layout = diagram.Presentation!;
        var block = layout.Blocks[0]; var port = layout.Ports[0]; var link = layout.Connections[0];
        StructuralPresentation[] invalid =
        [
            layout with { Blocks = [block, block] },
            layout with { Blocks = [block with { BlockId = Guid.NewGuid() }] },
            layout with { Blocks = [block with { WidthNm = 0 }] },
            layout with { Blocks = [block with { XNm = 1 }] },
            layout with { Blocks = [block with { XNm = long.MaxValue }] },
            layout with { Blocks = [block with { FillRgb = 0x1000000 }] },
            layout with { Ports = [port with { PortId = Guid.NewGuid() }] },
            layout with { Ports = [port, port] },
            layout with { Ports = [port with { Side = (StructuralPortSide)99 }] },
            layout with { Ports = [port with { OffsetNm = -100 }] },
            layout with { Ports = [port with { OffsetNm = block.HeightNm + 100 }] },
            layout with { Connections = [link with { ConnectionId = Guid.NewGuid() }] },
            layout with { Connections = [link with { Label = new(1, 0) }] }
        ];
        foreach (var value in invalid) Assert.ThrowsExactly<AutomationException>(() => value.Validate(diagram));
        (diagram with { Presentation = layout with { Ports = [port with { OffsetNm = 0 }] } }).Validate(circuit);
        (diagram with { Presentation = layout with { Ports = [port with { OffsetNm = block.HeightNm }] } }).Validate(circuit);
    }

    [TestMethod]
    public void UnsupportedXmlFieldsAndCoordinateSystemsFailClosed()
    {
        var (circuit, diagram) = Fixture();
        XNamespace ns = StructuralDiagramXml.Namespace;
        foreach (string field in new[] { "units", "coordinate-system", "unknown" })
        {
            var root = XElement.Parse(StructuralDiagramXml.Write(diagram, circuit));
            root.Element(ns + "presentation")!.SetAttributeValue(field, "unsupported");
            Assert.ThrowsExactly<AutomationException>(() => StructuralDiagramXml.Read(root.ToString(), circuit));
        }
    }

    private static (Circuit, StructuralDiagram) Fixture()
    {
        var (circuit, diagram) = StructuralDiagramTests.Fixture();
        var a = diagram.Blocks[0]; var b = diagram.Blocks[1];
        return (circuit, diagram with
        {
            Blocks = [a with { Purpose = "  User purpose\r\nwith exact whitespace\t " }, b],
            Connections = [diagram.Connections[0] with { Direction = StructuralConnectionDirection.FirstToSecond }],
            Properties = [new(a.Id, new(Guid.NewGuid(), "supply", "electrical", "  Wait for\r\nthe source. ",
                GuidanceStrength.Requirement, "active mode", [new("brief\tA", "r1\r\n", 1, "Table 1", "prototype")],
                Quantity: new(ParameterKind.Unclassified, "V", UnknownReason: "Not selected")))],
            Presentation = new([new(a.Id, 0, 0, 20_000_000, 15_000_000, true, 0xdcecff),
                new(b.Id, 50_000_000, 0, 20_000_000, 15_000_000)],
                [new(diagram.Ports[0].Id, StructuralPortSide.Right, 10_000_000), new(diagram.Ports[1].Id, StructuralPortSide.Left, 5_000_000)],
                [new(diagram.Connections[0].Id, [new(25_000_000, 10_000_000), new(25_000_000, 5_000_000)], new(25_000_000, 7_500_000), true)])
        });
    }
}
