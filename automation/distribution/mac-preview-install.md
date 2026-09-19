# Install the September 19 Mac preview

These development previews contain KiCad and its MCP companion. Both Apple
Silicon and Intel use `preview-20260918-f120a47cfbd1` from commit
`f120a47cfbd1832bc9133f6994b5e29aad082f3c`. Both are ad-hoc signed, not notarized.

| Mac | Application archive | Signed update feed |
| --- | --- | --- |
| Apple Silicon | [Download](https://kicad.vr.ae/platforms/osx-arm64/artifacts/kicad-codex-f120a47cfbd1832bc9133f6994b5e29aad082f3c-macos-arm64.tar.gz) | [Apple Silicon feed](https://kicad.vr.ae/platforms/osx-arm64/updates/preview.json) |
| Intel | [Download](https://kicad.vr.ae/platforms/osx-x64/artifacts/kicad-codex-f120a47cfbd1832bc9133f6994b5e29aad082f3c-macos-x64.tar.gz) | [Intel feed](https://kicad.vr.ae/platforms/osx-x64/updates/preview.json) |

[Matching source](https://kicad.vr.ae/artifacts/kicad-codex-f120a47cfbd1832bc9133f6994b5e29aad082f3c-source.tar.gz)
and other versions are available from [the download page](https://kicad.vr.ae/).
The feeds can advance: keep the downloaded archive and its matching signed feed
together. If they no longer match, obtain a matching pair; do not edit the feed.

## Install with managed updates

The package contains `install/KiCad.app`, but opening it directly does not
configure managed updates. The preliminary installer is a command-line bootstrap;
use it for an installation that can update itself.

Obtain the trusted public publisher key through the existing
[publisher bootstrap instructions](README.md). Before executing a downloaded
helper, compare the archive's SHA-256 with the matching value from your trusted
Git/source copy of this guide:

- Apple Silicon: `439ad587446d486d696b64218d374aea9d490dc42cf1f5d23d0ab6ada1a969b0`
- Intel: `b03ef84339b1171a70e6b10814f5a8c0bee106ab02222586d9b65c1b4908ab01`

On the Mac, `shasum -a 256 /absolute/path/to/the/downloaded/archive.tar.gz`
prints that hash. Keep the verified archive, save its matching feed as
`preview.json`, and extract the archive on the Mac.

Choose a **new** installation directory whose parent already exists. Save an
`install-request.json` like this, replacing every example path with your own
absolute Mac path:

```json
{
  "schemaVersion": 1,
  "installationRoot": "/Users/you/Applications/KiCad-Codex",
  "archivePath": "/Users/you/Downloads/kicad-codex-f120a47cfbd1832bc9133f6994b5e29aad082f3c-macos-arm64.tar.gz",
  "envelopePath": "/Users/you/Downloads/preview.json",
  "origin": "https://kicad.vr.ae/platforms/osx-arm64/",
  "channel": "preview"
}
```

On Intel, use the `macos-x64.tar.gz` archive and set `origin` to
`https://kicad.vr.ae/platforms/osx-x64/`.

From the extracted package directory, run its bootstrap helper:

```sh
./kicad-mcp --install-package \
  --configuration /Users/you/Downloads/install-request.json \
  --publisher-key /absolute/trusted/preview-publisher.spki
```

The installer verifies the signed feed, archive and native package before
creating the new installation. It refuses an existing destination. Do not
replace an existing managed installation with this command; use its Update
button instead.

## Launch and update

Open `<installationRoot>/KiCad.app`. The stable MCP entry point is
`<installationRoot>/kicad-mcp`, with no additional arguments for STDIO service.
Use these root-level launchers, not a path inside a retained version directory.

When a newer compatible build has downloaded and verified, the editor shows an
**Update** button. It is normal for that button to be absent when this build is
already current. Updating requires your action; cancelling the unsaved-work
dialog leaves the current design open. Keep project repositories outside the
installation directory.

The real update journey from the preceding public build passed on
[Apple Silicon](https://github.com/holyglory/KAICad/actions/runs/35411689256) and
[Intel](https://github.com/holyglory/KAICad/actions/runs/35411689256), including two
preserved designs, cancellation, native restart and packaged MCP reconnection.
These builds add measured initial schematic placement and field-layout proposals
through MCP. The update journey proves this specific upgrade, not every automatic
update or recovery scenario. Full XML reconstruction, routing and actual Codex
Desktop qualification remain unfinished.
