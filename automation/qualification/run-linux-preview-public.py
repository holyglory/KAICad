"""Run public download, deployment, and signed Linux updater journeys.

The request and signed catalogue are external immutable delivery inputs. The
wrapper derives every test environment value from those inputs so a commit or
archive identity is never copied into source configuration.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import time
import urllib.request


ROOT = Path(__file__).resolve().parents[2]


def required(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"Missing delivery input: {name}")
    return value


def run(command: list[str], environment: dict[str, str]) -> None:
    print(json.dumps({"operation": command[0], "arguments": command[1:]}), flush=True)
    subprocess.run(command, cwd=ROOT, env=environment, check=True)


def main() -> None:
    request = json.loads(Path(os.environ.get("KICAD_PREVIEW_REQUEST", "/mnt/build-storage/codex/kicad/delivery/preview28/request.json")).read_text())
    signed_root = Path(request["signed_root"])
    previous_root = Path(request["previous_root"])
    evidence = Path(os.environ.get("KICAD_DELIVERY_EVIDENCE",
                                  ROOT / "automation/artifacts/preview28-linux-delivery"))
    evidence.mkdir(parents=True, exist_ok=True)
    started_at_ms = int(time.time() * 1000)
    request["public_started_at_ms"] = started_at_ms
    Path(os.environ.get("KICAD_PREVIEW_REQUEST", "/mnt/build-storage/codex/kicad/delivery/preview28/request.json")).write_text(json.dumps(request, indent=2) + "\n")

    origin = request["public_url"].rstrip("/") + "/"
    with urllib.request.urlopen(origin + "healthz", timeout=20) as response:
        if response.status != 200:
            raise RuntimeError(f"Public health probe returned HTTP {response.status}")

    environment = os.environ.copy()
    environment.update({
        "KICAD_PUBLIC_DOWNLOAD_URL": origin,
        "KICAD_PACKAGE_CATALOGUE": str(signed_root),
        "KICAD_UPDATE_PUBLISHER_SPKI_FILE": request["publisher_public"],
        "KICAD_PUBLIC_DOWNLOAD_EVIDENCE": str(evidence / "public-downloads"),
        "KICAD_DELIVERY_ROUTE_ONLY": "false",
        "KICAD_DELIVERY_PLATFORM": "linux-x64",
        "KICAD_DELIVERY_COMMIT": request["commit"],
        "KICAD_DELIVERY_DEPLOYMENT": request["deployment_id"],
        "KICAD_COORDINATOR_EXECUTOR": os.environ.get(
            "KICAD_COORDINATOR_EXECUTOR", "/home/DevCoordinator2/target/release/devcoordinator2-executor"),
        "KICAD_DELIVERY_EVIDENCE": str(evidence),
        "KICAD_CAPTION_PACKAGE_CATALOGUE": str(previous_root),
        "KICAD_CAPTION_CANDIDATE_CATALOGUE": str(signed_root),
        "KICAD_CAPTION_INSTALLED_ENVELOPE": str(previous_root / "updates/preview.json"),
    })
    solution = "automation/KiCad.Automation.slnx"
    run(["dotnet", "test", solution, "--no-build", "--no-restore", "--configuration", "Debug",
         "--filter", "TestCategory=PublicDownloads", "--logger", "trx;LogFileName=results.trx",
         "--results-directory", str(evidence / "public-download-tests")], environment)
    run(["dotnet", "test", solution, "--no-build", "--no-restore", "--configuration", "Debug",
         "--filter", "TestCategory=DeliveryReceipts", "--logger", "trx;LogFileName=results.trx",
         "--results-directory", str(evidence / "delivery-receipt-tests")], environment)
    run(["dotnet", "test", solution, "--no-build", "--no-restore", "--configuration", "Debug",
         "--filter", "TestCategory=PublicCaptionMcpUpdate|TestCategory=EmptyCaptionUpdate",
         "--logger", "trx;LogFileName=results.trx", "--results-directory",
         str(evidence / "updater-tests"), "--", "MSTest.MapInconclusiveToFailed=true",
         "MSTest.Parallelize.Workers=1"], environment)
    run(["python3", "automation/qualification/observe-linux-preview.py"], environment)
    print(json.dumps({"status": "passed", "commit": request["commit"],
                      "source_sha256": request["source_sha256"],
                      "started_at_ms": started_at_ms, "evidence": str(evidence)}, indent=2))


if __name__ == "__main__":
    main()
