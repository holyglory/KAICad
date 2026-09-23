using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Xml.Linq;
using System.Xml.Schema;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using P = KiCad.Automation.Protocol.Diagrams;

namespace KiCad.Automation.Tests;

/// <summary>A canonical schema 2 diagram built on the linked System/PSU/CPU fixture. It states every
/// schema 2 fact: interface domains and directions, per-level layouts (frame, z-ordered and locked
/// blocks, fills, ports on every side, routes with waypoints and labels, one dormant entry), interface
/// realizations of every target kind and state, connection domains and directions, interconnect
/// realizations of every segment kind with joins, and a Harness physical target. All values are test
/// data, not engineering recommendations.</summary>
internal sealed record SchemaTwoFixture(RecursiveBlockGraph Graph, LinkedDiagramFixture Linked, Guid Harness, Guid Design,
    Guid Circuit, Guid Connector, Guid PowerStageComponent, DiagramPinTarget PowerStagePin)
{
    public static readonly RequirementRevisionOrigin Origin = RecursiveBlockFixture.Origin();

    public static SchemaTwoFixture Create()
    {
        var f = LinkedDiagramFixture.Create(); var g = f.Graph;
        Guid harness = Guid.NewGuid(), design = Guid.NewGuid(), circuit = Guid.NewGuid(), net = Guid.NewGuid(), connector = Guid.NewGuid();
        Guid component = Guid.NewGuid(), sheet = Guid.NewGuid(), hardwareInterface = Guid.NewGuid();
        var pin = new DiagramPinTarget(design, component, [sheet], "1");
        Guid system = g.SelectedRoot.BlockId, psu = f.Blocks["PSU"].BlockId, cpu = f.Blocks["CPU"].BlockId;
        Guid power = f.Blocks["Power stage"].BlockId, telemetry = f.Blocks["Telemetry"].BlockId, regulator = f.Blocks["Regulator"].BlockId;
        ImmutableArray<DiagramBoundaryInterface> Interfaces(RecursiveBlockRevision r) => [.. r.LocalDiagram.Interfaces.Select(i =>
            i.Id == f.Ports["PSU/Power"] ? i with { Domain = DiagramDomain.Power, Direction = DiagramInterfaceDirection.Output }
            : i.Id == f.Ports["CPU/Power"] ? i with { Domain = DiagramDomain.Power, Direction = DiagramInterfaceDirection.Input }
            : i.Id == f.Ports["PSU/Telemetry"] ? i with { Domain = DiagramDomain.Data, Direction = DiagramInterfaceDirection.Bidirectional } : i)];
        RecursiveBlockRevision Change(RecursiveBlockRevision r)
        {
            var local = r.LocalDiagram with { Interfaces = Interfaces(r) };
            Guid block = r.Selection.BlockId;
            if (block == system)
                return r with { PhysicalAllocation = new(PhysicalAllocationState.Resolved, [new(harness, PhysicalAllocationKind.Harness, "W1 supply harness")]),
                    Diagram = local with { Presentation = new(
                        [new(psu, new(100, 80, 240, 145), Locked: true, FillRgb: 0x336699), new(cpu, new(480, 80, 240, 145))],
                        [new(psu, f.Ports["PSU/Power"], DiagramPortSide.Right, 40), new(cpu, f.Ports["CPU/Power"], DiagramPortSide.Left, 40),
                         new(psu, f.Ports["PSU/Telemetry"], DiagramPortSide.Bottom, 120.5m), new(cpu, f.Ports["CPU/Telemetry"], DiagramPortSide.Top, 0)],
                        [new(f.Links["System/Power"].ConnectionId, 1, [new(400, 120), new(450, 120)], new(420, 110), Locked: true)]) } };
            if (block == psu)
                return r with { Diagram = local with {
                    Presentation = new([new(power, new(101.6m, 101.6m, 203.2m, 152.4m)), new(telemetry, new(400, 100, 240, 145), FillRgb: 0),
                            new(regulator, new(-50.25m, -12.125m, 10, 10))],
                        [new(psu, f.Ports["PSU/Power"], DiagramPortSide.Left, 400), new(power, f.Ports["Power stage/Output"], DiagramPortSide.Right, 76.2m)],
                        [new(f.Links["PSU/Supply"].ConnectionId, 1, [], null)], new(0, 0, 1200, 800)),
                    InterfaceRealizations = [new(f.Ports["PSU/Power"], DiagramRealizationState.Partial,
                        [InterfaceRealizationTarget.LocalConnection(f.Links["PSU/Supply"].ConnectionId),
                         InterfaceRealizationTarget.ChildInterface(power, f.Ports["Power stage/Output"])],
                        "Second rail not yet decided.", [new("brief", "rev1", 1, null, null)])] } };
            if (block == power)
                return r with { ComponentBindings = new([new(design, circuit, component)]), Diagram = local with { InterfaceRealizations =
                    [new(f.Ports["Power stage/Output"], DiagramRealizationState.Resolved, [InterfaceRealizationTarget.ExactPin(pin)], null, [])] } };
            if (block == cpu)
                return r with { Diagram = local with { InterfaceRealizations =
                    [new(f.Ports["CPU/Telemetry"], DiagramRealizationState.Unknown, [], "Not yet decided.", [])] } };
            return r.Diagram is null ? r : r with { Diagram = local };
        }
        InterconnectSegment Segment(Guid id, InterconnectSegmentKind kind, string? label = null, Guid? designId = null, Guid? circuitId = null,
            Guid? netId = null, Guid? componentId = null, ImmutableArray<DiagramPinTarget> pins = default, Guid? target = null, Guid? hardware = null,
            string? reference = null, string? path = null, string? reason = null) =>
            new(id, kind, label, designId, circuitId, netId, componentId, pins.IsDefault ? [] : pins, target, hardware, reference, path, reason);
        Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid(), s3 = Guid.NewGuid(), s4 = Guid.NewGuid(), s5 = Guid.NewGuid();
        var resolved = new InterconnectRealization(DiagramRealizationState.Resolved,
            [Segment(s1, InterconnectSegmentKind.BoardNet, "+5V", design, circuit, net),
             Segment(s2, InterconnectSegmentKind.Connector, "J1", design, circuit, componentId: connector,
                pins: [new(design, connector, [sheet], "1"), new(design, connector, [sheet], "2")]),
             Segment(s3, InterconnectSegmentKind.Harness, "W1", target: harness),
             Segment(s4, InterconnectSegmentKind.HardwareInterface, hardware: hardwareInterface),
             Segment(s5, InterconnectSegmentKind.External, "Bench supply", reference: "PSU-01", path: "docs/bench-supply.md")],
            [new(s1, s2), new(s2, s3), new(s3, s4), new(s4, s5)], null, [new("harness-drawing", "B", 2, "Table 1", null)]);
        var partial = new InterconnectRealization(DiagramRealizationState.Partial,
            [Segment(Guid.NewGuid(), InterconnectSegmentKind.Connector, "Memory header", design, circuit, componentId: connector,
                reason: "Header pins are not chosen yet.")], [], "Only the header is known.", []);
        DiagramConnectionRevision Link(DiagramConnectionRevision r) =>
            r.Selection == f.Links["System/Power"] ? r with { Domain = DiagramDomain.Power, Direction = DiagramConnectionDirection.FromFirst, Realization = resolved }
            : r.Selection == f.Links["System/Telemetry"] ? r with { Domain = DiagramDomain.Data, Direction = DiagramConnectionDirection.Bidirectional,
                Realization = new(DiagramRealizationState.Unknown, [], [], "The telemetry cable is not chosen.", []) }
            : r.Selection == f.Links["CPU/Memory"] ? r with { Direction = DiagramConnectionDirection.ToFirst, Realization = partial }
            : r;
        var graph = new RecursiveBlockGraph(g.DocumentId, g.SelectedRoot, g.States, g.Revisions.Select(Change), g.RequirementHistories,
            g.ConnectionArchives.Select(a => new DiagramConnectionArchive(a.DocumentId, a.OwnerBlockId, a.States, a.Revisions.Select(Link), a.RequirementHistories)));
        return new(graph, f, harness, design, circuit, connector, component, pin);
    }
}

[TestClass]
public sealed class RecursiveBlockGraphV2XmlTests
{
    private static readonly XNamespace Ns = RecursiveBlockGraphXml.Namespace;

    [TestMethod]
    public void SchemaTwoDocumentsRoundTripByteForByteThroughXmlAndProtocol()
    {
        var f = SchemaTwoFixture.Create(); var graph = f.Graph;
        Assert.AreEqual(2, RecursiveBlockGraphXml.RequiredSchemaVersion(graph));
        string xml = RecursiveBlockGraphXml.Write(graph);
        var root = XElement.Parse(xml);
        Assert.AreEqual(Ns + "recursive-block-graph", root.Name); Assert.AreEqual("2", (string?)root.Attribute("version"));
        Assert.IsTrue(root.Descendants(XName.Get("connection-archive", DiagramConnectionArchiveXml.Namespace)).Any());
        Assert.IsFalse(root.Descendants().Any(e => e.Name.NamespaceName is RecursiveBlockGraphXml.NamespaceV1 or DiagramConnectionArchiveXml.NamespaceV1));
        var (loaded, version) = RecursiveBlockGraphXml.ReadVersioned(xml);
        Assert.AreEqual(2, version);
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(loaded), "R5: a canonical schema 2 document is stored byte for byte.");
        var message = RecursiveBlockCodec.Encode(loaded);
        Assert.AreEqual(2U, message.SchemaVersion);
        var decoded = RecursiveBlockCodec.Decode(P.RecursiveBlockGraphData.Parser.ParseFrom(message.ToByteArray()));
        Assert.AreEqual(xml, RecursiveBlockGraphXml.Write(decoded), "The shared protocol keeps every schema 2 fact.");
        foreach (var revision in graph.Revisions)
        {
            var copy = decoded.Inspect(revision.Selection);
            Assert.IsTrue(revision.LocalDiagram.SameContents(copy.LocalDiagram), revision.Name);
            Assert.AreEqual(revision.PhysicalAllocation?.Targets.FirstOrDefault()?.Kind, copy.PhysicalAllocation?.Targets.FirstOrDefault()?.Kind);
        }
        var link = f.Linked.Links["System/Power"];
        var realization = decoded.Connections(graph.SelectedRoot.BlockId).Inspect(link).Realization!;
        Assert.IsTrue(realization.SameContents(graph.Connections(graph.SelectedRoot.BlockId).Inspect(link).Realization));
        Assert.AreEqual(DiagramConnectionDirection.FromFirst, decoded.Connections(graph.SelectedRoot.BlockId).Inspect(link).Direction);
        Assert.HasCount(5, realization.SegmentList); Assert.HasCount(4, realization.JoinList);
        var psu = decoded.Inspect(f.Linked.Blocks["PSU"]);
        Assert.AreEqual(new DiagramRect(0, 0, 1200, 800), psu.LocalDiagram.Layout.Frame);
        Assert.AreEqual(new DiagramRect(101.6m, 101.6m, 203.2m, 152.4m), psu.LocalDiagram.Layout.Blocks[0].Rect);
        Assert.AreEqual(DiagramRealizationState.Partial, psu.LocalDiagram.Realizations.Single().State);
        // Document text is canonical: decimals, trimmed zeros, ports and routes in key order, flags only when set.
        var psuXml = root.Descendants(Ns + "revision").Single(r => (string?)r.Attribute("id") == f.Linked.Blocks["PSU"].RevisionId.ToString("D"));
        var view = psuXml.Descendants(Ns + "presentation-view").Single();
        Assert.AreEqual("diagram-unit", (string?)view.Attribute("units"));
        Assert.AreEqual("101.6", (string?)view.Elements(Ns + "block").First().Attribute("x"));
        Assert.AreEqual("-12.125", (string?)view.Elements(Ns + "block").Last().Attribute("y"));
        Assert.IsNull(view.Elements(Ns + "block").First().Attribute("locked"));
        Assert.AreEqual("0", (string?)view.Elements(Ns + "block").ElementAt(1).Attribute("fill-rgb"));
        CollectionAssert.AreEqual(view.Elements(Ns + "port").Select(p => (string)p.Attribute("block")! + ":" + (string)p.Attribute("interface")!).Order(StringComparer.Ordinal).ToArray(),
            view.Elements(Ns + "port").Select(p => (string)p.Attribute("block")! + ":" + (string)p.Attribute("interface")!).ToArray());
        var interfaceXml = root.Descendants(Ns + "interface").Single(i => (string?)i.Attribute("id") == f.Linked.Ports["PSU/Power"].ToString("D")
            && i.Parent!.Parent!.Parent!.Attribute("id")!.Value == f.Linked.Blocks["PSU"].RevisionId.ToString("D"));
        Assert.AreEqual("Power", (string?)interfaceXml.Attribute("domain")); Assert.AreEqual("Output", (string?)interfaceXml.Attribute("direction"));
        Assert.IsFalse(xml.Contains("\"Unspecified\"", StringComparison.Ordinal), "Unspecified is omitted, never written.");
    }

    [TestMethod]
    public void VersionOneDocumentsReadNeutrallyAndTheFileFormatStoresEveryVersionOneFactAsSchemaTwo()
    {
        var linked = LinkedDiagramFixture.Create(); var graph = linked.Graph;
        string v1 = RecursiveBlockGraphXml.Write(graph, 1);
        Assert.AreEqual(1, RecursiveBlockGraphXml.RequiredSchemaVersion(graph));
        // Only the explicit version 1 writer (used by the frozen PSU-CPU fixture) keeps version 1 text for a graph
        // without schema 2 facts; the plain writer emits schema 2 like every diagram file write (R4).
        Assert.AreEqual(XName.Get("recursive-block-graph", RecursiveBlockGraphXml.NamespaceV1), XElement.Parse(v1).Name);
        Assert.AreEqual(Ns + "recursive-block-graph", XElement.Parse(RecursiveBlockGraphXml.Write(graph)).Name);
        var (read, version) = RecursiveBlockGraphXml.ReadVersioned(v1);
        Assert.AreEqual(1, version); Assert.AreEqual(v1, RecursiveBlockGraphXml.Write(read, 1));
        Assert.AreEqual(v1, RecursiveBlockGraphXml.Write(RecursiveBlockCodec.Decode(RecursiveBlockCodec.Encode(read)), 1));
        // R2: a version 1 document reads with neutral schema 2 values.
        foreach (var revision in read.Revisions)
        {
            Assert.IsNull(revision.LocalDiagram.Presentation); Assert.IsEmpty(revision.LocalDiagram.Realizations);
            Assert.IsTrue(revision.LocalDiagram.Interfaces.All(i => i.Domain == DiagramDomain.Unspecified && i.Direction == DiagramInterfaceDirection.Unspecified));
        }
        Assert.IsTrue(read.ConnectionArchives.SelectMany(a => a.Revisions).All(r => !r.UsesSchemaTwo));
        // R4: every diagram file write uses schema 2, also for a change version 1 could hold; the archives move with the document.
        var root = read.StartDraft(read.SelectedRoot);
        root = root with { Requirements = root.Requirements.Edit(DiagramRequirementField.Routing, "Keep the supply short.") };
        var requirementOnly = read.SaveDraft(read.SelectedRoot, [read.SelectedRoot], root, Guid.NewGuid(), Guid.NewGuid(), [], RecursiveBlockFixture.Origin()).Graph;
        string v2 = RecursiveBlockGraphXml.Write(requirementOnly, RecursiveBlockGraphXml.SchemaVersion);
        var stored = XElement.Parse(v2);
        Assert.AreEqual(Ns + "recursive-block-graph", stored.Name); Assert.AreEqual("2", (string?)stored.Attribute("version"));
        Assert.IsTrue(stored.Descendants(XName.Get("connection-archive", DiagramConnectionArchiveXml.Namespace)).Any());
        Assert.IsFalse(stored.Descendants().Any(e => e.Name.NamespaceName is RecursiveBlockGraphXml.NamespaceV1 or DiagramConnectionArchiveXml.NamespaceV1));
        var (reread, storedVersion) = RecursiveBlockGraphXml.ReadVersioned(v2);
        Assert.AreEqual(2, storedVersion);
        Assert.AreEqual(v2, RecursiveBlockGraphXml.Write(reread, RecursiveBlockGraphXml.SchemaVersion), "R5: stored schema 2 text is canonical.");
        foreach (var revision in requirementOnly.Revisions)
        {
            var kept = reread.Inspect(revision.Selection);
            Assert.AreEqual(revision.Name, kept.Name); CollectionAssert.AreEqual(revision.Children.ToArray(), kept.Children.ToArray());
            Assert.IsTrue(revision.LocalDiagram.SameContents(kept.LocalDiagram)); Assert.AreEqual(revision.RequirementRevisionId, kept.RequirementRevisionId);
            Assert.AreEqual(requirementOnly.Requirements(revision.Selection).Requirements, reread.Requirements(revision.Selection).Requirements);
        }
        foreach (var archive in requirementOnly.ConnectionArchives)
            Assert.IsTrue(reread.Connections(archive.OwnerBlockId).Retains(archive));
        Assert.AreEqual(requirementOnly.Revisions.Length, reread.Revisions.Length, "The upgrade itself adds no revision.");
        Assert.AreEqual(read.Revisions.Length + 1, requirementOnly.Revisions.Length);
        // A layout, the first schema 2 fact, can only be written as schema 2.
        var layout = requirementOnly.StartDraft(requirementOnly.SelectedRoot);
        layout = layout with { Diagram = layout.LocalDiagram with { Presentation = new([new(linked.Blocks["PSU"].BlockId, new(10, 10, 240, 145))], [], []) } };
        var laidOut = requirementOnly.SaveDraft(requirementOnly.SelectedRoot, [requirementOnly.SelectedRoot], layout, Guid.NewGuid(), Guid.NewGuid(), [],
            RecursiveBlockFixture.Origin()).Graph;
        Assert.AreEqual(2, RecursiveBlockGraphXml.RequiredSchemaVersion(laidOut));
        Assert.AreEqual(RecursiveBlockGraphXml.Write(laidOut, RecursiveBlockGraphXml.SchemaVersion), RecursiveBlockGraphXml.Write(laidOut));
    }

    [TestMethod]
    public void FrozenVersionOneSchemasAreByteIdenticalAndCannotDescribeSchemaTwoContent()
    {
        var assembly = typeof(RecursiveBlockGraphXml).Assembly;
        string Hash(string name)
        {
            using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal)))!;
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        Assert.AreEqual("fb11ab28f08f16d525ca093e0f1475344697b5793af41c51b136dfa6c7453100", Hash("recursive-block-graph-v1.xsd"));
        Assert.AreEqual("b6f3d4ac1014ebabbf7f6cf03bc1c3497e9128e8d445273744dcb0ae7bfce4df", Hash("connection-archive-v1.xsd"));
        // A schema 2 document relabelled as version 1 is refused: version 1 has no place for its facts.
        string v2 = RecursiveBlockGraphXml.Write(SchemaTwoFixture.Create().Graph);
        string relabelled = v2.Replace(RecursiveBlockGraphXml.Namespace, RecursiveBlockGraphXml.NamespaceV1, StringComparison.Ordinal)
            .Replace(DiagramConnectionArchiveXml.Namespace, DiagramConnectionArchiveXml.NamespaceV1, StringComparison.Ordinal)
            .Replace("version=\"2\"", "version=\"1\"", StringComparison.Ordinal);
        Assert.AreEqual("invalid_recursive_block_graph_xml", Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.Read(relabelled)).Code);
        // The schema 1 protocol, which the native editor of this build speaks, cannot carry the same content.
        var graph = RecursiveBlockGraphXml.Read(v2);
        Assert.AreEqual("invalid_recursive_diagram_data", Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Encode(graph, 1)).Code);
        var data = RecursiveBlockCodec.Encode(graph); data.SchemaVersion = 1;
        Assert.AreEqual("invalid_recursive_diagram_data", Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockCodec.Decode(data)).Code);
    }

    [TestMethod]
    [DataRow("urn:kicad:automation:recursive-block-graph:3", "diagram_schema_too_new")]
    [DataRow("urn:kicad:automation:recursive-block-graph:17", "diagram_schema_too_new")]
    [DataRow("urn:kicad:automation:recursive-block-graph:0", "invalid_recursive_block_graph_xml")]
    [DataRow("urn:kicad:automation:recursive-block-graph:02", "invalid_recursive_block_graph_xml")]
    [DataRow("urn:example:other:2", "invalid_recursive_block_graph_xml")]
    public void NewerOrForeignDocumentsFailBeforeAnythingIsRead(string rootNamespace, string code)
    {
        string xml = RecursiveBlockGraphXml.Write(SchemaTwoFixture.Create().Graph)
            .Replace("xmlns=\"" + RecursiveBlockGraphXml.Namespace + "\"", "xmlns=\"" + rootNamespace + "\"", StringComparison.Ordinal);
        var error = Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.ReadVersioned(xml));
        Assert.AreEqual(code, error.Code, error.Message);
    }

    [TestMethod]
    public void LayoutTextAndAttributesAreStrictAndCanonical()
    {
        var f = SchemaTwoFixture.Create(); string xml = RecursiveBlockGraphXml.Write(f.Graph);
        string psuRevision = f.Linked.Blocks["PSU"].RevisionId.ToString("D");
        void Reject(string name, Action<XElement> change, string code = "invalid_recursive_block_graph_xml")
        {
            var root = XElement.Parse(xml, LoadOptions.PreserveWhitespace); change(root);
            var error = Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.Read(root.ToString(SaveOptions.DisableFormatting)), name);
            Assert.AreEqual(code, error.Code, name + ": " + error.Message);
        }
        XElement View(XElement root) => root.Descendants(Ns + "revision").Single(r => (string?)r.Attribute("id") == psuRevision).Descendants(Ns + "presentation-view").Single();
        foreach (string text in new[] { "1.0", "-0", "01", "1e3", "+1", "0.0001", "1.2345", "1.", ".5", " 1", "1,5" })
            Reject("x=" + text, r => View(r).Elements(Ns + "block").First().SetAttributeValue("x", text));
        Reject("out of range", r => View(r).Elements(Ns + "block").First().SetAttributeValue("x", "9999999999"), "invalid_diagram_coordinate");
        Reject("empty width", r => View(r).Elements(Ns + "block").First().SetAttributeValue("width", "0"), "invalid_diagram_coordinate");
        Reject("offset beyond the side", r => View(r).Elements(Ns + "port").First().SetAttributeValue("offset", "900"), "invalid_diagram_coordinate");
        Reject("locked=false", r => View(r).Elements(Ns + "block").First().SetAttributeValue("locked", "false"));
        Reject("locked=1", r => View(r).Elements(Ns + "block").First().SetAttributeValue("locked", "1"));
        Reject("fill beyond 24 bits", r => View(r).Elements(Ns + "block").First().SetAttributeValue("fill-rgb", "16777216"));
        Reject("endpoint zero", r => View(r).Elements(Ns + "route").First().SetAttributeValue("endpoint", "0"));
        Reject("units", r => View(r).SetAttributeValue("units", "mm"));
        Reject("unknown attribute", r => View(r).SetAttributeValue("zoom", "2"));
        Reject("unknown side", r => View(r).Elements(Ns + "port").First().SetAttributeValue("side", "Middle"));
        Reject("duplicate block", r => View(r).Elements(Ns + "block").First().AddAfterSelf(new XElement(View(r).Elements(Ns + "block").First())),
            "invalid_presentation_view");
        Reject("Unspecified written", r => r.Descendants(Ns + "interface").First().SetAttributeValue("domain", "Unspecified"));
        Reject("realization without reason", r => r.Descendants(Ns + "realization").First(e => (string?)e.Attribute("state") == "Partial")
            .Element(Ns + "unresolved-reason")!.Remove(), "invalid_interface_realization");
        Reject("segment without kind fields", r => r.Descendants(XName.Get("segment", DiagramConnectionArchiveXml.Namespace))
            .First(e => (string?)e.Attribute("kind") == "BoardNet").Attribute("net")!.Remove(), "invalid_interconnect_realization");
    }

    [TestMethod]
    public void FlatConversionReceiptsAreAlwaysRefusedBecauseFlatDiagramsAreNotConverted()
    {
        // Owner decision n9af098253fec71da: legacy flat diagrams are discarded, not converted, so no build
        // writes a receipt and a file claiming one is refused instead of being silently dropped on save.
        var f = SchemaTwoFixture.Create(); var graph = f.Graph;
        var root = XElement.Parse(RecursiveBlockGraphXml.Write(graph), LoadOptions.PreserveWhitespace);
        string op = Guid.NewGuid().ToString("D"), sha = new('a', 64);
        root.Add(new XElement(Ns + "migration", new XAttribute("id", op), new XAttribute("source-path", "flat.engineering.xml"),
            new XAttribute("source-sha256", sha), new XAttribute("source-structure", Guid.NewGuid().ToString("D")),
            new XAttribute("source-circuit", Guid.NewGuid().ToString("D")), new XAttribute("source-envelope", "EngineeringDesign"),
            new XAttribute("converter", "kicad-structural-migration/1"), new XAttribute("nanometres-per-diagram-unit", "100000"),
            new XAttribute("design-resolution", "NoManifestSupplied"),
            new XElement(Ns + "root", new XAttribute("block", graph.SelectedRoot.BlockId.ToString("D")),
                new XAttribute("state", graph.SelectedRoot.StateId.ToString("D")), new XAttribute("revision", graph.SelectedRoot.RevisionId.ToString("D"))),
            new XElement(Ns + "origin", new XAttribute("actor-kind", "Import"), new XAttribute("actor", "Test-only converter"),
                new XAttribute("at", "2026-09-23T00:00:00.0000000+00:00"), new XElement(Ns + "summary", "Test-only receipt"),
                new XElement(Ns + "input", new XAttribute("ref", op))),
            new XElement(Ns + "items"), new XElement(Ns + "retained-guidance"),
            new XElement(Ns + "source-structure", new XAttribute("sha256", sha), "<structure/>")));
        var set = RecursiveBlockGraphXml.CreateSchemaSet();
        new XDocument(root).Validate(set, null); // The receipt itself is valid schema 2 text...
        var error = Assert.ThrowsExactly<AutomationException>(() => RecursiveBlockGraphXml.Read(root.ToString(SaveOptions.DisableFormatting)));
        Assert.AreEqual("invalid_recursive_block_graph_xml", error.Code); // ...but no build keeps one, so the file is refused.
        StringAssert.Contains(error.Message, "conversion receipt");
    }
}
