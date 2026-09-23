# AI-driven structural refinement: implementation and interaction plan

**Superseded planning draft.** The user rejected all three concepts below and clarified recursive root/child blocks, partial connections, agent-neutral refinement and rewritable three-field requirements. Use [the current revision 2 specification](recursive-structural-refinement-plan.md). This file and its images are retained as design history, not pending choices or implementation approval.

Planning candidate — 19 September 2026. This is a specification, not a claim that the capabilities below exist. The Coordinator owns task status, decisions and execution evidence; this document defines product behavior and acceptance criteria.

## 1. User outcome and scope

The user supplies requirements, source documents and an implementation vision. Codex develops and revises a solution. The native structural diagram lets the user inspect that reasoning, give instructions in context, compare solutions and choose what is active. Users do not need to see XML or enter component-derived electrical values.

A persistent `Power supplies CPU` relationship can first be unrealized, then realized by one regulator, then by several converters, regulators and sequencing circuits. The abstract function and original requirements survive those changes. This example is a workflow fixture, not a claim that those circuits are interchangeable or technically adequate without selected parts and checks.

Confirmed direction: `kicad-progressive-structural-diagram`, `kicad-intent-strength-realization`, `kicad-ai-refinement-not-parameter-first` (n9c8d9c93d5f8490d), and `kicad-block-alternatives-revisions-views` (nc19193a3e0f768ed).

Preserve the agreed C++ native KiCad UI, C#/.NET engineering model and MCP service, shared protobuf/NNG integration, multiple child designs and explicit instance/document ownership. Codex Desktop remains the conversation surface. Do not introduce another chat application, custom Codex App Server client, Python/JavaScript runtime, split-host control bridge or unattended Mac worker.

This request authorizes planning and design. Do not implement the new interaction model solely to complete this specification. Preserve the existing implementation, tests and dirty work while its successor is designed.

## 2. Product vocabulary and invariants

| Concept | Meaning and ownership |
| --- | --- |
| Logical block | Stable function, purpose, user requirements and boundary interfaces; independent of a particular topology. |
| Alternative | A candidate implementation of that function, with its own internal blocks, components, nets, decisions and layout. May remain abstract or partially realized. |
| Revision | An immutable checkpoint of an alternative or shared intent. Editing history creates a successor or forks an alternative; it never rewrites the historical record. |
| Draft | Mutable, recoverable work based on an exact revision. Unfinished or invalid edits remain drafts rather than corrupting committed revisions. |
| View | A projection of an explicitly targeted alternative/revision: functional overview, expanded implementation, schematic, PCB or a saved custom view. Changing views is not changing solutions. |
| Design configuration | The exact set of alternative revisions selected for the project, including child-block selections. Native outputs belong to this configuration. |
| Instruction | The user's original words/markup and explicit target. AI interpretation, execution and resolution are separate records. |
| Realization | Exact links from intent and interfaces to chosen sub-blocks, component instances, pins, nets, sheets and board objects. |

Original intent, AI interpretations, sourced facts, assumptions, proposed choices and current realizations remain distinguishable. Numerical specificity and requirement strength are independent: a sourced voltage is not automatically a user requirement; a textual instruction may be strict.

Implementation alternatives are not assembly/BOM variants or reusable component-class inheritance. Those existing concepts retain their own identity and lifecycle; a selected alternative may itself support assembly variants and use inherited library guidance.

An alternative does not own or overwrite shared user requirements. It pins the intent revision against which it was evaluated. A changed shared requirement marks affected alternatives for reevaluation without silently rewriting them. Original statements remain in history when the user explicitly revises requirements.

Previewing and editing an inactive alternative must never alter the active schematic, BOM or PCB. The UI must distinguish `Viewing` from `Used in design`. A historical inspection is read-only until the user chooses to continue from it. Ordinary view selection, zoom, layer visibility and selection must not invalidate electrical checks; presentation edits have their own revision scope.

## 3. Model and XML persistence

Extend the existing typed model; do not create a second authoritative C++ model or hide a native schematic blob in XML.

Proposed records:

- `LogicalBlock`: stable ID, name, purpose, intent-set reference, interface IDs and alternative IDs. Parentage of implementation-specific internal blocks belongs to an alternative, not to shared intent.
- `IntentRevision`: immutable authored statements, applicability, sources and supersession links. Preserve original user text/drawings separately from AI summaries.
- `ImplementationAlternative`: ID, owning block, name, origin/parent alternative, current committed revision, draft references and archive state.
- `AlternativeRevision`: ID, parent revision(s), pinned intent/dependency revisions, author/origin, instructions addressed, structural/electrical/native model references and exact realization map. Store concise rationale, not hidden model reasoning.
- `BoundaryInterface`: stable semantic relationship and revisioned contract. An abstract power interface may map to multiple rails; mappings may be one-to-many and many-to-one where explicitly supported. Do not equate abstract links with native wires.
- `DesignConfigurationRevision`: coherent selection map and pinned library/source revisions, including child designs and inter-board interfaces. Resolve dependencies before checking or materializing it.
- `ViewDefinition`: ID, name, target scope, abstraction/filter/expansion settings and independent presentation. Built-in views are restorable; custom views can be added, renamed, duplicated, edited and removed without deleting design data.
- `RefinementRequest`, `CandidateChangeSet`, `ActivationPlan` and `ActivationReceipt`: durable targeted requests, exact input digests, proposed changes, evidence and terminal outcomes.

Keep `hardware.xml` as repository entry point. Evolve `design.xml` to a new explicitly versioned envelope referencing typed revision documents under each child design's engineering directory. Prototype migration on isolated fixtures before choosing whether small records remain inline. Keep references relative and declared; complete schematic reconstruction still requires only XML and declared libraries/assets. PCB geometry stays native and is retained for the configuration/alternative that owns it.

Prefer stable, separate revision files and exact references over embedding all histories in one ever-growing XML document. Git stores/shares those files; Git branches are not the sole user-facing identity for a block alternative. A whole-project clone per block alternative would obscure shared requirements and multiply synchronization problems.

Identity rules:

- Stable logical block/interface IDs survive refinement; internal realization IDs survive only when the object is genuinely the same.
- Duplicating an alternative creates new ownership identities and explicit ancestry/mappings; it must not share mutable native UUIDs accidentally.
- Shared components/resources have an explicit owner outside competing implementations; reject double ownership and incompatible selections.
- Removed or split objects retain unresolved instruction/requirement bindings. Never guess replacement identity from names or position.
- Existing designs migrate to one alternative and initial revision with an exact mapping receipt. Preserve existing role/provenance; do not label all old fields as requirements or invent missing history.
- Older readers reject newer schemas before mutation. Import/export and migration are deterministic and idempotent.

## 4. User journeys

1. **Start with intent.** In existing Codex chat, describe the design and attach sources. AI creates abstract blocks and interfaces, preserves the words supplied, and identifies consequential unknowns. No mandatory voltage/current/component form.
2. **Refine in context.** Select a block, link, region or whole design; add an instruction or markup. Its target is visible before saving. Codex reads the exact target/revision and develops a new candidate, normally in a draft or alternative without replacing active work.
3. **Inspect the explanation.** Show what changed, why, the requirements it addresses, source references and unresolved assumptions. Open numerical details only when needed. A generated value has evidence or is explicitly an assumption, never fabricated verification.
4. **Compare alternatives.** Compare two candidate revisions against the same requirements and surrounding design. Show topology/realization differences and only measured or sourced cost/power/space facts. `Not evaluated` is not zero and an incompatible solution is not shown as qualified.
5. **Use a solution.** Select `Use in design` or give Codex an equivalent explicit instruction. Prepare an impact preview and apply a coherent configuration change. Do not require a new approval ritual for routine delegated changes, but never silently activate something merely because it was inspected.
6. **Revisit history.** Inspect revisions, compare them, fork an alternative from a past revision, or restore past content as a new revision. Retain later revisions and their provenance.
7. **Switch detail or view.** Collapse to `Power → CPU`, expand the selected implementation, or open its exact native realization. User-defined view filters and layout do not change electrical meaning. Editing an electrical object through any view changes its owning alternative, not a private copy of the circuit.
8. **Edit natively and feed back.** Changes to the active native schematic/PCB create a draft/revision of the active realization and update exact bindings. Preserve original intent, report violations, and invalidate only affected checks. Inactive alternatives remain unchanged.
9. **Retire and recover.** Remove an unused alternative/view through an exact-target action. Remove from normal lists recoverably; retained revisions referenced by configurations, evidence or children remain resolvable. An active alternative requires a replacement or an explicitly incomplete design state. Do not cascade-delete valuable history or other alternatives.

## 5. Complete UI design inventory

These are interaction contracts, not a commitment to a separate window for every row. Prefer one workspace with contextual panels and a few shared dialogs. Every control, context menu, shortcut, block representation, loading/error state and dialog below goes through Product Design before implementation.

| Surface | Main task and controls | Required states and design constraints |
| --- | --- | --- |
| Structural workspace | Navigate child design and block; change view; inspect selected alternative; give a scoped instruction. | Empty/unrealized, active solution, inactive preview, historical view, dirty, disconnected. Diagram is primary, not a form/dashboard. |
| Block and connection graphics | Select, expand/collapse, navigate interfaces, inspect realizations, attach instructions; optional manual move/resize/connect. | Abstract/partial/realized, selected/locked, warning, missing target. Labels retain meaning; abstract connections are not disguised pin-level nets. |
| Scoped instruction affordance | One concise text/markup entry with visible target, save/submit, edit and cancel. | Unsubmitted, pending, being handled, addressed, unresolved, failed/cancelled. No duplicated chat history or fake AI-running indicator. |
| Intent/details panel | Original requirements first; interpretation, sources, assumptions and realization details on demand. | User-authored versus derived; strict/preference; conflicting/unknown/violated. Manual numeric override stays secondary and records scope/reason. |
| Alternative picker/list | View, create, duplicate, rename, compare, use, remove and restore alternatives of this block. | Active versus merely viewed; draft, incomplete, stale evaluation, archived. Changing highlighted row does not activate it. |
| New alternative editor | Name plus optional instruction; start from current, selected past revision or fresh abstract draft. | Inherited requirements shown compactly; create/cancel and dirty draft recovery. Avoid a topology/parameter wizard. |
| Comparison view | Synchronized side-by-side or overlay diagrams and requirement/source differences; inspect, continue, use. | Different abstraction levels, incompatible interfaces, missing evidence and changed common baseline. No invented scores or metrics. |
| Revision history | Inspect, compare, continue from and restore a checkpoint. | Current/draft/past, unavailable dependency, divergent work. Exact scope and author/source remain available without UUIDs dominating UI. |
| Activation/impact preview | Show affected blocks, interfaces, schematic objects, board placement/routes and checks; apply/cancel. | Compatible/incomplete/stale/blocked, dirty native edits, progress, rollback and recovery. Keep current design until successful commit. |
| Saved-view controls | Add/duplicate/rename/edit/remove custom views; select targets, filters, expansion and layout; reset built-ins. | Current/historical/alternative preview, missing selected object; no hidden model mutation from a view preference. |
| Native schematic/PCB surfaces | Navigate exact realized objects; attach or edit instructions/markup; inspect associated requirements. | Repeated sheets, removed or ambiguous targets, inactive candidates. Use existing native windows; do not duplicate designers. |
| Source/provenance viewer | Open declared document, exact revision/page/table/variant and linked statement. | Missing asset, scanned page without text, contradictory source. Reuse existing document tooling and do not imply nonexistent extraction. |
| Shared delete/archive confirmation | Explain exact object, references, consequences and recovery; remove/cancel. | Active selection, last alternative, referenced history, dependent views. Removing a view never deletes its circuit. |
| Shared conflict/recovery panel | Compare base/current/candidate; preserve both, rebase, choose a scoped resolution, retry or cancel. | External XML, native edit, changed dependency, stale AI proposal, crash/write failure. No blanket whole-document replacement. |
| Job controls | Observe real refinement/check/simulation progress; cancel, inspect result or retry safely. | Queued, running, waiting for Codex/choice/editor, success, failure, cancellation, reattach. Do not claim a native note automatically starts Codex without verified support. |

For every surface, the design handoff includes: entry point, target and selection rules, empty/populated/long-content states, meaningful decisions, keyboard/focus behavior, success/cancel/failure/recovery, downstream persistence, light/dark themes, and compact-window/text-scaling behavior. Reuse the same native component specifications across surfaces instead of independently redesigning each button.

## 6. Product Design process and approval boundaries

Use `product-design:index` → context → ideation → selected design → implementation guidance → design QA. The existing approved option-1 native canvas is a visual reference, not permission to preserve the rejected parameter-first workflow.

Design pass A: exactly three alternative layouts for the primary AI-refinement workflow, grounded in the existing KiCad canvas and the supplied rejected-form screenshot. All represent the same product and capabilities; vary hierarchy/interaction rather than just colors. Selection is pending. Mock data is illustrative, not engineering evidence.

Design pass B: after selection, produce coherent journey frames for the main workspace, instructions, alternate solutions, comparison, revision history, view management, activation, conflicts and native annotations. Each state has a stable frame/control ID. Show both themes and compact desktop variants. Do not implement unselected substantial redesigns.

Design pass C: document shared native controls and block/link visual grammar, including focus, hover, selection, disabled, text overflow, source links, numerical overrides and validation. Routine controls inherit the approved system but still receive a Product Design review. New interaction patterns receive their own focused exploration when needed.

Design pass D: compare actual native captures with the selected frames at matching dimensions and states. Fix actionable differences and exercise each enabled control through rendered UI. No screenshot, stub, handler invocation or compile result alone proves the workflow.

The initial three overview images do not claim that every dialog is visually finalized. The inventory above ensures none is introduced without its later design and behavior review. Do not redesign the public download website as part of this work.

## 7. Codex/MCP and native contracts

Extend the existing compiled service, not a separate AI orchestrator. Native annotations are engineering records, not another conversation UI. The initial runtime integration spike must prove how the actual Codex Desktop session discovers pending scoped instructions through MCP. Verify advertised notification/subscription behavior in that build; if no automatic wake exists, provide an honest pending state and a verified explicit handoff through the existing chat. An automatic-handoff gap remains open rather than being hidden behind a button.

Planned typed operation families (names provisional):

- block/intent/interface inspect and versioned updates;
- alternatives list/create/fork/update/archive/restore and exact-revision comparison;
- draft apply/validate/checkpoint and history inspect/restore-as-new;
- view list/create/update/delete/render, with one target per observation and multiple requested views per MCP call;
- instruction list/read/claim/respond/cancel, preserving original text/markup and target revision;
- refinement candidate creation, evidence attachment and affected-object/requirement queries;
- configuration activation prepare/apply/status/recover and exact native-realization navigation.

Every mutation includes instance/document/configuration/block/alternative targets as applicable, expected revision/digests, process epoch and operation ID. Long jobs pin their inputs and expose cancellation, progress, terminal results and stale-result rejection. Do not expose proposed operations as available until they have real behavior.

Independent alternatives may be developed concurrently. Serialize changes to the same draft, shared intent or active configuration. Use event-driven change streams and filesystem notifications; resnapshot after missed events. Native manual edits always update the correct active alternative. Disconnection preserves dirty sessions and pending candidates.

Observations bind model, presentation, viewport and native revision. Comparing two candidates may render isolated preview documents while leaving the user's active windows unchanged. The UI can show one chosen view; MCP may obtain multiple detail/layer/region/3D views with explicit identities and capability limitations.

## 8. Safe activation and native synchronization

Activation is a recoverable multi-document operation, not a claim that separate KiCad documents share a native atomic transaction:

1. Capture exact active configuration, shared requirements, dependencies, XML and native revisions; handle dirty editor state without discarding it.
2. Resolve the candidate and its child selections. Check interface compatibility, ownership, requirements, libraries and electrical mappings. Materialize and verify the candidate in isolated documents first.
3. Build an impact plan and journal with preimages, exact operation IDs and native undo references. Include affected routes and required checks; preserve unaffected geometry and locks.
4. Recheck all guarded inputs, acquire affected document ownership and apply ordered native commits. Until completion, expose `applying` rather than falsely reporting a coherent new active design.
5. Publish a single configuration pointer/receipt after all required commits and verification succeed. Emit changes with origin IDs to suppress sync loops.
6. On failure, compensate owned completed steps or leave a recoverable paused journal. Never overwrite intervening user changes to force rollback. On restart, determine each step's actual result and resume or recover without duplicate edits.

Electrical/native realization switching may invalidate routes; report them for repair. Do not silently regenerate the whole PCB. Requirements and manual edits remain available even when a candidate is rejected. Incompatible candidates can be retained and edited but must not be reported as checked usable designs.

## 9. Implementation sequence and ownership

| Phase | Deliverable and exit condition | Depends on |
| --- | --- | --- |
| 0 — Design and contracts | Selected primary workflow, all surface/state specifications, identities, versioned XML/protobuf contracts and shared fixture. Actual Codex instruction-discovery spike resolves integration limits. | This planning/design review. |
| 1 — Model/history | Logical intent, alternatives, drafts, revisions, views and configurations persist; migration and duplicate/archive/restore semantics pass focused tests. No user data lost or fabricated. | Phase 0 contracts. |
| 2 — AI refinement | Exact scoped instructions reach real Codex/MCP; two iterative candidates preserve intent, sources and unresolved issues. Cancellation/retry/reattach work. | Phase 1; integration spike. |
| 3 — Native workspace | Selected AI-first canvas, alternate/history/view controls and comparison work through real UI; manual details remain optional. | Phase 1; approved surface designs. Can develop beside Phase 2. |
| 4 — Active realization | Activation planning, coherent configuration publication, schematic/PCB deltas and recovery; inactive edits never touch active designs. | Phases 1–3; existing full XML/sync foundations. |
| 5 — Cross-view refinement | Native edits, markup, exact source/object navigation and undo/redo feed back into the owning alternative, preserving requirements and unchanged work. | Phase 4; existing annotation/library/source outcomes. |
| 6 — Qualification/delivery | Full fixture, all enabled UI journeys, themes and failure/recovery checks on Linux; then exact-candidate Mac ARM64/Intel and Windows evidence once the complete agreed XML-workflow hold condition is met. | All earlier phases and the broader XML workflow; not just this editor checkpoint. |

Directory ownership for future implementation: model/XML/migrations in `automation/src/KiCad.Automation.Model` and its schemas; protocol in `api/proto`; IPC adapters in `KiCad.Automation.Native`; public tools in `KiCad.Automation.Mcp`; structural UI in the existing `kicad/structural_editor_*` subsystem; schematic/PCB/library/simulation behavior in their native owning subsystems. Managed, native and rendered tests stay with their established harnesses. No delegation before shared contracts and the fixture are fixed; one integration owner and one full qualification owner.

Reuse the compiler cache and warm Linux build, bulk storage and governed check graphs. Run focused tests per coherent change, then broad verification on a frozen candidate. Continue independent work in isolated source while required builds run. Preserve 96-hour delivery/110-hour stop settings for applicable targets, the public download site and signed update feeds. No Mac or Windows build is started during this planning task or before the existing XML-workflow hold condition is met.

## 10. Cross-component acceptance fixture

Use a small repository with a Controller child design and a second independent child design, original user instructions and fixed source-document/library revisions.

Begin with abstract Power → CPU and a data interface to Memory, no user-entered rail values. Use fixture components/models and supplied source facts, clearly separated from real product recommendations.

- Produce alternative A, a simple regulator realization; derive its applicability from selected parts rather than assuming it fits every CPU.
- Produce alternative B with multiple converters/regulators and rails; preserve the logical Power and CPU/interface identities, original intent and A.
- Revise B when CPU selection or operating conditions change. Retain old source references/revisions and record why the new topology differs.
- Compare A/B against one shared requirements baseline, including an intentionally incompatible case and a valid case. Do not present hypothetical numeric improvements as measured facts.
- Activate a compatible selection, generate schematics from XML and declared dependencies, and compare native objects, connectivity and geometry.
- Edit an inactive alternative and custom view; prove no changes to the active schematic/PCB/configuration. Switch view repeatedly with no electrical/file churn.
- Add instructions to a block, an interface, a region and the entire design through rendered UI; prove exact consumption by the actual Codex session, candidate generation and cancellation recovery.
- Rewire/move an active native object; preserve intent, update exact realization, record violations and synchronize undo/redo. Distinguish layout-only revisions from electrical changes.
- Restore historical content as a new revision, fork it, remove/recover an alternative and remove a view; preserve all still-referenced objects and unaffected designs.
- Inject stale requests, changed requirements, missing sources, library mismatch, net split/merge, deleted target, concurrent writer, dirty close, I/O failure, cancellation, native crash and service restart. Require explicit failure and recoverability without partial success claims.

UI acceptance additionally covers every enabled command/menu/shortcut, keyboard focus boundaries, rapid interactions, selection consistency, locked items, small windows, long labels, text scaling and both themes. Formal schematic readability checks still cover page fit, clipped images/labels, font sizes, crossings and object visibility. A beautiful block mockup is not schematic-readability evidence.

## 11. Existing completion work and boundary of this plan

Main outcome: `pa29e2edfadaa6964`. Reuse existing basic editor `p60bc4181a9aa1b40`, automatic structural synchronization `p4a84122a7cc39b0f`, exact native realization navigation `p1269c91a45922914`, ports/links/hierarchy `pe9e76b5403614990`, native annotations `p47c5ac4e8184b9a4`, full schematic XML `p168f8654d86c5a1a` and full sync `p7829ac40bf79acb9`. Their implementation/evidence status remains in the Coordinator, not in this file.

Concrete implementation outcomes recorded under the main outcome: model/history/view persistence `p353f93bbed7b2df6`; real scoped Codex refinement `pa48933d0fe0a5c2f`; native alternative/history/view interaction `pc12a7fddf47cab23`; safe configuration activation `pe84454b4680082e1`. These links are scope references, not a parallel status ledger.

Existing native edit/save/property tests are reusable foundation evidence, not proof of AI-first alternatives or full product readiness. The feature branch has useful work and unresolved rendered findings; neither erase it nor present it as a finished solution. This plan does not close any of those outcomes.

Next design decision: select or refine one of the three primary-workflow concepts. Then complete the selected journey/state/component specification before its implementation. The exact alternative/revision/view semantics above are agreed; the new layout is not yet selected.

## 12. Initial visual concepts and selection state

Generated with the built-in image tool using the approved native-canvas mock and the user's rejected parameter-form screenshot as references. These are design-only artifacts. Display order in the conversation, not prompt order, defines the selectable numbers:

| Displayed choice | Saved concept | Primary layout emphasis |
| --- | --- | --- |
| 1 | [Contextual intent](structural-refinement-concepts/01-contextual-intent.png) | Persistent contextual instructions beside the selected canvas object. |
| 2 | [Alternative review](structural-refinement-concepts/02-alternative-review.png) | Two solutions compared directly, with a shared requirement and scoped instruction area. |
| 3 | [Canvas refinement](structural-refinement-concepts/03-canvas-refinement.png) | Canvas-anchored instruction editor and a separate revision-history strip. |

Selection: all three rejected by the user. No outstanding selection request applies to this image set. Both light and dark themes remain required for the revised direction; the different theme in concept 3 was not a different platform requirement.

Visual review limits and required fixes: concept 1 draws a misleading CPU-to-supply power arrow and does not consistently distinguish expanded implementation from Overview; concept 2 duplicates some requirement/history copy; concept 3 visually joins distinct proposed rail outputs and mislabels a CPU/Memory relationship. These generated topology errors are not engineering instructions and must not be copied into code or model fixtures. Use actual typed model data for subsequent detailed/native designs; represent multiple rails as separate connections or an explicitly abstract interface bundle. Reduce inherited toolbar duplication and replace internal concept headings with ordinary product wording. Treat these images as layout choices, not finalized UI or validated circuit designs.
