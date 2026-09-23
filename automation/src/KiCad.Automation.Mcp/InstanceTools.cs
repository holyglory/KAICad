using System.ComponentModel;
using System.Text.Json;
using Google.Protobuf;
using Kiapi.Common.Commands;
using Kiapi.Common.Types;
using KiCad.Automation.Model;
using KiCad.Automation.Native;
using KiCad.Automation.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace KiCad.Automation.Mcp;

public sealed record InstanceView(string InstanceId, string ProjectPath, DateTimeOffset LastVerifiedAt);
public sealed record InspectedInstance(string InstanceId, string ProjectPath, string NativeVersion,
                                      IReadOnlyList<string> NativeCapabilities, string NativeCapabilityFormat);
public sealed record InstanceCapability(string Name, string Scope, string Source, string Availability,
                                        string RevisionContract, bool NativeAdvertised);

[McpServerToolType]
public sealed class InstanceTools(InstanceRegistry registry)
{
    [McpServerTool(Name = "kicad_instances_list", ReadOnly = true),
     Description("List instances attached to this MCP server. LastVerifiedAt is historical, not a claim that the process is still running."),
     KiCadCapability("service", "compiled-mcp", "explicit instance ID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public IReadOnlyList<InstanceView> List() => registry.List().Select(View).ToArray();

    [McpServerTool(Name = "kicad_instance_saved_sessions", ReadOnly = true),
     Description("List saved connection records, including after MCP restarts. These are historical verification records, not live process status or attached sessions. Reattach the chosen instance to verify its identity and recover it."),
     KiCadCapability("service", "compiled-mcp", "none; returns saved instance IDs to reattach"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public async Task<CallToolResult> SavedSessions(CancellationToken cancellationToken) =>
        await InstanceToolBoundary.Run<IReadOnlyList<InstanceView>>(async () =>
            (await registry.SavedSessionsAsync(cancellationToken)).Select(View).ToArray());

    [McpServerTool(Name = "kicad_instance_pending_launches", ReadOnly = true),
     Description("List unverified startup receipts after interrupted or cancelled starts. A receipt does not mean KiCad is running. Use its instance ID with reattach to verify and recover the native session."),
     KiCadCapability("service", "compiled-mcp", "none; returns unverified launch receipts by instance ID"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> PendingLaunches(CancellationToken cancellationToken) =>
        InstanceToolBoundary.Run(() => registry.PendingLaunchesAsync(cancellationToken));

    [McpServerTool(Name = "kicad_instance_start"),
     Description("Start a separate native KiCad automation process for an existing .kicad_pro. Requires the matching fork executable; preserves the process when MCP disconnects."),
     KiCadCapability("service", "compiled-mcp plus native-process", "absolute matching executable, existing .kicad_pro path"),
     KiCadVerification(KiCadVerificationLevel.NativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach")]
    public async Task<CallToolResult> Start(string executable, string projectPath, CancellationToken cancellationToken,
        [Description("Use the native software-rendering path. Defaults to enabled on Linux and native preferences on Mac.")]
        bool? softwareRendering = null) =>
        await InstanceToolBoundary.Run(async () => View(await registry.StartAsync(executable, projectPath, cancellationToken, softwareRendering)));

    [McpServerTool(Name = "kicad_instance_attach"),
     Description("Attach an explicitly identified automation instance at an absolute ipc:/// endpoint. Verifies the expected UUID before recording the connection."),
     KiCadCapability("service", "compiled-mcp plus native-api", "absolute ipc endpoint, expected instance ID"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach")]
    public async Task<CallToolResult> Attach(string endpoint, string expectedInstanceId, CancellationToken cancellationToken) =>
        await InstanceToolBoundary.Run(async () => View(await registry.AttachAsync(endpoint, expectedInstanceId, cancellationToken)));

    [McpServerTool(Name = "kicad_instance_reattach"),
     Description("Recover a saved session or an unverified interrupted launch after an MCP restart. Verifies the instance and project, and checks the previous process epoch when one was recorded. Never restarts or kills KiCad."),
     KiCadCapability("service", "compiled-mcp plus native-api", "saved instance ID, recorded process epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach")]
    public async Task<CallToolResult> Reattach(string instanceId, CancellationToken cancellationToken) =>
        await InstanceToolBoundary.Run(async () => View(await registry.ReattachAsync(instanceId, cancellationToken)));

    [McpServerTool(Name = "kicad_instance_inspect", ReadOnly = true),
     Description("Verify a live native instance and return its actual build version and advertised native capabilities: the request types it dispatches to registered handlers right now, and the format of that list."),
     KiCadCapability("service", "compiled-mcp", "verified instance epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach", "InstanceToolBoundaryTests.CompiledStdioReturnsActionableUnknownInstanceErrorAndRecovers")]
    public Task<CallToolResult> Inspect(string instanceId, CancellationToken cancellationToken) => InstanceToolBoundary.Run<InspectedInstance>(async () =>
    {
        NativeClient client = registry.Client(instanceId);
        AutomationSession session = await client.HandshakeAsync(cancellationToken);
        GetVersionResponse version = await client.GetVersionAsync(cancellationToken);
        return new(session.InstanceId, session.ProjectPath, version.Version.FullVersion, session.Capabilities.ToArray(),
                   CapabilityCatalog.Native(session).Format);
    });

    [McpServerTool(Name = "kicad_instance_capabilities", ReadOnly = true),
     Description("Return the versioned capability catalogue of one verified native instance and this MCP server. nativeCapabilities are the request types that instance dispatches to registered handlers right now, read from its handshake; opening or closing an editor changes them. serviceCapabilities list every tool registered in this server with its scope, target and revision contract and verification evidence, and limitations name unfinished work that no registered tool provides. Does not change documents."),
     KiCadCapability("service", "compiled-mcp plus native-handshake", "explicit instance ID, verified instance epoch"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach", "McpProcessTests.InitializeDiscoverAndCallOverStdio")]
    public Task<CallToolResult> Capabilities(string instanceId, McpServer server, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
    {
        var client = registry.Client(instanceId);
        var session = await client.HandshakeAsync(cancellationToken);
        var version = await client.GetVersionAsync(cancellationToken);
        var native = CapabilityCatalog.Native(session);
        var service = CapabilityCatalog.Service(RegisteredTools(server));
        return new
        {
            schemaVersion = CapabilityCatalog.SchemaVersion, session.InstanceId, session.ProjectPath, session.Epoch,
            nativeVersion = version.Version.FullVersion, nativeCapabilityFormat = native.Format, nativeCapabilities = native.Requests,
            serviceCapabilities = service, limitations = CapabilityCatalog.Limitations(service), notes = CapabilityCatalog.Notes
        };
    });

    [McpServerTool(Name = "kicad_service_capabilities", ReadOnly = true),
     Description("Return this MCP server's own capability catalogue without contacting KiCad: every registered tool with its scope, target and revision contract and verification evidence, plus unfinished work that no registered tool provides. Use kicad_instance_capabilities for the request types a running KiCad supports."),
     KiCadCapability("service", "compiled-mcp", "none"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach", "CapabilityCatalogTests.CompiledServerAdvertisesExactlyItsRegisteredTools")]
    public Task<CallToolResult> ServiceCapabilitiesCatalog(McpServer server) => InstanceToolBoundary.Run(() =>
    {
        var service = CapabilityCatalog.Service(RegisteredTools(server));
        return Task.FromResult(new
        {
            schemaVersion = CapabilityCatalog.SchemaVersion, serviceCapabilities = service,
            limitations = CapabilityCatalog.Limitations(service), notes = CapabilityCatalog.Notes
        });
    });

    // The tools/list catalogue itself: the collection the server lists and dispatches.
    private static IEnumerable<McpServerTool> RegisteredTools(McpServer server) =>
        server.ServerOptions.ToolCollection ?? throw new InvalidOperationException("The MCP server has no tool collection.");

    [McpServerTool(Name = "kicad_documents_list", ReadOnly = true),
     Description("Query actual open documents of one kind in an identified instance. Kinds: schematic, symbol, pcb, footprint. Does not open or change a document."),
     KiCadCapability("document", "native-api", "explicit instance ID, document kind"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach")]
    public Task<CallToolResult> Documents(string instanceId, string kind, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
    {
        // Numeric values are the shared DocumentType protobuf wire contract.
        int type = kind switch
        {
            "schematic" => 1, "symbol" => 2, "pcb" => 3, "footprint" => 4,
            _ => throw new AutomationException("invalid_document_kind", "Choose schematic, symbol, pcb or footprint.")
        };
        var result = await registry.Client(instanceId).InvokeAsync<GetOpenDocuments, GetOpenDocumentsResponse>(
            new GetOpenDocuments { Type = (DocumentType)type }, cancellationToken);
        return JsonFormatter.Default.Format(result);
    });

    [McpServerTool(Name = "kicad_schematic_open"),
     Description("Open an existing root .kicad_sch in the graphical editor belonging to an explicitly attached instance. Currently requires the project's root schematic path; does not switch projects or import other formats. Returns its native project/sheet descriptor. Repeating an open preserves the current document. Does not provide revision-safe editing or rendering."),
     KiCadCapability("document", "native-api", "explicit instance ID, absolute project-root .kicad_sch path"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public Task<CallToolResult> OpenSchematic(string instanceId, string path, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
    {
        OpenDocumentResponse response = await registry.Client(instanceId).OpenRootSchematicAsync(path, cancellationToken);
        return JsonFormatter.Default.Format(response.Document);
    });

    private static InstanceView View(InstanceRecord record) => new(record.InstanceId, record.ProjectPath, record.VerifiedAt);

    [McpServerTool(Name = "kicad_schematic_create"),
     Description("Create an unsaved empty root schematic for an explicitly attached instance if its project-root .kicad_sch is missing. Existing files and already open documents are returned without replacement. Requires an absolute path belonging to that instance's project. Save explicitly to persist the new schematic. Does not reconstruct a design or create another project."),
     KiCadCapability("document", "native-api", "explicit instance ID, absolute project-root .kicad_sch path"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.TwoNativeProjectsHaveIndependentEpochsAndCanReattach")]
    public Task<CallToolResult> CreateSchematic(string instanceId, string path, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
    {
        var response = await registry.Client(instanceId).CreateRootSchematicAsync(path, cancellationToken);
        return JsonFormatter.Default.Format(response.Document);
    });

    [McpServerTool(Name = "kicad_pcb_open"),
     Description("Open the existing project-root .kicad_pcb in the graphical editor belonging to an explicitly attached instance. Requires an absolute path belonging to that project; does not switch projects or import other formats. Returns the native project/board descriptor. Repeating an open preserves the current document. Does not provide revision-safe board editing or rendering."),
     KiCadCapability("document", "native-api", "explicit instance ID, absolute project-root .kicad_pcb path"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits")]
    public Task<CallToolResult> OpenPcb(string instanceId, string path, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
    {
        var response = await registry.Client(instanceId).OpenRootBoardAsync(path, cancellationToken);
        return JsonFormatter.Default.Format(response.Document);
    });

    [McpServerTool(Name = "kicad_pcb_create"),
     Description("Create an unsaved empty project-root PCB in an explicitly attached native instance if its .kicad_pcb file is missing. Existing files and open boards are returned without replacement. Requires an absolute path belonging to that instance's project. Save explicitly to persist the board. Does not place components or route a board."),
     KiCadCapability("document", "native-api", "explicit instance ID, absolute project-root .kicad_pcb path"),
     KiCadVerification(KiCadVerificationLevel.McpNativeJourney, "NativeSessionTests.NetClassesRoundTripThroughXmlAndNativeEdits", "NativeSessionTests.NativePcbItemsAreCreatedAndUpdatedThroughMcp")]
    public Task<CallToolResult> CreatePcb(string instanceId, string path, CancellationToken cancellationToken) => InstanceToolBoundary.Run(async () =>
    {
        var response = await registry.Client(instanceId).CreateRootBoardAsync(path, cancellationToken);
        return JsonFormatter.Default.Format(response.Document);
    });
}
