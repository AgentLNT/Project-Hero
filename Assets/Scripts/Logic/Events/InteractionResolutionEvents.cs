using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Logic.Events
{
    /// <summary>
    /// 任务 08「必须产出」15 的<strong>语义事件族</strong>（交互与 Resolution 段）。
    ///
    /// <para>
    /// <strong>不变量 10（00 号规则 10 / 任务包禁止事项）</strong>：本文件里的每个事件只描述
    /// <strong>已经提交的战斗事实</strong>——冲突组键、参与计划/机会/单位 ID、三段伤害数值、
    /// 抵抗前后的动量、请求/实际步数、From/To、停止原因与稳定排序的失效计划 ID。
    /// 它们<strong>不</strong>携带震屏强度、顿帧秒数、动画时长、插值或任何表现策略；
    /// 表现映射属于任务 10。
    /// </para>
    ///
    /// <para>
    /// <strong>发射点</strong>：全部落在既有阶段上，<strong>不新增 Step 阶段</strong>——
    /// 阶段 10（构图 + 分阶段求解）、阶段 11（伤害/合力提交）、阶段 13（批量换位与位移事件）、
    /// 阶段 14（状态/控制/其余终态）。事件经 <c>LogicEventOutbox</c> 在本 Tick 提交完成后一次性可见。
    /// </para>
    ///
    /// <para>
    /// <strong>冲突组键</strong>：取值口径与既有事件一致——<c>0</c> 表示"本 Tick 没有对应冲突组"
    /// （无接触的 Dodge 计划，以及构型下未入图的计划）；非零时严格等于
    /// <c>ConflictGroup.GroupKey</c>（= 组内最小 <c>IntentSequence</c>）。
    /// </para>
    /// </summary>
    public static class InteractionEventCodes
    {
        /// <summary>接触两侧按 1024 完全抵抗（Block）⇒ 该接触最终载荷严格为 0。</summary>
        public const string CONTACT_FULLY_RESISTED = "INTERACTION_CONTACT_FULLY_RESISTED";

        /// <summary>接触两侧都无可降低载荷/无抵抗来源 ⇒ 按原载荷命中。</summary>
        public const string CONTACT_UNRESISTED = "INTERACTION_CONTACT_UNRESISTED";

        /// <summary>目标本 Tick 不可承受直接命中 ⇒ 不产生直接伤害，接触事实仍保留（审计）。</summary>
        public const string CONTACT_SUPPRESSED_BY_STATE = "INTERACTION_CONTACT_SUPPRESSED_BY_STATE";

        /// <summary>至少一条旧有可动接触因换位失效 ⇒ 该反应计划获得"成功回避"资格。</summary>
        public const string CONTACT_AVOIDED_BY_RELOCATION = "INTERACTION_CONTACT_AVOIDED_BY_RELOCATION";

        /// <summary>换位未让任何接触失效 ⇒ 反应计划<strong>不</strong>获得成功奖励。</summary>
        public const string CONTACT_NO_CONTACT_INVALIDATED = "INTERACTION_CONTACT_NO_CONTACT_INVALIDATED";

        /// <summary>换位请求被拒绝或未到达触发点 ⇒ 位置未提交，不伪造触发。</summary>
        public const string CONTACT_DESTINATION_NOT_COMMITTED = "INTERACTION_CONTACT_DESTINATION_NOT_COMMITTED";

        /// <summary>Block 计划在触发 Tick 面对的全部接触都无可降低载荷 ⇒ 无效格挡，无成功奖励。</summary>
        public const string CONTACT_BLOCK_INELIGIBLE = "INTERACTION_CONTACT_BLOCK_INELIGIBLE";
    }

    /// <summary>
    /// 反应计划（Dodge/Block）在<strong>求解层面</strong>的按计划解析结果判别式。
    ///
    /// 它与"是否获得成功奖励"<strong>正交</strong>：只有 <see cref="DodgeAvoidedContact"/>、
    /// <see cref="BlockedFully"/>、<see cref="BlockedPartially"/> 三种形态
    /// （以及 Dodge 的"至少一条旧有接触因换位失效"）才算成功；
    /// <see cref="BlockIneffective"/> 与 <see cref="DodgeNotCommitted"/> <strong>永不</strong>算成功。
    /// </summary>
    public enum ReactionResolutionKind
    {
        /// <summary>Dodge 提交成功且至少一条旧有可动接触因换位失效。</summary>
        DodgeAvoidedContact = 0,

        /// <summary>Dodge 提交成功，但没有接触因换位失效（或只有不可回避接触被保留）。</summary>
        DodgeCommittedWithoutInvalidation = 1,

        /// <summary>Dodge 目的格未提交（失败码见事件载荷）⇒ 不伪造触发。</summary>
        DodgeNotCommitted = 2,

        /// <summary>Block：实际降低过合格载荷且最终载荷全为 0。</summary>
        BlockedFully = 3,

        /// <summary>Block：实际降低过合格载荷但仍有绕过载荷。</summary>
        BlockedPartially = 4,

        /// <summary>Block：完全没有可降低载荷 ⇒ 无效格挡（<strong>不</strong>产生成功 Block 奖励）。</summary>
        BlockIneffective = 5
    }

    /// <summary>
    /// <strong>分通道伤害明细</strong>（任务包 08:43「Raw/被动后/动作后伤害」的唯一语义出口）。
    ///
    /// 一个条目 = 一个（目标单位，攻击计划，伤害通道）三元组在本 Tick 的聚合结果：
    /// <see cref="RawQ10"/>（一次量化后）→ <see cref="AfterPassiveResistanceQ10"/>（被动抵抗后）
    /// → <see cref="AfterActionResistanceQ10"/>（Guard/Block 动作抵抗后）。
    /// 三个值都来自 <c>TargetAggregator</c> 的<strong>同一份</strong>聚合产物，事件侧不做第二次量化。
    /// </summary>
    public sealed record DamageChannelEntry(
        DamageChannelId ChannelId,
        long RawQ10,
        long AfterPassiveResistanceQ10,
        long AfterActionResistanceQ10,
        int PassiveResistanceQ10,
        int ActionResistanceQ10,
        DamageTagMask Tags);

    /// <summary>
    /// <strong>分通道伤害明细事件</strong>：一条接触的完整伤害三段 + 动量抵抗前后值。
    ///
    /// <see cref="StopReasonCode"/> 是
    /// <see cref="InteractionEventCodes"/> 里的稳定码：完全抵抗 / 无抵抗命中 / 状态抑制。
    /// </summary>
    public sealed record DamageChannelResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        UnitId TargetUnitId,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        ActionPlanId GuardPlanId,
        GridDirection IncomingDirection,
        int IncomingMomentumUnits,
        int AfterMomentumResistanceUnits,
        IReadOnlyList<DamageChannelEntry> Channels,
        long TotalDamageQ10,
        long AfterBlockDamageQ10,
        string StopReasonCode,
        IReadOnlyList<ActionPlanId> ParticipatingPlanIds) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>Dodge 旧/新位置与失效接触事件</strong>（任务包 08:72）。
    ///
    /// 每个<strong>实际进入 TriggerTick 提交尝试</strong>的 Dodge 计划恰好一条；
    /// 提前终止不伪造触发。<see cref="InvalidatedAttackPlanIds"/> 是稳定升序、
    /// 已去重的失效攻击计划集合；<see cref="StillHitAttackPlanIds"/> 与
    /// <see cref="RetainedUndodgeablePlanIds"/> 提供四格表的可审计分解。
    /// <see cref="RewardedSuccess"/> 只在"已提交 + 至少一条旧有可动接触失效"时为 true。
    /// </summary>
    public sealed record DodgeResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        ActionPlanId DodgePlanId,
        UnitId DefenderUnitId,
        GridPoint From,
        GridPoint To,
        bool DestinationCommitted,
        string FailureCode,
        bool RewardedSuccess,
        IReadOnlyList<ActionPlanId> InvalidatedAttackPlanIds,
        IReadOnlyList<ActionPlanId> StillHitAttackPlanIds,
        IReadOnlyList<ActionPlanId> RetainedUndodgeablePlanIds,
        IReadOnlyList<ActionPlanId> ParticipatingPlanIds) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>Block 载荷抵抗事件</strong>：完全抵抗（Blocked）与部分抵抗（PartiallyBlocked）
    /// 走同一事件、由 <see cref="BlockContactOutcome"/> 与
    /// <see cref="RewardsBlock"/> 区分；<c>BlockIneffective</c> 也发事件，但其
    /// <see cref="RewardsBlock"/> 恒为 <c>false</c>（"失败反应不得获得成功奖励"的可观察面）。
    /// </summary>
    public sealed record BlockResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        ActionPlanId BlockPlanId,
        UnitId DefenderUnitId,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        BlockContactOutcome Outcome,
        bool RewardsBlock,
        long RawDamageQ10,
        long AfterBlockDamageQ10,
        int IncomingMomentumUnits,
        int AfterBlockMomentumUnits,
        IReadOnlyList<ActionPlanId> ParticipatingPlanIds) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>Guard 部分抵抗事件</strong>：<c>RawQ10</c> 与动作抵抗后载荷都由聚合器给出，
    /// <see cref="PartiallyResistedQ10"/> = <c>Raw - 动作抵抗后</c>（含被动段，见 <see cref="AfterPassiveQ10"/>）。
    /// Guard 生效<strong>不</strong>终止来源攻击计划（任务包禁止事项），因此本事件没有终态副作用。
    /// </summary>
    public sealed record GuardPartiallyResistedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        ActionPlanId GuardPlanId,
        UnitId DefenderUnitId,
        string GuardSpecId,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        GridDirection IncomingDirection,
        long RawQ10,
        long AfterPassiveQ10,
        long AfterGuardQ10,
        long PartiallyResistedQ10,
        int IncomingMomentumUnits,
        int AfterMomentumResistanceUnits,
        IReadOnlyList<ActionPlanId> ParticipatingPlanIds) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>拼刀（Clash）参与事件</strong>：一个连通块的每个参与攻击各一条，
    /// 携带原始动量、累计反向损耗（<strong>同时求解</strong>的结果，不是逐边中间量）、
    /// 一次性得到的剩余动量，以及逐对手的损耗明细。
    /// </summary>
    public sealed record ClashParticipantResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        ActionPlanId ActionPlanId,
        UnitId OwnerUnitId,
        GridDirection Direction,
        int OriginalMomentumUnits,
        long TotalOppositionLossUnits,
        int RemainingMomentumUnits,
        IReadOnlyList<ClashOppositionLoss> Losses,
        IReadOnlyList<ActionPlanId> ParticipatingPlanIds) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>ClashResidualImpact 事件</strong>：剩余动量按稳定权重分配给直接 Clash 对手的结果。
    /// 它同时是"Clash 剩余伤害分量不得重复附加攻击基础分量"的可审计面：
    /// <see cref="ResidualMomentumUnits"/> 是<strong>唯一</strong>来源。
    /// </summary>
    public sealed record ClashResidualImpactResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        UnitId RecipientUnitId,
        ActionPlanId RecipientActionPlanId,
        UnitId SourceUnitId,
        ActionPlanId SourceActionPlanId,
        GridDirection SourceDirection,
        int ResidualMomentumUnits,
        long ClashTotalRemainingMomentumUnits,
        long ClashTotalResidualAllocatedUnits) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>移动拦截/逃脱事件</strong>：<see cref="MoveContactOutcome"/> 区分
    /// <c>Intercepted</c>（换位/移动后攻击区域仍覆盖）与 <c>Escaped</c>（旧格覆盖、新格不覆盖
    /// 且没有 Dodge 复核判为被回避）。
    /// </summary>
    public sealed record MoveContactResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        ActionPlanId AttackPlanId,
        UnitId AttackerUnitId,
        ActionPlanId MovePlanId,
        UnitId MovingUnitId,
        bool CoveredBefore,
        bool CoveredAfter,
        MoveContactOutcome Outcome) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>动作终止请求事件</strong>（拼刀 → 统一终态协调器）。
    ///
    /// 它描述的是<strong>请求</strong>，不是"已终止"：真正提交由阶段 14 的统一终态协调器
    /// 按"第一次成功请求胜出"完成。因此本事件的载荷里<strong>没有</strong>任何计划对象、
    /// Lane、Intent、MovementSegment 或 Reservation。
    /// </summary>
    public sealed record ActionPlanTerminationRequestedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        ActionPlanId ActionPlanId,
        UnitId OwnerUnitId,
        ActionTerminationReason Reason) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>目标聚合冲击事件</strong>（任务包 08:39/43）：一个目标单位在本 Tick 的
    /// 分通道聚合总量、总冲击、抵抗后动量与<strong>有向合力</strong>控制结果。
    ///
    /// <see cref="ResultantDirection"/> 为 <c>null</c> 且 <see cref="KnockbackSteps"/> == 0
    /// 只表示"有向合力互相抵消"，<strong>不</strong>表示伤害或总冲击为零（主方案 0.4.2）。
    /// </summary>
    public sealed record TargetAggregateResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        UnitId TargetUnitId,
        IReadOnlyList<AggregatedChannelDamage> Channels,
        long TotalDamageQ10,
        long TotalImpactUnits,
        long IncomingMomentumUnits,
        long ResultantMomentumUnits,
        GridDirection? ResultantDirection,
        int ControlResistanceUnits,
        bool IsStaggered,
        bool IsKnockedDown,
        int KnockbackSteps,
        string StopReasonCode) : LogicEvent(Tick, Sequence);

    /// <summary>
    /// <strong>反应计划解析事件</strong>（Block/Dodge 共用一个形状）：
    /// 每个实际进入触发提交尝试的反应计划恰好一条。
    ///
    /// 它是"失败反应不得获得成功奖励"最直接的判据面：<see cref="Kind"/> 与
    /// <see cref="RewardsSuccess"/> 必须一致——<c>BlockIneffective</c> /
    /// <c>DodgeNotCommitted</c> / <c>DodgeCommittedWithoutInvalidation</c> 恒为 false。
    /// </summary>
    public sealed record ReactionPlanResolvedEvent(
        long Tick,
        long Sequence,
        long ConflictGroupKey,
        ActionPlanId ReactionPlanId,
        UnitId OwnerUnitId,
        ReactionResolutionKind Kind,
        bool RewardsSuccess,
        string ReasonCode,
        IReadOnlyList<ActionPlanId> AttackPlanIds,
        IReadOnlyList<ActionPlanId> InvalidatedAttackPlanIds) : LogicEvent(Tick, Sequence);
}
