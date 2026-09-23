namespace KiCad.Automation.Mcp;

// Service rows advertised by kicad_instance_capabilities, in their published
// order. New tools also carry [KiCadCapability]; this list stays hand-kept
// until the catalogue is derived from those attributes.
public static class ServiceCapabilities
{
    // Keep this list intentionally limited to operations with real handlers
    // and focused evidence. Planned PCB routing, simulation, OCR and external
    // agent-client features are not represented as working capabilities.
    public static IReadOnlyList<InstanceCapability> Registered { get; } =
    [
        new InstanceCapability("kicad_design_sync_plan", "schematic-design", "compiled-mcp", "registered", "recovery revision token", false),
        new InstanceCapability("kicad_design_sync_apply", "schematic-design", "compiled-mcp", "registered", "instance epoch, recovery token, operation ID", false),
        new InstanceCapability("kicad_design_candidate_commit", "schematic-design", "compiled-mcp", "registered", "recovery revision token, candidate hash", false),
        new InstanceCapability("kicad_diagram_proposal_publish", "structural-diagram", "compiled-mcp", "registered", "instance epoch, source token, proposal identity", false),
        new InstanceCapability("kicad_diagram_proposal_select", "structural-diagram", "compiled-mcp", "registered", "instance epoch, source token, exact root/path", false),
        new InstanceCapability("kicad_diagram_physical_allocation", "structural-diagram", "compiled-mcp", "registered", "source token, exact block selection", false),
        new InstanceCapability("kicad_diagram_physical_allocation_set", "structural-diagram", "compiled-mcp", "registered", "instance epoch, source token, exact root/path, operation ID", false)
    ];
}
