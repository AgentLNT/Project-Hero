param([switch]$Calibration, [switch]$ExtraSeed)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
Push-Location -LiteralPath $repoPath
try {
    $recordPath = '优化任务/执行记录/温度采样与磨血防御-2026-11-13'
    $dll = & "$recordPath/harness/build.ps1"
    $env:HERO_MATCHUP_WORKERS = '4'
    $env:HERO22_STAGES = 'D'
    $env:HERO22_SEED_OFFSET = '0'
    if ($Calibration) {
        foreach ($configuration in @(@('greedy',0,1000), @('low-temperature',250,1000), @('high-temperature',2000,1000), @('top-p-95',1000,950), @('top-p-90',1000,900))) {
            $env:HERO22_BATCH = $configuration[0]; $env:HERO22_TEMPERATURE = [string]$configuration[1]; $env:HERO22_TOPP = [string]$configuration[2]
            New-Item -ItemType Directory -Path "$recordPath/$env:HERO22_BATCH" -Force | Out-Null
            Copy-Item "$recordPath/harness/build-manifest.json" "$recordPath/$env:HERO22_BATCH/build-manifest.json"
            dotnet $dll scan *> "$recordPath/$env:HERO22_BATCH-run.log"
            if ($LASTEXITCODE -ne 0) { throw 'Calibration generation failed' }
            dotnet $dll audit *> "$recordPath/$env:HERO22_BATCH-audit.log"
            if ($LASTEXITCODE -ne 0) { throw 'Calibration audit failed' }
        }
    }
    if ($ExtraSeed) {
        $env:HERO22_BATCH = 'extra-seed'; $env:HERO22_SEED_OFFSET = '1'; $env:HERO22_TEMPERATURE = '1000'; $env:HERO22_TOPP = '1000'
        New-Item -ItemType Directory -Path "$recordPath/extra-seed" -Force | Out-Null
        Copy-Item "$recordPath/harness/build-manifest.json" "$recordPath/extra-seed/build-manifest.json"
        dotnet $dll run *> "$recordPath/extra-seed-run.log"
        if ($LASTEXITCODE -ne 0) { throw 'Extra seed generation failed' }
        dotnet $dll audit *> "$recordPath/extra-seed-audit.log"
        if ($LASTEXITCODE -ne 0) { throw 'Extra seed audit failed' }
    }
} finally { Pop-Location }
