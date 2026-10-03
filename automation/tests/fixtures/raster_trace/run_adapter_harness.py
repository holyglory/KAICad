#!/usr/bin/env python3
"""Run the focused raster adapter harness against existing native artifacts.

This check intentionally does not invoke CMake or a KiCad target. It reuses
the compile flags and static libraries from an existing native compile database,
then compiles only the adapter and its deterministic fixture harness in a
temporary directory.
"""

from __future__ import annotations

import json
import shlex
import subprocess
import tempfile
from pathlib import Path


def main() -> int:
    root = Path(__file__).resolve().parents[4]
    build_root = Path("/home/holyglory/.codex/worktrees/0761/kicad/automation/artifacts/native")
    compile_db = build_root / "compile_commands.json"
    if not compile_db.is_file():
        raise SystemExit(f"existing native compile database is unavailable: {compile_db}")

    entries = json.loads(compile_db.read_text())
    entry = next(row for row in entries if row.get("file", "").endswith("common/bitmap.cpp"))
    args = shlex.split(entry["command"])
    rewritten: list[str] = []
    for arg in args:
        if arg == "/home/holyglory/.codex/worktrees/0761/kicad":
            rewritten.append(str(root))
        else:
            rewritten.append(arg.replace("/home/holyglory/.codex/worktrees/0761/kicad", str(root)))
    args = [arg for arg in rewritten if arg != str(root / "common/bitmap.cpp")]
    if "-c" in args:
        args.remove("-c")
    if "-o" in args:
        index = args.index("-o")
        del args[index : index + 2]
    while "-Winvalid-pch" in args:
        args.remove("-Winvalid-pch")
    while "-include" in args:
        index = args.index("-include")
        del args[index : index + 2]

    args[1:1] = [f"-I{root / 'common'}", f"-I{root / 'thirdparty/potrace/include'}"]
    args.extend(
        [
            str(root / "common/bitmap_trace.cpp"),
            str(root / "automation/tests/fixtures/raster_trace/raster_trace_adapter_harness.cpp"),
            *map(str, (root / "thirdparty/potrace/src").glob("*.cpp")),
            str(build_root / "libs/kimath/libkimath.a"),
            str(build_root / "thirdparty/clipper2/libclipper2.a"),
            str(build_root / "libs/core/libcore.a"),
            str(build_root / "common/libkicommon.so.10.99.0"),
            f"-Wl,-rpath,{build_root / 'common'}",
        ]
    )
    args.extend(shlex.split(subprocess.check_output(["wx-config", "--libs", "std,aui,core,base"], text=True)))
    with tempfile.TemporaryDirectory(prefix="kicad-raster-trace-") as directory:
        executable = Path(directory) / "raster-trace-adapter"
        subprocess.run([*args, "-o", str(executable)], cwd=root, check=True)
        subprocess.run([str(executable)], cwd=root, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
