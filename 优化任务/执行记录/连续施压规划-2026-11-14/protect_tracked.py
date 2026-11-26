from pathlib import Path
import hashlib,json,subprocess,zipfile
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
protected=json.loads((ROOT/'before-protected.json').read_text(encoding='utf-8'))
tracked=subprocess.check_output(['git','ls-files','-z'],cwd=REPO).decode('utf-8').split('\0')
extra=[p for p in tracked if p and p.startswith(('优化任务/执行记录/','Assets/Diagnostics/','Assets/Tests/')) and (REPO/p).is_file() and p not in protected]
with zipfile.ZipFile(ROOT/'before-tracked.zip','w',zipfile.ZIP_DEFLATED) as z:
    for p in extra:
        protected[p]=hashlib.sha256((REPO/p).read_bytes()).hexdigest()
        z.write(REPO/p,p)
(ROOT/'before-protected.json').write_text(json.dumps(protected,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('protected',len(protected))
