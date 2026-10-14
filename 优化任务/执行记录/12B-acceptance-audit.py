"""Native R2/R3 evidence catalog, source fingerprints and migration inventory."""
from pathlib import Path
import json, hashlib, csv, xml.etree.ElementTree as ET, re, sys, subprocess
ROOT=Path(__file__).resolve().parents[2]; RECORD=ROOT/'优化任务/执行记录'
def read(n):return json.loads((RECORD/n).read_text(encoding='utf-8-sig'))
def write(n,v):(RECORD/n).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def source_hashes():
 files=[p for directory in ['Assets/Scripts','Assets/Tests'] for p in (ROOT/directory).rglob('*') if p.is_file() and p.suffix in {'.cs','.asmdef','.xml'}]
 return [{'path':p.relative_to(ROOT).as_posix(),'sha256':sha(p)} for p in sorted(files,key=lambda p:p.relative_to(ROOT).as_posix())]
RENAMES={
 'DodgeRelocationFailureLeavesMovementChainUntouched':'DodgeWithoutRelocationDoesNotInvalidateMoves',
 'GuardTimingRequiresPositiveWindupActiveAndRecovery':'GuardTimingRequiresPositiveDuration',
 'GuardTimingResolvesWindupActiveRecoveryOnce':'GuardDurationResolvesOnceWithoutSpeedScaling',
 'GuardPhaseUsesItsActiveAndRecoveryBoundaries':'GuardPhaseCoversWholeDuration',
 'DodgeAgainstMultipleAttacksGrantsOneStableCounterWindow':'SuccessfulDodgeOnlyProducesRewardFacts',
 'FailedDodgeCommitEmitsResolutionButNoTriggerOrReward':'FailedDodgeCommitEmitsConsumedTriggerWithoutReward',
 'DodgeWithoutContactsEmitsOneResolutionWithNullGroup':'DodgeWithoutContactsDoesNotRelocateOrProduceResolution',
 'BlockingStateOutsideTriggerTickProvidesNoResistance':'BlockIntervalCanResistEarlierContact',
 'BlockingStateOutsideSourceImpactTickProvidesNoResistance':'BlockIntervalCanResistEarlierContact',
 'ReactionOptionWithoutCommandIngressLeadIsNotPublished':'ReactionDurationDoesNotSubtractFromIngressDeadline',
 'BlockStartsConsumesAtTriggerAndCompletesThroughProductionStep':'BlockTriggersConsumesAndTerminatesThroughProductionStep',
 'BlockWithoutContactsStillConsumesAtTriggerAndDoesNotEarnSuccessReward':'BlockWithoutContactsRemainsReservedUntilExpiryWithoutReward',
 'FailedDodgeAtTriggerReleasesItsReservationWithoutTriggerOrAvoidance':'FailedDodgeConsumesAndTerminatesWithoutAvoidance',
 'FrozenR1DefinitionAndInitialStateHashesAreReproducible':'FrozenR2DefinitionAndInitialStateHashesAreReproducible',
 'ReactionOptionDeadlineIsDerivedFromImpactAndReactionWindup':'ReactionOptionDeadlineIsDerivedFromImpactAndIngressLead',
 'RealBlockPlanNullifiesItsOwnTargetAndLeavesOthersUntouched':'RealBlockPlanNullifiesItsOwnTargetAndLeavesOthersUntouched',
 'MigrationShadowWithConfirmedFutureAttackKeepsRealCommonFieldsAndResolvesObligations':'DefenseDurationShadowProfileHasNoUnclassifiedDifference',
}
def renamed(name):
 name=name.replace('Task12AHashEvidenceTests','Task12BHashEvidenceTests').replace('TriggerTick','SourceImpactTick')
 for old,new in RENAMES.items():name=name.replace(old,new)
 return name
def catalog():
 c=read('12A-shadow-classification.json');c.update(caseId='task12B-final-classification',rulesVersion='battle-def-v4-defense-duration',definitionHash=read('12B-hash-anchors.json')['definitionHash'])
 for e in c['entries']:e['tests']=[renamed(t) for t in e['tests']]
 source='CombatSampleScene/Core/Gameplay#TacticsController'
 removed=['plans[i].activeStartTick','plans[i].activeEndTick','plans[i].reactionWindupTicks','plans[i].activeTicks','events.DodgeCounterWindowOpenedEvent','staged.CounterWindows']
 proof={
 'plans[i].durationTicks':['GuardHasNoWindupAndIsEffectiveFromStartTick','ReactionDefaultStartIsSourceImpactTick'],
 'plans[i].defenseQuotaRemainingQ10':['GuardQuotaDeductsOncePerTickRegardlessOfContactCount','GuardCumulativeResistanceNeverExceedsLargestSingleTickPayload'],
 'plans[i].reactionTriggeredAtTick':['BlockTriggersOnFirstEligibleContactEvenIfNotSource','DodgeTriggersOnFirstDodgeableOldCellContact','DodgeDestinationRecheckFailureConsumesAndTerminates'],
 'plans[i].sourceImpactTick':['OrdinaryPlanMayStartInsideReactionSoftSegment','UntriggeredReactionYieldsLaneWhenOrdinaryPlanStarts','SourceCancelledBeforeImpactRefundsSameCycleOnly'],
 'reactionOptions[i].responseDeadlineTick':['OptionDeadlineIsImpactMinusIngressLead','ReactionStartBeforeNextTickOrAfterImpactIsRejected']}
 for field in removed+list(proof):
  c['entries'].append({'field':field,'legacyObjectPath':source,'category':'RemovedRepresentation' if field in removed else 'NewRule',
   'reason':'R2/R3 removed representation with executable absence checks; no Legacy/New equality claim.' if field in removed else 'R2 duration, quota, first eligible contact and hard/soft lane rule verified by native execution.',
   'legacySource':'Assets/Scripts/Core/Gameplay/TacticsController.cs','newSource':'Assets/Scripts/Logic/Actions/ActionPlan.cs',
   'tests':['NoCounterWindowEventIsEverEmitted','GuardHasNoWindupAndIsEffectiveFromStartTick'] if field in removed else proof[field]})
 assert len(c['entries'])==73
 write('12B-shadow-classification.json',c)
def cases(n,required=True):
 root=ET.parse(RECORD/n).getroot(); rows=root.findall('.//test-case')
 if required: assert rows and all(r.get('result')=='Passed' for r in rows),(n,[(r.get('fullname'),r.get('result')) for r in rows if r.get('result')!='Passed'])
 return root,rows
def manifest():
 a=read('12B-hash-anchors.json');_,edit=cases('12B-editmode.xml');_,play=cases('12B-playmode-bootstrap.xml');passed=[r.get('fullname') for r in edit+play]
 c=read('12B-shadow-classification.json');entries=[]
 for e in c['entries']:
  executed=[]
  for t in e['tests']:
   matches=[n for n in passed if n.split('(')[0].endswith('.'+t)]
   assert matches,(e['field'],t,'not executed')
   executed+=matches
  entries.append({'id':e['legacyObjectPath']+'#'+e['field'],'category':e['category'],'reason':e['reason'],'executedCases':sorted(set(executed))})
 write('12B-shadow-migration-evidence.json',{'decision':'accepted-2026-10-14-R2-R3-defense-duration-performance-deferred','caseId':'task11-shadow-confirmed-future-attack',
  'rulesVersion':a['rulesVersion'],'definitionHash':a['definitionHash'],'editResults':'12B-editmode.xml','playResults':'12B-playmode-bootstrap.xml',
  'editSha256':sha(RECORD/'12B-editmode.xml'),'playSha256':sha(RECORD/'12B-playmode-bootstrap.xml'),'sourceHashes':source_hashes(),'entries':entries})
 with (RECORD/'12B-shadow-classification.csv').open('w',encoding='utf-8-sig',newline='') as f:
  w=csv.DictWriter(f,fieldnames=['id','category','reason','executedCases']);w.writeheader()
  for e in entries:w.writerow({**e,'executedCases':';'.join(e['executedCases'])})
 print('73 exact classification obligations; native executed cases and source/result hashes captured.')
def audit():
 m=read('12B-shadow-migration-evidence.json'); assert m['sourceHashes']==source_hashes()
 assert m['editSha256']==sha(RECORD/m['editResults']) and m['playSha256']==sha(RECORD/m['playResults'])
 counts={}; inventory=[]
 for mode in ['editmode','playmode']:
  oldRoot,old=cases('12A-'+mode+'.xml');newRoot,new=cases('12B-'+mode+'.xml')
  names={r.get('fullname') for r in new};oldNames={r.get('fullname') for r in old};matched=set()
  for n in sorted(oldNames):
   target=renamed(n)
   if target in names:
    matched.add(target);change='Retained' if target==n else 'Renamed'
   else:change='Removed';target=''
   inventory.append({'mode':mode,'change':change,'oldCase':n,'newCase':target,'reason':'R2/R3 replaces defense timing/termination and deletes counter facts; unrelated assertions retained.' if change!='Retained' else 'Regression retained.'})
  for n in sorted(names-matched):inventory.append({'mode':mode,'change':'Added','oldCase':'','newCase':n,'reason':'R2/R3 executable rule evidence or migrated parameter case.'})
  counts[mode]={'baseline':len(old),'current':len(new),'passed':len(new),'removed':sum(r['change']=='Removed' for r in inventory if r['mode']==mode),
   'added':sum(r['change']=='Added' for r in inventory if r['mode']==mode),'renamed':sum(r['change']=='Renamed' for r in inventory if r['mode']==mode)}
 with (RECORD/'12B-test-migration.csv').open('w',encoding='utf-8-sig',newline='') as f:
  w=csv.DictWriter(f,fieldnames=['mode','change','oldCase','newCase','reason']);w.writeheader();w.writerows(inventory)
 write('12B-test-counts.json',counts)
 assert not any(r['change']=='Removed' for r in inventory),[r for r in inventory if r['change']=='Removed']
 task=(ROOT/'优化任务/12B-防御时序与反应占用.md').read_text(encoding='utf-8-sig')
 required=re.findall(r'^- `([^`]+)`',task.split('## 必需测试')[1].split('## 验收标准')[0],re.M)
 passed=[r.get('fullname') for mode in ['editmode','playmode'] for r in cases('12B-'+mode+'.xml')[1]]
 proof={t:[n for n in passed if n.split('(')[0].endswith('.'+t)] for t in required}
 assert len(proof)==30 and all(proof.values()),proof
 archived=read('12A-shadow-migration-evidence.json')
 assert archived['editSha256']==sha(RECORD/archived['editResults']) and archived['playSha256']==sha(RECORD/archived['playResults'])
 evidence=[{'path':p.relative_to(ROOT).as_posix(),'sha256':sha(p)} for p in sorted(RECORD.glob('12B-main-defense-case-*')) if p.suffix in {'.png','.txt','.heroReplay'}]
 assert len(evidence)==12
 write('12B-required-tests.json',proof)
 write('12B-final-audit.json',{'tests':counts,'requiredTestCount':30,'classificationCount':73,'sourceFingerprintVerified':True,'nativeResultsFingerprintVerified':True,
  'archived12AResultsUnchanged':True,'hashAnchors':read('12B-hash-anchors.json'),'mainSceneEvidence':evidence,
  'finalNativeResults':[{'path':n,'sha256':sha(RECORD/n)} for n in ['12B-editmode.xml','12B-playmode.xml']],
  'scanSummary':read('12B-scan-summary.json')})
 print(json.dumps(counts))

def scan():
 # Search all project-owned text, including ignored/untracked files; generated Unity/cache/Git trees are excluded.
 exclusions=['.git','Library','Temp','Logs','obj','bin','.vs','.idea','node_modules','UserSettings']
 args=['rg','--files','-uuu']
 for directory in exclusions:args+=['-g','!'+directory+'/**']
 paths=subprocess.run(args,cwd=ROOT,check=True,capture_output=True,text=True,encoding='utf-8').stdout.splitlines()
 suffixes={'.cs','.md','.txt','.json','.xml','.yaml','.yml','.unity','.asset','.ps1','.py','.log','.asmdef'}
 pattern=re.compile(r'ReactionWindup|ActiveStartTick|ActiveEndTick|(?<!en)CounterWindow',re.I)
 rows=[]
 for name in paths:
  p=ROOT/name;rel=p.relative_to(ROOT).as_posix()
  if p.suffix.lower() not in suffixes or p.name.startswith('12B-scan'):continue
  try:lines=p.read_text(encoding='utf-8-sig').splitlines()
  except (UnicodeError,OSError):continue
  for lineNo,line in enumerate(lines,1):
   if not pattern.search(line):continue
   if rel=='Assets/Scripts/UnityView/ShadowCasePolicy.cs':reason='R2/R3 RemovedRepresentation 的逐字段名称；无生产字段、事件或分支。'
   elif rel.startswith('Assets/Tests/'):reason='删除规则的负断言、稳定测试名或历史比较范围说明；无旧防御执行逻辑。'
   elif rel.startswith('Assets/Scripts/Core/'):reason='隔离的 Legacy/Compatibility 比较代码，历史权威基线；新运行时不引用这些旧时序字段。'
   elif rel.startswith('优化任务/执行记录/12B-'):reason='本轮分类、迁移映射、原生测试输出或审计说明，保存旧字段名称以追溯删除规则。'
   elif rel.startswith('优化任务/执行记录/'):reason='已归档的任务01–12A证据或验证脚手架；按历史指纹保留，不代表当前规则。'
   elif p.suffix.lower()=='.md':reason='规则修订、被覆盖条款或历史任务说明；当前执行以主方案R2/R3和12B交接为准。'
   else:reason='历史诊断或工具文本；见逐行扫描记录，非新生产防御字段。'
   rows.append({'path':rel,'line':lineNo,'text':line,'explanation':reason})
 rows.sort(key=lambda r:(r['path'],r['line']))
 for name,selected in [('12B-scan-repository.csv',rows),('12B-scan-source.csv',[r for r in rows if r['path'].startswith(('Assets/Scripts/','Assets/Tests/'))])]:
  with (RECORD/name).open('w',encoding='utf-8-sig',newline='') as f:
   w=csv.DictWriter(f,fieldnames=['path','line','text','explanation']);w.writeheader();w.writerows(selected)
 active=[r for r in rows if r['path'].startswith(('Assets/Scripts/Logic/','Assets/Scripts/Authoring/','Assets/Scripts/UnityView/','Assets/Scripts/Editor/'))]
 assert all(r['path']=='Assets/Scripts/UnityView/ShadowCasePolicy.cs' for r in active),active
 write('12B-scan-summary.json',{'pattern':pattern.pattern,'caseInsensitive':True,'excludedFalsePositive':'EncounterWindow means encounter turn window, not counter window.',
  'excludedGeneratedTrees':exclusions,'repositoryHits':len(rows),
  'productionHits':active,'scannedTextSuffixes':sorted(suffixes),'perLineExplanation':'12B-scan-repository.csv'})
 print('Repository hits with per-line explanation:',len(rows),'Production declaration-only hits:',len(active))
if __name__=='__main__':globals()[sys.argv[1].lstrip('-')]()
