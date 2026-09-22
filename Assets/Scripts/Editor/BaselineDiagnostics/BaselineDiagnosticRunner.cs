// ============================================================================
// BaselineDiagnosticRunner — 任务 01 只读诊断工具（基线、清单与不变量测试）
// ----------------------------------------------------------------------------
// 变更日志:
//   2026-XX-XX v1  首次创建。严格只读：仅使用 AssetDatabase 只读 API 与
//                  System.IO 读取，不调用 SaveAssets / SetDirty / 创建或修改
//                  任何资产、场景与 .meta。唯一副作用是写出诊断文本文件。
//
// 入口:
//   - 人工: 菜单 Tools/Baseline/01 Run Read-Only Asset Diagnostic
//   - CI/批处理: -executeMethod ProjectHeroBaseline.BaselineDiagnosticRunner.RunAll
//
// 输出: C:\Users\1\repos\Project Hero\Project-Hero\优化任务\执行记录\01-诊断输出.txt
// ============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ProjectHeroBaseline
{
    public static class BaselineDiagnosticRunner
    {
        private const string OutputPath = @"C:\Users\1\repos\Project Hero\Project-Hero\优化任务\执行记录\01-诊断输出.txt";
        private const string ScenePath = "Assets/Scenes/CombatSampleScene.unity";
        private const string PrefabDir = "Assets/Prefab";

        private static readonly StringBuilder Sb = new StringBuilder();
        private static int _sectionErrors;
        private static int _totalErrors;

        [MenuItem("Tools/Baseline/01 Run Read-Only Asset Diagnostic")]
        public static void RunAll()
        {
            Sb.Clear();
            _totalErrors = 0;

            Sb.AppendLine("====================================================================================");
            Sb.AppendLine(" Project Hero 任务 01 — 只读资产/场景诊断输出 (BaselineDiagnosticRunner)");
            Sb.AppendLine($" 生成时间 (本地): {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            Sb.AppendLine($" Unity 版本: {Application.unityVersion}");
            Sb.AppendLine($" 项目路径: {Directory.GetParent(Application.dataPath)?.FullName}");
            Sb.AppendLine(" 说明: 本工具只读，不修改任何资产/场景/.meta；不调用 SaveAssets。");
            Sb.AppendLine("====================================================================================");
            Sb.AppendLine();

            RunSection("S01 环境与程序集", SectionEnvironment);
            RunSection("S02 配置资产总清单 (Resources 下 ScriptableObject)", SectionAssetInventory);
            RunSection("S03 ActionLibrarySO 全字段 dump", SectionActionLibraries);
            RunSection("S04 AttackPattern 全字段 dump", SectionAttackPatterns);
            RunSection("S05 UnitVolume 全字段 dump", SectionUnitVolumes);
            RunSection("S06 跨资产同名局部 ID 一致性 (QuickSlash 等)", SectionCrossAssetLocalIds);
            RunSection("S07 主战斗场景消费关系 (CombatSampleScene)", SectionSceneConsumption);
            RunSection("S08 Prefab 引用扫描", SectionPrefabScan);
            RunSection("S09 脚本程序集与 MonoScript GUID 报告", SectionScriptAssemblyReport);
            RunSection("S10 缺失稳定 ID 的源码证据扫描", SectionStableIdEvidence);

            Sb.AppendLine();
            Sb.AppendLine("====================================================================================");
            Sb.AppendLine($" 诊断结束。总错误数: {_totalErrors}");
            Sb.AppendLine("====================================================================================");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? "");
                File.WriteAllText(OutputPath, Sb.ToString(), new UTF8Encoding(true));
                Debug.Log($"[BaselineDiagnostic] 输出已写入: {OutputPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BaselineDiagnostic] 写输出文件失败: {ex.Message}");
                _totalErrors++;
            }

            Debug.Log($"[BaselineDiagnostic] RUN_COMPLETE totalErrors={_totalErrors} sections={Sb.Length} chars");
        }

        private static void RunSection(string title, Action body)
        {
            _sectionErrors = 0;
            Sb.AppendLine();
            Sb.AppendLine($"########## {title} ##########");
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _sectionErrors++;
                _totalErrors++;
                Sb.AppendLine($"  !! SECTION ERROR: {ex.GetType().Name}: {ex.Message}");
                Sb.AppendLine($"     {ex.StackTrace?.Replace("\n", "\n     ")}");
                Debug.LogError($"[BaselineDiagnostic] {title} 失败: {ex}");
            }
            Sb.AppendLine($"---------- {title} 小节错误数: {_sectionErrors} ----------");
        }

        // ------------------------------------------------------------------
        // S01 环境与程序集
        // ------------------------------------------------------------------
        private static void SectionEnvironment()
        {
            Sb.AppendLine($" 编辑器模式: batchmode/Editor ({Application.isBatchMode})");
            Sb.AppendLine($" 平台: {Application.platform}");
            Sb.AppendLine($" 公司/产品名: {Application.companyName} / {Application.productName}");

            string assetsRoot = Application.dataPath;
            var asmdefs = Directory.GetFiles(assetsRoot, "*.asmdef", SearchOption.AllDirectories);
            var asmrefs = Directory.GetFiles(assetsRoot, "*.asmref", SearchOption.AllDirectories);
            Sb.AppendLine($" 自定义 .asmdef 文件数: {asmdefs.Length} (预期 0)");
            foreach (var f in asmdefs) Sb.AppendLine($"   asmdef: {MakeRel(f)}");
            Sb.AppendLine($" 自定义 .asmref 文件数: {asmrefs.Length} (预期 0)");
            foreach (var f in asmrefs) Sb.AppendLine($"   asmref: {MakeRel(f)}");

            var csFiles = Directory.GetFiles(assetsRoot, "*.cs", SearchOption.AllDirectories);
            int editorCs = 0;
            foreach (var f in csFiles)
            {
                string rel = MakeRel(f).Replace('\\', '/');
                if (rel.Contains("/Editor/") || rel.StartsWith("Editor/")) editorCs++;
            }
            Sb.AppendLine($" 工程内 .cs 文件总数: {csFiles.Length} (含 TextMesh Pro 示例)");
            Sb.AppendLine($" 其中 Editor 目录脚本数 (→ Assembly-CSharp-Editor): {editorCs}");
            Sb.AppendLine(" 结论: 无自定义 asmdef → 全部运行时代码编译进 Assembly-CSharp，Editor 目录脚本编译进 Assembly-CSharp-Editor。");
        }

        // ------------------------------------------------------------------
        // S02 资产清单
        // ------------------------------------------------------------------
        private static void SectionAssetInventory()
        {
            DumpAssetsByType<ProjectHero.Core.Actions.ActionLibrarySO>("t:ActionLibrarySO", "ActionLibrarySO");
            DumpAssetsByType<ProjectHero.Core.Combat.AttackPattern>("t:AttackPattern", "AttackPattern");
            DumpAssetsByType<ProjectHero.Core.Grid.UnitVolume>("t:UnitVolume", "UnitVolume");

            // 兜底：Resources 下其余 ScriptableObject（若上面的类型过滤漏掉任何东西）
            var others = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets/Resources" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (so == null) continue;
                var t = so.GetType();
                if (t == typeof(ProjectHero.Core.Actions.ActionLibrarySO) ||
                    t == typeof(ProjectHero.Core.Combat.AttackPattern) ||
                    t == typeof(ProjectHero.Core.Grid.UnitVolume)) continue;
                others.Add($"  其他 SO: {t.FullName} \"{so.name}\" @ {path} [guid {AssetDatabase.AssetPathToGUID(path)}]");
            }
            Sb.AppendLine($" Resources 下其余 ScriptableObject: {others.Count}");
            foreach (var line in others) Sb.AppendLine(line);
        }

        private static void DumpAssetsByType<T>(string filter, string label) where T : UnityEngine.Object
        {
            var guids = AssetDatabase.FindAssets(filter);
            Sb.AppendLine($" {label} 资产数: {guids.Length}");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                string metaFileId = ReadMetaMainObjectFileID(path);
                string yamlAnchor = ReadYamlAnchorFileId(path);
                Sb.AppendLine($"   {label} \"{(asset != null ? asset.name : "<加载失败>")}\" @ {path}");
                Sb.AppendLine($"       guid={guid}  meta.mainObjectFileID={metaFileId}  yamlAnchor={yamlAnchor}  type={asset?.GetType().FullName ?? "null"}");
                if (asset == null) _sectionErrors++;
            }
        }

        // ------------------------------------------------------------------
        // S03/S04/S05 全字段 dump
        // ------------------------------------------------------------------
        private static void SectionActionLibraries()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:ActionLibrarySO"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var lib = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Actions.ActionLibrarySO>(path);
                if (lib == null) { Sb.AppendLine($"  !! 加载失败: {path}"); _sectionErrors++; continue; }

                Sb.AppendLine($" ==== ActionLibrarySO \"{lib.name}\" @ {path} [guid {guid}] ====");
                var so = new SerializedObject(lib);
                var actions = so.FindProperty("Actions");
                if (actions != null && actions.isArray)
                {
                    Sb.AppendLine($" Actions.Count = {actions.arraySize}");
                    for (int i = 0; i < actions.arraySize; i++)
                    {
                        var entry = actions.GetArrayElementAtIndex(i);
                        var idProp = entry.FindPropertyRelative("ID");
                        var dataProp = entry.FindPropertyRelative("Data");
                        Sb.AppendLine($" Actions[{i}].ID = {(idProp != null ? idProp.stringValue : "<missing>")}");
                        DumpGenericChildren(dataProp, "     Data");
                    }
                }
                else
                {
                    Sb.AppendLine("  Actions 属性缺失（异常，应至少存在数组）");
                    _sectionErrors++;
                }
            }
        }

        private static void SectionAttackPatterns()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:AttackPattern"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var pattern = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Combat.AttackPattern>(path);
                if (pattern == null) { Sb.AppendLine($"  !! 加载失败: {path}"); _sectionErrors++; continue; }

                Sb.AppendLine($" ==== AttackPattern \"{pattern.name}\" @ {path} [guid {guid}] ====");
                var so = new SerializedObject(pattern);
                DumpListOfTriangles(so.FindProperty("RelativeTriangles"), " RelativeTriangles (Even/East 基准)");
                DumpListOfTriangles(so.FindProperty("RelativeTrianglesOdd"), " RelativeTrianglesOdd (Odd/EastNorth 基准)");
            }
        }

        private static void SectionUnitVolumes()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:UnitVolume"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var vol = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Grid.UnitVolume>(path);
                if (vol == null) { Sb.AppendLine($"  !! 加载失败: {path}"); _sectionErrors++; continue; }

                Sb.AppendLine($" ==== UnitVolume \"{vol.name}\" @ {path} [guid {guid}] ====");
                var so = new SerializedObject(vol);
                var volumes = so.FindProperty("Volumes");
                if (volumes != null && volumes.isArray)
                {
                    Sb.AppendLine($" Volumes.Count = {volumes.arraySize}");
                    for (int i = 0; i < volumes.arraySize; i++)
                    {
                        var v = volumes.GetArrayElementAtIndex(i);
                        var dir = v.FindPropertyRelative("Direction");
                        Sb.AppendLine($" Volumes[{i}].Direction = {(dir != null ? $"{dir.enumValueIndex} ({dir.enumDisplayNames[dir.enumValueIndex]})" : "?")}");
                        DumpListOfTriangles(v.FindPropertyRelative("RelativeTriangles"), "   RelativeTriangles");
                    }
                }
                else { Sb.AppendLine("  Volumes 属性缺失"); _sectionErrors++; }
            }
        }

        private static void DumpListOfTriangles(SerializedProperty listProp, string title)
        {
            if (listProp == null || !listProp.isArray) { Sb.AppendLine($" {title}: <缺失>"); _sectionErrors++; return; }
            Sb.AppendLine($" {title} Count = {listProp.arraySize}");
            for (int i = 0; i < listProp.arraySize; i++)
            {
                var t = listProp.GetArrayElementAtIndex(i);
                int x = t.FindPropertyRelative("X")?.intValue ?? 0;
                int y = t.FindPropertyRelative("Y")?.intValue ?? 0;
                int tv = t.FindPropertyRelative("T")?.intValue ?? 0;
                Sb.AppendLine($"   [{i}] ({x}, {y}, {tv})");
            }
        }

        // ------------------------------------------------------------------
        // S06 跨资产同名局部 ID
        // ------------------------------------------------------------------
        private static void SectionCrossAssetLocalIds()
        {
            var perLibrary = new List<(string libPath, string libName, Dictionary<string, ActionSnapshot> actions)>();
            foreach (var guid in AssetDatabase.FindAssets("t:ActionLibrarySO"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var lib = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Actions.ActionLibrarySO>(path);
                if (lib == null) continue;
                var map = new Dictionary<string, ActionSnapshot>();
                foreach (var entry in lib.Actions)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.ID)) continue;
                    map[entry.ID] = Snapshot(lib, entry);
                }
                perLibrary.Add((path, lib.name, map));
            }

            var allIds = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (_, _, map) in perLibrary)
                foreach (var id in map.Keys) allIds.Add(id);

            Sb.AppendLine($" 参与比较的 ActionLibrarySO: {perLibrary.Count}");
            Sb.AppendLine($" 全部局部 ID（按 Ordinal 排序）: {string.Join(", ", allIds)}");

            foreach (var id in allIds)
            {
                Sb.AppendLine($" ---- 局部 ID \"{id}\" ----");
                var seen = new Dictionary<string, ActionSnapshot>();
                bool consistent = true;
                foreach (var (libPath, libName, map) in perLibrary)
                {
                    if (!map.TryGetValue(id, out var snap))
                    {
                        Sb.AppendLine($"   <缺失> in {libName} @ {libPath}");
                        continue;
                    }
                    Sb.AppendLine($"   {libName}: {snap.Summary}");
                    foreach (var (otherLib, otherSnap) in seen)
                    {
                        if (!ActionSnapshot.EqualsIgnorePatternAsset(snap, otherSnap)) consistent = false;
                    }
                    seen[libName] = snap;
                }
                Sb.AppendLine(consistent
                    ? $"   => 结论: 各库中同名 ID \"{id}\" 的标量字段（BaseTime/BaseDamage/ImpactType/StaminaCost/ForceMultiplier）一致"
                    : $"   => 结论: 同名 ID \"{id}\" 的标量字段跨库不一致，见上");
            }

            // 同名 Pattern 资产的 R1/R2 内容差异（QuickSlash→Slash 等的形状是否一致）
            Sb.AppendLine(" ---- 同原型 Pattern 资产 R1/R2 三角形集合比较 ----");
            var archetypes = new[] { "Slash", "Smash", "Cleave", "Thrust", "Whirlwind" };
            foreach (var arch in archetypes)
            {
                var r1 = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Combat.AttackPattern>($"Assets/Resources/GeneratedActions/Pattern_{arch}_R1_0.asset");
                var r2 = AssetDatabase.LoadAssetAtPath<ProjectHero.Core.Combat.AttackPattern>($"Assets/Resources/GeneratedActions/Pattern_{arch}_R2_0.asset");
                if (r1 == null || r2 == null)
                {
                    Sb.AppendLine($"   {arch}: R1 或 R2 资产缺失 (R1={(r1 != null)}, R2={(r2 != null)})");
                    _sectionErrors++;
                    continue;
                }
                var s1Even = SetKey(r1.RelativeTriangles);
                var s2Even = SetKey(r2.RelativeTriangles);
                var s1Odd = SetKey(r1.RelativeTrianglesOdd);
                var s2Odd = SetKey(r2.RelativeTrianglesOdd);
                bool evenSame = s1Even.SetEquals(s2Even);
                bool oddSame = s1Odd.SetEquals(s2Odd);
                Sb.AppendLine($"   {arch}: Even count R1={r1.RelativeTriangles.Count} R2={r2.RelativeTriangles.Count} setEqual={evenSame}; " +
                               $"Odd count R1={r1.RelativeTrianglesOdd.Count} R2={r2.RelativeTrianglesOdd.Count} setEqual={oddSame}");
            }
        }

        private sealed class ActionSnapshot
        {
            public float BaseTime, BaseDamage, StaminaCost, ForceMultiplier;
            public int ImpactTypeIndex;
            public string PatternGuid, PatternName, PatternPath;

            public string Summary =>
                $"BaseTime={BaseTime} BaseDamage={BaseDamage} ImpactType={ImpactTypeIndex} StaminaCost={StaminaCost} ForceMultiplier={ForceMultiplier} " +
                $"Pattern={PatternName} @ {PatternPath} [guid {PatternGuid}]";

            public static bool EqualsIgnorePatternAsset(ActionSnapshot a, ActionSnapshot b)
            {
                if (a == null || b == null) return a == b;
                return Math.Abs(a.BaseTime - b.BaseTime) < 1e-6 &&
                       Math.Abs(a.BaseDamage - b.BaseDamage) < 1e-6 &&
                       a.ImpactTypeIndex == b.ImpactTypeIndex &&
                       Math.Abs(a.StaminaCost - b.StaminaCost) < 1e-6 &&
                       Math.Abs(a.ForceMultiplier - b.ForceMultiplier) < 1e-6;
            }
        }

        private static ActionSnapshot Snapshot(ProjectHero.Core.Actions.ActionLibrarySO lib, ProjectHero.Core.Actions.ActionLibrarySO.ActionEntry entry)
        {
            var d = entry.Data;
            string patternGuid = "", patternPath = "", patternName = "";
            if (d != null && d.Pattern != null)
            {
                patternPath = AssetDatabase.GetAssetPath(d.Pattern);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(d.Pattern, out patternGuid, out _);
                patternName = d.Pattern.name;
            }
            return new ActionSnapshot
            {
                BaseTime = d?.BaseTime ?? float.NaN,
                BaseDamage = d?.BaseDamage ?? float.NaN,
                StaminaCost = d?.StaminaCost ?? float.NaN,
                ForceMultiplier = d?.ForceMultiplier ?? float.NaN,
                ImpactTypeIndex = d != null ? (int)d.ImpactType : -1,
                PatternGuid = patternGuid,
                PatternPath = patternPath,
                PatternName = patternName,
            };
        }

        private static HashSet<string> SetKey(List<ProjectHero.Core.Grid.TrianglePoint> tris)
        {
            var set = new HashSet<string>();
            if (tris != null)
                foreach (var t in tris) set.Add($"{t.X},{t.Y},{t.T}");
            return set;
        }

        // ------------------------------------------------------------------
        // S07 场景消费关系
        // ------------------------------------------------------------------
        private static void SectionSceneConsumption()
        {
            Sb.AppendLine($" 场景: {ScenePath}  (只读 YAML 文本解析；Unity 6 中 LoadAllAssetsAtPath 对场景返回 0 个对象，故不依赖它)");
            string sceneFullPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ScenePath);
            string sceneText = ReadAllTextBestEffort(sceneFullPath);
            var blocks = SplitSceneBlocks(sceneText);

            // 1) GameObject 名称表 (anchor -> name)
            var goNameById = new Dictionary<string, string>();
            foreach (var b in blocks)
            {
                if (b.ClassId != 1) continue;
                foreach (var line in b.Body)
                {
                    string t = line.TrimStart();
                    if (t.StartsWith("m_Name:"))
                    {
                        goNameById[b.Anchor] = t.Substring("m_Name:".Length).Trim();
                        break;
                    }
                }
            }
            Sb.AppendLine($" 场景 GameObject 数 (YAML u!1 块): {goNameById.Count}");

            // 2) MonoBehaviour 清单 (u!114 且带 m_Script)
            var mbList = new List<SceneMbInfo>();
            var missingScripts = new List<string>();
            foreach (var b in blocks)
            {
                if (b.ClassId != 114) continue;
                string goName = "<未绑定>";
                string scriptGuid = "";
                bool hasScript = false;
                foreach (var line in b.Body)
                {
                    string t = line.TrimStart();
                    if (t.StartsWith("m_GameObject:"))
                    {
                        var gm = Regex.Match(t, @"fileID:\s*(\d+)");
                        if (gm.Success && goNameById.TryGetValue(gm.Groups[1].Value, out var n)) goName = n;
                    }
                    else if (t.StartsWith("m_Script:"))
                    {
                        var sm = Regex.Match(t, @"guid:\s*([0-9a-f]{32})");
                        if (sm.Success) { scriptGuid = sm.Groups[1].Value; hasScript = true; }
                    }
                }
                if (!hasScript)
                {
                    missingScripts.Add(goName);
                    continue;
                }
                string scriptPath = AssetDatabase.GUIDToAssetPath(scriptGuid);
                mbList.Add(new SceneMbInfo { Anchor = b.Anchor, GoName = goName, ScriptGuid = scriptGuid, ScriptPath = scriptPath, Body = b.Body });
            }

            Sb.AppendLine($" 场景 MonoBehaviour 总数 (u!114 带 m_Script): {mbList.Count}");
            foreach (var mb in mbList)
                Sb.AppendLine($"   MonoBehaviour on \"{mb.GoName}\" (&{mb.Anchor}): script={mb.ScriptPath} [guid {mb.ScriptGuid}]");
            foreach (var goName in missingScripts)
            {
                Sb.AppendLine($"  !! MISSING SCRIPT on GameObject \"{goName}\"");
                _sectionErrors++;
            }

            // 3) 关键组件全字段 dump（CombatUnit / CombatDemo / LibraryGenerator / BattleTimeline / 其他 Core/Gameplay 脚本）
            int combatUnitCount = 0;
            foreach (var mb in mbList)
            {
                bool isCombatUnit = mb.ScriptPath.EndsWith("CombatUnit.cs");
                bool isSpecial = mb.ScriptPath.EndsWith("CombatDemo.cs") || mb.ScriptPath.EndsWith("LibraryGenerator.cs");
                if (!isCombatUnit && !isSpecial) continue;
                if (isCombatUnit)
                {
                    combatUnitCount++;
                    Sb.AppendLine($" ---- CombatUnit #{combatUnitCount} on \"{mb.GoName}\" (&{mb.Anchor}) 完整 YAML 块 ----");
                }
                else
                {
                    Sb.AppendLine($" ---- 特殊组件 {mb.ScriptPath} on \"{mb.GoName}\" (&{mb.Anchor}) 完整 YAML 块 ----");
                }
                foreach (var line in mb.Body)
                    Sb.AppendLine($"   {line}");
            }
            Sb.AppendLine($" 场景 CombatUnit 实例数: {combatUnitCount}");

            // 4) 场景 YAML 中的全部 guid 引用 → 资产路径
            Sb.AppendLine(" ---- 场景 YAML guid 引用解析 ----");
            var guidSet = new SortedSet<string>();
            foreach (Match m in Regex.Matches(sceneText ?? "", @"guid:\s*([0-9a-f]{32})"))
                guidSet.Add(m.Groups[1].Value);
            Sb.AppendLine($" 场景 YAML 中唯一 guid 数: {guidSet.Count}");
            foreach (var g in guidSet)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                string desc = string.IsNullOrEmpty(p) ? "<内置/包内资源或无映射>" : p;
                Sb.AppendLine($"   guid {g} -> {desc}");
            }

            // 5) 配置资产在场景中的直接引用判定
            Sb.AppendLine(" ---- 配置资产场景引用判定 ----");
            foreach (var filter in new[] { "t:ActionLibrarySO", "t:AttackPattern", "t:UnitVolume" })
            {
                foreach (var guid in AssetDatabase.FindAssets(filter))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    bool referenced = guidSet.Contains(guid);
                    Sb.AppendLine($"   {(referenced ? "[场景引用]" : "[未引用 ]")} {filter.Substring(2)} @ {path} [guid {guid}]");
                }
            }
        }

        private sealed class SceneMbInfo
        {
            public string Anchor, GoName, ScriptGuid, ScriptPath;
            public List<string> Body;
        }

        private sealed class SceneBlock
        {
            public int ClassId;
            public string Anchor;
            public List<string> Body;
        }

        private static List<SceneBlock> SplitSceneBlocks(string sceneText)
        {
            var result = new List<SceneBlock>();
            var lines = (sceneText ?? "").Replace("\r\n", "\n").Split('\n');
            SceneBlock current = null;
            foreach (var raw in lines)
            {
                var m = Regex.Match(raw, @"^--- !u!(\d+) &(\d+)");
                if (m.Success)
                {
                    current = new SceneBlock
                    {
                        ClassId = int.Parse(m.Groups[1].Value),
                        Anchor = m.Groups[2].Value,
                        Body = new List<string>(),
                    };
                    result.Add(current);
                }
                else if (current != null)
                {
                    current.Body.Add(raw);
                }
            }
            return result;
        }

        // ------------------------------------------------------------------
        // S08 Prefab 扫描
        // ------------------------------------------------------------------
        private static void SectionPrefabScan()
        {
            if (!AssetDatabase.IsValidFolder(PrefabDir))
            {
                Sb.AppendLine($" 目录不存在: {PrefabDir}");
                return;
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Sb.AppendLine($" ==== Prefab @ {path} [guid {guid}] ====");
                var objs = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (var obj in objs)
                {
                    if (obj is MonoBehaviour mb)
                    {
                        var so = new SerializedObject(mb);
                        var scriptProp = so.FindProperty("m_Script");
                        var goProp = so.FindProperty("m_GameObject");
                        string goName = goProp?.objectReferenceValue is GameObject g ? g.name : "?";
                        string scriptPath = scriptProp?.objectReferenceValue is MonoScript ms ? AssetDatabase.GetAssetPath(ms) : "<missing>";
                        Sb.AppendLine($"   MonoBehaviour on \"{goName}\": {scriptPath}");
                        if (scriptPath == "<missing>") { _sectionErrors++; continue; }
                        // 列出引用到本任务配置资产的对象引用字段
                        DumpInterestingRefs(so, "     ");
                    }
                }
            }
        }

        private static void DumpInterestingRefs(SerializedObject so, string indent)
        {
            var p = so.GetIterator();
            bool first = true;
            while (p.NextVisible(first))
            {
                first = false;
                if (p.propertyType != SerializedPropertyType.ObjectReference) continue;
                var v = p.objectReferenceValue;
                if (v == null) continue;
                string vPath = AssetDatabase.GetAssetPath(v);
                if (vPath.Contains("Resources/ActionLibrary") || vPath.Contains("Resources/GeneratedActions") || vPath.Contains("Resources/Unit Volume"))
                {
                    Sb.AppendLine($"{indent}{p.displayName} -> {ResolveRef(v)}");
                }
            }
        }

        // ------------------------------------------------------------------
        // S09 脚本程序集与 MonoScript GUID
        // ------------------------------------------------------------------
        private static void SectionScriptAssemblyReport()
        {
            var csFiles = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);
            var interesting = new List<string>
            {
                "Actions/ActionLibrarySO.cs", "Actions/Action.cs", "Actions/ActionType.cs", "Actions/ActionScheduler.cs",
                "Actions/Intents/AttackIntent.cs", "Combat/AttackPattern.cs", "Entities/CombatUnit.cs",
                "Grid/GridDirection.cs", "Grid/TrianglePoint.cs", "Grid/GridMath.cs", "Grid/UnitVolume.cs",
                "Pathfinding/Pathfinder.cs", "Physics/PhysicsEngine.cs", "Interactions/CombatIntent.cs",
                "Interactions/CombatArbiter.cs", "Gameplay/BattleManager.cs", "Gameplay/EnemyAIController.cs",
                "Gameplay/TacticsController.cs", "Demos/CombatDemo.cs", "Demos/LibraryGenerator.cs",
                "UI/UIManager.cs", "UI/HUDManager.cs", "UI/Timeline/TimelineEditorUI.cs",
                "Visuals/NextActionPreview/NextActionPreviewSystem.cs",
            };
            Sb.AppendLine(" 关键脚本的 MonoScript GUID（.meta 保留要求依据）与所属程序集：");
            foreach (var f in csFiles)
            {
                string rel = MakeRel(f).Replace('\\', '/');
                bool isInteresting = interesting.Any(rel.EndsWith);
                if (!isInteresting) continue;
                string guid = AssetDatabase.AssetPathToGUID("Assets/" + rel);
                string asm = rel.Contains("/Editor/") ? "Assembly-CSharp-Editor" : "Assembly-CSharp";
                Sb.AppendLine($"   {rel,-60} guid={guid}  asm={asm}");
            }
        }

        // ------------------------------------------------------------------
        // S10 缺失稳定 ID 的源码证据扫描
        // ------------------------------------------------------------------
        private static void SectionStableIdEvidence()
        {
            Sb.AppendLine(" 只读扫描 Assets/Scripts 下全部 .cs，寻找不稳定 ID 来源（对象名/注册顺序/GetInstanceID/Time.frameCount）。");
            Sb.AppendLine(" 以下为静态证据；语义归属由架构师记录负责。");
            var patterns = new (string label, string regex)[]
            {
                ("GetInstanceID", @"GetInstanceID\s*\("),
                ("Time.frameCount", @"Time\.frameCount"),
                ("注册顺序扫描 (FindObjects*/FindFirst*/FindAny*)", @"FindObjectsByType|FindObjectsOfType|FindObjectOfType|FindAnyObjectByType|FindFirstObjectByType"),
                ("名字拼进 ID / 名字派生对象名", @"ID\s*=\s*\$""[^""]*\{[^}]*\.name|\.name\s*=\s*\$""HUD_"),
                ("nameof/注册顺序顺序依赖提示 (List/Array 顺序索引注释不扫描)", @"GetComponentsInChildren|GetComponents\b"),
            };
            var csFiles = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);
            int totalMatches = 0;
            foreach (var f in csFiles)
            {
                string rel = MakeRel(f).Replace('\\', '/');
                if (!rel.StartsWith("Scripts/")) continue;
                string text;
                try { text = ReadAllTextBestEffort(f); }
                catch { continue; }
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var matched = new List<string>();
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (var (label, regex) in patterns)
                    {
                        if (Regex.IsMatch(lines[i], regex))
                        {
                            matched.Add($"L{i + 1} [{label}]: {lines[i].Trim()}");
                            totalMatches++;
                        }
                    }
                }
                if (matched.Count > 0)
                {
                    Sb.AppendLine($" -- {rel}");
                    foreach (var m in matched) Sb.AppendLine($"    {m}");
                }
            }
            Sb.AppendLine($" 匹配行总数: {totalMatches}");
        }

        // ==================================================================
        // 工具方法
        // ==================================================================

        private static void DumpGenericChildren(SerializedProperty prop, string indent, bool skipScriptAndName = false, bool topLevel = false)
        {
            if (prop == null) { Sb.AppendLine($"{indent}<null property>"); return; }

            if (topLevel)
            {
                // GetIterator 用法：NextVisible(true) 移动到第一个可见属性
                bool first = true;
                while (prop.NextVisible(first))
                {
                    first = false;
                    if (skipScriptAndName && (prop.name == "m_Script" || prop.name == "m_Name")) continue;
                    DumpPropertyNode(prop, indent);
                }
                return;
            }

            if (prop.isArray)
            {
                Sb.AppendLine($"{indent}{prop.displayName}.Count = {prop.arraySize}");
                for (int i = 0; i < prop.arraySize; i++)
                {
                    var el = prop.GetArrayElementAtIndex(i);
                    Sb.AppendLine($"{indent}{prop.displayName}[{i}]:");
                    DumpPropertyNode(el, indent + "  ");
                }
                return;
            }

            var child = prop.Copy();
            var end = prop.GetEndProperty();
            bool enter = true;
            while (child.Next(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                DumpPropertyNode(child, indent);
            }
        }

        private static void DumpPropertyNode(SerializedProperty p, string indent)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    Sb.AppendLine($"{indent}{p.displayName} -> {ResolveRef(p.objectReferenceValue)}");
                    break;
                case SerializedPropertyType.Enum:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.enumValueIndex} ({p.enumDisplayNames[p.enumValueIndex]})");
                    break;
                case SerializedPropertyType.Integer:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.intValue}");
                    break;
                case SerializedPropertyType.Float:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.floatValue}");
                    break;
                case SerializedPropertyType.Boolean:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.boolValue}");
                    break;
                case SerializedPropertyType.String:
                    Sb.AppendLine($"{indent}{p.displayName} = \"{p.stringValue}\"");
                    break;
                // 注意: Unity 6 将 long 序列化为 Integer，SerializedPropertyType 无 Long 成员
                case SerializedPropertyType.Character:
                    Sb.AppendLine($"{indent}{p.displayName} = '{(char)p.intValue}'");
                    break;
                case SerializedPropertyType.Color:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.colorValue}");
                    break;
                case SerializedPropertyType.LayerMask:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.intValue} (LayerMask)");
                    break;
                case SerializedPropertyType.Vector2:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.vector2Value}");
                    break;
                case SerializedPropertyType.Vector3:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.vector3Value}");
                    break;
                case SerializedPropertyType.Vector4:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.vector4Value}");
                    break;
                case SerializedPropertyType.Vector2Int:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.vector2IntValue}");
                    break;
                case SerializedPropertyType.Vector3Int:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.vector3IntValue}");
                    break;
                case SerializedPropertyType.Rect:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.rectValue}");
                    break;
                case SerializedPropertyType.RectInt:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.rectIntValue}");
                    break;
                case SerializedPropertyType.Bounds:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.boundsValue}");
                    break;
                case SerializedPropertyType.BoundsInt:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.boundsIntValue}");
                    break;
                case SerializedPropertyType.Quaternion:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.quaternionValue}");
                    break;
                case SerializedPropertyType.AnimationCurve:
                    Sb.AppendLine($"{indent}{p.displayName} = <AnimationCurve keys={p.animationCurveValue?.length}>");
                    break;
                case SerializedPropertyType.Gradient:
                    Sb.AppendLine($"{indent}{p.displayName} = <Gradient>");
                    break;
                case SerializedPropertyType.Hash128:
                    Sb.AppendLine($"{indent}{p.displayName} = {p.hash128Value}");
                    break;
                case SerializedPropertyType.ExposedReference:
                    Sb.AppendLine($"{indent}{p.displayName} -> ExposedRef {ResolveRef(p.exposedReferenceValue)}");
                    break;
                case SerializedPropertyType.ManagedReference:
                    Sb.AppendLine($"{indent}{p.displayName} = <ManagedReference {p.managedReferenceFullTypename}>");
                    break;
                case SerializedPropertyType.Generic:
                    // 自定义 [Serializable] 类/结构体 → 递归子属性
                    Sb.AppendLine($"{indent}{p.displayName}:");
                    DumpGenericChildren(p, indent + "  ");
                    break;
                default:
                    Sb.AppendLine($"{indent}{p.displayName} = <未处理类型 {p.propertyType}>");
                    break;
            }
        }

        private static string ResolveRef(UnityEngine.Object obj)
        {
            if (obj == null) return "<null>";
            string path = AssetDatabase.GetAssetPath(obj);
            string guid = "";
            long localId = 0;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out guid, out localId);
            return $"{obj.GetType().Name} \"{obj.name}\" @ {(string.IsNullOrEmpty(path) ? "<无路径>" : path)} [guid {guid} fileID {localId}]";
        }

        private static string ReadMetaMainObjectFileID(string assetPath)
        {
            try
            {
                string metaPath = assetPath + ".meta";
                string text = ReadAllTextBestEffort(metaPath);
                var m = Regex.Match(text, @"mainObjectFileID:\s*(\d+)");
                return m.Success ? m.Groups[1].Value : "<meta无mainObjectFileID>";
            }
            catch (Exception ex)
            {
                return $"<读meta失败: {ex.Message}>";
            }
        }

        private static string ReadYamlAnchorFileId(string assetPath)
        {
            try
            {
                string text = ReadAllTextBestEffort(assetPath);
                var m = Regex.Match(text, @"^--- !u!\d+ &(\d+)", RegexOptions.Multiline);
                return m.Success ? m.Groups[1].Value : "<无YAML锚点>";
            }
            catch (Exception ex)
            {
                return $"<读YAML失败: {ex.Message}>";
            }
        }

        private static string ReadAllTextBestEffort(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // GBK / 系统 ANSI 回退（Windows）
                try
                {
                    var gbk = Encoding.GetEncoding(936);
                    return gbk.GetString(bytes);
                }
                catch
                {
                    return Encoding.UTF8.GetString(bytes); // 宽容解码
                }
            }
        }

        private static string MakeRel(string fullPath)
        {
            string root = Application.dataPath.TrimEnd('\\', '/');
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(root.Length).TrimStart('\\', '/');
            return fullPath;
        }
    }
}
#endif // UNITY_EDITOR
