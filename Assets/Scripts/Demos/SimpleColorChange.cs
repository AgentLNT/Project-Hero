using UnityEngine;

namespace ProjectHero.Demos
{
    /// <summary>
    /// A simple helper script to change color of a 3D object.
    /// Attach this to any GameObject with a MeshRenderer (like a Cube or Capsule).
    /// </summary>
    public class SimpleColorChange : MonoBehaviour
    {
        [Tooltip("The color to apply to the object.")]
        public Color TargetColor = Color.green;

        void Start()
        {
            ChangeColor(TargetColor);
        }

        public void ChangeColor(Color newColor)
        {
            // 1. Get the MeshRenderer component
            // Unlike SpriteRenderer in 2D, 3D objects use a MeshRenderer
            var renderer = GetComponent<MeshRenderer>();
            
            if (renderer != null)
            {
                // 2. Access the material
                // .material returns a copy of the material unique to this object.
                // .sharedMaterial modifies the actual asset (affecting all objects using it).
                // Usually in code you want .material to change just this one instance.
                renderer.material.color = newColor;
            }
            else
            {
                Debug.LogWarning($"No MeshRenderer found on {gameObject.name}. Cannot change color.");
            }
        }
    }
}
