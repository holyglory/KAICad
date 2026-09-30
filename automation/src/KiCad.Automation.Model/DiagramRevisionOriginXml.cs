using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace KiCad.Automation.Model;

internal static class DiagramRevisionOriginXml
{
    internal static XElement Write(XNamespace ns, RequirementRevisionOrigin origin) => new(ns + "origin",
        new XAttribute("actor-kind", origin.ActorKind), new XAttribute("actor", origin.Actor),
        new XAttribute("at", origin.RecordedAt.ToString("O", CultureInfo.InvariantCulture)), new XElement(ns + "summary", origin.Summary),
        origin.Sources.Select(s => Source(ns, s)), origin.InputIds.Select(id => new XElement(ns + "input", new XAttribute("ref", id.ToString("D")))));

    internal static XElement Source(XNamespace ns, SourceReference source) => new(ns + "source",
        new XAttribute("document", source.DocumentId), new XAttribute("revision", source.Revision),
        source.Page is { } page ? new XAttribute("page", page) : null,
        source.Table is { } table ? new XAttribute("table", table) : null,
        source.PartVariant is { } variant ? new XAttribute("part-variant", variant) : null);

    internal static SourceReference ReadSource(XElement source) => new((string)source.Attribute("document")!,
        (string)source.Attribute("revision")!, source.Attribute("page") is { } page
            ? int.Parse(page.Value, NumberStyles.None, CultureInfo.InvariantCulture) : null,
        (string?)source.Attribute("table"), (string?)source.Attribute("part-variant"));

    internal static RequirementRevisionOrigin Read(XElement origin)
    {
        XNamespace ns = origin.Name.Namespace;
        return new(Enum.Parse<RequirementRevisionActor>((string)origin.Attribute("actor-kind")!),
            (string)origin.Attribute("actor")!, XmlConvert.ToDateTimeOffset((string)origin.Attribute("at")!),
            origin.Element(ns + "summary")!.Value, origin.Elements(ns + "source").Select(ReadSource).ToImmutableArray(),
            origin.Elements(ns + "input").Select(i => Guid.ParseExact((string)i.Attribute("ref")!, "D")).ToImmutableArray());
    }
}
