# Point Codex at a new KAICad preview's MCP server

After you install a new KAICad Linux preview, your AI client keeps using the MCP
server it started before. This applies to Codex Desktop and to a second client
such as the Claude Code CLI. The client sees the new preview's tools only after
two things happen: its configuration names the new server, and the client starts
that server again. This runbook covers five steps: find the new server, change
the client entry, restart the client, confirm which server is loaded, and run a
short check through the client.

## 1. Find the new server

Every Linux preview has two launchers at the top of its folder. `kicad-codex`
opens KiCad, and `kicad-mcp` starts the STDIO MCP server. `kicad-mcp` is a short
script: it resolves the preview's folder and runs the server program,
`runtime/lib/kicad-automation/kicad-mcp`. The `package.json` file in the same
folder records the preview's `version` and source `commit`. Point your client at
the `kicad-mcp` launcher.

Where that folder is depends on how you installed the preview:

| How you installed the preview | Server to configure | When the next preview arrives |
|---|---|---|
| Managed installation, created with `kicad-mcp --install-package` and updated with KiCad's caption **Update** button | `<installationRoot>/manager/current/kicad-mcp` | The path stays the same. An update adds `<installationRoot>/versions/<digest>/payload/` for the new release and moves `manager/current` to it. Earlier versions stay in place. |
| Extracted archive `kicad-codex-<version>-debian13-x64.tar.gz` | `<your folder>/kicad-mcp` | The path changes. The archive has no top-level folder, so extract each preview into a new, empty folder of its own. |
| Debian package of an older preview | `/opt/kicad-codex/<version>/kicad-mcp` | The path changes. Each package installs into its own `/opt/kicad-codex/<version>` folder and leaves the others in place. |

`<digest>` is the SHA-256 digest of the signed release record, written as 64
lower-case hexadecimal characters. To see which preview a managed installation
has selected:

```sh
readlink -f <installationRoot>/manager/current
cat <installationRoot>/manager/current/package.json
```

The [automation README](../README.md) explains how to create a managed
installation. Its section for each preview names that preview's commit and new
tools.

## 2. Change the client entry

### Codex Desktop

Codex Desktop, the Codex CLI and the Codex IDE extension read their MCP servers
from one shared file, `~/.codex/config.toml`. A trusted project can also have its
own `.codex/config.toml`. Use the file on the machine where the Codex app-server
runs. That is the Linux machine that runs the MCP server, which may not be the
computer that shows Codex Desktop.

Find the KAICad entry (named `kicad` here) and set `command` to the server from
step 1:

```toml
[mcp_servers.kicad]
command = "/absolute/path/to/kicad-install/manager/current/kicad-mcp"

[mcp_servers.kicad.env]
KICAD_AUTOMATION_STATE_DIRECTORY = "/absolute/path/to/kicad-mcp-state"
```

Change only `command`. Keep the `env` block and any timeouts you already had.
The state directory is where the server keeps its saved KiCad sessions, and
keeping it lets the new server find and reattach them. If the variable is not
set, the server uses `$XDG_DATA_HOME/kicad-automation`, or
`~/.local/share/kicad-automation` when `XDG_DATA_HOME` is not set.

`codex mcp get kicad` prints the command Codex will start. With the managed path
shown above, you edit this entry once: later caption updates only need step 3.

### Claude Code CLI

Claude Code keeps its own entry. Check which scope it is in, then replace it in
that same scope (`local`, `project` or `user`):

```sh
claude mcp get kicad
claude mcp remove kicad --scope user
claude mcp add kicad --scope user \
  -e KICAD_AUTOMATION_STATE_DIRECTORY=/absolute/path/to/kicad-mcp-state \
  -- /absolute/path/to/kicad-install/manager/current/kicad-mcp
```

## 3. Restart the client

Saving the configuration does not replace a server that is already running. A
Codex app-server that is already running does not reload the change. It keeps
the `kicad-mcp` processes it started, and they keep running the earlier preview
from its retained folder. Nothing fails; the new tools are simply missing.

- **Codex Desktop:** open **Settings → MCP servers** and choose **Restart**, or
  quit Codex Desktop and open it again. Then start a new conversation.
- **Codex Desktop connected over SSH:** the app-server runs as a daemon on the
  Linux machine and keeps running after you close Codex Desktop. Restart it
  there with `codex app-server daemon restart`, then reconnect Codex Desktop.
  If step 4 still shows an app-server started before your change, stop that
  process and reconnect Codex Desktop.
- **Claude Code:** exit `claude` and start it again, or reconnect the server
  from `/mcp` inside the session.

KiCad's caption **Update** restarts KiCad. It does not restart the MCP server
that your client started. That running server can reconnect to the replacement
KiCad with `kicad_instance_reconnect_after_update`. It keeps its old tools until
you restart the client.

## 4. Confirm which server is loaded

List the running servers with their command lines:

```sh
pgrep -af 'kicad-automation/kicad-mcp'
```

Ignore lines that include `--check-update`, `--prepare-update`,
`--restart-update` or another `--` option: those are KiCad's own update helper,
not your client's server. For each remaining process ID:

```sh
pid=12345   # a process ID from the list
exe=$(readlink /proc/$pid/exe); echo "$exe"
ps -o lstart= -p "$pid"
ps -o pid=,lstart=,args= -p "$(ps -o ppid= -p "$pid")"
cat "${exe%/runtime/lib/kicad-automation/kicad-mcp}/package.json"
```

What to check in the output:

- **The program path.** `exe` is the file that is actually running, and it must
  be inside the new preview's folder. The launcher resolves symbolic links first,
  so a server started through `manager/current` shows its real
  `versions/<digest>/payload` folder. A path that ends in ` (deleted)` means the
  program's files were removed after it started.
- **The start time.** The server's start time must be later than your restart.
- **The parent process.** The last `ps` line is the client that started the
  server: a `codex app-server` process for Codex, or `claude` for Claude Code.
  For Codex, the app-server's start time must also be later than your
  configuration change.
- **The preview identity.** `package.json` names the `version` and `commit`.
  Compare them with the preview you installed.

A client can run one server for each conversation, so you may see several
processes. Every one of them should belong to the new preview.

### The handshake

When a client connects, the server answers MCP's `initialize` request with its
name and version. Every preview so far gives the same answer:

```text
"serverInfo":{"name":"kicad-mcp","version":"1.0.0.0"}
```

The name confirms that the client reached the KAICad server. The version is the
same in every preview so far, so it cannot tell previews apart. Use the program
path and `package.json` above for that. To see the handshake of an installed
server without a client, start a throwaway copy with an empty state directory:

```sh
{ printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"refresh-check","version":"1"}}}'; sleep 2; } \
  | KICAD_AUTOMATION_STATE_DIRECTORY="$(mktemp -d)" /absolute/path/to/kicad-install/manager/current/kicad-mcp 2>/dev/null \
  | head -n 1 | grep -o '"serverInfo":{[^}]*}'
```

The `sleep` keeps the input open until the server has answered. If the input
closes at once, the server stops before it answers. This throwaway copy does not
touch your client's server or its saved sessions.

## 5. Check through the client

In a new conversation, ask the agent to do the following:

1. **Call `kicad_instances_list`.** A server that has just started returns an
   empty list, and it does not contact KiCad. KiCad instances that the earlier
   server attached are listed by `kicad_instance_saved_sessions`, and
   `kicad_instance_reattach` reconnects one of them.
2. **Call `kicad_service_capabilities` and name the tools it lists.** It lists
   every tool that the loaded server registered, without contacting KiCad. The
   list must include the tools that the new preview's README section introduces.
   For the September 24, 2026 preview, those include `kicad_diagram_create` and
   `kicad_diagram_discover`. If they are missing, the client is still running an
   older server: repeat steps 3 and 4.
3. **With KiCad running,** call `kicad_instance_capabilities` for its instance.
   It adds the features and requests that this KiCad supports to the same
   catalogue. `kicad_instance_start` takes the KiCad program as an explicit
   path, so give it one from the same preview.

## Keeping this runbook accurate

`CapabilityCatalogTests.RuntimeRefreshRunbookMatchesWhatAClientSees` starts the
compiled server with a real MCP client. It fails when this page names a tool
that the server does not register, when the quoted `serverInfo` differs from the
server's handshake, or when the first two calls of step 5 answer differently.
The folder layouts in step 1 come from
`tools/KiCad.Automation.Validation/LinuxPackage.cs` and `DebianPackage.cs`, and
from `src/KiCad.Automation.Distribution/LinuxVerifiedInstallation.cs` with
`PosixUpdateActivation.cs`.
