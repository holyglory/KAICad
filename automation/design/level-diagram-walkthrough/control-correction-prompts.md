# Approved walkthrough — Save/Decline correction

Method: built-in ImageGen, scoped image edits. The user approved the System/PSU/CPU navigation/layout and requested that editor actions be Save/Decline, with optional agent integration located globally.

Current image files:

- `01-system-v5-edit-controls.png`
- `02-psu-v3-edit-controls.png`
- `03-cpu-v2-edit-controls.png`

The original three images remain unchanged as design history. Each was supplied separately as the corresponding edit target. The same prompt below was used for each; all three outputs were visually inspected in the generation results. Diagram content, revision labels and requirement fields remain as in the approved layout. This is visual design, not working code or electrical validation.

The global Agent console control is a proposed optional integration location. It is not an implemented or verified launcher. The native editor's Save and Decline must never submit an AI refinement request.

## Exact prompt

```text
Use case: precise-object-edit. This is an APPROVED 1536x1024 KAICad native diagram-editor mockup. Preserve the entire image exactly except the two narrowly scoped UI-control changes below. Do not redesign, move or redraw the diagram, change wires, labels, notes, sidebar text, proportions, title bar, breadcrumbs, revision numbers, colors or fonts.

CHANGE ONE — editor draft actions:
At the bottom of the right inspector, remove BOTH the paperclip button and the large blue button whose label begins with "Refine". In the same footer area put two conventional native buttons side by side: secondary neutral-outline "Decline" on the left and primary blue "Save" on the right. Their labels are EXACTLY "Decline" and "Save", no unit names, no ellipses, no AI icon. They mean discard or save current abstract-diagram edits, NOT execute an AI request. Match the existing native button style and alignment with about12px between them. Keep existing Comments field above untouched. Do not add helper prose.

CHANGE TWO — proposed global entry:
In the TOP GLOBAL TOOLBAR, immediately after the existing "Note" tool, add a thin vertical separator and one compact tool labelled "Agent console" with a small native terminal-window icon. This is an entry to an existing agent application, not a chat panel or a per-block command. Keep it in the same toolbar row, with ordinary neutral native styling. Do not show provider names, a prompt composer, activity spinner, success badge or any AI response. No other toolbar changes.

The three General/Schematic/Routing fields and all History actions remain exactly as in the input. This is a UI design revision only; it must not imply functioning new integration or verified circuit behavior. Return one corrected full-window image, without comparison layout, numbering or explanatory border.
```
