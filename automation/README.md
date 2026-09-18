# Codex-operated KiCad automation

Development branch: `main` (the original `feature/codex-kicad-automation` branch is preserved), based on
`f638a860a05b3e48d1074314a656ad9b8f597466`. This is an incomplete implementation
of the approved six-milestone program, not a release.

Current downloads are on [kicad.vr.ae](https://kicad.vr.ae/). The
[Mac preview installation guide](distribution/mac-preview-install.md) covers the
September 15 Apple Silicon and Intel builds, including managed-update setup.
The [Windows preview installation guide](distribution/windows-preview-install.md)
covers the September 16 Windows package and its managed updater.

## Try provisional Schematic Setup edits in a source build

The feature branch contains the native Setup change from
`54e61d57ae0aa051ccd86aab2138f8de9d1cd5b5`. This section describes that source
increment, not the older Mac/Windows downloads or a qualified cross-platform release.

Open a disposable project in its Schematic Editor, then choose **File → Schematic
Setup**. Change the overbar offset under **Formatting**, switch to another page,
and press **Cancel**. Reopen Setup: the original value must remain. Repeat with
one **Pin Conflicts Map** cell. Its pending color change must also disappear on
Cancel. Pressing **OK** instead accepts the edit; the editor's Undo and Redo
controls must restore and reapply it together with its native revision.

To check error recovery, change a pin-conflict cell, switch to Formatting, enter
`0` for the connection grid and press OK. The validation error must leave Setup
open. Correct the grid to its previous value: OK must retain the pending pin-rule
edit, while Cancel must discard it. No XML editing is needed for this journey.

For a configured Linux source checkout, the compiled two-editor verification is:

```sh
devcoordinator2 test start . --test native-setup-dialog --tier development
```

The test owns disposable projects and a virtual display. The formatting and
pin-map page-switch/Cancel/OK/Undo/Redo paths, plus later-page validation recovery,
have real Linux evidence. Complete import, nested embedded-file and dependent-page
coverage are still being qualified; native Mac Setup evidence and full automatic
XML synchronization remain open. The authoritative completion state is in
DevCoordinator, not this usage guide.

## Linux preview — September 11, 2026

[Linux application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T163551Z-dd74f7aca989-debian13-x64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T163551Z-dd74f7aca989-source.tar.gz)
identify `dd74f7aca9892b4301a2a200dbc64db9fa77a578`, signed preview sequence 15.
Extract and run `./kicad-codex`; use `./kicad-mcp` for the matching STDIO tools.
This build adds continuous native-event intake into saved recovery state and
typed ERC configuration capture/restore. Intake is not yet automatic design-XML
write-back or full bidirectional synchronization.

Public run `t20260911T165510Z-2f9025` verified all 60 existing/new downloads and
the real Linux update/Save/restart journey with two designs, same-process MCP
reconnection, matching images/state and a subsequent MCP restart. Legacy MCP
clients receive an explicit missing-ERC coverage warning instead of unknown
fields they cannot preserve; current clients request the complete supported
snapshot. Native Mac/Windows and actual Codex Desktop qualification of this
increment remain open. Older previews and platform feeds remain available.

## Earlier Windows checkpoint — September 11, 2026

[Windows x64 application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-433c5e1fdf5954955a19df546236dce78e78b5b0-windows-x64.zip)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-433c5e1fdf5954955a19df546236dce78e78b5b0-source.tar.gz)
identify `433c5e1fdf5954955a19df546236dce78e78b5b0`, version
`preview-20260911-433c5e1fdf59`. Extract the archive and run `bin/kicad.exe`;
the matching STDIO server is `bin/kicad-mcp.exe`.

The native build and installed package were verified with two independent
schematic instances, real packaged MCP/NNG loading, preserved dirty objects
across MCP restart, and native keyboard Save/Close. Native requalification
`34571626328` passed including final cleanup; it used the unchanged archive
from build `34555975985`, whose original failed harness receipt remains retained.
Public check `7d0703` passed all 54 downloads and existing signed-feed/native
caption-update checks on September 11 at 07:32:44 UTC. Generation 32 publishes
the Windows archive without removing older downloads or Mac/Linux feeds.

This is a preview, not full project readiness. Extracting the archive alone
does not configure managed updating. Generation 33 adds the signed Windows
feed at `https://kicad.vr.ae/platforms/win-x64/`, sequence 2. Public check
`8d1abc` verified its ZIP download and preservation of the Mac/Linux update
paths. On native Windows, `34575956920` installed the public package and ran
its shipped updater: current-version checks, unchanged metadata, wrong-platform
rejection and subsequent recovery passed without changing the selected version.
That is not a two-version upgrade or editor restart. The actual Windows
Update-button and post-update MCP journey remain under qualification.

## Earlier Apple Silicon checkpoint — September 11, 2026

[Apple Silicon application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-3ac5af02cb7eee653578e0d5c85b7d841eb8724a-macos-arm64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-3ac5af02cb7eee653578e0d5c85b7d841eb8724a-source.tar.gz)
identify `3ac5af02cb7eee653578e0d5c85b7d841eb8724a`, version
`preview-20260911-3ac5af02cb7e`. Extract and open `install/KiCad.app`; the
companion entry is `kicad-mcp`. macOS 15.7+ is required; the build is ad-hoc
signed, not notarized. Use the verified installer for managed updates.

Native build `34552278753`, signed staging `cf1345`, and public verification
`8b39c7` passed. Generation 27 serves ARM sequence 7, retaining Intel sequence 4,
Linux sequence 11, and 46 downloads. Native Mac run `34559868961` verified the
public `670961b39bb1` to `3ac5af02cb7e` update with two preserved designs,
connected packaged MCP services, exact-operation retries, a candidate MCP restart,
matching images/state, and the manager/schematic/PCB module journey.
The earlier run `34559219495` reached those operations but failed final log
inspection; its receipt is not substituted for the clean rerun.

Current-task Codex tools were subsequently exercised directly against two Linux
native editors in governed run `025181`: both connected moves, duplicate-operation
retries, stale/wrong-instance rejection, matching PNG/state, independent region
images, and native undo were observed. This is more than a standalone MCP harness,
but does not establish native-Mac Codex frontend identity, automatic reverse XML
synchronization, or complete Desktop qualification. Those outcomes remain open.

### Earlier Intel checkpoint — September 11, 2026

[Intel application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-3ac5af02cb7eee653578e0d5c85b7d841eb8724a-macos-x64.tar.gz)
now uses the same `3ac5af02cb7eee653578e0d5c85b7d841eb8724a` source as Apple
Silicon above. Native build `34552278753`, signed staging `b27812` and public
verification `9679c0` passed. Generation 30 serves Intel feed 6, preserving ARM
feed 7, Linux feed 12 and 50 downloads. Its native `670961b39bb1` to `3ac5af02cb7e`
MCP-update journey `34565539266` passed two-project Save/Cancel/restart, preserved
objects, both packaged-MCP reconnections, an MCP restart into the new binary,
matching image/state and manager/schematic/PCB modules. The earlier `34564869650`
failure exposed text-only render-readiness handling in the test; its receipt is
not substituted for the clean rerun. Both native Mac architectures now have this
continuity proof. Full XML, native-Mac Codex frontend, Windows and engineering
qualification remain open. macOS 15.7+; ad-hoc signed, not notarized.

### Earlier Intel baseline — September 11, 2026

[Intel application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-670961b39bb12d162b71f38e36ce0755653e92db-macos-x64.tar.gz)
uses `670961b39bb12d162b71f38e36ce0755653e92db`, version
`preview-20260911-670961b39bb1`. Native build `34546456670`, signed staging
`469fe0`, and public verification `ca42bd` passed. Generation 28 serves Intel
feed 5 while preserving ARM feed 7, Linux feed 11, and 47 downloads.
macOS 15.7+ is required; the application is ad-hoc signed, not notarized.
The actual Intel `98c458670f26` to `670961b39bb1` update journey
`34562913032` passed Save/Cancel, both restarts, preserved objects and the native
manager/schematic/PCB modules. The older baseline lacks verified origin, so it
does not establish Intel MCP reconnection. That next native pair remains open.

### Earlier Apple Silicon baseline — September 11, 2026

[Apple Silicon application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-670961b39bb12d162b71f38e36ce0755653e92db-macos-arm64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-670961b39bb12d162b71f38e36ce0755653e92db-source.tar.gz)
identify `670961b39bb12d162b71f38e36ce0755653e92db`, version
`preview-20260911-670961b39bb1`. Extract and open `install/KiCad.app`; the
companion STDIO entry is `kicad-mcp`. macOS 15.7+ is required; this is ad-hoc
signed, not notarized. Managed updating uses the verified installer described below.

Native build `34546456670` passed for Apple Silicon. Signed staging `5ad528`
and public `be8fc2` passed; generation 26 serves ARM feed 6 while retaining
Linux feed 11 and Intel feed 4. All 44 downloads were checked. Native journey
`34556263479` verified the actual public `98c458670f26` to `670961b39bb1`
Update/Cancel/Save path, two preserved designs and manager/schematic/PCB modules.

This build contains verified native-session origin and MCP reconnection support.
The preceding build lacks that origin, so this update journey is not proof of
post-update MCP adoption on Mac. The subsequent origin-aware pair passed on Apple
Silicon as described above; Intel's newer baseline is also listed above.
Windows delivery and full engineering/Codex Desktop qualification remain open.

### Previous paired Mac checkpoint — September 11, 2026

[Apple Silicon application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-98c458670f26dd7da6a2cf55aea1077ffb744f49-macos-arm64.tar.gz),
[Intel application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-98c458670f26dd7da6a2cf55aea1077ffb744f49-macos-x64.tar.gz),
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-98c458670f26dd7da6a2cf55aea1077ffb744f49-source.tar.gz)
identify `98c458670f26dd7da6a2cf55aea1077ffb744f49`, version
`preview-20260910-98c458670f26`. Extract and open `install/KiCad.app`; the
companion STDIO executable is `kicad-mcp`. macOS 15.7+ is required. These builds
are ad-hoc signed, not notarized.

Both native builds passed `34530295753`. Public check `9dcab8` verified all 38
downloads and signed feeds on September 11 at 00:17–00:18 UTC. That generation-23
checkpoint served Apple Silicon sequence 5 and Intel sequence 4, preserving the
then-current Linux sequence 9 and prior downloads. Later Linux deliveries below
preserve those Mac feeds. The source contains the concurrent-preparation,
caption-visibility and shared callback-class fixes described below.

The fixed `2149b1d48295d00ee97a48295b285a064dba9a18` baseline remains available
for [Apple Silicon](https://kicad.vr.ae/artifacts/kicad-codex-2149b1d48295d00ee97a48295b285a064dba9a18-macos-arm64.tar.gz)
and [Intel](https://kicad.vr.ae/artifacts/kicad-codex-2149b1d48295d00ee97a48295b285a064dba9a18-macos-x64.tar.gz).
The first fixed-pair UI run (`34546066845`) failed before clicking Update.
Separating preparation from UI readiness and revealing the owning window fixed
both native architectures in `34547458961`: actual Update/Cancel/Save, preserved
objects, both restarts and the independent project passed. The concrete
concurrent-preparation and visible-control outcomes are closed. The extended
actual manager/schematic/PCB module journey then passed on Apple Silicon
(`34551617264`) and Intel (`34551788482`) using harness `3ac5af02cb7e` against the
same frozen package pair. Both projects retain their objects through updates,
open their requested PCB through the native menu, and show no duplicate
Objective-C class registrations. The concrete module issue
`p2e0d3607542a597b` is closed; full engineering/updater qualification is not.
These test repairs did not change the frozen package bytes. These
downloads are not full updater, Codex Desktop or engineering qualification.
They also predate the newer verified-origin MCP reconnection code.

### Earlier Mac checkpoint — September 10, 2026

[Apple Silicon application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-cd4934ad7bcb28c50d35586e6d022bda0d2ffee8-macos-arm64.tar.gz),
[Intel application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-cd4934ad7bcb28c50d35586e6d022bda0d2ffee8-macos-x64.tar.gz),
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-cd4934ad7bcb28c50d35586e6d022bda0d2ffee8-source.tar.gz)
identify `cd4934ad7bcb28c50d35586e6d022bda0d2ffee8`, version
`preview-20260910-cd4934ad7bcb`. Native build `34507122230` passed both architectures.
Publication `82fa2d` staged Apple Silicon feed 3; `91b9e2` staged Intel feed 2.
Public check `t20260910T202523Z-d7119c` verified all 32 downloads at
`2026-09-10T20:26:23.3002219Z`. Generation 19 preserves earlier downloads and the
Linux/Apple Silicon feed bytes. The Apple Silicon archive SHA-256 is
`35d39e37dfca1661183189b0353c7a4a1f146a27f650fc76e956f1bee5354c02`.
The Intel archive SHA-256 is
`2ef1a2764338e216ba2b4b011d2fb84803879401f47c4153c281c2ee057d7202`.

Extract and open `install/KiCad.app`, or use `./kicad-mcp` for STDIO tools.
macOS 15.7+ is required; the app is ad-hoc signed, not notarized. The real
Update-button journey from the previous `969193719393` baseline ran as
`34519945161`. The first editor passed Cancel/Save/restart and object preservation,
but the second preparation failed on the shared registration lock. Concurrent
Mac updating is therefore not qualified. Its source-controlled baseline envelope contains only public
signed metadata; the private publisher remains on the VPS. This is still a
preliminary package, not full updater, Codex Desktop or engineering qualification.

The native run also exposed unavailable caption-button actionability and duplicate
Objective-C callback class registrations across editor modules. Fixes in
`ef3f0f1140` were checked in `34521984823`; its UI driver needed an explicit
Accessibility enum conversion. Corrected source `c3b4b7ece7` passed all seven
executed checks on each Mac architecture in `34522786172`, including:
bounded cancellable registration waiting, actual button visibility/hit-testing,
and one runtime callback class with independent per-window callbacks. These fixes
are not present in the published `969193`/`cd4934` pair. A fixed package pair and
the real two-project journey remain required; the concrete gaps are tracked as
`p90e20f6eae915471`, `p75f1ac017c017371`, and `p2e0d3607542a597b`.
Fixed-baseline Apple Silicon build `34523682888` and Intel build
`34524407792` at `2149b1d482` subsequently passed and are retained above. That source also refreshes a background
registration's internal selection snapshot after download; user activation and
native document stale checks are not relaxed.

### Previous Apple Silicon update baseline

[Apple Silicon application and MCP](https://kicad.vr.ae/artifacts/kicad-codex-9691937193936d257f96da31b825e20f00aa88d9-macos-arm64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-9691937193936d257f96da31b825e20f00aa88d9-source.tar.gz)
identify `9691937193936d257f96da31b825e20f00aa88d9`, version
`preview-20260910-969193719393`. Extract the archive, open `install/KiCad.app`
for the native application, or launch `./kicad-mcp` for STDIO tools. macOS 15.7+
is required; the application is ad-hoc signed, not notarized.

Native Mac run `34493864978` passed its build, installation, signatures,
dependencies, runtime probes and selected native/managed checks. Publication
run `b903fd` retained that native evidence and signed platform feed sequence 2.
Public run `t20260910T173315Z-1c8eb6` verified all 29 downloads and platform feeds;
download observation was `2026-09-10T17:34:27.7285857Z`. Generation 17 preserves
the existing Linux and Intel feed bytes and all older archives. The public
archive SHA-256 is `833668210a23e2570565a30e4b2f4754f5ea54b52cea088e6e7ac00e9786bbc3`.

This build includes the Mac staging, installation and restart code described
below. Unpacking alone does not configure managed updates: the verified
`--install-package` bootstrap remains separate. The full dirty-document caption
update and actual Codex Desktop journeys are not yet qualified. A distinct
candidate at `cd4934ad7b` is now published for Apple Silicon above; the Intel
job in `34507122230` remains in progress.

## Latest preliminary Linux update — September 11, 2026

[Linux application](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T052623Z-7b22b31c2c74-debian13-x64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T052623Z-7b22b31c2c74-source.tar.gz)
identify `7b22b31c2c74ea9ddeb845df2d9b4100ccca6bd0`, version
`preview-20260911T052623Z-7b22b31c2c74`. Package `cf3e28`, signed staging
`bf63ca`, and public update/MCP regression `abc707` passed. Generation 31
serves Linux feed 13, preserving ARM feed 7, Intel feed 6, and 52 downloads.
The application SHA-256 is
`bc8c429171ac82a8fd91fbcfc7e019ae0c794c8aa53535cbf2e3e5b37ba3196b`.

This build adds explicit invalid-argument and native-transport error results.
Native instance contracts passed on both Mac architectures and Windows after
making test fixture paths platform-local; production path validation was not
relaxed. The public upgrade from `8776ebe319af` preserves two designs and both
packaged MCP reconnections, including restarting MCP into the candidate binary.
The already-connected Codex task may still run the older MCP process after a
configuration edit: inspect the live connection instead of assuming hot reload.
The final malformed-endpoint check in that live client remains open until it
reconnects. Full XML, Windows application delivery and overall qualification
remain unfinished.

### Previous Linux checkpoint — September 11, 2026

[Linux application](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T043346Z-8776ebe319af-debian13-x64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T043346Z-8776ebe319af-source.tar.gz)
identify `8776ebe319af36041f5dadef9ed4f409b224d2cc`, version
`preview-20260911T043346Z-8776ebe319af`. Generation 29 serves Linux feed 12,
preserving ARM feed 7, Intel feed 5, and 49 downloads. The application SHA-256 is
`a5b763e437139d71347d873210ea1f3b2a87a0fa856d7f8227deebb3c0683dbb`.

Package `fe7e30`, signed staging `1b9cb4`, and public update/MCP check `7ca7a6`
passed. Actual current-task Codex run `f6df6f` exercised this packaged MCP: a
wrong-instance attachment returned `instance_mismatch` and its explanation,
left both registrations unchanged, and subsequent connected moves, retries,
stale rejection and native undo preserved both designs. Snapshots and returned
images identified matching revisions. This extends the prior direct-client
journey without claiming native-Mac frontend or automatic reverse XML proof.

The exact machine-local `.codex/config.toml` is excluded when untracked and
rejected if tracked in a public source commit. Unknown uncommitted source is
still rejected. The subsequent invalid-endpoint argument-error extension is
source-tested but is not in this frozen package. Full native Windows, XML,
routing, simulation and Desktop qualification remain open.

### Previous Linux checkpoint — September 11, 2026

[Linux application](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T015249Z-3ac5af02cb7e-debian13-x64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T015249Z-3ac5af02cb7e-source.tar.gz)
identify `3ac5af02cb7eee653578e0d5c85b7d841eb8724a`, version
`preview-20260911T015249Z-3ac5af02cb7e`. Generation 25 publishes Linux feed
sequence 11, retaining both Mac feeds and all 42 downloads. The application
archive SHA-256 is `0a098f179e6abc7b82ecf4e0ab6f8b9241a5e3dc304b5276856c5f9b8ea7ce31`.
Use the verified `--install-package` bootstrap described below for managed updates.

Package `bcd1ab` and signed staging `91a534` passed. Public run
`t20260911T021009Z-7786f9` passed at 02:15:00 UTC using the authentic `de1dc4303ab2`
baseline. It exercised actual caption rejection/Cancel/Save/restart, stable
schematic text objects, two packaged-MCP reconnections through verified native
origin, matching images/state, and an MCP restart using the candidate binary.
The same-operation retry was idempotent and the other design remained usable.
The unsaved-document fixture uses the identity returned by creation; it does
not save early or reopen a nonexistent file to make the test pass.

This closes the Linux public-update-to-MCP evidence gap, not the full cross-platform
outcome. Mac and Windows origin-aware package pairs and their MCP journeys remain
in progress. Automatic XML synchronization, actual Codex Desktop and full
engineering qualification remain open; receipt qualification flags are unchanged.

### Earlier Linux checkpoint — September 11, 2026

[Linux application](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T005514Z-de1dc4303ab2-debian13-x64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260911T005514Z-de1dc4303ab2-source.tar.gz)
identify `de1dc4303ab29dec168dea6bcba41df188cbd235`, version
`preview-20260911T005514Z-de1dc4303ab2`. Generation 24 publishes Linux feed
sequence 10 and preserves the Mac feeds and earlier downloads.

Native package `c5c6b5` and signed staging `6bfe62` passed. Public run
`t20260911T010358Z-2f1ad7` passed at 01:08:18 UTC: authenticated downloads,
actual caption Update/Cancel/Save/restart from `9b748bcdb3c5`, a second independent
instance and the empty manager. The application archive SHA-256 is
`d82a8c9147ea672b645e0581670ecd608d98ba3fb9d24c01f4af5945efd9d77c`.
Use the verified `--install-package` bootstrap for managed updates; unpacking
alone does not configure the installation context.

This build includes verified-origin MCP reconnection, but the older update
baseline does not supply that origin. The public caption journey therefore
does not prove post-update MCP adoption from that baseline. Separate real Linux
STDIO tests prove reconnection, persistence across MCP restart, stale rejection,
object preservation and an untouched dirty neighboring instance. Cross-platform
reconnection, full automatic synchronization and actual Codex Desktop remain
open. Windows has no public package yet. Qualification flags remain false.

The manual `native-delivery.yml` Windows `integrated-update` fixture is prepared
for a published baseline/candidate pair. It requires their exact commits and a
retained `baseline-win-x64-COMMIT.signed.json`; it runs native UI-driver checks
before downloading the pair. It then exercises actual caption Cancel/Save,
two dirty designs, replacement identity and preserved objects. Until real
Windows packages and this journey pass, the fixture is verification code, not
evidence of a working Windows update delivery.

### Earlier Linux checkpoint — September 10, 2026

[Linux application](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260910T105342Z-9b748bcdb3c5-debian13-x64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260910T105342Z-9b748bcdb3c5-source.tar.gz)
identify native/managed candidate `9b748bcdb3c5aff57342ca00048d84163ae20029`,
version `preview-20260910T105342Z-9b748bcdb3c5`. The authenticated
[preview feed](https://kicad.vr.ae/updates/preview.json) advanced to sequence 9.
Use the verified `--install-package` bootstrap described below for managed
installation and automatic update context; simply unpacking is not that bootstrap.

Native package run `t20260910T105152Z-b27f17` checked the installed application
and produced the exact-source archives. Public run `t20260910T111624Z-895aa6`
verified all 27 downloads and the signed upgrade from `8c88184d3333` through
the real caption action, including Save/Cancel, two project sessions and the
empty manager. Public download observation was `2026-09-10T11:17:08.608667Z`;
the later management delivery receipt is not that observation time.
Both existing Mac downloads remain available. No Windows package is part of
this delivery; the corrected installed-editor candidate is tracked in GitHub run
`34505726568`.

This remains preliminary. Receipt qualification flags have not been promoted.
Mac updating (`p4d6c4ee22fd8078d`), Windows updating (`p67f11d25763f499e`) and
actual Codex Desktop qualification (`p8bf96f1c4b709a28`) remain explicit open
outcomes alongside the original engineering milestones. These checks do not
establish native Mac/Desktop execution, complete XML synchronization or routing.

## Mac update staging checkpoint — September 10, 2026

The compiled updater can now prepare a signed Mac TAR.GZ candidate in a new
private directory without changing the selected installation or closing editors.
It uses the system libarchive C API to inspect original archive bytes, including
AppleDouble and extended signature metadata, then the native Mac archive utility
to preserve those records. Paths, aliases, permissions, expanded size and file
hashes are checked before native signature, architecture, KiCad commit and MCP
transport verification. Failure leaves the existing installation untouched and
retains private diagnostics. Staging returns `installationReady=false`.

Native GitHub run `34476424199`, helper source
`6ffc01441a775d2d6916bd7c8cf2b15c55ea3193`, passed 21 executed checks on each
Mac architecture. Its staging fixture used the already-published native bundles
`f79a41ed7aae` (Apple Silicon) and `4c6a88502a4c` (Intel), each with 45
AppleDouble records and 45 extended-attribute entries. It verified the real
signatures/tools and rejected a wrong declared native commit. The fixture
publisher was isolated test material; the persistent preview private key was
not sent to GitHub. Linux run `t20260910T121612Z-dc5cfd` supplies supporting
archive and existing-updater regression evidence, not native Mac execution.

The Apple Silicon build above includes this staging code; the older Intel
download predates it. A staged directory alone is not a ready update; the
installation and lifecycle checkpoints below remain separate from final updater
qualification.

### Windows update preparation (source checkpoint)

The compiled Windows updater can verify and extract a signed ZIP into a new
staging tree, check its x64 native entry points and run the candidate's own
KiCad commit/MCP transport probes. ZIP inspection covers bounded classic/ZIP64
directories, actual counts, names/device aliases, links/reparse points, expanded
limits, CRC32 and file hashes. Failure or cancellation preserves existing
installations and retains bounded diagnostics. It returns `installationReady=false`.

Native Windows run `34510670116`, helper `0c37eb8888`, passed 62 executed checks
including a deliberately synthetic executable staging fixture: bad hash/commit,
subprocess failure, excessive output, cancellation and recovery. This is not a
real KiCad archive qualification or Authenticode verification. Linux run
`t20260910T175159Z-79afb5` passed 51 focused archive and updater-command checks;
its native Windows fixture was skipped.

`--prepare-update --configuration ABSOLUTE_JSON` now supports a trusted
`win-x64`/`zip` configuration. Standalone staging may omit `installationRoot`;
configurations generated by the verified version store bind that root, publisher,
installed envelope and private state/staging paths. Store-bound preparation
registers a candidate without selecting it and returns `installationReady=false`.
Changed configurations are rejected before network preparation. This source is
not yet a public Windows package or a complete installer. The low-level selection
mechanism does not authenticate packages or launch applications by itself.
Installer integration, the caption Update action, restart/recovery and actual
end-to-end Windows updating remain open under `p67f11d25763f499e`.

The exact retained Windows archive `84359da19e3b` subsequently passed real native
staging in run `34513479435`, helper `bf06326bb4`: 3,048 entries and 807,919,237
expanded bytes, native KiCad commit and packaged MCP runtime checks, followed by
wrong-commit rejection. The test used an isolated publisher and did not operate
the graphical editor. That older candidate predates the startup fixes and the
installed-editor publication gate, so it is not a public Windows delivery.

Source `d0441539dd` adds authenticated retained-version storage and selection:
create a new store, register without switching, verify retained payloads, identify
an old executable independently of the current selection, and bind activation
or rollback to exact selection/operation identities. Rollback retains the failed
candidate and preserves the accepted network checkpoint. Store and configuration
checks passed natively at `70ad68d505` in run
`34515993302`. These use a synthetic native payload and do not qualify an
installed KiCad update.

Source `dacee066bf` adds statically linked GUI/console root launchers, Windows
`--install-package`, and `--launch-installed` dispatch to a verified selected
version. The root launchers retain the initially installed helper and preserve
STDIO/argument boundaries; the managed helper validates full retained payloads.
Native rerun `34518970195` at `a7e2438d7a` passed 91 executed checks, including
compiled installation, changed-bootstrap rejection, Unicode, STDIO, selected
managed launch and dismissal of a real launch-error dialog. The test reader was
corrected to decode UTF-8 protocol bytes instead of the Windows default code
page. The payload is synthetic: actual installed KiCad/editor/update qualification
remains open. A caller-working-directory refinement is under native check in
`34519626858`, which passed 91 native checks. Windows kernel identity and
query-only pinned process-handle waiting passed native run `34520881553`.
Source `2149b1d482` now implements the Windows handoff/verified replacement path,
including durable old-version intent, cancellation before activation, selection
rollback after definite launch failure and preservation of uncertain live
replacements. Native preflight/cancellation verification passed in `34523685492`
using an explicitly synthetic payload and a real Windows process. It verified
persisted intent before acknowledgment, no original-process signal, unchanged
selection on cancellation, and no duplicate handoff execution. Real Windows
handoff run `34525358151` reached a successful native restart but then failed an
exact path comparison because generated paths used mixed separators. Source
`a2c942a29e` normalizes the derived executable paths. Rerun `34526278706` passed
real native cancellation, restart and restoration of the correct older retained
editor after a deliberate failed launch, preserving the newer shared selection.
Its release sequence and publisher are isolated test data; it does not qualify
a different-build, dirty-document caption update.
The compiled Windows `--restart-update` command now uses schema version 3 and
stops depending on the original window's stdout after acknowledgment. KiCad's
native Windows request builder now records the kernel process creation time as
an exact decimal string, preserves Unicode project paths, and reads the current
selection at the time of the click. Its C++ output passed the actual managed
schema and kernel-identity checks in native run `34530292881`. Native caption
Update integration and full dirty-document update qualification remain open.

Windows inspection and explicit interrupted-update recovery are now implemented
at source `cc3d606493`: `--inspect-update --installation ABSOLUTE_ROOT --operation
UUID` reads existing journals and live native identities without mutation;
`--recover-update --installation ABSOLUTE_ROOT --operation UUID --attempt UUID`
requires the original editor to be confirmed gone and persisted proof that no
replacement launch was reached. It reopens the exact verified previous version
without changing the shared selection. Repeating an attempt does not launch
another editor. Cancelled, legacy, live or ambiguous histories are not guessed.
The expanded real Windows journey passed in `34528431709`, including interrupted
recovery and a retry that did not launch another process. This is not full
dirty-document or caption-update qualification.

### Mac installation and restart checkpoints

Helper `062db56c9231fdf3662b81a4bade9b34a3aec2d5` passed native registration,
activation, rollback, stale-request and drift checks on both architectures in
run `34480071790`. Run `34481075647` additionally verified the compiled
`--install-package` entry point at `396598a2cb5e2aaef8cf09838395d1a520513a0d`.
Installations keep signed bundles in separate retained version directories;
registration does not change selection, and rollback does not lower the accepted
update-feed checkpoint. Existing live versions stay available after selection.

The Mac restart helper at `459e549e3e01e421b0ef8c3ecfedd50e04ed7a65` passed
native run `34483327352` on both architectures. It verifies boot-session,
kernel start time and physical executable identity before acknowledging close,
persists intent, waits for that exact process, and verifies replacement
instance/project/epoch through native IPC. The fixture covers cancellation,
wrong-process rejection, clean empty-manager restart and restoration after a
real startup failure. It does not prove a dirty-document caption-click journey.

The native caption component at `3c0699fc4f37828eb1a4cad1366f2c94f69e3cc4`
passed `34483913703` on Apple Silicon and Intel: actual AppKit rendering,
visibility, disabled actions, independent windows and light/dark narrow/wide
layouts. It reuses the platform implementation compiled into KiCad, but component
invocation is not proof of an entire application update.

Mac helper commands now include `--inspect-update --installation ABSOLUTE_ROOT
--operation UUID` and explicit `--recover-update --installation ABSOLUTE_ROOT
--operation UUID --attempt UUID`. Inspection is read-only. Recovery only reopens
the verified previous editor when the saved journal proves no replacement launch
was reached and the original is gone; retries do not duplicate a live process.
Native verification of this interrupted-helper path (`007521cca5`, run
`34485495786`) passed on Apple Silicon and Intel. Each target executed three
checks covering the kernel identity binding and the real restart/restore/
interrupted-recovery journeys; the non-Mac refusal control was not executed
on Mac. Retained receipts confirm that a live original is not duplicated,
recovery retries reuse their recorded outcome and shared selection is preserved.

The older `3c0699fc4f` builds, Apple Silicon `34484262464` and Intel
`34485572638`, completed native compilation but failed the later NNG binding
tests. Their diagnostic archives are not releases. The fixed Apple Silicon
build is now published above; the new Intel candidate is still building.
The integrated dirty-document Update-button journey, final recovery coverage,
same-candidate packaging and actual Codex Desktop operation remain open under
`p4d6c4ee22fd8078d` and `p8bf96f1c4b709a28`.

Windows run `34467734029` retained both its preparation checkpoint and final
failure logs. The standalone updater test was missing its `kicommon` link
dependency after adopting KiCad's JSON import wrapper. Source `84359da19e3b`
adds that dependency and a missing-provider negative link fixture; the repaired
delivery is run `34487042181`, not a published Windows package yet.

Platform feed routing is implemented and published. Mac update origins are
`https://kicad.vr.ae/platforms/osx-arm64/` and
`https://kicad.vr.ae/platforms/osx-x64/`; each serves `updates/preview.json` and
only matching platform archives under `artifacts/`. Apple Silicon feed sequence 3
and Intel feed sequence 2 now reference the matching `cd4934ad7bcb` previews.
Windows routing is implemented, but its real feed remains absent until a package
is available. This remaining delivery is tracked in `pe405c1d3374a315a`.

The root Linux feed remains byte-for-byte unchanged at sequence 9. Public run
`t20260910T143946Z-2b1ab9` verified both native feed downloads at
`2026-09-10T14:41:26.4166281Z`, all 27 archive downloads, and the real older Linux
`8c88184d3333` Update-button Save/Cancel/restart journey into `9b748bcdb3c5`.
That earlier generation-16 check is recorded by release `vd8776e3a2dacf0ae`.
The current generation-17 package delivery is described above. Neither record
establishes full updater qualification.

Use `kicad-validate stage-platform-feeds --previous PUBLIC_ROOT --output NEW_ROOT
--publisher TRUSTED_PUBLIC_SPKI --sources DECLARATION_JSON` to publish a verified
set of feed changes. The declaration has `schemaVersion: 1` and `feeds`, each
with `platform`, `channel` and an absolute `envelopePath`. The command verifies
already-published target bytes, signatures and sequence advancement before
staging a new tree. Unchanged payloads preserve existing signature bytes without
file churn; other feeds and downloads are retained. It never loads a private key
or publishes the live server itself.

Full Apple Silicon build `34484262464` compiled and installed the native app,
but its final managed tests exposed NNG's resolver reopening a deleted temporary
library path for later function bindings. Its retained diagnostic archive is
unqualified. Source `a82a87fc53` pins the selected native handle for the process
lifetime; `9691937193` makes the isolated compiled regression helper a declared
test dependency. Native regression `34493490753` passed on both architectures:
the fixed binding survives removal of its selected pathname, and an isolated
copy of the former resolver fails as expected. Full Apple Silicon retry
`34493864978` uses exact source `9691937193936d257f96da31b825e20f00aa88d9`.
No archive from the failed full run is promoted to a public package.

### Integrated Mac Update-button verification

Native run `34495577161` observed Accessibility and screen-capture access on both
hosted Mac architectures without changing permissions. The compiled QA driver
checks the process boot session, kernel start time and executable before native
Accessibility actions, and requires a unique enabled button. External-control
run `34498294011` passed on both architectures at source `258edf512079`: it
captured the actual other-process window, rejected stale identity and missing
targets, then pressed its native button. The receipt explicitly keeps
`applicationUpdateJourneyVerified=false`; this is not the whole KiCad journey.

The `NativeMacIntegratedUpdate` test opens two real projects with unique unsaved
schematic objects. It presses the actual Update and Cancel/Save controls, checks
that Cancel preserves the in-memory object and UUID, then verifies saved bytes,
object identity, new native epochs and isolation of the second project through
both updates. It uses normal installed-helper discovery and an authentic HTTPS
feed; it does not inject a replacement handler or bypass TLS verification.

Run it on a Mac against explicitly selected, frozen release inputs:

```sh
KICAD_MAC_UI_BASELINE_ARCHIVE=/absolute/prior-mac-package.tar.gz \
KICAD_MAC_UI_BASELINE_ENVELOPE=/absolute/prior-installed-envelope.json \
KICAD_MAC_UI_BASELINE_COMMIT=EXACT_BASELINE_COMMIT \
KICAD_MAC_UI_PUBLISHER_SPKI=/absolute/trusted-preview-publisher.spki \
KICAD_MAC_UI_ORIGIN=https://kicad.vr.ae/platforms/osx-arm64/ \
KICAD_MAC_UI_EXPECTED_COMMIT=EXACT_CANDIDATE_COMMIT \
dotnet test automation/KiCad.Automation.slnx --configuration Release \
  --filter TestCategory=NativeMacIntegratedUpdate --logger trx \
  --results-directory /absolute/new-evidence-directory
```

Use `platforms/osx-x64/` on Intel. The baseline must already contain native Mac
Update-button integration; the older initial public Mac packages do not. The
candidate must be a different actual source build, and its signed feed must stay
unchanged during the journey. Missing inputs or unavailable native UI access are
not successful qualification. The first Apple Silicon pair failed the second
project update; the concurrency and native-caption fixes have focused native
evidence but still require a complete fixed-pair journey. `p4d6c4ee22fd8078d`
remains open.

GitHub's manually dispatched `integrated-update` fixture accepts
`mac_update_baseline_commit` and `mac_update_candidate_commit`. Use `target=mac`
for both architectures, or select either architecture explicitly. The signed
baseline envelopes must already be retained in trusted source as
`automation/distribution/releases/baseline-osx-ARCH-COMMIT.signed.json`, where
`ARCH` is `arm64` or `x64`. The fixture verifies the declared baseline commit,
then the candidate commit and signed feed on that native architecture. Missing
envelopes fail before an editor starts. Its `source_commit` identifies the test
harness; it does not replace helpers inside either signed application package.

## Current source boundary

### Automatic component creation and reattachment (Linux source qualification)

The initialized design recovery workflow can now create unconnected components
whose exact part/unit definitions already exist in the schematic. Supply new
component definitions, component instances and symbol occurrences with placement
in the engineering section of `design.xml`; retain the existing native snapshot
and bindings. The planner generates deterministic symbol and placed-pin IDs,
copies the required library cache to target sheets, and keeps repeated-sheet
instances on one physical drawing. Existing circuit, sheet and binding changes
must be reconciled separately. New libraries, connected additions and missing
coordinates still require their unfinished definition, wiring and layout workflows.

For an explicitly attached instance and initialized recovery record, use
`kicad_design_automatic_sync_start(instanceId, recoveryPath, designPath,
expectedRecoveryRevision)`. Its returned session is inspected through `_list`,
waited on with `_wait` and stopped with `_stop`; `_resume` requires the current
paused sequence. File notifications and native commit events drive supported
application. Stopping the service leaves KiCad running and preserves dirty work.

Reloading a document changes its native epoch. Ordinary recovery refresh refuses
that change. Stop the old sync session, read the new epoch using
`kicad_schematic_checked_state`, then call
`kicad_design_recovery_reattach(instanceId, recoveryPath, expectedRevisionToken,
expectedDocumentEpoch)`. This records a fresh observation without editing KiCad,
writing XML or advancing the synchronized baseline. Pending old-session actions
must be resolved first. Inspect `kicad_design_sync_plan` for intervening edits,
then start automatic synchronization with the returned recovery token.

Run `devcoordinator2 test start . --test native-xml-component-creation --tier
development --client codex` for the two-editor Linux journey. It checks XML-driven
root/repeated-sheet creation, unchanged connectivity, exact retry, cancellation,
stale-request rejection, service termination after native commit, automatic
file-event creation, keyboard undo/redo, save/reload and public MCP reattachment.
This is source qualification, not a claim that existing downloads contain these
changes, that layouts are human-reviewed, or that Mac/Windows/Codex Desktop are
qualified. General creation remains open in outcome `pd983fe2bff600f07`.

### Engineering model and existing source capabilities

The compiled C# service uses the official MCP SDK over STDIO and generated KiCad
protobuf messages over a native NNG binding. It provides explicit session start,
attach, reattach, inspection, root-schematic opening and open-document queries.
The read-only component guidance tool resolves version-pinned class and instance
guidance with explicit conflict results. `kicad_engineering_design_validate`
validates the combined electrical, architectural and instance-guidance model
against explicitly supplied, version-pinned knowledge libraries. It returns
inherited/local guidance, named-property conflicts and abstract connections that
have no explicit net realization. The strict `engineering-design:1` XML section
preserves these owners together; it is not a native schematic snapshot and does
not generate or synchronize native files. Missing libraries and unknown fields
are errors, not silently discarded inputs.

Guidance statements may also carry an optional typed `quantity`: explicit unit,
nominal/minimum/maximum decimal values, absolute or percent minus/plus tolerance,
and a reason when the value is unknown. Classification is explicit:
`Unclassified`, `Nominal`, `OperatingLimit`, `AbsoluteMaximum`, or `Measurement`.
It is independent of requirement strength and verification. Applicability and
document revision/page/table/part-variant evidence stay on the owning statement;
library inheritance and explicit instance replacements preserve the old quantity.
An absolute maximum is never converted into an operating recommendation.

The resolver preserves contradictory ranges and reports `quantityIssues` with
the exact statement IDs. A valid response means structurally valid input, not
verified engineering claims or a conflict-free design. Unknown values remain
unknown; values outside exact supported decimal precision are rejected rather
than rounded. Existing text-only XML is unchanged. This is explicit model input
and compiled MCP resolution, not automatic datasheet interpretation, arbitrary
unit conversion, physical verification, or native guidance-authoring UI.

`kicad_design_bindings_inspect` accepts the composed `design:1` document: the
engineering section, typed native schematic hierarchy and explicit sheet/symbol
bindings. It resolves by UUID and sheet path, including repeated screens and
multi-unit symbols. It reports unresolved or ambiguous links separately from
reference/value/unit differences and preserves native coverage gaps. The codec
retains unresolved bindings for repair; serialization does not authorize applying
them. This is not live synchronization, pin-connectivity comparison or proof of
complete native reconstruction.

`kicad_design_reconcile_properties` prepares a three-way reverse projection of
native reference/value/unit/placement changes into the engineering model. It
preserves concurrent instructions, reports competing edits and inconsistent
multi-unit/shared owners, and does not add coordinates to unchanged coordinate-free
models. Its candidate is only a property-level proposal: other native snapshot
changes and coverage gaps remain explicit. It performs no file writes, connectivity
comparison, live revision admission or synchronization checkpoint advancement.

`kicad_design_plan_placement` prepares forward connected-symbol placement from
the same baseline, desired engineering model, observed hierarchy and declared
libraries. It combines equal-displacement symbols on one sheet into a native
selection and resolves repeated-screen geometry once by exact identity. Existing
locks, conflicting shared placements and unsupported electrical changes prevent
partial proposals. Explicit unlocks run first, followed by ordered quarter-turn/
reflection proposals, grouped translations, and requested locks. A containing
group's lock must be removed explicitly before an individual unlock can succeed.
Transforms use exact native pivots. Equivalent angle/mirror encodings do not
request another edit. Missing coordinates do not request movement. Results retain
coverage gaps and unprojected native changes; they require live state admission
and connectivity verification before use.

The internal executor has two-editor Linux proof for root-sheet XML transforms,
native geometry capture, all six service-interruption checkpoints, no-op
convergence and native undo/redo back into XML. Its large native replies are
budgeted before commit, so a reply-capacity rejection does not leave an unreported
edit. This does not advertise production automatic synchronization or complete
cross-platform qualification. Run the focused test with
`devcoordinator2 test start . --test native-transform-sync --tier development --client codex`.
That journey also checks lock-only changes, unlock-and-move, move-and-lock,
protected group rejection, failed-batch rollback and lock history back into XML.

The internal `DesignRecoveryStore` preserves the full composed baseline, observed
hierarchy, declared library snapshots, exact desired-file bytes (including invalid
XML), and any unconfirmed native operation. Atomic replacement and revision-token
checks prevent overwriting a competing recovery record. Reopening a record does
not prove that its pending operation committed or that synchronization completed.

`kicad_design_recovery_observe` reads one absolute recovery-store path for an
explicit attached instance. An optional `expectedRevisionToken` rejects a changed
record. It verifies the exact pending native operation, then returns its retained
receipt separately from a fresh typed hierarchy snapshot. The current snapshot
may contain later edits or undo; it is not the operation's historical result.
Wrong instances, roots, document epochs and regressing revisions are rejected.
The saved baseline, desired bytes (including invalid XML) and pending command
remain untouched, including on cancellation or concurrent recovery changes.
This is a read-only recovery step, not automatic reconciliation or write-back.
The compiled MCP/native Linux journey exercises failures followed by successful
observation without file churn. Actual Codex Desktop and Mac qualification remain
separate required evidence.

`kicad_schematic_hierarchy_reconcile` accepts baseline, desired and observed
native typed hierarchy XML. It combines independent changes by exact instance
paths and object identities, including supported branch insertion, removal and
reparenting. Project-wide settings retain one owner across sheet copies. Repeated
instances must agree on physical contents; only reference/unit/variant projections
validated against their instance records are excluded from that comparison.
Conflicts retain all three sheet versions and return no partial native operations.
The response includes a `snapshotToken`. To choose an entire conflicted sheet,
pass `choices` keyed by its returned `instancePath`, with `xml`, `native` or
`baseline`, and the same token as `expectedSnapshotToken`. Whole-sheet selection
intentionally replaces that complete version, including independent edits within
it. A changed snapshot, unknown conflict path, unresolved sibling or incompatible
choice for a shared physical sheet cannot produce a partial batch. Choices are
not persisted and do not authorize live edits.
The internal `DesignRecoveryStore.ResolveHierarchy` API can retain reviewed
choices separately in a version-2 recovery record, using the recovery revision
token to prevent competing writes. Reopening revalidates their hierarchy token
and native epoch/sequence. A changed hierarchy or native revision requires
clearing the old resolution before saving refreshed state. Invalid desired XML
cannot be resolved, but remains recoverable. Records without choices retain the
version-1 format. `kicad_design_recovery_plan` exposes the saved reconciliation
state for an explicit instance and absolute recovery-record path; it does not
contact the editor or claim the observation is current. It returns the recovery
revision and hierarchy snapshot tokens. `kicad_design_recovery_resolve` requires
both tokens and explicit whole-sheet choices and writes only that recovery
record. Neither tool advances the baseline or writes either design
representation. Keep recovery records in their own directory, separate from the
instance registry's session JSON records. The compiled STDIO journey verifies
inspection, persistent choices and reopening, wrong-instance/stale-write/locked-
file failures followed by success; direct handler checks cover cancellation
before the write. Native application and automatic synchronization remain separate.

The high-level synchronization executor is internal development work and is not
advertised as `kicad_design_sync_apply`. Version-5 recovery records can retain
candidate XML and a native-save request, but serialization tests do not establish
safe execution. Retained-file lifecycle, complete executor qualification and
automatic event-driven synchronization remain open under Coordinator outcome
`p7e712f1bb764e327`. Use the existing
read-only synchronization planner and individually qualified native tools; do not
treat the draft executor as automatic synchronization or cross-platform evidence.

Version-6 recovery records additionally bind an XML publication to its exact
operation ID, destination, staged/displaced paths, original bytes, candidate bytes
and phase. File-level recovery can distinguish a replacement that has already
happened from one still needing execution, without swapping the files back. A
changed or incomplete retained file pauses recovery; matching independent saves
converge without another replacement. Publication alone never advances the design
baseline. The record also covers publication without a schematic mutation, and
pending publication blocks fresh planning and observation refresh. The internal
native executor now uses this journal, validates native structure/connectivity,
and replays the exact saved edit/save requests after lost replies. A focused
two-editor Linux journey verifies title-metadata changes in both directions,
lost edit/save replies, unchanged reapplication and keyboard-undo write-back.
This is not proof of every edit type, process-crash recovery, the high-level MCP
surface or the automatic event loop. Retained-file cleanup remains open.
Existing version-1 through version-5 records remain
readable and are not rewritten merely because the reader supports version 6.

Version 7 binds pending synchronization to a caller-stable operation ID and the
original recovery token. Its latest completed receipt is committed atomically
with the new baseline, then retained as an immutable operation file before later
work can replace that receipt. Historical replay returns the recorded native
result without contacting KiCad; it is not a current design observation. Reusing
an ID with different inputs is rejected. The executor also checks the actual XML
before resuming native actions, even if file intake has not yet seen a save.
The focused Linux journey verifies these paths. A first synchronization may need
to establish a known full-content save checkpoint; later unchanged requests do
not rewrite the design or baseline. A compiled test-only STDIO host now calls
the same synchronization handler and is forcibly terminated after native-edit
reply, native-save reply, or completion before the MCP reply. The focused Linux
journey repeats those three boundaries for two real editor instances, reattaches
from a fresh host and verifies the same operation and revision, preserved dirty
work, native/XML agreement and connectivity. It is not a shipped server mode or
Codex Desktop proof. The journey also covers service termination after staging,
XML replacement, baseline commit, receipt archival and retained-history movement.
Completed displaced XML is moved without overwrite or copy/delete fallback into
`.kicad-sync-history/<operation-id>.xml` beside the design. Immutable receipts
resolve the original or archived location; collisions, missing files, links and
unexpected paths remain explicit. History is preserved, not purged. Cleanup
status is separate from the historical native result, and replay itself remains
read-only. Broader edit qualification and the automatic event loop remain open;
the normal MCP server still does not advertise the high-level apply tool.

`kicad_design_recovery_refresh` persists a freshly captured native hierarchy into
an existing recovery record. It requires an attached instance and the exact
recovery revision token. Baseline, libraries and desired-file bytes (including
invalid XML) remain unchanged. Competing recovery writes reject the refresh;
changed native content or revision invalidates prior hierarchy choices. An
unchanged snapshot makes no additional write. Pending mutations are deliberately
rejected: inspect and reconcile their exact receipts before replacing the saved
revision needed for retry. This operation only refreshes the recovery observation;
it does not write the design XML or edit KiCad. Incomplete native tracking remains
explicit and is not authorization for synchronized mutation.

The internal `DesignRecoveryFileObserver` subscribes to an explicit design file
before its initial read. Each notification captures the current saved bytes into
recovery, including invalid XML, without changing the baseline or editor. Atomic
save replacements are observed; failed persistence retains a notification for a
later retry. Unchanged bytes retain choices without rewriting the record, while
changed bytes invalidate prior choices. Cancellation cancels the receive call,
not the editor. `kicad_design_intake_start` owns a continuous intake task for an
explicit instance, recovery record and design path. `kicad_design_intake_list`
recovers process-local intake IDs after a lost start reply. `kicad_design_intake_wait`
inspects the latest status or waits for a newer sequence without polling. Invalid
XML stays preserved and watched; persistence failures pause until
`kicad_design_intake_resume` receives the current paused sequence. Cancelling a
wait leaves intake running. `kicad_design_intake_stop` and service shutdown await
owned tasks, so they cannot write after returning. The service rejects duplicate
normalized design or recovery paths within its registry; this is not cross-process
file ownership. Session IDs do not survive service restart; start a new intake
against the retained recovery record. These tools never launch or close editors,
apply native changes, or advance the baseline. Continuous file intake is not yet
automatic bidirectional synchronization.

The source MCP service also provides `kicad_design_native_intake_start`,
`_list`, `_wait`, `_resume` and `_stop`. Start takes an explicitly attached
`instanceId` and an absolute `recoveryPath`; the existing recovery record owns
the exact schematic hierarchy target. The observer verifies the native process
and event stream, subscribes before its first snapshot, and saves committed
native edits into recovery state while no tool call is active. Child sheets
remain in the same exact project/root scope. Missed-event hints request a new
snapshot; unchanged heartbeats do not repeatedly read the design.

Use `_wait` without `afterSequence` to inspect, or with the last returned
sequence to await a change. Persistence failures pause the observer; repair the
failure and pass the current sequence to `_resume`. Replaced processes, event
streams and prolonged silence require a newly verified attachment and intake.
`_list` recovers an ID after a lost start reply. IDs are local to the MCP
process. `_stop` and MCP shutdown await observer cleanup without closing KiCad
or discarding dirty documents. Baseline requirements, desired XML bytes
(including invalid input) and pending-operation identities remain preserved.

This native intake is **observation into recovery state**, not automatic
design-XML write-back, native delta application or complete revision admission.
Those workflows remain unfinished. Native intake is included in the Linux
preview described above; Mac/Windows qualification of these tools is separate
from the focused managed/STDIO and Linux native checks.
Linux native run `t20260911T151857Z-9b14cc` passed all 11 foundation checks on
`0fe15e0a79cf3ae3cfb65899567abd0d65a2932a`, including real MCP event intake,
native edits and keyboard undo/redo, persistence failure and explicit resume,
preserved invalid desired bytes/baseline, and graceful MCP shutdown with an
active observer. Native Mac/Windows and actual Codex Desktop qualification of
these new tools remain separate, unfinished release work.

The engineering structure also has optional typed `unresolved-net-bindings`.
These preserve an affected statement or structural connection, its former net
identity, change reason and possible current-net candidates after an explicit
split, merge, removal or identity change. Candidates are not assignments:
`kicad_engineering_design_validate` reports them separately and returns
`netBindingsResolved: false`. Requirement text, strength and provenance remain
unchanged, and explicit reassignment resolves only the selected owner. Ordinary
undeclared dangling references are still rejected. This model/XML support is
not yet connected to automatic native electrical reconciliation and is not in
the currently published packages.

Optional `unresolved-component-references` and `unresolved-guidance-bindings`
also retain component targets, either endpoint of a concrete pin instruction,
block realizations and class/instance guidance when ownership disappears or is
reidentified. Former identities, exact owner/slot, reasons and explicit candidate
identities remain typed data. Retained text, strength and provenance are unchanged.
The normal validator reports `componentReferencesResolved: false`; detached
guidance is excluded from the live assignment map. Explicit model resolution
changes only the chosen reference and refuses to overwrite another component's
guidance. Native-only deletion now uses this retention support through the
internal executor described below; general automatic ownership reconciliation
remains unfinished.

Each symbol occurrence may specify `sheet-instance` independently of its physical
component's owner sheet. Omission retains the original owner-sheet default.
This allows units of one component to occupy different sheets without duplicating
the component or its pin identities. Binding, property and placement operations
use the occurrence's exact sheet. Electrical comparison uses only active-unit
and active-body-style pins; known library pins on undrawn units are reported as
`undrawnPins`, without inventing placed identities or net connections. Missing
active pins still fail comparison. Linux real-editor run
`t20260916T011840Z-31098d` verifies cross-sheet reference/placement updates,
connectivity, preservation of another visible sheet, replay and undrawn-pin
accounting in two instances. Automatic ownership-change reconciliation and
native Mac/Windows qualification remain separate unfinished work.

The model's `PinPartitionEvolution` merges connections when exact pin sets
change. A pin absent on one side is not treated as deliberately disconnected.
Independent additions and removals can merge; deletion versus rewiring,
contradictory new-pin connections and inconsistent transitive joins return no
candidate. `t20260916T013559Z-afb38f` checks every three-/four-pin presence and
partition combination against an independent pairwise definition, plus large
sparse inputs. The net reconciler now uses this merge without inferring component
ownership or replacement identities.

The internal synchronization executor can project native-only symbol deletion
into XML through exact saved sheet paths and UUIDs. Removing one drawn unit keeps
its physical component; removing its last drawing retires the component only
when shared-definition ownership remains valid. Detached component/pin guidance,
structural instructions and retired-net requirements remain explicit. Concurrent
circuit/layout XML edits, sheet changes, new symbols and ambiguous shared-definition
retirement pause preparation without discarding either version.
Linux run `t20260916T015039Z-c53380` verifies unit and whole-component deletion,
actual pin connectivity, preserving instructions and repeat-safe XML publication
in two editors. These deletions use native API operations and explicit internal
executor calls; this is not manual rendered-UI or event-driven automatic-apply
qualification. Interrupted recovery during ownership changes, broader
creation/rebinding and Mac/Windows qualification remain open.
This source increment is not in the frozen `c26503fdc3` build or published previews.

New synchronization receipts use version 2 and bind retained prior XML to its
SHA-256 digest, derived from the immutable publication request. History inspection
and verified reading reject changed or missing bytes; archiving does not move a
changed file. Version-1 receipts remain readable for replay and inspection but do
not prove prior content and cannot supply verified automatic identity restoration.
The 49 focused checks in `t20260916T020853Z-02f629` cover compatibility, exact
digest/path binding, persistence, changed content and cancellation. Linux native
run `t20260916T022832Z-a313c3` additionally verifies exact retained bytes after
deletion and service termination/restart at publication replacement, baseline
commit and history archival in both editor instances.

Native undo restoration now reads verified historical declarations without
replacing current instructions or live objects with an old XML document. It
requires exact native owners and a compatible deletion predecessor, rejects
ambiguous mappings, and restores a historical net ID only for its exact pin group.
Only bindings that were resolved in the selected predecessor are reinstated.
Restored geometry comes from the current native checkpoint. Missing, changed,
legacy-only, foreign-circuit or future-revision history cannot authorize a candidate.
The public read-only `kicad_design_sync_plan` and internal executor use the same
history-aware preparation. Instruction-only edits can merge; competing circuit
and layout changes still pause synchronization.
Linux native run `t20260916T032035Z-c12d41` verifies real keyboard undo/redo of
unit and component deletions in two editors, including history older than the
latest receipt, exact restored identities, preserved newer instructions, actual
connectivity, normal STDIO planning and repeat-safe reverse publication.
This does not qualify event-driven automatic application, arbitrary creation or
rebinding, or the Mac/Windows/Codex Desktop journey.

When verified histories disagree, `kicad_design_owner_history_inspect` returns
the eligible mappings and an exact input-snapshot token. The corresponding
`kicad_design_owner_history_resolve` records one operation choice using both that
token and the current recovery revision; `kicad_design_owner_history_clear`
removes a stale choice without editing XML or native objects. Recovery envelope
version 9 persists a selected choice while older records retain their encoding.
History/content, XML, native revision and library changes invalidate the choice.
Pending synchronization freezes it; successful baseline commit clears it.
Focused checks cover competing mappings, stale/wrong targets, corruption,
cancellation, reopening and normal STDIO calls. Linux run
`t20260916T042123Z-84519a` also selects history through MCP, applies native keyboard
restoration, and terminates/restarts the service during publication replacement,
baseline commit and retained-history archival in two editors. Both the chosen
mapping and newer instructions survive without repeated native mutation.
These later source changes are not included in the frozen `c26503fdc3` packages.

`kicad_schematic_electrical_state` reads a loaded hierarchy and scalar-net
memberships in one native request. It accepts an explicit loaded sheet and an
optional expected revision; the sheet does not have to be visible. The native
read refreshes computed connectivity without cleanup, annotation or saving and
rejects changes across its capture. Net names are labels, not stable identities.
`kicad_design_connectivity_compare` compares supplied electrical state against
the engineering model through explicit sheet, symbol and placed-pin identities,
reporting split/joined pin partitions separately from missing bindings and
serializer coverage. It never transfers net requirements or edits a design.
These new electrical tools passed Linux native/MCP qualification in
`t20260911T183246Z-7d102b` on `cde8c2a7d539afab0e93c351f2d4d80d87ba53e8`:
the independently declared two-probe circuit matches before a manual
disconnection, reports a split afterward, and matches again after native undo.
Hidden/repeated sheets, exact revision rejection, unchanged save/view state and
all existing foundation checks passed. The tools are not yet in the published
packages. Complete revision admission, Mac/Windows/actual-Desktop qualification
and automatic electrical propagation remain unfinished.

Recovery can retain separate baseline and current electrical checkpoints.
Version 3 stores typed net XML, revision/coverage and limitations alongside the
already-owned hierarchies; legacy recovery records remain readable without
being rewritten when unchanged. `kicad_design_electrical_baseline_initialize`
requires the exact recovery token, no pending operation, an unchanged native
baseline hierarchy and agreement of explicitly mapped circuit pins. It will not
replace an existing baseline. Native intake refreshes current connectivity,
while preserving the original baseline, requirements and desired bytes. A
hierarchy-only refresh discards a now-stale electrical observation instead of
pretending it still matches. This source-only recovery integration passed all
11 Linux native checks in `t20260911T191535Z-583a7e` on
`86ac7cef7555f00a9592f09e2bd8d2bd48b90004`, including MCP initialization
refusals, manual disconnection/undo observation, preserved baseline and invalid
desired bytes, and unchanged recovery writes. It does not yet publish design
XML or apply electrical changes automatically, and its Mac/Windows/actual-Desktop
qualification remains unfinished.

`kicad_design_nets_reconcile` plans three-way pin connectivity from a saved
electrical baseline, desired XML and current native observation. It requires an
explicit instance, absolute recovery-record path and exact recovery token;
it does not establish live freshness. Independent and matching edits combine,
but contradictory connectivity returns conflicts without a partial candidate.
Exact surviving XML net groups keep their explicit identities and semantic
names. New groups receive deterministic identities; requirements on ambiguous
splits or merges remain unresolved, with possible replacements rather than
guessed assignments. Later changes update those possibilities without losing
the original requirement. Names and locations never establish identity.

The result is candidate engineering XML only. This tool does not write design
files, edit KiCad or advance the synchronized baseline. Changed component,
sheet, unit or pin ownership requires separate reconciliation. The focused
compiled STDIO checks and exhaustive four-pin partition oracle are covered by
the `net-reconciliation` development graph; `native-foundation` additionally
checks a real manual disconnection and Undo against the proposed model. This
source-only tool is not yet included in public packages; automatic publication
and Mac/Windows/actual-Desktop qualification remain unfinished.
Native run `t20260911T195851Z-e88a9a` passed all 11 foundation checks on
`a4519ba6a0ec8068c27eaccee6ea836063f5a5db`, with unchanged source. Its real
manual disconnection produced two proposed nets and an unresolved original
requirement; native Undo returned the exact original engineering XML. The
saved recovery baseline and intentionally invalid desired bytes were preserved.
The same run passed the exhaustive partition oracle, identity-collision guard,
independent/convergent/conflicting edit cases and compiled STDIO tool checks.

Successful `kicad_schematic_hierarchy_reconcile` plans include merged XML,
ordered native operations and explicit coverage gaps. That tool does not access files, apply a batch, resolve electrical
intent or advance the synchronized baseline. Concurrent edits inside a reparented
branch can still require explicit conflict resolution. Linux native tests verify
independent text/placement reconciliation, repeated-sheet operation deduplication,
reparenting and undo/redo; compiled STDIO tests verify tool results and failures.

Symbol definitions explicitly retain native De Morgan body-style semantics,
separately from ordinary custom styles named `Standard` and `Alternate`.
`demorganBodyStyles=true` requires that exact two-style pair; contradictory names
are rejected instead of silently discarded. The distinction survives typed XML,
native cache replacement, undo/redo and native save/reopen. Existing XML without
the flag retains ordinary named-style semantics; names alone are not evidence
of De Morgan intent. This does not imply complete library inheritance coverage.
The native schematic-cache parser rejects explicit `extends` declarations rather
than loading and later saving them without their parent relationship. This rule
does not change external symbol-library parsing. Already flattened root definitions
may retain a diagnostic parent name in memory; that is not persisted inheritance
and does not prevent their capture.

Registry records describe
the last verified connection, not live process health. KiCad owns its process and
project files; disposing the service's process handle does not kill an editor.

Startup persists an **unverified** launch receipt before creating KiCad. If startup
is cancelled or interrupted, `kicad_instance_pending_launches` exposes the receipt
for recovery without claiming the process is running. `kicad_instance_reattach`
checks the exact native identity and requested project before promoting it to a
verified connection record. `kicad_instance_saved_sessions` discovers historical
records after MCP restarts; listing does not attach or probe them. Previously
verified records also pin the process epoch. The Linux native journey cancels
before the first handshake, confirms the process survives and recovers it from
a fresh registry. Linux startup defaults to native software rendering; it still
requires an available graphical display. Native Mac validation remains pending.

After a verified application update, `kicad_instance_reconnect_after_update`
accepts `instanceId`, `installationRoot`, `operationId` and `expectedOldEpoch`.
It uses the private handoff's original native-session proof and verified live
replacement, then atomically replaces the saved connection and its pinned
client. Reusing the same operation is safe after a lost reply or MCP restart.
Ordinary attach/reattach cannot overwrite a saved different epoch. Legacy
handoffs without verified origin metadata are not guessed into a new binding.

The result supplies the new process and event epochs and requires fresh native
document snapshots before more edits or resumed event consumption. Linux native
journey `3b3475` verifies this through the actual STDIO MCP server after a real
KiCad restart, preserving a schematic object's text and UUID, observing a new
document epoch, rejecting old event cursors and repeating from a new MCP process.
The extended `419bb1` journey also keeps a second real project dirty and verifies
its unchanged process/event/document epochs and unsaved object across the other
project's restart and an MCP restart. That fixture uses isolated release metadata;
it is not a full cross-platform
update or automatic XML synchronization claim. Native registry contracts passed
on Windows and both Mac architectures; their full application-update/MCP
reconnection journeys remain open.

`kicad_schematic_save_state` reads native schematic save flags and the modified
sheet-instance paths without changing the visible sheet. It checks loaded
schematic screens only, not project-settings persistence or external disk changes;
it is not a discard/close authorization. The compiled MCP/native journey verifies
dirty-to-saved transitions and recovery of unchanged unsaved objects and revision
after shutting down and restarting MCP against the same saved connection record.

Typed schematic metadata also retains the project's dimensionless dash/gap,
text-offset, label-size and overbar-height ratios. The planner emits one explicit
project-wide operation across repeated sheets, rejects inconsistent projections,
and preserves conflicting ratio edits for resolution. Native batches apply this
group with scoped undo, cache invalidation, rollback and retry receipts. Linux
native checks cover actual dash-pattern changes, saved project values and keyboard
undo/redo. Other project settings remain an explicit reconstruction gap.

Native graphical line segments use `SchematicLine` with `SLT_GRAPHIC`. A generic
`SchematicGraphicShape` segment is rejected before mutation because the pinned
schematic writer cannot save that representation. This preserves the existing
native format instead of allowing an edit that later blocks saving.

The native extension adds explicit automation startup and session discovery.
Its capability response currently advertises only `session.info` and
`version.read`. A Linux native check has opened schematic windows in two isolated
instances and captured their actual software-rendered canvases as PNGs. This
preview is exposed through a preliminary MCP tool with viewport metadata and an
explicitly incomplete revision-tracking flag. Current-sheet presentation checks,
sheet activation and operation-receipt inspection are also exposed. Independent
region/layer images of any explicitly targeted loaded sheet are available through
the separate render tool below. PCB/3D views and complete synchronized
observation remain unfinished.

`kicad_schematic_render_views` accepts an explicit instance and
`RenderSchematicViews` protobuf JSON: the discovered document descriptor plus
one to four keyed views. Each view declares a nanometre `region` (`position` and
`size`), `widthPixels`, `heightPixels`, and optional `nativeLayers`. Dimensions
are 64–2048 pixels, with at most 8,388,608 pixels across the batch. Empty layers
select supported artwork independently of the GUI layer visibility, including
the native drawing-sheet frame/title block and page boundary. The response
provides the exact layer ID/name catalogue for subsequent layer-specific calls;
`drawing_sheet` and `page_boundary` can be requested separately or omitted.

These are native offscreen Cairo images, not crops of the human canvas. Regions
are aspect-fitted, and each image includes the exact pixel-to-sheet transform.
Private object copies keep painter cache/flag changes out of the user's objects.
Images share one supported native snapshot, and the MCP image blocks follow the
keyed view order. The target can be the displayed sheet, another loaded sheet, or
a particular instance of a repeated sheet, without navigating the editor. The
private painter resolves instance references, text and text geometry using that
explicit path, rather than the displayed sheet's cached text bounds. Rendering
uses printing-style artwork. Transient overlays and PCB/3D views remain unfinished. Cancellation
releases the caller's IPC wait; an already-dispatched bounded native render may
finish without delivering its images. It does not modify or undo the design.

Page frames use a private copy of the native drawing-sheet template with its own
coordinate environment and cached items; the global/alternate template is never
swapped. Title, sheet name/path, page numbering, variant text and first-page rules
use native drawing-sheet resolution. Native tests distinguish the built-in empty
layout's compatibility item from a truly void allowed layout, preserving both.
Whole-page test views derive their regions from the actual native page bounds,
not an assumed standard paper size; arbitrary requested regions still clip normally.

Linux native checks exercise overview/detail regions, notes-only and wires-only
views, exact transforms, deterministic repeated results, invalid requests and
recovery, while preserving the human image, viewport, design snapshot and unsaved
state. The compiled MCP journey delivers multiple exact native PNGs plus matching
metadata on displayed and non-visible root/repeated sheets. The repeated-sheet
journey compares full artwork and reference-only images with the root and either
instance displayed, including sheet-variable text and global labels. Explicit
text-measurement tests preserve the normal cache and cover rotated/mirrored
reference fields. This is not actual Codex Desktop
or native Mac qualification, complete render-feature coverage or complete native
revision tracking.

Presentation findings identify the sheet instance, objects, measured values and
policy thresholds. Excessive-crossing findings also include each distinct
crossing region and its involved wire IDs for local close-ups and repairs,
rather than only a bounding box spanning the signal. The Linux native journey
checks three crossings, repair to two, and same-net false-positive protection.
Text-size limits are explicit millimetre policy values, not electrical rules.
For ordinary stroke-font and outline-font text, page-fit facts use native glyph geometry,
including the project overbar metric, schematic drawing offset, rotation and
renderer-default stroke width, rather than the approximate editor hit box.
Focused native tests compare shifted glyph extents at both orientations, verify
stroke-width changes and exclude empty/device text from this measurement path.
Outline-font checks also compare the bounds with pixels painted by the actual
native Cairo schematic painter using the repository's Noto Sans font fixture,
at both orientations, for plain text, overbars and mixed markup. Cairo switches
fill/stroke state by glyph type so overbar strokes between outline letters remain
visible. A two-pixel raster tolerance covers edge quantization, not missing marks.
The native journey positions text from its measured extent just inside the page,
changes the metric to cross the edge, verifies the localized warning and clears
it through undo. Text-box containers, fields and additional font/markup combinations retain their existing
explicit coverage limits; this is not exhaustive glyph-clipping qualification.
Native facts include table cells, sheet pins and label fields with their owning
object identities. Covered merged-table cells are excluded from visible-content
findings, matching the native painter. Linux journeys check tiny text and repair
in these nested objects, including merged-cell false-positive protection.
Text boxes and table cells also expose separate native font-metric text bounds:
their rectangular containers are not clipping regions. The verifier reports
container overflow and detects text extending beyond the page even when the
container fits. Native journeys exercise horizontal and rotated multiline
overflow, restoration to a short note, and hidden merged-cell exclusion, with
retained canvas captures. These are geometry checks, not exhaustive glyph or
occlusion analysis.
Full glyph clipping/occlusion and complete multi-page coverage remain open;
an incomplete report cannot declare the diagram clear.

The full native journey retains `automation/artifacts/native-session-current`.
Focused table-variant and net-chain journeys use the same current/history layout
under `automation/artifacts/native-table-variants` and `automation/artifacts/native-net-chains`.
Before the next journey, its current directory is moved intact into the matching
`native-session-history`; older test results are preserved too. Coordinator retains
the hash-bound run archive.

The read-only `kicad_schematic_metadata` tool returns typed screen identity,
page/title data, project text variables, bus aliases and embedded-file records.
It also preserves net-chain terminal references, netclass/color and persistable
member-net names. Pending declarations with missing terminals remain present;
the `committed` flag identifies native collection membership, not electrical
verification. Live committed state takes precedence over older restore-map values.
Native import tests exercise resolved, unresolved, partial and name-only declarations
on repeated sheets, typed XML equality, save/reload and restoring the original fixture file.
The XML planner supports schematic-wide declaration replacement/removal in one native
undoable batch. Membership is recomputed: callers cannot force the observed `committed`
flag. Native journeys cover rollback, retries, no-ops and keyboard undo/redo.

In the development source, native **Tools → Create Net Chain**, two-pin creation,
chain naming and terminal replacement, and the **Schematic Setup → Net Chains**
name/class/netclass/color/deletion controls use native commits. The Linux journey
verifies both manual links and a real four-net path through three resistors in two
independent editor instances. It checks Cancel, unchanged OK, reserved-name rejection,
stale requests, exact Undo/Redo, and save/reload. Declared endpoint order and opacity
survive reload; the color picker exposes opacity and preserves unchanged values.
Run the `native-net-chains` development graph in an isolated build worktree to repeat
this journey. Its focused pass is not whole-project or native-Mac qualification.

The development removal action records explicit removed-member restrictions,
including exact placed-pin UUIDs and sheet-instance paths. It subtracts the
selected electrical net and blocks adjacent propagation without rewiring the
circuit. If the original path or endpoints no longer resolve, the declaration
stays uncommitted with its original endpoints and class intent; it does not guess
new endpoints or revive membership from a saved-net fallback. Native tests cover
label renames, splits/merges and shared-screen instance isolation; rendered tests
cover terminal, interior, multi-pin and manual no-bridge removals with save/reload
and Undo/Redo. Full integration remains tracked under `peb6785f04af5b15c`.
All-page Schematic Setup cancellation
(`pef184f5d6044415b`) also remains open in the Coordinator ledger.
These source changes are not yet part of the public downloads.

The development build's **Schematic Setup → Net Chains → Net Chain Classes**
page now keeps a class created with **Add** even before a chain uses it.
The class registry is stored separately from its assignments in the project;
older assignment-only projects retain their existing class names. Focused Linux
journeys cover empty/duplicate-name rejection, individual-dialog and whole-setup
Cancel, unused and assigned class rename/delete, exact Undo/Redo, and native
save/reopen without losing unrelated assignments.

Typed schematic metadata includes `net_chain_classes` with `definitions` and
`assignments`. The XML planner emits one validated project-wide operation across
repeated sheets. Independent edits merge; deleting a class while another edit
assigns a chain to it produces a conflict, not a guessed reassignment. Literal
Unicode names survive XML and presentation-order changes create no native edit.
Snapshot schema 3 includes this state. Requests for schemas 0/1/2 receive an
explicit coverage gap and no unknown class fields; absent state never means an
instruction to clear the registry. These are KiCad net-chain groups, not the
separate inheritable component-guidance classes. Broader integration and platform
qualification remain distinct from this focused Linux evidence.
Snapshot schema 4 adds presence-aware chain exclusions. Earlier schemas omit
those fields with an explicit coverage gap; an older/incomplete replacement
cannot clear existing restrictions by leaving the exclusion field out.

Snapshot schema 5 adds the project annotation policy: numbering order (X/Y),
method (incremental, sheet × 100, sheet × 1000), exact starting number and
reference reuse. Typed XML retains these independently of symbol positions and
already placed references. Changing the policy uses one revision-bound native
commit; independent policy-field edits merge, while competing values conflict.
Older snapshots report the missing policy explicitly instead of supplying defaults.
The focused Linux Setup journey covers real Annotation-page edits, page-switch
Cancel, complete policy import, native Undo/Redo, retry/stale/no-op behavior and
save/reopen. Run `devcoordinator2 test start /absolute/isolated/worktree --test
native-setup-dialog --tier development` against the development source.
Snapshot schema 6 adds `reference_inventory.allocated`: explicit entries such as
`R17`, including historical references absent from the current schematic. A
replacement leaves numbering policy unchanged, validates against native
persistence before mutation and is undoable. Missing legacy data cannot clear
the inventory; competing allocation-history changes remain conflicts. The
focused Linux journey has exercised XML apply, invalid input, no-op/retry/stale
requests, Undo/Redo, explicit clearing and save/reopen. The strengthened range
expansion guard and composed integration remain under qualification in
`p72f9010349b0093e`. Remaining project settings, whole-design reconstruction and
native-Mac qualification stay open. These are source-build integration features,
not new visible controls or a claim about the published packages.

The net-chain coverage gap remains until complete identity and electrical-change
restoration are qualified; this is not proof of full schematic reconstruction.
Its structured result retains incomplete revision tracking and lists
unrepresented state. Native and compiled MCP journeys compare actual page/title
data and nonempty project variables/aliases, verify repeated-sheet screen identity,
and reject wrong views and staged transactions. A populated fixture passes a
real embedded PNG through typed XML into an isolated native schematic, then
checks payload preservation, native legacy-checksum migration, repeated-sheet
metadata and the compiled MCP round trip.
Metadata also distinguishes `loaded_native_format_version` (the version read
from disk; zero means unknown) from `writer_native_format_version` (this build's
save format). Native tests pin the latter to fork version `20260912` and preserve both through
typed XML and compiled MCP. These are provenance fields, not editable settings
or proof of complete serializer compatibility. Item planning rejects a loaded
or writer version above the supported `20260912` ceiling, and a loaded version
above a declared nonzero writer version. The native journey checks that ceiling
against the actual writer. Read-only typed XML still preserves newer provenance
for inspection. Zero-version legacy DTO plans remain inspectable but are not
compatibility proof or live-edit authorization. Full import/restore admission
and complete feature coverage remain unfinished.

The read-only `kicad_schematic_data` tool returns supported objects and metadata
from one native dispatch, with typed XML of that same data. Targets include the
exact sheet-instance path; unsupported state and incomplete revision tracking
remain explicit. Compiled STDIO MCP tests compare the result to native reads
and parse the returned XML back into equivalent data on root and repeated
sheets. Wrong instances, missing or inactive sheet targets, and reads during
an open commit are rejected; reads resume after cancellation of the commit.
This tool does not write files, restore documents or capture the whole hierarchy.

The read-only `kicad_schematic_hierarchy_data` tool captures every loaded sheet
instance in one native dispatch without navigating the editor. The typed XML
retains separate instance paths, shared screen identities, supported objects and
settings, plus every unsupported-state marker. Its result uses a canonical root
target and deterministic path ordering and does not depend on which sheet is
visible. Native/compiled MCP tests compare it with individual sheet reads and
exercise wrong targets, open-transaction rejection and recovery. This is loaded
hierarchy capture, not full-fidelity archive/restore or complete revision tracking.

`kicad_schematic_hierarchy_validate` checks supplied hierarchy XML for missing
child contents, orphan/duplicate instances, mismatched child identities, wrong
parent paths, recursive screens and inconsistent child references between
instances of a shared screen. It preserves the input and reports topology
validity separately from native serializer coverage gaps. It does not resolve
filenames, compare shared electrical/layout content, establish connectivity or
authorize restoration/mutation. Unit tests include valid repeated sheets and
must-catch invalid graphs; compiled MCP tests cover malformed XML/recovery and
real native hierarchy captures.

`kicad_schematic_hierarchy_plan` previews changes between supplied current and
desired hierarchy XML. It returns sheet-targeted operations for one native batch
across loaded sheets and supported new nested sheets, deduplicates identical shared-screen plans, and
rejects conflicting shared edits or inconsistent schematic-wide assets. It also
returns serializer coverage gaps and explicitly does not authorize live edits or
claim complete schematic recreation. Shared-symbol references, units and variant
projections are checked against complete placement records before comparing
physical edits, so moving a symbol preserves differing assembly options.
New nested sheets are created before their contents and supplied page/title
settings are explicitly initialized. Project-wide bus aliases must agree across
every sheet, including new sheets, and their replacement is emitted only once.
Stored root-page
records, unrepresented state and loaded-file provenance are rejected rather than
silently discarded. Subtrees can be detached through their parent reference,
preserving child contents for native undo and leaving files untouched.
Additional instances of existing screens require consistent complete shared
contents and explicit symbol/sheet placement records; references are not invented.
Subtrees can be reparented while retaining physical screen identities; the model
must supply the resulting instance paths and placement records. Changing
project-wide variant descriptions uses an explicit registry operation rather
than an implicit write through a symbol's projected description. Registry
entries must agree across every sheet and are emitted once per project. Changing
a registry name does not rename symbol/sheet variant declarations: those have
their own explicit placement records.

Native project registry entries (including empty descriptions) are retained in
typed XML metadata. Inferred names remain with their symbol/sheet declarations;
refreshing the editor's variant list does not invent saved project entries.
Registry replacement is undoable and participates in atomic rollback, revision
admission and retry receipts. Native dialog Add/Delete/Rename/Copy and description
edits also use commits, including variants with no component overrides.
The Linux rendered journey checks cancellation, invalid names, saving, undo/redo
and XML registry changes combined with symbol placement. Per-symbol description
projections are validated but never sent as a second registry write; independent
registry-description and placement changes merge without a false object conflict.
This is not complete change-stream coverage or automatic bidirectional sync.
The Symbol Fields Table's Rename, Copy and Edit Description buttons also use
these native commits. Their rendered cancellation/no-op, save, stale-request,
Undo/Redo and exact restored-state journeys passed on both Linux project
instances in `t20260911T210335Z-6f8ab2` (all 11 foundation checks, unchanged
`94e2c9fc95535e97cfdebee7686d116965023b94`). The same candidate passed the
focused `native-table-variants` graph in `t20260911T205750Z-b27b85`; it reuses
the actual native fixture and assertions but is not a substitute for the full
foundation or release pass. These table-path fixes are source-only until
packaged, with native Mac/Windows and actual Codex Desktop qualification open.
Compiled STDIO tests cover discovery, target preservation, conflict/malformed-input
rejection and recovery. The underlying planner is exercised by the real Linux
editor journey; this is not Codex Desktop or native Mac qualification.

`kicad_schematic_observe` combines the current-canvas PNG and supported sheet
data in one native dispatch. It reads data, forces the native rendering
checkpoint, then rereads and compares supported content and revisions before
returning. The MCP result includes image content, viewport, object metadata and
typed XML; a detected change rejects the observation.
Capture also compares the complete published pixel-to-design transform and
visible-layer list before and after repainting, alongside sheet, center, scale
and revision. The Linux rendered journey exercises real keyboard zoom-in and
zoom-out in both instances: image/scale changes preserve supported design content,
revision and unsaved state. Zoom follows native preset steps, not an exact inverse
of an arbitrary starting fit scale. Mid-capture layer-change race injection and
independent render contexts remain unqualified.
Compiled MCP journeys
compare native image bytes and data on root/repeated sheets and exercise target
and open-commit rejection with recovery. Tracking and serializer coverage remain
incomplete. This is not yet an independent offscreen view, a full rapid-edit
stress qualification or a revision-safe mutation precondition.
The native edit journey also retains paired PNG/XML observations before a note
move/addition, after the edit, after graphical undo and after redo. It checks
exact current data/revisions, unchanged viewport, changed pixels after editing,
and exact restoration of earlier PNG bytes on undo/redo. These fixtures test
observation consistency, not aesthetic layout. Notes are separated so exact PNG
comparison does not depend on compositing order between overlapping glyphs;
the selected viewport does not show the entire page.

Native atomic batches now accept `replace_embedded_files` for schematic-owned
assets. The native decoder checks payloads and checksums before replacement;
the same commit owns asset and graphical changes, including rollback and one
undo entry. Linux editor journeys restore a real asset from typed XML, exercise
mixed and assets-only undo, redo, invalid payload/type/name rejection, stale
admission, receipt replay and unchanged-state no-ops. A newly named asset also
survives native save and reload. Public MCP mutation readiness, font/worksheet
consumer rendering and full-document reconstruction remain open; this is not
yet a complete asset-driven schematic generation workflow.

Metadata also preserves the native root-page record separately from page size,
title block and placement in a parent sheet. Native `set_root_instance` batches
support explicit presence/page number, no-ops, retry receipts, rollback and
page-settings undo. Linux journeys verify undo/redo, invalid page-number rejection,
XML preservation, absence/restoration and changed values surviving native reload.
Sheet-object XML accepts valid empty-parent-path root records alongside ordinary
placement records. Shared-screen reads and saves resolve one file-level value;
explicit updates cover every placement of that screen. Contradictory copies
produce an error instead of an arbitrary choice, and save preflight leaves the
schematic/project files unchanged. Linux journeys exercise explicit resolution,
undo back into conflict, redo and reload from both displayed instances. Broader
flat-hierarchy qualification and complete document reconstruction remain open.

No revision-safe editing, synchronized rendering, XML reconstruction, automatic
synchronization, routing, document extraction or simulation tools are advertised.
Their absence is unfinished work, not a reduction of scope. The authoritative
DevCoordinator completion ledger retains these outcomes.

## Source ownership

* `src/KiCad.Automation.Model`: engineering meaning, identities, units, XML and layout.
* `src/KiCad.Automation.Native`: protobuf/NNG client and instance registry.
* `src/KiCad.Automation.Mcp`: compiled STDIO executable and typed tool adapters.
* `tests/KiCad.Automation.Tests`: MSTest unit and compiled transport acceptance tests.
* `tools/KiCad.Automation.Validation`: manually invoked Mac build/evidence command; not a service.
* `../api/proto/common/commands/automation_commands.proto`: shared extension messages.
* Native behavior stays in `common`, `kicad`, `eeschema`, and `pcbnew` as appropriate.

There is no custom chat application or application-server client. Each execution
stack is co-located with its engineering repository. Project-specific confirmed
security assumptions are recorded in `../security-assumptions.md`.

## Development checks

### Symbol definition fidelity

Native snapshots distinguish normal, global-power and local-power symbols and
preserve the library's simulation, bill-of-materials, board and position-file
defaults separately from the placed component's explicit attributes. An untyped
legacy update cannot silently convert an existing power symbol to an ordinary
symbol. Linux native checks cover every combination of these four library
defaults, native library save/reload, and removal/restoration of connected global
and local power symbols from typed XML in two real editor instances.

The decoder rejects unknown or malformed symbol children and child/shape types
that the native library writer cannot persist. It constructs the replacement
definition before changing the destination; rejection does not leave a moved
symbol with missing artwork. Native tests cover direct rejection and editor-level
preservation of properties, connectivity and journal sequence. This is scoped
symbol fidelity, not complete library-cache reconstruction or automatic sync.

The definition also carries its own embedded files, separately from document
attachments. Asset names, kinds, compressed payloads and checksums are validated
before replacement. An explicitly empty file collection clears the library's
assets; omitting the collection cannot silently discard existing assets. Native
checks cover library save/reload and two-editor XML restoration, corruption
rejection and preservation of document attachments. Complete embedded-font
consumer coverage and unused library-cache reconstruction remain open.

### Connected symbol placement (preliminary)

`kicad_schematic_move_connected_symbols` sends a finite native drag through the
compiled MCP server. It accepts explicit instance/document and symbol identities,
an exact displacement in nanometers, and document epoch/revision plus a stable
operation ID. Native undo includes affected wires, labels and junctions. The
operation preserves the user's selection and rejects locked targets, stale
revisions and displacements that cannot be represented exactly.

After cancellation or a lost reply, do not submit the edit under a new operation
ID. Inspect the retained receipt or retry identical arguments with the same ID.
The MCP result reports `NotConfirmed` for errors after submission rather than
asserting that the native edit was rolled back. Use an explicit observation after
the result to obtain image and supported state together.

`kicad_schematic_transform_connected_symbols` adds clockwise/counterclockwise
quarter-turns and left-right/up-down reflections about an explicit sheet-space
pivot in nanometers. Left-right reverses X; up-down reverses Y. Both tools operate
on loaded displayed or non-displayed sheets, including all active units of a
shared drawing, without navigating the visible editor. Rejected batches preserve
surviving selection identities as well as design geometry.

These native operations do not consume creations staged earlier in the same
batch. They are not full electrical correctness verification, complete revision
tracking or automatic XML synchronization. XML-driven transforms have controlled
Linux proof, including hidden physical sheets and attached label guidance. The
compiled editor journey covers both tools, all four
transforms, connection preservation, rejection/rollback, retry/stale behavior,
observations and native undo/redo in two Linux editor instances. This is not
Codex Desktop or native-Mac qualification. Run the focused journey with
`devcoordinator2 test start . --test native-offscreen-move --tier development --client codex`.

### Running checks

From the repository root, use:

```sh
devcoordinator2 test start . --test automation --tier development --client codex
```

This runs locked restore, compilation, MSTest, native configuration and compilation
of the changed native translation units and shared protobuf library. It does not
establish a fully linked native application or release readiness. Test
results and stdout/stderr are retained by DevCoordinator; inspect the log catalogue
before retrieving bounded diagnostics. Dependencies are pinned in
`Directory.Packages.props` and package lockfiles.

On the current Linux development host, large working files use the mounted
`/mnt/build-storage` volume. The existing `automation/artifacts/native` build
path links to `/mnt/build-storage/codex/kicad/builds/main-native`; the governed
Linux check groups use `/mnt/build-storage/codex/kicad/t` for `TMPDIR`. Keep the
volume mounted before running those checks. New large worktrees and materialized
evidence copies belong under its `codex/kicad/worktrees` and `codex/kicad/evidence`
directories. This does not relocate Coordinator's private retained evidence or
change the Mac validation workflow. Short IPC socket paths remain separate from
bulk temporary storage.

The compiled transport tests exercise a real MCP subprocess and the installed NNG
library against an isolated echo peer. The peer is a test fixture, not KiCad.
The real transport preserves a 2 MiB binary reply and recovers after cancellation
after the peer has received the request; the subsequent exchange receives its own
reply. Cancellation releases the client connection, not a committed native edit.
This does not establish unlimited message sizes or complete multi-image delivery.
The required actual Codex Desktop image/result round trip remains unverified.
The separate `native-foundation` check
builds the manager, both editor libraries and runtime assets, then tests two
isolated native manager sessions under Xvfb, opens their root schematics and
captures PNGs, including wrong-target rejection and recovery. It also exercises
native editing and a compiled MCP STDIO subprocess; that subprocess is not the
Codex Desktop application.

The native journal check now records completed schematic commits and keyboard
undo/redo, with bounded history and explicit recovery for expired cursors or
replaced documents. The Linux journey moves a text object, drives undo/redo
through its rendered editor window, checks the resulting native positions and
captures the reapplied result. Cancelling a staged native edit is checked for
restoration without a committed-change entry. Connected symbol/wire moves now
compare native connectivity, with keyboard undo/redo, transformed and multi-unit
symbols, hierarchy edits, and atomic rollback checks. Page Settings is exercised
through the actual dialog, including export to repeated sheets, save, cancellation
and preservation of redo. Schematic Setup also compares live serialized project
settings and records accepted changes after native refresh, without counting
cancel or unchanged OK as edits. The rendered Linux journey changes a formatting
ratio, cancels and accepts it, rejects a stale batch without overwriting the user
change, and restores the fixture with a fresh revision. Setup itself does not gain
undo support from this change. Read-only settings capture preserves stores and
dirty flags, follows nested/parent parameter ownership, and compares persisted
values rather than transient preset flags; genuine serialization failures remain
conservative invalidations. Ordinary best-effort saving is unchanged.

ERC override edits now advance the native journal when an exclusion flag,
exclusion comment, or stored severity changes. Cancelling a comment or accepting
the same text leaves the revision and dirty state unchanged. The rendered Linux
journey exercises the real context menu, comment cancellation/edit/no-op,
restoring an exclusion, changing severity, rejecting a stale native batch, and
saving the resulting project settings while another project remains unchanged.
These are existing KiCad controls with improved change tracking, not a new
annotation interface or automatic XML synchronization. ERC settings do not gain
undo support from this change.

The `native-foundation` development graph restores locked managed dependencies
and builds CVPCB and the package-manager schemas needed by a fresh source editor.
It runs source-editor journeys separately from package updater journeys, which
retain their signed-catalogue checks. Frozen Linux checkpoint
`t20260911T124004Z-a428dd` passed all eleven checks at commit
`9e3d57faf5dccee669d0d295b07db69d8b4cbbc5` with unchanged source. This is native
development evidence, not a published package, native-Mac qualification, or full
ERC/settings revision coverage.

The journal still reports
`tracking_complete=false`: other direct settings/property paths,
complete change payloads and exhaustive revision tracking remain open. Native
batches reject stale tracked revisions and support operation-ID retries, but
that does not make their partial revision cursor safe for all persisted edits.
Clients must not use this partial journal cursor as a safe document revision.

`kicad_schematic_create` explicitly initializes an unsaved empty root schematic
for an attached project's missing `.kicad_sch`. An existing file or open document
is preserved, and retrying creation returns that document without replacing it.
Normal `kicad_schematic_open` still requires an existing file. Native project/file
ownership, dirty-session and modal-operation checks remain in force; save is a
separate explicit action. The headless CLI rejects the graphical creation flag.
Creation preserves a concrete identity/name in the project's root declaration;
a nil identity receives a new native ID. Mismatched root filenames and multi-root
creation are rejected without rewriting project declarations. The Linux journey
creates the missing root through compiled MCP, closes that MCP process, retries
from fresh MCP processes and verifies the same unsaved native document remains.
During automation reload, native automatic-repair notices are non-modal warnings
and remain in native diagnostics; repairs stay dirty until saved. Ordinary
interactive notices are unchanged. Creation is a construction primitive, not a
complete model-to-schematic generator or full reconstruction guarantee.

Typed schematic formatting includes operating-point display preferences:
voltage/current significant-digit precision (1–10) and the native automatic or
fixed unit ranges. These are display settings, not simulation results. Complete
formatting replacements require the explicit `operatingPoint` group; an omitted
group is rejected rather than filled from defaults. Native capture, XML planning,
undo/redo and save/reopen preserve these preferences. Changing them refreshes
the native operating-point display. Other project-owned settings and complete
simulation control remain unfinished.

Multi-unit reference formatting also round-trips through an explicit
`unitReference` group: the native separator character and first-unit character
code are preserved, including alphabetic and numeric conventions. Missing groups
and values outside native persisted ranges are rejected before mutation. The
Linux journey verifies two placed units changing from `U1A`/`U1B` to
`U1.1`/`U1.2`, then restoring alphabetic labels through keyboard undo. Native
display-text queries and independently rendered close-ups identify the same
supported revision; obtaining those close-ups leaves the human canvas unchanged.
Separate formatting checks exercise rollback, undo/redo and native save/reopen.
This is formatting fidelity evidence, not complete schematic reconstruction or
native-Mac qualification.

`kicad_events_wait` observes tracked schematic commits, including undo/redo,
through a separate per-instance native notification channel. Call it with an
attached `instanceId`; resume with the returned notification's `eventEpoch` and
`sequence`. `InitialStateRequired` and `RecoveryRequired` require a fresh native
snapshot rather than replaying guessed changes. `Change` contains the exact
document, journal revision, origin/operation attribution and incomplete-tracking
flag. A small head heartbeat detects a lost final notification without loading
designs repeatedly. `Deadline` means the bounded wait expired, not that the
design is certainly unchanged. `Failed` includes structured `errorCode` and
`errorMessage` fields, distinguishing invalid cursors/arguments, native API
rejection and transport failure. Cancellation remains cancellation rather than
being converted into a tool failure. It closes only the observer; it does
not close KiCad or discard unsaved work. Separate subscribers can observe the
same instance independently. This is partial committed-change notification,
not complete native edit coverage or automatic XML synchronization. The stream
does not yet report every document lifecycle/settings path, and snapshots still
carry their existing supported-format and incomplete-tracking limitations.

`HardwareRepositoryXml` reads and writes the root `hardware.xml` composition
model using the embedded `hardware-v1.xsd`: child project/model paths, stable
port identities, source documents and revisions, shared libraries and revisions,
and explicit inter-board interfaces. Descriptions and source-purpose prose are
preserved; ordering is deterministic. Validation rejects unknown XML fields,
unsupported versions, broken ownership references, competing interface bindings
and duplicate writable paths. Paths use portable repository-relative spelling;
this does not resolve symlinks, establish filesystem ownership, launch projects,
or verify electrical compatibility. Empty initial repositories are supported.

The model also includes electrical XML and version-pinned component guidance XML;
neither constitutes the complete schematic reconstruction format. Structural
diagram entities and revision-bound layout requests are model foundations, not
yet native UI instruments or an automatic synchronization service. A layout
candidate preserves established placement outside its explicit affected set,
cannot change locked placements, and cannot change electrical connectivity.
It still needs native validation and visual review before acceptance.

`ReadSchematicScreenData` collects current-sheet settings and supported top-level
objects in one native dispatch. Fields, pins and cells stay inside their owners;
objects are ordered by native identity. Unsupported objects are listed explicitly
with identity, native type and reason. Computed ERC markers are excluded. Its
`SchematicScreenData` record round-trips through typed XML, including coverage
gaps. Native journeys verify root/repeated-sheet reads, deterministic repeated
reads, XML equality, wrong-target rejection and open-commit rejection. This is
not yet a full multi-file design archive or a complete revision-safe observation;
the metadata gaps and incomplete tracking flag remain authoritative.

`SchematicItemDelta.Plan` compares two such records by exact native identity and
returns only changed create/update/delete operations. Reordering unchanged items
produces no edits. It rejects unsupported metadata changes, unresolved screen objects,
duplicate identities and type changes under one identity. Group dependencies
are ordered within a native batch. Sheet-symbol edits require an explicit parent
instance and child-screen identity; new repeated instances may reference a child
already represented in the current screen. New child contents and relinking to a
different child still require document-level dependency handling. It does not execute edits or
establish safe revision admission. The native journey applies XML-derived note
creation/movement in one commit, checks all unrelated supported objects, verifies
graphical undo/redo, applies the reverse deletion/restoration, and checks that
unchanged state produces no further operations. Automatic synchronization,
conflict handling and native guidance-authoring UI remain unfinished.

Bus-alias replacement participates in the same native undoable batch. Empty lists
remove aliases; unchanged lists create no edit. Names and members must be trimmed
and nonempty, and names must be unique because the project writer cannot retain
ambiguous definitions. Native checks cover rejected-batch rollback, retry/no-op
behavior, keyboard undo/redo, project-file persistence after replacement/removal,
and schematic reload. A separate native journey joins sheet-local probe nets through
bus entries and a global bus when an alias is added; rejection and undo leave them
separate, and redo reconnects them. Automatic synchronization remains unfinished.

Project text variables can also be replaced in the native batch. All sheets must
agree on the shared dictionary; the hierarchy planner emits one replacement,
including when new sheets are added. Empty values and Unicode/multiline text are
preserved, and an empty dictionary removes the variables. Native checks cover
expansion, rejected-batch rollback, retries/no-ops, keyboard undo/redo and saved
project contents. These are native project variables, not the complete engineering
instruction/class system. Settings refreshes preserve an explicitly launched
automation IPC endpoint without changing the user's ordinary API preference.

Group snapshots include the native design-block library link as typed data.
The model planner can update existing group metadata when membership is unchanged;
native repeated-sheet tests verify a planned name/link edit and native undo/redo.
Both current and desired group graphs are checked before planning: members must
exist on the same screen, have canonical identities, and cannot be duplicated,
multiply parented, self-referential or cyclic. Valid unchanged groups can coexist
with independent member edits; removing a referenced member without updating its
group is rejected. Native repeated-sheet fixtures verify nested membership,
library-link read/write/restoration and invalid-link rejection. New nonempty groups
are planned after their members, with nested groups before parents. Native batches
resolve members created earlier in the batch and record membership with the new
group in one commit. Tests create nested groups containing both new and existing
members, reject a later failed operation without leaving dangling ownership,
replay retries, undo/redo and recreate after undo. The planner also removes groups
parent-first while preserving members unless their deletion is explicitly requested.
Native removal records surviving member ownership for undo and avoids resurrecting
removed parents during commit finalization. Tests cover failed nested removal,
ungroup undo/redo, saved/reopened groups, and saved group/member removal.
Membership changes between surviving groups detach old memberships before
attaching new ones in the same native transaction. Tests cover reparenting,
rollback, retry, undo/redo and persistence. Native ownership restoration never
points surviving members at disposable undo images. Removing a group releases
live ownership while retaining its original membership for undo, so the same
batch can transfer survivors into an existing or newly created group. Tests cover
rollback, retries, undo/redo, persistence and reversing that transfer to recreate
the original groups. Complete group/hierarchy qualification remains open.
The fork introduced schematic and symbol-library UUID persistence in `20260907` for
symbol-definition graphics, text, and library-owned pins. Schematic format
`20260912` additionally persists explicit chain exclusions and pin/path anchors;
the symbol-library version remains `20260907`. These are local format extensions, not an
upstream upgrade. Older files without those IDs remain readable; older KiCad
readers may reject newly saved files. Group persistence checks compare complete
screen snapshots, including symbol graphics, without dropping identities.
This targeted comparison is not complete schematic feature-coverage proof.
Native group deserialization also rejects unresolved, duplicate, cross-screen,
self-referential and conflicting-owner membership before changing state, instead
of silently omitting unresolved IDs. Real native rejection checks compare the
entire observed screen and revision, not just the response status.

Explicit embedded-file/font collections, root-page records, title blocks and page settings can
produce undoable native batch operations alongside object edits. Missing
records are not interpreted as deletion: removal requires an explicit empty
collection, root-page record or title block. Managed checks cover combined planning, no-ops,
presence rejection and merging an XML root-page change with an independent
native note. A native journey applies the XML-planned root-page operation with
undo/redo and persistence checks. The asset journey now also plans changes from
actual screen data and typed XML: mixed image/note removal and restoration,
undo/redo, unchanged no-op, and a newly named embedded PNG surviving save/reload.
Independent page, root-page, title-block and asset changes merge; embedded files and
their font setting remain one unit so competing edits within that unit preserve
conflicts. Title fields remain one conflict unit. Native tests combine title
replacement and note movement in one undo entry, verify failed mixed-batch
rollback, duplicate retries, stale rejection, no-ops, undo/redo, all title fields
after save/reopen, explicit clearing and rejection of NUL-containing text.
Page operations share geometry validation with the standalone command, load
drawing-sheet assets before mutation, and restore the project drawing-sheet
setting on rollback. Model planning orders embedded-asset replacement before
page changes that may use those assets. Native checks cover custom/standard
pages, malformed/missing/future layouts, combined page/title/note undo and
persistence, and pixel equality between standalone and batch page commands.
Page/title commit and rollback invalidate cached drawing content. Native symbol
round-trip checks also render before replacement and verify pin connectivity:
replaced pin allocations are removed from the connection graph before destruction,
and explicitly dirty connectivity is recalculated even for identical geometry.

`SchematicItemMerge.Plan` performs a three-way comparison against the last
synchronized item state. Independent object edits combine; native-only and
identical edits require no feedback mutation. Competing edits, delete/modify
conflicts and competing creations retain baseline/XML/native versions and return
no partially applicable candidate. Existing IDs cannot change object type.
Notes additionally merge independent wording and placement edits on the same
identity. Wording plus its hyperlink remain one unit; position plus typography
remain another, so competing coordinates or formatting are not guessed. Other
object types retain whole-object conflict granularity. Metadata
conflicts pause with preserved versions; unsupported item deltas are reported.
Two-instance native tests preserve native wording alongside model placement on
the same note, as well as an independent native note through an XML
update and merged XML serialization, reject a competing note without native
changes, and exercise undo/redo and reverse restoration. This planner does not
watch files/events or establish complete revision admission; those integrations
remain open. `Resolve` accepts explicit XML/native/baseline choices for existing
object conflicts, preserves unrelated edits and leaves unselected conflicts
paused. It only plans changes against the supplied snapshot; applying a choice
still requires revalidation of the live revision. Metadata and object-type
conflicts cannot be resolved through this object-choice interface.

`SchematicSyncStore` preserves the last baseline and both competing versions in
a local recovery record. Reopening retains conflict data and session identity;
stale writers, corrupt records and unsupported versions are rejected without
overwriting the saved record. Same-directory replacement and exclusive local
writer ownership protect accepted records from failed writes. This is recovery
data, not the engineering source or completion ledger, and is not a guarantee
against whole-system power loss. Native tests save and reopen a conflicting
record before merging independent edits and checking reverse changes after
undo. Automatic lifecycle integration and actual service-restart recovery are
still unfinished.

Conflict choices are persisted separately from the original three versions.
`SchematicSyncStore.Resolve` requires the current checkpoint token; its choices
are additionally bound to the exact content and native session. `Plan` reuses
those choices after reopening without replacing the conflicting source data.
Changed content or session identity rejects an old choice until it is explicitly
cleared and reconsidered. Acceptance of a new synchronized baseline clears the
old choices. The native journey reopens a saved choice before applying its
result; this does not yet establish live-service restart or automatic admission.

`kicad_schematic_xml_plan` exposes supported item comparison through read-only
MCP. Supply baseline, desired and observed typed screen XML with the same exact
sheet target. Results contain merged XML and proposed operations, or conflicts
with all available versions. Optional object-ID choices select `xml`, `native`
or `baseline` only for those supplied snapshots. The tool never reads or writes
files, persists choices or edits a native document; `liveMutationAuthorized`
is always false. Compiled STDIO tests exercise conflict details, explicit choice,
unchanged input, malformed XML and subsequent recovery. Full hierarchy/settings
changes and automatic synchronization remain unfinished.
Target identity is not mergeable metadata. Changed screen IDs or full document
descriptors (including repeated-sheet instance paths) return `target_changed`,
even for native-only or convergent changes, with no candidate or operations.
Managed tests cover all three input combinations and the compiled MCP boundary.

`SchematicDataXml` adds typed native-object XML and a generated XSD. It preserves
message presence, exact integer coordinates, embedded typed messages and asset
bytes, and rejects unknown or unrepresentable data. Native tests export a placed
symbol, remove it, reconstruct it from XML, and compare its native properties,
identities and connectivity. Variant symbol substitutions and pin-to-pad
overrides are included. Shared symbols now include complete placement records:
exact UUID paths, project names, references, units and local variants, including
other projects sharing a schematic file. Tests reconstruct a shared symbol from
one XML snapshot, compare both displayed instances and the other-project record,
reject malformed replacements without edits, and exercise native undo/redo and
saving. Pin-map delegation is retained as an instruction, not a resolved value.
Shared sheet symbols also carry complete parent-path records, including page
numbers, project ownership and local variants. A nested-sheet fixture reconstructs
one shared sheet symbol under two parent instances, preserves different page
numbers/variants and an other-project record, and checks rejection, undo/redo and
saving. That fixture deliberately retains the referenced child document through
another sheet: it does not establish generation of child documents from XML.
This proves object-level reconstruction, not a complete schematic snapshot:
whole-hierarchy generation, full save/load coverage, project settings, embedded
assets and automatic synchronization still need implementation and qualification.
Native files must not be deleted in a real project on the basis
of these isolated fixture tests.

## Runtime shape

After a validated build, the intended STDIO launch command is `kicad-mcp`.
Diagnostics use stderr exclusively. Set `KICAD_AUTOMATION_STATE_DIRECTORY` to
choose the registry directory. Start requires an explicit matching fork executable
and an existing `.kicad_pro` path. Native endpoints use absolute local IPC paths.
Do not point a production workflow at this development build yet.

## GitHub-hosted Mac and Windows delivery (preliminary)

The `Native delivery` GitHub Actions workflow builds an exact fork commit on
Apple Silicon (`macos-15`), Intel Mac (`macos-15-intel`) and Windows x64
(`windows-2022`). It runs when dispatched, including exact-commit dispatch by the upstream-candidate workflow; it does not build every push or pull request.
One target failing does not cancel the other targets. The public update-signing
key remains on the VPS; no private publisher or engineering repository is sent
to the runners.

```sh
gh workflow run native-delivery.yml --repo holyglory/KAICad \
  --ref main -f source_commit=FULL_40_CHARACTER_FEATURE_COMMIT -f target=all
gh run list --repo holyglory/KAICad --workflow native-delivery.yml
gh run watch RUN_ID --repo holyglory/KAICad --exit-status
gh run download RUN_ID --repo holyglory/KAICad --dir NEW_EVIDENCE_DIRECTORY
```

Use `target=mac`, `target=mac-arm64`, `target=mac-x64` or `target=windows` for a
focused repair run. Individual Mac selections let an unaffected sibling finish
without starting a second build of that target. The workflow must
be registered on the default branch. The command above selects its `main`
definition; the native checkout and
compiled runner still come from the explicit requested source commit.

The compiled `hosted` command refuses other execution platforms and non-hosted
environments, checks source identity and pinned ancestry, prepares dependencies
only in a new output tree, and retains actual step results and dependency versions.
Mac uses the pinned official KiCad Mac Builder and customized wxWidgets, then
the exact-commit validation command below. Windows uses MSVC and KiCad's vcpkg
manifest/registry pins, publishes the self-contained MCP executable, checks PE
architecture and the installed KiCad commit, and opens real native NNG sockets.
The Windows archive places `kicad-mcp.exe` in `bin` beside KiCad, NNG and the
matching Microsoft C++ runtime DLLs. Its installed runtime probe does not use the
native-library override needed by source-tree tests.
Windows dependency preparation uses KiCad's documented public NuGet binary feed
read-only, with the existing manifest/registry pins and local vcpkg cache. An ABI
cache miss builds the pinned dependency from source; cache availability is not
a compatibility or native-execution claim.
Source archives accompany successfully built application archives.

New Windows candidates run in two stages within the same disposable job:
`hosted --phase prepare` restores dependencies, publishes MCP and configures
the native build; GitHub then uploads a separate `windows-preparation-*`
evidence artifact before `hosted --phase build` starts compilation. The second
stage requires the same source, dependency commit, job/attempt, paths and
unchanged prepared inputs/logs. Both stages share the original deadline.
The checkpoint is not a package or a cache that can resume on a different
runner. An interrupted or already-claimed final stage needs a fresh job;
previously uploaded preparation evidence remains available for diagnosis even
if that runner disappears. Failure during preparation itself can still prevent
its upload. Older exact source commits retain the all-in-one path.

Every full Mac build first runs small native loader fixtures. Set
`checks_only=true` with a Mac target to run those fixtures without compiling
KiCad. They check relocated transitive libraries, repeatable search-path repair,
missing dependencies, valid symlink aliases and genuinely conflicting copies.
These fixture runs do not produce a KiCad package or establish editor readiness.
For Windows, `target=windows -f checks_only=true` runs the small compiled delivery
and checkpoint fixtures on the native Windows runner without building KiCad.
It also checks literal native pipe addressing and process-scoped GUI capture,
Ctrl+S, clean close, cancellation, wrong-process rejection and blank-render
detection on an explicitly synthetic native window. These are not a completed
KiCad package or the full preparation/upload/compilation sequence.

New Windows candidates must also run the installed-editor journey before
packaging and public staging. It starts two projects through the packaged MCP
executable with its normal DLL loading, creates native test notes, obtains
matching image/state observations through MCP, disconnects and reattaches MCP
without losing dirty objects, and saves/closes through the native UI. The native
note setup and UI save are not proof of general MCP creation or save/close tools;
those remain open outcomes `p1efcacbeee4e12cc` and `pa11c69fd160444b7`. Compilation
on Linux or passing the small Windows UI fixture is not execution of this
installed-package journey. Evidence includes PNG captures, object identities,
process epochs and explicit qualification flags; it does not claim actual Codex
Desktop operation or working Windows updates.

Signed update metadata recognizes Linux TAR.GZ/DEB, Mac TAR.GZ/ZIP and Windows
x64 ZIP archives with exact target and format selection. That compatibility
does not enable Mac/Windows update installation: their verified staging,
activation, native caption action and recovery journeys remain unfinished.
Existing signed feeds are not rewritten by a metadata-code change.

Artifacts and `receipt.json` are retained even after ordinary failures. A failed
receipt is not a usable delivery; archive hashes alone are not native execution
proof. Failed installed Mac trees may be retained separately under `diagnostics`
for repair; they are explicitly unqualified and must not be published as packages.
Successful candidates still report `QualifyingDelivery=false`: native
Codex Desktop journeys, Mac/Windows automatic updating, Apple notarization,
Windows Authenticode and complete engineering qualification are not established
by this build workflow. GitHub artifacts require GitHub access; verified public
downloads continue to be published separately through `https://kicad.vr.ae`.
Do not replace that site's working Linux preview with an unverified CI artifact.

After retrieving a successful GitHub artifact, stage its verified application and
source archives with the compiled helper. `--candidate` identifies the directory
containing the hosted `receipt.json` and `packages/` (artifact layouts may include
a `kicad-delivery/` prefix). Supply the actual expected run, target and commit:

```sh
dotnet run --project automation/tools/KiCad.Automation.Validation -- stage-hosted \
  --candidate ABSOLUTE_DOWNLOADED_CANDIDATE --commit FULL_COMMIT \
  --platform osx-arm64 --run-id ACTUAL_GITHUB_RUN --version PREVIEW_VERSION \
  --previous EXISTING_PUBLIC_DIRECTORY --output NEW_PUBLIC_DIRECTORY
```

The command verifies the external receipt and archive hashes, preserves existing
declared downloads and update-feed bytes, and copies no private logs or diagnostic
installs. It neither authenticates the originating CI run nor executes the native
application: retrieve from the verified GitHub run first. A staged directory is
not a live delivery. Apply it through the declared DevCoordinator download service
and verify the public HTTPS download paths separately. A failed/cancelled partial
staging directory remains unpublished; the command never clears existing trees.

Existing personal Macs can use the command below without dependency bootstrap.
The hosted bootstrap never runs a clean-slate setup against a personal Mac.

## Manually invoked Mac validation

The validation implementation has run on native GitHub Mac runners, but complete
Mac validation and operator/Codex Desktop journeys remain unqualified. Prepare
matching dependencies with the [official KiCad Mac Builder workflow](https://dev-docs.kicad.org/en/build/macos/index.html) first. Reuse
its generated `toolchain/kicad-mac-builder.cmake`, including its customized
wxWidgets. The command does not run bootstrap scripts, clean dependencies, install
a worker, or change an existing worktree. The .NET SDK, CMake, Ninja and native
Poppler utilities must already be available. The native install must supply a
matching shared NNG dylib inside `KiCad.app/Contents/Frameworks`; a missing,
ambiguous or externally linked library fails explicitly.

Run from a checkout containing this helper, substituting actual absolute paths
and a full commit SHA available from that checkout's `origin`:

```sh
dotnet run --project automation/tools/KiCad.Automation.Validation -- mac \
  --repository /path/to/kicad \
  --commit FULL_40_CHARACTER_COMMIT_SHA \
  --architecture arm64 \
  --builder /path/to/kicad-mac-builder \
  --toolchain /path/to/kicad-mac-builder/toolchain/kicad-mac-builder.cmake \
  --output /path/to/new-validation-directory \
  --native-tests 'YOUR_CTEST_SELECTION'
```

Use `--architecture arm64` for Apple Silicon and `--architecture x64` for Intel
target validation, with separate output directories and matching Mac/.NET
execution architectures. The command rejects a process/target mismatch; merely
cross-compiling does not establish execution of the other target. Existing
builder dependencies must support the selected architecture.

Choose a dedicated output path without spaces or CMake metacharacters. The
pinned native installer embeds that path into generated CMake commands; the
helper rejects unsupported destinations before creating the output or invoking
installation. This restriction is on the Mac validation build destination,
not on engineering-project paths or ordinary application use.

The output directory must not already exist. The helper fetches the exact commit,
creates a detached source worktree there, restores/publishes the matching
self-contained `osx-arm64` or `osx-x64` MCP runtime before the expensive native
build, then builds with the supplied toolchain. It runs the pinned native bundle
install rules into its own `install` directory, verifies the existing native
bundle signature, checks manager/CLI/runtime/NNG architecture slices and native
commit, runs the actual compiled runtime/NNG probe, then selected CTest and
non-Linux-only MSTest checks. It retains the
source/build for inspection. It writes `result.json` and `evidence.tar.gz` on
success or a handled failure. The archive includes a versioned receipt, actual
step results, logs, CMake configuration and hashes of produced evidence. Images
are included only if checks actually produce them under
`KICAD_AUTOMATION_EVIDENCE_DIRECTORY`; the helper does not synthesize screenshots.

The self-contained MCP stays in `<output>/managed`, beside rather than inside
the already signed native app. `<output>/kicad-mcp` launches it with an explicit
binding to the matching bundle's NNG dylib. This does not rewrite or re-sign the
native bundle. The startup-only `KICAD_AUTOMATION_NNG_LIBRARY` override accepts
an existing absolute library path; document fields and update responses cannot
set it. With no override, existing native-loader behavior remains unchanged.

Receipt schema 3 adds managed-host, CoreCLR, host-policy and NNG identities plus
the actual runtime/NNG version probe. Schemas 1 and 2 remain integrity-readable
under their original limits. The runtime probe opens/closes real request and
subscription sockets without contacting an editor; it is not a GUI/MCP journey,
a dependency-closure audit, a redistributable/notarized Mac package or updater
qualification. Linux run `t20260909T120117Z-8fe7a7` verifies the contract logic,
real local NNG binding and failure behavior only. Native-Mac execution and both
architecture-specific distributions remain open.

The command now also runs a native CMake dependency audit before the selected
tests. Native applications/shared libraries/loadable modules and the MCP runtime
use separate executable contexts. Unresolved or conflicting dependencies fail;
non-system dependencies must resolve inside the preserved app or sibling runtime.
No extra search directory is injected to hide a missing runtime path. The generated
script and resolved inventory are retained in `evidence/dependency-audit.cmake`
and `evidence/dependency-audit.json`. Physical containment is checked before paths
are reported under the declared roots, preserving aliases such as Mac `/tmp`.
This uses [CMake's native dependency resolver](https://cmake.org/cmake/help/latest/command/file.html#get-runtime-dependencies).
Linux run `t20260909T124453Z-1a2822` checks generation, path/error contracts and
the refusal to execute this audit on Linux; it is not Mac resolver execution.
Dynamic plug-in loading, redistribution/signing and complete native-Mac journeys
still need their actual platform checks.

Transfer the result and archive through the existing Git/SSH workflow. On Linux
or Mac, the following verifies integrity and commit identity only:

```sh
dotnet run --project automation/tools/KiCad.Automation.Validation -- verify \
  --result /path/to/result.json --archive /path/to/evidence.tar.gz \
  --commit FULL_40_CHARACTER_COMMIT_SHA --architecture arm64
```

Receipt validation is not a Mac execution or an attestation. Even a successful
selected-check receipt has `CrossPlatformReady: false`; full rendered journeys,
Codex Desktop integration and every remaining milestone still need evidence.
Architecture-aware native receipts use schema 2 and identify the selected target
and native executable hashes. Legacy schema-1 receipts remain readable for
integrity checking but cannot satisfy an explicit `--architecture` request.
Native configuration and cache paths are isolated beneath the new output
directory. This does not configure signing, notarization or an unattended worker.
Self-contained distribution packaging remains unfinished.

### Linux installation staging (preliminary)

The governed `linux-package-stage` graph builds the normal native application
targets, publishes the MCP executable with its .NET runtime, and stages both in
a new private directory on the designated bulk volume. It uses `DESTDIR` with
KiCad's existing install rules; it does not install into the host's
`/usr/local` or modify a user installation.

The dependent installed-package journey verifies the staged file hashes, starts
the installed native manager under a test-owned virtual display, and uses the
self-contained MCP binary to create, render, retry, save and reopen a schematic.
MCP EOF must leave the native editor alive. It does not use
`KICAD_RUN_FROM_BUILD_DIR` or set `APPDIR` for this normal prefix layout.

Run from this Linux checkout:

```sh
devcoordinator2 test start /home/holyglory/kicad --test linux-package-stage --tier development --client codex
```

Each candidate retains its `staging.json` inventory; the generated
`automation/artifacts/distribution/current-staging.json` pointer identifies the
latest attempt for the dependent check. Neither is a public release catalogue.
Passing run `t20260908T083609Z-5ce6bd` proved this installed journey on Debian 13
x64. It does not prove independent Linux distributions, dependency closure,
public downloads, updating, Codex Desktop or either Mac architecture.
Those delivery requirements remain open; staging reports
`QualifyingDelivery: false`.

### Debian package preparation (preliminary)

The package-debian command builds a Debian 13 amd64 package from a hash-verified
frozen application catalogue. It uses Debian's shared-library analysis and
explicitly includes .NET dynamic runtime dependencies, Poppler utilities and
KiCad's ngspice library. It does not globally ignore missing-library errors.
Package names and installation prefixes are artifact-specific, preserving older
installations used by live editors; no system-wide KiCad alias is replaced.

The optional .NET LTTng 2.12 tracepoint provider is omitted only from the Debian
copy because Debian 13 uses the incompatible newer LTTng ABI. The receipt records
that exact omission. The original frozen tar archive remains unchanged.

The Debian package was built in governed run t20260908T105916Z-53e9c8. Its
construction receipt predates qualification and is retained unchanged.
Subsequent clean-Debian execution passed in deployment df9549b90e32b3da3 and
evidence check t20260908T141913Z-7e0621: no SDK or development libraries, a non-root
operator, and real packaged MCP create/render/reconnect, dirty-session survival
and native save. The verified render/evidence tree is
d2edcced4f3c692d03d8b4f92b6269c93219b079487d560f9b059e7a52dc1959.

Coordinator's explicit finite-container lifecycle now owns this check without
weakening NoNewPrivileges or adding a dummy service. The disposable qualification
deployment was removed after evidence retention. This proves the stated Debian
journey only: public HTTPS downloads, automatic updates, both native-Mac targets,
simulation and the complete engineering workflow remain unqualified.

### Authenticated update preparation (internal, preliminary)

The compiled distribution module verifies signed release metadata with the
publisher public key supplied by the installed application. It selects an exact
platform/package pair, downloads over HTTPS with signed byte-count and SHA-256
checks, and retains authenticated metadata across restarts. Repeated unchanged
checks do not rewrite the saved metadata. Invalid metadata, concurrent checks,
cancellation and persistence failures do not authorize installation.

The first installed-version envelope is an installer receipt, written after
the package has been verified. It is not the latest online feed or a manifest
embedded inside the archive whose own final hash it describes. Packaging and
first-install receipt provisioning still need integration.

The update feed (`updates/preview.json` or `updates/stable.json`) is separate
from the unsigned read-only public download catalogue. Payloads are fetched
only from its publisher's `artifacts/` path. There is no production signing key,
enabled production update feed or qualified native Update journey yet. Later
sections describe the implemented installation, restart and rollback increments.
A `download_verified` receipt
explicitly has `installationReady: false`; it does not prove native signing,
Mac notarization or a working automatic updater.

Linux tar-archive staging now verifies the downloaded bytes again, rejects
escaping/duplicate paths and special files, bounds expansion, preserves
ordinary executable permissions and internal library-file links, and checks
package identity before returning a new isolated directory. It never switches
the active installation. Unsupported archive layouts fail instead of being
partially installed; Mac archive preparation remains separate work.

Run focused signature, HTTPS transfer and persistence checks with:

```sh
devcoordinator2 test start /home/holyglory/kicad --test update-contracts --tier development --client codex
```

The focused contracts use synthetic release payloads and ephemeral test keys.
The `update-archive` graph additionally exercises the real frozen a5666e707777
Linux package through a test-only signed HTTPS feed, staged extraction and
rendered native/MCP create/render/reconnect/save. It passed in governed run
`t20260908T152803Z-450fb4`; retained native evidence tree:
`1a159879861fef8f91bae2e732436d9b8e5d9c47e93b99e9ea5eebe8e1fb38d5`.
This is not a public feed, installation activation, qualifying desktop delivery
or native-Mac execution.

The same `kicad-mcp` executable now has finite native-helper modes:

```sh
kicad-mcp --check-update --configuration /absolute/path/to/installed-updater.json
kicad-mcp --prepare-update --configuration /absolute/path/to/installed-updater.json
```

The trusted installed configuration names the HTTPS publisher, pinned public
key, installed signed receipt, exact channel/platform/package format, and
existing dedicated state and staging directories. It is not read from an
engineering repository. The helper emits JSON progress and a terminal result,
has a 15-minute ceiling, and accepts cancellation; normal invocation remains
the STDIO MCP server. Check-only mode fetches metadata but never the package.
Linux tar candidates can reach `archive_staged`, never
`installationReady: true`. Unsupported preparation targets are explicit.

Governed run `t20260908T154945Z-79bd1a` exercised the actual helper subprocess:
check without an artifact request, cancel during a real HTTPS transfer,
verify no partial/verified payload remained,
retry with the retained signed checkpoint, stage the frozen package, then
complete the native rendered journey. The disposable child used an ephemeral
test CA file without changing host trust or disabling production TLS checks.
Native startup/hourly wiring and the caption implementation are described below;
they are not present in the frozen public preview package.

Before returning a prepared Linux candidate, the helper also runs its native
CLI with isolated configuration/cache paths and checks the compiled commit
against the signed release. Startup failure, wrong commit, excessive output and
cancellation cannot produce a native-identity success. This check does not
replace rendering or full release qualification. Governed run
`t20260908T160325Z-3d07a9` covered those failures and the real package journey.

The low-level Linux activation module can switch a dedicated installation's
current-version link and switch back without replacing version files. Each
activation has a distinct pointer identity, including rollback, so stale
retries cannot silently reapply an earlier update. Contract tests cover open
file preservation, cancellation at the transition boundary, Unicode paths,
competing operations and conflicting pointer contents. Launchers generated for
future packages resolve physical version paths for libraries and resources.
The already published a5666e707777 artifacts remain unchanged.

This is an internal filesystem primitive, not an enabled installer: it does
not authenticate arbitrary version directories, handle native editor shutdown,
restart the application, qualify power-loss recovery, provision the first
installed receipt or implement Mac activation. The higher-level handoff and
caption wiring below are separate; the complete automatic-update workflow remains open.

### Verified initial Linux installation (preliminary)

The compiled bootstrap mode is available from the updated source build:

```sh
kicad-mcp --install-package --configuration /absolute/install-request.json \
  --publisher-key /absolute/trusted/publisher.spki
```

The request contains `schemaVersion: 1`, `installationRoot`, `archivePath`,
`envelopePath`, `origin` and `channel`. All three paths must be absolute; the
installation parent must exist, and the new installation root must not exist.
The publisher key is a DER SubjectPublicKeyInfo public key provided separately
by the operator's trusted bootstrap. The request cannot supply its own key.
The unsigned public download catalogue is not an installation envelope.

The installer authenticates the envelope and archive, extracts into new sibling
staging, verifies the native compiled commit, writes the installed signed
receipt and version-specific updater configuration, and creates the relative
manager/current selection. Only then does it publish the new installation root.
Cancellation or a conflicting destination before publication leaves existing
work untouched. No system package, desktop registration or running editor is
changed. Installation paths support spaces and Unicode.

Governed run `t20260908T173618Z-2e1fe7` used the real frozen Linux archive with an
ephemeral test publisher to exercise rejected inputs, cancellation at the
publication boundary, a competing destination, the actual bootstrap subprocess,
and native/MCP create/render/reconnect/save from the installed selection. It is
not native-Mac evidence, a production signing setup, power-loss qualification,
or the complete automatic updater. The public a5666e707777 preview predates
this bootstrap command; do not assume its embedded MCP executable provides it.

### Installation-bound update candidates (preliminary)

New bootstrap installations keep their original publisher policy at the root
and record a fingerprint for each verified version. A provisioned update
configuration includes `installationRoot`; `--prepare-update` then verifies and
registers a candidate beneath that installation instead of returning loose
staging. Its terminal status is `candidate_registered`, with the expected
current-selection identity and `installationReady: false`.

Registration inherits the original publisher/channel and rejects stale
selections, wrong publishers, older accepted metadata, changed configuration
and changed payload files. Repeating an unchanged candidate reuses it without
rewriting its files or selecting it. Ordinary timestamp changes alone do not
change the payload fingerprint. These receipts detect accidental/local drift;
they are not attestations against an operator who can rewrite all local trust
and receipt files. Roots predating the publisher policy/version-registration
metadata are not silently adopted by this path.

Governed run `t20260908T180714Z-ced0e8` exercised real bootstrap, helper-process
registration and unchanged-candidate reuse alongside the rendered installed
KiCad/MCP journey. The registration fixture deliberately uses a new signed
metadata sequence for the same real frozen application bytes; it does not prove
an application-version upgrade. Native close/restart, verified activation,
power-loss recovery, production signing and both Mac targets remain required.

Verified Linux activation now rechecks registered version bytes, configuration,
publisher identity and accepted metadata before changing the current selection.
Rollback is a separate operation bound to one update receipt and its exact
retained predecessor. It re-verifies that predecessor, rejects superseded
requests, and does not lower the accepted-update checkpoint. A damaged selected
candidate does not prevent recovery when its predecessor remains verified.

Governed run `t20260908T182001Z-81dcc7` covered rejected changed candidates,
stale/arbitrary targets, cancellation, metadata replay, activation retry,
receipt-bound rollback and retry, checkpoint preservation, and real native/MCP
rendering after restoration. It still uses different signed metadata sequences
for the same frozen application bytes. The selection APIs are not yet exposed
as a qualified desktop update journey; the native handoff and caption increments
below still require matching-build qualification and power-loss verification.

### Native updater lifecycle (preliminary)

The native project manager can own the compiled helper when its installed
launcher supplies absolute `KICAD_AUTOMATION_UPDATE_HELPER` and
`KICAD_AUTOMATION_UPDATE_CONFIG` paths. This context is separate from engineering
project fields. It checks on startup and schedules checks hourly, starts
preparation for an available candidate, and keeps an unchanged registered
candidate without preparing it again. Ordinary nonconfigured KiCad update
behavior is preserved. Helper output is bounded, parsed and checked; a timeout,
invalid response or exit failure never produces a ready-to-install state.

The native client owns a separate helper process group, supports cancellation,
and releases it on owner destruction. A still-running cancelled helper receives
a bounded termination fallback while the native event loop remains available.
The caption control described below is under qualification, not a release-ready
desktop updater.
The published a5666e707777 packages do not include this integration or set these
launcher variables. Automatic checking in a new packaged installation still
requires that launcher wiring and qualification.

Native Boost/CTest cases exercise the GUI event loop under a virtual display,
accelerated scheduling, repeated checks, malformed/contradictory responses,
failure recovery, cancellation and owner destruction, including a synthetic
helper that ignores the first cancellation signal. The rendered journey uses
the real manager and self-contained .NET helper against a test-only HTTPS
publisher: an up-to-date release, invalid configuration, and normal rendered
manager close during a pending check. It verifies HTTP cancellation and exit of
the exact owned helper process. Governed run `t20260908T190558Z-766a64` passed
these checks; this is Linux evidence, not a completed different-build update,
dirty-editor restart, production feed or native-Mac execution.

```sh
devcoordinator2 test start /home/holyglory/kicad --test native-updater-journey --tier development --client codex
```

### Native restart handoff (Linux, preliminary)

The compiled service includes a finite handoff command for the future native
Update action:

```sh
kicad-mcp --restart-update --configuration /absolute/restart-request.json
```

Its schema-1 request names the managed installation, expected selection,
registered manifest digest, operation ID, exact old process identity, project,
instance ID and a new short local socket. Old process identity uses PID, boot
ID and the kernel start counter, not a wall-clock timestamp. Candidate bytes
are checked before the command acknowledges that it is waiting for exit, and
again at activation. It never signals or closes the old editor process.

The acknowledgement identifies a durable handoff journal. Once acknowledged,
the caller's response pipe may disappear; final status, process identity,
endpoint, epoch and startup diagnostics are retained in that journal. After
the old process exits, the command selects the verified candidate, launches it
and checks the exact native instance/project handshake. A definite startup
failure can restore and launch the receipt-bound verified predecessor. A
still-live process with uncertain readiness is preserved for reconciliation,
not duplicated or killed. Reusing an existing handoff operation requires
inspection rather than silently spawning another editor.

Governed run `t20260908T193826Z-c712ac` exercised changed-candidate rejection
before acknowledgement, wrong kernel process identity, a real dirty schematic's
close/cancel/save flow, the standalone command after its response pipe closed,
native epoch change and reattachment, and verified rollback/relaunch after a
real native startup rejection. Handoff journals and before/after screenshots
are retained with the evidence. The failure injection is test-only and not a
production launch override.

This still uses signed metadata revisions of the same frozen application
archive; it is not a different-build upgrade. Caption cancel/retry wiring is
under qualification as described below. Full restart-state recovery,
power-loss qualification, production signing, new matching packages and both
native-Mac targets remain open. The public preview packages are unchanged and
predate this command.

### Native caption Update control (Linux preview qualification)

For explicitly configured Linux/GTK installations, the manager can show a
compact `Update` action in its toolkit-native caption when a candidate is
registered and a project is open. Clicking it creates an exact process/selection
handoff request; the manager closes only after the helper acknowledges its
preflight. Vetoing the normal save/close prompt cancels the owned handoff. A
failed request leaves the action retryable after the helper terminates. A
successful close detaches the handoff so it can continue after the old window
exits. Unconfigured installations retain their normal window/update behavior.

The caption follows window-title changes through a coalesced GTK event-loop
update. Direct property binding re-entered a GTK object lock; the isolated
native regression catches that startup/title-change hang. The caption fixture
also invokes real mouse input and checks enabled, disabled and hidden states.
Native client cases cover the generated restart request, cancellation and retry.
These cases passed in `t20260908T203257Z-cad8f4` and the manager/lifecycle
regression passed in `t20260908T203731Z-70c35a`.

The matching source `38b65981e64b6a9ed144573ee73ec6a37fb68b20` was built and
packaged in `t20260908T204920Z-1b274e`. The real installed caption-button journey
passed in `t20260908T210559Z-8ac79a`: click Update, reject a changed candidate
without closing the editor, dismiss the error and retry, cancel the native save
prompt, save the schematic, click Update again, and reconnect to the replacement
with the same instance ID and a new epoch. Saved schematic bytes were unchanged
after reopening. Retained evidence tree:
`d13693092cb8beac2eeceb886baf448686cd4dc070240db263d74dc3bb263805`.

This test uses a new signed metadata sequence for the same matching build and
an ephemeral test publisher. The matching archive is now published beside the
older preview; this is not a production signing/feed or different-build upgrade proof.
No Mac caption implementation is qualified, and updating without an open
project is not supported by this control. Different-build and concurrent-instance
update journeys, production signing and remaining platform checks stay open.

### Older instances and managed launchers (source increment)

The current source additionally separates a live process's verified version
from the installation selection used by future launches. Passing regression
`t20260908T214657Z-63c393` kept two real editors alive: the first updated, and
the older second instance retained its epoch and dirty state until its own
subsequent restart. Changed retained-version files and unrelated executable
paths remain rejected. A caption click now refreshes the selection token before
building its request; native request/cancel/retry tests passed in
`t20260908T215423Z-758559`.

Launchers generated from this source discover the updater configuration only
in the managed `versions/<digest>/payload` installation layout with its
publisher/version/envelope/configuration markers. Explicit operator settings
are not replaced, and the general MCP launcher does not inherit automatic
native-update context. Layout, override and ordinary-folder guards passed in
`t20260908T220009Z-cefdce`. Matching source
`8e6938a6006c7ce98776228845ea2570ad8eda38` was built and packaged in
`t20260908T220532Z-57054c`.

Installed caption qualification passed in `t20260908T221800Z-64a8d3`, with
separately retained cases for the real 38b65981e64b → 8e6938a6006c upgrade and a
new managed installation requiring no manually supplied updater variables. In
the latter case, two projects remained live: the first updated, then the older
second instance updated through its own caption while the first replacement
remained open. Instance identities and saved schematic bytes were preserved;
both replacements received new process epochs. Evidence tree:
`77fb1dfeee2dd5da730b289d167938ae5b4b8603057f2e91fb54e8d126793db4`.

That run used an ephemeral test publisher. Current public-channel and
empty-manager evidence appears below. Broader recovery/termination and power-loss
coverage, native-Mac execution and the full engineering workflow remain open.
The preserved 38b65981e64b preview does not contain these later
repairs; older running instances of that preview may require manual restart
after another instance changes the shared installation selection.

### Initial Linux download publication (historical)

The download-only catalogue is at [kicad.vr.ae](https://kicad.vr.ae/downloads.json).
It serves the frozen 8c88184d3333 Debian 13 x64 application archive and matching
source, alongside the preserved 095882e06694, 7a8d9ea241ee, 2ed9d688883f, 3492c24706a1, 6a93cb3fd254,
6a9de7fdd073, 8e6938a6006c, 38b65981e64b and a5666e707777
archives and older Debian installer. Public GET/hash, HEAD, range, private/control
path rejection and upload rejection for all twenty-one files passed in
`t20260909T112737Z-903032`.
No engineering repository, native control, credentials or private evidence is
served there. That older verification covered Linux only; current Mac downloads
and their evidence are listed at the top of this document.

Extract the newer Linux archive and run `./kicad-codex` for the native application
or `./kicad-mcp` for STDIO tools. Its caption-update flow requires a verified
managed installation with an explicitly trusted publisher; that installation
now discovers its update configuration automatically. The
[preview update feed](https://kicad.vr.ae/updates/preview.json) is signed with the
authorized persistent publisher. The Debian installer below is the older preview,
not an installer for the new caption-enabled archive.

On Debian 13, after downloading the `.deb` to the current directory:

```sh
sudo apt install ./kicad-codex-preview-20260908T093000Z-a5666e707777-debian13-amd64.deb
/opt/kicad-codex/preview-20260908T093000Z-a5666e707777/kicad-codex
```

The matching STDIO service is
`/opt/kicad-codex/preview-20260908T093000Z-a5666e707777/kicad-mcp`.
The package preserves other KiCad installations. These downloads are preliminary:
automatic updating, both native-Mac targets and the full engineering workflow
remain unfinished. Public availability is not a qualifying-delivery reset.

### Persistent publisher and public-channel Linux update

Preview `preview-20260909T034825Z-8c88184d3333` is available as an
[application archive](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260909T034825Z-8c88184d3333-debian13-x64.tar.gz)
and [matching source](https://kicad.vr.ae/artifacts/kicad-codex-preview-20260909T034825Z-8c88184d3333-source.tar.gz).
The application archive SHA-256 is
`8d5dc9823b20ed4f0ee10555530a158377f0fadda7be5280b40b43ee1a0132d8`.

Obtain `automation/distribution/preview-publisher.spki` from the trusted fork
checkout (present from commit `6a9de7fdd0731378712f9cdbfda3c322f93cd89a`).
Its SHA-256 is
`6b9f8e9dd462321076c3da20c4a71dbad7bfe745ab475983d952f1e99be0aafb`.
The download server neither supplies a replacement trust identity nor possesses
the private signing file in its download directory or configuration.

For a managed installation, save the signed feed beside the matching downloaded
archive. Use the compiled `--install-package` command described above, with
`origin` set to `https://kicad.vr.ae/`, `channel` set to `preview`, an absolute
`envelopePath` to that saved feed, an absolute `archivePath` to its exact archive,
and a new absolute `installationRoot`. Pass the trusted public key separately.
A feed naming a newer archive will correctly reject an older downloaded archive;
fetch the matching pair again instead of substituting unsigned metadata.

Launch `<installationRoot>/manager/current/kicad-codex`; the managed launcher
selects the installed updater configuration. When a newer compatible update has
downloaded and verified, the manager's caption offers **Update**, including when
no project is open. Builds before 6a93cb3fd254 require an open project for this
button to appear. Choosing it
uses KiCad's normal save/cancel behavior before restart. Do not assume the unpacked
archive or older Debian package has already been bootstrapped this way.

Run `t20260908T231824Z-f479c8`, acceptance source
`911b15635ae4838b255fe15b67e15ab79b7e98d1`, proved an actual 8e6938a6006c-to-6a9de7fdd073
upgrade through the public signed HTTPS channel. The native helper downloaded and
registered the candidate itself. Real caption clicks rejected changed candidate
bytes before closing, cancelled a dirty save prompt without losing work, then
saved and restarted with preserved schematic bytes and a new process epoch.
A second live project updated independently while the first replacement remained
open. The same pass retained the isolated-publisher regression journeys.

The subsequent `t20260908T234525Z-8622f6` pass, acceptance source
`37951f5c84eff258d8b698b9cba7f068763e2780`, proved the public signed upgrade from
6a9de7fdd073 to 6a93cb3fd254 with the same two-project save/cancel and isolation
checks, plus all eleven public downloads and the installed updater helper.
It also verified the new build's empty-manager caption: candidate drift is
rejected before close, then a valid update restarts without opening a project.
That empty-manager case uses the same frozen build with two fixture-signed
metadata revisions; it is not a different-build public no-project upgrade.
Separate startup checks reject invalid empty-manager arguments, preserve the
explicit-project automation requirement and reject implicit schematic creation.

Build 3492c24706a1 additionally restores the exact previous verified editor when
activation fails after the old editor has closed. It does not change a selection
made by another update or lower the accepted metadata checkpoint. Packaged-helper
run `t20260909T002935Z-e4bcf1` exercised candidate drift and a competing selection
change after preflight, real save/cancel/close, recovery of the previous native
executable, saved schematic bytes and retained failure receipts. These are
controlled faults with fixture-signed versions of the same real package, not
power-loss or native-Mac proof. Public run `t20260909T003832Z-901940` verified the
6a93cb3fd254-to-3492c24706a1 upgrade and empty-manager regression paths.
Additional run `t20260909T004505Z-b19019` activated the rendered Save button
inside the update's own save prompt, rather than pre-saving separately. It
verified creation of the previously unsaved schematic, successful restart,
saved-byte preservation and independent second-project updating. The original
cancel and candidate-drift rejection checks remain in that journey.

Build 2ed9d688883f extends recovery to failed replacement startup when another
project has already selected the candidate: it restores the closed editor's
actual verified version, independently of shared-selection rollback. A replacement
that exits before process identity can be captured now enters startup recovery
instead of aborting with a process-inspection exception. A still-live process
with uncertain identity is left for reconciliation, not duplicated.

Run `t20260909T013556Z-4ace69` passed four real-editor recovery cases with the
frozen package. Candidate drift and a concurrent selection change use its bundled
helper. The older-editor startup-failure and early-exit cases use source-helper
test hooks to trigger actual KiCad startup rejection and delay identity observation;
they are not packaged-helper fault-injection proof. Saved schematic bytes,
restored executable identity and preserved selection are checked after recovery.
Public run `t20260909T014326Z-8d4e0a` then verified the real signed
3492c24706a1-to-2ed9d688883f upgrade, direct Save-prompt action, two-project
isolation and fixture-signed empty-manager restart. The live-identity-error branch
and crash/power-loss recovery remain outside these specific proofs.

The retained early-exit evidence also exposed a native cleanup crash: rejecting
arguments before worker initialization dereferenced a null pool. Earlier checks
that accepted any nonzero exit did not catch this. Build 7a8d9ea241ee guards
partial/repeated cleanup in the manager and matching standalone-editor path.
Run `t20260909T015422Z-700b72` requires normal failure exits and actionable
diagnostics for six invalid starts, plus successful empty-manager startup/close.
The four recovery cases passed again against this package in
`t20260909T015852Z-ce8974`; the early-exit case now records exit 255, not the
earlier segmentation-fault exit 139. Public `t20260909T020550Z-17af01` verifies
the signed 2ed9d688883f-to-7a8d9ea241ee upgrade, Save-prompt action, two-project
isolation, empty-manager restart and all seventeen downloads. Older packages
remain available but do not acquire the newer cleanup fix.

These prove the stated Linux journeys, not all update recovery cases or either
native-Mac target. Broader restart/failure/power-loss recovery, Mac packaging
and the complete engineering outcome remain unfinished.

### Interrupted-update inspection (Linux preview)

Find out whether the original editor survived an updater interruption or a
replacement is already running without starting another editor:

```sh
kicad-mcp --inspect-update --installation /absolute/managed-installation \
  --operation 00000000-0000-4000-8000-000000000001
```

Use the real operation UUID from that installation's handoff, not the example.
New handoffs persist the exact previous verified version before acknowledging
the old editor's close. Inspection locks an existing journal for reading,
validates its bounded typed records, rechecks the previous signed installation,
and matches kernel process identity, executable and native session where
available. It never changes journal bytes, installation selection or editors.
An unavailable lock does not prove an updater is alive. Missing identity,
unreachable endpoints and legacy journals without the new intent record remain
explicitly unverified; inspection does not guess or authorize a restart.

Source check `t20260909T023746Z-4ee69d` exercises the compiled inspection command
after killing only a test-owned updater while its original editor remains dirty,
and after a normal replacement starts and then closes. It verifies stale kernel
identity and wrong native-instance rejection without closing the live editor,
unchanged journal hashes/write times, and absolute paths with a trailing separator.
The fixture uses source-built helpers with a real frozen Linux KiCad package.
This is observation evidence, not automatic interruption/power-loss recovery or
native-Mac qualification.

Packaged check `t20260909T025037Z-e8e8ec` repeats these journeys using the actual
helpers shipped in build 095882e06694, including the inspection subprocess.
Public `t20260909T030018Z-03c732` verifies its signed downloads and the normal
7a8d9ea241ee-to-095882e06694 Save-prompt upgrade, two-project isolation and
empty-manager restart. The new command does not resume an interrupted update;
`automaticRecoveryAvailable` remains false. Older handoffs without `intent.json`
report `legacy_journal_unverifiable`, including an upgrade supervised by an older
helper. They do not gain missing recovery facts retroactively.

### Explicit recovery before replacement launch (Linux preview)

If an updater stopped, the original editor has subsequently closed, and the
journal proves no replacement launch was reached, reopen its exact verified
previous version without changing the shared installation selection:

```sh
kicad-mcp --recover-update --installation /absolute/managed-installation \
  --operation 00000000-0000-4000-8000-000000000001 \
  --attempt 00000000-0000-4000-8000-000000000002
```

Use the real interrupted operation ID and a new recovery attempt ID. Repeating
an attempt returns its recorded outcome and does not launch again. A fresh
explicit attempt can follow a cancellation before launch; immutable attempt
records are retained. A live original, a recorded successful recovery, cancelled
update, ambiguous launch history, or missing context is not restarted.

New schema-2 intents capture only reviewed display/profile environment keys,
not the full environment, credentials, PATH or version-specific executable
overrides. Schema-1 intents remain inspectable but are insufficient for this
recovery command. Ordinary updates and recovery share the same verified native
launcher, with instance/project checks and early-exit handling.

Source run `t20260909T034132Z-625e26` kills only the test-owned updater, verifies
the dirty original remains usable, saves/closes it through KiCad, and explicitly
recovers it using the recorded display/profile context. It checks saved bytes,
unchanged shared selection, attempt replay, cancellation before launch followed
by a new attempt, and rejection of cancelled/ambiguous/legacy histories. The
cancellation boundary uses a source test hook; this is not packaged-signal,
automatic background resumption or power-loss proof. Recovery must not be
inferred from a missing final response: use inspection to observe the editor.

Packaged matrix `t20260909T035041Z-90a85a` verifies this flow with the bundled
8c88184d3333 update, inspection and recovery helpers, and reruns the existing
activation/startup/early-exit recovery cases. Cancellation at the exact pre-launch
boundary and the startup-failure injection still use source hooks and are labelled
as such. Public `t20260909T112737Z-903032` verifies the signed
095882e06694-to-8c88184d3333 upgrade, direct Save prompt, two-project isolation,
empty-manager restart and all twenty-one downloads. This does not establish
automatic resumption of ambiguous launches, recovery of missing legacy context,
power-loss durability or native-Mac qualification.

### Source PDF inspection (preliminary)

The `kicad_source_pdf_page` tool reads a PDF declared under `documents` in the
engineering repository's `hardware.xml`. Supply the absolute manifest path,
the document's exact ID, and a one-based page number. The default image's
longest side is 1800 pixels; `maximumImageDimension` can request 1–8192 pixels.

The result contains page text and a PNG, plus the declared source revision,
document ID, page number and SHA-256 of the exact PDF bytes used by both native
tools. Supply that hash as `expectedSha256` on later requests when changes to
the source must be rejected. The declared revision comes from the manifest;
it is not a verified publisher revision.

This requires the local `pdftotext` and `pdftoppm` Poppler executables on PATH.
It uses no Python or JavaScript runtime and does not open a KiCad editor.
Native PDF permission failures are returned, not bypassed. Each call owns its
temporary source snapshot and renderer outputs; original documents are never
rewritten. Cancellation terminates only that call's native subprocesses.

When a page has no extractable text, its image is still returned for visual
inspection. The reader does not perform OCR or infer missing values. It does
not classify operating limits versus absolute maxima, interpret tables,
reconcile contradictions, create library components, or validate pin-to-pad
maps. Those engineering outcomes remain separate unfinished work.

Run the focused native-document check through DevCoordinator:

```sh
devcoordinator2 test start /absolute/path/to/kicad --test automation --check restore --check build --check source-documents --tier development --client codex
```

Compiled handler and STDIO evidence is not evidence of operation in Codex
Desktop or on macOS. Those acceptance checks remain required separately.

### Project PCB opening (development branch)

With a matching native build, `kicad_pcb_open` opens an existing project-root
`.kicad_pcb` in the same instance as its schematic. `kicad_pcb_create` explicitly
creates an unsaved empty board when that file is absent. Both require an attached
`instanceId` and an absolute project-owned `path`; repeating the operation keeps
the existing board. Closing the MCP connection does not close the editor.

Locked, malformed, future-format and recovery-needed files return errors without
hidden dialogs or replacement of the schematic. These tools do not place or route
components. Guarded MCP save and clean-only close are described below. General
revision-safe PCB mutations and full lifecycle qualification remain unfinished.

The focused Linux journey opens two independent native projects, creates and
reopens their boards over real MCP STDIO, and checks shared net-class updates,
native undo/redo, geometry and lock preservation, persistence and recovery. Run:

```sh
devcoordinator2 test start /absolute/worktree --test native-net-settings --tier development
```

This is scoped Linux integration evidence, not full XML reconstruction,
automatic repository synchronization, Codex Desktop or native-Mac qualification.

### Guarded document lifecycle (development branch)

With the matching native build, call `kicad_document_state` for the explicit
instance/document, then pass its complete JSON result as `expectedStateJson` to
`kicad_document_save`, together with `instanceId` and a new UUID `operationId`.
The native process checks the observed document identity, revision, full model
fingerprint and loaded file versions. The actual writers check file versions
again before replacement. Unknown or changed baselines refuse admission.

After a timeout, reuse the exact request and operation ID or query
`kicad_document_operation` with the original document descriptor, operation ID
and process epoch. A retained receipt is replayed without another save, including
after an MCP reconnect. Failed and indeterminate results require inspection;
do not invent a new operation ID merely to bypass an uncertain result.

Linux native run `t20260913T010338Z-c02c70` passed the compiled contracts and real
two-instance MCP/editor saves, stale rejection, retry no-write assertions and
receipt lookup after reconnect. This evidence covers its frozen candidate, not
the public packages or Mac/Desktop execution. The guard does not provide an
atomic multi-file transaction or protection against arbitrary uncooperative
writers. General change tracking remains explicitly incomplete.

`kicad_document_close` takes `instanceId`, the complete `expectedStateJson` and a
new `operationId`. It requires state matching a native loaded/saved checkpoint
and refuses dirty or stale state, pending dialogs and associated editors/viewers.
It does not implicitly save, discard changes, force closure or stop the native
process. Query its receipt with `kicad_document_operation` after closure or an MCP
reconnect; retries must reuse the identical request and operation ID.

Linux run `t20260913T024019Z-0f674e` proved saved and read-only close/reopen for
both schematic and PCB documents, unchanged design bytes and timestamps, receipt
recovery after reconnect, and preservation of the other editor. That proof is
bound to its exact candidate; it does not qualify the public packages, later
source changes or Mac/Desktop execution. Field-specific editor-state coverage,
Save As, atomic multi-file persistence and complete lifecycle/platform acceptance
remain open.

### PCB DRC marker inventory (development branch)

With the matching native build, `kicad_pcb_drc_state` takes `instanceId` and
`documentJson` identifying the PCB. It returns the existing native violations,
their marker identities and geometry, exclusion flags and comments, together
with the document revision and process epoch. It does not start a DRC run.

While the native checker is running, the result has `running: true` and no
marker snapshot. Otherwise `markerSnapshotComplete` identifies a complete
inventory of the markers currently present. `resultsFreshnessKnown` remains
false: an empty inventory is not proof that the current board passed DRC.
Markers can predate a design edit or have been manually deleted. Re-run KiCad's
checker when current verification is needed; native DRC job control and
result-to-design revision qualification remain unfinished.

Linux run `t20260913T041710Z-0cee9d` passed all seven development stages on
`222d65281389c66d3f1077c650fa27208383dc52`, including eleven native regression
groups and both project instances. The actual DRC dialog journey proved comment
cancellation, exclusion/save/reload/rerun/removal, wrong-target rejection and
STDIO MCP inventory equality. Board-level exclusions use an explicit owning-board
reference so they survive the native board UUID changing on reload; unrelated
unresolved object IDs are not guessed or rebound.

The same run proved that an open native 3D viewer blocks checked PCB close,
preserves both windows and files, and permits normal close/reopen after explicitly
closing the viewer. This is close-safety evidence, not qualification of MCP 3D
rendering. The receipt covers its frozen Linux candidate, not public packages,
later source changes or Mac/Desktop execution. Cancellable DRC jobs and result
freshness remain open in completion-ledger outcome `p9966151ec04cd9cf`.
