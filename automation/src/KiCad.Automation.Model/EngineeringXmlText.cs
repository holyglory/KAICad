using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace KiCad.Automation.Model;

/// <summary>Preserve user-authored text when composing typed engineering XML sections.</summary>
internal static class EngineeringXmlText
{
    public static string Render(XElement root)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
            { OmitXmlDeclaration = true, Indent = true, NewLineChars = "\n", NewLineHandling = NewLineHandling.Entitize }))
            root.WriteTo(writer);
        return output.ToString() + "\n";
    }

    // SA-05: source references are evidence, not implicit fetches or executable instructions.
    public static XElement Parse(string xml)
    {
        if (xml.Length > 0 && xml[0] == '\uFEFF') xml = xml[1..];
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo).Root
            ?? throw EngineeringDesign.Invalid("An engineering XML root is required.");
    }
}
