using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    public interface IBattleFrameAdapter
    {
        string AdapterName { get; }

        string CallbackSite { get; }

        bool OwnsAutonomousUpdate { get; }

        MonoBehaviour GateTarget { get; }

        bool IsInitialized { get; }

        bool IsStopped { get; }

        int AdvanceCallCount { get; }

        int StopCallCount { get; }

        void Initialize(BattleRuntimeContext context);

        void AdvanceFrame(BattleFrameDelta delta);

        void StopBattle(string reason);

        void ResetForNewBattle();
    }
    public readonly struct BattleFrameDelta
    {
        public BattleFrameDelta(float deltaTime, bool isPaused, bool timeScaleIsolated)
        {
            DeltaTime = deltaTime;
            IsPaused = isPaused;
            TimeScaleIsolated = timeScaleIsolated;
        }

        public float DeltaTime { get; }

        public bool IsPaused { get; }

        public bool TimeScaleIsolated { get; }

        public override string ToString()
            => "dt=" + DeltaTime.ToString("F6") + " paused=" + IsPaused + " unscaled=" + TimeScaleIsolated;
    }
    public interface IBattleSimulationSource
    {
        string SourceName { get; }

        string LastConfigurationError { get; }

        BattleSimulationSeed BuildSeed();
    }
    public interface INewBattleAssemblySource
    {
        Logic.Simulation.BattleSimulationAssembly BuildNewAssembly(BattleSimulationSeed seed);
        void AttachNewSimulation(Logic.Simulation.BattleSimulation simulation);
    }
    public sealed class BattleSimulationSeed
    {
        public BattleSimulationSeed(
            Logic.Definitions.BattleDefinition definition,
            Logic.Ids.EncounterDefinitionId encounterId,
            Logic.Initialization.BattleRuntimeInputs runtimeInputs,
            string inputSummary)
        {
            Definition = definition;
            EncounterId = encounterId;
            RuntimeInputs = runtimeInputs;
            InputSummary = inputSummary ?? string.Empty;
        }

        public Logic.Definitions.BattleDefinition Definition { get; }

        public Logic.Ids.EncounterDefinitionId EncounterId { get; }

        public Logic.Initialization.BattleRuntimeInputs RuntimeInputs { get; }

        public string InputSummary { get; }

        public string BattleDefinitionHash => Definition != null ? Definition.BattleDefinitionHashValue : string.Empty;

        public string RulesVersion => Definition != null ? Definition.RulesVersion : string.Empty;

        public string Validate()
        {
            if (Definition == null) return "SIMULATION_SOURCE_MISSING_DEFINITION";
            if (RuntimeInputs == null) return "SIMULATION_SOURCE_MISSING_RUNTIME_INPUTS";
            return null;
        }
    }
    public class BattleRuntimeContext
    {
        public BattleRuntimeContext(IBattleSimulationSource simulationSource, IBattleRuntimeObserver observer = null)
        { SimulationSource = simulationSource; Observer = observer; }
        public IBattleSimulationSource SimulationSource { get; }
        public IBattleRuntimeObserver Observer { get; }
    }
    public interface IBattleRuntimeObserver
    {
        void OnDriverFrame(float deltaTime);
        void OnFrozenBatch(Logic.Commands.FrozenCommandBatch batch);
        void OnCommittedStep(Logic.Simulation.BattleSimulation simulation, long tick, Logic.Simulation.StepResult result);
        void OnSimulationStep(Logic.Simulation.StepStatus status);
    }
    public interface IBattleRuntimeHost
    {
        UnityBattleDriver NewDriver { get; }
        ProjectHero.UnityView.BattleClockController Clock { get; }
        bool IsPaused { get; }
        void SetPaused(bool paused);
    }
}
