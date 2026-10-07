using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 交互矩阵的冻结阶段优先级（主方案 0.4 交互判定优先级矩阵，数字越小越先求解）。
    /// 本类型只提供<strong>已冻结的矩阵阶段编号</strong>，不从动作速度、状态或模糊总时长推导任何东西。
    /// </summary>
    public static class InteractionPriorities
    {
        /// <summary>Attack vs Dodge（阶段 1）。</summary>
        public const int AttackVsDodge = 1;

        /// <summary>Attack vs Block（阶段 2）。</summary>
        public const int AttackVsBlock = 2;

        /// <summary>Attack vs Attack（阶段 3）；攻击 Intent 节点的默认优先级。</summary>
        public const int AttackVsAttack = 3;

        /// <summary>Attack vs Move(end) 拦截（阶段 4）。</summary>
        public const int AttackVsMoveEnd = 4;

        /// <summary>Attack vs Move(start) 逃脱（阶段 5）。</summary>
        public const int AttackVsMoveStart = 5;

        /// <summary>Remaining Hits：Guard/被动抵抗与目标聚合（阶段 6）。</summary>
        public const int RemainingHits = 6;
    }

    /// <summary>
    /// 生成攻击 Intent 的<strong>纯值请求</strong>（任务包 1 / 08-多方仲裁与伤害.md:26）。
    /// 它只承载"计划在 <c>ImpactTick</c> 已经决定的事实"，不含任何从当前状态、动作速度
    /// 或模糊总时长重新推导命中时刻的入口。
    ///
    /// 区域来源固定为 <see cref="AttackPatternSpec.Directions"/>：按 <c>(int)Facing</c> 查表
    /// 再对所有者逻辑格做整数平移；<strong>不存在</strong>运行时旋转、浮点旋转矩阵或三角函数入口。
    /// </summary>
    public sealed record AttackIntentRequest(
        ActionPlanId ActionPlanId,
        ActionSpecId ActionSpecId,
        UnitId OwnerUnitId,
        TargetPolicy TargetPolicy,
        UnitId? PrimaryTargetUnitId,
        TargetRelationMask AllowedTargetRelations,
        AttackTagMask Tags,
        GridDirection Facing,
        AttackPatternSpec Pattern,
        GridPoint OwnerAnchor,
        long ImpactTick,
        int InteractionPriority,
        long IntentSequence,
        IReadOnlyList<DamageComponentSpec> DamageComponents,
        MomentumPacket Momentum,
        int MomentumDirectionOffsetSteps,
        WindowId? SubmittedWindowId);

    /// <summary>
    /// 不含 Unity 引用的<strong>攻击 Intent 值数据</strong>（任务 08「必须产出」1）。
    ///
    /// 不可变性：全部字段只读；点集与分量集合在构造时拷贝、规范排序并包成只读集合，
    /// 外部既不能改写字段，也不能通过反向强转改写内部数组。
    ///
    /// 唯一性契约（由调用方遵守，本类型提供显式校验入口）：攻击 Intent 只能由任务 05 的计划生命周期
    /// 在 <see cref="ImpactTick"/> <strong>恰好产生一次</strong>。校验入口：
    /// <see cref="CombatIntentContract.ValidateMaterialization"/> 与
    /// <see cref="CombatIntentContract.ValidateProducedOnce"/>。
    ///
    /// 接触载荷是<strong>正交的两部分</strong>：分通道 <see cref="DamageComponents"/>（引用既有
    /// <c>Logic/Damage/**</c> 类型）与独立动量载荷 <see cref="Momentum"/>
    /// （<see cref="ProjectHero.Logic.Combat.MomentumPacket"/>，动量域的唯一权威类型）。
    /// 二者不会被合并成单一 <c>ImpactType</c> 或 <c>DamageReduction</c>（00 号规则 24）。
    /// </summary>
    public sealed class CombatIntent
    {
        private readonly TrianglePoint[] _areaPoints;
        private readonly DamageComponentSpec[] _damageComponents;

        internal CombatIntent(
            ActionPlanId actionPlanId,
            ActionSpecId actionSpecId,
            UnitId ownerUnitId,
            TargetPolicy targetPolicy,
            UnitId? primaryTargetUnitId,
            TargetRelationMask allowedTargetRelations,
            AttackTagMask tags,
            GridDirection facing,
            TrianglePoint[] areaPoints,
            long impactTick,
            int interactionPriority,
            long intentSequence,
            DamageComponentSpec[] damageComponents,
            MomentumPacket momentum,
            int momentumDirectionOffsetSteps,
            WindowId? submittedWindowId)
        {
            ActionPlanId = actionPlanId;
            ActionSpecId = actionSpecId;
            OwnerUnitId = ownerUnitId;
            TargetPolicy = targetPolicy;
            PrimaryTargetUnitId = primaryTargetUnitId;
            AllowedTargetRelations = allowedTargetRelations;
            Tags = tags;
            Facing = facing;
            _areaPoints = areaPoints ?? Array.Empty<TrianglePoint>();
            ImpactTick = impactTick;
            InteractionPriority = interactionPriority;
            IntentSequence = intentSequence;
            _damageComponents = damageComponents ?? Array.Empty<DamageComponentSpec>();
            Momentum = momentum;
            MomentumDirectionOffsetSteps = momentumDirectionOffsetSteps;
            SubmittedWindowId = submittedWindowId;
        }

        /// <summary>产生该 Intent 的计划（唯一性契约的键之一）。</summary>
        public ActionPlanId ActionPlanId { get; }

        /// <summary>攻击动作定义 ID。</summary>
        public ActionSpecId ActionSpecId { get; }

        /// <summary>攻击者单位。</summary>
        public UnitId OwnerUnitId { get; }

        /// <summary>目标策略：固定目标 或 区域内全部合格目标（来自 <c>ActionSpec</c>，不经仲裁推导）。</summary>
        public TargetPolicy TargetPolicy { get; }

        /// <summary><see cref="TargetPolicy.PrimaryTargetOnly"/> 时必须由计划固定（主方案 0.4.4）。</summary>
        public UnitId? PrimaryTargetUnitId { get; }

        /// <summary>攻击允许的目标关系掩码（唯一关系解析器使用的资格依据）。</summary>
        public TargetRelationMask AllowedTargetRelations { get; }

        /// <summary>攻击的反应可用性标签（Reactable/Blockable/Dodgeable）。</summary>
        public AttackTagMask Tags { get; }

        /// <summary>攻击朝向（用于向 <see cref="AttackPatternSpec.Directions"/> 查表）。</summary>
        public GridDirection Facing { get; }

        /// <summary>
        /// 已整数平移的攻击区域点集（规范升序、已去重）。
        /// 它<strong>只</strong>来自 <c>Directions[(int)Facing].GetTranslated(OwnerAnchor)</c>。
        /// </summary>
        public IReadOnlyList<TrianglePoint> AreaPoints => _areaPoints;

        /// <summary>命中 Tick：等于计划 <c>ActionPlan.ImpactTick</c>。</summary>
        public long ImpactTick { get; }

        /// <summary>交互优先级（交互矩阵冻结阶段；数字越小越先参与）。</summary>
        public int InteractionPriority { get; }

        /// <summary>逻辑层单调递增序号（不来自 <c>Time.frameCount</c>）。</summary>
        public long IntentSequence { get; }

        /// <summary>分通道伤害分量（正交载荷的第 1 部分）。</summary>
        public IReadOnlyList<DamageComponentSpec> DamageComponents => _damageComponents;

        /// <summary>
        /// 动量载荷（正交载荷的第 2 部分）：由 <c>Logic/Combat</c> 的伤害/动量流拥有并定义，
        /// 在 Intent 物化边界只量化一次，之后仲裁全链路只做整数运算。
        /// </summary>
        public MomentumPacket Momentum { get; }

        /// <summary>动量方向偏移步数（审计与哈希用；已体现在 <see cref="Momentum"/> 的方向里）。</summary>
        public int MomentumDirectionOffsetSteps { get; }

        /// <summary>
        /// 提交窗口（<strong>审计字段</strong>）。
        /// 全局 Intent 队列与冲突图<strong>不得</strong>读取它做过滤或排序：
        /// 不变量 5 要求仲裁读取本 Tick 全部有效动作，过去窗口提交的动作可与当前/未来窗口动作交互。
        /// </summary>
        public WindowId? SubmittedWindowId { get; }

        /// <summary>稳定排序键第一部分：命中 Tick。</summary>
        public long OrderTick => ImpactTick;

        /// <summary>
        /// 动量载荷的规范序列化（精确整数，无浮点、无区域设置依赖）。
        /// 方向用冻结枚举名而非枚举值，避免"数值即顺序"的隐式约定被序列化文本继承。
        /// </summary>
        public string MomentumCanonicalText
            => Momentum.Direction.ToString() + ":" + Momentum.MomentumUnits.ToString(CultureInfo.InvariantCulture)
               + ":" + (Momentum.ImpactProfileId.Value ?? string.Empty);

        /// <summary>
        /// 区域点集是否与另一攻击的区域相交（整数点集归并扫描；无浮点、无哈希顺序依赖）。
        /// 供"Attack 与 Attack 的有效区域相交"这条直接交互规则使用。
        /// </summary>
        public bool AreaIntersects(CombatIntent other)
        {
            if (other == null || _areaPoints.Length == 0 || other._areaPoints.Length == 0) return false;
            TrianglePoint[] left = _areaPoints;
            TrianglePoint[] right = other._areaPoints;
            int i = 0;
            int j = 0;
            while (i < left.Length && j < right.Length)
            {
                int byKey = left[i].CompareTo(right[j]);
                if (byKey == 0) return true;
                if (byKey < 0) i++;
                else j++;
            }
            return false;
        }

        /// <summary>是否与另一 Intent 出自同一计划（唯一性契约判定）。</summary>
        public bool SameSource(CombatIntent other)
            => other != null && ActionPlanId == other.ActionPlanId && ImpactTick == other.ImpactTick;

        /// <summary>
        /// 规范序列化（进入冲突图规范化文本与排列不变性断言）。
        /// 伤害量的 <c>float</c> 只按<strong>二进制位</strong>输出：图与队列从不对它做算术或比较，
        /// 因此浮点不参与任何判定，也不可能因区域设置或舍入改变结果。
        /// </summary>
        public string CanonicalText
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("intent|seq=").Append(IntentSequence.ToString(CultureInfo.InvariantCulture));
                sb.Append("|tick=").Append(ImpactTick.ToString(CultureInfo.InvariantCulture));
                sb.Append("|prio=").Append(InteractionPriority.ToString(CultureInfo.InvariantCulture));
                sb.Append("|plan=").Append(ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append("|spec=").Append(ActionSpecId.Value ?? string.Empty);
                sb.Append("|owner=").Append(OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append("|policy=").Append(TargetPolicy.ToString()).Append(':').Append((int)TargetPolicy);
                sb.Append("|primary=").Append(PrimaryTargetUnitId.HasValue
                    ? PrimaryTargetUnitId.Value.Value.ToString(CultureInfo.InvariantCulture)
                    : "-");
                sb.Append("|mask=").Append(AllowedTargetRelations.ToString()).Append(':').Append((int)AllowedTargetRelations);
                sb.Append("|tags=").Append(Tags.ToString()).Append(':').Append((int)Tags);
                sb.Append("|facing=").Append(Facing.ToString()).Append(':').Append((int)Facing);
                sb.Append("|mom=").Append(MomentumCanonicalText);
                sb.Append("|momOffset=").Append(MomentumDirectionOffsetSteps.ToString(CultureInfo.InvariantCulture));
                sb.Append("|area=");
                for (int i = 0; i < _areaPoints.Length; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append('(').Append(_areaPoints[i].X.ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(_areaPoints[i].Y.ToString(CultureInfo.InvariantCulture))
                      .Append(',').Append(_areaPoints[i].T.ToString(CultureInfo.InvariantCulture)).Append(')');
                }
                sb.Append("|dmg=");
                for (int i = 0; i < _damageComponents.Length; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(_damageComponents[i].ChannelId.Value ?? string.Empty).Append(':')
                      .Append(BitConverter.SingleToInt32Bits(_damageComponents[i].RawAmount)
                          .ToString("X8", CultureInfo.InvariantCulture)).Append(':')
                      .Append(_damageComponents[i].Tags.ToString()).Append(':')
                      .Append(((int)_damageComponents[i].Tags).ToString(CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        public override string ToString()
            => "intent#" + IntentSequence.ToString(CultureInfo.InvariantCulture)
               + "(plan=" + ActionPlanId.Value.ToString(CultureInfo.InvariantCulture)
               + ", owner=" + OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture)
               + ", tick=" + ImpactTick.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// 攻击 Intent 的构造与唯一性校验入口（任务包 1）。
    ///
    /// 全部失败都以稳定原因码抛出，绝不钳制、饱和或静默修正。
    /// </summary>
    public static class CombatIntentFactory
    {
        /// <summary>
        /// 由纯值请求构造攻击 Intent：查 <c>Directions[(int)Facing]</c> → 整数平移 → 规范排序去重。
        /// 不读取动作速度、不推导命中时刻、不旋转 Pattern、不读取任何单位运行时状态。
        /// </summary>
        public static CombatIntent Create(AttackIntentRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!request.ActionPlanId.IsValid || !request.OwnerUnitId.IsValid)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_IDENTITY_INVALID,
                    "plan=" + request.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture)
                    + " owner=" + request.OwnerUnitId.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (DefinitionIdValidation.ValidateFormat(request.ActionSpecId.Value) != null)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_IDENTITY_INVALID,
                    "spec=" + (request.ActionSpecId.Value ?? string.Empty));
            }

            if (request.IntentSequence <= 0)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_IDENTITY_INVALID,
                    "sequence=" + request.IntentSequence.ToString(CultureInfo.InvariantCulture));
            }

            if (!TargetRelationMasks.IsValid(request.AllowedTargetRelations))
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_RELATION_MASK_INVALID,
                    ((int)request.AllowedTargetRelations).ToString(CultureInfo.InvariantCulture));
            }

            if (request.TargetPolicy == TargetPolicy.PrimaryTargetOnly
                && (!request.PrimaryTargetUnitId.HasValue || !request.PrimaryTargetUnitId.Value.IsValid))
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_PRIMARY_TARGET_MISSING,
                    request.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            }

            TrianglePoint[] area = TranslatePatternArea(request.Pattern, request.Facing, request.OwnerAnchor);

            DamageComponentSpec[] damage = CopyAndValidateDamage(request.DamageComponents);

            if (!request.Momentum.IsValid)
            {
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "momentum=" + request.Momentum.ToString());
            }

            if (DefinitionIdValidation.ValidateFormat(request.Momentum.ImpactProfileId.Value) != null)
            {
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "impactProfile=" + (request.Momentum.ImpactProfileId.Value ?? string.Empty));
            }

            return new CombatIntent(
                request.ActionPlanId,
                request.ActionSpecId,
                request.OwnerUnitId,
                request.TargetPolicy,
                request.PrimaryTargetUnitId,
                request.AllowedTargetRelations,
                request.Tags,
                request.Facing,
                area,
                request.ImpactTick,
                request.InteractionPriority,
                request.IntentSequence,
                damage,
                request.Momentum,
                request.MomentumDirectionOffsetSteps,
                request.SubmittedWindowId);
        }

        /// <summary>
        /// 由只读计划事实 + 攻击载荷构造攻击 Intent：<paramref name="tick"/> 必须<strong>恰好</strong>等于
        /// 计划的 <c>ImpactTick</c>（唯一性契约的前半），且计划必须非终态。
        ///
        /// 刻意<strong>不</strong>检查计划的"Recovery/Active"等阶段状态：计划在 <c>ImpactTick</c> 上
        /// 可以已经进入 Recovery，合法 Intent 不得因此被过滤（08-多方仲裁与伤害.md:59）。
        /// </summary>
        public static CombatIntent CreateAtImpactTick(
            InteractionPlanFacts facts,
            ActionSpecId actionSpecId,
            GridDirection facing,
            AttackPayloadSpec payload,
            GridPoint ownerAnchor,
            long tick,
            int interactionPriority,
            long intentSequence,
            MomentumPacket momentum,
            int momentumDirectionOffsetSteps,
            WindowId? submittedWindowId)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            CombatIntentContract.ValidateMaterialization(facts, tick);

            return Create(new AttackIntentRequest(
                facts.ActionPlanId,
                actionSpecId,
                facts.OwnerUnitId,
                payload.TargetPolicy,
                facts.PrimaryTargetUnitId,
                payload.AllowedTargetRelations,
                payload.Tags,
                facing,
                payload.Pattern,
                ownerAnchor,
                facts.ImpactTick,
                interactionPriority,
                intentSequence,
                payload.DamageComponents,
                momentum,
                momentumDirectionOffsetSteps,
                submittedWindowId));
        }

        /// <summary>
        /// 只做"查表 + 整数平移 + 规范排序去重"，不旋转、不插值。
        /// 之所以公开：装配侧与测试需要在不构造完整 Intent 的情况下核对区域来源。
        /// </summary>
        public static TrianglePoint[] TranslatePatternArea(AttackPatternSpec pattern, GridDirection facing, GridPoint anchor)
        {
            if (pattern == null)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_PATTERN_DIRECTIONS_DEGENERATE,
                    "pattern=null");
            }

            IReadOnlyList<DirectionalTriangleSet> directions = pattern.Directions;
            if (directions == null || directions.Count != GridDirectionInfo.DirectionCount)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_PATTERN_DIRECTIONS_DEGENERATE,
                    "count=" + (directions == null ? "null" : directions.Count.ToString(CultureInfo.InvariantCulture)));
            }

            if (!GridDirectionInfo.IsValidIndex((int)facing))
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_PATTERN_DIRECTIONS_DEGENERATE,
                    "facing=" + ((int)facing).ToString(CultureInfo.InvariantCulture));
            }

            DirectionalTriangleSet set = directions[(int)facing];
            if (set == null)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_PATTERN_DIRECTIONS_DEGENERATE,
                    "facing=" + ((int)facing).ToString(CultureInfo.InvariantCulture));
            }

            if (set.Direction != facing)
            {
                throw new LogicDefinitionException(InteractionCodes.INTENT_PATTERN_DIRECTION_MISMATCH,
                    "index=" + ((int)facing).ToString(CultureInfo.InvariantCulture)
                    + " entry=" + set.Direction.ToString());
            }

            // 唯一允许的空间消费方式：查表 → 对所有者逻辑格做整数平移。
            return InteractionCollections.CanonicalTriangles(set.GetTranslated(anchor));
        }

        private static DamageComponentSpec[] CopyAndValidateDamage(IReadOnlyList<DamageComponentSpec> components)
        {
            if (components == null || components.Count == 0) return Array.Empty<DamageComponentSpec>();
            var copy = new DamageComponentSpec[components.Count];
            for (int i = 0; i < components.Count; i++)
            {
                string code = DamageComponentValidation.Validate(components[i]);
                if (code != null)
                {
                    throw new LogicDefinitionException(code, "index=" + i.ToString(CultureInfo.InvariantCulture));
                }
                copy[i] = components[i];
            }
            return copy;
        }
    }

    /// <summary>
    /// 攻击 Intent 的<strong>显式唯一性契约校验入口</strong>（任务包 1）。
    /// 调用方（阶段 8 的 Intent 物化点与其装配）必须在物化前后各调用一次：
    /// 物化前 <see cref="ValidateMaterialization"/>（计划在 ImpactTick 恰好到期且非终态），
    /// 物化后 <see cref="ValidateProducedOnce"/>（同一 Tick 内每个计划至多一个 Intent、序号唯一）。
    /// 违反时抛稳定原因码，绝不静默去重。
    /// </summary>
    public static class CombatIntentContract
    {
        /// <summary>非抛出版本：合法返回 null，否则返回稳定原因码。</summary>
        public static string TryValidateMaterialization(InteractionPlanFacts facts, long tick)
        {
            if (facts == null) return InteractionCodes.INTENT_IDENTITY_INVALID;
            if (!facts.ActionPlanId.IsValid || !facts.OwnerUnitId.IsValid) return InteractionCodes.INTENT_IDENTITY_INVALID;
            if (facts.ActionType != ActionType.Attack) return InteractionCodes.INTENT_NOT_ATTACK;
            if (facts.IsTerminal) return InteractionCodes.INTENT_PLAN_TERMINAL;
            if (facts.ImpactTick != tick) return InteractionCodes.INTENT_TICK_MISMATCH;
            return null;
        }

        /// <summary>计划必须非终态、是攻击动作、且本 Tick 恰好等于其 <c>ImpactTick</c>。</summary>
        public static void ValidateMaterialization(InteractionPlanFacts facts, long tick)
        {
            string code = TryValidateMaterialization(facts, tick);
            if (code == null) return;

            throw new LogicDefinitionException(code,
                "plan=" + (facts == null ? "-" : facts.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture))
                + " tick=" + tick.ToString(CultureInfo.InvariantCulture)
                + " impact=" + (facts == null ? "-" : facts.ImpactTick.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// 同一 Tick 产出的 Intent 集合必须满足：每个 (ActionPlanId, ImpactTick) 至多一个，
        /// 且 <c>IntentSequence</c> 全局唯一（否则"稳定键"退化为偏序，排列不变性无法成立）。
        /// 本方法只看值，不受输入顺序影响。
        /// </summary>
        public static void ValidateProducedOnce(IReadOnlyList<CombatIntent> produced)
        {
            if (produced == null || produced.Count == 0) return;

            var bySequence = new long[produced.Count];
            var byPlan = new long[produced.Count];
            int count = 0;
            for (int i = 0; i < produced.Count; i++)
            {
                CombatIntent intent = produced[i];
                if (intent == null) continue;
                bySequence[count] = intent.IntentSequence;
                byPlan[count] = intent.ActionPlanId.Value;
                count++;
            }

            Array.Sort(bySequence, 0, count);
            for (int i = 1; i < count; i++)
            {
                if (bySequence[i - 1] == bySequence[i])
                {
                    throw new LogicDefinitionException(InteractionCodes.INTENT_SEQUENCE_DUPLICATE,
                        bySequence[i].ToString(CultureInfo.InvariantCulture));
                }
            }

            Array.Sort(byPlan, 0, count);
            for (int i = 1; i < count; i++)
            {
                if (byPlan[i - 1] == byPlan[i])
                {
                    throw new LogicDefinitionException(InteractionCodes.INTENT_DUPLICATE,
                        byPlan[i].ToString(CultureInfo.InvariantCulture));
                }
            }
        }
    }
}
