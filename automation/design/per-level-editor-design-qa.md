# Per-level editor drawing tools — design QA

final result: blocked

The drawing tools work end to end in both themes and at compact size, and the reviewed captures follow the chosen sketches closely enough to use. The overall result stays blocked because the formal design audit (`product-design:audit` with `design-qa`) is not available in this session, so no formal audit has run (see "Limits of this review"). This comparison does not replace it, and the item cannot be handed off as finished until that audit runs or the owner accepts this result.

## What is compared

Item a1-drawing-tools lets a person draw a level of the system diagram in the per-level editor. They can add a block by typing its caption, connect blocks and ports, place ports on a block edge or on the level boundary, move and resize blocks, delete with undo and redo, and keep the drawn layout with the level when they save. The owner chose both A1 sketches together (decision n9f7cf92f32090daf):

- **Toolbar strip, option 1.** `/mnt/build-storage/codex/kicad/design-rounds/round-a/A1/option-1.png` (Coordinator sketch `sc23e8d0c5adbb401`, SHA-256 `99dcd9c60d36766be0a735e4de90eca078f4dcfacb27bbfa904056c9fd3fe3d0`). Select, Add block, Connect, Place port and Delete follow the existing toolbar commands.
- **Canvas-edge palette, option 2.** `/mnt/build-storage/codex/kicad/design-rounds/round-a/A1/option-2.png` (Coordinator sketch `s6d7f155fd468901a`, SHA-256 `228b866b188848dc385b1e534e83340d37da4acfa6d9df7efb0ea094c7940421`). The same tools plus Undo sit in a vertical palette at the canvas's left edge. View → Drawing palette hides and restores it.
- **Inspector.** Decision n98a3f3c41084f0ed says a new block or connection shows only its caption, Comments and Decline/Save. A requirement box appears once it has text, or when the person asks for it through one quiet "Add requirement…" action. The sketches still show all three boxes, and the decision takes precedence over them.

Both sketches are 1536 × 1024 full-window scenes of a finished PSU level. The captures are real KiCad editor windows: 1536 × 1024 normally and 1100 × 760 in the compact step, each inside a 1600 × 1150 virtual display. They show a new, empty "Fixture board" level that the journey draws from nothing. Diagram content therefore differs on purpose. The comparison covers the tools, their states, the hint, selection handles, ports, the inspector and the Save/Decline area. No browser, CSS or device scaling is involved. Only the editor region is compared: the black display area and, in the compact capture, the KiCad project window behind the editor are not part of the design.

## Evidence

The rendered journey is `VerifyDrawingTools` in `automation/tests/KiCad.Automation.Tests/NativeRecursiveEditorJourney.cs`. It runs inside `NativeRecursiveEditor` for the light and dark themes. It drives the real window with pointer and keyboard input, and it chooses where to click from the editor's reported canvas geometry and control rectangles. After each step it reads the editor state, and it reads the saved file back through the model.

### First comparison: run `t20260924T015015Z-eb2643`

All four development checks passed (native UI 462 s). Hash-verified captures are materialized at `/mnt/build-storage/codex/kicad/evidence/drawing-tools-eb2643/` (native-UI manifest `a25023abb675884d69c7cb5cf9d626578a1876ede83d9bacfc968f6f28a0fd34`). The two sketches and the light captures `…-drawing-selected-handles.png`, `…-drawing-inspector-requirement.png` and `…-drawing-remove-port-question.png` were opened together. Four problems were found:

- **P2, layout.** The level frame (the dashed boundary) stayed the size it had when the first boundary port was placed. Moving the CPU afterwards left it half outside the level.
- **P2, copy.** The remove-port question read "1 connections on this level use this port."
- **P3, labels.** A block port's label ("Rail") was drawn outside the block, where it collided with the caption of a connection starting at the same edge ("Power").
- **P3, palette outline.** The palette had a hard black border. The sketch shows a quiet floating card.

### Repaired comparison: run `t20260924T020158Z-3285da`

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

### Review repairs: runs `t20260924T025617Z-dafee0`, `t20260924T031011Z-ea0364` and `t20260924T032229Z-f4e31f`

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

### Interaction coverage

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

## Findings after repair

The first comparison rated the compact view and the connection paths P3. The review showed that both hid or confused primary content, so they were P2; both are repaired above. What remains is P3 polish:

- **P3, dark palette.** In dark mode the palette panel is lighter than its dark buttons, so the buttons look inset rather than floating. It is readable, and the active tool is still clear.
- **P3, highlight contrast.** In the dark theme, a selected connection's dark-blue highlight and a selected block's resize handles stand out less against the dark grey than they do in the light theme. Both are still visible.
- **P3, shared last leg.** Rail feed and Power still share their last short leg into the CPU, because rule F2 gives both unresolved ends one anchor on the CPU edge. Each connection is otherwise drawn, captioned and selectable on its own. Ending a connection on a port gives it its own anchor.
- **P3, corner start.** Power starts at the PSU's bottom-right corner. Rule F2 places an unresolved end on a block with one port at `(k+1)·h/max(2, n+1)` with k = n, which is the corner for one port.
- **P3, compact text.** At the compact window's scale (about 0.4), block text keeps its full size. "v1" is clipped inside the smaller blocks, and the two connection captions sit close together.

None of these blocks use of the tools. They are left for a later styling pass rather than changing the contract's layout rules here.

## Accepted differences from the sketches

- **Icons.** Every button in every capture shows "?", including the older Back/Up/Undo/Redo/Fit/Note buttons. The virtual test display has no icon theme. The source uses KiCad's own bitmaps (cursor, add_rectangle, add_line, add_hierar_pin, trash, undo). This is a limit of the test display, not a design choice.
- **Active tool styling.** The active tool uses the native pressed toggle rather than the sketch's blue fill. The strip and the palette use the same styling.
- **Other sketch elements.** The dotted grid, coloured links and the Agent console button are not part of A1. Agent console is still not integrated and stays hidden.
- **Save button.** Save remains a native button rather than the sketch's filled blue button. This matches the accepted difference already recorded in `design-qa.md`.
- **Level frame.** The sketches show no frame. The frame appears once a port is placed on the level boundary, because a stored boundary port needs an edge to sit on (contract rule F3).
- **Inspector.** The inspector shows fewer boxes than the sketches, by decision n98a3f3c41084f0ed.

## Limits of this review

The formal `product-design:audit` and `design-qa` skills are not available in this session. This comparison opened the sketches and captures side by side; it is not a formal audit, so the recorded final result stays blocked. The review covers only the drawing journey's states. Earlier editor states are covered in `design-qa.md`. The PSU/CPU fixture canvas journey (`VerifyPsuCpuDiagramCanvas`, category NativeDiagramCanvas) still has no graph that runs it, so it is not covered here.

Each themed native session opens two projects. The session-wide checks run only in the first project of each session: the retired flat-editor tools, diagram creation over MCP, the schema-2 tool checks and this drawing journey. The second project repeats the core editor journey to prove that instances stay isolated. This keeps the native-UI check inside its 600 s limit: it took 449.8 s to 567 s, depending on host load. Before the first commit, the second project also ran the first three checks.

## Checklist

- [x] Both chosen sketches identified by path, sketch id and hash.
- [x] Implementation identified by run, source hash, manifest and instance.
- [x] Light and dark captures for every drawing step. Compact and palette-hidden states are captured in both themes.
- [x] Every new control pressed through the real window: five strip buttons, six palette buttons, the B, P and C keys, View → Drawing palette and Add requirement….
- [x] Cancel and error paths: Escape on the caption and the tool, blank caption (also on Save and when focus leaves), starting on empty space, ending on the starting block, a click on empty space while connecting, the used-port question with its keep and remove answers, a read-only file.
- [x] Save/Decline, a save that rebases a layout-only change, save and reopen from the stored layout.
- [x] P2 findings repaired and asserted in the journey (first comparison and the review's connection and compact-window findings).
- [ ] Formal design audit (skills unavailable, so the final result stays blocked).
- [ ] PSU/CPU fixture canvas journey (needs a graph that runs NativeDiagramCanvas).
