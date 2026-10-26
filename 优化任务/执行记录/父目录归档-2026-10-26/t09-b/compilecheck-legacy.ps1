<#
=============================================================================
 Task 09 / stream B : Assembly-CSharp offline COMPILE CHECK (legacy shell)
=============================================================================
 Purpose: B-private iteration accelerator. NOT acceptance evidence.
 It compiles, with the Unity-bundled Roslyn:
   Grid -> Logic -> Compatibility.Runtime -> Authoring -> Assembly-CSharp
 so that syntax/type errors in Assets/Scripts/UI/Timeline/*.cs and
 Assets/Scripts/Core/{Input,Gameplay}/*.cs surface without opening Unity.

 Authoritative results still come only from the Unity Test Runner.
 ASCII-only on purpose (Windows PowerShell 5.1 cannot read UTF-8 without BOM).
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
$pkgCache   = Join-Path $ProjectRoot "Library\PackageCache"

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
    $lines.Add("-nowarn:1701,1702,1591,0618,0649")
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

$logicDll  = Join-Path $OutDir "ProjectHero.Logic.dll"
$gridDll   = Join-Path $OutDir "ProjectHero.Grid.dll"
$compatDll = Join-Path $OutDir "ProjectHero.Compatibility.Runtime.dll"
$authorDll = Join-Path $OutDir "ProjectHero.Authoring.dll"

foreach ($p in @($logicDll, $gridDll, $compatDll)) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "run compilecheck.ps1 first (missing $p)"; exit 4 }
}

$authorOk = New-Assembly -Name "Authoring" `
    -Sources (Get-Sources (Join-Path $ProjectRoot "Assets\Scripts\Authoring")) `
    -ExtraRefs (@($gridDll, $logicDll) + $engineRefs) -OutFile $authorDll
if (-not $authorOk) { exit 2 }

# Assembly-CSharp = every Assets/Scripts/*.cs not owned by an asmdef folder.
# Assets/Scripts/Editor/** belongs to Assembly-CSharp-Editor and is out of B's ownership.
$scriptsDir = Join-Path $ProjectRoot "Assets\Scripts"
$allCs = @(Get-ChildItem -LiteralPath $scriptsDir -Recurse -File -Filter *.cs)
$csScripts = @($allCs | Where-Object {
    $rel = $_.FullName.Substring($scriptsDir.Length + 1)
    -not ($rel -like 'Logic\*') -and
    -not ($rel -like 'Authoring\*') -and
    -not ($rel -like 'Grid\*') -and
    -not ($rel -like 'Core\Compatibility\*') -and
    -not ($rel -like 'Editor\*')
} | ForEach-Object { $_.FullName })
Write-Host ("legacy source candidates: {0} / {1}" -f $csScripts.Count, $allCs.Count)

# Package/asmdef assemblies Unity itself produced: InputSystem, uGUI, TextMeshPro.
$scriptAsm = Join-Path $ProjectRoot "Library\ScriptAssemblies"
$pkgRefs = @()
foreach ($name in @("Unity.InputSystem.dll", "UnityEngine.UI.dll", "Unity.TextMeshPro.dll")) {
    $candidate = Join-Path $scriptAsm $name
    if (Test-Path -LiteralPath $candidate) { $pkgRefs += $candidate }
    else { Write-Host "WARN missing package ref: $candidate" }
}
Write-Host "pkg refs: $($pkgRefs.Count)"

$ok = New-Assembly -Name "AssemblyCSharp" -Sources $csScripts `
    -ExtraRefs (@($gridDll, $logicDll, $compatDll, $authorDll) + $engineRefs + $pkgRefs) `
    -OutFile (Join-Path $OutDir "Assembly-CSharp.dll")

if (-not $ok) { exit 2 }
Write-Host "LEGACY COMPILE CHECK: OK"
exit 0
