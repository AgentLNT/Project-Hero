# =============================================================================
#  Task 07 sub-agent verification helper (ASCII only on purpose: PS 5.1 safe).
#
#  Compiles Assets/Scripts/Logic as a standalone library (same compiler,
#  same reference set as 07-offline-harness.ps1) while EXCLUDING source files
#  that are currently mid-edit by parallel agents.  The point is to get a
#  compile signal for ScheduleEditor.cs / ActionPlanCommandProcessor.cs that
#  does NOT depend on the still-incomplete BattleSimulation.cs work.
#
#  This script does not modify any Assets/** file.  Output goes to %TEMP%.
#
#  Exit 0 = the remaining source set compiles; 2 = it does not; 4 = prereq missing.
# =============================================================================
param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string[]]$Exclude = @(
        "Simulation\BattleSimulation.cs",
        "Snapshots\LogicSnapshot.cs"
    ),
    [string]$OutDir = "",
    [switch]$RunTests,
    [string]$Filter = "",
    [string]$ResultsFile = "",
    [string]$RunnerPath = "",
    [string]$ExcludeTests = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $env:TEMP "t07-sub" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$editorRoot = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1"
$editorData = Join-Path $editorRoot "Editor\Data"
$sdk        = Join-Path $editorData "DotNetSdk"
$dotnet     = Join-Path $sdk "dotnet.exe"
$cscPath    = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$nsRefDir   = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"

foreach ($p in @($dotnet, $cscPath, $nsRefDir)) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "PREREQ MISSING: $p"; exit 4 }
}

$logicDir = Join-Path $ProjectRoot "Assets\Scripts\Logic"
$all = @(Get-ChildItem -LiteralPath $logicDir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })

# ---- mirror mode -------------------------------------------------------------
# Parallel agents own other files in this tree.  To get a compile signal without
# touching them, copy the whole Logic tree into $OutDir\src and apply the known
# one-line patches (report them, never write them back to the repo).
$mirrorDir = Join-Path $OutDir "src"
if (Test-Path -LiteralPath $mirrorDir) { Remove-Item -LiteralPath $mirrorDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $mirrorDir | Out-Null
foreach ($f in $all) {
    $rel = $f.Substring($logicDir.Length).TrimStart('\')
    $dst = Join-Path $mirrorDir $rel
    $parent = Split-Path -Path $dst -Parent
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $f -Destination $dst -Force
}
Write-Host "MIRROR: $mirrorDir"

# Known upstream defect blocking a full compile (NOT ours, NOT written back):
#   Resources\AdrenalineLedgerRegistry.cs uses Combat.AdrenalineRules with no using.
$fixTarget = Join-Path $mirrorDir "Resources\AdrenalineLedgerRegistry.cs"
if (Test-Path -LiteralPath $fixTarget) {
    $text = Get-Content -LiteralPath $fixTarget -Raw
    if ($text -notmatch "using ProjectHero\.Logic\.Combat;") {
        $patched = $text -replace "(?m)^using ProjectHero\.Logic\.Ids;", "using ProjectHero.Logic.Combat;`r`nusing ProjectHero.Logic.Ids;"
        Set-Content -LiteralPath $fixTarget -Value $patched -Encoding UTF8 -NoNewline
        Write-Host "MIRROR-PATCH: Resources\AdrenalineLedgerRegistry.cs += using ProjectHero.Logic.Combat;"
    }
}
$all = @(Get-ChildItem -LiteralPath $mirrorDir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })
$logicDir = $mirrorDir

# Second known upstream defect (also NOT ours): the window-ledger encoder block in
# Snapshots\LogicSnapshot.cs declares "var reservations" inside a for-loop while the
# enclosing method already declares "var reservations" (CS0136).  Rename the local
# ONLY inside that block in the mirror, preserving behaviour exactly.
$snapTarget = Join-Path $mirrorDir "Snapshots\LogicSnapshot.cs"
if (Test-Path -LiteralPath $snapTarget) {
    $lines = [System.IO.File]::ReadAllLines($snapTarget, [System.Text.Encoding]::UTF8)
    $start = -1; $end = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($start -lt 0 -and $lines[$i] -match "var reservations = new List<TurnWindowReservationSnapshot>") { $start = $i; continue }
        if ($start -ge 0 -and $end -lt 0 -and $lines[$i] -match "encoder\.WriteBool\(ConcurrentAction\.HasActiveAuthorization\)") { $end = $i - 1; break }
    }
    if ($start -ge 0 -and $end -ge $start) {
        for ($i = $start; $i -le $end; $i++) {
            # case-sensitive: the "Reservations" property of TurnWindowSnapshot must stay intact
            $lines[$i] = $lines[$i] -creplace "\breservations\b", "windowReservations"
        }
        [System.IO.File]::WriteAllLines($snapTarget, $lines)
        Write-Host ("MIRROR-PATCH: Snapshots\LogicSnapshot.cs lines {0}-{1}: local 'reservations' -> 'windowReservations'" -f ($start + 1), ($end + 1))
    } else {
        Write-Host "MIRROR-PATCH: LogicSnapshot window-ledger block not located (start=$start end=$end)"
    }
}

$excludedFull = New-Object System.Collections.Generic.HashSet[string]
foreach ($e in $Exclude) {
    if ([string]::IsNullOrWhiteSpace($e)) { continue }
    $full = Join-Path $logicDir $e
    if (-not (Test-Path -LiteralPath $full)) { Write-Host "NOTE: exclude path not found -> $full" }
    [void]$excludedFull.Add([System.IO.Path]::GetFullPath($full))
}

$sources = @($all | Where-Object { -not $excludedFull.Contains([System.IO.Path]::GetFullPath($_)) })
Write-Host ("Logic sources: {0} total, {1} excluded, {2} compiled" -f $all.Count, ($all.Count - $sources.Count), $sources.Count)
foreach ($e in $Exclude) { if (-not [string]::IsNullOrWhiteSpace($e)) { Write-Host "  excluded: $e" } }

$dll = Join-Path $OutDir "ProjectHero.Logic.dll"
$rsp = Join-Path $OutDir "Logic.subcheck.rsp"

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("-target:library")
$lines.Add("-langversion:9.0")
$lines.Add("-nullable:disable")
$lines.Add("-nologo")
$lines.Add("-nostdlib+")
$lines.Add("-nowarn:1701,1702,1591,0618")
$lines.Add("-out:`"$dll`"")
foreach ($r in @(Get-ChildItem -LiteralPath $nsRefDir -Filter *.dll | ForEach-Object { $_.FullName })) {
    $lines.Add("-r:`"$r`"")
}
foreach ($s in $sources) { $lines.Add("`"$s`"") }
Set-Content -LiteralPath $rsp -Value $lines -Encoding UTF8

$output = & $dotnet $cscPath "@$rsp" 2>&1
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Host "=== SUBCHECK COMPILE FAILED (exit $code) ==="
    foreach ($line in $output) { Write-Host $line }
    Write-Host "=== end ==="
    exit 2
}
Write-Host "SUBCHECK COMPILE: OK -> $dll"

# ---------------------------------------------------------------- optional tests
# Compile Assets/Tests/EditMode/Logic against the subcheck Logic DLL and run it with
# the Unity Mono + Unity nunit.framework, reusing 06-nunit-runner.cs verbatim.
if ($RunTests) {
    $mono   = Join-Path $editorData "MonoBleedingEdge\bin\mono.exe"
    $nunit  = Join-Path $ProjectRoot "Library\PackageCache\com.unity.ext.nunit@0198eae3b53e\net472\unity-custom\nunit.framework.dll"
    # NOTE: the runner lives under a non-ASCII directory.  PS 5.1 mangles such literal
    # paths in this session, so locate it with a wildcard (which skips non-ASCII dirs)
    # AND fall back to an explicit ASCII copy passed via -RunnerPath.
    $runnerCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RunnerPath)) { $runnerCandidates += $RunnerPath }
    $runnerCandidates += @(Get-ChildItem -Path (Join-Path $ProjectRoot "*\*\06-nunit-runner.cs") -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $runner = $null
    foreach ($c in $runnerCandidates) { if (Test-Path -LiteralPath $c) { $runner = $c; break } }
    if ($null -eq $runner) { Write-Host "PREREQ MISSING (tests): 06-nunit-runner.cs not located; pass -RunnerPath"; exit 4 }
    Write-Host "runner source = $runner"
    foreach ($p in @($mono, $nunit)) {
        if (-not (Test-Path -LiteralPath $p)) { Write-Host "PREREQ MISSING (tests): $p"; exit 4 }
    }

    $testsOut = Join-Path $OutDir "ProjectHero.Logic.Tests.subcheck.dll"
    $testsRsp = Join-Path $OutDir "LogicTests.subcheck.rsp"
    $tlines = New-Object System.Collections.Generic.List[string]
    $tlines.Add("-target:library")
    $tlines.Add("-langversion:9.0")
    $tlines.Add("-nullable:disable")
    $tlines.Add("-nologo")
    $tlines.Add("-nostdlib+")
    $tlines.Add("-nowarn:1701,1702,1591,0618")
    $tlines.Add("-out:`"$testsOut`"")
    foreach ($r in @(Get-ChildItem -LiteralPath $nsRefDir -Filter *.dll | ForEach-Object { $_.FullName })) { $tlines.Add("-r:`"$r`"") }
    $tlines.Add("-r:`"$nunit`"")
    $tlines.Add("-r:`"$dll`"")
    $testDir = Join-Path $ProjectRoot "Assets\Tests\EditMode\Logic"
    $testSources = @(Get-ChildItem -LiteralPath $testDir -Recurse -File -Filter *.cs |
        Where-Object { [string]::IsNullOrWhiteSpace($ExcludeTests) -or ($_.Name -notmatch $ExcludeTests) } |
        ForEach-Object { $_.FullName })
    Write-Host ("test sources: {0} compiled (ExcludeTests='{1}')" -f $testSources.Count, $ExcludeTests)
    foreach ($s in $testSources) { $tlines.Add("`"$s`"") }
    Set-Content -LiteralPath $testsRsp -Value $tlines -Encoding UTF8
    $toutput = & $dotnet $cscPath "@$testsRsp" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "=== TESTS COMPILE FAILED (exit $LASTEXITCODE) ==="
        foreach ($line in $toutput) { Write-Host $line }
        Write-Host "=== end ==="
        exit 2
    }
    Write-Host ("SUBCHECK TESTS COMPILE: OK ({0} sources)" -f $testSources.Count)

    if ([string]::IsNullOrWhiteSpace($ResultsFile)) { $ResultsFile = Join-Path $OutDir "07-sub-logic-results.xml" }
    $runnerSrc = Join-Path $OutDir "T07SubRunner.cs"
    Copy-Item -LiteralPath $runner -Destination $runnerSrc -Force
    $runnerExe = Join-Path $OutDir "T07SubRunner.exe"
    $rlines = New-Object System.Collections.Generic.List[string]
    $rlines.Add("-target:exe")
    $rlines.Add("-langversion:9.0")
    $rlines.Add("-nologo")
    $rlines.Add("-nostdlib+")
    $rlines.Add("-nowarn:1701,1702,1591,0618")
    $rlines.Add("-out:`"$runnerExe`"")
    foreach ($r in @(Get-ChildItem -LiteralPath $nsRefDir -Filter *.dll | ForEach-Object { $_.FullName })) { $rlines.Add("-r:`"$r`"") }
    $rlines.Add("-r:`"$nunit`"")
    $rlines.Add("-r:`"$dll`"")
    $rlines.Add("-r:`"$testsOut`"")
    $rlines.Add("`"$runnerSrc`"")
    $runnerRsp = Join-Path $OutDir "Runner.subcheck.rsp"
    Set-Content -LiteralPath $runnerRsp -Value $rlines -Encoding UTF8
    $rout = & $dotnet $cscPath "@$runnerRsp" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "=== RUNNER COMPILE FAILED ==="
        foreach ($line in $rout) { Write-Host $line }
        exit 2
    }

    foreach ($dep in @($nunit, $testsOut, $dll)) {
        $dest = Join-Path $OutDir (Split-Path -Leaf $dep)
        if ([System.IO.Path]::GetFullPath($dep) -ne [System.IO.Path]::GetFullPath($dest)) {
            Copy-Item -LiteralPath $dep -Destination $dest -Force
        }
    }
    $runnerArgs = @($runnerExe, $testsOut, $ResultsFile)
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $runnerArgs += $Filter }
    Write-Host "running tests ... (Filter='$Filter')"
    & $mono $runnerArgs
    $testExit = $LASTEXITCODE
    Write-Host "runner exit=$testExit results=$ResultsFile"
    if ($testExit -ne 0) { exit 1 }
}

exit 0
