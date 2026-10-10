"""Bind sequence 27 delivery receipts to a bounded completion inventory."""
import json
import os
from pathlib import Path

COMMIT = "092cdcecc0df3a879cf8cd2564643071fbc4ecf5"
VERSION = "preview-20261010T094312Z-092cdcecc0df"
ARCHIVE = "kicad-codex-preview-20261010T094312Z-092cdcecc0df-debian13-x64.tar.gz"
ARCHIVE_SHA256 = "f136cc13d03f02c217475235a083e1e204e6d7fde96e1c7445d681413ef4d10f"
SOURCE_ARCHIVE_SHA256 = "dfc987073e37ad2315e6eba7d9275274d4a3e54e23374394024b95149aa15857"
ORIGIN = "https://kicad.vr.ae/"


def fail(message):
    raise RuntimeError(message)


def required(name, expected=None):
    value = os.environ.get(name)
    if not value:
        fail("Missing delivery observation input: " + name)
    if expected is not None and value != expected:
        fail(f"{name} changed: {value!r}")
    return value


def load(path):
    try:
        return json.loads(path.read_text())
    except (FileNotFoundError, json.JSONDecodeError) as error:
        fail(f"Cannot read retained delivery evidence {path}: {error}")


def require_fields(value, expected, label):
    for key, wanted in expected.items():
        if value.get(key) != wanted:
            fail(f"{label} mismatch for {key}: {value.get(key)!r}")


root = Path(__file__).resolve().parents[2]
raw_output = Path(required("KICAD_DELIVERY_EVIDENCE"))
output = raw_output if raw_output.is_absolute() else root / raw_output
run_id = "run/" + required("DEVCOORDINATOR_RUN_ID")
required("KICAD_DELIVERY_COMMIT", COMMIT)
required("KICAD_DELIVERY_PLATFORM", "linux-x64")
output.mkdir(parents=True, exist_ok=True)

linux = load(output / "linux.verification.json")
require_fields(linux, {
    "version": 1,
    "kind": "artifact",
    "target": "linux-x64",
    "file": ARCHIVE,
    "observed_sha256": ARCHIVE_SHA256,
    "access": ORIGIN + "artifacts/" + ARCHIVE,
    "observation": "download_matched",
}, "Linux verification")
if not isinstance(linux.get("source_sha256"), str) or len(linux["source_sha256"]) != 64:
    fail("Linux verification must bind a 64-character source digest")
if not isinstance(linux.get("checked_at_ms"), int) or linux["checked_at_ms"] <= 0:
    fail("Linux verification must retain its observation timestamp")

route = load(output / "package-route.observation.json")
require_fields(route, {
    "version": 1,
    "kind": "package-route",
    "target": "linux-x64",
    "file": "index.html",
    "access": ORIGIN,
    "observation": "package_route_passed",
}, "Package route observation")
if not isinstance(route.get("observed_sha256"), str) or len(route["observed_sha256"]) != 64:
    fail("Package route observation must bind the returned HTML digest")
if not isinstance(route.get("checked_at_ms"), int) or route["checked_at_ms"] <= 0:
    fail("Package route observation must retain its observation timestamp")

verification = output / "Verification"
completion = output / "completion.json"
if verification.exists() or completion.exists():
    fail("Fresh delivery evidence is required; completion.json or Verification already exists")
verification.write_text(json.dumps(linux, indent=2) + "\n")
capabilities = [
    {
        "id": "KICAD.DELIVERY.SIGNED_LINUX_DOWNLOAD",
        "scope": "product",
        "state": "real_e2e",
        "expected_result": "Download the exact signed Linux sequence 27 archive through the public HTTPS route.",
        "evidence_refs": [run_id + "/signed-linux-download", run_id + "/delivery-receipts"],
        "enabled_control": False,
    },
    {
        "id": "KICAD.DELIVERY.PUBLIC_PACKAGE_ROUTE",
        "scope": "product",
        "state": "real_e2e",
        "expected_result": "Serve the public download route while retaining its exact deployment observation.",
        "evidence_refs": [run_id + "/delivery-receipts", run_id + "/observe"],
        "enabled_control": False,
    },
    {
        "id": "KICAD.DELIVERY.LINUX_UPDATE",
        "scope": "product",
        "state": "deferred",
        "expected_result": "Verify the native Update journey against sequence 27 while preserving open projects.",
        "evidence_refs": [],
        "task_id": "p2e4103cde445d5aa",
        "enabled_control": False,
    },
    {
        "id": "KICAD.RECOVERY.REPEATED_SHEETS",
        "scope": "product",
        "state": "deferred",
        "expected_result": "Rebuild XML-bound repeated and shared physical sheets with complete native proof.",
        "evidence_refs": [],
        "task_id": "p195ade3ceaabb5f4",
        "enabled_control": False,
    },
    {
        "id": "KICAD.RECOVERY.PROJECT_SETTINGS_REMAINDER",
        "scope": "product",
        "state": "deferred",
        "expected_result": "Capture the remaining project settings outside the currently typed schematic groups.",
        "evidence_refs": [],
        "task_id": "p7ef37236419d4689",
        "enabled_control": False,
    },
    {
        "id": "KICAD.DIAGRAM.APPROVED_VISUALS",
        "scope": "product",
        "state": "visual_only",
        "expected_result": "Complete the native diagram editor audit against the selected targets and owner decisions.",
        "evidence_refs": [],
        "task_id": "p8c2081361b925bf8",
        "enabled_control": False,
    },
]
completion.write_text(json.dumps({
    "schema_version": 1,
    "claim": "preliminary",
    "source_sha256": linux["source_sha256"],
    "source_archive_sha256": SOURCE_ARCHIVE_SHA256,
    "release": {"sequence": 27, "version": VERSION, "commit": COMMIT},
    "artifacts": {"archive": ARCHIVE, "sha256": ARCHIVE_SHA256},
    "verification_file": "Verification",
    "capabilities": capabilities,
}, indent=2) + "\n")
print(json.dumps({
    "completion_claim": "preliminary",
    "sequence": 27,
    "verification_file": str(verification),
    "capabilities": len(capabilities),
    "source_sha256": linux["source_sha256"],
}))
