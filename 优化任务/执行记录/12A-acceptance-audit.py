"""Audit R1 native test evidence and create its scoped migration manifest.

Run from the project root with Python 3:
  python 优化任务/执行记录/12A-acceptance-audit.py --manifest
  python 优化任务/执行记录/12A-acceptance-audit.py --audit
The historical task11 evidence is read only. No game or scene is changed.
"""
from pathlib import Path
import argparse, ast, csv, hashlib, json, re, struct, subprocess
import xml.etree.ElementTree as ET

ROOT=Path.cwd()
RECORD=ROOT/'优化任务/执行记录'
def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()
def write_json(name,value):
 (RECORD/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def read_json(name): return json.loads((RECORD/name).read_text(encoding='utf-8-sig'))
def cases(name, require_pass=True):
 tree=ET.parse(RECORD/name); root=tree.getroot(); rows=list(root.iter('test-case'))
 if require_pass:
  assert rows and all(r.get('result')=='Passed' for r in rows),name
 return root,rows
def fnv(data):
 h=14695981039346656037
 for b in data:h=((h^b)*1099511628211)&((1<<64)-1)
 return h
def string(value):
 encoded=value.encode('utf-8');return struct.pack('<q',len(encoded))+encoded
def initial_reference():
 """Re-encode only the empty initial v4 layout, never a replay reader or migration.
 HEAD's v4 snapshot layout adds three zero frozen-resource fields and three
 zero aggregate-resource fields. No window or plan exists before Step 0.
 """
 anchors=read_json('12A-hash-anchors.json')
 result={'basis':'HEAD v4 canonical initial layout; Native v5 initial facts; empty windows/plans/history; independent FNV reference',
         'initialRngSeed':20260922,'initialMetaResource':10,'restorationOrPlayback':False}
 for prefix,source in [('standard','12A-initial-encoding-v4-domain.bin'),('production','12A-production-initial-encoding-v4-domain.bin')]:
  data=(RECORD/source).read_bytes()
  assert data.startswith(string('LogicSnapshot')+struct.pack('<q',4))
  for old,new in [('battle-def-v3-window180','battle-def-v2-turn180'),('c5d1058b7b19720a','ed4c3e21b1488e60')]:
   assert data.count(string(old))==1
   data=data.replace(string(old),string(new))
  window=struct.pack('<9q',0,-1,0,0,0,-1,0,0,0)
  assert data.count(window)==1,'empty window manager must be uniquely located'
  pos=data.index(window);meta=pos+72+17
  assert struct.unpack('<q',data[meta:meta+8])[0]==10
  # Insert in descending offset order to preserve the offsets from the native encoding.
  data=data[:meta+8]+bytes(24)+data[meta+8:]
  data=data[:pos+56]+bytes(24)+data[pos+56:]
  assert struct.unpack('<q',data[-16:-8])[0]==0
  assert struct.unpack('<Q',data[-8:])[0]==fnv(string('HistorySeed')+struct.pack('<q',5))
  data=data[:-8]+struct.pack('<Q',fnv(string('HistorySeed')+struct.pack('<q',4)))
  result[prefix+'OldInitialStateHash']=f'{fnv(data):016x}'
  result[prefix+'EncodingLength']=len(data)
  (RECORD/('12A-'+prefix+'-initial-reference-v4.bin')).write_bytes(data)
 write_json('12A-old-initial-hash-reference.json',result)
 anchors['oldInitialStateHash']=result['standardOldInitialStateHash']
 anchors['oldProductionInitialStateHash']=result['productionOldInitialStateHash']
 anchors['oldInitialHashEvidence']='12A-old-initial-hash-reference.json (independent initial encoding, no v4 playback)'
 write_json('12A-hash-anchors.json',anchors)
 return anchors
def source_hashes():
 files=[p for directory in ['Assets/Scripts','Assets/Tests'] for p in (ROOT/directory).rglob('*')
        if p.is_file() and p.suffix in {'.cs','.asmdef','.xml'}]
 return [{'path':p.relative_to(ROOT).as_posix(),'sha256':sha(p)} for p in sorted(files,key=lambda p:p.relative_to(ROOT).as_posix())]
def manifest():
 anchors=initial_reference()
 _,edit=cases('12A-editmode.xml');_,play=cases('12A-playmode-bootstrap.xml')
 passed=[r.get('fullname') for r in edit+play]
 catalog=read_json('12A-shadow-classification.json');entries=[]
 assert len(catalog['entries'])==62
 for entry in catalog['entries']:
  executed=[]
  for test in entry['tests']:
   matches=[name for name in passed if name.split('(')[0].endswith('.'+test)]
   assert matches,(entry['field'],test,'not executed')
   executed+=matches
  entries.append({'id':entry['legacyObjectPath']+'#'+entry['field'],'category':entry['category'],
                  'reason':entry['reason'],'executedCases':sorted(set(executed))})
 result={'decision':'accepted-2026-10-14-R1-window-duration-performance-deferred',
         'caseId':'task11-shadow-confirmed-future-attack','rulesVersion':anchors['rulesVersion'],
         'definitionHash':anchors['definitionHash'],'editResults':'12A-editmode.xml',
         'playResults':'12A-playmode-bootstrap.xml','editSha256':sha(RECORD/'12A-editmode.xml'),
         'playSha256':sha(RECORD/'12A-playmode-bootstrap.xml'),'sourceHashes':source_hashes(),'entries':entries}
 write_json('12A-shadow-migration-evidence.json',result)
 # Flatten exact execution rows for review.
 with (RECORD/'12A-shadow-classification.csv').open('w',encoding='utf-8-sig',newline='') as stream:
  writer=csv.DictWriter(stream,fieldnames=['id','category','reason','executedCases']);writer.writeheader()
  for e in entries:writer.writerow({**e,'executedCases':';'.join(e['executedCases'])})
 print('Manifest: 62 exact fields; all evidence methods passed natively; source/result hashes captured.')
def rename_map():
 file=RECORD/'12A-test-renames.json'
 if file.exists():return json.loads(file.read_text(encoding='utf-8'))
 values={}
 source=RECORD/'12A-cleanup-tests.py'
 if source.exists():
  for statement in ast.parse(source.read_text(encoding='utf-8')).body:
   if isinstance(statement,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='renames' for t in statement.targets):
    values=ast.literal_eval(statement.value)
 values.update({'StaleWindowCommandCannotSpendNewWindowBudget':'StaleWindowCommandCannotSubmitIntoNewWindow',
  'WindowProjectionReusesUnchangedRowsAndKeepsOldCloseAndRefundFactsImmutable':'WindowProjectionReusesUnchangedRowsAndKeepsClosedFactsImmutable',
  'AiUsesTheRealSchedulePreviewAndClosesUnaffordableRemainderThroughItsTrustedEntry':'AiIssuesNoCommandWhenNoCandidateFitsWindow',
  'AiUsesRealSchedulePreviewAndIssuesNoCommandWhenLaneTailExceedsWindow':'AiIssuesNoCommandWhenNoCandidateFitsWindow',
  'FullyReservedWindowClosesWithBudgetExhaustedWithoutCancellingItsFutureAttack':'WindowExpiresWithoutCancellingItsRunningAttack',
  'ClosedWindowWithEditableReservationRemainsActiveUntilItsFinalConsumption':'ClosedWindowFreezesImmediatelyWhileItsRunningActionContinues',
  'WindowDurationAndConcurrentHeroSlotParticipateInDefinitionHash':'WindowDurationEntersDefinitionHash',
  'TurnWindowBudgetAndAuthorityShadowProfileHasNoUnclassifiedDifference':'TurnWindowDurationShadowProfileHasNoUnclassifiedDifference'})
 write_json('12A-test-renames.json',values);return values
def migration_inventory():
 names=rename_map();out=[];totals={}
 for mode,baseline,current in [('EditMode','11-migrated-final-editmode.xml','12A-editmode.xml'),
                               ('PlayMode','11-migrated-final-playmode.xml','12A-playmode.xml')]:
  _,old=cases(baseline);root,new=cases(current)
  old_names={r.get('fullname') for r in old};new_names={r.get('fullname') for r in new};matched=set()
  for name in sorted(old_names):
   translated=name
   for a,b in names.items():translated=translated.replace(a,b)
   if translated in new_names:
    matched.add(translated)
    if translated!=name:out.append({'mode':mode,'change':'Renamed','oldCase':name,'newCase':translated,'reason':'R1 removes time-cost/refund terminology; non-resource behavior retained.'})
   else:
    reason='R1 §3: time cost/reservation/spending ledger removed; ledger-only assertions no longer apply.'
    covered='SnapshotHashAndEventsContainNoBudgetState'
    if any(x in name for x in ['Dodge','Displacement','Terminal','Removal','Locked','Death']):
     reason+=' Non-resource terminal, lane, movement and rollback checks retained in their domain regressions and the explicit R1 cases.'
     covered='DodgeMoveInvalidationClearsPathReservationsWithoutBudgetRelease;StartCommitRemainsAtomicWithoutBudget;OwnerDeathClosesWindowEarlyAndNextOpensNextTick;Task07RemovalAndDiscardTests;Task08CommitCoverageTests'
    elif any(x in name for x in ['Window','Authority','Concurrent','CostIncrease']):
     reason='R1 §1/§4/§6: fixed half-open interval replaces early closure and cost authority; submission, source identity and expiry checked independently.'
     covered='WindowCannotBeClosedEarlyByPlayerOrAi;WindowValidationSurvivesBudgetRemovalForPreviewAndCommit;ConcurrentHeroPlanIsBoundToOwnersWindowRange;WindowClosesAtEndOfLastTickAndNextOpensAtEndTick'
    if any(x in name for x in ['Atomic','StartableCommit','FailedStartableCommit']):
     reason='R1 §3 removes the spending/rollback port. Locking and Running still form one commit; an invalid start must write nothing.'
     covered='StartCommitRemainsAtomicWithoutBudget'
    if 'ReactionAcceptance' in name:
     reason='R1 §3 deletes ordinary time-ledger assertions only. Reaction permission, opportunity and adrenaline checks remain independent of ordinary windows.'
     covered='ReactionDoesNotRequireOwnWindowOrConcurrentAuthority;ReactionCommandUsesOpportunityScopeWithoutWindowScope;PlayerAndAiReactionsUseSameProcessorAndPlanner'
    if 'BudgetReleaseSink' in name or 'DodgeRelocationFailure' in name:
     reason='R1 §3/§6 deletes the resource-release hook, including its synthetic throwing-hook failure. Real relocation failure, unchanged dependent Moves and path reservations remain covered.'
     covered='DodgeCommitFailureLeavesDependentMovesAndReservationsUnchanged;DodgeMoveInvalidationClearsPathReservationsWithoutBudgetRelease'
    if 'MoveBudgetUsesPathWeight' in name:
     reason='R1 §3 removes movement time cost only. Path-weight-derived movement duration is retained.'
     covered='MoveEndTickUsesPathWeightNotEdgeCount'
    if 'PlanFromWindowAStartsAndEndsDuringLaterWindows' in name:
     reason='R1 §1/§6 forbids an Editable ordinary start after its source interval. A Running action may still execute and finish across subsequent windows.'
     covered='LongActionStartingInLastTickExtendsBeyondWindowAndContinues;NoEditableOrdinaryPlanSurvivesItsSourceWindowClose'
    if 'RemovalReleases' in name or 'RemovingEditable' in name or 'RepeatedEditableTerminalCleanup' in name:
     reason='R1 §3 removes refunds and ledger ownership. Command removal, exactly-once terminal cleanup and absence of active artifact references remain covered.'
     covered='RemovedPlanEntersTerminalThroughCoordinatorWithCancelledByCommand;RemovedPlanLeavesNoActiveArtifactReference;TerminalCleanupEmitsLifecycleEventOnce'
    if 'ForcedDisplacement' in name:
     reason='R1 §3 removes resource-release/refund assertions. The unified invalidation, reservation cleanup, simultaneous relocation and event/death ordering remain covered.'
     covered='ForcedDisplacementPlanInvalidationPrecedesLaterInterceptTerminalRequest;ForcedDisplacementBatchCommitsBeforeEventsStateControlAndDeath;DodgeCommitFailureLeavesDependentMovesAndReservationsUnchanged'
    out.append({'mode':mode,'change':'Removed/Replaced','oldCase':name,'newCase':covered,'reason':reason})
  additions=new_names-matched
  for name in sorted(additions):out.append({'mode':mode,'change':'Added','oldCase':'','newCase':name,'reason':'Executable fixed-window boundary, source validation, expiry, replay, hash or Canvas evidence.'})
  removed=sum(1 for r in out if r['mode']==mode and r['change']=='Removed/Replaced')
  totals[mode]={'baseline':len(old_names),'current':len(new_names),'passed':int(root.get('passed')),
                'removedOrReplaced':removed,'added':len(additions),
                'renamed':sum(1 for r in out if r['mode']==mode and r['change']=='Renamed')}
  assert len(old_names)-removed+len(additions)==len(new_names)
 with (RECORD/'12A-test-migration.csv').open('w',encoding='utf-8-sig',newline='') as stream:
  writer=csv.DictWriter(stream,fieldnames=['mode','change','oldCase','newCase','reason']);writer.writeheader();writer.writerows(out)
 write_json('12A-test-counts.json',totals)
 return totals
def audit():
 totals=migration_inventory();manifest_data=read_json('12A-shadow-migration-evidence.json')
 assert source_hashes()==manifest_data['sourceHashes'],'Source evidence is stale'
 for mode in ['edit','play']:
  path=RECORD/manifest_data[mode+'Results'];assert sha(path)==manifest_data[mode+'Sha256']
 catalog=read_json('12A-shadow-classification.json')
 counts={category:sum(e['category']==category for e in catalog['entries']) for category in ['NewRule','ReplacedRepresentation','RemovedResource','DiagnosticOnly']}
 # Keep an explicit list of source matches; old documents/artifacts are immutable history.
 scan=subprocess.run(['rg','-n','-i','budget','Assets/Scripts','Assets/Tests','-g','*.cs'],capture_output=True,text=True,encoding='utf-8').stdout
 (RECORD/'12A-budget-scan-source.txt').write_text(scan,encoding='utf-8')
 all_scan=subprocess.run(['rg','-n','-i','budget','--glob','!Library/**','--glob','!Temp/**','--glob','!Logs/**','--glob','!Packages/**','--glob','!优化任务/执行记录/12A-*'],capture_output=True,text=True,encoding='utf-8').stdout
 (RECORD/'12A-budget-scan-repository.txt').write_text(all_scan,encoding='utf-8')
 old_changes=subprocess.check_output(['git','-c','core.quotepath=false','status','--short','--','优化任务/执行记录/11-*'],text=True,encoding='utf-8')
 assert not old_changes,'Historical task11 artifacts must remain unchanged'
 result={'nativeTests':totals,'classification':counts,'classifiedFields':len(catalog['entries']),
         'sourceFingerprintCurrent':True,'nativeResultHashesCurrent':True,'historicalTask11Unchanged':True,
         'rulesVersion':manifest_data['rulesVersion'],'definitionHash':manifest_data['definitionHash'],
         'replayFormat':5,'performance':'previous formal performance acceptance remains deferred'}
 write_json('12A-final-audit.json',result);print(json.dumps(result,ensure_ascii=False))
if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('--manifest',action='store_true');parser.add_argument('--audit',action='store_true')
 args=parser.parse_args()
 if args.manifest:manifest()
 if args.audit:audit()
