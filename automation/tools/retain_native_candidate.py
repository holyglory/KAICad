#!/usr/bin/env python3
"""Retain identity after this graph's managed and native build checks succeed."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tomllib


def git(root, *arguments):
    return subprocess.check_output(
        ["git", "-C", str(root), *arguments], text=True
    ).strip()


def file_record(root, relative_path):
    path = root / relative_path
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1048576), b""):
            digest.update(chunk)
    return {
        "path": relative_path,
        "bytes": path.stat().st_size,
        "sha256": digest.hexdigest(),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    status = git(root, "status", "--porcelain=v1", "--untracked-files=normal")
    if status:
        raise RuntimeError("Candidate source is not frozen: " + status)
    commit = git(root, "rev-parse", "HEAD")
    build = root / "automation/artifacts/native"
    cache = (build / "CMakeCache.txt").read_text()
    source_match = re.search(r"^CMAKE_HOME_DIRECTORY:INTERNAL=(.+)$", cache, re.M)
    if source_match is None or Path(source_match[1]).resolve() != root:
        raise RuntimeError("Native build directory belongs to another source checkout.")
    version = (build / "kicad_build_version.h").read_text()
    native_commit = re.search(r'^#define KICAD_COMMIT_HASH\s+"([0-9a-f]+)"$', version, re.M)
    if native_commit is None or native_commit[1] != commit:
        raise RuntimeError("Regenerated native commit does not match the frozen candidate.")
    if re.search(r'^#define KICAD_VERSION_FULL\s+"[^"]*-dirty"$', version, re.M):
        raise RuntimeError("Native build metadata reports unfrozen source.")

    configuration = tomllib.loads((root / ".devcoordinator.toml").read_text())
    graph = configuration["test"]["pcb-simulation-checkpoint"]
    build_check = next(check for check in graph["check"] if check["name"] == "native-build")
    identity = {
        "schema": 1,
        "repository": str(root),
        "sourceCommit": commit,
        "sourceTree": git(root, "rev-parse", "HEAD^{tree}"),
        "sourceStatus": "clean",
        "nativeBuildMode": "incremental",
        "nativeBuildCheck": "native-build",
        "nativeBuildCommand": build_check["command"],
        "nativeSourceDirectory": str(Path(source_match[1]).resolve()),
        "generatedNativeCommit": native_commit[1],
        "nativeBinaries": [file_record(root, "automation/artifacts/native/" + path) for path in (
            "kicad/kicad", "kicad/kicad-cli", "eeschema/eeschema", "eeschema/_eeschema.kiface",
            "pcbnew/pcbnew", "pcbnew/_pcbnew.kiface", "cvpcb/_cvpcb.kiface",
            "api/libkiapi.so", "common/libkicommon.so", "common/gal/libkigal.so",
        )],
        "managedAssemblies": [file_record(root, "automation/" + path) for path in (
            "src/KiCad.Automation.Mcp/bin/Debug/net10.0/kicad-mcp.dll",
            "tests/KiCad.Automation.Tests/bin/Debug/net10.0/KiCad.Automation.Tests.dll",
        )],
        "buildMetadata": [file_record(root, path) for path in (
            ".devcoordinator.toml", "automation/artifacts/native/CMakeCache.txt",
            "automation/artifacts/native/build.ninja", "automation/artifacts/native/.ninja_log",
            "automation/artifacts/native/kicad_build_version.h",
            "automation/artifacts/native/resources/images.tar.gz",
        )],
        "schemas": [file_record(root, str(path.relative_to(root)))
                    for path in sorted((build / "schemas").glob("*.json"))],
    }
    if not identity["schemas"]:
        raise RuntimeError("Required native startup schemas are missing.")
    if git(root, "rev-parse", "HEAD") != commit or git(
        root, "status", "--porcelain=v1", "--untracked-files=normal"
    ):
        raise RuntimeError("Candidate source changed during identity capture.")
    output = arguments.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    (output / "identity.json").write_text(json.dumps(identity, sort_keys=True, indent=2) + "\n")
    print(json.dumps({"sourceCommit": commit, "nativeBuildCheck": "native-build",
                      "nativeBinaryCount": len(identity["nativeBinaries"])}))


if __name__ == "__main__":
    main()
