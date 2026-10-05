#!/usr/bin/env python3
"""Retain pre-build context, then replace this process with the exact build command."""

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    command = sys.argv[1:]
    if not command:
        raise ValueError("A build command is required.")
    diagnostics = Path(os.environ["MSBUILDDEBUGPATH"])
    diagnostics.mkdir(parents=True, exist_ok=True)
    context = {
        "schema": 1,
        "diagnosticsRequested": True,
        "recordedBeforeExecution": True,
        "requestedAtUtc": datetime.now(timezone.utc).isoformat(),
        "runId": os.environ.get("DEVCOORDINATOR_RUN_ID"),
        "checkName": os.environ.get("DEVCOORDINATOR_CHECK_NAME"),
        "sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
        "command": command,
        "crashFilesPresentAtStart": sorted(path.name for path in diagnostics.glob("MSBuild_*.failure.txt")),
    }
    (diagnostics / "context.json").write_text(json.dumps(context, sort_keys=True, indent=2) + "\n")
    # The Coordinator observes the build's own exit and signal; this context
    # requests diagnostics and never represents a successful build receipt.
    os.execvpe(command[0], command, os.environ)


if __name__ == "__main__":
    main()
