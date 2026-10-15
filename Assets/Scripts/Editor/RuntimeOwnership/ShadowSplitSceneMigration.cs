using System;
using System.Linq;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectHero.Editor.RuntimeOwnership
{
    /// <summary>One-time, idempotent rewiring of the preserved migration scenes.</summary>
    public static class ShadowSplitSceneMigration
    {
        public const string HiddenScene = "Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity";
        public static string Migrate()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(HiddenScene) == null)
            {
                var error = AssetDatabase.MoveAsset("Assets/Scenes/HiddenRuntimeValidation.unity", HiddenScene);
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            }
            foreach (var path in new[] { Task11ProductionMigration.LegacyScene, HiddenScene })
            {
                var scene = EditorSceneManager.OpenScene(path);
                var root = scene.GetRootGameObjects();
                var old = root.SelectMany(g => g.GetComponentsInChildren<BattleRuntimeBootstrap>(true)).SingleOrDefault();
                MigrationBattleHost host;
                if (old != null)
                {
                    var go = old.gameObject;
                    var fields = new SerializedObject(old);
                    fields.FindProperty("m_Script").objectReferenceValue = AssetDatabase.LoadAssetAtPath<MonoScript>(
                        "Assets/Diagnostics/Editor/Migration/MigrationBattleHost.cs");
                    fields.ApplyModifiedPropertiesWithoutUndo();
                    host = go.GetComponent<MigrationBattleHost>();
                }
                else host = root.SelectMany(g => g.GetComponentsInChildren<MigrationBattleHost>(true)).Single();
                if (host == null) throw new InvalidOperationException("MIGRATION_HOST_REBIND_FAILED:" + path);
                var config = new SerializedObject(host);
                config.FindProperty("m_EditorClassIdentifier").stringValue = typeof(MigrationBattleHost).Assembly.GetName().Name
                    + "::" + typeof(MigrationBattleHost).FullName;
                config.FindProperty("_legacyFrameAdapterSlot").objectReferenceValue = host.GetComponent<ProjectHero.Demos.CombatDemo>();
                config.FindProperty("_simulationSourceSlot").objectReferenceValue = host.GetComponent<ProjectHero.Core.Compatibility.Authoring.BattleSimulationSourceFactory>();
                config.ApplyModifiedPropertiesWithoutUndo();
                var bridge = host.GetComponent<ProjectHero.Core.Compatibility.Authoring.ShadowSceneInputBridge>();
                if (bridge != null)
                {
                    var fields = new SerializedObject(bridge);
                    fields.FindProperty("_bootstrap").objectReferenceValue = host;
                    fields.ApplyModifiedPropertiesWithoutUndo();
                }
                var view = root.SelectMany(g => g.GetComponentsInChildren<ProjectHero.UnityView.BattlePresentationView>(true)).SingleOrDefault();
                if (view != null)
                {
                    var fields = new SerializedObject(view);
                    fields.FindProperty("_bootstrap").objectReferenceValue = host;
                    fields.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            var main = EditorSceneManager.OpenScene(RuntimeOwnershipSceneTool.MainScenePath);
            EditorSceneManager.MarkSceneDirty(main);
            EditorSceneManager.SaveScene(main);
            AssetDatabase.SaveAssets();
            return "Migration scenes rewired; main scene saved with the New-only bootstrap.";
        }
    }
}
