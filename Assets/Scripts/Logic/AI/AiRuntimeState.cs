using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// 一条已公开反应选项的<strong>只读投影</strong>（Block/Dodge）。
    ///
    /// 它是 AI 层拥有的值对象：AI <strong>不</strong>引用机会系统的运行时类型，
    /// 因此"AI 自己创建一个机会/选项/反应计划"在类型上不可能；它只能看到
    /// 本投影并在选中后产出一条标准 <see cref="CommandRequest"/>（Reaction scope）。
    /// </summary>
    public sealed record AiReactionOptionView(
        ReactionOpportunityId ReactionOpportunityId,
        ActionSpecId ActionSpecId,
        ReactionCommandKind ReactionKind,
        long ResponseDeadlineTick,
        bool IsPublished,
        bool IsOpen,
        IReadOnlyList<GridPoint> PublishedDodgeDestinations)
    {
        /// <summary>本选项在该时刻是否对 AI 可用（已公开 + 仍开放）。</summary>
        public bool IsAvailableAt(long tick) => IsPublished && IsOpen && tick <= ResponseDeadlineTick;

        /// <summary><c>null</c> = 不使用目的格（Block）。</summary>
        public GridPoint? SelectedDestination { get; init; }

        /// <summary>目的格资格（由 Logic 公布面在构造时判定，AI 只是读它）。</summary>
        public AiReactionDestinationEligibility DestinationEligibility { get; init; }
            = AiReactionDestinationEligibility.NotApplicable;

        /// <summary>
        /// 稳定排序键：<c>ActionSpecId(Ordinal) → ReactionKind → ResponseDeadlineTick</c>。
        /// 它<strong>不含</strong>候选集合的枚举序号：原始顺序绝不成为末级键。
        /// </summary>
        public int CompareStableKeyTo(AiReactionOptionView other)
        {
            if (other == null) return 1;
            int bySpec = string.CompareOrdinal(ActionSpecId.Value, other.ActionSpecId.Value);
            if (bySpec != 0) return bySpec;
            int byKind = ((int)ReactionKind).CompareTo((int)other.ReactionKind);
            if (byKind != 0) return byKind;
            return ResponseDeadlineTick.CompareTo(other.ResponseDeadlineTick);
        }

        public override string ToString()
            => (ActionSpecId.Value ?? "<null>") + "/" + ReactionKind + "@" +
               ResponseDeadlineTick.ToString(CultureInfo.InvariantCulture);
    }
    /// <summary>
    /// <strong>按格键的 Dodge 候选公布</strong>（Logic → AI 的只读投影）。
    ///
    /// 首版口径（必须逐字遵守，不得放宽）：
    /// <list type="bullet">
    /// <item>它<strong>不是</strong>路径规划器，也<strong>不</strong>做任何权重/距离估算：
    /// 候选格只来自权威 <c>LogicGrid.GetNeighborsOrdered</c> 的规范 12 向序列，
    /// 并逐个经任务 06 的<strong>同一个</strong> <see cref="Timeline.IMovementPathCalculator"/>
    /// 求证"逻辑上确实可达"；因此"AI 自己算一条路线"在实现上不存在。</item>
    /// <item>目的格是否合法（占位、越界、预留冲突、位移允许）的<strong>最终</strong>判定仍在
    /// 命令处理时由任务 06 的目的格预留权威执行：本投影只缩小候选面，绝不放行。</item>
    /// <item><see cref="Candidates"/> 为空 = 当前 Logic 面孔下没有可公布的 Dodge 目的格
    /// （fail-closed：AI 于是不会提出 Dodge，而不是随便挑一格）。</item>
    /// </list>
    /// </summary>
    public sealed record AiDodgeDestinationView(
        GridPoint Origin,
        IReadOnlyList<GridPoint> Candidates);

    /// <summary>
    /// 一条<strong>已公开</strong>反应机会的只读投影：防御者、来源攻击计划、ImpactTick 与逐选项截止。
    /// 它是 AI 决定"Block/Dodge/不响应"的唯一输入面。
    /// </summary>
    public sealed record AiOpportunityView(
        ReactionOpportunityId ReactionOpportunityId,
        UnitId DefenderUnitId,
        ActionPlanId SourceAttackPlanId,
        long TriggerTick,
        IReadOnlyList<AiReactionOptionView> Options)
    {
        /// <summary>
        /// 该防御者的 Dodge 目的格候选（<c>null</c> = 本机会没有任何已公布目的格）。
        /// 它由装配方在构建投影时按格键实测可达性后填入，AI <strong>不</strong>再自行计算。
        /// </summary>
        public AiDodgeDestinationView DodgeDestinations { get; init; }

        /// <summary>本机会在 <paramref name="tick"/> 上对 <paramref name="controllerId"/> 是否可用。</summary>
        public bool IsOfferedTo(IReadOnlyList<UnitId> controlledUnits, long tick)
        {
            if (controlledUnits == null || controlledUnits.Count == 0) return false;
            for (int i = 0; i < controlledUnits.Count; i++)
            {
                if (controlledUnits[i].Value != DefenderUnitId.Value) continue;
                for (int j = 0; j < (Options?.Count ?? 0); j++)
                {
                    if (Options[j] != null && Options[j].IsAvailableAt(tick)) return true;
                }
                return false;
            }
            return false;
        }
    }

    /// <summary>
    /// <strong>已公开反应机会来源</strong>（Logic → AI 的只读投影端口）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>实现<strong>只投影</strong>机会系统的公开事实（开放状态、公开标志、逐选项截止、
    /// Dodge 的<strong>已公布逻辑目的格</strong>），<strong>不</strong>让 AI 看到"当前最近攻击"
    /// 这类猜测来源，也不返回未公开的选项；</item>
    /// <item>本投影<strong>不</strong>暴露 <c>ReactionOpportunitySystem</c> 本身，因此
    /// "AI 直接调 <c>TryAcceptById</c>"在类型上不可达；</item>
    /// <item>排序由实现保证（<c>ReactionOpportunityId</c> 升序）；AI 侧仍会按稳定键重排一次，
    /// 原始枚举序不参与任何决策。</item>
    /// </list>
    /// </summary>
    public interface IAiReactionOpportunitySource
    {
        /// <summary>该防御者当前<strong>已公开且仍开放</strong>的机会及其选项（稳定顺序；无则空集合）。</summary>
        IReadOnlyList<AiOpportunityView> PublishedOpportunitiesFor(UnitId defenderUnitId, long tick);
    }

    /// <summary>
    /// <strong>AI 未来决策状态</strong>（任务 09 产出 15）：一个 AI 控制者的规范化运行态。
    ///
    /// 全部字段都必须进入规范化快照与哈希——回放时 AI <strong>不</strong>载入记录的命令，
    /// 而是由相同初始输入与 RNG 从 Tick 0 重新生成；任何影响未来决策的状态若不在本记录里，
    /// 重演就会分叉。
    /// </summary>
    public sealed record AiControllerRuntimeState(
        string ControllerId,
        long DecisionCount,
        long LastDecisionTick,
        long NextThinkTick,
        RngSnapshot Rng)
    {
        public override string ToString()
            => (ControllerId ?? "<null>") +
               " decisions=" + DecisionCount.ToString(CultureInfo.InvariantCulture) +
               " lastDecision=" + LastDecisionTick.ToString(CultureInfo.InvariantCulture) +
               " nextThink=" + NextThinkTick.ToString(CultureInfo.InvariantCulture) +
               " rng=" + (Rng == null ? "<null>" : Rng.ToString());
    }

    /// <summary>
    /// <strong>AI 运行态来源</strong>（AI → 模拟的快照投影端口）。
    ///
    /// 它只暴露<strong>只读</strong>状态：集合按 <c>ControllerId</c>（Ordinal）升序，
    /// 不含任何写入入口，因此"UI/驱动器现场改 AI 状态"在类型上不存在。
    /// </summary>
    public interface IAiRuntimeStateSource
    {
        /// <summary>按 <c>ControllerId</c>（Ordinal）升序的全部 AI 控制者运行态（无则空集合）。</summary>
        IReadOnlyList<AiControllerRuntimeState> CaptureRuntimeStatesOrdered();
    }
}
