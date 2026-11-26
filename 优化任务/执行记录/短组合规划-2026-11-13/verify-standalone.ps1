# Reproduce the frozen matrix in an isolated workspace. Never overwrite sealed outputs or current source.
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$isolatedPath = Join-Path ([IO.Path]::GetTempPath()) ('ProjectHero-ShortPlan-Reproduction-' + [Guid]::NewGuid().ToString('N'))
$recordRelative = '优化任务/执行记录/短组合规划-2026-11-13'
$oldRelative = '优化任务/执行记录/温度采样与磨血防御-2026-11-13'
New-Item -ItemType Directory -Path $isolatedPath | Out-Null
Expand-Archive -LiteralPath "$PSScriptRoot/matrix-validation-source.zip" -DestinationPath $isolatedPath
$content = Get-Content -LiteralPath (Join-Path $repoPath '优化任务/执行记录/偏好互打长局-2026-11-11/before-content.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($relative in $content.Keys) {
    $source = Join-Path $repoPath $relative
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $content[$relative].ToLowerInvariant()) { throw "Formal content changed: $relative" }
    $target = Join-Path $isolatedPath $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$recordPath = Join-Path $isolatedPath $recordRelative
$oldPath = Join-Path $isolatedPath $oldRelative
New-Item -ItemType Directory -Path $recordPath -Force | Out-Null
New-Item -ItemType Directory -Path "$oldPath/baseline/cases" -Force | Out-Null
foreach ($folder in @('harness','formal','unity-reference','native-mechanisms')) {
    Copy-Item -LiteralPath "$PSScriptRoot/$folder" -Destination "$recordPath/$folder" -Recurse
}
Copy-Item -LiteralPath "$PSScriptRoot/analyze.py" -Destination "$recordPath/analyze.py"
foreach ($name in @('analyze.py','analysis.json','sealed-manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $repoPath "$oldRelative/$name") -Destination "$oldPath/$name"
}
Get-ChildItem -LiteralPath (Join-Path $repoPath "$oldRelative/baseline/cases") -Filter 'D-*' -File |
    Copy-Item -Destination "$oldPath/baseline/cases"
Push-Location -LiteralPath $isolatedPath
try {
    $dll = & "$recordPath/harness/build.ps1"
    foreach ($mode in @('mechanisms','legacy','run','audit','reference')) {
        & dotnet $dll $mode *> "$recordPath/$mode-reproduction.log"
        if ($LASTEXITCODE -ne 0) { throw "Standalone $mode failed; see $recordPath" }
    }
    foreach ($mode in @('analyze','outcomes','cross-runtime')) {
        python "$recordPath/analyze.py" $mode
        if ($LASTEXITCODE -ne 0) { throw "Evidence $mode failed; see $recordPath" }
    }
} finally { Pop-Location }
Write-Output "Reproduction evidence: $recordPath"
