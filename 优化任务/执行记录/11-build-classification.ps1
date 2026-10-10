$ErrorActionPreference = 'Stop'
$recordRoot = $PSScriptRoot
$catalog = @{}
function Register-Evidence([string[]]$Fields, [string]$Category, [string]$Source, [string[]]$Tests) {
    foreach ($field in $Fields) {
        if ($catalog.ContainsKey($field)) { throw "Duplicate classification: $field" }
        $catalog[$field] = @{category=$Category; newSource=$Source; tests=$Tests}
    }
}
Register-Evidence @('availableAdrenaline') ReplacedRepresentation 'Assets/Scripts/Logic/Resources/AdrenalineLedger.cs' @('AdrenalinePersistsAcrossOtherUnitsWindowsAndOwnWindowClose','OwnersNextWindowOpenClearsAvailableAdrenalineBeforeCommands')
Register-Evidence @('staminaQ10') RemovedResource 'Assets/Scripts/Logic/Snapshots/LogicSnapshot.cs' @('EveryFullShadowRegistrationHasExactClassificationAndExecutableEvidence')
Register-Evidence @('battleEnd.isEnded','battleEnd.resultCode','units[i].state') ReplacedRepresentation 'Assets/Scripts/Core/Compatibility/Authoring/BattleSimulationSourceFactory.cs' @('ShadowStateFieldsAreReadLiveFromLegacySceneAndCanFail','StateDeathAndVictoryShadowProfileHasNoUnclassifiedDifference')
Register-Evidence @('effects.count') ReplacedRepresentation 'Assets/Scripts/Logic/Status/BuffSystem.cs' @('EffectIdsAreUniqueAndMonotonicAcrossUnitsAndSpecs','InsertionOrderDoesNotChangeOutcomeOrEventSequence')
Register-Evidence @('plans.count','scheduledEventCount') ReplacedRepresentation 'Assets/Scripts/Logic/Timeline/ActionPlanRegistry.cs' @('ActionPlanAndLaneShadowProfileHasNoUnclassifiedDifference','TerminalPlanLeavesEverySchedulableCollection')
Register-Evidence @('rng.state') NewRule 'Assets/Scripts/Logic/Determinism/DeterministicRng.cs' @('CommandAndAiShadowProfileHasNoUnclassifiedDifference','TickZeroReplayFromRecordedPlayerFactsReproducesIdenticalHashes')
Register-Evidence @('intents.count') ReplacedRepresentation 'Assets/Scripts/Logic/Simulation/BattleSimulation.cs' @('CommandAndAiShadowProfileHasNoUnclassifiedDifference','ImpactTickTransitionsToRecoveryAndStillEmitsIntent')
Register-Evidence @('plans[i].state','plans[i].startTick','plans[i].lockedAtTick','plans[i].automaticDeferralCount','plans[i].impactTick','plans[i].submittedWindowId','plans[i].terminationReason','actorLanes[i].pendingPlanCount','actorLanes[i].locked','reactionOpportunities[i].state','nextReactionOpportunityId','terminalPlanRecordCount') NewRule 'Assets/Scripts/Logic/Timeline/ActionScheduleAuthority.cs' @('ActionPlanAndLaneShadowProfileHasNoUnclassifiedDifference','CommandAndAiShadowProfileHasNoUnclassifiedDifference')
Register-Evidence @('occupancy[i].anchor','movementSegments[i].from','movementSegments[i].to','movementSegments[i].endTick','reservations[i].cell','movementCommit[i].expectedFrom','movementTerminal[i].segments','movementTerminal[i].reservations') NewRule 'Assets/Scripts/Logic/Movement/LogicGridMovementAuthority.cs' @('LogicGridMovementAndReservationShadowProfileHasNoUnclassifiedDifference','TerminalPlanNeverCommitsDestinationAtFormerEndTick')
Register-Evidence @('currentWindowId','windows.count','windows[i].openedAtTick','windows[i].isOpen','windows[i].isAcceptingSubmissions','windows[i].closeReason','windows[i].totalBudgetTicks','windows[i].reservedBudgetTicks','windows[i].spentBudgetTicks','windows[i].availableBudgetTicks','windows[i].reservations','windows[i].budgetIdentity','nextWindowTick','lastClosedWindowId','resources.turnBudgetAvailable','resources.turnBudgetReserved','resources.turnBudgetSpent','resources.turnBudgetIdentity') NewRule 'Assets/Scripts/Logic/Turns/TurnWindowManager.cs' @('TurnWindowBudgetAndAuthorityShadowProfileHasNoUnclassifiedDifference','BudgetLedgerInvariantHoldsAcrossReserveAdjustAndRelease','ReleasingClosedWindowReservationDoesNotReopenSubmissions')
Register-Evidence @('concurrentAction.hasActiveAuthorization','concurrentAction.windowId','concurrentAction.playerUnitId','resources.metaResource') NewRule 'Assets/Scripts/Logic/Simulation/BattleSimulation.cs' @('TurnWindowBudgetAndAuthorityShadowProfileHasNoUnclassifiedDifference','ConcurrentActivationRejectsStaleWindow','ConcurrentCostComesFromAbilityDefinition')
Register-Evidence @('adrenaline[unitId].cycleId','adrenaline[unitId].reservations','adrenaline[unitId].reservedTotal','units[i].adrenalineCycleId') NewRule 'Assets/Scripts/Logic/Resources/AdrenalineLedger.cs' @('TurnWindowBudgetAndAuthorityShadowProfileHasNoUnclassifiedDifference','OldCycleReservationSurvivesToTriggerButCannotRefundIntoNewCycle')
Register-Evidence @('plans[i].budgetCostTicks','plans[i].reservedTurnBudgetTicks','plans[i].submittedWindowLedger') NewRule 'Assets/Scripts/Logic/Turns/TurnWindow.cs' @('TurnWindowBudgetAndAuthorityShadowProfileHasNoUnclassifiedDifference','CostIncreaseCannotTransferPlanBudgetToAnotherWindow','DodgeMoveInvalidationReleasesReservedToEachOriginalWindowExactlyOnce')
Register-Evidence @('stagedResolution.remainingHits') DiagnosticOnly 'Assets/Scripts/Logic/Interactions/StagedConflictResolution.cs' @('StagedResolutionRunsAllFivePhasesAndCommitsDamageOncePerUnit','TargetDamageAndImpulseCommitOnceInStableOrder')

$entries = @(Get-Content (Join-Path $recordRoot '11-completion-project-audit.txt') | Where-Object { $_ -like 'BLOCKED_TEMPORARY|*' } | ForEach-Object {
    $parts = $_ -split '\|',4; $key = $parts[1]; $index = $key.LastIndexOf('#'); $field = $key.Substring($index+1); $object = $key.Substring(0,$index)
    if (!$catalog.ContainsKey($field)) { throw "Unclassified field: $field" }
    $legacySource = if($object -match 'CombatUnit') {'Assets/Scripts/Core/Entities/CombatUnit.cs'}
        elseif($object -match 'BattleTimeline') {'Assets/Scripts/Core/Timeline/BattleTimeline.cs'}
        elseif($object -match 'BattleManager') {'Assets/Scripts/Core/Gameplay/BattleManager.cs'}
        elseif($object -match 'GridManager') {'Assets/Scripts/Core/Grid/GridManager.cs'}
        elseif($object -match 'Pathfinder') {'Assets/Scripts/Core/Pathfinding/Pathfinder.cs'}
        elseif($object -match 'UnitMovement') {'Assets/Scripts/Visuals/UnitMovement.cs'}
        elseif($object -match 'TacticsController') {'Assets/Scripts/Core/Gameplay/TacticsController.cs'} else {throw $object}
    [ordered]@{field=$field;legacyObjectPath=$object;category=$catalog[$field].category;reason=$parts[3];legacySource=$legacySource;newSource=$catalog[$field].newSource;tests=@($catalog[$field].tests)}
})
if($entries.Count -ne 60 -or $catalog.Count -ne 60) {throw "Inventory mismatch: rows=$($entries.Count), keys=$($catalog.Count)"}
$entries | ForEach-Object { if (!(Test-Path $_.legacySource) -or !(Test-Path $_.newSource)) {throw "Missing source: $($_.field)"} }
@{caseId='task11-final-classification';sceneCaseId='03B-shadow-equivalent-empty-ticks';rulesVersion='battle-def-v2-turn180';definitionHash='ed4c3e21b1488e60';entries=$entries} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $recordRoot '11-shadow-classification.json') -Encoding utf8
$entries | ForEach-Object { [pscustomobject]@{CaseId='task11-final-classification';SceneCaseId='03B-shadow-equivalent-empty-ticks';RulesVersion='battle-def-v2-turn180';DefinitionHash='ed4c3e21b1488e60';Field=$_.field;LegacyObjectPath=$_.legacyObjectPath;Category=$_.category;LegacySource=$_.legacySource;NewSource=$_.newSource;Tests=$_.tests -join ';';Reason=$_.reason} } | Export-Csv (Join-Path $recordRoot '11-shadow-classification.csv') -NoTypeInformation -Encoding utf8
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Shadow 60 项逐字段分类（2026-10-10）')
$lines.Add('')
$lines.Add('登记审计用例 task11-final-classification；实际主场景用例 03B-shadow-equivalent-empty-ticks；规则 battle-def-v2-turn180；正式定义 ed4c3e21b1488e60。作用域逐行写入 CSV，不批准其他用例、版本或字段。')
$lines.Add('')
$lines.Add('本表完成兼容边界的分类，不修改生产 Shadow 策略，不注册批准差异，不宣称旧实现与新规则完整等价。原来的 60 项登记保留为历史与实时对照的披露面；分类遗漏数应为 0，不能把“旧侧不存在”伪造成可采样值。')
$lines.Add('')
$lines.Add('NewRule：旧侧缺少同口径结构，按新规则及独立双世界/篡改负对照验证。ReplacedRepresentation：旧侧事实或结构存在，但量纲、生命周期或实体含义改变；共有可比较字段仍逐字段读取和比较。RemovedResource：主方案明确删除的体力，新快照/新资源模型无对应字段。DiagnosticOnly：新侧只读诊断，不参与快照哈希，实际事件/聚合提交另行验证。')
$lines.Add('')
$lines.Add('主场景真实 Shadow 零写入与对照结果见 11-shadow-scene-observations.txt；逐字段验证方法的实际执行结果由 11-final-acceptance-audit.ps1 核对最新 XML，不用类名或总通过数代替。仍可比较的 Tick、定义、槽位、阵营、生命、位置、朝向、旧状态标签及结束事实不被本表豁免。')
$lines.Add('')
$lines.Add('| 字段 | 分类 | 新规则证据（具体方法） |')
$lines.Add('|---|---|---|')
foreach($entry in $entries) { $lines.Add('| '+$entry.field+' | '+$entry.category+' | '+($entry.tests -join '；')+' |') }
$lines.Add('')
$lines.Add('完整旧对象路径、旧/新源码路径及逐项原因保存在 CSV/JSON 中。分类中的新结构不是被批准的差异；若新规则验证失败、基础设施差异、写入计数非零或共有字段出现未经解释的差异，仍阻止正式迁移。')
$lines | Set-Content (Join-Path $recordRoot '11-shadow-逐字段分类-2026-10-10.md') -Encoding utf8
Write-Output "Classification entries=$($entries.Count); approvals added=0"
