using System;
using System.Globalization;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 任务包「必须产出」2：<strong>动量量化器</strong>——从只读单位快照与 ActionPlan 固定朝向
    /// <strong>只算一次</strong>的整数动量。
    ///
    /// <strong>冻结公式（逐字，不得改写）</strong>
    /// <code>
    /// MomentumUnits = max(1, RoundHalfUp(Mass * MomentumSpeed * ForceMultiplier * ImpactTransferPercent[ImpactProfileId]))
    /// </code>
    /// <c>ImpactTransferPercent</c> 兼容旧 Blunt/Slash/Pierce 的 <c>100 / 60 / 30</c>
    /// （= 旧 Kw <c>1.0 / 0.6 / 0.3</c>）；<c>ImpactProfileId</c> <strong>不再充当伤害类型</strong>
    /// （00 号规则 24：通道与动量正交）。
    ///
    /// <strong>量化发生在哪一步 / 之后为什么全程整数（口径承诺）</strong>
    /// <list type="number">
    /// <item>唯一入口：<see cref="QuantizePacket"/>／<see cref="QuantizeMomentumUnits"/>，
    /// 由任务 05 的计划生命周期在 <c>ActionPlan.ImpactTick</c> 产生攻击 Intent 时调用<strong>恰好一次</strong>。</item>
    /// <item>本类的浮点参数（<c>Mass / MomentumSpeed / ForceMultiplier</c>）是<strong>只读快照上的定义值</strong>，
    /// 在此处被读完后立即消失：乘积用 <c>double</c> 计算（IEEE-754 确定性）、一次
    /// <c>Math.Round(AwayFromZero)</c>（即正数 half-up）落到 <c>int</c>。</item>
    /// <item>输出只有 <see cref="MomentumPacket"/>（<c>GridDirection + int</c>）：损耗、剩余、分配、
    /// 聚合全链路只做 checked 整数运算，<strong>不再读取任何浮点属性</strong>。
    /// 该结构性保证由 <c>Task08MomentumTests.MomentumQuantizationOccursOnceBeforeArbitration</c> 反射守护。</item>
    /// </list>
    ///
    /// <strong>非法输入</strong>：<c>Mass / MomentumSpeed / ForceMultiplier / legacyDeliveredMomentum</c>
    /// 必须是有限正数（控制阻力另见 <see cref="QuantizeControlResistanceUnits"/>），
    /// 冲击 Profile 必须已知；结果必须落在正 <c>int</c>（≥1 且 ≤ <see cref="MomentumLimits.MaxMomentumUnitsPerIntent"/>）。
    /// 任一不满足即以 <see cref="MomentumCodes.MOMENTUM_OUT_OF_RANGE"/> 稳定拒绝，<strong>不得</strong>钳制、
    /// 饱和或回绕（任务包 08:99 / 08:105）。
    /// </summary>
    public static class MomentumQuantizer
    {
        /// <summary><c>MomentumUnitsPerLegacyMomentum = 100</c>（任务包 08:98 冻结）。</summary>
        public const int MomentumUnitsPerLegacyMomentum = 100;

        /// <summary><c>NormalImpactDamageDivisor = 5000</c>（任务包 08:100 冻结）。</summary>
        public const int NormalImpactDamageDivisor = 5000;

        /// <summary><c>ClashResidualDamageDivisor = 1000</c>（任务包 08:100 冻结）。</summary>
        public const int ClashResidualDamageDivisor = 1000;

        /// <summary>控制阻力系数 <c>100</c>（任务包 08:103 冻结公式里的乘数）。</summary>
        public const int ControlResistancePerUnit = 100;

        /// <summary>Q10 基准：1 生命点 = 1024 <c>HealthQ10</c>（既有唯一量化约定）。</summary>
        public const int HealthQ10PerLifePoint = 1024;

        /// <summary>
        /// 攻击 Intent 的整数动量（<strong>唯一一次</strong>量化）：
        /// <c>max(1, RoundHalfUp(Mass * MomentumSpeed * ForceMultiplier * ImpactTransferPercent))</c>。
        /// </summary>
        public static int QuantizeMomentumUnits(float mass, float momentumSpeed, float forceMultiplier,
            ImpactProfileId impactProfileId)
        {
            RequireFinitePositive(mass, "mass");
            RequireFinitePositive(momentumSpeed, "momentumSpeed");
            RequireFinitePositive(forceMultiplier, "forceMultiplier");
            int transferPercent = TransferPercent(impactProfileId);
            return QuantizePositiveProduct((double)mass * momentumSpeed * forceMultiplier * transferPercent,
                "momentum");
        }

        /// <summary>
        /// 旧数据兼容量化（任务包「验收标准」:393）：<c>MomentumUnits == RoundHalfUp(legacyDeliveredMomentum * 100)</c>。
        /// 与 <see cref="QuantizeMomentumUnits"/> 用同一条 <c>max(1, RoundHalfUp(...))</c> 路径，
        /// 因此 Blunt 且 <c>legacyDeliveredMomentum == Mass * MomentumSpeed * ForceMultiplier</c> 时两者恒等。
        /// </summary>
        public static int QuantizeLegacyMomentumUnits(float legacyDeliveredMomentum)
        {
            RequireFinitePositive(legacyDeliveredMomentum, "legacyDeliveredMomentum");
            return QuantizePositiveProduct((double)legacyDeliveredMomentum * MomentumUnitsPerLegacyMomentum,
                "legacyMomentum");
        }

        /// <summary>
        /// 控制阻力（任务包 08:103）：
        /// <c>ControlResistanceUnits = max(1, RoundHalfUp(Target.Mass * Target.MomentumSpeed * 100))</c>。
        /// 它同样只在目标聚合边界读一次浮点属性，之后阈值比较全用整数。
        /// </summary>
        public static int QuantizeControlResistanceUnits(float mass, float momentumSpeed)
        {
            RequireFinitePositive(mass, "mass");
            RequireFinitePositive(momentumSpeed, "momentumSpeed");
            return QuantizePositiveProduct((double)mass * momentumSpeed * ControlResistancePerUnit,
                "controlResistance");
        }

        /// <summary>
        /// 一次性的「创作态生命点 → Q10 生命整数」量化（口径裁定 P0-1）：
        /// <c>RoundHalfUp(RawAmount × 1024)</c>。<c>RawAmount</c> 是创作态生命点（旧 <c>BaseDamage</c> 原样），
        /// 转换目标是 <c>int HealthQ10</c>（口径裁定 P0-2），<strong>不是 float</strong>。
        /// 本方法是该转换的<strong>唯一</strong>落点：聚合第一步对每个分量调用一次，之后全程整数。
        /// </summary>
        public static int QuantizeDamageQ10(float rawAmountLifePoints)
        {
            if (float.IsNaN(rawAmountLifePoints) || float.IsInfinity(rawAmountLifePoints) || rawAmountLifePoints < 0f)
                throw new LogicDefinitionException(MomentumCodes.TARGET_AGGREGATE_INVALID,
                    "rawAmountLifePoints=" + rawAmountLifePoints.ToString("R", CultureInfo.InvariantCulture));
            double scaled = (double)rawAmountLifePoints * HealthQ10PerLifePoint;
            long rounded;
            try
            {
                rounded = checked((long)Math.Round(scaled, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(MomentumCodes.DAMAGE_AGGREGATE_OUT_OF_RANGE, ex.Message);
            }
            if (rounded > int.MaxValue)
                throw new LogicDefinitionException(MomentumCodes.DAMAGE_AGGREGATE_OUT_OF_RANGE,
                    "damageQ10=" + rounded.ToString(CultureInfo.InvariantCulture));
            return (int)rounded;
        }

        /// <summary>
        /// 兼容冲击伤害分量（主方案 0.4.2.1 <c>:400-402</c>）：
        /// <c>DamageComponent(physical.blunt, MomentumUnits / 5000)</c>，整数除法（向下取整）。
        /// </summary>
        public static int NormalImpactDamageLifePoints(int momentumUnits)
        {
            if (momentumUnits < 0)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "momentumUnits=" + momentumUnits.ToString(CultureInfo.InvariantCulture));
            return momentumUnits / NormalImpactDamageDivisor;
        }

        /// <summary>
        /// Clash 剩余伤害分量（主方案 0.4.2.1 <c>:400-402</c>）：
        /// <c>DamageComponent(physical.blunt, ResidualMomentumUnits / 1000)</c>，整数除法（向下取整）。
        /// </summary>
        public static int ClashResidualDamageLifePoints(int residualMomentumUnits)
        {
            if (residualMomentumUnits < 0)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "residualMomentumUnits=" + residualMomentumUnits.ToString(CultureInfo.InvariantCulture));
            return residualMomentumUnits / ClashResidualDamageDivisor;
        }

        /// <summary>显式兼容冲击伤害分量（通道 <c>physical.blunt</c> + 该通道的默认标签）。</summary>
        public static DamageComponentSpec NormalImpactComponent(int momentumUnits)
            => new DamageComponentSpec(DamageChannels.PhysicalBlunt, NormalImpactDamageLifePoints(momentumUnits),
                DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt));

        /// <summary>显式 Clash 剩余伤害分量（只构造剩余动量分量，<strong>不得</strong>重复附加攻击基础分量）。</summary>
        public static DamageComponentSpec ClashResidualComponent(int residualMomentumUnits)
            => new DamageComponentSpec(DamageChannels.PhysicalBlunt, ClashResidualDamageLifePoints(residualMomentumUnits),
                DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt));

        /// <summary>
        /// 攻击 Intent 的动量载荷唯一构造入口：先按
        /// <c>Normalize12(Facing + MomentumDirectionOffsetSteps)</c> 定方向，再量化一次，得到纯整数
        /// <see cref="MomentumPacket"/>。
        /// </summary>
        public static MomentumPacket QuantizePacket(GridDirection facing, int momentumDirectionOffsetSteps,
            float mass, float momentumSpeed, float forceMultiplier, ImpactProfileId impactProfileId)
        {
            GridDirection direction = MomentumRuleTable.ResolveMomentumDirection(facing, momentumDirectionOffsetSteps);
            int units = QuantizeMomentumUnits(mass, momentumSpeed, forceMultiplier, impactProfileId);
            return MomentumPacket.Require(direction, units, impactProfileId);
        }

        private static int TransferPercent(ImpactProfileId impactProfileId)
        {
            try
            {
                return ImpactProfileCatalog.GetTransferPercent(impactProfileId);
            }
            catch (LogicDefinitionException)
            {
                // 未知冲击 Profile 与"动量越界"共用同一冻结码（任务包 08:99）。
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "impactProfileId=" + (impactProfileId.Value ?? string.Empty));
            }
        }

        private static void RequireFinitePositive(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    label + "=" + value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// <c>max(1, RoundHalfUp(product))</c> + 正 <c>int</c> 上界检查。
        /// <c>product</c> 是"有限正数 × 正整数"的 double 乘积；非有限或超上界一律稳定拒绝，绝不回绕。
        /// </summary>
        private static int QuantizePositiveProduct(double product, string label)
        {
            if (double.IsNaN(product) || double.IsInfinity(product) || product <= 0d)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    label + "Product=" + product.ToString("R", CultureInfo.InvariantCulture));

            long rounded;
            try
            {
                rounded = checked((long)Math.Round(product, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE, ex.Message);
            }
            if (rounded < 1L) rounded = 1L;   // 冻结 max(1, ...)：正配置永不为零动量
            if (rounded > MomentumLimits.MaxMomentumUnitsPerIntent)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    label + "=" + rounded.ToString(CultureInfo.InvariantCulture));
            return (int)rounded;
        }
    }
}
