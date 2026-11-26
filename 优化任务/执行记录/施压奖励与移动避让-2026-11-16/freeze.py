from pathlib import Path
import hashlib,json,zipfile,difflib
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
files=sorted(p.relative_to(REPO).as_posix() for folder in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/folder).rglob('*.cs'))
hashes={p:sha(REPO/p) for p in files}
fingerprint=hashlib.sha256(''.join(f'{p}:{h}\n' for p,h in hashes.items()).encode()).hexdigest()
(ROOT/'frozen-final-source.json').write_text(json.dumps({'fingerprint':fingerprint,'files':hashes},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
before=json.loads((ROOT/'before-source.json').read_text(encoding='utf-8'))
content=json.loads((ROOT.parent/'偏好互打长局-2026-11-11/before-content.json').read_text(encoding='utf-8-sig'))
assert all(sha(REPO/p)==h for p,h in content.items())
paths=set(files)|set(content)
paths|={p+'.meta' for p in list(paths) if (REPO/(p+'.meta')).is_file()}
with zipfile.ZipFile(ROOT/'source-and-content.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in sorted(paths):z.write(REPO/p,p)
with zipfile.ZipFile(ROOT/'before-source.zip') as z:
    patch=[]
    for p in files:
        if before.get(p)==hashes[p]:continue
        old=z.read(p).decode('utf-8-sig').splitlines(keepends=True) if p in before else []
        patch+=difflib.unified_diff(old,(REPO/p).read_text(encoding='utf-8-sig').splitlines(keepends=True),fromfile='before/'+p,tofile='after/'+p)
    (ROOT/'implementation.patch').write_text(''.join(patch),encoding='utf-8')
print('frozen',len(files),fingerprint)
