using ProjectHero.Logic.Events;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>Legacy owners hide their generated graphics during New and restore their prior visibility.</summary>
    public interface ILegacyVisualGate { void SetNewViewActive(bool active); }
    public static class LegacyVisualRegistry
    {
        private static readonly System.Collections.Generic.List<ILegacyVisualGate> Gates = new System.Collections.Generic.List<ILegacyVisualGate>();
        private static bool _newViewActive;
        public static void Register(ILegacyVisualGate gate)
        { if (gate != null && !Gates.Contains(gate)) { Gates.Add(gate); if (_newViewActive) gate.SetNewViewActive(true); } }
        public static void Unregister(ILegacyVisualGate gate) => Gates.Remove(gate);
        public static void SetNewViewActive(bool active)
        {
            _newViewActive = active;
            for (int i = Gates.Count - 1; i >= 0; i--)
            {
                if (Gates[i] is UnityEngine.Object obj && obj == null) { Gates.RemoveAt(i); continue; }
                Gates[i].SetNewViewActive(active);
            }
        }
    }
    public interface IBattleViewConsumer
    {
        void Bind(BattleInitializationResult initialization, LogicSnapshot snapshot);
        void Consume(EventBatch events, LogicSnapshot snapshot);
    }
    public interface IBattleInputConsumer
    {
        string PlayerControllerId { get; }
        void BindInput(Input.ViewInputPorts ports);
    }
    public interface IBattleViewLifetimeConsumer
    {
        void ReleaseVisuals();
    }
    public interface IBattleDiagnosticFrameConsumer
    {
        void AdvanceDiagnostics(double unscaledSeconds);
    }
}
