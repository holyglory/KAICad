using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

public sealed record McpCapability(string Id, string Status, string Scope, string Notes);

[McpServerToolType]
public sealed class CapabilityTools
{
    [McpServerTool(Name = "kicad_capabilities", ReadOnly = true),
     Description("Describe the agent-facing KiCad MCP capability families and their current implementation status. This is a product capability catalogue, not proof that a particular native instance is live; inspect the instance before using instance-bound tools.")]
    public IReadOnlyList<McpCapability> List() =>
    [
        new("instances", "complete", "runtime", "Explicit attach, start, inspect and reconnect lifecycle."),
        new("schematic.observe", "preliminary", "schematic", "Current-sheet and hierarchy observation, rendering and recovery; coverage and revision tracking remain explicit."),
        new("schematic.mutate", "partial", "schematic", "Typed native batches for supported item and metadata operations are exposed; complete revision tracking remains explicit."),
        new("symbol.authoring", "partial", "symbol and schematic", "Typed library-item create/update/delete and library-item opening are exposed; complete symbol lifecycle qualification remains open."),
        new("pcb.inspect", "preliminary", "pcb", "Board lifecycle, layer state, stackup, nets and item inspection are exposed by the board foundation."),
        new("pcb.authoring", "planned", "pcb", "Footprint, pad, graphic, zone and board-item mutations require the next board tools."),
        new("pcb.routing", "partial", "pcb", "Deterministic single-layer orthogonal planning and native track commit are available; differential, tuning, layer-change and push-and-shove routing remain open."),
        new("rendering.layers", "planned", "schematic and pcb", "Schematic multi-view rendering exists; PCB layer rendering is the next rendering surface."),
        new("verification", "planned", "schematic and pcb", "ERC, DRC, persistence and packaged per-module qualification remain open."),
        new("jobs.simulation.export", "partial", "project", "Native PCB SVG, 3D and manufacturing job adapters are exposed; simulation and full project-wide qualification remain open.")
    ];
}
