param([switch]$CreateManifest)
$ErrorActionPreference = 'Stop'
$m1Project = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$m1Reports = @('M1-full-editmode.xml', 'M1-full-playmode-bootstrap.xml')
$m1Passed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($m1Report in $m1Reports) {
    [xml]$m1Xml = Get-Content -LiteralPath (Join-Path $PSScriptRoot $m1Report)
    if ($m1Xml.'test-run'.failed -ne '0' -or $m1Xml.'test-run'.result -ne 'Passed') { throw "Native test failure: $m1Report" }
    foreach ($m1Case in $m1Xml.SelectNodes('//test-case')) {
        if ($m1Case.result -ne 'Passed') { throw "Nonpassing native case: $($m1Case.fullname)" }
        [void]$m1Passed.Add($m1Case.fullname)
    }
}
$m1ManifestPath = Join-Path $PSScriptRoot 'M1-shadow-migration-evidence.json'
if ($CreateManifest) {
    $m1Manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '12C-shadow-migration-evidence.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $m1Manifest.decision = 'accepted-2026-10-15-M1-performance-deferred'
    $m1Manifest.editResults = $m1Reports[0]; $m1Manifest.playResults = $m1Reports[1]
    $m1Manifest.editSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $m1Reports[0])).Hash
    $m1Manifest.playSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $m1Reports[1])).Hash
    $m1Sources = Get-ChildItem -LiteralPath (Join-Path $m1Project 'Assets/Scripts'),(Join-Path $m1Project 'Assets/Tests') -Recurse -File |
        Where-Object { $_.Extension -in @('.cs','.asmdef','.xml') } | Sort-Object FullName
    $m1Manifest.sourceHashes = @($m1Sources | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($m1Project.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
    foreach ($m1Entry in $m1Manifest.entries) {
        $m1Entry.executedCases = @($m1Entry.executedCases | ForEach-Object {
            $_.Replace('StaleRevisionOrLockRejectionRebasesFromLatestSnapshot','StaleRevisionKeepsIntentUntilExplicitRevalidation').Replace('SystemAutoDeferralCancelsAffectedDraftAndRebasesTimeline','SystemAutoDeferralPreservesStaleIntentAndRebasesTimeline').Replace('MainSceneTimelineDiscardsDraftWhenPlanStartsWithoutRevisionChange','MainSceneTimelinePreservesDraftWhenPlanStartsWithoutRevisionChange')
        })
        foreach ($m1Name in $m1Entry.executedCases) { if (!$m1Passed.Contains($m1Name)) { throw "Missing obligation proof: $m1Name" } }
    }
    $m1Manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $m1ManifestPath -Encoding UTF8
}
$m1Saved = Get-Content -LiteralPath $m1ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($m1Source in $m1Saved.sourceHashes) {
    if ((Get-FileHash -LiteralPath (Join-Path $m1Project $m1Source.path)).Hash -ne $m1Source.sha256) { throw "Source changed: $($m1Source.path)" }
}
$m1MouseBefore = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'M1-real-mouse-awaiting-resume.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$m1MouseAfter = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'M1-real-mouse-executed.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($m1MouseBefore.tick -ne 0 -or $m1MouseBefore.draftCount -ne 3 -or $m1MouseBefore.submitted -ne 0 -or $m1MouseBefore.authoritativePlans -ne 0) { throw 'Mouse planning evidence invalid' }
if ($m1MouseAfter.submitted -ne 1 -or $m1MouseAfter.draftState -ne 'Accepted' -or !($m1MouseAfter.plans | Where-Object { $_.ActionSpecId -match 'heavy' -and $_.StartTick -eq 179 -and $_.EndTick -gt 360 -and $_.State -eq 2 })) { throw 'Mouse execution evidence invalid' }
$m1ShadowCount = 0
$m1ShadowPath = Join-Path $PSScriptRoot 'M1-shadow-final.xml'
if (Test-Path -LiteralPath $m1ShadowPath) {
    [xml]$m1ShadowXml = Get-Content -LiteralPath $m1ShadowPath -Encoding UTF8
    if ($m1ShadowXml.'test-run'.result -ne 'Passed' -or $m1ShadowXml.'test-run'.failed -ne '0') { throw 'Final Shadow test failed' }
    $m1ShadowCount = [int]$m1ShadowXml.'test-run'.passed
} elseif (!$CreateManifest) { throw 'Final Shadow result missing' }
$m1PhysicalKeyboard = 'pending'
$m1KeyboardPath = Join-Path $PSScriptRoot 'M1-physical-keyboard-verification.json'
if (Test-Path -LiteralPath $m1KeyboardPath) {
    $m1Keyboard = Get-Content -LiteralPath $m1KeyboardPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($m1Keyboard.verifiedBy -ne 'user' -or $m1Keyboard.result -ne 'passed' -or [string]::IsNullOrWhiteSpace($m1Keyboard.response)) { throw 'Physical keyboard verification invalid' }
    foreach ($m1CheckId in @('esc_system_pause', 'space_tactical_pause', 'input_focus_blocks_space')) {
        $m1Check = @($m1Keyboard.checks | Where-Object { $_.id -eq $m1CheckId })
        if ($m1Check.Count -ne 1 -or $m1Check[0].result -ne 'passed') { throw "Physical keyboard check missing or failed: $m1CheckId" }
    }
    $m1PhysicalKeyboard = 'passed'
}
[pscustomobject]@{nativePassed=$m1Passed.Count;shadowFinalPassed=$m1ShadowCount;totalNativePassed=($m1Passed.Count+$m1ShadowCount);sourceFiles=$m1Saved.sourceHashes.Count;obligations=$m1Saved.entries.Count;mouseTransaction='passed';physicalKeyboard=$m1PhysicalKeyboard;performance='deferred'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'M1-audit.json') -Encoding UTF8
Write-Output "M1 audit passed: $($m1Passed.Count + $m1ShadowCount) native cases, $($m1Saved.entries.Count) obligations, mouse transaction verified, physical keyboard $m1PhysicalKeyboard."
