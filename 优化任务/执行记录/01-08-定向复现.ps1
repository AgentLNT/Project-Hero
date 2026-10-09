# Read-only audit runner. No Assets or production sources are changed.
[CmdletBinding()]
param([string]$ResultsFile = '')
$ErrorActionPreference = 'Stop'
$auditProject = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$auditOut = Join-Path $auditProject 'Logs/Task01-08Audit'
$auditHarness = Join-Path $PSScriptRoot '07-offline-harness.ps1'
& $auditHarness -ProjectRoot $auditProject -OutDir $auditOut -CompileOnly
if ($LASTEXITCODE -ne 0) { throw 'Logic compilation failed' }
$auditSource = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '01-08-*.cs')
if ($auditSource.Count -ne 1) { throw 'Expected exactly one audit source' }
$auditExe = Join-Path $auditOut 'AuditProbe.exe'
$auditRsp = Join-Path $auditOut 'AuditProbe.rsp'
$auditLines = @(Get-Content -LiteralPath (Join-Path $auditOut 'Runner.rsp') |
    Where-Object { $_ -notmatch '^-out:' -and $_ -notmatch '^".*\.cs"$' })
$auditLines += '-out:"' + $auditExe + '"'
$auditLines += '-r:"' + (Join-Path $auditOut 'ProjectHero.Logic.Tests.dll') + '"'
$auditLines += '"' + $auditSource[0].FullName + '"'
Set-Content -LiteralPath $auditRsp -Value $auditLines -Encoding UTF8
$auditEditorData = 'C:/Program Files/Unity/Hub/Editor/6000.6.2f1/Editor/Data'
& (Join-Path $auditEditorData 'DotNetSdk/dotnet.exe') `
    (Join-Path $auditEditorData 'DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll') "@$auditRsp"
if ($LASTEXITCODE -ne 0) { throw 'Audit compilation failed' }
$auditNunitRef = @($auditLines | Where-Object { $_ -match 'nunit\.framework\.dll' })[0]
$auditNunitPath = $auditNunitRef.Substring(4).TrimEnd('"')
$auditPreviousMonoPath = $env:MONO_PATH
try {
    $env:MONO_PATH = Split-Path -Parent $auditNunitPath
    if ([string]::IsNullOrWhiteSpace($ResultsFile)) {
        $auditExisting = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '01-08-*.txt')
        $ResultsFile = if ($auditExisting.Count -eq 1) { $auditExisting[0].FullName }
            else { Join-Path $PSScriptRoot '01-08-audit-probe-results.txt' }
    }
    & (Join-Path $auditEditorData 'MonoBleedingEdge/bin/mono.exe') $auditExe |
        Tee-Object -FilePath $ResultsFile
    if ($LASTEXITCODE -ne 0) { throw 'Audit runner failed before observations completed' }
} finally {
    $env:MONO_PATH = $auditPreviousMonoPath
}
# Exit 0 means observations were collected, not that the defects have been fixed.
