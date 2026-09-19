# Structural editor — option 1 comparison

final result: blocked

## Evidence and state

Source visual: `/home/holyglory/.codex/generated_images/01a06e57-5f4c-7b23-8476-b42467c8c6c7/exec-14f611d6-b267-4632-a0ee-71436aff425f.png` (1487 × 1058).

First native capture: `/mnt/build-storage/codex/kicad/evidence/structural-option1-528c0d/editor-evidence/6b23a438-cbca-4ab8-83f8-8c9f3fbce479-structural-initial.png` (1600 × 1150 display, 1440 × 1024 editor).

Both images were opened in the same comparison input. The source has MCU selected; the first capture has no block selected. Compare region organization and geometry only, not inspector content or exact pixel spacing. This is a native desktop surface; CSS viewport and browser device scale do not apply. A matching 1487 × 1058 editor capture with MCU selected is required for the next comparison.

## Findings

- P1: Default power paths leave through the wrong block sides and overlap block edges. The mock connects power ports from below. Draw side-aware orthogonal paths and honor stored waypoints, labels and directions.
- P2: The diagram is anchored too high and block labels are much smaller. Center fit in the available canvas and increase label hierarchy without losing truncation.
- P2: The first inspector has no selected object and omits the populated starting state of the reference. Select the initial block while keeping empty designs valid.
- P2: Custom properties can be added but cannot yet be inspected or edited. Finish their real editing controls, including quantities, before calling the editor complete.
- P2: Narrow-window and dark-theme interaction evidence is missing. Check supported desktop sizes, text scaling, keyboard focus, panel scrolling and toolbar overflow.

## Required surfaces

- Typography: native system UI font is appropriate for the KiCad platform; block label size/hierarchy needs repair. Reference mock typography is not a request to replace platform fonts.
- Spacing/layout: correct Properties-above-Hierarchy organization; centered diagram, compact properties rows and toolbar density still need a matching-state comparison.
- Colors: block fills and link colors follow the chosen direction. Dark-theme foreground/background contrast is not yet qualified.
- Images/assets: blocks, ports and lines are actual editable engineering objects, not decorative assets. Existing KiCad icons are reused; no replacement raster illustration is needed.
- Copy: native editing labels describe real operations. Source is plain text until navigation is implemented; no fake link or enabled schematic-navigation action is presented.

## Interaction evidence

Run `t20260919T155428Z-528c0d` proves real native opening in two independent instances, not the full editing journey. The save assertion in that version did not wait for an acknowledged save attempt; it cannot prove completed publication. The next fixture uses completed-save counts and changed content, then exercises undo/redo, creation cancellation, dirty-close cancellation, conflict preservation and reopen.

## Comparison history

1. First comparison found the issues above. Pending source changes address path direction, label scale, selection, fit, resizing and panning. Compilation is not post-fix visual proof.
2. Matching 1487 × 1058 capture from `t20260919T161828Z-ddaf3b`: `/mnt/build-storage/codex/kicad/evidence/structural-option1-ddaf3b/editor-evidence/1223afdb-d9f2-45fa-a0f6-a9825e2cdfe0-structural-initial.png`. Opened together with the reference. Direction arrows, side-aware paths and populated MCU inspector now render. P2: hierarchy still highlights Memory when the canvas selects MCU; synchronize both selection surfaces. P2: fit enlarges blocks more than the reference; cap automatic fit scale while preserving manual zoom. Native font and icon rendering differ by platform as expected. Fields remain stacked rather than the compact reference rows. Two real instances renamed and saved the MCU, retained circuit data and completed unchanged saves, but immediate Redo after Undo timed out; this is a functional blocker, not a visual pass. Evidence remains sealed. Later checks must qualify cancellation/conflicts/reopen, dragging and resizing.

## Next checks

Capture the revised native UI at the source dimensions and state, compare both images together, then review focused inspector and canvas regions. Keep the result blocked until the actionable findings and rendered interaction gaps are resolved.
