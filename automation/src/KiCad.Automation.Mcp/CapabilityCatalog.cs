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
/// the test sources: an mcp-native-journey claim needs a cited NativeSessionTests journey and a
/// call to the tool by name in a NativeSessionTests source (the Linux native journeys); a
/// native-journey claim needs a cited NativeSessionTests journey; every other cited class of an
/// mcp-native-journey, native-journey or mcp-process claim must start the compiled MCP STDIO server
/// and call the tool by name. Comments never count as calls. The call check is per
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
/// RunNativeSessions(NativeJourney.X[, further arguments that are each a single identifier or
/// keyword, such as a parameter name, true or null, or a single string or character literal on one
/// line with no interpolation holes]), optionally awaited. Any other body that names
/// RunNativeSessions, such as a block body making the same call, is read as a helper chain, which
/// rejects it. A direct citation is rejected when a switch expression over the running journey
/// sends X to a stub or has a default arm leading to a stub that X may reach. A switch is read as
/// dispatch only over a parameter that always holds the journey being run, passed on unchanged from
/// RunNativeSessions. A default arm is limited only by earlier switches over that same parameter,
/// each a whole assignment statement of the member's own block whose arms name journeys, except a
/// discard arm (_) that throws for any other journey, and only when the default arm's switch also
/// runs directly in that block, not inside a lambda, local function or nested block. Every direct
/// citation is rejected when a stub is named anywhere in the fixture other than in its declaration
/// and as the call a switch expression arm makes when the arm has no when clause and its pattern is
/// _ or one or more NativeJourney values joined by the keyword or (NativeJourney.X => Stub(...),
/// NativeJourney.X or NativeJourney.Y => Stub(...), _ => Stub(...)), when a switch over any other
/// value leads to a stub, or when any fixture member makes a call into another test class that may
/// reach a stub.</item>
/// <item>A helper-chain citation (any other cited method), followed through every
/// NativeSessionTests member it names without a qualifier, to any depth. It is rejected when a
/// reached member, including the cited test itself, is or names a stub, names NativeJourney or
/// RunNativeSessions, makes an unqualified call the check cannot find, or makes a call into another
/// test class that may reach a stub.</item>
/// </list>
/// Another test class is any class other than NativeSessionTests whose declaration line in the test
/// sources begins with the class keyword, after only access, static, sealed, abstract, partial or
/// file modifiers; records, structs and interfaces are not read. A call into one is followed only
/// when written Class.Member(...), with Class a simple class name, or new Class(...). From each
/// member reached there, the check follows the members of its own class named without a qualifier
/// and the calls made in the same two forms into test classes, but never a call back into
/// NativeSessionTests. After new Class(...), only the constructor is followed when the class
/// declares one, and every member of the class when it does not.
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
/// only when a member the chain follows names it, and such code that is itself a stub is caught
/// only by chance, for example when its name appears elsewhere in the fixture.</item>
/// <item>In a helper chain, a NativeSessionTests member named with a qualifier (this., base.,
/// NativeSessionTests., an instance or a field): it is not followed, so a stub it reaches further
/// on is missed.</item>
/// <item>Calls through any instance or field of another test class, and extension-method calls.
/// Only the new Class(...) that creates such an instance is followed, as above, never the later
/// call.</item>
/// <item>Calls into another test class written with a namespace, an enclosing class or generic
/// arguments on the class, with target-typed new(), or as an object initializer with no
/// parentheses (new Helper { ... }); members another test class inherits from its base class; and
/// calls into records and structs.</item>
/// <item>Property and field reads and method groups of another test class, including a delegate
/// passed on or invoked through Invoke or ?.Invoke (Helper.Value, Helper.Callback.Invoke(),
/// Run(Helper.Callback), Run(Helper.Pending)). Only a direct call of a delegate field or property,
/// Helper.Callback(...), is followed, and then only to that member's own declaration, initializer
/// included.</item>
/// <item>Inside another test class, its own members named through this (this.Member(...)), and
/// calls back into NativeSessionTests.</item>
/// <item>A variable that a lambda or local function redeclares under the journey parameter's name
/// in a form the check does not recognise as a declaration.</item>
/// <item>An exception class declared outside NativeSessionTests that derives from
/// AssertInconclusiveException and is raised under its own name.</item>
/// <item>An unqualified call made available by using static, such as a bare Inconclusive(...) or a
/// helper of another test class. A bare Inconclusive(...) is never read as a stub, and such a call
/// is never followed into the class it comes from. A helper chain rejects it, as a call it cannot
/// find, only in a NativeSessionTests member the chain reaches. It is missed in any other test
/// class, whichever way the test is cited, and in fixture code that a direct citation runs.</item>
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
/// DocumentKinds names the native document or job boundaries of the declared scope; an empty list
/// is deliberately unknown or not document-scoped.
/// </summary>
public sealed record ServiceToolCapability(string Name, string Availability, string Declaration, string? Scope,
                                           string? Source, string? RevisionContract, bool? ReadOnly,
                                           ToolVerification Verification, IReadOnlyList<string> DocumentKinds);

/// <summary>One request type that the native handshake lists as dispatched to a registered handler.</summary>
public sealed record NativeRequestCapability(string Name, string Availability);

/// <summary>
/// Work that no registered tool provides. Tools listed in RegisteredToolsInScope share the scope and
/// remain available within their own declared contracts; the summary names only what is missing.
/// TrackedBy names the KAICad completion-ledger outcomes (p...) and recorded decisions that hold
/// the remaining work, so the summary can be traced to its authoritative record.
/// </summary>
public sealed record CapabilityLimitation(string Id, string Scope, string Summary, IReadOnlyList<string> RegisteredToolsInScope,
                                          IReadOnlyList<string> TrackedBy, string Availability = "incomplete");

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
    public const int SchemaVersion = 3;

    public static IReadOnlyList<string> Notes { get; } =
    [
        "serviceCapabilities lists every tool registered in this MCP server process, derived from the server's tool collection; availability 'registered' means the tool can be called, subject to its revision contract.",
        "nativeFeatures lists the named feature contracts the native process advertises, for example session.info; each is advertised only when the whole feature works in that build. A feature contract is a lower-case dotted name, which no protocol message type is; any other entry of the handshake's capabilities is left out rather than presented as a feature.",
        "nativeRequests lists the request types the native process dispatches to a registered handler at this moment, when nativeRequestCoverage is 'handled-requests'. Opening or closing an editor changes the list, and a handler can still reject a request for its target, its arguments, the editor's state or a mode this process does not support. A KiCad built before this list existed reports nativeRequestCoverage 'unknown' and nativeRequests null: its request support is not known, not empty.",
        "documentKinds on a service capability names the native document or job kinds its declared scope targets: schematic, pcb, library, job or simulation. An empty list means this scope does not make a document-kind claim; it is never inferred from a tool name or a limitation.",
        "verification.level names the strongest evidence in this build's test suite: mcp-native-journey, native-journey, mcp-process, in-process, or undeclared when a tool declares none.",
        "limitations name unfinished work that no registered tool provides; availability distinguishes incomplete capability from platform-specific or unsupported capability without withdrawing any registered tool. trackedBy cites the KiCad completion-ledger outcomes and recorded decisions behind each one."
    ];

    // Unfinished outcomes that cannot be derived from code. Each names what is missing, never a
    // registered tool, so it cannot contradict a tool's availability, and cites the ledger outcomes
    // and decisions that track it.
    private static readonly (string Id, string Scope, string Summary, string[] TrackedBy)[] Unfinished =
    [
        ("pcb-routing-completion", "pcb-routing",
            "The pcb-routing tools preview, validate, measure, constrain, duplicate/zero-length cleanup, explicit-net rip-up candidate generation, safe degree-two collinear corner-optimization planning/apply, deterministic via-span minimization planning, lifecycle-checked reroute replacement apply/receipt tools, and versioned direct ordinary-group ownership deltas plan preliminary single-layer and additive multilayer routes, explicit-corridor serpentine tuning proposals and matched differential-pair geometry. A read-only differential-pair validator now reports exact spacing, copper-edge gap, endpoint correspondence, width, layer, parallelism and skew for one straight track per net and returns an explicit incomplete result for arcs, vias, multi-segment or unknown geometry. Native route deltas carry complete create/update/delete identities and properties into a bounded checked commit receipt, with operation-ID replay, stale admission, cleanup outcomes and post-commit state; the fresh Linux native checkpoint for this integrated path is still pending. Grouped routing now has typed version-2 before/after membership, source links, DRC proof, replay and undo journeys in the source, but direct ordinary groups remain unqualified until that checkpoint. Via minimization emits only final replacement vias and rejects incompatible drill/padstack profiles; tuning reports physical segment length. Persistent PNS previews retain a detached branch across continue/read/cancel with operation identity, process-epoch and revision checks; cancellation and stale-board reconciliation are terminal and never commit copper. Remaining gaps include custom-property ordering/duplicate-key normalization, partial generator failure, nested/generator/design-block ownership, complete native failure recovery, corner/via durable receipts, full interactive GUI parity, and production geometry/impedance/RF qualification.",
            ["p09bc531842a6fe23", "p6c4984d8d18debaf", "pae1ffb62ea2e4c93", "pedd037392746b32b"]),
        ("pcb-group-routing-qualification", "pcb-routing",
            "Checked routing preserves direct ordinary unlocked PCB_GROUP membership in its typed preview, detached DRC candidate and single undo transaction, with exact replay and unsupported ownership rejection covered by managed contracts and a native journey. Live qualification remains pending; nested, generator-owned and design-block groups remain fail-closed.",
            ["pee706b40550ea1f3", "p3ab3b850b0c851a3"]),
        ("pcb-manufacturing-qualification", "pcb-manufacturing",
            "The PCB manufacturing export tools are registered, with typed settings and receipt-backed lifecycle identity for the export families. The focused Linux MCP journey now passes against the existing native binaries, covering successful typed exports, exact replay, MCP reconnect receipt inspection, output manifests and refusal paths. Receipts and durable recovery records now bind to the exact submitted request fingerprint. Native cancellation, failure and process-restart recovery remain unqualified, so the full manufacturing surface is not production-ready.",
            ["p1f3c61ce83c7d797"]),
        ("pcb-operations-qualification", "pcb-operations",
            "PCB netlist import, zone refill, GUI cross-probe and revision-paired project netclass-read tools are registered with lifecycle guards. Netlist import and zone refill carry operation IDs into the native handler, replay exact requests and retain native-process receipts readable after an MCP reconnect. Cross-probe admission now has managed coverage for exact document/revision binding, GUI-only selection/highlight semantics, malformed requests, stale epochs and wrong-instance refusal; live native cross-probe qualification remains open. New peers expose explicit terminal zone outcomes; the bounded wait tool reports completion only from a matching native Completed=true receipt, while editor responsiveness proves lifecycle settlement only. Older peers without a terminal receipt remain explicitly unconfirmed even after settlement. Native headless success receipts still do not independently prove fill geometry, and headless failure/cancellation handling remains unqualified. The Linux PCB journey qualifies a successful two-footprint netlist import, changed net assignment, save/close/reopen persistence, receipt recovery and lifecycle observation, and exercises explicit pad selection plus net highlighting without board mutation. The native net-settings journey now also qualifies project netclass reads, stale lifecycle refusal and no-mutation recovery; terminal-result qualification across failure/recovery and broader cross-platform qualification remain open.",
            ["pa512d3c7c739352d"]),
        ("pcb-connectivity-query", "pcb-connectivity",
            "The typed PCB connectivity query reads one named net at a complete lifecycle checkpoint and returns deterministic pad, track, arc, via and zone summaries with canonical identities, parent/layer data and fail-closed handling for unknown native items. It is currently qualified only by managed normalization and compiled STDIO discovery/refusal checks; live native net contents, stale-state behavior on a real board, concurrent-instance isolation and save/reopen evidence remain open.",
            ["pfb6962d39fc1f813"]),
        ("pcb-placement-authoring", "pcb-placement",
            "The footprint placement tools validate an explicit board checkpoint, emit deterministic Any-packed placement candidates, provide an undoable apply with MCP-process replay and receipt inspection, and prepare strict SVG-derived footprint graphics with asset provenance. A managed footprint validator checks canonical identities, child types, unique pads, pad-stack layers, positive pad sizes and 3D model filenames/transforms before native contact; schematic-to-PCB assignment validation now compares the exact library:name footprint and shared sheet path without inferring pin maps or persistence. The Linux MCP PCB journey qualifies plan/apply/readback for placement. The managed apply still coordinates several IPC calls rather than one retained native authoring transaction. Complete transaction recovery, electrical/DRC qualification, generated-artwork validation and footprint-library lifecycle remain open.",
            ["p74ccef564f23a805", "pe0d8a4f922a0c00e", "peb739e0453b017ee"]),
        ("pcb-item-transactions", "pcb",
            "The checked PCB item transaction now exposes one ordered native create/update/delete command with full lifecycle admission, process epoch, operation identity, bounded retained receipts, exact replay, post-commit observation and fail-closed rollback states. A focused Linux MCP fixture now exercises checked create/update/delete, replay and rollback receipts. Legacy item wrappers still use separate BeginCommit/item/EndCommit calls, and broad native persistence, cancellation, transport-loss recovery and concurrent-instance proof remain open.",
            ["p74ccef564f23a805"]),
        ("pcb-keepout-authoring", "pcb-zones",
            "Typed rule-area keepout tools now expose explicit physical layers, polygon holes and separate restrictions for copper, vias, tracks, pads and footprints, with lifecycle guards, native readback and fail-closed rollback reporting. Create/update/delete now use native checked receipts; update retries reuse the exact merged request, including native-owned metadata, before any live target read. Native save/reopen, undo/redo, routing/DRC enforcement, cross-process recovery and cross-platform qualification remain open.",
            ["p079fb6adb164b14c", "p74ccef564f23a805"]),
        ("pcb-zone-authoring", "pcb-zones",
            "Typed copper-zone read/create/update/delete tools are lifecycle-guarded and now route through checked PCB item receipts with exact operation replay, cached update envelopes and fail-closed target validation. Committed create/update receipts validate authored geometry and settings; replies expose authoredReadbackVerified and refillRequired, and postCommitVerified requires both lifecycle confirmation and authored readback. Updates invalidate native-derived fill rather than preserving stale polygons; callers must use the separate refill operation before treating copper as filled. Graphical, rule-area and teardrop zones, native save/reload, native zone journey qualification and broader cross-platform qualification remain fail-closed or unfinished.",
            ["p74ccef564f23a805", "p16f42b1cbade388b"]),
        ("pcb-artwork-authoring", "pcb-artwork",
            "Non-copper SVG artwork commits retain source archive and SHA-256 provenance with a lifecycle checkpoint and an MCP-process receipt. They still use the general multi-call item transaction boundary; complete native atomic commit/recovery and generated-raster-to-CAD validation remain unfinished.",
            ["p74ccef564f23a805", "pe0d8a4f922a0c00e"]),
        ("pcb-layer-rendering", "pcb-rendering",
            "The PCB rendering tools provide native 3D PNGs, composite/per-layer SVG exports, a revision-bound per-layer object manifest with friendly and canonical layer names, native object/model bounding boxes paired with an explicit nanometre-to-pixel transform, and typed pad/via pad-stack and drill metadata, zone outlines/holes and board-graphic geometry. Managed render metadata validation now gates completeness on exact camera dimensions/conventions/hashes, finite invertible transforms, one-to-one model/object rows, loaded asset hashes and finite bounds; invalid metadata cannot claim exact pixels or occlusion. Live native qualification of the typed overlay payload remains pending; full track and footprint-child mapping, occlusion pixels and complete layer overlays remain open.",
            ["pc840ffe0134ebf34", "pe0d8a4f922a0c00e"]),
        ("raster-trace-proposal", "artwork",
            "The typed raster-trace tool reads exact PNG bytes, verifies their hash and bounded dimensions before decoding, reuses KiCad's Potrace boundary, returns deterministic integer-nanometre contours with signed parent/hole topology and filled pieces, and rejects malformed, oversized or over-complex proposals. A governed adapter check passes; full MCP/native qualification remains pending.",
            ["p0963bdff2c16fe89", "pe0d8a4f922a0c00e"]),
        ("raster-cad-projection", "artwork",
            "The raster projection tool can map accepted filled pieces into symbol, footprint, schematic and non-copper board artwork while retaining source hashes and declared provenance. Symbol/footprint/schematic embedded-source paths and board external archive limitations are explicit; native save/reload and cross-target qualification remain pending.",
            ["p8e3658579737b432", "pe0d8a4f922a0c00e"]),
        ("footprint-library-authoring", "footprint-library",
            "The footprint editor wrappers can open, enumerate, delete, save, save-copy and revert explicitly identified library footprints through native handlers. A focused Linux MCP journey now passes open/save/save-copy/reopen with matching pad and graphic snapshots, delete/replay and native receipt recovery after reconnect. Mutation replies still report library-entry observation only, with geometryPersistenceVerified=false rather than treating name enumeration as independent saved-geometry proof. Complete library revision snapshots, rollback qualification and general persisted-geometry qualification remain unfinished.",
            ["peb739e0453b017ee"]),
        ("symbol-library-authoring", "symbol-library",
            "The native symbol-library adapter now exposes typed list, read, write, clone and delete MCP commands with before/after modify hashes; managed validation and strict SVG symbol-artwork proposals check complete definitions before native contact. Generated artwork carries per-shape preview provenance plus a private field manifest that survives native .kicad_sym write/read, and writes/deletes can require the observed modify hash while preserving exact operation-ID replay. A Linux MCP journey qualifies list/read/write/stale-admission/replay/delete/restore and generated-artwork provenance against a disposable project library. Global symbol-library documents still lack a durable lifecycle revision snapshot, while write/delete replies retain process-owned receipts.",
            ["peb739e0453b017ee", "p2d3aca18552132fe"]),
        ("schematic-authoring-completion", "schematic-design",
            "Typed wire, bus, junction, bus-entry, label and text creation is registered with checked revision admission and its live native qualification segment passes. Create now also requires a root CheckedSchematicState and is replay-safe; managed refusal/replay coverage passes, while real editor persistence, undo/redo and native wire/label qualification remain open. Existing connection items now also have typed read, patch-update and delete tools that preserve complete native label fields/properties and use retained checked receipts for exact replay. Checked symbol-instance create/read/update/delete tools now accept complete instance payloads and retain exact native operation receipts; the Linux native MCP journey passes create/read/update/delete, exact create/update replay, readback and cleanup on two disposable instances, while broader library-definition authoring, hierarchy, connectivity/ERC and cross-platform qualification remain open. Embedded schematic library-cache replacement, revision-paired live ERC-marker inspection and operation-replay-safe headless ERC execution are implemented through the native boundary; managed ERC execution contracts now reject stale/failed/indeterminate results, while the combined native ERC dialog journey exercises legacy Run, typed Execute, replay and reconnect receipts and its post-integration native qualification remains pending. Hierarchical and repeated/shared sheets, buses, images, title data, variants, embedded files and the remaining authoring families still need complete native persistence and electrical-repair qualification; zero ERC markers do not prove electrical completeness.",
            ["p78cb4c45bc18af7b", "pb0cc4fd249f3fb96"]),
        ("simulation-qualification", "simulation",
            "The simulation tools run KiCad's own ngspice on an explicit schematic and are verified on Linux only. Caller-supplied decks reject interpreter directives and external file includes; operation IDs are bound to the instance, process epoch, schematic and deck hash, MCP results retain inline deck SHA-256 provenance plus native job/status/vector validation, and generated-deck dependency reads return the actual retained deck hash with explicit incomplete dependency coverage and warnings. Native dependency path enumeration, broader simulation qualification, and qualification on Mac, Windows and through Codex Desktop are unfinished.",
            ["p682f6173a40ec389", "pbf17c124768447fb"]),
        ("agent-client-qualification", "qualification",
            "Operation through an actual agent client is verified only for the Linux Codex fixture. The Mac-local and Mac-to-VPS modes and other agent clients are not qualified.",
            ["p8bf96f1c4b709a28", "p9bdc0986708bd9b2"]),
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
            service.Where(tool => tool.Scope == item.Scope).Select(tool => tool.Name).ToArray(), item.TrackedBy,
            item.Id == "platform-qualification" ? "platform-specific" : "incomplete")).ToArray();

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
            return new(name, "registered", "attribute", declared.Scope, declared.Source, declared.RevisionContract, readOnly,
                verification, DocumentKinds(declared.Scope));
        if (legacy.TryGetValue(name, out var row))
            return new(name, "registered", "legacy-service-list", row.Scope, row.Source, row.RevisionContract, readOnly,
                verification, DocumentKinds(row.Scope));
        return new(name, "registered", "undeclared", null, null, null, readOnly, verification, []);
    }

    // Scope names are the existing capability contract. Keep the mapping here rather than
    // guessing from tool names: a new scope has no document-kind claim until its owner declares
    // one. The values describe the target boundary, not a promise that every operation in that
    // boundary is qualified or that an unsupported native request will be accepted.
    private static IReadOnlyList<string> DocumentKinds(string? scope) => scope switch
    {
        // The document scope contains both the mixed open-document query (including library
        // entries) and schematic/PCB open/create operations. It therefore makes no single
        // document-kind claim until those methods carry explicit metadata.
        "document" => [],
        "document-lifecycle" => ["schematic", "pcb"],
        "schematic-design" => ["schematic"],
        "symbol-library" or "footprint-library" => ["library"],
        "simulation" => ["schematic", "simulation"],
        "pcb-manufacturing" => ["pcb", "job"],
        // Artwork projection accepts several target kinds under one scope, so do not turn its
        // mixed target enum into a false per-tool claim.
        "generated-artwork-cad" => [],
        "pcb" => ["pcb"],
        "pcb-artwork" or "pcb-connectivity" or "pcb-guide" or "pcb-observation"
            or "pcb-operations" or "pcb-placement" or "pcb-rendering" or "pcb-routing" or "pcb-rules"
            or "pcb-zones" => ["pcb"],
        _ => []
    };
}
