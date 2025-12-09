using UnityEngine;
using UnityEngine.UI;
using TMPro;
using ProjectHero.Core.Actions;

namespace ProjectHero.UI
{
    public class ActionButton : MonoBehaviour
    {
        public Button Button;
        public TextMeshProUGUI Label;
        
        private Action _action; // Null means "Move" or special command
        private System.Action<Action> _callback;

        public void Setup(string name, Action action, System.Action<Action> onClick)
        {
            if (Label != null) Label.text = name;
            _action = action;
            _callback = onClick;
            
            if (Button != null)
            {
                Button.onClick.RemoveAllListeners();
                Button.onClick.AddListener(() => _callback?.Invoke(_action));
            }
        }

        public void SetSelected(bool selected)
        {
            // Visual feedback (e.g. change color)
            if (Button != null)
                Button.image.color = selected ? Color.green : Color.white;
        }
    }
}