from pathlib import Path
import csv,json,gzip,collections,hashlib,zipfile,sys,difflib,xml.etree.ElementTree as E
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
PREVIOUS=ROOT.parent/'正式动作混合AI-2026-11-05'
def sha(data):return hashlib.sha256(data).hexdigest()
def write(name,obj):(ROOT/name).write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def rows(p):
 opener=gzip.open if p.suffix=='.gz' else open
 with opener(p,'rt',encoding='utf-8-sig',newline='') as f:return list(csv.DictReader(f))
def analyze():
 data=rows(ROOT/'summary.csv');assert len(data)==48
 probes=rows(ROOT/'conditional-probe.csv');assert len(probes)==80
 assert sum(p['conditionalActualMatches']=='True' for p in probes)==20
 grouped={};chosen=collections.defaultdict(collections.Counter);actual=collections.defaultdict(lambda:collections.defaultdict(lambda:dict(acceptedPlans=0,resolvedDamageAfterBlockQ10=0,completed=0,terminated=0,running=0)))
 choice_traces={};bad=[]
 for row in data:
  h=int(row['horizon']);grouped.setdefault(h,[]).append(row)
  case=row['case'];traces=[]
  for c in rows(ROOT/'cases'/(case+'.candidates.csv.gz')):
   if c['coverage']=='Uncovered' and (c['score']!='' or c['damageQ10']!='' or c['chosen']=='True'):bad.append((case,c))
   if c['chosen']=='True':
    assert c['coverage']=='Complete' and c['score']!=''
    chosen[h][c['key'].split('/')[2]]+=1;traces.append((int(c['tick']),c['controller'],c['key']))
  choice_traces[case]=sorted(traces)
  for p in rows(ROOT/'cases'/(case+'.plans.csv')):
   if p['type']!='Attack':continue
   value=actual[h][p['action']]
   value['acceptedPlans']+=1;value['resolvedDamageAfterBlockQ10']+=int(p['resolvedDamageAfterBlockQ10'])
   value['completed']+=p['state']=='Completed';value['terminated']+=p['state']=='Terminated';value['running']+=p['state'] not in ('Completed','Terminated')
 assert not bad,bad[:1]
 report={}
 for h,group in sorted(grouped.items()):
  assert len(group)==12
  report[h]=dict(cases=len(group),recordedTicks=sum(int(r['ticks']) for r in group),ended=sum(r['ended']=='True' for r in group),
   commandRejections=sum(int(r['rejections']) for r in group),commands=sum(int(r['commands']) for r in group),attackStarts=sum(int(r['attackStarts']) for r in group),moves=sum(int(r['moves']) for r in group),
   reactions=sum(int(r['reactions']) for r in group),guards=sum(int(r['guardStarts']) for r in group),healthLossQ10=sum(int(r['healthLossQ10']) for r in group),
   controlTicks=sum(int(r['controlTicks']) for r in group),resourceConsumed=sum(int(r['resourceSpent']) for r in group),candidateRows=sum(int(r['candidateRows']) for r in group),
   uncovered=sum(int(r['uncovered']) for r in group),covered=sum(int(r['covered']) for r in group),candidateRejections=sum(int(r['rejectedCandidates']) for r in group),
   chosen=dict(chosen[h]),actualAttacks=dict(actual[h]),maxCandidates=max(int(r['maxCandidates']) for r in group))
 pairs=collections.defaultdict(dict)
 for row in data:pairs[(row['policy'],row['near'],row['mirror'])][row['horizon']]=row
 comparison=[]
 for key,pair in pairs.items():
  first=pair['120']
  comparison.append(dict(policy=key[0],near=key[1],mirror=key[2],health={h:r['health'] for h,r in pair.items()},outcome={h:r['outcome'] for h,r in pair.items()},
   ticks={h:int(r['ticks']) for h,r in pair.items()},choicesChangedFrom120={h:choice_traces[r['case']]!=choice_traces[first['case']] for h,r in pair.items()}))
 legacy={r['case']:r for r in rows(PREVIOUS/'summary.csv')};legacy_comp=[];legacy180=[]
 for r in grouped[120]:
  name=r['policy']+'-UtilityBehaviorTree-'+('near' if r['near']=='True' else 'main')+'-mirror'+('1' if r['mirror']=='True' else '0');a=legacy[name]
  legacy_comp.append(dict(case=r['case'],legacyHealth=a['health'],complete120Health=r['health'],legacyCommands=int(a['commands']),complete120Commands=int(r['commands'])))
 for r in grouped[180]:
  name=r['policy']+'-UtilityBehaviorTree-'+('near' if r['near']=='True' else 'main')+'-mirror'+('1' if r['mirror']=='True' else '0');a=legacy[name]
  old_choices=[(int(c['tick']),c['controller'],c['key']) for c in rows(PREVIOUS/'cases'/(name+'.candidates.csv')) if c['chosen']=='True']
  legacy180.append(dict(case=r['case'],metricsEqual=all(r[k]==a[k] for k in ('health','commands','attackStarts','moves','reactions','rejections')),chosenTraceEqual=sorted(old_choices)==choice_traces[r['case']]))
 stable=[]
 for key,pair in pairs.items():
  a,b=pair['240'],pair['360'];stable.append(dict(policy=key[0],near=key[1],mirror=key[2],
   chosenTraceEqual=choice_traces[a['case']]==choice_traces[b['case']],metricsEqual=all(a[k]==b[k] for k in ('ticks','ended','outcome','health','commands','rejections','attackStarts','moves','reactions','guardStarts','controlTicks','healthLossQ10','resourceSpent'))))
 stats=dict(cases=48,recordedTicks=sum(int(r['ticks']) for r in data),groups=report,probes=80,conditionalActualMatches=20,uncoveredScoresBlank=True,complete120VsLegacy120=legacy_comp,complete180VsLegacy120=legacy180,stability240To360=stable)
 write('analysis.json',stats);write('paired-comparison.json',comparison)
 print(json.dumps({h:{k:v for k,v in g.items() if k not in ('chosen','actualAttacks')} for h,g in report.items()},ensure_ascii=False,indent=2))
 print('Chosen attacks:',json.dumps({h:{a:n for a,n in c.items() if 'action.' in a} for h,c in chosen.items()},ensure_ascii=False))
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
 for name,count in [('full-editmode.xml',1135),('production-shadow-playmode.xml',15)]:
  t=E.parse(ROOT/name).getroot();assert t.attrib['failed']=='0' and int(t.attrib['passed'])==count,(name,t.attrib)
 assert 'cases=48;' in (ROOT/'native-disk-audit.txt').read_text(encoding='utf-8-sig')
 assert 'cases=12;' in (ROOT/'native-compatibility.txt').read_text(encoding='utf-8-sig')
 inputs=[json.loads(p.read_text(encoding='utf-8-sig')) for p in (ROOT/'cases').glob('*.input.json')];assert len(inputs)==48
 sources=sorted([*(REPO/'Assets/Scripts').rglob('*.cs'),*(REPO/'Assets/Tests').rglob('*.cs')],key=lambda p:p.relative_to(REPO).as_posix())
 hashes={p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in sources};fp=sha(''.join(p+':'+h+'\n' for p,h in hashes.items()).encode())
 assert all(i['sourceFingerprint']==fp for i in inputs)
 frozen=json.loads((ROOT/'frozen-final-source.json').read_text(encoding='utf-8'));assert hashes==frozen['files']
 before=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
 formal_before={p for p in before if p.startswith(('Assets/Resources/','Assets/Scenes/'))}
 formal_now={p.relative_to(REPO).as_posix() for folder in ('Assets/Resources','Assets/Scenes') for p in (REPO/folder).rglob('*') if p.is_file()}
 assert formal_now==formal_before,(formal_now-formal_before,formal_before-formal_now)
 for p,h in before.items():
  if p.startswith(('Assets/Resources/','Assets/Scenes/')) or p=='Assets/Scripts/Logic/AI/AiUtilityProfile.cs':assert sha((REPO/p).read_bytes())==h,p
 previous=json.loads((PREVIOUS/'sealed-manifest.json').read_text(encoding='utf-8'))
 assert all(sha((PREVIOUS/p).read_bytes())==h for p,h in previous.items())
 write('previous-evidence-preservation.json',dict(verifiedFiles=len(previous),unchanged=True))
 files=set(sources)
 for p in sources:files.add(p.with_name(p.name+'.meta'))
 files.update(p for p in (REPO/'Assets/Resources').rglob('*') if p.is_file());files.add(REPO/'Assets/Scenes/CombatSampleScene.unity')
 for folder in ('Assets/Scripts','Assets/Tests'):
  for p in (REPO/folder).rglob('*.asmdef'):files.update((p,p.with_name(p.name+'.meta')))
 files.update(REPO/p for p in ('Packages/manifest.json','Packages/packages-lock.json','ProjectSettings/ProjectVersion.txt'))
 with zipfile.ZipFile(ROOT/'source-and-content.zip','x',zipfile.ZIP_DEFLATED) as z:
  for p in sorted(files):assert p.is_file(),p;z.write(p,p.relative_to(REPO).as_posix())
 changed=[p for p,h in before.items() if p.startswith(('Assets/Scripts/','Assets/Tests/')) and sha((REPO/p).read_bytes())!=h];new=[p for p in hashes if p not in before]
 diff=[]
 with zipfile.ZipFile(ROOT/'before-task.zip') as z:
  for p in changed:
   if p.endswith(('.cs','.asmdef')):diff.extend(difflib.unified_diff(z.read(p).decode('utf-8-sig').splitlines(),(REPO/p).read_text(encoding='utf-8-sig').splitlines(),fromfile=p+' (before task)',tofile=p,lineterm=''))
 for p in new:diff.extend(difflib.unified_diff([], (REPO/p).read_text(encoding='utf-8-sig').splitlines(),fromfile='/dev/null',tofile=p,lineterm=''))
 (ROOT/'task-diff.patch').write_text('\n'.join(diff)+'\n',encoding='utf-8')
 write('provenance.json',dict(sourceFingerprint=fp,sourceFiles=len(hashes),sourceDefinitionHash=inputs[0]['sourceHash'],contentFingerprint=inputs[0]['contentFingerprint'],modifiedExistingSource=changed,newSource=new,archiveFiles=len(files),formalDataAndWeightsUnchanged=True))
 manifest={p.relative_to(ROOT).as_posix():sha(p.read_bytes()) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name!='sealed-manifest.json'}
 write('sealed-manifest.json',manifest);print('Sealed',len(manifest),'files; source',fp)
if __name__=='__main__':globals()[sys.argv[1]]()
