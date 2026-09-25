using System.Text.RegularExpressions;
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
/// KiCad.Automation.Tests. CapabilityCatalogTests resolves every name and checks the claim against
/// the source files of the cited classes: an mcp-native-journey claim needs a cited NativeSessionTests
/// journey and a call to the tool by name in a NativeSessionTests source (the Linux native
/// journeys); a native-journey claim needs a cited NativeSessionTests journey; every other cited
/// class of an mcp-native-journey, native-journey or mcp-process claim must start the compiled MCP
/// STDIO server and call the tool by name. Comments never count as calls. The call check is per
/// class, not per method: it cannot tell which journey of NativeSessionTests makes the call.
/// <para>
/// The check guards against honest mistakes, such as citing a journey that still ends Inconclusive;
/// it does not guard against deliberate evasion. It reads source text, not compiled code, and
/// rejects a cited NativeSessionTests method only for what it finds in these forms:
/// </para>
/// <list type="bullet">
/// <item>An Inconclusive lane stub, recognised by its body: a member whose own text names
/// AssertInconclusiveException or Assert.Inconclusive, whether to raise, catch or test for it,
/// except a catch filter that only lets such a result through. A cited stub is rejected.</item>
/// <item>A direct citation, whose expression body (=>) is exactly
/// RunNativeSessions(NativeJourney.X[, further arguments that are each a plain name or a string
/// literal with no interpolated values]), optionally awaited. A block body that makes the same call
/// is read as a helper chain, which rejects it because it names RunNativeSessions. A direct
/// citation is rejected when a switch over the running journey sends X to a stub or has a default
/// arm leading to a stub that X may reach. A switch is read as dispatch only over a parameter that
/// always holds the journey being run, passed on unchanged from RunNativeSessions. A default arm is
/// limited only by earlier unconditional switches over that same parameter in the same block, and
/// only when it is not inside a lambda, local function or nested block. Every direct citation is
/// rejected when a stub is named other than in its declaration and switch arms, when a switch over
/// any other value leads to a stub, or when a fixture member calls into another test class a member
/// that can end Inconclusive.</item>
/// <item>A helper-chain citation (any other cited method), followed through every
/// NativeSessionTests member it names without a qualifier, to any depth. It is rejected when a
/// reached member, including the cited test itself, is or names a stub, names NativeJourney or
/// RunNativeSessions, makes an unqualified call the check cannot find, or calls into another test
/// class a member that can end Inconclusive.</item>
/// </list>
/// A call into another test class (any other class declared in the test sources) is followed only
/// when written Class.Member(...), with Class a simple class name, or new Class(...); it is followed
/// member by member through that class's own members and the test classes they call the same way.
/// After new Class(...), only the constructor is followed when the class declares one.
/// <para>
/// A construct the check recognises but cannot follow, such as a cited name whose declaration it
/// cannot find, a switch leading to a stub over a value it cannot tie to the running journey or, in
/// a helper chain, an unqualified call it cannot find, is rejected rather than accepted. Known
/// forms it does not read, and so does not check:
/// </para>
/// <list type="bullet">
/// <item>Code the test framework runs without the test naming it: initialize and cleanup hooks in
/// any class, the NativeSessionTests constructor, Dispose and DisposeAsync, and field and property
/// initializers. It is not treated as part of the tests it runs around: a helper chain reaches it
/// only when the test names it, and such code that is itself a stub is caught only by chance, when
/// its name appears elsewhere in the fixture.</item>
/// <item>In a helper chain, a NativeSessionTests member named with a qualifier (this., base.,
/// NativeSessionTests., an instance or a field): it is not followed, so a stub it reaches further
/// on is missed.</item>
/// <item>Calls through any instance or field of another test class, and extension-method
/// calls.</item>
/// <item>Calls into another test class written with a namespace, an enclosing class or generic
/// arguments on the class, or with target-typed new(); property reads, method groups and delegates
/// of another test class; and members another test class inherits from its base class.</item>
/// <item>A variable that a lambda or local function redeclares under the journey parameter's name
/// in a form the check does not recognise as a declaration.</item>
/// <item>An exception class declared outside NativeSessionTests that derives from
/// AssertInconclusiveException and is raised under its own name.</item>
/// <item>An unqualified call made available by using static, such as a bare Inconclusive(...), in
/// fixture code that a direct citation runs. A helper chain rejects it as a call it cannot
/// find.</item>
/// <item>Code outside the test sources.</item>
/// </list>
/// VerificationEvidenceRules in CapabilityCatalogTests.cs states the full rules.
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

/// <summary>One request type that the native handshake lists as dispatched to a registered handler.</summary>
public sealed record NativeRequestCapability(string Name, string Availability);

/// <summary>
/// Work that no registered tool provides. Tools listed in RegisteredToolsInScope share the scope and
/// remain available within their own declared contracts; the summary names only what is missing.
/// TrackedBy names the KAICad completion-ledger outcomes (p...) and recorded decisions that hold
/// the remaining work, so the summary can be traced to its authoritative record.
/// </summary>
public sealed record CapabilityLimitation(string Id, string Scope, string Summary, IReadOnlyList<string> RegisteredToolsInScope,
                                          IReadOnlyList<string> TrackedBy);

/// <summary>
/// The native half of the catalogue. Features are the named feature contracts of the handshake's
/// capabilities field, each advertised only when the whole feature works; an entry that is not a
/// feature contract name is left out. RequestCoverage says
/// whether the handshake lists handled requests: "handled-requests" when it does, and "unknown"
/// for a KiCad built before that field, whose Requests are then null rather than guessed.
/// </summary>
public sealed record NativeCapabilityCatalog(IReadOnlyList<string> Features, string RequestCoverage,
                                             IReadOnlyList<NativeRequestCapability>? Requests);

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
        "nativeFeatures lists the named feature contracts the native process advertises, for example session.info; each is advertised only when the whole feature works in that build. A feature contract is a lower-case dotted name, which no protocol message type is; any other entry of the handshake's capabilities is left out rather than presented as a feature.",
        "nativeRequests lists the request types the native process dispatches to a registered handler at this moment, when nativeRequestCoverage is 'handled-requests'. Opening or closing an editor changes the list, and a handler can still reject a request for its target, its arguments, the editor's state or a mode this process does not support. A KiCad built before this list existed reports nativeRequestCoverage 'unknown' and nativeRequests null: its request support is not known, not empty.",
        "verification.level names the strongest evidence in this build's test suite: mcp-native-journey, native-journey, mcp-process, in-process, or undeclared when a tool declares none.",
        "limitations name unfinished work that no registered tool provides; they do not withdraw any registered tool. trackedBy cites the KAICad completion-ledger outcomes and recorded decisions behind each one."
    ];

    // Unfinished outcomes that cannot be derived from code. Each names what is missing, never a
    // registered tool, so it cannot contradict a tool's availability, and cites the ledger outcomes
    // and decisions that track it.
    private static readonly (string Id, string Scope, string Summary, string[] TrackedBy)[] Unfinished =
    [
        ("pcb-routing-completion", "pcb-routing",
            "The pcb-routing tools preview, propose, validate and measure routes; none of them commits copper, which is added only through the primitive kicad_pcb_items_create edit. Committing a validated candidate with undo and rollback, layer changes and vias, full push-and-shove, differential-pair and tuning modes, failure and cancellation recovery, and DRC, impedance or RF qualification of routed results are unfinished.",
            ["p09bc531842a6fe23"]),
        ("simulation-qualification", "simulation",
            "The simulation tools run KiCad's own ngspice on an explicit schematic and are verified on Linux only. Capturing every model and library file a simulation depends on, broader simulation qualification, and qualification on Mac, Windows and through Codex Desktop are unfinished.",
            ["p682f6173a40ec389", "pbf17c124768447fb"]),
        ("agent-client-qualification", "qualification",
            "Operation through an actual agent client is verified only for the Linux Codex fixture. The Mac-local and Mac-to-VPS modes and other agent clients are not qualified.",
            ["p8bf96f1c4b709a28", "p9bdc0986708bd9b2"]),
        ("exited-operation-release", "schematic-design",
            "When KiCad ends while an XML synchronization is applying its edit or saving it, the design recovery record keeps that operation exactly, and the operation is never replayed on another KiCad process: reattaching the record and resuming the operation are refused. Releasing such an operation so that a restarted KiCad can resume the design from its saved files is unfinished; a KiCad that ends while synchronization is idle resumes normally.",
            ["pcb5513dd69eda714"]),
        ("platform-qualification", "qualification",
            "The KiCad design workflow through this MCP server (native editing, synchronization and capability discovery) is verified only on Linux, where this build's native editing journeys run. Installed Mac (Apple Silicon and Intel) and Windows packages are not qualified for it; their separate startup, packaged-MCP, installer and update-helper runs do not qualify this workflow, and new Mac and Windows builds are on hold until the XML editing workflow is complete.",
            ["kicad-hold-mac-windows-until-xml-editor", "p9bdc0986708bd9b2", "p4d6c4ee22fd8078d", "p67f11d25763f499e"])
    ];

    public static IReadOnlyList<ServiceToolCapability> Service(IEnumerable<McpServerTool> tools)
    {
        // Transitional: rows for tools that do not carry [KiCadCapability] yet. The attribute wins
        // where both exist, and CapabilityCatalogTests requires the two to agree.
        var legacy = new Dictionary<string, InstanceCapability>(StringComparer.Ordinal);
        foreach (var row in ServiceCapabilities.Registered) legacy.TryAdd(row.Name, row);
        return tools.Select(tool => Describe(tool, legacy)).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
    }

    public const string HandledRequestCoverage = "handled-requests", UnknownRequestCoverage = "unknown";

    // Feature contracts are lower-case dotted names (session.info, schematic.connection-realization.v1).
    // Every protocol message type has an upper-case message name, so no request type name matches;
    // CapabilityCatalogTests checks that over the whole protocol.
    private static readonly Regex FeatureContractName = new(@"^[a-z][a-z0-9-]*(\.[a-z0-9-]+)+$", RegexOptions.CultureInvariant);

    /// <summary>Whether a handshake capabilities entry names a feature contract rather than, for example, a request type.</summary>
    public static bool IsFeatureContract(string name) => FeatureContractName.IsMatch(name);

    public static NativeCapabilityCatalog Native(AutomationSession session)
    {
        // Only feature contract names are published as features, so a request type name in the
        // capabilities field is never presented as a feature.
        string[] features = session.Capabilities.Where(IsFeatureContract).ToArray();
        // A peer that answers the handshake dispatches the handshake request itself, so an empty
        // list can only come from a KiCad built before handled_requests: coverage unknown, never
        // "handles nothing", and its feature labels are never read as handlers.
        if (session.HandledRequests.Count == 0) return new(features, UnknownRequestCoverage, null);
        return new(features, HandledRequestCoverage,
            session.HandledRequests.Select(name => new NativeRequestCapability(name, "handler-registered")).ToArray());
    }

    public static IReadOnlyList<CapabilityLimitation> Limitations(IReadOnlyList<ServiceToolCapability> service) =>
        Unfinished.Select(item => new CapabilityLimitation(item.Id, item.Scope, item.Summary,
            service.Where(tool => tool.Scope == item.Scope).Select(tool => tool.Name).ToArray(), item.TrackedBy)).ToArray();

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
