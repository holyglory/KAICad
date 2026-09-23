using KiCad.Automation.Protocol;
using ModelContextProtocol.Server;

namespace KiCad.Automation.Mcp;

/// <summary>The strongest evidence that exists for one MCP tool in this build's test suite.</summary>
public enum KiCadVerificationLevel
{
    /// <summary>The tool itself was called through the compiled MCP STDIO server against a real native KiCad in a Linux journey.</summary>
    McpNativeJourney,
    /// <summary>The operation behind the tool ran against a real native KiCad in a Linux journey, but not through this tool.</summary>
    NativeJourney,
    /// <summary>The tool was called through the compiled MCP STDIO server without a native KiCad: discovery, validation and error paths only.</summary>
    McpProcess,
    /// <summary>Only in-process tests exercise the tool or the model behind it.</summary>
    InProcess
}

/// <summary>
/// Declares how a tool's behaviour is proven. Evidence names test methods as "Class.Method" in
/// KiCad.Automation.Tests; CapabilityCatalogTests resolves every name, so a removed or renamed
/// test cannot stay advertised as proof.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class KiCadVerificationAttribute(KiCadVerificationLevel level, params string[] evidence) : Attribute
{
    public KiCadVerificationLevel Level { get; } = level;
    public IReadOnlyList<string> Evidence { get; } = evidence;
}

public sealed record ToolVerification(string Level, IReadOnlyList<string> Evidence);

/// <summary>
/// One tool of the running MCP server. Availability "registered" means the tool is registered in
/// this process and can be called; its revision contract still applies. Declaration says where the
/// scope, source and contract come from: the tool's [KiCadCapability] attribute, the transitional
/// hand-kept service list, or nowhere (they are then null rather than guessed).
/// </summary>
public sealed record ServiceToolCapability(string Name, string Availability, string Declaration, string? Scope,
                                           string? Source, string? RevisionContract, bool? ReadOnly,
                                           ToolVerification Verification);

/// <summary>One request type that the native handshake advertises.</summary>
public sealed record NativeRequestCapability(string Name, string Availability);

/// <summary>
/// Work that no registered tool provides. Tools listed in RegisteredToolsInScope share the scope and
/// remain available within their own declared contracts; the summary names only what is missing.
/// </summary>
public sealed record CapabilityLimitation(string Id, string Scope, string Summary, IReadOnlyList<string> RegisteredToolsInScope);

public sealed record NativeCapabilityCatalog(string Format, IReadOnlyList<NativeRequestCapability> Requests);

/// <summary>
/// Builds the capability catalogue from what is actually registered: the MCP server's own tool
/// collection and the native handshake. Nothing here lists tools by hand.
/// </summary>
public static class CapabilityCatalog
{
    public const int SchemaVersion = 2;

    public static IReadOnlyList<string> Notes { get; } =
    [
        "serviceCapabilities lists every tool registered in this MCP server process, derived from the server's tool collection; availability 'registered' means the tool can be called, subject to its revision contract.",
        "nativeCapabilities lists the request types the native process dispatches to a registered handler at this moment. Opening or closing an editor changes the list, and a handler can still reject a request for its target, its arguments, the editor's state or a mode this process does not support.",
        "verification.level names the strongest evidence in this build's test suite: mcp-native-journey, native-journey, mcp-process, in-process, or undeclared when a tool declares none.",
        "limitations name unfinished work that no registered tool provides; they do not withdraw any registered tool."
    ];

    // Unfinished outcomes that cannot be derived from code. Each names what is missing, never a
    // registered tool, so it cannot contradict a tool's availability.
    private static readonly (string Id, string Scope, string Summary)[] Unfinished =
    [
        ("pcb-routing-completion", "pcb-routing",
            "The pcb-routing tools preview, propose, validate and measure routes; none of them commits copper, which is added only through the primitive kicad_pcb_items_create edit. Committing a validated candidate with undo and rollback, layer changes and vias, full push-and-shove, differential-pair and tuning modes, failure and cancellation recovery, and DRC, impedance or RF qualification of routed results are unfinished."),
        ("simulation-qualification", "simulation",
            "The simulation tools run KiCad's own ngspice on an explicit schematic and are verified on Linux only. Capturing every model and library file a simulation depends on, broader simulation qualification, and qualification on Mac, Windows and through Codex Desktop are unfinished."),
        ("agent-client-qualification", "qualification",
            "Operation through an actual agent client is verified only for the Linux Codex fixture. The Mac-local and Mac-to-VPS modes and other agent clients are not qualified."),
        ("platform-qualification", "qualification",
            "All native verification evidence comes from Linux. Installed packages for Mac (Apple Silicon and Intel) and Windows are not qualified for this workflow.")
    ];

    public static IReadOnlyList<ServiceToolCapability> Service(IEnumerable<McpServerTool> tools)
    {
        // Transitional: rows for tools that do not carry [KiCadCapability] yet. The attribute wins
        // where both exist, and CapabilityCatalogTests requires the two to agree.
        var legacy = new Dictionary<string, InstanceCapability>(StringComparer.Ordinal);
        foreach (var row in ServiceCapabilities.Registered) legacy.TryAdd(row.Name, row);
        return tools.Select(tool => Describe(tool, legacy)).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
    }

    public static NativeCapabilityCatalog Native(AutomationSession session)
    {
        // An unknown future format fails closed: its entries are not claimed as handlers.
        var (format, availability) = session.CapabilityFormat switch
        {
            NativeCapabilityFormat.NcpRegisteredRequestTypes => ("registered-request-types", "handler-registered"),
            NativeCapabilityFormat.NcpLegacyLabels => ("legacy-labels", "legacy-label"),
            _ => ("unrecognized", "unrecognized")
        };
        return new(format, session.Capabilities.Select(name => new NativeRequestCapability(name, availability)).ToArray());
    }

    public static IReadOnlyList<CapabilityLimitation> Limitations(IReadOnlyList<ServiceToolCapability> service) =>
        Unfinished.Select(item => new CapabilityLimitation(item.Id, item.Scope, item.Summary,
            service.Where(tool => tool.Scope == item.Scope).Select(tool => tool.Name).ToArray())).ToArray();

    public static string Level(KiCadVerificationLevel level) => level switch
    {
        KiCadVerificationLevel.McpNativeJourney => "mcp-native-journey",
        KiCadVerificationLevel.NativeJourney => "native-journey",
        KiCadVerificationLevel.McpProcess => "mcp-process",
        KiCadVerificationLevel.InProcess => "in-process",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    private static ServiceToolCapability Describe(McpServerTool tool, IReadOnlyDictionary<string, InstanceCapability> legacy)
    {
        string name = tool.ProtocolTool.Name;
        bool? readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint;
        var verification = tool.Metadata.OfType<KiCadVerificationAttribute>().SingleOrDefault() is { } verified
            ? new ToolVerification(Level(verified.Level), verified.Evidence)
            : new ToolVerification("undeclared", []);
        if (tool.Metadata.OfType<KiCadCapabilityAttribute>().SingleOrDefault() is { } declared)
            return new(name, "registered", "attribute", declared.Scope, declared.Source, declared.RevisionContract, readOnly, verification);
        if (legacy.TryGetValue(name, out var row))
            return new(name, "registered", "legacy-service-list", row.Scope, row.Source, row.RevisionContract, readOnly, verification);
        return new(name, "registered", "undeclared", null, null, null, readOnly, verification);
    }
}
