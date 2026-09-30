# Recursive, agent-neutral structural design

Current planning specification, revision 4 — 19 September 2026.

Supersedes the product-model and visual-design framing in [the first plan](structural-refinement-plan.md). Both three-image concept sets have been rejected by the user. Nothing in this specification claims that the revised workflow is implemented. Confirmed direction: Coordinator decisions `kicad-recursive-agent-neutral-refinement` (n3d8b99101da211e6) and `kicad-diagram-at-every-system-level`. Task status remains in the Coordinator.

## 1. The fundamental object is a recursive block

The whole structure is a root block. The root, a whole device, a subsystem, a board-level function and a component-level block use the same model and editing operations. **Every level owns its own connected diagram.** In that diagram, direct child blocks are peers joined by relationships/interfaces meaningful at that level. Opening a child navigates to that child's diagram; it does not merely enlarge a nested box inside the parent's drawing. A tree is navigation assistance, not a substitute for those diagrams.

| Current diagram | Its units and connections |
| --- | --- |
| Device/root | PSU connected to CPU unit through power and telemetry/control interfaces; any other peer units relevant at this scale. |
| Device / PSU | Converters/regulators connected to rails and output interfaces; rail measurement connected to a telemetry ADC; ADC linked to a telemetry MCU and the external telemetry interface. |
| Device / CPU | Processor, memory and other CPU-unit functions connected through their internal power, data and control interfaces. |

Each diagram has its own layout, annotations, selected block/connection, requirements and history. Parent and child diagrams show different levels of the same model, not disconnected drawings. The parent PSU-to-CPU connection maps through each unit's boundary interfaces to the relevant internal rails, signals or pins. A parent link may realize as several internal signals or physical connections; no automatic one-line-to-one-net equivalence.

The abstraction may describe a single PCB, several boards in a stack, a larger device, a car or an airplane. **Functional containment and physical allocation are separate.** Several logical blocks may share a PCB; one unit may span several PCBs or assemblies. A block is not automatically a KiCad project, sheet, PCB or component. A system connection is not automatically a routed copper net: its eventual realization may use board nets, connectors, a cable/harness or another explicitly specified inter-unit interface.

Do not force board-specific fields at higher levels or generate a KiCad project for every abstract unit. Record physical allocation and interface realizations when known, keeping uncertainty explicit. KiCad realizes the appropriate electronics portions; system-level abstraction does not by itself claim specialized vehicle/aircraft CAD or physical-analysis capabilities. A known black-box component may expose interfaces without an internal decomposition; do not invent its internal circuit merely to fill a child diagram.

Every block has a stable identity, selected design state, alternative states/implementations, revision history, requirements and annotations. Opening a block changes the editing scope to its local diagram. Breadcrumbs, hierarchy and up-navigation preserve context; selecting the root exposes the root's own requirements and history rather than a special unrelated dashboard. Returning to the parent restores its layout and selection rather than displaying the child's internals at every ancestor level.

The user may call these states views, versions or implementations. Internally retain enough distinction to avoid accidental circuit changes:

- A **design state/implementation** contains the actual chosen structure and details; each block can select one.
- A **revision** is an immutable historical state; continuing from it creates a new revision or branch.
- A **presentation view** controls visible detail, layout, expansion, layers or projection without changing the selected electrical design.

Do not force this terminology into three permanent selectors everywhere. Show the current block's selected state and history compactly; expand management controls only where needed. Browsing an older or nonselected state remains distinguishable from selecting it for the containing design.

A parent revision pins exact child state/revision references. A child edit creates a new child revision and a new containing selection snapshot up to the root; unchanged siblings remain shared immutable references. Historic root revisions reproduce the exact old hierarchy, not today's mutable child heads. Reused library definitions are distinct from block occurrences, so editing one occurrence does not silently modify all others.

## 2. Partial definition is first-class

Definition is a set of independent facts and constraints, not a mandatory linear wizard or an invented completion percentage.

| Facet | Examples of allowed partial information |
| --- | --- |
| Purpose | Unknown, free-text behavior, known functional responsibility. |
| Type/class | Unknown, regulator, LDO, MCU, memory; may refer to inheritable library classes. |
| Implementation | Undecided, partial child graph, several competing decompositions, selected topology. |
| Component | Candidate set, preferred family, exact model, exact orderable part. |
| Package/footprint | Size or thermal constraints, allowed packages, exact package, validated footprint mapping. |
| Electrical detail | Unknown rails/load/frequency, constraints, sourced characteristics, selected values and conditions. |
| Geometry | No coordinates, relative placement instructions, layout proposal, partial or locked native geometry. |

A package may be constrained before the exact component is chosen; a model may be selected while its package is unresolved. Refinement can change direction or return a facet to unresolved. Record the state of each fact, its sources, applicability and reason when unknown. Do not force a placeholder component, invent values, or silently promote a preference to a strict requirement.

Conceptual states may be saved and selected while incomplete. Native generation must explicitly identify what it can materialize and which unresolved constraints prevent a complete schematic/PCB. An incomplete graph is not an invalid document; pretending it is a complete electrically verified design is invalid behavior.

## 3. Connections are recursive, partially resolved engineering objects

A connection has identity, ownership, selectable refinements/revisions, endpoint descriptions, member connections/signals/groups, all three requirement fields and annotations. Its refinement is not merely relabelling one wire.

Examples supported without premature pin assignment:

- `MCU ↔ Memory`: protocol and signal count not chosen.
- `I2C interface`: a logical interface with identified roles, possibly unresolved signals or pins.
- `I2C_Data`: one exact memory pin connected to an MCU endpoint constrained to a compatible function, with no MCU pin chosen yet.
- A selected detailed interface: named member signals, applicable pairs/groups, requirements, exact assignments and native nets where resolved.

The user example of choosing I2C/SPI versus a DDR5-style interface is an architectural exploration, not a promise that a fixed MCU and memory can use either. Selecting a protocol must check actual component/interface compatibility and may require a component alternative. Do not assume every signal belongs to a differential pair or invent impedance/timing values.

Each endpoint independently supports one of: unresolved intent, block/interface role, typed compatibility selector, explicit candidate set, or exact component-instance/pin/sheet-path reference. Preserve the selector after resolving it so the assignment remains explainable and can be reevaluated. An unresolved selector is not a native electrical connection.

Connection refinement creates member identities linked to their parent relationship. General, schematic and routing requirements apply at a connection, group, pair or individual signal as appropriate. Parent guidance retains its scope: do not blindly copy a total budget or group-matching rule onto every member. Derived member requirements cite their source and conditions; overrides and contradictions are visible.

Cross-level connections use explicit boundary interfaces and realization maps. The local diagram shows child-to-child connections and the current block's boundary interfaces where needed; parents see that block through its external contract. Navigating levels does not cut relationships or create copied ports/nets. Keep functional interface refinement separate from physical allocation: an abstract power/data link may traverse multiple boards or connectors. Resolve exact native nets only from validated endpoint assignments and their board ownership. Net split/merge, pin swaps and component replacement retain old mappings and unresolved requirement bindings; no name/position guessing.

## 4. Three required text fields, with immediate history

Every block, including the root, and every connection/member has these first-class text fields:

| Field | Purpose |
| --- | --- |
| General requirements | Required behavior, implementation vision, operating context, preferences and constraints. |
| Schematic requirements | How to communicate and represent the design: hierarchy, grouping, labels, notes, readability and placement. |
| Routing requirements | PCB placement/routing intent, topology, relative location, layers, thermal/mechanical preferences and electrical constraints. |

Each field may begin empty or openly incomplete. Empty is not zero and does not authorize inferred facts. Additional typed requirement categories can be added later without replacing these three or silently collecting unknown semantics in a generic property bag.

AI and users can rewrite, clarify and improve **any current field** on each iteration. The UI presents the current working text, not a permanently frozen initial paragraph plus an accumulating explanation wall. Preserve immutable earlier revisions, original source statements and their links. When content is derived, retain the source and distinction between evidence, assumption, preference and requirement.

Field history is available next to each field in one action. It shows previous/current text and additions/removals, author or agent, linked prompt/comments, sources and the containing revision. Users can inspect any stage, compare revisions and restore a previous field value as a new revision. Per-field history and whole-block history must agree.

Do not confuse rewriting with permission to silently weaken a strict constraint. A change in meaning, scope or requirement strength appears explicitly in the candidate diff, with consequences or unresolved conflict. Use the existing user-directed refinement/selection workflow; do not introduce a separate approval ritual for every sentence edit. Derived typed constraints and natural-language text remain linked; conflicting interpretations are reported, not silently reconciled.

## 5. Annotations and the agent-neutral refinement loop

The workflow is:

`Selected block revision + recursive diagram + comments/markup + user prompt and attachments → chosen AI agent → new block revision`

The selected block may be the root. The same contract works for a subsystem or a component-level scope. Codex, Claude, Antigravity and other compatible agents are clients, not special cases in the engineering model. Refinement is initiated through the chosen agent console, not a per-block action in the diagram editor. Keep existing agent applications as the prompt/chat surface; no replacement chat client, agent router platform or custom Codex App Server client.

### Editing is separate from agent execution

The user approved the linked diagram walkthrough but rejected its `Refine PSU` / `Refine CPU unit` buttons. The abstract-diagram editor edits the diagram, requirements and comments; its draft actions are **Save** and **Decline**, independent of what the displayed blocks represent.

- **Save:** validate and persist the current editing draft as a new diagram/model revision where changed. Do not silently send an agent request or run AI reasoning. Existing explicitly configured validation/native synchronization can still follow the save.
- **Decline:** cancel the current unsaved editing draft and return to its saved baseline. Do not delete saved revisions, another agent's candidate or unrelated edits. Saving conflicts and write failures leave the draft recoverable.
- Keep the action scope clear when navigating with dirty changes. Unchanged drafts must not create revisions. Reviewing/accepting a separate AI candidate and activating an implementation remain distinct operations; do not overload Decline with deleting a selected implementation or rejecting unseen results.
- **Agent console** is a proposed optional global toolbox entry that opens or hands context to the chosen existing agent application. The agent console owns the prompt, attachments and explicit refinement request. No per-PSU/CPU refine control, hidden auto-submit on Save, or invented console-integration success.

Any integrated console entry must be designed and verified against the actual supported client before becoming an enabled product control. The mockup can show its proposed location, but does not establish implemented launch/context-transfer behavior. This does not reopen the previously excluded custom-chat/App-Server-client architecture.

Annotations can target a block, connection, group, signal, pin, region or empty diagram location. A free-space note belongs to a diagram/block revision and has a presentation anchor; it does not need an invented electrical target. Store original text, drawing strokes/images, author and history. Keep original markup alongside any AI interpretation. After target removal, retain the note as unresolved rather than discarding or reattaching it by proximity.

The context bundle includes:

- Exact root/target block, selected state and base revisions; ancestor context and relevant cross-boundary dependencies.
- The recursive structural/electrical model and view/geometry metadata, not only a screenshot.
- Current General/Schematic/Routing fields plus source/history references as needed.
- Object and free-space comments/markup; relevant unsatisfied requirements and known conflicts.
- The user's original prompt, graphics, files and declared source/library revisions, with content identity and applicability.
- An explicit authorized edit scope and pinned native document/epoch where native edits are involved.

An agent returns a candidate new block revision with rewritten fields, graph/details, source references, changes explained concisely, resolved and unresolved comments, realization deltas, and verification results or honest unknowns. Never overwrite a newer revision with a stale candidate; preserve it for comparison/rebase. Original prompts and attachments remain accessible as provenance without copying full files into every revision.

Use a versioned agent-neutral MCP interface and compiled service. Client identity is provenance, not a hardcoded provider dependency. Verify discovery, tool/image exchange, attachments and request/result handoff with actual clients before advertising support. Standard MCP support alone is not proof of automatic invocation, wakeup or attachment transfer in every application. If an agent is disconnected or has no automatic request notification, save the request and show its true pending state; do not pretend work has started. No new public control endpoint is introduced.

## 6. Versioned storage and ownership

Retain typed XML as persistence and native KiCad files as editable outputs. No opaque native schematic payload in XML. Preserve .NET model ownership, shared protobuf messages and C++ native subsystem ownership.

Proposed contract additions/refinements:

- `BlockIdentity` and `BlockRevision`: same type for root and child occurrences; definition facets, three versioned requirement fields, owned local diagram, external interfaces and exact selected-child revision map.
- `LocalDiagram`: the current block's direct child instances, local connections, boundary-interface appearances, layout and annotations, all revision-bound. It is a full diagram at that scope, not a recursive display container.
- `PhysicalAllocation` and `InterfaceRealization`: explicit mappings between logical units/interfaces and assemblies, boards, connectors/interconnects or native objects. These need not follow logical parentage one-to-one; unresolved mappings remain valid conceptual state.
- `DesignState`: per-block branch/implementation identity and selected revision, with draft/history/archive lifecycle.
- `ConnectionRevision`: abstract or detailed relationship, endpoint selectors/bindings, child member/group/pair revisions, requirements, provenance and realization map.
- `RequirementFieldRevision`: stable owner/category identity, current text, parent revisions, scope, source/interpretation links and author/agent origin.
- `AnnotationRevision`: original text/graphics, element or free-space target, diagram anchor, context revision, interpretation and resolution status.
- `RefinementContext` and `CandidateRevision`: agent-neutral input/output identities, attachments, expected revisions and affected-ancestor/root selection updates.
- `PresentationView`: layout/filter/expansion/projection only; preserve model identity across all rendered views.

The root's selected revision pins the complete reproducible structure. Large unchanged subtrees use immutable references; Git remains sharing/history infrastructure rather than the only meaning of an implementation version. Assembly variants, library-class inheritance and independent design instances retain their existing semantics.

Migration creates a root block and one selected initial state without inventing prior history. Preserve existing names, exact identities, electrical models, typed quantities and native geometry. Existing general guidance is classified only where known; retain unclassified guidance explicitly instead of guessing schematic versus routing intent. Older schemas/readers reject unsupported new formats before mutation. Coordinate-free and partly resolved structures must round-trip without forced completion.

## 7. Revised UI contracts

This is a multilevel diagram editor. The same interaction works at whole-system, unit, subsystem, board-function and component scope. The primary view is one meaningful level's connected diagram, not a tree of nested cards or a single drawing containing the entire recursive structure.

| Surface | Required behavior |
| --- | --- |
| Diagram navigation | Open the current block's complete local diagram of direct child units and connections. Enter PSU, return to the parent, then enter CPU; keep each diagram's layout, notes, selection and viewport. Breadcrumbs and optional hierarchy aid navigation without replacing the diagrams. |
| Root/block header | Selected design state and revision, simple history/change access, new/duplicate/select/remove states. Root gets the same controls as every child. Distinguish preview from selection. |
| Block representation | At the current level show a unit through its purpose and external interfaces, with its selected state and an open-diagram affordance. Type/part/package appear only when meaningful and known. Do not conflate hierarchy with physical boards or display all descendants by default. |
| Connection representation | Abstract link, interface bundle, member/group/pair or signal; expandable detail and independently unresolved endpoint indication. Do not draw an unbound endpoint as a proven pin-level wire. |
| Requirements editor | General, Schematic and Routing fields for the selected element; concise current text with history next to each. Apply/revise/cancel and diff preserve the user's context. Numerical details remain optional. |
| Comment/markup tools | Attach to any element, sketch a region, or leave text on empty canvas. Make target/scope explicit; edit/delete/restore and preserve orphaned notes. |
| Revision/history view | Quickly inspect field or block history at every level, see what changed and which agent/prompt caused it; continue/restore-as-new without rewriting history. |
| Diagram draft actions | Save or Decline the current diagram edits, requirements and comments. Preserve history and conflict recovery, distinguish an unchanged draft, and never initiate AI refinement from these actions. |
| Global agent-console entry | Optional toolbox action for an existing agent application and its explicit prompt/context handoff. No object-specific Refine buttons. Show real integration limits; do not present a proposed connection as working. |
| Candidate review | New block revision with changes to fields, children, connections and bindings; compare/revise/select, surface unresolved constraints and affected siblings/ancestors. |
| Endpoint resolution | Inspect required compatibility, candidates, chosen assignment and why; manually constrain/override when desired, cancel without pin assignment. |
| Native/source views | Open exact native realization or source page with context; synchronize supported native changes back to the correct selected block revision and fields. |
| Shared dialogs and recovery | Create/rename/duplicate/remove/restore states or views, conflict/rebase, dirty close and interrupted activation; exact targets, meaningful recovery and no partial-success claims. |

Apply Product Design to every window, dialog, block, connector, field, menu, shortcut and state. Reuse approved native design components; do not create independent windows for every table row. Design both themes, compact desktop layouts, long text, keyboard/focus behavior, loading/error/cancel/recovery and accessible non-color-only states. Both previous three-image sets are rejected; their numbered choices must not be reused as an approval.

Before another visual-option round, the design brief must express the linked navigation journey: root diagram with PSU/CPU peers → PSU diagram with connected internal units → back to root → CPU diagram with processor/memory internals. Those are states of one experience, not three competing layouts. Keep selected versions, external-interface continuity, differing definition levels, three requirement fields/history and annotations consistent throughout. Product Design can explore alternative interactions once the sequence is faithfully represented; do not repeat an isolated nested-container mockup as evidence of this workflow.

## 8. Native synchronization and activation

Preserve the first plan's revision guards, exact targeting, operation IDs, event-driven updates, dirty-session retention and recoverable multi-document activation, with recursive ownership applied consistently.

Edits to a selected child or a native realized object create the owning new revision and an updated containing/root selection snapshot; inactive states and unrelated siblings do not change. Propagate exact pin swaps and net changes into the right connection members and endpoint bindings while retaining original relationship/history. Requirement violations survive the update and remain visible.

Before materializing a selected root, validate its full pinned hierarchy, resolved endpoints, libraries and native ownership. Prepare deltas, preserve untouched schematic geometry and PCB placement/routes, journal changes across documents, apply native commits, and publish the new selected root only when the coherent operation succeeds. Recovery does not overwrite newer user work. Unknown conceptual portions remain explicit and cannot masquerade as fully generated or verified native designs.

## 9. Implementation sequence and qualification

1. **Recursive contracts and fixtures:** agree block/root identity, partial-definition facets, connection/endpoint selectors, three-field revision history, annotations and agent-neutral context/result contracts. Prototype migration; freeze shared fixtures before implementation is divided.
2. **Model/XML/history:** persist nested states and exact child selections, editable field histories, partial connections, file/graphics references and migration. Prove no history loss or inactive-state leakage.
3. **Agent-neutral refinement:** expose typed MCP reading/writing/rendering/context operations; prove actual agent input → new block revision with attachments, source references, stale-result rejection and cancellation/recovery. Qualify each claimed client independently.
4. **Designed native hierarchy editor:** implement chosen recursive navigation, local state selection, three-field editor/history, element/free-space comments, connection expansion and partially bound endpoint inspection. Manual details are support tools, not the main workflow.
5. **Native realization and reverse refinement:** integrate hierarchical generation, precise native synchronization, root selection activation, schematic readability checks and PCB impact/recovery.
6. **Complete rendered and cross-client qualification:** run focused tests during development and one frozen complete pass once stable. Linux first; Mac ARM64/Intel and Windows remain on hold until the full agreed XML editing workflow is implemented.

Maintain the existing .NET/C++ stack, compiler cache, governed tests, public downloads and signed update feeds. Subsequent user approval “Fantastic! Go ahead!” authorizes implementation of the agreed design and history/conflict workflow (n045fff50030deea3). The Mac/Windows build hold remains in force; design approval is not platform qualification.

## 10. Acceptance fixtures derived from this feedback

- The root and at least three nested levels use the same operations. Each child has two selectable states; changing one child creates a new containing snapshot, preserves siblings and reproduces the old whole design from history.
- The root shows PSU and CPU as connected peer units. Open PSU and operate its local converter/rail/ADC/telemetry-MCU diagram; return without losing root context, then open CPU and operate its processor/memory diagram. Show explicit parent-to-child interface correspondence without flattening all internal objects into the root.
- Reuse this abstraction with (a) multiple functions on one PCB, (b) units spanning a PCB stack, and (c) a larger-device fixture with units and inter-unit links. Do not infer one block per board/project or treat a cable/interface as a copper trace. Preserve conceptual diagrams before physical allocation is decided.
- Mix an unknown block, type-only LDO, part-selected/package-unresolved block, package-constrained/part-unresolved block and fully mapped component in the same saved diagram. Prove independent facets and coordinate-free round trips.
- Start with MCU ↔ Memory. Keep an undecided protocol, create distinct interface alternatives, expand appropriate members/groups/pairs and derive constraints only from declared sources. Reject an actually incompatible fixed-component pairing without rejecting valid incomplete intent.
- Resolve one I2C data endpoint exactly and leave the other as a compatibility selector. Preserve it without native pin fabrication; later select a validated endpoint and retain the selector/source/history. Test no candidates, multiple candidates, a changed component and a stale pin choice.
- Rewrite General, Schematic and Routing text over several agent and user revisions on the root, a child, a connection and a signal member. One-action field history shows every old value and author/prompt; restore as new and expose meaningful constraint changes.
- Add an object comment, connection comment, free-space note and drawing, plus an agent prompt with a file/image. The selected client receives the actual structured diagram, notes and attachment identities and produces a traceable new block revision. A second compatible client can consume that same contract; untested clients are not advertised as proven.
- Save and Decline manual diagram/comment/requirement edits through the rendered editor and prove neither creates an AI request. Save preserves validated content/history; Decline preserves the saved baseline and unrelated work. Exercise dirty navigation, unchanged drafts, failed writes and concurrent updates. Initiate refinement separately from the actual agent console; qualify an optional global launcher/handoff independently.
- Test parent/child requirement scope without copying total budgets onto every member. Preserve conflicting/unknown statements and their applicable conditions rather than resolving by last-write-wins.
- Open/back/up across complete local diagrams, change presentation view, select a local implementation, preview past revisions, compare, edit/remove/restore and reopen through the rendered UI. Confirm which actions change model selection and which are navigation/presentation-only.
- Exercise native move/rewire/pin swap, undo/redo, missed events, competing field rewrites, dirty close, interrupted activation, write failure, cancelled agent job and process restart. Never lose original notes/fields or silently rebind deleted targets.

## 11. Completion scope and design state

Main outcome `pa29e2edfadaa6964` remains open. Scope links: recursive model/history `p353f93bbed7b2df6`, agent loop `pa48933d0fe0a5c2f`, native UI `pc12a7fddf47cab23`, activation `pe84454b4680082e1`, connection editing `pe9e76b5403614990` and annotations `p47c5ac4e8184b9a4`. Concrete connection-refinement acceptance is tracked by `pf92d0ecdec8805b4`; three-field rewrite/history acceptance by `p390b40bed99e0ab2`. These are references, not a parallel file ledger.

Design status: both first- and second-round concept sets remain rejected. The linked System/PSU/CPU walkthrough is now approved as the visual direction, subject to replacing per-unit Refine controls with Save/Decline and treating any agent-console integration as a global toolbox action. Do not request another selection among rejected images or reopen this layout approval. Current code and passing basic editor tests remain useful groundwork, not evidence that this revised experience is implemented.

## 12. Rejected second concept set — historical reference

These generated studies were rejected because they did not adequately express a separate connected diagram at every system level. The files are preserved as design history; their former display order was:

| Current displayed choice | Local asset |
| --- | --- |
| 1 | [Recursive canvas](recursive-refinement-concepts/01-recursive-canvas.png) |
| 2 | [Focused level](recursive-refinement-concepts/02-focused-level.png) |
| 3 | [Inline field history](recursive-refinement-concepts/03-field-history.png) |

All three are rejected. There is no pending choice among them. The next design artifact is the linked root/PSU/CPU diagram-navigation journey, grounded in the clarified system-level model, not another selection among these images.

Review caveats before detailed design: generated version labels must be made scope-consistent (concept1 conflates selected root and Controller revisions; concept2 gives Diagnostics an inconsistent tree marker). Distinguish an entire bus from its individual data member; a label such as I2C_Data is not itself the whole two-signal interface (concept3). Current requirement text must match the current entry in its opened history (concept3 does not yet). Use source-backed fixture data in the next detailed design and later actual native rendering, not generated labels as engineering truth. Remove duplicate actions and unnecessary helper copy while preserving keyboard access and visible target scope. Both light and dark themes are required for the selected direction regardless of the mock's theme.

## 13. Diagram-level walkthrough — approved direction with corrected edit controls

The user requested ImageGen for the linked navigation journey. This is one light-theme walkthrough, not a third set of competing options:

1. [System, revision 5](level-diagram-walkthrough/01-system-v5.png): PSU v3 and CPU unit v2 are peer units with Power and Telemetry interfaces. No internals are drawn inside those blocks.
2. [Open PSU, revision 3](level-diagram-walkthrough/02-psu-v3.png): a separate local diagram contains converters, an LDO, rails, telemetry ADC and telemetry MCU. External Power/Telemetry boundaries correspond to the root interfaces.
3. Return to the same System diagram, then [open CPU unit, revision 2](level-diagram-walkthrough/03-cpu-v2.png): a separate processor/memory diagram exposes the corresponding input boundaries and an unresolved memory interface. The inspector now demonstrates connection requirements and history.

All scenes use the same chrome and keep General/Schematic/Routing fields and comments available. Generated connection errors were corrected in the final PSU/CPU images; earlier generations are not the selected walkthrough files. [Exact prompts](level-diagram-walkthrough/imagegen-prompts.md) record built-in ImageGen generation and targeted edits.

Feedback/approval: the user approved the diagram-navigation direction and requested Save/Decline in place of Refine PSU/CPU. The original images above preserve the previous artwork. Current references are [System](level-diagram-walkthrough/01-system-v5-edit-controls.png), [PSU](level-diagram-walkthrough/02-psu-v3-edit-controls.png) and [CPU](level-diagram-walkthrough/03-cpu-v2-edit-controls.png), edited with built-in ImageGen using the [saved correction prompt](level-diagram-walkthrough/control-correction-prompts.md). They preserve the approved layout and put the proposed Agent console entry in the global toolbar.

The approval is not a runnable prototype or electrical validation. No actual component/pin selections or board allocation are established by these images. Remaining detailed design includes edit/decline states, root-level Up disabled state, history/implementation selection, optional global agent-console behavior, compact windows and dark theme. Use native/typed diagram data and rendered interaction tests before claiming the interface works. The authoritative correction is `kicad-diagram-save-decline-agent-console` (n68d6149bd8ab9bca).

## 14. History and edit-conflict design continuation

The user's instruction to continue covers the next detailed design pass. [History and conflict interaction contracts](history-and-conflicts/interaction-specification.md) and [generated states](history-and-conflicts/01-diagram-history.png) extend the approved layout. They cover whole-diagram read-only history, [one field's history](history-and-conflicts/02-field-history.png), and [a conflicting save](history-and-conflicts/03-save-conflict.png). Exact [generation prompts](history-and-conflicts/imagegen-prompts.md) are retained.

Core rule: historical browsing changes no saved selection; restoring produces a draft; only Save publishes a successor. Field restoration preserves unrelated draft edits. Closing a history/conflict surface preserves work; editor Decline cancels the current draft, never overwrites another actor's newer saved revision. Conflicts retain base/draft/latest and require a revision-bound resolution, without silently preferring the user or an agent. None of these actions initiates AI refinement.

The user approved these history/conflict states and instructed implementation to proceed (n045fff50030deea3). Do not reopen the approved basic layout or require another selection among these states. They are not yet implemented or rendered-interaction-qualified controls. Dark/compact variants, complete control coverage and remaining implementation/view-management surfaces remain part of the full UI design and native qualification scope.
