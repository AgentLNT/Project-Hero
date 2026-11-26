from pathlib import Path
import hashlib,json,zipfile,shutil
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
OLD=ROOT.parent/'短组合规划-2026-11-13'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def save(p,x): p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
frozen=json.loads((OLD/'frozen-final-source.json').read_text(encoding='utf-8'))
assert all(sha(REPO/p)==h for p,h in frozen['files'].items())
protected={}
for name in ('短组合规划-2026-11-13','温度采样与磨血防御-2026-11-13'):
    folder=ROOT.parent/name
    manifest=json.loads((folder/'sealed-manifest.json').read_text(encoding='utf-8'))
    for p,h in manifest.items():
        assert sha(folder/p)==h,(name,p)
        protected[(folder/p).relative_to(REPO).as_posix()]=h
    protected[(folder/'sealed-manifest.json').relative_to(REPO).as_posix()]=sha(folder/'sealed-manifest.json')
save(ROOT/'before-protected.json',protected)
content=json.loads((ROOT.parent/'偏好互打长局-2026-11-11/before-content.json').read_text(encoding='utf-8-sig'))
paths=set(frozen['files'])|set(content)
paths|={p+'.meta' for p in list(paths) if (REPO/(p+'.meta')).is_file()}
with zipfile.ZipFile(ROOT/'before-source.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in sorted(paths): z.write(REPO/p,p)
save(ROOT/'before-source.json',{p:sha(REPO/p) for p in sorted(paths)})
shutil.copytree(OLD/'harness',ROOT/'harness',ignore=shutil.ignore_patterns('build-manifest.json'),dirs_exist_ok=True)
print('snapshot',len(paths),'protected sealed files',len(protected))
