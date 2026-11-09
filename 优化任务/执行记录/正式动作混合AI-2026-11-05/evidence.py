from pathlib import Path
import csv,json,collections,hashlib,zipfile,sys,difflib
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
def sha(data):return hashlib.sha256(data).hexdigest()
def write(name,obj):(ROOT/name).write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def rows(p):
 with p.open(encoding='utf-8-sig',newline='') as f:return list(csv.DictReader(f))
def analyze():
 data=rows(ROOT/'summary.csv');assert len(data)==24
 pairs=collections.defaultdict(dict)
 for row in data:pairs[(row['policy'],row['near'],row['mirror'])][row['execution']]=row
 comparisons=[]
 for (policy,near,mirror),pair in pairs.items():
  a,b=pair['SingleCommand'],pair['UtilityBehaviorTree']
  def choices(row):return sorted((int(c['tick']),c['controller'],c['key']) for c in rows(ROOT/'cases'/(row['case']+'.candidates.csv')) if c['chosen']=='True')
  ca,cb=choices(a),choices(b)
  comparisons.append(dict(policy=policy,near=near,mirror=mirror,healthBefore=a['health'],healthAfter=b['health'],outcomeBefore=a['outcome'],outcomeAfter=b['outcome'],ticksBefore=int(a['ticks']),ticksAfter=int(b['ticks']),commandsBefore=int(a['commands']),commandsAfter=int(b['commands']),chosenCommandsMatch=ca==cb,chosenBefore=len(ca),chosenAfter=len(cb),changedFields=[k for k in ('health','outcome','ticks','commands','attackStarts','moves','reactions') if a[k]!=b[k]]))
 chosen=collections.Counter();offered=set();unsupported=set();failures=collections.Counter();selectedByMode=collections.Counter()
 for row in data:
  mode=row['execution']
  for candidate in rows(ROOT/'cases'/(row['case']+'.candidates.csv')):
   if candidate['type']=='Attack':offered.add(candidate['key'].split('/')[2])
   if candidate['chosen']=='True':
    chosen[(mode,candidate['key'].split('/')[2])]+=1;selectedByMode[mode]+=1
  for d in rows(ROOT/'cases'/(row['case']+'.decisions.csv')):
   if d['unsupported']:unsupported.add(d['unsupported'])
   for channel in ('ordinary','reaction'):
    if d[channel+'Status']=='Failure' and d[channel+'Reason'] not in ('IDLE',''):
     failures[d[channel+'Reason']]+=1
 stats=dict(cases=len(data),recordedTicks=sum(int(d['ticks']) for d in data),commandRejections=sum(int(d['rejections']) for d in data),pairedComparisons=len(pairs),chosenCommandsChangedPairs=sum(not x['chosenCommandsMatch'] for x in comparisons),truncatedAt900=sum(int(d['ticks'])==900 and d['outcome']=='' for d in data),healthChangedPairs=sum(x['healthBefore']!=x['healthAfter'] for x in comparisons),outcomeChangedPairs=sum(x['outcomeBefore']!=x['outcomeAfter'] for x in comparisons),maxCandidates=max(int(d['maxCandidates']) for d in data),formalAttackIdsOffered=sorted(offered),unsupported=sorted(unsupported),treeFailureObservationTicks=dict(failures),chosenActions={mode:{action:n for (m,action),n in chosen.items() if m==mode} for mode in ('SingleCommand','UtilityBehaviorTree')})
 write('analysis.json',stats);write('paired-comparison.json',comparisons);print(json.dumps(stats,ensure_ascii=False,indent=2));print('Changed pairs:',[c for c in comparisons if c['changedFields']])
def restore():
 old=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
 def generated(p):
  return p.startswith(('优化任务/执行记录/M1-regression/','优化任务/执行记录/拼刀后摇纠偏-2026-10-29/','优化任务/执行记录/规则AI阶段三-2026-11-03/','Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/')) or p.startswith('Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity') or p=='ProjectSettings/ProjectSettings.asset' or (p.startswith('Assets/Tests/') and p.endswith(('.asset','.asset.meta')))
 changed=[p for p,h in old.items() if generated(p) and (not (REPO/p).exists() or sha((REPO/p).read_bytes())!=h)]
 with zipfile.ZipFile(ROOT/'validation-regenerated-files.zip','x',zipfile.ZIP_DEFLATED) as z:
  for p in changed:
   if (REPO/p).exists():z.write(REPO/p,p)
 with zipfile.ZipFile(ROOT/'before-task.zip') as z:
  for p in changed:
   data=z.read(p);assert sha(data)==old[p];(REPO/p).write_bytes(data)
 assert all(sha((REPO/p).read_bytes())==h for p,h in old.items() if generated(p))
 write('restoration.json',dict(restoredFiles=changed,verifiedFiles=sum(generated(p) for p in old)))
 print('Restored generated files:',len(changed))
def seal():
 import xml.etree.ElementTree as E
 for name in ('full-editmode.xml','production-shadow-playmode.xml'):
  test=E.parse(ROOT/name).getroot();assert test.attrib['failed']=='0' and int(test.attrib['passed'])==(1116 if name=='full-editmode.xml' else 15),(name,test.attrib)
 assert 'cases=24;' in (ROOT/'native-disk-audit.txt').read_text(encoding='utf-8-sig')
 assert 'cases=16;' in (ROOT/'native-v2-compatibility.txt').read_text(encoding='utf-8-sig')
 inputs=[json.loads(p.read_text(encoding='utf-8-sig')) for p in (ROOT/'cases').glob('*.input.json')];assert len(inputs)==24
 sources=sorted([*(REPO/'Assets/Scripts').rglob('*.cs'),*(REPO/'Assets/Tests').rglob('*.cs')],key=lambda p:p.relative_to(REPO).as_posix())
 hashes={p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in sources}
 fp=sha(''.join(p+':'+h+'\n' for p,h in hashes.items()).encode());assert all(i['sourceFingerprint']==fp for i in inputs)
 files=set(sources)
 for p in sources:files.add(p.with_name(p.name+'.meta'))
 files.update(p for p in (REPO/'Assets/Resources').rglob('*') if p.is_file())
 files.add(REPO/'Assets/Scenes/CombatSampleScene.unity')
 for base in ('Assets/Scripts','Assets/Tests'):
  for p in (REPO/base).rglob('*.asmdef'):files.update((p,p.with_name(p.name+'.meta')))
 files.update(REPO/p for p in ('Packages/manifest.json','Packages/packages-lock.json','ProjectSettings/ProjectVersion.txt'))
 with zipfile.ZipFile(ROOT/'source-and-content.zip','x',zipfile.ZIP_DEFLATED) as z:
  for p in sorted(files):assert p.is_file(),p;z.write(p,p.relative_to(REPO).as_posix())
 old=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
 changed=[p for p,h in old.items() if p.startswith(('Assets/Scripts/','Assets/Tests/')) and sha((REPO/p).read_bytes())!=h]
 new=[p for p in hashes if p not in old]
 with zipfile.ZipFile(ROOT/'before-task.zip') as z:
  diff=[]
  for p in changed:
   if not p.endswith(('.cs','.asmdef')):continue
   diff.extend(difflib.unified_diff(z.read(p).decode('utf-8-sig').splitlines(),(REPO/p).read_text(encoding='utf-8-sig').splitlines(),fromfile=p+' (before task)',tofile=p,lineterm=''))
  for p in new:
   diff.extend(difflib.unified_diff([], (REPO/p).read_text(encoding='utf-8-sig').splitlines(),fromfile='/dev/null',tofile=p,lineterm=''))
  (ROOT/'task-diff.patch').write_text('\n'.join(diff)+'\n',encoding='utf-8')
 assert all(sha((REPO/p).read_bytes())==h for p,h in old.items() if p.startswith('Assets/Resources/'))
 legacy=REPO/'优化任务/执行记录/阶段四后摇策略对照-2026-11-03/corrected-preview-v2'
 sealed=json.loads((legacy/'sealed-evidence-manifest.json').read_text(encoding='utf-8-sig'))
 assert all(sha((legacy/p).read_bytes())==h for p,h in sealed['files'].items())
 write('historical-v2-preservation.json',dict(sealedCases=96,verifiedFiles=len(sealed['files']),nativeCompatibilityCases=16,unchanged=True))
 write('provenance.json',dict(sourceFingerprint=fp,sourceFiles=len(hashes),contentFingerprint=inputs[0]['contentFingerprint'],sourceDefinitionHash=inputs[0]['sourceHash'],modifiedExistingSource=changed,newSource=new,archiveFiles=len(files),formalResourcesUnchanged=True))
 manifest={p.relative_to(ROOT).as_posix():sha(p.read_bytes()) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name not in ('sealed-manifest.json',)}
 write('sealed-manifest.json',manifest);print('Sealed',len(manifest),'files; source',fp)
if __name__=='__main__':globals()[sys.argv[1]]()
