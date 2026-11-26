$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'harness/probe-build-manifest.json') | ConvertFrom-Json
foreach ($row in $manifest.sourceHashes) {
    $pair = $row -split ':', 2
    if ((Get-FileHash -LiteralPath (Join-Path $repoPath $pair[0]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pair[1]) { throw ('Changed source: ' + $pair[0]) }
}
$dllPath = Join-Path $PSScriptRoot ('harness/build/' + $manifest.compilerKey.Substring(0,16) + '/ProjectHero.Logic.dll')
if ((Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.assemblySha256) { throw 'Assembly hash differs' }
$assembly = [Reflection.Assembly]::LoadFrom($dllPath)
$probe = $assembly.GetType('RecipeProbe')
$full = $assembly.GetType('FullMoves')
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$percentProperty = $probe.GetProperty('MomentumPercent', $flags)
$define = $full.GetMethod('Define', $flags)
$source = $full.GetField('Source', $flags).GetValue($null)
$audit = @(
    foreach ($strength in 3,4) {
        foreach ($percent in 100,90,80) {
            $percentProperty.SetValue($null, $percent)
            $d = $define.Invoke($null, [object[]]@($strength,20,30,[ProjectHero.Authoring.FormalAiLayout]::Near,
                [ProjectHero.Logic.AI.AiPolicyKind]::SustainedOffense,[ProjectHero.Logic.AI.AiPolicyKind]::SustainedOffense,$false,$false))
            $attributes = $d.Units[0].CombatAttributes
            $parameters = @(
                foreach ($attack in $d.Actions | Where-Object { $_.Type -eq 'Attack' -and $_.ActionSpecId.Value.EndsWith('.radius_1') }) {
                    $original = $source.Actions | Where-Object ActionSpecId -eq $attack.ActionSpecId
                    if (-not [object]::ReferenceEquals($attack.Timing,$original.Timing) -or
                        -not [object]::ReferenceEquals($attack.Payload.Pattern,$original.Payload.Pattern) -or
                        $attack.Payload.TargetPolicy -ne $original.Payload.TargetPolicy -or
                        $attack.Payload.Tags -ne $original.Payload.Tags -or
                        $attack.Payload.AllowedTargetRelations -ne $original.Payload.AllowedTargetRelations -or
                        $attack.Payload.MomentumDirectionOffsetSteps -ne $original.Payload.MomentumDirectionOffsetSteps) {
                        throw ('Formal shape/timing changed: ' + $attack.ActionSpecId.Value)
                    }
                    [ordered]@{
                        Spec = $attack.ActionSpecId.Value
                        Recipe = $attack.Payload.Strength.Recipe.ToString()
                        Q10 = $attack.Payload.Strength.BaseStrengthQ10
                        DamageQ10 = [ProjectHero.Logic.Combat.CombatValuesV05]::DamageQ10($attributes,$attack.Payload.Strength)
                        MomentumUnits = [ProjectHero.Logic.Combat.CombatValuesV05]::MomentumUnits($attributes,$attack.Payload.Strength)
                        BaseWindupTicks = $attack.Timing.BaseWindupTicks
                        RecoveryTicks = $attack.Timing.RecoveryTicks
                        Pattern = $attack.Payload.Pattern.AttackPatternId.Value
                        TargetPolicy = $attack.Payload.TargetPolicy.ToString()
                        Tags = $attack.Payload.Tags.ToString()
                        ShapeAndTimingIdenticalToSource = $true
                    }
                }
            )
            [ordered]@{ Strength=$strength; Percent=$percent; Parameters=$parameters; Hash=$d.BattleDefinitionHashValue }
        }
    }
)
$audit | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'parameter-audit.json') -Encoding utf8
