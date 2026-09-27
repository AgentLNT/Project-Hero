using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Movement
{
    /// <summary>
    /// <strong>任务 06 的真实移动链路径计算器</strong>：把唯一的纯逻辑
    /// <see cref="LogicPathfinder"/> 接到任务 05 冻结的 <see cref="IMovementPathCalculator"/> 端口上。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>预测起点 = 该单位时间线上"在 <c>newStartTick</c> 之前结束"的最后一个移动族计划的
    /// 目的格；没有这样的前序计划时才回落到该单位当前<strong>权威逻辑格</strong>；</item>
    /// <item><strong>Locked/Running/终态</strong>的 Move 一律返回 <c>null</c>（"无需重算"）：
    /// Locked 之后只读取冻结结果，绝不按新路径输入或视觉速度重采样；</item>
    /// <item>MoveSpeed <strong>不进入</strong>寻路：本类只产出
    /// <see cref="MovementPathProjection"/>（边数 + 路径权重单位），
    /// 每段时长由 <c>ActionPlan.ResolvedBaseStepTicks</c> 在消费端相乘；</item>
    /// <item>寻路失败<strong>不</strong>返回 <c>null</c>：以寻路的稳定失败码抛出，
    /// 因此"无路/越界/上限/溢出"都会令整批排程事务失败且零局部写入。</item>
    /// </list>
    /// </summary>
    public sealed class LogicGridMovementPathCalculator : IMovementPathCalculator
    {
        private readonly LogicGrid _grid;
        private readonly ActionScheduleAuthority _authority;
        private readonly LogicPathfinder _pathfinder;

        public LogicGridMovementPathCalculator(
            LogicGrid grid, ActionScheduleAuthority authority, PathCostRules costRules, PathSearchRules searchRules)
        {
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            CostRules = costRules ?? throw new ArgumentNullException(nameof(costRules));
            SearchRules = searchRules ?? throw new ArgumentNullException(nameof(searchRules));
            _pathfinder = new LogicPathfinder(grid, CostRules, SearchRules);
        }

        public LogicGrid Grid => _grid;

        public PathCostRules CostRules { get; }

        public PathSearchRules SearchRules { get; }

        public LogicPathfinder Pathfinder => _pathfinder;

        public MovementPathProjection Recompute(ActionPlan plan, long newStartTick, long deltaTicks)
        {
            if (plan == null) return null;
            if (!plan.IsOrdinaryMove) return null;
            if (!plan.IsEditable) return null;         // Locked/Running/终态：冻结，不重算

            PathSearchResult result = FindPathFor(plan, newStartTick);
            if (!result.Succeeded)
                throw new LogicDefinitionException(result.FailureCode, Describe(plan, newStartTick));

            return new MovementPathProjection(result.EdgeCount, result.PathWeightUnits);
        }

        /// <summary>
        /// 为计划求一条**纯候选**路径（<strong>只读</strong>：不写任何权威状态、不分配 ID）。
        /// 显式编辑、系统自动延期与只读预览共用本入口。
        /// </summary>
        public PathSearchResult FindPathFor(ActionPlan plan, long startTick)
        {
            if (plan == null) return PathSearchResult.FailedAtPreflight(ScheduleCodes.SCHEDULE_OPERATION_INVALID);
            if (!plan.Destination.HasValue)
                return PathSearchResult.FailedAtPreflight(PathSearchCodes.PATH_INVALID_DESTINATION);

            GridPoint from = ProjectedOriginOf(plan, startTick);
            return _pathfinder.FindPath(from, plan.Destination.Value, plan.OwnerUnitId, plan.ActionPlanId);
        }

        /// <summary>
        /// 计划在其 <paramref name="startTick"/> 上的预测起点。
        /// 只读：不修改任何计划、不分配 ID、不推进修订号。
        /// </summary>
        public GridPoint ProjectedOriginOf(ActionPlan plan, long startTick)
        {
            if (plan == null) return default;
            if (!_grid.TryGetAnchor(plan.OwnerUnitId, out GridPoint anchor))
                throw new LogicDefinitionException(LogicGridCodes.LOGIC_GRID_UNIT_UNKNOWN,
                    plan.OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture));

            ActorLane lane = _authority.FindLane(plan.OwnerUnitId);
            if (lane == null) return anchor;

            IReadOnlyList<ActionPlan> plans = lane.Plans;
            ActionPlan best = null;
            for (int i = 0; i < plans.Count; i++)
            {
                ActionPlan candidate = plans[i];
                if (candidate == null) continue;
                if (candidate.ActionPlanId.Value == plan.ActionPlanId.Value) continue;
                if (!candidate.IsMovementFamily) continue;
                if (candidate.IsTerminal) continue;
                if (!candidate.Destination.HasValue) continue;
                if (candidate.EndTick > startTick) continue;
                if (best == null || candidate.EndTick > best.EndTick ||
                    (candidate.EndTick == best.EndTick &&
                     candidate.ActionPlanId.Value > best.ActionPlanId.Value))
                {
                    best = candidate;
                }
            }

            return best?.Destination ?? anchor;
        }

        private static string Describe(ActionPlan plan, long startTick)
            => "plan=" + (plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture)) +
               " unit=" + plan.OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture) +
               " startTick=" + startTick.ToString(CultureInfo.InvariantCulture) +
               " destination=" + plan.Destination;
    }
}
