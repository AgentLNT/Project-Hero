using UnityEngine;
using ProjectHero.Core.Grid;

namespace ProjectHero.Visuals
{
    public static class NextActionPreviewBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
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
