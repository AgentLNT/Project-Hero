$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$evidencePath = '优化任务/执行记录/进攻防御评价-2026-11-09'
Push-Location -LiteralPath $repoPath
try {
    unity test . --mode EditMode --output "$evidencePath/full-editmode.xml" --timeout 1200 --format json -- -nographics -logFile "$evidencePath/full-editmode.log"
    if ($LASTEXITCODE -ne 0) { throw 'EditMode validation failed' }
    $sceneFilter = 'FormalAiSceneTests;ShadowSplitLifecycleTests;Task10WiredPresentationTests;Task11ProductionSceneTests;Task11ShadowClassificationTests;RuntimeOwnershipTests.NewModeNeverCallsLegacyAdvanceTime;RuntimeOwnershipTests.ShadowModeKeepsLegacyAsOnlyAuthority;RuntimeOwnershipTests.MainSceneBattleDefinitionMatchesTask02BFrozenHash'
    unity test . --mode PlayMode --filter $sceneFilter --output "$evidencePath/production-shadow-playmode.xml" --timeout 900 --format json -- -nographics -logFile "$evidencePath/production-shadow-playmode.log"
    if ($LASTEXITCODE -ne 0) { throw 'Scene validation failed' }
    unity run . --timeout 1800 --format json -- -nographics -executeMethod ProjectHero.Editor.AttackDefenseEvaluationTools.Run -logFile "$evidencePath/run.log"
    if ($LASTEXITCODE -ne 0) { throw 'Native experiment failed' }
    unity run . --timeout 2400 --format json -- -nographics -executeMethod ProjectHero.Editor.AttackDefenseEvaluationTools.Audit -logFile "$evidencePath/audit.log"
    if ($LASTEXITCODE -ne 0) { throw 'Independent native audit failed' }
}
finally { Pop-Location }
