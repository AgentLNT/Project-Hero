<#
  Task 09 acceptance cross-check: required tests "verbatim named" AND "passed in Unity results"
  ---------------------------------------------------------------------------------------------
  Purpose: one command at close-out that cannot produce the two classic false greens:
    - name present but never executed
    - executed under a different name

  Two independent columns:
    (1) NAMED  : a strictly same-named method exists under Assets/Tests/**
                 regex: ^\s*public\s+(void|async\s+void|IEnumerator)\s+<name>\s*\(
    (2) PASSED : in the given Unity result XML(s), some <test-case> whose fullname ends with
                 .<name> has result="Passed"

  Usage:
    .\09-acceptance-check.ps1 -Results <editmode.xml>,<playmode.xml> [-TaskDoc <task package .md>]

  Exit codes: 0 = all required tests NAMED and PASSED; 2 = at least one missing; 4 = prereq error.

  IMPORTANT: this file is intentionally ASCII-only. Windows PowerShell reads .ps1 sources as
  the ANSI code page unless a BOM is present, so non-ASCII literals in a UTF-8-without-BOM
  script get mangled (that produced a false "file not found" during authoring). All file
  discovery here is done at runtime with ASCII wildcards instead of hard-coded paths.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string[]]$Results,
    [string]$ProjectRoot = "",
    [string]$TaskDoc = ""
)

$ErrorActionPreference = 'Stop'
$exitMissing = 2
$exitPrereq  = 4

# Resolve ProjectRoot so this script works BOTH as a file invocation (& script.ps1) and when
# dot-sourced from a [scriptblock]::Create(...) string (where $PSScriptRoot is empty).
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    } elseif (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
        $ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
    } else {
        $ProjectRoot = (Get-Location).Path
    }
}

function Fail-Prereq([string]$msg) { Write-Host ("PREREQ: " + $msg); exit $exitPrereq }

if (-not (Test-Path -LiteralPath $ProjectRoot)) { Fail-Prereq ("ProjectRoot not found: " + $ProjectRoot) }
$testsDir = Join-Path $ProjectRoot 'Assets\Tests'
if (-not (Test-Path -LiteralPath $testsDir)) { Fail-Prereq ("tests dir not found: " + $testsDir) }

# ---- locate the task package if not given: any 09-*.md that carries the required-test section
function Test-TaskDoc([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    return ($text -match '(?m)^##\s*\S+\s*$' -and $text.Contains('## ') -and
            ($text -split "`n" | Where-Object { $_ -match '^##\s' } | Measure-Object).Count -gt 3 -and
            $text -match '(?m)^-\s+`[A-Za-z0-9_]+`\s*$')
}

$candidates = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($TaskDoc)) {
    $candidates.Add($TaskDoc)
} else {
    foreach ($f in Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -Filter '09-*.md' -ErrorAction SilentlyContinue) {
        $candidates.Add($f.FullName)
    }
}
if ($candidates.Count -eq 0) { Fail-Prereq "no 09-*.md found under ProjectRoot; pass -TaskDoc explicitly" }

# parse each candidate; keep the one with the most "`- \`name\`` bullet lines inside its required-test section
$best = $null
foreach ($cand in $candidates) {
    if (-not (Test-Path -LiteralPath $cand)) { continue }
    $lines = [System.IO.File]::ReadAllLines($cand, [System.Text.Encoding]::UTF8)
    $startIdx = -1; $endIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($startIdx -lt 0 -and $lines[$i] -match '^##\s') {
            # first section whose body is dominated by `name` bullets is chosen below
            $startIdx = $i
        }
    }
    # collect every `- \`Name\`` line in the whole doc, grouped per '##' section
    $names = New-Object System.Collections.Generic.List[string]
    $section = ''
    $sectionNames = @{}
    foreach ($ln in $lines) {
        if ($ln -match '^##\s*(.+?)\s*$') { $section = $matches[1]; if (-not $sectionNames.ContainsKey($section)) { $sectionNames[$section] = New-Object System.Collections.Generic.List[string] } ; continue }
        if ($ln -match '^-\s+`([A-Za-z0-9_]+)`\s*$' -and $section -ne '') { $sectionNames[$section].Add($matches[1]) }
    }
    $bestSection = $null; $bestCount = 0
    foreach ($k in $sectionNames.Keys) {
        if ($sectionNames[$k].Count -gt $bestCount -and $sectionNames[$k].Count -ge 20) { $bestCount = $sectionNames[$k].Count; $bestSection = $k }
    }
    if ($bestSection -ne $null -and $bestCount -gt 0) {
        $entry = [pscustomobject]@{ Path = $cand; Section = $bestSection; Names = $sectionNames[$bestSection] }
        if ($null -eq $best -or $entry.Names.Count -gt $best.Names.Count) { $best = $entry }
    }
}
if ($null -eq $best) { Fail-Prereq "could not locate a required-test list (a '##' section with >=20 `name` bullets)" }

$required = $best.Names
if ($required.Count -eq 0) { Fail-Prereq "required-test list parsed as EMPTY (an empty list would be a false green; failing hard)" }

# ---- NAMED: strict same-name scan ----
$blob = New-Object System.Text.StringBuilder
foreach ($f in Get-ChildItem -LiteralPath $testsDir -Recurse -File -Filter *.cs) {
    [void]$blob.AppendLine([System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8))
}
$blobText = $blob.ToString()

# ---- PASSED: collect passed short names from the result XMLs ----
$passed = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
$xmlSeen = New-Object System.Collections.Generic.List[string]
foreach ($r in $Results) {
    $path = $r
    if (-not [System.IO.Path]::IsPathRooted($path)) { $path = Join-Path $ProjectRoot $r }
    if (-not (Test-Path -LiteralPath $path)) { Fail-Prereq ("result XML not found: " + $path) }
    [xml]$x = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $run = $x.'test-run'
    $xmlSeen.Add(("{0}  total={1} passed={2} failed={3}" -f (Split-Path -Leaf $path), $run.total, $run.passed, $run.failed))
    foreach ($tc in $x.SelectNodes('//test-case')) {
        if ($tc.result -ne 'Passed') { continue }
        $full = $tc.fullname
        if ([string]::IsNullOrEmpty($full)) { continue }
        [void]$passed.Add($full.Substring($full.LastIndexOf('.') + 1))
    }
}

# ---- per-test verdict ----
$rows = @()
$namedCount = 0; $passedCount = 0
foreach ($name in $required) {
    $named = $blobText -match ('(?m)^\s*public\s+(?:void|async\s+void|IEnumerator)\s+' + [regex]::Escape($name) + '\s*\(')
    $ok = $passed.Contains($name)
    if ($named) { $namedCount++ }
    if ($named -and $ok) { $passedCount++ }
    $rows += [pscustomobject]@{ Name = $name; Named = $named; Passed = $ok }
}

Write-Host "Task 09 acceptance cross-check"
Write-Host ("task package   : {0}" -f $best.Path)
Write-Host ("required list  : section '{0}' with {1} entries" -f $best.Section, $required.Count)
Write-Host "result XMLs    :"
foreach ($s in $xmlSeen) { Write-Host ("  - " + $s) }
Write-Host ""
$bad = @($rows | Where-Object { -not ($_.Named -and $_.Passed) })
Write-Host ("NAMED   : {0}/{1}" -f $namedCount, $required.Count)
Write-Host ("PASSED  : {0}/{1}" -f $passedCount, $required.Count)
if ($bad.Count -eq 0) { Write-Host "ACCEPTANCE: OK"; exit 0 }

Write-Host ""
Write-Host "Not satisfied:"
foreach ($b in $bad) { Write-Host ("  - {0,-64} Named={1,-6} Passed={2}" -f $b.Name, $b.Named, $b.Passed) }
Write-Host ""
Write-Host "ACCEPTANCE: FAILED"
exit $exitMissing
