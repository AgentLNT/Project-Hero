# 任务 09 · C2a 离线编译门（不启动 Unity）
#
# 目的：把 **Compatibility.Runtime + Tests/PlayMode** 也编进离线编译校验。
#   07-offline-harness.ps1 只编 `ProjectHero.Logic` + `ProjectHero.Logic.Tests`，
#   因此 `Core/Compatibility/Runtime/**` 与 `Assets/Tests/PlayMode/**` 的编译错误
#   （典型：CS0103 未声明标识符、缺 using、局部变量错位）在那个脚手架上**完全看不见**。
#   本脚本是 C2a 交付过程中真实抓到两处 CS0103 的那道门，供 C2b / 任务 10 / 11 复用。
#
# 用法（本机 pwsh 执行策略禁止直接跑 .ps1，必须按下面两行的方式调用；原因见「坑」一节）：
#   $sb = [scriptblock]::Create((Get-Content -LiteralPath '<本文件>' -Raw -Encoding UTF8))
#   . $sb                      # dot-source 才会让内部变量生效
#   # 需要加编别的测试文件（例如 C2b 新增的 PlayMode 用例）：
#   $ExtraSources = @('C:\...\Assets\Tests\PlayMode\SomeOtherTests.cs')
#   . $sb
#
# 退出码：0 = 全部编译通过；2 = 至少一个程序集编译失败；4 = 前置缺失（Unity/Roslyn/nunit 路径）
#
# ---------------------------------------------------------------- 引用集与 asmdef 的对齐关系
# 本脚本的四个程序集与工程里 asmdef 的对应关系（2026-10-07 当时态）：
#   Logic                 <- Assets/Scripts/Logic/ProjectHero.Logic.asmdef                  （无额外引用）
#   Grid                  <- Assets/Scripts/Grid/ProjectHero.Grid.asmdef                   （引擎引用）
#   CompatibilityRuntime  <- Assets/Scripts/Core/Compatibility/Runtime/…Runtime.asmdef
#                            references = [ProjectHero.Logic]；本脚本额外加 Grid（工程内实际可解析）
#   PlayModeTests         <- Assets/Tests/PlayMode/ProjectHero.Compatibility.Runtime.Tests.asmdef
#                            references = [ProjectHero.Logic, ProjectHero.Compatibility.Runtime]
#                            optionalUnityReferences = [TestAssemblies] ⇒ 需要
#                            Library/ScriptAssemblies/{UnityEngine,UnityEditor}.TestRunner.dll
#                            defineConstraints = [UNITY_INCLUDE_TESTS] ⇒ 本脚本 define 它
#                            （再加 UNITY_EDITOR：PlayMode 测试源码里有 #if UNITY_EDITOR 段，
#                             例如 RuntimeOwnershipTestBase.HiddenSceneAssetText）
#
# 为什么**只**引用 `Managed/UnityEngine/*.dll`，不同时引用 `Managed/UnityEditor.dll`：
#   两者同时引用会让 MonoScript / MonoImporter / AssetDatabase 等类型重复出现（CS0433 类型冲突）。
#   该结论是 07 轮实测得出，见 `优化任务/执行记录/07-验证脚手架说明.md` 第 3 节。
#
# ---------------------------------------------------------------- 坑（都真实踩过）
# 1) 本机 PowerShell 执行策略禁止直接运行 .ps1（`& .\x.ps1` 报 "running scripts is disabled"）。
#    必须用 `[scriptblock]::Create(<文本>)` + dot-source。
# 2) **必须 `-Encoding UTF8` 读本文件**：`Get-Content` 在中文 Windows 上默认按 GBK 解码，
#    会把文件头的 UTF-8 中文注释解成乱码，**并吃掉紧随其后的赋值行**，于是 `$ProjectRoot` 为 null，
#    所有 `Join-Path` 静默失败、没有任何程序集被编译，最后打印出一个**假绿的 "COMPILE ONLY: OK"**。
#    看到 `OK` 之前，请先确认上方四行 `OK  <asm>  (N sources)` 都出现了。
# 3) `$ProjectRoot` / `$OutDir` 必须在脚本**最外层作用域**赋值：若包在 `param()` 里再用
#    `Invoke-Expression` 调用，参数块不会被解析（Unexpected token ')'）。
#
# ---------------------------------------------------------------- 可调变量（dot-source 前先设好）
# $ProjectRoot  工程根（默认见下）
# $OutDir       中间产物目录（.rsp / .dll）
# $ExtraSources 额外要加编进 PlayModeTests 的 .cs 全路径数组（默认空）
# $SkipPlayMode 置 $true 可只校验 Runtime 链

if (-not $ProjectRoot) { $ProjectRoot = 'C:\Users\1\repos\Project Hero\Project-Hero' }
if (-not $OutDir)      { $OutDir      = Join-Path $env:TEMP 't09-c2a-compilecheck' }
if (-not $ExtraSources) { $ExtraSources = @() }

$editorRoot = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1"
$editorData = Join-Path $editorRoot "Editor\Data"
$sdk        = Join-Path $editorData "DotNetSdk"
$dotnet     = Join-Path $sdk "dotnet.exe"
$cscPath    = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$nsRefDir   = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"
$engineDir  = Join-Path $editorData "Managed\UnityEngine"
$nunit      = Join-Path $ProjectRoot "Library\PackageCache\com.unity.ext.nunit@0198eae3b53e\net472\unity-custom\nunit.framework.dll"
$scriptAsm  = Join-Path $ProjectRoot "Library\ScriptAssemblies"

foreach ($p in @($dotnet, $cscPath, $nsRefDir, $engineDir, $nunit)) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "PREREQ MISSING: $p"; exit 4 }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$nsRefs     = @(Get-ChildItem -LiteralPath $nsRefDir -Filter *.dll | ForEach-Object { $_.FullName })
$engineRefs = @(Get-ChildItem -LiteralPath $engineDir -Filter *.dll | ForEach-Object { $_.FullName })

# [UnityTest] / [UnitySetUp] / [UnityTearDown] 来自 Unity Test Framework。
$utpRefs = @()
foreach ($n in @('UnityEngine.TestRunner.dll', 'UnityEditor.TestRunner.dll')) {
    $p = Join-Path $scriptAsm $n
    if (Test-Path -LiteralPath $p) { $utpRefs += $p }
}

function Get-Sources([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })
}

$script:compileFailed = New-Object System.Collections.Generic.List[string]

function New-Assembly {
    param([string]$Name, [string[]]$Sources, [string[]]$ExtraRefs, [string]$OutFile,
          [string]$Target = "library", [string[]]$Defines = @())
    if ($null -eq $Sources -or $Sources.Length -eq 0) {
        Write-Host "SKIP  $Name : 没有源文件（前置失败，非静默跳过）"
        $script:compileFailed.Add($Name)
        return $false
    }

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
    foreach ($r in $nsRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($r in $ExtraRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($s in $Sources) { $lines.Add("`"$s`"") }
    Set-Content -LiteralPath $rsp -Value $lines -Encoding UTF8

    $output = & $dotnet $cscPath "@$rsp" 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "=== $Name COMPILE FAILED (exit $code) ==="
        foreach ($line in $output) { Write-Host $line }
        Write-Host "=== end of $Name compiler output ==="
        $script:compileFailed.Add($Name)
        return $false
    }
    Write-Host ("OK  {0}  ({1} sources)" -f $Name, $Sources.Length)
    return $true
}

$gridDll    = Join-Path $OutDir "ProjectHero.Grid.dll"
$logicDll   = Join-Path $OutDir "ProjectHero.Logic.dll"
$compatDll  = Join-Path $OutDir "ProjectHero.Compatibility.Runtime.dll"
$pmTestsDll = Join-Path $OutDir "ProjectHero.Compatibility.Runtime.Tests.dll"

Write-Host "ProjectRoot = $ProjectRoot"
Write-Host "OutDir      = $OutDir"
Write-Host "ExtraSources= $($ExtraSources.Count)"

$logicOk = New-Assembly -Name "Logic" `
    -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Logic")) `
    -ExtraRefs @() -OutFile $logicDll

$gridOk = New-Assembly -Name "Grid" `
    -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Grid")) `
    -ExtraRefs $engineRefs -OutFile $gridDll

$compatOk = $false
if ($logicOk -and $gridOk) {
    $compatOk = New-Assembly -Name "CompatibilityRuntime" `
        -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Core\Compatibility\Runtime")) `
        -ExtraRefs (@($gridDll, $logicDll) + $engineRefs) -OutFile $compatDll
} else {
    Write-Host "SKIP  CompatibilityRuntime: Logic/Grid 未编译成功（前置失败，非静默跳过）"
    $script:compileFailed.Add("CompatibilityRuntime")
}

if (-not $SkipPlayMode) {
    if ($compatOk) {
        $pmSources = @(Get-Sources (Join-Path $ProjectRoot "Assets\Tests\PlayMode")) + @($ExtraSources)
        New-Assembly -Name "PlayModeTests" -Sources $pmSources `
            -ExtraRefs (@($nunit, $logicDll, $compatDll) + $engineRefs + $utpRefs) `
            -OutFile $pmTestsDll -Defines @("UNITY_INCLUDE_TESTS", "UNITY_EDITOR") | Out-Null
    } else {
        Write-Host "SKIP  PlayModeTests: CompatibilityRuntime 未编译成功（前置失败，非静默跳过）"
        $script:compileFailed.Add("PlayModeTests")
    }
}

if ($script:compileFailed.Count -gt 0) {
    Write-Host ("COMPILE ONLY: FAILED -> " + ($script:compileFailed -join ','))
    exit 2
}
Write-Host "COMPILE ONLY: OK"
exit 0
