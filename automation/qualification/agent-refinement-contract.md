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

A proposal that refines an existing block, connection or member, and an implementation made with New or Duplicate,
continue the field history of the implementation they were made from instead of starting a new one. The new implementation's first
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
`contextVersion` belong to that implementation, and `ownerName` is the name the block, connection or member had in
that revision. The editor's history list names that implementation for a row saved in an earlier one, in the
implementation selector's form ("Initial approach · v1 · Fixture user"), and the selected row's heading repeats its
version and author, which a narrow list can cut off. When the heading is too narrow for all of it, only the earlier
implementation's name is shortened ("Initial ap… · v2 · Fixture user — Selected text"); the version and author stay
whole, and the whole heading is its tooltip.

Evidence: `NativeRecursiveEditorJourney` (production MCP server and rendered editor: an unselected root proposal,
a chosen supply proposal with its refined supply connection and the member it refines inside that connection, the
root after the choice, an agent's duplicate; then, in the rendered editor, the history list as shown and restoring
the pre-proposal text of the chosen supply and of its refined supply connection, each saved and read back). The
member is made over MCP before the proposal: a supply agent groups the PSU level's Supply signals into a "Converted
rails" group with `kicad_diagram_connection_members_refine`, citing a datasheet by page, table and part variant. After
the choice the member's history is read over MCP (the proposal's rewrite, then the agent's text with that source), and
its pre-proposal text is restored once through the diagram companion (the editor lists members only in the Signals
row) and read back first in that history. Every MCP entry is compared field by field, owner name and each source
statement included. `NativeFieldHistoryTests` (the rendered history dialog, labelled by the editor's own row builder,
restores a text saved before an implementation switch, and at the compact size keeps the heading's version and author
whole) and the isolated rules in `RecursiveBlockProposalTests`, `RecursiveImplementationTests`,
`DiagramFieldHistoryQueryTests`, `DiagramConnectionArchiveTests`, `DiagramRequirementHistoryTests` and
`DiagramRequirementHistoryFileTests`.

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
`stale_connection_revision`, and `stale_connection_parent` for a group containing the member
that has a newer saved revision), an identity the level does not have
(`connection_edit_target_missing`), an edit that does not say exactly one thing
(`ambiguous_connection_edit`), a reused identity (`identity_reused`) and an invalid group or pair
(`invalid_connection_refinement`). An edit that changes nothing writes nothing.

The connection's new revision takes the call's `operationId` as its identity, and every other
identity the save creates (the groups containing it, the level and each level above it, new
members) is derived from it. An agent whose call was cut off reads the connection: a revision
with the operation's identity means the operation landed. Repeating a landed operation never
saves a second copy: on the file it produced it finds nothing to change and writes nothing. The native editor
shows the result after it reloads: bound or unresolved ends with what they say in the connection's
Endpoints row, refined members in its Signals row. Native schematic and PCB files are not touched.

Evidence: `RecursiveEditorFileCommandTests.AgentConnectionEditsBindEndsThroughTheLevelsAndRefineMembersInOneGuardedSave`
(helper process, PSU/CPU fixture), the connection-details and PSU/CPU canvas steps of
`NativeRecursiveEditorJourney` (production MCP server, rendered editor; the unbind's revision is its
operation's identity and repeating it writes nothing; the grouped Supply revision before the PSU
proposal likewise) and
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
named, `unknown_refinement_input` for an input the diagram does not have, and `invalid_agent_context_scope` for a path
that names a revision the diagram does not have, does not start at the root, is not pinned revision by revision, or lies
outside the named input's revisions.

**Stale proposals.** A proposal's base is the target revision its input captured. `kicad_diagram_proposal_compare`
compares a published proposal, or a request this server retained after a refused publication, with today's saved
design. It reports the target's current path, three lists and three flags:

- `proposalChanges`: what the proposal changes from the base to its candidate.
- `currentChanges` and `changedOnBothSides`: what changed from the base to today's target, and the elements both sides
  changed.
- `candidateAdopted`: the proposal was chosen, so today's target is its candidate or descends from it: a later saved
  revision of the candidate's implementation, or an implementation made from one of those (a duplicate, or a later
  proposal that refined it). The proposal's own changes are then part of today's design: `currentChanges` lists only
  what changed after the candidate (nothing right after the choice), and `changedOnBothSides` is empty.
  `candidateSelected` says today's target is exactly the candidate.
- `stale`: the proposal is not adopted and today's target is no longer the base (or has left the design), so choosing it
  is refused rather than applied over the newer work. An adopted proposal is not stale; choosing it again is refused too.

Each change names its level (`levelPath`, block ids from the target down, including each changed child's own level),
the connection and members that contain it (`connectionPath`), its category and kind (added, removed, changed,
reordered), the element's id and name, the requirement field or `aspect` (a definition facet, a connection's kind,
domain, direction or ends, or a list's order), and, for a child block or connection, its revision before and after. A
target that left today's design is reported as one removed block whose `levelPath` is today's root-to-block path of the
deepest block of its base path that today's design still has: its old parent while that remains, otherwise the nearest
remaining ancestor, at least the root.

Nothing is merged or chosen silently:

- publishing reports the comparison with the saved candidate;
- a proposal sent with an outdated token is refused (`block_proposal_source_changed`) with the comparison against
  today's file, and nothing is written;
- choosing is refused, with the same comparison, when the target changed after the base, left the design or already is
  the adopted candidate (`proposal_target_changed`), for an outdated token (`block_proposal_source_changed`), an outdated
  expected root or a path starting at another root revision (`stale_root_revision`), a path through a containing block
  revision the design no longer pins although the target is unchanged (`stale_block_revision`), and a containing
  implementation with a newer saved revision (`stale_parent_revision`); the comparison's `currentPath` is today's path to
  the target. The refusal's `details` list one entry per change (`current_change`, `proposal_change`,
  `changed_on_both_sides`) naming the level and element. A path is outdated only when it runs through saved revisions of
  exactly the blocks on today's path to the target, each revision containing the next block. Any other path (a block
  skipped or added, a start at another block, a revision the diagram does not have, or one that never contained the next
  block) is not outdated but invalid: it is refused with `invalid_recursive_block_graph` and no comparison;
- a comparison that cannot be made never replaces a refusal's code or fails a saved publication: it is reported as
  `comparisonUnavailable` with its code. A request that was never published and no longer prepares against today's file
  reports the code its publication would be refused with (`invalid_block_proposal` for a malformed proposal), and
  comparing such a retained request directly is refused with that code. A published proposal was validated when it was
  saved, so a comparison of it that fails without a code is the comparer's own failure (`proposal_comparison_failed`),
  never blamed on the agent's proposal.

**Cancellation and reattachment.** Operations are identified by the agent. Repeating the same proposal and operation
identity after a cancelled or uncertain call returns the recorded candidate (`added=false`) with its publication
receipt; repeating a completed choice returns its recorded outcome (`recorded=true`, the source token and root it
produced, and whether the file still has them). Neither creates a second candidate or root revision. An operation
interrupted between its recorded phases is completed from its receipt with `kicad_diagram_proposal_publication_resume`.
A server that restarts reattaches the saved instance with `kicad_instance_reattach` and finds the same receipts in its
state directory. An open native editor keeps its unsaved draft throughout; it is neither reloaded nor saved.

Evidence: `NativeRecursiveEditorJourney` over the production MCP server and the rendered editor. It checks the input's
context (prompt, attachment, fields), a level below the input inside its revisions (the input without its focus, not on
today's design) and a commented level's context (element and free-space comments, sketch points), all unchanged by
fingerprint after many later edits, and the refusals, an unknown revision and an unknown input included. A proposal
built on the original root is compared with today's root: its own changes exactly, and today's changes level by level
against the saved history comparison (`kicad_diagram_history_compare` for the root, the model for each changed child).
The same comparison comes back when a second stale proposal's publication and the stale proposal's choice are refused.
An outdated-token refusal and an outdated-path refusal (through an older root revision) report no change on today's
side, and the latter today's path. A third request sent with the outdated token no longer prepares against today's file
(its implementation name was taken meanwhile): its refusal keeps `block_proposal_source_changed`, carries no comparison
and reports `comparisonUnavailable` `invalid_block_proposal`, nothing is written, and comparing the retained request is
refused with `invalid_block_proposal`; a request whose block list holds an empty entry is refused with
`invalid_block_proposal` before anything is read. After the choice, the already-chosen refusal reports the adopted candidate with
nothing changed on today's side or on both; after the chosen supply is edited in the editor, the comparison is still
adopted and today's side is exactly that edit (against the saved history comparison). Finally, with an unsaved edit
open in the editor, an agent's publication and its choice are each abandoned by the agent in flight: the agent cancels
as soon as the operation's first recorded phase appears in the server's state directory, so the server has started the
operation and has not answered (the step fails unless the call was cancelled on the agent's side). Each time a new
server reattaches the instance, reads the same context, inspects the receipt (resuming it if it was interrupted) and
repeats the operation: one input, one candidate and one new root revision exist, and the draft is unchanged. Which
server-side branch ran after each cancelled call, and only then, is recorded as `serverBranch` in the journey's
`agent-reattachment` evidence. The interrupted-phase branches (process death at each recorded phase, then resumption)
are also proved by `BlockProposalInterruptionTests` and `BlockProposalSelectionInterruptionTests`.
`RecursiveBlockProposalTests.StaleProposalComparisonNamesEachChangedElementOnEachSide` covers what the journey's fixture
cannot reach: a nested level changed on both sides, the parts of a refined connection and of its member, an adopted
proposal edited later and one refined by a later chosen proposal, an outdated path through a containing block
(`stale_block_revision`), paths that are invalid rather than outdated (one skipping a block, one starting below the
root, one through a revision the diagram does not have) and a nested target removed from the design at each depth.
`RecursiveBlockProposalTests.ComparisonThatCannotBeMadeNamesItsCauseWithoutBlamingAPublishedProposal` covers the one
code no real request reaches: a published proposal whose comparison fails without a code reports
`proposal_comparison_failed`.
