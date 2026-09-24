# Per-level editor — design QA

final result: blocked

This record covers two items of the per-level diagram editor: the drawing tools (Round A1, item a1-drawing-tools) and component choices on blocks (Round A4, item a4-block-chips). Both work end to end in the light and dark themes and in a compact window, and the reviewed captures follow the chosen sketches closely enough to use. The overall result stays blocked because the formal design audit (`product-design:audit` with `design-qa`) is not available in this session, so no formal audit has run (see each item's "Limits of this review"). These comparisons do not replace it, and neither item can be handed off as finished until that audit runs or the owner accepts this result.

## Round A1 — drawing tools (item a1-drawing-tools)

The A4 item renamed the inspector's "Add requirement…" action to "Add detail…" (it now also gives a facet its first value) and fixed the block selection handles, which were drawn 8 pixels inside the block while dragging used its corners. The A1 text below describes the A1 runs as they were.

### What is compared

Item a1-drawing-tools lets a person draw a level of the system diagram in the per-level editor. They can add a block by typing its caption, connect blocks and ports, place ports on a block edge or on the level boundary, move and resize blocks, delete with undo and redo, and keep the drawn layout with the level when they save. The owner chose both A1 sketches together (decision n9f7cf92f32090daf):

- **Toolbar strip, option 1.** `/mnt/build-storage/codex/kicad/design-rounds/round-a/A1/option-1.png` (Coordinator sketch `sc23e8d0c5adbb401`, SHA-256 `99dcd9c60d36766be0a735e4de90eca078f4dcfacb27bbfa904056c9fd3fe3d0`). Select, Add block, Connect, Place port and Delete follow the existing toolbar commands.
- **Canvas-edge palette, option 2.** `/mnt/build-storage/codex/kicad/design-rounds/round-a/A1/option-2.png` (Coordinator sketch `s6d7f155fd468901a`, SHA-256 `228b866b188848dc385b1e534e83340d37da4acfa6d9df7efb0ea094c7940421`). The same tools plus Undo sit in a vertical palette at the canvas's left edge. View → Drawing palette hides and restores it.
- **Inspector.** Decision n98a3f3c41084f0ed says a new block or connection shows only its caption, Comments and Decline/Save. A requirement box appears once it has text, or when the person asks for it through one quiet "Add requirement…" action. The sketches still show all three boxes, and the decision takes precedence over them.

Both sketches are 1536 × 1024 full-window scenes of a finished PSU level. The captures are real KiCad editor windows: 1536 × 1024 normally and 1100 × 760 in the compact step, each inside a 1600 × 1150 virtual display. They show a new, empty "Fixture board" level that the journey draws from nothing. Diagram content therefore differs on purpose. The comparison covers the tools, their states, the hint, selection handles, ports, the inspector and the Save/Decline area. No browser, CSS or device scaling is involved. Only the editor region is compared: the black display area and, in the compact capture, the KiCad project window behind the editor are not part of the design.

### Evidence

The rendered journey is `VerifyDrawingTools` in `automation/tests/KiCad.Automation.Tests/NativeRecursiveEditorJourney.cs`. It runs inside `NativeRecursiveEditor` for the light and dark themes. It drives the real window with pointer and keyboard input, and it chooses where to click from the editor's reported canvas geometry and control rectangles. After each step it reads the editor state, and it reads the saved file back through the model.

#### First comparison: run `t20260924T015015Z-eb2643`

All four development checks passed (native UI 462 s). Hash-verified captures are materialized at `/mnt/build-storage/codex/kicad/evidence/drawing-tools-eb2643/` (native-UI manifest `a25023abb675884d69c7cb5cf9d626578a1876ede83d9bacfc968f6f28a0fd34`). The two sketches and the light captures `…-drawing-selected-handles.png`, `…-drawing-inspector-requirement.png` and `…-drawing-remove-port-question.png` were opened together. Four problems were found:

- **P2, layout.** The level frame (the dashed boundary) stayed the size it had when the first boundary port was placed. Moving the CPU afterwards left it half outside the level.
- **P2, copy.** The remove-port question read "1 connections on this level use this port."
- **P3, labels.** A block port's label ("Rail") was drawn outside the block, where it collided with the caption of a connection starting at the same edge ("Power").
- **P3, palette outline.** The palette had a hard black border. The sketch shows a quiet floating card.

#### Repaired comparison: run `t20260924T020158Z-3285da`

All four development checks passed (build 8 s, contracts 82 s, native protocol 29 s, native UI 465 s). Source SHA-256 is `8e00c6457acf0b9a5b8a932277497cc62e99325a97b4e7fda4eff0766a95b322`, native-UI manifest `64dbaa6f0572a8e889c5223a3eace69c39c14b83aef217ccd3186eeeb31bfe16`. Hash-verified captures are at `/mnt/build-storage/codex/kicad/evidence/drawing-tools-3285da/editor-light/editor-light/` (instance `e112ac39-e5b2-480d-9171-c25e9e67dcc2`) and `…/editor-dark/editor-dark/` (instance `b85d3942-27d2-4950-8604-7361dc63bddc`).

The repairs:

- The stored frame now grows, and never shrinks, to keep a moved, resized or added block 40 units inside it. This is the same margin the first frame uses (contract rule F3). Boundary ports stay where they were drawn, and undo restores the frame with the block.
- The question reads "A connection on this level uses this port. Remove it together with the port?", with a "Remove port and connection" button. The plural wording is kept for more than one connection.
- Port labels sit just inside the block edge, the way KiCad labels sheet pins.
- The palette has a thin rounded outline in the theme's muted tone.

The journey now asserts the frame after the move (100, 90, 740, 280), after the resize (100, 90, 780, 310), after undo and redo of the resize, and in the saved file.

Step-linked captures (the same step names exist for both instances):

| Step | Capture | What it shows |
|---|---|---|
| Empty level | `…-drawing-empty.png` | Select is highlighted in the strip and the palette. The inspector shows the caption, "Add requirement…" and Comments. Delete is disabled. |
| Add block (strip) | `…-drawing-block-caption.png` | The dashed outline of the block being added, with its caption field open in place. Add block is highlighted in both places. |
| Block added | `…-drawing-block-added.png` | The caption-only PSU block is selected, and the inspector shows only the caption and Comments. |
| Connect (palette) | `…-drawing-connect-hint.png` | Dashed rubber band from the PSU and the shared hint "Click a port to finish connection". The status bar explains how to cancel after a click on empty space. |
| Move and fit | `…-drawing-selected-handles.png` | The moved CPU with eight resize handles, the grown frame, "DC input" on the frame and "Rail" inside the PSU. |
| Remove a used port | `…-drawing-remove-port-question.png` | The Remove port question, with "Keep port" as the default. |
| Add requirement | `…-drawing-inspector-requirement.png` | Only the chosen General box has appeared. It has focus and holds typed text. |
| Saved | `…-drawing-saved.png` | Rail feed and Power are two separate connections, each with its caption on its own leg. Rail feed is selected with its endpoints and comment. Save and Decline are disabled after the save. |
| Compact | `…-drawing-compact.png` | At 1100 × 760 the level is re-fitted beside the palette: both blocks, both connections and the "DC input" name are in view. The strip, the palette and Save/Decline stay usable. |
| Palette hidden | `…-drawing-palette-hidden.png` | View → Drawing palette has removed the palette. The strip remains. |
| Reopened | `…-drawing-reopened.png` | After close and reopen, the level comes back from its stored layout, with Rail feed on its stored route. |
| Read-only | `…-drawing-read-only.png` | The file is read-only. After a move, Save is disabled, Decline is enabled, and the status bar says "This diagram file is read-only; changes cannot be saved." |

The sketches were opened with the light and dark `selected-handles`, `connect-hint` and `saved` captures, and the light `remove-port-question`, `compact` and `reopened` captures.

The first commit's gate runs repeated this journey on its final source, which differed from `3285da` only by this document and one added journey step (the compact window presses the strip's Select button).

#### Review repairs: runs `t20260924T025617Z-dafee0`, `t20260924T031011Z-ea0364` and `t20260924T032229Z-f4e31f`

An adversarial review of the first commit rated two drawing problems as P2, where the earlier comparison had rated them P3:

- **P2, connections.** Rail feed and Power both end at the CPU. Rule F2 gives every unresolved end on a block the same anchor, so the two computed paths shared their last legs and looked like one closed box. A click near the CPU selected Power, and the Power caption sat inside the box.
- **P2, compact window.** At 1100 × 760 the CPU and both connection ends were outside the canvas, and the canvas cannot scroll.

The repairs:

- When the Connect tool creates a connection whose computed path would run along one already on the level, the editor stores a route for it (rule F4, an editor-only layout edit under section 4.7). The route's middle leg moves to the free channel with the least overlap, in 30-unit steps between the two ends. The stored route follows every later move, resize, port move, port addition and removal that shifts its ends. Only Rail feed needs a route here, and Power keeps its computed path.
- A click on the canvas selects the nearest connection. Where two connections share a segment, the selected one stays selected.
- Each connection caption sits at the middle of that connection's longest leg: centred above a horizontal leg, or beside a vertical one. A route's stored label position, when it has one, takes precedence.
- Resizing the window re-fits the level while the view is still the fitted one, or whenever part of the level would otherwise be out of view.
- Fitting leaves room for the names of ports on the level frame. Run `ea0364` showed "DC input" half under the palette in the compact capture, so that capture reproduced the P2 again. Run `f4e31f` shows the whole name.

Run `dafee0` failed in both themes. In the dark theme the rebase step expected the PSU to stay at y 130, although the journey's drag also moves it down by 20. That was a wrong expectation. In the light theme the Fit click after the CPU drag reached the canvas instead of the Fit button. The canvas then selected the level, and the view stayed on its first fit. The most likely cause is that the editor had not yet handled the drag's release. Three changes followed. The editor reports whether a drag is in progress. The journey waits until a drag is released before its next step. A new press on the canvas ends a drag whose release never arrived, and a press outside the canvas area changes nothing. Runs `ea0364` and `f4e31f` passed all four checks. For `f4e31f`, native UI took 567 s against its 600 s limit on a loaded host; each themed recursive-editor session took about 4 minutes. Source SHA-256 is `52aca15aada76ca31589a5c3da135b8dbbcdcf2a7482afe6a1db85e2383077c8`, and the native-UI manifest is `abe73c446eda8522408924b7add797d657d1d2c277f9df6a9fd3e6671b5e60b6`. Hash-verified captures are at `/mnt/build-storage/codex/kicad/evidence/drawing-tools-f4e31f/editor-light/editor-light/` (instance `1bf09eec-ff6c-4698-b419-f41ac661fffd`) and `…/editor-dark/editor-dark/` (instance `f8188527-5014-4d97-a070-4db07118ca63`). The sketches were opened together with the light and dark `saved`, `selected-handles`, `compact`, `connect-hint` and `read-only` captures.

This commit's gate runs repeat the journey on the final source. That source differs from `f4e31f` only in this document and in one test change: the journey now reopens the saved diagram once, read-only, instead of once writable and once read-only.

#### Interaction coverage

For each theme, the journey proves the following through the real window:

- **One tool state.** Exactly one strip button and one palette button are active at a time, and they always name the same tool. The plain letters B, P and C choose the same tools. Ctrl+C, Ctrl+B and Ctrl+P never switch tools.
- **Add block.** A click opens the caption editor. Escape cancels without a change. An empty caption is refused with a notice. A typed caption adds a caption-only block where the person clicked (240 × 140 at 140, 130). The tool then returns to Select, and the inspector shows no requirement box, no Open diagram button and no field History.
- **Connect.** The palette and the strip share one hint. A connection cannot start on empty space ("Start the connection on a block or port."). It cannot end on the block it started from ("Connect two different blocks or ports."). A click on empty space while connecting is refused and the hint stays. Finishing on a block asks for the caption. Finishing from a port records that port as the endpoint. A new connection shows only its caption. Rail feed is stored with a route at (500, 230)–(500, 275) after the later moves. A click near its CPU end selects Rail feed rather than Power.
- **Place port.** The first boundary port stores the frame (100, 90, 680, 220) and a Left port at offset 110. A block-edge port records its side and offset.
- **Move, resize and fit.** Dragging moves and selects a block, and the frame grows around it. The Fit toolbar button works. A handle resizes the block. Ctrl+Z and Ctrl+Y undo and redo the resize together with the frame. A port can be dragged along its edge.
- **Delete.** The Delete key removes a connection and names it in the notice. Palette Undo, Ctrl+Y and Ctrl+Z follow. Strip Delete removes a block and reports its two dependent connections. A port that a connection uses asks first: Escape keeps it, and Alt+R removes it with its connection. Escape also returns from Place port to Select.
- **Rename and comment.** F2 renames in place, and undo reverses it. "Add requirement…" opens a menu, and the chosen box appears with focus. Ctrl+4 comments on a connection.
- **Save.** With a blank caption open, Ctrl+S keeps the caption editor open with its notice, sends nothing and writes nothing. Clicking into the inspector then cancels the blank caption and leaves focus in the inspector. Ctrl+S then writes one schema-2 level revision. The CPU stays caption-only and has no stored diagram. The file holds the block rectangles, the frame, the port side and offset, Rail feed's route, both connections without requirements, and the comment.
- **Save against a changed file.** Another editor moves the PSU while this draft moves it too. Ctrl+S rebases without a dialog. The status bar shows "Your layout kept a position that was also moved in the saved design." The automatic save stores the draft's position, and Rail feed's route follows the PSU.
- **Decline.** Alt+D discards a later move and leaves the file byte-identical.
- **Compact window.** At 1100 × 760 the view re-fits: every block rectangle lies inside the canvas, to the right of the palette. The palette fits the canvas and the strip stays visible. The strip's Connect and Select buttons work. View → Drawing palette hides and restores the palette. Back at full size, the view re-fits again.
- **Close and reopen read-only.** Ctrl+W on a clean window writes nothing. The file is then made read-only. `kicad_diagram_open` reopens the level with the same source token, every block from its stored placement, the stored frame, Rail feed's stored route, Power's computed path and no dormant entries. Save stays disabled after a move, and the status bar explains why. Ctrl+S reports `diagram_file_read_only` and sends nothing. The file stays byte-identical, Decline restores the saved level, and the file mode is restored afterwards.

### Findings after repair

The first comparison rated the compact view and the connection paths P3. The review showed that both hid or confused primary content, so they were P2; both are repaired above. What remains is P3 polish:

- **P3, dark palette.** In dark mode the palette panel is lighter than its dark buttons, so the buttons look inset rather than floating. It is readable, and the active tool is still clear.
- **P3, highlight contrast.** In the dark theme, a selected connection's dark-blue highlight and a selected block's resize handles stand out less against the dark grey than they do in the light theme. Both are still visible.
- **P3, shared last leg.** Rail feed and Power still share their last short leg into the CPU, because rule F2 gives both unresolved ends one anchor on the CPU edge. Each connection is otherwise drawn, captioned and selectable on its own. Ending a connection on a port gives it its own anchor.
- **P3, corner start.** Power starts at the PSU's bottom-right corner. Rule F2 places an unresolved end on a block with one port at `(k+1)·h/max(2, n+1)` with k = n, which is the corner for one port.
- **P3, compact text.** At the compact window's scale (about 0.4), block text keeps its full size. "v1" is clipped inside the smaller blocks, and the two connection captions sit close together.

None of these blocks use of the tools. They are left for a later styling pass rather than changing the contract's layout rules here.

### Accepted differences from the sketches

- **Icons.** Every button in every capture shows "?", including the older Back/Up/Undo/Redo/Fit/Note buttons. The virtual test display has no icon theme. The source uses KiCad's own bitmaps (cursor, add_rectangle, add_line, add_hierar_pin, trash, undo). This is a limit of the test display, not a design choice.
- **Active tool styling.** The active tool uses the native pressed toggle rather than the sketch's blue fill. The strip and the palette use the same styling.
- **Other sketch elements.** The dotted grid, coloured links and the Agent console button are not part of A1. Agent console is still not integrated and stays hidden.
- **Save button.** Save remains a native button rather than the sketch's filled blue button. This matches the accepted difference already recorded in `design-qa.md`.
- **Level frame.** The sketches show no frame. The frame appears once a port is placed on the level boundary, because a stored boundary port needs an edge to sit on (contract rule F3).
- **Inspector.** The inspector shows fewer boxes than the sketches, by decision n98a3f3c41084f0ed.

### Limits of this review

The formal `product-design:audit` and `design-qa` skills are not available in this session. This comparison opened the sketches and captures side by side; it is not a formal audit, so the recorded final result stays blocked. The review covers only the drawing journey's states. Earlier editor states are covered in `design-qa.md`. The PSU/CPU fixture canvas journey (`VerifyPsuCpuDiagramCanvas`, category NativeDiagramCanvas) still has no graph that runs it, so it is not covered here.

Each themed native session opens two projects. The session-wide checks run only in the first project of each session: the retired flat-editor tools, diagram creation over MCP, the schema-2 tool checks and this drawing journey. At the time of the A1 commits the second project repeated the whole core editor journey to prove that instances stay isolated, and the native-UI check took 449.8 s to 567 s of its 600 s limit, depending on host load. Before the first commit, the second project also ran the first three checks. The A4 item changed this split; see "Time limit" in the A4 section.

### Checklist

- [x] Both chosen sketches identified by path, sketch id and hash.
- [x] Implementation identified by run, source hash, manifest and instance.
- [x] Light and dark captures for every drawing step. Compact and palette-hidden states are captured in both themes.
- [x] Every new control pressed through the real window: five strip buttons, six palette buttons, the B, P and C keys, View → Drawing palette and Add requirement….
- [x] Cancel and error paths: Escape on the caption and the tool, blank caption (also on Save and when focus leaves), starting on empty space, ending on the starting block, a click on empty space while connecting, the used-port question with its keep and remove answers, a read-only file.
- [x] Save/Decline, a save that rebases a layout-only change, save and reopen from the stored layout.
- [x] P2 findings repaired and asserted in the journey (first comparison and the review's connection and compact-window findings).
- [ ] Formal design audit (skills unavailable, so the final result stays blocked).
- [ ] PSU/CPU fixture canvas journey (needs a graph that runs NativeDiagramCanvas).

## Round A4 — component choices on blocks (item a4-block-chips)

### What is compared

A block can now show what has been decided about the part it will become. Each of its seven independent facets (purpose, type, manufacturer, family, model, orderable part and package; decision na7aa99408263431e) is unspecified, unknown with a reason, a candidate list or one chosen value, with a strength. On the canvas a block shows its caption and one chip per chosen or candidate facet, and a Review facets link. The inspector's facet overview lists only the facets that have a value. One facet's detail edits its state, value or reason, and strength; the facet can return to unknown or be cleared. A caption-only block shows no chips, and a chosen name never adds ports, a component or a footprint.

The owner chose option 3 of Round A4 (decision n0b2a908b00e78823, "shows the evolution of the block"): `/mnt/build-storage/codex/kicad/design-rounds/round-a/A4/option-3.png` (Coordinator sketch `s9be57c366ffe2d65`, SHA-256 `a405914aef7b5b64216e981cbdc43340c3d2241aebdac40dce2e8e8d8dfb5733`, generated with the model recorded in its `generation-record.verified.json`). The same decision applies the grow-with-definition rule (n98a3f3c41084f0ed) to the sketch: the sketch's Unspecified rows are left out.

The sketch is a 1536 × 1024 full-window scene of a PSU level with the LDO selected. The captures are real KiCad editor windows at 1536 × 1024 (and 1100 × 760 in the compact step) inside a 1600 × 1150 virtual display. They show the "Fixture board" level that the drawing journey drew: a PSU with a Rail port and a General requirement, and a caption-only CPU. The journey gives the PSU the sketch's LDO choices: type chosen ("linear regulator"), package candidate ("SOT-23-5") and manufacturer unknown ("No preference recorded."). Diagram content therefore differs from the sketch on purpose; the comparison covers the block chips, the Review facets link, the facet overview, the facet detail and the Save/Decline area. No browser, CSS or device scaling is involved.

### Evidence

The rendered journey is `VerifyBlockChoices` in `automation/tests/KiCad.Automation.Tests/NativeRecursiveEditorJourney.cs`. It runs in `NativeRecursiveEditor` for the light and dark themes, right after the drawing journey, on the level that journey saved. It drives the real window with pointer and keyboard input, chooses where to click from the editor's reported control rectangles and chip rectangles, reads the editor state after each step, and reads the saved file back through the model. The core editor journey also checks that the root's agent-recorded partial definition is listed by facet (purpose, type, model, package).

#### First comparison: run `t20260924T054756Z-477672`

All four development checks passed (native UI 524.3 s). The run's source differs from the final source only in the repairs below, this document and the design comparison; its status is "failed" only because the worktree changed while it ran. Hash-verified captures are materialized at `/mnt/build-storage/codex/kicad/evidence/block-chips-477672/` (native-UI manifest `7dfc05ccfcb998172cfd369f79d51c806ec2d84b1bc9734060f8bcfd1ce62cf4`; `editor-light` instance `0605e6e0-197d-4734-8408-a233d057043e`, `editor-dark` instance `62bf7f95-0d8c-40df-8b25-e0fde38ba8c9`). The sketch was opened together with the light and dark `detail`, `compact` and `reopened` captures. Three problems were found:

- **P2, copy.** The overview's label column clipped "Manufacturer" to "Manufactur…", so a facet's name was not readable.
- **P2, compact window.** At 1100 × 760 the PSU was too small for a chip, and nothing showed that it had choices; only its caption was drawn.
- **P3, strength row.** The three strength choices wrap "Requirement" onto a second line.

Earlier runs (`t20260924T042415Z-da6759`, `t20260924T043653Z-0a7e64`, `t20260924T045013Z-2329cb`, and `t20260924T050317Z-01ce0f`, which was cancelled by replacement) failed while the journey learned to drive the detail: the Add detail menu was pressed while the inspector was still re-laying out, and drop-down lists could not be driven reliably from the keyboard in this display (a list opened by a click lost its entry, and focus moved by Tab onto a list was not visible to the editor). The detail now uses one-click choices, and the journey presses a control only where two readings of its position agree. Their captures also showed two P2 problems with one cause, repaired before `477672`: the block's 8-pixel content margin was applied to the rectangle itself, so the resize handles (an A1 element) were drawn 8 pixels inside the corners where pressing them works, and only one chip row fitted, which turned the Package chip into "+1 more".

#### Repaired comparison: run `t20260924T060130Z-088f6e`

The repairs:

- The overview's label column fits the longest facet name, so every name is readable and every value starts at the same place.
- A block too small for a chip shows its choices' state marks (a check for chosen, a ring for a candidate) beside its caption, and the editor still reports the chips as hidden.
- The strength choices sit closer together. They still wrap in a narrow inspector or when its scroll bar shows (P3 below).

Both themed sessions ran the whole choice journey without a failure (captures up to `…-choices-reopened.png` in both themes). The native-UI check then reached its 600 s limit while the later field-history, simulation and PCB tests were still running: the light recursive-editor session took 296 s on the loaded host, against 229–245 s before this item. The captures are copied, with their SHA-256 list (`SHA256SUMS`, itself `6158dc62afbdb2e8e06b27c2d1237ea8b5d55b8455a886581ed44ad6b61c5788`), to `/mnt/build-storage/codex/kicad/evidence/block-chips-088f6e/` (`editor-light` instance `d5480b87-8c37-439c-a51e-af5ca4ac6861`, `editor-dark` instance `83b15db9-1583-475b-8e53-3dc8b47022ff`). The sketch was opened together with the light and dark `add-facet`, `detail`, `compact` and `reopened` captures.

This commit's gate run repeats the journey on the final source, which differs from `088f6e` only in this document and in the second project's shorter pass (see "Time limit").

Step-linked captures (the same step names exist for both themes):

| Step | Capture | What it shows |
|---|---|---|
| Add a first value | `…-choices-add-facet.png` | Add detail > Type opened the Type detail ready for a first chosen value: State (Chosen, Candidate, Unknown), Value with focus, Strength. The PSU still shows no chips. |
| Detail | `…-choices-detail.png` | The facet overview lists Type, Manufacturer and Package, with Manufacturer's detail open as Unknown with its reason. The PSU shows the chosen Type chip, the candidate Package chip and Review facets. The CPU is only its caption. |
| Saved | `…-choices-saved.png` | After Save the PSU shows the same two chips from its saved revision. |
| Compact | `…-choices-compact.png` | At 1100 × 760 the level is re-fitted. The smaller PSU has no room for a chip, so its two choices show as a check and a ring beside its caption; nothing is drawn clipped. |
| Reopened | `…-choices-reopened.png` | After close and reopen, the chips come back from the file, and Manufacturer's detail shows its saved reason. |

### Interaction coverage

For each theme the journey proves the following through the real window:

- **Caption-only.** A caption-only block has no chips and no Review facets link, and its inspector lists no facet.
- **Add detail.** The one quiet Add detail menu lists the hidden requirement boxes and then the facets without a value. Choosing Type opens its detail ready for a first chosen value. Escape cancels without a change. Typing "linear regulator" stores a chosen value, Enter keeps it and returns to the overview with focus on its row, and the PSU shows the chip "Type: linear regulator" and Review facets. Enter on the row opens the detail again, and Escape returns.
- **Candidate and strength.** Package takes "SOT-23-5" as a chosen value, then Preference, then Candidate: the value moves into the candidate list. Emptying the list shows "List the candidates, one per line.", keeps the last entry that could be stored, and Ctrl+S refuses with the same notice, sends nothing and writes nothing. Typing the candidate again clears the notice.
- **Unknown.** Manufacturer becomes Unknown: "Say why this is unknown." until a reason is typed. An unknown facet is listed but has no chip; chosen and candidate chips are told apart by their state.
- **Review facets.** With the CPU selected, the PSU's Review facets link selects the PSU and opens its first facet's detail.
- **Return to unknown and back.** Type returns to Unknown with a reason and its chip goes. Back and Enter reopen it; Chosen asks for a value ("Type the chosen value, or choose another state.") until "linear regulator" is typed again.
- **Clear.** Clear facet returns Package to unspecified: its row and chip go. Ctrl+Z brings it back with its strength.
- **Save.** Ctrl+S writes one revision of the PSU with exactly the chosen type, the unknown manufacturer and the candidate package (Preference). Its Rail port, General requirement and absent component bindings and physical allocation are unchanged, and the CPU still has no definition.
- **Decline.** A later strength change is discarded by Alt+D and the file stays byte-identical.
- **Compact window.** Every chip is either drawn inside the canvas or reported as hidden; the state marks that stand for hidden chips were checked in the captures.
- **Close and reopen.** Ctrl+W writes nothing; reopening shows the same chips and the same facets, and opening a facet's detail changes nothing.

Controls pressed through the real window: Add detail (three facets), the Type, Manufacturer and Package overview rows (click and Enter), Back to facet overview, the Chosen, Candidate and Unknown state choices, the Value, Candidates and Reason entries, the Preference and Requirement strength choices, Clear facet and the canvas Review facets link.

#### Time limit

The native-UI check has a 600 s limit, set in the parent-owned `.devcoordinator.toml`. It already took up to 567 s before this item, and the choice journey adds about 10 s per theme. On the loaded host the two recursive-editor sessions alone then took 552 s (`088f6e`: 296 s and 256 s) and 530 s (`t20260924T061606Z-01a656`: 246 s and 284 s), so the check reached its limit while the later field-history, simulation and PCB tests were still running, although every journey step had passed. The second project of each session now repeats the editor journey only up to the compact window: its own MCP attachment, file, window, observations, navigation, edits, saves, field history, comments, conflict dialog, implementation preview and compact window. The implementation management, whole-diagram history, agent tool and proposal steps after that do not depend on the instance and run once per session, in the first project. The parent is asked to raise the limit instead (seam request); the second project's full repeat can then return.

### Findings after repair

The P2 findings above are repaired. What remains is P3 polish:

- **P3, strength row.** In the inspector's default width the three strength choices wrap "Requirement" onto a second line, most visibly when the inspector's scroll bar shows. Nothing is clipped and every choice works.
- **P3, compact marks.** In the compact window the state marks sit close to the "Rail" port name inside the PSU; they do not overlap.
- **P3, open-row highlight.** The open facet's overview row is highlighted in the theme's selection colour; the sketch does not highlight it.

### Accepted differences from the sketch

- **Only defined facets.** The sketch lists all seven facets with Unspecified values. By decision n98a3f3c41084f0ed and the A4 decision's own note, only facets that have a value are listed, and a facet gets its first value through the one quiet Add detail action.
- **One add-detail action.** Add requirement… became Add detail…: one quiet action for a requirement box or a facet's first value, as decision n98a3f3c41084f0ed asks ("one quiet add-detail affordance").
- **State and strength choices.** The sketch shows drop-down lists. The detail shows the three states and the three strengths as one-click choices, which the standing UI rule prefers for small fixed sets ("Use one-click choices … for small option sets"). It also shows every state a facet can return to, including Unknown.
- **No inspector Review facets link.** The sketch repeats Review facets beside the overview heading. Each overview row already opens its facet, so the inspector does not repeat it; the canvas keeps the link.
- **Caption and link alignment.** Captions stay left-aligned as in the approved A1 drawing tools, and the Review facets link sits at the block's lower left instead of centred.
- **Unknown facets have no chip.** The sketch shows chips only for the chosen type and the candidate package, and the decision describes chips for "each decided or candidate choice". An unknown facet is listed in the overview only.
- **Version line.** A block with component choices shows its chips where a caption-only saved block shows "v1", as in the sketch. The inspector still shows its selected design version.
- **Icons.** Toolbar icons still show "?" on the test display (see the A1 section); the chip and row state marks are drawn by the editor and render in both themes.

### Limits of this review

The formal `product-design:audit` and `design-qa` skills are not available in this session, so the recorded final result stays blocked; this comparison opened the sketch and the captures side by side. In the compact window the chips do not fit a block, so the canvas shows state marks beside the caption there; the editor's observation reports those chips as hidden, and the marks themselves were checked only in the captures. Knowledge-class guidance, facet history and missing-library states named in ledger task peb041240f16fc59b are outside Round A4 option 3 and were not built here.

### Checklist

- [x] The chosen sketch identified by path, sketch id and hash.
- [x] Implementation identified by runs, manifest and instances; captures preserved with hashes.
- [x] Light and dark captures for every choice step, including the compact window.
- [x] Every new control pressed through the real window: Add detail (three facets), the overview rows (click and Enter), Back to facet overview, the three state choices, the Value, Candidates and Reason entries, two strength choices, Clear facet and the canvas Review facets link.
- [x] Cancel and error paths: Escape in a new facet, Escape and Back from a detail, an emptied candidate list, an unknown without a reason, a chosen state without a value, Save refused with a notice and nothing written.
- [x] Save, Decline, undo of a cleared facet, close and reopen.
- [x] P2 findings repaired and rechecked in the repaired captures (label column, compact marks, handles, two chip rows).
- [ ] Formal design audit (skills unavailable, so the final result stays blocked).
