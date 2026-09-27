param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string]$OutDir = "",
    [string]$Filter = "",
    [switch]$CompileOnly,
    [string]$ResultsFile = "",
    [string]$TestDir = "Assets\Tests\EditMode",
    [string]$ExcludePattern = "RuntimeOwnershipSceneWiringTests|AssemblyBoundaryAuthoringTests|[\\/]Compatibility[\\/]"
)

# Task 05 offline compile-verification chain.
#
# Why it exists: in this environment Unity's package-manager IPC initialisation fails
# (see 05-jiaojie-jilu.md section 17.2), so the Unity Test Runner is unusable.
# This script uses the Roslyn compiler that ships inside the Unity editor to build each
# asmdef into a real assembly, giving offline *compile-level* verification.
#
# It compiles only; it never executes tests. Pass/fail must be re-judged in a working Unity.
#
# Default OutDir is the current session temp directory: the DLLs are reproducible
# intermediates and must not be committed. Pass -OutDir explicitly to keep evidence,
# then clean it up yourself.

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $env:TEMP "t05-harness" }

$editorData = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Data"
$sdk        = Join-Path $editorData "DotNetSdk"
$dotnet     = Join-Path $sdk "dotnet.exe"
$cscPath    = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$refDir     = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"
$engineDir  = Join-Path $editorData "Managed"
$mono       = Join-Path $editorData "MonoBleedingEdge\bin\mono.exe"
$nunit      = Join-Path $ProjectRoot "Library\PackageCache\com.unity.ext.nunit@0198eae3b53e\net472\unity-custom\nunit.framework.dll"

foreach ($p in @($dotnet, $cscPath, $refDir, $engineDir, $mono, $nunit)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "missing prerequisite: $p" }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$script:mscorlibRefs = @(Get-ChildItem -LiteralPath $refDir -Filter *.dll | ForEach-Object { $_.FullName })

$script:engineRefs = @(
    (Join-Path $engineDir "UnityEngine\UnityEngine.CoreModule.dll")
) | Where-Object { Test-Path -LiteralPath $_ }

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
    $lines.Add("-nowarn:1701,1702,1591")
    $lines.Add("-out:`"$OutFile`"")
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

function Get-Sources([string]$dir, [string]$excludePattern) {
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    $items = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.cs)
    if (-not [string]::IsNullOrWhiteSpace($excludePattern)) {
        $items = @($items | Where-Object { $_.FullName -notmatch $excludePattern })
    }
    return @($items | ForEach-Object { $_.FullName })
}

$gridDll      = Join-Path $OutDir "ProjectHero.Grid.dll"
$logicDll     = Join-Path $OutDir "ProjectHero.Logic.dll"
$authoringDll = Join-Path $OutDir "ProjectHero.Authoring.dll"
$testsDll     = Join-Path $OutDir "ProjectHero.Authoring.Tests.dll"
$runnerExe    = Join-Path $OutDir "T05Runner.exe"

New-Assembly -Name "Grid" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Grid") "") `
    -ExtraRefs $script:engineRefs -OutFile $gridDll

New-Assembly -Name "Logic" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Logic") "") `
    -ExtraRefs @($gridDll) -OutFile $logicDll

$authoringDir = Join-Path $ProjectRoot "Assets\Scripts\Authoring"
if (Test-Path -LiteralPath $authoringDir) {
    New-Assembly -Name "Authoring" -Sources (Get-Sources $authoringDir "") `
        -ExtraRefs (@($gridDll, $logicDll) + $script:engineRefs) -OutFile $authoringDll
}

$testSources = Get-Sources (Join-Path $ProjectRoot $TestDir) $ExcludePattern
New-Assembly -Name "AuthoringTests" -Sources $testSources `
    -ExtraRefs (@($gridDll, $logicDll, $authoringDll, $nunit) + $script:engineRefs) -OutFile $testsDll

if ($CompileOnly) { Write-Host "COMPILE ONLY: OK"; exit 0 }

# The reflection runner below cannot actually execute the EditMode suite in this environment:
# the fixtures load real Unity assets through Resources.LoadAll, which needs the Unity runtime.
# It is kept so that a future environment (or an asset-free fixture) can use it directly.
$runnerSrc = Join-Path $OutDir "T05Runner.cs"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "05-reflect-runner.cs") -Destination $runnerSrc -Force
New-Assembly -Name "Runner" -Sources @($runnerSrc) `
    -ExtraRefs @($nunit, $testsDll, $logicDll, $gridDll, $authoringDll) -OutFile $runnerExe -Target "exe"

# Mono only probes the application directory by default, so stage every runtime dependency there.
$runtimeDeps = @($nunit) + $script:engineRefs + @($testsDll, $logicDll, $gridDll, $authoringDll)
foreach ($dep in $runtimeDeps) {
    if (Test-Path -LiteralPath $dep) {
        Copy-Item -LiteralPath $dep -Destination (Join-Path $OutDir (Split-Path -Leaf $dep)) -Force
    }
}

if ([string]::IsNullOrWhiteSpace($ResultsFile)) { $ResultsFile = Join-Path $OutDir "results.xml" }

$runnerArgs = @($runnerExe, $testsDll, $ResultsFile)
if (-not [string]::IsNullOrWhiteSpace($Filter)) { $runnerArgs += $Filter }

Write-Host "running tests ..."
& $mono @runnerArgs
$testExit = $LASTEXITCODE
Write-Host "runner exit=$testExit  results=$ResultsFile"
exit $testExit
