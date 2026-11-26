using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;

internal static class RecipeProbe
{
    const string Output = "优化任务/执行记录/撞墙转换系数对照-2026-11-19";
    public static int MomentumPercent => 100;
    public static int WallPercent { get; private set; } = 100;
    public static int ScaleMomentum(int value) => value;
    public static long ScaleWallDamage(long remaining, int unitsPerPoint)
        => TickQuantization.RoundHalfUpDivide(checked(remaining * 1024 * WallPercent), checked(unitsPerPoint * 100));
    const int One = 1024;
    const int AiLimit = 720;
    static readonly PhysicalRecipe[] Recipes = Enum.GetValues<PhysicalRecipe>();
    static readonly int[] Strengths = { 1, 2, 3, 4, 5, 6, 8 };
    static readonly AiPolicyKind[] Policies = { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense };
    static readonly BattleDefinition Template = CombatExperimentDefinition.Build(new CombatExperimentScenario("recipe-probe"));
    static ActionSpecId Action(string name) => CombatExperimentDefinition.Action(name);
    static ActionSpecId Attack(PhysicalRecipe recipe) => Action("probe_" + recipe.ToString().ToLowerInvariant());
    record Profile(string Id, int Hp, int A, int M, int R);
    static readonly Profile[] Profiles = {
        new("Native", 24, 20, 20, 10), new("A20M30-R20", 50, 20, 30, 20), new("A24M20-R20", 50, 24, 20, 20),
        new("A20M30-R25", 50, 20, 30, 25), new("A24M20-R25", 50, 24, 20, 25)
    };
    public static bool IsHp50Probe(UnitDefinition d) => (d.UnitDefinitionId.Value.StartsWith("unit.recipe_probe.", StringComparison.Ordinal)
        || d.UnitDefinitionId.Value.StartsWith("unit.hero.radius_", StringComparison.Ordinal)
        || d.UnitDefinitionId.Value.StartsWith("unit.monster.radius_", StringComparison.Ordinal))
        && d.InitialHealth == 50 && ((d.ActionSpeed == 20 && d.MoveSpeed == 30) || (d.ActionSpeed == 24 && d.MoveSpeed == 20));

    static void Main(string[] args)
    {
        WallPercent = int.Parse(args[^1], CultureInfo.InvariantCulture);
        Require(WallPercent is 50 or 60 or 100, "Expected wall percent 50/60/100");
        args = args[..^1];
        Directory.CreateDirectory(Path.Combine(Output, "replays"));
        if (args.Length == 1 && args[0] == "fixed")
        {
            RunFixed(false);
            RunFixed(true);
        }
        else if (args.Length == 3 && args[0] == "ai" && (args[2] == "open" || args[2] == "wall"))
        {
            FullMoves.Run(int.Parse(args[1], CultureInfo.InvariantCulture), args[2] == "wall", WallPercent);
        }
        else throw new ArgumentException("Expected fixed <percent> or ai <strength> <open|wall> <percent>");
    }

    // One ordinary attack per unit. Keep the fixture's geometry, timing, tags and defenses identical.
    static BattleDefinition Define(Profile profile, int strength, PhysicalRecipe left, PhysicalRecipe right,
        int gap, bool bothWalls, bool clash, AiPolicyKind? policy, bool mirror)
    {
        var shared = Template.Actions.Where(a => a.Type != ActionType.Attack).ToArray();
        var leftId = clash ? Action("aligned_" + left.ToString().ToLowerInvariant()) : Attack(left);
        var rightId = Attack(right);
        var attacks = Recipes.Select(recipe =>
        {
            var attack = Template.FindAction(Attack(recipe));
            var payload = (AttackPayloadSpec)attack.Payload;
            return attack with { Payload = payload with { Strength = payload.Strength with { BaseStrengthQ10 = strength * One } } };
        }).Where(a => a.ActionSpecId == Attack(left) || a.ActionSpecId == rightId).ToList();
        if (clash)
        {
            var attack = attacks.Single(a => a.ActionSpecId == Attack(left));
            if (Attack(left) != rightId) attacks.Remove(attack);
            attacks.Add(attack with { ActionSpecId = leftId, Timing = new AttackTimingSpec(90, 30) });
        }
        var sets = new[] { leftId, rightId }.Select((id, n) => new ActionSetDefinition(
            new ActionSetId("action_set.recipe_probe." + (n + 1)), shared.Select(a => a.ActionSpecId).Append(id)
                .OrderBy(a => a.Value, StringComparer.Ordinal).ToArray())).ToArray();
        var units = sets.Select((set, n) => CombatValuesV05.Unit(new UnitDefinitionId("unit.recipe_probe." + (n + 1)),
            new CombatAttributesV05(ProbeStabilityQ10: profile.R * One), set.ActionSetId, Template.Volumes[0].VolumeSpecId) with
            { InitialHealth = profile.Hp, ActionSpeed = profile.A, MoveSpeed = profile.M }).ToArray();
        var encounter = Template.Encounters[0];
        var slots = encounter.Slots.Select((slot, n) => slot with {
            DefinitionId = units[n].UnitDefinitionId,
            InitialPosition = mirror ? new GridPoint(6 - slot.InitialPosition.X, -slot.InitialPosition.Y) : slot.InitialPosition,
            InitialFacing = ((n == 0) != mirror) ? GridDirection.East : GridDirection.West
        }).ToArray();
        var walls = new SortedSet<(int X, int Y)>();
        if (gap >= 0)
        {
            foreach (var slot in bothWalls ? slots : new[]{slots[1]})
            {
                bool atRight = slot.InitialPosition.X == 6;
                var cells = VolumeFootprint.ResolveCells(Template.Volumes[0].Directions, slot.InitialPosition, slot.InitialFacing);
                int edge = atRight ? cells.Max(p => p.X) : cells.Min(p => p.X);
                foreach (int y in Enumerable.Range(-19, 39).Where(y => ((edge + y) & 1) == 0))
                    walls.Add((edge + (atRight ? 2 : -2) * (gap + 1), y));
            }
        }
        var forecast = new AiForecastSettings(360, true, true,
            new AiSamplingSettings(SignedDamage: true, HealthDefense: true), new AiShortPlanSettings(PressureValueQ10: 0));
        encounter = encounter with {
            Slots = slots, StaticWalls = walls.Select(p => new GridPoint(p.X, p.Y)).ToArray(),
            Controllers = policy == null ? encounter.Controllers : encounter.Controllers.Select(c => c with {
                SourceKind = CommandSourceKind.Ai, AiPolicy = policy.Value, AiExecution = AiExecutionKind.UtilityBehaviorTree,
                AiForecast = forecast }).ToArray()
        };
        var d = Template with {
            Rules = BattleRules.WalkingDisplacementV2, Units = units,
            Actions = shared.Concat(attacks).OrderBy(x => x.ActionSpecId.Value, StringComparer.Ordinal).ToArray(),
            ActionSets = sets, Encounters = new[]{encounter}
        };
        Require(CombatValuesV05.ValidateDefinition(d) == null, "definition validation");
        return d with { BattleDefinitionHashValue = CombatExperimentDefinition.Hash(d) };
    }

    static void Require(bool accepted, string message)
    {
        if (!accepted) throw new InvalidOperationException(message);
    }

    static BattleSimulation Create(BattleDefinition d)
        => ProductionBattleComposition.Create(d, d.Encounters[0].EncounterId, new BattleRuntimeInputs(31, 4));

    static void Submit(BattleSimulation sim, int owner, ActionSpecId action, int start)
    {
        var binding = sim.Definition.Encounters[0].Controllers[owner - 1];
        var ctx = AiDecisionContextFactory.Build(sim.DecisionSnapshotFor(binding.ControllerId), binding.ControllerId,
            sim.AiReactionOpportunities, sim.AiActionPlanLookup, sim.MovementPathCalculator);
        var op = new AddOrdinaryPlanOperation(1, new UnitId(owner), action, start, PrimaryTargetUnitId: new UnitId(3 - owner),
            Facing: owner == 1 ? GridDirection.East : GridDirection.West);
        var rejection = sim.CommandIngress.FindEntry(binding.ControllerId).Submit(new CommandRequest(ctx.NextTick,
            new ScheduleEditScope(ctx.Snapshot.ScheduleRevision, ctx.ExpectedWindowId), new ScheduleEditPayload(new[] {op})));
        Require(rejection == null, "fixed ingress " + rejection?.ReasonCode);
    }

    static IReadOnlyList<LogicEvent> Step(BattleSimulation sim, ReplayRecorder recorder, int tick)
    {
        recorder.CaptureBeforeStep();
        var result = sim.Step(tick, sim.CommandIngress.FreezeTick(tick));
        recorder.RecordCommittedStep(result);
        var events = result.Events.Events;
        Require(!events.Any(e => e is CommandRejectedEvent || e is CommandIngressRejectedEvent),
            $"command rejected at {tick}: " + string.Join("; ", events.Where(e => e is CommandRejectedEvent || e is CommandIngressRejectedEvent)));
        return events;
    }

    static void Verify(BattleDefinition d, ReplayRecorder recorder, string save)
    {
        using var stream = new MemoryStream();
        ReplayFile.Save(stream, recorder.BuildReplay()); stream.Position = 0;
        var replay = ReplayFile.Load(stream, d);
        using var player = new ReplayPlayer(d);
        player.Load(replay); player.Play();
        while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
        Require(player.IsComplete && player.Deviation == null, "replay diverged: " + player.Deviation);
        if (save != null) File.WriteAllBytes(Path.Combine(Output, "replays", save + ".heroReplay"), stream.ToArray());
    }

    static long SingleDamage(PhysicalRecipe recipe, int q) => CombatValuesV05.DamageCoefficient(recipe) * q * One;
    static int SingleMomentum(PhysicalRecipe recipe, int q) => ScaleMomentum(CombatValuesV05.MomentumCoefficient(recipe) * q * One);
    record Effect(long Direct, long Momentum, bool Stagger, bool Knockdown, int Steps, long WallRaw, long WallApplied, long Hp, long? WallDelay);

    static Effect CheckEffect(Profile p, int gap, int unit, long direct, int m, int impact, List<LogicEvent> events, BattleSimulation sim)
    {
        var hit = events.OfType<DamageChannelResolvedEvent>().Where(e => e.TargetUnitId.Value == unit).ToArray();
        Require(hit.Sum(e => e.AfterBlockDamageQ10) == direct && hit.Sum(e => e.AfterMomentumResistanceUnits) == m,
            $"contact unit {unit}, got {hit.Sum(e => e.AfterBlockDamageQ10)}/{hit.Sum(e => e.AfterMomentumResistanceUnits)}, expected {direct}/{m}");
        var agg = events.OfType<TargetAggregateResolvedEvent>().SingleOrDefault(e => e.TargetUnitId.Value == unit && e.Tick == impact);
        int r = p.R * One;
        bool stagger = m >= r;
        bool knockdown = m >= r * 3 / 2;
        Require((agg?.IsStaggered ?? false) == stagger && (agg?.IsKnockedDown ?? false) == knockdown, $"control {unit}");
        int attempts = direct < p.Hp * One ? m / r : 0;
        bool collides = gap >= 0 && attempts > gap;
        int steps = events.OfType<ForcedDisplacementResolvedEvent>().Where(e => e.TargetUnitId.Value == unit).Sum(e => e.AppliedSteps);
        var wall = events.OfType<WallImpactDamageEvent>().SingleOrDefault(e => e.TargetUnitId.Value == unit);
        long raw = collides ? ScaleWallDamage(m - gap * r, 1024) : 0;
        long actual = Math.Min(raw, Math.Max(0, p.Hp * One - direct));
        long expectedHp = Math.Max(0, p.Hp * One - direct - actual);
        long hp = Math.Max(0, sim.CurrentSnapshot.Units.Single(x => x.UnitId == unit).HealthQ10);
        int baseTicks = TickQuantization.ResolveMoveBaseStepTicks(12, 20, p.M);
        Require(steps == (collides ? gap : attempts) && (wall?.RawDamageQ10 ?? 0) == raw && (wall?.AppliedDamageQ10 ?? 0) == actual
            && hp == expectedHp && (!collides || wall.Tick - impact == baseTicks * (gap + 1)),
            $"movement unit{unit}, expected steps {(collides ? gap : attempts)} and wall {raw}/{actual}; got {steps}, {wall?.RawDamageQ10}/{wall?.AppliedDamageQ10}, hp={hp} expected hp={expectedHp}");
        return new Effect(direct, m, stagger, knockdown, steps, raw, actual, hp, wall?.Tick - impact);
    }

    static void RunFixed(bool clash)
    {
        string name = clash ? "clash" : "fixed";
        using var output = File.CreateText(Path.Combine(Output, name + "-W" + WallPercent + ".jsonl"));
        int n = 0;
        foreach (var p in Profiles)
        foreach (int q in Strengths)
        foreach (var left in Recipes)
        foreach (var right in clash ? Recipes : new[]{left})
        foreach (int gap in clash ? new[]{-1, 0} : new[]{-1, 0, 1})
        foreach (bool guard in clash ? new[]{false} : new[]{false, true})
        {
            string id = $"{name}-W{WallPercent}-{p.Id}-Q{q}-{left}-{right}-gap{gap}-guard{guard}";
            try
            {
                var d = Define(p, q, left, right, gap, clash, clash, null, false);
                using var sim = Create(d);
                var recorder = new ReplayRecorder(sim);
                var events = new List<LogicEvent>();
                int shortWindup = TickQuantization.ResolveWindupTicks(30, 20, p.A);
                int impact = (clash ? 182 : 160) + shortWindup;
                for (int tick = 0; tick <= impact + 96 && !sim.IsEnded; tick++)
                {
                    if (tick == 1)
                        Submit(sim, 1, clash ? Action("aligned_" + left.ToString().ToLowerInvariant()) : Attack(left),
                            clash ? impact - TickQuantization.ResolveWindupTicks(90, 20, p.A) : 160);
                    if (tick == 182 && clash) Submit(sim, 2, Attack(right), 182);
                    if (tick == 182 && guard) Submit(sim, 2, Action("guard"), impact - 1);
                    events.AddRange(Step(sim, recorder, tick));
                }
                int mLeft = SingleMomentum(left, q), mRight = SingleMomentum(right, q);
                long dLeft = SingleDamage(left, q), dRight = SingleDamage(right, q);
                Effect onLeft = null;
                Effect onRight;
                if (!clash)
                    onRight = CheckEffect(p, gap, 2, guard ? dLeft / 2 : dLeft, guard ? (mLeft + 1) / 2 : mLeft, impact, events, sim);
                else
                {
                    int residualLeft = Math.Max(0, mLeft - mRight), residualRight = Math.Max(0, mRight - mLeft);
                    var participants = events.OfType<ClashParticipantResolvedEvent>().OrderBy(e => e.OwnerUnitId.Value).ToArray();
                    Require(participants.Length == 2 && participants[0].Tick == impact && participants[1].Tick == impact
                        && participants[0].OriginalMomentumUnits == mLeft && participants[1].OriginalMomentumUnits == mRight
                        && participants[0].RemainingMomentumUnits == residualLeft && participants[1].RemainingMomentumUnits == residualRight,
                        "clash participants did not meet at the same impact");
                    onLeft = CheckEffect(p, gap, 1, dRight * residualRight / mRight, residualRight, impact, events, sim);
                    onRight = CheckEffect(p, gap, 2, dLeft * residualLeft / mLeft, residualLeft, impact, events, sim);
                }
                bool save = (p.Id == "Native" && q == 3 || p.Id == "A20M30-R20" && q == 6)
                    && gap == 0 && !guard && left == PhysicalRecipe.Blunt && (!clash || right == PhysicalRecipe.Slash);
                Verify(d, recorder, save ? id : null);
                output.WriteLine(JsonSerializer.Serialize(new { Id = id, Percent = WallPercent, Profile = p, Q = q, Left = left.ToString(),
                    Right = clash ? right.ToString() : null, Gap = gap, Guard = guard, OnLeft = onLeft,
                    OnRight = onRight, Impact = impact, ReplayVerified = true, Hash = d.BattleDefinitionHashValue }));
                n++;
            }
            catch (Exception error) { throw new InvalidOperationException(id, error); }
        }
        Console.WriteLine($"{name}: {n}");
    }

    record Loss(int Tick, long Source, long Amount, bool Wall);

    static void RunAi(int q, bool wall)
    {
        string scene = wall ? "wall" : "open";
        using var output = File.CreateText(Path.Combine(Output, $"ai-Q{q}-{scene}.jsonl"));
        int n = 0;
        foreach (var p in Profiles.Where(p => p.Hp == 50 && p.R == 20))
        foreach (var left in Recipes)
        foreach (var right in Recipes)
        foreach (var policy in Policies)
        foreach (bool mirror in new[]{false, true})
        {
            string id = $"ai-Q{q}-{scene}-{p.Id}-{left}-{right}-{policy}-mirror{mirror}";
            try
            {
                var d = Define(p, q, left, right, wall ? 0 : -1, true, false, policy, mirror);
                using var sim = Create(d);
                var recorder = new ReplayRecorder(sim);
                var owners = new Dictionary<long,long>();
                var windows = new Dictionary<long,List<long>> { [1] = new(), [2] = new() };
                var remaining = windows.Keys.ToDictionary(unit => unit, _ => (long)p.Hp * One);
                var losses = new List<Loss>();
                var deaths = new Dictionary<long,int>();
                int walls = 0, clashes = 0, blocks = 0, dodges = 0;
                for (int tick = 0; tick < AiLimit && !sim.IsEnded; tick++)
                {
                    foreach (var e in Step(sim, recorder, tick))
                    {
                        if (e is ActionPlanCreatedEvent plan) owners[plan.ActionPlanId] = plan.OwnerUnitId;
                        if (e is TurnWindowOpenedEvent win) windows[win.OwnerUnitId.Value].Add(tick + win.DurationTicks);
                        if (e is UnitDiedEvent die) deaths[die.UnitId.Value] = tick;
                        if (e is ClashParticipantResolvedEvent) clashes++;
                        if (e is ReactionPlanResolvedEvent r && r.RewardsSuccess) {
                            if (r.Kind is ReactionResolutionKind.BlockedFully or ReactionResolutionKind.BlockedPartially) blocks++;
                            if (r.Kind == ReactionResolutionKind.DodgeAvoidedContact) dodges++;
                        }
                        if (e is DamageChannelResolvedEvent hit && hit.AfterBlockDamageQ10 > 0)
                        {
                            long target = hit.TargetUnitId.Value;
                            long applied = Math.Min(remaining[target], hit.AfterBlockDamageQ10);
                            remaining[target] -= applied;
                            losses.Add(new Loss(tick, hit.AttackerUnitId.Value, applied, false));
                        }
                        if (e is WallImpactDamageEvent impact)
                        {
                            walls++;
                            remaining[impact.TargetUnitId.Value] -= impact.AppliedDamageQ10;
                            losses.Add(new Loss(tick, owners[impact.SourcePlanId.Value], impact.AppliedDamageQ10, true));
                        }
                    }
                }
                foreach (var unit in sim.CurrentSnapshot.Units)
                    Require(Math.Max(0, unit.HealthQ10) == remaining[unit.UnitId], "health ledger inconsistent");
                var actors = windows.Select(pair => new { Unit = pair.Key, Windows = pair.Value.Count,
                    Deadline = pair.Value.Count >= 2 ? (long?)pair.Value[1] : null,
                    DamageByDeadlineQ10 = losses.Where(l => l.Source == pair.Key && l.Tick < (pair.Value.Count >= 2 ? pair.Value[1] : AiLimit)).Sum(l => l.Amount),
                    TotalQ10 = losses.Where(l => l.Source == pair.Key).Sum(l => l.Amount),
                    WallQ10 = losses.Where(l => l.Source == pair.Key && l.Wall).Sum(l => l.Amount),
                    RemainingQ10 = remaining[pair.Key],
                    DeathTick = deaths.TryGetValue(pair.Key, out int death) ? (int?)death : null }).ToArray();
                int early = actors.Count(a => a.DeathTick != null && a.Windows == 0);
                bool pass = sim.IsEnded && actors.Any(a => a.DamageByDeadlineQ10 == p.Hp * One);
                bool replay = !mirror && (left == right || left == PhysicalRecipe.Pierce && right == PhysicalRecipe.Blunt)
                    && policy == AiPolicyKind.SustainedOffense;
                if (replay) Verify(d, recorder, id);
                output.WriteLine(JsonSerializer.Serialize(new { Id = id, Profile = p, Q = q, Scene = scene, Left = left.ToString(),
                    Right = right.ToString(), Policy = policy.ToString(), Mirror = mirror, Ended = sim.IsEnded, Tick = sim.Tick,
                    Pass = pass, EarlyDeaths = early, Walls = walls, ClashParticipants = clashes, Blocks = blocks, Dodges = dodges,
                    Actors = actors, ReplayVerified = replay, Hash = d.BattleDefinitionHashValue }));
                output.Flush(); n++;
                if (n % 18 == 0) Console.WriteLine($"Q{q} {scene}: {n}");
            }
            catch (Exception error) { throw new InvalidOperationException(id, error); }
        }
    }
}
