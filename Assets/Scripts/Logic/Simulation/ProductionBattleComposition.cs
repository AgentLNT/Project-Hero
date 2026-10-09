using System;
using System.Collections.Generic;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>Production windows and AI use only validated definitions and this world's readonly facts.</summary>
    public sealed class ProductionBattleComposition : ITurnWindowSchedule, IAiRuntimeStateSource
    {
        private readonly EncounterDefinition _encounter;
        private readonly List<AiControllerLogic> _ai = new List<AiControllerLogic>();
        private readonly List<UnitId> _order = new List<UnitId>();
        private BattleSimulation _simulation;
        public BattleSimulationAssembly Assembly { get; }

        public ProductionBattleComposition(BattleDefinition definition, EncounterDefinitionId encounterId, BattleRuntimeInputs inputs)
        {
            _encounter = definition.FindEncounter(encounterId);
            if (_encounter?.TurnSubmission == null) throw new LogicDefinitionException("PRODUCTION_TURN_SUBMISSION_MISSING", encounterId.Value);
            var initial = BattleInitializer.BuildInitialState(definition, encounterId, inputs);
            var playerUnits = new HashSet<long>();
            foreach (var binding in _encounter.Controllers)
            {
                if (binding.SourceKind == CommandSourceKind.Player)
                    foreach (var id in initial.ControllerToUnitIds[binding.ControllerId]) playerUnits.Add(id.Value);
                if (binding.SourceKind != CommandSourceKind.Ai) continue;
                var ai = new AiControllerLogic(inputs.InitialRngSeed); ai.RegisterController(binding); _ai.Add(ai);
            }
            var speed = new Dictionary<long, float>();
            foreach (var slot in _encounter.Slots)
            {
                UnitId id = initial.SlotToUnitId[slot.SlotId]; _order.Add(id);
                speed.Add(id.Value, definition.FindUnit(slot.DefinitionId).ActionSpeed);
            }
            _order.Sort((a, b) =>
            {
                int rank = (playerUnits.Contains(a.Value) ? 0 : 1).CompareTo(playerUnits.Contains(b.Value) ? 0 : 1);
                if (rank != 0) return rank;
                int actionSpeed = speed[b.Value].CompareTo(speed[a.Value]);
                return actionSpeed != 0 ? actionSpeed : a.Value.CompareTo(b.Value);
            });
            _ai.Sort((a, b) => StringComparer.Ordinal.Compare(a.RegisteredControllerIds[0], b.RegisteredControllerIds[0]));
            var observers = new IDecisionObserver[_ai.Count];
            for (int i = 0; i < observers.Length; i++) observers[i] = _ai[i];
            Assembly = new BattleSimulationAssembly(turnWindowSchedule: this, decisionObservers: observers,
                aiRuntimeStates: this, concurrentHeroUnitId: initial.SlotToUnitId[_encounter.TurnSubmission.ConcurrentHeroSlot]);
        }

        public void Attach(BattleSimulation simulation)
        {
            if (_simulation != null) throw new InvalidOperationException("Composition is already attached");
            _simulation = simulation;
            foreach (var ai in _ai) ai.AttachPorts(simulation.AiReactionOpportunities, simulation.AiActionPlanLookup, simulation.MovementPathCalculator);
        }

        public WindowOpenRequest TryOpenDue(long tick)
        {
            if (_simulation == null || _simulation.IsEnded || _simulation.CurrentTurnWindow != null) return null;
            var manager = _simulation.WindowManager;
            if (tick <= manager.LastClosedAtTick) return null;
            // The latest closed window is already captured in the authoritative window ledger.
            // Advance from its actual owner; skipped dead slots must not cause repeated turns.
            int first = manager.ClosedWindows.Count == 0 ? 0
                : (_order.IndexOf(manager.ClosedWindows[manager.ClosedWindows.Count - 1].OwnerUnitId) + 1) % _order.Count;
            for (int i = 0; i < _order.Count; i++)
            {
                UnitId id = _order[(first + i) % _order.Count];
                var state = _simulation.FindUnitStateMachine(id);
                if (state != null && state.CurrentState != Units.UnitState.Dead)
                    return new WindowOpenRequest(id, _encounter.TurnSubmission.BudgetTicks);
            }
            return null;
        }
        public bool ShouldCloseCurrentWindow(long tick) => false;
        public IReadOnlyList<AiControllerRuntimeState> CaptureRuntimeStatesOrdered()
        {
            var states = new List<AiControllerRuntimeState>();
            foreach (var ai in _ai) states.AddRange(ai.CaptureRuntimeStatesOrdered());
            return states.AsReadOnly();
        }
        public static BattleSimulation Create(BattleDefinition definition, EncounterDefinitionId encounterId, BattleRuntimeInputs inputs)
        {
            var composition = new ProductionBattleComposition(definition, encounterId, inputs);
            var simulation = BattleSimulation.Create(definition, encounterId, inputs, composition.Assembly);
            composition.Attach(simulation); return simulation;
        }
    }
}
