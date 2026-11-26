$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$sourcePaths = @(
    Get-ChildItem -LiteralPath (Join-Path $repoPath 'Assets/Scripts/Logic') -Recurse -Filter '*.cs' | Where-Object Name -ne 'IsExternalInit.cs' | Select-Object -ExpandProperty FullName
    Join-Path $repoPath 'Assets/Scripts/Authoring/FormalAiBaseline.cs'
    Join-Path $repoPath 'Assets/Scripts/Authoring/BattleDefinitionValidator.cs'
    Join-Path $repoPath 'Assets/Scripts/Authoring/FormalAiMatchup.cs'
    Join-Path $PSScriptRoot 'Shims.cs'
    Join-Path $PSScriptRoot 'FormalAiSourceExport.cs'
    Join-Path $PSScriptRoot 'RecipeProbe.cs'
    Join-Path $PSScriptRoot 'FullMoves.cs'
) | Sort-Object -Unique
$hashRows = @($sourcePaths | ForEach-Object { $_.Substring($repoPath.Length + 1).Replace('\','/') + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() })
$compilerKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($hashRows -join "`n") + (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash))).ToLowerInvariant()
$buildPath = Join-Path $PSScriptRoot ('build/' + $compilerKey.Substring(0,16))
$dllPath = Join-Path $buildPath 'ProjectHero.Logic.dll'
if (-not (Test-Path -LiteralPath $dllPath)) {
    New-Item -ItemType Directory -Path $buildPath -Force | Out-Null
    [Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')) | Out-Null
    [Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')) | Out-Null
    $parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview).WithPreprocessorSymbols([string[]]@('PROJECT_HERO_STANDALONE'))
    $trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    foreach ($sourcePath in $sourcePaths) {
        $sourceText = [IO.File]::ReadAllText($sourcePath)
        if ($sourcePath -eq (Join-Path $repoPath 'Assets/Scripts/Logic/Simulation/BattleSimulation.ForcedDisplacement.cs')) {
            $oldWall = @'
            long raw = checked(contact.RemainingMomentumUnits * MomentumQuantizer.HealthQ10PerLifePoint + unitsPerPoint / 2)
                / unitsPerPoint;
'@
            $sourceText = $sourceText.Replace("`r`n", "`n")
            $oldWall = $oldWall.Replace("`r`n", "`n")
            if ([regex]::Matches($sourceText, [regex]::Escape($oldWall)).Count -ne 1) { throw 'Expected single wall damage conversion' }
            $sourceText = $sourceText.Replace($oldWall, '            long raw = global::RecipeProbe.ScaleWallDamage(contact.RemainingMomentumUnits, unitsPerPoint);')
        }
        if ($sourcePath -eq (Join-Path $repoPath 'Assets/Scripts/Logic/Combat/CombatValuesV05.cs')) {
            $oldHash = 'writer.Write("combat_values.version", Version).Write("combat_values.momentum_scale", MomentumUnitsPerPoint)'
            $newHash = 'writer.Write("probe.wall_percent", global::RecipeProbe.WallPercent).Write("combat_values.version", Version).Write("combat_values.momentum_scale", MomentumUnitsPerPoint)'
            if ([regex]::Matches($sourceText, [regex]::Escape($oldHash)).Count -ne 1) { throw 'Expected single protocol writer' }
            $sourceText = $sourceText.Replace($oldHash, $newHash)
        }
        if ($sourcePath -eq (Join-Path $repoPath 'Assets/Scripts/Logic/Combat/UnitDefinition.cs')) {
            # The archived v0.5 schema otherwise pins HP/A/M to attribute-derived values.
            # This definition-only exception exists exclusively in this standalone assembly.
            $oldCheck = @'
attributes.Validate() != null || definition.InitialHealth != attributes.MaximumHealth
                    || definition.ActionSpeed != 20f || definition.MoveSpeed != attributes.MoveSpeed
'@
            $newCheck = @'
attributes.Validate() != null
                    || (!global::RecipeProbe.IsHp50Probe(definition) &&
                        (definition.InitialHealth != attributes.MaximumHealth
                         || definition.ActionSpeed != 20f || definition.MoveSpeed != attributes.MoveSpeed))
'@
            $oldCheck = $oldCheck.Replace("`r`n", "`n")
            $newCheck = $newCheck.Replace("`r`n", "`n")
            $sourceText = $sourceText.Replace("`r`n", "`n")
            if ([regex]::Matches($sourceText, [regex]::Escape($oldCheck)).Count -ne 1) {
                throw 'Expected single v0.5 unit validation check; update the probe before rebuilding.'
            }
            $sourceText = $sourceText.Replace($oldCheck, $newCheck)
        }
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sourceText, $parseOptions, $sourcePath, [Text.Encoding]::UTF8))
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
$manifest = @{ compilerKey = $compilerKey; assemblySha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant(); sourceHashes = $hashRows; buildScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant(); compiler = $PSVersionTable.PSVersion.ToString(); compatibilityPolyfillExcluded = 'Assets/Scripts/Logic/IsExternalInit.cs'; inMemoryOverrides = @('UnitDefinition.cs: HP50 and two speed pairs only for named probe/archived formal units', 'BattleSimulation.ForcedDisplacement.cs: only wall damage scaled, single half-up rounding', 'CombatValuesV05.cs: wall percent included in protocol hash; momentum formula unmodified') } | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'probe-build-manifest.json'), $manifest)
Write-Output $dllPath
