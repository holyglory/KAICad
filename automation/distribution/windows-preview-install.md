# Install the September 15 Windows preview

[Download the Windows x64 package](https://kicad.vr.ae/platforms/win-x64/artifacts/kicad-codex-4975b884f73ee19eff74c2fc1e9d00414b603199-windows-x64.zip)
for version `preview-20260915-4975b884f73e`, commit
`4975b884f73ee19eff74c2fc1e9d00414b603199`. Other platforms and versions are on
[the download page](https://kicad.vr.ae/).

This is a development preview, not the completed XML/routing/simulation product.
The ZIP contains KiCad and its self-contained MCP companion. Extracting and
running `bin/kicad.exe` alone does not configure managed updates.

## Install with managed updates

Obtain the trusted public key through the existing
[publisher bootstrap instructions](README.md). Before executing the downloaded
helper, compare the ZIP's SHA-256 with the value from your trusted Git/source
copy of this guide:

`f1ad84a1b822afe342294fdf0117cfdbf5a88fcca0d424b6dada2befe41b9593`

PowerShell prints the hash with `Get-FileHash -Algorithm SHA256` followed by the
ZIP's path. Keep the verified ZIP and save its matching
[signed feed](https://kicad.vr.ae/platforms/win-x64/updates/preview.json) as
`preview.json`. Feeds advance; if the feed and ZIP no longer match, obtain a
matching pair rather than editing the signed metadata.

Extract the ZIP and choose a new installation directory whose parent already
exists. Save `install-request.json`, replacing these example paths with your
own absolute paths:

```json
{
  "schemaVersion": 1,
  "installationRoot": "C:/Users/you/Applications/KAICad",
  "archivePath": "C:/Users/you/Downloads/kicad-codex-4975b884f73ee19eff74c2fc1e9d00414b603199-windows-x64.zip",
  "envelopePath": "C:/Users/you/Downloads/preview.json",
  "origin": "https://kicad.vr.ae/platforms/win-x64/",
  "channel": "preview"
}
```

From the extracted package directory, run:

```powershell
& .\bin\kicad-mcp.exe --install-package `
  --configuration "C:/Users/you/Downloads/install-request.json" `
  --publisher-key "C:/absolute/trusted/preview-publisher.spki"
```

The command verifies the package and creates the new installation. It refuses
an existing destination and does not register system shortcuts or start editors.
Use the Update button for an existing managed installation instead.

## Launch and update

Run `<installationRoot>/kicad.exe`. The stable STDIO MCP entry is
`<installationRoot>/kicad-mcp.exe`, with no extra arguments. Keep design
repositories outside this installation and use the root launchers, not paths
inside retained version directories.

When a newer compatible package has downloaded and verified, an **Update**
button appears. It is absent when the installation is already current. Updating
requires your action; cancelling the unsaved-work dialog leaves the design open.

The [real Windows update test](https://github.com/holyglory/KAICad/actions/runs/34970361837)
passed the preceding public release's upgrade to this package: two preserved
designs, cancel/save, both native restarts, exact object identities and packaged
MCP reconnection. This is preview/update evidence, not full automatic XML
synchronization or Codex Desktop qualification.
