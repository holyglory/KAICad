# Level-diagram walkthrough — image prompts

Generated using the built-in ImageGen tool, not the CLI. These are illustrative UI studies, not runtime or circuit-validation evidence. The final scenes form one walkthrough, not competing options.

Final scene files: `01-system-v5.png`, `02-psu-v3.png`, `03-cpu-v2.png` in this directory. CPU and PSU targeted corrections supersede their initial generations. The original native-style reference is the previously supplied KiCad mock; it is not approval of its rejected interaction model.

## 1. levelWalkthroughRootPrompt

```text
Use case: ui-mockup. High-fidelity native desktop KAICad editor walkthrough, one application window per image, 1536 x 1024 pixels. This is a DESIGN STUDY, not a verified circuit. Every level is ITS OWN connected diagram of immediate child units. Opening a block REPLACES the main canvas with that block's diagram. NEVER show descendants embedded within parent boxes, nested cards, a hierarchy tree replacing a diagram, a dashboard, or side-by-side level montage.

Style: native KiCad-inspired light desktop editor. White/offwhite canvas, subtle gray grid, charcoal text, restrained blue selection, orthogonal functional-interface lines. Native 14–16 px readable UI text, clean small square/rounded-corner engineering blocks (not SaaS cards). Thin separators, no decorative imagery or gradients. Keep all controls and contents visible within frame. No code, XML, voltage/current entry, fabricated numerical specs, statistics or active AI progress. Same exact menu/toolbar/sidebar positions across the walkthrough.

Frame geometry: thin native title bar "KAICad — Controller device", menus File Edit View Tools Help; below compact toolbar with back, up, undo, redo, fit and Note (at most six groups). Beneath toolbar a narrow breadcrumb/current-diagram row spanning canvas, with exact local revision and "History". No permanent hierarchy tree; tiny hierarchy-toggle optional. Canvas about1160px wide, right contextual inspector about360px. Primary content is the current connected diagram with generous space.

Inspector: contextual object title, small selected revision text and implementation/history access; three short editable text fields with headings EXACTLY "General requirements", "Schematic requirements", "Routing requirements". Put a small "History" action beside EACH heading. Current words can be rewritten by AI; no immutable original-brief banner. Three fields are concise, no tall numeric form. Below fields context notes/comments and one primary agent-neutral "Refine [target]…" action, no second chat interface or provider logo. A paperclip can indicate attachments. Keep the agent chosen externally; no claim of automatic execution.

Every unit can have versions and may be partially specified; use readable local version labels and type/part undecided text only where relevant. Free-space notes use a small yellow annotation distinct from diagram blocks. A clicked block opens its own diagram, not expanded contents. System root revision v5 pins PSU revision v3 and CPU unit revision v2; keep these identities/values consistent.

Abstract interface contract for this entire three-screen walkthrough:
- System includes only PSU and CPU unit as peer internal units. PSU sends Power to CPU unit; Telemetry is a separate bidirectional interface between them.
- PSU boundary has external DC input, Power output (an aggregate of multiple rails), Telemetry. Its diagram explains power conversion and telemetry internals.
- CPU unit boundary has Power input and Telemetry. Its diagram explains processor/memory and internal links.
Lines are functional interface abstractions. Different rails must NEVER be electrically shorted or drawn joining their converter outputs. Measurement and telemetry lines must NOT look like high-current power paths. No inferred board boundaries: the device could occupy one PCB or several. Avoid drawing real numbered component pins without actual supplied parts.
SCREEN: ROOT DIAGRAM.
Input image: native visual-style reference ONLY, not a layout or diagram to reproduce. Do not copy its flat MCU/Memory/Power trio or repetitive toolbar.

Breadcrumb: "System" with "Diagram v5" and History. Canvas shows TWO peer blocks: PSU on left centered around(330,410), CPU unit on right around(830,410). Each about210x180 pixels, readable and not huge. PSU is selected with blue outline; its caption inside: "PSU", then "v3 · functional unit", then small "Open diagram". CPU caption: "CPU unit", "v2 · functional unit", "Open diagram". NO internal objects, no memory or rails shown inside either block.

Left canvas edge: small boundary port "DC input" with line leading into PSU. PSU right face has distinct Power and Telemetry ports. A solid muted-red horizontal arrow labelled "Power" runs from PSU Power port to CPU unit Power input. A separate blue bidirectional line below it labelled "Telemetry" joins PSU Telemetry port to CPU unit Telemetry port. Keep lines apart and label clear. Do not connect Power and Telemetry together.
A free-space note lower left reads "Keep PSU replaceable as a unit." Preserve plenty of empty diagram space.

Right inspector selected PSU, "Selected design: v3". General requirements: "Supply CPU power and report rail status." Schematic requirements: "Separate power conversion and telemetry." Routing requirements: "Keep high-current paths away from sensing." Each heading has History. Small Comments area: "Explore a quieter supply." Bottom primary button "Refine PSU…" with paperclip beside it. Small contextual "Open diagram" link near selected object's title is okay; no Open schematic promoted at this abstraction. Overall screen should clearly invite opening PSU to see a DIFFERENT diagram. This is the single root state in a coherent walkthrough, not a competing design option.
```

## 2. levelWalkthroughPsuPrompt

```text
Use case: ui-mockup. High-fidelity native desktop KAICad editor walkthrough, one application window per image, 1536 x 1024 pixels. This is a DESIGN STUDY, not a verified circuit. Every level is ITS OWN connected diagram of immediate child units. Opening a block REPLACES the main canvas with that block's diagram. NEVER show descendants embedded within parent boxes, nested cards, a hierarchy tree replacing a diagram, a dashboard, or side-by-side level montage.

Style: native KiCad-inspired light desktop editor. White/offwhite canvas, subtle gray grid, charcoal text, restrained blue selection, orthogonal functional-interface lines. Native 14–16 px readable UI text, clean small square/rounded-corner engineering blocks (not SaaS cards). Thin separators, no decorative imagery or gradients. Keep all controls and contents visible within frame. No code, XML, voltage/current entry, fabricated numerical specs, statistics or active AI progress. Same exact menu/toolbar/sidebar positions across the walkthrough.

Frame geometry: thin native title bar "KAICad — Controller device", menus File Edit View Tools Help; below compact toolbar with back, up, undo, redo, fit and Note (at most six groups). Beneath toolbar a narrow breadcrumb/current-diagram row spanning canvas, with exact local revision and "History". No permanent hierarchy tree; tiny hierarchy-toggle optional. Canvas about1160px wide, right contextual inspector about360px. Primary content is the current connected diagram with generous space.

Inspector: contextual object title, small selected revision text and implementation/history access; three short editable text fields with headings EXACTLY "General requirements", "Schematic requirements", "Routing requirements". Put a small "History" action beside EACH heading. Current words can be rewritten by AI; no immutable original-brief banner. Three fields are concise, no tall numeric form. Below fields context notes/comments and one primary agent-neutral "Refine [target]…" action, no second chat interface or provider logo. A paperclip can indicate attachments. Keep the agent chosen externally; no claim of automatic execution.

Every unit can have versions and may be partially specified; use readable local version labels and type/part undecided text only where relevant. Free-space notes use a small yellow annotation distinct from diagram blocks. A clicked block opens its own diagram, not expanded contents. System root revision v5 pins PSU revision v3 and CPU unit revision v2; keep these identities/values consistent.

Abstract interface contract for this entire three-screen walkthrough:
- System includes only PSU and CPU unit as peer internal units. PSU sends Power to CPU unit; Telemetry is a separate bidirectional interface between them.
- PSU boundary has external DC input, Power output (an aggregate of multiple rails), Telemetry. Its diagram explains power conversion and telemetry internals.
- CPU unit boundary has Power input and Telemetry. Its diagram explains processor/memory and internal links.
Lines are functional interface abstractions. Different rails must NEVER be electrically shorted or drawn joining their converter outputs. Measurement and telemetry lines must NOT look like high-current power paths. No inferred board boundaries: the device could occupy one PCB or several. Avoid drawing real numbered component pins without actual supplied parts.
SCREEN: OPEN THE PSU DIAGRAM.
Input image is the ROOT screen from this same walkthrough. Preserve its exact native window, theme, dimensions, toolbar, grid, sidebar width, typography and right inspector fields. Change ONLY the breadcrumb/current local diagram, canvas contents and small contextual note. The new canvas is the complete PSU internal diagram; NEVER retain the root PSU or CPU boxes behind/around the child graph.

Breadcrumb must read "System > PSU" and "Diagram v3" with History. "System" is the back-to-root link; Back/Up available. Inspector still describes PSU selected design v3, same3 fields: General "Supply CPU power and report rail status."; Schematic "Separate power conversion and telemetry."; Routing "Keep high-current paths away from sensing." Comments "Explore a quieter supply." Refine PSU button as before.

CURRENT PSU DIAGRAM: direct peer internal blocks and explicit boundary ports. Canvas left boundary "DC input" branches to two separate DC-DC blocks arranged upper/middle left: "DC-DC A" and "DC-DC B", each "Part undecided". Upper DC-DC A output runs right as rail A to an upper right boundary port "Power / Rail A". Middle DC-DC B output runs to one peer "LDO" block ("Type chosen") then right as rail B to a second boundary port "Power / Rail B". These two output rails remain distinct, NEVER connected together. A light bracket by their boundary labels names their aggregate parent interface "Power → CPU unit"; it is a nonconducting brace, not a joining wire.

Below these conversion paths are peer blocks "Telemetry ADC" and "Telemetry MCU". Draw separate THIN DASHED sensing relationships from the two rails into distinct ADC input points, clearly labelled "Sense" (not power paths). ADC connects to Telemetry MCU by one thin blue line labelled "Measurements". Telemetry MCU connects bidirectionally to the right boundary port "Telemetry ↔ CPU unit". Telemetry output is separate from both power ports. NO circuit numeric values, no pretend complete schematics, no extra outputs, do not route power through the ADC/MCU. Functional diagram links only.

Small free-space yellow note lower left "Keep sensing away from switching nodes." Each internal block can have compact v1 if it fits, but don't overload. Include external ports at canvas edge to illustrate how these internals realize PSU's parent-level Power and Telemetry. Main appearance is a real connected diagram at this level, not a nested-container chart.
```

## 3. levelWalkthroughCpuPrompt

```text
Use case: ui-mockup. High-fidelity native desktop KAICad editor walkthrough, one application window per image, 1536 x 1024 pixels. This is a DESIGN STUDY, not a verified circuit. Every level is ITS OWN connected diagram of immediate child units. Opening a block REPLACES the main canvas with that block's diagram. NEVER show descendants embedded within parent boxes, nested cards, a hierarchy tree replacing a diagram, a dashboard, or side-by-side level montage.

Style: native KiCad-inspired light desktop editor. White/offwhite canvas, subtle gray grid, charcoal text, restrained blue selection, orthogonal functional-interface lines. Native 14–16 px readable UI text, clean small square/rounded-corner engineering blocks (not SaaS cards). Thin separators, no decorative imagery or gradients. Keep all controls and contents visible within frame. No code, XML, voltage/current entry, fabricated numerical specs, statistics or active AI progress. Same exact menu/toolbar/sidebar positions across the walkthrough.

Frame geometry: thin native title bar "KAICad — Controller device", menus File Edit View Tools Help; below compact toolbar with back, up, undo, redo, fit and Note (at most six groups). Beneath toolbar a narrow breadcrumb/current-diagram row spanning canvas, with exact local revision and "History". No permanent hierarchy tree; tiny hierarchy-toggle optional. Canvas about1160px wide, right contextual inspector about360px. Primary content is the current connected diagram with generous space.

Inspector: contextual object title, small selected revision text and implementation/history access; three short editable text fields with headings EXACTLY "General requirements", "Schematic requirements", "Routing requirements". Put a small "History" action beside EACH heading. Current words can be rewritten by AI; no immutable original-brief banner. Three fields are concise, no tall numeric form. Below fields context notes/comments and one primary agent-neutral "Refine [target]…" action, no second chat interface or provider logo. A paperclip can indicate attachments. Keep the agent chosen externally; no claim of automatic execution.

Every unit can have versions and may be partially specified; use readable local version labels and type/part undecided text only where relevant. Free-space notes use a small yellow annotation distinct from diagram blocks. A clicked block opens its own diagram, not expanded contents. System root revision v5 pins PSU revision v3 and CPU unit revision v2; keep these identities/values consistent.

Abstract interface contract for this entire three-screen walkthrough:
- System includes only PSU and CPU unit as peer internal units. PSU sends Power to CPU unit; Telemetry is a separate bidirectional interface between them.
- PSU boundary has external DC input, Power output (an aggregate of multiple rails), Telemetry. Its diagram explains power conversion and telemetry internals.
- CPU unit boundary has Power input and Telemetry. Its diagram explains processor/memory and internal links.
Lines are functional interface abstractions. Different rails must NEVER be electrically shorted or drawn joining their converter outputs. Measurement and telemetry lines must NOT look like high-current power paths. No inferred board boundaries: the device could occupy one PCB or several. Avoid drawing real numbered component pins without actual supplied parts.
SCREEN: RETURN TO SYSTEM, THEN OPEN CPU UNIT.
Input image1 is the ROOT screen and image2 is the PSU internal screen from this same walkthrough. Preserve their exact window chrome, theme, canvas grid, toolbar, sidebar geometry and typography. This screen follows returning to System and opening the OTHER peer, CPU unit. Replace the whole canvas with CPU's internals. No PSU/converter objects or root blocks remain visible as nested containers.

Breadcrumb "System > CPU unit" and "Diagram v2" with History. Back/Up return to root. The root's CPU unit selected design was v2; preserve it here.

CPU LOCAL DIAGRAM: left boundary input aggregate "Power ← PSU" shown as a nonconducting brace grouping TWO distinct ports "Power / Rail A" and "Power / Rail B". Rail A muted-red line goes independently to a peer block "Processor", caption "v1 · family undecided". Rail B muted-red line goes independently to a peer "Memory", caption "v1 · type under review". Power lines NEVER join; assignments are illustrative functional connections, no electrical pin numbers or voltage data.
Arrange Processor center-left and Memory center-right with a separate BLUE bidirectional abstract connection between them labelled "Memory interface". This data connection is selected with restrained blue emphasis and open endpoint markers to indicate assignments remain unresolved. Do not draw the connection as a third component box. Its label may add tiny "Protocol undecided". No claim that a fixed pair supports multiple arbitrary protocols.

A left lower boundary port "Telemetry ↔ PSU" connects to Processor through a separate bidirectional blue path labelled "Supply status / control", not via Memory and not through power rail lines. This is the SAME external Telemetry interface shown at root and at the PSU output.
One free-space note below diagram says "Compare memory-interface implementations." A small comment cue attached to Memory interface says "Keep pin choices open for placement."

RIGHT INSPECTOR selected "Memory interface", subline "Connection · v1". Two compact endpoint summaries: Processor — compatible endpoint unresolved; Memory — endpoint unresolved. Then General requirements "Provide memory access; choose a compatible interface.", Schematic requirements "Keep interface signals grouped and readable.", Routing requirements "Leave pin assignment open until placement." History next to every heading; Comments text "Keep pin choices open for placement." Bottom primary action "Refine CPU unit…" (scope of new returned block revision), same paperclip. No provider branding, numeric form, nested drawings, new UI style or giant title. Make the whole walkthrough clearly root peer-units -> one child's diagram -> other child's diagram.
```

## 4. levelWalkthroughCpuCorrectionPrompt

```text
Use case: precise-object-edit. Edit this 1536x1024 KAICad CPU-unit mockup. Preserve the ENTIRE native UI, all text, typography, positions, blocks, selected Memory interface, note and sidebar exactly. Fix ONLY the red lower Power / Rail B connection. It currently wrongly ends at the lower-left side of Processor. Remove that connection from Processor. From the existing Rail B boundary port at (162,555), run its red path down to y610, right along y610 to x974, then up to the BOTTOM EDGE of Memory (near974,560) with an arrow pointing into Memory. The route must pass BELOW Processor and Memory, not across their bodies. Processor remains fed by the separate upper Rail A line; Memory is fed only by Rail B. Keep Rail A and Rail B distinct, never joining their output nets. Preserve the blue telemetry path and Memory-interface line exactly. No new labels, blocks, symbols, invented specs or changes elsewhere. The final result is still a UI design study.
```

## 5. levelWalkthroughPsuCorrectionPrompt

```text
Use case: precise-object-edit. Edit this 1536x1024 KAICad PSU internal-diagram mockup. Preserve all application chrome, typography, blocks, sidebar fields, notes, red power paths and blue ADC-to-MCU/Telemetry interfaces exactly. Correct ONLY the sensing paths and one redundant navigation link.
1. Remove the vertical blue dashed segment between the bottom of DC-DC A and the top of DC-DC B. There must be NO sensing line linking those two converter blocks.
2. Remove the blue dashed sensing segment below DC-DC B that goes to the ADC's left input.
3. Replace those deleted segments by one THIN GRAY DASHED measurement path that starts on RED Rail A AFTER the output of DC-DC A, at approximately(490,325). From this tap at490,325 drop down along x490 through the empty column BETWEEN the converter blocks and the LDO (crossing the red DC-DC B-to-LDO path with NO junction dot). Continue to y600, then go left to x420, then down into the ADC's LEFT top input around(420,680). Label that dashed path "Rail A sense".
4. Keep the LDO-to-ADC second sensing path but make it THIN GRAY DASHED and label "Rail B sense". It ends at a separate ADC input. Do not join the two sensing paths to each other. These are functional measurement relationships, not copper traces.
5. The inspector already shows the current open PSU, so replace its redundant top-right "Open diagram" text with "System" as a parent navigation link. Do not change anything else.
No new blocks, loops or numerical specs. Keep power Rail A and Rail B completely separate. All text and panels must remain readable and uncropped.
```
