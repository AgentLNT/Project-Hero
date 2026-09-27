param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string]$OutDir = "",
    [string]$Filter = "",
    [switch]$CompileOnly,
    [string]$ResultsFile = ""
)

# Task 06 offline compile + run harness for the PURE logic test assembly.
#
# Difference from 05-offline-harness.ps1: that one targets ProjectHero.Authoring.Tests,
# whose fixtures load real Unity assets through Resources.LoadAll, so it can only COMPILE.
# ProjectHero.Logic.Tests (Assets\Tests\EditMode\Logic) references nothing but
# ProjectHero.Logic (pure C#, noEngineReferences=true) + nunit.framework, so the suite can be
# compiled with the editor's Roslyn AND executed under the editor's bundled mono.
#
# Scope discipline: this harness is an *iteration accelerator only*. Every pass/fail claim that
# enters the hand-off record must be re-confirmed through the harness `unity_cli` test entry
# (EditMode/PlayMode). It never substitutes for the Unity Test Runner evidence.
#
# Default OutDir is the session temp dir: DLLs are reproducible intermediates, not deliverables.

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $env:TEMP "t06-harness" }

$editorData = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Data"
$sdk        = Join-Path $editorData "DotNetSdk"
$dotnet     = Join-Path $sdk "dotnet.exe"
$cscPath    = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$refDir     = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"
$mono       = Join-Path $editorData "MonoBleedingEdge\bin\mono.exe"
$nunit      = Join-Path $ProjectRoot "Library\PackageCache\com.unity.ext.nunit@0198eae3b53e\net472\unity-custom\nunit.framework.dll"

foreach ($p in @($dotnet, $cscPath, $refDir, $mono, $nunit)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "missing prerequisite: $p" }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$script:mscorlibRefs = @(Get-ChildItem -LiteralPath $refDir -Filter *.dll | ForEach-Object { $_.FullName })

function New-Assembly {
    param([string]$Name, [string[]]$Sources, [string[]]$ExtraRefs, [string]$OutFile, [string]$Target = "library")
    if ($null -eq $Sources -or $Sources.Length -eq 0) { throw "no sources for $Name" }

    $rsp = Join-Path $OutDir "$Name.rsp"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("-target:$Target")
    $lines.Add("-langversion:9.0")
    $lines.Add("-nullable:disable")
    $lines.Add("-nologo")
    $lines.Add("-nostdlib+")
    $lines.Add("-nowarn:1701,1702,1591,0618")
    $lines.Add("-out:`"$OutFile`"")
    $lines.Add("-define:UNITY_INCLUDE_TESTS")
    foreach ($r in $script:mscorlibRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($r in $ExtraRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($s in $Sources) { $lines.Add("`"$s`"") }
    Set-Content -LiteralPath $rsp -Value $lines -Encoding UTF8

    $output = & $dotnet $cscPath "@$rsp" 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "=== $Name COMPILE FAILED (exit $code) ==="
        $output | ForEach-Object { Write-Host $_ }
        throw "$Name failed to compile"
    }
    Write-Host ("OK  {0}  ({1} sources)" -f $Name, $Sources.Length)
}

function Get-Sources([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })
}

$logicDll  = Join-Path $OutDir "ProjectHero.Logic.dll"
$testsDll  = Join-Path $OutDir "ProjectHero.Logic.Tests.dll"
$runnerExe = Join-Path $OutDir "T06Runner.exe"

New-Assembly -Name "Logic" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Logic")) `
    -ExtraRefs @() -OutFile $logicDll

$testDir = Join-Path $ProjectRoot "Assets\Tests\EditMode\Logic"
New-Assembly -Name "LogicTests" -Sources (Get-Sources $testDir) `
    -ExtraRefs @($nunit, $logicDll) -OutFile $testsDll

if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "06-nunit-runner.cs"))) {
    throw "missing 06-nunit-runner.cs next to this script"
}
$runnerSrc = Join-Path $OutDir "T06Runner.cs"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "06-nunit-runner.cs") -Destination $runnerSrc -Force
New-Assembly -Name "Runner" -Sources @($runnerSrc) `
    -ExtraRefs @($nunit, $testsDll, $logicDll) -OutFile $runnerExe -Target "exe"

if ($CompileOnly) { Write-Host "COMPILE ONLY: OK"; exit 0 }

foreach ($dep in @($nunit, $testsDll, $logicDll)) {
    $target = Join-Path $OutDir (Split-Path -Leaf $dep)
    if ([System.IO.Path]::GetFullPath($dep) -ne [System.IO.Path]::GetFullPath($target)) {
        Copy-Item -LiteralPath $dep -Destination $target -Force
    }
}

if ([string]::IsNullOrWhiteSpace($ResultsFile)) { $ResultsFile = Join-Path $OutDir "results.xml" }

$runnerArgs = @($runnerExe, $testsDll, $ResultsFile)
if (-not [string]::IsNullOrWhiteSpace($Filter)) { $runnerArgs += $Filter }

Write-Host "running tests ..."
& $mono $runnerArgs
$testExit = $LASTEXITCODE
Write-Host "runner exit=$testExit  results=$ResultsFile"
exit $testExit
