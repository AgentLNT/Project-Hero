from pathlib import Path
import hashlib, json, subprocess, sys, zipfile, csv, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]
PREVIOUS = ROOT.parent / '进攻防御评价-2026-11-09'
def sha(data): return hashlib.sha256(data).hexdigest()
def write(name, value): (ROOT/name).write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
def generated(p):
    return p.startswith(('优化任务/执行记录/M1-regression/', '优化任务/执行记录/拼刀后摇纠偏-2026-10-29/',
        '优化任务/执行记录/规则AI阶段三-2026-11-03/', 'Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/',
        'Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity')) or p == 'ProjectSettings/ProjectSettings.asset' or (
        p.startswith('Assets/Tests/') and p.endswith(('.asset', '.asset.meta')))
def preserve():
    m=json.loads((PREVIOUS/'sealed-manifest.json').read_text(encoding='utf-8'))
    assert all(sha((PREVIOUS/p).read_bytes())==h for p,h in m.items())
    write('previous-evidence-preservation.json', dict(previous=str(PREVIOUS), files=len(m), unchanged=True))
def snapshot():
    tracked=subprocess.check_output(['git','ls-files','-z'],cwd=REPO).decode('utf-8').split('\0')
    files={p for p in tracked if p and generated(p) and (REPO/p).is_file()}
    files.update(p.relative_to(REPO).as_posix() for d in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/d).rglob('*.cs'))
    with zipfile.ZipFile(ROOT/'before-task.zip','x',zipfile.ZIP_DEFLATED) as z:
        for p in sorted(files): z.write(REPO/p,p)
    write('before-task.json',{p:sha((REPO/p).read_bytes()) for p in sorted(files)})
    protected={p for p in tracked if p.startswith(('Assets/Resources/','Packages/')) or p=='Assets/Scenes/CombatSampleScene.unity'}
    protected.add('Assets/Scripts/Logic/AI/AiUtilityProfile.cs')
    write('protected-content.json',{p:sha((REPO/p).read_bytes()) for p in sorted(protected)})
    write('origin.json',dict(commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO).decode().strip(),
        status=subprocess.check_output(['git','-c','core.quotepath=false','status','--short'],cwd=REPO).decode('utf-8'),
        date='2026-11-11',note='Task 19 changes already present and preserved; baseline is current worktree, not HEAD.'))
    preserve(); print('Snapshotted',len(files),'current-worktree files; protected',len(protected))
def restore():
    before=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
    changed=[p for p,h in before.items() if generated(p) and (not (REPO/p).is_file() or sha((REPO/p).read_bytes())!=h)]
    with zipfile.ZipFile(ROOT/'validation-regenerated-files.zip','x',zipfile.ZIP_DEFLATED) as z:
        for p in changed:
            if (REPO/p).is_file(): z.write(REPO/p,p)
    with zipfile.ZipFile(ROOT/'before-task.zip') as z:
        for p in changed:
            target=(REPO/p).resolve(); target.relative_to(REPO.resolve()); target.write_bytes(z.read(p))
    assert all(sha((REPO/p).read_bytes())==h for p,h in before.items() if generated(p))
    write('restoration.json',dict(restoredFiles=changed,verifiedFiles=sum(generated(p) for p in before)))
    preserve(); print('Restored',len(changed))
def analyze():
    def rows(name):
        with (ROOT/name).open(encoding='utf-8-sig',newline='') as f: return list(csv.DictReader(f))
    current=rows('summary.csv'); old={r['case']:r for r in rows('baseline-summary.csv')}
    tests={}
    for name in ('targeted-final.xml','full-editmode.xml','production-shadow-playmode.xml'):
        node=ET.parse(ROOT/name).getroot()
        tests[name]={k:node.attrib.get(k) for k in ('total','passed','failed','skipped','result')}
        assert node.attrib['result']=='Passed',name
    cases=[]
    for r in current:
        b=old[r['case']]; assert r['ticks']==b['ticks']; assert r['rejections']=='0'
        if r['case'].startswith('main-'):
            assert int(r['goalMoves'])>0 and int(r['minimumDistance'])<int(r['initialDistance']) and b['moves']=='0'
        cases.append(dict(case=r['case'],ticks=int(r['ticks']),oldCommands=int(b['commands']),newCommands=int(r['commands']),
            oldMoves=int(b['moves']),newMoves=int(r['moves']),goalMoves=int(r['goalMoves']),attacks=int(r['attacks']),
            initialDistance=int(r['initialDistance']),minimumDistance=int(r['minimumDistance']),finalDistance=int(r['finalDistance']),
            oldHealthLossQ10=int(b['healthLossQ10']),newHealthLossQ10=int(r['healthLossQ10'])))
    result=dict(tests=tests,cases=cases,totalTicks=sum(r['ticks'] for r in cases),naturallyEnded=sum(r['ended']=='True' for r in current))
    write('analysis.json',result); print(json.dumps(result,ensure_ascii=False,indent=2))
def seal():
    preserve(); protected=json.loads((ROOT/'protected-content.json').read_text(encoding='utf-8'))
    assert all(sha((REPO/p).read_bytes())==h for p,h in protected.items())
    files=sorted((p for d in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/d).rglob('*.cs')),key=lambda p:p.relative_to(REPO).as_posix())
    hashes={p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in files}
    fp=sha(''.join(p+':'+h+'\n' for p,h in hashes.items()).encode())
    assert all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint']==fp for p in (ROOT/'cases').glob('*.input.json'))
    write('frozen-final-source.json',dict(fingerprint=fp,files=hashes))
    with zipfile.ZipFile(ROOT/'source-and-content.zip','w',zipfile.ZIP_DEFLATED) as z:
        for p in sorted(set(hashes)|set(protected)): z.write(REPO/p,p)
    write('sealed-manifest.json',{p.relative_to(ROOT).as_posix():sha(p.read_bytes()) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name!='sealed-manifest.json'})
    print('Sealed',fp)
if __name__=='__main__': {'snapshot':snapshot,'restore':restore,'analyze':analyze,'seal':seal}[sys.argv[1]]()
