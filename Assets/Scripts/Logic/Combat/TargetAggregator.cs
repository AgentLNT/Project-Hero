using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    /// <summary>接触类型（只用于聚合排序与审计；不是伤害类型）。</summary>
    public enum TargetContactType
    {
        /// <summary>直接 Hit（含兼容冲击伤害分量）。</summary>
        DirectHit = 0,

        /// <summary>Clash 剩余冲击（<c>ClashResidualImpact</c>）。</summary>
        ClashResidualImpact = 1
    }

    /// <summary>
    /// 目标聚合的规范接触键（主方案 0.4.2.1 <c>:421</c> 的 <c>ContactKey → DamageChannelId</c> 排序键一侧）。
    ///
    /// 构图流的 <c>ContactKey</c> 是它的超集（接触类型 + 双方稳定 UnitId + 双方 ActionPlanId + 目标 UnitId）；
    /// 这里冻结的是<strong>聚合排序所需的最小投影</strong>，顺序为
    /// <c>接触类型 → 来源 UnitId → 来源 ActionPlanId → 目标 UnitId → 目标 ActionPlanId</c>。
    /// "两个攻击同时命中同一目标"时来源计划不同，因此排序稳定且与发现顺序无关。
    /// </summary>
    public readonly struct TargetContactKey : IEquatable<TargetContactKey>, IComparable<TargetContactKey>
    {
        public readonly TargetContactType ContactType;
        public readonly long SourceUnitId;
        public readonly long SourceActionPlanId;
        public readonly long TargetUnitId;
        public readonly long TargetActionPlanId;

        public TargetContactKey(TargetContactType contactType, UnitId sourceUnitId, ActionPlanId sourceActionPlanId,
            UnitId targetUnitId, ActionPlanId targetActionPlanId)
        {
            ContactType = contactType;
            SourceUnitId = sourceUnitId.Value;
            SourceActionPlanId = sourceActionPlanId.Value;
            TargetUnitId = targetUnitId.Value;
            TargetActionPlanId = targetActionPlanId.Value;
        }

        public int CompareTo(TargetContactKey other)
        {
            int byType = ((int)ContactType).CompareTo((int)other.ContactType);
            if (byType != 0) return byType;
            int bySourceUnit = SourceUnitId.CompareTo(other.SourceUnitId);
            if (bySourceUnit != 0) return bySourceUnit;
            int bySourcePlan = SourceActionPlanId.CompareTo(other.SourceActionPlanId);
            if (bySourcePlan != 0) return bySourcePlan;
            int byTargetUnit = TargetUnitId.CompareTo(other.TargetUnitId);
            if (byTargetUnit != 0) return byTargetUnit;
            return TargetActionPlanId.CompareTo(other.TargetActionPlanId);
        }

        public bool Equals(TargetContactKey other)
            => ContactType == other.ContactType && SourceUnitId == other.SourceUnitId
               && SourceActionPlanId == other.SourceActionPlanId && TargetUnitId == other.TargetUnitId
               && TargetActionPlanId == other.TargetActionPlanId;

        public override bool Equals(object obj) => obj is TargetContactKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine((int)ContactType, SourceUnitId, SourceActionPlanId, TargetUnitId, TargetActionPlanId);

        public override string ToString()
            => ((int)ContactType).ToString(CultureInfo.InvariantCulture) + ":" +
               SourceUnitId.ToString(CultureInfo.InvariantCulture) + ":" +
               SourceActionPlanId.ToString(CultureInfo.InvariantCulture) + "->" +
               TargetUnitId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 一条目标接触（直接 Hit 或 Clash 剩余冲击）。动量与伤害分量<strong>正交</strong>
    /// （00 号规则 24）：<see cref="DamageComponents"/> 是分通道载荷，动量的抵抗是独立维度。
    ///
    /// <see cref="ActionMomentumResistanceQ10"/> 与 <see cref="ActionDamageResistanceQ10"/> 由
    /// Guard/Block 阶段<strong>显式解析后</strong>传入（没有活动防御就传 0 / 空表），
    /// 不从 UnitState、Controller 或 <c>CanReceiveDirectHit</c> 推断。
    /// 便捷构造见 <see cref="TargetAggregator.BuildDefendedContact"/>。
    /// </summary>
    public sealed record TargetContact(
        TargetContactKey Key,
        GridDirection IncomingDirection,
        int IncomingMomentumUnits,
        int ActionMomentumResistanceQ10,
        IReadOnlyDictionary<DamageChannelId, int> ActionDamageResistanceQ10,
        IReadOnlyList<DamageComponentSpec> DamageComponents);

    /// <summary>目标侧聚合输入（被动抵抗来自目标定义；控制阻力已量化一次）。</summary>
    public sealed record TargetAggregationInput(
        UnitId TargetUnitId,
        IReadOnlyDictionary<DamageChannelId, int> PassiveDamageResistanceQ10,
        int ControlResistanceUnits,
        IReadOnlyList<TargetContact> Contacts);

    /// <summary>单个伤害分量的三段数值（事件条目 107/112 需要的 Raw / 被动后 / 动作后）。</summary>
    public sealed record AggregatedContactComponent(
        DamageChannelId ChannelId,
        long RawQ10,
        long AfterPassiveResistanceQ10,
        long AfterActionResistanceQ10,
        int PassiveResistanceQ10,
        int ActionResistanceQ10,
        DamageTagMask Tags);

    /// <summary>按 <c>DamageChannelId</c> 的分通道求和（主方案 0.4.2.1 <c>:421</c>）。</summary>
    public sealed record AggregatedChannelDamage(
        DamageChannelId ChannelId,
        long RawQ10,
        long AfterPassiveResistanceQ10,
        long AfterActionResistanceQ10);

    /// <summary>单个接触的聚合结果。</summary>
    public sealed record TargetContactResolution(
        TargetContactKey Key,
        GridDirection IncomingDirection,
        int IncomingMomentumUnits,
        int AfterMomentumResistanceUnits,
        IReadOnlyList<AggregatedContactComponent> Components);

    /// <summary>
    /// 任务包「必须产出」11 的目标聚合结果（主方案 0.4.2.1 五步 + 0.4.5 步骤 7-9）。
    ///
    /// 三个结果彼此独立（主方案 0.4.2 <c>:300</c>）：<see cref="TotalDamageQ10"/>（伤害）、
    /// <see cref="TotalImpactUnits"/>（硬直/击倒读的总冲击）、
    /// <see cref="ResultantDirection"/>/<see cref="ResultantMomentumUnits"/>（击退读的有向合力）。
    /// <strong>反向合力抵消不得抵消已产生的伤害与总冲击</strong>：等强反向夹击时
    /// <see cref="ResultantDirection"/> 为 null、<see cref="KnockbackSteps"/> 为 0，
    /// 而伤害与 <see cref="TotalImpactUnits"/> 照常累加。
    /// </summary>
    public sealed record TargetAggregateResolution(
        UnitId TargetUnitId,
        IReadOnlyList<TargetContactResolution> Contacts,
        IReadOnlyList<AggregatedChannelDamage> ChannelTotals,
        long TotalDamageQ10,
        long TotalImpactUnits,
        long IncomingMomentumUnits,
        long ResultantVectorXQ10,
        long ResultantVectorYQ10,
        GridDirection? ResultantDirection,
        long ResultantMomentumUnits,
        int ControlResistanceUnits,
        long StaggerThresholdUnits,
        long KnockdownThresholdUnits,
        bool IsStaggered,
        bool IsKnockedDown,
        int KnockbackSteps);

    /// <summary>
    /// 任务包「必须产出」11：<strong>分通道目标聚合器</strong>（主方案 <c>0.4.2.1</c> 五步）。
    ///
    /// <strong>步骤 0/1（逐分量：一次量化 → 被动抵抗 → 动作抵抗）</strong>
    /// <code>
    /// RawQ10            = RoundHalfUp(RawAmount × 1024)                       // 口径裁定 P0-1：唯一一次量化
    /// AfterPassive      = Has(BypassPassiveResistance) ? RawQ10
    ///                   : RoundHalfUp(RawQ10 × (1024 - Target.BaseDamageResistanceQ10[channel]) / 1024)
    /// AfterAction       = Has(BypassActionResistance)  ? AfterPassive
    ///                   : RoundHalfUp(AfterPassive × (1024 - ActiveDefense.DamageResistanceQ10[channel]) / 1024)
    /// AfterMomentum     = RoundHalfUp(IncomingMomentumUnits × (1024 - ActiveDefense.MomentumResistanceQ10) / 1024)
    /// </code>
    /// 抵抗<strong>相乘、不相加</strong>（依次乘算）；<c>Block</c> 对合格分量/动量用 1024 ⇒ 严格为 0；
    /// 禁止用单个 <c>DamageReduction</c> 同时覆盖所有通道与动量（00 号规则 24）。
    /// 整数除法一律复用既有唯一舍入原语 <c>TickQuantization.RoundHalfUpDivide</c>（不写第二份舍入）。
    ///
    /// <strong>步骤 2（稳定排序 + 分通道求和）</strong>：接触先按
    /// <see cref="TargetContactKey"/> 升序、分量再按 <c>DamageChannelId</c> 序数升序；
    /// 用 <c>checked long</c> 分通道求和（因此不存在"按容器发现顺序逐次 float 累加"的路径）。
    ///
    /// <strong>步骤 3/4（控制与合力）</strong>
    /// <code>
    /// TotalImpactUnits  = Σ 抵抗后入射动量绝对值
    /// ResultantVector   = Σ DirectionVectorQ10(direction) × 抵抗后动量
    /// 合力方向 = 与合力点积最大的 GridDirection，并列取枚举值较小者；最大点积 ≤ 0 ⇒ 无方向
    /// 合力强度 = maxDot / 1024² 向下取整（= Σ 动量·cosθ，与动量/CRU 同域；裁定 5 · 读法 B）；
    /// KnockbackSteps = floor(强度 / ControlResistanceUnits)
    /// 硬直阈值 = ControlResistanceUnits；击倒阈值 = ceil(ControlResistanceUnits × 3 / 2)
    /// </code>
    /// </summary>
    public static class TargetAggregator
    {
        /// <summary>聚合（<see cref="TargetAggregationInput.ControlResistanceUnits"/> 必须已按冻结公式量化）。</summary>
        public static TargetAggregateResolution Aggregate(TargetAggregationInput input)
        {
            try
            {
                return AggregateCore(input);
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE, ex.Message);
            }
        }

        /// <summary>
        /// 分通道伤害只转换一次为逻辑血量域（口径裁定 P0-2：目标是 <c>int</c> Q10，不是 float）。
        /// 供扣血接口 <c>IStatusEffectHost.ApplyDamageQ10(int)</c> 直接消费。
        /// </summary>
        public static int AggregateDamageQ10(TargetAggregationInput input)
            => (int)Aggregate(input).TotalDamageQ10;

        private static TargetAggregateResolution AggregateCore(TargetAggregationInput input)
        {
            if (input == null || !input.TargetUnitId.IsValid)
                throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID, "target");
            if (input.ControlResistanceUnits <= 0)
                throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID,
                    "controlResistanceUnits=" + input.ControlResistanceUnits.ToString(CultureInfo.InvariantCulture));

            var contacts = new List<TargetContact>(input.Contacts ?? (IReadOnlyList<TargetContact>)Array.Empty<TargetContact>());
            for (int i = 0; i < contacts.Count; i++)
            {
                if (contacts[i] == null)
                    throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID, "contact");
                if (contacts[i].Key.TargetUnitId != input.TargetUnitId.Value)
                    throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID, "contactTargetMismatch");
                if (contacts[i].IncomingMomentumUnits < 0)
                    throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                        "incomingMomentumUnits=" + contacts[i].IncomingMomentumUnits.ToString(CultureInfo.InvariantCulture));
                RequireResistanceQ10(contacts[i].ActionMomentumResistanceQ10, "actionMomentumResistanceQ10");
            }
            contacts.Sort((x, y) => x.Key.CompareTo(y.Key));
            for (int i = 1; i < contacts.Count; i++)
            {
                if (contacts[i - 1].Key.Equals(contacts[i].Key))
                    throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID, "duplicateContactKey");
            }

            // —— 步骤 1：逐分量（一次量化 → 被动抵抗 → 动作抵抗）——
            var resolutions = new List<TargetContactResolution>(contacts.Count);
            long totalImpactUnits = 0L;
            long incomingMomentum = 0L;
            long resultantX = 0L;
            long resultantY = 0L;

            for (int c = 0; c < contacts.Count; c++)
            {
                TargetContact contact = contacts[c];
                long afterMomentum = TickQuantization.RoundHalfUpDivide(
                    (long)contact.IncomingMomentumUnits *
                    (TickQuantizationQ10 - contact.ActionMomentumResistanceQ10), TickQuantizationQ10);

                var components = new List<AggregatedContactComponent>(
                    (contact.DamageComponents ?? (IReadOnlyList<DamageComponentSpec>)Array.Empty<DamageComponentSpec>()).Count);
                var componentSource = new List<DamageComponentSpec>(
                    contact.DamageComponents ?? (IReadOnlyList<DamageComponentSpec>)Array.Empty<DamageComponentSpec>());
                componentSource.Sort((x, y) => CompareComponents(x, y));

                for (int k = 0; k < componentSource.Count; k++)
                {
                    DamageComponentSpec component = componentSource[k];
                    string componentError = DamageComponentValidation.Validate(component);
                    if (componentError != null)
                        throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID, componentError);

                    int passiveResistance = TagBypasses(component.Tags, DamageTagMask.BypassPassiveResistance)
                        ? 0
                        : ResistanceFor(input.PassiveDamageResistanceQ10, component.ChannelId);
                    RequireResistanceQ10(passiveResistance, "passiveResistanceQ10");
                    int actionResistance = TagBypasses(component.Tags, DamageTagMask.BypassActionResistance)
                        ? 0
                        : ResistanceFor(contact.ActionDamageResistanceQ10, component.ChannelId);
                    RequireResistanceQ10(actionResistance, "actionResistanceQ10");

                    long rawQ10 = MomentumQuantizer.QuantizeDamageQ10(component.RawAmount);
                    long afterPassive = TagBypasses(component.Tags, DamageTagMask.BypassPassiveResistance)
                        ? rawQ10
                        : TickQuantization.RoundHalfUpDivide(rawQ10 * (TickQuantizationQ10 - passiveResistance),
                            TickQuantizationQ10);
                    long afterAction = TagBypasses(component.Tags, DamageTagMask.BypassActionResistance)
                        ? afterPassive
                        : TickQuantization.RoundHalfUpDivide(afterPassive * (TickQuantizationQ10 - actionResistance),
                            TickQuantizationQ10);

                    components.Add(new AggregatedContactComponent(component.ChannelId, rawQ10, afterPassive,
                        afterAction, passiveResistance, actionResistance, component.Tags));
                }

                DirectionVectorQ10 vector = MomentumRuleTable.DirectionVectorQ10(contact.IncomingDirection);
                resultantX = checked(resultantX + vector.X * afterMomentum);
                resultantY = checked(resultantY + vector.Y * afterMomentum);
                totalImpactUnits = checked(totalImpactUnits + afterMomentum);
                incomingMomentum = checked(incomingMomentum + contact.IncomingMomentumUnits);

                resolutions.Add(new TargetContactResolution(contact.Key, contact.IncomingDirection,
                    contact.IncomingMomentumUnits, (int)afterMomentum, components.AsReadOnly()));
            }

            // —— 步骤 2：按通道求和（规范排序；check ed long，最后只转换一次到逻辑血量域）——
            var channelTotals = new List<AggregatedChannelDamage>();
            for (int c = 0; c < resolutions.Count; c++)
            {
                IReadOnlyList<AggregatedContactComponent> components = resolutions[c].Components;
                for (int k = 0; k < components.Count; k++)
                {
                    AggregatedContactComponent component = components[k];
                    int index = FindChannel(channelTotals, component.ChannelId);
                    if (index < 0)
                    {
                        channelTotals.Add(new AggregatedChannelDamage(component.ChannelId, component.RawQ10,
                            component.AfterPassiveResistanceQ10, component.AfterActionResistanceQ10));
                    }
                    else
                    {
                        AggregatedChannelDamage existing = channelTotals[index];
                        channelTotals[index] = new AggregatedChannelDamage(existing.ChannelId,
                            checked(existing.RawQ10 + component.RawQ10),
                            checked(existing.AfterPassiveResistanceQ10 + component.AfterPassiveResistanceQ10),
                            checked(existing.AfterActionResistanceQ10 + component.AfterActionResistanceQ10));
                    }
                }
            }
            channelTotals.Sort((x, y) => string.CompareOrdinal(x.ChannelId.Value ?? string.Empty, y.ChannelId.Value ?? string.Empty));

            long totalDamageQ10 = 0L;
            for (int i = 0; i < channelTotals.Count; i++)
                totalDamageQ10 = checked(totalDamageQ10 + channelTotals[i].AfterActionResistanceQ10);
            if (totalDamageQ10 > int.MaxValue)
                throw new LogicDefinitionException(MomentumCodes.DAMAGE_AGGREGATE_OUT_OF_RANGE,
                    "totalDamageQ10=" + totalDamageQ10.ToString(CultureInfo.InvariantCulture));

            // —— 步骤 3/4：控制阈值、有向合力、击退 ——
            bool hasDirection = MomentumRuleTable.TrySelectResultantDirection(resultantX, resultantY,
                out GridDirection direction, out long maxDot);
            // 裁定 5（读法 B）：方向向量是 1024 标度、动量是**已量化的** MomentumUnits，
            // 因此 maxDot = 1024² × Σ(m·cosθ)；必须除两次 1024 才回到动量域（强度 = Σm·cosθ）。
            // 只除一次会得到 1024× 偏大的强度（单个 1000 动量对齐攻击 ⇒ 1024×1000 ⇒ 荒谬步数）。
            long resultantMomentumUnits = hasDirection
                ? maxDot / (MomentumRuleTable.Q10One * MomentumRuleTable.Q10One)
                : 0L;
            long knockbackSteps = hasDirection
                ? resultantMomentumUnits / input.ControlResistanceUnits
                : 0L;
            if (knockbackSteps > int.MaxValue)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "knockbackSteps=" + knockbackSteps.ToString(CultureInfo.InvariantCulture));

            long staggerThreshold = input.ControlResistanceUnits;
            long knockdownThreshold = ((long)input.ControlResistanceUnits * 3L + 1L) / 2L;   // ceil(3n/2)

            return new TargetAggregateResolution(
                input.TargetUnitId,
                resolutions.AsReadOnly(),
                channelTotals.AsReadOnly(),
                totalDamageQ10,
                totalImpactUnits,
                incomingMomentum,
                resultantX,
                resultantY,
                hasDirection ? direction : (GridDirection?)null,
                resultantMomentumUnits,
                input.ControlResistanceUnits,
                staggerThreshold,
                knockdownThreshold,
                totalImpactUnits >= staggerThreshold,
                totalImpactUnits >= knockdownThreshold,
                (int)knockbackSteps);
        }

        /// <summary>
        /// Guard/Block 抵抗的<strong>唯一解析点</strong>：按分量标签与防御方 <c>EligibleTags</c> 决定
        /// 该分量的动作抵抗与动量抵抗。<c>Block</c> 对合格载荷固定 1024（完全抵抗 ⇒ 结果严格为 0）；
        /// <c>Guard</c> 只缩放 <c>Guardable</c> 分量与可格挡动量；不合格分量继续正常结算。
        /// 传入 <c>null</c> 表示该防御在<strong>本 Tick</strong>不生效（由防御阶段判定，不从 UnitState 推断）。
        /// </summary>
        public static TargetContact BuildDefendedContact(
            TargetContactKey key,
            GridDirection incomingDirection,
            int incomingMomentumUnits,
            IReadOnlyList<DamageComponentSpec> components,
            AttackTagMask attackTags,
            GuardPayloadSpec guard,
            BlockPayloadSpec block)
        {
            var channelResistance = new Dictionary<DamageChannelId, int>();
            if (components != null)
            {
                for (int i = 0; i < components.Count; i++)
                {
                    DamageComponentSpec component = components[i];
                    int resistance = 0;
                    if (block != null && HasDefenseTag(block.EligibleTags, DefenseTagMask.Blockable) &&
                        TagBypasses(component.Tags, DamageTagMask.Blockable))
                    {
                        resistance = BattleRules.FullBlockResistanceQ10;
                    }
                    else if (guard != null && HasDefenseTag(guard.EligibleTags, DefenseTagMask.Guardable) &&
                             TagBypasses(component.Tags, DamageTagMask.Guardable))
                    {
                        resistance = ResistanceFor(guard.DamageResistanceQ10, component.ChannelId);
                    }

                    // 动作抵抗按通道索引（主方案 :407-410 的 ActiveDefense.DamageResistanceQ10[Channel]）。
                    // 同一通道出现多个分量且合格性不同时取<strong>最大</strong>抵抗（最保守）：
                    // 不合格的其它通道分量仍然完全不受影响（"Block 只修改该防御者载荷"）。
                    if (channelResistance.TryGetValue(component.ChannelId, out int existing) && existing >= resistance)
                        continue;
                    channelResistance[component.ChannelId] = resistance;
                }
            }

            int momentumResistance = 0;
            if (block != null && HasDefenseTag(block.EligibleTags, DefenseTagMask.Blockable) &&
                (attackTags & AttackTagMask.Blockable) != 0)
            {
                momentumResistance = BattleRules.FullBlockResistanceQ10;
            }
            else if (guard != null && HasDefenseTag(guard.EligibleTags, DefenseTagMask.Guardable))
            {
                // AttackTagMask（既有定义契约，冻结）只有 Reactable/Blockable/Dodgeable，
                // **没有** Guardable 位，因此 Guard 的动量抵抗门槛只能来自防御方自己的
                // EligibleTags（本接触被活动 Guard 覆盖），不从攻击标签推断、也不新增枚举成员。
                momentumResistance = guard.MomentumResistanceQ10;
            }

            return new TargetContact(key, incomingDirection, incomingMomentumUnits, momentumResistance,
                channelResistance, components);
        }

        private const int TickQuantizationQ10 = MomentumQuantizer.HealthQ10PerLifePoint;

        private static int FindChannel(List<AggregatedChannelDamage> totals, DamageChannelId channelId)
        {
            for (int i = 0; i < totals.Count; i++)
            {
                if (totals[i].ChannelId == channelId) return i;
            }
            return -1;
        }

        private static int CompareComponents(DamageComponentSpec x, DamageComponentSpec y)
            => string.CompareOrdinal(x.ChannelId.Value ?? string.Empty, y.ChannelId.Value ?? string.Empty);

        private static int ResistanceFor(IReadOnlyDictionary<DamageChannelId, int> table, DamageChannelId channelId)
        {
            if (table == null) return 0;
            return table.TryGetValue(channelId, out int value) ? value : 0;
        }

        private static bool TagBypasses(DamageTagMask tags, DamageTagMask flag) => (tags & flag) != 0;

        private static bool HasDefenseTag(DefenseTagMask mask, DefenseTagMask flag) => (mask & flag) != 0;

        private static void RequireResistanceQ10(int value, string label)
        {
            if (value < 0 || value > MomentumQuantizer.HealthQ10PerLifePoint)
                throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID,
                    label + "=" + value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
