$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$recordPath = '优化任务/执行记录/主动接近目标-2026-11-11'
Push-Location -LiteralPath $repoPath
try {
    unity test . --mode EditMode --output "$recordPath/full-editmode.xml" --timeout 1200 --format json -- -nographics -logFile "$recordPath/full-editmode.log"
    if ($LASTEXITCODE -ne 0) { throw 'EditMode validation failed' }
    $sceneFilter = 'FormalAiSceneTests;ShadowSplitLifecycleTests;Task10WiredPresentationTests;Task11ProductionSceneTests;Task11ShadowClassificationTests;RuntimeOwnershipTests.NewModeNeverCallsLegacyAdvanceTime;RuntimeOwnershipTests.ShadowModeKeepsLegacyAsOnlyAuthority;RuntimeOwnershipTests.MainSceneBattleDefinitionMatchesTask02BFrozenHash'
    unity test . --mode PlayMode --filter $sceneFilter --output "$recordPath/production-shadow-playmode.xml" --timeout 900 --format json -- -nographics -logFile "$recordPath/production-shadow-playmode.log"
    if ($LASTEXITCODE -ne 0) { throw 'Scene validation failed' }
    unity run . --timeout 1200 --format json -- -nographics -executeMethod ProjectHero.Editor.ApproachGoalTools.Run -logFile "$recordPath/run.log"
    if ($LASTEXITCODE -ne 0) { throw 'Native generation failed' }
    unity run . --timeout 1800 --format json -- -nographics -executeMethod ProjectHero.Editor.ApproachGoalTools.Audit -logFile "$recordPath/audit.log"
    if ($LASTEXITCODE -ne 0) { throw 'Native audit failed' }
}
finally { Pop-Location }
