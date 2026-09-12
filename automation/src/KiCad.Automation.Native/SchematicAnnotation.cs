using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal static class SchematicAnnotation
{
    internal static void Validate(SchematicAnnotationSettings value)
    {
        if ((int)value.Order is < 1 or > 2 || (int)value.Method is < 1 or > 3)
            throw new AutomationException("unsupported_schematic_delta",
                "Annotation requires explicit supported numbering order and method.");
    }
}
