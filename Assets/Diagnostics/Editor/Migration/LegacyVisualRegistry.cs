using UnityEngine;
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
}
