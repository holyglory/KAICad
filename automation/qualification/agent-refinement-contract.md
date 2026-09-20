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
