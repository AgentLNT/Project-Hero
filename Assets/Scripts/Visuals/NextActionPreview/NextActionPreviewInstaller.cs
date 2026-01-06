using UnityEngine;
using UnityEngine.SceneManagement;
using ProjectHero.Core.Grid;

namespace ProjectHero.Visuals
{
    public sealed class NextActionPreviewInstaller : MonoBehaviour
    {
        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureInstalled();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureInstalled();
        }

        private static void EnsureInstalled()
        {
            if (Object.FindFirstObjectByType<NextActionPreviewSystem>() != null) return;

            var grid = GridManager.Instance;
            if (grid == null) grid = Object.FindFirstObjectByType<GridManager>();
            if (grid == null) return;

            var root = new GameObject("NextActionPreview");
            root.transform.SetParent(grid.transform, false);

            root.AddComponent<NextActionPreviewRenderer>();
            root.AddComponent<NextActionPreviewSystem>();
        }
    }
}
