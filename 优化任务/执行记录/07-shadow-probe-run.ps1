# =============================================================================
#  Task 07 shadow-probe runner (ASCII only on purpose: PowerShell 5.1 reads
#  BOM-less .ps1 files as ANSI, so non-ASCII here would be mis-decoded).
#
#  What it does (offline; no Unity Editor, no package-manager IPC):
#    1. compiles Assets/Scripts/Logic                      -> Logic.dll
#    2. compiles Assets/Scripts/Grid                       -> Grid.dll
#    3. compiles Assets/Scripts/Core/Compatibility/Runtime -> Compat.dll
#    4. compiles the two probes in this folder + 06-nunit-runner.cs
#    5. runs the probes on Unity's bundled Mono + nunit.framework
#
#  Probes (read their headers for exactly what each one proves):
#    07-shadow-probe-detector.cs : ShadowDifferenceDetector.Task07TurnWindowFacts really
#                                  compares every declared field path (13 tamper probes) and
#                                  the policy registration counts are 10+12+8+29 = 59.
#    07-shadow-probe-scenario.cs : the real BattleSimulation + Step pipeline mechanics the
#                                  PlayMode case relies on (window open clears adrenaline,
#                                  reserve->spent, concurrent authority, cross-window
#                                  invariance) plus the two production wiring gaps D-A / D-C.
#
#  Preconditions: Unity 6000.6.2f1 installed at $EditorRoot; PowerShell execution policy is
#  disabled by default on this machine -> call it as:
#     Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
#     & '.\07-shadow-probe-run.ps1'
#
#  Exit codes: 0 = both probes passed; 2 = compile failure; 1 = a probe failed.
# =============================================================================

param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string]$EditorRoot = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $env:TEMP "t07-shadow-probe" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$editorData = Join-Path $EditorRoot "Editor\Data"
$dotnet = Join-Path $editorData "DotNetSdk\dotnet.exe"
$csc = Join-Path $editorData "DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll"
$nsRefDir = Join-Path $editorData "DotNetSdk\packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"
$engineDir = Join-Path $editorData "Managed\UnityEngine"
$mono = Join-Path $editorData "MonoBleedingEdge\bin\mono.exe"
$nunit = Join-Path $ProjectRoot "Library\PackageCache\com.unity.ext.nunit@0198eae3b53e\net472\unity-custom\nunit.framework.dll"

$missing = @()
foreach ($p in @($dotnet, $csc, $nsRefDir, $engineDir, $mono, $nunit)) {
    if (-not (Test-Path -LiteralPath $p)) { $missing += $p }
}
if ($missing.Count -gt 0) {
    Write-Host "PREREQ FAILED (adjust -EditorRoot / -ProjectRoot):"
    foreach ($m in $missing) { Write-Host "  - $m" }
    exit 4
}

$nsRefs = @(Get-ChildItem -LiteralPath $nsRefDir -Filter *.dll | ForEach-Object { $_.FullName })
$engineRefs = @(Get-ChildItem -LiteralPath $engineDir -Filter *.dll | ForEach-Object { $_.FullName })

function Compile-Assembly {
    param([string]$Name, [string[]]$Sources, [string[]]$ExtraRefs, [string]$Target = "library", [string[]]$Defines = @())
    $rsp = Join-Path $OutDir "$Name.rsp"
    $extension = "dll"
    if ($Target -eq "exe") { $extension = "exe" }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("-target:$Target")
    $lines.Add("-langversion:9.0")
    $lines.Add("-nullable:disable")
    $lines.Add("-nologo")
    $lines.Add("-nostdlib+")
    $lines.Add("-nowarn:1701,1702,1591,0618,0067")
    $lines.Add("-out:`"$(Join-Path $OutDir "$Name.$extension")`"")
    foreach ($d in $Defines) { $lines.Add("-define:$d") }
    foreach ($r in $nsRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($r in $ExtraRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($s in $Sources) { $lines.Add("`"$s`"") }
    Set-Content -LiteralPath $rsp -Value $lines -Encoding UTF8

    $output = & $dotnet $csc "@$rsp" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "=== $Name COMPILE FAILED ==="
        foreach ($line in $output) { Write-Host $line }
        return $false
    }
    Write-Host ("OK  {0} ({1} sources)" -f $Name, $Sources.Length)
    return $true
}

function Get-Sources([string]$dir) {
    return @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })
}

$logicDll = Join-Path $OutDir "Logic.dll"
$gridDll = Join-Path $OutDir "Grid.dll"
$compatDll = Join-Path $OutDir "Compat.dll"

$ok = Compile-Assembly -Name "Logic" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Logic")) -ExtraRefs @()
if ($ok) { $ok = Compile-Assembly -Name "Grid" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Grid")) -ExtraRefs $engineRefs }
if ($ok) { $ok = Compile-Assembly -Name "Compat" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Core\Compatibility\Runtime")) -ExtraRefs (@($logicDll, $gridDll) + $engineRefs) }
if (-not $ok) { exit 2 }

$probeSources = @(
    (Join-Path $PSScriptRoot "07-shadow-probe-detector.cs"),
    (Join-Path $PSScriptRoot "07-shadow-probe-scenario.cs")
)
$probeDll = Join-Path $OutDir "Probe.dll"
if (-not (Compile-Assembly -Name "Probe" -Sources $probeSources -ExtraRefs (@($nunit, $logicDll, $compatDll) + $engineRefs))) { exit 2 }

$runnerSource = Join-Path $OutDir "T06Runner.cs"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "06-nunit-runner.cs") -Destination $runnerSource -Force
if (-not (Compile-Assembly -Name "T07ProbeRunner" -Sources @($runnerSource) -ExtraRefs @($nunit, $logicDll, $compatDll, $probeDll) -Target "exe")) { exit 2 }

Copy-Item -LiteralPath $nunit -Destination (Join-Path $OutDir "nunit.framework.dll") -Force
$results = Join-Path $OutDir "07-shadow-probe-results.xml"
& $mono (Join-Path $OutDir "T07ProbeRunner.exe") $probeDll $results
$runnerExit = $LASTEXITCODE
Write-Host "runner exit=$runnerExit results=$results"
if ($runnerExit -ne 0) { exit 1 }
exit 0
