using System;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>Restores the old prototype visuals when the Editor host switches back from New.</summary>
    [Serializable]
    public sealed class MigrationPresentationGate
    {
        [SerializeField] private GameObject[] _canvases = Array.Empty<GameObject>();
        [SerializeField] private Behaviour[] _behaviours = Array.Empty<Behaviour>();
        private bool[] _canvasStates, _behaviourStates;
        public void Configure(GameObject[] canvases, Behaviour[] behaviours)
        { _canvases = canvases ?? Array.Empty<GameObject>(); _behaviours = behaviours ?? Array.Empty<Behaviour>(); }
        public void EnterNew()
        {
            _canvasStates = new bool[_canvases.Length]; _behaviourStates = new bool[_behaviours.Length];
            for (int i = 0; i < _canvases.Length; i++)
                if (_canvases[i] != null) { _canvasStates[i] = _canvases[i].activeSelf; _canvases[i].SetActive(false); }
            for (int i = 0; i < _behaviours.Length; i++)
                if (_behaviours[i] != null) { _behaviourStates[i] = _behaviours[i].enabled; _behaviours[i].enabled = false; }
            LegacyVisualRegistry.SetNewViewActive(true);
        }
        public void Restore()
        {
            if (_canvasStates != null)
                for (int i = 0; i < _canvases.Length; i++) if (_canvases[i] != null) _canvases[i].SetActive(_canvasStates[i]);
            if (_behaviourStates != null)
                for (int i = 0; i < _behaviours.Length; i++) if (_behaviours[i] != null) _behaviours[i].enabled = _behaviourStates[i];
            _canvasStates = null; _behaviourStates = null;
            LegacyVisualRegistry.SetNewViewActive(false);
        }
    }
}
