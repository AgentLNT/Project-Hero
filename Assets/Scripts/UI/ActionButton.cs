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
        private Color _baseColor = Color.white;

        public void Setup(string name, Action action, System.Action<Action> onClick, Color? baseColor = null)
        {
            if (Label != null) Label.text = name;
            _action = action;
            _callback = onClick;

            _baseColor = baseColor ?? Color.white;
            if (Button != null && Button.image != null)
            {
                Button.image.color = _baseColor;
            }
            
            if (Button != null)
            {
                Button.onClick.RemoveAllListeners();
                Button.onClick.AddListener(() => _callback?.Invoke(_action));
            }
        }

        public void SetSelected(bool selected)
        {
            if (Button == null || Button.image == null) return;

            // Mild highlight without changing the hue.
            if (selected)
            {
                var c = _baseColor;
                Button.image.color = new Color(
                    Mathf.Clamp01(c.r * 1.15f),
                    Mathf.Clamp01(c.g * 1.15f),
                    Mathf.Clamp01(c.b * 1.15f),
                    c.a);
            }
            else
            {
                Button.image.color = _baseColor;
            }
        }
    }
}