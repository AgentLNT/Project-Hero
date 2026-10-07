using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>
    /// <strong>区域攻击的真实逻辑威胁候选来源</strong>（任务 08 收尾裁定：补齐
    /// <see cref="IAreaThreatCandidateSource"/> 的生产实现）。
    ///
    /// <para>
    /// <strong>它补的是哪个洞</strong>：任务 05 只定义了接入面，默认实现
    /// <see cref="NoAreaThreatCandidateSource"/> 明确 fail-closed 返回空候选
    /// （<c>ReactionOpportunitySystem.cs:48-54</c>）。<c>CollectDefenders</c> 在非
    /// <see cref="TargetPolicy.PrimaryTargetOnly"/> 策略下<strong>只能</strong>向该来源取候选
    /// （<c>:828</c>）⇒ 空候选 ⇒ <c>NO_REACHABLE_OPTION</c> ⇒ <strong>区域攻击永远不公开任何反应机会</strong>。
    /// 任务 05 交接把"区域威胁来源"留给任务 06/08；任务 06 未交付，因此按
    /// <c>06-交接记录.md:808-810</c>「归属任务 08」在此补齐。
    /// </para>
    ///
    /// <para>
    /// <strong>几何口径 = 与接触候选完全同一套</strong>（<c>InteractionCandidateBuilder</c> /
    /// <c>UnitOccupancy.IntersectsCanonical</c>）：本来源只回答"该攻击的<strong>有效区域</strong>
    /// 是否与<b>某个</b>单位的占用三角形集相交"，不回答命中与否、不回答对手是谁。
    /// 有效区域由<strong>唯一</strong>的区域翻译器产出
    /// （<see cref="CombatIntentFactory.TranslatePatternArea"/>：查表 + 整数平移 + 规范排序去重），
    /// 因此这里<strong>不存在</strong>第二套区域几何。
    /// </para>
    ///
    /// <para>
    /// <strong>它刻意不做的事</strong>：
    /// <list type="bullet">
    /// <item>不读 <c>IsPlayerControlled</c>、<c>ControllerId</c>、<c>UnitState</c>、
    /// <c>CanReceiveDirectHit</c> 或任何"敌我/防御"提示位；</item>
    /// <item>不按阵营过滤 —— 阵营与目标资格的唯一判据是权威关系解析器 + 该攻击自己的
    /// <c>AllowedTargetRelations</c>，由 <c>CollectDefenders</c> 在拿到候选<strong>之后</strong>统一施加
    /// （候选收集因此不可能"提前替关系判定"或漏掉合法目标）；</item>
    /// <item>不排除攻击者自己 —— 自伤资格由同一个关系掩码决定，不在几何层特判；</item>
    /// <item>不用 <c>HashSet</c>/<c>Dictionary</c> 枚举顺序，也不使用浮点：占用集合取自
    /// <c>LogicGrid</c> 的稳定注册顺序，输出按 <c>UnitId</c> 升序（系统还会再规范化一次）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>锚点/朝向的读取时机</strong>：机会在来源攻击<strong>启动（阶段 7 原子提交）</strong>时公开。
    /// 普通攻击计划在锁定后不再移动其锚点/朝向，故 telegraph 时刻读到的
    /// <c>LogicGrid</c> 投影就是结算时刻的同一份几何。这与阶段 8 物化 Intent 时
    /// "锚点/朝向的唯一来源是 <c>LogicGrid</c>，不回落运行时镜像"是同一条纪律。
    /// 空间权威查不到拥有者（未注册/已注销）⇒ 返回空候选：这是
    /// <strong>fail-closed</strong>，绝不猜一个几何；死者本就无法被当成本 Tick 的合法威胁。
    /// </para>
    ///
    /// <para>
    /// 全部依赖都是"活投影"（定义查询 + 本场 <c>LogicGrid</c> + 注册顺序），因此本对象可以
    /// 在构造期绑定、跨 Tick 复用，不会持有一份会过期的空间快照。
    /// </para>
    /// </summary>
    public sealed class GridAreaThreatCandidateSource : IAreaThreatCandidateSource
    {
        public const string AREA_THREAT_AREA_INVALID = "AREA_THREAT_AREA_INVALID";

        private readonly BattleDefinition _definition;
        private readonly LogicGrid _grid;
        private readonly Func<IReadOnlyList<UnitId>> _registeredUnitsOrdered;

        public GridAreaThreatCandidateSource(
            BattleDefinition definition,
            LogicGrid grid,
            Func<IReadOnlyList<UnitId>> registeredUnitsOrdered)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            _registeredUnitsOrdered =
                registeredUnitsOrdered ?? throw new ArgumentNullException(nameof(registeredUnitsOrdered));
        }

        /// <summary>
        /// 返回"该攻击的有效区域与本单位占用三角集相交"的全部<strong>已注册</strong>单位
        /// （含攻击者自己，由调用方的关系掩码决定它是否被接受），按 <c>UnitId</c> 升序、
        /// 不含重复项。
        /// </summary>
        public IReadOnlyList<UnitId> CandidatesFor(ActionPlan sourcePlan, long telegraphTick)
        {
            if (sourcePlan == null) return Array.Empty<UnitId>();

            ActionSpec spec = _definition.FindAction(sourcePlan.ActionSpecId);
            if (spec == null || !(spec.Payload is AttackPayloadSpec attack)) return Array.Empty<UnitId>();
            if (attack.Pattern == null) return Array.Empty<UnitId>();

            // 空间权威查不到拥有者 ⇒ fail-closed 空候选（不猜几何，见类型注释）。
            if (!_grid.TryGetAnchor(sourcePlan.OwnerUnitId, out GridPoint anchor)) return Array.Empty<UnitId>();
            if (!_grid.TryGetFacing(sourcePlan.OwnerUnitId, out GridDirection facing)) return Array.Empty<UnitId>();

            TrianglePoint[] area = CombatIntentFactory.TranslatePatternArea(attack.Pattern, facing, anchor);
            if (area.Length == 0)
            {
                throw new LogicDefinitionException(AREA_THREAT_AREA_INVALID,
                    "empty area plan=" + sourcePlan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            }

            IReadOnlyList<UnitId> units = _registeredUnitsOrdered();
            if (units == null || units.Count == 0) return Array.Empty<UnitId>();

            var candidates = new List<UnitId>(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                UnitId unitId = units[i];
                if (!unitId.IsValid) continue;

                IReadOnlyList<TrianglePoint> occupied = _grid.TrianglesOf(unitId);
                if (occupied == null || occupied.Count == 0) continue;
                if (!IntersectsCanonicalArea(occupied, area)) continue;

                AddUniqueOrdered(candidates, unitId);
            }
            return candidates;
        }

        /// <summary>
        /// 整数点集相交（与 <c>UnitOccupancy.IntersectsCanonical</c> 同一算法）：
        /// 两侧都已规范升序，用归并扫描，<strong>不</strong>使用哈希容器、<strong>不</strong>使用浮点。
        /// </summary>
        private static bool IntersectsCanonicalArea(
            IReadOnlyList<TrianglePoint> sortedUnit, TrianglePoint[] sortedArea)
        {
            int i = 0;
            int j = 0;
            while (i < sortedUnit.Count && j < sortedArea.Length)
            {
                int byKey = sortedUnit[i].CompareTo(sortedArea[j]);
                if (byKey == 0) return true;
                if (byKey < 0) i++;
                else j++;
            }
            return false;
        }

        private static void AddUniqueOrdered(List<UnitId> ordered, UnitId unitId)
        {
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                if (ordered[i].Value < unitId.Value) break;
                if (ordered[i].Value == unitId.Value) return;
            }

            int insertAt = ordered.Count;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Value > unitId.Value)
                {
                    insertAt = i;
                    break;
                }
            }
            ordered.Insert(insertAt, unitId);
        }
    }
}
