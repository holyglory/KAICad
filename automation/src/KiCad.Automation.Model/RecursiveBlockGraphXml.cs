using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace KiCad.Automation.Model;

/// <summary>Typed revision-graph storage. Children pin exact revisions and requirement
/// histories are explicit XML fields, never an embedded opaque native schematic.
/// Schema 2 (contract rbg-v2 section 2.2): readers accept versions 1 and 2 by root namespace.
/// Every write emits version 2 (R4): <see cref="Write(RecursiveBlockGraph)"/> and every diagram file
/// write, so a version 1 file keeps its exact bytes only until its first changed write. The one
/// version 1 output is the explicit <c>Write(graph, 1)</c> for a graph without schema 2 facts, used
/// only by the frozen PSU-CPU fixture, whose <c>system.blocks.xml</c> stays version 1 byte for byte
/// (owner clarification n4d3b2157f0a9f98d).</summary>
public static partial class RecursiveBlockGraphXml
{
    public const string Namespace = "urn:kicad:automation:recursive-block-graph:2";
    public const string NamespaceV1 = "urn:kicad:automation:recursive-block-graph:1";
    /// <summary>The newest schema this build reads and writes.</summary>
    public const int SchemaVersion = 2;
    private const string NamespacePrefix = "urn:kicad:automation:recursive-block-graph:";
    private static readonly Lazy<XmlSchemaSet> Schema = new(CreateSchemaSet);

    [GeneratedRegex("^urn:kicad:automation:recursive-block-graph:(?<version>[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionedNamespace();

    /// <summary>Both schema versions and their imports; a document validates against the
    /// version named by its root namespace.</summary>
    public static XmlSchemaSet CreateSchemaSet()
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        BlockDefinitionXml.AddSchema(set);
        DiagramRefinementInputXml.AddSchema(set);
        var assembly = typeof(RecursiveBlockGraphXml).Assembly;
        foreach (string name in new[] { "requirement-history-v1.xsd", "connection-archive-v1.xsd", "recursive-block-graph-v1.xsd",
            "connection-archive-v2.xsd", "recursive-block-graph-v2.xsd" })
        {
            string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            set.Add(null, reader);
        }
        set.Compile(); return set;
    }

    /// <summary>1 unless some revision or connection states a schema 2 fact: interface or connection
    /// domain or direction, a non-empty layout, a realization or a Harness physical target.</summary>
    public static int RequiredSchemaVersion(RecursiveBlockGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Revisions.Any(r => r.LocalDiagram.UsesSchemaTwo
                || r.PhysicalAllocation?.Targets.Any(t => t.Kind == PhysicalAllocationKind.Harness) == true)
            || graph.ConnectionArchives.Any(a => DiagramConnectionArchiveXml.RequiredSchemaVersion(a) > 1) ? 2 : 1;
    }

    /// <summary>Canonical schema 2 text (R4), so <c>Write(Read(x)) == x</c> for every canonical version 2
    /// file (R5).</summary>
    public static string Write(RecursiveBlockGraph graph) => Write(graph, SchemaVersion);

    /// <summary>Writes at least <paramref name="minimumSchemaVersion"/>. Diagram writers use schema 2;
    /// <c>1</c> keeps a graph without schema 2 facts in version 1 and is used only by the frozen PSU-CPU
    /// fixture (see the class summary).</summary>
    public static string Write(RecursiveBlockGraph graph, int minimumSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (minimumSchemaVersion is < 1 or > SchemaVersion) throw new ArgumentOutOfRangeException(nameof(minimumSchemaVersion));
        int version = Math.Max(minimumSchemaVersion, RequiredSchemaVersion(graph));
        var root = new Format(version).Write(graph);
        new XDocument(root).Validate(Schema.Value, null);
        return EngineeringXmlText.Render(root);
    }

    public static RecursiveBlockGraph Read(string xml) => ReadVersioned(xml).Graph;

    /// <summary>R1: dispatches by root namespace. A newer recursive-block-graph version fails with
    /// diagram_schema_too_new; any other root fails as invalid XML. Nothing is written.</summary>
    public static (RecursiveBlockGraph Graph, int StoredSchemaVersion) ReadVersioned(string xml)
    {
        if (xml is null) throw Invalid("A recursive block graph document is required.");
        try
        {
            var root = EngineeringXmlText.Parse(xml);
            int version = root.Name.LocalName == "recursive-block-graph" && VersionedNamespace().Match(root.Name.NamespaceName) is { Success: true } match
                ? int.Parse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
            if (version > SchemaVersion)
                throw new AutomationException("diagram_schema_too_new",
                    $"This diagram uses recursive-block-graph schema {version}; this build reads versions 1 and 2. Nothing was changed.");
            if (version == 0) throw Invalid("Use the supported recursive block graph root and namespace.");
            var format = new Format(version);
            // Legacy flat diagrams are discarded, never converted (owner decision n9af098253fec71da): no build writes a
            // conversion receipt and schema 2 no longer declares one. A file still claiming one is refused, never silently
            // dropped, and is named before schema validation so the refusal says why.
            if (root.Element(format.Ns + "migration") is not null)
                throw Invalid("This diagram carries a flat-diagram conversion receipt; flat diagrams are not converted, so this file is not supported and nothing was changed.");
            new XDocument(root).Validate(Schema.Value, null);
            return (format.Read(root), version);
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException)
        { throw Invalid("Invalid recursive block graph XML: " + error.Message); }
    }

    private static AutomationException Invalid(string message) => new("invalid_recursive_block_graph_xml", message);

    /// <summary>One schema version's element names. Version 1 content is written by exactly the
    /// same code in both versions, so a version 1 document round trips byte for byte.</summary>
    private sealed class Format(int version)
    {
        public XNamespace Ns { get; } = NamespacePrefix + version.ToString(CultureInfo.InvariantCulture);

        public XElement Write(RecursiveBlockGraph graph) => new(Ns + "recursive-block-graph", new XAttribute("version", version), Attr("document", graph.DocumentId),
            Selection("selected-root", graph.SelectedRoot),
            new XElement(Ns + "states", graph.States.OrderBy(s => s.Id).Select(s => new XElement(Ns + "state",
                Attr("id", s.Id), Attr("block", s.BlockId), new XAttribute("name", s.Name), Attr("head", s.HeadRevisionId),
                s.Archived ? new XAttribute("archived", true) : null,
                s.ForkedFrom is { } source ? Selection("forked-from", source) : null))),
            new XElement(Ns + "revisions", graph.Revisions.OrderBy(r => r.Selection.RevisionId).Select(r =>
                new XElement(Ns + "revision", Attr("id", r.Selection.RevisionId), Attr("block", r.Selection.BlockId), Attr("state", r.Selection.StateId),
                    r.ParentRevisionId is { } parent ? Attr("parent", parent) : null, new XAttribute("name", r.Name),
                    Attr("requirements", r.RequirementRevisionId), WriteOrigin(r.Origin),
                    new XElement(Ns + "children", r.Children.Select(c => Selection("child", c))),
                    r.RestoredFrom is { } source ? Selection("restored-from", source) : null,
                    r.Diagram is { } diagram ? WriteLocal(diagram) : null,
                    r.Definition is { } definition ? EngineeringXmlText.Parse(BlockDefinitionXml.Write(definition)) : null,
                    r.ComponentBindings is { } bindings ? new XElement(Ns + "component-bindings", bindings.Targets.Select(t =>
                        new XElement(Ns + "component", Attr("design", t.DesignId), Attr("circuit", t.CircuitId), Attr("id", t.ComponentId)))) : null,
                    r.PhysicalAllocation is { } allocation ? WritePhysicalAllocation(allocation) : null))),
            new XElement(Ns + "requirement-histories", graph.RequirementHistories.OrderBy(h => h.Scope.DesignStateId)
                .Select(h => EngineeringXmlText.Parse(DiagramRequirementHistoryXml.Write(h)))),
            graph.ConnectionArchives.IsEmpty ? null : new XElement(Ns + "connection-archives", graph.ConnectionArchives.OrderBy(a => a.OwnerBlockId)
                .Select(a => EngineeringXmlText.Parse(DiagramConnectionArchiveXml.Write(a, version)))),
            graph.ImplementationChanges.IsEmpty ? null : new XElement(Ns + "implementation-changes", graph.ImplementationChanges.Select(c =>
                new XElement(Ns + "change", Attr("id", c.Id), Attr("state", c.StateId), new XAttribute("kind", c.Kind),
                    new XAttribute("before-name", c.BeforeName), new XAttribute("after-name", c.AfterName),
                    new XAttribute("before-archived", c.BeforeArchived), new XAttribute("after-archived", c.AfterArchived), DiagramRevisionOriginXml.Write(Ns, c.Origin)))),
            graph.RefinementInputs.IsEmpty ? null : new XElement(Ns + "refinement-inputs", graph.RefinementInputs.Select(i =>
                EngineeringXmlText.Parse(DiagramRefinementInputXml.Write(i)))),
            graph.Proposals.IsEmpty ? null : new XElement(Ns + "proposals", graph.Proposals.Select(p =>
                new XElement(Ns + "proposal", Attr("id", p.Id), Attr("input", p.InputId), new XAttribute("request-sha256", p.RequestSha256),
                    new XElement(Ns + "base-path", p.BasePath.Select(s => Selection("block", s))), Selection("candidate", p.Candidate),
                    WriteOrigin(p.Origin), new XElement(Ns + "issues", p.Issues.Select(i => new XElement(Ns + "issue", Attr("id", i.Id),
                        new XAttribute("kind", i.Kind), i.TargetId is { } target ? Attr("target", target) : null,
                        new XElement(Ns + "message", i.Message), i.Sources.Select(s => DiagramRevisionOriginXml.Source(Ns, s)))))))));

        public RecursiveBlockGraph Read(XElement root)
        {
            var states = root.Element(Ns + "states")!.Elements(Ns + "state").Select(s =>
                new BlockDesignState(Id(s, "id"), Id(s, "block"), Text(s, "name"), Id(s, "head"),
                    s.Element(Ns + "forked-from") is { } source ? ReadSelection(source) : null, (bool?)s.Attribute("archived") ?? false));
            var revisions = root.Element(Ns + "revisions")!.Elements(Ns + "revision").Select(r =>
                new RecursiveBlockRevision(new(Id(r, "block"), Id(r, "state"), Id(r, "id")),
                    r.Attribute("parent") is null ? null : Id(r, "parent"), Text(r, "name"), Id(r, "requirements"),
                    r.Element(Ns + "children")!.Elements(Ns + "child").Select(ReadSelection).ToImmutableArray(),
                    ReadOrigin(r.Element(Ns + "origin")!), r.Element(Ns + "restored-from") is { } source ? ReadSelection(source) : null,
                    r.Element(Ns + "local-diagram") is { } diagram ? ReadLocal(diagram) : null,
                    r.Element(XName.Get("definition", BlockDefinitionXml.Namespace)) is { } definition
                        ? BlockDefinitionXml.Read(EngineeringXmlText.Render(definition)) : null,
                    r.Element(Ns + "component-bindings") is { } bindings ? new BlockComponentBindings(bindings.Elements(Ns + "component")
                        .Select(t => new ComponentRealization(Id(t, "design"), Id(t, "circuit"), Id(t, "id"))).ToImmutableArray()) : null,
                    r.Element(Ns + "physical-allocation") is { } allocation ? ReadPhysicalAllocation(allocation) : null));
            var histories = root.Element(Ns + "requirement-histories")!.Elements(XName.Get("requirement-history", DiagramRequirementHistoryXml.Namespace))
                .Select(h => DiagramRequirementHistoryXml.Read(EngineeringXmlText.Render(h)));
            XNamespace archiveNamespace = version == 2 ? DiagramConnectionArchiveXml.Namespace : DiagramConnectionArchiveXml.NamespaceV1;
            var archives = root.Element(Ns + "connection-archives")?.Elements(archiveNamespace + "connection-archive")
                .Select(a => DiagramConnectionArchiveXml.Read(EngineeringXmlText.Render(a))) ?? [];
            var changes = root.Element(Ns + "implementation-changes")?.Elements(Ns + "change").Select(c => new ImplementationChange(
                Id(c, "id"), Id(c, "state"), Enum.Parse<ImplementationChangeKind>(Text(c, "kind")), Text(c, "before-name"), Text(c, "after-name"),
                (bool)c.Attribute("before-archived")!, (bool)c.Attribute("after-archived")!, DiagramRevisionOriginXml.Read(c.Element(Ns + "origin")!))) ?? [];
            var inputs = root.Element(Ns + "refinement-inputs")?.Elements(XName.Get("refinement-input", DiagramRefinementInputXml.Namespace))
                .Select(i => DiagramRefinementInputXml.Read(EngineeringXmlText.Render(i))) ?? [];
            var proposals = root.Element(Ns + "proposals")?.Elements(Ns + "proposal").Select(p => new BlockProposalRecord(
                Id(p, "id"), Id(p, "input"), Text(p, "request-sha256"),
                p.Element(Ns + "base-path")!.Elements(Ns + "block").Select(ReadSelection).ToImmutableArray(),
                ReadSelection(p.Element(Ns + "candidate")!), p.Element(Ns + "issues")!.Elements(Ns + "issue").Select(i => new BlockProposalIssue(
                    Id(i, "id"), Enum.Parse<BlockProposalIssueKind>(Text(i, "kind")), i.Element(Ns + "message")!.Value,
                    i.Attribute("target") is null ? null : Id(i, "target"), i.Elements(Ns + "source").Select(DiagramRevisionOriginXml.ReadSource).ToImmutableArray())).ToImmutableArray(),
                ReadOrigin(p.Element(Ns + "origin")!))) ?? [];
            return new(Id(root, "document"), ReadSelection(root.Element(Ns + "selected-root")!), states, revisions, histories, archives, changes, inputs, proposals);
        }

        private XElement WriteOrigin(RequirementRevisionOrigin origin) => new(Ns + "origin",
            new XAttribute("actor-kind", origin.ActorKind), new XAttribute("actor", origin.Actor),
            new XAttribute("at", origin.RecordedAt.ToString("O", CultureInfo.InvariantCulture)), new XElement(Ns + "summary", origin.Summary),
            origin.Sources.Select(s => new XElement(Ns + "source", new XAttribute("document", s.DocumentId), new XAttribute("revision", s.Revision),
                s.Page is { } page ? new XAttribute("page", page) : null, s.Table is { } table ? new XAttribute("table", table) : null,
                s.PartVariant is { } variant ? new XAttribute("part-variant", variant) : null)),
            origin.InputIds.Select(id => new XElement(Ns + "input", Attr("ref", id))));

        private RequirementRevisionOrigin ReadOrigin(XElement origin) => new(
            Enum.Parse<RequirementRevisionActor>(Text(origin, "actor-kind")), Text(origin, "actor"), XmlConvert.ToDateTimeOffset(Text(origin, "at")),
            origin.Element(Ns + "summary")!.Value,
            origin.Elements(Ns + "source").Select(s => new SourceReference(Text(s, "document"), Text(s, "revision"),
                s.Attribute("page") is { } page ? int.Parse(page.Value, NumberStyles.None, CultureInfo.InvariantCulture) : null,
                (string?)s.Attribute("table"), (string?)s.Attribute("part-variant"))).ToImmutableArray(),
            origin.Elements(Ns + "input").Select(i => Id(i, "ref")).ToImmutableArray());

        private XElement Selection(string name, BlockSelection selection) => new(Ns + name,
            Attr("block", selection.BlockId), Attr("state", selection.StateId), Attr("revision", selection.RevisionId));
        private static BlockSelection ReadSelection(XElement e) => new(Id(e, "block"), Id(e, "state"), Id(e, "revision"));

        private XElement WriteLocal(BlockLocalDiagram diagram) => new(Ns + "local-diagram",
            new XElement(Ns + "interfaces", diagram.Interfaces.Select(i => new XElement(Ns + "interface", Attr("id", i.Id),
                new XAttribute("name", i.Name), i.Domain == DiagramDomain.Unspecified ? null : new XAttribute("domain", i.Domain),
                i.Direction == DiagramInterfaceDirection.Unspecified ? null : new XAttribute("direction", i.Direction),
                new XElement(Ns + "intent", i.Intent)))),
            new XElement(Ns + "connections", diagram.Connections.Select(c => new XElement(Ns + "connection",
                Attr("connection", c.ConnectionId), Attr("state", c.StateId), Attr("revision", c.RevisionId)))),
            diagram.Notes.IsEmpty ? null : new XElement(Ns + "annotations", diagram.Notes.Select(WriteNote)),
            diagram.Presentation is { IsEmpty: false } view ? WritePresentation(view) : null,
            diagram.Realizations.IsEmpty ? null : new XElement(Ns + "interface-realizations",
                InterfaceRealization.CanonicalList(diagram.Realizations).Select(WriteRealization)));

        private BlockLocalDiagram ReadLocal(XElement diagram) => new(
            diagram.Element(Ns + "interfaces")!.Elements(Ns + "interface").Select(i => new DiagramBoundaryInterface(
                Id(i, "id"), Text(i, "name"), i.Element(Ns + "intent")!.Value,
                i.Attribute("domain") is { } domain ? Enum.Parse<DiagramDomain>(domain.Value) : DiagramDomain.Unspecified,
                i.Attribute("direction") is { } direction ? Enum.Parse<DiagramInterfaceDirection>(direction.Value) : DiagramInterfaceDirection.Unspecified)).ToImmutableArray(),
            diagram.Element(Ns + "connections")!.Elements(Ns + "connection").Select(c => new ConnectionSelection(
                Id(c, "connection"), Id(c, "state"), Id(c, "revision"))).ToImmutableArray(),
            diagram.Element(Ns + "annotations")?.Elements(Ns + "annotation").Select(ReadNote).ToImmutableArray() ?? [],
            diagram.Element(Ns + "presentation-view") is { } view ? ReadPresentation(view) : null,
            diagram.Element(Ns + "interface-realizations") is { } realizations
                ? realizations.Elements(Ns + "realization").Select(ReadRealization).ToImmutableArray() : default);

        private XElement WritePresentation(DiagramPresentationView view)
        {
            view = view.Canonical();
            return new(Ns + "presentation-view", new XAttribute("units", "diagram-unit"),
                view.Frame is { } frame ? WriteRect("frame", frame) : null,
                view.Blocks.Select(b => new XElement(Ns + "block", Attr("ref", b.BlockId), RectAttributes(b.Rect),
                    b.Locked ? new XAttribute("locked", "true") : null,
                    b.FillRgb is { } fill ? new XAttribute("fill-rgb", fill.ToString(CultureInfo.InvariantCulture)) : null)),
                view.Ports.Select(p => new XElement(Ns + "port", Attr("block", p.BlockId), Attr("interface", p.InterfaceId),
                    new XAttribute("side", p.Side), new XAttribute("offset", DiagramCoordinates.Format(p.Offset)))),
                view.Routes.Select(r => new XElement(Ns + "route", Attr("connection", r.ConnectionId),
                    new XAttribute("endpoint", r.EndpointIndex.ToString(CultureInfo.InvariantCulture)), r.Locked ? new XAttribute("locked", "true") : null,
                    r.Points.Select(p => WritePoint2("point", p)), r.Label is { } label ? WritePoint2("label", label) : null)));
        }

        private DiagramPresentationView ReadPresentation(XElement view) => new(
            view.Elements(Ns + "block").Select(b => new DiagramBlockPlacement(Id(b, "ref"), ReadRect(b), b.Attribute("locked") is not null,
                b.Attribute("fill-rgb") is { } fill ? uint.Parse(fill.Value, NumberStyles.None, CultureInfo.InvariantCulture) : null)).ToImmutableArray(),
            view.Elements(Ns + "port").Select(p => new DiagramPortPlacement(Id(p, "block"), Id(p, "interface"),
                Enum.Parse<DiagramPortSide>(Text(p, "side")), DiagramCoordinates.ParseCanonical(Text(p, "offset")))).ToImmutableArray(),
            view.Elements(Ns + "route").Select(r => new DiagramConnectionRoute(Id(r, "connection"),
                int.Parse(Text(r, "endpoint"), NumberStyles.None, CultureInfo.InvariantCulture),
                r.Elements(Ns + "point").Select(ReadPoint2).ToImmutableArray(),
                r.Element(Ns + "label") is { } label ? ReadPoint2(label) : null, r.Attribute("locked") is not null)).ToImmutableArray(),
            view.Element(Ns + "frame") is { } frame ? ReadRect(frame) : null);

        private XElement WriteRect(string name, DiagramRect rect) => new(Ns + name, RectAttributes(rect));
        private static XAttribute[] RectAttributes(DiagramRect rect) => [new("x", DiagramCoordinates.Format(rect.X)),
            new("y", DiagramCoordinates.Format(rect.Y)), new("width", DiagramCoordinates.Format(rect.Width)), new("height", DiagramCoordinates.Format(rect.Height))];
        private static DiagramRect ReadRect(XElement e) => new(DiagramCoordinates.ParseCanonical(Text(e, "x")), DiagramCoordinates.ParseCanonical(Text(e, "y")),
            DiagramCoordinates.ParseCanonical(Text(e, "width")), DiagramCoordinates.ParseCanonical(Text(e, "height")));
        private XElement WritePoint2(string name, DiagramPoint point) => new(Ns + name,
            new XAttribute("x", DiagramCoordinates.Format(point.X)), new XAttribute("y", DiagramCoordinates.Format(point.Y)));
        private static DiagramPoint ReadPoint2(XElement e) => new(DiagramCoordinates.ParseCanonical(Text(e, "x")), DiagramCoordinates.ParseCanonical(Text(e, "y")));

        private XElement WriteRealization(InterfaceRealization record) => new(Ns + "realization", Attr("interface", record.InterfaceId),
            new XAttribute("state", record.State), record.UnresolvedReason is { } reason ? new XElement(Ns + "unresolved-reason", reason) : null,
            record.TargetList.Select(t => new XElement(Ns + "target", new XAttribute("kind", t.Kind),
                t.BlockId is { } block ? Attr("block", block) : null, t.InterfaceId is { } port ? Attr("interface", port) : null,
                t.ConnectionId is { } link ? Attr("connection", link) : null,
                t.Pin is { } pin ? DiagramConnectionArchiveXml.WritePin(Ns, "pin", pin) : null)),
            record.SourceList.Select(s => DiagramRevisionOriginXml.Source(Ns, s)));

        private InterfaceRealization ReadRealization(XElement record) => new(Id(record, "interface"),
            Enum.Parse<DiagramRealizationState>(Text(record, "state")),
            record.Elements(Ns + "target").Select(t => new InterfaceRealizationTarget(Enum.Parse<InterfaceRealizationTargetKind>(Text(t, "kind")),
                t.Attribute("block") is null ? null : Id(t, "block"), t.Attribute("interface") is null ? null : Id(t, "interface"),
                t.Attribute("connection") is null ? null : Id(t, "connection"),
                t.Element(Ns + "pin") is { } pin ? DiagramConnectionArchiveXml.ReadPin(Ns, pin) : null)).ToImmutableArray(),
            record.Element(Ns + "unresolved-reason")?.Value, record.Elements(Ns + "source").Select(DiagramRevisionOriginXml.ReadSource).ToImmutableArray());

        private XElement WritePhysicalAllocation(BlockPhysicalAllocation allocation)
        {
            allocation.Validate();
            return new XElement(Ns + "physical-allocation", new XAttribute("state", allocation.State),
                allocation.Targets.Select(t => new XElement(Ns + "target", Attr("id", t.Id), new XAttribute("kind", t.Kind),
                    new XAttribute("name", t.Name), t.Reference is { } reference ? new XAttribute("reference", reference) : null,
                    t.RepositoryPath is { } path ? new XAttribute("repository-path", path) : null,
                    t.ParentId is { } parent ? Attr("parent", parent) : null)),
                allocation.UnknownReason is { } reason ? new XElement(Ns + "unknown-reason", reason) : null);
        }

        private BlockPhysicalAllocation ReadPhysicalAllocation(XElement allocation)
        {
            var result = new BlockPhysicalAllocation(Enum.Parse<PhysicalAllocationState>(Text(allocation, "state")),
                allocation.Elements(Ns + "target").Select(t => new PhysicalAllocationTarget(Id(t, "id"),
                    Enum.Parse<PhysicalAllocationKind>(Text(t, "kind")), Text(t, "name"), (string?)t.Attribute("reference"),
                    (string?)t.Attribute("repository-path"), t.Attribute("parent") is { } parent ? Id(t, "parent") : null)).ToImmutableArray(),
                allocation.Element(Ns + "unknown-reason")?.Value);
            result.Validate(); return result;
        }

        private XElement WriteNote(DiagramAnnotation note) => new(Ns + "annotation", Attr("id", note.Id),
            new XAttribute("role", note.Role), new XAttribute("units", "diagram-unit"), DiagramRevisionOriginXml.Write(Ns, note.Origin),
            new XElement(Ns + "text", note.Text), new XElement(Ns + "target", new XAttribute("kind", note.Target.Kind),
                note.Target.TargetId is { } id ? Attr("ref", id) : null,
                note.Target.UnresolvedReason is { } reason ? new XElement(Ns + "unresolved-reason", reason) : null),
            note.Position is { } position ? WritePoint("position", position) : null,
            new XElement(Ns + "strokes", note.Strokes.Select(s => new XElement(Ns + "stroke", s.Points.Select(p => WritePoint("point", p))))));
        private XElement WritePoint(string name, DiagramAnnotationPoint point) => new(Ns + name,
            new XAttribute("x", point.X), new XAttribute("y", point.Y));

        private DiagramAnnotation ReadNote(XElement note)
        {
            var target = note.Element(Ns + "target")!;
            return new(Id(note, "id"), Enum.Parse<DiagramAnnotationRole>(Text(note, "role")), note.Element(Ns + "text")!.Value,
                new(Enum.Parse<DiagramAnnotationTargetKind>(Text(target, "kind")), target.Attribute("ref") is null ? null : Id(target, "ref"),
                    target.Element(Ns + "unresolved-reason")?.Value), note.Element(Ns + "position") is { } position ? ReadPoint(position) : null,
                note.Element(Ns + "strokes")!.Elements(Ns + "stroke").Select(s => new DiagramAnnotationStroke(
                    s.Elements(Ns + "point").Select(ReadPoint).ToImmutableArray())).ToImmutableArray(), DiagramRevisionOriginXml.Read(note.Element(Ns + "origin")!));
        }
        private static DiagramAnnotationPoint ReadPoint(XElement point) => DiagramAnnotationPoint.Parse(Text(point, "x"), Text(point, "y"));
    }

    private static string Text(XElement element, string name) => element.Attribute(name)!.Value;
    private static Guid Id(XElement element, string name) => Guid.ParseExact(Text(element, name), "D");
    private static XAttribute Attr(string name, Guid value) => new(name, value.ToString("D"));
}
