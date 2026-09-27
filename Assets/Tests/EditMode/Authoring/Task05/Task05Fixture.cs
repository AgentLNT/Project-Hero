using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// 任务 05 测试夹具：在<strong>真实 02B 定义</strong>（<c>BattleDefinitionFixture</c>）之上
    /// 追加五个时序完全受控的合成动作，用来精确断言任务 05 的时序解析契约。
    ///
    /// 为什么必须这样做：
    /// <list type="bullet">
    /// <item>真实资产只提供 <c>quick_slash</c>（攻击，前摇 30）与 <c>block</c>/<c>dodge</c>；
    /// 任务 05 还必须覆盖 Guard 三段半开区间、Move 的 <c>ResolvedBaseStepTicks</c>
    /// 与路径权重重算，这些没有真实资产，只能显式构造。</item>
    /// <item>夹具<strong>不</strong>新建第二套配置模型：它只做 <c>with { Actions = ... }</c>
    /// 的纯追加，Encounter / Faction / Rules / 图案全部沿用真实定义。</item>
    /// </list>
    ///
    /// 冻结时序（全部进入断言，改动即让对应用例失败）：
    /// <list type="bullet">
    /// <item>Attack：<c>BaseWindupTicks = 30</c>、<c>RecoveryTicks = 30</c>；
    /// <c>ActionSpeed = 20</c>（= ReferenceActionSpeed）⇒ <c>ResolvedWindupTicks = 30</c>、
    /// <c>ImpactTick = Start + 30</c>、<c>EndTick = Start + 60</c>、<c>BudgetCostTicks = 60</c>。</item>
    /// <item>Guard：<c>Windup = 10</c>、<c>Active = 20</c>、<c>Recovery = 30</c> ⇒
    /// <c>Active = [Start+10, Start+30)</c>、<c>EndTick = Start + 60</c>、预算 60（三段全固定，不读速度）。</item>
    /// <item>Move：<c>BaseStepTicks = 5</c>、<c>RecoveryTicks = 10</c>；
    /// <c>MoveSpeed = 20</c> ⇒ <c>ResolvedBaseStepTicks = 5</c>；
    /// 路径权重 4 ⇒ 移动时长 20、<c>EndTick = Start + 30</c>、预算 30。</item>
    /// <item>Block：<c>ReactionWindupTicks = 60</c>、<c>RecoveryTicks = 20</c>（01B 拍板 B3）。</item>
    /// <item>Dodge：<c>ReactionWindupTicks = 30</c>、<c>RecoveryTicks = 10</c>（01B 拍板 B3）。</item>
    /// </list>
    /// </summary>
    internal static class Task05Farm
    {
        public const string AttackId = "action.t05.attack";
        public const string GuardId = "action.t05.guard";
        public const string MoveId = "action.t05.move";
        public const string BlockId = "action.t05.block";
        public const string DodgeId = "action.t05.dodge";

        // —— 阶段 D（反应机会）追加的受控攻击 ——
        /// <summary>前摇足够长，Block/Dodge 的截止 Tick 都能满足命令提前量（可公开）。</summary>
        public const string LongTelegraphAttackId = "action.t05.attack.long";
        /// <summary>缺 <c>Reactable</c> 标签：永不创建机会。</summary>
        public const string UnreactableAttackId = "action.t05.attack.unreactable";
        /// <summary>缺 <c>Dodgeable</c> 标签：只可能提供 Block。</summary>
        public const string NoDodgeAttackId = "action.t05.attack.nododge";
        /// <summary>没有可格挡分量且动量为零：只可能提供 Dodge。</summary>
        public const string NoBlockAttackId = "action.t05.attack.noblock";
        /// <summary>显式允许友军伤害（掩码 = Allied）：友军威胁与反应走完全相同流程。</summary>
        public const string FriendlyFireAttackId = "action.t05.attack.friendly";
        /// <summary>区域攻击（<c>TargetPolicy.AllTargetsInArea</c>）：候选由区域来源提供。</summary>
        public const string AreaAttackId = "action.t05.attack.area";

        /// <summary>冻结动作时序常量（用例直接引用，避免魔法数字散落）。</summary>
        public const int AttackWindup = 30;
        public const int AttackRecovery = 30;
        public const int GuardWindup = 10;
        public const int GuardActive = 20;
        public const int GuardRecovery = 30;
        public const int MoveBaseStepTicks = 5;
        public const int MoveRecovery = 10;
        public const int BlockReactionWindup = 60;
        public const int BlockRecovery = 20;
        public const int DodgeReactionWindup = 30;
        public const int DodgeRecovery = 10;

        /// <summary>动作速度基准（= <c>BattleRules.ReferenceActionSpeed</c>）；测试可改写。</summary>
        public const float ReferenceSpeed = 20f;

        /// <summary>长前摇攻击的前摇（足以让 Block/Dodge 截止都满足命令提前量）。</summary>
        public const int LongTelegraphWindup = 200;

        /// <summary>真实 02B 定义里的反应规则（命令提前量 / 最小反应提前量）。</summary>
        public static ReactionRules ReactionRules
            => ProjectHero.Authoring.Tests.Task03.Task03.Definition.ReactionRules;

        /// <summary>第三个单位（Faction 与 Hero 相同 ⇒ 对 Hero 是 Allied）。</summary>
        public static UnitId Ally => new UnitId(3);

        public static UnitId Hero => ProjectHero.Authoring.Tests.Task03.Task03.HeroUnitId;

        public static UnitId Enemy => ProjectHero.Authoring.Tests.Task03.Task03.EnemyUnitId;

        public static BattleRules Rules => ProjectHero.Authoring.Tests.Task03.Task03.Definition.Rules;

        public static AttackPatternSpec FirstAttackPattern()
            => ProjectHero.Authoring.Tests.Task03.Task03.Definition.AttackPatterns[0];

        public static MovementPatternSpec AnyMovementPattern()
        {
            IReadOnlyList<MovementPatternSpec> patterns =
                ProjectHero.Authoring.Tests.Task03.Task03.Definition.MovementPatterns;
            return patterns != null && patterns.Count > 0 ? patterns[0] : null;
        }

        /// <summary>真实定义 + 受控动作（纯追加，不改任何既有非反应动作）。</summary>
        public static BattleDefinition Definition()
        {
            BattleDefinition real = ProjectHero.Authoring.Tests.Task03.Task03.Definition;
            var actions = new List<ActionSpec>(real.Actions);

            // 真实 02B 资产里也有 Block/Dodge。它们的反应前摇不是本任务冻结的受控值，
            // 若与受控动作并存，反应选项集合就<strong>不可预测</strong>（截止 Tick/排序/过期计数全部失真）。
            // 因此这里<b>只</b>把反应族替换成本任务冻结的两个动作；Attack/Guard/Move 一律原样保留。
            actions.RemoveAll(spec => spec != null &&
                (spec.Type == ActionType.Block || spec.Type == ActionType.Dodge));

            actions.Add(new ActionSpec(
                new ActionSpecId(AttackId), ActionType.Attack,
                new AttackTimingSpec(AttackWindup, AttackRecovery),
                new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable) },
                    ImpactProfiles.Blunt, 1f, TargetPolicy.PrimaryTargetOnly,
                    TargetRelationMask.Hostile, 0, FirstAttackPattern(),
                    AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable),
                AdrenalineCost: 0));

            actions.Add(new ActionSpec(
                new ActionSpecId(GuardId), ActionType.Guard,
                new GuardTimingSpec(GuardWindup, GuardActive, GuardRecovery),
                new GuardPayloadSpec(
                    new Dictionary<DamageChannelId, int>(), 512,
                    DefenseTagMask.Blockable | DefenseTagMask.Guardable),
                AdrenalineCost: 0));

            actions.Add(new ActionSpec(
                new ActionSpecId(MoveId), ActionType.Move,
                new MoveTimingSpec(MoveBaseStepTicks, MoveRecovery),
                new MovePayloadSpec(64, AnyMovementPattern()),
                AdrenalineCost: 0));

            actions.Add(new ActionSpec(
                new ActionSpecId(BlockId), ActionType.Block,
                new BlockReactionTimingSpec(BlockReactionWindup, BlockRecovery),
                new BlockPayloadSpec(DefenseTagMask.Blockable),
                AdrenalineCost: FrozenDesignValues.BlockAdrenalineCost));

            actions.Add(new ActionSpec(
                new ActionSpecId(DodgeId), ActionType.Dodge,
                new DodgeReactionTimingSpec(DodgeReactionWindup, DodgeRecovery),
                new DodgePayloadSpec(2, AnyMovementPattern()),
                AdrenalineCost: FrozenDesignValues.DodgeAdrenalineCost));

            AddReactionAttacks(actions);

            return real with { Actions = actions };
        }

        /// <summary>攻击载荷的统一构造（阶段 D 的五个受控攻击共用）。</summary>
        private static AttackPayloadSpec AttackPayload(
            AttackTagMask tags, float forceMultiplier, DamageTagMask componentTags,
            TargetRelationMask relations, TargetPolicy policy = TargetPolicy.PrimaryTargetOnly)
            => new AttackPayloadSpec(
                new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, componentTags) },
                ImpactProfiles.Blunt, forceMultiplier, policy,
                relations, 0, FirstAttackPattern(), tags);

        /// <summary>阶段 D 追加的五个受控攻击（纯追加）。</summary>
        private static void AddReactionAttacks(List<ActionSpec> actions)
        {
            const AttackTagMask allTags =
                AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable;

            actions.Add(new ActionSpec(
                new ActionSpecId(LongTelegraphAttackId), ActionType.Attack,
                new AttackTimingSpec(LongTelegraphWindup, AttackRecovery),
                AttackPayload(allTags, 1f, DamageTagMask.Blockable, TargetRelationMask.Hostile),
                AdrenalineCost: 0));

            actions.Add(new ActionSpec(
                new ActionSpecId(UnreactableAttackId), ActionType.Attack,
                new AttackTimingSpec(LongTelegraphWindup, AttackRecovery),
                AttackPayload(AttackTagMask.Blockable | AttackTagMask.Dodgeable, 1f,
                    DamageTagMask.Blockable, TargetRelationMask.Hostile),
                AdrenalineCost: 0));

            actions.Add(new ActionSpec(
                new ActionSpecId(NoDodgeAttackId), ActionType.Attack,
                new AttackTimingSpec(LongTelegraphWindup, AttackRecovery),
                AttackPayload(AttackTagMask.Reactable | AttackTagMask.Blockable, 1f,
                    DamageTagMask.Blockable, TargetRelationMask.Hostile),
                AdrenalineCost: 0));

            // 无 Blockable 分量且 ForceMultiplier = 0 ⇒ 没有任何可格挡接触。
            actions.Add(new ActionSpec(
                new ActionSpecId(NoBlockAttackId), ActionType.Attack,
                new AttackTimingSpec(LongTelegraphWindup, AttackRecovery),
                AttackPayload(AttackTagMask.Reactable | AttackTagMask.Dodgeable, 0f,
                    DamageTagMask.None, TargetRelationMask.Hostile),
                AdrenalineCost: 0));

            // 显式友军伤害：掩码只放行 Allied。
            actions.Add(new ActionSpec(
                new ActionSpecId(FriendlyFireAttackId), ActionType.Attack,
                new AttackTimingSpec(LongTelegraphWindup, AttackRecovery),
                AttackPayload(allTags, 1f, DamageTagMask.Blockable, TargetRelationMask.Allied),
                AdrenalineCost: 0));

            // 区域攻击：候选来自任务 06 的逻辑威胁区域来源（同一关系掩码过滤）。
            actions.Add(new ActionSpec(
                new ActionSpecId(AreaAttackId), ActionType.Attack,
                new AttackTimingSpec(LongTelegraphWindup, AttackRecovery),
                AttackPayload(allTags, 1f, DamageTagMask.Blockable, TargetRelationMask.Hostile,
                    TargetPolicy.AllTargetsInArea),
                AdrenalineCost: 0));
        }

        /// <summary>真实阵营关系解析器（hero ↔ monster = Hostile；唯一实例，不复制矩阵）。</summary>
        public static IFactionRelationResolver Factions()
        {
            BattleDefinition definition = Definition();
            var unitFactions = new Dictionary<UnitId, FactionId>
            {
                [Hero] = FactionIds.Hero,
                [Enemy] = FactionIds.Monster
            };
            return new FactionRelationResolver(definition.FactionModel, unitFactions);
        }

        /// <summary>默认事实：双方存活、速度均为基准值、不绑定 ActionSet（跳过归属校验）。</summary>
        public static MutableFacts Facts()
        {
            var facts = new MutableFacts();
            facts.Set(Hero, FactionIds.Hero);
            facts.Set(Enemy, FactionIds.Monster);
            return facts;
        }

        /// <summary>工厂 + 事实 + 关系解析器 + ID 生成器的组合夹具。</summary>
        public static Task05Factory NewFactory(MutableFacts facts = null, LogicIdGenerator ids = null)
            => new Task05Factory(Definition(), Factions(), facts ?? Facts(), ids ?? new LogicIdGenerator());

        /// <summary>含第三个单位（与 Hero 同阵营 ⇒ Allied）的事实来源，用于显式友军伤害用例。</summary>
        public static MutableFacts FactsWithAlly()
        {
            MutableFacts facts = Facts();
            facts.Set(Ally, FactionIds.Hero);
            return facts;
        }

        /// <summary>认识三个单位的关系解析器（Ally 与 Hero 同阵营 ⇒ Allied）。</summary>
        public static IFactionRelationResolver FactionsWithAlly()
        {
            BattleDefinition definition = Definition();
            var unitFactions = new Dictionary<UnitId, FactionId>
            {
                [Hero] = FactionIds.Hero,
                [Enemy] = FactionIds.Monster,
                [Ally] = FactionIds.Hero
            };
            return new FactionRelationResolver(definition.FactionModel, unitFactions);
        }
    }

    /// <summary>工厂组合：把 定义 / 关系解析器 / 事实 / ID 生成器 绑在一起，避免每个用例重复五行装配。</summary>
    internal sealed class Task05Factory
    {
        public Task05Factory(
            BattleDefinition definition, IFactionRelationResolver factions,
            MutableFacts facts, LogicIdGenerator ids)
        {
            Definition = definition;
            Factions = factions;
            Facts = facts;
            Ids = ids;
            Create = new ActionPlanFactory(definition, factions, facts, ids);
        }

        public BattleDefinition Definition { get; }

        public IFactionRelationResolver Factions { get; }

        public MutableFacts Facts { get; }

        public LogicIdGenerator Ids { get; }

        public ActionPlanFactory Create { get; }

        /// <summary>默认所有者 = hero（UnitId 2）。</summary>
        public UnitId DefaultOwner => Task05Farm.Hero;

        /// <summary>默认主目标 = enemy（UnitId 1）；hero 对它是 Hostile。</summary>
        public UnitId DefaultTarget => Task05Farm.Enemy;

        public ActionScheduleAuthority NewAuthority(ScheduleLimits limits = null)
            => new ActionScheduleAuthority(null, limits);

        /// <summary>
        /// 创建指定动作的普通计划（结构化结果；调用方自行断言失败原因）。
        ///
        /// 夹具便利：<paramref name="target"/> 为 null 时，攻击类动作自动取
        /// <strong>对立单位</strong>作为主目标（hero↔enemy 是 Hostile）。
        /// 这<strong>不</strong>削弱契约——"PrimaryTargetOnly 缺目标必须被拒绝"由
        /// <c>ActionPlanFactory.TryCreateOrdinary</c> 的直接调用用例验证，
        /// 走的正是生产入口；本方法只是让其余用例不必每次都写目标。
        /// </summary>
        public ActionPlanCreationResult CreatePlan(
            string actionSpecId, long startTick, UnitId? owner = null, UnitId? target = null,
            int pathEdgeCount = 0, int pathWeightUnits = 0, long createdAtTick = 0L)
        {
            UnitId resolvedOwner = owner ?? DefaultOwner;
            UnitId? resolvedTarget = target ?? OpposingUnit(resolvedOwner);
            return Create.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    resolvedOwner, new ActionSpecId(actionSpecId), GridDirection.East,
                    resolvedTarget, null, null, startTick, pathEdgeCount, pathWeightUnits),
                createdAtTick);
        }

        /// <summary>对立单位（hero↔enemy）；未知单位返回 null。</summary>
        public static UnitId? OpposingUnit(UnitId unitId)
        {
            if (unitId == Task05Farm.Hero) return Task05Farm.Enemy;
            if (unitId == Task05Farm.Enemy) return Task05Farm.Hero;
            return null;
        }

        /// <summary>创建一个处于 Editable 的普通攻击计划（断言必须成功，失败即夹具错误）。</summary>
        public ActionPlan OrdinaryAttack(long startTick, UnitId? target = null, long createdAtTick = 0L)
        {
            ActionPlanCreationResult result = CreatePlan(
                Task05Farm.AttackId, startTick, null, target, 0, 0, createdAtTick);
            if (!result.Succeeded)
                throw new InvalidOperationException("fixture create failed: " + result.RejectionCode);
            return result.Plan;
        }

        /// <summary>
        /// 创建并注册一个普通计划（注册表 + Lane 一次完成）。
        /// 创建失败会<strong>显式抛出</strong>——夹具级失败不得伪装成被测行为。
        /// </summary>
        public ActionPlan AddOrdinary(
            ActionScheduleAuthority authority, string actionSpecId, long startTick,
            int pathEdgeCount = 0, int pathWeightUnits = 0, UnitId? owner = null, UnitId? target = null)
        {
            ActionPlanCreationResult result = CreatePlan(
                actionSpecId, startTick, owner, target, pathEdgeCount, pathWeightUnits);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    "fixture create failed: " + actionSpecId + " => " + result.RejectionCode);
            authority.RegisterPlan(result.Plan);
            return result.Plan;
        }

        /// <summary>
        /// 创建一个位于固定触发 Tick 的反应计划（Block/Dodge）。
        /// <c>TriggerTick</c> 只能由来源攻击的 <c>ImpactTick</c> 推导，测试也必须显式给出它。
        /// </summary>
        public ActionPlanCreationResult CreateReaction(
            string actionSpecId, long triggerTick, long responseDeadlineTick,
            long sourcePlanIdValue = 1L, long opportunityIdValue = 1L)
            => Create.TryCreateReaction(
                new ReactionPlanRequest(
                    DefaultOwner, new ActionSpecId(actionSpecId), GridDirection.East, null,
                    new ReactionOpportunityId(opportunityIdValue),
                    new ActionPlanId(sourcePlanIdValue),
                    triggerTick, responseDeadlineTick, 0, 0),
                0L);
    }

    /// <summary>
    /// 排程事务组合：权威 + 工厂 + ID 生成器 + <c>ScheduleEditor</c>。
    /// 阶段 B 的全部用例都经它构造，避免每个用例重复五行装配。
    /// </summary>
    internal sealed class Task05Scheduler
    {
        public Task05Scheduler(ScheduleLimits limits = null, IMovementPathCalculator pathCalculator = null)
            : this(limits, pathCalculator, null, null, null)
        {
        }

        /// <param name="definition">自定义定义（默认 = 夹具定义）。</param>
        /// <param name="factions">自定义关系解析器（默认 = hero/monster 两单位）。</param>
        /// <param name="facts">自定义事实来源（默认 = 两单位事实）。</param>
        public Task05Scheduler(
            ScheduleLimits limits, IMovementPathCalculator pathCalculator,
            BattleDefinition definition, IFactionRelationResolver factions, MutableFacts facts)
        {
            Task05Factory fixture = new Task05Factory(
                definition ?? Task05Farm.Definition(),
                factions ?? Task05Farm.Factions(),
                facts ?? Task05Farm.Facts(),
                new LogicIdGenerator());
            Bundle = fixture;
            Authority = fixture.NewAuthority(limits);
            // 任务 06 的路径重算端口必须<strong>同时</strong>交给权威事务与只读求值器：
            // 否则事务内部会自己造一个没有端口的求值器，注入被静默忽略。
            PathCalculator = pathCalculator;
            Editor = new ScheduleEditor(Authority, fixture.Create, fixture.Ids, pathCalculator);
            Evaluator = new ScheduleEvaluator(Authority, pathCalculator);
        }

        public Task05Factory Bundle { get; }

        public ActionScheduleAuthority Authority { get; }

        public IMovementPathCalculator PathCalculator { get; }

        public LogicIdGenerator Ids => Bundle.Ids;

        public ActionPlanFactory Factory => Bundle.Create;

        public ScheduleEditor Editor { get; }

        public ScheduleEvaluator Evaluator { get; }

        public long Revision => Authority.ScheduleRevision;

        /// <summary>创建一个位于 <paramref name="startTick"/> 的 Editable 普通攻击计划。</summary>
        public ActionPlan Attack(long startTick) => Bundle.OrdinaryAttack(startTick);

        public ActionPlan Create(string actionSpecId, long startTick, int pathEdgeCount = 0, int pathWeightUnits = 0)
        {
            ActionPlanCreationResult result = Bundle.CreatePlan(
                actionSpecId, startTick, null, null, pathEdgeCount, pathWeightUnits);
            if (!result.Succeeded) throw new InvalidOperationException("fixture create failed: " + result.RejectionCode);
            return result.Plan;
        }

        /// <summary>
        /// 构造一条 Add 操作。
        ///
        /// 夹具便利：<paramref name="target"/> 为 null 时对<strong>攻击类</strong>动作自动取
        /// <strong>对立单位</strong>作为主目标（hero↔enemy 是 Hostile），与
        /// <see cref="Task05Factory.CreatePlan"/> 的默认完全一致。
        /// 这<strong>不</strong>削弱契约——"PrimaryTargetOnly 缺目标必须被拒绝"由直接调用
        /// 生产入口 <c>ActionPlanFactory.TryCreateOrdinary</c> 的用例验证；
        /// 本方法只是让其余用例不必每次都写目标。
        /// </summary>
        public AddOrdinaryPlanOperation AddOp(long temporaryKey, long requestedStartTick,
            UnitId? owner = null, string actionSpecId = null, ActionPlanId anchor = default,
            UnitId? target = null)
        {
            UnitId resolvedOwner = owner ?? Task05Farm.Hero;
            return new AddOrdinaryPlanOperation(
                temporaryKey, resolvedOwner, new ActionSpecId(actionSpecId ?? Task05Farm.AttackId),
                requestedStartTick, anchor,
                target ?? Task05Factory.OpposingUnit(resolvedOwner));
        }

        public static MoveEditablePlanOperation MoveOp(ActionPlanId planId, long requestedStartTick,
            ActionPlanId anchor = default)
            => new MoveEditablePlanOperation(planId, requestedStartTick, anchor);

        public static RemoveEditablePlanOperation RemoveOp(ActionPlanId planId,
            ActionTerminationReason reason = ActionTerminationReason.CancelledByCommand)
            => new RemoveEditablePlanOperation(planId, reason);

        /// <summary>提交一批排程编辑（<c>ExpectedScheduleRevision</c> 默认取当前修订号）。</summary>
        public ScheduleEditTransactionResult Apply(
            IReadOnlyList<ScheduleEditOperation> operations, long currentTick = 0L,
            long? expectedRevision = null, long? batchBaseRevision = null)
            => Editor.Apply(operations, currentTick,
                batchBaseRevision ?? Revision, expectedRevision ?? Revision);

        /// <summary>预览一批排程编辑（不动任何权威状态）。</summary>
        public ScheduleEditTransactionResult Preview(
            IReadOnlyList<ScheduleEditOperation> operations, long currentTick = 0L)
            => Editor.Apply(operations, currentTick, Revision, Revision, preview: true);

        /// <summary>把计划直接置为 Locked（阶段 C 的启动门禁尚未接入，测试用它构造障碍）。</summary>
        public static ActionPlan Lock(ActionPlan plan, long lockedAtTick = 0L)
        {
            plan.State = ActionPlanState.Locked;
            plan.LockedAtTick = lockedAtTick;
            return plan;
        }

        /// <summary>把计划直接置为 Running。</summary>
        public static ActionPlan Run(ActionPlan plan, long lockedAtTick = 0L)
        {
            plan.State = ActionPlanState.Running;
            plan.LockedAtTick = lockedAtTick;
            return plan;
        }

        /// <summary>Lane 的规范投影（<c>ActionPlanId</c> 列表），用于断言顺序。</summary>
        public long[] LanePlanIds(UnitId unitId)
        {
            ActorLane lane = Authority.FindLane(unitId);
            if (lane == null) return Array.Empty<long>();
            var ids = new List<long>(lane.Count);
            for (int i = 0; i < lane.Count; i++) ids.Add(lane.Plans[i].ActionPlanId.Value);
            return ids.ToArray();
        }

        /// <summary>Lane 的起点列表（与 <see cref="LanePlanIds"/> 同序）。</summary>
        public long[] LaneStartTicks(UnitId unitId)
        {
            ActorLane lane = Authority.FindLane(unitId);
            if (lane == null) return Array.Empty<long>();
            var starts = new List<long>(lane.Count);
            for (int i = 0; i < lane.Count; i++) starts.Add(lane.Plans[i].StartTick);
            return starts.ToArray();
        }

        public long[] StartTicksOf(params ActionPlan[] plans)
        {
            var starts = new long[plans.Length];
            for (int i = 0; i < plans.Length; i++) starts[i] = plans[i].StartTick;
            return starts;
        }
    }

    /// <summary>
    /// 可变的单位事实来源：让用例能证明"速度/存活/朝向变化<strong>不</strong>改已解析的时序"。
    /// 它只回答只读事实，不写任何计划。
    /// </summary>
    internal sealed class MutableFacts : IActionPlanFactsSource
    {
        private readonly Dictionary<long, ActionPlanOwnerFacts> _facts =
            new Dictionary<long, ActionPlanOwnerFacts>();

        public void Set(
            UnitId unitId, FactionId factionId = default,
            float actionSpeed = Task05Farm.ReferenceSpeed, float moveSpeed = Task05Farm.ReferenceSpeed,
            bool isAlive = true, ActionSetId actionSetId = default)
        {
            _facts[unitId.Value] = new ActionPlanOwnerFacts(
                unitId, factionId, actionSpeed, moveSpeed, actionSetId, isAlive);
        }

        /// <summary>只改速度（证明"改速度不改已解析时序"）。</summary>
        public void SetActionSpeed(UnitId unitId, float actionSpeed)
        {
            ActionPlanOwnerFacts current = _facts[unitId.Value];
            _facts[unitId.Value] = current with { ActionSpeed = actionSpeed };
        }

        public void SetAlive(UnitId unitId, bool isAlive)
        {
            ActionPlanOwnerFacts current = _facts[unitId.Value];
            _facts[unitId.Value] = current with { IsAlive = isAlive };
        }

        public bool TryGetOwnerFacts(UnitId unitId, out ActionPlanOwnerFacts facts)
            => _facts.TryGetValue(unitId.Value, out facts);
    }
}
