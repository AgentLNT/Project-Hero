using System;
using System.IO;
using System.Linq;
using System.Text;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.UnityView;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectHero.Editor.RuntimeOwnership
{
    public static class Task11ProductionMigration
    {
        public const string LegacyScene = "Assets/Diagnostics/Editor/LegacyComparisonScene.unity";
        public static void PrepareDiagnosticCopy()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Diagnostics")) AssetDatabase.CreateFolder("Assets", "Diagnostics");
            if (!AssetDatabase.IsValidFolder("Assets/Diagnostics/Editor")) AssetDatabase.CreateFolder("Assets/Diagnostics", "Editor");
            if (!File.Exists(LegacyScene) && !AssetDatabase.CopyAsset(RuntimeOwnershipSceneTool.MainScenePath, LegacyScene))
                throw new InvalidOperationException("LEGACY_DIAGNOSTIC_COPY_FAILED");
            AssetDatabase.SaveAssets();
        }
        public static void Migrate()
        {
            if (!File.Exists("优化任务/执行记录/11-migration-shadow-accepted.txt"))
                throw new InvalidOperationException("VERIFIED_MIGRATION_SHADOW_REQUIRED");
            PrepareDiagnosticCopy();
            var scene = EditorSceneManager.OpenScene(RuntimeOwnershipSceneTool.MainScenePath, OpenSceneMode.Single);
            var bootstrap = Find<BattleRuntimeBootstrap>(scene);
            var old = Find<BattleSimulationSourceFactory>(scene);
            var source = Find<NewBattleSimulationSource>(scene);
            if (source == null)
            {
                if (old == null) throw new InvalidOperationException("MIGRATION_OLD_SOURCE_MISSING");
                var original = old.BuildSeed();
                Task11MigrationEvidence.Load("task11-shadow-confirmed-future-attack", original.RulesVersion, original.BattleDefinitionHash);
                var oldFields = new SerializedObject(old);
                var hero = (ProjectHero.Core.Entities.CombatUnit)oldFields.FindProperty("_heroUnit").objectReferenceValue;
                var enemy = (ProjectHero.Core.Entities.CombatUnit)oldFields.FindProperty("_enemyUnit").objectReferenceValue;
                source = bootstrap.gameObject.AddComponent<NewBattleSimulationSource>();
                source.Configure(LegacyCombatUnitStatsReader.Read(hero), LegacyCombatUnitStatsReader.Read(enemy),
                    original.RuntimeInputs.InitialRngSeed, original.RuntimeInputs.InitialMetaResource);
                if (source.BuildSeed()?.BattleDefinitionHash != original.BattleDefinitionHash)
                    throw new InvalidOperationException("MIGRATION_DEFINITION_CHANGED");
            }
            var presentation = Find<BattlePresentationView>(scene);
            var fields = new SerializedObject(presentation);
            var oldCanvases = fields.FindProperty("_legacyCanvases");
            var canvases = Enumerable.Range(0, oldCanvases.arraySize)
                .Select(i => oldCanvases.GetArrayElementAtIndex(i).objectReferenceValue as GameObject).Where(g => g != null).ToArray();
            oldCanvases.ClearArray(); fields.FindProperty("_legacyPresentation").ClearArray(); fields.ApplyModifiedPropertiesWithoutUndo();
            foreach (var canvas in canvases) UnityEngine.Object.DestroyImmediate(canvas);
            var config = new SerializedObject(bootstrap);
            config.FindProperty("_requestedMode").enumValueIndex = (int)BattleRuntimeMode.New;
            config.FindProperty("_productionNewOnly").boolValue = true;
            config.FindProperty("_legacyFrameAdapterSlot").objectReferenceValue = null;
            config.FindProperty("_simulationSourceSlot").objectReferenceValue = source;
            config.ApplyModifiedPropertiesWithoutUndo();
            var retired = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MonoBehaviour>(true))
                .Where(m => m != null && m.GetType().Assembly == typeof(ProjectHero.Demos.CombatDemo).Assembly).ToArray();
            var pending = retired.ToList();
            while (pending.Count > 0)
            {
                var removable = pending.FirstOrDefault(candidate => !pending.Any(other => other != candidate
                    && other.gameObject == candidate.gameObject && other.GetType().GetCustomAttributes(typeof(RequireComponent), true)
                        .Cast<RequireComponent>().Any(r => Requires(r.m_Type0, candidate.GetType())
                            || Requires(r.m_Type1, candidate.GetType()) || Requires(r.m_Type2, candidate.GetType()))));
                if (removable == null) throw new InvalidOperationException("LEGACY_COMPONENT_DEPENDENCY_CYCLE");
                UnityEngine.Object.DestroyImmediate(removable); pending.Remove(removable);
            }
            AssertProductionScene(scene);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            File.WriteAllText("优化任务/执行记录/11-production-migration.txt", "mode=New\nlegacyAdapter=removed\nlegacyComponentsRemoved="
                + retired.Length + "\nsource=" + source.GetType().FullName + "\ndefinition=" + source.BuildSeed().BattleDefinitionHash
                + "\nperformance=deferred\nLegacy code is Editor-only diagnostic support, excluded from Player compilation.\n", new UTF8Encoding(false));
        }
        public static void Audit()
        {
            var report = new StringBuilder("Production migration reference audit; performance deferred\n");
            foreach (var item in EditorBuildSettings.scenes.Where(s => s.enabled))
            {
                var scene = EditorSceneManager.OpenScene(item.path, OpenSceneMode.Single); AssertProductionScene(scene);
                report.AppendLine(item.path + "|mode=New|bootstrap=1|LegacyComponents=0|MissingScript=0");
                var dependencyGuids = AssetDatabase.GetDependencies(item.path, true);
                foreach (var path in dependencyGuids.Where(p => p.EndsWith(".prefab", StringComparison.Ordinal)))
                {
                    var root = PrefabUtility.LoadPrefabContents(path);
                    try { AssertObject(root); report.AppendLine(path + "|LegacyComponents=0|MissingScript=0"); }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
            }
            File.WriteAllText("优化任务/执行记录/11-production-reference-audit.txt", report.ToString(), new UTF8Encoding(false));
        }
        public static void RetireDiagnosticPrefabs()
        {
            const string directory = "Assets/Diagnostics/Editor/Prefabs";
            if (!AssetDatabase.IsValidFolder(directory)) AssetDatabase.CreateFolder("Assets/Diagnostics/Editor", "Prefabs");
            const string original = "Assets/Prefab/UnitStatusHUD.prefab";
            if (File.Exists(original))
            {
                string guid = AssetDatabase.AssetPathToGUID(original);
                string error = AssetDatabase.MoveAsset(original, directory + "/UnitStatusHUD.prefab");
                if (!string.IsNullOrEmpty(error) || AssetDatabase.AssetPathToGUID(directory + "/UnitStatusHUD.prefab") != guid)
                    throw new InvalidOperationException("DIAGNOSTIC_PREFAB_MIGRATION_FAILED|" + error);
            }
            AssetDatabase.SaveAssets(); Audit();
        }
        private static void AssertProductionScene(Scene scene)
        {
            var bootstraps = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<BattleRuntimeBootstrap>(true)).ToArray();
            if (bootstraps.Length != 1) throw new InvalidOperationException("PRODUCTION_BOOTSTRAP_COUNT");
            var config = new SerializedObject(bootstraps[0]);
            if (config.FindProperty("_requestedMode").enumValueIndex != (int)BattleRuntimeMode.New
                || !config.FindProperty("_productionNewOnly").boolValue
                || config.FindProperty("_legacyFrameAdapterSlot").objectReferenceValue != null
                || !(config.FindProperty("_simulationSourceSlot").objectReferenceValue is NewBattleSimulationSource))
                throw new InvalidOperationException("PRODUCTION_MIGRATION_SLOTS_INVALID");
            foreach (var root in scene.GetRootGameObjects()) AssertObject(root);
        }
        private static void AssertObject(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) != 0)
                    throw new InvalidOperationException("PRODUCTION_MISSING_SCRIPT|" + t.name);
                foreach (var script in t.GetComponents<MonoBehaviour>())
                    if (script != null && script.GetType().Assembly == typeof(ProjectHero.Demos.CombatDemo).Assembly)
                        throw new InvalidOperationException("PRODUCTION_LEGACY_REFERENCE|" + script.GetType().FullName);
            }
        }
        private static T Find<T>(Scene scene) where T : Component
            => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).SingleOrDefault();
        private static bool Requires(Type required, Type candidate) => required != null && required.IsAssignableFrom(candidate);
    }
}
