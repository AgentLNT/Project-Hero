using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Editor;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;

static class TempoProbes
{
    const string Root = "优化任务/执行记录/双回合拆解与多人预演-2026-11-16/";
    sealed record Tempo(int Health, int ActionSpeed, int MoveSpeed);
    sealed record Scenario(Tempo Model, int TeamSize, FormalAiLayout Layout, bool Mirror, AiPolicyKind Policy,
        AiPolicyKind? Opponent = null);
    sealed record Strike(string Action, int Start, int Impact, int DamageQ10);
    sealed record Actor(long Id, long? Deadline, long? Death, int WindowsOpened, long BestSingleTargetDamageQ10, bool Lethal);

    public static int Main(string[] args)
    {
        var source = FormalAiSourceExport.Seed();
        if (args[0] == "hash")
        {
            Console.WriteLine(Hash(source.Definition));
            Console.WriteLine(Hash(source.Definition with { RulesVersion = "battle-def-v7-movement-reservations" }));
            return 0;
        }
        Write("admission-checks.json", VerifyLedger(source.Definition.AdrenalineRules));
        var ceilings = from unit in source.Definition.Units from size in new[] { 2, 4, 6 }
            from speed in new[] { (int)unit.ActionSpeed, 20 }
            select new { Unit = unit.UnitDefinitionId.Value, Count = size, Speed = speed,
                unit.InitialHealth, Best = Ceiling(source.Definition, unit, size, speed) };
        Write("damage-ceilings.json", ceilings.ToArray());

        string folder = args[0];
        var policies = new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense };
        var scouts = from health in new[] { 50, 60 } from action in new[] { 20, 24 } from move in new[] { 20, 30 }
            from policy in new[] { policies[0], policies[2] } from layout in Enum.GetValues<FormalAiLayout>()
            from mirror in new[] { false, true } select new Scenario(new Tempo(health, action, move), 1, layout, mirror, policy);
        var multi = from model in new[]{ new Tempo(80,10,10),new Tempo(60,10,10),new Tempo(80,20,20),
                new Tempo(60,20,20),new Tempo(50,20,20),new Tempo(40,20,20),new Tempo(40,10,10) }
            from size in new[]{2,3} from mirror in new[]{false,true}
            select new Scenario(model, size, FormalAiLayout.Near, mirror, policies[0]);
        var confirmed = from health in new[]{50,60} from variant in args.Skip(1)
            let speeds = variant.Split('/').Select(int.Parse).ToArray() from policy in policies from opponent in policies
            from layout in Enum.GetValues<FormalAiLayout>() from mirror in new[]{false,true}
            select new Scenario(new Tempo(health,speeds[0],speeds[1]),1,layout,mirror,policy,opponent);
        bool team = folder.StartsWith("multiplayer", StringComparison.Ordinal);
        bool trace = folder == "counterexamples";
        var repeatedFailures = from speed in new[]{ new Tempo(50,20,30), new Tempo(50,24,20) }
            from layout in new[]{FormalAiLayout.Near,FormalAiLayout.Far}
            from mirror in new[]{false,true} where layout != FormalAiLayout.Far || !mirror
            select new Scenario(speed, 1, layout, mirror, AiPolicyKind.CautiousDefense, AiPolicyKind.CautiousDefense);
        var cases = (trace ? repeatedFailures : team ? multi : folder == "confirmed" ? confirmed : scouts).ToArray();
        Directory.CreateDirectory(Root + folder);
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = team ? 6 : 10 }, scenario =>
        {
            var name = $"H{scenario.Model.Health}-A{scenario.Model.ActionSpeed}-M{scenario.Model.MoveSpeed}"
                + $"-{scenario.TeamSize}v{scenario.TeamSize}-{scenario.Layout}-{scenario.Policy}"
                + (scenario.Opponent.HasValue ? "-vs-" + scenario.Opponent : "") + $"-mirror{(scenario.Mirror ? 1 : 0)}";
            string file = folder + "/" + name + ".json";
            if (File.Exists(Root + file)) return;
            try
            {
                Write(file, Run(source, scenario, trace));
                Console.WriteLine("TEMPO_COMPLETE " + name);
            }
            catch (ProjectHero.Logic.LogicDefinitionException error)
            {
                Write(file, new { Scenario = scenario, Failed = true, Error = error.ToString() });
                Console.WriteLine("TEMPO_FAILED " + name + " " + error.Message);
            }
        });
        return 0;
    }

    static object VerifyLedger(AdrenalineRules rules)
    {
        var result = new List<object>();
        foreach (int balance in new[] { 1, 2, 3 })
        foreach (int cost in new[] { 2, 3 })
        {
            var ledger = new AdrenalineLedger(new UnitId(1), rules);
            ledger.InitializeFromBattleStart(balance, 1);
            var plan = new ActionPlanId(1);
            var error = ledger.TryReserveForReaction(plan, cost);
            var admitted = error == null;
            if (admitted != (balance >= cost)) throw new InvalidOperationException("Unexpected affordability");
            int? afterCancel = null, afterSuccess = null, afterNextCycle = null;
            if (admitted)
            {
                ledger.ReleaseForSourceThreatCancelled(plan);
                afterCancel = ledger.AvailableAdrenaline;
                if (afterCancel != balance) throw new InvalidOperationException("Same-cycle cancellation");
                if (ledger.TryReserveForReaction(plan, cost) != null) throw new InvalidOperationException("Restored affordability");
                ledger.ConsumeAtTrigger(plan);
                if (cost == 3) ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(new UnitId(1), 0, 0, 1, 0, 0));
                afterSuccess = ledger.AvailableAdrenaline;
                if (afterSuccess != balance - cost + (cost == 3 ? rules.SuccessfulBlockReward : 0))
                    throw new InvalidOperationException("Unexpected net successful cost");
                var crossCycle = new AdrenalineLedger(new UnitId(1), rules);
                crossCycle.InitializeFromBattleStart(balance, 1);
                if (crossCycle.TryReserveForReaction(plan, cost) != null) throw new InvalidOperationException("Missing reservation");
                crossCycle.BeginOwnWindow();
                crossCycle.ReleaseForSourceThreatCancelled(plan);
                afterNextCycle = crossCycle.AvailableAdrenaline;
                if (afterNextCycle != 0) throw new InvalidOperationException("Cross-cycle refund");
            }
            result.Add(new { Balance = balance, Cost = cost, Admitted = admitted, Error = error,
                AfterSameCycleCancel = afterCancel, AfterSuccess = afterSuccess, AfterCrossCycleCancel = afterNextCycle });
        }
        return result;
    }

    static IReadOnlyList<Strike> Ceiling(BattleDefinition d, UnitDefinition unit, int count, int speed)
    {
        int width = d.Encounters.Single().TurnSubmission.WindowDurationTicks;
        int close = (count + 1) * width;
        var actions = d.FindActionSet(unit.ActionSetId).ActionSpecIds.Select(d.FindAction).Where(a => a.Type == ActionType.Attack)
            .Select(a => (a.ActionSpecId, Timing: (AttackTimingSpec)a.Timing,
                Damage: ((AttackPayloadSpec)a.Payload).DamageComponents.Sum(c => MomentumQuantizer.QuantizeDamageQ10(c.RawAmount))))
            .ToArray();
        var best = new int[close + 1];
        var chosen = new Strike[close];
        var next = new int[close];
        for (int t = close - 1; t >= 0; t--)
        {
            best[t] = best[t + 1];
            next[t] = t + 1;
            if (!(t > 0 && t < width || t > count * width)) continue;
            foreach (var a in actions)
            {
                int impact = t + TickQuantization.ResolveWindupTicks(a.Timing.BaseWindupTicks, d.Rules.ReferenceActionSpeed, speed);
                if (impact >= close) continue;
                int freeAt = Math.Min(close, impact + a.Timing.RecoveryTicks);
                if (a.Damage + best[freeAt] <= best[t]) continue;
                best[t] = a.Damage + best[freeAt];
                chosen[t] = new Strike(a.ActionSpecId.Value, t, impact, a.Damage);
                next[t] = freeAt;
            }
        }
        var schedule = new List<Strike>();
        for (int t = 0; t < close; t = next[t])
            if (chosen[t] != null) schedule.Add(chosen[t]);
        return schedule;
    }

    static object Run(BattleSimulationSeed source, Scenario scenario, bool trace)
    {
        var d = FormalAiMatchup.Build(source.Definition, scenario.Policy, scenario.Opponent ?? scenario.Policy, scenario.Layout,
            scenario.Mirror, new AiForecastSettings(360, true, true,
                new AiSamplingSettings(SignedDamage: true, HealthDefense: true),
                new AiShortPlanSettings(PressureValueQ10: 0)));
        var encounter = d.Encounters.Single();
        var slots = encounter.Slots.ToList();
        var controllers = encounter.Controllers.ToList();
        if (scenario.TeamSize > 1)
        {
            int[] rows = scenario.TeamSize == 2 ? new[] { -6, 6 } : new[] { -10, 0, 10 };
            slots.Clear();
            for (int lane = 0; lane < scenario.TeamSize; lane++)
            {
                foreach (var old in encounter.Slots)
                {
                    var id = lane == 0 ? old.SlotId : new EncounterSlotId(old.SlotId.Value + "." + lane);
                    var pos = new GridPoint(old.InitialPosition.X, scenario.Mirror ? -rows[lane] : rows[lane]);
                    slots.Add(old with { SlotId = id, InitialPosition = pos });
                    if (lane == 0) continue;
                    var binding = encounter.Controllers.Single(c => c.ControlledSlots.Contains(old.SlotId));
                    controllers.Add(binding with { ControllerId = new ControllerId(binding.ControllerId.Value + "." + lane),
                        ControlledSlots = new[] { id } });
                }
            }
            encounter = encounter with { Slots = slots.OrderBy(s => s.SlotId.Value, StringComparer.Ordinal).ToArray(),
                Controllers = controllers.OrderBy(c => c.ControllerId.Value, StringComparer.Ordinal).ToArray() };
        }
        d = d with
        {
            RulesVersion = "battle-def-v7-movement-reservations",
            Encounters = new[] { encounter },
            Units = d.Units.Select(u => u with { InitialHealth = scenario.Model.Health, ActionSpeed = scenario.Model.ActionSpeed,
                MoveSpeed = scenario.Model.MoveSpeed }).ToArray(),
            Actions = d.Actions.Select(a => a.Type switch
            {
                ActionType.Block => a with { AdrenalineCost = 3 },
                ActionType.Dodge => a with { AdrenalineCost = 2 },
                _ => a
            }).ToArray()
        };
        d = d with { BattleDefinitionHashValue = Hash(d) };
        using var sim = ProductionBattleComposition.Create(d, source.EncounterId, source.RuntimeInputs);
        int cap = 4 * scenario.TeamSize * encounter.TurnSubmission.WindowDurationTicks;
        var windows = sim.CurrentSnapshot.Units.ToDictionary(u => u.UnitId, _ => new List<long>());
        var deaths = new Dictionary<long, long>();
        var damage = new List<DamageChannelResolvedEvent>();
        var facts = new List<string>();
        int rejections = 0;
        for (long tick = 0; tick < cap && !sim.IsEnded; tick++)
        {
            IReadOnlyList<LogicEvent> events;
            try
            {
                events = sim.Step(tick, sim.CommandIngress.FreezeTick(tick)).Events.Events;
            }
            catch (ProjectHero.Logic.LogicDefinitionException error)
            {
                throw new ProjectHero.Logic.LogicDefinitionException(error.ErrorCode,
                    $"Tick {tick}: {error.Message}" + Environment.NewLine + error);
            }
            foreach (var e in events)
            {
                if (trace && (e is TurnWindowOpenedEvent || e is ActionPlanCreatedEvent || e is ActionPlanLockedEvent
                    || e is ActionPlanTerminatedEvent || e is DamageChannelResolvedEvent || e is ReactionTriggeredEvent
                    || e is DodgeResolvedEvent || e is BlockResolvedEvent))
                    facts.Add(ProjectHero.Logic.Replay.ReplayEventComparison.Canonical(e));
                if (e is TurnWindowOpenedEvent opened) windows[opened.OwnerUnitId.Value].Add(tick + opened.DurationTicks);
                if (e is UnitDiedEvent death) deaths.Add(death.UnitId.Value, tick);
                if (e is DamageChannelResolvedEvent hit && hit.AfterBlockDamageQ10 > 0) damage.Add(hit);
                if (e is CommandRejectedEvent || e is CommandIngressRejectedEvent) rejections++;
            }
        }
        var remaining = windows.Keys.ToDictionary(id => id, _ => scenario.Model.Health * 1024L);
        var applied = damage.Select(hit =>
        {
            long target = hit.TargetUnitId.Value;
            long amount = Math.Min(remaining[target], hit.AfterBlockDamageQ10);
            remaining[target] -= amount;
            return (hit.Tick, Attacker: hit.AttackerUnitId.Value, Target: target, Amount: amount);
        }).ToArray();
        var actors = windows.Select(p =>
        {
            long? deadline = p.Value.Count >= 2 ? p.Value[1] : null;
            long best = applied.Where(hit => hit.Attacker == p.Key && hit.Tick < (deadline ?? cap))
                .GroupBy(hit => hit.Target).Select(hits => hits.Sum(hit => hit.Amount)).DefaultIfEmpty(0).Max();
            return new Actor(p.Key, deadline, deaths.TryGetValue(p.Key, out long died) ? died : null,
                p.Value.Count, best, best >= scenario.Model.Health * 1024);
        }).ToArray();
        return new { Scenario = scenario, DefinitionHash = d.BattleDefinitionHashValue, Cap = cap, Ticks = sim.Tick + 1,
            Ended = sim.IsEnded, FirstDeath = deaths.Count == 0 ? (long?)null : deaths.Values.Min(),
            DeathsBeforeAnyWindow = actors.Count(u => u.Death.HasValue && u.WindowsOpened == 0), Rejections = rejections,
            Actors = actors, Events = trace ? facts : null };
    }

    static string Hash(BattleDefinition d)
        => BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, LegacyIdMigrationManifest.LibraryVolumes.Count,
            d.Rules, d.ConcurrentAction, d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles,
            d.Units, d.Actions, d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters,
            d.DefaultDynamicSpawnPolicy);

    static void Write(string path, object value) => File.WriteAllText(Root + path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
