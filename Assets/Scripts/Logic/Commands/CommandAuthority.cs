using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// <strong>发行者控制权的全局只读投影</strong>（任务 09「必须产出」6 第二段
    /// / 00 号规则 18「载荷中的单位 ID 只表达意图目标，不证明权限」）。
    ///
    /// 冻结语义：
    /// <list type="bullet">
    /// <item>它是 <c>ControllerId -&gt; UnitId 集合</c> 的<strong>唯一</strong>权威查询面。
    /// 玩家、AI、旧壳与系统走<strong>同一条</strong>判定，不存在"玩家更宽 / AI 更严"的第二套规则。</item>
    /// <item>控制权只来自已验证定义里的 <c>ControllerBinding</c>：它<strong>不</strong>读
    /// <c>CommandSourceKind</c>、不读玩家标志、不读 Tag/Layer，也不使用"默认敌人列表"。
    /// 因此「更换来源种类不改变资格」在本类型上是结构事实。</item>
    /// <item>它与 <c>FactionRelationResolver</c> <strong>正交</strong>：控制权回答"谁有权提交"，
    /// 阵营关系回答"允许打谁"。二者都通过才成立，谁也不能替代谁。</item>
    /// </list>
    /// </summary>
    public interface IBattleUnitAuthority
    {
        /// <summary>该发行者当前可控制的单位集合（稳定顺序；未知发行者返回空集合，绝不返回 null）。</summary>
        IReadOnlyList<UnitId> ControlledUnitsOf(ControllerId controllerId);

        /// <summary>该发行者现在是否可以控制该单位。</summary>
        bool CanControl(ControllerId controllerId, UnitId unitId);
    }

    /// <summary>
    /// 反应命令的<strong>作用单位来源</strong>（把 <c>ReactionOpportunityId</c> 解析成防御者）。
    ///
    /// 反应载荷<strong>不</strong>携带 <c>DefenderUnitId</c>（那会让生产者自报作用单位），
    /// 因此控制权校验必须先经权威机会系统解析出防御者，再交给
    /// <see cref="IBattleUnitAuthority"/> 判定。为 null / 解析不到机会 ⇒
    /// 一律按"无法证明控制权"稳定拒绝，绝不猜测或跳过校验。
    /// </summary>
    public interface IReactionCommandUnitSource
    {
        /// <summary>返回该机会的防御者；机会不存在时返回 false（零写入）。</summary>
        bool TryGetDefenderUnitId(ReactionOpportunityId opportunityId, out UnitId defenderUnitId);
    }

    /// <summary>
    /// <strong><c>CommandAuthority</c></strong>：命令处理器唯一的控制权校验入口
    /// （任务 09「必须产出」6）。
    ///
    /// 两个职责，均在<strong>任何载荷处理之前</strong>完成：
    /// <list type="number">
    /// <item><see cref="TryGetIntentUnitIds"/>：从已通过网关的 <see cref="CommandEnvelope"/>
    /// 里读出"该载荷意图涉及的单位"。判别不匹配或无法解析 ⇒ 返回稳定拒绝码；</item>
    /// <item><see cref="Authorize"/>：逐个单位向 <see cref="IBattleUnitAuthority"/> 求证发行者身份。
    /// 任一个不可控 ⇒ <see cref="CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT"/>，
    /// 该命令零局部写入（同批其他命令不受影响）。</item>
    /// </list>
    /// 它<strong>不</strong>读 <c>CommandSourceKind</c>、不读窗口、不读阵营关系、不读任何费用字段。
    /// </summary>
    public sealed class CommandAuthority
    {
        private readonly IBattleUnitAuthority _units;
        private readonly IReactionCommandUnitSource _reactionUnits;

        public CommandAuthority(IBattleUnitAuthority units, IReactionCommandUnitSource reactionUnits = null)
        {
            _units = units ?? throw new ArgumentNullException(nameof(units));
            _reactionUnits = reactionUnits;
        }

        public IBattleUnitAuthority Units => _units;

        /// <summary>该发行者现在可以控制的单位（只读投影）。</summary>
        public IReadOnlyList<UnitId> ControlledUnitsOf(ControllerId controllerId)
            => _units.ControlledUnitsOf(controllerId);

        /// <summary>发行者是否可以直接控制该单位（唯一的布尔查询面）。</summary>
        public bool CanControl(ControllerId controllerId, UnitId unitId)
            => _units.CanControl(controllerId, unitId);

        /// <summary>
        /// 读出载荷意图涉及的单位集合（按 <c>UnitId</c> 升序、去重）。
        /// 返回非 null 即稳定拒绝码，此时<strong>不得</strong>继续处理该载荷。
        /// </summary>
        public string TryGetIntentUnitIds(CommandEnvelope envelope, out List<UnitId> unitIds)
        {
            unitIds = null;
            if (envelope == null || envelope.Request == null) return CommandCodes.COMMAND_REQUEST_NULL;

            if (CommandScopes.KindOf(envelope.Request.Scope) != envelope.Request.Payload.Kind)
                return CommandCodes.SCOPE_PAYLOAD_MISMATCH;

            switch (envelope.Request.Payload)
            {
                case ScheduleEditPayload schedule:
                    return TryCollectScheduleUnits(schedule, out unitIds);

                case ReactionCommandPayload _:
                    return TryCollectReactionUnits(envelope, out unitIds);

                case WindowCommandPayload window:
                    return TryCollectWindowUnits(window, out unitIds);

                default:
                    return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
            }
        }

        /// <summary>
        /// <strong>唯一的控制权判定入口</strong>：先取出意图单位，再逐个求证，最后返回
        /// "载荷实际涉及的单位"（供处理器做后续审计/诊断，绝不被当作授权输入）。
        /// 返回 null = 全部通过；否则为稳定拒绝码（命令零局部写入）。
        /// 它<strong>不</strong>关心发行者来自哪个来源种类——玩家与 AI 走完全相同的代码路径。
        /// </summary>
        public string Authorize(CommandEnvelope envelope, out IReadOnlyList<UnitId> intentUnitIds)
        {
            intentUnitIds = Array.Empty<UnitId>();
            string resolveError = TryGetIntentUnitIds(envelope, out List<UnitId> unitIds);
            if (resolveError != null) return resolveError;

            ControllerId issuer = envelope.ControllerId;
            for (int i = 0; i < unitIds.Count; i++)
            {
                if (_units.CanControl(issuer, unitIds[i])) continue;
                return CommandCodes.COMMAND_ISSUER_CANNOT_CONTROL_UNIT;
            }

            intentUnitIds = unitIds;
            return null;
        }

        /// <summary>便捷断言：该发行者是否能控制载荷涉及的全部单位。</summary>
        public bool Allows(CommandEnvelope envelope) => Authorize(envelope, out _) == null;

        /// <summary>
        /// 排程编辑涉及的 Lane 所有者集合。逐个操作独立校验：对 Remove 与 Move，
        /// <strong>计划自己的所有者</strong>就是作用单位——因此"用别人的计划 ID 顶替自己单位的编辑"
        /// 在控制权这一步就被拦住，而不是等到 Lane 查找。
        /// </summary>
        private string TryCollectScheduleUnits(ScheduleEditPayload payload, out List<UnitId> unitIds)
        {
            unitIds = null;
            if (payload.Operations == null || payload.Operations.Count == 0)
                return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;

            var collected = new List<UnitId>(payload.Operations.Count);
            for (int i = 0; i < payload.Operations.Count; i++)
            {
                ScheduleEditOperation operation = payload.Operations[i];
                if (operation == null) return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;

                UnitId unitId;
                switch (operation)
                {
                    case AddOrdinaryPlanOperation add:
                        unitId = add.OwnerUnitId;
                        break;
                    case MoveEditablePlanOperation move:
                        if (!TryResolvePlanOwner(move.PlanId, out unitId))
                            return CommandCodes.COMMAND_PLAN_OWNER_UNRESOLVABLE;
                        break;
                    case RemoveEditablePlanOperation remove:
                        if (!TryResolvePlanOwner(remove.PlanId, out unitId))
                            return CommandCodes.COMMAND_PLAN_OWNER_UNRESOLVABLE;
                        break;
                    default:
                        return CommandCodes.PAYLOAD_STRUCTURALLY_INVALID;
                }

                if (!unitId.IsValid) return CommandCodes.COMMAND_PLAN_OWNER_UNRESOLVABLE;
                collected.Add(unitId);
            }

            unitIds = Canonicalize(collected);
            return null;
        }

        /// <summary>反应命令的作用单位 = 机会自己的防御者（绝不来自载荷）。</summary>
        private string TryCollectReactionUnits(CommandEnvelope envelope, out List<UnitId> unitIds)
        {
            unitIds = null;
            if (!(envelope.Request.Scope is ReactionCommandScope scope))
                return CommandCodes.SCOPE_PAYLOAD_MISMATCH;
            if (_reactionUnits == null) return CommandCodes.COMMAND_ISSUER_UNIT_UNRESOLVABLE;

            if (!_reactionUnits.TryGetDefenderUnitId(scope.ReactionOpportunityId, out UnitId defender))
                return CommandCodes.COMMAND_ISSUER_UNIT_UNRESOLVABLE;
            if (!defender.IsValid) return CommandCodes.COMMAND_ISSUER_UNIT_UNRESOLVABLE;

            unitIds = Canonicalize(new[] { defender });
            return null;
        }

        /// <summary>
        /// 窗口命令的作用单位：
        /// 并发激活<strong>必须</strong>声明其作用的单位（<c>PlayerUnitId</c>，任务包「必须输出」4 第二段），
        /// 而控制权校验与并发授权校验是<strong>两件事</strong>——前者由本类判定，
        /// 后者仍由任务 07 的 <c>ConcurrentActionSystem</c>（<c>IActionAuthority</c>）判定，
        /// 因此"有并发授权"绝不能替代控制权。把载荷的单位经
        /// <see cref="ConcurrentActionUnitResolver"/> 解析成权威 <c>UnitId</c> 后，
        /// 它走<strong>与排程/反应完全相同的</strong>控制权判定。
        /// 关窗命令不声明单位：它是不是"自己的窗口"由任务 07 的窗口权威按
        /// <c>ExpectedWindowId</c> + 发行者判定（<c>WINDOW_ISSUER_CANNOT_CONTROL_UNIT</c>），
        /// 处理器<strong>不</strong>替它猜窗口拥有者。
        /// </summary>
        private string TryCollectWindowUnits(WindowCommandPayload payload, out List<UnitId> unitIds)
        {
            unitIds = new List<UnitId>(0);
            switch (payload.WindowKind)
            {
                case WindowCommandKind.CloseOwnWindow:
                    return null;

                case WindowCommandKind.ActivateConcurrentAction:
                    if (ConcurrentActionUnitResolver == null) return null;
                    UnitId? actor = ConcurrentActionUnitResolver();
                    if (!actor.HasValue || !actor.Value.IsValid)
                        return CommandCodes.COMMAND_ISSUER_UNIT_UNRESOLVABLE;
                    unitIds = Canonicalize(new[] { actor.Value });
                    return null;

                default:
                    return CommandCodes.SCOPE_PAYLOAD_MISMATCH;
            }
        }

        /// <summary>
        /// 并发行动的作用单位解析端口（装配方回填为"本场并发行动的权威单位"）。
        /// 未回填时窗口载荷不参与控制权判定（保持任务 07 既有语义：由并发系统自行判定）。
        /// </summary>
        public Func<UnitId?> ConcurrentActionUnitResolver { get; set; }

        /// <summary>计划所有者解析端口（装配方回填；未回填时 Remove/Move 无法证明控制权）。</summary>
        public Func<ActionPlanId, UnitId?> PlanOwnerResolver { get; set; }

        private bool TryResolvePlanOwner(ActionPlanId planId, out UnitId ownerUnitId)
        {
            ownerUnitId = default;
            if (!planId.IsValid) return false;
            if (PlanOwnerResolver == null) return false;
            UnitId? resolved = PlanOwnerResolver(planId);
            if (!resolved.HasValue) return false;
            ownerUnitId = resolved.Value;
            return true;
        }

        private static List<UnitId> Canonicalize(IReadOnlyList<UnitId> source)
        {
            var ordered = new List<UnitId>(source);
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
            var distinct = new List<UnitId>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                if (distinct.Count > 0 && distinct[distinct.Count - 1].Value == ordered[i].Value) continue;
                distinct.Add(ordered[i]);
            }
            return distinct;
        }
    }

    /// <summary>
    /// 由已验证的 <c>ControllerBinding</c> 构建的<strong>可变但只写一次</strong>控制权映射。
    ///
    /// 它<strong>不</strong>是容器扫描：唯一装配路径是 <c>Register(controllerId, unitIds)</c>，
    /// 由模拟方在初始化时按 <c>Initialization.ControllerToUnitIds</c> 填入。
    /// 快照/查询面只暴露只读集合，因此"UI/AI 现场给自己加一个单位"在类型上不可能。
    /// </summary>
    public sealed class ControllerUnitAuthority : IBattleUnitAuthority
    {
        private static readonly UnitId[] NoUnits = new UnitId[0];

        private readonly Dictionary<string, IReadOnlyList<UnitId>> _byController =
            new Dictionary<string, IReadOnlyList<UnitId>>(StringComparer.Ordinal);

        /// <summary>注册/替换一个发行者的可控单位集合（去重、按 <c>UnitId</c> 升序）。</summary>
        public void Register(ControllerId controllerId, IReadOnlyList<UnitId> unitIds)
        {
            string key = controllerId.Value;
            if (key == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID, "<null>");

            var ordered = new List<UnitId>();
            if (unitIds != null)
            {
                for (int i = 0; i < unitIds.Count; i++)
                {
                    if (!unitIds[i].IsValid)
                        throw new ProjectHero.Logic.LogicDefinitionException(
                            CommandCodes.COMMAND_PLAN_OWNER_UNRESOLVABLE, "unit id invalid");
                    ordered.Add(unitIds[i]);
                }
            }
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));

            var distinct = new List<UnitId>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                if (distinct.Count > 0 && distinct[distinct.Count - 1].Value == ordered[i].Value) continue;
                distinct.Add(ordered[i]);
            }

            _byController[key] = distinct.Count == 0 ? NoUnits : distinct.ToArray();
        }

        public IReadOnlyList<UnitId> ControlledUnitsOf(ControllerId controllerId)
        {
            string key = controllerId.Value;
            if (key == null) return NoUnits;
            return _byController.TryGetValue(key, out IReadOnlyList<UnitId> units) ? units : NoUnits;
        }

        public bool CanControl(ControllerId controllerId, UnitId unitId)
        {
            if (!unitId.IsValid) return false;
            IReadOnlyList<UnitId> units = ControlledUnitsOf(controllerId);
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i].Value == unitId.Value) return true;
            }
            return false;
        }
    }
}
