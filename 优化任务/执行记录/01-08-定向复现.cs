// Read-only audit runner. Compile against the existing Logic and Logic.Tests assemblies.
// Fixture reflection only creates scenarios; all commands and ticks use production entry points.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Units;

public static class Task0108AuditProbe
{
    private static readonly Type Fixture = typeof(ProjectHero.Logic.Tests.Task09ReactionCommandTests)
        .Assembly.GetType("ProjectHero.Logic.Tests.Task09A2Fixture", true);
    private static readonly UnitId Hero = new UnitId(1);
    private static readonly UnitId Monster = new UnitId(2);

    private static object Call(string name, params object[] args)
    {
        MethodInfo method = Fixture.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length);
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static BattleSimulation New(BattleDefinition definition = null, BattleSimulationAssembly assembly = null)
        => (BattleSimulation)Call("NewSim", definition, assembly, null);

    private static StepResult Step(BattleSimulation sim, long tick)
        => sim.Step(tick, sim.CommandIngress.FreezeTick(tick));

    private static ActionPlan AddBlock(BattleSimulation sim, out ReactionOpportunityRuntime opportunity)
    {
        Call("ArrangeStartedAttack", sim, Monster);
        Console.WriteLine("ATTACK_START ownerState=" + sim.FindUnitStateMachine(Hero).CurrentState);
        opportunity = (ReactionOpportunityRuntime)Call("OpenOpportunityFor", sim, Monster);
        var request = new CommandRequest(2, new ReactionCommandScope(opportunity.Id),
            new ReactionCommandPayload(ReactionCommandKind.Block, new ActionSpecId("action.t09a2.block")));
        var rejected = sim.CommandIngress.FindEntry(new ControllerId("controller.enemy_ai")).Submit(request);
        if (rejected != null) throw new Exception("fixture ingress rejected: " + rejected.ReasonCode);
        Step(sim, 2);
        return sim.ScheduleAuthority.Registry.ActivePlans.Single(p => p.IsReaction);
    }

    private sealed class StateInput : IUnitStateAdvanceSystem
    {
        public bool Kill;
        public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(IReadOnlyList<UnitSnapshot> units, long tick)
        {
            if (Kill && tick == 1) return new[] { new UnitStateAdvanceRequest(Monster.Value, 0) };
            if (!Kill && tick == 9) return new[] { new UnitStateAdvanceRequest(Monster.Value, null,
                StateTransitionSpec.Timed(UnitState.Staggered, 30, UnitState.Idle)) };
            return Array.Empty<UnitStateAdvanceRequest>();
        }
    }

    public static int Main()
    {
        try
        {
            using (BattleSimulation sim = New())
            {
                ActionPlan reaction = AddBlock(sim, out ReactionOpportunityRuntime opportunity);
                for (long t = 3; t <= 14; t++)
                {
                    Step(sim, t);
                    if (t == 10 || t == 11 || t == 12 || t == 14)
                    {
                        Console.WriteLine("BLOCK tick=" + t + " start=" + reaction.StartTick + " trigger="
                            + reaction.TriggerTick + " end=" + reaction.EndTick + " plan=" + reaction.State
                            + " opportunity=" + opportunity.State + " reservations="
                            + sim.AdrenalineLedgerOf(Monster).ReservationCount + " blockResults="
                            + sim.StagedResolution.BlockPlans.Count);
                    }
                }
            }
            using (BattleSimulation sim = New(assembly: new BattleSimulationAssembly(unitStateAdvance: new StateInput())))
            {
                ActionPlan reaction = AddBlock(sim, out ReactionOpportunityRuntime opportunity);
                for (long t = 3; t <= 11; t++) Step(sim, t);
                Console.WriteLine("CONTROL tick=11 defenderState=" + sim.FindUnitStateMachine(Monster).CurrentState
                    + " reactionPlan=" + reaction.State + " termination=" + reaction.TerminationReason
                    + " blockResults=" + sim.StagedResolution.BlockPlans.Count);
            }
            using (BattleSimulation sim = New())
            {
                Call("ArrangeStartedAttackOnly", sim, Monster);
                for (long t = 2; t <= 11; t++) Step(sim, t);
                var aggregate = sim.StagedResolution.Aggregations.Single(a => a.TargetUnitId == Monster);
                Console.WriteLine("IMPACT tick=11 stagger=" + aggregate.IsStaggered + " knockdown="
                    + aggregate.IsKnockedDown + " actualState=" + sim.FindUnitStateMachine(Monster).CurrentState);
            }
            BattleDefinition withExtraEnemy = (BattleDefinition)Call("BuildDefinition",
                CommandSourceKind.Player, CommandSourceKind.Ai, true);
            using (BattleSimulation sim = New(withExtraEnemy,
                new BattleSimulationAssembly(unitStateAdvance: new StateInput { Kill = true })))
            {
                sim.WindowManager.ScheduleWindow(1, Monster, 400);
                Step(sim, 0);
                var request = (CommandRequest)Call("AddPlan", 1L, 0L, (WindowId?)new WindowId(1),
                    Monster, new ActionSpecId("action.t09a2.attack"), (UnitId?)Hero, (long?)1L, 1L);
                Call("Submit", sim, new ControllerId("controller.enemy_ai"), request);
                StepResult result = Step(sim, 1);
                bool opened = result.Events.Events.Any(e => e.GetType().Name == "TurnWindowOpenedEvent");
                Console.WriteLine("PRECOMMAND_DEATH tick=1 monsterAlive="
                    + result.Snapshot.Units.Single(u => u.UnitId == Monster.Value).IsAlive
                    + " openedDeadOwnersWindow=" + opened + " battleEnded=" + sim.BattleEnd.IsEnded
                    + " deadOwnersActivePlans=" + sim.ScheduleAuthority.Registry.ActivePlans.Count(p => p.OwnerUnitId == Monster)
                    + " rejectedCommands=" + ((IReadOnlyList<string>)Call("AllRejectionCodes", result)).Count);
                Step(sim, 2);
                Console.WriteLine("DEATH_CLEANUP tick=2 deadOwnersActivePlans="
                    + sim.ScheduleAuthority.Registry.ActivePlans.Count(p => p.OwnerUnitId == Monster));
            }
            using (BattleSimulation sim = New())
            {
                sim.WindowManager.ScheduleWindow(0, Hero, 400);
                Step(sim, 0);
                var op = new AddOrdinaryPlanOperation(1, Hero, new ActionSpecId("action.t09a2.attack"), 1,
                    default(ActionPlanId), Monster, GridDirection.West, null);
                var request = new CommandRequest(1, new ScheduleEditScope(0, sim.CurrentTurnWindow.WindowId),
                    new ScheduleEditPayload(new ScheduleEditOperation[] { op }));
                Call("Submit", sim, new ControllerId("controller.player"), request);
                Step(sim, 1);
                ActionPlan attack = sim.ScheduleAuthority.Registry.ActivePlans.Single();
                for (long t = 2; t <= 11; t++) Step(sim, t);
                var intent = sim.IntentQueue.Intents.Single();
                Console.WriteLine("FACING tick=11 requestedPlanFacing=" + attack.Facing + " intentFacing="
                    + intent.Facing + " momentumFacing=" + intent.Momentum.Direction);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE_ERROR " + ex);
            return 2;
        }
    }
}
