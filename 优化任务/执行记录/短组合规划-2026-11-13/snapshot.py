from pathlib import Path
import concurrent.futures,hashlib,json,subprocess,zipfile
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def save(name,data):(ROOT/name).write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
source=sorted(p for directory in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/directory).rglob('*.cs'))
frozen=json.loads((ROOT.parent/'温度采样与磨血防御-2026-11-13/frozen-final-source.json').read_text(encoding='utf-8'))
assert all(sha(REPO/p)==h for p,h in frozen['files'].items()),'Task22 source has changed since the preceding turn'
content=json.loads((ROOT.parent/'偏好互打长局-2026-11-11/before-content.json').read_text(encoding='utf-8-sig'))
files=set(p.relative_to(REPO).as_posix() for p in source)|set(content)
files|={p+'.meta' for p in list(files) if (REPO/(p+'.meta')).is_file()}
with zipfile.ZipFile(ROOT/'before-source.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in sorted(files):z.write(REPO/p,p)
save('before-source.json',{p:sha(REPO/p) for p in sorted(files)})
tracked=subprocess.check_output(['git','ls-files','-z'],cwd=REPO).decode('utf-8').split('\0')
protected=[p for p in tracked if p and (p.startswith('优化任务/执行记录/') or p.startswith('Assets/Diagnostics/') or p.startswith('Assets/Tests/')) and (REPO/p).is_file()]
old=ROOT.parent/'温度采样与磨血防御-2026-11-13'
protected+=['优化任务/执行记录/温度采样与磨血防御-2026-11-13/'+p for p in json.loads((old/'sealed-manifest.json').read_text(encoding='utf-8'))]
protected+=['优化任务/执行记录/温度采样与磨血防御-2026-11-13/sealed-manifest.json']
with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    protected_hashes=dict(zip(protected,pool.map(lambda p:sha(REPO/p),protected)))
save('before-protected.json',protected_hashes)
print('sourceFiles',len(source),'protectedFiles',len(protected_hashes))
