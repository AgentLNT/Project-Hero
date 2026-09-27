using System;
using System.Text;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEditor;
using UnityEngine;

namespace ProjectHero.Editor.RuntimeOwnership
{
    /// <summary>
    /// 任务 03B 的批处理入口（供 <c>Unity.exe -batchmode -executeMethod</c> 调用）。
    ///
    /// 它只转发到 <see cref="RuntimeOwnershipSceneTool"/>，不包含任何独立逻辑：
    /// 交互式菜单与批处理必须走同一条代码路径，否则"工具幂等"这一条就无法自证。
    /// </summary>
    public static class RuntimeOwnershipBatchEntry
    {
        public const string MainScenePath = "Assets/Scenes/CombatSampleScene.unity";
        public const string HiddenValidationScenePath = "Assets/Scenes/HiddenRuntimeValidation.unity";

        /// <summary>接入主场景 + 创建隐藏验证场景 + 审计。返回 0 表示成功。</summary>
        public static void RunBatchWiring()
        {
            int exitCode = 0;
            var log = new StringBuilder();
            try
            {
                log.AppendLine("=== RuntimeOwnership batch wiring start ===");
                log.AppendLine(RuntimeOwnershipSceneTool.WireMainScene());
                log.AppendLine(RuntimeOwnershipSceneTool.CreateHiddenValidationScene());
                log.AppendLine(RuntimeOwnershipSceneTool.AuditAllScenes());

                AssertAssetExists(MainScenePath, log);
                AssertAssetExists(HiddenValidationScenePath, log);

                log.AppendLine("=== RuntimeOwnership batch wiring done ===");
            }
            catch (Exception exception)
            {
                exitCode = 1;
                log.AppendLine("BATCH_WIRING_FAILED|" + exception.GetType().Name + "|" + exception.Message);
                log.AppendLine(exception.StackTrace);
            }

            Debug.Log(log.ToString());
            if (Application.isBatchMode)
            {
                // 让批处理调用者拿到确定退出码（--executeMethod 默认退出码不反映返回值）。
                EditorApplication.Exit(exitCode);
            }
            else if (exitCode != 0)
            {
                throw new InvalidOperationException("RuntimeOwnership batch wiring failed; see log above.");
            }
        }

        private static void AssertAssetExists(string path, StringBuilder log)
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) == null)
                throw new InvalidOperationException("expected asset missing: " + path);
            log.AppendLine("asset ok: " + path);
        }

        /// <summary>只做只读审计（不写任何资产），用于复核。</summary>
        public static void RunBatchAudit()
        {
            var log = new StringBuilder();
            log.AppendLine(RuntimeOwnershipSceneTool.AuditAllScenes());
            log.AppendLine("LegacyWriterExecutionOrder=" + RuntimeCallbackRegistry.LegacyWriterExecutionOrder);
            log.AppendLine("BootstrapExecutionOrder=" + RuntimeCallbackRegistry.BootstrapExecutionOrder);
            log.AppendLine("CheckpointExecutionOrder=" + RuntimeCallbackRegistry.CheckpointExecutionOrder);
            Debug.Log(log.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
