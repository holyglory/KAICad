> **Frozen 2026-09-23 by the integration owner** (decision `kicad-phase2-contracts-frozen-20260923`).
> Field numbering: fields this contract fixes are declared by the parent seam at the freeze, sequentially
> below 100; later lane additions use the lane bands of `psu-cpu-fixture-and-ownership.md`
> (2A 100-199, 2B 200-299, 2C 300-399, 2D 400-499, 500-999 Phase 3). Items marked owner-pending below
> use the stated default until the owner decides; changing them later is a contract revision.

> **Errata 2026-09-23** (decision `kicad-phase2-contract-errata-20260923`): the expected native result has
> **11** placements (K20:0x10 to 0x1a), not 12; the standard `Device:R` pins have empty names in this file format
> (a lone `~` reads as empty before format 20250318); `fixture.json` `normalizedAt` records the base commit because a
> commit cannot contain its own hash. The parent graphs are `psu-cpu-fixture` (gating, including the native seed
> check `NativePsuCpuSeed`) and `psu-cpu-fixture-writer-drift` (non-gating); a writer-drift failure alone means the
> symbol writer changed and triggers a version-2 re-freeze, not a lane block. CN-1 parent-seam behaviour
> (classification, policy/geometry/identity helpers, `allowConnected`) is built by lane 2A; the recovery-store
> version-10 rule and `AbandonRejectedRealization` are landed by the parent at the freeze integration. The rebuild
> seam used by lane 2C is provisional.

> **Errata 2026-09-24** (decision `kicad-stacked-pins-one-node-20260924`, ne741e8800f5b394f): the LP3982 symbol draws
> pins 1 and 4 at the same point, and KiCad always joins pins that one placed symbol's own definition stacks at one
> point. §1.6.3 "Other stages" therefore reads: `Components`: same sheets and symbols; every pin is alone in its native
> net except U2 pins 1 and 4, which KiCad shows joined in one native net of exactly those two pins; no labels or sheet
> pins. The same holds for `PsuComponents`, which places the same U2. `ExpectedNative` lists the pair as `JoinedPins`
> (derived from the exact definition geometry of `lib_symbols.kicad_sexpr`, never from names) and leaves it out of
> `IsolatedPins`: 220 isolated pins in `Components`, 35 in `PsuComponents`. `Complete` is unchanged: both pins are
> already in RAIL_B. The electrical comparison treats each stacked group as one node, and XML that puts stacked pins of
> one symbol on different nets is refused while planning with `stacked_pins_on_different_nets`, before KiCad changes.
> The frozen fixture files are unchanged.

# KAICad Phase 2 shared contract — PSU→CPU acceptance fixture and lane file ownership

Base read: worktree `the codex/finalization-integration worktree`, branch `codex/finalization-integration`. HEAD moved from `0da1dcdbd8` to `86dbd67c5d` while I was reading. That merge touched only `NativeStructuralEditorJourney.cs` and `pcbnew/api/pcb_drc_run_inputs.h`, so nothing below is affected. The work was read-only: I edited nothing, ran no build or test, and wrote no Coordinator record.

Sources:
- Approved plan the approved finalization plan (2026-09-23): Phase 1 "Fix the shared interfaces" items 3 and 4, the Phase 2 lane scopes, and the parent-owned hot files.
- Coordinator decisions `kicad-single-per-level-diagram-editor-20260923` (nab403c279a16e4fa) and `kaicad-finalization-full-scope-20260923` (n67463edd3942a80c).
- `automation/design/recursive-structural-refinement-plan.md` §§1–13.
- `automation/design/history-and-conflicts/interaction-specification.md`.
- `automation/design/level-diagram-walkthrough/imagegen-prompts.md`.
- `automation/qualification/agent-refinement-contract.md`, which requires the acceptance fixture to have "a component whose units appear on different schematic sheets".
- `automation/README.md`.

Parent items 1 (wiring and net-realization intent contract) and 2 (`recursive-block-graph` v2 schema) are specified elsewhere. This document only reserves room for them.

## 0. Binding rules this contract encodes

1. The fixture is test data, not an engineering recommendation. Pin choices are declared. Only the STM32C011J pin roles are backed by alternates declared in its own repository symbol. The F28P659 GPIO choices are not checked against a datasheet. No voltage, current or other value is invented; R1's value stays the library default `R`.
2. Exact identities only. Every pin connection is declared by component ID and pin number. Nothing is matched by name or position:
   - The CPU memory SDA endpoint deliberately stays a compatibility selector, so no net exists for it and the memory SDA pin stays unconnected.
   - Root-level links stay abstract (not realized as nets), because no explicit cross-level realization exists in v1.
   - A cross-level flat connection is left out of the conversion and recorded in the receipt.
3. Functional containment and physical placement are separate. The sheet hierarchy (Root, PSU, CPU, CPU_POWER) differs from the block hierarchy. Processor unit 4 sits on the CPU_POWER sheet, which has no block of its own.
4. Save/Decline and the agent console are untouched. No fixture journey may invoke an agent. Automatic synchronization reads saved files only, never an unsaved editor draft.
5. XML stays behind the normal UI. Fixture XML is written by the harness or by tools; no journey asks a person to open or edit XML.
6. An unchanged save or synchronization is a no-op, and old revisions are never rewritten.
7. The Mac/Windows hold stays in force. All evidence named here is Linux evidence.
8. Design Round A gates 2B's new canvas controls (`ui-design-gate`). 2B's model, codec, migration and tool work does not wait for it.

---

## 1. Shared fixture `psu-cpu`, version 1

### 1.1 Location and files (parent-owned, frozen)

Directory: `automation/tests/fixtures/psu-cpu/` (new). The existing `automation/tests/fixtures/` holds only native helper sources. Journeys find it via `FindRoot()`, the same way `NativeInferredNetChainJourney` reads `qa/data/...`.

| File | Produced by | Content |
|---|---|---|
| `fixture.json` | parent | `{"fixture":"psu-cpu","version":1,"idPrefix":"7e57f1c5-0000-4000-8000-","files":{name:sha256},"symbols":[{cacheKey,sourcePath,gitBlob}],"normalizedAt":"<freeze commit>"}` |
| `parts.json` | parent, derived from the sources in §1.3 | 8 parts and all 222 pins as `{number,name,unit}`, with `cacheKey`, `library`, `entry`, `units` and source provenance |
| `lib_symbols.kicad_sexpr` | parent, see below | one `(lib_symbols …)` list holding the 8 definitions under cache names `Lib:Entry` |
| `hardware.xml` | `HardwareRepositoryXml.Write(H1)` | §1.4.1 |
| `design.engineering.xml` | `EngineeringDesignXml.Write(E1, [])` | complete circuit (§1.4.2) and an empty structure (ID K02:2) |
| `flat-structure.engineering.xml` | `EngineeringDesignXml.Write(F1, [])` | the E1 circuit, the legacy flat structure and its presentation (§1.8) |
| `system.blocks.xml` | `RecursiveBlockGraphXml.Write(G1)`, schema v1 | §1.5 |
| `expected-native.json` | parent | §1.6.4 |
| `expected-realization.json` | parent | §1.7 |
| `expected-migration.json` | parent | §1.8 |

How `lib_symbols.kicad_sexpr` is made: the definitions are copied from the sources in §1.3. For `Device:R`, the library-file entry `R` is renamed to the cache name `Device:R`. The parent then normalizes the list once, at the freeze commit, with this fork's eeschema (current `SEXPR_SCHEMATIC_FILE_VERSION` 20260912): load the S0 catalog, save it, and take the saved `lib_symbols`. This is needed because the sources span format versions 20220904 to 20260830.

Parent-owned C# alongside the files:
- `automation/tests/KiCad.Automation.Tests/PsuCpuFixture.cs`: loader, ID constants, stage filters, native seeds and assertions.
- `PsuCpuFixtureBuilder.cs`: builds H1, E1, F1 and G1 from the constants and `parts.json`.
- `PsuCpuFixtureTests.cs`: integrity checks.

The checked-in XML must equal the builder output byte for byte.

### 1.2 Deterministic identities

`PsuCpuIds.Id(kind, n) = Guid.ParseExact($"7e57f1c5-0000-4000-8000-{kind:x4}{n:x8}", "D")`, lowercase. Below, `Kkk:n` is shorthand for `Id(0xkk, n)`.

| Kind | Entity (ordinals) |
|---|---|
| 01 | 1 repository "PSU-CPU acceptance fixture"; 2 design "Controller"; 3 hardware port "DC input" |
| 02 | 1 circuit; 2 empty structural diagram inside `design.engineering.xml` |
| 03 | parts 1–8 (§1.3 order) |
| 04 | sheet definitions: 1 System (root), 2 PSU, 3 CPU, 4 CPU_POWER |
| 05 | sheet instances: 1 root, 2 PSU (parent 1), 3 CPU (parent 1), 4 CPU_POWER (parent 3) |
| 06 | component definitions: 1 J1, 2 U1, 3 R1, 4 U2, 5 U3, 6 U4 (in sheet definition 2); 7 U5, 8 U6 (in sheet definition 3) |
| 07 | component instances, same ordinals as kind 06 |
| 08 | nets 1–11 (§1.4.2 order) |
| 09 | symbol occurrences 1–11 (§1.4.2) |
| 10 | 1 block-graph document |
| 11, 12, 13, 14 | blocks, block states, block revisions, block requirement revisions: 1 System, 2 PSU, 3 CPU, 4 DC-DC, 5 LDO, 6 Telemetry ADC, 7 Telemetry MCU, 8 Processor, 9 Memory |
| 15 | boundary interfaces 0x01–0x17 (§1.5.2) |
| 16, 17, 18, 19 | connections, connection states, connection revisions, connection requirement revisions 0x01–0x1f (§1.5.3) |
| 1a | annotations 1–5 |
| 20 | native seed IDs (§1.6.1); 0x10–0x1b for S0 catalog symbols |
| 21 | normalized definition-pin UUIDs 1–222: sort by (cache key, ordinal compare; unit; body style; pin number, ordinal compare) and number from 1 |
| 30 | flat structure F1: 1 diagram; 0x10 the cross-level connection; 0x20 the PSU intent statement |

Fixed origin for every revision in the fixture: `RequirementRevisionOrigin(User, "PSU-CPU fixture", 2026-09-23T00:00:00Z, "Frozen PSU-CPU acceptance fixture v1", [], [])`.

### 1.3 Parts (b): real symbols already in the repository

| # | Cache key / lib_id | Part name = Value | Units | Pins | Source (git blob) |
|---|---|---|---|---|---|
| 1 | `Connector_Generic:Conn_01x02` | Conn_01x02 | 1 | 2: 1 Pin_1, 2 Pin_2 | `qa/data/pcbnew/issue21739/topology_mismatch.kicad_sch` (aa19dde7083e) |
| 2 | `Regulator_Switching:LM2595S-ADJ` | LM2595S-ADJ | 1 | 5: 1 OUT, 2 VIN, 3 GND, 4 FB, 5 ON/~{OFF} | `qa/data/pcbnew/issue23658/issue23658.kicad_sch` (7ca406bfd263) |
| 3 | `Device:R` | R | 1 | 2: 1 ~, 2 ~ | `qa/data/libraries/Device.kicad_sym`, entry `R` (44d6aed53aab) |
| 4 | `Regulator_Linear:LP3982ILD-3.3` | LP3982ILD-3.3 | 1 | 9: 1 OUT, 2 IN, 3 GND, 4 OUT (hidden, stacked on pin 1), 5 NC (hidden), 6 CC, 7 ~{SHDN}, 8 ~{FAULT}, 9 EP | `qa/data/eeschema/issue6588.kicad_sch` (16198a2086e2) |
| 5 | `Battery_Management:LTC2959` | LTC2959 | 1 | 11: 1 V_{DD}, 2 SENSEP, 3 CFP, 4 CFN, 5 SENSEN, 6 SCL, 7 SDA, 8 GPIO, 9 V_{REG}, 10 GND, 11 EP (hidden, no_connect) | `qa/data/pcbnew/issue24474/issue24474.kicad_sch` (29e8bc8c5dc5) |
| 6 | `MCU_ST_STM32C0:STM32C011J_4-6_Mx` | STM32C011J_4-6_Mx | 1 | 8: 1 PB7/PC14, 2 VDD, 3 VSS, 4 PA0/PA1/PA2/PF2, 5 PA8/PA9/PA11, 6 PA10/PA12, 7 PA13, 8 PA14/PB6/PC15 (147 alternates, none active) | `qa/data/eeschema/api_kitchen_sink.kicad_sch` (d6e844a33232) |
| 7 | `Library:F28P659DK8PTPQ1` | F28P659DK8PTPQ1 | 4 | 177: unit 1 36, unit 2 98, unit 3 12, unit 4 31; no duplicate numbers; used pins 3 VDDIO (u4), 177 VSS (u4), 73 GPIO29 (u2), 74 GPIO28 (u2), 161 GPIO1 (u2) | `qa/data/eeschema/issue19646/MCU.kicad_sch` (019e6b225fd3) |
| 8 | `pic_programmer:24C16` | 24C16 | 1 | 8: 1 A0, 2 A1, 3 A2, 4 GND (unit 0, common), 5 SDA, 6 SCL, 7 WP, 8 VCC (unit 0) | `qa/data/cli/variants/pic_sockets.kicad_sch` (d02d7bc49709) |

- `PartDefinition(Kxx:03:n, Name, Units, Pins)`. Pins are exactly the `(number, name, unit)` triples of the source body style 1 plus the style-0 common pins. That is 222 pins in total, and it is what `SchematicPartSymbols.Validate` checks.
- No pin name or number contains a space, and no source symbol uses `extends`.
- Declarations are `SchematicPartSymbol(PartId, LibraryIdentifier(nick, entry), captured SchematicCachedSymbol, BodyStyle: 1)`.
- Footprint and datasheet fields come from the definition and are not asserted.

### 1.4 Engineering XML (b)

#### 1.4.1 `hardware.xml` (H1)

- Repository K01:1, "PSU-CPU acceptance fixture".
- One design K01:2 "Controller": `project="fixture.kicad_pro"`, `model="design.xml"`. Paths are relative to the native project directory, which is the repository root in journeys. The harness keeps the project stem `fixture`.
- One port K01:3 "DC input", described as "External supply connector J1 (fixture data)."
- No documents, libraries or interfaces; a single design cannot have inter-board interfaces.
- `design.xml` is the runtime design:1 file (§1.9) and is not checked in, because it contains the live native snapshot.

#### 1.4.2 Circuit E1 (`Circuit` K02:1)

Sheets:

| Definition / instance | Name | Parent instance | Components |
|---|---|---|---|
| K04:1 / K05:1 | System | — | — |
| K04:2 / K05:2 | PSU | K05:1 | J1, U1, R1, U2, U3, U4 |
| K04:3 / K05:3 | CPU | K05:1 | U5, U6 |
| K04:4 / K05:4 | CPU_POWER | K05:3 | — (hosts only U5 unit 4 through an occurrence override) |

Components: `ComponentDefinition(K06:n, part, Value=part name)` and `ComponentInstance(K07:n, K06:n, sheet instance, reference)`:

| n | Ref | Part | Instance sheet |
|---|---|---|---|
| 1 | J1 | 1 | K05:2 |
| 2 | U1 | 2 | K05:2 |
| 3 | R1 | 3 | K05:2 |
| 4 | U2 | 4 | K05:2 |
| 5 | U3 | 5 | K05:2 |
| 6 | U4 | 6 | K05:2 |
| 7 | U5 | 7 | K05:3 |
| 8 | U6 | 8 | K05:3 |

Symbol occurrences `SymbolOccurrence(K09:n, component, unit, Placement=null, SheetInstanceId)`:
- 1 J1u1, 2 U1u1, 3 R1u1, 4 U2u1, 5 U3u1, 6 U4u1: sheet K05:2.
- 7 U5u1, 8 U5u2, 9 U5u3: sheet K05:3.
- 10 U5u4: **SheetInstanceId = K05:4**.
- 11 U6u1: sheet K05:3.
- Every occurrence is coordinate-free; layout is realization output.

Nets `CircuitNet(K08:n, name, pins)` (41 connected pins):

| n | Name | Pins (Ref.number pin name) |
|---|---|---|
| 1 | VIN | J1.1 Pin_1, U1.2 VIN |
| 2 | GND | J1.2 Pin_2, U1.3 GND, U2.3 GND, U2.9 EP, U3.10 GND, U4.3 VSS, U5.177 VSS, U6.1 A0, U6.2 A1, U6.3 A2, U6.4 GND, U6.7 WP |
| 3 | DCDC_OUT | U1.1 OUT, U1.4 FB, R1.1 ~, U3.1 V_{DD}, U3.2 SENSEP |
| 4 | RAIL_A | R1.2 ~, U2.2 IN, U2.7 ~{SHDN}, U3.5 SENSEN, U4.2 VDD, U5.3 VDDIO |
| 5 | RAIL_B | U2.1 OUT, U2.4 OUT, U3.8 GPIO, U6.8 VCC |
| 6 | LDO_FAULT | U2.8 ~{FAULT}, U4.4 PA0/PA1/PA2/PF2 |
| 7 | PSU_SCL | U3.6 SCL, U4.5 PA8/PA9/PA11 (symbol alternate I2C1_SCL on PA9) |
| 8 | PSU_SDA | U3.7 SDA, U4.6 PA10/PA12 (alternate I2C1_SDA on PA10) |
| 9 | TELEM_MCU_TO_CPU | U4.8 PA14/PB6/PC15 (alternate USART1_TX on PB6), U5.74 GPIO28 |
| 10 | TELEM_CPU_TO_MCU | U4.1 PB7/PC14 (alternate USART1_RX on PB7), U5.73 GPIO29 |
| 11 | MEM_SCL | U5.161 GPIO1, U6.6 SCL |

Unconnected pins (181), which must stay unconnected with no marker added:
- U1.5
- U2.5 and U2.6
- U3.3, U3.4, U3.9 and U3.11
- U4.7
- every U5 pin except 3, 73, 74, 161 and 177 (172 pins)
- U6.5 (the intentionally unresolved memory SDA)

Stages, as pure filters of E1 by identity that keep K02:1 and structure K02:2:

| Stage | Contents |
|---|---|
| `RootOnly` | sheet definition K04:1 and instance K05:1 only |
| `SheetsOnly` | all 4 sheet definitions and instances; no parts, components, occurrences or nets |
| `PsuComponents` | SheetsOnly + parts 1–6 + components 1–6 + occurrences 1–6; no nets |
| `Components` | E1 without nets |
| `Complete` | E1 |

### 1.5 Recursive block graph (a): `system.blocks.xml` (G1, v1, coordinate-free)

Document K10:1; selected root = System (K11:1, K12:1, K13:1). Each block has:
- exactly one state `BlockDesignState(K12:n, K11:n, "Initial approach", K13:n)`;
- one revision (parent null, origin as in §1.2);
- one requirement history with one revision K14:n.

Definition and physical allocation are null (unspecified) for every block.

#### 1.5.1 Blocks

| n | Name | Children (in order) | Boundary interfaces | Component bindings (K01:2, K02:1, …) | General / Schematic / Routing |
|---|---|---|---|---|---|
| 1 | System | PSU, CPU | 01 DC input | — | "" / "" / "" |
| 2 | PSU | DC-DC, LDO, Telemetry ADC, Telemetry MCU | 02 DC input, 03 Power, 04 Telemetry | J1 | "Supply CPU power and report rail status." / "Separate power conversion and telemetry." / "Keep high-current paths away from sensing." |
| 3 | CPU | Processor, Memory | 05 Power, 06 Telemetry | — | "" / "" / "" |
| 4 | DC-DC | — | 07 Input, 08 Output, 09 Sense | U1, R1 | "" |
| 5 | LDO | — | 0a Input, 0b Output, 0c Fault | U2 | "" |
| 6 | Telemetry ADC | — | 0d Rail A sense, 0e Rail B sense, 0f Measurements | U3 | "" |
| 7 | Telemetry MCU | — | 10 Measurements, 11 Fault, 12 Telemetry | U4 | "" |
| 8 | Processor | — | 13 Power, 14 Telemetry, 15 Memory | U5 | "" |
| 9 | Memory | — | 16 Power, 17 Data | U6 | "" |

Every interface's `Intent` is "".

#### 1.5.2 Endpoint notation

- `If(B,i)`: `DiagramEndpointBinding(Interface, B, K15:i, "", null, [], null)`.
- `Pin(B,i,Ref.p)`: kind `Pin`, `InterfaceId` K15:i, `DiagramPinTarget(K01:2, K07:ref, path, "p")`. The path is **[K05:1, owning sheet instance of the component]**: [K05:1, K05:2] for PSU parts and [K05:1, K05:3] for U5 and U6, even for U5 unit-4 pins.
- `Sel(B,i)`: kind `Compatible`, selector `(Role "I2C SDA", Protocol "I2C", RequiredFunctions ["I2C_SDA"], Sources [])`.

#### 1.5.3 Connections

Each connection has one state `ConnectionDesignState(K17:c, K16:c, "Initial interface", K18:c)` and one revision (parent null). Requirements are "" except connection 1d. A local diagram lists only its top-level connections; members are reachable through `Members`.

| c | Owner | Name | Kind | Endpoint A | Endpoint B | Members |
|---|---|---|---|---|---|---|
| 01 | System | DC input | Interface | If(System,01) | If(PSU,02) | — |
| 02 | System | Power | Interface | If(PSU,03) | If(CPU,05) | 03, 04, 05 |
| 03/04/05 | System | Rail A / Rail B / Return | Signal | If(PSU,03) | If(CPU,05) | — |
| 06 | System | Telemetry | Interface | If(PSU,04) | If(CPU,06) | 07, 08 |
| 07/08 | System | MCU to CPU / CPU to MCU | Signal | If(PSU,04) | If(CPU,06) | — |
| 09 | PSU | DC input | Signal | Pin(PSU,02,J1.1) | Pin(DC-DC,07,U1.2) | — |
| 0a | PSU | Rail A | Signal | Pin(DC-DC,08,R1.2) | If(PSU,03) | — |
| 0b | PSU | LDO supply | Signal | Pin(DC-DC,08,R1.2) | Pin(LDO,0a,U2.2) | — |
| 0c | PSU | Rail B | Signal | Pin(LDO,0b,U2.1) | If(PSU,03) | — |
| 0d | PSU | Rail A sense | SignalGroup | If(DC-DC,09) | If(Telemetry ADC,0d) | 0e, 0f |
| 0e | PSU | Sense+ | Signal | Pin(DC-DC,09,U1.1) | Pin(Telemetry ADC,0d,U3.2) | — |
| 0f | PSU | Sense- | Signal | Pin(DC-DC,09,R1.2) | Pin(Telemetry ADC,0d,U3.5) | — |
| 10 | PSU | Rail B sense | Signal | Pin(LDO,0b,U2.1) | Pin(Telemetry ADC,0e,U3.8) | — |
| 11 | PSU | Measurements | Interface | If(Telemetry ADC,0f) | If(Telemetry MCU,10) | 12, 13 |
| 12 | PSU | I2C SCL | Signal | Pin(Telemetry ADC,0f,U3.6) | Pin(Telemetry MCU,10,U4.5) | — |
| 13 | PSU | I2C SDA | Signal | Pin(Telemetry ADC,0f,U3.7) | Pin(Telemetry MCU,10,U4.6) | — |
| 14 | PSU | Fault | Signal | Pin(LDO,0c,U2.8) | Pin(Telemetry MCU,11,U4.4) | — |
| 15 | PSU | Telemetry | Interface | If(Telemetry MCU,12) | If(PSU,04) | 16, 17 |
| 16 | PSU | MCU to CPU | Signal | Pin(Telemetry MCU,12,U4.8) | If(PSU,04) | — |
| 17 | PSU | CPU to MCU | Signal | Pin(Telemetry MCU,12,U4.1) | If(PSU,04) | — |
| 18 | CPU | Rail A | Signal | If(CPU,05) | Pin(Processor,13,U5.3) | — |
| 19 | CPU | Rail B | Signal | If(CPU,05) | Pin(Memory,16,U6.8) | — |
| 1a | CPU | Supply status / control | Interface | If(CPU,06) | If(Processor,14) | 1b, 1c |
| 1b | CPU | MCU to CPU | Signal | If(CPU,06) | Pin(Processor,14,U5.74) | — |
| 1c | CPU | CPU to MCU | Signal | If(CPU,06) | Pin(Processor,14,U5.73) | — |
| 1d | CPU | Memory interface | Interface | If(Processor,15) | If(Memory,17) | 1e, 1f |
| 1e | CPU | I2C SCL | Signal | Pin(Processor,15,U5.161) | Pin(Memory,17,U6.6) | — |
| 1f | CPU | I2C SDA | Signal | **Sel(Processor,15)** | Pin(Memory,17,U6.5) | — |

Top-level connections per local diagram:
- System: 01, 02, 06.
- PSU: 09, 0a, 0b, 0c, 0d, 10, 11, 14, 15.
- CPU: 18, 19, 1a, 1d.

Connection 1d requirements: "Provide memory access; choose a compatible interface." / "Keep interface signals grouped and readable." / "Leave pin assignment open until placement."

Annotations (`DiagramAnnotation`, role Comment, no strokes, fixed origin, `units="diagram-unit"`):

| a | Diagram | Target | Position | Text |
|---|---|---|---|---|
| 1 | System | canvas | (40, 260) | "Keep PSU replaceable as a unit." |
| 2 | System | block PSU | — | "Explore a quieter supply." |
| 3 | PSU | canvas | (40, 300) | "Keep sensing away from switching nodes." |
| 4 | CPU | canvas | (40, 300) | "Compare memory-interface implementations." |
| 5 | CPU | connection 1d | — | "Keep pin choices open for placement." |

Invariants checked by `PsuCpuFixtureTests`:
- **G-1 Ownership totality.** In the selected-root closure, each circuit component is bound by exactly one block.
- **G-2** Every `Pin` endpoint's component is in the endpoint block's own bindings.
- **G-3** Every pin path equals [K05:1, ComponentInstance.SheetInstanceId].
- **G-4** Every pin number exists in the part.
- **G-5** Every `Pin`-only Signal has all its pins in one circuit net (§1.7).
- **G-6** `RecursiveBlockGraphXml` and `RecursiveBlockCodec` round trips are byte-identical.

### 1.6 Native seeds and expected native schematic (c)

#### 1.6.1 Native IDs (kind 20)

| n | Item |
|---|---|
| 1 | root screen uuid (`fixture.kicad_sch`) |
| 2 | sheet symbol PSU |
| 3 | sheet symbol CPU |
| 4 | sheet symbol CPU_POWER, inside `cpu.kicad_sch` |
| 5 | PSU screen |
| 6 | CPU screen |
| 7 | CPU_POWER screen |

The root sheet instance ID `R` is the native-created empty root that the harness already makes (`emptyRoot.SheetPath.Path[0]`).

#### 1.6.2 Seeds (written by `PsuCpuFixture`, format `(version 20250114)` like the existing fixtures, project stem `fixture`)

**S0 catalog (temporary, used only to capture definitions).**
- `fixture.kicad_sch`, paper A0.
- Contains `lib_symbols.kicad_sexpr` and the 12 unit placements, with symbol UUIDs K20:0x10–0x1b, a grid of 6 columns at 101.6 mm pitch and 2 rows at 203.2 mm pitch, the §1.4.2 references, and `(instances (project "fixture" (path "/R" …)))`.
- Load it, capture the 8 `CachedSymbols` from `ReadSchematicHierarchyData`, then normalize pin IDs to K21:k.
- It is never saved; the next revert discards it.

**S1 "Sheets"** (the 2A, 2C ownership-sync and 2D seed):
- `fixture.kicad_sch`: uuid K20:1, paper "A4", empty `lib_symbols`, `(sheet_instances (path "/" (page "1")))`, and two sheet symbols with no pins:
  - PSU: `(at 50.8 50.8) (size 38.1 50.8)`, uuid K20:2, Sheetname "PSU", Sheetfile "psu.kicad_sch", `(instances (project "fixture" (path "/R" (page "2"))))`.
  - CPU: `(at 152.4 50.8) (size 38.1 50.8)`, uuid K20:3, Sheetname "CPU", Sheetfile "cpu.kicad_sch", page "3".
- `psu.kicad_sch`: uuid K20:5, paper "A4", empty.
- `cpu.kicad_sch`: uuid K20:6, paper "A3", with sheet symbol CPU_POWER `(at 330.2 25.4) (size 38.1 25.4)`, uuid K20:4, Sheetfile "cpu_power.kicad_sch", `(instances (project "fixture" (path "/R/K20:3" (page "4"))))`.
- `cpu_power.kicad_sch`: uuid K20:7, paper "A4", empty.
- Native sheet paths: K05:1→[R], K05:2→[R, K20:2], K05:3→[R, K20:3], K05:4→[R, K20:3, K20:4].

**S2 "RootOnly"** (the 2C rebuild and sheet-generation seed): `fixture.kicad_sch` with uuid K20:1, paper A4 and no sheets.

**None**: repository files only (graph-only journeys).

#### 1.6.3 Expected result after realizing `Complete` from S1

**Sheets.** Exactly these four, with the pages and papers above: ROOT, PSU, CPU, CPU_POWER. No other screens exist.

**Symbols.** Exactly 12 unit placements:

| Sheet | Placements |
|---|---|
| PSU | J1, U1, R1, U2, U3, U4 (unit 1 each) |
| CPU | U5 units 1, 2, 3; U6 unit 1 |
| CPU_POWER | U5 unit 4 |
| ROOT | none |

References, lib_ids and Values follow §1.3 and §1.4.2. Identity is resolved through `SymbolBindings`, never through references.

**Nets.** The native pin partition equals the 11 nets in §1.4.2 exactly, both the set of multi-pin nets and their members. The stacked LP3982 pins 1 and 4 fall together into RAIL_B. Each of the 181 unconnected pins is alone in its native net. For every net, the text after the last `/` in the native name equals the circuit net name. Full hierarchical net names are recorded, not asserted.

**Hierarchical labels.** Exact sets, computed as the nets that cross a sheet subtree's boundary:
- PSU: {GND, RAIL_A, RAIL_B, TELEM_CPU_TO_MCU, TELEM_MCU_TO_CPU}
- CPU: {GND, RAIL_A, RAIL_B, TELEM_CPU_TO_MCU, TELEM_MCU_TO_CPU}
- CPU_POWER: {GND, RAIL_A}
- ROOT: none

**Sheet pins.** Exact name sets:
- PSU and CPU symbols (on ROOT): the same 5 names.
- CPU_POWER symbol (on CPU): {GND, RAIL_A}.

Pin shape is not asserted in v1 (open question 3).

**Labels.** Local labels whose text must appear at least once:
- PSU: VIN, DCDC_OUT, LDO_FAULT, PSU_SCL, PSU_SDA
- CPU: MEM_SCL

Labels whose text is allowed (the nets present on each sheet):
- ROOT: the 5 crossing nets
- PSU: the 10 nets present there
- CPU: GND, RAIL_A, RAIL_B, TELEM_CPU_TO_MCU, TELEM_MCU_TO_CPU, MEM_SCL
- CPU_POWER: GND, RAIL_A

Whether the realization uses wire stubs or labels placed on pin anchors is a 2A choice. The assertions do not depend on it.

**Forbidden:**
- global labels
- power symbols (#PWR), because the v1 circuit has no power-port concept (open question 1)
- no-connect markers
- buses and bus entries
- any symbol, sheet or label text not listed above
- a wire joining two different circuit nets
- a net named MEM_SDA or containing U6.5 together with another pin

**Presentation:** every symbol body and visible field lies inside the page inset (10 mm) and above the bottom 50 mm reserve, which is the existing fixture policy in `NativeXmlComponentCreationJourney`. Symbol bodies are disjoint. The checks are 2A's `NativePresentationChecks`.

**Other stages:**
- `Components`: same sheets and symbols; all 222 pins isolated; no labels or sheet pins.
- `PsuComponents`: the PSU symbols only.
- `SheetsOnly`: no symbols.

#### 1.6.4 `expected-native.json` shape

Keys:
- `fixture`, `version`, `stage`, `seed`
- `sheets[]`: `key`, `modelSheetInstance`, `definition`, `parent`, `file`, `sheetName`, `nativeSheetSymbol`, `nativeScreen`, `page`, `paper`
- `symbols[]`: `component`, `occurrence`, `reference`, `libId`, `value`, `unit`, `sheet`
- `nets[]`: `net`, `name`, `pins: [[ref, number]]`
- `isolatedPins`: map from reference to a list, or `{"allExcept": [...]}` for U5
- `hierarchicalLabels`, `sheetPins`, `requiredLocalLabelNames`, `allowedLabelNames`: maps from sheet key to a sorted list
- `forbidden[]`, `netNameRule: "leaf"`
- `presentation: {pageInsetMm: 10, reservedBottomMm: 50, symbolBodiesDisjoint: true}`

### 1.7 Expected realization map (`expected-realization.json`)

A pure derivation from G1 and E1. 2A implements it as `InterfaceRealizationDerivation`; §1.10's integrity test checks the table independently.

- **R1 (Signal):**
  - If any endpoint is Unresolved, Compatible or Candidates: status `Unresolved`, no nets.
  - Otherwise, with no `Pin` endpoints: status `Abstract`.
  - Otherwise, if all pins are in one circuit net N: status `Realized` [N], with `boundary` = the number of `Interface` endpoints on the owner block.
  - Otherwise: status `Conflict`, code `realization_net_mismatch`.
- **R2 (Interface, SignalGroup or DifferentialPair with members):**
  - Status is `Unresolved` if any member is Unresolved; else `Conflict` if any is Conflict; else `Abstract` if all are Abstract; else `Realized` if all are Realized; else `Partial`.
  - Nets are the union of member nets, ordered by net ordinal.
  - A composite with no members applies R1.
- **R3:** no cross-level or name matching. Root members stay Abstract until explicit v2 interconnect-realization records exist.
- **R4 pin validity codes:** `realization_pin_owner_mismatch` (G-2), `realization_pin_path_mismatch` (G-3), `realization_pin_unknown` (G-4).

Expected results:
- **System:** 01, 02 (members 03–05) and 06 (members 07–08) are Abstract.
- **PSU:**
  - 09 → [VIN]
  - 0a → [RAIL_A] with boundary 1
  - 0b → [RAIL_A]
  - 0c → [RAIL_B] with boundary 1
  - 0d → [DCDC_OUT, RAIL_A], from 0e → [DCDC_OUT] and 0f → [RAIL_A]
  - 10 → [RAIL_B]
  - 11 → [PSU_SCL, PSU_SDA], from 12 and 13
  - 14 → [LDO_FAULT]
  - 15 → [TELEM_MCU_TO_CPU, TELEM_CPU_TO_MCU], from 16 and 17, each with boundary 1
- **CPU:**
  - 18 → [RAIL_A] with boundary 1
  - 19 → [RAIL_B] with boundary 1
  - 1a → both TELEM nets, from 1b and 1c
  - 1d → **Unresolved**, nets so far [MEM_SCL]: 1e is Realized [MEM_SCL] and 1f is Unresolved []

### 1.8 Legacy flat structure F1 (`flat-structure.engineering.xml`) and expected migration

`StructuralDiagram` K30:1 reuses the G1 identities for blocks, ports and connections, so conversion identity preservation can be checked directly.

**Blocks** (`ParentId`, `ComponentIds`):
- PSU (null, [J1]; purpose "Supply CPU power and report rail status.")
- CPU (null, [])
- DC-DC (PSU, [U1, R1])
- LDO (PSU, [U2])
- Telemetry ADC (PSU, [U3])
- Telemetry MCU (PSU, [U4])
- Processor (CPU, [U5])
- Memory (CPU, [U6])

Other purposes are "".

**Ports** (IDs K15:i): 02 PSU/DC input, 03 PSU/Power, 04 PSU/Telemetry, 05 CPU/Power, 06 CPU/Telemetry, 07 DC-DC/Input, 08 DC-DC/Output, 0a LDO/Input.

**Connections** (ID: ports, kind, direction, NetIds):

| ID | Ports | Kind, direction | NetIds |
|---|---|---|---|
| K16:02 "Power" | 03↔05 | Power, FirstToSecond | RAIL_A, RAIL_B, GND |
| K16:06 "Telemetry" | 04↔06 | Data, Bidirectional | both TELEM nets |
| K16:09 "DC input" | 02↔07 | Power, FirstToSecond | VIN |
| K16:0a "Rail A" | 08↔03 | Power | RAIL_A |
| K16:0b "LDO supply" | 08↔0a | Power | RAIL_A |
| **K30:0x10 "Direct supply"** | 08↔05 | Power | RAIL_A (cross-level) |

**Statement:** K30:0x20, target PSU, role Intent, strength Preference, text "Keep high-current paths away from sensing."

**StructuralPresentation** (nm, 100 nm quantum):

| Item | Values |
|---|---|
| PSU block | (10 000 000, 10 000 000), size 60 000 000 × 40 000 000 |
| CPU block | (100 000 000, 10 000 000), size 60 000 000 × 40 000 000 |
| DC-DC, LDO | (10M, 70M), (50M, 70M); size 30M × 20M each |
| Telemetry ADC, Telemetry MCU | (10M, 100M), (50M, 100M); size 30M × 20M each |
| Processor, Memory | (100M, 70M), (140M, 70M); size 30M × 20M each |
| Ports (side, offset) | 03 Right 10M; 04 Right 30M; 05 Left 10M; 06 Left 30M; 02 Left 20M; 07 Left 10M; 08 Right 10M; 0a Left 10M |
| K16:02 waypoints | [(70M, 20M), (100M, 20M)] |

**`expected-migration.json` invariants** (exact root naming, ID derivation, kind mapping and coordinate units belong to the migration contract):
- Exactly one new root block, with children [PSU, CPU].
- PSU children: [DC-DC, LDO, Telemetry ADC, Telemetry MCU]. CPU children: [Processor, Memory].
- Every block has one state and one revision with `ParentRevisionId = null`, origin kind Import, and no invented history.
- Interface IDs equal port IDs:
  - PSU: {02, 03, 04}
  - CPU: {05, 06}
  - DC-DC: {07, 08}
  - LDO: {0a}
  - all other blocks: none
- Connection IDs are preserved: root local diagram {02, 06}; PSU local diagram {09, 0a, 0b}, with 09 and 0a using PSU boundary endpoints.
- K30:0x10 appears in no local diagram; the receipt records it under `cross_level_connection`.
- Component bindings come from `ComponentIds`.
- Flat NetIds are kept as explicit realization links in the v2 record when that record exists, otherwise in the receipt.
- The statement is kept as `unclassified_statement` in the receipt. PSU's Routing field stays "", because the text is not classified by guessing.
- Presentation becomes per-level layouts: root [PSU, CPU], PSU [4 children], CPU [2 children], with sizes and order preserved.
- Converting again returns the same receipt and writes nothing.
- The flat source bytes are unchanged.

### 1.9 How journeys load the fixture

Parent-owned API in `PsuCpuFixture.cs`:

```csharp
internal enum PsuCpuStage { RootOnly, SheetsOnly, PsuComponents, Components, Complete }
internal enum PsuCpuSeed { None, RootOnly, Sheets }
internal sealed record PsuCpuNativeContext(string ProjectDirectory, DocumentSpecifier Root, Guid RootInstanceId,
    PsuCpuSeed Seed, IReadOnlyList<SchematicPartSymbol> PartSymbols, SchematicDesign? Baseline,
    string HardwarePath, string BlocksPath, string DesignPath, string FlatStructurePath);
internal static class PsuCpuFixture {
  public const int Version = 1;
  public static string Directory { get; }
  public static HardwareRepository Hardware();
  public static EngineeringDesign Engineering(PsuCpuStage stage = PsuCpuStage.Complete);
  public static EngineeringDesign FlatStructure();
  public static RecursiveBlockGraph Graph();
  public static IReadOnlyList<PsuCpuPart> Parts();
  public static PsuCpuExpectedNative ExpectedNative(PsuCpuStage stage);
  public static PsuCpuExpectedRealization ExpectedRealization();
  public static PsuCpuExpectedMigration ExpectedMigration();
  public static Task WriteRepositoryFilesAsync(string directory, CancellationToken token);   // hardware.xml, system.blocks.xml, flat-structure.engineering.xml (no native needed)
  public static Task<PsuCpuNativeContext> PrepareNativeAsync(NativeClient client, DocumentSpecifier emptyRoot,
      string projectDirectory, PsuCpuSeed seed, string evidence, CancellationToken token);
  public static SchematicDesign Desired(PsuCpuNativeContext context, PsuCpuStage stage);
  public static Task<StoredDesignRecovery> InitializeRecoveryAsync(NativeClient client, PsuCpuNativeContext context,
      string recoveryPath, CancellationToken token);
  public static void AssertNative(SchematicDesign synchronized, SchematicElectricalState state, PsuCpuStage stage);
}
```

`PrepareNativeAsync` runs these steps unless the seed is None:
1. Write S0, revert and reopen, then capture and normalize the 8 part symbols.
2. Check them with `SchematicPartSymbols.Validate` against the Complete stage, which enforces exact pin coverage against `parts.json`.
3. Write the chosen seed, revert and reopen, and verify the screens and native paths in §1.6.2.
4. Capture a checked state. Baseline = `SchematicDesign(Engineering(SheetsOnly or RootOnly), hierarchy, sheet bindings for existing paths, [], null)`, and `SchematicDesignBindings.Inspect` must report no issues.
5. Write `hardware.xml`, `system.blocks.xml`, `flat-structure.engineering.xml` and the baseline `design.xml`, with the bytes defined by the builder or codecs.

For every seed, the evidence written is `psu-cpu-seed.json` and `psu-cpu-part-symbols.xml`.

`Desired` keeps the baseline and sets Engineering to the requested stage. `PartSymbols` covers the parts present in that stage. Bindings for sheets missing from S2 are intentionally absent; supplying them is 2C's sheet-generation job.

**Harness seam (parent, `NativeSessionTests.cs`).** A PSU/CPU journey runs after `VerifyEmptyRootCreation` and instead of the probe fixture:

| Enum value | Test method / category | Evidence directory | Seed | Xvfb | Seconds | Lane method (file) |
|---|---|---|---|---|---|---|
| `PsuCpuSeed` | `PsuCpuFixtureSeedsLoadWithExactIdentities` / NativePsuCpuSeed | native-psu-cpu-seed | S0, S1, S2 | 1280x900 | 300 | parent (`PsuCpuFixture.cs`) |
| `ConnectedRealization` | `PsuCpuXmlRealizesAConnectedHierarchicalSchematic` / NativeConnectedRealization | native-connected-realization | Sheets | 1600x1150 | 600 | 2A `VerifyPsuCpuConnectedRealization` (NativeXmlComponentCreationJourney.cs) |
| `DiagramCanvas` (DataRow light and dark; GTK theme as for RecursiveEditor) | `PerLevelCanvasEditsPersistLayout` / NativeDiagramCanvas | native-diagram-canvas/{theme} | None | 1600x1150 | 300 | 2B `VerifyPsuCpuDiagramCanvas` (NativeRecursiveEditorJourney.cs) |
| `StructuralMigration` | `FlatStructureConvertsOnceIntoARootBlock` / NativeStructuralMigration | native-structural-migration | None | 1600x1150 | 300 | 2B `VerifyStructuralMigration` (new NativeStructuralMigrationJourney.cs) |
| `XmlRebuild` | `DeletedNativeSheetsRebuildFromXmlWithoutLoss` / NativeXmlRebuild | native-xml-rebuild | RootOnly | 1280x900 | 600 | 2C `VerifyPsuCpuXmlRebuild` (new NativeXmlRebuildJourney.cs) |
| `OwnershipSync` | `NativeEditsReachTheOwningBlockByExactIdentity` / NativeOwnershipSync | native-ownership-sync | Sheets | 1280x900 | 600 | 2C `VerifyPsuCpuOwnershipSync` (NativeSymbolSheetOwnershipJourney.cs) |
| `NativeCrash` | `NativeCrashKeepsXmlAndRegistryTruthful` / NativeCrash | native-crash | Sheets | 1280x900 | 300 | 2D `VerifyPsuCpuNativeCrash` (new NativeCrashJourney.cs) |

Lane method signature: `private static Task VerifyX(NativeClient client, PsuCpuNativeContext context, int processId, string display, string evidence, string instanceId, CancellationToken token)`. The NativeCrash method also receives `Process native`.

The freeze commit adds each method with body `throw new AssertInconclusiveException("Phase 2 lane 2X has not delivered this journey")`, and the lane replaces that body. Graphs treat Inconclusive as not passed. A category joins `native-acceptance` only after integration.

### 1.10 Integrity, error codes, versioning

**Managed `PsuCpuFixtureTests`** (parent):
- File SHA-256 values match `fixture.json`.
- The production codecs parse every file and validate it, including every stage.
- Builder output is byte-equal to the checked-in files.
- Counts: 8 parts, 222 part pins, 4 sheets, 8 components, 11 occurrences, 11 nets, 41 connected pins.
- G-1 to G-6 hold.
- `expected-native` nets equal the circuit nets.
- Label and sheet-pin sets equal an independent boundary-crossing computation.
- `expected-realization` pins are members of the stated nets.

**Native `NativePsuCpuSeed`:**
- S0 captures exactly the 8 cache keys.
- The declarations validate.
- S1 and S2 screens and paths are exact.
- The baseline bindings resolve.
- Two preparations produce byte-identical `psu-cpu-part-symbols.xml`.

Error codes (the message prefix of the `AssertFailedException` raised by the loader):
- `psu_cpu_fixture_file_changed`
- `psu_cpu_fixture_builder_drift`
- `psu_cpu_symbol_capture_incomplete`
- `psu_cpu_seed_hierarchy_mismatch`
- `psu_cpu_baseline_unresolved`
- `psu_cpu_native_mismatch`: lists the first 20 differences by category (sheet, symbol, net, isolated_pin, hierarchical_label, sheet_pin, label, forbidden, naming, presentation)
- `psu_cpu_unknown_stage`

**Versioning:**
- Version 1 is immutable. Only the parent changes it, by a re-freeze commit that moves `version` to 2 and updates every expected file. Lanes then rebase.
- Lanes never edit fixture files. Lane-specific variants, such as history chains built with `RecursiveBlockGraph.SaveDraft`, live in lane test code.
- G1 stays v1 on disk. A v2 golden (`system.blocks.v2.xml`) is added only at a re-freeze after the v2 schema is frozen.

---

## 2. File-ownership map

### 2.1 Rules

1. **Exclusive ownership.** A path belongs to exactly one owner: PARENT, 2A, 2B, 2C or 2D. Paths not listed stay parent-held during Phase 2; changes to them go through a seam request in the lane handoff.
2. **Shared protos.** Lanes edit only between their own band markers (§3). New messages go into lane message blocks or lane files, using the lane's name prefix.
3. **Partial classes, not shared files.**
   - `NativeSessionTests` and `SchematicSynchronizationPlanTests` are extended through lane-owned partial files.
   - `NativeClient` helpers from other lanes are C# extension methods in lane-owned files; the generic `InvokeAsync<TReq,TResp>` covers most needs.
4. **Every new MCP tool** carries `[KiCadCapability(scope, source, revisionContract)]`. The lane's handoff lists the tool names, the `McpProcessTests` assertions and the capability rows for the parent to apply.
5. **Frozen shared utilities** are read-only for every lane. Consumers use their public APIs; changes arrive by seam request.
   - 2C writes block-ownership revisions through the existing public API of 2B's `RecursiveBlockGraph` (`StartDraft`, `SaveDraft`, `Inspect`, `Select`). 2B keeps that API source-compatible throughout Phase 2.
6. **Integration.** The parent integrates every 2–3 days and is the only one who edits the seams.

### 2.2 Parent seams

- **Coordinator config:** `.devcoordinator.toml`. The parent adds the graphs `psu-cpu-fixture`, `native-connected-realization`, `native-diagram-canvas`, `native-structural-migration`, `native-xml-rebuild`, `native-ownership-sync` and `native-crash`.
- **Docs and release records:**
  - `automation/README.md`
  - `automation/distribution/**`
  - `automation/qualification/landing-*.json`, `subject.sha256`, `archive/*.toml`, `compiler-cache.md`
  - `automation/design/**` (approved and binding)
- **Test and tool seams:**
  - `automation/tests/KiCad.Automation.Tests/NativeSessionTests.cs`
  - `McpProcessTests.cs`
  - `SchematicSynchronizationPlanTests.cs` and `.Creation.cs`
- **Capability seams:**
  - `automation/src/KiCad.Automation.Mcp/ServiceCapabilities.cs` (new, extracted from `InstanceTools.cs`)
  - `KiCadCapabilityAttribute.cs` (new)
  - `Program.cs`
- **Protocol:**
  - `api/proto/common/types/diagram_revision_types.proto`
  - `api/proto/common/commands/automation_commands.proto`
  - `api/proto/schematic/schematic_types.proto`
  - `api/CMakeLists.txt`
- **Synchronization seam:** `automation/src/KiCad.Automation.Native/SchematicSynchronizationPlan.cs` and `SchematicSynchronizationExecutor.cs`.
- **Build files:**
  - `eeschema/CMakeLists.txt`, `kicad/CMakeLists.txt`, `common/CMakeLists.txt`
  - `qa/tests/eeschema/CMakeLists.txt`, `qa/tests/common/CMakeLists.txt`
  - `automation/KiCad.Automation.slnx`, `Directory.Packages.props`, `Directory.Build.props`, all `*.csproj`
- **Fixture:**
  - `automation/tests/fixtures/psu-cpu/**` (new)
  - `PsuCpuFixture.cs`, `PsuCpuFixtureBuilder.cs`, `PsuCpuFixtureTests.cs`, `SharedProtoBandTests.cs` (all new)
- **Frozen shared, `automation/src/KiCad.Automation.Native`:** `SchematicElectricalComparison.cs`, `SchematicDesign.cs`, `SchematicDesignXml.cs`, `Schemas/design-v1.xsd`, `SchematicJson.cs`.
- **Frozen shared, `automation/src/KiCad.Automation.Model`:** `Circuit.cs`, `CircuitXml.cs`, `EngineeringDesign.cs`, `EngineeringDesignXml.cs`, `HardwareRepository.cs`, `HardwareRepositoryXml.cs`, `StructuralDiagram.cs`, `StructuralDiagramXml.cs`, `StructuralPresentation.cs`, `StructuralPresentationXml.cs`, `Identity.cs`, `Coordinates.cs`.
- **Frozen shared, `automation/schemas`:** `circuit-v1.xsd`, `engineering-design-v1.xsd`, `hardware-v1.xsd`, `structure-v1.xsd`, `recursive-block-graph-v1.xsd`.
- **Frozen shared, tests:** `StdioMcpFixture.cs`, `NativeEvidenceDirectory.cs`, `NativeKeyboard.cs`, `NativeElectricalJourney.cs`, `NativeHierarchyJourney.cs`, `SchematicElectricalComparisonTests.cs`.

### 2.3 Lane 2A: connected XML realization

**.NET, `automation/src/KiCad.Automation.Native`:**
- New: `SchematicWiringPlanner.cs`; `SchematicConnectedAddition.cs` (entry points the parent seam calls).
- Existing: `SchematicNativeCreationProjection.cs`, `SchematicPartSymbols.cs`, `SchematicInitialLayoutPlanner.cs`, `SchematicFieldLayoutPlanner.cs`, `SchematicFieldTextModes.cs`, `NativePresentationChecks.cs`.

**.NET, `automation/src/KiCad.Automation.Model`:**
- New: `InterfaceRealizationDerivation.cs`.
- Existing: `InitialSchematicLayout.cs`, `LayoutRefinement.cs`, `PresentationVerification.cs`.

**MCP:** `automation/src/KiCad.Automation.Mcp/SchematicViewTools.cs`, after the freeze moves its four recovery tools to 2C.

**C++:**
- `eeschema/sch_symbol.{h,cpp}` (variant pin mapping)
- `eeschema/api/api_sch_utils.{h,cpp}`
- `eeschema/api/api_sch_symbol_definition.{h,cpp}`
- `eeschema/api/api_handler_sch_placement_geometry.cpp`
- `eeschema/api/api_handler_sch_render.cpp`
- Optional new `eeschema/api/api_sch_realization.{h,cpp}`: the parent adds a dispatch hook in `api_handler_sch.cpp` only if 2A asks for it.

**QA:** new `qa/tests/eeschema/test_sch_variant_pin_mapping.cpp` (pre-registered at freeze).

**Proto:** new `api/proto/common/commands/schematic_realization_commands.proto`, plus bands 100–199.

**Tests:**
- Extend: `NativeXmlComponentCreationJourney.cs` (adds `VerifyPsuCpuConnectedRealization`), `SchematicNativeCreationProjectionTests.cs`, `SchematicInitialLayoutPlannerTests.cs`, `InitialSchematicLayoutTests.cs`, `PresentationVerificationTests.cs`, `SchematicFieldLayoutPlannerTests.cs`, `SchematicPartSymbolTests.cs`, `SymbolPinCoverageTests.cs`, `SchematicGeometryToolTests.cs`, `LayoutRefinementTests.cs`, `NativePresentationJourney.cs`, `NativeMultiUnitJourney.cs`, `NativeRepeatedSymbolXmlJourney.cs`, `NativePlacementGeometryJourney.cs`, `NativeFieldLayoutJourney.cs`, `NativeUnitReferenceJourney.cs`.
- New: `SchematicWiringPlannerTests.cs`, `InterfaceRealizationDerivationTests.cs`, `SchematicSynchronizationPlanTests.Connected.cs`.

### 2.4 Lane 2B: per-level editor and diagram model v2

**Model, `automation/src/KiCad.Automation.Model`:**
- Existing: `RecursiveBlockGraph.cs`, `RecursiveBlockGraphXml.cs`, `BlockLocalDiagram.cs`, `DiagramAnnotation.cs`, `DiagramEndpointBinding.cs`, `DiagramConnectionArchive.cs`, `DiagramConnectionArchiveXml.cs`, `DiagramRequirements.cs`, `DiagramRequirementHistory.cs`, `DiagramRequirementHistoryXml.cs`, `DiagramFieldHistoryQuery.cs`, `DiagramHistoryQuery.cs`, `DiagramRevisionOriginXml.cs`, `BlockDefinition.cs`, `BlockDefinitionXml.cs`, `BlockDefinitionGuidance.cs`, `BlockProposal.cs`, `BlockProposalRecord.cs`, `BlockComponentBindings.cs`, `BlockPhysicalAllocation.cs`, `ImplementationManagement.cs`, `RecursiveRequirementMerge.cs`, `DiagramRefinementInput.cs`, `DiagramRefinementInputXml.cs`.
- New: `StructuralMigration.cs`.

**Schemas, `automation/schemas`:**
- New: `recursive-block-graph-v2.xsd`.
- Existing: `connection-archive-v1.xsd`, `block-definition-v1.xsd`, `requirement-history-v1.xsd`, `refinement-input-v1.xsd`. A change to any of these creates a new version file.

**Native .NET, `automation/src/KiCad.Automation.Native`:** `RecursiveBlockCodec.cs`, `RecursiveBlockFiles.cs`, `RecursiveEditorFiles.cs`, `BlockProposalFiles.cs`, `BlockProposalReceipts.cs`, `BlockProposalRecovery.cs`, `RefinementAssetFiles.cs`, `RefinementInputFiles.cs`, `RefinementInputReceipts.cs`, `RefinementInputRecovery.cs`, `RefinementPublicationProof.cs`, `ImplementationFiles.cs`, `DiagramRequirementHistoryFiles.cs`, `BlockDefinitionLibraries.cs`, `BlockComponentFiles.cs`, `BlockComponentResolver.cs`, `StructuralEditorCodec.cs`, `StructuralEditorFiles.cs`.

**MCP:** `RecursiveEditorTools.cs` (gains `kicad_diagram_create` and `kicad_diagram_migrate`), `RecursiveFileCommand.cs`, `StructuralEditorTools.cs`, `StructuralFileCommand.cs`. `kicad_structure_open` is retired only after the conversion journey passes.

**C++:**
- `kicad/recursive_diagram_frame.{h,cpp}`
- new `kicad/recursive_diagram_canvas.{h,cpp}` (pre-registered)
- `kicad/dialogs/panel_diagram_history.{h,cpp}`, `dialog_diagram_field_history.{h,cpp}`, `dialog_diagram_conflict.{h,cpp}`
- `kicad/structural_editor_control.{h,cpp}`, `structural_editor_frame.{h,cpp}`, `structural_editor_admission.h`
- `kicad/kicad_manager_frame.{h,cpp}`, `menubar.cpp`, `toolbars_kicad_manager.cpp`
- `kicad/tools/kicad_manager_actions.{h,cpp}`, `kicad_manager_control.{h,cpp}`

**Proto:**
- whole files: `api/proto/common/commands/recursive_diagram_commands.proto`, `structural_commands.proto`, `api/proto/common/types/structural_types.proto`
- new `diagram_canvas_commands.proto`
- bands 200–299, including all new `RecursiveFileAction` values

**QA:** `qa/tests/common/test_diagram_field_history.cpp`.

**Docs:** `automation/qualification/agent-refinement-contract.md`; new `automation/design/per-level-editor-design-qa.md`.

**Tests:**
- Extend: `NativeRecursiveEditorJourney.cs` (adds `VerifyPsuCpuDiagramCanvas`), `NativeStructuralEditorJourney.cs`, `NativeStructuralPropertyJourney.cs`, `NativeFieldHistoryTests.cs`, every `Recursive*Tests.cs`, `Diagram*Tests.cs`, `BlockProposal*Tests.cs`, `Structural*Tests.cs`, `RetainedXmlHistoryTests.cs`.
- `RecursiveBlockGraphTests.cs` holds `RecursiveBlockFixture` and `RecursiveBlockLocalDiagramTests.cs` holds `LinkedDiagramFixture`; both stay with 2B.
- New: `NativeStructuralMigrationJourney.cs`, `StructuralMigrationTests.cs`, `RecursiveBlockGraphV2XmlTests.cs`.

### 2.5 Lane 2C: lossless rebuild, change tracking, ownership sync

**Native .NET, `automation/src/KiCad.Automation.Native`:**
- Existing: `SchematicHierarchyDelta.cs`, `SchematicHierarchyMerge.cs`, `SchematicHierarchyTopology.cs`, `SchematicItemDelta.cs`, `SchematicItemMerge.cs`, `SchematicNetReconciliation.cs`, `SchematicNativeRemovalProjection.cs`, `SchematicNativeRestorationProjection.cs`, `SchematicOwnershipHistory.cs`, `SchematicOwnershipResolutionService.cs`, `SchematicModelProjection.cs`, `SchematicPropertyProjection.cs`, `SchematicPlacementPlan.cs`, `SchematicLayoutResolution.cs`, `SchematicSyncStore.cs`, `SchematicElectricalCheckpoints.cs`, `SchematicLibraryCacheEquivalence.cs`, `SchematicNetChainClasses.cs`, `SchematicNetSettingsState.cs`, `SchematicDataXml.cs`, `SchematicGroupGraph.cs`, `SchematicVariantProjection.cs`, `DesignRecoveryStore.cs`, `DesignRecoveryInspector.cs`, `DesignRecoveryFileObserver.cs`, `DesignRecoveryNativeObserver.cs`, `DesignRecoveryReattachment.cs`, `DesignFileIntakeSession.cs`, `DesignFilePublisher.cs`, `DesignFileSubscription.cs`, `DesignPublicationCommitter.cs`, `DesignPublicationIntent.cs`, `DesignLayoutIntent.cs`, `DesignNativeIntakeSession.cs`, `DesignSynchronizationReceipt.cs`, `DesignSynchronizationReceipts.cs`, `AutomaticDesignSynchronization.cs`, `AutomaticDesignDriver.cs`, `NativeEventCursor.cs`, `NativeEventSubscription.cs`, `PreservingFileReplacement.cs`, `RetainedXmlHistory.cs`, `CheckedSchematicContract.cs`.
- New: `SchematicRebuild.cs`, `BlockOwnershipSynchronization.cs`.

**Model:** `ComponentReferences.cs`, `ComponentReferenceRetention.cs`, `PinPartitionEvolution.cs`, `PinPartitionMerge.cs`.

**MCP:**
- Existing: `RecoveryTools.cs`, `AutomaticDesignTools.cs`, `AutomaticDesignRegistry.cs`, `FileIntakeTools.cs`, `NativeIntakeTools.cs`, `NativeIntakeRegistry.cs`, `PlacementTools.cs`, `SchematicXmlTools.cs`, `SchematicMutationTools.cs`, `EventTools.cs`, `KnowledgeTools.cs`.
- New: `RecoveryObservationTools.cs`, which receives these tools, unchanged, from `SchematicViewTools.cs` at freeze: `kicad_design_electrical_baseline_initialize`, `kicad_design_recovery_refresh`, `kicad_design_recovery_reattach`, `kicad_design_recovery_observe`.

**C++:**
- `eeschema/sch_commit.{h,cpp}`
- `eeschema/schematic.{h,cpp}` (journal and tracking only)
- `include/api/document_change_journal.h`, `include/api/native_state_digest.h`
- `eeschema/api/api_handler_sch.{h,cpp}`
- `eeschema/api/sch_context.cpp`, `headless_sch_context.cpp`
- `common/api/checked_schematic_controller.cpp`, `include/api/checked_schematic_controller.h`
- `eeschema/dialogs/dialog_erc.cpp`, `eeschema/tools/sch_editor_control.cpp`, `eeschema/eeschema_config.cpp`
- the other `OnModify()` call sites in the 34 eeschema files
- new `eeschema/api/api_sch_state_groups.{h,cpp}` (pre-registered)

**QA:** new `qa/tests/eeschema/test_sch_change_tracking.cpp`; `qa/tests/common/test_document_change_journal.cpp`.

**Proto:** new `schematic_tracking_commands.proto`, plus bands 300–399.

**Harness:** `automation/tests/KiCad.Automation.SyncHarness/**`.

**Tests:**
- Extend: `NativeSymbolSheetOwnershipJourney.cs` (adds `VerifyPsuCpuOwnershipSync`), `NativeAutomaticSynchronizationJourney.cs`, `NativeSynchronization{Execution,Layout,Lock,Property,Service}Journey.cs`, `NativeItemDeltaJourney.cs`, `NativeEventJourney.cs`, `NativeTransformJourney.cs`, `NativeOffscreenTransformXmlJourney.cs`, `NativeSheetXmlJourney.cs`, `NativeHierarchyProjectPolicyJourney.cs`, the connected, interactive, manual, shared-screen and offscreen move journeys, `Design*Tests.cs`, `Schematic{HierarchyDelta,HierarchyMerge,HierarchyTopology,ItemDelta,ItemMerge,NetReconciliation,NativeRemovalProjection,NativeRestoration,OwnershipResolution,ModelProjection,PropertyProjection,SyncStore,SynchronizationAdmission,LayoutResolution,LibraryCacheEquivalence,NetChainClasses,NetSettingsSnapshot}Tests.cs`, `PinPartition*Tests.cs`, `ComponentReferenceRetentionTests.cs`, `UnresolvedNetBindingTests.cs`, `SymbolSheetOwnershipTests.cs`, `AutomaticDesignSynchronizationTests.cs`, `CheckedDesignRecoveryTests.cs`, `CandidateRecoveryCommitTests.cs`, `NativeEventCursorTests.cs`, `SyncHarnessProcessTests.cs`.
- New: `NativeXmlRebuildJourney.cs`, `SchematicRebuildTests.cs`, `SchematicSynchronizationPlanTests.Rebuild.cs`.

### 2.6 Lane 2D: instances, capabilities, Codex runtime

**Native .NET:** `InstanceRegistry.cs`, `InstanceRegistry.Launches.cs`, `InstanceRegistry.Replacements.cs`, `NativeClient.cs`, `NativeIpcEndpoint.cs`, `NngTransport.cs`.

**MCP:**
- `InstanceTools.cs`, with the capability list now in `ServiceCapabilities.cs`. 2D replaces that list with a derivation from `[KiCadCapability]`, with parent review.
- `InstanceToolBoundary.cs`, `InstanceUpdateTools.cs`, `InstanceUpdateReconnection.cs`, `RuntimeInfoCommand.cs`, `DocumentLifecycleTools.cs`, `DocumentStateTools.cs`, `CheckedSchematicTools.cs`.
- New: `CapabilityCatalog.cs`.

**C++:** `common/api/api_server.cpp`, `include/api/api_server.h` (handshake advertises the handlers that really exist), `common/api/api_handler.cpp`, `include/api/api_handler.h`, `common/api/document_lifecycle_controller.cpp`, `include/api/document_lifecycle_controller.h`, `include/api/native_file_observation.h`, `eeschema/api/sch_api_save.{h,cpp}`.

**Proto:** new `capability_commands.proto`, plus bands 400–499.

**Tests:**
- Extend: `InstanceRegistryTests.cs`, `InstanceReplacementTests.cs`, `InstanceToolBoundaryTests.cs`, `InstanceUpdateReconnectionTests.cs`, `NativeCheckedSchematicBatchJourney.cs`, `NativeDocumentStateJourney.cs`, `NativeCheckedSaveJourney.cs`, `NativeCleanCloseJourney.cs`, `NativeStartupFailureJourney.cs`, `NativeStartupRecoveryJourney.cs`, `NativeRetryJourney.cs`, `CodexDesktopJourneyTests.cs`, `RuntimeInfoTests.cs`, `DocumentLifecycleToolTests.cs`, `DocumentStateToolTests.cs`, `CheckedSchematicToolTests.cs`, `NativeIpcEndpointTests.cs`, `NngTransportTests.cs`, `NngBindingLifetimeTests.cs`, `NativeClientTests.cs`, `McpReattachmentJourney.cs`.
- New: `NativeCrashJourney.cs`, `CapabilityCatalogTests.cs`, `NativeObserveApplyStressJourney.cs`.

**Docs:** new `automation/qualification/codex-runtime-refresh.md` (runbook; no private `.codex` content).

### 2.7 Freeze-commit checklist (parent, before fan-out; must keep the existing graphs green)

1. Add the fixture directory and the parent C# files in §1.1; generate `lib_symbols.kicad_sexpr` with the fork's eeschema. The `psu-cpu-fixture` graph must pass.
2. Add band markers and lane message blocks to the three shared protos. Reformat `RecursiveFileAction` to one value per line. Add `SharedProtoBandTests`.
3. Create the four lane proto files (syntax, package and options only) and register them in `api/CMakeLists.txt`.
4. Pre-create and register the empty lane C++ files:
   - `kicad/recursive_diagram_canvas.{h,cpp}`
   - `eeschema/api/api_sch_state_groups.{h,cpp}`
   - `qa/tests/eeschema/test_sch_variant_pin_mapping.cpp`
   - `qa/tests/eeschema/test_sch_change_tracking.cpp`
5. Extract `ServiceCapabilities.cs` and add `KiCadCapabilityAttribute.cs`.
6. Move the four recovery tools into `RecoveryObservationTools.cs` and register it in `Program.cs`. Tool names do not change.
7. Refactor `KICAD_API_SERVER::PublishSchematicCommit` onto a generic `PublishAutomationEvent(AutomationEvent)`, so 2C can publish new payloads without editing `api_server.cpp`.
8. Add the `NativeSessionTests` enum values, test methods, evidence names, limits and seed dispatch, with Inconclusive stubs in the lane-owned files.
9. Add the `SchematicSynchronizationPlan` and `SchematicSynchronizationExecutor` dispatch hooks that call 2A's `SchematicConnectedAddition` and 2C's `SchematicRebuild`. The names are final only when parent item 1 confirms them.
10. Add the `.devcoordinator.toml` graphs.
11. Record the ownership-map decision in the Coordinator.

### 2.8 Dependencies and integration order

- 2A needs parent item 1 and S1. Its first item, p95e0c19e6143deb6 (declared multi-unit creation across sheets), unlocks U5 unit 4 for 2C.
- 2B needs parent item 2. Its canvas UI waits for the Round A choice; its model, codec, migration and tools do not.
- 2C uses `PsuComponents` until 2A lands cross-sheet creation, then `Components` and `Complete`.
- 2D is independent. Its capability derivation relies on every lane annotating its tools.
- Suggested merge order at each integration: 2B model/codec → 2A → 2C → 2D. After each merge the parent applies the seam requests (`McpProcessTests` names, capability rows, README) and runs the focused graphs on the exact integration commit.

## 3. Proto numbering (summary; exact list in proto_reservations)

- Existing numbers never change.
- Parent-frozen v2 additions use the next sequential number and stay below 100.
- Lane bands: 2A 100–199, 2B 200–299, 2C 300–399, 2D 400–499. 500–999 are reserved for Phase 3.
- The same bands apply to new values in shared enums and to new members of existing `oneof`s.
- Marker lines: `// -- lane 2X (NNN-MMM) --` and `// -- end lane 2X --`. Messages a lane adds to a shared file go between `// == lane 2X messages ==` and `// == end ==` at the end of the file.
- New enum types and messages use lane prefixes, because C++ enum values share package scope.
