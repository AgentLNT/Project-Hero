namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 战斗生命周期与模式门控的<b>纯数据契约检查</b>（任务 03B 模式契约的可测试落点）。
    ///
    /// 为什么放在契约程序集：模式不可变、Legacy/Shadow 启用写入组、New 全程禁用
    /// 这三条规则必须能被自动化测试直接验证，而不是靠"读代码确认"。
    /// 本类型<strong>不</strong>引用任何 Legacy 类型，也不接触 Unity 对象。
    /// </summary>
    public static class BattleRuntimeLifecycle
    {
        public const string MODE_CHANGE_WHILE_ACTIVE = BattleRuntimeBootstrap.BOOTSTRAP_MODE_CHANGE_REJECTED;
        public const string START_WHILE_ACTIVE = BattleRuntimeBootstrap.BOOTSTRAP_DOUBLE_START;
        public const string STOP_WITHOUT_ACTIVE_BATTLE = "BATTLE_RUNTIME_STOP_WITHOUT_ACTIVE_BATTLE";
        public const string RELEASE_WITHOUT_ACTIVE_BATTLE = "BATTLE_RUNTIME_RELEASE_WITHOUT_ACTIVE_BATTLE";

        /// <summary>冻结的模式状态快照（不引用任何 Unity 对象）。</summary>
        public readonly struct ModeState
        {
            public ModeState(BattleRuntimeMode mode, bool battleActive, bool stopped, bool writerGroupEnabled)
            {
                Mode = mode;
                BattleActive = battleActive;
                Stopped = stopped;
                WriterGroupEnabled = writerGroupEnabled;
            }

            public BattleRuntimeMode Mode { get; }

            public bool BattleActive { get; }

            public bool Stopped { get; }

            /// <summary>Legacy 从属写入组当前是否启用。</summary>
            public bool WriterGroupEnabled { get; }

            /// <summary>本场战斗固定之后是否还允许修改模式。</summary>
            public bool AllowsModeChange => !BattleActive;

            /// <summary>按模式应处于的写入组启用状态。</summary>
            public bool ExpectedWriterGroupEnabled => BattleRuntimeModes.EnablesLegacyWriterGroup(Mode);

            /// <summary>本场战斗是否仍然活动（可推进）。</summary>
            public bool IsAdvancing => BattleActive && !Stopped;
        }

        /// <summary>
        /// 校验当前模式状态是否满足全部模式契约。返回 null 表示全部成立。
        /// </summary>
        public static string Validate(ModeState state)
        {
            if (!state.BattleActive) return null;

            if (state.WriterGroupEnabled != state.ExpectedWriterGroupEnabled)
            {
                return "BATTLE_RUNTIME_WRITER_GROUP_GATE_MISMATCH|mode="
                    + BattleRuntimeModes.Describe(state.Mode)
                    + "|enabled=" + state.WriterGroupEnabled;
            }

            return null;
        }

        /// <summary>尝试切换模式：活动战斗期间显式拒绝（不迁移任何运行中状态）。</summary>
        public static bool TrySwitchMode(ModeState state, BattleRuntimeMode requested, out string rejection)
        {
            if (state.BattleActive)
            {
                rejection = MODE_CHANGE_WHILE_ACTIVE
                    + "|active=" + BattleRuntimeModes.Describe(state.Mode)
                    + "|requested=" + BattleRuntimeModes.Describe(requested);
                return false;
            }

            rejection = null;
            return true;
        }

        /// <summary>尝试开始一场新战斗：已经活动时必须显式拒绝。</summary>
        public static bool TryStartBattle(ModeState state, out string rejection)
        {
            if (state.BattleActive)
            {
                rejection = START_WHILE_ACTIVE + "|mode=" + BattleRuntimeModes.Describe(state.Mode);
                return false;
            }

            rejection = null;
            return true;
        }
    }
}
