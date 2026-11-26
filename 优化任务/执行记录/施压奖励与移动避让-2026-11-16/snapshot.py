from pathlib import Path
import hashlib, json, shutil, zipfile, subprocess
root=Path(__file__).resolve().parent
repo=root.parents[2]
prior=root.parent/'连续施压规划-2026-11-14'
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
assert not (root/'before-source.json').exists()
frozen=load(prior/'frozen-final-source.json')['files']
assert all(digest(repo/p)==h for p,h in frozen.items())
content=load(root.parent/'偏好互打长局-2026-11-11/before-content.json')
assert all(digest(repo/p)==h for p,h in content.items())
paths=set(frozen)|set(content)
paths|={p+'.meta' for p in paths if (repo/(p+'.meta')).is_file()}
paths.add('ProjectSettings/ProjectSettings.asset')
with zipfile.ZipFile(root/'before-source.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in sorted(paths):z.write(repo/p,p)
(root/'before-source.json').write_text(json.dumps({p:digest(repo/p) for p in sorted(paths)},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
tracked=subprocess.check_output(['git','ls-files','-z'],cwd=repo).decode().split('\0')
prefixes=('优化任务/执行记录/M1-regression/','Assets/Diagnostics/')
state=[p for p in tracked if p.startswith(prefixes) and (repo/p).is_file()]
with zipfile.ZipFile(root/'prior-test-artifacts.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in state:z.write(repo/p,p)
for name in ('harness/FormalAiSourceExport.cs','harness/Shims.cs','harness/build.ps1','freeze.py'):
    (root/name).parent.mkdir(exist_ok=True,parents=True)
    shutil.copyfile(prior/name,root/name)
print('Verified and saved',len(paths),'source/content paths and',len(state),'test artifacts')
