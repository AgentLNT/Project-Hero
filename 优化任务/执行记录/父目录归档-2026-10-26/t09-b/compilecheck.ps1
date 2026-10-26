<#
=============================================================================
 任务 09 / B 流：本地编译检查（仅编译，不运行用例）
=============================================================================

 定位
 ----
 * 这是 B 流的**私有**迭代加速器，不写进任何交付物，也不构成验收证据。
 * 它用 Unity 自带 Roslyn 离线编译
     ProjectHero.Grid -> ProjectHero.Logic -> ProjectHero.Compatibility.Runtime
   用来在**不开 Unity** 的前提下先排掉 B 新增/修改代码的语法与类型错误。
 * 权威结论只来自 Unity Test Runner（Assembly-CSharp-Editor 不在离线脚手架内）。
 * 现有环境事实：本机 PowerShell 执行策略默认禁用脚本；调用前先
     Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

 为什么不在 Assets/Scripts 下写脚本：
   本任务的文件所有权只覆盖指定路径，脚本放 %TEMP% 之外的仓库外目录，
   或放在 B 的私有证据目录（仓库内但不属于 Unity 工程）——
   这里选择 %TEMP%，避免污染仓库。
=============================================================================
#>

param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string]$OutDir = "$env:TEMP\t09-b-compilecheck"
)

$ErrorActionPreference = "Stop"

$editorRoot = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1"
$editorData = Join-Path $editorRoot "Editor\Data"
$sdk        = Join-Path $editorData "DotNetSdk"
$dotnet     = Join-Path $sdk "dotnet.exe"
$cscPath    = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$nsRefDir   = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"
$engineDir  = Join-Path $editorData "Managed\UnityEngine"

foreach ($p in @($dotnet, $cscPath, $nsRefDir, $engineDir)) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "PREREQ MISSING: $p"; exit 4 }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$nsRefs     = @(Get-ChildItem -LiteralPath $nsRefDir -Filter *.dll | ForEach-Object { $_.FullName })
$engineRefs = @(Get-ChildItem -LiteralPath $engineDir -Filter *.dll | ForEach-Object { $_.FullName })

function Get-Sources([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })
}

function New-Assembly {
    param(
        [string]$Name, [string[]]$Sources, [string[]]$ExtraRefs,
        [string]$OutFile, [string]$Target = "library"
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
    foreach ($r in $nsRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($r in $ExtraRefs) { $lines.Add("-r:`"$r`"") }
    foreach ($s in $Sources) { $lines.Add("`"$s`"") }
    Set-Content -LiteralPath $rsp -Value $lines -Encoding UTF8

    $output = & $dotnet $cscPath "@$rsp" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "=== $Name COMPILE FAILED (exit $LASTEXITCODE) ==="
        foreach ($line in $output) { Write-Host $line }
        return $false
    }
    Write-Host ("OK  {0}  ({1} sources)" -f $Name, $Sources.Length)
    return $true
}

$logicDll  = Join-Path $OutDir "ProjectHero.Logic.dll"
$gridDll   = Join-Path $OutDir "ProjectHero.Grid.dll"
$compatDll = Join-Path $OutDir "ProjectHero.Compatibility.Runtime.dll"

$logicOk = New-Assembly -Name "Logic" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Logic")) `
    -ExtraRefs @() -OutFile $logicDll
if (-not $logicOk) { exit 2 }

$gridOk = New-Assembly -Name "Grid" -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Grid")) `
    -ExtraRefs $engineRefs -OutFile $gridDll
if (-not $gridOk) { exit 2 }

$compatOk = New-Assembly -Name "CompatibilityRuntime" `
    -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Core\Compatibility\Runtime")) `
    -ExtraRefs (@($gridDll, $logicDll) + $engineRefs) -OutFile $compatDll
if (-not $compatOk) { exit 2 }

Write-Host "COMPILE CHECK: OK"
exit 0
