$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
Push-Location -LiteralPath $repoPath
try {
    $recordPath = '优化任务/执行记录/温度采样与磨血防御-2026-11-13'
    unity test . --mode EditMode --output "$recordPath/full-editmode.xml" --timeout 1500 --format json -- -nographics -logFile "$recordPath/full-editmode.log" *> "$recordPath/full-editmode-cli.log"
    if ($LASTEXITCODE -ne 0) { throw 'EditMode validation failed' }
    unity run . --timeout 900 --format json -- -nographics -executeMethod ProjectHero.Editor.Task22NativeValidation.Run -logFile "$recordPath/native-mechanisms.log" *> "$recordPath/native-mechanisms-cli.log"
    if ($LASTEXITCODE -ne 0) { throw 'Native mechanism validation failed' }
    $sceneFilter = 'FormalAiSceneTests;ShadowSplitLifecycleTests;Task10WiredPresentationTests;Task11ProductionSceneTests;Task11ShadowClassificationTests;RuntimeOwnershipTests.NewModeNeverCallsLegacyAdvanceTime;RuntimeOwnershipTests.ShadowModeKeepsLegacyAsOnlyAuthority;RuntimeOwnershipTests.MainSceneBattleDefinitionMatchesTask02BFrozenHash'
    unity test . --mode PlayMode --filter $sceneFilter --output "$recordPath/production-shadow-playmode.xml" --timeout 900 --format json -- -nographics -logFile "$recordPath/production-shadow-playmode.log" *> "$recordPath/production-shadow-playmode-cli.log"
    if ($LASTEXITCODE -ne 0) { throw 'Production scene validation failed' }
} finally { Pop-Location }
