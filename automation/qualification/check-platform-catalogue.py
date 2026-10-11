"""Check preservation against the actual public baseline before platform delivery."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--previous', type=Path, required=True)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--changed-platform', action='append', default=[])
parser.add_argument('--public-origin')
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()

def inventory(root):
    rows = json.loads((root / 'downloads.json').read_text())['artifacts']
    keyed = {r['fileName']: r for r in rows}
    if len(rows) != len(keyed):
        raise RuntimeError('Duplicate archive names in catalogue')
    return keyed

before, after = inventory(args.previous), inventory(args.candidate)
missing = [name for name, row in before.items() if after.get(name) != row]
if missing:
    raise RuntimeError('Existing downloads were removed or changed: ' + ', '.join(missing[:8]))
if not set(after) - set(before):
    raise RuntimeError('No new package was staged')
feeds = []
for old in sorted((args.previous / 'updates').rglob('*.json')):
    relative = old.relative_to(args.previous)
    new = args.candidate / relative
    if not new.is_file():
        raise RuntimeError('Existing update feed disappeared: ' + str(relative))
    expected_change = str(relative) in {
        'updates/platforms/' + platform + '/preview.json' for platform in args.changed_platform
    }
    if not expected_change and old.read_bytes() != new.read_bytes():
        raise RuntimeError('Unchanged update feed bytes drifted: ' + str(relative))
    feeds.append(str(relative))

if args.public_origin:
    origin = args.public_origin.rstrip('/') + '/'
    for name, row in after.items():
        request = urllib.request.Request(origin + 'artifacts/' + name, method='HEAD')
        with urllib.request.urlopen(request, timeout=30) as response:
            if response.status != 200 or int(response.headers['Content-Length']) != row['bytes']:
                raise RuntimeError('Public archive metadata mismatch: ' + name)
            if response.headers.get('ETag', '').strip('"') != row['sha256']:
                raise RuntimeError('Public archive identity mismatch: ' + name)
    for relative in feeds:
        # Disk feeds use updates/platforms/P; public routing uses platforms/P/updates.
        parts = relative.split('/')
        route = 'platforms/' + parts[2] + '/updates/' + parts[3] if len(parts) == 4 else relative
        with urllib.request.urlopen(origin + route, timeout=30) as response:
            if response.read() != (args.candidate / relative).read_bytes():
                raise RuntimeError('Public feed does not match the verified candidate: ' + route)

args.output.parent.mkdir(parents=True, exist_ok=True)
receipt = dict(previous_archives=len(before), candidate_archives=len(after),
               preserved_archives=len(before), feeds_preserved=feeds,
               public_verified=bool(args.public_origin),
               catalogue_sha256=hashlib.sha256((args.candidate / 'downloads.json').read_bytes()).hexdigest())
args.output.write_text(json.dumps(receipt, indent=2) + '\n')
print(json.dumps(receipt))
