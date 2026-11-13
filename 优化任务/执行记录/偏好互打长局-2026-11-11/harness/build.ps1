$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$sourcePaths = @(
    Get-ChildItem -LiteralPath (Join-Path $repoPath 'Assets/Scripts/Logic') -Recurse -Filter '*.cs' | Where-Object Name -ne 'IsExternalInit.cs' | Select-Object -ExpandProperty FullName
    Join-Path $repoPath 'Assets/Scripts/Authoring/FormalAiBaseline.cs'
    Join-Path $repoPath 'Assets/Scripts/Authoring/BattleDefinitionValidator.cs'
    Join-Path $repoPath 'Assets/Scripts/Authoring/FormalAiMatchup.cs'
    Join-Path $repoPath 'Assets/Scripts/Editor/FormalAiMatchupTools.cs'
    Join-Path $PSScriptRoot 'FormalAiSourceExport.cs'
    Join-Path $PSScriptRoot 'Shims.cs'
    Join-Path $PSScriptRoot 'Program.cs'
) | Sort-Object -Unique
$hashRows = @($sourcePaths | ForEach-Object { $_.Substring($repoPath.Length + 1).Replace('\','/') + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() })
$compilerKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($hashRows -join "`n") + (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash))).ToLowerInvariant()
$buildPath = Join-Path ([IO.Path]::GetTempPath()) ('ProjectHero-Matchup-' + $compilerKey.Substring(0,16))
$dllPath = Join-Path $buildPath 'ProjectHero.Logic.dll'
if (-not (Test-Path -LiteralPath $dllPath)) {
    New-Item -ItemType Directory -Path $buildPath -Force | Out-Null
    [Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')) | Out-Null
    [Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')) | Out-Null
    $parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview).WithPreprocessorSymbols([string[]]@('PROJECT_HERO_STANDALONE'))
    $trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    foreach ($sourcePath in $sourcePaths) {
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($sourcePath), $parseOptions, $sourcePath, [Text.Encoding]::UTF8))
    }
    $references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    foreach ($referencePath in (Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll')) {
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($referencePath.FullName))
    }
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ProjectHero.Logic', $trees, $references, $options)
    $outputStream = [IO.File]::Create($dllPath)
    try { $result = $compilation.Emit($outputStream) } finally { $outputStream.Dispose() }
    if (-not $result.Success) {
        $result.Diagnostics | Where-Object Severity -eq Error | Select-Object -First 20 | ForEach-Object { Write-Host $_.ToString() }
        Remove-Item -LiteralPath $dllPath
        throw 'Standalone compilation failed'
    }
    $configuration = @{ runtimeOptions = @{ tfm = 'net10.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = '10.0.0' }; rollForward = 'LatestPatch' } } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $buildPath 'ProjectHero.Logic.runtimeconfig.json'), $configuration)
}
$manifest = @{ compilerKey = $compilerKey; assemblySha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant(); sourceHashes = $hashRows; compiler = $PSVersionTable.PSVersion.ToString(); compatibilityPolyfillExcluded = 'Assets/Scripts/Logic/IsExternalInit.cs' } | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'build-manifest.json'), $manifest)
Write-Output $dllPath
