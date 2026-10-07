using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 冲突图与接触候选的<strong>纯值输入</strong>（冻结清单 §3.2）。
    ///
    /// 全部字段都是本 Tick 一次性冻结的只读事实：阶段 8 冻结的 Intent、阶段 9 产出的
    /// Dodge 提交前/后两个统一只读空间快照、以及唯一阵营关系解析器。
    /// 构图与求解阶段<strong>不再访问</strong> LogicGrid、计划索引、单位状态或事件队列。
    ///
    /// 注意：这里<strong>没有</strong> Controller 之外的任何"敌我/防御"提示位，
    /// 也没有 <c>Guarding/Blocking/Dodging</c> 状态或 <c>CanReceiveDirectHit</c>：
    /// 候选收集在结构上就无法按状态提前丢弃接触（主方案 0.4 目标资格分层）。
    /// </summary>
    public sealed class ConflictGraphInput
    {
        public ConflictGraphInput(
            long tick,
            IReadOnlyList<CombatIntent> frozenIntents,
            IReadOnlyList<InteractionPlanFacts> plans,
            IReadOnlyList<UnitOccupancy> unitsBefore,
            IReadOnlyList<UnitOccupancy> unitsAfter,
            IFactionRelationResolver factions)
        {
            Tick = tick;
            FrozenIntents = frozenIntents ?? Array.Empty<CombatIntent>();
            Plans = plans ?? Array.Empty<InteractionPlanFacts>();
            UnitsBefore = unitsBefore ?? Array.Empty<UnitOccupancy>();
            UnitsAfter = unitsAfter ?? Array.Empty<UnitOccupancy>();
            Factions = factions;
        }

        /// <summary>本 Tick。</summary>
        public long Tick { get; }

        /// <summary>阶段 8 冻结的本 Tick Intent（<strong>不得</strong>被追溯删除）。</summary>
        public IReadOnlyList<CombatIntent> FrozenIntents { get; }

        /// <summary>计划生命周期只读视图（节点资格与对手计划判定的唯一来源）。</summary>
        public IReadOnlyList<InteractionPlanFacts> Plans { get; }

        /// <summary>Dodge 提交前的统一只读空间快照（旧格）。</summary>
        public IReadOnlyList<UnitOccupancy> UnitsBefore { get; }

        /// <summary>全部 Dodge 原子位置提交完成后的空间快照（新格）。</summary>
        public IReadOnlyList<UnitOccupancy> UnitsAfter { get; }

        /// <summary>Logic 中唯一的阵营关系解析器（不得按 Controller/玩家标志推断敌我）。</summary>
        public IFactionRelationResolver Factions { get; }
    }

    /// <summary>
    /// 接触候选集合：入图节点（规范排序）+ 按 <see cref="ContactKey"/> 去重排序的带类型接触。
    /// 不可变。
    /// </summary>
    public sealed class InteractionCandidateSet
    {
        private readonly CombatIntent[] _nodes;
        private readonly InteractionContact[] _contacts;

        internal InteractionCandidateSet(long tick, CombatIntent[] nodes, InteractionContact[] contacts)
        {
            Tick = tick;
            _nodes = nodes ?? Array.Empty<CombatIntent>();
            _contacts = contacts ?? Array.Empty<InteractionContact>();
        }

        public long Tick { get; }

        /// <summary>入图节点：只含本 Tick <strong>有效</strong> Intent，按 <c>IntentSequence</c> 升序。</summary>
        public IReadOnlyList<CombatIntent> Nodes => _nodes;

        /// <summary>全部带类型接触，按 <see cref="ContactKey"/> 升序且已按键去重。</summary>
        public IReadOnlyList<InteractionContact> Contacts => _contacts;

        /// <summary>节点下标查询（按 <c>ActionPlanId</c>，供装配与测试使用）。</summary>
        public int NodeIndexOf(ActionPlanId actionPlanId)
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                if (_nodes[i].ActionPlanId == actionPlanId) return i;
            }
            return -1;
        }

        /// <summary>按接触键升序查找接触（二分）。</summary>
        public int ContactIndexOf(ContactKey key)
        {
            int low = 0;
            int high = _contacts.Length - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                int byKey = _contacts[mid].Key.CompareTo(key);
                if (byKey == 0) return mid;
                if (byKey < 0) low = mid + 1;
                else high = mid - 1;
            }
            return -1;
        }
    }
}
