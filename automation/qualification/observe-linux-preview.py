"""Validate fresh public observations and emit bounded delivery evidence."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


ROOT = Path(__file__).resolve().parents[2]


def required(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"Missing delivery input: {name}")
    return value


def load(path: Path) -> dict:
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError) as error:
        raise RuntimeError(f"Cannot read delivery evidence {path}: {error}") from error


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def require_fields(value: dict, expected: dict, label: str) -> None:
    for key, wanted in expected.items():
        if value.get(key) != wanted:
            raise RuntimeError(f"{label} mismatch for {key}: {value.get(key)!r}")


def main() -> None:
    request_path = Path(os.environ.get("KICAD_PREVIEW_REQUEST", "/mnt/build-storage/codex/kicad/delivery/preview28/request.json"))
    request = load(request_path)
    evidence = Path(os.environ.get("KICAD_DELIVERY_EVIDENCE",
                                  ROOT / "automation/artifacts/preview28-linux-delivery"))
    signed_root = Path(request["signed_root"])
    identity = json.loads(subprocess.check_output([
        os.environ.get("KICAD_COORDINATOR_EXECUTOR", "/home/DevCoordinator2/target/release/devcoordinator2-executor"),
        "source-digest", "--worktree", str(ROOT)
    ], text=True))
    source_sha256 = identity.get("sha256")
    if source_sha256 != request["source_sha256"]:
        raise RuntimeError("The delivery checkout source digest changed after publication staging.")
    catalogue = load(signed_root / "downloads.json")
    artifacts = catalogue.get("artifacts", [])
    linux = [item for item in artifacts if item.get("platform") == "linux-x64" and item.get("commit") == request["commit"]]
    source = [item for item in artifacts if item.get("platform") == "source" and item.get("commit") == request["commit"]]
    if len(linux) != 1 or len(source) != 1:
        raise RuntimeError("Signed catalogue is missing the exact Linux/source candidate pair.")
    app = linux[0]
    if app.get("sourceSha256") != source[0].get("sha256") or app.get("sourceSha256") != source_sha256:
        raise RuntimeError("Signed catalogue source binding is inconsistent.")
    started_at_ms = int(request.get("public_started_at_ms", request.get("published_candidate_at_ms", 0)))
    linux_observation = load(evidence / "linux.verification.json")
    web_observation = load(evidence / "web.verification.json")
    require_fields(linux_observation, {
        "version": 1,
        "kind": "artifact",
        "target": "linux-x64",
        "source_sha256": source_sha256,
        "file": app["fileName"],
        "observed_sha256": app["sha256"],
        "access": request["public_url"].rstrip("/") + "/artifacts/" + app["fileName"],
        "observation": "download_matched",
    }, "Linux verification")
    require_fields(web_observation, {
        "version": 1,
        "kind": "web-deployment",
        "target": "downloads-web",
        "source_sha256": source_sha256,
        "access": request["public_url"],
        "observation": "web_route_passed",
    }, "Web verification")
    for label, value in (("Linux", linux_observation), ("Web", web_observation)):
        checked = value.get("checked_at_ms")
        if not isinstance(checked, int) or checked < started_at_ms:
            raise RuntimeError(f"{label} observation is not fresh for this delivery run.")
    deployment = web_observation.get("deployment", {})
    require_fields(deployment, {"deployment_id": request["deployment_id"]}, "Deployment observation")
    if not isinstance(deployment.get("generation_number"), int) or deployment["generation_number"] <= 0:
        raise RuntimeError("Web observation has no deployment generation.")
    verification = {
        "version": 1,
        "kind": "artifact",
        "target": "linux-x64",
        "source_sha256": source_sha256,
        "file": app["fileName"],
        "observed_sha256": app["sha256"],
        "checked_at_ms": linux_observation["checked_at_ms"],
        "access": linux_observation["access"],
        "observation": "download_matched",
    }
    (evidence / "Verification").write_text(json.dumps(verification, indent=2) + "\n")
    completion = {
        "schema_version": 1,
        "claim": "preliminary",
        "source_sha256": source_sha256,
        "source_archive_sha256": source[0]["sha256"],
        "release": {"sequence": request["sequence"], "version": request["version"], "commit": request["commit"]},
        "artifacts": {"archive": app["fileName"], "sha256": app["sha256"]},
        "verification_file": "Verification",
        "capabilities": [
            {"id": "KICAD.DELIVERY.SIGNED_LINUX_DOWNLOAD", "scope": "product", "state": "real_e2e",
             "expected_result": "Download the exact signed Linux preview through the public HTTPS route.",
             "evidence_refs": ["public-download-tests", "delivery-receipt-tests"], "enabled_control": False},
            {"id": "KICAD.DELIVERY.LINUX_UPDATE", "scope": "product", "state": "real_e2e",
             "expected_result": "Install the signed Linux update while preserving the native editor journey.",
             "evidence_refs": ["updater-tests"], "enabled_control": False},
            {"id": "KICAD.DELIVERY.PUBLIC_PACKAGE_ROUTE", "scope": "product", "state": "real_e2e",
             "expected_result": "Serve the complete preserved catalogue from the owned public deployment.",
             "evidence_refs": ["delivery-receipt-tests"], "enabled_control": False},
        ],
    }
    (evidence / "completion.json").write_text(json.dumps(completion, indent=2) + "\n")
    check = subprocess.run([
        "devcoordinator2", "completion", "check", "--file", str(evidence / "completion.json"), str(ROOT)
    ], text=True, capture_output=True, check=False)
    if check.returncode != 0:
        raise RuntimeError("Completion inventory rejected: " + check.stdout[-2000:])
    print(json.dumps({"status": "passed", "sequence": request["sequence"],
                      "commit": request["commit"], "source_sha256": source_sha256,
                      "verification": str(evidence / "Verification"),
                      "completion": str(evidence / "completion.json")}, indent=2))


if __name__ == "__main__":
    main()
