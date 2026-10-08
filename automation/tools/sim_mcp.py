from pathlib import Path
import hashlib,json

def row(path):
    p=Path(path); h=hashlib.sha256()
    with p.open('rb') as f:
        for chunk in iter(lambda:f.read(1048576),b''): h.update(chunk)
    return {'path':str(p),'bytes':p.stat().st_size,'sha256':h.hexdigest()}

assemblies=[row('automation/'+p) for p in ('src/KiCad.Automation.Mcp/bin/Debug/net10.0/kicad-mcp.dll','tests/KiCad.Automation.Tests/bin/Debug/net10.0/KiCad.Automation.Tests.dll')]
native=[row('automation/artifacts/native/'+p) for p in ('kicad/kicad','eeschema/_eeschema.kiface','pcbnew/_pcbnew.kiface','cvpcb/_cvpcb.kiface','kicad/kicad-cli')]
managed=[row(p) for p in sorted(Path('automation').rglob('*.cs')) if not {'bin','obj','artifacts'}.intersection(p.parts)]
result={'nativeBuild':'reused','nativeBinaries':native,'managedSourceSha256':hashlib.sha256(json.dumps(managed,sort_keys=True,separators=(',',':')).encode()).hexdigest(),'managedSources':managed,'managedAssemblies':assemblies}
p=Path('automation/artifacts/simulation-mcp-identity');p.mkdir(parents=True,exist_ok=True)
(p/'identity.json').write_text(json.dumps(result,sort_keys=True,indent=2)+'\n')
print(json.dumps({'nativeBuild':'reused','nativeBinaryCount':len(native),'managedSourceSha256':result['managedSourceSha256']}))
