"""Retain verified download observations and the preliminary capability inventory."""
import json
import os
from pathlib import Path
import subprocess
import time

started_at_ms = int(time.time() * 1000)
output = Path(os.environ['KICAD_DELIVERY_EVIDENCE'])
run_id = 'run/' + os.environ['DEVCOORDINATOR_RUN_ID']
public_journey = 'journey/w63a10cb0cbea46d1/t20260929T220328Z-676b0d'
subprocess.run([
    'dotnet', 'test', 'automation/KiCad.Automation.slnx', '--no-build', '--no-restore',
    '--configuration', 'Debug', '--filter', 'TestCategory=DeliveryReceipts',
    '--logger', 'trx;LogFileName=results.trx', '--results-directory',
    'automation/artifacts/preview25-delivery-tests'
], check=True)
observations = [json.loads((output / name).read_text())
                for name in ['linux.verification.json', 'web.verification.json']]
assert all(o['checked_at_ms'] >= started_at_ms for o in observations), 'Fresh observations are required.'
assert observations[0]['source_sha256'] == observations[1]['source_sha256']
source = observations[0]['source_sha256']
recovery = 'journey/w63a10cb0cbea46d1/t20260929T210523Z-4f48f2/project-recovery/project-recovery-evidence'
startup = 'journey/w63a10cb0cbea46d1/t20260929T213022Z-da8b2c/native-startup-refusals/native-startup-failure-evidence'
capabilities = []

def capability(identity, result, state='real_e2e', evidence=(), task=None, rendered=()):
    row = dict(id=identity, scope='product', state=state, expected_result=result,
               evidence_refs=list(evidence), enabled_control=bool(rendered),
               rendered_evidence_refs=list(rendered))
    if task is not None:
        row['task_id'] = task
    capabilities.append(row)

capability('KICAD.RECOVERY.PROJECT_CREATE', 'Recreate a missing project container without replacing an existing file.', evidence=[recovery])
capability('KICAD.RECOVERY.PROJECT_START', 'Restart the exact registered instance and rebuild the connected schematic from settled XML.', evidence=[recovery, startup])
capability('KICAD.RECOVERY.TYPED_SETTINGS', 'Restore captured schematic settings, identities, connections and saved file bytes.', evidence=[recovery])
capability('KICAD.RECOVERY.HISTORY', 'Undo and redo a rebuilt schematic with exact captured state.', evidence=[recovery])
capability('KICAD.DELIVERY.LINUX_UPDATE', 'Download and install the signed Linux preview through the native Update button while preserving open projects.', evidence=[run_id + '/observations', public_journey + '/rendered'], rendered=[public_journey + '/rendered/public-caption-evidence'])
capability('KICAD.DELIVERY.DOWNLOAD_PAGE', 'Choose a platform and download its published archive from the existing download page.', evidence=[run_id + '/observations', public_journey + '/browser', public_journey + '/landing'], rendered=[public_journey + '/browser/download-browser', public_journey + '/landing'])
capability('KICAD.RECOVERY.REPEATED_SHEETS', 'Rebuild XML-bound repeated and shared physical sheets with complete native proof.', state='deferred', task='p195ade3ceaabb5f4')
capability('KICAD.RECOVERY.PROJECT_SETTINGS_REMAINDER', 'Capture the remaining project settings outside the currently typed schematic groups.', state='deferred', task='p7ef37236419d4689')
capability('KICAD.DIAGRAM.APPROVED_VISUALS', 'Complete the native diagram editor audit against the selected targets and owner decisions.', state='visual_only', task='p8c2081361b925bf8')
(output / 'completion.json').write_text(json.dumps(dict(schema_version=1, claim='preliminary', source_sha256=source, capabilities=capabilities), indent=2) + '\n')
print(json.dumps(dict(completion_claim='preliminary', capabilities=len(capabilities), source_sha256=source)))
