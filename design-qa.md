# Per-level diagram editor — mockup audit and design QA

final result: blocked

**What this means for the owner.** Claude Opus 5.5 ran the full Product Design audit and paired design QA of the per-level diagram editor against your four selected sketches (A1 toolbar strip, A1 canvas palette, A4 chips and facet review, A3 caption-first connection inspector), the approved System/PSU/CPU walkthrough and the approved history and conflict states, in light and dark and the compact window, from fresh captures. Every problem found in the two earlier design QA rounds is still fixed. The audit found no P0 or P1 problem and nine moderate (P2) ones. Six were in this lane's files and are fixed and re-checked: Comments no longer opens on (and rewrites) a note of the level instead of the selected block's own comment, port names no longer run into blocks in a small window, and the history and conflict dialogs now have the approved filled primary buttons, padded text, readable list rows and marked differences. Three cannot be fixed without a decision from someone else, so the editor still cannot be handed off as finished:

1. **The shared PSU/CPU test design puts its PSU note on top of a block** (the note "Keep sensing away from switching nodes." covers the start of "Telemetry ADC", one of its ports and a wire). The note's position is part of the shared fixture, which the integration owner owns.
2. **A computed wire still passes behind a block** on the PSU level (Rail B looks as if it ends at DC-DC's port). Connection paths are fixed by the diagram contract (rule F4, three straight segments); routing around blocks is the open ledger outcome `p9fd01b0c9678331c`, waiting for the integration owner's contract change.
3. **A connection's domain is never shown on the canvas.** Every approved image draws power connections red, data connections blue and sense connections as grey dashes, but the editor draws every wire grey even after you choose its Domain. The images define only the light-theme red and blue; the dark-theme colours, the colours of the other domains, and how a selected blue data wire stays distinct from the blue selection need your visual decision.

Everything else is either fixed, an accepted difference that you or the integration owner already decided, or P3 polish listed at the end.

## How the audit was run

- **Who ran it.** Claude Opus 5.5 (model `claude-opus-5-5`), working as lane 2B's engineer, executed the installed Product Design contracts directly, because the Codex CLI is unavailable. The contracts read and followed are, under `/home/holyglory/.codex/plugins/cache/openai-curated-remote/product-design/0.1.56/`: `skills/index/SKILL.md` (router), `skills/audit/SKILL.md` with `skills/audit/references/design-audit-framework.md` (combined UX, design and accessibility audit), `skills/design-qa/SKILL.md` with `skills/design-qa/references/qa-rubric.md` (paired comparison and this report), `skills/user-context/SKILL.md` and `references/critical-overrides.md`. The Coordinator's post-implementation mockup gate (`ui-design-gate`) was followed as well.
- **User context.** The user-context preflight (`skills/user-context/scripts/user_context_preflight.py`, `CODEX_HOME=/home/holyglory/.codex`) reported no saved Product Design context (`user-context.md` missing), so only the project's own approved targets and decisions were used.
- **Targets.** The four selected sketches were fetched through the Coordinator (`design_sketch_get` and `design_sketch_image` over `devcoordinator2 mcp`) and their SHA-256 checked against the Coordinator's record. The approved walkthrough and history/conflict mockups were taken from the repository at the audited commit.
- **Captures.** Every capture comes from a governed run of the graph `diagram-requirement-history` on the source being audited, materialized hash-checked with `devcoordinator2 test artifact materialize`. No earlier capture is reused as evidence.
- **Comparison.** Each approved image and the matching capture are placed side by side in one composite at 1:1 (`pairs/`), with focused crops where detail matters (`crops/`). Every capture used was opened and inspected before it was accepted; contact sheets of all of them are in `sheets/`. Colours and insets were measured from the raw pixels (WCAG 2 contrast).
- **Output.** This report (the inline audit report the audit contract defaults to) with step-linked composites. No canvas walkthrough was requested.

## Approved targets (source visual truth)

| Target | What it shows | Identity | Decision |
|---|---|---|---|
| A1 option 1, "Compact command strip" | Select, Add block, Connect, Place port, Delete in the main toolbar; PSU level with Connect active, dashed preview to a port ring, hint pill | Coordinator sketch `sc23e8d0c5adbb401`, 1536 × 1024, SHA-256 `99dcd9c60d36766be0a735e4de90eca078f4dcfacb27bbfa904056c9fd3fe3d0` | keep, owner 2026-09-23 (`n9f7cf92f32090daf`, both A1 options) |
| A1 option 2, "Canvas edge palette" | The same tools plus Undo in a floating palette on the canvas's left edge, hint callout | `s6d7f155fd468901a`, 1536 × 1024, `228b866b188848dc385b1e534e83340d37da4acfa6d9df7efb0ea094c7940421` | keep, owner 2026-09-23 (`n9f7cf92f32090daf`) |
| A4 option 3, "Canvas Chips with Drill-down" | Chips on a selected LDO block, Review facets, the facet overview and the Manufacturer detail in the inspector | `s9be57c366ffe2d65`, 1536 × 1024, `a405914aef7b5b64216e981cbdc43340c3d2241aebdac40dce2e8e8d8dfb5733` | keep, owner 2026-09-23 (`n0b2a908b00e78823`) |
| A3 round 2 option 1, "Quiet growth menu" | A new I2C connection selected: Connection, Caption, + Add detail, + Add requirement, Comments, Decline/Save | `sd44465992aa73168`, 1536 × 1024, `7a6fba3076725bed6e730f936b13e92a50d4b310b5dc4cb2f99a36ffdbdd2982` | keep, owner 2026-09-23 (`nf53af9d74841b7d3`) |
| Walkthrough System | System level, PSU selected, Save/Decline | `automation/design/level-diagram-walkthrough/01-system-v5-edit-controls.png`, `cccf05b01b66a4dc8f7aad7246e42eb31e45d882d42a3ffc5a9e1e77b5403685` | approved walkthrough with Save/Decline (`n68d6149bd8ab9bca`) |
| Walkthrough PSU | PSU level, root selected | `…/02-psu-v3-edit-controls.png`, `f8c9a8a5e17b21904263b6c597524702ab1e5906173e1c444c82d76b63e70518` | `n68d6149bd8ab9bca` |
| Walkthrough CPU | CPU level, a connection selected | `…/03-cpu-v2-edit-controls.png`, `b6bd20db76f8543f821914cab47bc8ad5de8a45104475b38c9f2035b65d62cfb` | `n68d6149bd8ab9bca` |
| Diagram history | PSU — History panel, v2 inspected | `automation/design/history-and-conflicts/01-diagram-history.png`, `a8c0557a4aab7dedd9b9cc36d98dc4546b598a235500c9fde9a0482cbce3feca` | approved, implement (`n045fff50030deea3`) |
| Field history | Routing requirements — History dialog | `…/02-field-history.png`, `aee9aa3489be31f2092d89cc866fccfe0b9dd89197dde8158de8c24a2d8bc294` | `n045fff50030deea3` |
| Save conflict | Resolve changes before saving dialog | `…/03-save-conflict.png`, `432692578af4bc4bf3fec7544c12ce397d21f394beb09859f79b3b934420c2c4` | `n045fff50030deea3` |

The interaction contract for the history and conflict states is `automation/design/history-and-conflicts/interaction-specification.md`. The earlier root `design-qa.md` (the recursive editor's first comparisons, last changed in `433ad8c00e`) is replaced by this report; it stays in the history.

## Source and implementation identities

- **Audited source.** Lane 2B branch `codex/lane-2b`, worktree `/mnt/build-storage/codex/kicad/worktrees/lane-2b.20260923`. Pass 1 audited commit `7e8af4b5c6190685a07dabd78fcfd053393e7d6f` (integration line `09fae3a73e` merged at `4451d5e3f3`); pass 2 audited that commit plus this commit's fixes; the commit that carries this report is validated by the runs its message names.
- **Pass 1 run.** `t20260925T224749Z-389908`, graph `diagram-requirement-history`, release tier, complete, passed, `source_changed` false (22:47:49 to 23:03:07 UTC, 917 s): build, contracts, native protocol, native UI (674.8 s, 6 of 6) and PSU/CPU canvas (113.3 s, 2 of 2). Artifact manifests: native UI `7e992a69217d821487808175564a2712e397b5a550b28fa52e0ac1b8a45b6394`, PSU/CPU canvas `c7b0d80bf2425dc2e4c30a2ebcdc326883f6e58b50eee3c4e678345c490d3913`. Instances: editor light `60373abc-e7f0-4b07-a4bd-bb6a4f00057e` (with `ee51ae35-f005-45b7-b74c-8c21ce75dd36`), dark `49f4251f-d1ba-4ebb-a0ed-6747a426f0cd` (with `c9a43ec1-7c19-4140-b352-3085a096a857`); canvas light `696ae6d9-f143-4e75-b519-e941ce083ebd` and `74ed4ac3-43b1-4f6a-b990-59e8b8bba6dc`, dark `1a534460-f96e-4aac-bda2-434099110710` and `ed313e96-642d-45a9-bb9b-abea0dbb700e`. Evidence `EV1` = `/mnt/build-storage/codex/kicad/evidence/editor-mockup-audit-t20260925T224749Z-389908/`.
- **Pass 2 run.** `t20260925T232648Z-826429`; identities under "Pass 2". Evidence `EV2` = `/mnt/build-storage/codex/kicad/evidence/editor-mockup-audit-t20260925T232648Z-826429/`.
- **Evidence layout** (both folders): `sketches/` (the approved images and the Coordinator sketch records), `materialized/` (the run's hash-checked artifacts as `devcoordinator2 test artifact materialize` wrote them, with its receipts `materialized-*.json`), `captures/` (links into them by theme), `pairs/` (approved image and capture side by side, 1:1, with `index.json`), `crops/` (focused regions, approved above and capture below), `sheets/` (contact sheets of every capture used) and `SHA256SUMS` over every file.

## Comparison conditions

- **Surface.** The native KiCad per-level editor (C++/wxWidgets 3.2.8 on GTK 3, Adwaita and Adwaita:dark), not a browser; CSS viewport and device-scale settings do not apply. Captures are the whole 1600 × 1150 virtual display at density 1 (1 capture pixel = 1 screen pixel); the editor window is at (0, 0).
- **Window sizes.** The default window is 1536 × 1024, the same pixel size as every approved image, so composites put both at 1:1 with no rescaling. The compact steps use the 1100 × 760 window (cropped to it). The field-history and conflict dialogs are captured on their own at their default client sizes (740 × 520 and 730 × 650, and the compact 590 × 440); the approved images show them over the editor, so their composites compare the dialog region.
- **States.** Where the journeys reach the approved image's exact state, the pair says so: A1 connect hint and port target (option 1 and 2), A3 new connection, A4 Manufacturer detail, walkthrough System with PSU selected (pass 2), PSU and CPU levels, the field history's earlier text, and the unresolved conflict. Other states are judged against the approved visual language.
- **Content.** The drawing, choice and connection journeys draw their own "Fixture board" level (PSU, CPU, DC input, Rail feed, Power, Supply input) instead of the sketches' PSU level; the walkthrough comparisons use the shared PSU/CPU acceptance design (`automation/tests/fixtures/psu-cpu/system.blocks.xml`, contract `psu-cpu-fixture-and-ownership.md` §1.5), whose blocks, names and connections differ from the mockups (for example "DC-DC", "LDO", "Telemetry ADC", "Telemetry MCU" and nine connections on the PSU level); the core editor journey's own fixture ("Power stage", "Processor", "Memory") appears in the `recursive-*` and history steps. Names, versions and texts therefore differ on purpose; the comparison judges layout, controls, states, colours, type and behaviour.
- **Capture set-up, not judged.** No window manager runs on the test display, so there is no title bar, no dialog frame or close button, and dialogs open at the toolkit's default position; the pointer and hover tooltips appear where the journey left them; in the compact steps other KiCad windows show outside the editor. KiCad's own toolbar bitmaps (Back, Up, Undo, Redo, Fit, Note) render as "?" in this lane's build (they render in the integration build, see round 2's captures); the drawing tools' own glyphs render.

## Audit scope, user goal and accessibility target

- **Scope.** The per-level diagram editor as a person uses it on one level: drawing blocks, ports and connections with the toolbar strip and the canvas palette (A1), recording component choices on a block and reviewing its facets (A4), growing a connection from its caption into details (A3), moving between the System, PSU and CPU levels (walkthrough), and the field history, whole-diagram history and save-conflict states. Light and dark themes, the default 1536 × 1024 window and the compact 1100 × 760 window.
- **User goal.** A hardware designer sketches a system level by level, lets each block and connection start as a caption and become concrete as decisions are made, and can always see what was decided, by whom, and undo, decline, compare or restore without losing work.
- **Accessibility target.** WCAG 2.2 AA where it applies to a native desktop editor: text 4.5:1, graphics and state indicators 3:1, a visible keyboard path to every action, controls with a role, name and state for assistive technology, no information carried by colour alone, and layouts that keep every control reachable in the compact window. Contrast, roles and names were measured; no screen reader was run (see limits).

## Strengths, UX risks and accessibility risks (summary)

- **Strengths.** One tool state across the strip and the palette; every block and connection starts as its caption and grows only with what is defined; chips never cut to fragments; every connection caption is drawn; the Connect preview goes around the source block; one accent for everything selected or in progress, readable in both themes; Save and Decline behave the same everywhere, and nothing is written until Save; every history and conflict action is explicit and reversible until Save; tools, captions, fields, saves, history and dialogs are reachable from the keyboard, and tools, links, choices and facet rows report their role, name and state to assistive technology.
- **UX risks found.** A comment typed with a block selected could rewrite the level's shared note (M1-1, fixed); a boundary name could hide a block's port in a small window (M1-2, fixed); the history and conflict dialogs lacked a clear primary action and did not show where two versions differ (M1-4, M1-6, fixed); a wire can look as if it ends on the wrong block (M1-8, open); power and data connections look alike (M1-9, open).
- **Accessibility risks found.** Rows cut mid-word hid the author (M1-3, fixed); the conflict marks carry an underline as well as colour, so the difference is not colour-only (M1-6); a note drawn over a block hides part of its caption and a port (M1-7, open). Contrast of every text and state indicator measured by the journeys meets the target in both themes.

## Numbered steps (final captures, pass 2)

Paths are relative to the pass 2 evidence folder, `EV2`. Each composite shows the approved image on the left and the capture on the right at 1:1; each light composite has a dark twin (`__dark__`). "Health" is the state after pass 2.

| # | Step | Composite (light) | What matches | Findings | Health |
|---|---|---|---|---|---|
| 1 | Empty level, Select active (A1) | `pairs/A1-option-1-toolbar-strip__light__drawing-empty.png` | strip tools after the approved commands, one active tool in strip and palette, caption-only inspector | — (sketch shows a drawn level; state difference) | good |
| 2 | Add block: caption typed in place | `…__drawing-block-caption.png` | Add block active in both entry points, dashed accent outline, focus ring, status "Type a name and press Enter, or press Escape to cancel." | P3 (R2 P3-2 frame) | good |
| 3 | Connect started from the PSU (the sketch's state) | `…__drawing-connect-hint.png`, `pairs/A1-option-2-canvas-palette__light__drawing-connect-hint.png` | Connect active, source selected, dashed accent preview, hint pill "Click a port to finish connection" | — | good |
| 4 | Connect aiming at a port | `…__drawing-connect-port-target.png` | target ring on DC input, preview around the source block, hint clear of blocks | P3 1 (frame not re-fitted) | good |
| 5 | Move and resize: selected block with handles | `…__drawing-selected-handles.png` (both sketches) | accent outline, eight handles, frame grows | — | good |
| 6 | Remove a used port: question | `…__drawing-remove-port-question.png` | "Keep port" default, "Remove port and connection" | sketch has no such state | good |
| 7 | Saved, a connection selected | `…__drawing-saved.png` | Rail feed selected in accent, captions beside their wires, Save and Decline unavailable after save | P3 (comment selector, R2 P3-5) | good |
| 8 | Compact window | `pairs/A1-option-2-canvas-palette__light__drawing-compact.png` | the level, the frame and "DC input" re-fitted beside the palette; strip, palette and Save/Decline usable | — | good |
| 9 | Palette hidden (View → Drawing palette) | `…__drawing-palette-hidden.png` | the strip stays, the diagram keeps its place | P3 (no recovery tab, R1 P3-12) | good |
| 10 | Read-only file | `…__drawing-read-only.png` | edits allowed in the draft, Save unavailable, reason in the status bar | — | good |
| 11 | New connection selected (A3's state) | `pairs/A3-round2-option-1-connection-inspector__light__details-new-connection.png` | Connection, Caption, + Add detail, rule, + Add requirement, Comments, Decline/Save; caption on the canvas | P3 (no end handles, no placeholder) | good |
| 12 | Saved connection | `…__details-selected.png` | the same controls plus "Selected connection: v1" | — | good |
| 13 | Direction added | `…__details-direction-row.png` | one row, choices named after the ends, remove link | — | good |
| 14 | Signals | `…__details-signals.png` | "Remove signals" link, a "×" per signal, "Both ways" in the accent tile, arrowheads | M1-9 (no domain colour) | open: owner |
| 15 | All details | `…__details-all-details.png` | four detail kinds, + Add detail gone, Signal unavailable | M1-9 | open: owner |
| 16 | Differential pair | `…__details-pair.png` | signal controls unavailable | — | good |
| 17 | An agent's details after reload | `…__details-agent-details.png` | the agent's rows appear like a person's, with remove links | M1-9 | open: owner |
| 18 | Compact connection inspector | `…__details-compact.png` | every choice inside the inspector, "Supply input" whole | — | good |
| 19 | Add a facet (A4) | `pairs/A4-option-3-chips__light__choices-add-facet.png` | Type detail with State, Value, Strength | P3 (one-click rows, R1 P3-18) | good |
| 20 | Facet detail, Manufacturer unknown (A4's state) | `…__choices-detail.png` | chips "Type: linear regulator" and "Package: SOT-23-5", Review facets, overview rows with marks and chevrons, Back to facet overview, detail | P3 (Open diagram as a button) | good |
| 21 | A third choice | `…__choices-more.png` | "+2 more" instead of a fragment | — | good |
| 22 | Saved chips | `…__choices-saved.png` | chips and overview after Save | — | good |
| 23 | Compact chips and package detail | `…__choices-compact.png`, `…__choices-compact-package-detail.png` | "+3 more", full facet names | — | good |
| 24 | Narrowest inspector | `…__choices-narrowest-inspector-package-detail.png` | short Strength labels only here, Clear facet clear of them | — | good |
| 25 | System level, PSU selected (walkthrough 01's state) | `pairs/01-system-v5-edit-controls__light__canvas-system-psu-selected.png`, `…__canvas-system.png` | breadcrumb path, PSU selected, Comments shows the PSU's own comment, the free-space note drawn as a plain note, requirement fields with History, Save/Decline | M1-1 fixed | good |
| 26 | PSU level (walkthrough 02) | `pairs/02-psu-v3-edit-controls__light__canvas-psu.png`, `…__canvas-psu-captions.png` | the level's blocks, ports and every caption drawn; requirement fields; Comments empty for the PSU itself | M1-7, M1-8; M1-9 | open: integration owner and owner |
| 27 | CPU level (walkthrough 03) | `pairs/03-cpu-v2-edit-controls__light__canvas-cpu.png`, `…__canvas-chip.png` | routes in free space, the Memory's chip after Add detail | M1-9 | open: owner |
| 28 | Core journey levels and compact window | `pairs/01-system-v5-edit-controls__light__recursive-system.png`, `pairs/02-psu-v3-edit-controls__light__recursive-psu.png`, `pairs/03-cpu-v2-edit-controls__light__recursive-cpu.png`, `pairs/02-psu-v3-edit-controls__light__recursive-compact.png` | navigation with the selection kept; in the compact window every boundary name clear of the blocks | M1-2 fixed | good |
| 29 | Whole-diagram history (01) | `pairs/01-diagram-history__light__diagram-history-inspection.png`, `pairs/01-diagram-history__light__diagram-history-preview.png`, `pairs/01-diagram-history__light__diagram-history-compact.png`, `pairs/01-diagram-history__light__09-diagram-history-panel.png` | title, saved version, rows with author and summary, comparison, Preview and Restore as draft (now the accent primary), Return to current while previewing | M1-4, M1-5 fixed; P3 4 | good |
| 30 | Field history (02) | `pairs/02-field-history__light__02-earlier-text.png`, `pairs/02-field-history__light__03-compact.png`, `pairs/02-field-history__light__continued-field-history.png` | title with scope, list and comparison, Selected and Saved text, Close and Use … text in draft (accent primary), rows ending in "…", padded texts | M1-3, M1-4, M1-5 fixed; heading accepted (deviation 7) | good |
| 31 | Save conflict (03) | `pairs/03-save-conflict__light__04-conflict-unresolved.png`, `pairs/03-save-conflict__light__05-conflict-resolved.png`, `pairs/03-save-conflict__light__recursive-conflict.png` | base, draft and latest saved with the differing words marked, three choices, Resolved text, Back to editing, Save resolved version (accent primary once available) | M1-4, M1-5, M1-6 fixed; P3 3, 7 | good |

## Pass 1 findings (run `t20260925T224749Z-389908`, commit `7e8af4b5c6`)

Paths are relative to `/mnt/build-storage/codex/kicad/evidence/editor-mockup-audit-t20260925T224749Z-389908/`. Each light composite has a dark twin with `__dark__` in its name.

### P0 and P1

None. Nothing blocks use, and no accessibility failure is severe.

### P2 (moderate; each blocks handoff until fixed or decided)

**M1-1. Comments opens on a note in the level's free space, and the note is drawn as selected** — fixed in this lane.
- Where: inspector Comments with a block or the level itself selected; walkthrough System, PSU and CPU.
- Evidence: `pairs/01-system-v5-edit-controls__light__canvas-system.png`, `pairs/02-psu-v3-edit-controls__light__canvas-psu.png`, `pairs/03-cpu-v2-edit-controls__dark__canvas-cpu.png`. The mockups show the selected block's own comment ("Explore a quieter supply.") in Comments and the free-space note as an ordinary yellow note. The implementation shows the level's note in Comments ("Keep PSU replaceable as a unit.", "Keep sensing away from switching nodes.", "Compare memory-interface implementations."), picks it in the comment list, and draws that note with the selection accent. Typing in Comments then rewrites the level's note instead of commenting on the block; with the PSU selected on System, the PSU's own comment is hidden behind the list.
- Impact: a person who comments on a block silently edits a different, shared note; the canvas shows a note as selected that the person never picked.
- Fix: Comments opens on the selected element's own first comment, or on a new comment when it has none; free-space notes stay in the list (`RECURSIVE_DIAGRAM_FRAME::fillComments`).

**M1-2. In the compact window a boundary port's name runs into a block** — fixed in this lane.
- Where: CPU level in the 1100 × 760 window, both themes and both projects.
- Evidence: `pairs/02-psu-v3-edit-controls__dark__recursive-compact.png` (the same in both projects and both themes; pass 2's matching 2× crop is `EV2/crops/M1-2-compact-cpu-names__*.png`): "Telemetry", the name of the level's second boundary port, is drawn across the Processor's left edge, over its port square and into its caption.
- Impact: hidden text and a hidden port in a supported window size (responsive defect).
- Fix: an unplaced boundary port names itself above and right of its square only when that place is clear of the blocks and their edge ports; otherwise above and left of it, outside the level, like a port placed on the level's left side, and fitting keeps room for such names.

**M1-3. Field-history rows are cut off mid-word** — fixed in this lane.
- Where: the field-history dialog (`pairs/02-field-history__light__02-earlier-text.png`, `…__03-compact.png`, `…__continued-field-history.png`).
- Evidence: the approved rows read "v2 · User"; the rows here read "Initial approach · v2 · Fix" and "Initial approach · v1 · Fix", hard-cut at the list's edge with no "…".
- Impact: the author of an earlier row is unreadable and the cut looks broken.
- Fix: rows wider than the list end in "…" (GTK text renderer ellipsizing in a fixed column); the full row stays the accessible name and the selected row's heading shows it whole.

**M1-4. The history and conflict surfaces have no primary action** — fixed in this lane.
- Where: "Use … text in draft" (field history), "Restore as draft" (diagram history panel), "Save resolved version" once it is available (conflict).
- Evidence: `pairs/02-field-history__light__02-earlier-text.png`, `pairs/01-diagram-history__dark__diagram-history-inspection.png`, `pairs/03-save-conflict__light__04-conflict-unresolved.png`. Each approved image fills its primary action with the accent blue ("Use v2 text in draft", "Restore v2 as draft", and the disabled grey "Save resolved version"); the implementation draws plain grey buttons, unlike the editor's own Save (design QA round 1 P2-8).
- Fix: the three buttons use the editor's Save styling while available: an accent fill 3:1 or more from the surface and a label 4.5:1 or more on it; unavailable, the theme's disabled look.

**M1-5. Text in the history and conflict boxes touches the box border** — fixed in this lane.
- Where: the field-history "Selected text" and "Saved text" boxes, the conflict dialog's Base, Your draft, Latest saved and Resolved text boxes, and the diagram-history comparison.
- Evidence: in `…02-earlier-text.png` and `…04-conflict-unresolved.png` the text starts 2 pixels inside each box; the mockups keep about 12 pixels, and the editor's own boxes keep 8 (round 1 P2-9).
- Fix: the same 8 × 6 pixel inner margins as the editor's boxes.

**M1-6. The conflict dialog does not show where the two versions differ** — fixed in this lane.
- Where: conflict dialog, Your draft and Latest saved (`pairs/03-save-conflict__light__04-conflict-unresolved.png`, `…__recursive-conflict.png`).
- Evidence: the approved state marks "top edge" in the draft and "bottom edge" in the saved text; the implementation shows both texts unmarked.
- Impact: in a long requirement the person has to compare the two texts word by word to see what conflicts.
- Fix: the words that differ are tinted amber and underlined in both boxes (the underline keeps it visible without colour, interaction specification §5).

**M1-7. The PSU fixture's note covers a block, a port and a wire** — needs the integration owner (fixture).
- Where: walkthrough PSU level (`pairs/02-psu-v3-edit-controls__light__canvas-psu.png`, `…__canvas-psu-captions.png`, both themes, both projects).
- Evidence: the mockup places "Keep sensing away from switching nodes." in free space at the lower left. The fixture stores it at (40, 300) (contract §1.5.3, annotation 3), and the legacy grid (contract rule F1) puts Telemetry ADC at (140, 360); the 240 × 100 note covers "Tele" of the block's caption, its upper-left port and 50 pixels of Rail A sense. Round psu-cpu-canvas already recorded this as an unrepaired P2.
- Fix (parent-owned fixture): move annotation 3 into free space, for example (40, 560), below the grid's second row (y 360 to 505), in `PsuCpuFixtureBuilder.cs`, `system.blocks.xml`, `PsuCpuFixtureTests.cs` and §1.5.3.

**M1-8. A computed wire passes behind a block** — needs the integration owner (contract F4, outcome `p9fd01b0c9678331c`).
- Where: walkthrough PSU level, both themes (`pairs/02-psu-v3-edit-controls__light__canvas-psu-captions.png`): Rail B runs behind DC-DC at y 175, so it looks as if it ends at DC-DC's right-hand port, and Telemetry's last run passes 5 units under DC-DC's lower edge. The CPU level's routes stay in free space.
- Evidence: the mockups route every wire in free space. Contract rule F4 (erratum 2026-09-24 and its 2026-09-25 amendment) keeps every computed leg to three segments, so a leg whose two heights are fixed by its ends can only pass behind a block.
- Fix: the F4 change already requested in `automation/design/per-level-editor-design-qa.md` ("Request for the integration owner: finished connections around blocks").

**M1-9. A connection's domain is not shown on the canvas** — needs the owner's visual decision.
- Where: every approved image with connections; the implementation's `details-*` steps after Domain is chosen (`pairs/A3-round2-option-1-connection-inspector__light__details-agent-details.png`, where the unselected Power, domain Power, is drawn grey).
- Evidence: the sketches draw power connections red with filled arrowheads, data connections blue and sense connections as grey dashes; the implementation draws every unselected connection as a thin grey line whatever its Domain.
- Why not fixed here: the sketches give light-theme red and blue only. The dark-theme colours, the colours of Control and Mechanical, and how a selected data wire (blue) stays distinct from the blue selection accent are new visual decisions for the owner (the sketches mark a selected wire with end handles, which round 2 already left to the owner as P3 14).

### P3 (polish; may remain as follow-up)

Carried from rounds 1 and 2 and seen again here: the dotted grid (R1 P3-5), centred larger block captions and status fills (R1 P3-6), inspector title size and the Open diagram and History buttons drawn as buttons rather than links (R1 P3-14), the comment selector beside Comments (R2 P3-5), the smaller palette (R2 P3-6), thin grey unselected wires (R2 P3-7), a selected connection without end handles (R2 P3-14), no "Add a comment…" placeholder (R2 P3-15), option rows that wrap (R2 P3-18), the near-black dark Save label (R2 P3-3). New in this audit:

1. When the first boundary port creates the level frame during Connect, the view is not re-fitted, so the frame's right edge runs under the inspector until Fit (`pairs/A1-option-1-toolbar-strip__dark__drawing-connect-port-target.png`).
2. "Measurements" is placed in free space 50 pixels under its wire on the crowded PSU level, where the channel between the columns has no room (`…canvas-psu-captions.png`).
3. The approved reassurance lines are missing: "Only Routing requirements will change." (field history), "Save the draft to create a new revision." (diagram history) and "Both versions are preserved." (conflict). The button labels already say "in draft" and "as draft".
4. The diagram-history panel's Back is a text button, not the approved chevron; its Saved marker is text, not a chip; its buttons read "Preview" and "Restore as draft", not "Preview v2" and "Restore v2 as draft"; its comparison is one text box rather than the approved list of changes.
5. Dialog titles are 2 points larger than the body, less than the mockups' clear title size; GTK's list selection is a solid accent row, not the mockup's pale tint.
6. While a history or conflict dialog is open the status bar reads "Working…".
7. The conflict dialog opens with the keyboard focus in the read-only Base box.
8. On the PSU level, the PSU's own comment from the System level is not shown (each level keeps its own annotations, `recursive-structural-refinement-plan.md` §1: "Each diagram has its own layout, annotations, …").
9. A canvas note is a fixed 240 × 100 box, so a two-line note leaves most of the box empty (the mockups fit the note to its text).

### Round 2 findings, re-checked in pass 1's fresh captures

| Round 2 finding | Status | Evidence (pass 1) |
|---|---|---|
| R2-P2-1 new connection's caption missing | fixed | `pairs/A3-round2-option-1-connection-inspector__light__details-new-connection.png`: "Supply input" drawn beside its wire, outside the frame, under DC input's name |
| R2-P2-2 preview through the source block | fixed for the preview | `pairs/A1-option-1-toolbar-strip__light__drawing-connect-port-target.png`: the dashed preview leaves Rail outward and goes around the PSU to DC input (the finished path is M1-8's contract question) |
| R2-P2-3 unrelated block stays selected during Connect | fixed | `…__drawing-connect-hint.png`: the PSU (the connection's start) is selected and fills the inspector |
| R2-P2-4 chosen options faint | fixed | `…__details-signals.png`, `…__details-all-details.png`: chosen "Both ways", "Power", "Signal group" in the accent tile |
| R2-P2-5 two removes look alike | fixed | `…__details-signals.png`: "Remove signals" link beside the heading, a small "×" per signal |
| R2-P2-6 chip cut to "Family: T…" | fixed | `pairs/A4-option-3-chips__light__choices-more.png`: "Type: linear regulator" and "+2 more", no fragment |
| R2-P2-7 Strength abbreviated | fixed | `…__choices-detail.png`: Information, Preference, Requirement at the default size |
| R2-P2-8 "Rail f…" in the compact window | fixed | `pairs/A1-option-2-canvas-palette__light__drawing-compact.png`: "Power" and "Rail feed" whole |

The journeys measure each of these in both themes (values in each instance's `*-design-measurements.tsv`), and all their checks passed in this run.

## Pass 2 (run `t20260925T232648Z-826429`, the fixes of M1-1 to M1-6)

Graph `diagram-requirement-history`, release tier, complete, **passed**, `source_changed` false, 2026-09-25 23:26:48 to 23:43:48 UTC: build 32.9 s, contracts 108.8 s, native protocol 53.8 s, native UI 762.5 s (6 of 6 cases passed: the recursive editor and field-history journeys in both themes, and the simulation and PCB-item journeys), PSU/CPU canvas 114.6 s (2 of 2). Its source is this commit's source except `design-qa.md` and the design record, which changed after it. Evidence: `EV2` = `/mnt/build-storage/codex/kicad/evidence/editor-mockup-audit-t20260925T232648Z-826429/` (`SHA256SUMS`); artifact manifests native UI `b6b3e4f5a4c5209ca0305ffc5aa69045cc7e3eb0a3dbd8880078ee95c229c982` and PSU/CPU canvas `2fb896776483e9420d71178cbf5afe48be782512ff2919148264aedf42766dd2`, configuration `558dcafbcdec8234b7d72baa70b5ae985691ec80839613e091b3d7aa38e5b0d2`. Instances: editor light `9358055f-c060-4914-bc57-11f28faa4710` (with `066dd378-0d7a-422b-93a1-2a43bdeedb3d`), dark `9451df86-62d4-455a-9ec3-fd1e8243aae0` (with `21088402-9ddb-426a-b351-863ebba11cf5`); canvas light `5da88c5a-ce5e-43f7-b45a-d24774a60ea5` and `a2a1f10b-75b1-45b1-abae-2ab2a2f70b1c`, dark `80ca8b5d-e3f2-420f-830f-fad1ad639527` and `be8b22f2-1b6c-4b95-a8a4-7e9379e8d571`.

Every composite and crop of pass 1 was rebuilt from these captures (`EV2/pairs/`, `EV2/crops/`), all captures were inspected again (`EV2/sheets/`), and two states were added: the System level with the PSU selected, which is walkthrough 01's state (`canvas-system-psu-selected`), and the resolved conflict (`05-conflict-resolved`).

| Pass 1 finding | Fix (lane 2B files) | Post-fix evidence | Asserted by the journeys (both themes) |
|---|---|---|---|
| M1-1 Comments on the level's note | `fillComments` opens the element's own comment or a new one | `EV2/pairs/01-system-v5-edit-controls__light__canvas-system-psu-selected.png` and `EV2/crops/M1-1-system-psu-comment__*.png`: Comments shows "Explore a quieter supply." with the PSU selected, as the mockup does; `EV2/crops/M1-1-system-canvas-note__*.png`: "Keep PSU replaceable as a unit." is drawn as a plain note; on the PSU and CPU levels Comments offers "New comment" | the canvas journey's `Level` and `OwnComment` check the selected comment on System, PSU and CPU in both projects, and the System step with the PSU selected (`…-canvas-system-psu-selected-state.json`: selected comment `…001a00000002`, the PSU's own) |
| M1-2 compact boundary name over the Processor | `boundaryName` puts a fallback-column name left of its port when the right side would touch a block; `labelRoom` and `fit` keep room for it | `EV2/crops/M1-2-compact-cpu-names__*.png`, `EV2/pairs/02-psu-v3-edit-controls__light__recursive-compact.png`: "Telemetry" at (138, 365), 79 px wide, left of its port and clear of the Processor at (280, 355) | `VerifyBoundaryNamesClear` in `recursive-psu`, `recursive-cpu`, `recursive-compact` and every fixture level: each name inside the canvas and clear of every block (4 px around it) and every port square |
| M1-3 rows cut mid-word | `EllipsizeRows`: the list's text renderer ends long rows in "…" | `EV2/crops/M1-3-5-field-history__*.png`: "Initial approach · v2 · …" | `rows_ellipsize` true (read back from GTK: end ellipsizing, fixed column no wider than the list) |
| M1-4 no primary action | `StylePrimary` on Use … text in draft, Restore as draft and Save resolved version | `EV2/crops/M1-3-5-field-history__*.png`, `EV2/crops/M1-4-5-history-panel__*.png`, `EV2/crops/M1-4-conflict-resolved__*.png` (Save resolved version filled once available; unresolved it keeps the disabled look, as the mockup) | fill against the dialog 4.19 light / 3.25 dark (asserted 3:1), label on the fill 4.57 / 4.83 (asserted 4.5:1), for all three buttons |
| M1-5 text against the border | `PadTextBox` on every history and conflict text box | the same crops: text starts 10 px inside each box | 10 px for the selected, saved, base, draft, latest saved and comparison boxes (asserted 8) |
| M1-6 no difference marks | `markDifferences`: differing words tinted and underlined | `EV2/crops/M1-4-5-6-conflict__*.png`: "top" and "bottom" marked, as in the mockup; the editor's own conflict (`…recursive-conflict.png`) marks the whole differing sentences | marked words exactly `top` and `bottom`; 272 and 620 tint pixels drawn; text on the tint 17.74 light / 7.10 dark (asserted 4.5:1) |

No P0 or P1 difference and no new P2 difference was found in pass 2's captures; the fixes changed nothing else that is visible (the compact CPU level is fitted slightly smaller to keep room for the moved name). M1-7, M1-8 and M1-9 are unchanged because they are outside this lane's authority (see "What unblocks the handoff").

## Iteration history

| Round | Run and source | Result | What happened next |
|---|---|---|---|
| Design QA round 1 | `t20260924T075214Z-0f0a8f` on integration `3f1222a94a`; record `/mnt/build-storage/codex/kicad/evidence/editor-design-qa-0f0a8f/design-qa.md` | blocked: 1 P1, 13 P2, 21 P3 (A1 and A4) | Lane 2B fixed the P1 and every P2 (`b61cec3afa`, `9fe46db347`; routing erratum `n03892aa8cecf933f`); record `automation/design/per-level-editor-design-qa.md`, "Design QA fixes". |
| Design QA round 2 | `t20260925T023751Z-2b814c` on `455776ebf9`; record `/mnt/build-storage/codex/kicad/evidence/editor-design-qa-2b814c/design-qa.md` | blocked: 0 P1, 8 P2, 20 P3 (A1, A3, A4); every round 1 P1/P2 fixed or reduced to P3 | Lane 2B fixed R2-P2-1 to R2-P2-8 (`5ae1f0e0c7`, review repairs `7e8af4b5c6`), except that a finished connection still keeps F4's three-segment path (request for the integration owner, outcome `p9fd01b0c9678331c`). |
| This audit, pass 1 | `t20260925T224749Z-389908` on `7e8af4b5c6` | blocked: 0 P0, 0 P1, 9 P2 (M1-1 to M1-9), P3 list | Every round 2 P2 checked again in the fresh captures and holds (below). Lane 2B fixed M1-1 to M1-6; M1-7 to M1-9 need the integration owner or the owner. |
| This audit, pass 2 | `t20260925T232648Z-826429` on `7e8af4b5c6` plus this commit's fixes | blocked: 0 P0, 0 P1, 3 P2 (M1-7 to M1-9), P3 list; M1-1 to M1-6 fixed and measured | M1-7 and M1-8 go to the integration owner, M1-9 to the owner. The commit's own validating runs are named in its message. |

## Rendered interaction pass

Every visible control of the audited states was driven through the real window by the governed journeys, with pointer and keyboard input aimed at the geometry the editor reports, and every result read back from the editor's state and the saved file. The journeys are `RecursiveEditorNavigatesLevelsAndSavesRequirementHistory` (category NativeRecursiveEditor, light and dark: the core editor journey, then `VerifyDrawingTools`, `VerifyBlockChoices` and `VerifyConnectionDetails` in `NativeRecursiveEditorJourney.cs`), `PerLevelCanvasEditsPersistLayout` (NativeDiagramCanvas, light and dark: `VerifyPsuCpuDiagramCanvas`, driven partly by an agent over the production MCP server on STDIO) and `RenderedHistoryReturnsOnlyTheChosenFieldRevision` (NativeFieldHistory, light and dark: the field-history, paging, conflict and whole-diagram history dialogs built by `qa_diagram_field_history`). Each passed in both themes in every run named in this report.

| Control (where) | Action and observable result proved | Cancel, error and recovery proved |
|---|---|---|
| Strip and palette tools: Select, Add block, Connect, Place port, Delete, Undo; B, P, C keys | one shared active tool; each tool changes the canvas mode; Delete removes and names what it removed; Undo and Redo restore exactly | Escape returns to Select; Ctrl+C/B/P never switch tools; unavailable tools are disabled and reported so to assistive technology |
| Canvas: blocks, ports, connections, notes, frame | add a block with its caption, connect blocks and ports, place ports on edges and the frame, move, resize, drag a port and a note; nearest-connection selection | empty caption refused, connection from empty space or back to its own block refused, used-port question with Keep port (Escape) and Remove port and connection (Alt+R) |
| View → Drawing palette | hides and restores the palette; the diagram keeps its place | — |
| Inspector, block: caption (F2), Open diagram, + Add detail (facets), facet rows, State and Strength choices, Reason and Candidates, Clear facet, Back to facet overview, + Add requirement, requirement boxes and their History, Comments and its comment list | every edit changes only the draft; Save writes one revision; chips and the facet overview follow; "&" in a value is shown and read as written | Clear facet returns a facet to unknown; Decline (Alt+D) leaves the file byte-identical; read-only files keep Save unavailable with the reason in the status bar |
| Inspector, connection: Caption, + Add detail (Signals, Direction, Domain, Type), signal entry, each signal's ×, Remove signals / direction / domain / type / endpoint details, the one-click choices | each detail is added as its own row and removed exactly; direction arrowheads follow; a differential pair locks its two signals | Escape in + Add detail adds nothing; blank caption refused (also on Ctrl+S); repeated signal refused; drawn signals leave as they came, saved ones through the removal cascade; Undo and Redo |
| Path row: level path, implementation selector, History | Back, Up, Enter and Backspace move between System, PSU and CPU with the selection kept; the selector previews, creates, duplicates, selects and removes implementations | a changed file refuses the save and keeps the draft; Decline restores |
| Diagram history panel: Back, rows, Load older, Retry, Preview, Restore as draft, Return to current | inspecting a row changes nothing; Preview shows the old revision read-only; Restore as draft makes a draft that Save turns into a new revision | a wrong comparison is refused; paging failure keeps the loaded rows; Back restores the inspector |
| Field-history dialog: rows, Use … text in draft, Close, Load older | Use copies only that field into the draft and closes; the row labels, heading and button name the earlier implementation | Escape and Close change nothing and clear a pending restore; a failed or malformed page keeps the rows; closing cancels a load |
| Conflict dialog: field selector, Use my text, Use saved text, Write merged text, Resolved text, Back to editing, Save resolved version | Save stays unavailable until every field has a choice; the saved resolution is bound to the exact base and saved revisions | Back to editing and Escape publish nothing and keep both versions |

Pass 2 adds, through the same journeys: Comments opening on the block's own comment on the System, PSU and CPU levels (M1-1), boundary names clear of every block and port at the default and compact sizes (M1-2), and the measured primary actions, insets, ellipsized rows and difference marks of the dialogs (M1-3 to M1-6); see "Pass 2".

No enabled control without real behaviour was found. The Agent console entry and "View source instruction" are not shown, because their integrations are not built (accepted deviations 2 and 9).

## Required fidelity surfaces

- **Fonts and typography.** The GTK system font (DejaVu Sans on the test display) replaces the sketches' humanist sans by owner default `n46bd6c44b42d5a61`. Hierarchy holds: bold block captions, bold section labels (R2 P3-17 done), 9 pt palette labels. Truncation: no chip, caption or facet value is cut to a fragment in any capture after pass 2 (R2-P2-6 and R2-P2-8 hold); the field-history rows now end in "…" (M1-3). Titles are only 2 points above the body (P3 5).
- **Spacing and layout.** Toolbar cells, palette inset (12 px), inspector section rhythm, the 12 px gap after the facet table and the 8 × 6 text insets match the sketches and earlier measurements; the history and conflict boxes now keep the same insets (M1-5). The walkthrough levels are drawn on the contract's legacy grid (rule F1), which spreads the diagram differently from the mockups; this is an accepted contract layout, except where it lets the fixture's note and a wire overlap blocks (M1-7, M1-8).
- **Colours and tokens.** One accent for selection, preview, active tools, links and primary actions in both themes (measured 3:1 or more for graphics, 4.5:1 for text, R1 P1-1 and later rounds, and M1-4 in pass 2). Chip state colours read 11.9:1 or more. Domain colours on wires are missing (M1-9, owner decision). The difference mark in the conflict dialog is amber in both themes (M1-6).
- **Image quality and assets.** Tool glyphs are drawn monochrome at 24 DIP and render crisply in both themes; the port glyph is the owner's hollow square. No image, illustration or logo in the targets is replaced by a drawing. KiCad's own toolbar bitmaps show "?" in this lane's build only (limit).
- **Copy and content.** Tool labels, the hint "Click a port to finish connection", "+ Add detail", "+ Add requirement", "Review facets", "Back to facet overview", "Clear facet", "Remove signals" and the history and conflict copy match the approved images. Three approved reassurance lines are missing (P3 3). The field-history heading adds the author and the earlier implementation (accepted deviation below).

## Accepted deviations (each with the decision or requirement that allows it)

1. **Caption first, grow with definition** (`n98a3f3c41084f0ed`, `nf53af9d74841b7d3`): new blocks and connections show only their caption; the inspector shows only what is defined, behind "+ Add detail" and "+ Add requirement"; no "Selected design", Open diagram or History until they exist. The A4 facet overview lists only facets with a value (`n0b2a908b00e78823`).
2. **Owner defaults for the round 1 open questions** (`n46bd6c44b42d5a61`): hollow-square port glyph, GTK system font, the approved native window chrome (File and View menus, no Properties pane header, no Agent console until it is integrated, `recursive-structural-refinement-plan.md` §5, "Editing is separate from agent execution": "Any integrated console entry must be designed and verified against the actual supported client before becoming an enabled product control"), no repeated Review facets link, the hint text unchanged.
3. **Both A1 options together** (`n9f7cf92f32090daf`): the strip and the palette drive one tool state and share one hint.
4. **Computed paths** (`n03892aa8cecf933f`, amended in `nb44e7980df701f1b`): every connection has its own path, so routes differ from the sketches' hand-drawn lines. This does not cover a wire behind a block (M1-8).
5. **"+N more"** when chips do not fit (round 2 record), and one-click State and Strength choices instead of drop-downs (open owner question R1 P3-18).
6. **Level frame and legacy grid** (contract rbg-v2 rules F1 and F3): a level that stores no layout is drawn on the grid with its boundary ports in the left column, and a stored boundary port draws the dashed frame.
7. **Field-history heading and rows name the author and the earlier implementation** (p390b, ledger `p390b40bed99e0ab2`). The approved image's heading reads "v2 — Selected text" and its rows "v2 · User"; the implementation's heading reads "Initial approach · v2 · Fixture user — Selected text" and a row saved in an earlier implementation reads "Initial approach · v2 · Fixture user". **Passed, with this rationale:** the approved interaction specification requires the selected row to show "that exact field value, actor and provenance" (`automation/design/history-and-conflicts/interaction-specification.md`, §3, "Select history row"), and an implementation made from another continues its field history, so two rows can both be "v2" of different implementations; without the implementation's name they would read as the same version, and the narrow list can cut the actor off (M1-3), so the heading is where the actor and provenance stay readable. The heading keeps the approved "— Selected text" form and shortens only the implementation's name when its column is narrow. Rows of the chosen implementation keep the approved "vN · actor" form (with "Saved" before the actor, repaired earlier so the marker is never cut).
8. **Field-history paging** ("Load older", "N of M changes") appears only for histories over 200 changes, as the specification's "Longer histories" section requires; the approved image shows a short history.
9. **View source instruction** is shown only when a revision has a retained source; the journeys' fixtures have none, and opening sources is the separate open outcome `pb09aa30cf44e609b` (specification §2: "A missing reference is not an invented link").
10. **Several conflicts in one dialog** show a field selector instead of the field name in the subtitle (specification §4: "For several conflicts, reuse this same surface with an explicit conflict list/count").

## What unblocks the handoff

| Finding | Who decides | The smallest change that clears it | How the next audit proves it |
|---|---|---|---|
| M1-7 fixture note over Telemetry ADC | integration owner (parent-owned fixture, contract `psu-cpu-fixture-and-ownership.md` §1.5.3) | move PSU annotation 3 from (40, 300) into free space, for example (40, 560), in `PsuCpuFixtureBuilder.cs`, `automation/tests/fixtures/psu-cpu/system.blocks.xml`, `PsuCpuFixtureTests.cs` (the `Notes("PSU")` expectation) and §1.5.3 | `VerifyPsuCpuDiagramCanvas` already asserts each note's box; add that no canvas note overlaps a block, a port or a wire on System, PSU and CPU |
| M1-8 Rail B behind DC-DC | integration owner (contract CN-2 rule F4; outcome `p9fd01b0c9678331c`) | append the F4 erratum text already written in `automation/design/per-level-editor-design-qa.md` ("Request for the integration owner: finished connections around blocks"), then lane 2B routes computed legs around blocks | `VerifyRoutesClear` on the PSU level: no computed segment runs through a block's inside |
| M1-9 domain colours | owner (visual decision) | choose: (a) power red, data blue and analog dashed grey as the sketches show, with dark-theme versions derived like the accent (4.5:1 on the canvas), Control and Mechanical left neutral, and end handles on a selected connection so a selected data wire stays distinguishable; or (b) keep neutral wires and accept the difference from the sketches | (a) a measured colour per domain in both themes and a selected data wire with handles; (b) recorded as an accepted deviation |

## Evidence limits

- **Screen reader.** Roles, names and states are read back from GTK's accessibility objects by the journeys; no screen reader ran on the test display.
- **Keyboard.** The journeys use the keyboard for tools, captions, fields, comments, saves, navigation, history and dialogs (tool letters, F2, Ctrl+1 to Ctrl+5, Alt mnemonics, Escape, Enter, arrows, Backspace), and the pointer for canvas drawing, dragging and the inspector's buttons; not every control was driven both ways, and focus visibility was judged from captures, not measured for every control.
- **Window manager.** No title bars, dialog frames, close buttons or dialog centring on the test display; the approved images' window chrome is judged only where the owner decided it.
- **Toolkit icons.** KiCad's own toolbar bitmaps render as "?" in this lane's build; they were not re-judged here (round 2 judged them in the integration build).
- **Platforms.** Linux (GTK) only; the Mac and Windows builds are on hold by the owner's instruction (`kicad-hold-mac-windows-until-xml-editor`), and the painted GTK styles have platform-native fallbacks there that were not compiled.
- **Mock content.** Fixture names and structure differ from the mockups (see "Comparison conditions"); M1-7 and M1-8 are judged on the fixture as it is.
- **Tooltips.** Hover tooltips are proven by the text the canvas and controls report; captures show them only where the journey waited for them.

## Final checklist

- [x] Product Design contracts loaded and followed (index, audit with its framework, design-qa with its rubric, user-context preflight, critical overrides); executed by Claude Opus 5.5.
- [x] The four selected sketches resolved from the Coordinator with verified SHA-256, and the approved walkthrough and history/conflict mockups identified by path and hash.
- [x] Fresh captures from governed runs of the audited source, hash-checked (`SHA256SUMS` in each evidence folder); none reused from earlier rounds.
- [x] Light and dark themes, the default window and the 1100 × 760 compact window; the dialogs at their default and compact sizes.
- [x] Every approved image placed side by side with its matching capture in one composite; focused crops where detail matters; every capture used opened and inspected (contact sheets).
- [x] Combined UX, design and accessibility audit with numbered steps, strengths, risks and evidence limits.
- [x] Required fidelity surfaces checked: typography, spacing, colours, assets, copy.
- [x] Rendered interaction pass complete through the real journeys: every visible control, cancellation, errors, persistence and recovery; no enabled control without behaviour.
- [x] Round 2's eight P2 findings re-checked one by one in fresh captures, and round 1's P1 and P2 repairs hold in the journeys' measurements of every run (`*-design-measurements.tsv`); none has come back.
- [x] Pass 1 P2 findings in lane 2B's files (M1-1 to M1-6) fixed, re-captured under the same conditions and re-audited in pass 2, with measured assertions in the journeys.
- [x] The field-history heading change (p390b) passed with a rationale citing the approved interaction specification §3.
- [x] Accepted deviations cite an owner decision or the approved specification.
- [x] P3 follow-ups listed.
- [ ] M1-7 fixture note over Telemetry ADC — waits for the integration owner (fixture).
- [ ] M1-8 Rail B behind DC-DC — waits for the integration owner (contract F4, `p9fd01b0c9678331c`).
- [ ] M1-9 domain colours on the canvas — waits for the owner's visual decision.

final result: blocked — three P2 findings remain that lane 2B cannot fix in its own files without changing direction: M1-7 needs the integration owner to move the PSU fixture's note off Telemetry ADC, M1-8 needs the integration owner's F4 contract change so that Rail B no longer runs behind DC-DC, and M1-9 needs the owner's decision on showing a connection's domain by colour on the canvas.
