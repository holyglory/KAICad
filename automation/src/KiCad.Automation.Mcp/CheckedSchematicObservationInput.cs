using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;

namespace KiCad.Automation.Mcp;

/// <summary>
/// Writable MCP input for one complete checked schematic observation.
///
/// The generated protobuf classes intentionally expose repeated and map fields
/// as get-only collections.  Deserializing a CheckedSchematicState directly
/// with System.Text.Json would therefore produce a partial hierarchy and can
/// lose Any-packed items.  This boundary keeps each protobuf branch as raw JSON
/// and lets the reviewed protobuf parser construct the complete message.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CheckedSchematicObservationInput : NativeDocumentInputObject
{
    private JsonElement? state;
    private JsonElement? electrical;

    [Description("The lifecycle state branch from one checked schematic observation.")]
    public JsonElement? State { get => state; set => state = BindOnce(value); }

    [Description("The electrical hierarchy branch from the same checked schematic observation. Repeated screens, items and Any-packed payloads are preserved verbatim.")]
    public JsonElement? Electrical { get => electrical; set => electrical = BindOnce(value); }

    internal CheckedSchematicState ToNative()
    {
        string json = ToJson();
        try
        {
            var result = SchematicJson.Parser.Parse<CheckedSchematicState>(json);
            if (result.State is null || result.Electrical?.Hierarchy?.Data?.Document is null)
                throw Invalid("expectedState must include both lifecycle state and electrical hierarchy observations.");
            return result;
        }
        catch (AutomationException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidJsonException or InvalidProtocolBufferException
            or InvalidOperationException or ArgumentException or FormatException or JsonException)
        {
            throw Invalid("expectedState is not a complete checked schematic observation: " + error.Message);
        }
    }

    internal string ToJson()
    {
        if (State is not { } stateValue || stateValue.ValueKind != JsonValueKind.Object
            || Electrical is not { } electricalValue || electricalValue.ValueKind != JsonValueKind.Object)
            throw Invalid("expectedState must include object-valued state and electrical branches from kicad_schematic_checked_state.");
        RejectDuplicateProperties(stateValue);
        RejectDuplicateProperties(electricalValue);

        // JsonElement.GetRawText() is deliberate: it retains protobuf JSON
        // field names, repeated arrays and Any @type/data values without a
        // second System.Text.Json model round-trip.
        return $$"""{"state":{{stateValue.GetRawText()}},"electrical":{{electricalValue.GetRawText()}}}""";
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw Invalid("expectedState contains a duplicate protobuf field: " + property.Name);
                    RejectDuplicateProperties(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in value.EnumerateArray())
                    RejectDuplicateProperties(child);
                break;
        }
    }

    private static AutomationException Invalid(string message)
        => new("invalid_checked_state", message);
}
