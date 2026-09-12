using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

internal static class SchematicBomSettingsValidation
{
    internal static void Validate(SchematicBomSettings settings)
    {
        if (settings.CurrentView is null || settings.CurrentFormat is null)
            throw new AutomationException("unsupported_schematic_delta", "BOM settings require explicit current view and format.");
        foreach (var view in settings.SavedViews.Prepend(settings.CurrentView))
            if (view.FilterScope is not (SchematicBomFilterScope.SbfsReference
                or SchematicBomFilterScope.SbfsVisible or SchematicBomFilterScope.SbfsAll))
                throw new AutomationException("unsupported_schematic_delta", "BOM view contains an unsupported filter scope.");
        // The descriptor codec rejects unknown fields/enums and non-XML text.
        // Empty and duplicate names are allowed by native project persistence.
        SchematicDataXml.Write(settings);
    }
}
