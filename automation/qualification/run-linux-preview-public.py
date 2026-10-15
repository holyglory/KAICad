"""Qualify one frozen Linux archive with a separately identified harness."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
ALLOWED = {".devcoordinator.toml", "automation/qualification/publish-linux-preview.py",
           "automation/qualification/run-linux-preview-public.py", "automation/qualification/observe-linux-preview.py"}


def sha(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for b in iter(lambda: f.read(1024 * 1024), b""): h.update(b)
    return h.hexdigest()


def out(args: list[str]) -> str:
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def run(args: list[str], env: dict[str, str]) -> int:
    print(json.dumps({"operation": args[0], "arguments": args[1:]}), flush=True)
    return subprocess.run(args, cwd=ROOT, env=env, check=False).returncode


def main() -> None:
    req_path = Path(os.environ.get("KICAD_PREVIEW_REQUEST", "/mnt/build-storage/codex/kicad/delivery/preview28/request.json"))
    req = json.loads(req_path.read_text())
    run_id = os.environ.get("DEVCOORDINATOR_RUN_ID", "")
    if not re.fullmatch(r"[A-Za-z0-9._-]+", run_id): raise RuntimeError("Missing governed run identity")
    if out(["git", "status", "--porcelain=v1", "--untracked-files=all"]): raise RuntimeError("Checkout is dirty")
    package_commit = req["commit"]
    current_commit = out(["git", "rev-parse", "HEAD"])
    unexpected = set(out(["git", "diff", "--name-only", package_commit, current_commit]).splitlines()) - ALLOWED
    if unexpected: raise RuntimeError("Verification source changed outside the delivery harness: " + ", ".join(sorted(unexpected)))
    executor = os.environ.get("KICAD_COORDINATOR_EXECUTOR", "/home/DevCoordinator2/target/release/devcoordinator2-executor")
    source_sha = json.loads(out([executor, "source-digest", "--worktree", str(ROOT)]))["sha256"]
    evidence = Path(os.environ.get("KICAD_DELIVERY_EVIDENCE", ROOT / "automation/artifacts/preview28-linux-delivery")).resolve()
    if not evidence.is_relative_to((ROOT / "automation/artifacts").resolve()): raise RuntimeError("Invalid evidence root")
    if evidence.exists():
        hist = Path("/mnt/build-storage/codex/kicad/evidence/preview28-public-history")
        hist.mkdir(parents=True, exist_ok=True)
        target = hist / (time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + run_id)
        if target.exists(): raise RuntimeError("Evidence preservation target exists")
        shutil.move(str(evidence), str(target))
    evidence.mkdir(parents=True, exist_ok=False)
    run_identity = {"schema_version": 1, "run_id": run_id, "started_at_ms": int(time.time() * 1000),
                    "commit": current_commit, "source_sha256": source_sha, "package_commit": package_commit,
                    "package_source_sha256": req["source_sha256"], "source_archive_sha256": req["source_archive_sha256"],
                    "request_sha256": sha(req_path)}
    (evidence / "run-identity.json").write_text(json.dumps(run_identity, indent=2) + "\n")
    (evidence / "publication-request.json").write_bytes(req_path.read_bytes())
    for key in ("linux_artifact", "source_artifact"):
        item = req[key]; p = Path(req["candidate_root"]) / item["fileName"]
        if p.stat().st_size != item["bytes"] or sha(p) != item["sha256"]: raise RuntimeError("Frozen archive changed")
    origin = req["public_url"].rstrip("/") + "/"
    env = os.environ.copy()
    env.update({"KICAD_PUBLIC_DOWNLOAD_URL": origin, "KICAD_PACKAGE_CATALOGUE": req["signed_root"],
        "KICAD_UPDATE_PUBLISHER_SPKI_FILE": req["publisher_public"], "KICAD_PUBLIC_DOWNLOAD_EVIDENCE": str(evidence / "public-downloads"),
        "KICAD_DELIVERY_ROUTE_ONLY": "true", "KICAD_DELIVERY_PLATFORM": "linux-x64", "KICAD_DELIVERY_COMMIT": package_commit,
        "KICAD_DELIVERY_DEPLOYMENT": req["deployment_id"], "KICAD_COORDINATOR_EXECUTOR": executor,
        "KICAD_DELIVERY_EVIDENCE": str(evidence), "KICAD_CAPTION_PACKAGE_CATALOGUE": req["previous_package_root"],
        "KICAD_CAPTION_CANDIDATE_CATALOGUE": req["candidate_root"], "KICAD_CAPTION_INSTALLED_ENVELOPE": str(Path(req["previous_root"]) / "updates/preview.json")})
    outcomes = []
    try:
        with urllib.request.urlopen(origin + "healthz", timeout=20) as response:
            if response.status != 200: raise RuntimeError(f"HTTP {response.status}")
        outcomes.append({"suite": "tls-preflight", "status": "passed"})
    except Exception as e: outcomes.append({"suite": "tls-preflight", "status": "failed", "reason": str(e)})
    solution = "automation/KiCad.Automation.slnx"
    suites = [("public-download-tests", "TestCategory=PublicDownloads", []),
              ("delivery-receipt-tests", "TestCategory=DeliveryReceipts", []),
              ("updater-tests", "TestCategory=PublicCaptionMcpUpdate|TestCategory=EmptyCaptionUpdate", ["--", "MSTest.MapInconclusiveToFailed=true", "MSTest.Parallelize.Workers=1"])]
    for name, filt, extra in suites:
        args = ["dotnet", "test", solution, "--no-build", "--no-restore", "--configuration", "Debug", "--filter", filt,
                "--logger", "trx;LogFileName=results.trx", "--results-directory", str(evidence / name), *extra]
        started = int(time.time() * 1000); code = run(args, env)
        outcomes.append({"suite": name, "status": "passed" if code == 0 else "failed", "exit_code": code, "started_at_ms": started})
        if name == "updater-tests":
            native = ROOT / "automation/artifacts/native-session-current"
            if native.is_dir(): shutil.copytree(native, evidence / "updater-native")
    (evidence / "suite-results.json").write_text(json.dumps({"run_id": run_id, "suites": outcomes}, indent=2) + "\n")
    failures = [x["suite"] for x in outcomes if x["status"] != "passed"]
    if failures: raise RuntimeError("Qualification suites failed: " + ", ".join(failures))
    if sha(req_path) != run_identity["request_sha256"]: raise RuntimeError("Publication request changed")
    subprocess.run(["python3", "automation/qualification/observe-linux-preview.py"], cwd=ROOT, env=env, check=True)


if __name__ == "__main__": main()
