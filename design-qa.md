# Recursive structural editor — design QA

final result: blocked

## Active comparison: requirement field history

The approved target is `/home/holyglory/kicad/automation/design/history-and-conflicts/02-field-history.png` (1536 × 1024 full scene, with the Routing requirements history dialog). The approved recursive System/PSU/CPU diagrams supersede the earlier option-1 layout retained below. This is native desktop UI; CSS viewport and browser density do not apply.

The first component captures are the hash-verified `native-dialog` artifacts from `t20260919T225954Z-d035cc`, materialized at `/mnt/build-storage/codex/kicad/evidence/field-history-d035cc/`. The source, `light/02-earlier-text.png`, and `dark/02-earlier-text.png` were opened together in one comparison input. Both implementation images are 426 × 330. The fixture has no resolvable source instruction, so no source-opening control is enabled. This is a component test, not the complete editor journey or its background canvas.

- P1, observation correctness: `01-current.png` and `02-earlier-text.png` have identical hashes and show v3 selected. Native control-state assertions reached v2, but capture ran before the next GTK paint. These images do not prove the intended historical state. Wait for a completed native frame and reject unchanged captures after this visible change.
- P2, sizing and typography: the dialog opens at 426 × 330 instead of its intended 740 × 520 client area. KiCad's final sizer setup fits the dialog after its preferred size is assigned. Apply preferred sizing afterward, keep a usable compact minimum, and match the reference's readable text hierarchy using the native font family.
- P1, integration: the dialog component is not yet connected to the recursive editor's History actions. Save/Decline, conflict recovery, navigation and source-document opening still require the full rendered editor journey. Passing component checks cannot close that gap.

Fidelity review: typography and layout are blocked by the incorrect initial size; colors follow native light/dark themes but need review after a valid capture; there are no decorative bitmap assets to generate; copy uses the approved History/Close/Use-text-in-draft actions. Focused comparison of the revision list, comparison text and footer will follow a matching-state capture. No visual pass is claimed from the stale frames.

Interaction evidence: the compiled model supplied revision-bound data to a real native dialog under a virtual display. Keyboard selection, Escape, restore, reopen, compact controls and cross-scope content isolation passed in both themes. The missing rendering checkpoint limits what those screenshots establish. The sealed run remains unchanged.

Next comparison: inspect the repaired 740 × 520 and compact captures, confirm the selected row and text are v2 while saved text remains v3, compare both themes, then connect the component to the editor and verify the complete journey.

### Second comparison: corrected native window captures

Run `t20260919T232148Z-1ddc15` passes the focused managed checks, native compilation and both themed GUI journeys. Hash-verified captures are at `/mnt/build-storage/codex/kicad/evidence/field-history-1ddc15/`. Source, light/dark `02-earlier-text.png` and light `03-compact.png` were opened in the same comparison input. The normal captures are 740 × 520; compact is 590 × 440. These are native window pixels at the test display's normal density, with no browser/CSS scaling. The source is a full 1536 × 1024 scene; compare its modal region, not the unimplemented background editor.

The capture now reads the actual native window after a GTK frame checkpoint. Current and earlier-state images have distinct hashes and show the correct row, heading and text. The original screen-DC captures remain sealed as failed visual evidence. Preferred sizing is applied after KiCad's sizer setup, and the native font hierarchy is larger. The observation mismatch and undersized initial dialog are repaired.

Typography is readable in both themes; the two comparison fields and footer remain visible at compact size. Colors follow the native theme with visible focus and selection. No bitmap assets are approximated; all content is native controls. Copy distinguishes selected text from saved text and restoration from saving. The fixture's absent source provider explains the absent source action; its real editor integration is still unverified. The revision list is denser and the primary button less visually emphasized than the mock; refine those with the editor integration rather than claiming full visual parity now.

The focused regions reviewed are the revision list, selected/saved text and footer actions. Component success does not qualify the editor route, source opening, Save/Decline, conflicts or a full system-level diagram journey. The overall result remains blocked on those concrete integration and visual gaps.

## Historical comparison — superseded option 1

The following records describe the earlier native editor and are retained as historical evidence. They are not the approved visual target for the recursive editor.

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

3. Matching-state reference comparison of the passing `t20260919T163307Z-f87328` capture: `/mnt/build-storage/codex/kicad/evidence/structural-option1-f87328/editor-evidence/aa190eab-92b7-451f-9847-670f50ca1eec-structural-initial.png`. Both 1487 × 1058 images opened in one comparison input. MCU selection now agrees between canvas and hierarchy; automatic fit scale and larger block labels retain the selected composition. Native font/icon differences are expected platform rendering. P2: the inspector remains less compact than the reference; narrow-height and dark-theme checks are still missing. Saved text-property editing and optional numeric fields are new isolated source changes and have no rendered qualification yet. Keep the result blocked.

## Next checks

Capture the revised native UI at the source dimensions and state, compare both images together, then review focused inspector and canvas regions. Keep the result blocked until the actionable findings and rendered interaction gaps are resolved.
