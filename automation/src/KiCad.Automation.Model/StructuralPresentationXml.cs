using System.Xml;
using System.Xml.Linq;

namespace KiCad.Automation.Model;

internal static class StructuralPresentationXml
{
    private static readonly XNamespace Ns = StructuralDiagramXml.Namespace;
    internal static StructuralPresentation Read(XElement root) => new(
        root.Element(Ns + "blocks")!.Elements(Ns + "block").Select(b => new StructuralBlockPlacement(Id(b),
            Number(b, "x-nm"), Number(b, "y-nm"), Number(b, "width-nm"), Number(b, "height-nm"), Flag(b, "locked"),
            b.Attribute("fill-rgb") is { } color ? XmlConvert.ToUInt32(color.Value) : null)).ToArray(),
        root.Element(Ns + "ports")!.Elements(Ns + "port").Select(p => new StructuralPortPlacement(Id(p),
            Enum.Parse<StructuralPortSide>(p.Attribute("side")!.Value), Number(p, "offset-nm"))).ToArray(),
        root.Element(Ns + "connections")!.Elements(Ns + "connection").Select(c => new StructuralConnectionPlacement(Id(c),
            c.Elements(Ns + "point").Select(Point).ToArray(), c.Element(Ns + "label") is { } label ? Point(label) : null,
            Flag(c, "locked"))).ToArray());

    internal static XElement Write(StructuralPresentation value) => E("presentation", A("coordinate-system", "structural-canvas"), A("units", "nm"),
        E("blocks", value.Blocks.OrderBy(b => b.BlockId).Select(b => E("block", A("ref", b.BlockId),
            A("x-nm", b.XNm), A("y-nm", b.YNm), A("width-nm", b.WidthNm), A("height-nm", b.HeightNm),
            b.Locked ? A("locked", true) : null, b.FillRgb is uint rgb ? A("fill-rgb", rgb) : null))),
        E("ports", value.Ports.OrderBy(p => p.PortId).Select(p => E("port", A("ref", p.PortId),
            A("side", p.Side), A("offset-nm", p.OffsetNm)))),
        E("connections", value.Connections.OrderBy(c => c.ConnectionId).Select(c => E("connection", A("ref", c.ConnectionId),
            c.Locked ? A("locked", true) : null, c.Waypoints.Select(p => Point("point", p)),
            c.Label is { } label ? Point("label", label) : null))));

    private static StructuralPoint Point(XElement point) => new(Number(point, "x-nm"), Number(point, "y-nm"));
    private static XElement Point(string name, StructuralPoint p) => E(name, A("x-nm", p.XNm), A("y-nm", p.YNm));
    private static Guid Id(XElement value) => Guid.ParseExact(value.Attribute("ref")!.Value, "D");
    private static long Number(XElement value, string name) => XmlConvert.ToInt64(value.Attribute(name)!.Value);
    private static bool Flag(XElement value, string name) => value.Attribute(name) is { } flag && XmlConvert.ToBoolean(flag.Value);
    private static XElement E(string name, params object?[] content) => new(Ns + name, content);
    private static XAttribute A(string name, object value) => new(name, value);
}
