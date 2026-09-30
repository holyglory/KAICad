using System.Collections;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

/// <summary>Typed native DTO XML, not yet a complete schematic-file snapshot.
/// Native serializer coverage must be established separately from DTO fidelity.</summary>
public static class SchematicDataXml
{
    public const string Namespace = "urn:kicad:automation:schematic-data:1";
    private static readonly XNamespace Ns = Namespace;
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly Dictionary<string, MessageDescriptor> Messages = Collect(SchematicText.Descriptor.File);
    private static readonly Lazy<XmlSchemaSet> Schema = new(() =>
    {
        var schema = new XmlSchemaSet { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(ExportSchema()), ReaderSettings());
        schema.Add(Namespace, reader); schema.Compile(); return schema;
    });

    public static string Write(IMessage message)
    {
        RequireRoot(message.Descriptor);
        var root = new XElement(Ns + "schematic-data", new XAttribute("version", "1"),
            WriteMessage(message, message.Descriptor.FullName));
        string xml;
        try { xml = Render(root); }
        catch (Exception error) when (error is XmlException or ArgumentException)
        { throw Invalid("Native text cannot be represented as XML: " + error.Message); }
        // Unknown protobuf fields, noncanonical Any type URLs and other data
        // not representable by this version must fail, never silently disappear.
        if (!message.Equals(Read(xml)))
            throw Invalid("Native data contains information not represented by this XML schema version.");
        return xml;
    }

    public static IMessage Read(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings());
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (document.Root?.Name != Ns + "schematic-data" || (string?)document.Root.Attribute("version") != "1")
                throw Invalid("Unsupported schematic-data XML envelope or version.");
            document.Validate(Schema.Value, null);
            XElement value = document.Root.Elements().Single();
            MessageDescriptor descriptor = Messages[value.Name.LocalName];
            RequireRoot(descriptor);
            return ReadMessage(value, descriptor);
        }
        catch (Exception error) when (error is XmlException or XmlSchemaException or FormatException or OverflowException
                                     or InvalidProtocolBufferException or ArgumentException)
        {
            throw Invalid("Invalid schematic data XML: " + error.Message);
        }
    }

    private static XElement WriteMessage(IMessage message, string name)
    {
        var node = new XElement(Ns + name);
        if (message is Any any)
        {
            string type = any.TypeUrl[(any.TypeUrl.LastIndexOf('/') + 1)..];
            if (!Messages.TryGetValue(type, out var descriptor) || descriptor.IsMapEntry || descriptor == Any.Descriptor)
                throw Invalid("Unsupported embedded native message: " + type);
            node.Add(WriteMessage(descriptor.Parser.ParseFrom(any.Value), type));
            return node;
        }
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            object value = field.Accessor.GetValue(message);
            if (field.IsMap)
            {
                var map = (IDictionary)value;
                foreach (object key in map.Keys.Cast<object>().OrderBy(k => ScalarText(field.MessageType.Fields[1], k), StringComparer.Ordinal))
                    node.Add(new XElement(Ns + field.Name, WriteValue(field.MessageType.Fields[1], key),
                        WriteValue(field.MessageType.Fields[2], map[key]!)));
            }
            else if (field.IsRepeated)
            {
                foreach (object item in (IList)value) node.Add(WriteValue(field, item));
            }
            else if (field.HasPresence ? field.Accessor.HasValue(message) : !IsDefault(field, value))
                node.Add(WriteValue(field, value));
        }
        return node;
    }

    private static XElement WriteValue(FieldDescriptor field, object value) => field.FieldType == FieldType.Message
        ? WriteMessage((IMessage)value, field.Name) : new XElement(Ns + field.Name, ScalarText(field, value));

    private static IMessage ReadMessage(XElement node, MessageDescriptor descriptor)
    {
        if (descriptor == Any.Descriptor)
        {
            var child = node.Elements().Single();
            return Any.Pack(ReadMessage(child, Messages[child.Name.LocalName]));
        }
        var message = descriptor.Parser.ParseFrom(Array.Empty<byte>());
        var oneofs = new HashSet<OneofDescriptor>();
        foreach (var group in node.Elements().GroupBy(e => e.Name.LocalName))
        {
            var field = descriptor.FindFieldByName(group.Key) ?? throw Invalid("Unknown native field: " + group.Key);
            if (field.RealContainingOneof is { } oneof && !oneofs.Add(oneof))
                throw Invalid("Conflicting native union fields: " + oneof.Name);
            if (field.IsMap)
            {
                var map = (IDictionary)field.Accessor.GetValue(message);
                foreach (var entry in group)
                {
                    var keyField = field.MessageType.Fields[1]; var valueField = field.MessageType.Fields[2];
                    object key = ReadValue(entry.Element(Ns + keyField.Name)!, keyField);
                    if (map.Contains(key)) throw Invalid("Duplicate native map key.");
                    map.Add(key, ReadValue(entry.Element(Ns + valueField.Name)!, valueField));
                }
            }
            else if (field.IsRepeated)
            {
                var list = (IList)field.Accessor.GetValue(message);
                foreach (var entry in group) list.Add(ReadValue(entry, field));
            }
            else field.Accessor.SetValue(message, ReadValue(group.Single(), field));
        }
        return message;
    }

    private static object ReadValue(XElement node, FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Message => ReadMessage(node, field.MessageType),
        FieldType.String => node.Value,
        FieldType.Bytes => ByteString.FromBase64(node.Value),
        FieldType.Bool => XmlConvert.ToBoolean(node.Value),
        FieldType.Double => XmlConvert.ToDouble(node.Value),
        FieldType.Float => XmlConvert.ToSingle(node.Value),
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => XmlConvert.ToInt32(node.Value),
        FieldType.UInt32 or FieldType.Fixed32 => XmlConvert.ToUInt32(node.Value),
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => XmlConvert.ToInt64(node.Value),
        FieldType.UInt64 or FieldType.Fixed64 => XmlConvert.ToUInt64(node.Value),
        FieldType.Enum => System.Enum.ToObject(field.EnumType.ClrType, XmlConvert.ToInt32(node.Value)),
        _ => throw Invalid("Unsupported native field type: " + field.FieldType)
    };

    private static string ScalarText(FieldDescriptor field, object value) => field.FieldType switch
    {
        FieldType.String => (string)value,
        FieldType.Bytes => ((ByteString)value).ToBase64(),
        FieldType.Bool => XmlConvert.ToString((bool)value),
        FieldType.Double => XmlConvert.ToString((double)value),
        FieldType.Float => XmlConvert.ToString((float)value),
        FieldType.Enum => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
    };

    private static bool IsDefault(FieldDescriptor field, object value) => field.FieldType switch
    {
        FieldType.Message => value is null,
        FieldType.String => (string)value == "",
        FieldType.Bytes => ((ByteString)value).IsEmpty,
        FieldType.Bool => !(bool)value,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture) == 0
    };

    public static string ExportSchema()
    {
        string TypeName(MessageDescriptor descriptor) => "t_" + descriptor.FullName.Replace('.', '_');
        XElement Choice(IEnumerable<MessageDescriptor> types) => new(Xs + "choice", types.Select(d =>
            new XElement(Xs + "element", new XAttribute("name", d.FullName), new XAttribute("type", "s:" + TypeName(d)))));
        var schema = new XElement(Xs + "schema", new XAttribute(XNamespace.Xmlns + "xs", Xs),
            new XAttribute(XNamespace.Xmlns + "s", Ns), new XAttribute("targetNamespace", Ns),
            new XAttribute("elementFormDefault", "qualified"));
        schema.Add(new XElement(Xs + "element", new XAttribute("name", "schematic-data"),
            new XElement(Xs + "complexType", Choice(Messages.Values.Where(IsRoot)),
                new XElement(Xs + "attribute", new XAttribute("name", "version"), new XAttribute("type", "xs:string"),
                    new XAttribute("use", "required"), new XAttribute("fixed", "1")))));
        foreach (var descriptor in Messages.Values.OrderBy(d => d.FullName, StringComparer.Ordinal))
        {
            var type = new XElement(Xs + "complexType", new XAttribute("name", TypeName(descriptor)));
            if (descriptor == Any.Descriptor)
                type.Add(Choice(Messages.Values.Where(d => !d.IsMapEntry && d != Any.Descriptor)));
            else
            {
                var sequence = new XElement(Xs + "sequence");
                foreach (var field in descriptor.Fields.InFieldNumberOrder())
                {
                    var element = new XElement(Xs + "element", new XAttribute("name", field.Name),
                        new XAttribute("minOccurs", descriptor.IsMapEntry ? "1" : "0"),
                        new XAttribute("maxOccurs", field.IsRepeated ? "unbounded" : "1"));
                    if (field.FieldType == FieldType.Enum)
                        element.Add(new XElement(Xs + "simpleType", new XElement(Xs + "restriction", new XAttribute("base", "xs:int"),
                            field.EnumType.Values.Select(v => v.Number).Distinct().Select(n =>
                                new XElement(Xs + "enumeration", new XAttribute("value", n))))));
                    else element.Add(new XAttribute("type", field.FieldType == FieldType.Message
                        ? "s:" + TypeName(field.MessageType) : "xs:" + ScalarType(field.FieldType)));
                    sequence.Add(element);
                }
                type.Add(sequence);
            }
            schema.Add(type);
        }
        return Render(schema);
    }

    private static string ScalarType(FieldType type) => type switch
    {
        FieldType.String => "string", FieldType.Bytes => "base64Binary", FieldType.Bool => "boolean",
        FieldType.Float => "float", FieldType.Double => "double",
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => "int",
        FieldType.UInt32 or FieldType.Fixed32 => "unsignedInt",
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => "long",
        FieldType.UInt64 or FieldType.Fixed64 => "unsignedLong",
        _ => throw Invalid("Unsupported native scalar type: " + type)
    };

    private static Dictionary<string, MessageDescriptor> Collect(FileDescriptor file)
    {
        var messages = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        var files = new HashSet<string>();
        void AddMessage(MessageDescriptor message)
        {
            messages.Add(message.FullName, message);
            foreach (var nested in message.NestedTypes) AddMessage(nested);
        }
        void AddFile(FileDescriptor current)
        {
            if (!files.Add(current.Name)) return;
            foreach (var dependency in current.Dependencies) AddFile(dependency);
            foreach (var message in current.MessageTypes) AddMessage(message);
        }
        AddFile(file); return messages;
    }

    private static bool IsRoot(MessageDescriptor descriptor) => descriptor.File == SchematicText.Descriptor.File && !descriptor.IsMapEntry;
    private static void RequireRoot(MessageDescriptor descriptor)
    {
        if (!IsRoot(descriptor)) throw Invalid("Unsupported schematic data type: " + descriptor.FullName);
    }
    internal static string Render(XElement element)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings
        { Indent = true, OmitXmlDeclaration = true, NewLineChars = "\n", NewLineHandling = NewLineHandling.Entitize })) element.Save(writer);
        return text + "\n";
    }
    // Preserve the existing source-data boundary: no DTD or external resource resolution.
    private static XmlReaderSettings ReaderSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
    private static AutomationException Invalid(string message) => new("invalid_schematic_xml", message);
}
