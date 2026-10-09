using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 运行时创建的 Legacy 从属写入者登记表（任务 03B「必须产出」3 后半句）。
    ///
    /// 为什么需要它：<c>TimelineEditorUI</c> 不是场景序列化组件，而是
    /// <c>UIManager.EnsureTimelineUI()</c> 在运行时 <c>AddComponent</c> 创建的。
    /// 因此模式门控不可能只靠"场景扫描 + 脚本执行顺序"完成——新创建的写入者必须
    /// <strong>在创建点主动登记</strong>，Bootstrap 才能对它应用当前模式的启用/禁用决策。
    ///
    /// 契约：
    /// <list type="bullet">
    /// <item>登记后立即按 <see cref="BattleRuntimeBootstrap.BattleMode"/> 应用当前门控；
    /// 战斗尚未创建时按"基线（启用）"处理，等 Bootstrap 创建战斗时统一应用。</item>
    /// <item><strong>New 模式注册未分类类型即失败</strong>：注册表会把它记为未分类逻辑写入者，
    /// 并立即禁用该组件（禁止事项第 2 条）。</item>
    /// <item>登记表是引用表：Bootstrap 每条登记的 <c>GateTarget</c> 必须是<b>运行时实例</b>，
    /// 不用类型名推断。</item>
    /// </list>
    /// </summary>
    public static class LegacyWriterRegistry
    {
        public const string RUNTIME_WRITER_UNCLASSIFIED = "RUNTIME_WRITER_UNCLASSIFIED";

        private static readonly List<Registration> Registrations = new List<Registration>();

        private static BattleRuntimeBootstrap _active;

        /// <summary>当前活动的 Bootstrap（场景中恰有一个时非空）。</summary>
        public static BattleRuntimeBootstrap ActiveBootstrap => _active;

        /// <summary>已登记的运行时写入者实例（诊断与测试直接读取）。</summary>
        public static IReadOnlyList<Behaviour> RegisteredWriters
        {
            get
            {
                var list = new List<Behaviour>(Registrations.Count);
                for (int i = 0; i < Registrations.Count; i++) list.Add(Registrations[i].Writer);
                return list;
            }
        }

        /// <summary>
        /// 登记一个运行时创建的 Legacy 从属写入者。
        /// 返回稳定拒绝码（null = 已登记）。
        /// </summary>
        public static string Register(Behaviour writer, string sceneObjectPath, string timeSource)
        {
            if (writer == null) return "RUNTIME_WRITER_NULL";

            for (int i = 0; i < Registrations.Count; i++)
            {
                if (ReferenceEquals(Registrations[i].Writer, writer)) return null;
            }

            Registrations.Add(new Registration(writer, sceneObjectPath, timeSource));

            var bootstrap = _active;
            if (bootstrap == null) return null;

            bool classified = RuntimeCallbackRegistry
                .ClassOf(writer.GetType().FullName, "Update")
                .HasValue;

            if (bootstrap.BattleMode == BattleRuntimeMode.New && !classified)
            {
                // New 模式中出现未分类的运行时逻辑写入者：记为未分类并立即禁用。
                BattleRuntimeBootstrap.MarkUnclassifiedLogicWriterType(writer.GetType().FullName);
                writer.enabled = false;
                return RUNTIME_WRITER_UNCLASSIFIED + "|" + sceneObjectPath;
            }

            writer.enabled = bootstrap.BattleMode != BattleRuntimeMode.New;
            return null;
        }

        /// <summary>注销（组件销毁时调用；引用表不保留已销毁对象）。</summary>
        public static void Unregister(Behaviour writer)
        {
            if (writer == null) return;
            for (int i = Registrations.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(Registrations[i].Writer, writer)) Registrations.RemoveAt(i);
            }
        }

        /// <summary>Bootstrap 创建战斗时成为活动实例（重复 Bootstrap 不会静默改写）。</summary>
        internal static void SetActiveBootstrap(BattleRuntimeBootstrap bootstrap)
        {
            if (bootstrap == null) return;
            if (_active != null && !ReferenceEquals(_active, bootstrap)) return;
            _active = bootstrap;
        }

        internal static void ClearActiveBootstrap(BattleRuntimeBootstrap bootstrap)
        {
            if (ReferenceEquals(_active, bootstrap)) _active = null;
        }

        /// <summary>把当前模式的启用/禁用决策应用到全部已登记的运行时写入者。</summary>
        internal static void ApplyGate(bool enabled)
        {
            for (int i = 0; i < Registrations.Count; i++)
            {
                var writer = Registrations[i].Writer;
                if (writer == null) continue;
                writer.enabled = enabled;
            }
        }

        /// <summary>当前登记的运行时写入者的启用状态快照（诊断与测试）。</summary>
        public static IReadOnlyDictionary<string, bool> EnabledStates()
        {
            var states = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < Registrations.Count; i++)
            {
                var registration = Registrations[i];
                if (registration.Writer == null) continue;
                states[registration.SceneObjectPath + "#" + registration.Writer.GetType().Name] =
                    registration.Writer.enabled;
            }
            return states;
        }

        /// <summary>测试/工具重置（不改变任何战斗状态）。</summary>
        public static void ResetForTests()
        {
            Registrations.Clear();
            _active = null;
        }

        private sealed class Registration
        {
            public Registration(Behaviour writer, string sceneObjectPath, string timeSource)
            {
                Writer = writer;
                SceneObjectPath = sceneObjectPath ?? string.Empty;
                TimeSource = timeSource ?? string.Empty;
            }

            public Behaviour Writer { get; }

            public string SceneObjectPath { get; }

            public string TimeSource { get; }
        }
    }
}
