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
            "The pcb-routing tools preview, validate, measure, constrain, duplicate/zero-length cleanup, explicit-net rip-up candidate generation, safe degree-two collinear corner-optimization planning/apply, deterministic via-span minimization planning with Via-only checked apply/receipt wrappers, lifecycle-checked reroute replacement apply/receipt tools, and versioned direct ordinary-group ownership deltas plan preliminary single-layer and additive multilayer routes, lifecycle-bound constrained centerline meanders with real requested length addition, explicit-corridor serpentine tuning proposals and matched differential-pair geometry. Differential-pair previews now bind an optional endpoint item and preserve the complete native candidate; their DRC-gated checked apply and process receipt wrapper accepts at least two Track objects containing only the two requested positive and negative net names on one copper layer, including multi-segment candidates. Multilayer proposals now have a DRC-gated checked Track/Via apply and process receipt wrapper that rejects non-copper or malformed geometry before native dispatch. A read-only differential-pair validator remains limited to one straight track per net: it reports exact spacing, copper-edge gap, endpoint correspondence, width, layer, parallelism and skew and returns an explicit incomplete result for arcs, vias, multi-segment or unknown geometry. Fresh governed PCB-pair run t20261009T093116Z-6f9b1c qualifies the endpoint-bound preview, complete candidate, finding-free detached DRC, checked apply, native receipt/replay, connectivity, undo/redo and save/reopen; retained pcb-tests sha256 2354aa58782013d7bdf5a74ad6e811a2b4b953949c2243c94ab7094bf6e7b069 and pcb-evidence sha256 ea5c1e118ddf7b0ba15eb2163a16aaa2b4b694b7837ccac6b7834c214abf2d58. Native route deltas carry complete create/update/delete identities and properties into a bounded checked commit receipt, with operation-ID replay, stale admission, cleanup outcomes and post-commit state. The source-stable Linux NativePcbItems leaf t20261008T211437Z-df490b now qualifies the multilayer Track/Via apply path with exact detached DRC, native receipt and replay, process receipt lookup, electrical connectivity, foreign-obstacle preservation, undo/redo and save/reopen; the same journey continues to cover native push-and-shove delta commit/replay/save-reopen. The source-stable Linux PCB fixture-retry t20261013T235510Z-071f36 qualifies the live native free-angle GUI drag path with connected two-segment route geometry, route-set change, pad connectivity, undo/redo, save/reopen and editor survival. Focused governed native run t20261014T071633Z-cead47 now explicitly executes qa_pcbnew_pns_detached_update and passes it with the zone-fill and DRC lifecycle tests (3/3). Cleanup and corner replacement now use checked native item transactions; DRC-bound reroute and tuning use checked route transactions with exact replay while the original KiCad process is running. The route-optimization receipt wrapper now persists successful terminal results in MCP state, holds a durable per-operation file lease across read/native/write admission, validates embedded instance/process/operation identities, and recovers exact results after an MCP restart; governed managed run t20261014T065756Z-255094 passes restore, managed-build and managed-contracts source-stably with no failures. Native process receipts remain a separate live-editor concern: read-only native route receipt recovery validates the complete request, operation and process identity before publishing a result and never resubmits a mutation, while native process restart, unknown/indeterminate route outcomes, native via/tuning breadth, and production geometry qualification remain open. Focused native DRC provider/job contracts also pass after generator ownership repair. Grouped routing now has typed version-2 before/after membership, source links, DRC proof, replay and undo journeys in the source, but nested, generator-owned and design-block groups remain unqualified. Via minimization emits only final replacement vias and rejects incompatible drill/padstack profiles; tuning reports physical segment length. Persistent PNS previews retain a detached branch across continue/read/cancel with operation identity, process-epoch and revision checks; cancellation and stale-board reconciliation are terminal and never commit copper. Remaining gaps include custom-property ordering/duplicate-key normalization, partial generator failure, advanced grouped ownership, complete native failure recovery, native via-minimization qualification/cross-process receipts, full interactive GUI parity, native tuning breadth, and production geometry/impedance/RF qualification.",
            ["p09bc531842a6fe23", "p6c4984d8d18debaf", "pae1ffb62ea2e4c93", "pedd037392746b32b"]),
        ("pcb-group-routing-qualification", "pcb-routing",
            "Checked routing preserves direct ordinary unlocked PCB_GROUP membership in its typed preview, detached DRC candidate and single undo transaction, with exact replay and unsupported ownership rejection covered by managed contracts and a native journey. The core NativePcbItems route journey now passes source-stably; nested, generator-owned and design-block groups remain fail-closed.",
            ["pee706b40550ea1f3", "p3ab3b850b0c851a3"]),
        ("pcb-drc-operation-recovery-qualification", "pcb",
            "The read-only native PCB DRC operation lookup is registered with exact PCB document, original operation ID and current process epoch. It returns an existing job through the normal freshness checks and reports found=false with unknown retry safety when the native owner has no retained record. Managed contracts now explicitly reject foreign-observer marker inventories in source-stable run t20261014T004414Z-de072d. The source-stable Linux NativePcbItems journey exercises the lookup before polling a real DRC job; native editor-process restart retention remains open, and operation lookup does not replace the explicit candidate-payload checks required before committing a checked route.",
            ["p4aa00ee192dd3acd", "kicad-drc-readonly-operation-recovery-20261008"]),
        ("pcb-manufacturing-qualification", "pcb-manufacturing",
            "The PCB manufacturing export tools are registered with typed settings and receipt-backed lifecycle identity. The native manager durably records dispatch intent, closes uncertain adapter outcomes as terminal indeterminate, and synchronously publishes SVG through KiCad’s owner-thread plot path. Commit 9bf8de5104 also requires request-fingerprint and journal proofs for not-found reconciliation responses, so an unverified absence cannot authorize a retry. Commit 37997847b6 now rechecks completed receipts against the owned output directory, exact file set, byte counts, SHA256 values and manifest before read or restart reconciliation; changed, missing, redirected or extra output is refused without rerunning the exporter, and the typed MCP requires the verification proof. Its new manager-owned worker boundary copies the source/dependency checkpoint, supports cooperative cancellation and join, contains worker exceptions, and terminalizes prior-epoch running work through explicit restart reconciliation. Managed qualification t20261014T130047Z-ebffd3 passes 415 typed contracts; focused governed run t20261014T141344Z-a5d74d passes qa_pcbnew_drc_job_lifecycle 1/1 for the completed-output tamper refusal and no-rerun path. A proven detached production exporter, complete dependency enumeration and atomic production publication remain unqualified, so the full manufacturing surface is not production-ready.",
            ["p1f3c61ce83c7d797"]),
        ("schematic-manufacturing-qualification", "schematic-manufacturing",
            "The schematic manufacturing wrappers are registered for SVG, DXF, PDF, PostScript, netlist and BOM jobs. They require an explicit schematic root and lifecycle checkpoint, refuse dirty native content, dispatch the existing native protobuf handlers and return bounded hashed output manifests with post-job state verification. Optional operation IDs now bind MCP-process export receipts with exact replay and conflict refusal; missing receipts and transport loss remain explicitly unknown and are not retried automatically. Linux live output is qualified; durable recovery and cross-platform qualification remain open under task pc7459e02fa90eafb.",
            ["pc7459e02fa90eafb"]),
        ("pcb-operations-qualification", "pcb-operations",
            "PCB netlist import, zone refill, GUI cross-probe and revision-paired project netclass-read tools are registered with lifecycle guards. The checked kicad_project_net_classes_set wrapper now accepts an explicit schematic-root checked state and netclass digest, derives merge/replace changes without dropping unrelated net settings, and delegates to the existing checked net-settings receipt/replay path; its own contract is in-process qualified, while the underlying native net-settings journey remains the live integration evidence. PCB-only project netclass mutation is refused because no equivalent checked project-settings transaction exists, and saving remains an explicit document-lifecycle action. Project text-variable reads use the same explicit project plus lifecycle-document checkpoint and report unsupported peers honestly; typed writes now use the common native owner's map-digest admission, process-owned operation receipt/replay, save/readback verification and explicit rollback outcome while the legacy SDK command remains unchanged. Netlist import and zone refill carry operation IDs into the native handler, replay exact requests and retain native-process receipts readable after an MCP reconnect. Cross-probe admission and live Linux qualification now cover exact document/revision binding, GUI-only selection/highlight receipts, malformed requests, stale epochs/revisions, wrong-instance and wrong-document refusal, and no board mutation in t20261005T011327Z-0af69f. New peers expose explicit terminal zone outcomes; the bounded wait tool reports completion only from a matching native Completed=true receipt, while editor responsiveness proves lifecycle settlement only. Older peers without a terminal receipt remain explicitly unconfirmed even after settlement. Native headless success receipts still do not independently prove fill geometry, and headless failure/cancellation handling remains unqualified. The Linux PCB journey qualifies a successful two-footprint netlist import, changed net assignment, save/close/reopen persistence, receipt recovery and lifecycle observation, and exercises explicit pad selection plus net highlighting without board mutation. Fresh governed native-net-settings run t20261014T111631Z-34bc35 passes source-stably through managed/input contracts, native build, five native codec CTests, the rendered NativeNetSettings journey and PCB lifecycle; its 354-file evidence bundle includes public net-settings API application, XML/screenshots and manual edits. This refreshes the checked native net-settings mutation/replay/readback boundary; PCB-only project-settings mutation remains explicitly refused. Terminal-result qualification across failure/recovery and broader cross-platform qualification remain open.",
            ["pa512d3c7c739352d"]),
        ("schematic-cross-probe-qualification", "schematic-operations",
            "Typed schematic selection and net-highlight wrappers are registered with exact document, process-epoch and lifecycle-revision admission plus MCP-process receipts. Managed contracts cover typed protobuf mapping, stale admission and exact operation replay. Source-stable Linux native journey t20261008T140641Z-736959 now qualifies both public MCP operations against live schematic editors, including exact lifecycle/process identity, stale and wrong-target refusal, exact replay, reconnect receipt semantics and unchanged source/design state. MCP-process receipts remain intentionally non-durable across reconnect, and rendered GUI proof plus broader cross-platform qualification remain open.",
            ["p9b350f7cc194d413"]),
        ("pcb-connectivity-query", "pcb-connectivity",
            "The typed PCB connectivity queries read named nets or netclasses, connectivity closures from source item IDs, and direct item IDs at a complete lifecycle checkpoint. Responses now include machine-readable selector metadata and duplicate direct IDs fail closed; source-stable managed contracts t20261014T004804Z-0ec1bf qualify the selector boundary. They return deterministic footprint, pad, track, arc, via and zone summaries with canonical identities, parent/layer data and fail-closed handling for unknown native items. The governed Linux PCB run t20261005T024812Z-58e191 now qualifies live pad, track, via and copper-zone summaries after typed zone creation/refill and save/close/reopen index refresh, exact lifecycle binding, stale and wrong-instance refusal, and no mutation; durable zone save/reopen/undo-redo, arc breadth and cross-platform coverage remain open.",
            ["pfb6962d39fc1f813"]),
        ("pcb-placement-authoring", "pcb-placement",
            "The footprint placement tools validate an explicit board checkpoint, emit deterministic Any-packed placement candidates, provide an undoable apply through one checked native item transaction with exact replay while the original KiCad process is running, and prepare strict SVG-derived footprint graphics with asset provenance. A managed footprint validator checks canonical identities, child types, unique pads, pad-stack layers, positive pad sizes and 3D model filenames/transforms before native contact; schematic-to-PCB assignment validation now compares the exact library:name footprint and shared sheet path without inferring pin maps or persistence. The Linux MCP PCB journey qualifies plan/apply/readback for placement. The separate placement receipt lookup reads an MCP-process cache; restarting MCP loses that cache, and restarting KiCad loses its native receipts. Complete transaction recovery, electrical/DRC qualification, generated-artwork validation and footprint-library lifecycle remain open.",
            ["p74ccef564f23a805", "pe0d8a4f922a0c00e", "peb739e0453b017ee"]),
        ("pcb-item-transactions", "pcb",
            "The checked PCB item transaction now exposes one ordered native create/update/delete command with full lifecycle admission, process epoch, operation identity, bounded retained receipts, exact replay, post-commit observation and fail-closed rollback states. A focused Linux MCP fixture now exercises checked create/update/delete, replay and rollback receipts. General item, placement, cleanup, artwork and corner replacement wrappers now use checked native item transactions; DRC-bound reroute and tuning use checked route transactions. Managed public-path tests cover exact replay, receipt identity refusals and read-only route recovery using protocol fixtures. Native receipts belong to the originating KiCad process; task-specific receipt lookup tools may instead read an MCP-process cache. Broad native persistence, cancellation, transport-loss recovery, editor-process restart recovery, concurrent-instance proof and cross-platform qualification remain open.",
            ["p74ccef564f23a805"]),
        ("pcb-keepout-authoring", "pcb-zones",
            "Typed rule-area keepout tools expose explicit physical layers, polygon holes and separate restrictions for copper, vias, tracks, pads and footprints, with lifecycle guards, native readback and fail-closed rollback reporting. Create/update/delete use native checked receipts; update retries reuse the exact merged request, including native-owned metadata, before any live target read. Commit 87af27834f now persists the exact checked update envelope across MCP restart, while source-stable managed build t20261014T050106Z-19ca5c and typed contracts t20261014T050220Z-b42c23 pass with 380 retained artifacts. Fresh governed fixture-retry keepout run t20261014T110740Z-b15bc4 passes managed-build and the live NativePcbKeepout journey with 109 retained evidence files (SHA256 44b6162fce0016e9c0884c4e53538bfd95f3488cc691363a2a88fb07a7851a80). Native save/reopen, undo/redo, routing/DRC enforcement, native process recovery and cross-platform qualification remain open.",
            ["p079fb6adb164b14c", "p74ccef564f23a805"]),
        ("pcb-zone-authoring", "pcb-zones",
            "Typed copper-zone read/create/update/delete tools are lifecycle-guarded and route through checked PCB item receipts with exact operation replay, canonical request fingerprints and fail-closed target validation. Committed create/update receipts validate authored geometry and settings; replies expose authoredReadbackVerified and refillRequired, and postCommitVerified requires both lifecycle confirmation and authored readback. Production deserialization now replaces derived fill authoritatively, clears stale polygons on update, preserves copy/undo layer containers and requires separate refill. The governed NativePcbItems journey t20261005T072040Z-382d21 passes typed update invalidation, selected refill terminal geometry, undo/redo, save/close/reopen and process-owned receipt recovery; full cross-platform qualification and broader graphical/rule-area/teardrop zones remain open.",
            ["p74ccef564f23a805", "p16f42b1cbade388b"]),
        ("pcb-artwork-authoring", "pcb-artwork",
            "Non-copper SVG artwork commits retain source archive and SHA-256 provenance with a lifecycle checkpoint and dispatch one checked native item transaction. Archive preflight rejects repository-relative paths that traverse symbolic-link ancestors, and the durable MCP-side artwork receipt store is wired into SVG and non-copper raster-board projection dispatch: exact archive/request identity is reserved before native contact, terminal results replay after MCP restart, admitted or indeterminate outcomes fail closed, invalid native receipts and delivered transport loss persist as unknown, and only proven pre-native rejection releases admission. Source-stable managed run t20261014T062722Z-8a2e82 passes the integrated receipt-store, admitted-boundary reconciliation and pre-native classification contracts (347/347 managed tests; retained slice-managed-contracts SHA256 585a2f7daa265add1e4df8d330a01d0f9157fe585763e07dc455eefa0587129e). Source archive persistence is still separate from the native transaction and provenanceAtomic remains false, so complete atomic commit/recovery across both boundaries, native live recovery, ImageGen provenance and generated-raster-to-CAD validation remain unfinished.",
            ["p74ccef564f23a805", "pe0d8a4f922a0c00e"]),
        ("pcb-layer-rendering", "pcb-rendering",
            "The PCB rendering tools provide native 3D PNGs, composite/per-layer SVG exports, a revision-bound per-layer object manifest with friendly and canonical layer names, deterministic object rows and footprint-child mappings with canonical identity/parent validation, native object/model bounding boxes paired with an explicit nanometre-to-pixel transform, and typed pad/via pad-stack and drill metadata, zone outlines/holes and board-graphic geometry. Geometry layer filters canonicalize friendly or protobuf enum aliases and refuse unknown names; pixel projection keeps the declared image-axis convention and reports viewport intersection without claiming clipped pixels. Managed render metadata validation now gates completeness on exact camera dimensions/conventions/hashes, finite invertible transforms, one-to-one model/object rows, loaded asset hashes, finite bounds and projected bounds that remain inside the declared viewport; invalid metadata cannot claim exact pixels or occlusion. Current-source governed PCB run t20261014T104245Z-f75612 passes NativePcbItems 5/5, including the live kicad_pcb_render_3d_geometry and typed 3D inspection path; current PCB evidence is bound to that source-matched candidate. Earlier managed render contracts t20261014T002628Z-2a13df cover projected-bounds rejection and native-tolerance mirror. Exact raster pixels, occlusion, DRC overlays and complete layer overlays remain open.",
            ["pc840ffe0134ebf34", "pe0d8a4f922a0c00e"]),
        ("raster-trace-proposal", "artwork",
            "The typed raster-trace tool reads exact PNG bytes, verifies their hash and bounded dimensions before decoding, reuses KiCad's Potrace boundary, returns deterministic integer-nanometre contours with signed parent/hole topology and filled pieces, and rejects malformed, oversized or over-complex proposals. Current-source governed raster qualification t20261014T113858Z-61caa8 passes managed-build and NativeRasterTrace with 40 retained evidence files (SHA256 2c389475f48a5007ee3c548167053993a552df39c91e64832be28238b6617476); full target projection and authoritative CAD commit qualification remains pending.",
            ["p0963bdff2c16fe89", "pe0d8a4f922a0c00e"]),
        ("raster-cad-projection", "artwork",
            "The raster projection tool can map accepted filled pieces into symbol, footprint, schematic and non-copper board artwork while retaining source hashes and declared provenance. Focused native journeys now save, close and reopen board, footprint, symbol and schematic targets, verifying filled geometry, source content, custom provenance, embedded source survival and existing-object retention. Commit 67c62dec69 also persists the bounded topology/provenance manifest on the native-persistent schematic artwork group and the managed contract verifies its schema, hashes, contour/piece identities and source links; per-shape custom properties remain live-only. Focused governed run t20261014T141344Z-a5d74d passes the exact NativeRasterSchematicProjectionSurvivesSaveReload journey after a short-TMPDIR harness retry; cross-target rendered comparison and broader rollback qualification remain open.",
            ["p8e3658579737b432", "pe0d8a4f922a0c00e"]),
        ("footprint-library-authoring", "footprint-library",
            "The footprint editor wrappers can open, enumerate, delete, save, save-copy and revert explicitly identified library footprints through native handlers. A checked direct .pretty/.kicad_mod writer structurally validates the complete typed FootprintInstance, captures sibling baselines, locks the provider, persists through KiCad’s native serializer, verifies the target and unrelated entries, and retains exact replay plus rollback or indeterminate receipts. MCP-side checked-write receipts persist exact admissions, terminal native responses and unknown transport outcomes across MCP restart, block unsafe redispatch, and bind public receipt lookup to the exact request before native contact; managed contracts t20261014T050544Z-fe1e49 pass with 387 retained artifacts. Fresh source-stable governed footprint-library-focus run t20261014T105634Z-cc3374 passes managed contracts, native adapter, native footprint and native PCB placement journeys on one source-matched candidate; retained footprint and PCB evidence trees are bound to that run. Screenshot capture remains a separate visual limitation. Placement planning admits the complete state digest plus unchanged file baselines even when the native numeric revision cursor is explicitly partial. Native provider lifecycle, process restart, cross-process writer ownership and cancellation remain open.",
            ["peb739e0453b017ee"]),
        ("symbol-library-authoring", "symbol-library",
            "The native symbol-library adapter now exposes typed list, read, write, clone and delete MCP commands with before/after modify hashes; managed validation and strict SVG symbol-artwork proposals check complete definitions before native contact. Checked symbol operations now require complete direct-file metadata, canonical dependency baselines, table/expanded-file identity, provider identity and a nonzero revision sequence; source-stable managed contracts t20261014T003129Z-13d184 qualify the hardened boundary. Generated artwork carries per-shape preview provenance plus a private field manifest that survives native .kicad_sym write/read, and writes/deletes can require the observed modify hash while preserving exact operation-ID replay. The source-stable Linux native-library-readback run t20261005T024345Z-2f8cf6 now also proves raster-projected filled geometry, pins/fields, embedded source, provenance and modify identity survive schematic save/close/reopen. Global symbol-library documents still lack a durable lifecycle revision snapshot, while write/delete replies retain process-owned receipts.",
            ["peb739e0453b017ee", "p2d3aca18552132fe"]),
        ("library-snapshot-transport", "symbol-library",
            "The read-only library snapshot transport accepts an explicit project-bound symbol or footprint DocumentSpecifier and native process epoch, refreshes the direct KiCad provider, and returns compact detached-definition and table/library baseline digests with provider row metadata. It refuses unsupported providers, derived symbols and incomplete baselines with typed errors. The Linux native-library-readback run t20261006T221556Z-13f111 passed symbol and footprint snapshot reads with retained evidence; the transport still never exposes the detached definition or claims complete mutation tracking.",
            ["p21a549c786ae5b28", "kicad-library-snapshot-readonly-transport-20261005"]),
        ("schematic-authoring-completion", "schematic-design",
            "Typed wire, bus, junction, bus-entry, label and text creation is registered with checked revision admission and its live native qualification segment passes. Create now also requires a root CheckedSchematicState and is replay-safe; managed refusal/replay coverage passes, while real editor persistence, undo/redo and native wire/label qualification remain open. Existing connection items now also have typed read, patch-update and delete tools that preserve complete native label fields/properties and use retained checked receipts for exact replay. Checked symbol-instance create/read/update/delete tools now accept complete instance payloads and retain exact native operation receipts; the Linux native MCP journey passes create/read/update/delete, exact create/update replay, readback and cleanup on two disposable instances, while broader library-definition authoring, hierarchy, connectivity/ERC and cross-platform qualification remain open. Typed kicad_schematic_replace_embedded_files, kicad_schematic_create/update/delete_image, kicad_schematic_set_title_block, kicad_schematic_set_page_settings and kicad_schematic_set_net_settings wrappers now build checked native operations from explicit root lifecycle checkpoints, validate bounded typed payloads and preserve operation receipts. The source-stable NativeSetupDraft run t20261008T191530Z-0d7d82 now qualifies live image create/update/delete, exact image-byte and position persistence across save/reload, title-block and supported A4-landscape page-settings mutations, title/page save-reload, and embedded-file save-reload. Embedded schematic library-cache replacement, revision-paired live ERC-marker inspection and operation-replay-safe headless ERC execution are implemented through the native boundary; managed ERC execution contracts now reject stale/failed/indeterminate results, and source-stable managed run t20261014T011615Z-bfdca1 passes the explicit missing/pending/terminal/indeterminate recovery-classification contract (324 retained artifacts). Cooperative long-run ERC cancellation, late completion, disconnect/restart recovery and broader electrical-repair qualification remain open. Hierarchical and repeated/shared sheets, buses, variants and the remaining authoring families still need complete native persistence; zero ERC markers do not prove electrical completeness.",
            ["p78cb4c45bc18af7b", "pb0cc4fd249f3fb96"]),
        ("simulation-qualification", "simulation",
        "The simulation tools run KiCad's own ngspice on an explicit schematic. The earlier source-stable Linux focus t20261005T051924Z-81b5e9 proves native vector/status coverage, checked save/reload, reconnect and deterministic failure receipts; it does not qualify later archive changes. Caller-supplied decks reject interpreter directives and external file includes. The read-only kicad_simulation_operation lookup recovers a process-owned job by its original operation identity after a lost start reply; it returns found=false when authoritative native evidence is absent and never dispatches a duplicate. Accepted simulation operations now fail closed as explicitly unresolved when the native instance disappears, preventing duplicate dispatch; managed typed contracts cover that receipt boundary in source-stable run t20261013T233204Z-d9aff3. The read-only kicad_simulation_result tool retrieves atomically retained terminal protobuf results and captured deck/dependency evidence under their original instance, process epoch, document and job. kicad_simulation_vector additionally exposes one named archived/native vector with pair-safe logical slicing while retaining that identity. Recorded terminal operations replay without rerunning; a dispatched or accepted operation remains explicitly unresolved when native lookup has no record. The native-simulation leaf of t20261005T192704Z-a0b6a0 passed on source 53955d57e059b4d482038b646c58ac098fe30131, proving exact completed, cancelled and failed archive retention after later jobs and MCP/native restart. The overall graph failed an independent PCB routing fixture; this passing leaf qualifies only the simulation archive journey. Live controls still target the actual editor; native process restart does not rebind old jobs. Revision preconditions, native AC/archive/restart qualification breadth, dependency path enumeration, broader simulation qualification and Mac/Windows/Codex Desktop qualification remain unfinished.",
            ["pa931ae7ad80eccec", "p682f6173a40ec389", "pbf17c124768447fb"]),
        ("simulation-complex-vector-qualification", "simulation",
            "Native AC vector capture now reads real and imaginary components together with their native storage type, emits interleaved real/imaginary pairs for complex vectors, and preserves flat real vectors. The read-only kicad_simulation_vector contract exposes named vectors with logical sample slicing, explicit pair encoding and separate real/imaginary arrays while retaining the original archive identity. Zero imaginary components do not change a complex vector's type. Fresh governed simulation-mcp-focus run t20261009T135547Z-71bd5e passes source-stable with native complex/real vector reads, pair-safe logical slicing, archive identity, reconnect/replay and truthful refusal cases; retained simulation-tests sha256 cfce3af3e0d56f95d654d48f67f97c1534415b192f34bd7ce83fc1e80de97812 and simulation-evidence sha256 0ca1fe7ee5b9f0b785cbd1b736a7630e6589ddb783bc2748e3356b242420cdf3. Broader revision, dependency, failure/cancellation, restart-mode and cross-platform qualification remain open. Historical real-only receipts retain their original evidence and cannot recover discarded imaginary samples.",
            ["pa25acc702fd0ed58", "p682f6173a40ec389"]),
        ("agent-client-qualification", "qualification",
            "Operation through an actual agent client is verified only for the Linux Codex fixture. The Mac-local and Mac-to-VPS modes and other agent clients are not qualified.",
            ["p8bf96f1c4b709a28", "p9bdc0986708bd9b2"]),
        ("platform-qualification", "qualification",
            "The complete KiCad design workflow through this MCP server (native editing, synchronization and capability discovery) remains qualified only on Linux. The hosted Mac Apple Silicon and Intel packages and the Windows package now have exact-source native build, install, signature, focused native tests, managed MCP-contract, and packaged-editor journey evidence; public Mac and Windows downloads are available. Those package receipts do not yet qualify the full cross-platform editing workflow, signed update journeys, Codex Desktop operation, or XML editing parity, which remain open.",
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
        "simulation" or "schematic-manufacturing" => ["schematic", "job"],
        "schematic-operations" => ["schematic"],
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
