using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Snapshots
{
    /// <summary>
    /// 面向 Controller（玩家 UI 与 AI）的<strong>只读决策快照</strong>（任务 09「必须产出」17）。
    ///
    /// 与 <see cref="LogicSnapshot"/> <strong>分型</strong>：
    /// <list type="bullet">
    /// <item>它是<strong>按 Controller 过滤</strong>的投影：只公开该控制者<strong>可控制单位</strong>的
    /// <c>Editable</c> 普通计划与可编辑字段；其他 Controller 的普通计划在<strong>锁定并按玩法公开前</strong>
    /// 不出现——<c>ActionSpec</c>、目标、<c>Destination</c> 与精确 Tick 都不泄露
    /// （它们共同由 <see cref="VisiblePlans"/> 表达，见其文档）。</item>
    /// <item>它<strong>不</strong>是 Canonical Snapshot 的别名：<see cref="LogicSnapshot"/> 仍包含
    /// 全部权威状态用于哈希与 Shadow 比较，两者不得合并、也不得互相赋值。</item>
    /// <item>它持有整场唯一的 <see cref="IFactionRelationResolver"/> 实例本身，
    /// <strong>不复制</strong>任何可被 UI/AI 修改的关系矩阵。</item>
    /// <item><see cref="ForController"/> 是<strong>唯一</strong>的按 Controller 过滤入口；
    /// 过滤只改变"看到什么"，不改变任何权威状态，也不可能写出任何东西。</item>
    /// </list>
    /// </summary>
    public sealed class DecisionSnapshot
    {
        private readonly IFactionRelationResolver _factionResolver;
        private readonly IReadOnlyList<ActionPlanSnapshot> _canonicalPlans;
        private readonly IReadOnlyList<UnitId> _controlledUnitIds;

        /// <summary>
        /// Canonical 构造（<strong>不</strong>做任何 Controller 过滤）：<paramref name="plans"/> 是该 Tick
        /// 全部非终态计划，<see cref="VisiblePlans"/> 因此等于全量，也就是"开发者视角"。
        /// 装配方若要给某个 Controller 决策，必须显式调用 <see cref="ForController"/>。
        /// </summary>
        public DecisionSnapshot(
            long tick,
            string rulesVersion,
            string battleDefinitionHash,
            IReadOnlyList<UnitSnapshot> visibleUnits,
            IReadOnlyList<ActionPlanSnapshot> plans,
            long scheduleRevision,
            BattleEndSnapshot battleEnd,
            IFactionRelationResolver factionResolver)
            : this(tick, rulesVersion, battleDefinitionHash, visibleUnits, plans, plans,
                scheduleRevision, battleEnd, factionResolver, default, null, null, null)
        {
        }

        private DecisionSnapshot(
            long tick,
            string rulesVersion,
            string battleDefinitionHash,
            IReadOnlyList<UnitSnapshot> visibleUnits,
            IReadOnlyList<ActionPlanSnapshot> publicPlans,
            IReadOnlyList<ActionPlanSnapshot> ownPlans,
            long scheduleRevision,
            BattleEndSnapshot battleEnd,
            IFactionRelationResolver factionResolver,
            ControllerId controllerId,
            IReadOnlyList<UnitId> controlledUnitIds,
            TurnWindowSnapshot ownWindow,
            BattleDefinition definition)
        {
            Tick = tick;
            RulesVersion = rulesVersion ?? string.Empty;
            BattleDefinitionHash = battleDefinitionHash ?? string.Empty;
            VisibleUnits = Freeze(visibleUnits);
            VisiblePlans = Freeze(publicPlans);
            OwnPlans = Freeze(ownPlans);
            ScheduleRevision = scheduleRevision;
            BattleEnd = battleEnd ?? BattleEndSnapshot.Active();
            _factionResolver = factionResolver
                ?? throw new ProjectHero.Logic.LogicDefinitionException(
                    FactionCodes.FACTION_RELATION_INVARIANT_VIOLATION, "decision snapshot requires the single resolver");

            ControllerId = controllerId;
            _controlledUnitIds = Freeze(controlledUnitIds ?? Array.Empty<UnitId>());
            OwnWindow = ownWindow;
            _canonicalPlans = Freeze(publicPlans);
            Definition = definition;
        }

        private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<T>();
            var copy = new T[source.Count];
            for (int i = 0; i < source.Count; i++) copy[i] = source[i];
            return Array.AsReadOnly(copy);
        }

        public long Tick { get; }
        public string RulesVersion { get; }
        public string BattleDefinitionHash { get; }
        public IReadOnlyList<UnitSnapshot> VisibleUnits { get; }

        /// <summary>
        /// <strong>按玩法公开</strong>的计划（其他 Controller 的 Editable 普通计划<strong>不</strong>在此列）。
        /// 这是 UI/AI 可以据此渲染时间线的集合。
        /// </summary>
        public IReadOnlyList<ActionPlanSnapshot> VisiblePlans { get; }

        /// <summary>该 Controller <strong>自己</strong>的全部非终态计划（含仍为 <c>Editable</c> 者）。</summary>
        public IReadOnlyList<ActionPlanSnapshot> OwnPlans { get; }

        public long ScheduleRevision { get; }
        public BattleEndSnapshot BattleEnd { get; }

        /// <summary>整场唯一的只读阵营关系解析器（引用同一条实例，不是副本）。</summary>
        public IFactionRelationResolver FactionResolver => _factionResolver;

        /// <summary>本快照所面向的控制者（<c>null</c> = Canonical 视角，未按 Controller 过滤）。</summary>
        public ControllerId ControllerId { get; }

        /// <summary>该控制者现在可控制的单位（权威控制权投影；Canonical 视角为空集合）。</summary>
        public IReadOnlyList<UnitId> ControlledUnitIds => _controlledUnitIds;

        /// <summary>该控制者当前窗口的只读快照（<c>null</c> = 没有窗口；Canonical 视角恒为 <c>null</c>）。</summary>
        public TurnWindowSnapshot OwnWindow { get; }

        /// <summary>
        /// 动作定义只读面（与命令层<strong>同一个</strong> <see cref="BattleDefinition"/> 实例）。
        /// 未装配时为 <c>null</c>——那表示"没有动作表"，不是"AI 有一份自己的动作表"。
        /// </summary>
        public BattleDefinition Definition { get; }

        // ================= 只读资格/定义查询（与命令层同源） =================

        public UnitRelation Classify(UnitId sourceUnitId, UnitId targetUnitId)
            => _factionResolver.Classify(sourceUnitId, targetUnitId);

        public bool Allows(TargetRelationMask allowedRelations, UnitId sourceUnitId, UnitId targetUnitId)
            => _factionResolver.Allows(allowedRelations, sourceUnitId, targetUnitId);

        /// <summary>存活单位的 <c>UnitId</c> 集合（按 <c>UnitId</c> 升序）。</summary>
        public IReadOnlyList<UnitId> VisibleUnitIds
        {
            get
            {
                var ids = new List<UnitId>(VisibleUnits.Count);
                for (int i = 0; i < VisibleUnits.Count; i++)
                {
                    UnitSnapshot unit = VisibleUnits[i];
                    if (unit != null && unit.IsAlive) ids.Add(new UnitId(unit.UnitId));
                }
                ids.Sort((a, b) => a.Value.CompareTo(b.Value));
                return ids;
            }
        }

        /// <summary>按 ID 查动作定义（未装配或不存在时返回 <c>null</c>）。</summary>
        public ActionSpec FindAction(ActionSpecId actionSpecId)
        {
            if (actionSpecId.Value == null || Definition == null) return null;
            return Definition.FindAction(actionSpecId);
        }

        /// <summary>
        /// 该单位的 <c>ActionSet</c>（<strong>与命令层同一个定义</strong>；无绑定或未装配时为空集合）。
        /// AI 与玩家的理论动作可用性都来自这个集合，不存在第二份"AI 可用动作表"。
        /// </summary>
        public IReadOnlyList<ActionSpecId> ActionSetOf(UnitId unitId)
        {
            if (Definition == null) return Array.Empty<ActionSpecId>();
            for (int i = 0; i < VisibleUnits.Count; i++)
            {
                UnitSnapshot unit = VisibleUnits[i];
                if (unit == null || unit.UnitId != unitId.Value) continue;
                UnitDefinition definition = Definition.FindUnit(new UnitDefinitionId(unit.DefinitionId ?? string.Empty));
                if (definition == null) break;
                ActionSetDefinition set = Definition.FindActionSet(definition.ActionSetId);
                if (set == null) break;
                return set.ActionSpecIds ?? (IReadOnlyList<ActionSpecId>)Array.Empty<ActionSpecId>();
            }
            return Array.Empty<ActionSpecId>();
        }

        /// <summary>该计划是否<strong>已按玩法公开</strong>（= 反应计划，或状态已离开 <c>Editable</c>）。</summary>
        public static bool IsPubliclyRevealed(ActionPlanSnapshot plan)
        {
            if (plan == null) return false;
            if (plan.Origin == (int)ActionPlanOrigin.Reaction) return true;
            return plan.State != (int)ActionPlanState.Editable;
        }

        /// <summary>
        /// 该控制者自己仍为 <c>Editable</c> 的<strong>普通</strong>计划（按 <c>ActionPlanId</c> 升序）。
        /// <c>null</c> 控制者（Canonical 视角）返回空集合——过滤需要一个明确的主体。
        /// </summary>
        public IReadOnlyList<ActionPlanSnapshot> EditablePlansOf(ControllerId controllerId)
        {
            IReadOnlyList<ActionPlanSnapshot> own = OwnPlansOf(controllerId);
            var editable = new List<ActionPlanSnapshot>(own.Count);
            for (int i = 0; i < own.Count; i++)
            {
                ActionPlanSnapshot plan = own[i];
                if (plan == null) continue;
                if (plan.Origin != (int)ActionPlanOrigin.Ordinary) continue;
                if (plan.State != (int)ActionPlanState.Editable) continue;
                editable.Add(plan);
            }
            editable.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
            return editable;
        }

        /// <summary>
        /// 该控制者自己的全部非终态计划（按 <c>ActionPlanId</c> 升序）。
        /// 只有<strong>本快照自己的控制者</strong>能拿到私有面；其他控制者只能看到已公开者，
        /// 因此"借用别人的 ControllerId 读别人的 Editable 计划"不成立。
        /// </summary>
        public IReadOnlyList<ActionPlanSnapshot> OwnPlansOf(ControllerId controllerId)
        {
            if (controllerId.Value == null) return Array.Empty<ActionPlanSnapshot>();
            if (ControllerId.Value == null ||
                !string.Equals(ControllerId.Value, controllerId.Value, StringComparison.Ordinal))
                return Array.Empty<ActionPlanSnapshot>();
            return OwnPlans;
        }

        // ================= 按 Controller 过滤（唯一入口） =================

        /// <summary>
        /// <strong>按 Controller 过滤</strong>（任务 09「必须产出」17 的唯一口径）。
        ///
        /// 冻结语义：
        /// <list type="number">
        /// <item><strong>公开面</strong>（<see cref="VisiblePlans"/>）= 非终态计划中
        /// "该控制者可控制单位的计划" ∪ "已按玩法公开者（<see cref="IsPubliclyRevealed"/>）"。
        /// 其他 Controller 的 <c>Editable</c> 普通计划被<strong>整条剔除</strong>，
        /// 因此它们的 <c>ActionSpecId</c>、<c>PrimaryTargetUnitId</c>、<c>Destination</c>
        /// 与精确 Tick 都不出现在被过滤的快照里。</item>
        /// <item><strong>私有面</strong>（<see cref="OwnPlans"/>）= 该控制者可控制单位的
        /// <strong>全部</strong>非终态计划（含 <c>Editable</c>），供它编辑自己的时间线。</item>
        /// <item><strong>单位面</strong>（<see cref="VisibleUnits"/>）<strong>不</strong>裁剪：
        /// 单位的存活/位置/血量是玩法公开事实，且 <c>FactionRelationResolver</c> 与候选筛面
        /// 都需要完整单位集合（裁剪会让关系分类随视角漂移，那才是真正的不一致）。</item>
        /// <item><strong>控制者面</strong>（<see cref="ControlledUnitIds"/> / <see cref="OwnWindow"/>）
        /// 只放该控制者自己的控制权投影与自己的窗口快照。</item>
        /// </list>
        /// 它<strong>不</strong>改变 <see cref="ScheduleRevision"/>、不算任何计划、不校验权限：
        /// 展示永远不是授权，确认时命令层仍会重新校验。
        /// </summary>
        /// <param name="controllerId">目标控制者；<c>Value == null</c> 时原样返回本实例（Canonical 视角）。</param>
        /// <param name="controlledUnitIds">该控制者可控制的单位（权威控制权投影；<c>null</c> = 空集合）。</param>
        /// <param name="ownWindow">该控制者当前窗口的只读快照（<c>null</c> = 没有窗口）。</param>
        public DecisionSnapshot ForController(
            ControllerId controllerId,
            IReadOnlyList<UnitId> controlledUnitIds,
            TurnWindowSnapshot ownWindow)
        {
            if (controllerId.Value == null) return this;

            var units = new List<UnitId>(controlledUnitIds ?? Array.Empty<UnitId>());
            units.Sort((a, b) => a.Value.CompareTo(b.Value));

            var publicPlans = new List<ActionPlanSnapshot>(_canonicalPlans.Count);
            var ownPlans = new List<ActionPlanSnapshot>(_canonicalPlans.Count);
            for (int i = 0; i < _canonicalPlans.Count; i++)
            {
                ActionPlanSnapshot plan = _canonicalPlans[i];
                if (plan == null) continue;
                bool mine = Contains(units, plan.OwnerUnitId);
                if (mine)
                {
                    // 自己的非终态计划一律进私有面（含仍为 Editable 者，供它编辑自己的时间线）。
                    ownPlans.Add(plan);
                }
                // "按玩法公开前"任何普通计划都不得出现在公开面——自己的也不行：
                // 公开面是"玩法已经揭示的事实"，未公开的计划字段一个都不该由它承载。
                if (!IsPubliclyRevealed(plan)) continue;
                publicPlans.Add(plan);
            }

            publicPlans.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));
            ownPlans.Sort((a, b) => a.ActionPlanId.CompareTo(b.ActionPlanId));

            return new DecisionSnapshot(
                Tick, RulesVersion, BattleDefinitionHash, VisibleUnits, publicPlans, ownPlans,
                ScheduleRevision, BattleEnd, _factionResolver, controllerId, units, ownWindow, Definition);
        }

        /// <summary>
        /// 装配<b>动作定义只读面</b>（与命令层<strong>同一个</strong> <see cref="BattleDefinition"/> 实例）：
        /// AI 与玩家的理论动作可用性因此来自同一个 <c>ActionSet</c>，不存在第二份动作表。
        /// </summary>
        public DecisionSnapshot WithDefinition(BattleDefinition definition)
        {
            if (definition == null) return this;
            return new DecisionSnapshot(
                Tick, RulesVersion, BattleDefinitionHash, VisibleUnits, VisiblePlans, OwnPlans,
                ScheduleRevision, BattleEnd, _factionResolver, ControllerId, _controlledUnitIds,
                OwnWindow, definition);
        }

        private static bool Contains(IReadOnlyList<UnitId> units, long unitId)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Value == unitId) return true;
            }
            return false;
        }
    }
}
