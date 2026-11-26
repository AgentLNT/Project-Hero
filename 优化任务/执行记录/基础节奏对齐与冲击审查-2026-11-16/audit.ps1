$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoPath = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$sourceRoot = Join-Path $repoPath '优化任务/执行记录/双回合拆解与多人预演-2026-11-16/harness'
$manifestPath = Join-Path $sourceRoot 'probe-build-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
# Reuse the archived experiment binary only if every compiled input still matches.
foreach ($entry in $manifest.sourceHashes) {
    $pair = $entry -split ':', 2
    if ((Get-FileHash -LiteralPath (Join-Path $repoPath $pair[0]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pair[1]) {
        throw ('Source differs from experiment assembly: ' + $pair[0])
    }
}
$assemblyPath = Join-Path $sourceRoot ('build/' + $manifest.compilerKey.Substring(0, 16) + '/ProjectHero.Logic.dll')
if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.assemblySha256) {
    throw 'Experiment assembly hash mismatch'
}
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$exportType = $assembly.GetTypes() | Where-Object Name -eq 'FormalAiSourceExport'
$source = $exportType.GetMethod('Definition').Invoke($null, @())
$unit = $source.Units[0]
$resistance = [ProjectHero.Logic.Combat.MomentumQuantizer]::QuantizeControlResistanceUnits($unit.Mass, $unit.MomentumSpeed)
$move = $source.Actions | Where-Object Type -eq 'Move' | Select-Object -First 1
$referenceActions = @(
    foreach ($action in $source.Actions | Where-Object { $_.Type -eq 'Attack' -and $_.ActionSpecId.Value.EndsWith('radius_1') }) {
        $damage = ($action.Payload.DamageComponents | Measure-Object -Property RawAmount -Sum).Sum
        $momentum = [ProjectHero.Logic.Combat.MomentumQuantizer]::QuantizeMomentumUnits(
            $unit.Mass, $unit.MomentumSpeed, $action.Payload.ForceMultiplier, $action.Payload.ImpactProfileId)
        $recipe = switch ($action.Payload.ImpactProfileId.Value) {
            'impact.slash' { [ProjectHero.Logic.Combat.PhysicalRecipe]::Slash }
            'impact.pierce' { [ProjectHero.Logic.Combat.PhysicalRecipe]::Pierce }
            'impact.blunt' { [ProjectHero.Logic.Combat.PhysicalRecipe]::Blunt }
            default { throw 'Unknown recipe mapping' }
        }
        $alignedDamage24 = $damage * 24 / 50
        $recipeMomentum24 = $alignedDamage24 * [ProjectHero.Logic.Combat.CombatValuesV05]::MomentumCoefficient($recipe) / [ProjectHero.Logic.Combat.CombatValuesV05]::DamageCoefficient($recipe)
        [pscustomobject]@{
            action = $action.ActionSpecId.Value
            directDamage = $damage
            fractionOf50Hp = $damage / 50
            windupA20 = [ProjectHero.Logic.Combat.TickQuantization]::ResolveWindupTicks($action.Timing.BaseWindupTicks, 20, 20)
            windupA24 = [ProjectHero.Logic.Combat.TickQuantization]::ResolveWindupTicks($action.Timing.BaseWindupTicks, 20, 24)
            recoveryTicks = $action.Timing.RecoveryTicks
            momentumUnits = $momentum
            resistanceUnits = $resistance
            singleHitImpactRatio = $momentum / $resistance
            matchedDamageAt24Hp = $alignedDamage24
            unquantizedV05RecipeMomentumAt24Hp = $recipeMomentum24
            stabilityRequiredToAlsoMatchImpactRatio = $recipeMomentum24 / ($momentum / $resistance)
        }
    }
)
$baseAttributes = [ProjectHero.Logic.Combat.CombatAttributesV05]::new(1, 1, 1, 0, $null)
$experiment = [ProjectHero.Logic.Experiments.CombatExperimentDefinition]::Build(
    [ProjectHero.Logic.Experiments.CombatExperimentScenario]::new('alignment-read', 'quick', 0, 24, 10240,
        [ProjectHero.Logic.Experiments.ExperimentScript]::Unopposed, $false, $false, 300, [ulong]31, 0))
$v05Actions = @(
    foreach ($action in $experiment.Actions | Where-Object { $_.ActionSpecId.Value -in @('action.experiment.quick','action.experiment.heavy','action.experiment.sweep') }) {
        [pscustomobject]@{
            action = $action.ActionSpecId.Value
            damage = [ProjectHero.Logic.Combat.CombatValuesV05]::DamageQ10($baseAttributes, $action.Payload.Strength) / 1024
            fractionOf24Hp = [ProjectHero.Logic.Combat.CombatValuesV05]::DamageQ10($baseAttributes, $action.Payload.Strength) / (1024 * 24)
            momentum = [ProjectHero.Logic.Combat.CombatValuesV05]::MomentumUnits($baseAttributes, $action.Payload.Strength) / 1024
            windup = $action.Timing.BaseWindupTicks
            recovery = $action.Timing.RecoveryTicks
        }
    }
)
$growth = @(
    foreach ($level in 1..5) {
        $attributes = [ProjectHero.Logic.Combat.CombatAttributesV05]::new($level, $level, $level, 0, $null)
        $quick = $experiment.Actions | Where-Object { $_.ActionSpecId.Value -eq 'action.experiment.quick' }
        $heavy = $experiment.Actions | Where-Object { $_.ActionSpecId.Value -eq 'action.experiment.heavy' }
        $damage = [ProjectHero.Logic.Combat.CombatValuesV05]::DamageQ10($attributes, $quick.Payload.Strength) / 1024
        [pscustomobject]@{
            allAttributes = $level; health = $attributes.MaximumHealth; moveSpeed = $attributes.MoveSpeed
            quickDamage = $damage; quickHitsToKill = [Math]::Ceiling($attributes.MaximumHealth / $damage)
            damageReceivedIncomePerQuickHit = $damage / 8
            heavyImpactToStabilityRatio = [ProjectHero.Logic.Combat.CombatValuesV05]::MomentumUnits($attributes, $heavy.Payload.Strength) / $attributes.StabilityQ10
        }
    }
)
$rows = [Collections.Generic.List[object]]::new()
$probeDir = Join-Path $PSScriptRoot 'probes'
New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
foreach ($spec in @(
    @('early-r10','EarlyInterrupt',10240,$false,$false,300),
    @('early-r10_5','EarlyInterrupt',10752,$false,$false,300),
    @('late-r10','LateInterrupt',10240,$false,$false,300),
    @('late-r10_5','LateInterrupt',10752,$false,$false,300),
    @('clash-r10','Clash',10240,$false,$false,300),
    @('clash-r10_5','Clash',10752,$false,$false,300),
    @('open-r10','Unopposed',10240,$true,$false,800),
    @('open-r10_5','Unopposed',10752,$true,$false,800),
    @('wall-r10','Unopposed',10240,$true,$true,800),
    @('wall-r10_5','Unopposed',10752,$true,$true,800)
)) {
    $scenario = [ProjectHero.Logic.Experiments.CombatExperimentScenario]::new(
        $spec[0], 'heavy', 0, 24, $spec[2], [ProjectHero.Logic.Experiments.ExperimentScript]$spec[1],
        $spec[3], $spec[4], $spec[5], [ulong]31, 0)
    $result = [ProjectHero.Logic.Experiments.CombatExperimentRunner]::Run($scenario)
    [ProjectHero.Logic.Experiments.CombatExperimentRunner]::VerifyReplay($result)
    $rejections = @($result.Decisions | Where-Object { $_.IngressRejection -and $_.IngressRejection -ne 'not-submitted' })
    if ($rejections.Count) { throw ('Probe rejected input: ' + $spec[0]) }
    $stream = [IO.File]::Create((Join-Path $probeDir ($spec[0] + '.heroReplay')))
    try { [ProjectHero.Logic.Replay.ReplayFile]::Save($stream, $result.Replay) } finally { $stream.Dispose() }
    $facts = @($result.Events | Where-Object { $_.GetType().Name -in @(
        'DamageChannelResolvedEvent','TargetAggregateResolvedEvent','ActionPlanTerminatedEvent',
        'ClashParticipantResolvedEvent','ForcedDisplacementResolvedEvent') } | ForEach-Object {
            [pscustomobject]@{ type = $_.GetType().Name; fact = $_ }
        })
    $facts | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $probeDir ($spec[0] + '.facts.json')) -Encoding utf8
    $row = [pscustomobject]@{
        id = $spec[0]; scenario = $scenario; definitionHash = $result.Definition.BattleDefinitionHashValue
        outcome = $result.Outcome; finalTick = $result.FinalSnapshot.Tick
        units = @($result.FinalSnapshot.Units | Select-Object UnitId, HealthQ10, X, Y, IsAlive)
        controlTerminations = @($result.Events | Where-Object {
            $_.GetType().Name -eq 'ActionPlanTerminatedEvent' -and $_.ReasonCode -eq 'ACTION_TERMINATION_INTERRUPTED_BY_CONTROL'
        } | Select-Object Tick, OwnerUnitId, ActionPlanId, ReasonCode)
        replayVerified = $true; rejectedInputs = $rejections.Count
    }
    $rows.Add($row)
}
$report = [ordered]@{
    scope = 'Read-only reference alignment and fixed-script stability probes; no balance migration or AI win-rate claim.'
    verifiedSourceFiles = $manifest.sourceHashes.Count
    assemblySha256 = $manifest.assemblySha256
    sourceDefinitionHash = $source.BattleDefinitionHashValue
    reference = @(@{actionSpeed=20;moveSpeed=30;health=50},@{actionSpeed=24;moveSpeed=20;health=50})
    referenceBlockCost = 3; referenceDodgeCost = 2
    moveTicksPerWeightM30 = [ProjectHero.Logic.Combat.TickQuantization]::ResolveMoveBaseStepTicks($move.Timing.BaseStepTicks, 20, 30)
    moveTicksPerWeightM20 = [ProjectHero.Logic.Combat.TickQuantization]::ResolveMoveBaseStepTicks($move.Timing.BaseStepTicks, 20, 20)
    moveRecoveryTicks = $move.Timing.RecoveryTicks
    referenceActions = $referenceActions; v05Actions = $v05Actions; v05SimultaneousGrowth = $growth
    probes = $rows.ToArray()
}
$report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'audit.json') -Encoding utf8
$rows | Select-Object id, outcome, finalTick, replayVerified | Format-Table -AutoSize
