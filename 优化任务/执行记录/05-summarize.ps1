param([Parameter(Mandatory=$true)][string]$Path, [int]$MaxFailures = 12)

if (-not (Test-Path -LiteralPath $Path)) { Write-Output "NO RESULTS FILE: $Path"; exit 2 }

[xml]$d = Get-Content -LiteralPath $Path -Raw
$run = $d.'test-run'
Write-Output ("total={0} passed={1} failed={2} skipped={3} inconclusive={4} result={5}" -f `
    $run.total, $run.passed, $run.failed, $run.skipped, $run.inconclusive, $run.result)

$failed = $d.SelectNodes("//test-case[@result!='Passed']")
if ($failed.Count -eq 0) { Write-Output "ALL PASSED"; exit 0 }

Write-Output ("---- {0} non-passing ----" -f $failed.Count)
$i = 0
foreach ($tc in $failed) {
    if ($i -ge $MaxFailures) { Write-Output ("... {0} more suppressed" -f ($failed.Count - $MaxFailures)); break }
    $i++
    Write-Output ("[{0}] {1}" -f $tc.result, $tc.fullname)
    $msg = $null
    if ($tc.failure -ne $null) {
        $msg = $tc.failure.message
        if ([string]::IsNullOrWhiteSpace($msg)) { $msg = $tc.failure.'#cdata-section' }
    }
    if ([string]::IsNullOrWhiteSpace($msg)) { $msg = $tc.'#cdata-section' }
    if (-not [string]::IsNullOrWhiteSpace($msg)) {
        $lines = $msg -split "`r?`n"
        $n = 0
        foreach ($line in $lines) {
            if ($n -ge 5) { break }
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            Write-Output ("     " + $line.Trim())
            $n++
        }
    }
}
exit 1
