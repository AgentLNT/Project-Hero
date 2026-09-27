<#
=============================================================================
 任务 07 离线脚手架（TurnWindow 与提交权限）
=============================================================================

 定位（必须遵守，不得曲解）
 --------------------------
 本脚本是 **迭代加速器**，不是验收工具。

   * 它用 Unity 编辑器自带 Roslyn 离线编译 ProjectHero.Logic + ProjectHero.Logic.Tests，
     再用编辑器自带 Mono 执行同一批 NUnit 用例，从而在**不开 Unity、不做包管理器 IPC 初始化**
     的前提下拿到快速反馈。
   * 它给出的 pass/fail 只能用于「改代码时别把东西改坏」的过程判断。
   * **任务 07 的任何结论性证据（尤其 Assets/Tests/EditMode/Authoring 与 PlayMode）必须由
     Unity Test Runner 复核后才能写入交接记录/独立验证记录。** 见同目录
     `07-验证脚手架说明.md` 第 4 节的权威性边界。
   * 现有环境事实：本机 PowerShell 执行策略默认禁用脚本。每次调用前先执行
       Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

 与 06-offline-harness.ps1 的差异
 --------------------------------
   * 本脚本是 07 专用，**06 脚本保持原样不动**，两者互不依赖。
   * 默认结果文件改为 `07-logic-results.xml`（放在本脚本同目录，即 优化任务\执行记录\）。
   * 新增 `-AuthoringCompile`：尝试离线编译 Assets/Tests/EditMode/Authoring 这整条
     Authoring 证据链（ProjectHero.Grid → ProjectHero.Logic →
     ProjectHero.Compatibility.Runtime → ProjectHero.Authoring →
     ProjectHero.Authoring.Tests）。**不可行时显式打印原因并给出非 0 退出码，绝不静默跳过。**

 退出码约定（写进说明文档，供自动化判断）
 ----------------------------------------
   0  全部成功（编译通过；若未 -CompileOnly 则用例也全通过）
   1  编译通过但存在失败/不确定的用例
   2  Logic（或 Logic.Tests）编译失败 —— 当前 07 半成品就处于该状态
   3  Authoring 证据链编译失败 / 因前置缺失而无法进行
   4  环境前置缺失（编辑器、SDK、nunit、包缓存路径等）或其他不可预期错误

 关于 -EngineTag
 ---------------
 默认的 `06-nunit-runner.cs` 会在 XML 里写死 engine="T06Runner-reflect"。该文件属于任务 06 的
 交付物，本任务无权修改，因此 -EngineTag 参数只做记录（打印到控制台），不写入 XML。
 这是刻意的取舍：宁可少一个 XML 属性，也不改动他人已验收的产物。

 编码规范：全部使用 PowerShell 5.1 兼容语法（无 `??`、无 `?.`、无 -NoNewline 之外的 PS7 特性）。
=============================================================================
#>

param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string]$OutDir = "",
    [string]$Filter = "",
    [switch]$CompileOnly,
    [string]$ResultsFile = "",
    [switch]$AuthoringCompile,
    [string]$EngineTag = "T07Harness"
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------- 路径与常量

# 相对路径解析必须用 PowerShell 的 Resolve-Path，**不能**用 [System.IO.Path]::GetFullPath：
# PowerShell 5.1 里 Set-Location 只改 PowerShell 自己的当前位置，
# .NET 的 [Environment]::CurrentDirectory 可能仍是宿主启动目录，两者会不一致，
# 于是 '.\bin' 这类相对路径会被解析到错误目录（07 实测踩到过：凭空在仓库上级建出 bin\）。
function Resolve-FullPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    $resolved = Resolve-Path -LiteralPath $path -ErrorAction SilentlyContinue
    if ($null -ne $resolved) { return $resolved.ProviderPath }
    # 目标尚不存在时，解析父目录再拼回末段
    $parent = Split-Path -Path $path -Parent
    $leaf   = Split-Path -Path $path -Leaf
    if ([string]::IsNullOrWhiteSpace($parent)) { return (Join-Path (Get-Location).ProviderPath $leaf) }
    return (Join-Path (Resolve-FullPath $parent) $leaf)
}

if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $env:TEMP "t07-harness" }
$OutDir = Resolve-FullPath $OutDir
if ([string]::IsNullOrWhiteSpace($ResultsFile)) {
    $ResultsFile = Join-Path $PSScriptRoot "07-logic-results.xml"
}
$ResultsFile = Resolve-FullPath $ResultsFile

$editorRoot = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1"
$editorData = Join-Path $editorRoot "Editor\Data"
$sdk        = Join-Path $editorData "DotNetSdk"
$dotnet     = Join-Path $sdk "dotnet.exe"
$cscPath    = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$nsRefDir   = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"
$engineDir  = Join-Path $editorData "Managed\UnityEngine"
$mono       = Join-Path $editorData "MonoBleedingEdge\bin\mono.exe"
$nunit      = Join-Path $ProjectRoot "Library\PackageCache\com.unity.ext.nunit@0198eae3b53e\net472\unity-custom\nunit.framework.dll"
$runnerCs   = Join-Path $PSScriptRoot "06-nunit-runner.cs"

$exitCompileFail      = 2
$exitAuthoringFail    = 3
$exitPrereqFail       = 4

$script:compileFailures = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------- 前置检查
$missing = New-Object System.Collections.Generic.List[string]
foreach ($p in @($dotnet, $cscPath, $nsRefDir, $engineDir, $mono, $nunit)) {
    if (-not (Test-Path -LiteralPath $p)) { $missing.Add($p) }
}
if ($missing.Count -gt 0) {
    Write-Host "PREREQ FAILED: 以下前置在磁盘上不存在，无法离线编译/执行："
    foreach ($m in $missing) { Write-Host "  - $m" }
    Write-Host "提示：编辑器根目录假定为 $editorRoot；若本机 Unity 版本不同，请同步修改本脚本头部常量。"
    exit $exitPrereqFail
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$script:nsRefs    = @(Get-ChildItem -LiteralPath $nsRefDir -Filter *.dll | ForEach-Object { $_.FullName })
# Authoring 链需要引擎 + 编辑器托管程序集。
# 关键：**只引用 Managed\UnityEngine\*.dll（模块化程序集），不再额外引用 Managed\UnityEditor.dll**。
# 两者同时引用会让 MonoScript/MonoImporter/AssetDatabase 等类型重复出现（CS0433 类型冲突），
# 该结论由 07 实测得出，见 07-验证脚手架说明.md 第 3 节。
$script:engineRefs = @(Get-ChildItem -LiteralPath $engineDir -Filter *.dll | ForEach-Object { $_.FullName })

# ---------------------------------------------------------------- 编译辅助
function New-Assembly {
    param(
        [string]$Name,
        [string[]]$Sources,
        [string[]]$ExtraRefs,
        [string]$OutFile,
        [string]$Target = "library",
        [string[]]$Defines = @()
    )
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
    foreach ($d in $Defines) { $lines.Add("-define:$d") }
    foreach ($r in $script:nsRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($r in $ExtraRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($s in $Sources) { $lines.Add("`"$s`"") }
    Set-Content -LiteralPath $rsp -Value $lines -Encoding UTF8

    $output = & $dotnet $cscPath "@$rsp" 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "=== $Name COMPILE FAILED (exit $code) ==="
        foreach ($line in $output) { Write-Host $line }
        Write-Host "=== end of $Name compiler output ==="
        return $false
    }
    Write-Host ("OK  {0}  ({1} sources)" -f $Name, $Sources.Length)
    return $true
}

function Get-Sources([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })
}

function New-Dir([string]$path) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

# ---------------------------------------------------------------- 1. 纯 Logic 链（可离线执行）
Write-Host ""
Write-Host "########## [1/3] 纯 Logic 链（离线编译 + 可选执行）##########"
Write-Host "ProjectRoot = $ProjectRoot"
Write-Host "OutDir      = $OutDir"
Write-Host "EngineTag   = $EngineTag （仅记录；XML 内 engine 由 06-nunit-runner.cs 固定写入）"

$logicDll  = Join-Path $OutDir "ProjectHero.Logic.dll"
$testsDll  = Join-Path $OutDir "ProjectHero.Logic.Tests.dll"
$runnerExe = Join-Path $OutDir "T07Runner.exe"

$logicOk = New-Assembly -Name "Logic" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Logic")) `
    -ExtraRefs @() -OutFile $logicDll
if (-not $logicOk) { $script:compileFailures.Add("Logic") }

$testsOk = $false
if ($logicOk) {
    $testDir = Join-Path $ProjectRoot "Assets\Tests\EditMode\Logic"
    $testsOk = New-Assembly -Name "LogicTests" -Sources (Get-Sources $testDir) `
        -ExtraRefs @($nunit, $logicDll) -OutFile $testsDll
    if (-not $testsOk) { $script:compileFailures.Add("LogicTests") }
} else {
    Write-Host "SKIP  LogicTests: ProjectHero.Logic 未编译成功，测试程序集无法编译（前置失败，非静默跳过）。"
}

$runnerOk = $false
if ($testsOk) {
    if (-not (Test-Path -LiteralPath $runnerCs)) {
        Write-Host "PREREQ FAILED: 缺少 $runnerCs"
        exit $exitPrereqFail
    }
    $runnerSrc = Join-Path $OutDir "T07Runner.cs"
    Copy-Item -LiteralPath $runnerCs -Destination $runnerSrc -Force
    $runnerOk = New-Assembly -Name "Runner" -Sources @($runnerSrc) `
        -ExtraRefs @($nunit, $testsDll, $logicDll) -OutFile $runnerExe -Target "exe"
    if (-not $runnerOk) { $script:compileFailures.Add("Runner") }
}

# ---------------------------------------------------------------- 2. Authoring 链（只编译）
$authoringStatus = "not requested"
$authoringReason = ""
if ($AuthoringCompile) {
    Write-Host ""
    Write-Host "########## [2/3] Authoring 证据链（只编译，需 -AuthoringCompile）##########"
    Write-Host "链序：Grid -> Logic -> Compatibility.Runtime -> Authoring -> Authoring.Tests"

    if (-not $logicOk) {
        $authoringStatus = "blocked by Logic"
        $authoringReason = "ProjectHero.Logic 未编译成功，Compatibility.Runtime / Authoring / Authoring.Tests 都会因缺少元数据文件而失败，因此本链无法进行。原因见上方 [1/3] 段落的编译器原始输出。"
        Write-Host "AUTHORING COMPILE: 不可行（前置缺失，显式声明，非静默跳过）"
        Write-Host "  reason: $authoringReason"
    } else {
        $gridDll   = Join-Path $OutDir "ProjectHero.Grid.dll"
        $compatDll = Join-Path $OutDir "ProjectHero.Compatibility.Runtime.dll"
        $authorDll = Join-Path $OutDir "ProjectHero.Authoring.dll"
        $authorTestsDll = Join-Path $OutDir "ProjectHero.Authoring.Tests.dll"

        $gridOk = New-Assembly -Name "Grid" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Grid")) `
            -ExtraRefs $script:engineRefs -OutFile $gridDll
        $compatOk = $false
        if ($gridOk) {
            $compatOk = New-Assembly -Name "CompatibilityRuntime" `
                -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Core\Compatibility\Runtime")) `
                -ExtraRefs (@($gridDll, $logicDll) + $script:engineRefs) -OutFile $compatDll
        } else {
            Write-Host "SKIP  CompatibilityRuntime: Grid 未编译成功（前置失败，非静默跳过）。"
        }

        $authorOk = $false
        if ($gridOk) {
            $authorOk = New-Assembly -Name "Authoring" `
                -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Authoring")) `
                -ExtraRefs (@($gridDll, $logicDll) + $script:engineRefs) -OutFile $authorDll
        } else {
            Write-Host "SKIP  Authoring: Grid 未编译成功（前置失败，非静默跳过）。"
        }

        $authorTestsOk = $false
        if ($compatOk -and $authorOk) {
            $authorTestsOk = New-Assembly -Name "AuthoringTests" `
                -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Tests\EditMode\Authoring")) `
                -ExtraRefs (@($nunit, $gridDll, $logicDll, $compatDll, $authorDll) + $script:engineRefs) `
                -OutFile $authorTestsDll -Defines @("UNITY_INCLUDE_TESTS", "UNITY_EDITOR")
        } else {
            Write-Host "SKIP  AuthoringTests: CompatibilityRuntime/Authoring 未编译成功（前置失败，非静默跳过）。"
        }

        if ($gridOk -and $compatOk -and $authorOk -and $authorTestsOk) {
            $authoringStatus = "compiled"
            Write-Host "AUTHORING COMPILE: OK（仅证明离线可编译；通过/失败结论仍须 Unity Test Runner 给出）"
        } else {
            $authoringStatus = "compile failed"
            $authoringReason = "Authoring 证据链中至少一个程序集离线编译失败，原始错误见上方输出。"
            Write-Host "AUTHORING COMPILE: FAILED"
            Write-Host "  reason: $authoringReason"
        }
    }
} else {
    Write-Host ""
    Write-Host "########## [2/3] Authoring 证据链：未请求（加 -AuthoringCompile 启用）##########"
}

# ---------------------------------------------------------------- 收尾判定
function Get-WorstExit {
    if ($script:compileFailures.Count -gt 0) { return $exitCompileFail }
    if ($AuthoringCompile -and $authoringStatus -ne "compiled") { return $exitAuthoringFail }
    return 0
}

if ($CompileOnly) {
    Write-Host ""
    Write-Host "########## [3/3] -CompileOnly：跳过用例执行 ##########"
    Write-Host ("SUMMARY logic={0} authoring={1}" -f $logicOk, $authoringStatus)
    $code = Get-WorstExit
    if ($code -eq 0) {
        Write-Host "COMPILE ONLY: OK"
    } else {
        Write-Host "COMPILE ONLY: FAILED (exit $code)"
    }
    exit $code
}

if (-not $runnerOk) {
    Write-Host ""
    Write-Host "########## [3/3] 用例执行：不可进行 ##########"
    Write-Host ("SUMMARY logic={0} authoring={1}" -f $logicOk, $authoringStatus)
    Write-Host "OFFLINE RUN: SKIPPED-WITH-REASON —— 运行器或测试程序集未编译成功，无法执行任何用例（非静默跳过）。"
    $code = Get-WorstExit
    Write-Host "exit=$code"
    exit $code
}

Write-Host ""
Write-Host "########## [3/3] 执行 Logic 用例（Mono + Unity nunit.framework）##########"
foreach ($dep in @($nunit, $testsDll, $logicDll)) {
    $target = Join-Path $OutDir (Split-Path -Leaf $dep)
    if ([System.IO.Path]::GetFullPath($dep) -ne [System.IO.Path]::GetFullPath($target)) {
        Copy-Item -LiteralPath $dep -Destination $target -Force
    }
}

$runnerArgs = @($runnerExe, $testsDll, $ResultsFile)
if (-not [string]::IsNullOrWhiteSpace($Filter)) { $runnerArgs += $Filter }

Write-Host "running tests ... (Filter='$Filter')"
& $mono $runnerArgs
$testExit = $LASTEXITCODE
Write-Host "runner exit=$testExit  results=$ResultsFile"

if ((Get-WorstExit) -ne 0) {
    $code = Get-WorstExit
    Write-Host ("SUMMARY logic={0} authoring={1}" -f $logicOk, $authoringStatus)
    Write-Host "exit=$code （编译/Authoring 问题优先于用例结果上报）"
    exit $code
}
if ($testExit -ne 0) { exit 1 }
exit 0
