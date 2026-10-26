<#
=============================================================================
 Task 09 / stream B : Assembly-CSharp-Editor offline COMPILE CHECK
=============================================================================
 B-private iteration accelerator. NOT acceptance evidence.
 Compiles the Editor/ test files (UnityView\Editor\Task09*.cs) plus
 Assets/Scripts/Editor/** into one Assembly-CSharp-Editor assembly, so that
 syntax/type errors and missing API members surface without opening Unity.

 ASCII-only on purpose (Windows PowerShell 5.1 cannot read UTF-8 without BOM).
=============================================================================
#>

param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string]$OutDir = "$env:TEMP\t09-b-compilecheck",
    [switch]$IncludeScriptsEditor
)

$ErrorActionPreference = "Stop"

$editorRoot = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1"
$editorData = Join-Path $editorRoot "Editor\Data"
$sdk        = Join-Path $editorData "DotNetSdk"
$dotnet     = Join-Path $sdk "dotnet.exe"
$cscPath    = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$nsRefDir   = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"
$engineDir  = Join-Path $editorData "Managed\UnityEngine"
$editorAsm  = Join-Path $editorData "Managed\UnityEditor.dll"

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
        [string]$OutFile, [string]$Target = "library", [string[]]$Defines = @()
    )
    if ($null -eq $Sources -or $Sources.Length -eq 0) { throw "no sources for $Name" }
    $rsp = Join-Path $OutDir "$Name.rsp"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("-target:$Target")
    $lines.Add("-langversion:9.0")
    $lines.Add("-nullable:disable")
    $lines.Add("-nologo")
    $lines.Add("-nostdlib+")
    $lines.Add("-nowarn:1701,1702,1591,0618,0649,0219,0414,0169")
    $lines.Add("-out:`"$OutFile`"")
    foreach ($d in $Defines) { $lines.Add("-define:$d") }
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

$scriptAsm = Join-Path $ProjectRoot "Library\ScriptAssemblies"
$logicDll  = Join-Path $OutDir "ProjectHero.Logic.dll"
$gridDll   = Join-Path $OutDir "ProjectHero.Grid.dll"
$compatDll = Join-Path $OutDir "ProjectHero.Compatibility.Runtime.dll"
$authorDll = Join-Path $OutDir "ProjectHero.Authoring.dll"

# Fall back to Unity's own ScriptAssemblies for the project asmdefs if the
# offline ones are absent.
if (-not (Test-Path -LiteralPath $compatDll)) { $compatDll = Join-Path $scriptAsm "ProjectHero.Compatibility.Runtime.dll" }
if (-not (Test-Path -LiteralPath $logicDll))  { $logicDll  = Join-Path $scriptAsm "ProjectHero.Logic.dll" }
if (-not (Test-Path -LiteralPath $gridDll))   { $gridDll   = Join-Path $scriptAsm "ProjectHero.Grid.dll" }
if (-not (Test-Path -LiteralPath $authorDll)) { $authorDll = Join-Path $scriptAsm "ProjectHero.Authoring.dll" }

$refs = @($gridDll, $logicDll, $compatDll, $authorDll) + $engineRefs
# The predefined Assembly-CSharp is a legitimate reference for Assembly-CSharp-Editor.
$predefined = Join-Path $OutDir "Assembly-CSharp.dll"
if (Test-Path -LiteralPath $predefined) { $refs += $predefined }
else {
    $unityPredefined = Join-Path $scriptAsm "Assembly-CSharp.dll"
    if (Test-Path -LiteralPath $unityPredefined) { $refs += $unityPredefined }
    else { Write-Host "WARN missing Assembly-CSharp.dll (Assets/Scripts/Editor sources may not compile)" }
}
foreach ($name in @("Unity.InputSystem.dll", "UnityEngine.UI.dll", "Unity.TextMeshPro.dll",
                    "nunit.framework.dll", "UnityEngine.TestRunner.dll", "UnityEditor.TestRunner.dll")) {
    $candidate = Join-Path $scriptAsm $name
    if (Test-Path -LiteralPath $candidate) { $refs += $candidate }
    else { Write-Host "WARN missing ref: $candidate" }
}
$nunit = Join-Path $ProjectRoot "Library\PackageCache\com.unity.ext.nunit@0198eae3b53e\net472\unity-custom\nunit.framework.dll"
if (Test-Path -LiteralPath $nunit) { $refs += $nunit }

$sources = @()
$sources += Get-Sources (Join-Path $ProjectRoot "Assets\Tests\EditMode\UnityView\Editor")
if ($IncludeScriptsEditor) { $sources += Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Editor") }
Write-Host "editor source candidates: $($sources.Count)"
if ($sources.Count -eq 0) { Write-Host "no sources"; exit 4 }

$ok = New-Assembly -Name "AssemblyCSharpEditor" -Sources $sources -ExtraRefs $refs `
    -OutFile (Join-Path $OutDir "Assembly-CSharp-Editor.dll") `
    -Defines @("UNITY_EDITOR", "UNITY_INCLUDE_TESTS")

if (-not $ok) { exit 2 }
Write-Host "EDITOR COMPILE CHECK: OK"
exit 0
