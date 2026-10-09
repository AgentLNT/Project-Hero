using System;

namespace ProjectHero.Core.Compatibility.Runtime.Input
{
    /// <summary>
    /// 视图时间轴（秒）↔ Logic 绝对 Tick 的<strong>唯一</strong>换算点
    /// （任务 09 / B2 范围 §3.3）。
    ///
    /// <para>
    /// <strong>为什么必须唯一</strong>：<c>TimelineEditorUI</c> 的整个渲染面是秒
    /// （<c>PixelsPerSecond</c>、<c>BattleTimeline.CurrentTime</c>、<c>StartTimeAbs</c>），
    /// 而 <c>ViewInputController.BeginPlacement</c> / <c>BeginReorder</c> 只接受整数 Tick。
    /// 换算散落在各处（"这里乘 60、那里除 60"）时，任何一处取整口径不同都会让
    /// "用户看到的位点"与"提交上去的位点"悄悄错开一格——这类偏差在时间线 UI 上
    /// 无法用眼睛发现，却会被 Logic 以位点冲突的形式拒绝。因此本类是全工程
    /// <strong>唯一</strong>做该换算的地方。
    /// </para>
    ///
    /// <para>
    /// <strong>tick 率来自哪里</strong>：由宿主在构造时显式注入。视图层的调用方
    /// （<c>TimelineEditorUI</c>）注入的是旧时间线的<strong>唯一常量</strong>
    /// <c>BattleTimeline.TicksPerSecond</c>，本类自己<strong>不</strong>再写一个 60，
    /// 因此"tick 率"在整个视图层只有一个来源。
    /// </para>
    ///
    /// <para>
    /// <strong>为什么可以引用 Logic 端口</strong>：<see cref="CurrentTick"/> 取自权威只读快照
    /// （<see cref="IViewLogicPort.CurrentTick"/>），而不是旧时间线的本地时钟；
    /// "相对当前时刻的偏移"（<see cref="TickAtRelativeSeconds"/>）因此总是相对
    /// <strong>权威现在</strong>表达，不依赖两条时钟在绝对意义上是否已经对齐。
    /// </para>
    ///
    /// <para>
    /// <strong>刻意的取整口径</strong>：四舍五入到最近 Tick（<c>MidpointRounding.AwayFromZero</c>），
    /// 负值与 <c>NaN</c>/无穷一律夹到 Tick <c>0</c>——Tick 上没有负数，
    /// 也不存在"把非法输入悄悄顺延到当前 Tick"的分支（任务包禁止事项）。
    /// </para>
    /// </summary>
    public sealed class ViewTickConverter
    {
        private readonly IViewLogicPort _logic;

        public ViewTickConverter(IViewLogicPort logic, int ticksPerSecond)
        {
            _logic = logic;
            TicksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : 0;
        }

        /// <summary>注入的 tick 率（&lt;= 0 表示未配置，此时一切换算按不可用处理）。</summary>
        public int TicksPerSecond { get; }

        /// <summary>换算可用性：tick 率必须已配置。不可用时所有换算返回 <c>0</c>（绝不猜）。</summary>
        public bool IsAvailable => TicksPerSecond > 0;

        /// <summary>权威当前 Tick（含 <c>0</c>；无端口时为 <c>0</c>）。</summary>
        public long CurrentTick => _logic != null ? _logic.CurrentTick : 0L;

        /// <summary>设备输入的默认目标 Tick（<c>CurrentTick + 1</c>，与命令时间桶同一口径）。</summary>
        public long NextTick => CurrentTick + 1L;

        /// <summary>视图时间轴的绝对秒 → 绝对 Tick。</summary>
        public long TickAtAbsoluteSeconds(double seconds)
        {
            if (!IsAvailable) return 0L;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0L;
            double ticks = seconds * TicksPerSecond;
            if (ticks <= 0d) return 0L;
            return (long)Math.Round(ticks, MidpointRounding.AwayFromZero);
        }

        /// <summary>相对<strong>权威现在</strong>的秒偏移 → 绝对 Tick。</summary>
        public long TickAtRelativeSeconds(double offsetSeconds)
        {
            if (!IsAvailable) return 0L;
            if (double.IsNaN(offsetSeconds) || double.IsInfinity(offsetSeconds)) return CurrentTick;
            return CurrentTick + TickAtAbsoluteSeconds(offsetSeconds);
        }

        /// <summary>绝对 Tick → 视图时间轴的绝对秒（渲染用；与 <see cref="TickAtAbsoluteSeconds"/> 互逆）。</summary>
        public double SecondsAtTick(long tick)
        {
            if (!IsAvailable || tick <= 0L) return 0d;
            return tick / (double)TicksPerSecond;
        }
    }
}
