param([ValidateSet('info','reference','run','audit')][string]$Mode = 'info')
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$assemblyPath = & (Join-Path $PSScriptRoot 'build.ps1')
$logPath = Join-Path $PSScriptRoot ("../standalone-$Mode.log")
Push-Location -LiteralPath $repoPath
try {
    & dotnet exec $assemblyPath $Mode 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) { throw "Standalone $Mode failed: $LASTEXITCODE" }
}
finally { Pop-Location }
