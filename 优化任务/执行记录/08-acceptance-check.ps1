param(
    [string[]]$Results = @(),
    [string]$Output = ""
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$taskDoc = Get-ChildItem (Split-Path $PSScriptRoot -Parent) -Filter '08-*.md' | Select-Object -First 1
if (!$taskDoc) { throw 'Task 08 document missing' }
if ($Results.Count -eq 0) {
    $Results = @((Join-Path $PSScriptRoot '08-completion-editmode-results.xml'),
                 (Join-Path $PSScriptRoot '08-completion-playmode-results.xml'))
}
$taskContent = [IO.File]::ReadAllText($taskDoc.FullName)
$taskSection = $taskContent -split '(?m)^## ' | Where-Object { $_ -match 'DodgeRelocationInvalidatesDependentMovesBeforeContactGraphBuild' } | Select-Object -Last 1
$required = @([regex]::Matches($taskSection, '(?m)^- `([A-Za-z0-9_]+)`') | ForEach-Object { $_.Groups[1].Value })
if ($required.Count -ne 113) { throw "Required list changed: $($required.Count)" }
$sources = @{}
foreach ($file in Get-ChildItem (Join-Path $taskRoot 'Assets/Tests') -Recurse -Filter '*.cs') {
    $content = [IO.File]::ReadAllText($file.FullName)
    foreach ($match in [regex]::Matches($content, '(?m)^\s*public\s+(?:void|IEnumerator)\s+(\w+)\s*\(')) {
        $sources[$match.Groups[1].Value] = $file.FullName.Substring($taskRoot.Length + 1).Replace('\','/')
    }
}
$passed = @{}
$runs = @()
foreach ($path in $Results) {
    $resolved = (Resolve-Path -LiteralPath $path).Path
    [xml]$xml = [IO.File]::ReadAllText($resolved)
    $root = $xml.DocumentElement
    if ($root.result -ne 'Passed' -or [int]$root.failed -ne 0 -or [int]$root.skipped -ne 0) {
        throw "Run is not fully passed: $path"
    }
    $runs += [pscustomobject]@{ File = [IO.Path]::GetFileName($path); Total = [int]$root.total; Passed = [int]$root.passed; Failed = [int]$root.failed }
    foreach ($case in $xml.SelectNodes('//test-case[@result="Passed"]')) {
        # NUnit appends arguments to parameterized fullname. Match the complete method, never a substring.
        $method = ($case.fullname -split '\(',2)[0] -split '\.' | Select-Object -Last 1
        $passed[$method] = $true
    }
}
$rows = @($required | ForEach-Object {
    [pscustomobject]@{ Name = $_; Named = $sources.ContainsKey($_); Passed = $passed.ContainsKey($_); Source = $sources[$_] }
})
$missing = @($rows | Where-Object { !$_.Named -or !$_.Passed })
$report = [pscustomobject]@{ Required = $required.Count; Named = @($rows | Where-Object Named).Count;
    Passed = @($rows | Where-Object Passed).Count; Runs = $runs; Tests = $rows }
if ($Output) { [IO.File]::WriteAllText((Join-Path $taskRoot $Output), ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false)) }
Write-Output "NAMED=$($report.Named)/$($report.Required) PASSED=$($report.Passed)/$($report.Required)"
if ($missing.Count -gt 0) { $missing | Format-Table; exit 2 }
Write-Output 'TASK08 ACCEPTANCE OK'
