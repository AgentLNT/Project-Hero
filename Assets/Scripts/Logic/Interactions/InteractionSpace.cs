using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 计划生命周期的<strong>只读值视图</strong>：接触候选与冲突图节点资格的唯一计划事实来源。
    ///
    /// 它刻意只暴露"计划是否存在、是否终态、有效区间、ImpactTick、固定目标"这些
    /// <strong>生命周期</strong>事实，不暴露 <c>Guarding/Blocking/Dodging</c> 状态布尔值、
    /// <c>CanReceiveDirectHit</c>、<c>UnitState</c>、Controller 或玩家标志 —— 这样候选收集
    /// （08-多方仲裁与伤害.md:30、:70、主方案 0.4 目标资格分层）在结构上就无法按状态提前丢弃接触。
    ///
    /// 全部字段为整数/值类型；纯值数据，不引用 UnityEngine。
    /// </summary>
    public sealed record InteractionPlanFacts(
        ActionPlanId ActionPlanId,
        UnitId OwnerUnitId,
        ActionType ActionType,
        long StartTick,
        long EndTick,
        long ImpactTick,
        long ActiveStartTick,
        long ActiveEndTick,
        long TriggerTick,
        UnitId? PrimaryTargetUnitId,
        bool IsTerminal)
    {
        /// <summary>
        /// 节点资格用的有效区间判定：<c>StartTick &lt;= tick &lt;= EndTick</c>（<strong>闭</strong>区间）。
        ///
        /// 为什么右端闭合：攻击计划的 <c>EndTick = ImpactTick + RecoveryTicks</c>（ActionPlan.cs:507）。
        /// 若 <c>RecoveryTicks == 0</c>，<c>EndTick == ImpactTick</c>；半开区间会把恰好落在
        /// <c>ImpactTick</c> 的合法 Intent 判成"不在有效时间区间"，直接违反验收
        /// 「仲裁只消费上游在 ImpactTick 产生的攻击 Intent」。闭区间对所有 RecoveryTicks 取值都不丢合法 Intent。
        /// </summary>
        public bool CoversTick(long tick) => StartTick <= tick && tick <= EndTick;

        /// <summary>Guard 判定用的 <c>[ActiveStartTick, ActiveEndTick)</c>（主方案 0.4 冻结语义）。</summary>
        public bool IsActiveAt(long tick) => ActiveStartTick <= tick && tick < ActiveEndTick;

        /// <summary>Move 判定用的 <c>[StartTick, EndTick)</c>。</summary>
        public bool OccupiesTick(long tick) => StartTick <= tick && tick < EndTick;

        /// <summary>
        /// 从既有计划映射（只读；装配侧调用）。<c>ImpactTick</c> 未解析时为 0，
        /// 由 <see cref="CombatIntentContract"/> 在物化点以稳定码拒绝，而不是在这里猜测。
        /// </summary>
        public static InteractionPlanFacts From(ActionPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            return new InteractionPlanFacts(
                plan.ActionPlanId,
                plan.OwnerUnitId,
                plan.ActionType,
                plan.StartTick,
                plan.EndTick,
                plan.ImpactTick,
                plan.ActiveStartTick,
                plan.ActiveEndTick,
                plan.HasFixedTriggerTick ? plan.TriggerTick : 0L,
                plan.PrimaryTargetUnitId,
                plan.IsTerminal);
        }
    }

    /// <summary>
    /// 单位在本 Tick 的<strong>只读空间占用</strong>：占用三角形集合（doubled coordinates）。
    /// 接触判定只做整数点集相交，绝不读取 Transform、Collider、视觉位置或浮点体积。
    /// </summary>
    public sealed class UnitOccupancy
    {
        private readonly TrianglePoint[] _triangles;

        public UnitOccupancy(UnitId unitId, IReadOnlyList<TrianglePoint> occupiedTriangles, ControllerId controllerId)
        {
            if (!unitId.IsValid)
                throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID, "unitId=0");

            UnitId = unitId;
            _triangles = InteractionCollections.CanonicalTriangles(occupiedTriangles);
            ControllerId = controllerId;
        }

        public UnitId UnitId { get; }

        /// <summary>规范占用点集：按 <c>(X, Y, T)</c> 升序且已去重。</summary>
        public IReadOnlyList<TrianglePoint> OccupiedTriangles => _triangles;

        /// <summary>
        /// <strong>审计字段</strong>：控制者只标识"谁下命令"，与阵营正交。
        /// 任何过滤、连边、共享目标判定或稳定键都<strong>不得</strong>读取它
        /// （08-多方仲裁与伤害.md:62、:409；不变量 30）。
        /// </summary>
        public ControllerId ControllerId { get; }

        /// <summary>
        /// 整数点集相交：攻击区域点集与本单位占用点集是否有公共三角形。
        /// 两侧都已规范升序，用归并扫描（O(n+m)），不使用哈希容器、不使用浮点。
        /// </summary>
        public bool IntersectsCanonical(IReadOnlyList<TrianglePoint> canonicalArea)
        {
            if (canonicalArea == null || canonicalArea.Count == 0 || _triangles.Length == 0) return false;
            int i = 0;
            int j = 0;
            while (i < _triangles.Length && j < canonicalArea.Count)
            {
                int byKey = _triangles[i].CompareTo(canonicalArea[j]);
                if (byKey == 0) return true;
                if (byKey < 0) i++;
                else j++;
            }
            return false;
        }
    }
}
