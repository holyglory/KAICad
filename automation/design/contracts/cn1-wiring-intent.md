> **Frozen 2026-09-23 by the integration owner** (decision `kicad-phase2-contracts-frozen-20260923`).
> Field numbering: fields this contract fixes are declared by the parent seam at the freeze, sequentially
> below 100; later lane additions use the lane bands of `psu-cpu-fixture-and-ownership.md`
> (2A 100-199, 2B 200-299, 2C 300-399, 2D 400-499, 500-999 Phase 3). Items marked owner-pending below
> use the stated default until the owner decides; changing them later is a contract revision.

> Owner-pending: power nets (labels in M1 vs declared power symbols), naming existing unlabelled nets from XML, and the p20323 native file-format extension (separate seam freeze).

# CN-1: XML net intent → connected native schematic (contract v1, judged)

**Status:** final judged contract, ready for the integration owner to freeze.
**Baseline:** read-only verification of `the codex/finalization-integration worktree`, branch `codex/finalization-integration`, HEAD `7465f06533`.

**Ledger outcomes this contract enables:**
- `pf1134ca32f913781`: electrical connections for XML additions.
- `p1f9bc19460f72e2b`: connection-aware initial placement.
- `pd983fe2bff600f07`: connected creation closure.
- `p20323fd749ff825e`: exact variant pin identities. CN-1 gates this case now and reserves its interface.

**Lanes:**
- **Parent seam:** the integration owner. Lands first and is then frozen.
- **Lane A:** net intent and topology (managed code, pure).
- **Lane B:** geometry realizer plus native measurement.
- **Lane C:** native atomic assertion, executor, resolution, recovery and end-to-end journeys.
- **Lane D:** connection-aware placement, and later the p20323 work.

---

## 0. How the two drafts were combined (each point checked in code)

| Topic | Decision | Evidence |
|---|---|---|
| Base design | The "minimal" draft is the base: no `design.xml` schema change, power symbols stay model components, no provenance ledger. It is grafted with the "robust" draft's native atomic post-condition, native power facts, pin-group assertion, capability gate and verified abandonment of a rejected batch. | Below. |
| Rollback | A **native atomic assertion** inside the single batch. The minimal draft's compensating second batch is rejected. | The pf1134 verification text requires "expected connectivity admitted atomically". `API_HANDLER_SCH::handleApplyItemBatch` already captures `captureMovePinPartitions` in the middle of a batch for connected moves and rejects through `reject()`→`SCH_COMMIT::Revert()` before `pushCurrentCommit`; the checked controller then reports `CSBS_REJECTED` with unchanged state. Creations are staged (`createdItems`, `commit->Add`) but can still be evaluated before the push. The minimal draft's claim that "a check inside the batch is impossible" is wrong. |
| Power symbols | Never generated. Power symbols are ordinary model components declared in XML. | `SchematicDesignBindings.Inspect` reports `unmapped_native_symbol` for every unbound native symbol. The robust draft's cloned or generated power symbols would change the semantics of `Inspect`, `SchematicElectricalComparison` and `NativeOwners`, and would need an XML ledger. |
| A global label joins a power net | Allowed and used. | In `connection_graph.cpp`, both `SCH_GLOBAL_LABEL_T` and `IsGlobalPower()` pins populate `m_global_label_cache[name]`. `SCH_LABEL_T`, `SCH_HIER_LABEL_T` and local-power pins share `m_local_label_cache[(sheet,name)]`. |
| Hidden power-in pins | Never wired. | `connection_graph.cpp` skips a global power pin that has non-empty `ConnectedItems` unless its parent symbol is a power symbol. `SCH_PIN::IsGlobalPower`/`GetType`/`GetShownName` apply the active alternate. |
| XML schema | Unchanged. The robust draft's `net-realization` element is rejected. | Not needed without generated power symbols or a ledger. |
| Identity salt | Origin + native revision + SHA-256 of the desired bytes (minimal draft). | The robust draft's baseline-XML seed repeats when an undo is reverse-synced back to byte-identical baseline XML. Native revisions never repeat within an epoch. |
| Batch size | At most 2,097,152 serialized bytes. | `checked_schematic_controller.cpp` rejects any request larger than (16 MiB − 16 KiB)/6 ≈ 2.79 MB. The minimal draft's 4 MiB would be refused. |
| Recovery | Reuse `PendingLayout`. Relax `invalid_layout_intent`, use envelope version 10, and add a verified `AbandonRejectedRealization`. No `PendingRollback`. | `DesignRecoveryStore.Validate` currently requires a connected move, transform or lock operation in every `PendingLayout` mutation. |
| Label orientation | Outward (+1,0)→`SLSS_RIGHT`, (−1,0)→`SLSS_LEFT`, (0,−1)→`SLSS_UP`, (0,+1)→`SLSS_BOTTOM` for local, global and hierarchical labels. A sheet pin on `SHS_LEFT` uses `SLSS_RIGHT`; on `SHS_RIGHT` it uses `SLSS_LEFT`. | `sch_label.cpp` label shapes and bounding boxes; `SCH_SHEET_PIN::SetSide`. |
| Contact in the middle of a wire | Connects only through an explicit `Junction`. | `connection_graph.cpp` builds a point-based connection map, and junctions attach wires that pass through them. Automation batches preserve geometry (`m_automationBatch`), so there is no cleanup. |
| Pin reference in the proto | Reuse the existing `kiapi.schematic.types.SchematicNetChainPinAnchor{SheetPath path=1; KIID pin=2}`. | `schematic_types.proto`. |
| Label text source | The explicit model net `Name`. Auto-generated names are rejected. | `SchematicNetReconciliation` names new nets from the native name or `"NET-"+12 hex`. |
| Capabilities | Only two strings are advertised today (`api_server.cpp`). CN-1 adds one capability, advertised only when every CN-1 native piece is present. | |

---

## 1. Scope and binding rules

### In scope

1. **Additive changes to `Circuit.Nets` in a saved `engineering-design:1` revision.** This means new nets and new members of existing nets. They may come:
   - on their own (**connect-only**); or
   - together with component additions that `SchematicNativeCreationProjection` supports (**connected addition**).
2. **Milestone 1 (M1)** realizes each connection with:
   - a stub wire plus a label for each pin;
   - a `GlobalLabel` for nets with a global name;
   - a `HierarchicalLabel` plus a `SheetPin` for each sheet crossing;
   - a join anchor on an existing unlabelled island.
3. **Milestone 2 (M2):** orthogonal, obstacle-aware wires with explicit junctions. M2 starts only after M1 qualifies.
4. **One native commit and one undo step per operation.** The native side admits the commit only if an exact pin-partition assertion holds. Otherwise nothing changes natively: no revision change, no journal entry, no undo entry, no event and no dirty flag.
5. **Connection-aware initial placement** and power-component attachment for coordinate-free additions (p1f9bc).
6. **The variant gate now.** The p20323 interface is reserved.

### Binding rules

- **Where intent comes from.** Intent comes only from `Circuit.Nets[*].Pins` (`PinEndpoint(ComponentId, Pin)`). The following never produce native items:
  - `StructuralConnection` entries and block links;
  - boundary interfaces;
  - `DiagramEndpointBinding` of kind `Unresolved`, `Interface`, `Compatible` or `Candidates`;
  - `UnresolvedNetBinding`.

  A `DiagramEndpointKind.Pin` binding counts only after its owner has materialized it into `Circuit.Nets`. Missing mappings stay explicitly unresolved.
- **Exact identity chain.** The chain is component → `SymbolOccurrence` → `SchematicSymbolBinding` → the bound `SchematicSymbolInstance` → a definition pin with `library_pin_id`, the same number, and the active unit and body style. This is the existing `SchematicElectricalComparison` rule. Names, positions and enumeration order never identify anything.
- **Label texts are outputs.** They are never used as identities, and the native assertion proves the resulting pin partition.
- **Power components are never invented.** Realization never creates a model component or an unbound native symbol.
- **Existing native objects are never moved, modified or removed.** The one exception: `SheetPin`s may be appended to an existing, unlocked `SheetSymbol`, with every other field and pin kept byte-for-byte.
- **Sync scope.** Automatic sync realizes saved XML revisions and external XML changes only, never an unsaved draft. Save/Decline semantics are unchanged. The per-level diagram editor (`RECURSIVE_DIAGRAM_FRAME`) is not an input to realization, and neither is the one-time conversion of flat Structure into a root block.
- **No hidden data.** No `design.xml` schema change and no hidden provenance. Generated items are ordinary native items in the schematic section.

### Out of scope (each fails explicitly; codes in §13)

- XML-only disconnection or rewiring.
- Merging an added pin that already carries a driver.
- Buses.
- Sheet creation or reparenting (existing creation codes).
- Repeated instances that realize differently.
- Variant symbol overrides until p20323 lands.
- No-connect intent.
- Retiring generated items.
- Concurrent native edits (existing creation stability codes).
- Coordinate-free creation inside automatic sync (`created_symbol_placement_required`).

---

## 2. Terms

| Term | Meaning |
|---|---|
| Pin key | `ConnectionPinKey(SheetPathKey, PlacedPinId)`. `SheetPathKey` is the '/'-joined canonical `D` UUIDs of `Metadata.Document.SheetPath.Path` (= `SchematicDesignBindings.PathKey`). `PlacedPinId` is the placed `SchematicPin.id` whose `library_pin_id` is set. |
| Physical pin | `(ScreenId, SymbolId, PlacedPinId)`, shared by every instance of a repeated screen. |
| Drawn endpoint | A model-level test for `(C,n)`, where `p = part(C).Pins[n]`. If `p.Unit == 0`, C needs at least one occurrence. Otherwise C needs an occurrence with `Unit == p.Unit`. |
| Native group | The pin keys of one non-bus entry of `ObservedElectrical.Nets`. A pin that appears in no entry is its own singleton group (`captureMovePinPartitions` semantics). |
| Anchor partition of N | The native net that contains N's baseline placements. It is unique because the baseline is aligned. New nets have none. |
| Island | The members of net N on one sheet instance, plus any ports attached there. |
| Driver | A `GlobalLabel`, `LocalLabel`, `HierarchicalLabel`, or a global or local power pin. `DirectiveLabel` and `SheetPin` are not drivers. |
| Carrier | A member placement whose symbol `definition.type` is `SST_GLOBAL_POWER` or `SST_LOCAL_POWER`. Its name is `value_field.text`. |
| Implicit power pin | A pin on an `SST_NORMAL` symbol whose effective electrical type is `EPT_POWER_INPUT` and whose `visible` is false. The effective type is the active alternate's type when `active_alternate` is set. Its name is `active_alternate`, otherwise `name`. |
| Generated item | A native item created by the realizer, with the deterministic identity from §6.7. |

---

## 3. Invariants

- **I1.** Nothing is generated until every member of every changed net resolves exactly (§5.1).
- **I2.** Each generated item belongs to exactly one island or port per instance and has one deterministic ID.
- **I3.** Generated geometry never touches a foreign connection point or foreign segment. Only these contacts are permitted:
  - the member's own pin anchor;
  - the label or carrier anchor at the member's own stub end;
  - items of the same island at a join anchor (§6.4).
- **I4.** All creation and realization for one operation form **one** `CheckedSchematicBatch`: one commit and one undo step, with `assert_connectivity` as the last operation. Native commits nothing unless the assertion holds.
- **I5.** XML is published only after resolution (§9.4) and the existing `RequireCandidate` both pass.
- **I6.** Existing items are never changed, except for `SheetPin` appends.
- **I7.** Implicit power pins are never touched: nothing is placed at their anchors.
- **I8.** Every instance of a physical screen receives an identical physical realization, or the operation fails.
- **I9.** Determinism: the same origin, native revision, desired bytes and native measurements always produce the same items, IDs, operation order and bytes.
- **I10.** Planning (`kicad_design_sync_plan`) is pure. Native measurement is read-only, runs only in the executor, and runs at the checkpoint revision.
- **I11.** Realization changes only the `Schematic` section relative to the pre-realization candidate. The engineering section equals the desired engineering section.

---

## 4. Planner/executor seam: admitting mixed add+connect diffs (parent seam)

### 4.1 Classification: new file `SchematicConnectedAddition.cs`, fully implemented by the parent seam

```csharp
public enum SchematicConnectedAdditionKind { NotApplicable = 0, Admitted = 1, Rejected = 2 }
public sealed record SchematicConnectedAdditionClassification(SchematicConnectedAdditionKind Kind,
    IReadOnlyList<Guid> AddedComponentIds, IReadOnlyList<Guid> ChangedNetIds,
    string? ErrorCode = null, string? ErrorMessage = null);
public static class SchematicConnectedAddition
{
    public static SchematicConnectedAdditionClassification Classify(DesignRecoveryState state,
        SchematicDesign desired, CancellationToken token = default);
}
```

Let B be `state.Baseline.Engineering.Circuit` and D be `desired.Engineering.Circuit`.

1. If `B.Id != D.Id`, return NotApplicable.
2. **Shape check.** Return NotApplicable if any of the following fails:
   - no component is removed;
   - every old component, symbol occurrence and sheet-definition component is record-equal in D;
   - every old part is `NormalizePart`-equal in D;
   - `SheetInstances` are equal (ordered by id) and the sheet definition ids are equal;
   - `SchematicNetReconciliation.Bindings(state.Baseline) == Bindings(desired)`;
   - `SchematicHierarchyDelta.Plan(state.Baseline.Schematic, desired.Schematic)` is empty.

   New parts are allowed; creation validates their declarations.
3. **Delta, over drawn endpoints only** (evaluated in D):
   - `added[N] = drawn(D.N.Pins) − drawn(B.N.Pins)`, where `B.N` is empty for a new id;
   - `lost` is true when some baseline net M has a drawn pin that is no longer in `D.M`. This includes deleted nets and pins moved to another net;
   - `addedComponents = D.Components − B.Components`.
4. Return NotApplicable in two cases:
   - `addedComponents` is empty, nothing is added and nothing is lost. This covers renames and requirement-only edits.
   - `addedComponents` is empty, nothing is lost, and every net with added pins has fewer than 2 drawn endpoints in D. Such a regrouping needs no native change, and the general path publishes it.
5. `stable` holds when all of the following hold:
   - `HierarchyResolution == null`;
   - `OwnershipResolution == null`;
   - `HierarchyDelta(Baseline.Schematic, Observed)` is empty;
   - `SchematicElectricalCheckpoints.Require` succeeds;
   - `Compare(Baseline, ObservedElectrical)` is complete and equivalent.
6. If `lost`:
   - when `stable`, return `Rejected(xml_disconnection_unsupported)`;
   - otherwise return NotApplicable, because the general three-way path may merge a matching native disconnection.
7. If `addedComponents` is empty, the state is not `stable`, and `Compare(state.Baseline with {Engineering = desired.Engineering}, ObservedElectrical)` is complete and equivalent, return NotApplicable. The native side already realizes the change.
8. Otherwise return `Admitted(addedComponents ordered by id, changed net ids ordered)`.

### 4.2 Plan record: `SchematicSynchronizationPlan.cs`

Append one parameter:

```csharp
public sealed record SchematicSynchronizationPlan(/* existing … */ bool NativeLayoutResolutionRequired = false,
    SchematicConnectionIntent? Connections = null)
{
    public bool CanPrepare => Candidate is not null && ErrorCode is null;             // unchanged
    public bool NativeConnectionRealizationRequired => Connections is not null;
}
```

**Invariant.** `Connections != null` implies all of:
- `CanPrepare`;
- `CandidateXml == null`: there is no publishable preview;
- `NativeOperations.Count == 0`;
- `NativeConnectivityValidationRequired`;
- `NativeLayoutResolutionRequired`.

`Candidate` is the pre-realization candidate (desired engineering data plus created symbols).

### 4.3 Dispatch in `SchematicSynchronizationPlanner.Prepare`

This dispatch covers `Plan`, `PlanForExecution` and the history variants.

```text
hierarchy = (unchanged merge)
if IsSupportedAddition(baseline, desired.Engineering) -> PrepareCreation      // unchanged, byte-identical
shape = SchematicConnectedAddition.Classify(state, desired, token)
Rejected      -> Failure(shape.ErrorCode, shape.ErrorMessage)
Admitted      -> SchematicConnectedAdditionPlanner.Prepare(state, desired, hierarchy, shape, gaps, token)   // lane A
NotApplicable -> existing general path (unchanged)
```

`SchematicNativeCreationProjection.IsSupportedAddition` is unchanged. Both `Project` overloads gain a trailing `bool allowConnected = false`. When it is true, only the `created_component_connectivity_requires_resolution` throw is skipped.

### 4.4 `SchematicConnectedAdditionPlanner.Prepare` (lane A; the parent seam provides a stub)

1. **Guards**, in `PrepareCreation` order with the same codes:
   - `SchematicElectricalCheckpoints.Require`;
   - hierarchy `CanApply`;
   - `creation_bindings_changed`;
   - `HierarchyResolution` or `OwnershipResolution` present → `creation_requires_stable_native_hierarchy`;
   - the two hierarchy deltas → `creation_requires_stable_native_hierarchy`;
   - `unaligned_electrical_baseline`;
   - `creation_requires_stable_connectivity`.
2. **Candidate:**
   - with components added: `Project(state.Baseline, desired, libraries, token, allowConnected: true).Candidate`;
   - otherwise: `state.Baseline with { Engineering = desired.Engineering, PartSymbols = desired.PartSymbols }`.
3. `SchematicDesignBindings.Inspect(candidate)` must resolve, otherwise `created_binding_invalid`.
4. `intent = SchematicConnectionIntentBuilder.Build(state, candidate, shape, token)`. All §5 codes are thrown here.
5. `SchematicDesignXml` round-trip of the candidate must be byte-equal, otherwise `inconsistent_design_serialization`. The XML is not exposed.
6. Return `new(candidate, null, [], hierarchy, null, null, [], [], null, gaps, NativeConnectivityValidationRequired: true, NativeLayoutResolutionRequired: true, Connections: intent)`.

---

## 5. Intent model (lane A: `SchematicConnectionIntentBuilder.Build`, pure)

### 5.1 Endpoint → placements

This applies to every endpoint `e=(C,n)` of every changed net, and to every pin of every created symbol (needed by §5.3 and §5.8).

1. Let `p = part(C).Pins.Single(Number==n)`, and `O = {s ∈ Symbols | s.ComponentId==C ∧ (p.Unit==0 ∨ s.Unit==p.Unit)}`.
2. If `O` is empty, fail with `connected_pin_unresolved`: the unit is undrawn, and the XML must add the occurrence.
3. For each `s ∈ O`, let X be the bound native symbol in `candidate.Schematic` at `SheetBindings[s.EffectiveSheetInstanceId(C)]`.
   - If `X.SeparatePinIdentities == false` or `X.Definition == null`, fail with `connected_pin_identity_missing`.
   - Candidate pins are X's definition pin children with:
     - unit absent, 0, or equal to `s.Unit`;
     - body style absent, 0, or equal to `X.BodyStyle ?? 1`;
     - `LibraryPinId` set;
     - `Number == n`.
   - Exactly one must match. None → `connected_pin_unresolved`; more than one → `connected_pin_ambiguous`.
   - A common pin (unit 0) yields one placement per drawn occurrence, and **all** of them are realized.
4. Lane A extracts this lookup from `SchematicElectricalComparison.Compare` into one internal helper used by both, with no behaviour change.

### 5.2 Members and roles

- **Role:**
  - `PowerCarrier` if `X.Definition.Type ∈ {SST_GLOBAL_POWER, SST_LOCAL_POWER}`;
  - `ImplicitPower` if the pin is an implicit power pin (§2);
  - `Signal` otherwise.
- `AlreadyConnected` holds if the placement is in N's anchor partition.
- `RequiresStub := Role==Signal ∧ ¬AlreadyConnected`.
- An added existing placement whose native net contains any driver fails with `connected_pin_driver_conflict`. Under an aligned baseline, that driver names another net or is a lone label; merging it would silently rename.

### 5.3 Scope, global name and implicit power (planner prediction; §6.2 confirms it natively)

**Global-name sources for N:**
- values of `SST_GLOBAL_POWER` carriers among N's members;
- names of `ImplicitPower` members;
- texts of `GlobalLabel` items and global-power pins in N's anchor partition.

**Rules:**
- A carrier value containing `${` fails with `connected_power_name_unresolved`.
- More than one distinct source value:
  - if any source is an implicit pin → `connected_implicit_power_conflict`;
  - otherwise → `connected_global_name_conflict`.
- Exactly one source value makes the net `Scope=Global` with `GlobalName=G`. No source means `Scope=Local`.
- Two model nets with the same G → `connected_global_name_conflict`. So does any native partition other than N's anchor that carries G (global label, global power pin or implicit pin).

**Rules for every created symbol** (not only the changed nets):
- Each global-carrier pin, and each implicit pin named G, must be a member of the model net whose `GlobalName` is G.
- It may stay unassigned only if no other source of G exists anywhere, native or created.
- Otherwise fail with `connected_implicit_power_conflict` (implicit pin) or `connected_global_name_conflict` (carrier).

Local carriers on one island must share one value, and that value is the island's `LabelText` (§5.5). Otherwise → `connected_net_name_conflict`.

### 5.4 Islands, ports and joins

Group each changed net's placements by sheet instance. Order members by `(ComponentId, Pin ordinal, PlacedPinId)`.

**Global scope.** There are no ports.
- An island needs realization if it has a `RequiresStub` member.
- If an anchor partition exists and carries no G, exactly one island has `JoinRequired=true`: the island of the first `AlreadyConnected` Signal member. Its `JoinCandidates` are its `AlreadyConnected` Signal members, in order.

**Local scope:**
1. Build the tree: the union of the `SheetInstance.ParentId` chains from every instance with members, up to their lowest common ancestor (LCA). Tree nodes without members become member-less islands. No LCA → `connected_hierarchy_unsupported`.
2. For each tree edge child→parent:
   - K is the last UUID of the child's native path, i.e. its `SheetSymbol` in the parent screen. K locked → `connected_locked_sheet_symbol`.
   - `PortText` is the child island's `LabelText`.
   - `SheetPinExists` holds if the anchor partition contains a `SheetPin` on K with that text. If K has a `SheetPin` with that text outside the anchor partition → `connected_net_name_conflict`.
   - `UplinkLabelExists` holds if the anchor partition contains a `HierarchicalLabel` with that text on the child path.
   - When `!UplinkLabelExists`, the child island's `UplinkSheetSymbolId = K`. When `!SheetPinExists`, K goes into the parent island's `ChildSheetSymbolIds`.
3. **`AnchorHasMatchingDriver`** holds if the anchor items on this path include a `LocalLabel`, `HierarchicalLabel` or local carrier whose text is the island's `LabelText`.
4. An island **needs realization** if it has a `RequiresStub` member, a non-null `UplinkSheetSymbolId`, or non-empty `ChildSheetSymbolIds`.
5. **`JoinRequired`** holds if the island needs realization, has `AlreadyConnected` members, and either:
   - `!AnchorHasMatchingDriver`; or
   - an uplink label is needed but the island has no `RequiresStub` member and no `ChildSheetSymbolIds`.
6. Anchor items on the island's path that include a `BusEntry`, or a `SchematicLine` of type `SLT_BUS` → `connected_bus_realization_unsupported`.

### 5.5 Label text

- **Global scope:** the text is G.
- **Local island text T, in precedence order:**
  1. the island's local-carrier value;
  2. the ordinal-minimum text among the `LocalLabel`/`HierarchicalLabel` anchor items on this path;
  3. `LocalName(N.Name)`: the substring after the last `/`, or the whole name if there is none.

  T must be identical for every instance net projected onto the same physical island. Otherwise → `connected_label_text_divergent`.

**Validity** (otherwise `connected_label_text_invalid`):
- 1–128 Unicode scalar values;
- no whitespace or control characters;
- none of `{ } [ ] / \ $ ~ ^ , "`;
- does not start with `#`.

A T derived from `N.Name` is also rejected when `N.Name` contains `Net-(` or `unconnected-(`, or matches `^NET-[0-9a-f]{12}$`. These are auto-generated names; the error message asks for an explicit net name in XML. A rename in the same diff is admitted.

**Conflicts** (`connected_net_name_conflict`):
- on some instance path, a `LocalLabel`, `HierarchicalLabel` or local carrier with text T belongs to another native partition;
- two islands on one physical screen use the same T for different nets;
- a `PortText` collides with a `SheetPin` or child `HierarchicalLabel` of another partition.

### 5.6 Repeated screens

Islands on one physical screen are compared by a physical key, made of:
- the stub-requiring physical pins and join candidates;
- `LabelText` and `Scope`;
- `UplinkSheetSymbolId` and `ChildSheetSymbolIds`;
- `AnchorHasMatchingDriver` and `JoinRequired`.

Every instance path of the screen must yield an identical island set. A per-instance unit selection that differs for an involved symbol is also divergent. Any violation → `connected_repeated_screen_divergent`.

### 5.7 Variant gate (until p20323 lifts it; §11)

**Symbols in scope:**
- owners of `RequiresStub` or `JoinCandidates` placements;
- carriers;
- every created symbol, since a created symbol copies its template's variants.

Fail with `connected_variant_symbol_unsupported` if any `SchematicSymbolVariant`, in `X.Variants` or in any `X.InstanceRecords.Records[*].Variants`, has a `symbol_override` with a non-empty `entry_name` that differs from `X.LibraryId ?? X.Definition.Id`.

### 5.8 Expected native groups (these feed the assertion)

- For each changed N: `E(N) = ⋃{native groups intersecting placements(N)} ∪ placements(N)`. Created pins contribute one key per sharing instance path.
- Every created pin key that is in no `E(N)` becomes a singleton group.
- Groups must be disjoint, otherwise `connected_internal_inconsistency`.
- Keys are ordered by `(SheetPathKey ordinal, PlacedPinId)`, and groups by their first key.

### 5.9 Records (declared by the parent seam in `SchematicConnectionContract.cs`)

```csharp
public enum ConnectionScope { Local = 1, Global = 2 }
public enum ConnectionMemberRole { Signal = 1, PowerCarrier = 2, ImplicitPower = 3 }
public sealed record ConnectionPinKey(string SheetPathKey, Guid PlacedPinId);
public sealed record ConnectionPlacedPin(PinEndpoint Endpoint, Guid SymbolOccurrenceId, Guid SheetInstanceId,
    string SheetPathKey, Guid ScreenId, Guid SymbolId, Guid PlacedPinId, Guid LibraryPinId, bool CreatedSymbol);
public sealed record ConnectionMember(ConnectionPlacedPin Pin, ConnectionMemberRole Role, bool AlreadyConnected,
    string? PowerName) { public bool RequiresStub => Role == ConnectionMemberRole.Signal && !AlreadyConnected; }
public sealed record ConnectionIsland(Guid NetId, Guid SheetInstanceId, string SheetPathKey, Guid ScreenId,
    ConnectionScope Scope, string LabelText, IReadOnlyList<ConnectionMember> Members,
    IReadOnlyList<Guid> AnchorItemIds, bool AnchorHasMatchingDriver, bool JoinRequired,
    IReadOnlyList<ConnectionPlacedPin> JoinCandidates, Guid? UplinkSheetSymbolId,
    IReadOnlyList<Guid> ChildSheetSymbolIds);
public sealed record ConnectionPort(Guid NetId, Guid ChildSheetInstanceId, string ChildPathKey, Guid ChildScreenId,
    Guid ParentSheetInstanceId, string ParentPathKey, Guid ParentScreenId, Guid SheetSymbolId, string PortText,
    bool SheetPinExists, bool UplinkLabelExists);
public sealed record ConnectionNet(Guid NetId, string Name, ConnectionScope Scope, string? GlobalName,
    IReadOnlyList<PinEndpoint> AddedPins);
public sealed record ConnectionScreen(Guid ScreenId, IReadOnlyList<string> InstancePathKeys /* ordinal; [0] representative */,
    IReadOnlyList<ConnectionIsland> Islands /* representative-path islands needing realization */);
public sealed record SchematicConnectionIntent(int Version, Guid OriginId, Guid CircuitId,
    KiCad.Automation.Model.DocumentRevision NativeRevision, string DesiredSha256 /* lowercase hex of DesiredFileBytes */,
    IReadOnlyList<ConnectionNet> Nets, IReadOnlyList<ConnectionScreen> Screens, IReadOnlyList<ConnectionPort> Ports,
    IReadOnlyList<IReadOnlyList<ConnectionPinKey>> ExpectedGroups, IReadOnlyList<Guid> CreatedSymbolIds)
{ public const int CurrentVersion = 1; }
```

### 5.10 Limits

At most 1024 changed nets and at most 4096 placements that need items. Beyond that → `connected_scope_too_large`.

---

## 6. Realization, milestone 1: label stubs (lane B, `SchematicConnectionRealizer`)

### 6.1 Policy and geometry (the parent seam declares and implements these helpers)

```csharp
public sealed record SchematicConnectionPolicy(long GridNm, long ClearanceNm, long TextSizeNm, long PageInsetNm,
    long SheetPinPitchNm, long LabelBackToleranceNm)
{
    public static readonly int[] StubMultiples = [2, 3, 4, 6, 8];
    public static SchematicConnectionPolicy FromSnapshot(SchematicHierarchyData data);
}
public static class SchematicConnectionGeometry
{
    public static (int Dx, int Dy) Outward(SchematicPinAnchor pin);          // (-body_direction_x, -body_direction_y)
    public static Kiapi.Common.Types.Vector2 StubEnd(Kiapi.Common.Types.Vector2 anchor, (int Dx, int Dy) outward, long length);
    public static SchematicLabelSpinStyle Spin((int Dx, int Dy) outward);    // (1,0)->RIGHT (-1,0)->LEFT (0,-1)->UP (0,1)->BOTTOM
}
```

**`FromSnapshot`:**
- `GridNm` is `Metadata.Formatting.ConnectionGridNm`. Every instance must agree, and the value must be > 0 and a multiple of 100.
- `TextSizeNm` is `DefaultTextSizeNm` and must be > 0.
- If either is unavailable → `realization_grid_unavailable`.
- Derived values:
  - `ClearanceNm = floor(Grid/2 to 100 nm)`;
  - `PageInsetNm = 2·Grid`;
  - `SheetPinPitchNm = 2·Grid`;
  - `LabelBackToleranceNm = Grid/2`.

`Outward` must be an axis-aligned unit vector, otherwise `realization_pin_geometry_mismatch`.

### 6.2 Measurement

The realizer receives `Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure`, bound to the checkpoint revision. The pattern and the `schema_version` convention follow `SchematicInitialLayoutPlanner`.

**Round 1** runs for **every** instance path of every screen in `intent.Screens`.
- **Request:** `candidates` are the created symbols on that screen, at most 256 per request.
- **Checks on each response:**
  - `document`, `revision` and `screen_id` match the request → otherwise `realization_measurement_stale`;
  - obstacle IDs equal exactly the checkpoint screen's items, excluding groups and markers, each exactly once, and candidate IDs equal the request → otherwise `realization_measurement_incomplete`;
  - `pin_geometry_available` is true → otherwise `realization_measurement_unsupported`.
- **Symbols that own a stub, join or attach placement** must report `symbol_pins.complete`. If not:
  - `incomplete_reason == SPGIR_VARIANT_PIN_MAPPING_UNRESOLVED` → `realization_variant_pin_identity_unresolved`;
  - any other reason → `realization_pin_geometry_incomplete`.

  An obstacle symbol with incomplete pins is allowed, but its whole bounds become a keep-out for every generated point, segment and envelope.
- **Across instance paths:**
  - obstacles are combined as a union by ID;
  - every involved physical pin must have identical anchor, body direction, unit and body style across paths, otherwise `realization_pin_geometry_mismatch`.
- **Native power confirmation.** For every member placement, the anchor's `power_scope`/`power_net` must match the §5.3 prediction:

  | Member | Expected native report |
  |---|---|
  | Global carrier | GLOBAL, with the value |
  | Local carrier | LOCAL, with the value |
  | Implicit power | GLOBAL, with the name |
  | Signal | NONE |

  Any mismatch → `realization_implicit_power_mismatch`.

**Round 2: label prototypes.**
- `item_candidates` hold one prototype per distinct (kind, text, spin, shape) on the screen, positioned at the first stub end that uses it. Prototype IDs come from `SchematicConnectionIdentity.Probe`.
- The relative envelope is the measured bounds minus the anchor. It is translation-invariant and reused at every other position.
- Orientation guard: the envelope may extend at most `LabelBackToleranceNm` behind E along −outward. Otherwise → `realization_label_orientation_mismatch`.

### 6.3 Algorithm (screens in ScreenId order; islands in `(NetId, SheetPathKey)` order)

1. **(a) Join, if `JoinRequired`.** Try each join candidate in order, first as a *join stub* and then as an *anchor label* (§6.4). The first admissible option wins. None → `realization_no_join_anchor`. Mark the island in the outcome with the diagnostic `existing_net_named_by_realization`.
2. **(b)** One stub per `RequiresStub` placement, in member order. A common pin gets a stub on every placement.
3. **(c)** For each `ChildSheetSymbolIds` entry: a new `SheetPin` (§6.5), a sheet-pin stub, and a label.
4. **(d) Label kind for each stub:**
   - Global scope → `GlobalLabel(G)`.
   - Local scope → `LocalLabel(T)`. Exception: when `UplinkSheetSymbolId != null`, the first stub of the island in (a)(b)(c) order carries `HierarchicalLabel(T)` instead. Local and hierarchical labels with the same text on one sheet join natively.
5. **(e) Carrier attachment.** If a same-net carrier pin anchor equals E, emit the wire only (no label) and set `AttachedCarrier`.
6. **(f) Stub length.** Try `L = m·GridNm` for `m ∈ StubMultiples`, and take the first admissible. None → `realization_no_free_stub`.
7. **(g) Pre-existing contacts:**
   - a `NoConnectMarker` whose position equals a stub or join anchor → `realization_no_connect_conflict`;
   - a created pin anchor that coincides with any foreign connection point, or lies on a foreign wire or bus segment → `realization_created_pin_contact`.

### 6.4 Admission (stub from anchor A, outward o, length L, end E, label envelope R)

**Foreign points:**
- every measured pin anchor, including hidden pins and created candidates;
- wire and bus endpoints;
- junctions;
- no-connect positions;
- bus-entry endpoints (position, and position+size);
- label and directive-label positions;
- sheet-pin positions;
- every generated point accepted so far.

Exceptions: A itself, and an attached carrier anchor at E.

**Foreign segments:** existing wire and bus segments, plus accepted generated segments.

**A stub is admissible when all of the following hold:**
1. E and R lie inside the page bounds shrunk by `PageInsetNm`.
2. No foreign point lies on the closed segment [A,E] or within `ClearanceNm` (Chebyshev distance) of E.
3. No foreign point lies inside R.
4. [A,E] neither intersects nor comes within `ClearanceNm` of any foreign segment.
5. [A,E] intersects no obstacle bounds, except those of its owning symbol (the owning sheet for sheet-pin stubs) and, when attaching, the carrier symbol.
6. R (an open rectangle) intersects no obstacle bounds (including the owning symbol), no foreign segment, and no accepted generated envelope. Envelopes may touch but not overlap.

**Join stub variant.** Items of the same island that sit exactly at A are permitted. (A,E] must touch no item at all, including items of the same island.

**Anchor label variant.** The label sits at A (no wire), with `spin = Spin(o)`. R may overlap only same-island wire segments that end at A. All other rules apply.

### 6.5 Sheet pins

For a port on sheet symbol K in the parent screen:
- **Side:** `SHS_LEFT` if the mean x of the parent island's measured member anchors is less than K's centre x. `SHS_RIGHT` otherwise, including when the island has no members.
- **Position:** x is `K.position.x` for left, or `K.position.x + K.size.x` for right. y is the first value from `K.top + SheetPinPitch` to `K.bottom − SheetPinPitch`, stepping by `GridNm`, such that:
  - no existing `SheetPin` on that side is within `SheetPinPitchNm`;
  - no earlier allocation holds it;
  - the outward stub (−x for left, +x for right) and its label are admissible.

  Allocation order is `PortText` ordinal. No slot → `realization_no_free_sheet_pin_slot`.
- **Spin style:** left → `SLSS_RIGHT`, right → `SLSS_LEFT`, matching `SCH_SHEET_PIN::SetSide`.

### 6.6 Generated item payloads (exact fields; everything else unset)

| Item | Fields |
|---|---|
| `SchematicLine` | `id`, `start=A`, `end=E`, `type=SLT_WIRE`, `locked=LS_UNLOCKED` |
| `LocalLabel` | `id`, `position=E` (or A for an anchor label), `text{text_=T, attributes{size=(TextSizeNm,TextSizeNm), multiline=false}}`, `spin_style=Spin(o)`, `locked=LS_UNLOCKED` |
| `GlobalLabel` / `HierarchicalLabel` | the `LocalLabel` fields plus `shape=SLSH_PASSIVE`. `intersheet_refs_field` stays unset; native creates it. |
| `SheetPin` | appended to K's `pins` in every instance copy: `id`, `position`, `text{T, size, multiline=false}`, `spin_style` per §6.5, `shape=SLSH_PASSIVE`, `side`, `locked=LS_UNLOCKED` |
| `Junction` (M2 only) | `id`, `position`, `locked=LS_UNLOCKED` |

All coordinates are multiples of 100 nm.

### 6.7 Deterministic identities (the parent seam implements `SchematicConnectionIdentity`)

```text
Generated(origin, revision, desiredSha256, screenId, role, anchorKey, ordinal):
  material = "kicad-connection-realization-v1\n" + origin:D + "\n" + revision.Epoch + "\n" + revision.Sequence(invariant)
             + "\n" + desiredSha256 + "\n" + screenId:D + "\n" + role + "\n" + anchorKey + "\n" + ordinal(invariant)
  b = SHA256(UTF8(material)); b[6] = (b[6] & 0x0F) | 0x80; b[8] = (b[8] & 0x3F) | 0x80
  id = new Guid(b[0..16], bigEndian: true), written in canonical lowercase D format
Probe(checkpointRevision, screenId, kind, text, spin, shape [, symbolId, rotation]) = same construction under
  "kicad-connection-probe-v1\n"; probe IDs are measured only, never committed.
```

**Roles:** `stub-wire`, `stub-label`, `anchor-label`, `sheet-pin`, `sheet-pin-wire`, `sheet-pin-label`, `route-wire`, `junction`.

**`anchorKey` by role:**
- stub, join and anchor roles: the attachment's `PlacedPinId:D`;
- sheet-pin roles: `SheetSymbolId:D + "#" + PortText`;
- `route-wire`: `NetKey + "#" + segmentOrdinal`;
- `junction`: `NetKey + "#" + x + "," + y`.

`NetKey` is the ordinal-minimum `PlacedPinId:D` among the terminals. `ordinal` is 0 except for route segments.

Net IDs are excluded from the material, so a shared screen gets identical items in every instance. `revision` is `intent.NativeRevision`, which the executor has already checked equals the checkpoint. An ID already present in the observed schematic, the candidate, or earlier generated or probe IDs → `realization_identity_collision`.

### 6.8 Emission as typed `ApplySchematicItemBatch` operations

1. Insert every generated item into **every** instance copy of its physical screen in `realization.Design.Schematic`, and append every `SheetPin` to every instance copy of K.
2. Compute the operations:

   ```text
   ops = SchematicHierarchyDelta.Plan(checkpoint.Electrical.Hierarchy.Data, realization.Design.Schematic)
         ++ [ SchematicItemOperation{ assert_connectivity = { version=1, expected_groups=intent.ExpectedGroups } } ]
   ```

   The delta yields `replace_library_cache`, `create` operations for created symbols and generated items, and `update` operations for `SheetSymbol`s that gain pins. It removes duplicates for shared screens. The assertion has no `target_document`. Pin references reuse each screen's `Metadata.Document.SheetPath` and the `PlacedPinId`.
3. `SchematicDesignXml` round-trip of `realization.Design` must be byte-equal, otherwise `inconsistent_design_serialization`.
4. The batch is:

   ```text
   ApplySchematicItemBatch{document=root, document_epoch, expected_revision=checkpoint revision,
     operation_id=new UUID, origin_id=recovery OriginId, description="Apply XML connections", operations=ops}
   ```

   It is wrapped in `CheckedSchematicBatch{batch, expected_state=checkpoint.State}`. A serialized request larger than 2,097,152 bytes → `realization_batch_too_large`.

**Output records and entry point:**

```csharp
public enum GeneratedConnectionRole { StubWire=1, StubLabel=2, AnchorLabel=3, SheetPin=4, SheetPinWire=5, SheetPinLabel=6, RouteWire=7, Junction=8 }
public enum ConnectionRealizationStrategy { LabelStub = 1, OrthogonalWire = 2 }
public sealed record GeneratedConnectionItem(Guid Id, GeneratedConnectionRole Role, Guid ScreenId, IReadOnlyList<Guid> NetIds,
    Guid? PlacedPinId, Guid? SheetSymbolId, string TypeUrl);
public sealed record ConnectionIslandOutcome(Guid NetId, Guid ScreenId, string RepresentativePathKey,
    ConnectionRealizationStrategy Strategy, IReadOnlyList<Guid> GeneratedIds, bool AttachedCarrier, string? FallbackReason);
public sealed record ConnectionDiagnostic(string Code, string Severity /* info|warning */, Guid? NetId, Guid? ItemId, string Message);
public sealed record SchematicConnectionRealization(SchematicDesign Design, IReadOnlyList<SchematicItemOperation> Operations,
    IReadOnlyList<GeneratedConnectionItem> Generated, IReadOnlyList<ConnectionIslandOutcome> Outcomes,
    IReadOnlyList<ConnectionDiagnostic> Diagnostics, IReadOnlyList<string> Limitations);
public static class SchematicConnectionRealizer
{
    public static Task<SchematicConnectionRealization> RealizeAsync(SchematicConnectionIntent intent, SchematicDesign candidate,
        CheckedSchematicState checkpoint, Func<MeasureSchematicPlacement, CancellationToken, Task<SchematicPlacementGeometry>> measure,
        SchematicConnectionPolicy policy, CancellationToken token = default);
}
```

**Diagnostics:**
- `existing_net_named_by_realization` (info);
- `realization_page_reservations_unspecified` (info). The drawing-sheet title block is not an item; only the page inset protects it.

---

## 7. Milestone 2: orthogonal wires (lane B, after M1 qualifies; contract level)

**When it applies.** For the islands of one net on one screen with two or more terminals, `OrthogonalWire` is used whenever it is routable. Otherwise the island falls back to M1 deterministically, with `FallbackReason` recorded. Labels are still used for:
- ports: one uplink `HierarchicalLabel` and the sheet-pin label;
- global names: one `GlobalLabel` stub on the screen's tree root, unless an island on that screen already carries G.

**Terminals.**
- Each terminal is a stub-requiring pin that starts with a forced outward escape of one `GridNm`.
- An existing island attaches only at its own existing connection points: pin anchors and wire endpoints. Existing wires are never split.

**Search.**
- Grid: `GridNm`. Region: page bounds shrunk by `PageInsetNm`.
- Blocked cells:
  - obstacle bounds inflated by `ClearanceNm`, excluding each terminal's escape corridor;
  - foreign points within the clearance;
  - runs parallel to a foreign segment within `ClearanceNm`.
- A foreign wire may be crossed only at right angles and away from its endpoints and bends.

**Cost and tree construction.**
- Cost = length/Grid + 2 per bend + 10 per crossing.
- Order terminals by (escape x, escape y, `PlacedPinId`). Grow the tree from the first terminal, repeatedly connecting the Manhattan-nearest remaining terminal (ties by `PlacedPinId`) to any grid node of the tree with A*.
- A* tie-break: horizontal first, then lower x, then lower y.
- Collinear steps merge into maximal segments.

**Junctions.** Emit an explicit `Junction` wherever:
- a path end lands on the interior of a generated segment; or
- three or more connection items (wire ends and pins) meet.

**Budgets.** Total length at most 4× the Manhattan minimum spanning tree length, and at most 200,000 expanded nodes per island. Exceeding either triggers the M1 fallback.

All §6.4 rules still apply.

---

## 8. Native atomic assertion (lane C)

### 8.1 Semantics of `SchematicItemOperation.assert_connectivity` (field 27)

**Definitions:**
- **B** is the pin partition captured immediately before the batch's first operation is applied. Capture uses `captureMovePinPartitions` semantics: every loaded sheet instance, every `SCH_SYMBOL` pin including power-symbol pins, bus nets skipped, singletons included.
- **E** is `expected_groups`. **P** is `⋃E`.
- **A** is the partition after every operation of the batch has been applied, with connectivity recalculated and no cleanup.

**Requirement:** `A == { g ∈ B : g ∩ P = ∅ } ∪ E`. Pins are compared by exact `KIID_PATH` and pin `KIID`.

**Evaluation point.** Evaluate inside `handleApplyItemBatch` after every operation has been validated and staged, and before `ValidateLibraryCaches`/`pushCurrentCommit`. Two implementations are allowed, with identical observable behaviour:
- temporarily materialize the staged creations and removals on their screens, recalculate, capture, then restore; or
- evaluate on a private connection graph.

**On failure.** Use the existing `reject()` path (`SCH_COMMIT::Revert`, selection restore), with the message `connectivity_postcondition_failed: expected=<n> mismatches=<m> first=<missing_pin|unexpected_join|unexpected_split|unaffected_group_changed>:<keys>`, at most 2048 bytes. `DocumentLifecycleState` (revision, `state_sha256`, dirty flags), the journal, undo, notifications and selection must be byte-identical to before.

**On success.** Set `SchematicItemBatchResult.connectivity_assertion_verified = true` and push normally.

**Validation.** The following are rejected before any operation is applied, with the existing `Atomic operation N rejected:` prefix:
- more than one assertion;
- an assertion that is not the last operation;
- `target_document` set;
- `version != 1`;
- an empty group, or a pin that appears twice;
- a non-canonical `KIID`, or a path that is not a loaded instance;
- a missing `operation_id` or `expected_revision`;
- any other operation kind besides `create`, `update` and `replace_library_cache` in the same batch.

No editor frame is required.

### 8.2 Checked controller (`common/api/checked_schematic_controller.cpp`)

| Native outcome | Receipt |
|---|---|
| Rejection whose message starts with `connectivity_postcondition_failed:` and whose state is unchanged | `CSBS_REJECTED` with `error_code = "connectivity_postcondition_failed"` |
| A batch that contained an assertion, completed without `connectivity_assertion_verified` | `CSBS_INDETERMINATE` with `connectivity_assertion_unverified` |

### 8.3 Capability

`AutomationSession.capabilities` gains `schematic.connection-realization.v1`. It is advertised only when all of the following are present:
- the assertion;
- `item_candidates` measurement;
- the pin power fields;
- `incomplete_reason`.

---

## 9. Execution, resolution and recovery (lane C)

### 9.1 Executor (`SchematicSynchronizationExecutor.ApplyAsync`)

When `plan.Connections != null`:
1. Keep the existing request, desired-file, plan, checkpoint and staleness checks. `CandidateXml == null` is accepted only in this branch. The no-op shortcut and the ordinary batch builder are skipped.
2. The handshake must list `schematic.connection-realization.v1`, otherwise `native_capability_missing`.
3. `policy = SchematicConnectionPolicy.FromSnapshot(checkpoint.Electrical.Hierarchy.Data)`, then `realization = await SchematicConnectionRealizer.RealizeAsync(plan.Connections, plan.Candidate, checkpoint, measure, policy, token)`.
4. `plannedBytes = UTF8(SchematicDesignXml.Write(realization.Design))`. Build the batch as in §6.8.
5. In one `store.Save`, record `PendingMutation = batch`, `PendingNativeState = checkpoint.State` and `PendingLayout = DesignLayoutIntent.Create(designPath, original, plannedBytes, operationId, token)`. Checkpoint `"layout-prepared"`.
6. `ResolveLayoutAsync`, then the existing `ResumeAsync`. `RequireCandidate` stays as defence in depth.

### 9.2 `ResolveLayoutAsync` dispatch

- If the batch contains `assert_connectivity`, the realization rules below apply. Otherwise the existing `SchematicLayoutResolution` path runs unchanged.
- **Receipt `CSBS_REJECTED` with `error_code == "connectivity_postcondition_failed"`:**
  - call `store.AbandonRejectedRealization(saved, receipt)` (§9.3);
  - throw `realization_connectivity_mismatch`, whose message carries the bounded native detail;
  - the XML stays unchanged and the baseline does not advance.
- **Any other non-completed receipt:** existing behaviour (`native_sync_not_committed`, pending state kept).
- **`CSBS_COMPLETED`:**
  - require `receipt.Result.ConnectivityAssertionVerified`, otherwise `realization_assertion_unverified` (pending state kept);
  - capture state, which must equal `receipt.ObservedAfter`, otherwise `native_changed_during_sync`;
  - call `SchematicConnectionResolution.Resolve` (§9.4);
  - then `PendingLayout = null` and `PendingPublication = Create(candidate bytes)`, and checkpoint `"layout-resolved"`.

### 9.3 Recovery store (`DesignRecoveryStore.cs`)

**Validation.** A `PendingLayout` is valid when `PendingMutation` matches exactly one of two forms:
- (a) the existing form: at least one move, transform or lock operation, and no assertion; or
- (b) the new realization form:
  - exactly one `assert_connectivity`, and it is the last operation;
  - every other operation is `create`, `update` or `replace_library_cache`;
  - at least one `create` or `update`.

Anything else → `invalid_layout_intent`.

**Envelope version.** Version 10 is required when `PendingMutation` contains an assertion. Otherwise the existing rules (1–9) apply unchanged. Older services reject version 10 with `invalid_design_recovery`, which fails closed.

**`AbandonRejectedRealization(StoredDesignRecovery saved, CheckedSchematicBatchReceipt receipt)`.** Accept only if all of the following hold:
- the current revision token matches;
- `PendingLayout` and an assertion batch are present, and `PendingPublication == null`;
- `receipt.OperationId` and `Document` match the batch;
- `ExpectedRequestVerified`;
- `Status == CsbsRejected` and `ErrorCode == "connectivity_postcondition_failed"`;
- `Result == null`;
- `ObservedBefore == PendingNativeState == ObservedAfter`.

It then saves the state with `PendingLayout`, `PendingMutation` and `PendingNativeState` cleared, leaving everything else (`Baseline`, `DesiredFileBytes`, `LastSynchronization`, `Observed*`) unchanged. `ValidateTransition` allows this shape only through this method. Any violation → `invalid_realization_rejection`.

### 9.4 `SchematicConnectionResolution.Resolve(planned, before, native, batch, result, libraries)`

1. Bindings resolve with no reference, value or unit differences. Sheet paths are unchanged. Screen metadata equals the planned metadata, including `cached_symbols`.
2. Items that existed before are equal to `before`. The exception is `SheetSymbol`s that gained generated pins: compare them without those pins, and compare pins as a set by ID.
3. Created symbols equal the planned symbols exactly.
4. Generated items exist with the planned type. Connection geometry and identity must be equal:
   - line: `type`, `start`, `end`;
   - junction: `position`;
   - label: `position`, `text.text_`, `spin_style`, `shape`, `locked`;
   - sheet pin: `position`, `side`, `shape`, `spin_style`, `text.text_`, `locked`.
5. Native values are adopted **only** for:
   - line `stroke`, `start_ending`, `end_ending`;
   - junction `diameter`, `color`;
   - label `text` fields other than `text_`, plus `fields`, `fields_autoplaced` and `intersheet_refs_field`;
   - sheet-pin `text` fields other than `text_`.

   Lane C freezes this list from the first native journey run. Any change to it is a contract amendment.
6. Any other difference → `realization_resolution_mismatch`, and the pending state is kept (as for connected moves).
7. `candidate = planned with { Schematic = native }`. `Compare(candidate, native)` must be complete and equivalent, otherwise `realization_resolution_mismatch`.

Only the `Schematic` section differs between planned and resolved, which satisfies the existing layout transition invariant.

### 9.5 Automatic sync and MCP

- `AutomaticDesignSynchronization` needs no structural change. A realization plan has `CanPrepare == true`. Every thrown code pauses the session, and nothing retries until an explicit resume.
- `kicad_design_sync_plan` (`RecoveryTools.cs`, lane A):
  - returns `candidateDesignXml: null`;
  - adds `connectionRealizationRequired` and `connectionIntent`. The intent is a JSON summary: nets with scope and global name, islands with label text, members, roles, `requiresStub` and `joinRequired`, ports, and the expected group count.

---

## 10. Connection-aware initial placement (lane D, p1f9bc)

**Admission.** `SchematicInitialLayoutPlanner.ProposeMeasuredAsync` accepts a diff when `IsSupportedAddition` holds or `Classify(...).Kind == Admitted`. Anything else gets the existing `unsupported_layout_creation`. The final validation calls `Project(..., allowConnected: true)`. The seeded plan supplies `plan.Connections`.

**Model** (`InitialSchematicLayout.cs`):
- `InitialLayoutBody` gains trailing optional `PresentationPoint? PreferredAnchor` and `PresentationBounds? ReservedRelativeBounds`. `ReservedRelativeBounds` must be a superset of `RelativeBounds` and is used for fit and collision.
- Preferred point: `PreferredAnchor` takes precedence over the functional-group centre.
- Body order: `(FixedAnchor is null, PreferredAnchor is null, SheetId, FunctionalGroupId ?? Id, Id)`. Unconnected additions have no `PreferredAnchor`, so their output stays byte-identical.

**Preferred anchor.**
- Partners are the measured anchors of same-net members on the same physical screen that are existing, or new with an explicit placement.
- Formula: snap to grid (mean partner anchor − mean own member-pin offset).
- No partner → null.

**Reservation.** On each side of the body that has a `RequiresStub` pin, reserve `2·Grid` of stub plus the depth of the measured label prototype on that side (via `item_candidates`).

**Power attachment.**
- New coordinate-free single-pin global carriers, ordered by component ID, are paired greedily with same-net, same-screen `RequiresStub` Signal members, ordered by `(ComponentId, Pin, PlacedPinId)`.
- Rotation: measure four probes (rotations 0/90/180/270, no mirror; `Probe` IDs). Choose the smallest rotation whose probe pin body direction equals the partner's outward direction.
- `FixedAnchor = StubEnd(partnerAnchor, outward, 2·Grid) − (probePinAnchor − probeAnchor)`.
- A collision or page overflow drops the pairing and uses free placement.
- Report the pairs as `powerAttachments` in the result.

**MCP.** `kicad_design_propose_initial_layout` keeps its signature; the result adds `powerAttachments` and `preferredAnchors`. `LayoutRefinement.ForAddedSymbols` is unchanged.

---

## 11. Exact variant pin identities (lane D, p20323; interface reserved now)

**Now (M1 lane B).** `PackSchematicPinGeometry` sets `incomplete_reason` at every early return:
- unresolved definition → `SPGIR_DEFINITION_UNRESOLVED`;
- missing placed or owned identity → `SPGIR_PLACED_IDENTITY_MISSING`;
- alternate-variant similarity guard → `SPGIR_VARIANT_PIN_MAPPING_UNRESOLVED`.

The §5.7 gate stays in force.

**p20323** needs a separate seam freeze by the integration owner; the numbers are reserved in `proto_reservations`.
- **Persistent mapping.** `SchematicSymbolVariant.pin_links` (field 7) is an explicit, complete map from placed pin ID to the variant definition's owned library-pin ID.
  - It is persisted in `.kicad_sch`, with a native format bump and an update to `SchematicItemDelta.SupportedWriterFormatVersion`.
  - It is never derived by name, number, position or order. A missing map with an override means unresolved.
  - It replaces the similarity-based `MapLibPins` on API paths.
- **Measurement.** `SchematicSymbolPinGeometry.variants` (field 5) lists every variant's geometry, with pin IDs equal to the persistent placed IDs. It is trusted only under the capability `schematic.pin.variant-geometry.v1`.
- **Gate lift**, which the integration owner flips after qualification. For every realized pin:
  - every variant must be complete;
  - every variant must map the same placed pin, otherwise `realization_variant_pin_missing`;
  - every variant must have an identical anchor and body direction, otherwise `realization_variant_geometry_divergent`.

  Only then does §5.7 relax to "every override has a complete `pin_links`".

---

## 12. Reverse synchronization of generated items

1. After commit, generated items are ordinary native items stored in the schematic section. They carry no provenance flag or ledger, and deterministic IDs are never used to recognise them later.
2. Native edits that keep the pin partition (moving a stub, restyling or renaming a label) are layout changes. They merge through the existing hierarchy path. Model nets do not change, because label names are not identities.
3. Deleting or rewiring generated items changes the partition. The existing `SchematicNetReconciliation`/`PinPartitionEvolution` then updates the model: split nets, with requirement bindings left unresolved. Nothing is ever regenerated or repaired automatically.
4. One native undo removes the created symbols and every generated item, because they were one commit. The existing removal projection reverse-syncs the removal; redo goes through restoration.
5. A later XML edit that re-adds the connection gets a new native revision salt, so no UUID is reused.
6. XML-side removal of components that carry generated items is not retired by CN-1: generated wires and labels remain as native items (open question).

---

## 13. Error codes

The parent seam declares every string below as a constant in `SchematicConnectionErrors`.

### Planning (no native mutation)

| Code | Stage |
|---|---|
| `xml_disconnection_unsupported` | classify |
| `connected_pin_unresolved` | intent |
| `connected_pin_ambiguous` | intent |
| `connected_pin_identity_missing` | intent |
| `connected_pin_driver_conflict` | intent |
| `connected_variant_symbol_unsupported` | intent |
| `connected_bus_realization_unsupported` | intent |
| `connected_global_name_conflict` | intent |
| `connected_implicit_power_conflict` | intent |
| `connected_power_name_unresolved` | intent |
| `connected_net_name_conflict` | intent |
| `connected_label_text_invalid` | intent |
| `connected_label_text_divergent` | intent |
| `connected_repeated_screen_divergent` | intent |
| `connected_hierarchy_unsupported` | intent |
| `connected_locked_sheet_symbol` | intent |
| `connected_scope_too_large` | intent |
| `connected_internal_inconsistency` | intent |
| `connected_addition_unavailable` | seam stub only; must not exist at freeze completion |

### Realization (no native mutation)

| Code | Stage |
|---|---|
| `native_capability_missing` | execute |
| `realization_grid_unavailable` | realize |
| `realization_measurement_unsupported` | realize |
| `realization_measurement_stale` | realize |
| `realization_measurement_incomplete` | realize |
| `realization_pin_geometry_incomplete` | realize |
| `realization_variant_pin_identity_unresolved` | realize |
| `realization_pin_geometry_mismatch` | realize |
| `realization_implicit_power_mismatch` | realize |
| `realization_label_orientation_mismatch` | realize |
| `realization_no_connect_conflict` | realize |
| `realization_created_pin_contact` | realize |
| `realization_no_free_stub` | realize |
| `realization_no_join_anchor` | realize |
| `realization_no_free_sheet_pin_slot` | realize |
| `realization_identity_collision` | realize |
| `realization_batch_too_large` | realize |
| `realization_variant_pin_missing` | realize (post-p20323) |
| `realization_variant_geometry_divergent` | realize (post-p20323) |

### Native receipt `error_code` strings

| Code | Status | Native mutation |
|---|---|---|
| `connectivity_postcondition_failed` | REJECTED | none, by proof |
| `connectivity_assertion_unverified` | INDETERMINATE | inspect |

### Execution

| Code | Meaning |
|---|---|
| `realization_connectivity_mismatch` | Assertion failed; pending state cleared; no mutation. |
| `realization_assertion_unverified` | Completed receipt lacks the verification flag; pending state kept. |
| `realization_resolution_mismatch` | Committed and verified, but the projection differs; pending state kept. |
| `invalid_realization_rejection` | Store refused an abandonment request. |
| `invalid_layout_intent` | Existing code, with the rule extended in §9.3. |

### Diagnostics (non-blocking)

`existing_net_named_by_realization`, `realization_page_reservations_unspecified`.

### Reused unchanged

- every `SchematicNativeCreationProjection` code;
- `creation_bindings_changed`, `creation_requires_stable_native_hierarchy`, `creation_requires_stable_connectivity`, `unaligned_electrical_baseline`;
- `created_binding_invalid`, `created_symbol_placement_required`;
- `inconsistent_design_serialization`;
- `native_checkpoint_stale`, `native_file_conflict`, `native_sync_not_committed`, `native_changed_during_sync`, `native_sync_connectivity_mismatch`;
- `publication_target_changed`, `instance_changed`;
- `unsupported_layout_creation`.

---

## 14. Versioning and compatibility

- **Protobuf.** Additive only. Older native peers fail closed:
  - `MeasureSchematicPlacement` with new fields → "Placement measurement contains unsupported fields";
  - an unknown operation kind → "Operation kind is missing", which the controller maps to `native_batch_rejected`.

  The managed side also refuses up front without the capability. Enum value 0 means "none/unspecified or legacy peer".
- **Recovery envelope.** Version 10 applies only with a realization mutation; versions 1–9 stay readable. The number is subject to allocation by the integration owner (§open questions).
- **`design.xml`.** No change. **Native file format:** unchanged for M1 and M2; p20323 bumps it.
- **In-memory contract.** `SchematicConnectionIntent.Version = 1`. Hash namespaces `kicad-connection-realization-v1` and `kicad-connection-probe-v1`; changing either algorithm requires a new namespace.
- **MCP.** Additive output fields only. No new tool.
- **Error codes** are API: codes may be added, never renamed.

---

## 15. Examples

### Root addition

- **Baseline:** R1.1 is in SIG and has a `LocalLabel "SIG"` at the root. TP1.1 is bare.
- **XML adds:** placed R2 (existing template), with R2.1 in SIG and `{R2.2, TP1.1}` as a new net named `/OUT`.
- **Intent:**
  - SIG island: Local, T = "SIG" (anchor driver), `AnchorHasMatchingDriver`; R2.1 requires a stub.
  - OUT island: T = "OUT"; R2.2 and TP1.1 require stubs.
- **Batch:** create R2 (plus a cache entry if needed), 3 `SchematicLine` wires, 3 `LocalLabel`s, then `assert_connectivity` with groups:
  - `{SIG group ∪ R2.1}`;
  - `{R2.2, TP1.1}`;
  - a singleton for each other R2 pin.
- **Result:** one commit and one undo step. XML is published with the generated items.

### Hierarchy crossing

- New U1.3 in child instance C (screen S) joins net DATA with bare root pin R5.2. The LCA is root.
- **On S:** a `stub-wire` plus `HierarchicalLabel "DATA"` at U1.3.
- **On root:**
  - a `SheetSymbol` update appending `SheetPin "DATA"` on the side facing R5.2;
  - `sheet-pin-wire` plus `LocalLabel "DATA"`;
  - `stub-wire` plus `LocalLabel "DATA"` at R5.2.
- **Repeated children C1 and C2 whose crossings are uniform:** one shared hierarchical label and two sheet-pin appends. If only one child crosses → `connected_repeated_screen_divergent` with no change.

### Power

- GND contains the existing model component `#PWR01` (`SST_GLOBAL_POWER`, value GND). A new U2 GND pin gets a stub plus `GlobalLabel "GND"`.
- A new legacy IC with a hidden power-in pin "VCC" in net VCC: that pin gets nothing. Its other new member gets `GlobalLabel "VCC"`.
- A coordinate-free declared `#PWR02` paired by §10 lands at the stub end and gets a wire-only stub.
- Putting the hidden VCC pin in another net → `connected_implicit_power_conflict`.

---

## 16. Lanes and sequencing

1. **Parent seam lands and is frozen:** protos, `SchematicConnectionContract.cs`, a fully implemented `Classify` with its tests, plan field and dispatch, the store version-10 and validation rule, the `AbandonRejectedRealization` signature, and stubs that throw `connected_addition_unavailable`.
2. **Lanes A, B, C and D run in parallel.**
   - Lane B builds against recorded measurement fixtures.
   - Lane C builds the native assertion first, then executor and resolution against the stubs.
   - Lane D builds placement against A's intent records.
3. **Integration order:** A → B → C (end-to-end journeys and capability advertisement) → D placement → M2 (B) → p20323 (D, after a new seam freeze).
