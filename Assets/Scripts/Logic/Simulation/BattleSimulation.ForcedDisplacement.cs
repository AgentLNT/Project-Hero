using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Simulation
{
    public sealed partial class BattleSimulation
    {
        private bool UsesWalkingDisplacement => _definition.Rules.ForcedDisplacementProtocolVersion.Value
            == ForcedDisplacementProtocolVersion.WalkingMomentumV2;

        private void BeginForcedDisplacements(long tick)
        {
            foreach (var request in _displacementRequests)
            {
                if (!_unitsById.TryGetValue(request.TargetUnitId.Value, out var unit)
                    || !unit.IsCombatEffective)
                    continue;
                if (request.ControlResistanceUnits <= 0 || request.MomentumUnits < request.ControlResistanceUnits)
                    continue;

                var definition = _definition.FindUnit(unit.DefinitionId);
                var actionSet = _definition.FindActionSet(definition.ActionSetId);
                int baseStepTicks = BattleRules.DefaultWalkBaseStepTicks;
                if (actionSet != null)
                {
                    foreach (var actionId in actionSet.ActionSpecIds)
                    {
                        if (_definition.FindAction(actionId)?.Timing is not MoveTimingSpec walk) continue;
                        baseStepTicks = walk.BaseStepTicks;
                        break;
                    }
                }
                var rules = _definition.Rules;
                int baseTicks = TickQuantization.ResolveMoveBaseStepTicks(baseStepTicks, rules.ReferenceMoveSpeed, definition.MoveSpeed);
                long stepTicks = (long)baseTicks * rules.PathCostRules.StepWeightUnits(request.Direction);
                var motion = new ForcedDisplacementMotion(request.Direction, request.MomentumUnits,
                    request.ControlResistanceUnits, checked(tick + stepTicks), stepTicks, request.ConflictGroupKey,
                    request.SourcePlanId)
                {
                    AdrenalineSourceUnitId = _definition.AdrenalineRules.UseHealthThresholds
                        ? _scheduleAuthority.Registry.Find(request.SourcePlanId)?.OwnerUnitId ?? default : default
                };

                EndForcedDisplacement(unit, tick, ForcedDisplacementExitReason.ReplacedByImpact);
                unit.ForcedMotion = motion;
                _outbox.Emit(sequence => new ForcedDisplacementStartedEvent(tick, sequence, unit.UnitId, unit.Position, motion));
            }
        }

        private void AdvanceForcedDisplacement(long tick)
        {
            if (!UsesWalkingDisplacement) return;
            _displacementBatch = ForcedDisplacementBatch.Empty;
            _invalidatedPlanIds = Array.Empty<ActionPlanId>();
            List<ForcedDisplacementRequest> due = null;
            foreach (var unit in _units)
            {
                if (unit.ForcedMotion is not { } motion || motion.NextStepTick > tick || !unit.IsCombatEffective)
                    continue;
                due ??= new List<ForcedDisplacementRequest>();
                due.Add(new ForcedDisplacementRequest(unit.UnitId, motion.Direction, 1,
                    motion.RemainingMomentumUnits, motion.ConflictGroupKey));
            }
            if (due == null) return;

            _displacementBatch = _displacementSolver.ResolveAll(due, BuildUnitSnapshots(), _encounter.GridBoundary, tick)
                ?? ForcedDisplacementBatch.Empty;
            CommitDisplacementBatch(tick, _displacementBatch, due);

            foreach (var step in _displacementBatch.Resolutions)
            {
                var unit = _unitsById[step.TargetUnitId.Value];
                var motion = unit.ForcedMotion;
                if (step.AppliedSteps == 0)
                {
                    EndForcedDisplacement(unit, tick, ForcedDisplacementExitReason.Blocked);
                    if (step.StopReason is ForcedDisplacementStopReason.StaticObstacle
                        or ForcedDisplacementStopReason.Boundary or ForcedDisplacementStopReason.WallUnit)
                    {
                        _outbox.Emit(sequence => new WallContactEvent(tick, sequence, unit.UnitId, step.From,
                            motion.Direction, step.StopReason, motion.RemainingMomentumUnits,
                            motion.ConflictGroupKey, motion.SourcePlanId) { AdrenalineSourceUnitId = motion.AdrenalineSourceUnitId });
                    }
                    continue;
                }

                unit.ForcedMotion = motion with
                {
                    RemainingMomentumUnits = motion.RemainingMomentumUnits - motion.ControlResistanceUnits,
                    NextStepTick = checked(tick + motion.StepTicks)
                };
                if (unit.ForcedMotion.RemainingMomentumUnits < motion.ControlResistanceUnits)
                    EndForcedDisplacement(unit, tick, ForcedDisplacementExitReason.MomentumExhausted);
            }
        }

        private void EndForcedDisplacement(UnitRuntimeState unit, long tick, ForcedDisplacementExitReason reason)
        {
            if (unit.ForcedMotion is not { } motion) return;
            unit.ForcedMotion = null;
            _outbox.Emit(sequence => new ForcedDisplacementEndedEvent(tick, sequence, unit.UnitId, unit.Position, motion, reason));
        }

        private void InterruptForcedDisplacementPlans(long tick)
        {
            if (!UsesWalkingDisplacement || _definition.Rules.UseExplicitControlEffects || _displacementRequests.Count == 0) return;
            foreach (var plan in _scheduleAuthority.Registry.ActivePlans)
            {
                if (plan.ClashResolvedAtTick >= 0 || (!plan.IsRunning && !plan.IsReaction)) continue;
                if (IsInForcedDisplacement(plan.OwnerUnitId))
                    _terminalCoordinator.EnterTerminal(plan, ActionTerminationReason.InterruptedByControl, tick);
            }
        }

        private bool IsInForcedDisplacement(UnitId unitId)
            => _unitsById.TryGetValue(unitId.Value, out var unit) && unit.ForcedMotion != null;

        private void OpenThreatsAfterForcedRelocation(long tick)
        {
            if (!_definition.Rules.UseExplicitControlEffects || !UsesWalkingDisplacement || _displacementBatch.IsEmpty) return;
            var relocatedUnits = new HashSet<long>();
            foreach (var relocation in _displacementBatch.Resolutions)
                if (relocation.IsRelocating) relocatedUnits.Add(relocation.TargetUnitId.Value);
            if (relocatedUnits.Count == 0) return;

            foreach (var plan in _scheduleAuthority.Registry.ActivePlans)
                if (plan.IsRunning && plan.ActionType == ActionType.Attack && plan.ImpactTick > tick && plan.ClashResolvedAtTick < 0)
                    _reactionSystem.OpenForForcedRelocation(plan, tick, _openedOpportunitiesThisTick, relocatedUnits);
        }

        private void ApplyWallImpactDamage(WallContactEvent contact)
        {
            var unit = _unitsById[contact.TargetUnitId.Value];
            int unitsPerPoint = _definition.AdrenalineRules.UseCombatValuesV05
                ? CombatValuesV05.MomentumUnitsPerPoint : MomentumQuantizer.MomentumUnitsPerLegacyMomentum;
            int wallDamagePermille = _definition.Rules.ScalarImpact?.WallDamagePermille ?? 1000;
            long raw = TickQuantization.RoundHalfUpDivide(
                checked(contact.RemainingMomentumUnits * MomentumQuantizer.HealthQ10PerLifePoint * wallDamagePermille),
                checked(unitsPerPoint * 1000L));
            int applied = (int)Math.Min(raw, Math.Max(0, unit.HealthQ10));
            int before = unit.HealthQ10;
            ((IResolutionDamageApplier)this).ApplyDamageQ10(unit.UnitId, applied);
            _outbox.Emit(sequence => new WallImpactDamageEvent(contact.Tick, sequence, unit.UnitId,
                contact.Sequence, raw, applied, unit.HealthQ10, contact.ConflictGroupKey, contact.SourcePlanId));
            if (_definition.AdrenalineRules.UseHealthThresholds && applied > 0)
                ClaimAdrenalineThresholds(unit, before, contact.AdrenalineSourceUnitId);
        }
    }
}
