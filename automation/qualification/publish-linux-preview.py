"""Discover, sign, and stage one frozen Linux package candidate.

The package graph writes candidates outside the checkout. This helper binds the
newest candidate for the current checkout commit to a signed update feed and a
complete catalogue copy. All outputs are external delivery evidence; the
checkout remains untouched.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR = ROOT / "automation/tools/KiCad.Automation.Validation/bin/Debug/net10.0/kicad-validate.dll"


def required(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"Missing delivery input: {name}")
    return value


def configured(name: str, default: str) -> str:
    return os.environ.get(name, default)


def read_json(path: Path) -> dict:
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError) as error:
        raise RuntimeError(f"Cannot read delivery JSON {path}: {error}") from error


def digest(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def run(command: list[str]) -> None:
    print(json.dumps({"operation": command[0], "arguments": command[1:]}), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def main() -> None:
    candidate_root = Path(configured("KICAD_PUBLIC_CANDIDATE_ROOT", "/mnt/build-storage/codex/kicad/public-candidates"))
    previous_root = Path(configured("KICAD_PREVIOUS_PUBLIC_ROOT", "/mnt/build-storage/codex/kicad/public-candidates/win-e40-full-signed-000012"))
    request_path = Path(configured("KICAD_PREVIEW_REQUEST", "/mnt/build-storage/codex/kicad/delivery/preview28/request.json"))
    evidence_root = Path(os.environ.get("KICAD_PREVIEW_PUBLISH_EVIDENCE",
                                       ROOT / "automation/artifacts/preview28-publish"))
    sequence = int(configured("KICAD_PREVIEW_SEQUENCE", "28"))
    publisher_private = Path(configured("KICAD_PUBLISHER_PRIVATE", "/mnt/build-storage/codex/kicad/signing/preview-publisher.pkcs8"))
    publisher_public = Path(configured("KICAD_PUBLISHER_PUBLIC", "/home/holyglory/kicad/automation/distribution/preview-publisher.spki"))
    if request_path.exists():
        raise RuntimeError(f"Refusing to overwrite an existing delivery request: {request_path}")
    if not candidate_root.is_dir() or not previous_root.is_dir():
        raise RuntimeError("Candidate and previous public roots must already exist.")
    if not (previous_root / "updates/preview.json").is_file():
        raise RuntimeError("The previous public root has no signed preview feed.")
    if not VALIDATOR.is_file():
        raise RuntimeError(f"The built validation tool is missing: {VALIDATOR}")

    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    identity = json.loads(subprocess.check_output([
        required("KICAD_COORDINATOR_EXECUTOR"), "source-digest", "--worktree", str(ROOT)
    ], text=True))
    source_sha256 = identity.get("sha256")
    if not isinstance(source_sha256, str) or len(source_sha256) != 64:
        raise RuntimeError("The governed source digest is unavailable.")

    candidates: list[tuple[float, Path, dict]] = []
    for manifest_path in candidate_root.glob("*/downloads.json"):
        manifest = read_json(manifest_path)
        artifacts = manifest.get("artifacts")
        if not isinstance(artifacts, list) or not artifacts:
            continue
        if any(item.get("commit") != commit or item.get("sourceSha256") != source_sha256
               for item in artifacts if isinstance(item, dict)):
            continue
        if any(not isinstance(item, dict) for item in artifacts):
            continue
        candidates.append((manifest_path.stat().st_mtime_ns, manifest_path.parent, manifest))
    if not candidates:
        raise RuntimeError(f"No package candidate matches commit {commit} and source {source_sha256}.")
    _, candidate_root, catalogue = max(candidates, key=lambda item: item[0])
    artifacts = catalogue["artifacts"]
    linux = [item for item in artifacts if item.get("platform") == "linux-x64"]
    source = [item for item in artifacts if item.get("platform") == "source"]
    if len(linux) != 1 or len(source) != 1 or len(artifacts) != 2:
        raise RuntimeError("The package candidate must contain exactly one Linux and one source archive.")
    linux_artifact, source_artifact = linux[0], source[0]
    if linux_artifact.get("sourceSha256") != source_artifact.get("sha256"):
        raise RuntimeError("Linux and source archive identities do not match.")
    for item in (linux_artifact, source_artifact):
        archive = candidate_root / item["fileName"]
        if not archive.is_file() or archive.stat().st_size != item["bytes"] or digest(archive) != item["sha256"]:
            raise RuntimeError(f"Candidate archive failed its manifest check: {archive}")

    version = linux_artifact["version"]
    run_name = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + commit[:12]
    work_root = Path(configured("KICAD_PREVIEW_WORK_ROOT", "/mnt/build-storage/codex/kicad/delivery/preview28")) / run_name
    work_root.mkdir(parents=True, exist_ok=False)
    release_path = work_root / "release.json"
    feed_path = work_root / "preview.json"
    signed_root = work_root / "signed"
    release = {
        "schemaVersion": 1,
        "product": "kicad-codex",
        "channel": "preview",
        "sequence": sequence,
        "version": version,
        "commit": commit,
        "artifacts": [{
            "platform": "linux-x64",
            "format": "tar.gz",
            "fileName": linux_artifact["fileName"],
            "bytes": linux_artifact["bytes"],
            "sha256": linux_artifact["sha256"],
        }],
    }
    release_path.write_text(json.dumps(release, indent=2) + "\n")
    run(["dotnet", str(VALIDATOR), "release-sign", "--release", str(release_path),
         "--artifacts", str(candidate_root), "--private", str(publisher_private),
         "--output", str(feed_path), "--previous", str(previous_root / "updates/preview.json")])
    run(["dotnet", str(VALIDATOR), "stage-signed-linux", "--candidate", str(candidate_root),
         "--commit", commit, "--previous", str(previous_root), "--output", str(signed_root),
         "--feed", str(feed_path), "--publisher", str(publisher_public)])
    request = {
        "schema_version": 1,
        "sequence": sequence,
        "candidate_root": str(candidate_root),
        "previous_root": str(previous_root),
        "signed_root": str(signed_root),
        "release_manifest": str(release_path),
        "feed": str(feed_path),
        "commit": commit,
        "version": version,
        "source_sha256": source_sha256,
        "linux_artifact": linux_artifact,
        "source_artifact": source_artifact,
        "publisher_public": str(publisher_public),
        "public_url": configured("KICAD_PUBLIC_DOWNLOAD_URL", "https://kicad.vr.ae/"),
        "deployment_id": configured("KICAD_DELIVERY_DEPLOYMENT", "d30d9eeb63ffeccbc"),
        "published_candidate_at_ms": int(time.time() * 1000),
    }
    request_path.parent.mkdir(parents=True, exist_ok=True)
    request_path.write_text(json.dumps(request, indent=2) + "\n")
    evidence_root.mkdir(parents=True, exist_ok=True)
    (evidence_root / "request.json").write_text(json.dumps(request, indent=2) + "\n")
    print(json.dumps({"status": "staged", "candidate": str(candidate_root),
                      "signed_root": str(signed_root), "request": str(request_path),
                      "commit": commit, "source_sha256": source_sha256}, indent=2))


if __name__ == "__main__":
    main()
