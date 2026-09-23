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
        new InstanceCapability("kicad_instances_list", "service", "compiled-mcp", "registered", "explicit instance ID", false),
        new InstanceCapability("kicad_instance_inspect", "service", "compiled-mcp", "registered", "verified instance epoch", false),
        new InstanceCapability("kicad_design_sync_plan", "schematic-design", "compiled-mcp", "registered", "recovery revision token", false),
        new InstanceCapability("kicad_design_sync_apply", "schematic-design", "compiled-mcp", "registered", "instance epoch, recovery token, operation ID", false),
        new InstanceCapability("kicad_design_candidate_commit", "schematic-design", "compiled-mcp", "registered", "recovery revision token, candidate hash", false),
        new InstanceCapability("kicad_diagram_proposal_publish", "structural-diagram", "compiled-mcp", "registered", "instance epoch, source token, proposal identity", false),
        new InstanceCapability("kicad_diagram_proposal_select", "structural-diagram", "compiled-mcp", "registered", "instance epoch, source token, exact root/path", false),
        new InstanceCapability("kicad_diagram_physical_allocation", "structural-diagram", "compiled-mcp", "registered", "source token, exact block selection", false),
        new InstanceCapability("kicad_diagram_physical_allocation_set", "structural-diagram", "compiled-mcp", "registered", "instance epoch, source token, exact root/path, operation ID", false),
        new InstanceCapability("kicad_simulation_start", "simulation", "native-ngspice", "registered", "instance epoch, document, operation ID", false),
        new InstanceCapability("kicad_simulation_job", "simulation", "native-ngspice", "registered", "instance epoch, document, job ID", false),
        new InstanceCapability("kicad_simulation_wait", "simulation", "native-ngspice", "registered", "instance epoch, document, job ID, sequence", false),
        new InstanceCapability("kicad_simulation_cancel", "simulation", "native-ngspice", "registered", "instance epoch, document, job ID", false),
        new InstanceCapability("kicad_pcb_items_read", "pcb", "native-board-api", "registered", "explicit board document", false),
        new InstanceCapability("kicad_pcb_items_create", "pcb", "native-board-api", "registered", "board lifecycle state, typed Any items", false),
        new InstanceCapability("kicad_pcb_items_update", "pcb", "native-board-api", "registered", "board lifecycle state, typed Any items", false),
        new InstanceCapability("kicad_pcb_guide_create", "pcb-guide", "native-board-api", "registered", "board lifecycle state, non-copper guide objects, source hash", false),
        new InstanceCapability("kicad_pcb_guide_svg_create", "pcb-guide", "compiled-mcp plus native-board-api", "registered", "SVG source archive, non-copper layer, board lifecycle state", false),
        new InstanceCapability("kicad_pcb_route_candidate_from_guide", "pcb-routing", "compiled-mcp plus native-board-api", "registered", "board lifecycle state, guide provenance, existing net, deterministic candidate", false),
        new InstanceCapability("kicad_pcb_route_candidate_validate", "pcb-routing", "compiled-mcp plus native-board-api", "registered", "board lifecycle state, guide provenance, typed copper candidate", false),
        new InstanceCapability("kicad_pcb_render_3d", "pcb-observation", "compiled-mcp plus native-board-job-renderer", "registered", "board lifecycle state, native image snapshot", false),
        new InstanceCapability("kicad_pcb_route_geometry", "pcb-routing", "compiled-mcp plus native-board-api", "registered", "board lifecycle state, track/arc/via geometry", false),
        new InstanceCapability("kicad_pcb_route_preview", "pcb-routing", "compiled-mcp plus native-pns-router", "registered", "board lifecycle state, start item, native PNS waypoint preview", false),
        new InstanceCapability("kicad_pcb_drc_start", "pcb", "compiled-mcp plus native-api", "registered", "process epoch, document revision, operation ID", false),
        new InstanceCapability("kicad_pcb_drc_job", "pcb", "compiled-mcp plus native-api", "registered", "process epoch, document and job ID", false),
        new InstanceCapability("kicad_pcb_drc_cancel", "pcb", "compiled-mcp plus native-api", "registered", "process epoch, document and job ID", false)
    ];
}
