# History and conflict mockup prompts

Method: built-in ImageGen. Source reference for each independent state: `../level-diagram-walkthrough/02-psu-v3-edit-controls.png`. These are states of the approved workflow, not alternative directions or verified application behavior. Each image was inspected inline after generation.

## Diagram history

```text
Use case: ui-mockup. Create ONE high-fidelity 1536x1024 native KAICad screenshot-like design state extending the ATTACHED APPROVED UI. Preserve its application title, menus, toolbar including global Agent console, breadcrumb System > PSU, Diagram v3, drawing, colors, fonts and visual grammar wherever not explicitly changed for this state. This is a design mockup with illustrative revision history, NOT a real running app or verified electrical circuit. Same light native desktop theme, readable14–16px product typography, natural native controls, restrained blue action/selection. Do not add a web/SaaS dashboard, chat transcript, decorative illustrations, gradients, huge shadows, ungrounded circuit specs or another "Refine" button.

Important behavioral model: every scope is a connected local diagram. History belongs to THIS PSU diagram or THIS named requirement field, never silently to the whole project. Browsing historical data is READ-ONLY and does NOT activate it. Restoring creates an EDITING DRAFT; only the editor's Save publishes a new revision. Saved history remains intact. Decline cancels the unsaved draft and leaves saved state untouched. Existing agent console initiates any AI work separately.

The saved PSU revision in the attached image is v3. Its current General text is "Supply CPU power and report rail status." Schematic text is "Separate power conversion and telemetry." Routing text is "Keep high-current paths away from sensing." Use these exact baseline words wherever current saved v3 appears. Shared UI must not confuse the whole diagram revision with one selected historic field. No timestamps or dates needed. Do not put UUIDs, XML or internal engine labels in ordinary UI. Maximize diagram context and clearly scoped useful content, not long explanation blocks.

This is a state of the already-approved workflow, NOT an alternative visual direction or an invitation to choose one of three designs. Only show controls relevant to the current state; action names must make their exact effect understandable. All contents and buttons fit completely in the window. The requested dialog should use approved native theme and appropriate spacing; no clipped content. NO extra invented screens or unactionable placeholder features.
STATE: WHOLE-DIAGRAM REVISION HISTORY.
Keep the approved PSU v3 diagram fully intact in the large left canvas; browsing the history panel has NOT changed it. Replace ONLY the right Properties panel with a native history panel, same width. Panel header "PSU — History" with back chevron to Properties and close X. Small line "Saved diagram: v3".
Below, a compact vertical revision list with exactly three rows:
"v3   Saved   AI agent" / subtitle "Added rail telemetry"
"v2   User" / subtitle "Defined two power paths"
"v1   AI agent" / subtitle "Initial PSU concept"
Highlight v2 with pale blue SELECTION background, but keep a separate Saved marker on v3. Do not label v2 current or active. No dates/times.

Below selected row details: heading "Revision v2", author "User". Small "Changes in v3" section with three concise rows: "+ Telemetry ADC", "+ Telemetry MCU", "+ Telemetry interface". These explain what saved v3 added after historical v2; not evidence that browsing applies changes. A "View source instruction" text link may appear.
In the panel below, two clear actions: neutral "Preview v2" and blue "Restore v2 as draft". One short helper sentence under them: "Save the draft to create a new revision." This copy matters because restore is not immediate publication.
At bottom preserve ordinary editor Decline and Save buttons but visibly DISABLED, because browsing alone has not changed the document. Global Agent console stays in toolbar unchanged; no refinement action appears in history.
Allow enough vertical space and no cramming; keep the user able to see the complete current PSU functional diagram. This screen illustrates version browsing and safe restoration, not a comparison of design options.
```

## Field history

```text
Use case: ui-mockup. Create ONE high-fidelity 1536x1024 native KAICad screenshot-like design state extending the ATTACHED APPROVED UI. Preserve its application title, menus, toolbar including global Agent console, breadcrumb System > PSU, Diagram v3, drawing, colors, fonts and visual grammar wherever not explicitly changed for this state. This is a design mockup with illustrative revision history, NOT a real running app or verified electrical circuit. Same light native desktop theme, readable14–16px product typography, natural native controls, restrained blue action/selection. Do not add a web/SaaS dashboard, chat transcript, decorative illustrations, gradients, huge shadows, ungrounded circuit specs or another "Refine" button.

Important behavioral model: every scope is a connected local diagram. History belongs to THIS PSU diagram or THIS named requirement field, never silently to the whole project. Browsing historical data is READ-ONLY and does NOT activate it. Restoring creates an EDITING DRAFT; only the editor's Save publishes a new revision. Saved history remains intact. Decline cancels the unsaved draft and leaves saved state untouched. Existing agent console initiates any AI work separately.

The saved PSU revision in the attached image is v3. Its current General text is "Supply CPU power and report rail status." Schematic text is "Separate power conversion and telemetry." Routing text is "Keep high-current paths away from sensing." Use these exact baseline words wherever current saved v3 appears. Shared UI must not confuse the whole diagram revision with one selected historic field. No timestamps or dates needed. Do not put UUIDs, XML or internal engine labels in ordinary UI. Maximize diagram context and clearly scoped useful content, not long explanation blocks.

This is a state of the already-approved workflow, NOT an alternative visual direction or an invitation to choose one of three designs. Only show controls relevant to the current state; action names must make their exact effect understandable. All contents and buttons fit completely in the window. The requested dialog should use approved native theme and appropriate spacing; no clipped content. NO extra invented screens or unactionable placeholder features.
STATE: ONE FIELD'S HISTORY.
Use the approved original PSU screen as background, slightly dimmed by a focused native modal. Preserve background diagram and UI; no history sidebar. Center a clean native dialog approximately900x540, fully inside the window.
Dialog title EXACTLY "Routing requirements — History". Context line "PSU · Saved diagram v3". The field name is explicit so this cannot be confused with restoring the whole PSU.
Left narrow chronological list: "v3 · AI agent · Saved", "v2 · User" (selected), "v1 · AI agent". Use a clear row selection without pretending it is the active model version.
Right larger comparison area with two vertically stacked plain sections:
Heading "v2 — Selected text", body EXACTLY "Keep power paths short."
Heading "v3 — Saved text", body EXACTLY "Keep high-current paths away from sensing."
Below a small "View source instruction" link for selected v2. No invented dates and no other text fields.
At the dialog bottom, one short sentence: "Only Routing requirements will change." Left secondary button "Close", right blue primary button "Use v2 text in draft". Make it clear this copies historic text into the editable draft, NOT immediate Save, deletion of history or activation. Do not use "Accept all", "Refine" or "Overwrite".
Background editor Save and Decline can remain visible but dim/inactive while the modal owns focus. Global Agent console remains unchanged in background. Make crisp readable natural native UI with comfortable spacing, no giant banner or ornamental containers.
```

## Save conflict

```text
Use case: ui-mockup. Create ONE high-fidelity 1536x1024 native KAICad screenshot-like design state extending the ATTACHED APPROVED UI. Preserve its application title, menus, toolbar including global Agent console, breadcrumb System > PSU, Diagram v3, drawing, colors, fonts and visual grammar wherever not explicitly changed for this state. This is a design mockup with illustrative revision history, NOT a real running app or verified electrical circuit. Same light native desktop theme, readable14–16px product typography, natural native controls, restrained blue action/selection. Do not add a web/SaaS dashboard, chat transcript, decorative illustrations, gradients, huge shadows, ungrounded circuit specs or another "Refine" button.

Important behavioral model: every scope is a connected local diagram. History belongs to THIS PSU diagram or THIS named requirement field, never silently to the whole project. Browsing historical data is READ-ONLY and does NOT activate it. Restoring creates an EDITING DRAFT; only the editor's Save publishes a new revision. Saved history remains intact. Decline cancels the unsaved draft and leaves saved state untouched. Existing agent console initiates any AI work separately.

The saved PSU revision in the attached image is v3. Its current General text is "Supply CPU power and report rail status." Schematic text is "Separate power conversion and telemetry." Routing text is "Keep high-current paths away from sensing." Use these exact baseline words wherever current saved v3 appears. Shared UI must not confuse the whole diagram revision with one selected historic field. No timestamps or dates needed. Do not put UUIDs, XML or internal engine labels in ordinary UI. Maximize diagram context and clearly scoped useful content, not long explanation blocks.

This is a state of the already-approved workflow, NOT an alternative visual direction or an invitation to choose one of three designs. Only show controls relevant to the current state; action names must make their exact effect understandable. All contents and buttons fit completely in the window. The requested dialog should use approved native theme and appropriate spacing; no clipped content. NO extra invented screens or unactionable placeholder features.
STATE: SAVE CONFLICT, NOTHING DISCARDED.
Preserve approved PSU editor as dimmed background. Center ONE clean native dialog approximately1080x680, all inside the1536x1024 window. It is an editing conflict dialog, not an approval platform.

Title EXACTLY "Resolve changes before saving". Context line "PSU · Routing requirements". Short status line "Your draft is based on v3. The saved diagram is now v4." Use neutral informative styling, not alarming red danger banner.
At top of contents a compact expanded baseline row:
"Base — v3"
"Keep high-current paths away from sensing."
Then two equal side-by-side plain text sections:
LEFT heading "Your draft", content EXACTLY "Keep high-current paths away from sensing. Place converters on the top edge."
RIGHT heading "Latest saved — v4 · AI agent", content EXACTLY "Keep high-current paths away from sensing. Place converters on the bottom edge."
The conflicting terms "top edge" and "bottom edge" may have subtle amber underline/highlight, but the headings and words carry meaning without color.

Below, three compact radio choices in one row, NONE selected:
"Use my text"   "Use saved text"   "Write merged text"
Below those, a short initially disabled empty text area labelled "Resolved text" with no fake merged content. It becomes editable when Write merged text is selected.
Footer left secondary button "Back to editing". Footer right button "Save resolved version" visibly DISABLED until there is an explicit valid resolution. One small sentence above footer: "Both versions are preserved." It explains actual recovery semantics, not implementation internals.
Do NOT include Keep both as a magic conflict resolution, don't silently select mine/latest, don't have a destructive overwrite action, no AI-refine action. Native Close/Escape is equivalent to Back to editing and preserves draft. The illustrative scenario genuinely conflicts because top vs bottom cannot be assumed equivalent. Include no filesystem paths, UUIDs, protocol errors or hidden logging text.
Keep exact baseline requirement text consistent with approved background, one dialog only, crisp readable UI, meaningful whitespace and no clipped buttons.
```
