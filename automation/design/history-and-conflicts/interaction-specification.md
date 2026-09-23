# Diagram history, field history and conflicting saves

Design continuation of the approved [diagram-level walkthrough](../recursive-structural-refinement-plan.md#13-diagram-level-walkthrough--approved-direction-with-corrected-edit-controls).

The user approved these design states and authorized implementation with “Fantastic! Go ahead!” (decision n045fff50030deea3 / kicad-history-conflict-design-approved). They are implementation contracts, not evidence of working UI. The System/PSU/CPU layout and Save/Decline versus global agent-console separation remain unchanged. Linux implementation and tests may proceed; the Mac/Windows build hold remains in force.

## Visual references

1. [Diagram history](01-diagram-history.png): the saved PSU diagram remains v3 while the user selects v2 in its history panel.
2. [Routing field history](02-field-history.png): one-action access from that field's History control; old and saved text are compared with exact scope.
3. [Conflicting save](03-save-conflict.png): the draft based on v3 and the new saved v4 remain available; no default winner is chosen.

These are three states of one experience, not alternative layouts. The light-theme frames extend the approved design. Dark-theme, compact-window and complete native interaction evidence remain required before readiness. Generated background drawings are illustrative; the actual native diagram must remain model-bound and must not be reconstructed from generated pixels.

## 1. Shared draft and revision semantics

Keep five distinct concepts internally: selected design state, saved revision, currently inspected revision, editable draft and current external saved revision. The UI exposes only the distinctions relevant to the action. A highlighted history row is not a selected implementation and is not the user's unsaved work.

- Browsing, previewing and closing history never creates a model revision or changes native design files.
- Save persists validated draft content, with the exact base/current checks and existing synchronization rules. It does not request AI work.
- Decline discards only this unsaved draft. If another actor advanced the saved design, Decline does not write the old baseline over that newer revision. Refresh the saved view from its authoritative revision while preserving unrelated drafts.
- Source history and original prompts remain immutable. Restoring any content creates a new draft/revision, never erases later history.
- An unchanged Save is a no-op and must not create file churn or a meaningless revision.
- All these actions have explicit diagram/element/field identities behind human-readable paths. Same-named units at different levels must not be confused.

## 2. Whole-diagram history

Entry: History beside the current diagram revision. The right-hand Properties panel temporarily becomes the scoped diagram-history panel; the current canvas remains visible.

Each revision row identifies its revision, actor and concise change description. Use actual timestamps, author/agent identity and source links when available; the mock's v1/v2/v3 content is fixture data. The saved version carries a distinct marker. Row selection only displays metadata and differences.

| Control | Result | What must remain unchanged |
| --- | --- | --- |
| Select revision | Inspect its metadata and a clearly labelled comparison against the saved revision. | Current canvas, selected implementation, drafts, native files. |
| Preview v2 | Open a read-only rendering of that exact revision with an unmistakable historical-view indicator and Return to current action. | Saved selection and existing editing draft. |
| Restore v2 as draft | Prepare historical content as a new draft in this diagram's scope, including exact child revision references. Return to editing with a change summary. | Saved v3 and all history until Save; unrelated siblings and scopes. |
| Source instruction | Open the actual retained prompt/comments/attachments or source references that produced the revision. | Model and draft. A missing reference is not an invented link. |
| Back / Close panel | Restore Properties and previous selection/focus. | Any draft or saved state. |

The frame deliberately retains v3 on the canvas while v2 is selected in the list. Choosing Preview is the explicit transition to a historical canvas. Do not restore automatically on row selection or double-click.

Restoring a whole diagram is distinct from selecting a different implementation alternative. It reintroduces old content as a draft based on the present saved revision. The previous source revision is retained as provenance. Saving creates a new successor after compatibility and synchronization checks; it never moves the saved pointer backward or pretends to undo unrelated edits.

If an editing draft already exists, do not replace it silently. Use the shared dirty-draft resolution: Save it, explicitly Decline it, or Cancel the restore. Preserve a recovery copy until the replacement operation has completed. Unsupported schema, missing child revision/library/source or unavailable native preview produces an honest unavailable state with no partial restoration.

## 3. One field's history

Entry: the History action beside General, Schematic or Routing requirements. Open the shared field-history dialog labelled with the full owner and category. Selection and comparison are read-only.

The example shows Routing requirements for PSU: selected v2 text `Keep power paths short.` versus saved v3 text `Keep high-current paths away from sensing.` These are deliberately different statements. The UI does not imply that old text still satisfies current design requirements.

| Control | Result |
| --- | --- |
| Select history row | Show that exact field value, actor and provenance; update the labelled comparison. |
| View source instruction | Inspect the available original prompt/comments/source revision without executing it. |
| Use v2 text in draft | Copy only this field into the current editing draft, close the dialog and focus the changed field. Preserve every other draft field and diagram edit. |
| Close / Escape / window close | Leave history without changing the draft. Return focus to the originating History action. |

Using old text does not save immediately. Save/Decline in the editor remain the commit/cancel actions. Restoring the already-current value does not introduce a change. Removal or weakening of a strict statement is visible in the content/constraint diff; existing validation must not silently treat that change as satisfying the former requirement.

History is per field and exact owner, while the containing diagram revision pins it. A field view can omit revisions where that field did not change without hiding their existence from full diagram history. Do not fabricate an edit record for every unrelated revision. Support long Unicode text, graphics/source references and missing provenance without hiding the original words.

### Longer histories

Keep the approved list-and-comparison arrangement. When there are more than 200 field changes, show the loaded/total count and a compact **Load older** action beneath the revision list. Loading disables only that action; the current selection, comparison, Close and draft-restore actions remain available. New rows append without changing the inspected revision. The count remains once all rows are available; the loading action disappears.

A failed page adds a concise retry message under the list, without replacing loaded rows or the editing draft. Every page is bound to the same owner, implementation, diagram revision and source-file content. If the saved file changes, keep the historical comparison intact and ask the user to close history and reload; never mix histories from different snapshots. Closing cancels delivery of an in-flight response, so it cannot reopen the dialog or update another scope. Choosing an older row still requires the explicit Use text in draft action, followed by the editor's Save to publish.

Verify a history exceeding 200 changes in both themes, exact selection preservation, the oldest text restored with other draft fields unchanged, compact loading/error layout, cancellation, a changed file and recovery. This is the bounded-loading behavior recorded under `p8ebf18838a55934f`; it does not substitute for whole-diagram history or native schematic/PCB activation.

## 4. Conflicting save

Entry: Save discovers that the authoritative design has advanced and this draft cannot be merged safely. Preserve base, draft and latest saved state before presenting the conflict. Do not open a modal for a harmless view change or independent, safely composable fields.

Compare three exact versions: base, the user's draft, and latest saved. The example is a genuine location conflict: the draft asks for converters at the top edge while the latest saved field asks for the bottom edge. Neither one wins by timestamp or agent identity.

The dialog shows the owner/category and baseline revision. For several conflicts, reuse this same surface with an explicit conflict list/count; choices apply to the selected field/object, not to the entire project unless a separate deliberate bulk action is designed and authorized.

| Control/state | Result |
| --- | --- |
| No choice yet | Save resolved version disabled. Retain both versions and the draft. |
| Use my text | Select this draft field for the merged candidate; preserve latest independent changes elsewhere. Show the resulting text. |
| Use saved text | Select the current saved field for the candidate; preserve unrelated draft edits. Show the resulting text. |
| Write merged text | Enable a combined-text editor. Do not treat concatenated contradictory requirements as a valid automatic resolution. |
| Back to editing / Escape / close | Close the conflict dialog and preserve the unresolved draft. Do not publish or discard either side. |
| Save resolved version | Validate the whole candidate, recheck the latest revision and commit one new revision through the normal guarded save path. |

If the saved design advances again while the dialog is open, preserve the user's resolution as draft work and show the new conflict against the new authoritative state. Never reuse an old resolution token against new content. Validation/write failure leaves the candidate editable and recoverable; show the precise affected scope and useful next action.

A conflict choice is not permission to delete historical requirements or override locked/unrelated native objects. Graph changes such as deleted targets, reparented blocks, swapped pins or missing interfaces use structured conflict resolution, not this text field's radio controls. A deleted owner must not be recreated or matched by name merely because old text was chosen.

Native/XML publication remains the established recoverable transaction; the new dialog does not replace its operation journal, stale checks, exact identities or failure handling. The existing general recovery outcome stays open for unproven integration cases.

## 5. State, keyboard and failure coverage

All visible controls need real behavior through the native interface before being enabled in the product. The mockups are not handler or interaction evidence.

- History: empty/single/many revisions, loading/failure, exact source missing, historical preview unavailable, interrupted restore, dirty existing draft, unchanged restore and repeated restore.
- Field history: all three categories, root/child/connection/member ownership, very long text, no provenance, renamed/deleted targets, concurrent edits and selective restore without losing other edits.
- Save conflicts: true positive overlapping changes; false-positive guards for unchanged fields, independent edits and view-only changes; multiple actors and a second update during resolution.
- Persistence: Save/Decline/Cancel distinctions, failed write/retry, crash and reattach, no duplicate operation and no discarded competing version.
- Navigation/focus: return to the originating field/history action; editor shortcuts must not escape to the project manager; modal Escape returns without discarding the draft.
- Layout: readable disabled controls, no color-only difference encoding, long source labels, compact window and text scaling, light/dark native themes, fixed accessible dialog actions when content scrolls.
- Side effects: zero agent invocations from browse/preview/restore/Save/Decline; only explicit action in the agent console initiates refinement.

## 6. Implementation handoff

Extend the existing immutable field/block history and recoverable save contracts before enabling these controls. Do not introduce separate per-dialog persistence. Native C++ owns interaction/focus/rendering; the compiled .NET model owns version identity, comparison and save/conflict semantics; shared typed contracts carry explicit targets and revision-bound results.

Retain an inspect-only historical snapshot distinct from the active native instance. History queries must be bounded/lazy for large projects, but one-action field history should immediately show available current/previous text and a truthful loading/error state when additional records are needed.

Acceptance fixture: saved PSU v3, earlier v2, a current draft and independently saved v4. Prove read-only browsing, selective field restore, whole-diagram restore-as-draft, Save/Decline, a true top/bottom conflict and a non-conflicting edit. Repeat at root, a child block and a connection while preserving exact historical root reconstruction.

Existing outcomes: native UI `pc12a7fddf47cab23`, three-field history `p390b40bed99e0ab2`, recursive model `p353f93bbed7b2df6` and recoverable XML publication `p7e712f1bb764e327`. These references do not mark any implementation complete. The proposed Agent console integration remains separate (`p0d93605c3a084996`).
