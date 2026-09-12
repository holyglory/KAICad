using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal static class SchematicFieldTemplatesValidation
{
    internal static void Validate(SchematicFieldTemplates value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string[] mandatory = ["Reference", "Value", "Footprint", "Datasheet", "Description"];
        foreach (var field in value.Fields)
        {
            if (string.IsNullOrEmpty(field.Name) || field.Name.Contains('\0')
                || !names.Add(field.Name) || mandatory.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                throw new AutomationException("unsupported_schematic_delta",
                    "Project templates require unique nonempty names, without NUL or reserved symbol fields.");
        }
    }
}
