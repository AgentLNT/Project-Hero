using System;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// <strong><c>ReactionCommandPlanner</c></strong>（任务 09「必须产出」7 第二段）：
    /// <c>ReactionCommand</c> 的<strong>唯一</strong>命令处理路径。
    ///
    /// 它把一条已通过控制权校验的反应命令，按固定顺序转给任务 05 的
    /// <see cref="ReactionPlanner"/> 命令路径（<c>ReactionOpportunitySystem.TryAcceptByIdWithCommandSequence</c>），
    /// 由机会系统在<strong>同一处</strong>重新校验：
    /// <list type="number">
    /// <item><c>ReactionOpportunityId</c> 存在（不存在 ⇒ <c>REACTION_OPPORTUNITY_NOT_OPEN</c>，
    /// <strong>绝不</strong>改用"当前最近攻击"临时猜一个机会）；</item>
    /// <item>选项截止（<c>tick &gt; ResponseDeadlineTick</c> 才过期 ⇒ 截止 Tick 本身仍可接受）；</item>
    /// <item>来源威胁仍在（来源计划存在且非终态）、选项已公开且未被占用；</item>
    /// <item>ActionSet 归属（与玩家完全相同的 <c>ActionSetDefinition</c> 规则）；</item>
    /// <item>状态 / Lane（提交锁定与固定反应区间占位）；</item>
    /// <item>任务 06 的 Dodge 目的格预留（按 <c>ReactionOpportunityId</c> 冻结、按
    /// <c>CommandSequence</c> 判胜者）与任务 07 的肾上腺素预留（费用只来自权威
    /// <c>ActionSpec.AdrenalineCost</c>）。</item>
    /// </list>
    ///
    /// 它<strong>不</strong>（00 号规则 26、任务 08 交接 §4.5）：
    /// <list type="bullet">
    /// <item>检查当前窗口、<c>ExpectedWindowId</c> 或并发授权——反应权与窗口正交；</item>
    /// <item>读取或推导 <c>TriggerTick</c>（它恒等于来源攻击的 <c>ImpactTick</c>，
    /// 载荷里根本没有该字段）；</item>
    /// <item>自己拼装 <c>ActionPlan</c>、写终态或创建机会。</item>
    /// </list>
    ///
    /// <see cref="DefenderUnitSource"/> 同时实现
    /// <see cref="IReactionCommandUnitSource"/>，因此"反应命令的控制权校验"与"反应命令的计划创建"
    /// 读的是<strong>同一个</strong>权威机会对象，不存在两份机会视图。
    /// </summary>
    public sealed class ReactionCommandPlanner : IAuthorityRoutedCommandProcessor, IReactionCommandUnitSource
    {
        private readonly ReactionOpportunitySystem _opportunities;
        private readonly BattleDefinition _definition;

        public ReactionCommandPlanner(ReactionOpportunitySystem opportunities, BattleDefinition definition)
        {
            _opportunities = opportunities ?? throw new ArgumentNullException(nameof(opportunities));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        }

        /// <summary>本场唯一的机会系统（只读暴露；它是反应命令的唯一权威作用域来源）。</summary>
        public ReactionOpportunitySystem Opportunities => _opportunities;

        /// <summary>
        /// 反应计划创建成功后的语义事件端口（装配方接到 <c>ReactionOpportunitySystem.EmitPlanCreated</c>）。
        /// 失败路径<strong>不</strong>触发它，因此"每个成功接受恰好一条 PlanCreated"是结构事实。
        /// </summary>
        public Action<ActionPlan, long> PlanCreatedSink { get; set; }

        /// <summary>
        /// 成功接受的次数（诊断；只在计划真正创建并注册之后 +1）。
        /// </summary>
        public int AcceptedCount { get; private set; }

        /// <inheritdoc />
        public bool TryGetDefenderUnitId(ReactionOpportunityId opportunityId, out UnitId defenderUnitId)
        {
            defenderUnitId = default;
            if (!opportunityId.IsValid) return false;
            return _opportunities.TryGetDefenderUnitId(opportunityId, out defenderUnitId);
        }

        /// <inheritdoc />
        public string ProcessAuthorized(CommandEnvelope envelope, long tick, long batchBaseScheduleRevision)
        {
            if (envelope?.Request == null) return CommandCodes.COMMAND_REQUEST_NULL;
            if (!(envelope.Request.Scope is ReactionCommandScope scope))
                return CommandCodes.SCOPE_PAYLOAD_MISMATCH;
            if (!(envelope.Request.Payload is ReactionCommandPayload payload))
                return CommandCodes.SCOPE_PAYLOAD_MISMATCH;

            // 结构层已经拒绝过"Block 带目的格 / Dodge 缺目的格"；这里做第二道防线：
            // 绝不因为载荷形态而让 Block 悄悄换位，也绝不让 Dodge 凭空换位。
            if (payload.ReactionKind == ReactionCommandKind.Block && payload.DodgeDestination.HasValue)
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;
            if (payload.ReactionKind == ReactionCommandKind.Dodge && !payload.DodgeDestination.HasValue)
                return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;

            // 载荷种类必须与权威动作定义一致（Dodge 只能提交 Dodge，Block 只能提交 Block）。
            ActionSpec spec = _definition.FindAction(payload.ReactionActionSpecId);
            if (spec == null) return ActionPlanCodes.ACTION_PLAN_SPEC_NOT_FOUND;
            bool isDodge = spec.Type == ActionType.Dodge;
            if (spec.Type != ActionType.Block && !isDodge)
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;
            if (isDodge != (payload.ReactionKind == ReactionCommandKind.Dodge))
                return ReactionCodes.REACTION_ACTION_NOT_BLOCK_OR_DODGE;

            // 防御者<strong>只能</strong>由机会系统解析（载荷不携带单位）；
            // 解析不到 ⇒ 稳定拒绝，绝不跳过控制权校验。
            if (!TryGetDefenderUnitId(scope.ReactionOpportunityId, out UnitId defender))
                return ReactionOpportunityCodes.OPPORTUNITY_NOT_OPEN;

            string error = _opportunities.TryAcceptByIdWithCommandSequence(
                scope.ReactionOpportunityId, payload.ReactionActionSpecId, envelope.CommandSequence, tick,
                isDodge ? payload.DodgeDestination : (GridPoint?)null, out ActionPlan plan);
            if (error != null) return error;

            AcceptedCount++;
            PlanCreatedSink?.Invoke(plan, tick);
            return null;
        }

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "reaction-accepted=" + AcceptedCount.ToString(CultureInfo.InvariantCulture) +
               " opportunities=" + _opportunities.ActiveOpportunities.Count.ToString(CultureInfo.InvariantCulture);
    }
}
