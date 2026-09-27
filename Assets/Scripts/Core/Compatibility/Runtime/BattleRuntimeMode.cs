namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 顶层战斗时钟的启动模式（任务 03B「必须产出」2；主方案不变量 23）。
    ///
    /// 语义：
    /// <list type="bullet">
    /// <item><see cref="Legacy"/>：只有旧运行组写场景。Bootstrap 调用 Legacy 适配器，
    /// 已登记的 Legacy 从属写入者全部启用；<strong>不创建、不推进</strong>新模拟。</item>
    /// <item><see cref="Shadow"/>：Legacy 仍是唯一场景权威与唯一反馈来源。Bootstrap 在
    /// <c>Update()</c> 推进两边，但只在只读 <c>LateUpdate()</c> 检查点采样比较；
    /// 新模拟对 Unity 对象、旧状态与反馈的写入次数必须为 0。</item>
    /// <item><see cref="New"/>：旧 <c>BattleTimeline.AdvanceTime()</c> 调用数为 0，
    /// Legacy 从属写入组<strong>全部禁用</strong>，由 Bootstrap 通过最小 New Driver 调用
    /// 任务 03 的唯一 <c>BattleSimulation.Step()</c>。</item>
    /// </list>
    ///
    /// <strong>模式在创建本场战斗之前固定，活动战斗期间禁止修改</strong>；所谓"可回切"只表示
    /// 销毁当前战斗后用同一入口以另一模式新建一场新战斗，不得迁移运行中状态
    /// （不变量 23 / 任务包「模式契约」）。
    /// </summary>
    public enum BattleRuntimeMode
    {
        Legacy = 0,
        Shadow = 1,
        New = 2
    }

    /// <summary>
    /// 模式相关常量。
    /// </summary>
    public static class BattleRuntimeModes
    {
        /// <summary>三个模式的冻结枚举顺序（诊断输出与调用矩阵按此顺序）。</summary>
        public static readonly BattleRuntimeMode[] All =
        {
            BattleRuntimeMode.Legacy,
            BattleRuntimeMode.Shadow,
            BattleRuntimeMode.New
        };

        /// <summary>该模式是否启用已登记的 Legacy 从属写入组（不变量 21）。</summary>
        public static bool EnablesLegacyWriterGroup(BattleRuntimeMode mode)
            => mode != BattleRuntimeMode.New;

        /// <summary>该模式是否必须调用 Legacy 顶层推进入口。</summary>
        public static bool CallsLegacyAdvance(BattleRuntimeMode mode)
            => mode == BattleRuntimeMode.Legacy || mode == BattleRuntimeMode.Shadow;

        /// <summary>该模式是否推进新模拟。</summary>
        public static bool AdvancesNewSimulation(BattleRuntimeMode mode)
            => mode == BattleRuntimeMode.Shadow || mode == BattleRuntimeMode.New;

        public static string Describe(BattleRuntimeMode mode)
        {
            switch (mode)
            {
                case BattleRuntimeMode.Legacy: return "Legacy";
                case BattleRuntimeMode.Shadow: return "Shadow";
                case BattleRuntimeMode.New: return "New";
                default: return "Unknown(" + (int)mode + ")";
            }
        }
    }
}
