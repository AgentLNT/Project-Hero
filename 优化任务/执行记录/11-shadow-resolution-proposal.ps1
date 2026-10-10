param(
    [string]$EditResults = '11-nonperformance-final-editmode.xml',
    [string]$PlayResults = '11-nonperformance-final-playmode.xml',
    [switch]$Accepted,
    [string]$MigrationCaseId = 'task11-shadow-confirmed-future-attack'
)
$ErrorActionPreference = 'Stop'
$recordRoot = $PSScriptRoot
$catalog = Get-Content (Join-Path $recordRoot '11-shadow-classification.json') -Raw -Encoding utf8 | ConvertFrom-Json
[xml]$edit = Get-Content (Join-Path $recordRoot $EditResults)
[xml]$play = Get-Content (Join-Path $recordRoot $PlayResults)
$cases = @($edit.SelectNodes('//test-case')) + @($play.SelectNodes('//test-case'))
if (@($cases | Where-Object { $_.result -ne 'Passed' }).Count) { throw 'Verification includes a non-passing case' }
if ($catalog.entries.Count -ne 60) { throw 'Expected 60 explicit obligations' }
$keys = @{}
$rows = @(foreach ($entry in $catalog.entries) {
    $key = $entry.legacyObjectPath + '#' + $entry.field
    if ($keys.ContainsKey($key)) { throw "Duplicate obligation: $key" }
    $keys[$key] = $true
    $proof = @(foreach ($method in $entry.tests) {
        $matching = @($cases | Where-Object { $_.name -eq $method -or $_.name.StartsWith($method + '(') })
        if ($matching.Count -eq 0) { throw "Unexecuted proof: $key -> $method" }
        $matching | ForEach-Object { $_.fullname }
    })
    if (!(Test-Path -LiteralPath $entry.legacySource) -or !(Test-Path -LiteralPath $entry.newSource)) { throw "Source missing: $key" }
    $proposal = switch ($entry.category) {
        'NewRule' { '按指定字段的新规则断言、负对照和确定性回放验收；不伪造旧侧值' }
        'ReplacedRepresentation' { '保留实际旧侧观测及共同字段比较；新量纲/生命周期单独按规则验收，不自动批准观测差异' }
        'RemovedResource' { '按主方案删除资源验收；验证新侧无该资源及读取依赖；历史旧值保留在迁移记录' }
        'DiagnosticOnly' { '按真实提交事件与独立聚合断言验收；保持诊断不参与权威哈希' }
        default { throw "Unknown category: $($entry.category)" }
    }
    [pscustomobject]@{
        Id = $key; Category = $entry.category; ProposedResolution = $proposal
        Status = $(if($Accepted){'RuleEvidenceVerified'}else{'PendingDesignDecision'}); RulesVersion = $catalog.rulesVersion; DefinitionHash = $catalog.definitionHash
        PassedCases = $proof -join ';'; LegacySource = $entry.legacySource; NewSource = $entry.newSource
        LegacySourceSha256 = (Get-FileHash -LiteralPath $entry.legacySource -Algorithm SHA256).Hash
        NewSourceSha256 = (Get-FileHash -LiteralPath $entry.newSource -Algorithm SHA256).Hash
        OriginalReason = $entry.reason
    }
})
$rows | Export-Csv (Join-Path $recordRoot '11-shadow-resolution-proposal.csv') -NoTypeInformation -Encoding utf8
if($Accepted) {
    $sourceHashes = @(rg --files Assets/Scripts Assets/Tests -g '*.cs' -g '*.asmdef' -g '*.xml' | Sort-Object | ForEach-Object {
        [ordered]@{path=$_ -replace '\\','/';sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    })
    [ordered]@{decision='accepted-2026-10-10-rule-obligations-performance-deferred';caseId=$MigrationCaseId;
        rulesVersion=$catalog.rulesVersion;definitionHash=$catalog.definitionHash;
        editResults=$EditResults;playResults=$PlayResults;
        editSha256=(Get-FileHash (Join-Path $recordRoot $EditResults) -Algorithm SHA256).Hash;
        playSha256=(Get-FileHash (Join-Path $recordRoot $PlayResults) -Algorithm SHA256).Hash;
        sourceHashes=$sourceHashes;entries=@($rows | ForEach-Object {
            [ordered]@{id=$_.Id;category=$_.Category;reason=$_.OriginalReason;executedCases=@($_.PassedCases -split ';')}
        })} | ConvertTo-Json -Depth 9 | Set-Content (Join-Path $recordRoot '11-shadow-migration-evidence.json') -Encoding utf8
}
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Shadow 收口规则提案（2026-10-10）')
$lines.Add('')
$lines.Add($(if($Accepted){'状态：用户于 2026-10-10 采用提案，授权非性能门槛通过后迁移。此表记录已执行的新规则验收义务；共同字段仍真实比较，不宣称全部旧新等价。'}else{'状态：待设计决策。此提案没有修改生产比较策略，没有移除暂不可比较登记，也没有批准任何差异。'}))
$lines.Add('')
$lines.Add('## 当前冲突')
$lines.Add('')
$lines.Add('主方案要求“切换前暂不可比较字段为 0”；但原表同时登记旧侧没有的新对象、量纲与生命周期不同的表达、已删除体力和非权威诊断。仅补采样不能得到相同语义的旧值。体力条目要求随 Legacy 字段删除清零，而任务 11 又要求清零后才能删除 Legacy，形成循环门槛。不得通过填空值、自比或宽泛忽略解决。')
$lines.Add('')
$lines.Add('## 建议的具体收口规则')
$lines.Add('')
$lines.Add('1. 旧侧实际存在且语义相同的事实继续逐字段比较，基础设施错误和未经解释的共同字段差异仍阻断迁移。')
$lines.Add('2. 每一条新规则、替代表达、已删除资源和诊断面，建立独立且精确到规则版本、定义哈希、字段、源码及实际执行案例的验收义务。通过其规则断言、负对照和回放证据后，从“待补采样”转为“按明确新契约验收”；保留原原因与全部历史观察。')
$lines.Add('3. 共同字段的真实观测不能因上述收口而失去比较；若实际玩法对照产生差异，仍须逐用例/检查点审查，不自动生成批准差异。报告区分可比面一致和新规则通过，不宣称全部 Legacy/New 等价。')
$lines.Add('4. 正式性能保持 DeferredPerformance。若要在其未通过时切换默认 New 或删除 Legacy，需要明确将它列为后续发布前门槛；这不会改变性能阈值或宣称性能通过。')
$lines.Add('')
$lines.Add('## 已执行证据与逐字段提案')
$lines.Add('')
$lines.Add("当前全量 EditMode $($edit.'test-run'.passed)/$($edit.'test-run'.total)，PlayMode $($play.'test-run'.passed)/$($play.'test-run'.total)。下表每条方法均匹配本轮实际 Passed 案例；CSV 同时保存源码 SHA256 和完整案例名。通过这些测试不自动批准契约调整。")
$lines.Add('')
$lines.Add('| 字段义务 | 分类 | 提议收口方式 | 实际通过方法 |')
$lines.Add('|---|---|---|---|')
foreach ($entry in $catalog.entries) {
    $row = $rows | Where-Object { $_.Id -eq ($entry.legacyObjectPath + '#' + $entry.field) }
    $lines.Add('| ' + $entry.field + ' | ' + $entry.category + ' | ' + $row.ProposedResolution + ' | ' + ($entry.tests -join '；') + ' |')
}
$lines.Add('')
$lines.Add($(if($Accepted){'用户已选择采用该契约，并授权非性能门槛通过后默认 New 切换及 Legacy 清理。执行结果见 11-migration-shadow-accepted.txt 与本轮迁移验收；性能没有被判为通过。'}else{'决策选项：采用上述分离的迁移契约，继续实现逐字段验收义务及非空 Shadow 场景，再决定切换/清理；或保留原严格门槛和 Legacy。不得把未批准的提案当作门槛改动。'}))
$lines | Set-Content (Join-Path $recordRoot '11-shadow-收口规则提案-2026-10-10.md') -Encoding utf8
Write-Output "Proposed obligations=$($rows.Count); policy changes=0; approvals=0"
