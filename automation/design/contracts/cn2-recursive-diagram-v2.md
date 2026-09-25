> **Frozen 2026-09-23 by the integration owner** (decision `kicad-phase2-contracts-frozen-20260923`).
> Field numbering: fields this contract fixes are declared by the parent seam at the freeze, sequentially
> below 100; later lane additions use the lane bands of `psu-cpu-fixture-and-ownership.md`
> (2A 100-199, 2B 200-299, 2C 300-399, 2D 400-499, 500-999 Phase 3). Items marked owner-pending below
> use the stated default until the owner decides; changing them later is a contract revision.

> Owner-pending: whether moving a block to another level is an immediate guarded action with Undo or part of the Save/Decline draft. Decided by the integration owner with contract defaults: new diagram file `<project>.system-diagram.xml`, silent v1-to-v2 upgrade reported in results, per-session viewports, no operating-system user in actor names.

# Contract rbg-v2: recursive-block-graph schema 2, its model, proto and codec for the single per-level diagram editor

Status: final judged proposal, ready for the integration owner to freeze. Nothing here is implemented. Checked read-only against worktree `the codex/finalization-integration worktree` (branch `codex/finalization-integration`, HEAD `7465f06533`). All paths are relative to the repository root.

## 0. How the two drafts were judged

**Base.** The "robust" draft is the base. Code review supports three of its choices:

- **One coordinate system per level, in diagram units.**
  - `kicad_diagram_read` already outputs `units = "diagram-unit"`.
  - Annotations, the offscreen observation viewports and the legacy `nodeRect` grid are all in diagram units.
- **A separate `connection-archive:2` namespace.** The v1 schemas stay frozen byte-for-byte.
- **One draft per level with a three-way level merge.** The approved interaction specification (§4) says: "Do not open a modal for a harmless view change or independent, safely composable fields". A draft that combines structure and layout could not be rebased under the minimal draft's `structural_draft_requires_comparison` rule.

**Ideas taken from the "minimal" draft:**

- The flat `StructuralDiagram.Id` becomes the root block occurrence id. This keeps the identity exact; flat properties and statements can be owned by the diagram itself.
- A flat cross-level connection is converted, with explicit Unresolved endpoints. The robust draft's "not converted" would hide a link the user drew in a receipt that has no UI.
- A flat block's placement becomes that block's own-level frame, so boundary ports keep their flat geometry.
- The helper is located only through existing trusted contexts.
- The MCP surface stays small.
- There is no migration lock file.

**Errors in the drafts, found by reading the code, and how this contract fixes them:**

| # | Draft claim | Code fact | Resolution |
|---|---|---|---|
| 1 | Both drafts add `ImmutableArray<…> X = default` optional parameters to `BlockLocalDiagram` and `DiagramBoundaryInterface`. | These records reach MCP JSON-schema generation through `ProposedBlock.Diagram`. Commit `ab6c19bbfb` and test `RecursiveBlockToolSchemaTests.OptionalAnnotationsDoNotBreakTypedMcpSchemaGeneration` show that a default `ImmutableArray` value breaks schema generation. | Rule §2.5. |
| 2 | Minimal adds `domain`/`direction` to `connection-archive-v1.xsd` in place, with a code guard. | That changes a frozen v1 schema, and v1 documents could then carry v2 data. | `connection-archive-v2.xsd`, namespace `:2`. |
| 3 | Minimal uses nm placements next to diagram-unit annotations. | This gives two unit systems in one canvas. | Presentation uses decimal diagram units (§3). |
| 4 | Minimal prunes layout inside `Select`. | This silently changes the content of ancestor snapshots. | Dormant entries (§4.3). `Select` stays unchanged. |
| 5 | Both drafts put the interconnect realization in the owning block's local diagram, "like BlockPhysicalAllocation". | `BlockPhysicalAllocation` lives on the block's own revision. Plan §6 lists "ConnectionRevision: … realization map". | The realization lives on `DiagramConnectionRevision` (§4.2). |
| 6 | Minimal keeps flat statements and properties as text only. | Plan §6: "Preserve … typed quantities". | The receipt embeds the verbatim canonical `<structure>` element plus typed rows (§4.12). |
| 7 | Minimal: "more than one hardware.xml design declares this model file". | `HardwareRepository.Validate` forbids two designs sharing a model path. | That case is removed. |
| 8 | Robust F1 fallback moves unplaced blocks when others are placed, and its F2 formula is `(k+1)*h/(n+1)`. | The code uses `(index+1)*h/max(2, count+1)`. | Materialize-on-first-layout-edit (§9.2). The exact formula is quoted. |
| 9 | Robust: lock file with a 10-minute stale heuristic, a preview token, 7 MCP tools, interface-member trees, a sibling-`kicad-mcp` lookup. | Create-only `File.Move(overwrite:false)` plus the source token already give the needed guarantees. Interface members are not required by the listed outcomes. | Dropped or deferred (§13). |
| 10 | Both drafts: create, migrate and probe are ordinary requests. | `RecursiveEditorFiles.ExecuteAsync` runs `Id(request.DocumentId)` before dispatch. | These actions require an empty `document_id` and are dispatched first (§7). |
| 11 | "26 kicad_diagram_* tools". | Exactly 25 are registered. `grep -c McpServerTool RecursiveEditorTools.cs` returns 26 only because it also counts `[McpServerToolType]`. | Baseline is 25. After this contract there are 28. |

## 1. Scope

| Ledger outcome | What this contract provides |
|---|---|
| p353f93bbed7b2df6 (every block has its own versioned diagram and an independent physical mapping) | Per-level `DiagramPresentationView` versioned with the block revision; `InterfaceRealization` (boundary mapped to interior); `InterconnectRealization` on connection revisions; `Harness` allocation kind; v2 XML; migration without invented history. |
| pf92d0ecdec8805b4 (connections refined into members and partial endpoints; interconnect mappings) | One boundary interface mapped to many child interfaces, local connections or members, and pins, with Unknown/Partial/Resolved states. Typed interconnect segments (board net, connector with pins, harness, hardware.xml interface, external) plus joins. The owning design is always explicit. A link is never equated with copper. |
| pe9e76b5403614990 (edit connections, ports and hierarchy) | Domain and direction on connections and on boundary interfaces. Level edits. Reparent with checks for cycle, root, same parent, head and attached references, plus ancestor snapshots. |
| pc12a7fddf47cab23 (per-level navigation, layout, history and comments) | Revision-bound layout per level. Per-level viewport kept for the session. `layout_only` flag and `DHCC_LAYOUT` category in history. Layout never opens a modal. |
| p60bc4181a9aa1b40 (create, move, resize and connect in the canvas; save through the helper) | `LevelDraftData` saved atomically by `RFA_SAVE_LEVEL` and discarded by Decline. Removals go through `RFA_PREPARE_LEVEL_EDIT`. The fallback reproduces today's grid. |
| p57f63bd870523b16 (project-manager create/open and one-time flat conversion) | `RFA_DISCOVER_DIAGRAMS`, `RFA_CREATE_DIAGRAM`, `RFA_PREPARE_MIGRATION` and `RFA_MIGRATE_FLAT_DIAGRAM`. A receipt stored in the document. MCP `kicad_diagram_create`, `kicad_diagram_migrate` and `kicad_diagram_discover`. A companion locator with no MCP dependency. |
| pe84454b4680082e1 (activation, later) | Reserved field ranges only. Layout never takes part in activation or synchronization. Partial and Unknown are never treated as resolved. |

**Binding inputs:**

- Decisions nab403c279a16e4fa (`kicad-single-per-level-diagram-editor-20260923`), n40ae6000bed84b18 (`kicad-structural-auto-sync-meaning-20260923`), n68d6149bd8ab9bca, n045fff50030deea3, nb79cedb30025a45c, nd4461f02f40c05ce and n147cb8d12861906b (design rounds A–E).
- `recursive-structural-refinement-plan.md` §1, §3, §6 and §7.
- `history-and-conflicts/interaction-specification.md` §1 and §4.
- `automation/README.md`, "Recursive structural diagrams". It sets these rules: unchanged saves do not churn; Decline never writes the old baseline over a newer file; `AlreadyPresent`-style observations are not receipts; names and locations never establish identity.

**Non-goals.** This contract does not include:

- any new UI element design (Round A1: canvas tools; A2: project-manager entry and conversion prompt; A3: connection, port and hierarchy inspector; B: realization, receipt and guidance display; C: structured conflicts);
- named or filtered views;
- viewport persistence across sessions;
- interface-member trees;
- MCP tools for level edits, reparent or realizations;
- activation;
- retiring the flat editor. `kicad_structure_open` and `STRUCTURAL_EDITOR_FRAME` stay until the conversion journey passes.

## 2. Identity and compatibility rules

### 2.1 Identity

Existing rules are unchanged. New rules:

- Interface ids stay owned by one block occurrence.
- `InterfaceRealization` is keyed by `InterfaceId`, at most one record per interface per revision.
- `InterconnectRealization` belongs to one connection revision.
- `InterconnectSegment.Id` values, `DiagramMigrationReceipt.Id`, and every id created by a level draft, create or migration join the document's no-alias identity set. A segment id stays owned by one connection occurrence.
- Nothing is inferred from a name or a position. Names in the migration's Unresolved endpoint intent text are descriptive text only. A missing mapping is explicitly Unknown or Partial with a reason, or it is absent, which means "not stated".

### 2.2 XML versions

| Artifact | v1 (frozen) | v2 (new) |
|---|---|---|
| Graph root | `urn:kicad:automation:recursive-block-graph:1`, `version="1"` | `urn:kicad:automation:recursive-block-graph:2`, `version` fixed `"2"`, file `automation/schemas/recursive-block-graph-v2.xsd` |
| Connection archive | `urn:kicad:automation:connection-archive:1` | `urn:kicad:automation:connection-archive:2`, `version` fixed `"2"`, file `automation/schemas/connection-archive-v2.xsd` |
| requirement-history, block-definition, refinement-input | `:1` | unchanged, imported as `:1` |

- **R1 — Namespace dispatch.** `RecursiveBlockGraphXml.ReadVersioned(string xml) -> (RecursiveBlockGraph Graph, int StoredSchemaVersion)` picks the schema set by the root namespace.
  - A root named `recursive-block-graph` in `...:N` with N > 2 fails with `diagram_schema_too_new`.
  - Any other root or namespace fails with the existing `invalid_recursive_block_graph_xml`.
  - Both failures happen before any lock or write.
  - `Read(xml)` keeps its signature and accepts v1 and v2.
- **R2 — v1 maps to neutral v2 values.** A v1 document loads with: no presentation (every element unplaced), no realizations, Unspecified domain and direction, and no receipt.
- **R3 — Reads never write.** This covers read, history, compare, prepare-restoration, prepare-level-edit, prepare-reparent, prepare-migration and discovery.
- **R4 — Writers always emit v2.** There is no v1 writer.
  - A write happens only when content changed.
  - When the loaded file was v1, the result reports `upgraded_from_schema_version = 1`.
  - An unchanged save leaves a v1 file byte-identical.
  - The upgrade itself adds no revision and no history row.
- **R5 — Determinism.** `Write(Read(x)) == x` byte-for-byte for every canonical v2 fixture. Reading a v1 fixture and saving one change keeps every v1 fact.
- **R6 — Older readers reject v2 before any mutation.** The old `Read` checks `root.Name == {...:1}recursive-block-graph` before validating. Every old writer (`RecursiveBlockFiles`, `ImplementationFiles`, `BlockProposalFiles`, `RefinementInputFiles`) loads through `Load` → `Read` first. Result: `invalid_recursive_block_graph_xml`, and the file is unchanged.

### 2.3 Protobuf versions

- The package stays `kiapi.automation.diagrams.v1`.
- `RecursiveBlockGraphData.schema_version`, `RecursiveEditorDocument.schema_version`, `RecursiveFileRequest.schema_version` and `OpenRecursiveDiagramEditor.schema_version` are exactly **2** in v2 builds.
- The codec rejects other graph versions with `invalid_recursive_diagram_data`, the helper uses `unsupported_diagram_file_request`, and native uses `AS_BAD_REQUEST`. Each rejection happens before any file access.
- Strict unknown-field rejection (`Known()` / `DiscardUnknownFields` size check) stays on both sides.
- `RecursiveEditorDocument.stored_schema_version` (1 or 2) reports the on-disk format.

### 2.4 Version-skew matrix

Every mismatched pair fails closed and writes nothing.

| Pair | Result |
|---|---|
| Old helper, old MCP or preview build reads a v2 file | `invalid_recursive_block_graph_xml` |
| New native editor with an old helper (for example an old MCP passed its own `helper_path`) | The old helper rejects request schema 2 with `unsupported_diagram_file_request`. The editor shows `companion_version_mismatch`. |
| Old native editor with a new helper | The new helper rejects request schema 1. The old frame also rejects `document.schema_version != 1`. |
| New MCP opens an old native build | `AS_BAD_REQUEST`, reported as `native_status_*` |
| Old MCP opens a new native build | `AS_BAD_REQUEST` (open schema 1) |
| New code reads a v1 file | Loads. Upgrades only on a changed write. |
| New code reads a `:3` file | `diagram_schema_too_new` |

This is why request schema 2 is required: an older editor would send a `BlockDraftData` without presentation, realizations, domain or direction, and a save would erase them.

### 2.5 Enum and record rules (exact)

- **New enums whose zero is a valid Unspecified are cast directly** between C# and proto: `DiagramDomain`, `DiagramInterfaceDirection`, `DiagramConnectionDirection`. This follows the `PhysicalAllocationStateData` precedent.
- **Every other new proto enum** has `0 = *_UNSPECIFIED`, which decoders always reject, and C# = proto − 1. This is the existing convention for `DiagramConnectionKind` and `PhysicalAllocationKindData`.
- `PhysicalAllocationKind.Harness` is C# 4 and proto `PAK_HARNESS = 5`.
- `DiagramHistoryChangeCategory` appends `Layout`, `InterfaceRealization` and `InterconnectRealization` as C# 8..10, which map to proto 9..11.
- **XML enum strings** are the C# member names. An attribute whose value is Unspecified is omitted, and the literal `Unspecified` is never written.
- **MCP JSON-schema rule.** Any record reachable from an MCP tool parameter must follow these rules:
  - Reachable records include `BlockLocalDiagram`, `DiagramBoundaryInterface`, `ProposedBlock`, `ProposedConnection` and every new record nested in them.
  - Never declare an `ImmutableArray<T>` parameter with a default value.
  - Put new collection members in the `[JsonConstructor]` primary constructor without defaults.
  - Keep secondary constructors with the old arities so existing call sites still compile.
  - Expose coalescing accessors (`X.IsDefault ? [] : X`), because an omitted JSON member deserializes as a default array.
  - Nullable reference defaults (`= null`) and enum defaults are allowed.
  - `RecursiveBlockToolSchemaTests` is extended to cover every new reachable record.
- **Explicit `SameContents`.** Records holding `ImmutableArray` compare arrays by reference under record equality, so each new such record gets an explicit `SameContents`.

## 3. Presentation coordinates

- The unit is `diagram-unit`, the space shared by annotations, the legacy grid and observation: X points right, Y points down. It is presentation only, never PCB or schematic geometry.
- **Presentation decimals** (not annotations) must satisfy:
  - value in [-1e9, 1e9];
  - quantum 0.001 (value × 1000 is an integer);
  - width > 0 and height > 0;
  - x + width ≤ 1e9 and y + height ≤ 1e9;
  - 0 ≤ offset ≤ the length of its side;
  - otherwise `invalid_diagram_coordinate`.
- **Canonical text**: pattern `-?(0|[1-9][0-9]{0,9})(\.[0-9]{0,2}[1-9])?`, with `-0` forbidden, no exponent and no `+`.
  - XML readers require the canonical form.
  - Proto decoders accept `^[+-]?[0-9]+(\.[0-9]+)?$` when the value is exact to 0.001, and canonicalize it. For example `"12.500000"` from C++ `std::to_string` becomes `"12.5"`, while `"1.2345"` is rejected.
  - Encoders emit canonical text.
- **C++** keeps every presentation value as `int64` thousandths.
  - Formatting: sign, `t/1000`, then `.` plus a zero-padded three-digit fraction with trailing zeros trimmed when `t%1000 != 0`.
  - Parsing: split at `.`, never through `double`.
  - Pointer math converts to `double` only for painting.
- Annotation coordinates keep the v1 `xs:decimal` rules.
- **Migration factor**: exactly **100 000 nm per diagram unit** (`StructuralMigration.NanometresPerDiagramUnit`). Flat values are multiples of 100 nm, so conversion is exact to 3 decimals. The flat editor's default block of 20 320 000 × 15 240 000 nm becomes 203.2 × 152.4, which is comparable to the recursive default of 240 × 145.

## 4. Model contract

Lane A implements the types, validation and XML. Lane E implements the operations. Lane B implements the converter. Positional records gain members appended at the end, subject to §2.5.

### 4.1 Boundary interfaces: domain and direction (for pe9e76 "ports: kinds, directions")

```csharp
public enum DiagramDomain { Unspecified, Power, Data, Control, Analog, Mechanical }
public enum DiagramInterfaceDirection { Unspecified, Input, Output, Bidirectional } // relative to the owning block
public sealed record DiagramBoundaryInterface(Guid Id, string Name, string Intent,
    DiagramDomain Domain = DiagramDomain.Unspecified, DiagramInterfaceDirection Direction = DiagramInterfaceDirection.Unspecified);
```

Record equality now includes Domain and Direction, so `Interfaces.SequenceEqual` detects changes. Interface members are deferred (§13).

### 4.2 Connections: domain, direction and InterconnectRealization (on the connection revision)

```csharp
public enum DiagramConnectionDirection { Unspecified, FromFirst, ToFirst, Bidirectional } // FromFirst: Endpoints[0] -> every other endpoint
// DiagramConnectionRevision(..., RequirementRevisionOrigin Origin, DiagramDomain Domain = Unspecified,
//     DiagramConnectionDirection Direction = Unspecified, InterconnectRealization? Realization = null)
// DiagramConnectionDraft(..., ImmutableArray<DiagramAnnotation> DiagramAnnotations = default, DiagramDomain Domain = Unspecified,
//     DiagramConnectionDirection Direction = Unspecified, InterconnectRealization? Realization = null)
// ProposedConnection(..., Guid? ForkRequirementRevisionId = null, DiagramDomain Domain = Unspecified,
//     DiagramConnectionDirection Direction = Unspecified, InterconnectRealization? Realization = null)
public enum DiagramRealizationState { Unknown, Partial, Resolved }
public enum InterconnectSegmentKind { BoardNet, Connector, Harness, HardwareInterface, External }
public sealed record InterconnectSegment(Guid Id, InterconnectSegmentKind Kind, string? Label, Guid? DesignId, Guid? CircuitId,
    Guid? NetId, Guid? ComponentId, ImmutableArray<DiagramPinTarget> Pins, Guid? PhysicalTargetId, Guid? HardwareInterfaceId,
    string? Reference, string? RepositoryPath, string? UnresolvedReason) { public ImmutableArray<DiagramPinTarget> PinList { get; } }
public sealed record InterconnectJoin(Guid FirstSegmentId, Guid SecondSegmentId);
public sealed record InterconnectRealization(DiagramRealizationState State, ImmutableArray<InterconnectSegment> Segments,
    ImmutableArray<InterconnectJoin> Joins, string? UnresolvedReason, ImmutableArray<SourceReference> Sources)
{ public void Validate(); public bool SameContents(InterconnectRealization? other); }
```

**E1 — Domain and direction** are part of `Same`, `SameDefinition` (archive `Retains`), unchanged-save detection in `SaveDraft`, and history. Direction is valid for any endpoint count ≥ 2.

**Realization invariants** (`invalid_interconnect_realization` unless noted):

- **IC1 — One per revision.** At most one realization per connection revision. An absent realization means "not stated".
- **IC2 — Required fields per segment kind.** Fields not listed for a kind must be null or empty.

  | Kind | Required |
  |---|---|
  | BoardNet | `DesignId`, `CircuitId`, `NetId` |
  | Connector | `DesignId`, `CircuitId`, `ComponentId`; every pin has the same DesignId and ComponentId, and pins are distinct under `SamePin` |
  | Harness | `PhysicalTargetId` |
  | HardwareInterface | `HardwareInterfaceId` (the exact hardware.xml interface id) |
  | External | a non-blank `Label` plus `Reference` or `RepositoryPath`; `RepositoryPath` follows `HardwareRepository.ValidatePath` |

  `Label` is optional display text for every kind. It is never an identity.

- **IC3 — Completeness and state.** A segment is *complete* when its required fields are present, a Connector has ≥ 1 pin, and `UnresolvedReason` is null. An incomplete segment must carry a reason.
  - Resolved: ≥ 1 segment, all complete, and a null record reason.
  - Partial: ≥ 1 segment and a non-blank record reason.
  - Unknown: no segments, no joins, and a reason.
- **IC4 — Joins.** Segment ids are distinct. Joins reference segments of the same record, are never self-joins, and are unique as unordered pairs. An empty join list means "topology unknown".
- **IC5 — Circuit consistency.** Within a realization, each DesignId maps to exactly one CircuitId. It must also agree with every block revision pinning this connection (its `EffectiveComponentBindings`), or the check fails with `ambiguous_block_circuit`.
- **IC6 — Harness targets (graph-level).** In `ValidateConnections`, for every block revision whose `Walk(roots)` pins this connection revision:
  - a Harness `PhysicalTargetId` must name a target of kind `Harness` or `Assembly` in that block revision's `PhysicalAllocation`;
  - a new realization that breaks this fails `invalid_interconnect_realization`;
  - a block save that drops a target still referenced fails `physical_target_in_use`.
- **IC7 — Syntax only.** The model checks syntax and internal consistency only. Checking that designs, nets and components exist is a future read-only resolver that reports misses and never fixes them (§13).

`PhysicalAllocationKind` gains `Harness`, added to the v2 XSD enumeration.

### 4.3 DiagramPresentationView: the per-level layout, versioned with the block revision

```csharp
public enum DiagramPortSide { Left, Right, Top, Bottom }
public sealed record DiagramPoint(decimal X, decimal Y);
public sealed record DiagramRect(decimal X, decimal Y, decimal Width, decimal Height);
public sealed record DiagramBlockPlacement(Guid BlockId, DiagramRect Rect, bool Locked = false, uint? FillRgb = null);
public sealed record DiagramPortPlacement(Guid BlockId, Guid InterfaceId, DiagramPortSide Side, decimal Offset);
public sealed record DiagramConnectionRoute(Guid ConnectionId, int EndpointIndex, ImmutableArray<DiagramPoint> Waypoints,
    DiagramPoint? Label = null, bool Locked = false);
public sealed record DiagramPresentationView(ImmutableArray<DiagramBlockPlacement> Blocks, ImmutableArray<DiagramPortPlacement> Ports,
    ImmutableArray<DiagramConnectionRoute> Routes, DiagramRect? Frame = null)
{ public static DiagramPresentationView Empty { get; } public void Validate(); public DiagramPresentationView Canonical();
  public bool SameContents(DiagramPresentationView? other); /* null ≡ Empty */ }
```

**Decision: layout is part of `BlockLocalDiagram` and versioned with the owning block revision.** Reasons:

- The plan §6 says "LocalDiagram … layout and annotations, all revision-bound".
- Save and Decline, restore-as-draft, fork, proposals, history compare and the stale guards all apply unchanged.
- Historical roots and previews show their exact old layout.
- A separate layout chain would need a second save path, conflict path, restore path and history, and old revisions could only approximate their layout.

Costs and mitigations:

- Layout-only saves add a revision plus ancestor snapshots. They are labelled with `layout_only` and `DHCC_LAYOUT`.
- Layout merges never block (§4.8).
- Layout never takes part in synchronization, activation or native realization.

**Decision: the per-level viewport (pan and zoom) is not stored in the shared document.**

- It is session state per level in the editor (the existing `m_views`, reported as `level_viewports`).
- Pan and zoom never dirty a draft, create a revision or conflict with anything.
- Cross-session per-user memory is deferred (§13).
- Proto field 6 of `DiagramPresentationViewData` is reserved by comment for a possible authored "home framing". It is not part of v2.

**Structural invariants** (`invalid_presentation_view`), checked without graph context:

- **PV1 — Coordinates and unique keys.** Coordinates follow §3 and `FillRgb ≤ 0xFFFFFF`. Keys are unique:
  - blocks by `BlockId`;
  - ports by `(BlockId, InterfaceId)`;
  - routes by `(ConnectionId, EndpointIndex)`.
- **PV2 — Routes.** `EndpointIndex ≥ 1`. The route draws Endpoints[0] → Endpoints[i]; this is today's star drawing. At most 256 waypoints.
- **PV3 — Port geometry.** A port whose `BlockId` is not the scope needs that block's placement in the same view. A port whose `BlockId` is the scope needs `Frame`. The offset is measured from the top for Left/Right and from the left for Top/Bottom, and must satisfy 0 ≤ offset ≤ side length.
- **PV4 — Ordering.** Block order is z-order (later paints on top) and is significant. Port and route order is not significant. `Canonical()` sorts ports by (BlockId, InterfaceId) and routes by (ConnectionId, EndpointIndex), using ordinal `"D"` strings. The writer and `SameContents` use the canonical form.

**Scope classification** is graph-level; see `RecursiveBlockGraph.ActivePresentation(BlockSelection)` and `DormantPresentationCount(BlockSelection)`. An entry is **active** when its target exists in *this* revision:

- `BlockId` is in `Children`, or is the scope;
- `InterfaceId` is in that pinned child's interfaces, or in the scope's own interfaces;
- `ConnectionId` is in `Walk(LocalDiagram.Connections)`;
- `EndpointIndex` is less than the pinned connection revision's endpoint count.

Otherwise the entry is **dormant**. Dormant entries are allowed, never rendered, and ignored by change detection.

- **PV5 — Verbatim copies.** `Select` (ancestor snapshots), restore and fork copy the view verbatim, dormant entries included. `Select` is not changed.
- **PV6 — Pruning.** When a level save is otherwise *changed*, dormant entries are removed from the new scope revision and counted in `pruned_presentation_entries`. Pruning alone never causes a write.
- **Unplaced means missing.** A missing entry means "unplaced", which is valid (coordinate-free round trip). The model never stores fallback positions.
- **Null and Empty.** A writer omits an empty view. A reader maps an absent view to null. `SameContents` treats null and Empty as equal.

### 4.4 InterfaceRealization: a block's boundary mapped to its interior

It is stored in `B.LocalDiagram.InterfaceRealizations` and versioned with B.

```csharp
public enum InterfaceRealizationTargetKind { ChildInterface, LocalConnection, Pin }
public sealed record InterfaceRealizationTarget(InterfaceRealizationTargetKind Kind, Guid? BlockId, Guid? InterfaceId,
    Guid? ConnectionId, DiagramPinTarget? Pin);
public sealed record InterfaceRealization(Guid InterfaceId, DiagramRealizationState State,
    ImmutableArray<InterfaceRealizationTarget> Targets, string? UnresolvedReason, ImmutableArray<SourceReference> Sources)
{ public bool SameContents(InterfaceRealization? other); }
// BlockLocalDiagram [JsonConstructor](Interfaces, Connections, Annotations, DiagramPresentationView? Presentation,
//     ImmutableArray<InterfaceRealization> InterfaceRealizations) + existing 2- and 3-argument constructors;
//     accessors Layout (Presentation ?? Empty) and Realizations (default -> []).
```

Invariants (`invalid_interface_realization`):

- **IR1 — Scope.** `InterfaceId` is one of B's own interfaces in this revision. There is at most one record per interface.
- **IR2 — Targets.** Targets are distinct (sorted by kind, then ids) and exact:
  - ChildInterface: `BlockId` is in `Children` and `InterfaceId` is in that pinned child revision;
  - LocalConnection: `ConnectionId` is in `Walk(LocalDiagram.Connections)` (a root or a member);
  - Pin: `Pin.Validate()` passes and `(Pin.DesignId, Pin.ComponentId)` is in `B.EffectiveComponentBindings`;
  - every field not used by the kind is null.
- **IR3 — State rules.**
  - Unknown: no targets and a non-blank reason.
  - Partial: ≥ 1 target and a non-blank reason saying what remains.
  - Resolved: ≥ 1 target and a null reason.
  - No record at all means "not stated" and is shown as unmapped. Nothing derives a record from connection endpoints.
- **IR4 — No equivalence implied.** One parent link may be realized by many internal targets. No one-link-to-one-net equivalence is implied.

### 4.5 Graph-level rules (`RecursiveBlockGraph`, Lane A)

- **G1 — Identity set.** The no-alias identity set gains segment ids and the receipt id. Segment ids use an "owned by one connection occurrence" map.
- **G2 — Validation per revision.** Every revision validates PV1–PV4, IR1–IR4 and IC1–IC6.
- **U — Removal rule.** No revision may leave a dangling reference to a child's interface through a connection endpoint or a realization target.
  - The error is **`boundary_interface_in_use`**, with `AutomationException.Details` listing (kind, scope block, object id).
  - It applies to child saves, implementation selection (detected while building ancestor snapshots) and level edits.
  - It replaces today's generic `invalid_recursive_block_graph` from `ValidateConnections` for this case. Existing assertions must be updated.
  - Presentation entries never trigger it; they become dormant instead.
- **G3 — Empty-interior fork.** `ForkImplementation(emptyInterior: true)` keeps interfaces, `Frame` and boundary-port placements. It drops child placements, child ports, routes and interface realizations. Connections, and therefore their realizations, are dropped as today.
- **G4 — Receipt carry-forward.** `public DiagramMigrationReceipt? Migration { get; }` is appended to the constructor as `DiagramMigrationReceipt? migration = null`.
  - Every graph-producing member must pass it through: `AppendRevision`, `AddImplementation`, `ForkImplementation`, `RenameImplementation`, `SetImplementationArchived`, `Select`, `SaveDraft*`, `SaveConnectionDraft`, `WithConnections`, `WithRefinementInput`, `WithProposal`, `BlockProposalCompiler`, and every E operation.
  - Only `StructuralMigration` sets a receipt.
  - The file layer rejects a publication whose receipt differs from the loaded one with `migration_receipt_immutable`.
- **G5 — Partial class.** `RecursiveBlockGraph` becomes `public sealed partial class`. This is a parent-seam change at freeze, so that Lane E adds operations in its own files.
- **G6 — Error details.** `AutomationException` gains an optional constructor parameter `IReadOnlyList<AutomationErrorDetail>? details = null`, where `AutomationErrorDetail(string Kind, Guid? ScopeBlockId, Guid? ObjectId, string Message)`. It is additive and all existing call sites are unchanged.
- **G7 — Deterministic ids.** `DiagramIdentity.Derive(Guid operationId, string name)` produces UUIDv5 (RFC 9562 §5.5): SHA-1 over the namespace bytes in big-endian order (`Guid.ToByteArray(bigEndian: true)`) followed by UTF-8(name), then version 5 and the RFC variant, built with `new Guid(bytes, bigEndian: true)`.
  - Test vectors for namespace `6f1c2a4e-0b7d-4c55-9a51-3f0e8d2b7c10`:
    - `"document"` → `74ec87fc-edcd-5b78-80e2-e903035a5282`
    - `"root-block"` → `2f5add7e-1b7c-52bc-95da-e7870273b2a2`
    - `"state:root"` → `cc2d2fdb-fc5b-593f-8ea6-62a9e18243fa`
    - `"revision:root"` → `54feb920-3bbd-5034-a144-6b7beaeb0d03`
    - `"requirements:root"` → `a0fcd4c6-1f62-54f9-a0b9-ae5d77eb35ad`

### 4.6 Level draft: one Save/Decline scope per diagram level (Lane E)

```csharp
public sealed record NewBlockOccurrence(BlockSelection Selection, Guid RequirementRevisionId, string ImplementationName, string Name,
    DiagramRequirements Requirements, ImmutableArray<DiagramBoundaryInterface> Interfaces, BlockDefinition? Definition);
public sealed record NewConnectionOccurrence(ConnectionSelection Selection, Guid RequirementRevisionId, string ImplementationName,
    string Name, DiagramConnectionKind Kind, DiagramDomain Domain, DiagramConnectionDirection Direction,
    ImmutableArray<DiagramEndpointBinding> Endpoints, DiagramRequirements Requirements, InterconnectRealization? Realization);
public sealed record RecursiveLevelDraft(RecursiveBlockDraft Scope, ImmutableArray<RecursiveBlockDraft> ChildDrafts,
    ImmutableArray<DiagramConnectionDraft> ConnectionDrafts, ImmutableArray<NewBlockOccurrence> NewChildren,
    ImmutableArray<NewConnectionOccurrence> NewConnections);
public sealed record LevelRevisionId(Guid RevisionId, Guid RequirementRevisionId);
public sealed record LevelRevisionIds(Guid ScopeRevisionId, Guid ScopeRequirementRevisionId, ImmutableArray<Guid> AncestorRevisionIds,
    ImmutableDictionary<Guid, LevelRevisionId> Children, ImmutableDictionary<Guid, LevelRevisionId> Connections);
public sealed record RecursiveLevelSaveResult(RecursiveBlockGraph Graph, bool Changed, ImmutableArray<BlockSelection> CreatedBlockRevisions,
    ImmutableArray<ConnectionSelection> CreatedConnectionRevisions, int PrunedPresentationEntries, ImmutableArray<BlockSelection> CreatedAncestors);
// partial RecursiveBlockGraph:
public RecursiveLevelDraft StartLevelDraft(BlockSelection scope);
public RecursiveLevelSaveResult SaveLevelDraft(BlockSelection expectedRoot, ImmutableArray<BlockSelection> path, RecursiveLevelDraft draft,
    LevelRevisionIds ids, RequirementRevisionOrigin origin, IReadOnlyCollection<DiagramRequirementResolution>? resolutions = null,
    bool selectImplementation = false);
```

Precedent: `ProposedBlock` and `ProposedConnection` already create new occurrences with client-chosen fresh ids.

Invariants (`invalid_level_draft` unless noted):

- **L1 — Scope baseline.** `path[^1] == Scope.Baseline`, except that `selectImplementation` keeps the `SaveImplementationDraft` semantics.
- **L2 — Child drafts.** Each child draft's `Baseline` is a distinct child pinned in the scope baseline, at its state head (`stale_child_revision`).
  - It may change: Name, Requirements, Interfaces (including domain and direction), Definition, ComponentBindings, PhysicalAllocation.
  - It may remove, and only remove, its own `InterfaceRealizations` records whose interface it removes.
  - Children, Connections, Annotations, Presentation, other realizations and RestoredFrom must equal the baseline (`child_interior_edit_not_allowed`).
- **L3 — Connection drafts.** Each connection draft's `Baseline` is a distinct root of the scope baseline, at head (`stale_connection_revision`).
  - `Members` must equal the baseline (`connection_member_edit_requires_member_path`).
  - `DiagramAnnotations` must be default.
  - It may change Name, Kind, Endpoints, Requirements, Domain, Direction and Realization.
- **L4 — New occurrences.**
  - Each new child selection appears exactly once in `Scope.Children`.
  - Each new connection selection appears exactly once in the scope roots.
  - New children have non-blank, XML-safe names and fresh distinct interface ids.
  - Every new id is fresh against the whole graph and against each other (`identity_reused`).
- **L5 — Revision ids.** `ids.Children` and `ids.Connections` contain exactly the child-draft and connection-draft keys. Ids for drafts that turn out unchanged are not consumed.

`SaveLevelDraft` runs these steps atomically; any failure leaves `this` unchanged:

1. Apply the `SaveDraftCore` stale checks: expected root, path and scope head.
2. Create each new child. Each gets one state (`ImplementationName`), one requirement history with one revision, and one revision with `ParentRevisionId = null`, no children, `Diagram = new(Interfaces, [], [])`, `Definition`, and the save origin.
3. Commit each child draft with requirement `Commit` semantics through `AppendRevision`, never `Select`. An unchanged child draft creates nothing.
4. Create each new connection (state, one revision, requirement history) in the scope's archive, creating the archive if absent. Commit each connection draft with `archive.SaveDraft`.
5. Build the scope revision: substitute the child and connection selections, commit the scope fields, and compare with the baseline (active presentation only). If it changed, apply PV6.
6. If nothing changed anywhere, return `Changed = false`. The caller writes nothing.
7. Otherwise append the scope revision, then `Select` up `path`, creating ancestor snapshots. Graph construction validates the result, and failures map to §11 codes.

### 4.7 Level edit commands: the removal cascade (Lane E; the helper's model is authoritative)

- **Native-only edits.** Additive and presentation-only edits are made natively and validated at save: add child, add interface, connect (Abstract with Unresolved or Interface endpoints), set domain or direction, move, resize, port side and offset, routes, notes and text.
- **Removals always go through `RFA_PREPARE_LEVEL_EDIT`**, so the cascade has one implementation.

```csharp
public enum LevelEditCommandKind { RemoveChild, RemoveConnection, RemoveInterface }
public sealed record LevelEditCommand(LevelEditCommandKind Kind, Guid? BlockId, Guid? ConnectionId, Guid? InterfaceId,
    bool DetachConnections, RequirementRevisionOrigin Origin);
public enum LevelEditEffectKind { ChildRemoved, ConnectionRemoved, InterfaceRemoved, AnnotationUnresolved, RealizationTargetRemoved,
    RealizationStateChanged, RealizationRemoved, PresentationEntryRemoved }
public sealed record LevelEditEffect(LevelEditEffectKind Kind, Guid ObjectId, Guid ScopeBlockId, string Detail);
public static class RecursiveLevelEdits
{ public static (RecursiveLevelDraft Draft, ImmutableArray<LevelEditEffect> Effects) Apply(RecursiveBlockGraph graph,
      ImmutableArray<BlockSelection> path, RecursiveLevelDraft draft, LevelEditCommand command); }
```

**RemoveChild(B)** — B may be an existing child or a new one:

1. Remove B from `Scope.Children`, `ChildDrafts` and `NewChildren`.
2. Remove every root connection (saved root, connection draft or new connection) whose `Walk` has an endpoint on B → ConnectionRemoved.
3. Scope notes targeting B or a removed connection get `UnresolvedReason = "Target removed from this diagram level."` and `Origin = command.Origin` → AnnotationUnresolved.
4. Interface-realization targets on B or on removed connections are removed. If no targets remain, the record becomes Unknown with reason `"Realizing element was removed."`. A Resolved record that keeps some targets becomes Partial with the same reason. A Partial record keeps its reason. Effects: RealizationTargetRemoved and RealizationStateChanged.
5. Placements for B, ports on B, and routes of removed connections are removed → PresentationEntryRemoved.

**RemoveConnection(C)** applies steps 2–5 to the root C only. Members are removed through their member path.

**RemoveInterface(owner O, I):**

- When O is the scope:
  - any use by the parent level (connections or realizations of `path[^2]`) is rejected with `boundary_interface_in_use`;
  - scope connections with endpoint (O, I) require `DetachConnections`, otherwise `boundary_interface_in_use`; with it they are removed as in RemoveConnection;
  - the scope's own realization record for I is removed (RealizationRemoved);
  - the (O, I) boundary port placement is removed;
  - I is removed from the scope interfaces.
- When O is a child:
  - any use inside the child (its own connections with boundary endpoint (O, I)) is rejected with `boundary_interface_in_use`; the user must detach inside the child first;
  - scope connections on (O, I) follow the same `DetachConnections` rule;
  - scope realization targets `ChildInterface(O, I)` follow step 4;
  - the (O, I) port placement is removed;
  - the child draft for O is created or updated: I is removed from its Interfaces and its own realization record for I is removed (the L2 exception).

Effects are returned in (kind, object id) order. The editor shows them and pushes the previous draft onto undo, so undo needs no helper call.

### 4.8 Level merge: rebase after a stale save (Lane E)

`RecursiveLevelMerge.Prepare(RecursiveBlockGraph latest, RecursiveLevelDraft draft).Inspect(IEnumerable<DiagramRequirementResolution>? resolutions)` returns `(RecursiveLevelDraft? Candidate, ImmutableArray<LevelConflict> Conflicts, ImmutableArray<PresentationOverride> Overrides, ImmutableArray<BlockSelection> Path, contexts, SavedOrigin)`.

**Locating the scope.** Find the scope in `latest.SelectedRoot` by exact block-id path (never by name). Absent → conflict `LCK_OWNER_REMOVED`. Another implementation selected → `LCK_SELECTED_IMPLEMENTATION_CHANGED`. A newer unselected head → existing error `unselected_block_candidate`. Base = the draft baselines; Saved = the latest heads on that path.

**Composition rules:**

| Part | Rule |
|---|---|
| Requirement text (scope, each child draft, each connection draft) | Existing three-way text merge. Conflicts are `LCK_REQUIREMENT_TEXT`, resolvable with `RequirementResolutionData`. |
| Name, Definition, ComponentBindings, PhysicalAllocation | Whole record: one side changed → take it; both changed differently → `LCK_NAME` / `LCK_DEFINITION` / `LCK_COMPONENT_BINDINGS` / `LCK_PHYSICAL_ALLOCATION`. |
| Children and root connections | Compose by id. Disjoint additions and removals compose; the same id added or removed on both sides is idempotent. A pin changed differently on both sides → `LCK_CHILD_SET`. Order: saved order, then draft additions in draft order. |
| Child drafts | The saved pin must equal the child draft's baseline, else `LCK_STALE_CHILD`. A child draft with no changes is dropped. |
| Connection drafts | Saved head changed and draft changed (name, kind, endpoints, members, domain, direction, realization) → `LCK_CONNECTION`. Text is merged per the first row. |
| Interfaces | Per interface id, whole record three-way → `LCK_INTERFACE`. |
| Annotations | Per id three-way → `LCK_ANNOTATION`. |
| Interface realizations | Per interface id three-way → `LCK_INTERFACE_REALIZATION`. |
| Presentation | Per element key (`block:<id>`, `port:<block>:<iface>`, `route:<conn>:<i>`, `frame`): one side changed → take it; both changed differently → take the draft and emit `PresentationOverride(key, savedValueJson)`, a non-modal notice. Z-order: the draft's relative order if the draft reordered common ids, otherwise the saved order; additions are appended. Never blocking. |
| New children and connections | Kept. An id now present in `latest` → `identity_reused` error. |

**After composing**, a merged element that references an object the other side removed yields `LCK_DANGLING_REFERENCE` (object id in the conflict).

**Candidate.** It is null while any non-text conflict remains. Otherwise it has saved baselines and merged content. The UI keeps the draft. It shows the approved three-way dialog for text and a read-only conflict list for the other kinds until Design Round C. Nothing is preferred silently except the layout rule above.

`RecursiveRequirementMerge` and `RFA_REBASE_REQUIREMENTS` stay unchanged as legacy.

### 4.9 Reparent: moving a block to another parent (Lane E; an immediately saved management action)

```csharp
public sealed record ReparentRequest(BlockSelection ExpectedRoot, ImmutableArray<BlockSelection> SourceParentPath,
    ImmutableArray<BlockSelection> TargetParentPath, Guid BlockId, ImmutableHashSet<Guid> DetachConnectionIds,
    DiagramBlockPlacement? TargetPlacement, ImmutableDictionary<Guid, Guid> NewRevisionIds, RequirementRevisionOrigin Origin);
public sealed record ReparentPreview(ImmutableHashSet<Guid> RequiredDetachConnectionIds, ImmutableArray<LevelEditEffect> Effects,
    ImmutableArray<Guid> SuccessorBlockIds);
public ReparentPreview PrepareReparent(ReparentRequest request);   // pure; NewRevisionIds may be empty
public RecursiveBlockSelectionResult Reparent(ReparentRequest request);
```

Let P = `SourceParentPath[^1]`, Q = `TargetParentPath[^1]`, B = the moved block. Checks, in order:

1. `ExpectedRoot == SelectedRoot`, else `stale_root_revision`.
2. Both paths start at the root with consecutive exact children, else `reparent_not_in_selected_design`.
3. B is not the root (`reparent_root`) and B is in P.Children (`reparent_block_not_child`).
4. B is not on `TargetParentPath` (cycle: Q is B or a descendant of B), else `reparent_cycle`.
5. P ≠ Q, else `reparent_same_parent`.
6. Every block on both paths is at its state head, else `stale_parent_revision`.
7. `DetachConnectionIds` must equal exactly the set of P's roots whose `Walk` has an endpoint on B: `reparent_connected_block` when some are missing, `reparent_disposition_mismatch` when there are extras.
8. `NewRevisionIds` keys must equal the union of block ids on both paths, with fresh distinct values, else `invalid_reparent_request` / `identity_reused`.
9. `TargetPlacement`, if present, has `BlockId == B` and valid geometry, else `invalid_presentation_view`.

Effects:

- **P′** removes the detached roots and applies the RemoveChild(B) cascade (§4.7).
- **Q′** appends B's unchanged `BlockSelection` to `Children` and adds `TargetPlacement` if given; otherwise B is unplaced in Q.
- **Successors.** Every block on the union of both paths gets exactly one successor revision, built deepest first. Each new children list is the old list with: B removed if the block is P, B appended if the block is Q, and each child on the union replaced by its new selection. Each successor gets `Parent` = old revision, `Origin`, `RestoredFrom = null`, and keeps `RequirementRevisionId`. A shared ancestor gets both changes in its one revision.
- **Unchanged:** B's identity, interior, interfaces, archive, component bindings and allocation. Connections stay in P's archive. Historic roots still reproduce the old hierarchy.

Preview and commit are protected by the file source token (the commit requires the same `expected_source_token` the preview used). No separate preview token is used.

### 4.10 History (Lane A)

- `DiagramHistoryQuery.Compare` reports these categories:
  - `Layout`: object is the block, interface or connection id, or the scope block for `frame`; kind Added, Removed or Changed; name `"Layout"`;
  - `InterfaceRealization`: object is the interface id;
  - `InterconnectRealization`: reported when a pinned connection's selection changed and the only difference is `Realization`.
- `DiagramHistoryEntry` gains `bool LayoutOnly = false`. It is true when the revision differs from its parent revision only in `LocalDiagram.Presentation`.

### 4.11 Create (Lane A)

```csharp
public static RecursiveBlockGraph CreateEmpty(Guid operationId, string rootName, string implementationName,
    DiagramRequirements fields, RequirementRevisionOrigin origin);
```

- Ids from `DiagramIdentity.Derive(op, …)`: document `"document"`, root block `"root-block"`, state `"state:root"`, revision `"revision:root"`, requirements `"requirements:root"`.
- Result: one state, one revision (`Parent = null`, no children, `Diagram = null`), one requirement revision, and no receipt.
- `origin.InputIds` must contain `operationId`.

### 4.12 Migration receipt (types: Lane A) and converter (Lane B, `StructuralMigration.cs` in the Model project)

```csharp
public enum MigrationItemKind { Structure, Block, Port, Connection, ConnectionEndpoint, BlockPurpose, ComponentLink, NetLink,
    ConnectionDescription, Statement, Property, UnresolvedNetBinding, UnresolvedComponentReference, BlockPlacement, PortPlacement,
    ConnectionPlacement }
public enum MigrationOutcome { Converted, Partial, Retained }
public enum MigrationTargetKind { None, Block, Interface, Connection, UnresolvedEndpoint, DefinitionPurpose, ComponentBinding,
    InterconnectSegment, ConnectionName, BlockPlacement, PortPlacement, ConnectionRoute, RetainedGuidance }
public enum MigrationReason { Exact, NameDerivedFromPortNames, NameFromDescriptionFirstLine, CrossLevelEndpoint, SameBlockConnection,
    NoManifestSupplied, DesignNotDeclaredInManifest, GuidanceUnclassified, UnresolvedReferenceRetained, TargetIsElectrical,
    NetCompletenessUnrecorded, PurposeStrengthUnrecorded, PlacementReusedAsFrame }
public enum MigrationSourceEnvelope { EngineeringDesign, NativeSchematicDesign }
public enum RetainedGuidanceKind { Statement, Property, ConnectionDescription }
public sealed record MigrationItem(MigrationItemKind Kind, Guid SourceId, string SourceKey, MigrationOutcome Outcome,
    MigrationTargetKind TargetKind, Guid? TargetId, Guid? ScopeBlockId, MigrationReason Reason, string? Detail);
public sealed record RetainedGuidance(RetainedGuidanceKind Kind, Guid SourceId, Guid TargetId, Guid? ScopeBlockId,
    EngineeringStatementRole? Role, GuidanceStrength? Strength, string? Key, string? Category, string Text, bool HasQuantity,
    bool HasPinConnection);
public sealed record DiagramMigrationReceipt(Guid Id, string SourcePath, string SourceSha256, Guid SourceStructureId,
    Guid SourceCircuitId, MigrationSourceEnvelope SourceEnvelope, string Converter, long NanometresPerDiagramUnit, BlockSelection Root,
    RequirementRevisionOrigin Origin, string? ManifestPath, string? ManifestSha256, Guid? DesignId, MigrationReason DesignResolution,
    ImmutableArray<MigrationItem> Items, ImmutableArray<RetainedGuidance> RetainedGuidance, string SourceStructureXml,
    string SourceStructureSha256)
{ public void Validate(); /* memoizable per instance */ public void ValidateAgainst(RecursiveBlockGraph graph);
  public bool SameContents(DiagramMigrationReceipt? other); }
public sealed record FlatMigrationSource(string RepositoryPath, string Sha256, MigrationSourceEnvelope Envelope, string? ManifestPath,
    string? ManifestSha256, Guid? DesignId, MigrationReason DesignResolution);
public sealed record FlatMigrationOptions(Guid OperationId, string RootName, string ImplementationName, RequirementRevisionOrigin Origin);
public sealed record FlatMigrationResult(RecursiveBlockGraph Graph, DiagramMigrationReceipt Receipt, int Converted, int Partial, int Retained);
public static class StructuralMigration
{ public const long NanometresPerDiagramUnit = 100_000; public const string Converter = "kicad-structural-migration/1";
  public static FlatMigrationResult Convert(EngineeringDesign flat, FlatMigrationSource source, FlatMigrationOptions options); }
```

**Receipt invariants** (`migration_receipt_invalid`):

- **M1 — One receipt.** At most one per document.
- **M2 — Field formats.**
  - `Id` is the operation id.
  - `SourcePath` and `ManifestPath` follow `HardwareRepository.ValidatePath`.
  - The sha values are 64 lowercase hex characters.
  - `Converter == "kicad-structural-migration/1"` and `NanometresPerDiagramUnit == 100000`.
  - `Origin.ActorKind == Import` and `Origin.InputIds` contains `Id`.
- **M3 — Root.** `Root.BlockId == SourceStructureId`, and `Root` is the Parent-null first revision of its state.
- **M4 — Verbatim structure.** `SourceStructureXml` is the canonical `EngineeringXmlText.Render` of the flat `<s:structure>` element, stored as text. `sha256(UTF-8(text)) == SourceStructureSha256`. It parses with DTDs prohibited, validates against `EngineeringDesignXml.CreateSchemaSet()`, and its `id` equals `SourceStructureId`.
- **M5 — Exact coverage.** Items are unique by `(Kind, SourceId, SourceKey)`. Every flat item below appears exactly once:
  - the structure, each block, port, connection, statement and property;
  - each non-blank purpose;
  - each block component link (key: component id) and each connection net link (key: net id);
  - each multi-line description;
  - each block, port and connection placement;
  - each unresolved net binding (SourceId = owner, key = former net id);
  - each unresolved component reference (key `"<Slot>:<componentId>[:<pin>]"`).

  `ConnectionEndpoint` items (key: port id) are added only for unresolved endpoints. `SourceKey` is `""` otherwise.
- **M6 — Targets exist.** Every Converted or Partial item's `TargetId` exists somewhere in graph history: block occurrence, interface, connection occurrence or segment id.

**Converter algorithm.** It is pure. The same flat bytes, source and options give byte-identical XML.

1. **Inputs.** The flat design has already been validated with its declared knowledge libraries by the helper. `options.Origin.ActorKind == Import`, else `invalid_migration_request`.
2. **Ids.**
   - Root block occurrence = `flat.Structure.Id`. Flat block ids become occurrence ids, flat port ids become interface ids, and flat connection ids become connection occurrence ids.
   - Document = `Derive(op, "document")`.
   - For each block X (the root included, keyed by its occurrence id): `Derive(op, "state:<X>")`, `Derive(op, "revision:<X>")`, `Derive(op, "requirements:<X>")`.
   - For each connection L: `connection-state:<L>`, `connection-revision:<L>`, `connection-requirements:<L>`.
   - Segment `segment:<L>:<net>`.
   - Receipt id = op.
3. **No invented history.**
   - Every block and converted connection gets exactly one state, named `options.ImplementationName`, one revision with `Parent = null`, and one requirement revision.
   - Nothing is forked, restored, changed or proposed.
   - Every origin is `options.Origin` with summary `"Converted from flat structural diagram <SourcePath>"`, source `SourceReference(SourcePath, "sha256:<hex>", null, null, null)` and `InputIds = [op]`.
4. **Hierarchy.**
   - The root is named `options.RootName` and has no interfaces.
   - Its children are the flat blocks with `ParentId == null`, in flat order.
   - Each other block is a child of its flat parent.
   - Block interfaces are the block's flat ports in flat order, with `Intent = ""` and Unspecified domain and direction.
5. **Text.**
   - Requirement fields are `("", "", "")`.
   - A non-blank `Purpose` becomes `Definition.Purpose = DefinitionChoice(Selected, [purpose], Information, "", [SourceReference(path, "sha256:<hex>")], Unverified)`, with reason `PurposeStrengthUnrecorded`.
6. **Connection owner and endpoints.** Let a and b be the owners of the first and second port.
   - a == b: owner O = parent of a (root for a top-level block); endpoints `Interface(a, p1)` and `Interface(a, p2)`; reason `SameBlockConnection`.
   - Otherwise O = the lowest common ancestor of a and b, where the root is an ancestor of everything and a block counts as its own ancestor.
   - The endpoint for port p owned by x is:
     - x == O → `Interface(O, p)` (boundary);
     - x is a direct child of O → `Interface(x, p)`;
     - otherwise → `Unresolved(BlockId = direct child of O toward x, Intent = "<block name> / <port name>")`, which adds a `ConnectionEndpoint` item (Partial, `CrossLevelEndpoint`, TargetKind `UnresolvedEndpoint`). The Connection item is then Partial.
   - No intermediate interface is ever invented.
   - Endpoint order is [first, second]. Kind is Abstract and Members are empty.
   - Domain comes from the flat `Kind` by the same ordinal. Direction maps FirstToSecond → FromFirst and SecondToFirst → ToFirst.
7. **Connection name.** The name is the first non-blank line of `Description`, trimmed. If there is none, the name is `"<first port name> – <second port name>"` (U+2013 with spaces) and the reason is `NameDerivedFromPortNames`. If the description has more than that line, a `ConnectionDescription` item is added and the full text is kept verbatim as a `RetainedGuidance` row, not in the requirement fields.
8. **Components and nets.** These convert only when `source.DesignId` is known. That happens only when a manifest was supplied, its bytes matched, and exactly one design has `ModelPath ==` the flat repository path; `HardwareRepository.Validate` makes duplicates impossible.
   - A block component that exists in the circuit becomes `ComponentRealization(DesignId, flat.Circuit.Id, componentId)`.
   - Each connection's `NetIds` become one realization: `InterconnectRealization(Partial, [BoardNet segment per net: DesignId, Circuit.Id, NetId], [], "Converted from the flat diagram's explicit net links; connectors, harnesses, joins and endpoint pins were not recorded.", [source])`, with reason `NetCompletenessUnrecorded`.
   - Otherwise the component and net items are Retained with `source.DesignResolution` as the reason.
9. **Guidance.**
   - Statements and properties are Retained with reason `GuidanceUnclassified` and a `RetainedGuidance` row: text verbatim, role and strength (statements) or key, category and strength (properties), `HasQuantity`, `HasPinConnection`.
   - `ScopeBlockId` is the recursive owner of the mapped target (the block itself, the interface's owner, the connection's owner O, or the root for the structure). For an electrical target it is null and the reason is `TargetIsElectrical`.
   - Nothing is classified into General, Schematic or Routing.
10. **Unresolved references.** They are Retained (`UnresolvedReferenceRetained`) with `Detail` = the flat change kind, slot and reason verbatim.
11. **Geometry.** Divide nm by 100 000 to get diagram units (exact).
    - A block's flat placement becomes its rectangle in its parent's view, keeping locked and fill.
    - The same rectangle becomes the **Frame** of the block's own view (reason `PlacementReusedAsFrame`).
    - A port placement becomes a port on its block's rectangle in the parent view **and** on the frame of the block's own view. One item covers both.
    - A connection placement becomes route `(L, 1)` in O's view, keeping waypoints, label and locked.
    - The root has no frame.
    - When there is no flat presentation, nothing is placed; the coordinate-free round trip is valid.
12. **Item order.**
    1. Structure
    2. For each block in flat order: Block, BlockPurpose?, ComponentLink*, BlockPlacement?
    3. For each port: Port, PortPlacement?
    4. For each connection: Connection, ConnectionEndpoint*, ConnectionDescription?, NetLink*, ConnectionPlacement?
    5. Statements, then properties, then unresolved nets, then unresolved components
13. **Flat file.** Only its bytes are read. It stays byte-identical.

Excluded from conversion: the flat circuit and `EngineeringDesign.ComponentBindings` (knowledge guidance bindings). They stay the electrical model in the flat or model file.

## 5. XML schema 2 (the parent writes the XSDs at freeze; Lane A implements the reader and writer)

`recursive-block-graph-v2.xsd` is v1 with `targetNamespace` and default namespace `:2`, `version` fixed `"2"`, and import `c2` = `connection-archive:2` instead of `:1`. It also adds the following.

**Simple types:**

- `pdecimal`: `xs:string` with the pattern from §3
- `port-side`: Left, Right, Top, Bottom
- `domain`: Power, Data, Control, Analog, Mechanical
- `interface-direction`: Input, Output, Bidirectional
- `realization-state`: Unknown, Partial, Resolved
- `realization-target-kind`: ChildInterface, LocalConnection, Pin
- `sha256`: `[0-9a-f]{64}`
- the migration enums (member names of §4.12)
- `physical-allocation-kind` adds `Harness`

**Local diagram and root additions:**

```xml
<interface id="" name="" domain="Power"? direction="Output"?><intent/></interface>
<local-diagram> interfaces, connections, annotations?,
  <presentation-view units="diagram-unit">?            <!-- units fixed -->
    <frame x="" y="" width="" height=""/>?
    <block ref="" x="" y="" width="" height="" locked="true"? fill-rgb="0..16777215"?/>*   <!-- document order = z-order -->
    <port block="" interface="" side="Left" offset=""/>*
    <route connection="" endpoint="1.." locked="true"?><point x="" y=""/>*<label x="" y=""/>?</route>*
  </presentation-view>
  <interface-realizations>?
    <realization interface="" state="Partial"><unresolved-reason/>?
      <target kind="ChildInterface" block="" interface=""/> | <target kind="LocalConnection" connection=""/> |
      <target kind="Pin"><pin design="" component="" pin=""><sheet instance=""/>+</pin></target>
      <source document="" revision="" page=""? table=""? part-variant=""?/>*</realization>+
  </interface-realizations>
</local-diagram>
... requirement-histories, connection-archives (c2:connection-archive)?, implementation-changes?, refinement-inputs?, proposals?,
<migration id="" source-path="" source-sha256="" source-structure="" source-circuit="" source-envelope="EngineeringDesign"
           converter="kicad-structural-migration/1" nanometres-per-diagram-unit="100000" design=""? manifest-path=""?
           manifest-sha256=""? design-resolution="Exact">?
  <root block="" state="" revision=""/><origin …/>
  <items><item kind="" source="" source-key=""? outcome="" target-kind="" target=""? scope=""? reason=""><detail/>?</item>*</items>
  <retained-guidance><guidance kind="" source="" target="" scope=""? role=""? strength=""? key=""? category=""?
      has-quantity="" has-pin-connection=""><text/></guidance>*</retained-guidance>
  <source-structure sha256="">…escaped canonical structure XML text…</source-structure>
</migration>
```

**`connection-archive-v2.xsd`** is v1 with namespace `:2` and `version` fixed `"2"`. It adds:

- `revision/@domain` and `revision/@direction` (`FromFirst|ToFirst|Bidirectional`), both optional;
- after `members`, an optional:

```xml
<interconnect-realization state=""><unresolved-reason/>?
  <segment id="" kind="BoardNet|Connector|Harness|HardwareInterface|External" label=""? design=""? circuit=""? net=""? component=""?
           physical-target=""? hardware-interface=""? reference=""? repository-path=""?><pin …/>*<unresolved-reason/>?</segment>*
  <join first="" second=""/>*<source …/>*</interconnect-realization>
```

**Writer rules:**

- States, revisions and archives are sorted as in v1.
- Presentation blocks keep z-order. Ports and routes use canonical order.
- Realization records are sorted by interface id; targets by (kind, ids); segments keep model order.
- Receipt items and guidance keep model order.
- Omit `locked` when false, `fill-rgb` when null, Unspecified enums, and empty optional containers.
- `DiagramConnectionArchiveXml` reads `:1` and `:2` and writes `:2`. Constants: `Namespace` becomes the `:2` value, and `NamespaceV1` is added (same for `RecursiveBlockGraphXml`).

## 6. Protobuf (the parent writes at freeze; exact numbers are in `proto_reservations`)

Summary:

- Interfaces gain domain and direction.
- `BlockLocalDiagramData` gains presentation and interface realizations.
- Connection revisions and drafts gain domain, direction and realization.
- The graph gains `migration`.
- The document gains `stored_schema_version` and `source_writable`.
- History entries gain `layout_only`; categories 9–11 are added.
- `PAK_HARNESS` is added.
- New level-draft, level-edit, level-merge, reparent, create, migrate and discovery messages.
- `RecursiveFileAction` gains 11–19; request fields 18–24; result fields 12–20.
- Native editor state 29–34; view `resolved_layout` = 13.
- Presentation decimals travel as canonical strings (`DiagramRectData`, and `DiagramAnnotationPointData` for points).
- `DiagramPresentationViewData.units` must equal `"diagram-unit"`.

## 7. Helper actions (`kicad-mcp --diagram-file`, `RecursiveFileRequest` schema 2; Lane B)

**Dispatch order.**

1. Schema and unknown-field check.
2. Actions 16–19 are dispatched before `Id(document_id)`. For them `document_id` must be empty, else `ambiguous_diagram_file_request`.
3. Each action accepts only its own payload. Unset `block`, `connection`, `field`, `offset` and `limit` are required for 11–19. Otherwise `ambiguous_diagram_file_request`.
4. Every read of a diagram sets `document.stored_schema_version` and `document.source_writable`. The writability probe opens the file ReadWrite without truncating and closes it; it never changes bytes or mtime.
5. Errors fill `error_code`, `error_message` and `error_details` (from `AutomationException.Details`) in `RecursiveFileCommand`.

| Action | Target fields | Payload | Result | Writes |
|---|---|---|---|---|
| 11 `RFA_PREPARE_LEVEL_EDIT` | root, path, id, token (must match) | `level_edit` | `level_edit {draft, effects}` | never |
| 12 `RFA_SAVE_LEVEL` | same | `save_level` | `document`, `save_summary`, `upgraded_from_schema_version` | only if changed |
| 13 `RFA_REBASE_LEVEL` | same; the token must match the latest file only when resolutions are present (`stale_requirement_resolution`) | `rebase_level` | `document`, `level_merge` | never |
| 14 `RFA_PREPARE_REPARENT` | same, token must match | `reparent` (no ids or origin needed) | `reparent_preview` | never |
| 15 `RFA_REPARENT_BLOCK` | same, token must match | `reparent` (complete) | `document`, `save_summary`, `upgraded_from_schema_version` | yes |
| 16 `RFA_CREATE_DIAGRAM` | root; `source_path` = absolute target ending in `.xml`, inside root, parent directory exists; no id; no token | `create` | `document`, `created` | create-only |
| 17 `RFA_PREPARE_MIGRATION` | root; target path; no id; no token | `migrate` (flat token optional) | `migration {receipt, written=false, target_exists, already_converted, existing_*, counts}` | never |
| 18 `RFA_MIGRATE_FLAT_DIAGRAM` | same | `migrate` with a 64-hex `expected_flat_source_token` and origin `DAK_IMPORT` | `document`, `migration {written=true}`, `created` | create-only |
| 19 `RFA_DISCOVER_DIAGRAMS` | no root, path, id or token | `discover {project_file}` | `discovery` | never |

Existing actions 0–10 are unchanged. `RFA_SAVE_BLOCK` behaves like `RFA_SAVE_LEVEL` with only a scope draft.

**Create-only publication.** The new `DesignFilePublisher.CreateNewAsync` reuses the `DiagramRequirementHistoryFiles.CreateAsync` pattern:

1. Write a staged file `<target>.initial-<random N>` with `FileMode.CreateNew`, then flush.
2. Re-check containment.
3. `File.Move(overwrite: false)`.
4. Re-read the target and compare its hash.

Outcomes:

- An IOException after the move was attempted → `diagram_creation_requires_recovery`, and both paths are kept.
- The target already exists:
  - if it was created by the same operation (create: `DocumentId == Derive(op, "document")`; migrate: `Migration.Id == op`), the result is success with `created = false`. This is an observation, not a receipt;
  - otherwise `diagram_file_exists`.
- The migration target equal to the flat path → `invalid_migration_request`.

**Migration inputs.**

- The flat file is loaded once through `FlatStructuralDocuments.LoadAsync(root, path)`. This is extracted from `StructuralEditorFiles.Load` with no behavior change. It accepts `engineering-design` or the native `design` envelope, loads the declared knowledge libraries and validates.
- Token mismatch → `flat_source_changed`. Load failure → `flat_diagram_invalid`, with the inner code in details.
- Manifest: when a path is given, it must be inside the root, and the sha must equal `expected_manifest_token`, else `manifest_changed`. Parse and `Validate`. The design id is resolved per §4.12 step 8. No manifest → `NoManifestSupplied`. These are never errors.
- Already converted: before publishing, scan the target directory's top-level `*.xml` (at most 256 files, same sniffing as discovery) for a recursive document whose receipt names the same `SourceStructureId` → `flat_diagram_already_converted`, with the path and id in details.

**Discovery.** Bounded, deterministic and read-only. Documents are recognized by content, never by file name.

- `project_file` must be an absolute existing `.kicad_pro` path, else `invalid_discovery_request`. Let D be its directory and N its base name.
- `repository_root` is the nearest ancestor-or-self of D that contains `hardware.xml`. The walk goes upward at most 8 levels and stops after the first directory containing `.git` (inclusive). Reparse points on the chain are rejected. If no such directory exists, it is D.
- Candidates:
  - (a) `D/N.system-diagram.xml`, the conventional path (reported with `conventional = true`);
  - (b) every top-level `D/*.xml` except dot-files and `hardware.xml`;
  - (c) each `ModelPath` of hardware.xml designs whose `ProjectPath` equals the repository-relative `.kicad_pro` path.
- At most 256 files, each read as a stream up to the root element with DTDs prohibited.
  - A `recursive-block-graph` root is loaded fully to get the id, stored version, root name and receipt source, or an error status.
  - An `engineering-design` root, or a `design` envelope containing one, is stream-read up to its `structure` subtree to get the id and counts. It is not validated here.
- The result lists:
  - recursive documents: status READY, READ_ONLY, TOO_NEW, INVALID or UNREADABLE;
  - flat documents: CONVERTIBLE, ALREADY_CONVERTED (a listed receipt names the structure id), INVALID or UNREADABLE, plus `changed_since_conversion` when the receipt sha differs from the current sha;
  - `suggested_new_path` and `suggested_path_exists`;
  - the manifest path and token;
  - `truncated`.

## 8. MCP tools (Lane B; they delegate to `RecursiveEditorFiles.ExecuteAsync`)

Every tool keeps the existing error shape `{code, message}`, adds `details[]`, and returns `storedSchemaVersion`. No tool opens a window, starts an agent or touches native schematic or PCB files.

| Tool | Access | Maps to | Parameters and result |
|---|---|---|---|
| `kicad_diagram_create` | write | 16 | `instanceId, expectedInstanceEpoch, repositoryRoot, sourcePath, rootName, operationId (Guid), actor`, optional `implementationName = "Initial"`, `general/schematic/routing = ""`. Handshake epoch guard as in `kicad_diagram_components_set`. Origin `{Agent, actor, now, "Create diagram", [], [operationId]}`. Returns `{instanceId, instanceEpoch, documentId, sourcePath, sourceToken, created, selectedRoot, storedSchemaVersion: 2}`. |
| `kicad_diagram_migrate` | read when `dryRun`, otherwise write | 17 / 18 | `instanceId, expectedInstanceEpoch, repositoryRoot, flatSourcePath, targetPath, rootName, operationId, actor, dryRun = false`, `expectedFlatSourceToken` (required unless dryRun), `hardwareManifestPath?`, `expectedManifestToken?`, `implementationName = "Initial"`. Origin `{Import, actor, now, …, [operationId]}`. Returns the receipt, counts, `created` or `written`, the document id and the token. |
| `kicad_diagram_discover` | read | 19 | `instanceId, projectFile`. Returns the discovery payload. |

Changes to existing tools:

- `kicad_diagram_open` sends schema 2 and uses a schema-2 in-process read.
- `kicad_diagram_read` reports `schemaVersion: 2`, adds `storedSchemaVersion` and a receipt summary at the root (counts, source path and sha, design resolution), and adds `retainedGuidance` rows scoped to the inspected block. Presentation and realizations appear automatically inside `block` and the connection rows.
- `kicad_diagram_observe` passes `resolved_layout` through.
- Every other writing tool reports `upgradedFromSchemaVersion` when non-zero.
- `InstanceTools` capability entries are added for the 3 tools.
- The tool count goes from 25 to 28.

## 9. Native contract

### 9.1 Per-level editor (Lane C, `kicad/recursive_diagram_frame.*`)

- **Versions.** The editor sends schema 2 and accepts only `document.schema_version == 2`. A mismatch shows `companion_version_mismatch`, and nothing is written.
- **Drafts.**
  - `m_draft` and `m_connectionDraft` become one `LevelDraftData` per open level. A separate connection draft remains only for member-path edits (`RFA_SAVE_CONNECTION`).
  - Selecting another element on the same level no longer prompts. Changing level with a dirty draft keeps the approved Save/Decline/Cancel prompt.
  - Undo and redo snapshot the whole `LevelDraftData`.
  - Save uses `RFA_SAVE_LEVEL`. On `recursive_block_file_changed` the editor calls `RFA_REBASE_LEVEL` (bounded at 2, as today) and auto-saves a conflict-free candidate. Overrides show as a non-modal notice. Text conflicts use the approved dialog. Other conflicts keep the draft and show a read-only list until Round C.
  - Decline discards the whole level draft.
  - Removals call `RFA_PREPARE_LEVEL_EDIT` and show the returned effects.
- **Reparent** ("Move to…", control per Round A3): requires a clean level, then PREPARE_REPARENT, then confirm, then REPARENT_BLOCK. After a commit, Undo issues the inverse reparent as a new guarded operation that creates new revisions; history is never rewritten.
- **Viewport.** The existing `m_views`, per level and for the session only, is reported as `level_viewports`. It never dirties a draft or writes.
- **Observation.** `stored_schema_version`, `source_writable` (Save is disabled with an explanation when false), `level_draft`, `resolved_layout` (source PLACED or FALLBACK, plus the dormant count), `canvas_tool` and `selected_interface_id`.

### 9.2 Rendering and fallback (exact; asserted through `resolved_layout`)

- **F1 — Child rectangles.** When no child has an active placement, use the legacy grid over all n children, identical to `nodeRect`: `(140 + (i mod c)·370, 110 + (i div c)·250, 240, 145)` with `c = max(1, ⌈√n⌉)`. A v1 level therefore renders exactly as today. Otherwise the unplaced children U, in `Children` order, use the same grid with `c = max(1, ⌈√|U|⌉)` and origin `(max(x+width over placed) + 130, min(y over placed))`.
- **F1a — Materialize-on-first-layout-edit.** The first layout-affecting edit in a level draft (move, resize, add or remove a child, port placement, route, frame) writes every still-unplaced child's current fallback rectangle into the draft, in the same undo step. Viewing, navigating, selecting and pan/zoom never materialize. Decline removes these entries; Save persists them as the user's layout.
- **F2 — Unplaced child-port anchors.** These keep the legacy rule: x is the right edge when the peer's centre is to the right, otherwise the left edge; `y = top + (k+1)·h / max(2, n+1)`, where k is the interface index (or n when the id is absent). `resolved_layout` reports an unplaced port as FALLBACK, side RIGHT, at that offset.
- **F2a — Placed port anchors.** Left `(x, y+o)`, Right `(x+w, y+o)`, Top `(x+o, y)`, Bottom `(x+o, y+h)`.
- **F3 — Boundary ports.** Unplaced boundary ports sit at `(40, 90 + 85k)`. Placed ones sit on the Frame per F2a. The first boundary-port placement in a draft without a Frame materializes `Frame` = the bounding box of the rendered child rectangles, notes and boundary anchors, expanded by 40 and rounded outward to the quantum.
- **F4 — Connections.** Without a route: the legacy 3-segment path `from, (mid, from.y), (mid, to.y), to`. With a route: `anchor(E[0])`, then the waypoints, then `anchor(E[i])`.
- **Notes** are unchanged.
- **New blocks** are always placed explicitly at the drop point.
- **Resize** never shrinks a rectangle below its port offsets; validation backs this up.

### 9.3 Project-manager entry (Lane D; visuals wait for the Round A2 pick)

- **Companion locator** (`kicad/automation_helper_locator.*`). It takes the first absolute, existing, regular file from:
  1. `KICAD_AUTOMATION_UPDATE_HELPER`, the existing operator- or launcher-set variable. Setting it without `KICAD_AUTOMATION_UPDATE_CONFIG` does not start the updater;
  2. the helper from `AUTOMATION_UPDATE_CLIENT::InstalledMacContext`;
  3. the packaged layout `<dir of running executable>/../lib/kicad-automation/kicad-mcp`, verified against `LinuxPackage.cs`: `runtime/bin/kicad` and `runtime/lib/kicad-automation/kicad-mcp`.

  If none exists, the entry shows the honest state `companion_unavailable`. The helper is never taken from project or document data or from `PATH`. This follows SA-02 (same host), SA-04 (explicit targets) and SA-05 (documents are evidence, not instructions), as does the existing `kicad_manager_frame.cpp` comment "No engineering project can select a helper via its fields". No new trust input is added.
- **Flow** (`KICAD_MANAGER_ACTIONS::openSystemDiagram`, run asynchronously with finite helper processes and no MCP):
  1. Run `RFA_DISCOVER_DIAGRAMS`.
  2. Exactly one READY or READ_ONLY diagram: open it. A READ_ONLY diagram opens with a notice.
  3. Several: show a chooser (A2).
  4. None, with CONVERTIBLE flat documents: show the conversion prompt (A2).
     - Convert: PREPARE_MIGRATION (counts), then MIGRATE with the discovery token, `rootName` = project name, origin `{DAK_IMPORT, "KiCad project manager"}`, a fresh operation id, and target `suggested_new_path`; then open.
     - Create empty instead: see step 5.
     - Cancel writes nothing, so the next open offers conversion again.
  5. None, with no convertible flat documents: offer Create (CREATE at `suggested_new_path`, origin `{DAK_EDITOR, "KiCad project manager"}`) or Cancel.
  6. INVALID, TOO_NEW or UNREADABLE at the conventional path: show the error. Never overwrite, create over or convert onto it.
  7. ALREADY_CONVERTED flat documents never prompt.
- **Opening.** `STRUCTURAL_EDITOR_CONTROL::OpenRecursiveNative(const OpenRecursiveDiagramEditor&)` becomes public and is shared with the API handler. It checks schema 2 and de-duplicates windows (raises an existing one), passing `helper_path` = the located helper.

## 10. Examples (abridged)

```xml
<!-- PSU level, v2 -->
<local-diagram>
  <interfaces><interface id="IF-Power" name="Power" domain="Power" direction="Input"><intent/></interface></interfaces>
  <connections><connection connection="L-Supply" state="…" revision="…"/></connections>
  <presentation-view units="diagram-unit">
    <frame x="0" y="0" width="1200" height="800"/>
    <block ref="PowerStage" x="101.6" y="101.6" width="203.2" height="152.4"/>
    <port block="PSU" interface="IF-Power" side="Left" offset="400"/>
  </presentation-view>
  <interface-realizations>
    <realization interface="IF-Power" state="Partial"><unresolved-reason>Second rail not yet decided.</unresolved-reason>
      <target kind="LocalConnection" connection="L-Supply"/></realization>
  </interface-realizations>
</local-diagram>
```

At the root, the `L-Power` revision (connection archive `:2`) holds a Resolved realization. Its segments are:

1. BoardNet (PSU design, +5V net)
2. Connector J1 with its pins
3. Harness (target W1 in the root's allocation)
4. Connector J3
5. BoardNet (CPU design, VIN)

The joins run 1–2, 2–3, 3–4 and 4–5.

## 11. Error codes (new or newly specific; stable strings)

| Area | Codes |
|---|---|
| Schema and protocol | `diagram_schema_too_new` (A). Existing and reused: `invalid_recursive_block_graph_xml`, `invalid_connection_archive_xml`, `invalid_recursive_diagram_data`, `unsupported_diagram_file_request`, `ambiguous_diagram_file_request`, `invalid_diagram_identity`. |
| Geometry | `invalid_diagram_coordinate`, `invalid_presentation_view` (A). |
| Realizations | `invalid_interface_realization`, `invalid_interconnect_realization`, `physical_target_in_use`, `boundary_interface_in_use` (with details) (A). `ambiguous_block_circuit` is extended. |
| Level draft, edit and merge | `invalid_level_draft`, `child_interior_edit_not_allowed`, `connection_member_edit_requires_member_path`, `stale_child_revision`, `identity_reused`, `level_edit_target_missing` (E). Reused: `unselected_block_candidate`, `stale_connection_revision`, `stale_block_revision`, `stale_root_revision`, `stale_parent_revision`, `recursive_block_file_changed`, `stale_requirement_resolution`. |
| Reparent | `reparent_root`, `reparent_block_not_child`, `reparent_cycle`, `reparent_same_parent`, `reparent_not_in_selected_design`, `reparent_connected_block`, `reparent_disposition_mismatch`, `invalid_reparent_request` (E). |
| Receipt | `migration_receipt_invalid`, `migration_receipt_immutable` (A/B). |
| Create, migrate, discover (B) | `invalid_diagram_create_request`, `invalid_diagram_path`, `diagram_file_exists`, `diagram_creation_requires_recovery`, `invalid_migration_request`, `flat_source_changed`, `flat_diagram_invalid`, `flat_diagram_already_converted`, `manifest_changed`, `invalid_discovery_request`, `diagram_file_read_only`. |
| Native states (C/D) | `companion_version_mismatch`, `companion_unavailable`. |

## 12. Lanes, ownership and sequencing

1. **P — Parent seam (freeze commit):**
   - protos and both new XSDs;
   - the `partial` keyword on `RecursiveBlockGraph`, `RecursiveBlockGraphTests` and `RecursiveBlockLocalDiagramTests`;
   - golden fixtures;
   - the frozen contract copy;
   - `.devcoordinator.toml` native filter;
   - README integration.
2. **A — Model types, validation, XML, history and receipt types.** Milestone **A0**, within the first day, is the complete §4 type surface with each new record's own validation, merged to integration. A1 is the rest.
3. **E — Model operations** (level save, edits, merge, reparent) in new partial-class files. Starts on A0.
4. **B — Codec, helper actions, files, discovery, converter and MCP tools.** File, create and discovery primitives start at freeze. Model-bound work starts on A0.
5. **C — Native per-level editor.** Rendering, fallback, decimals, observation and the level-draft plumbing start at freeze against the protos. Canvas tools wait for Round A1. "Move to…" and inspectors wait for A3.
6. **D — Project-manager entry and locator.** Plumbing starts at freeze. Visible entry, prompt and chooser wait for A2.

**Integration order:** A, then E, then B. B, C and D then flip versions together in one merge train, because the request, document and open schemas move to 2 at once. Rendered journeys run on the integration branch through governed `devcoordinator2 test start` runs only.

## 13. Deferred scope (the parent must record each as a specific open ledger outcome before handoff)

- Boundary interface members (tree), member-level endpoints and per-member realizations. Proto reserved: `DiagramBoundaryInterfaceData` 6–9, `DiagramEndpointBindingData` 8–11.
- MCP tools for level edits, reparent and setting realizations. Agents can carry v2 content through proposals today.
- A read-only resolver that checks interconnect and realization targets against hardware.xml and native designs.
- UI for realizations, the receipt and retained guidance (Round B). The structured-conflict UI (Round C).
- Cross-session per-user viewport memory. Named, filtered, expanded and projection views.
- Diagram-document change notifications (`AutomationEvent.payload` 20–29) for Phase 3 automatic sync (p4a84122a7cc39b0f).
- Retiring `kicad_structure_open` and `STRUCTURAL_EDITOR_FRAME` after the conversion journey passes.
- A notice on the first v1 upgrade (if the owner wants it). Turning attached connections into boundary interfaces during reparent.
- The `automation.diagram.v2` capability advertisement, owned by plan lane 2D (p20d75a441d50afdc).

## Erratum 2026-09-24: flat-diagram conversion retired

Owner decision `n9af098253fec71da`: legacy flat structural diagrams are discarded, not converted. The converter was never
shipped and the flat editor is already removed, so the conversion is retired from the frozen seam (integration grant for
item `flat-proto-cleanup`, ledger `pecd3343bbab4075c`). The earlier sections stay as history; this erratum overrides them.

- **Proto (`diagram_revision_types.proto`, frozen part below 100 included).** Reserved by number and by name, never to be
  reused: `RecursiveFileAction` 17 `RFA_PREPARE_MIGRATION` and 18 `RFA_MIGRATE_FLAT_DIAGRAM`; `RecursiveFileRequest.migrate`
  23; `RecursiveFileResult.migration` 17; `RecursiveBlockGraphData.migration` 11; `DiscoveredDiagramData`
  `migrated_from_structure_id` 7 and `migrated_from_path` 8; `DiagramDiscoveryData.flat_diagrams` 4. Removed types:
  `MigrateFlatDiagramData`, `FlatDiagramStatus`, `DiscoveredFlatDiagramData`, `MigrationItemKind`, `MigrationOutcome`,
  `MigrationTargetKind`, `MigrationReason`, `MigrationSourceEnvelope`, `RetainedGuidanceKind`, `MigrationItemData`,
  `RetainedGuidanceData`, `DiagramMigrationReceiptData`, `MigrationResultData`. `SharedProtoBandTests` is re-frozen in the
  same commit, and lane 2B no longer owns the `StructuralMigration` type prefix or the `SMG_` value prefix.
- **XML (`recursive-block-graph-v2.xsd`).** The `<migration>` receipt and its types (`migration-*`,
  `retained-guidance-kind`, `statement-role`, `guidance-strength`, `sha256`) are removed. A file that still carries a
  receipt is refused with `invalid_recursive_block_graph_xml`, naming the receipt, and nothing is changed.
- **Helper (§7).** A request naming a retired action (by name or number) or the `migrate` payload is refused with
  `unsupported_diagram_file_request` before any file access, and the message says the entry was retired. Any other JSON
  field or enum name this build does not declare gets the same code (strict unknown-field rejection, §2.3), no longer the
  generic `diagram_file_error`.
- **Superseded text.** The flat-conversion ideas of §0 and its rows 6 and 10, the p57f63bd870523b16 row of §1, R2 and R3 (receipt,
  prepare-migration), the §3 migration factor, G4 in §4.5, §4.12, the `<migration>` element of §5, "the graph gains
  `migration`" in §6, actions 17 and 18 and the migration inputs and flat discovery rows of §7, `kicad_diagram_migrate` in
  §8, steps 4 and 7 of §9.3, the receipt and migrate codes of §11, and the §13 item that waited for a conversion journey.
- **Retired flat-editor files** (removed, not converted): `kicad/structural_editor_frame.{h,cpp}`,
  `kicad/structural_editor_control.{h,cpp}`, `kicad/structural_editor_admission.h`,
  `api/proto/common/commands/structural_commands.proto`, `StructuralEditorCodec.cs`, `StructuralEditorFiles.cs`,
  `StructuralEditorTools.cs` (the `kicad_structure_*` tools), `StructuralFileCommand.cs`,
  `NativeStructuralEditorJourney.cs`, `NativeStructuralPropertyJourney.cs`, `StructuralEditorFileTests.cs` and
  `NativeStructuralMigrationJourney.cs`. Never created: `StructuralMigration.cs`, `StructuralMigrationTests.cs` and the
  `kicad_diagram_migrate` tool.

## Erratum 2026-09-24: connection signals in the level draft

Owner decision `ne0261047035e58c6` (`kicad-cn2-connection-signals-in-draft-20260924`): an abstract connection grows during
development into a concrete bus with named signals (for example I2C gaining SDA and SCL), added in the connection panel with
"+ Add detail" (owner decisions `nf53af9d74841b7d3` and `n98a3f3c41084f0ed`). The frozen text of §4.6 L3 and L4, §4.7, §4.8
and §9.1 kept a connection's members unchanged in a level draft, which would make that impossible. The erratum is accepted
(integration grant for lane 2B). The earlier sections stay as history; this erratum overrides them.

Terms: a signal is a member of one of the level's root connections. A drawn signal was added in the current level draft; a
saved signal is stored in the file.

- **§4.6 `NewConnectionOccurrence`** gains `Guid? MemberOf = null` (proto `NewConnectionData.member_of = 200`, lane 2B band).
  An occurrence with `MemberOf` is a drawn signal. `MemberOf` names one of the level's root connections: one drawn in the same
  draft (an occurrence without `MemberOf`), or a saved root that the same draft edits through a connection draft. A drawn
  signal is never itself one of the level's connections. Anything else, a signal of a signal included, fails with
  `invalid_level_draft`.
- **§4.6 L3 (connection drafts).** Instead of "`Members` must equal the baseline": a connection draft's `Members` are the saved
  members it keeps, in their saved order, followed by exactly the signals drawn for it in this draft, in the order the draft
  lists them among its new connections (the order they were added). Anything else fails with
  `connection_member_edit_requires_member_path`: saved members out of their saved order, a member listed twice, a drawn
  signal before or between the kept members, a drawn signal left out, or a member that is neither (a saved or drawn signal
  of another connection). A saved member's own content is still edited only through its member path.
- **§4.6 L3, dropping a saved member directly.** A connection draft may leave out saved members it no longer keeps. The graph's
  own checks then refuse the save while anything still points at a dropped member or at one of its own members: a note that
  targets it without an unresolved reason fails with `invalid_recursive_block_graph`, and a realization of one of the level's
  boundary interfaces that still names it as a `LocalConnection` target fails with `invalid_interface_realization`. Nothing
  is written. Once those notes are unresolved (or removed) and those targets removed, the same drop saves, and the dropped
  signal's saved revisions stay in the history. The editor never drops a saved member this way; it removes saved signals with
  `RemoveConnectionMembers` (§4.7 below), whose cascade does exactly that.
- **§4.6 L4 and step 4 (new occurrences).** A drawn signal's selection appears in none of the scope's roots (instead of
  exactly once). Step 4 gives a drawn root the `Members` of the signals drawn for it, in the order the draft lists them, and
  a drawn signal no members. A drawn root of kind Signal that has drawn signals fails with
  `invalid_diagram_connection_archive` (the archive's rule that a single signal has no members).
- **§4.6 step 2 (new children).** A new block drawn without ports is created with no local diagram. `Diagram = new(Interfaces,
  [], [])` applies only to a new block drawn with ports. A block without a local diagram keeps none until it has ports or
  content: step 3 stores none for a child draft without ports, and step 5 stores none for a level that still has no ports,
  connections, notes, layout or realizations.
- **§4.7 (removals).**
  - `LevelEditCommandKind` gains `RemoveConnectionMembers` (C# value 199, proto `LECK_REMOVE_CONNECTION_MEMBERS = 200`,
    keeping the §2.5 rule C# = proto − 1). `LevelEditCommand` gains `MemberIds` (proto `LevelEditCommandData.member_ids =
    200`) under the §2.5 record rules: no default in the primary constructor, the six-value constructor kept, and an omitted
    list reads as empty.
  - **RemoveConnectionMembers(C, M).** `ConnectionId` C is one of the level's root connections and `MemberIds` M a non-empty
    list of distinct signals C has in the draft: the saved members its connection draft keeps (its saved members when it has
    no connection draft) and the signals drawn for it. `BlockId` and `InterfaceId` are absent and `DetachConnections` is false.
    Anything else fails with `level_edit_target_missing`. `MemberIds` on any other kind of removal fails with
    `invalid_level_draft`.
  - Each signal in M gives one `ConnectionRemoved` effect with its name. A drawn signal leaves the draft's new connections. A
    saved signal leaves C's member list (C gets a connection draft if it had none). Steps 3 to 5 then apply to every removed
    signal and to a saved signal's own members: notes on them become unresolved (`AnnotationUnresolved`), realization targets
    on them are removed (`RealizationTargetRemoved`, and `RealizationStateChanged` when a record's state changes), and their
    routes are removed (`PresentationEntryRemoved`). C itself, its other signals and its other details stay.
  - **Removing a root.** RemoveConnection(C), and a removal that takes C with it (RemoveChild, or RemoveInterface with
    `DetachConnections`), also removes the signals drawn for C. Which roots a removed block or interface touches, and what
    steps 3 to 5 cover, both read one list: C, the saved signals its draft keeps with their own members, and the signals
    drawn for it. A saved signal the draft already dropped is not part of C. "Members are removed through their member path"
    now applies only to a signal's own members.
  - **In the editor.** Instead of "Removals always go through `RFA_PREPARE_LEVEL_EDIT`": when every signal removed at once was
    drawn in the same draft, the editor removes them itself, as the inverse of their addition. Each leaves the draft's new
    connections and the member list its addition extended, the previous draft goes onto undo, no effect is reported and
    nothing is sent to the helper. When any of them is saved, the whole removal goes through `RFA_PREPARE_LEVEL_EDIT` with
    `RemoveConnectionMembers`, and the editor shows its effects. The editor removes no signal of a differential pair; its type
    changes first.
- **§4.8 (rebase).** Drawn signals follow their root. A drawn root keeps them, as every new connection is kept. A saved root's
  connection draft carries the signals drawn for it, so its member list is a change of that draft: when the other side also
  changed or removed that root, the rebase reports `LCK_CONNECTION` for it and returns no candidate, so nothing drawn is
  dropped. Otherwise the candidate keeps the connection draft with its members and every drawn signal.
- **§9.1 (native editor).** A signal is added in the level draft from the connection's Signals row: a drawn signal of kind
  Signal whose `MemberOf` is the selected connection. Its ends are the blocks and ports the connection's ends are drawn on,
  without any pin, selector, candidates or intent stated for the connection's ends. A saved connection's draft appends it
  to its `Members`. Removing a connection's Endpoints row returns its ends, and those of the signals drawn for it in the
  draft, to the blocks and ports they are drawn on; a saved signal's own ends change only through its member path.
  Signals are removed as in §4.7. A separate connection draft remains only for edits of a signal's own content.
