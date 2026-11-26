param(
    [Parameter(Mandatory)] [string] $DotNet,
    [Parameter(Mandatory)] [string] $Python,
    [ValidateSet('run','audit','legacy','reference','analysis')] [string] $Mode='audit'
)
$ErrorActionPreference='Stop'
$repoPath=(Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'sealed-manifest.json')) {
    throw 'Use an isolated copy and keep the original sealed directory unchanged.'
}
Push-Location -LiteralPath $repoPath
try {
    $env:HERO25_BATCH='formal'
    $env:HERO25_STAGES='Z,M,B'
    if($Mode -eq 'analysis') {
        & $Python (Join-Path $PSScriptRoot 'analyze.py')
    } else {
        $dll=& (Join-Path $PSScriptRoot 'harness/build.ps1')
        & $DotNet $dll $Mode
    }
    if($LASTEXITCODE -ne 0){throw "Verification failed: $Mode"}
} finally {
    Pop-Location
}
