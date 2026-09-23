using UnityEngine;
using System.Collections.Generic;
using ProjectHero.Core.Physics;
using ProjectHero.Core.Combat;

namespace ProjectHero.Core.Actions
{
    [CreateAssetMenu(fileName = "NewActionLibrary", menuName = "ProjectHero/Actions/ActionLibrary")]
    public class ActionLibrarySO : ScriptableObject
    {
        [System.Serializable]
        public class ActionEntry
        {
            public string ID; // Unique ID for lookup (e.g. "HeavyCharge_L")
            public Action Data;
        }

        public List<ActionEntry> Actions = new List<ActionEntry>();

        public Action GetAction(string id)
        {
            var entry = Actions.Find(a => a.ID == id);
            if (entry != null) return entry.Data;
            
            Debug.LogWarning($"Action '{id}' not found in library {name}");
            return null;
        }
    }
}
