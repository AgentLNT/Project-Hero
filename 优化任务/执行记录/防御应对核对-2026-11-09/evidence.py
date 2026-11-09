from pathlib import Path
import sys,json,hashlib,zipfile,csv,gzip,collections,difflib,xml.etree.ElementTree as E
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
def sha(data):return hashlib.sha256(data).hexdigest()
def write(name,obj):(ROOT/name).write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def snapshot():
 files=[]
 for folder in ('Assets','Packages','ProjectSettings'):
  files.extend(p for p in (REPO/folder).rglob('*') if p.is_file())
 for folder in ('M1-regression','拼刀后摇纠偏-2026-10-29','规则AI阶段三-2026-11-03'):
  files.extend(p for p in (ROOT.parent/folder).rglob('*') if p.is_file())
 files=sorted(set(files));hashes={p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in files}
 with zipfile.ZipFile(ROOT/'before-task.zip','x',zipfile.ZIP_DEFLATED) as z:
  for p in files:z.write(p,p.relative_to(REPO).as_posix())
 write('before-task.json',hashes);preserve();print('Snapshot:',len(files))
def preserve():
 results={}
 for folder in ('正式动作混合AI-2026-11-05','长动作预演覆盖-2026-11-05'):
  previous=ROOT.parent/folder;manifest=json.loads((previous/'sealed-manifest.json').read_text(encoding='utf-8'))
  assert all(sha((previous/p).read_bytes())==h for p,h in manifest.items()),folder
  results[folder]=dict(files=len(manifest),unchanged=True)
 write('previous-evidence-preservation.json',results)
def restore():
 before=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
 def generated(p):
  return p.startswith(('优化任务/执行记录/M1-regression/','优化任务/执行记录/拼刀后摇纠偏-2026-10-29/','优化任务/执行记录/规则AI阶段三-2026-11-03/','Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/')) or p.startswith('Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity') or p=='ProjectSettings/ProjectSettings.asset' or (p.startswith('Assets/Tests/') and p.endswith(('.asset','.asset.meta')))
 changed=[p for p,h in before.items() if generated(p) and (not (REPO/p).exists() or sha((REPO/p).read_bytes())!=h)]
 with zipfile.ZipFile(ROOT/'validation-regenerated-files.zip','x',zipfile.ZIP_DEFLATED) as z:
  for p in changed:
   if (REPO/p).exists():z.write(REPO/p,p)
 with zipfile.ZipFile(ROOT/'before-task.zip') as z:
  for p in changed:
   data=z.read(p);assert sha(data)==before[p];(REPO/p).write_bytes(data)
 assert all(sha((REPO/p).read_bytes())==h for p,h in before.items() if generated(p))
 write('restoration.json',dict(restoredFiles=changed,verifiedFiles=sum(generated(p) for p in before)))
 print('Restored:',len(changed));preserve()
def rows(p):
 opener=gzip.open if p.suffix=='.gz' else open
 with opener(p,'rt',encoding='utf-8-sig',newline='') as f:return list(csv.DictReader(f))
def analyze():
 current=rows(ROOT/'summary.csv');assert len(current)==12
 previous=rows(ROOT.parent/'长动作预演覆盖-2026-11-05'/'summary.csv')
 old={r['case'].removesuffix('-h360'):r for r in previous if r['horizon']=='360'}
 pairs=[]
 for r in current:
  p=old[r['case']];trace=lambda path:[(int(c['tick']),c['controller'],c['key']) for c in rows(path) if c['chosen']=='True']
  a=trace(ROOT/'cases'/(r['case']+'.candidates.csv.gz'))
  b=trace(ROOT.parent/'长动作预演覆盖-2026-11-05'/'cases'/(r['case']+'-h360.candidates.csv.gz'))
  pairs.append(dict(case=r['case'],oldLossPoints=int(p['healthLossQ10'])/1024,newLossPoints=int(r['healthLossQ10'])/1024,
   oldReactions=int(p['reactions']),newReactions=int(r['reactions']),oldSpent=int(p['resourceSpent']),newSpent=int(r['spent']),
   oldCommands=int(p['commands']),newCommands=int(r['commands']),choicesEqual=a==b,
   firstDifferentChoice=next((dict(old=x,new=y) for x,y in zip(b,a) if x!=y),None)))
 responses=rows(ROOT/'responses.csv');assert len(responses)==160
 controls=[r for r in responses if r['response']=='None'];assert len(controls)==40 and all(r['noResponseMatches']=='True' for r in controls)
 grouped={}
 control_by_key={(r['case'],r['checkpoint'],r['action']):int(r['actualAttackDamageQ10']) for r in controls}
 for response in ('None','Guard','Block','Dodge'):
  group=[r for r in responses if r['response']==response]
  grouped[response]=dict(cases=len(group),status=dict(collections.Counter(r['status'] for r in group)),reasons=dict(collections.Counter(r['responseReason'] for r in group)))
  applicable=[(control_by_key[(r['case'],r['checkpoint'],r['action'])],int(r['actualAttackDamageQ10'])) for r in group if r['status']=='ACCEPTED' and control_by_key[(r['case'],r['checkpoint'],r['action'])]>0]
  grouped[response]['acceptedAgainstHittingAttack']=dict(cases=len(applicable),controlDamagePoints=sum(a for a,b in applicable)/1024,
   responseDamagePoints=sum(b for a,b in applicable)/1024,fullyPrevented=sum(b==0 for a,b in applicable),partlyPrevented=sum(0<b<a for a,b in applicable),notReduced=sum(b>=a for a,b in applicable))
 scores=rows(ROOT/'wait-scores.csv');best=[]
 for name in sorted({r['case'] for r in scores}):
  moves=[r for r in scores if r['case']==name and r['type']=='Move']
  if moves:
   m=max(moves,key=lambda r:int(r['score']));best.append(dict(case=name,key=m['key'],score=int(m['score']),positionTerm=int(m['positionTerm']),durationPenalty=int(m['durationPenalty'])))
 for m in best:
  if m['case'].startswith('CautiousDefense-main'):assert (m['score'],m['positionTerm'],m['durationPenalty'])==(-72,128,200)
 report=dict(cases=12,ticks=sum(int(r['ticks']) for r in current),ended=sum(r['ended']=='True' for r in current),
  commandRejections=sum(int(r['rejections']) for r in current),healthLossPoints=sum(int(r['healthLossQ10']) for r in current)/1024,
  oldHealthLossPoints=sum(int(r['healthLossQ10']) for r in old.values())/1024,commands=sum(int(r['commands']) for r in current),
  attacks=sum(int(r['attackStarts']) for r in current),reactions=sum(int(r['reactions']) for r in current),spent=sum(int(r['spent']) for r in current),
  responseBranches=grouped,noResponseMatches=40,bestInitialMoveScores=best,pairs=pairs)
 write('analysis.json',report)
 print(json.dumps({k:v for k,v in report.items() if k not in ('pairs','bestInitialMoveScores')},ensure_ascii=False,indent=2))
def seal():
 analyze();preserve()
 for name,count in (('full-editmode.xml',1147),('production-shadow-playmode.xml',18)):
  t=E.parse(ROOT/name).getroot();assert t.get('failed')=='0' and int(t.get('passed'))==count,(name,t.attrib)
 assert 'cases=12;ticks=10800;' in (ROOT/'native-disk-audit.txt').read_text(encoding='utf-8-sig')
 assert 'cases=12;ticks=10800;' in (ROOT/'native-legacy-audit.txt').read_text(encoding='utf-8-sig')
 before=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
 formal=lambda p:p.startswith(('Assets/Resources/','Assets/Scenes/')) or p=='Assets/Scripts/Logic/AI/AiUtilityProfile.cs'
 for p,h in before.items():
  if formal(p):assert sha((REPO/p).read_bytes())==h,p
 actual={p.relative_to(REPO).as_posix() for folder in ('Assets/Resources','Assets/Scenes') for p in (REPO/folder).rglob('*') if p.is_file()}
 assert actual=={p for p in before if p.startswith(('Assets/Resources/','Assets/Scenes/'))}
 sources=sorted(p for folder in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/folder).rglob('*.cs'))
 for name in ('full-editmode.xml','production-shadow-playmode.xml'):
  assert max(p.stat().st_mtime for p in sources)<=(ROOT/name).stat().st_mtime,'Source changed after '+name
 hashes={p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in sources}
 fp=sha(''.join(p+':'+h+'\n' for p,h in sorted(hashes.items())).encode())
 inputs=[json.loads(p.read_text(encoding='utf-8-sig')) for p in (ROOT/'cases').glob('*.input.json')]
 assert len(inputs)==12 and all(i['sourceFingerprint']==fp for i in inputs)
 anchors=json.loads((ROOT/'hash-anchors.json').read_text(encoding='utf-8'))
 assert anchors['definitionHash']=='84116d595d222a72'
 assert all(i['sourceHash']==anchors['definitionHash'] and i['protocol']=='defense-audit-v2' for i in inputs)
 write('frozen-final-source.json',dict(fingerprint=fp,files=hashes))
 files=set(sources)
 files.update(p.with_name(p.name+'.meta') for p in sources)
 for folder in ('Assets/Resources','Assets/Scenes'):
  files.update(p for p in (REPO/folder).rglob('*') if p.is_file())
 for folder in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics'):
  for p in (REPO/folder).rglob('*.asmdef'):files.update((p,p.with_name(p.name+'.meta')))
 files.update(REPO/p for p in ('Packages/manifest.json','Packages/packages-lock.json','ProjectSettings/ProjectVersion.txt'))
 with zipfile.ZipFile(ROOT/'source-and-content.zip','x',zipfile.ZIP_DEFLATED) as z:
  for p in sorted(files):assert p.is_file(),p;z.write(p,p.relative_to(REPO).as_posix())
 diff=[];changed=[]
 with zipfile.ZipFile(ROOT/'before-task.zip') as z:
  for p,h in hashes.items():
   if before.get(p)==h:continue
   changed.append(p);old=z.read(p).decode('utf-8-sig').splitlines() if p in before else []
   diff.extend(difflib.unified_diff(old,(REPO/p).read_text(encoding='utf-8-sig').splitlines(),fromfile=p+' (before)' if p in before else '/dev/null',tofile=p,lineterm=''))
 (ROOT/'task-diff.patch').write_text('\n'.join(diff)+'\n',encoding='utf-8')
 write('provenance.json',dict(sourceFingerprint=fp,sourceFiles=len(hashes),changedSource=changed,sourceDefinitionHash=inputs[0]['sourceHash'],
  contentFingerprint=inputs[0]['contentFingerprint'],archiveFiles=len(files),formalContentAndAiWeightsUnchanged=True))
 manifest={p.relative_to(ROOT).as_posix():sha(p.read_bytes()) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name!='sealed-manifest.json'}
 write('sealed-manifest.json',manifest);print('Sealed:',len(manifest),'Source:',fp)
if __name__=='__main__':globals()[sys.argv[1]]()
