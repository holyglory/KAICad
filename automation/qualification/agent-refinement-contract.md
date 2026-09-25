# Agent-neutral refinement contract

Implementation preparation for the existing recursive-diagram workflow. This is
a contract specification, not evidence that the client journey is implemented.
The authoritative remaining outcome is DevCoordinator `pa48933d0fe0a5c2f`.

## User journey

The user works in their existing agent console, supplies a prompt and optional
files/graphics, and refers to a selected diagram or element. The agent receives
the exact diagram context and its comments, then produces a new block revision.
It can rewrite General, Schematic and Routing requirements while retaining the
original input and previous text. The same workflow applies at the root, inside
a subsystem, or at a component. No per-unit Refine button or automatic submission
from native Save/Decline is introduced.

## Ownership and identities

- Retain a typed immutable refinement input in the existing diagram document,
  not another authoritative design model or a provider-specific conversation.
  Identify its input ID, author/time, original prompt, diagram document, exact
  source hash, root-to-block revision path and attachment references.
- Existing immutable graph revisions supply the historical diagram, requirements,
  connections and original annotations. Validate the complete pinned path; never
  replace it with current implementation heads or duplicate the entire history
  for every prompt. A later edit does not change the captured context.
- Preserve attachments as declared repository assets with exact content hashes,
  byte counts, original names and media types. Keep original source revision/page/
  table/variant metadata separately from an agent's interpretation. A reference
  to a mutable file alone is not preservation of the original graphic or document.
- New block/requirement origins reference the retained input ID. Explicit component
  realizations stay independent of model/package strings and physical allocation.

## Service behavior

Use the existing compiled .NET model/file publisher and typed MCP/NNG contracts.
The service does not start an AI run, choose a provider or implement a chat client.
Clients request context and submit typed candidates through the same operations.

Context includes the requested local diagram, direct children and boundary
interfaces, the three requirement fields, comments and original markup, exact
component/connection references, and provenance. Additional levels and native
views remain separately queryable by exact revision. Images and structured views
must identify the same native observation checkpoint.

Prepare preserved assets before publishing an input that refers to them. Use
repository-contained paths and the existing source-as-data controls. Read failures,
changed attachments and unsupported media are explicit; no invented extracted text,
implicit execution, or replacement with a different source revision.

A candidate carries its input ID, observed source token and expected root/path,
typed changes, retained unresolved issues and agent provenance. Validate its full
scope: changed children/connections must belong to that block, new identities must
be distinct, historical records must be retained, and untouched siblings must not
change. Use the existing immutable revisions and guarded publication.

Publishing a candidate and choosing it for the containing design are distinct.
An agent authorized to apply a change can perform the existing explicit selection
operation; this separation must not create a new mandatory human approval ritual.
Native Save/Decline still means saving or cancelling the current editor draft.

Stale or invalid candidates remain inspectable and cannot overwrite newer work.
Cancellation and process reattachment preserve inputs, candidates and dirty editor
sessions. Reuse the established operation journal so retrying an uncertain result
returns its recorded outcome rather than creating another revision. A source hash
is not by itself an operation receipt.

## Acceptance fixture

Use a root with PSU and CPU peers, a connected diagram inside each, and a component
whose units appear on different schematic sheets. Start with unknown electrical
values, an exact package preference, an element comment, a free-space sketch and
an original image/document attachment. An agent rewrites current requirement text
and proposes a more detailed implementation without discarding the original input.

Check exact XML/shared-message preservation, historical context after newer edits,
attachment replacement/missing-file recovery, cancellation, stale candidates,
duplicate operation IDs, independent sibling edits, and candidate preview versus
selection. Verify actual context consumption and candidate submission through Codex
and a second available compatible agent. An installed CLI or SDK protocol test alone
does not qualify a particular agent, its image handling, or native Desktop integration.

The Mac/Windows build hold remains in force until the complete agreed XML editing
workflow is implemented. This contract does not enable a global console control or
claim automatic structural-to-schematic generation or electrical verification.

## Next implementation boundary: complete proposals

The original-input archive and definition/component setters are not a substitute
for complete diagram proposals. The remaining concrete outcome is
`p1231ce557d949a70`. Its shared contract must carry the original input identity,
captured source token and target path, the proposed block selection, and the typed
new block/connection revisions with their requirement histories and unresolved issues.
All existing histories and input records remain immutable. A proposal can add an
internal decomposition while its parent still presents the unit's boundary interfaces.

Prepare and validate the whole proposed closure before publishing any part of it:
every newly referenced child/member exists; exact endpoint and ownership constraints
hold; original statements and attachments remain reachable; and previously selected
siblings and unrelated implementations remain unchanged. A head lookup is not a
historical reference. An invalid final connection cannot leave only the new blocks
published. Native electrical generation remains a separately verified operation.

Publishing an unselected candidate may append its history, but must not activate
it in the containing root or replace a user's native editing draft. Choosing it
uses explicit root/path guards and updates the necessary ancestors together.
Reuse the same input and operation identity on retries; do not manufacture a second
candidate to work around an uncertain response. Retain a stale proposal for comparison
with newer changes rather than rewriting its original context or guessing a merge.

Acceptance must include an abstract power unit evolving from one regulator into
multiple converters plus telemetry, and an abstract data connection evolving into
a signal bundle with partly resolved endpoints. These are test-only synthetic designs,
not claimed engineering recommendations. Verify rewritten current General/Schematic/
Routing text with original history, exact unrelated-sibling preservation, preview
without activation, cancellation, invalid member rejection and stale-result recovery.

## Field history across implementations and proposals (ledger p390b40bed99e0ab2)

A proposal that refines an existing block or connection, and a duplicated implementation, continue the field
history of the implementation they were made from instead of starting a new one. The new implementation's first
requirement revision names, as its parent, the exact requirement revision it was derived from; the proposal's
rewrite is the next revision. Reading General, Schematic or Routing history (`kicad_diagram_field_history`, the
editor's History action) therefore lists the rewrite and then every earlier text, each with its own author or
agent, sources, linked input and proposal identities, and the version it has in its own implementation's diagram
history. Choosing the proposal or switching implementations keeps that history, and restoring an earlier text,
including one written before the switch, saves a new revision that names the revision the text came from. The
diagram file checks every link: it must name a saved revision of another implementation of the same block or
connection (for a block, the exact revision the implementation was made from) and cannot be circular, and the
continuing implementation's first revision must be an unchanged copy of the text it continues, with no restoration.
A separate requirement-history file cannot hold a continued history and is refused. Implementations saved before
this rule keep the separate history they were saved with; no earlier link is invented.

Which implementation an entry came from is always shown. `kicad_diagram_field_history` gives every entry the
implementation it was saved in (`contextStateId`, `contextImplementation`); its `contextRevisionId` and
`contextVersion` belong to that implementation. The editor's history list names that implementation for a row saved
in an earlier one, in the implementation selector's form ("Initial approach · v1 · Fixture user"), and the selected
row's heading repeats its author, which a narrow list can cut off.

Evidence: `NativeRecursiveEditorJourney` (production MCP server and rendered editor: an unselected root proposal,
a chosen supply proposal and its refined supply connection, the root after the choice, an agent's duplicate; then,
in the rendered editor, the history list as shown and restoring the pre-proposal text of the chosen supply and of
its refined supply connection, each saved and read back), `NativeFieldHistoryTests` (the rendered history dialog,
labelled by the editor's own row builder, restores a text saved before an implementation switch), and the isolated
rules in `RecursiveBlockProposalTests`, `RecursiveImplementationTests`, `DiagramFieldHistoryQueryTests` (a member
restoring an earlier implementation's text, saved and read back through XML and the codec),
`DiagramConnectionArchiveTests`, `DiagramRequirementHistoryTests` and `DiagramRequirementHistoryFileTests`. A
member is refined through the same connection code; no rendered or MCP journey yet refines a member, because no
journey level has a group whose member a proposal refines.

## Connection ends and members (ledger pf92d0ecdec8805b4)

Two agent tools change one exact connection or member of a saved diagram level without a
whole-block proposal. Both name the observed file token, the selected root, the root-to-level
block path and the root-to-member connection path by exact revision, and both save through the
same guarded connection save the diagram companion runs (`RFA_SAVE_CONNECTION`).

- `kicad_diagram_connection_endpoint_set` binds one end to a port of one of the level's blocks,
  or to a port on the level's own boundary, or leaves it explicitly Unresolved on its block with
  what is still open. An end that states pins, candidates or a compatibility selector keeps them
  while it stays on its block. For a boundary port the result reports how that port maps through
  the levels: the parent level's connections that use it and the level's stated realization.
- `kicad_diagram_connection_members_refine` restructures a connection's members into groups,
  differential pairs and new signals. Every current member keeps its exact revision, requirement
  history and notes, and interface realizations naming it stay exact. A refinement never drops a
  member. New members start their own three requirement fields; the agent's sources and original
  input are recorded in their origin.

Refusals write nothing: a changed file or stale target (`recursive_block_file_changed`,
`stale_root_revision`, `stale_block_revision`, `stale_parent_revision`,
`stale_connection_revision`), an identity the level does not have
(`connection_edit_target_missing`), an edit that does not say exactly one thing
(`ambiguous_connection_edit`), a reused identity (`identity_reused`) and an invalid group or pair
(`invalid_connection_refinement`). An edit that changes nothing writes nothing. The native editor
shows the result after it reloads: bound or unresolved ends with what they say in the connection's
Endpoints row, refined members in its Signals row. Native schematic and PCB files are not touched.

Evidence: `RecursiveEditorFileCommandTests.AgentConnectionEditsBindEndsThroughTheLevelsAndRefineMembersInOneGuardedSave`
(helper process, PSU/CPU fixture), the connection-details and PSU/CPU canvas steps of
`NativeRecursiveEditorJourney` (production MCP server, rendered editor) and
`RecursiveBlockLocalDiagramTests.AgentConnectionEditsNameExactlyOneCurrentTarget` (the refusal
matrix of the isolated rules).

## Agent context and stale proposals (ledger pa48933d0fe0a5c2f)

Any agent, whatever product or model runs it, gets the same context and compares its result the same way. These are
contract parts only: the real journey with two agent clients is Phase 3 work.

**Context.** `kicad_diagram_agent_context` returns one saved diagram level as plain JSON. The agent names the level by
an original input (`inputId`: the level the input captured, or a deeper level inside that input's revisions given by
`blockPath`) or by `blockPath` alone: the exact root-to-level path, starting at a revision of the root block, each
block pinned by the revision before it. Older revisions are allowed. The context holds:

- the path, and the level's block and direct children, each by exact block, implementation and revision, with its
  name, General/Schematic/Routing text and the requirement revision that text belongs to, its boundary ports, its
  definition, component and physical choices, and how many children and connections lie below it;
- every connection and member of the level by exact revision, with its kind, domain, direction, ends, members,
  realization and its own three fields;
- every comment of the level, marked `Element` (on a block or connection) or `FreeSpace` (on the canvas, with any
  original sketch strokes), and the level's interface realizations and saved layout;
- with an input: the original prompt exactly as given, who recorded it, the file token it was captured at, its scope
  and focus connections, and its attachment references (preserved asset path, SHA-256, byte count, media type, source).

`contextSha256` fingerprints exactly these contents. Because they come from immutable revisions, the same input or
path gives the same context and fingerprint after later edits, renames and a server restart. Outside the context the
result reports today's file token and selected root, whether the level is on today's design (`current`, `currentPath`),
today's name of each implementation the context names (`implementations`, which a rename changes) and the present
integrity of each attachment. Deeper levels are read with their own context. The tool reads only.
It is refused with `recursive_block_file_changed` for an outdated token, `ambiguous_agent_context` when no level is
named, and `invalid_agent_context_scope` for a path that does not start at the root, is not pinned revision by revision,
or lies outside the named input's revisions.

**Stale proposals.** A proposal's base is the target revision its input captured. `kicad_diagram_proposal_compare`
compares a published proposal, or a request this server retained after a refused publication, with today's saved
design. It reports `stale` (today's target is no longer the base), `candidateSelected` (today's target already is the
candidate), the target's current path, and three lists: `currentChanges` (base to today), `proposalChanges` (base to
the candidate) and `changedOnBothSides`. Each change names its level (`levelPath`, block ids from the target down,
including each changed child's own level), the connection and members that contain it (`connectionPath`), its
category and kind (added, removed, changed, reordered), the element's id and name, the requirement field or `aspect`
(a definition facet, a connection's kind, domain, direction or ends, or a list's order), and, for a child block or
connection, its revision before and after. A target that left today's design is reported as removed.

Nothing is merged or chosen silently:

- publishing reports the comparison with the saved candidate;
- a proposal sent with an outdated token is refused (`block_proposal_source_changed`) with the comparison against
  today's file, and nothing is written;
- choosing a proposal whose target moved on is refused (`proposal_target_changed`, or a stale token or path) with the
  same comparison; the refusal's `details` list one entry per change (`current_change`, `proposal_change`,
  `changed_on_both_sides`) naming the level and element.

**Cancellation and reattachment.** Operations are identified by the agent. Repeating the same proposal and operation
identity after a cancelled or uncertain call returns the recorded candidate (`added=false`) with its publication
receipt; repeating a completed choice returns its recorded outcome (`recorded=true`, the source token and root it
produced, and whether the file still has them). Neither creates a second candidate or root revision. An operation
interrupted between its recorded phases is completed from its receipt with `kicad_diagram_proposal_publication_resume`.
A server that restarts reattaches the saved instance with `kicad_instance_reattach` and finds the same receipts in its
state directory. An open native editor keeps its unsaved draft throughout; it is neither reloaded nor saved.

Evidence: `NativeRecursiveEditorJourney` over the production MCP server and the rendered editor. It checks the input's
context (prompt, attachment, fields) and a commented level's context (element and free-space comments, sketch points),
both unchanged by fingerprint after many later edits, and the refusals. A proposal built on the original root is
compared with today's root: its own changes exactly, and today's changes level by level against the saved history
comparison (`kicad_diagram_history_compare` for the root, the model for each changed child). The same comparison comes
back when a second stale proposal's publication and the stale proposal's choice are refused, and an outdated-token and
an already-chosen refusal report no change on today's side and the chosen candidate. Finally, with an unsaved edit
open in the editor, an agent's publication and its choice are each cancelled in flight. Each time a new server
reattaches the instance, reads the same context, inspects (and if needed resumes) the receipt and repeats the
operation: one input, one candidate and one new root revision exist, and the draft is unchanged.
`RecursiveBlockProposalTests.StaleProposalComparisonNamesEachChangedElementOnEachSide` covers what the journey's fixture
cannot reach: a nested level changed on both sides, the parts of a refined connection and of its member, and a target
removed from the design.
