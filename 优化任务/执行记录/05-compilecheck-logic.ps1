param(
    [string]$ProjectRoot = "C:\Users\1\repos\Project Hero\Project-Hero",
    [string]$OutDir = "$env:TEMP\t05-compilecheck",
    [switch]$IncludeTests
)

$sdk = "C:\Program Files\Unity\Hub\Editor\6000.6.2f1\Editor\Data\DotNetSdk"
$dotnet = Join-Path $sdk "dotnet.exe"
$csc = Join-Path $sdk "sdk\8.0.318\Roslyn\bincore\csc.dll"
$netstd = Join-Path $sdk "packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1"

if (-not (Test-Path -LiteralPath $dotnet)) { throw "bundled dotnet not found: $dotnet" }
if (-not (Test-Path -LiteralPath $csc)) { throw "bundled csc not found: $csc" }
if (-not (Test-Path -LiteralPath $netstd)) { throw "netstandard2.1 ref pack not found: $netstd" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$logicDir = Join-Path $ProjectRoot "Assets\Scripts\Logic"
if (-not (Test-Path -LiteralPath $logicDir)) { throw "Logic dir not found: $logicDir" }

$logicFiles = @(Get-ChildItem -LiteralPath $logicDir -Recurse -File -Filter *.cs | ForEach-Object { $_.FullName })
if ($logicFiles.Count -eq 0) { throw "no Logic sources found under $logicDir" }

$refs = @(Get-ChildItem -LiteralPath $netstd -Filter *.dll | ForEach-Object { "-r:`"$($_.FullName)`"" })

$outDll = Join-Path $OutDir "ProjectHero.Logic.dll"
$rsp = Join-Path $OutDir "logic.rsp"

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("-target:library")
$lines.Add("-langversion:9.0")
$lines.Add("-nullable:disable")
$lines.Add("-nologo")
$lines.Add("-nostdlib+")
$lines.Add("-warn:4")
$lines.Add("-out:`"$outDll`"")
foreach ($r in $refs) { $lines.Add($r) }
foreach ($f in $logicFiles) { $lines.Add("`"$f`"") }
Set-Content -LiteralPath $rsp -Value $lines -Encoding UTF8

Write-Host "compiling $($logicFiles.Count) Logic sources -> $outDll"
$output = & $dotnet $csc "@$rsp" 2>&1
$exit = $LASTEXITCODE
$output | ForEach-Object { $_ }
Write-Host "csc exit=$exit"
if ($exit -eq 0) { Write-Host "LOGIC COMPILE: OK" } else { Write-Host "LOGIC COMPILE: FAILED" }
exit $exit
