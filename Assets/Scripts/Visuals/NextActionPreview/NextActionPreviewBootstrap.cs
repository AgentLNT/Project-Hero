using UnityEngine;
using ProjectHero.Core.Grid;
using UnityEngine.SceneManagement;

namespace ProjectHero.Visuals
{
    public static class NextActionPreviewBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Object.FindFirstObjectByType<NextActionPreviewInstaller>() != null) return;

            var go = new GameObject("NextActionPreviewInstaller");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<NextActionPreviewInstaller>();
        }
    }
}
