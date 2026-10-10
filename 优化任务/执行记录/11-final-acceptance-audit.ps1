param([string]$EditResults='11-classification-final-editmode.xml', [string]$PlayResults='11-classification-final-playmode.xml')
$ErrorActionPreference='Stop'
$recordRoot=$PSScriptRoot
[xml]$edit=Get-Content (Join-Path $recordRoot $EditResults)
[xml]$play=Get-Content (Join-Path $recordRoot $PlayResults)
$cases=@($edit.SelectNodes('//test-case'))+@($play.SelectNodes('//test-case'))
if(@($cases | Where-Object {$_.result -ne 'Passed'}).Count) {throw 'Final run contains failed/skipped/inconclusive cases'}
function Resolve-Evidence([string]$Methods) {
    $found=[Collections.Generic.List[string]]::new()
    foreach($method in ($Methods -split ';')) {
        $matches=@($cases | Where-Object {$_.methodname -eq $method})
        if(!$matches.Count){throw "No executed method: $method"}
        foreach($case in $matches){if($case.result -ne 'Passed'){throw "Evidence did not pass: $($case.fullname)"}; $found.Add($case.fullname)}
    }
    return @($found | Sort-Object -Unique)
}
$map=@{}
@'
1|PlanFromWindowAStartsAndEndsDuringLaterWindows
2|ReactionOpportunityOpensThroughSimulationAtAtomicStart;ReactionCommandCannotProvideTriggerTick;FinalCombatClassFileReplaysOneHundredTimesFromTickZero
3|WindowSwitchDoesNotMutatePlanLaneIntentOrReservation
4|NonIdleStateDoesNotByItselfRejectFuturePlan;LaneSubmissionLockBlocksAddButNotExistingEdits
5|LegacyHeadOnPairKeepsScalarSubtractionAndTerminatesBothPlans
6|ThreeWayClashUsesAllOriginalMomentaSimultaneously;ThreeWayAttackResultIsIndependentOfAllIntentPermutations;ClashResidualMomentumIsDistributedWithoutLossOrDuplication;ThreeWayClashTerminatesThreeRealScheduledAttacks
7|OppositeFlankingContactsKeepPerAttackEventsAndZeroResultant
8|UndodgeableRetainsPreDodgeContactOutsideNewArea;DodgeContactSetsEventsAndRewardsArePermutationInvariant;DodgedAoeTargetDoesNotProtectOtherTargets;DodgeCommitEmitsOneTriggerEvenWithoutSuccessfulAvoidance;FailedDodgeCommitEmitsResolutionButNoTriggerOrReward;DodgeWithoutContactsEmitsOneResolutionWithNullGroup;FinalCombatClassFileReplaysOneHundredTimesFromTickZero
9|BlockHandlesAllSameTickEligibleContactsIndependentOfOrder;PartiallyBlockedContactKeepsBypassComponents;FullyUnblockableContactIsIneffectiveHitAndDoesNotRewardBlock;BlockingStateOutsideTriggerTickProvidesNoResistance
10|SimultaneousLethalHitsAllowMutualDeath;SimultaneousFactionEliminationProducesConfiguredDraw;TargetDamageAndImpulseCommitOnceInStableOrder
10A|ForcedDisplacementStopsAtBoundaryOnFirstOrLaterStep;ForcedDisplacementStopsAtStaticObstacleOnFirstOrLaterStep;ForcedDisplacementKeepsSuccessfulPrefixWhenLaterStepFails
10B|StationaryUnitHardBlocksForcedDisplacement;SameAnchorContentionFailsAllProposalsWithoutWinner;TwoUnitSwapStopsBothAsDependencyCycle;DependencyChainEndingInEmptyCellMovesSimultaneously;ThreeUnitPositionCycleStopsAllParticipants
10C|MultiCellUnitCannotMoveWhenAnyFootprintPointIsIllegal;ForcedDisplacementDoesNotImplicitlyPushUnitWithoutOwnRequest;CompletedShorterDisplacementBecomesStationaryBlockerForLaterRounds;BatchRelocationDoesNotUsePathfinderSegmentsOrReservationArbitration
10D|OnlyFinalFootprintReservationConflictsArePreempted;ReservationPreemptionTerminatesPlansInActionPlanIdOrder;ForcedDisplacementDoesNotRefundSpentMoveBudget;ForcedDisplacementReleasesOnlyUnconsumedEditableReservation;ForcedDisplacementTerminationDoesNotPullLaterPlansForward
10E|LethalTargetIsDisplacedBeforeDeathRemovesFinalFootprint;ForcedDisplacementBatchCommitsBeforeEventsStateControlAndDeath;ForcedDisplacementInputAndResolutionPermutationsProduceIdenticalResults;FinalCombatClassFileReplaysOneHundredTimesFromTickZero
11|MovementCommitsToDestinationAtEndTickBeforeCommands
12|PreCommandDeathWithoutVictoryCannotOpenWindowOrAcceptDeadOwnersCommand;ScheduledWindowOpenRunsAfterPreCommandDeathAndVictory
13|FinalCombatClassFileReplaysOneHundredTimesFromTickZero;ReplayRepeatsOneHundredTimesWithFullEventPayloadAndHashes
13A|EvenGridDirectionsHaveOneWeightUnit;OddGridDirectionsHaveTwoWeightUnits;OpenSetTieDoesNotDependOnInsertionSequence;EqualTentativeGKeepsFirstCanonicalParent;MoveSpeedDoesNotChangeChosenPath;PathSearchAllowsExactly4096ExpandedNodes;PathSearchAllowsExactly256WeightUnits;PathSearchAllowsExactly192Edges;PathFailurePriorityIsOverflowThenEdgeThenWeight;LegacyPerEdgeDurationClampIsNotANewTimingRule
14|VisualTimeScaleIsolationDoesNotLeakIntoNewSimulation;DriverRetainsCatchUpDebtInsteadOfDroppingTicks;SemanticEventsMapToFeedbackWithoutLogicWriteback
15|MainSceneNewCompletesThreeColdBattlesWithPlayerCommandsAndOneEndFeedback;MainSceneBlockButtonUsesRealThreatAndDamageEarnedAdrenaline;MainSceneDodgeSelectionIsLocalUntilConfirmAndUsesRealThreat;MainCanvasPlayerAttackFundsAiReactionAndFeedbackThroughProductionPorts
16|BattleEndTerminatesPlansInActionPlanIdOrderAndLocksAllLanes;BattleEndedPlansRemainInHistoryButLeaveActiveIndexes;BattleEndClearsReservationsAcrossOpenAndClosedWindows
17|EncounterSlotOrderingUsesStringComparerOrdinal;RenamingDisplayNameDoesNotChangeLogicIdOrHash;EncounterSlotsMapFactionIdsToRuntimeUnits
18|CommandSequenceMatchesAcrossFreshTickZeroRuns;EffectIdsAreUniqueAndMonotonicAcrossUnitsAndSpecs;PauseDoesNotAdvanceReplayAndRestartCreatesFreshWorld;FinalCombatClassFileReplaysOneHundredTimesFromTickZero
19|AlreadyEndedStepReturnsEmptyEventsAndSameFinalSnapshot;AlreadyEndedStepDoesNotAdvanceTickRngIdsOrSequences;FinalCombatClassFileReplaysOneHundredTimesFromTickZero
20|CommandAfterBattleEndIsRejectedBeforeTickBucketAndReplayLog;MainSceneNewCompletesThreeColdBattlesWithPlayerCommandsAndOneEndFeedback
21|FrozenRequestsAreRejectedWhenBattleEndsBeforeCommandPhase;BattleEndFinalizerClearsFutureRequestBuckets
22|SolverRecordsPerformanceStatisticsWithoutAffectingResults
23|AttackImpactOccursAtResolvedWindupEndAndBudgetEqualsWindupPlusRecovery;ActionSpeedChangesWindupButNotRecovery
24|AttackInterruptedBeforeImpactEmitsNoIntent;ImpactTickTransitionsToRecoveryAndStillEmitsIntent;FrozenCurrentTickIntentResolvesBeforeTerminalCleanup
25|AcceptedAttackTimingDoesNotChangeAfterSpeedMutation;MovingEditableAttackRebindsAbsoluteTicksWithoutResamplingSpeed
26|MainEncounterBuildsWithoutFallbackOrSkippedAsset;BuilderRejectsMissingOrUnknownSlotFaction;BuilderRejectsMissingDuplicateReverseDuplicateOrDanglingFactionRelation;BuilderRejectsEmptyOrUnknownTargetRelationMaskBits;BuilderRejectsVictoryGroupsContradictingFactionMatrix
27|DefinitionAndHashIgnoreAssetDiscoveryOrder
28|ReplayHeaderUsesFormatVersionAndPreStepInitialStateHash;RuntimeInputsProvideMetaResourceWithoutExternalSingleton
29|RawRequestPermutationProducesSameCanonicalCommandOrder;PlayerAndAiDisjointScheduleEditsFromSameSnapshotCanBothSucceed
30|DuplicateProducerOrdinalRejectsEntireCollisionGroup;ProducerOrdinalRegressionIsRejected;CommandSequenceMatchesAcrossFreshTickZeroRuns
31|ProducerCannotProvideSourcePriorityOrCommandSequence;PlayerCannotIssueReactionForUnitItDoesNotControl;ConcurrentActivationRejectsStaleWindow;ScopePayloadMismatchIsRejectedDeterministically;ProducerCannotProvideReactionTriggerTickOrRuleCost
32|FutureRequestKeepsOriginalSubmissionBoundaryAndEveryCheckpoint;FutureBucketsWithEqualCountsAndDifferentPayloadsHaveDifferentHashes;TickZeroReplayFromRecordedPlayerFactsReproducesIdenticalHashes
33|AllTerminalTransitionsUseSingleLifecycleEntry;TerminalPlanLeavesEverySchedulableCollection;StepEndHasNoActiveArtifactReferencingTerminalPlan;LethalHitTerminatesVictimsEditableLockedAndRunningPlans
34|PlanTerminalBeforeMovementEndKeepsLastCommittedCell;TerminalPlanNeverCommitsDestinationAtFormerEndTick;AttackUsesMovementSegmentInsteadOfVisualPosition
35|TerminalResolutionDoesNotRetroactivelyRemoveFrozenCurrentTickIntent;ClashTerminationRemovesUnfrozenAndFutureIntents
36|TerminalCleanupIsIdempotentAndFirstReasonWins;RepeatedEditableTerminalCleanupCannotReleaseReservationTwice;RepeatedLockedTerminalCleanupCannotInvokeBudgetRollback;OldCycleReservationSurvivesToTriggerButCannotRefundIntoNewCycle;TerminalCleanupDoesNotRewindIdsOrSequences
37|BlockOrDodgeDoesNotTerminateAttackingPlan;GuardDoesNotInvalidateContact;SuccessfulDodgeConsumesAndCompletesWithoutCancellingSourceAttack
38|IncompatibleHeaderIsRejectedBeforeCreatingWorld;PriorV3ReplayIsRejectedBeforeCreatingWorld
39|ReplayCannotAuthorizeAiOrSystemInputs;ShadowMirrorsPlayerRequestsButRebuildsAiAndSystemExactlyOnce
40|SceneHasExactlyOneBattleRuntimeBootstrap;CombatDemoNoLongerOwnsAutonomousUpdate;NewModeDisablesEveryRegisteredLegacyLogicWriter
41|MainSceneNewCompletesThreeColdBattlesWithPlayerCommandsAndOneEndFeedback;NewModeNeverCallsLegacyAdvanceTime
42|MainSceneShadowRetainsRealLegacyObservationsAndWritesNothingToPresentation;ShadowSimulationCannotBindViewsOrPlayFeedback;ShadowSimulationCannotMutateLegacyOrUnityState;BroadDifferenceAllowlistIsRejected
43|MainSceneNewCompletesThreeColdBattlesWithPlayerCommandsAndOneEndFeedback
44|SceneHasExactlyOneBattleRuntimeBootstrap
45|ReactionBeforeOpportunityExistsIsRejected;ReactionOptionDeadlineIsInclusiveAndExpiresAfterCommandPhase;ReactionAtOptionDeadlineIsAcceptedAndAfterDeadlineIsRejected
46|PlayerAndAiShareOneTickReactionIngressLead;AiAndPlayerAvailabilityUsesSameActionSetRules;AiCannotCreateOpportunityOrReactionPlanDirectly;PlayerAndAiReactionsUseSameProcessorAndPlanner;MainCanvasPlayerAttackFundsAiReactionAndFeedbackThroughProductionPorts
47|GuardPartiallyReducesEligibleDamageAndMomentumOnlyWhileActive;BlockZeroesAllEligibleDamageAndMomentumOnTriggerTick;GuardPhaseUsesItsActiveAndRecoveryBoundaries
48|DamageEventContainsPerChannelAndMomentumBeforeAfterValues;TrueDamageBypassesPassiveAndActionResistance;MixedSameTickBlockReportsPartialSuccessAndAccruesOnce
49|MoveSpeedOnlyChangesResolvedBaseStepTicks;MoveChainRecomputesProjectedStartPathDurationAndBudget;MoveBudgetUsesPathWeightUnitsInsteadOfEdgeCount;DodgeDoesNotCreateContinuousMovementOrInvulnerability
50|AdrenalinePersistsAcrossOtherUnitsWindowsAndOwnWindowClose;OwnersNextWindowOpenClearsAvailableAdrenalineBeforeCommands;OldCycleReservationSurvivesToTriggerButCannotRefundIntoNewCycle;AdrenalineDamageGainAggregatesOncePerUnitPerTick;AvailableAdrenalineIsCappedWithoutChangingReservations
51|DodgeContactSetsEventsAndRewardsArePermutationInvariant;BlockHandlesAllSameTickEligibleContactsIndependentOfOrder;ThreeWayAttackResultIsIndependentOfAllIntentPermutations;FinalCombatClassFileReplaysOneHundredTimesFromTickZero
52|CommandAndAiShadowProfileHasNoUnclassifiedDifference;FinalCombatClassFileReplaysOneHundredTimesFromTickZero;AdrenalineSnapshotsAreOrderedAndCarryCycleAndReservations;MainCanvasPlayerAttackFundsAiReactionAndFeedbackThroughProductionPorts
53|InputAfterFreezeTargetsNPlusTwoAndCannotEditNPlusOnePlan;ControlExpiringAtStartTickAllowsPlanToStartWithoutDelay;StartableCommitAtomicallyIncludesSpentLockedAndRunning
54|OrdinaryPlanUsesSingleIdentityAcrossEditableLockedAndRunning
55|SystemAutoDeferralRipplesOnlyEditableClosureRight;AutoDeferralNeverMovesLockedRunningOrReactionPlan;RemoveEditablePlanDoesNotPullLaterPlansLeft
56|MoveChainRecomputesProjectedStartPathDurationAndBudget;ScheduleEditFailureRollsBackBudgetLanePlanAndReservation
57|ScheduleEditsCompareExpectedRevisionToFrozenBatchBase;PlayerAndAiDisjointScheduleEditsFromSameSnapshotCanBothSucceed;FailedSystemAutoDeferralDoesNotIncrementRevision;MainSceneTimelineDiscardsDraftWhenPlanStartsWithoutRevisionChange
58|EditablePlanReservesBudgetWithoutSpendingIt;StartableCommitConvertsReservedBudgetToSpentExactlyOnce;SystemAutoDeferralAdjustsBudgetWithScheduleAndReservationAtomically;ReleasingClosedWindowReservationDoesNotReopenSubmissions;LockedOrRunningTerminalDoesNotRefundSpentBudget
59|UncommittedDragTrajectoryIsAbsentFromReplayInput;ConfirmedScheduleEditRecordsOnlyFinalOperations;MainSceneTimelineDragDeleteAndRevisionRebaseUseRealPlayerPort
60|MainSceneTimelineDragDeleteAndRevisionRebaseUseRealPlayerPort;TimelineCannotEditReactionLockedRunningOrTerminalPlans
61|VictoryUsesFactionIdNotControllerOrPlayerFlag;SimultaneousFactionEliminationProducesConfiguredDraw;OutOfObjectiveFactionDoesNotBlockEliminationVictoryOrLoseItsDisposition;ControllerRegistrationCannotMutateUnitFactionOrRelations
62|ControlExpiringAtStartTickAllowsPlanToStartWithoutDelay;TimedControlBlockerAutoDefersDueEditablePlan
63|DirectMoveUpdatesLastRequestedStartTickButAutoDeferralDoesNot;SystemAutoDeferralAdjustsBudgetWithScheduleAndReservationAtomically;ActionPlanAutoDeferredEventContainsCanonicalRippleAndRevision
64|NoFiniteRetryTickTerminatesPlanAsActorUnavailableAtStart;AutoDeferralLimitOrHorizonTerminatesWithExplicitReason;FailedAutoDeferralTerminalReleasesReservationExactlyOnce
65|AutoDeferralNeverMovesLockedRunningOrReactionPlan;ControlInvalidatesLockedReactionAsInterruptedByControl;ActionStateLaneDivergenceIsInvariantViolation;StartGateLockAndRunningCommitIsAtomic
66|FinalCombatClassFileReplaysOneHundredTimesFromTickZero;RecordedPlayerFactsExcludeAiAndSystemSources
67|FactionMatrixCanonicalizesEveryUnorderedPair;FactionMatrixHashIgnoresInputEnumerationOrder;BuilderRejectsMissingDuplicateReverseDuplicateOrDanglingFactionRelation
68|AreaAndPrimaryTargetPoliciesShareTheSameFactionRelationResolverAsTheCommandLayer;AreaOpportunityCandidatesUseSameFactionRelationMaskAsAttack;UiAndAiCandidateFiltersUseDecisionSnapshotFactionResolver;PrimaryTargetRelationMustBeAllowedByActionSpec;FinalCombatClassFileReplaysOneHundredTimesFromTickZero
69|DynamicSpawnFactionComesOnlyFromFixedOrExplicitInheritancePolicy
D1|DodgeOriginInvalidationFindsTransitiveMovesAcrossNonMovementPlans;DodgeMoveInvalidationReleasesReservedToEachOriginalWindowExactlyOnce;MainCanvasDodgeCancelsFutureMoveChainAndReleasesClosedOriginalWindow
D2|DodgeRelocationFailureDoesNotReleaseFutureMoveBudget;DodgeCommitFailureLeavesDependentMovesReservationsAndBudgetsUnchanged;CancelledDodgePreservesFutureMovementChain;DodgeWithoutPositionChangeDoesNotInvalidateMoves
D3|DodgeMovementTerminalIsIdempotentAndDoesNotIncrementScheduleRevision;DodgeOriginInvalidationFindsTransitiveMovesAcrossNonMovementPlans
D4|DodgeStillHitByAreaAttackInvalidatesOldOriginMovesWithoutSuccessReward;DodgeConfirmationShowsLogicProvidedConditionalMoveCancellationAndBudgetByWindow;DodgeMoveInvalidationDoesNotReopenClosedWindowOrTransferBudget;MainCanvasDodgeCancelsFutureMoveChainAndReleasesClosedOriginalWindow
D5|FinalCombatClassFileReplaysOneHundredTimesFromTickZero;DodgeContactSetsEventsAndRewardsArePermutationInvariant;ReactionCostsTimingsTagsAndAdrenalineRulesAffectDefinitionHash
'@ -split '\r?\n' | ForEach-Object {$p=$_ -split '\|',2; $map[$p[0]]=$p[1]}
$requirements=[ordered]@{}
Get-Content (Join-Path $recordRoot '../11-回放性能与收尾.md') | ForEach-Object {if($_ -match '^(\d+[A-E]?)\. (.+)$' -and [int]($_ -replace '^(\d+).*','$1') -le 69){$requirements[$Matches[1]]=$Matches[2]}}
$dodge=@(Get-Content (Join-Path $recordRoot '../11-回放性能与收尾.md') | Where-Object {$_ -match '^- (A→B→D|来源取消|预检之后|成功换位|相同输入)'})
for($i=0;$i -lt $dodge.Count;$i++){$requirements['D'+($i+1)]=$dodge[$i].Substring(2)}
if($requirements.Count -ne 80){throw "Expected 75 numbered requirements plus five Dodge supplements; got $($requirements.Count)"}
$rows=@(foreach($key in $requirements.Keys){
    if(!$map.ContainsKey($key)){throw "Unmapped criterion $key"}
    $proof=Resolve-Evidence $map[$key]
    $status='Verified'; $boundary='具体断言及其执行结果；组件级排列/失败边界与代表性全流水线文件回放组合构成证据，不宣称每条配置均已在正式场景复现。'
    if($key -eq '22'){$status='DeferredPerformance';$boundary='仅统计接口测试通过；稀疏/大型分量/共享目标的正式构建性能测量不在本轮验收范围。'}
    if($key -eq '44'){$status='DeferredMigration';$boundary='Legacy 未删除；基础唯一时钟检查不能代替删除后的扫描。'}
    if($key -eq '69'){$status='RuntimeGap';$boundary='只有固定/继承阵营策略定义、非法值拒绝与哈希测试；无战斗中动态单位创建事件、UnitSnapshot 或完整回放实现。'}
    if($key -eq '42'){$boundary='60 项逐字段兼容边界已分类；主场景 Shadow 零写入。生产比较策略及 60 项披露保留；未批准任何差异，未宣称新规则与 Legacy 完整等价。'}
    if($key -eq 'D1' -or $key -eq 'D4'){$boundary='实际主场景 Canvas、真实 Player 端口与生产 AI；组合夹具显式降低攻击冲击，正式网格/单位体积/180 Tick/hero，显式统一各朝向攻击范围并配置攻击 60/30、反应 1/30 时序、真实伤害入账；不冒充未改配置的正式主场景组合。'}
    [pscustomobject]@{Criterion=$key;Status=$status;Requirement=$requirements[$key];EvidenceMethods=$map[$key];ExecutedCases=$proof -join ';';Boundary=$boundary}
})
$rows | Export-Csv (Join-Path $recordRoot '11-final-acceptance-matrix.csv') -NoTypeInformation -Encoding utf8
$classification=Get-Content (Join-Path $recordRoot '11-shadow-classification.json') -Raw | ConvertFrom-Json
if($classification.entries.Count -ne 60 -or @($classification.entries | ForEach-Object {$_.legacyObjectPath+'#'+$_.field} | Sort-Object -Unique).Count -ne 60){throw 'Classification contains a duplicate or missing registration'}
$classified=@(foreach($entry in $classification.entries){$proof=Resolve-Evidence ($entry.tests -join ';'); [pscustomobject]@{CaseId=$classification.caseId;SceneCaseId=$classification.sceneCaseId;RulesVersion=$classification.rulesVersion;DefinitionHash=$classification.definitionHash;LegacyObjectPath=$entry.legacyObjectPath;Field=$entry.field;Category=$entry.category;PassedCases=$proof -join ';'}})
$classified | Export-Csv (Join-Path $recordRoot '11-shadow-classification-executed.csv') -NoTypeInformation -Encoding utf8
$mainMap=@{}
@'
可运行性|15
纯逻辑边界|methods:LogicAssemblyHasNoUnityEngineReference
Authoring 边界|methods:AuthoringAssemblyDoesNotReferenceAssemblyCSharp;CustomAssemblyDoesNotReferenceAssemblyCSharp
配置完整性|26;27
初始化确定性|17;28
离散几何确定性|methods:LegacyEvenAndOddBasesExpandToCanonicalTwelveDirections;ExplicitLegacyDirectionMustMatchCanonicalExpansion;LogicDirectionalGeometryHasNoRuntimeRotationDependency;LogicGridUsesCanonicalVolumeTableForEveryDirection
寻路成本确定性|13A;49
阵营模型与胜负|17;26;61;67;68
运行时唯一性|40
模式互斥|39;40;41;42
Shadow 隔离|42
切换可逆性|43
快照一致性|18;25;27;52
重复运行|13
开发者回放|13;18;28;32;38;39
窗口独立性|1;3
命令原子性|29;31;56;57;58
启动门禁|53;55;62;63;64;65
攻击时序|23;24;25
普通动作时序|47;49
反应时序|2;45;46
反应公平性|31;46
防御语义|8;9;37;47;48;D1;D2;D3;D4
伤害统一|10;48
肾上腺素周期|36;50;52
跨窗口交互|1;3;6;7;8;9;10
计划终态闭合性|33;34;35;36;58
战斗终态|16;19;20;21
结束幂等性|19;20
状态机|methods:StateTransitionMatrixAcceptsOnlyDeclaredTransitions;AutomaticAndExplicitTransitionsEmitEquivalentEvents;DeadIsTerminal;TimedTransitionUsesHalfOpenInterval
多方仲裁|5;6;7;51
同时强制位移|10A;10B;10C;10D;10E;13
时钟隔离|14
表现兼容|14;15;59;60;D4
性能|22
性能基准|22
'@ -split '\r?\n' | ForEach-Object {$p=$_ -split '\|',2;$mainMap[$p[0]]=$p[1]}
$mainSection=((Get-Content (Join-Path $recordRoot '../../优化方案-逻辑表现解耦与逻辑帧判定.md') -Raw) -split '### 每阶段验收标准',2)[1] -split '## 五、',2
$mainRows=@(foreach($line in ($mainSection[0] -split '\r?\n')) {
    if($line -notmatch '^\| ([^|]+) \| (.+) \|$'){continue}
    $label=$Matches[1].Trim();$requirement=$Matches[2];if($label -eq '验收项'){continue}
    if(!$mainMap.ContainsKey($label)){throw "Unmapped main-plan criterion: $label"}
    $refs=$mainMap[$label];$methods=if($refs.StartsWith('methods:')){$refs.Substring(8)}else{(($refs -split ';' | ForEach-Object {$map[$_]}) -join ';')}
    if($label -eq '初始化确定性'){$methods+=';RepeatedInitializationIsByteIdenticalAcrossOneHundredRuns'}
    $proof=Resolve-Evidence $methods
    $status='Verified';$boundary='主方案要求由具体组件断言、真实场景及代表性完整回放组合关联；不把自动化总数作为独立证据。'
    if($label -eq 'Shadow 隔离'){$status='DeferredCutover';$boundary='零写入和逐字段分类已验证，但生产 TemporarilyUncomparable 仍为 60，尚不满足切换前为 0 的严格门槛；不得将分类等同清零。'}
    if($label -eq '性能' -or $label -eq '性能基准'){$status='DeferredPerformance';$boundary='原生分配继续优化按用户决定暂缓；正式交互/冲突图/强制位移压力与 GC 频率/停顿仍未闭合。现有静态审计、统计接口与历史 IL2CPP 数据不能代替该门槛。'}
    [pscustomobject]@{Criterion=$label;Status=$status;Requirement=$requirement;FinalScenarioRefs=$refs;ExecutedCases=$proof -join ';';Boundary=$boundary}
})
if($mainRows.Count -ne 36){throw "Expected 36 main-plan criteria; got $($mainRows.Count)"}
$mainRows | Export-Csv (Join-Path $recordRoot '11-main-plan-acceptance-matrix.csv') -NoTypeInformation -Encoding utf8
$lines=[Collections.Generic.List[string]]::new();$lines.Add('# 最终验收矩阵（2026-10-10）');$lines.Add('')
$lines.Add('本次完成 1–69、10A–10E、13A 和五条 Dodge 补充，共 80 项的逐条证据映射。矩阵有明确的 Deferred/Gap 项，不能据测试全绿宣布整个优化任务验收通过。')
$lines.Add('');$lines.Add("最新执行：EditMode $($edit.'test-run'.passed)/$($edit.'test-run'.total)，PlayMode $($play.'test-run'.passed)/$($play.'test-run'.total)。所有关联证据方法都由脚本匹配实际 Passed 的测试案例；缺名、失败、跳过、重复分类或遗漏会令审计失败。")
$lines.Add('');$lines.Add('| 项 | 状态 | 具体证据方法 |');$lines.Add('|---|---|---|')
foreach($row in $rows){$lines.Add('| '+$row.Criterion+' | '+$row.Status+' | '+($row.EvidenceMethods -replace ';','；')+' |')}
$lines.Add('');$lines.Add('原生分配优化按用户决定延期。第 22 项正式性能测量、第 44 项旧实现删除属于后续整体门槛；第 69 项是实际运行实现缺口，不是“测试名称不同”。60 项分类审计完成，不删除旧观测登记或批量批准差异。完整要求、执行案例全名和每行证据边界见 CSV。')
$lines.Add('');$lines.Add('## 主方案每阶段验收标准（36 项）');$lines.Add('')
$lines.Add('主方案另要求切换前暂不可比较字段为 0。本轮完成分类，不满足该严格切换门槛；主场景继续保留 Legacy 默认值。逐项原文、关联场景编号和实际案例见 11-main-plan-acceptance-matrix.csv。')
$lines.Add('');$lines.Add('| 主方案验收项 | 状态 | 最终场景编号或额外测试 |');$lines.Add('|---|---|---|')
foreach($row in $mainRows){$lines.Add('| '+$row.Criterion+' | '+$row.Status+' | '+($row.FinalScenarioRefs -replace ';','；')+' |')}
$lines | Set-Content (Join-Path $recordRoot '11-最终验收矩阵-2026-10-10.md') -Encoding utf8
Write-Output "Criteria=$($rows.Count); classified=$($classified.Count); edit=$($edit.'test-run'.passed); play=$($play.'test-run'.passed)"
$rows | Group-Object Status | Select-Object Name,Count
