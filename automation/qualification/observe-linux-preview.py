"""Emit strict completion and artifact proof for one fresh qualification run."""
from __future__ import annotations
import hashlib, json, os, subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def load(p: Path) -> dict: return json.loads(p.read_text())


def sha(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for b in iter(lambda: f.read(1024 * 1024), b""): h.update(b)
    return h.hexdigest()


def fields(value: dict, expected: dict, label: str) -> None:
    for key, wanted in expected.items():
        if value.get(key) != wanted: raise RuntimeError(f"{label} mismatch for {key}: {value.get(key)!r}")


def main() -> None:
    req_path = Path(os.environ.get("KICAD_PREVIEW_REQUEST", "/mnt/build-storage/codex/kicad/delivery/preview28/request.json"))
    req = load(req_path)
    evidence = Path(os.environ.get("KICAD_DELIVERY_EVIDENCE", ROOT / "automation/artifacts/preview28-linux-delivery")).resolve()
    run_id = os.environ["DEVCOORDINATOR_RUN_ID"]
    identity = load(evidence / "run-identity.json")
    fields(identity, {"run_id": run_id, "package_commit": req["commit"], "request_sha256": sha(req_path)}, "identity")
    executor = os.environ.get("KICAD_COORDINATOR_EXECUTOR", "/home/DevCoordinator2/target/release/devcoordinator2-executor")
    current_source = json.loads(subprocess.check_output([executor, "source-digest", "--worktree", str(ROOT)], text=True))["sha256"]
    if current_source != identity["source_sha256"]: raise RuntimeError("Verification source changed during run")
    suites = load(evidence / "suite-results.json")
    if {x.get("suite") for x in suites["suites"]} != {"tls-preflight", "public-download-tests", "delivery-receipt-tests", "updater-tests"} or any(x.get("status") != "passed" for x in suites["suites"]):
        raise RuntimeError("Every independent suite must pass")
    catalogue = load(Path(req["signed_root"]) / "downloads.json")
    artifacts = catalogue["artifacts"]
    linux = [x for x in artifacts if x.get("platform") == "linux-x64" and x.get("commit") == req["commit"]]
    source = [x for x in artifacts if x.get("platform") == "source" and x.get("commit") == req["commit"]]
    if len(linux) != 1 or len(source) != 1 or linux[0] != req["linux_artifact"] or source[0] != req["source_artifact"]: raise RuntimeError("Signed catalogue differs from frozen package")
    app = linux[0]
    archive = evidence / app["fileName"]
    if archive.stat().st_size != app["bytes"] or sha(archive) != app["sha256"]: raise RuntimeError("Retained archive hash mismatch")
    lv = load(evidence / "linux.verification.json"); route = load(evidence / "package-route.observation.json")
    fields(lv, {"version":1,"kind":"artifact","target":"linux-x64","source_sha256":identity["source_sha256"],"file":app["fileName"],"observed_sha256":app["sha256"],"access":req["public_url"].rstrip("/")+"/artifacts/"+app["fileName"],"observation":"download_matched"}, "linux")
    fields(route, {"version":1,"kind":"package-route","target":"linux-x64","source_sha256":identity["source_sha256"],"file":"index.html","access":req["public_url"],"observation":"package_route_passed"}, "route")
    fields(route["deployment"], {"deployment_id":req["deployment_id"],"http_status":200}, "deployment")
    if not isinstance(route["deployment"].get("generation_number"), int) or route["deployment"]["generation_number"] < 1: raise RuntimeError("Missing deployment generation")
    if sha(evidence / "index.html") != route["observed_sha256"]: raise RuntimeError("Route response hash mismatch")
    native = evidence / "updater-native"
    if not native.is_dir() or not any(native.rglob("*.png")): raise RuntimeError("Updater screenshots missing")
    (evidence / "Verification").write_text(json.dumps(lv, indent=2)+"\n")
    ref = "run/" + run_id + "/public-and-updater/"
    completion = {"schema_version":1,"claim":"preliminary","source_sha256":identity["source_sha256"],"capabilities":[
        {"id":"KICAD.DELIVERY.SIGNED_LINUX_DOWNLOAD","scope":"product","state":"real_e2e","expected_result":"Download the unchanged signed Linux archive through public HTTPS.","evidence_refs":[ref+"public-download-tests",ref+"delivery-receipt-tests"],"enabled_control":False},
        {"id":"KICAD.DELIVERY.LINUX_UPDATE","scope":"product","state":"real_e2e","expected_result":"Install the signed Linux update while preserving both open designs.","evidence_refs":[ref+"updater-tests"],"enabled_control":True,"rendered_evidence_refs":[ref+"updater-native"]},
        {"id":"KICAD.DELIVERY.PUBLIC_PACKAGE_ROUTE","scope":"product","state":"real_e2e","expected_result":"Reach the desktop package through the healthy public download route.","evidence_refs":[ref+"delivery-receipt-tests"],"enabled_control":False}]}
    (evidence / "completion.json").write_text(json.dumps(completion, indent=2)+"\n")
    (evidence / "release-metadata.json").write_text(json.dumps({"sequence":req["sequence"],"version":req["version"],"package_commit":req["commit"],"package_source_sha256":req["source_sha256"],"source_archive_sha256":req["source_archive_sha256"],"verification_commit":identity["commit"],"verification_source_sha256":identity["source_sha256"],"archive":app["fileName"],"archive_sha256":app["sha256"],"verification_file":"Verification","download_page_source_outcome":"pb280780a9ff12740"}, indent=2)+"\n")
    result = subprocess.run(["devcoordinator2","completion","check","--file",str(evidence/"completion.json"),str(ROOT)], text=True, capture_output=True, check=False)
    receipt = json.loads(result.stdout) if result.stdout else {}
    (evidence / "completion-check.json").write_text(json.dumps(receipt, indent=2)+"\n")
    if result.returncode != 0 or not receipt.get("ok") or receipt.get("data",{}).get("valid") is not True: raise RuntimeError("Completion inventory rejected")


if __name__ == "__main__": main()
