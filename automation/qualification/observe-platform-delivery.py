#!/usr/bin/env python3
"""Run one governed route-only package observation and retain its proof.

The hosted Mac and Windows builds already exist in the public catalogue.  This
check deliberately exercises only the public signed feed and deployment route;
it never starts a native build or an editor process.  The resulting directory
is retained by the surrounding DevCoordinator check and can be passed to
``release deliver-evidence`` after its manifest digest is inspected.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


COMMITS = {
    "osx-arm64": "d6119b2b130bc2b141a082f675e5e2c3786420c1",
    "osx-x64": "d6119b2b130bc2b141a082f675e5e2c3786420c1",
    "win-x64": "e7aacb7d19d03b06169bb2eff0bf47f020c4f058",
}


def fail(message: str) -> None:
    raise SystemExit(message)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=sorted(COMMITS), required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--check", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    expected = COMMITS[args.platform]
    if args.commit != expected:
        fail(f"{args.platform} is frozen to {expected}, received {args.commit}")
    if os.environ.get("KICAD_DELIVERY_ROUTE_ONLY") != "true":
        fail("Route-only delivery evidence requires KICAD_DELIVERY_ROUTE_ONLY=true")
    run_id = os.environ.get("DEVCOORDINATOR_RUN_ID")
    if not run_id:
        fail("The observation must run under DevCoordinator (DEVCOORDINATOR_RUN_ID is missing)")

    root = Path(__file__).resolve().parents[2]
    output = Path(args.evidence)
    if not output.is_absolute():
        output = root / output
    if output.exists():
        # A new governed run must never silently reuse another run's proof.
        shutil.rmtree(output)
    output.mkdir(parents=True)
    tests = output / "tests"
    env = os.environ.copy()
    env.update(
        {
            "KICAD_DELIVERY_PLATFORM": args.platform,
            "KICAD_DELIVERY_COMMIT": args.commit,
            "KICAD_DELIVERY_EVIDENCE": str(output),
        }
    )
    command = [
        "dotnet",
        "test",
        "automation/KiCad.Automation.slnx",
        "--no-build",
        "--no-restore",
        "--configuration",
        "Debug",
        "--filter",
        "TestCategory=DeliveryReceipts",
        "--logger",
        "trx;LogFileName=results.trx",
        "--results-directory",
        str(tests),
    ]
    result = subprocess.run(command, cwd=root, env=env, check=False)
    if result.returncode != 0:
        fail(f"DeliveryReceiptTests failed for {args.platform} with exit code {result.returncode}")

    verification_name = f"{args.platform}.verification.json"
    verification_path = output / verification_name
    route_path = output / "package-route.observation.json"
    if not verification_path.is_file() or not route_path.is_file():
        fail("The delivery test did not produce both package and route observations")
    verification = json.loads(verification_path.read_text())
    route = json.loads(route_path.read_text())
    if verification.get("kind") != "artifact" or verification.get("target") != args.platform:
        fail("The package verification is not bound to the requested platform")
    if verification.get("source_sha256") != route.get("source_sha256"):
        fail("Package and route observations have different source identities")
    if route.get("kind") != "package-route" or route.get("target") != args.platform:
        fail("The route observation is not bound to the requested platform")

    # release.deliver_evidence accepts one bounded verification document.  Keep
    # the route observation beside it for auditability, and expose the package
    # verification under the stable name used in the request.
    delivery = output / "delivery.json"
    delivery.write_text(json.dumps(verification, indent=2) + "\n")
    completion = {
        "schema_version": 1,
        "claim": "preliminary",
        "source_sha256": verification["source_sha256"],
        "capabilities": [
            {
                "id": f"KICAD.DELIVERY.SIGNED_{args.platform.upper().replace('-', '_')}",
                "scope": "product",
                "state": "real_e2e",
                "expected_result": (
                    f"Download the exact signed {args.platform} package through the public HTTPS feed."
                ),
                "evidence_refs": [f"run/{run_id}/{args.check}"],
                "enabled_control": False,
            }
        ],
    }
    (output / "completion.json").write_text(json.dumps(completion, indent=2) + "\n")
    print(
        json.dumps(
            {
                "platform": args.platform,
                "commit": args.commit,
                "run_id": run_id,
                "evidence": str(output),
                "verification_file": "delivery.json",
                "completion_file": "completion.json",
                "source_sha256": verification["source_sha256"],
                "archive": verification["file"],
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
