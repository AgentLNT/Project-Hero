using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>窗口关闭原因（任务 07 会追加新值；数值参与快照哈希，不得重排）。</summary>
    public enum TurnWindowCloseReason
    {
        /// <summary>拥有者或规则显式请求关闭。</summary>
        OwnerRequested = 0,

        /// <summary>战斗进入终态：当前窗口以该原因关闭。</summary>
        BattleEnded = 1
    }

    /// <summary>一个待打开的提交窗口请求（阶段 3 消费）。</summary>
    public sealed record WindowOpenRequest(UnitId OwnerUnitId, int BudgetTicks);

    /// <summary>
    /// 窗口排程扩展点（阶段 3 / 17）。任务 03 的默认实现是"不排定任何窗口"；
    /// 任务 07 用真实 <c>TurnWindowManager</c> 替换它。
    ///
    /// 窗口<strong>只</strong>控制提交新动作的权限和预算：它不拥有动作，也不是动作执行或结算边界
    /// （00 号规则 2）。因此这里没有、也不允许出现"窗口内含计划集合"的结构。
    /// </summary>
    public interface ITurnWindowSchedule
    {
        /// <summary>本 Tick 应打开的窗口；null 表示没有。</summary>
        WindowOpenRequest TryOpenDue(long tick);

        /// <summary>本 Tick 是否正式关闭当前窗口。</summary>
        bool ShouldCloseCurrentWindow(long tick);
    }

    /// <summary>默认窗口排程：永不打开、永不关闭（任务 07 接入真实实现）。</summary>
    public sealed class NoTurnWindowSchedule : ITurnWindowSchedule
    {
        public static readonly NoTurnWindowSchedule Instance = new NoTurnWindowSchedule();

        public WindowOpenRequest TryOpenDue(long tick) => null;

        public bool ShouldCloseCurrentWindow(long tick) => false;
    }

    /// <summary>运行期窗口状态（只描述权限边界，不拥有任何计划）。</summary>
    internal sealed class TurnWindowState
    {
        public WindowId WindowId;
        public UnitId OwnerUnitId;
        public long OpenedAtTick;
        public int BudgetTicks;
        public bool CloseRequested;
        public bool IsOpen;

        public void Clear()
        {
            WindowId = default;
            OwnerUnitId = default;
            OpenedAtTick = -1L;
            BudgetTicks = 0;
            CloseRequested = false;
            IsOpen = false;
        }
    }
}
