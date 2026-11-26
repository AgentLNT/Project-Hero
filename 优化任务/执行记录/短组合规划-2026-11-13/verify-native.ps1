param([switch]$FrozenMatrixOnly)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
Push-Location -LiteralPath $repoPath
try {
    $recordPath = '优化任务/执行记录/短组合规划-2026-11-13'
    # Use the archived matrix source in a separate project copy for this mode.
    if ($FrozenMatrixOnly) {
        unity run . --timeout 900 --format json -- -nographics -executeMethod ProjectHero.Editor.ShortPlanNativeValidation.Run -logFile "$recordPath/native-validation.log" *> "$recordPath/native-validation-cli.log"
        if ($LASTEXITCODE -ne 0) { throw 'Native matrix validation failed' }
        return
    }
    unity test . --mode EditMode --output "$recordPath/editmode.xml" --timeout 1200 --format json -- -nographics -logFile "$recordPath/editmode.log" *> "$recordPath/editmode-cli.log"
    if ($LASTEXITCODE -ne 0) { throw 'EditMode validation failed' }
    $sceneFilter = 'FormalAiSceneTests;ShadowSplitLifecycleTests;Task10WiredPresentationTests;Task11ProductionSceneTests;Task11ShadowClassificationTests;RuntimeOwnershipTests.NewModeNeverCallsLegacyAdvanceTime;RuntimeOwnershipTests.ShadowModeKeepsLegacyAsOnlyAuthority;RuntimeOwnershipTests.MainSceneBattleDefinitionMatchesTask02BFrozenHash'
    unity test . --mode PlayMode --filter $sceneFilter --output "$recordPath/production-shadow-playmode.xml" --timeout 900 --format json -- -nographics -logFile "$recordPath/production-shadow-playmode.log" *> "$recordPath/production-shadow-playmode-cli.log"
    if ($LASTEXITCODE -ne 0) { throw 'Production scene validation failed' }
} finally { Pop-Location }
