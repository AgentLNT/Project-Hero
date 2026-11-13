param([string]$PythonExecutable = 'python')
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$recordPath = '优化任务/执行记录/偏好互打长局-2026-11-11'
Push-Location -LiteralPath $repoPath
try {
    unity test . --mode EditMode --output "$recordPath/full-editmode.xml" --timeout 1200 --format json -- -nographics -logFile "$recordPath/full-editmode.log"
    if ($LASTEXITCODE -ne 0) { throw 'EditMode validation failed' }
    unity run . --timeout 600 --format json -- -nographics -executeMethod ProjectHero.Editor.FormalAiMatchupTools.Export -logFile "$recordPath/export.log"
    if ($LASTEXITCODE -ne 0) { throw 'Formal definition export failed' }
    & "$recordPath/harness/run.ps1" -Mode info
    & "$recordPath/harness/run.ps1" -Mode reference
    & "$recordPath/harness/run.ps1" -Mode run
    & "$recordPath/harness/run.ps1" -Mode audit
    $sceneFilter = 'FormalAiSceneTests;ShadowSplitLifecycleTests;Task10WiredPresentationTests;Task11ProductionSceneTests;Task11ShadowClassificationTests;RuntimeOwnershipTests.NewModeNeverCallsLegacyAdvanceTime;RuntimeOwnershipTests.ShadowModeKeepsLegacyAsOnlyAuthority;RuntimeOwnershipTests.MainSceneBattleDefinitionMatchesTask02BFrozenHash'
    unity test . --mode PlayMode --filter $sceneFilter --output "$recordPath/production-shadow-playmode.xml" --timeout 900 --format json -- -nographics -logFile "$recordPath/production-shadow-playmode.log"
    if ($LASTEXITCODE -ne 0) { throw 'Scene validation failed' }
    & $PythonExecutable "$recordPath/evidence.py" redact-logs
    if ($LASTEXITCODE -ne 0) { throw 'Log redaction failed' }
    & $PythonExecutable "$recordPath/evidence.py" analyze
    if ($LASTEXITCODE -ne 0) { throw 'Metric analysis failed' }
    & $PythonExecutable "$recordPath/check-evidence.py"
    if ($LASTEXITCODE -ne 0) { throw 'Independent evidence check failed' }
}
finally { Pop-Location }
