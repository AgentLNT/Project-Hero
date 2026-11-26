using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Units;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Tempo = ProjectHero.Authoring.BattleDefinitionBuilder.MainBattleTempo;

namespace ProjectHero.Editor
{
    public static class ScalarImpactValidation
    {
        private sealed class Case
        {
            public readonly Tempo Tempo;
            public readonly int Size;
            public readonly bool Mirror;
            public readonly bool Walls;
            public readonly int? WallPermille;
            public readonly int Growth;
            public readonly bool TacticalAi;
            public readonly bool ValueControl;

            public Case(Tempo tempo, int size, bool mirror, bool walls, int? wallPermille = null,
                int growth = 0, bool tacticalAi = false, bool valueControl = false)
            {
                Tempo = tempo;
                Size = size;
                Mirror = mirror;
                Walls = walls;
                WallPermille = wallPermille;
                Growth = growth;
                TacticalAi = tacticalAi;
                ValueControl = valueControl;
            }

            public string Id => $"{Tempo}-{Size}v{Size}-{(Mirror ? 1 : 0)}-{(Walls ? "wall" : "open")}-"
                + $"{(WallPermille.HasValue ? WallPermille.Value.ToString(CultureInfo.InvariantCulture) : "reference")}-{Growth}-"
                + (TacticalAi ? ValueControl ? "utility-control" : "utility" : "fixed");
            public bool RecordReplay => Growth == 0 && !TacticalAi && !Mirror && WallPermille == 500;
        }

        public static void Run()
        {
            string output = Environment.GetEnvironmentVariable("HERO_SCALAR_VALIDATION_OUTPUT");
            if (string.IsNullOrWhiteSpace(output) || Directory.Exists(output))
                throw new InvalidOperationException("Set HERO_SCALAR_VALIDATION_OUTPUT to a fresh output directory.");
            Directory.CreateDirectory(output);
            var seed = LoadProductionSeed();
            var rawUnits = new LegacyUnitStatInputs(10, 10, 10, 10, 0, 0, false);
            var units = new LegacyBattleUnitSource(rawUnits, rawUnits);
            var current = BattleDefinitionBuilder.WithProductionEnemyAi(Build(BattleDefinitionBuilder.BuildThresholdAdrenalineBattleDefinition(
                units, true, Array.Empty<GridPoint>(), true, Tempo.Action20Move30)));
            if (current.BattleDefinitionHashValue != seed.Definition.BattleDefinitionHashValue)
                throw new InvalidOperationException("The authored production tempo and validation baseline differ.");

            var heroWeapon = CreateWeapon(1);
            var enemyWeapon = CreateWeapon(2);
            (Case Case, BattleDefinition Definition)[] cases;
            try
            {
                bool pilot = Environment.GetEnvironmentVariable("HERO_SCALAR_VALIDATION_PILOT") == "1";
                bool diagnostics = Environment.GetEnvironmentVariable("HERO_SCALAR_VALIDATION_DIAGNOSTICS") == "1";
                bool fixedOnly = Environment.GetEnvironmentVariable("HERO_SCALAR_VALIDATION_FIXED_ONLY") == "1";
                cases = Matrix().Where(configuration => !pilot
                    || (configuration.Tempo == Tempo.Action20Move30 && !configuration.Mirror))
                    .Where(configuration => !diagnostics || (configuration.TacticalAi && !configuration.Mirror))
                    .Where(configuration => !fixedOnly || !configuration.TacticalAi)
                    .Select(configuration => (Case: configuration, Definition:
                    CreateDefinition(configuration, units, heroWeapon, enemyWeapon))).ToArray();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(heroWeapon);
                UnityEngine.Object.DestroyImmediate(enemyWeapon);
            }
            var rows = new string[cases.Length];
            Parallel.For(0, cases.Length, new ParallelOptions { MaxDegreeOfParallelism = 6 }, index =>
            {
                var job = cases[index];
                rows[index] = Simulate(job, seed.EncounterId, seed.RuntimeInputs, output);
                Debug.Log("SCALAR_CASE_COMPLETE " + job.Case.Id);
            });
            File.WriteAllLines(Path.Combine(output, "matrix.csv"), new[]
            {
                "case,tempo,teamSize,mirror,walls,wallPermille,growth,tacticalAi,controlAi,ticks,ended,damageQ10,positiveContacts,wallHits,wallDamageQ10,clashParticipants,knockdowns,interruptions,crossWindowAttacks,commandRejections,firstDeath,preWindowDeaths,teamKills,unitTwoWindowSharedKills,replay,definitionHash,observedCrossings,crossingHits,illegalStarts"
            }.Concat(rows));
            Debug.Log($"SCALAR_VALIDATION_COMPLETE cases={cases.Length} audited={cases.Count(work => work.Case.RecordReplay)}");
        }

        public static void CheckProductionAi()
        {
            string output = Environment.GetEnvironmentVariable("HERO_SCALAR_VALIDATION_OUTPUT");
            if (string.IsNullOrWhiteSpace(output) || Directory.Exists(output))
                throw new InvalidOperationException("Set HERO_SCALAR_VALIDATION_OUTPUT to a fresh output directory.");
            var reference = LoadProductionSeed();
            var scalar = LoadProductionSeed(enableScalar: true);
            if (reference.Definition.Rules.ScalarImpact != null || scalar.Definition.Rules.ScalarImpact == null)
                throw new InvalidOperationException("Production replay audit requires v11 on disk with v12 enabled only in memory.");

            var trials = (from seed in new[] { reference, scalar }
                from scriptedPlayer in new[] { false, true }
                select (Seed: seed, ScriptedPlayer: scriptedPlayer)).ToArray();
            Directory.CreateDirectory(output);
            var rows = new string[trials.Length];
            Parallel.For(0, trials.Length, index =>
            {
                var trial = trials[index];
                rows[index] = AuditProductionAi(trial.Seed, trial.ScriptedPlayer, output);
            });
            File.WriteAllLines(Path.Combine(output, "matrix.csv"), new[]
            {
                "definition,definitionHash,player,aiExecution,preflight,ticks,ended,rejections,staleRevision,cancelledPlans,playerSubmissions,aiPlans,positiveContacts,knockdowns,interruptions,heroHpQ10,enemyHpQ10,replay"
            }.Concat(rows));
            Debug.Log($"SCALAR_PRODUCTION_AUDIT_COMPLETE cases={trials.Length}");
        }

        private static string AuditProductionAi(BattleSimulationSeed seed, bool scriptedPlayer, string output)
        {
            var definition = seed.Definition;
            var encounter = definition.FindEncounter(seed.EncounterId);
            var ai = encounter.Controllers.Single(binding => binding.SourceKind == CommandSourceKind.Ai);
            if (ai.AiExecution != AiExecutionKind.UtilityBehaviorTree || ai.AiForecast?.RequireSubmissionPreflight != true)
                throw new InvalidOperationException("Production utility AI or submission preflight is missing.");
            var players = encounter.Controllers.Where(binding => binding.SourceKind == CommandSourceKind.Player)
                .Select(binding => binding.ControllerId).ToArray();
            using var simulation = ProductionBattleComposition.Create(definition, seed.EncounterId, seed.RuntimeInputs);
            var aiUnits = simulation.DecisionSnapshotFor(ai.ControllerId).ControlledUnitIds.Select(unit => unit.Value).ToHashSet();
            var recorder = new ReplayRecorder(simulation);
            int rejections = 0, stale = 0, cancelled = 0, aiPlans = 0, contacts = 0, knockdowns = 0, interruptions = 0;
            for (int tick = 0; tick < 4 * encounter.TurnSubmission.WindowDurationTicks && !simulation.IsEnded; tick++)
            {
                if (scriptedPlayer && tick > 0) SubmitFixedCommand(simulation, players, tick);
                recorder.CaptureBeforeStep();
                var step = simulation.Step(tick, simulation.CommandIngress.FreezeTick(tick));
                recorder.RecordCommittedStep(step);
                foreach (var fact in step.Events.Events)
                {
                    switch (fact)
                    {
                        case CommandRejectedEvent rejected:
                            rejections++;
                            if (rejected.ReasonCode == ProjectHero.Logic.Timeline.ScheduleCodes.STALE_SCHEDULE_REVISION) stale++;
                            break;
                        case ActionPlanTerminatedEvent terminal when terminal.Reason == (int)ActionTerminationReason.CancelledByCommand:
                            cancelled++;
                            break;
                        case ActionPlanTerminatedEvent terminal when terminal.Reason == (int)ActionTerminationReason.InterruptedByControl:
                            interruptions++;
                            break;
                        case ActionPlanCreatedEvent created when aiUnits.Contains(created.OwnerUnitId):
                            aiPlans++;
                            break;
                        case DamageChannelResolvedEvent hit when hit.AfterBlockDamageQ10 > 0:
                            contacts++;
                            break;
                        case UnitStateChangedEvent state when state.ToState == UnitState.KnockedDown:
                            knockdowns++;
                            break;
                    }
                }
            }
            string player = scriptedPlayer ? "scripted" : "idle";
            var finalUnits = simulation.CurrentSnapshot.Units;
            var replay = recorder.BuildReplay();
            VerifyDiskReplay(definition, replay, Path.Combine(output, definition.RulesVersion + "-" + player + ".heroReplay"));
            return Csv(definition.RulesVersion, definition.BattleDefinitionHashValue, player, ai.AiExecution,
                ai.AiForecast.RequireSubmissionPreflight, simulation.Tick + 1, simulation.IsEnded, rejections, stale,
                cancelled, replay.Submissions.Count, aiPlans, contacts, knockdowns, interruptions,
                finalUnits.Single(unit => !aiUnits.Contains(unit.UnitId)).HealthQ10,
                finalUnits.Single(unit => aiUnits.Contains(unit.UnitId)).HealthQ10, true);
        }

        private static BattleSimulationSeed LoadProductionSeed(bool enableScalar = false)
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/CombatSampleScene.unity");
            var source = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<NewBattleSimulationSource>(true)).Single();
            if (enableScalar)
            {
                var serialized = new SerializedObject(source);
                serialized.FindProperty("_useScalarImpact").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return source.BuildSeed() ?? throw new InvalidOperationException(source.LastConfigurationError);
        }

        private static IEnumerable<Case> Matrix()
        {
            foreach (var tempo in new[] { Tempo.Action20Move30, Tempo.Action24Move20 })
            foreach (int size in new[] { 1, 2, 3 })
            foreach (bool mirror in new[] { false, true })
            foreach (bool walls in new[] { false, true })
            {
                yield return new Case(tempo, size, mirror, walls);
                yield return new Case(tempo, size, mirror, walls, 1000);
                if (walls) yield return new Case(tempo, size, mirror, walls, 600);
                yield return new Case(tempo, size, mirror, walls, 500);
                if (!walls)
                {
                    yield return new Case(tempo, size, mirror, walls, 500, tacticalAi: true);
                    yield return new Case(tempo, size, mirror, walls, 500, tacticalAi: true, valueControl: true);
                }
                if (size > 1)
                {
                    yield return new Case(tempo, size, mirror, walls, 500, growth: 1);
                    yield return new Case(tempo, size, mirror, walls, 500, growth: 5);
                    if (walls)
                    {
                        yield return new Case(tempo, size, mirror, walls, 600, growth: 1);
                        yield return new Case(tempo, size, mirror, walls, 600, growth: 5);
                        yield return new Case(tempo, size, mirror, walls, 1000, growth: 1);
                        yield return new Case(tempo, size, mirror, walls, 1000, growth: 5);
                    }
                }
            }
        }

        private static BattleDefinition CreateDefinition(Case configuration, LegacyBattleUnitSource units,
            WeaponBattleProfile heroWeapon, WeaponBattleProfile enemyWeapon)
        {
            var walls = configuration.Walls
                ? (from x in new[] { -10, 10 } from y in Enumerable.Range(-8, 17)
                    select new GridPoint(x, y * 2)).ToArray()
                : Array.Empty<GridPoint>();
            bool useWeapon = configuration.Growth > 0;
            var source = configuration.WallPermille is int factor
                ? Build(BattleDefinitionBuilder.BuildScalarImpactBattleDefinition(units, walls, configuration.Tempo,
                    new ScalarImpactRules(WallDamagePermille: factor), useWeapon ? heroWeapon : null,
                    useWeapon ? enemyWeapon : null, useWeapon
                        ? new CombatAttributesV05(Strength: configuration.Growth, Agility: configuration.Growth,
                            Constitution: configuration.Growth, ProbeStabilityQ10: 20 * 1024) : null))
                : Build(BattleDefinitionBuilder.BuildThresholdAdrenalineBattleDefinition(units, true, walls, true, configuration.Tempo));
            var settings = new AiForecastSettings(360, true, true,
                new AiSamplingSettings(SignedDamage: true, HealthDefense: true),
                new AiShortPlanSettings(PressureValueQ10: 0), ValueScalarControl: configuration.ValueControl,
                RequireSubmissionPreflight: configuration.TacticalAi);
            var definition = FormalAiMatchup.Build(source, AiPolicyKind.SustainedOffense, AiPolicyKind.SustainedOffense,
                FormalAiLayout.Near, configuration.Mirror, settings);
            var encounter = definition.Encounters.Single();
            if (!configuration.TacticalAi)
            {
                encounter = encounter with
                {
                    Controllers = encounter.Controllers.Select(controller => controller with
                    {
                        SourceKind = CommandSourceKind.Player, AiPolicy = AiPolicyKind.Legacy,
                        AiExecution = AiExecutionKind.SingleCommand, AiForecast = null
                    }).ToArray()
                };
            }
            if (configuration.Size == 1) return WithEncounter(definition, encounter);
            var slots = new List<EncounterUnitSlot>();
            var bindings = new List<ControllerBinding>();
            var rows = configuration.Size == 2 ? new[] { -4, 4 } : new[] { -6, 0, 6 };
            for (int i = 0; i < rows.Length; i++)
            foreach (var original in encounter.Slots)
            {
                string suffix = i == 0 ? "" : "." + i;
                var id = new EncounterSlotId(original.SlotId.Value + suffix);
                slots.Add(original with
                {
                    SlotId = id,
                    InitialPosition = new GridPoint(original.InitialPosition.X, configuration.Mirror ? -rows[i] : rows[i])
                });
                var controller = encounter.Controllers.Single(binding => binding.ControlledSlots.Contains(original.SlotId));
                bindings.Add(controller with
                {
                    ControllerId = new ControllerId(controller.ControllerId.Value + suffix),
                    ControlledSlots = new[] { id }
                });
            }
            encounter = encounter with
            {
                Slots = slots.OrderBy(slot => slot.SlotId.Value, StringComparer.Ordinal).ToArray(),
                Controllers = bindings.OrderBy(binding => binding.ControllerId.Value, StringComparer.Ordinal).ToArray()
            };
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateEncounter(encounter, definition.FactionModel, errors);
            if (errors.HasErrors) throw new InvalidOperationException("Invalid scalar validation formation " + configuration.Id);
            return WithEncounter(definition, encounter);
        }

        private static BattleDefinition WithEncounter(BattleDefinition definition, EncounterDefinition encounter)
        {
            definition = definition with { Encounters = new[] { encounter } };
            return definition with
            {
                BattleDefinitionHashValue = BattleDefinitionHash.Compute(definition.RulesVersion, definition.TicksPerSecond,
                    LegacyIdMigrationManifest.LibraryVolumes.Count, definition.Rules, definition.ConcurrentAction, definition.ReactionRules,
                    definition.AdrenalineRules, definition.FactionModel, definition.DamageChannels, definition.ImpactProfiles, definition.Units,
                    definition.Actions, definition.AttackPatterns, definition.Volumes, definition.MovementPatterns, definition.ActionSets,
                    definition.StatusEffects, definition.Encounters, definition.DefaultDynamicSpawnPolicy)
            };
        }

        private static WeaponBattleProfile CreateWeapon(int radius)
        {
            var weapon = ScriptableObject.CreateInstance<WeaponBattleProfile>();
            weapon.GrowthAttribute = AttackGrowthAttribute.Strength;
            WeaponBattleProfile.Technique Technique(string id, PhysicalRecipe recipe, int strength, AttackControlEffects control)
                => new WeaponBattleProfile.Technique
                {
                    ActionId = "action." + id + ".radius_" + radius, Recipe = recipe,
                    BaseStrengthQ10 = strength, Controls = control
                };
            weapon.Techniques = new[]
            {
                Technique("quick_slash", PhysicalRecipe.Slash, 3072, AttackControlEffects.None),
                Technique("wide_cleave", PhysicalRecipe.Slash, 2304, AttackControlEffects.Interrupt),
                Technique("heavy_smash", PhysicalRecipe.Blunt, 6144, AttackControlEffects.All),
                Technique("spear_thrust", PhysicalRecipe.Pierce, 3072, AttackControlEffects.None)
            };
            return weapon;
        }

        private static BattleDefinition Build(BattleDefinitionBuildResult result)
        {
            if (!result.Succeeded) throw new InvalidOperationException(string.Join(", ", result.ErrorCodes()));
            return result.Definition;
        }

        private static string Simulate((Case Case, BattleDefinition Definition) job, EncounterDefinitionId encounter,
            BattleRuntimeInputs inputs, string output)
        {
            var composition = new ProductionBattleComposition(job.Definition, encounter, inputs);
            using var simulation = BattleSimulation.Create(job.Definition, encounter, inputs, composition.Assembly);
            composition.Attach(simulation);
            var controllerByUnit = composition.AiControllers.SelectMany(ai => ai.RegisteredControllerIds
                .SelectMany(id => simulation.DecisionSnapshotFor(new ControllerId(id)).ControlledUnitIds
                    .Select(unit => (Unit: unit, Ai: ai))))
                .ToDictionary(binding => binding.Unit, binding => binding.Ai);
            var recorder = job.Case.RecordReplay ? new ReplayRecorder(simulation) : null;
            var initial = simulation.CurrentSnapshot.Units.ToDictionary(u => u.UnitId);
            var windows = initial.Keys.ToDictionary(unit => unit, _ => new List<long>());
            var deaths = new Dictionary<long, long>();
            var damage = new List<(long Tick, long Attacker, long Target)>();
            int positiveContacts = 0, wallHits = 0, clash = 0, knockdown = 0, interruptions = 0, rejections = 0, crossWindow = 0;
            long wallDamage = 0;
            int windowLength = job.Definition.Encounters.Single().TurnSubmission.WindowDurationTicks;
            int cap = (job.Case.TacticalAi ? 2 : 4 * job.Case.Size) * windowLength;
            var closeByWindow = new Dictionary<long, long>();
            var owners = new Dictionary<long, long>();
            var crossing = new Dictionary<long, long>();
            var crossed = new HashSet<long>();
            var crossedAndHit = new HashSet<long>();
            var waits = new Dictionary<(string Reason, bool Attack, bool Move, bool Guard), int>();
            var rejectionReasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var controllers = job.Definition.Encounters.Single().Controllers.Select(binding => binding.ControllerId).ToArray();
            int illegalStarts = 0;
            for (int tick = 0; tick < cap && !simulation.IsEnded; tick++)
            {
                if (!job.Case.TacticalAi && tick > 0) SubmitFixedCommand(simulation, controllers, tick);
                recorder?.CaptureBeforeStep();
                var result = simulation.Step(tick, simulation.CommandIngress.FreezeTick(tick));
                recorder?.RecordCommittedStep(result);
                foreach (var fact in result.Events.Events)
                {
                    switch (fact)
                    {
                        case TurnWindowOpenedEvent opened:
                            long close = tick + opened.DurationTicks;
                            windows[opened.OwnerUnitId.Value].Add(close);
                            closeByWindow[opened.WindowId.Value] = close;
                            break;
                        case ActionPlanCreatedEvent created:
                            owners[created.ActionPlanId] = created.OwnerUnitId;
                            break;
                        case ActionPlanLockedEvent locked:
                            var plan = simulation.ScheduleAuthority.Registry.Find(new ActionPlanId(locked.ActionPlanId));
                            if (plan?.ActionType == ActionType.Attack && plan.SubmittedWindowId is WindowId windowId
                                && closeByWindow.TryGetValue(windowId.Value, out long closeTick))
                            {
                                if (plan.StartTick >= closeTick) illegalStarts++;
                                if (plan.EndTick > closeTick)
                                {
                                    crossWindow++;
                                    crossing[locked.ActionPlanId] = closeTick;
                                }
                            }
                            break;
                        case DamageChannelResolvedEvent hit when hit.AfterBlockDamageQ10 > 0:
                            positiveContacts++;
                            damage.Add((tick, hit.AttackerUnitId.Value, hit.TargetUnitId.Value));
                            if (crossing.TryGetValue(hit.AttackPlanId.Value, out long impactClose) && tick >= impactClose)
                                crossedAndHit.Add(hit.AttackPlanId.Value);
                            break;
                        case WallImpactDamageEvent wall when wall.AppliedDamageQ10 > 0:
                            wallHits++;
                            wallDamage += wall.AppliedDamageQ10;
                            if (owners.TryGetValue(wall.SourcePlanId.Value, out long attacker))
                                damage.Add((tick, attacker, wall.TargetUnitId.Value));
                            break;
                        case ClashParticipantResolvedEvent _:
                            clash++;
                            break;
                        case UnitStateChangedEvent state when state.ToState == UnitState.KnockedDown:
                            knockdown++;
                            break;
                        case ActionPlanTerminatedEvent terminal when terminal.Reason == (int)ActionTerminationReason.InterruptedByControl:
                            interruptions++;
                            break;
                        case UnitDiedEvent death:
                            deaths.Add(death.UnitId.Value, tick);
                            break;
                        case CommandRejectedEvent rejected:
                            rejections++;
                            rejectionReasons.TryGetValue(rejected.ReasonCode, out int count);
                            rejectionReasons[rejected.ReasonCode] = count + 1;
                            break;
                    }
                    if (fact is ActionPlanTerminatedEvent ended && crossing.TryGetValue(ended.ActionPlanId, out long endClose)
                        && tick > endClose) crossed.Add(ended.ActionPlanId);
                    if (fact is ActionPlanCompletedEvent completed && crossing.TryGetValue(completed.ActionPlanId, out long completeClose)
                        && tick > completeClose) crossed.Add(completed.ActionPlanId);
                }
                if (job.Case.TacticalAi && simulation.CurrentTurnWindow is { IsAcceptingSubmissions: true } current
                    && controllerByUnit.TryGetValue(current.OwnerUnitId, out var activeAi))
                {
                    var decision = activeAi.DecisionAt(tick);
                    if (decision != null && !decision.HasSelection)
                    {
                        var legal = decision.Evaluations.Where(evaluation => !evaluation.IsReaction
                            && evaluation.RejectionCode == null && evaluation.Score.HasValue).ToArray();
                        var reason = (decision.Behavior?.Ordinary?.Reason ?? decision.NoSelectionReason ?? "NO_SELECTION",
                            legal.Any(e => e.Type == ActionType.Attack), legal.Any(e => e.Type == ActionType.Move),
                            legal.Any(e => e.Type == ActionType.Guard));
                        waits.TryGetValue(reason, out int count);
                        waits[reason] = count + 1;
                    }
                }
            }
            foreach (var plan in simulation.CurrentSnapshot.Plans)
                if (crossing.TryGetValue(plan.ActionPlanId, out long closeTick) && simulation.Tick > closeTick)
                    crossed.Add(plan.ActionPlanId);
            if (job.Case.TacticalAi)
                File.WriteAllLines(Path.Combine(output, job.Case.Id + "-waits.csv"),
                    new[] { "reason,scoredAttack,scoredMove,scoredGuard,count" }.Concat(waits
                        .OrderBy(row => row.Key.Reason, StringComparer.Ordinal)
                        .Select(row => Csv(row.Key.Reason, row.Key.Attack, row.Key.Move, row.Key.Guard, row.Value))));
            if (rejectionReasons.Count > 0)
                File.WriteAllLines(Path.Combine(output, job.Case.Id + "-rejections.csv"),
                    new[] { "reason,count" }.Concat(rejectionReasons.Select(row => Csv(row.Key, row.Value))));
            var shared = deaths.Select(death => new
            {
                death.Value,
                Groups = damage.Where(hit => hit.Target == death.Key && hit.Tick <= death.Value
                    && initial[hit.Attacker].FactionId != initial[death.Key].FactionId)
                    .GroupBy(hit => initial[hit.Attacker].FactionId)
                    .Select(group => group.Select(hit => hit.Attacker).Distinct().ToArray())
                    .Where(group => group.Length > 1).ToArray()
            }).Where(death => death.Groups.Length > 0).ToArray();
            var units = simulation.CurrentSnapshot.Units.Select(unit =>
            {
                var opened = windows[unit.UnitId];
                long? deadline = opened.Count >= 2 ? opened[1] : null;
                int kills = shared.Count(death => opened.Count > 0 && death.Value < (deadline ?? cap)
                    && death.Groups.Any(group => group.Contains(unit.UnitId)));
                return (Unit: unit.UnitId, SecondWindowEnd: deadline,
                    Death: deaths.TryGetValue(unit.UnitId, out long tick) ? (long?)tick : null,
                    SharedKills: kills, DamageTakenQ10: initial[unit.UnitId].HealthQ10 - Math.Max(0, unit.HealthQ10));
            }).ToArray();
            if (recorder != null)
            {
                string path = Path.Combine(output, job.Case.Id + ".heroReplay");
                VerifyDiskReplay(job.Definition, recorder.BuildReplay(), path);
                File.WriteAllLines(Path.Combine(output, job.Case.Id + "-units.csv"),
                    new[] { "unit,secondWindowEnd,death,sharedKills,damageTakenQ10" }.Concat(units.Select(unit =>
                        Csv(unit.Unit, unit.SecondWindowEnd, unit.Death, unit.SharedKills, unit.DamageTakenQ10))));
            }
            return Csv(job.Case.Id, job.Case.Tempo, job.Case.Size, job.Case.Mirror, job.Case.Walls,
                job.Case.WallPermille, job.Case.Growth, job.Case.TacticalAi, job.Case.ValueControl, simulation.Tick + 1, simulation.IsEnded,
                units.Sum(u => u.DamageTakenQ10), positiveContacts, wallHits, wallDamage, clash, knockdown, interruptions,
                crossWindow, rejections, deaths.Count > 0 ? deaths.Values.Min() : (long?)null,
                deaths.Count(death => windows[death.Key].Count == 0), shared.Length, units.Sum(u => u.SharedKills),
                recorder != null, job.Definition.BattleDefinitionHashValue, crossed.Count, crossedAndHit.Count, illegalStarts);
        }

        private static void VerifyDiskReplay(BattleDefinition definition, BattleReplay replay, string path)
        {
            using (var file = File.Create(path)) ReplayFile.Save(file, replay);
            using var read = File.OpenRead(path);
            using var player = new ReplayPlayer(definition);
            player.Load(ReplayFile.Load(read, definition));
            player.Play();
            while (player.AdvanceOneTick()) { }
            if (!player.IsComplete || player.Deviation != null)
                throw new InvalidOperationException("Scalar replay deviation " + Path.GetFileName(path) + " " + player.Deviation);
        }

        private static void SubmitFixedCommand(BattleSimulation simulation, ControllerId[] controllers, int tick)
        {
            foreach (var controller in controllers)
            {
                var snapshot = simulation.DecisionSnapshotFor(controller);
                var units = snapshot.ControlledUnitIds;
                if (units.Count == 0) continue;
                var reactions = units.SelectMany(unit => simulation.AiReactionOpportunities.PublishedOpportunitiesFor(unit, snapshot.Tick))
                    .ToArray();
                if (reactions.Length == 0 && (snapshot.OwnWindow == null || tick % 6 != 0)) continue;
                var context = new AiDecisionContext(snapshot, controller, units, snapshot.EditablePlansOf(controller),
                    snapshot.OwnWindow, reactions, simulation.MovementPathCalculator, simulation.AiActionPlanLookup);
                int available = snapshot.AdrenalineLedgers.FirstOrDefault(ledger => ledger.UnitId == units[0].Value)
                    ?.AvailableAdrenaline ?? 0;
                var reaction = AiTacticalCandidateBuilder.Reactions(context)
                    .Where(candidate => candidate.Cost <= available)
                    .OrderBy(candidate => candidate.Type == ActionType.Block ? 0 : 1)
                    .FirstOrDefault(candidate =>
                    {
                        if (candidate.Type != ActionType.Dodge) return true;
                        var scope = (ReactionCommandScope)candidate.Request.Scope;
                        var payload = (ReactionCommandPayload)candidate.Request.Payload;
                        return simulation.HudPreviewFor(controller).PreviewDefense(scope.ReactionOpportunityId,
                            payload.ReactionActionSpecId, payload.StartTick, payload.DodgeDestination).RejectionCode == null;
                    });
                if (reaction != null && simulation.CommandIngress.FindEntry(controller).Submit(reaction.Request) == null)
                    return;
                if (!context.HasSubmissionWindow || tick >= context.OwnWindow.EndTick || tick % 6 != 0) continue;
                var owner = new UnitId(context.OwnWindow.OwnerUnitId);
                var actor = snapshot.VisibleUnits.First(unit => unit.UnitId == owner.Value);
                if (!actor.IsAlive || actor.StateValue == UnitState.KnockedDown || actor.StateValue == UnitState.Staggered) continue;
                var targets = snapshot.VisibleUnits.Where(unit => unit.IsAlive
                    && snapshot.Classify(owner, new UnitId(unit.UnitId)) == UnitRelation.Hostile).ToArray();
                if (targets.Length == 0) continue;
                int distance = Distance(actor.X, actor.Y);
                var candidates = AiTacticalCandidateBuilder.Ordinary(context).Where(item => item.Type != ActionType.Guard)
                    .Select(item =>
                    {
                        var edit = (ScheduleEditPayload)item.Request.Payload;
                        var operation = (AddOrdinaryPlanOperation)edit.Operations[0];
                        var spec = snapshot.FindAction(operation.ActionSpecId);
                        if (item.Type != ActionType.Attack)
                            return (Candidate: item, Edit: edit, Rank: distance - Distance(operation.Destination.Value.X, operation.Destination.Value.Y));
                        var attack = (AttackPayloadSpec)spec.Payload;
                        var area = VolumeFootprint.ResolveTriangles(attack.Pattern.Directions,
                            new GridPoint(actor.X, actor.Y), operation.Facing);
                        var target = new UnitId(operation.PrimaryTargetUnitId.Value.Value);
                        bool covered = area.Intersect(simulation.LogicGrid.TrianglesOf(target)).Any();
                        return (Candidate: item, Edit: edit,
                            Rank: covered ? 100 + ((AttackTimingSpec)spec.Timing).BaseWindupTicks : 0);
                    }).Where(item => item.Rank > 0).OrderByDescending(item => item.Rank);
                foreach (var item in candidates)
                {
                    var request = item.Candidate.Request;
                    var scope = (ScheduleEditScope)request.Scope;
                    var check = simulation.ScheduleEditor.Apply(item.Edit.Operations, tick, scope.ExpectedScheduleRevision,
                        simulation.ScheduleRevision, preview: true, expectedWindowId: scope.ExpectedWindowId, issuer: controller);
                    if (check.Succeeded && simulation.CommandIngress.FindEntry(controller).Submit(request) == null) return;
                }
                int Distance(int x, int y) => targets.Min(unit => Math.Abs(unit.X - x) + Math.Abs(unit.Y - y));
            }
        }

        private static string Csv(params object[] values)
            => string.Join(",", values.Select(value =>
            {
                string cell = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                return cell.Contains(",") || cell.Contains("\"") || cell.Contains("\n")
                    ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
            }));
    }
}
