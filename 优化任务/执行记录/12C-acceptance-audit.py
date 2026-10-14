"""R1-R4 native evidence audit. Writes only new 12/12C records."""
from pathlib import Path
import json, hashlib, csv, re, sys, subprocess, xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2]; RECORD=ROOT/'优化任务/执行记录'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def read(n): return json.loads((RECORD/n).read_text(encoding='utf-8-sig'))
def write(n,v): (RECORD/n).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def sources():
    files=[p for folder in ['Assets/Scripts','Assets/Tests'] for p in (ROOT/folder).rglob('*') if p.is_file() and p.suffix in {'.cs','.asmdef','.xml'}]
    return [{'path':p.relative_to(ROOT).as_posix(),'sha256':sha(p)} for p in sorted(files,key=lambda p:p.relative_to(ROOT).as_posix())]
def cases(n):
    rows=ET.parse(RECORD/n).getroot().findall('.//test-case')
    assert rows and all(r.get('result')=='Passed' for r in rows), (n,[r.get('fullname') for r in rows if r.get('result')!='Passed'])
    return [r.get('fullname') for r in rows]
def match(names,method):
    matches=[n for n in names if n.split('(')[0].endswith('.'+method)]
    assert matches,method
    return matches
def manifest():
    names=cases('12C-editmode.xml')+cases('12C-playmode-bootstrap.xml')
    entries=[]
    for e in read('12C-shadow-classification.json')['entries']:
        executed=sorted(set(n for method in e['tests'] for n in match(names,method)))
        entries.append({'id':e['legacyObjectPath']+'#'+e['field'],'category':e['category'],'reason':e['reason'],'executedCases':executed})
    assert len(entries)==73
    a=read('12C-hash-anchors.json')
    write('12C-shadow-migration-evidence.json',{'decision':'accepted-2026-10-14-R1-R4-performance-deferred',
        'caseId':'task11-shadow-confirmed-future-attack','rulesVersion':a['rulesVersion'],'definitionHash':a['definitionHash'],
        'editResults':'12C-editmode.xml','playResults':'12C-playmode-bootstrap.xml','editSha256':sha(RECORD/'12C-editmode.xml'),
        'playSha256':sha(RECORD/'12C-playmode-bootstrap.xml'),'sourceHashes':sources(),'entries':entries})
    print('73 obligations tied to passed native cases and current source hashes.')
ROWS=[
('R1.1','固定半开窗口、自动关闭和连续打开','WindowEndTickEqualsOpenedAtTickPlusDuration;WindowClosesAtEndOfLastTickAndNextOpensAtEndTick;ConsecutiveWindowsTileWithoutGapOrOverlap'),
('R1.2','普通起点限定、末尾长动作跨窗口、数量不受预算限制','StartTickAtEndTickMinusOneIsAccepted;StartTickAtEndTickIsRejectedWithStableCode;LongActionStartingInLastTickExtendsBeyondWindowAndContinues;UnlimitedActionsWithinWindowAreAcceptedWithoutCost'),
('R1.3','删除预算及相关快照和事件','SnapshotHashAndEventsContainNoBudgetState;StartCommitRemainsAtomicWithoutBudget'),
('R1.4','取消主动关窗、死亡/战斗结束与AI无候选','WindowCannotBeClosedEarlyByPlayerOrAi;ClosedWindowCommandKindZeroIsRejectedAndNotReused;OwnerDeathClosesWindowEarlyAndNextOpensNextTick;AiIssuesNoCommandWhenNoCandidateFitsWindow'),
('R1.5','并发授权与当前窗口起点限制','ConcurrentHeroPlanIsBoundToOwnersWindowRange;FixedWindowHudRemovesEndTurnAndShowsOutOfRangeRejection'),
('R1.6','编辑越界拒绝、自动延期越界终止与依赖闭包','EditMovingStartTickOutsideSourceWindowIsRejected;MoveChainRecomputePushingStartBeyondWindowRejectsWholeTransaction;AutoDeferralBeyondSourceWindowTerminatesWithDelayedBeyondSourceWindow;RippleOverflowTerminatesOffendingPlanAndRetriesDuePlanAtomically'),
('R1.7','关闭窗口无遗留Editable普通计划；已公开行动持续','NoEditableOrdinaryPlanSurvivesItsSourceWindowClose;WindowSwitchDoesNotMutatePlanLaneIntentOrReservation'),
('R1.8','个人窗口打开清零，关闭和其他窗口保留','AdrenalinePersistsAcrossOtherUnitsWindowsAndOwnWindowClose'),
('R2.1','三种防御无前后摇、固定整段持续','GuardHasNoWindupAndIsEffectiveFromStartTick;GuardEndTickExclusiveGivesNoResistance;ReactionDefaultStartIsSourceImpactTick'),
('R2.2a','Guard普通权限、每Tick扣一次、实际最大比例与额度耗尽','GuardQuotaDeductsOncePerTickRegardlessOfContactCount;GuardQuotaUsesMaxActualRatioAcrossChannelsAndMomentum;GuardTerminatesWithQuotaExhaustedAfterResolvingThatTick'),
('R2.2b','比例小于1024、Guardable必为Blockable、累计抵抗上限','BuilderRejectsGuardResistanceAtOrAboveFull;BuilderRejectsGuardableComponentWithoutBlockable;GuardCumulativeResistanceNeverExceedsLargestSingleTickPayload'),
('R2.3','反应有效机会、接受预留、生效消费、起点和覆盖约束','ReactionStartBeforeNextTickOrAfterImpactIsRejected;ReactionIntervalMustCoverSourceImpact;ReactionDefaultStartIsSourceImpactTick'),
('R2.4','首个合格接触触发Block、同Tick抵抗与立即终态','BlockTriggersOnFirstEligibleContactEvenIfNotSource;BlockResistsAllEligiblePayloadsInTriggerTickThenTerminates;BlockNotTriggeredUntilEndExpiresWithoutRefund'),
('R2.5','Dodge首个旧格合格接触、落点重检、失败消耗、换位失效','DodgeTriggersOnFirstDodgeableOldCellContact;DodgeDestinationRecheckFailureConsumesAndTerminates;DodgeWithoutRelocationDoesNotInvalidateMoves;DodgeMoveInvalidationClearsPathReservationsWithoutBudgetRelease'),
('R2.6','来源命中前取消同周期退款、未命中后自然结束不取消反应','SourceCancelledBeforeImpactRefundsSameCycleOnly;MissedSourceImpactAndLaterSourceCompletionDoNotCancelReaction'),
('R2.7','成功Block/Dodge肾上腺素奖励保持一次','SuccessfulDodgeRewardStillAccruesOnce;OptionDeadlineIsImpactMinusIngressLead;AiReactionCommandCarriesExplicitStartTick'),
('R2.8a','反应硬段互斥，软段允许普通计划','OrdinaryPlanOverlappingReactionHardSegmentIsRejected;OrdinaryPlanMayStartInsideReactionSoftSegment'),
('R2.8b','普通启动让未触发反应让轨；已触发立即释放轨道','UntriggeredReactionYieldsLaneWhenOrdinaryPlanStarts;TriggeredReactionFreesLaneImmediately;YieldPointPreviewMatchesActualYield'),
('R3','删除反击窗口事件/结算；保留成功Dodge奖励与攻击自然后摇','NoCounterWindowEventIsEverEmitted;SuccessfulDodgeRewardStillAccruesOnce;AttackImpactOccursAtResolvedWindupEndAndEndsAfterRecovery'),
('R4.1','所有控制者可见可用值、周期及预留，UnitId排序','DecisionSnapshotExposesAllUnitsAdrenalineToEveryController;PlayerAndAiSeeIdenticalAdrenalineFacts;MainSceneShowsBothSidesAdrenaline'),
('R4.2','他方Editable仍隐藏、反应能力只用公开硬段和资源','DecisionSnapshotStillHidesOtherControllersEditablePlans;OpponentReactionCapabilityUsesOnlyPublicFacts;HudReadWorldContainsNoAiStateFutureIngressOrHiddenPlan'),
('HUD.1','窗口序列与已知视界，Tick0回放比对','WindowSequenceProjectionMatchesActualOpenings;KnownHorizonEqualsNextForeignWindowOpen'),
('HUD.2','反应每选项拒绝原因、冲突可编辑性、无可行反应不自动暂停','ReactionFeasibilityReportsEachInfeasibleReasonStably;AutoPauseFeasibilityIsFalseWhenAdrenalineInsufficient'),
('HUD.3','首接触Tick/来源及抢先消耗，公开防御及Guard逐Tick额度','ExpectedTriggerContactMatchesActualWithinHorizon;ExpectedTriggerContactFlagsEarlierPreemptingContact;GuardQuotaPreviewMatchesActualDeductions;ProposedGuardUsesTheSamePlacementAndQuotaEvaluators;VisibleForeignDefenseCanBePreviewedWithoutAccessToForeignEditablePlans'),
('HUD.4','无状态/ID/修订写入，每快照缓存且旧投影冻结','PreviewsDoNotMutateStateIdsOrRevision;PreviewIsCachedPerCommittedSnapshot;CachedProjectionRemainsFrozenAfterTheLiveBattleAdvances'),
('HUD.5','主场景重击无法反应预告、实际伤害及100次回放','MainSceneInsufficientAdrenalinePreviewMatchesHeavyResult'),
('HUD.6','每个已公开攻击目标的Block/Dodge可用性及后摇标记','OpponentReactionCapabilityUsesOnlyPublicFacts;MainSceneInsufficientAdrenalinePreviewMatchesHeavyResult'),
('10.4.1','跨窗口长攻击与后续单位交互','LongActionStartingInLastTickExtendsBeyondWindowAndContinues;FinalCombatClassFileReplaysOneHundredTimesFromTickZero'),
('10.4.2','窗口外起点拒绝、系统延期越界终止','StartTickAtEndTickIsRejectedWithStableCode;AutoDeferralBeyondSourceWindowTerminatesWithDelayedBeyondSourceWindow'),
('10.4.3','Guard区间部分抵抗、结束排除、额度用完','GuardHasNoWindupAndIsEffectiveFromStartTick;GuardEndTickExclusiveGivesNoResistance;GuardTerminatesWithQuotaExhaustedAfterResolvingThatTick'),
('10.4.4','轻击抢先Block、后续重击不再抵抗','BlockTriggersOnFirstEligibleContactEvenIfNotSource;ExpectedTriggerContactFlagsEarlierPreemptingContact'),
('10.4.5','同Tick多攻击Block、AOE其他目标和穿透','BlockResistsAllEligiblePayloadsInTriggerTickThenTerminates;RealBlockPlanNullifiesItsOwnTargetAndLeavesOthersUntouched;FinalCombatClassFileReplaysOneHundredTimesFromTickZero'),
('10.4.6','Dodge新格仍被AOE覆盖继续受伤且不奖励','DodgeStillHitByAreaAttackInvalidatesOldOriginMovesWithoutSuccessReward'),
('10.4.7','实际换位取消旧移动闭包；未换位保留','DodgeRelocationInvalidatesDependentMovesBeforeContactGraphBuild;DodgeWithoutRelocationDoesNotInvalidateMoves'),
('10.4.8','反向冲击伤害与控制累加、无合力击退','OppositeFlankingContactsKeepPerAttackEventsAndZeroResultant'),
('10.4.9','三方拼刀排列不变','ThreeWayAttackResultIsIndependentOfAllIntentPermutations;MultiAttackMultiDefenseInputPermutationInvariance'),
('10.4.10','争抢、交换、循环、静止阻挡及空格链','StationaryUnitHardBlocksForcedDisplacement;SameAnchorContentionFailsAllProposalsWithoutWinner;TwoUnitSwapStopsBothAsDependencyCycle;DependencyChainEndingInEmptyCellMovesSimultaneously;ThreeUnitPositionCycleStopsAllParticipants'),
('10.4.11','有限恢复原子延期、永久失效释放','ControlExpiringAtStartTickAllowsPlanToStartWithoutDelay;TimedControlBlockerAutoDefersDueEditablePlan;NoFiniteRetryTickTerminatesPlanAsActorUnavailableAtStart;AutoDeferralLimitOrHorizonTerminatesWithExplicitReason;TerminalPlanLeavesEverySchedulableCollection'),
('10.4.12','同Tick全灭Draw且唯一结束事实','SimultaneousLethalHitsAllowMutualDeath;SimultaneousFactionEliminationProducesConfiguredDraw;TargetDamageAndImpulseCommitOnceInStableOrder'),
('10.4.13','暂停不推进及已有多操作草稿/拒绝回执基础','PauseStopsStepsAndDoesNotAccumulatePausedTime;MainSceneTimelineDragDeleteAndRevisionRebaseUseRealPlayerPort'),
('FINAL.Shadow','隐藏场景真实输入、共同面比较、73项分类且零写入','DefenseDurationShadowProfileHasNoUnclassifiedDifference'),
]
def audit():
    names=cases('12C-editmode.xml')+cases('12C-playmode.xml'); m=read('12C-shadow-migration-evidence.json')
    assert m['sourceHashes']==sources()
    assert m['editSha256']==sha(RECORD/m['editResults']) and m['playSha256']==sha(RECORD/m['playResults'])
    rows=[]
    for key,requirement,methods in ROWS:
        executed=sorted(set(n for method in methods.split(';') for n in match(names,method)))
        boundary='组件边界测试与实际场景/回放组合；不宣称全部配置都在主场景执行。'
        if key=='10.4.13': boundary='已验证暂停时钟和既有多操作事务；M1的暂停中编辑多草稿并在恢复时提交的完整产品流程仍待独立HUD实现，不记为通过。'
        rows.append({'Clause':key,'Requirement':requirement,'Status':'Passed' if key!='10.4.13' else 'Passed foundation / HUD M1 pending',
                     'EvidenceMethods':methods,'ExecutedCases':';'.join(executed),'NativeEvidence':'12C-editmode.xml;12C-playmode.xml','Boundary':boundary})
    with (RECORD/'12-修订迁移验收矩阵-2026-10-14.csv').open('w',encoding='utf-8-sig',newline='') as f:
        w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
    required=re.findall(r'^- `([^`]+)`',(ROOT/'优化任务/12C-肾上腺素公开与收尾.md').read_text(encoding='utf-8-sig').split('## 必需测试')[1].split('## 验收标准')[0],re.M)
    proof={method:match(names,method) for method in required};assert len(proof)==14;write('12C-required-tests.json',proof)
    inventory=[];counts={}
    for mode in ['editmode','playmode']:
        old=set(cases('12B-'+mode+'.xml'));new=set(cases('12C-'+mode+'.xml'));assert old<=new,old-new
        counts[mode]={'baseline':len(old),'current':len(new),'passed':len(new),'added':len(new-old),'removed':0}
        inventory.extend({'mode':mode,'status':'Retained' if n in old else 'Added','test':n} for n in sorted(new))
    with (RECORD/'12C-test-inventory.csv').open('w',encoding='utf-8-sig',newline='') as f:
        w=csv.DictWriter(f,fieldnames=['mode','status','test']);w.writeheader();w.writerows(inventory)
    archive=[]
    paths=subprocess.run(['git','-c','core.quotepath=false','ls-files','优化任务/执行记录'],cwd=ROOT,capture_output=True,check=True).stdout.decode('utf-8')
    for rel in paths.splitlines():
        if Path(rel).name=='README.md' or Path(rel).name.startswith(('12-','12C-')): continue
        original=subprocess.run(['git','show','HEAD:'+rel],cwd=ROOT,capture_output=True,check=True).stdout
        assert (ROOT/rel).read_bytes().replace(b'\r\n',b'\n')==original.replace(b'\r\n',b'\n'),rel
        archive.append({'path':rel,'sha256':sha(ROOT/rel)})
    evidence=[{'path':p.relative_to(ROOT).as_posix(),'sha256':sha(p)} for p in sorted(RECORD.glob('12C-main-*')) if p.suffix in {'.png','.txt','.heroReplay'}]
    write('12C-final-audit.json',{'tests':counts,'requiredTests':14,'matrixRows':len(rows),'shadowObligations':73,'sourceFingerprintVerified':True,
        'nativeResults':[{'path':n,'sha256':sha(RECORD/n)} for n in ['12C-editmode.xml','12C-playmode-bootstrap.xml','12C-playmode.xml']],
        'archivedEvidenceUnchanged':archive,'hashAnchors':read('12C-hash-anchors.json'),'mainSceneEvidence':evidence,
        'scope':'R1-R4 and Logic previews complete; HUD M1-M4 and formal performance remain pending.'})
    print(json.dumps(counts));print('Matrix rows:',len(rows),'Historical files unchanged:',len(archive))
def scan():
    args=['rg','--files','-uuu']
    for folder in ['.git','Library','Temp','Logs','obj','bin','node_modules','UserSettings','.vs','.idea']: args+=['-g','!'+folder+'/**']
    paths=subprocess.run(args,cwd=ROOT,capture_output=True,check=True).stdout.decode('utf-8').splitlines()
    rows=[]
    pattern=re.compile(r'代码[^。\n]{0,25}(?:尚未|未迁移|待迁移)|(?:尚未|未迁移|待迁移)[^。\n]{0,25}代码|R4.{0,12}待.{0,5}12C|OwnAdrenaline')
    for rel in paths:
        rel=rel.replace(chr(92),'/')
        p=ROOT/rel
        if p.suffix not in {'.md','.cs','.json','.txt','.py','.csv'} or p.name.startswith('12C-state-scan'): continue
        try: lines=p.read_text(encoding='utf-8-sig').splitlines()
        except (OSError,UnicodeError): continue
        for number,line in enumerate(lines,1):
            if not pattern.search(line): continue
            history=rel.startswith(('优化任务/执行记录/','设计文档/历史/'))
            instruction=rel.startswith('优化任务/') and p.suffix=='.md'
            assert history or instruction,(rel,number,line)
            rows.append({'path':rel,'line':number,'text':line,'classification':'历史证据/本轮审计与接口替换说明' if history else '历史任务设计或12C要求引用，非当前状态'})
    with (RECORD/'12C-state-scan.csv').open('w',encoding='utf-8-sig',newline='') as f:
        w=csv.DictWriter(f,fieldnames=['path','line','text','classification']);w.writeheader();w.writerows(rows)
    print('No stale active state descriptions; explained historical/task references:',len(rows))
if __name__=='__main__': globals()[sys.argv[1]]()
