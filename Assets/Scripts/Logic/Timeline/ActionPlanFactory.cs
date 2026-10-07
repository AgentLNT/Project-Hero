using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 计划所有者（单位）的<strong>只读事实</strong>：一次性时序采样与目标关系校验的唯一输入。
    /// 它<strong>不</strong>含 Controller、玩家标志或任何表现字段。
    /// </summary>
    public sealed record ActionPlanOwnerFacts(
        UnitId UnitId,
        FactionId FactionId,
        float ActionSpeed,
        float MoveSpeed,
        ActionSetId ActionSetId,
        bool IsAlive);

    /// <summary>
    /// 计划所有者事实来源。实现方只回答只读事实，<strong>不得</strong>写状态、不得改计划。
    /// </summary>
    public interface IActionPlanFactsSource
    {
        bool TryGetOwnerFacts(UnitId unitId, out ActionPlanOwnerFacts facts);
    }

    /// <summary>
    /// 普通计划的创建请求（任务包「必须产出」1 的字段集）。
    /// <see cref="StartTick"/> 是<strong>请求</strong>起点；权威投影由排程事务决定。
    /// </summary>
    public sealed record OrdinaryPlanRequest(
        UnitId OwnerUnitId,
        ActionSpecId ActionSpecId,
        GridDirection Facing,
        UnitId? PrimaryTargetUnitId,
        GridPoint? Destination,
        WindowId? SubmittedWindowId,
        long StartTick,
        int PathEdgeCount = 0,
        int PathWeightUnits = 0);

    /// <summary>
    /// 反应计划的创建请求（只能由 <c>ReactionPlanner</c> 构造）。
    /// <strong>不提供</strong> <c>TriggerTick</c>：它由来源攻击的 <c>ImpactTick</c> 唯一推导。
    /// </summary>
    public sealed record ReactionPlanRequest(
        UnitId OwnerUnitId,
        ActionSpecId ActionSpecId,
        GridDirection Facing,
        GridPoint? Destination,
        ReactionOpportunityId ReactionOpportunityId,
        ActionPlanId SourceThreatPlanId,
        long TriggerTick,
        long ResponseDeadlineTick,
        int PathEdgeCount = 0,
        int PathWeightUnits = 0);

    /// <summary>计划创建结果（结构化；失败<strong>不</strong>分配 ID、不写注册表）。</summary>
    public sealed record ActionPlanCreationResult(ActionPlan Plan, string RejectionCode)
    {
        public bool Succeeded => Plan != null;

        public static ActionPlanCreationResult Rejected(string code) => new ActionPlanCreationResult(null, code);
    }

    /// <summary>
    /// <c>ActionPlan</c> 工厂（任务包「工作步骤」1）。
    ///
    /// 契约：
    /// <list type="bullet">
    /// <item><strong>普通计划</strong>：创建即获得稳定 <see cref="ActionPlanId"/> 并进入
    /// <c>Editable</c>；首次进入 Editable 时<strong>一次性</strong>解析速度相关相对时序
    /// （Attack 的 <c>ResolvedWindupTicks</c>、Move 的 <c>ResolvedBaseStepTicks</c>）。
    /// 这里<strong>没有</strong> DraftPlan/ScheduledAction，也不存在第二个计划对象。</item>
    /// <item><strong>反应计划</strong>：创建时解析固定时序、按
    /// <c>StartTick = TriggerTick - ReactionWindupTicks</c> 建立固定区间，并<strong>直接 Locked</strong>；
    /// <c>SubmittedWindowId</c> 恒为空、<c>BudgetCostTicks = 0</c>。</item>
    /// <item><strong>PrimaryTarget 权威校验</strong>：创建时就必须满足 Attack 载荷的
    /// <c>AllowedTargetRelations</c> 与唯一 <see cref="IFactionRelationResolver"/>；
    /// 不合法即<strong>拒绝创建</strong>（永不改选目标）。</item>
    /// <item>工厂<strong>不</strong>写 Lane、不改 ScheduleRevision、不分配机会 ID：
    /// 那些都属于排程事务与反应系统的职责。</item>
    /// </list>
    /// </summary>
    public sealed class ActionPlanFactory
    {
        private readonly BattleDefinition _definition;
        private readonly IFactionRelationResolver _factions;
        private readonly IActionPlanFactsSource _facts;
        private readonly LogicIdGenerator _ids;

        public ActionPlanFactory(
            BattleDefinition definition,
            IFactionRelationResolver factions,
            IActionPlanFactsSource facts,
            LogicIdGenerator ids)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _factions = factions ?? throw new ArgumentNullException(nameof(factions));
            _facts = facts ?? throw new ArgumentNullException(nameof(facts));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        }

        public BattleRules Rules => _definition.Rules;

        /// <summary>
        /// 只读事实查询（<c>ReactionOpportunitySystem</c> 用它取得防御者候选的阵营/存活/
        /// ActionSet 归属）。它<strong>不</strong>写任何状态，也不暴露事实源实例本身。
        /// </summary>
        public bool TryGetOwnerFacts(UnitId unitId, out ActionPlanOwnerFacts facts)
            => _facts.TryGetOwnerFacts(unitId, out facts);

        /// <summary>权威定义（只读；反应候选需要动作集合与 ReactionRules）。</summary>
        public BattleDefinition Definition => _definition;

        /// <summary>创建普通计划。失败时返回稳定拒绝码且不消耗 <c>ActionPlanId</c>。</summary>
        public ActionPlanCreationResult TryCreateOrdinary(OrdinaryPlanRequest request, long createdAtTick)
            => TryCreateOrdinary(request, createdAtTick, default);

        /// <summary>
        /// 创建普通计划，并使用<strong>调用方预先准备</strong>的 <paramref name="reservedPlanId"/>。
        ///
        /// 用途（排程事务）：事务先按"下一个待分配值"准备候选计划以便求值，
        /// 但<strong>不</strong>推进 ID 计数器；只有全部校验通过后的提交点才真正占用它。
        /// 因此失败或预览的事务<strong>不消耗</strong>任何 <c>ActionPlanId</c>。
        ///
        /// <paramref name="reservedPlanId"/> 为 <c>default</c>（无效）时行为与单参重载完全一致。
        /// 本重载<strong>不</strong>校验计数器一致性——那是提交点
        /// （<c>LogicIdGenerator.ReserveActionPlanId</c>）的职责，那里会以
        /// <c>ACTION_PLAN_ID_RESERVATION_MISMATCH</c> 稳定拒绝任何跳号或冲突。
        /// </summary>
        public ActionPlanCreationResult TryCreateOrdinary(
            OrdinaryPlanRequest request, long createdAtTick, ActionPlanId reservedPlanId)
        {
            if (request == null) return ActionPlanCreationResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID);
            if (!request.OwnerUnitId.IsValid)
                return ActionPlanCreationResult.Rejected(ActionPlanCodes.ACTION_PLAN_OWNER_INVALID);
            if (request.StartTick < 0L)
                return ActionPlanCreationResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID);

            ActionSpec spec = _definition.FindAction(request.ActionSpecId);
            if (spec == null) return ActionPlanCreationResult.Rejected(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND);
            if (spec.Type == ActionType.Block || spec.Type == ActionType.Dodge)
                return ActionPlanCreationResult.Rejected(ScheduleCodes.SCHEDULE_REACTION_CANNOT_BE_EDITED);

            if (!_facts.TryGetOwnerFacts(request.OwnerUnitId, out ActionPlanOwnerFacts facts))
                return ActionPlanCreationResult.Rejected(ScheduleCodes.SCHEDULE_LANE_UNKNOWN_UNIT);

            string actionSetError = ValidateActionSetMembership(spec, facts);
            if (actionSetError != null) return ActionPlanCreationResult.Rejected(actionSetError);

            // PrimaryTargetOnly 必须显式给出目标，且关系掩码必须放行；
            // 这是"创建时用权威关系校验目标"的第一处（第二处是排程候选，第三处是启动门禁）。
            string targetError = ValidatePrimaryTarget(spec, request.OwnerUnitId, request.PrimaryTargetUnitId,
                requirePresence: true);
            if (targetError != null) return ActionPlanCreationResult.Rejected(targetError);

            ActionPlanId planId = reservedPlanId.IsValid ? reservedPlanId : _ids.NextActionPlanId();
            var plan = new ActionPlan(
                planId,
                ActionPlanOrigin.Ordinary,
                request.OwnerUnitId,
                request.ActionSpecId,
                spec.Type,
                request.Facing,
                request.PrimaryTargetUnitId,
                request.Destination,
                request.SubmittedWindowId,
                null,
                createdAtTick);

            // 首次进入 Editable：一次性解析速度相关相对时序（此后任何编辑都只重绑绝对 Tick）。
            plan.ResolveTimingOnce(_definition.Rules, facts.ActionSpeed, facts.MoveSpeed, spec);

            if (spec.Type == ActionType.Move && (request.PathEdgeCount > 0 || request.PathWeightUnits > 0))
                plan.SetPathProjection(request.PathEdgeCount, request.PathWeightUnits);

            plan.RebindAbsoluteTicks(request.StartTick);
            plan.LastRequestedStartTick = plan.StartTick;
            plan.LastEditedScheduleRevision = 0L;
            plan.SyncEditableReservation();

            return new ActionPlanCreationResult(plan, null);
        }

        /// <summary>
        /// 创建反应计划（Block/Dodge）。<strong>直接 Locked</strong>：没有 Editable 阶段，
        /// 也不接受排程编辑。失败时不分配 ID。
        /// </summary>
        public ActionPlanCreationResult TryCreateReaction(ReactionPlanRequest request, long createdAtTick)
        {
            if (request == null) return ActionPlanCreationResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID);
            if (!request.OwnerUnitId.IsValid)
                return ActionPlanCreationResult.Rejected(ActionPlanCodes.ACTION_PLAN_OWNER_INVALID);
            if (!request.ReactionOpportunityId.IsValid)
                return ActionPlanCreationResult.Rejected(ReactionCodes.OPPORTUNITY_ID_INVALID);
            if (!request.SourceThreatPlanId.IsValid)
                return ActionPlanCreationResult.Rejected(ScheduleCodes.REACTION_PLAN_REQUIRES_SOURCE_THREAT);

            ActionSpec spec = _definition.FindAction(request.ActionSpecId);
            if (spec == null) return ActionPlanCreationResult.Rejected(ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND);
            if (spec.Type != ActionType.Block && spec.Type != ActionType.Dodge)
                return ActionPlanCreationResult.Rejected(ScheduleCodes.SCHEDULE_OPERATION_INVALID);

            if (!_facts.TryGetOwnerFacts(request.OwnerUnitId, out ActionPlanOwnerFacts facts))
                return ActionPlanCreationResult.Rejected(ScheduleCodes.SCHEDULE_LANE_UNKNOWN_UNIT);

            string actionSetError = ValidateActionSetMembership(spec, facts);
            if (actionSetError != null) return ActionPlanCreationResult.Rejected(actionSetError);

            ActionPlanId planId = _ids.NextActionPlanId();
            var binding = new ActionPlanTriggerBinding(
                request.ReactionOpportunityId, request.SourceThreatPlanId,
                request.TriggerTick, request.ResponseDeadlineTick);

            var plan = new ActionPlan(
                planId,
                ActionPlanOrigin.Reaction,
                request.OwnerUnitId,
                request.ActionSpecId,
                spec.Type,
                request.Facing,
                null,
                request.Destination,
                null,           // 高阶反应恒为空 SubmittedWindowId
                binding,
                createdAtTick);

            plan.ResolveTimingOnce(_definition.Rules, facts.ActionSpeed, facts.MoveSpeed, spec);
            if (spec.Type == ActionType.Dodge && (request.PathEdgeCount > 0 || request.PathWeightUnits > 0))
                plan.SetPathProjection(request.PathEdgeCount, request.PathWeightUnits);

            plan.ApplyReactionInterval();
            plan.LastRequestedStartTick = plan.StartTick;
            plan.LastEditedScheduleRevision = 0L;

            // 反应计划<strong>直接 Locked</strong>：它没有 Editable 阶段，
            // 因此不存在"可观察的 Editable 反应计划"，也不需要经过启动门禁的锁定提交。
            // 锁定 Tick 是创建 Tick（可审计），Running 仍要等到固定 StartTick 由门禁推进。
            plan.State = ActionPlanState.Locked;
            plan.LockedAtTick = createdAtTick;

            return new ActionPlanCreationResult(plan, null);
        }

        /// <summary>
        /// PrimaryTarget 的关系掩码校验（<strong>唯一实现</strong>；创建、排程候选与启动门禁共用）。
        /// <paramref name="requirePresence"/> 为真时，PrimaryTargetOnly 动作缺少目标即为失败。
        /// 返回稳定拒绝码（null = 通过）。
        /// </summary>
        public string ValidatePrimaryTarget(
            ActionSpec spec, UnitId ownerUnitId, UnitId? primaryTargetUnitId, bool requirePresence)
        {
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;

            var attack = spec.Payload as AttackPayloadSpec;
            if (attack == null) return null;    // 非攻击动作没有关系掩码

            if (!TargetRelationMasks.IsValid(attack.AllowedTargetRelations))
                return FactionCodes.TARGET_RELATION_MASK_INVALID;

            bool requiresTarget = requirePresence && attack.TargetPolicy == TargetPolicy.PrimaryTargetOnly;
            if (requiresTarget && !primaryTargetUnitId.HasValue)
                return ScheduleCodes.SCHEDULE_PRIMARY_TARGET_REQUIRED;
            if (!primaryTargetUnitId.HasValue) return null;

            UnitId target = primaryTargetUnitId.Value;
            if (!_facts.TryGetOwnerFacts(target, out ActionPlanOwnerFacts targetFacts))
                return FactionCodes.FACTION_RELATION_UNKNOWN_ID;
            if (!targetFacts.IsAlive) return ScheduleCodes.SCHEDULE_PRIMARY_TARGET_DEAD;

            return _factions.Allows(attack.AllowedTargetRelations, ownerUnitId, target)
                ? null
                : ScheduleCodes.TARGET_RELATION_NOT_ALLOWED;
        }

        /// <summary>
        /// 启动门禁的 PrimaryTarget 校验（与创建、排程候选<strong>共用同一实现</strong>）。
        ///
        /// 与创建时的区别只有一处：门禁对<strong>已存在</strong>的计划求值，因此
        /// <c>requirePresence</c> 恒为 false（缺失目标在创建时就已经被拒绝）；
        /// 关系掩码与存活判定完全复用，因此"三处读取同一 FactionRelationResolver 与
        /// AllowedTargetRelations"是结构事实。
        /// 返回稳定拒绝码（null = 通过）。
        /// </summary>
        public string ValidatePrimaryTargetForGate(ActionPlan plan)
        {
            if (plan == null) return ActionPlanCodes.ACTION_PLAN_NOT_FOUND;
            ActionSpec spec = _definition.FindAction(plan.ActionSpecId);
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            return ValidatePrimaryTarget(spec, plan.OwnerUnitId, plan.PrimaryTargetUnitId, requirePresence: false);
        }

        /// <summary>
        /// 动作集合归属校验。只在单位<strong>声明了</strong> ActionSetId 且该集合可解析时生效：
        /// 未绑定动作集合的单位不被这一条拦截（首版兼容既有资产）。
        /// </summary>
        private string ValidateActionSetMembership(ActionSpec spec, ActionPlanOwnerFacts facts)
        {
            string setId = facts.ActionSetId.Value;
            if (string.IsNullOrEmpty(setId)) return null;

            ActionSetDefinition set = _definition.FindActionSet(facts.ActionSetId);
            if (set == null) return null;
            return set.Contains(spec.ActionSpecId) ? null : ScheduleCodes.SCHEDULE_ACTION_NOT_IN_ACTION_SET;
        }

        /// <summary>诊断文本（不参与逻辑）。</summary>
        public static string Describe(ActionPlan plan)
            => plan == null ? "<null>" : plan.ToString() + " owner=" + plan.OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture);
    }
}
