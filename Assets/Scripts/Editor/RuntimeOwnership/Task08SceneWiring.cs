using System;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Editor.RuntimeOwnership
{
    public static class Task08SceneWiring
    {
        // Reuse both existing scenes, preserving their mode, authored units, and unrelated objects.
        public static void Wire()
        {
            foreach (string path in new[] { RuntimeOwnershipSceneTool.MainScenePath, RuntimeOwnershipSceneTool.HiddenValidationScenePath })
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                BattleRuntimeBootstrap bootstrap = null;
                BattleSimulationSourceFactory source = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var candidate in root.GetComponentsInChildren<BattleRuntimeBootstrap>(true))
                    {
                        if (bootstrap != null) throw new InvalidOperationException("duplicate bootstrap: " + path);
                        bootstrap = candidate;
                    }
                    var factory = root.GetComponentInChildren<BattleSimulationSourceFactory>(true);
                    if (factory != null) source = factory;
                }
                if (bootstrap == null || source == null) throw new InvalidOperationException("missing explicit scene source: " + path);
                WireBridge(bootstrap, source);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("TASK08_SCENE_WIRED|" + path + "|mode preserved");
            }
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void WireBridge(BattleRuntimeBootstrap bootstrap, BattleSimulationSourceFactory source)
        {
            var bridge = bootstrap.GetComponent<ShadowSceneInputBridge>() ?? bootstrap.gameObject.AddComponent<ShadowSceneInputBridge>();
            var binding = new SerializedObject(bridge);
            binding.FindProperty("_bootstrap").objectReferenceValue = bootstrap;
            binding.FindProperty("_source").objectReferenceValue = source;
            binding.ApplyModifiedPropertiesWithoutUndo();
            var configuration = new SerializedObject(source);
            configuration.FindProperty("_shadowInitialWindowBudgetTicks").intValue = 600;
            configuration.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
