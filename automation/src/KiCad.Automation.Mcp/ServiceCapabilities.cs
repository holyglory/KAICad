namespace KiCad.Automation.Mcp;

// Transitional rows for registered tools that do not carry [KiCadCapability]
// yet. The capability catalogue is derived from the server's registered tools:
// a tool's attribute wins where both exist, CapabilityCatalogTests requires the
// two to agree, and a row is deleted once its tool carries the attribute.
public static class ServiceCapabilities
{
    // Only tools that are really registered may appear here; unfinished work is
    // described by CapabilityCatalog limitations, never by a row.
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
