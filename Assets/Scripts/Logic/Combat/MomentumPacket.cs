using System;
using System.Globalization;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 任务 08「动量 / 方向 / 对立 / Clash / 目标聚合」的稳定原因码（<strong>动量域</strong>）。
    ///
    /// <strong>归属裁定（父代理，立即生效）</strong>：一个常量只能有一个家。本类只声明<strong>动量域</strong>的码；
    /// <strong>冲突图域</strong>的码与上限（<c>CONFLICT_GROUP_LIMIT_EXCEEDED</c>、
    /// <c>Interactions.ConflictGraphLimits.{MaxConflictGroupNodes, MaxConflictGroupEdges,
    /// MaxTargetsPerConflictGroup}</c>）归 <c>Logic/Interactions</c>（构图流）。本流<strong>不</strong>反向引用它，
    /// 也不复制其数值：需要图上限时由调用方经 <c>MomentumClashQuota</c> 参数传入（见
    /// <see cref="MomentumClashSolver"/>）。全部为字符串常量，不得改名、复用或本地化。
    /// </summary>
    public static class MomentumCodes
    {
        /// <summary>
        /// 动量非正、超出正 <c>int</c>、冲击 Profile 未知，或 <c>Mass/MomentumSpeed/ForceMultiplier</c>
        /// 不是有限正数（任务包 08:99 冻结码）。必须稳定拒绝，不得钳制、饱和或回绕。
        /// </summary>
        public const string MOMENTUM_OUT_OF_RANGE = "MOMENTUM_OUT_OF_RANGE";

        /// <summary>Clash 输入结构非法（重复参与者、自环、未知端点、非有效对立边、孤立参与者）。</summary>
        public const string CLASH_INPUT_INVALID = "CLASH_INPUT_INVALID";

        /// <summary>Clash 剩余分配不守恒（分配总和必须严格等于 RemainingMomentum）。</summary>
        public const string CLASH_RESIDUAL_ALLOCATION_INVALID = "CLASH_RESIDUAL_ALLOCATION_INVALID";

        /// <summary>目标聚合输入结构非法（目标不合法、抵抗越界、接触键重复、负入射动量）。</summary>
        public const string TARGET_AGGREGATE_INVALID = "TARGET_AGGREGATE_INVALID";

        /// <summary>聚合后的 Q10 伤害超出逻辑血量 <c>int</c> 域（checked long 中间值已保证不回绕）。</summary>
        public const string DAMAGE_AGGREGATE_OUT_OF_RANGE = "DAMAGE_AGGREGATE_OUT_OF_RANGE";
    }

    /// <summary>
    /// <strong>动量域</strong>的冻结上限。
    ///
    /// <strong>归属裁定（父代理）</strong>：这里只留动量域的上限
    /// <see cref="MaxMomentumUnitsPerIntent"/>。冲突图域的三个上限
    /// （节点 256 / 边 32640 / 目标 256）归 <c>Interactions.ConflictGraphLimits</c>（构图流唯一权威），
    /// 本流既不声明也不引用，改由调用方在 <c>MomentumClashQuota</c> 里传入。
    /// 数值属于版本化玩法规则并进入 <c>BattleDefinitionHash</c>。
    /// </summary>
    public static class MomentumLimits
    {
        /// <summary>单个 Intent 的动量上限（正 <c>int</c> 全域；越界以稳定码拒绝）。</summary>
        public const int MaxMomentumUnitsPerIntent = int.MaxValue;
    }

    /// <summary>
    /// 攻击 Intent 的<strong>动量载荷</strong>（任务包「必须产出」1 的
    /// <c>DamageComponents[] + MomentumPacket</c> 正交结构中的动量一侧；
    /// 主方案 0.4.2 <c>:297</c> 称其为 <c>DirectionalMomentum(GridDirection, int)</c>）。
    ///
    /// <strong>冻结语义（跨流接口，先到先冻结）</strong>
    /// <list type="bullet">
    /// <item><see cref="Direction"/> 是 12 向 <see cref="GridDirection"/>，数值 <c>0 → 11</c> 冻结。</item>
    /// <item><see cref="MomentumUnits"/> 是<strong>已经量化过的正 int</strong>：
    /// 由 <see cref="MomentumQuantizer.QuantizeMomentumUnits"/> 在 Intent 产生边界
    /// （<c>ActionPlan.ImpactTick</c>）<strong>恰好一次</strong>算出，之后全链路（损耗、剩余、
    /// 聚合）只做整数运算，不再读取任何浮点属性。</item>
    /// <item><see cref="ImpactProfileId"/> 只用于<strong>审计/事件/哈希</strong>：
    /// 量化完成后求解不再需要它，它不是伤害类型（00 号规则 24）。</item>
    /// <item>本类型<strong>不含任何 float/double</strong>：这是"仲裁期间不读浮点"的结构性保证，
    /// 由 <c>Task08MomentumQuantizationTests.MomentumQuantizationOccursOnceBeforeArbitration</c> 反射守护。</item>
    /// </list>
    /// 注：Unity 6000.6.2f1 默认 C# 9，<c>record struct</c> 为 C# 10 特性，故手写只读值类型
    /// （与 <c>DamageComponentSpec</c> 同风格）。
    /// </summary>
    public readonly struct MomentumPacket
    {
        /// <summary>动量的 12 向方向。</summary>
        public readonly GridDirection Direction;

        /// <summary>量化后的动量单位（严格 <c>&gt; 0</c>；上界 <see cref="MomentumLimits.MaxMomentumUnitsPerIntent"/>）。</summary>
        public readonly int MomentumUnits;

        /// <summary>动量来源的冲击 Profile（100/60/30 传递百分数）；只用于审计与哈希，不参与求解。</summary>
        public readonly ImpactProfileId ImpactProfileId;

        public MomentumPacket(GridDirection direction, int momentumUnits, ImpactProfileId impactProfileId)
        {
            Direction = direction;
            MomentumUnits = momentumUnits;
            ImpactProfileId = impactProfileId;
        }

        /// <summary>无 Profile 审计信息的动量载荷（等价于 <c>default(ImpactProfileId)</c>）。</summary>
        public MomentumPacket(GridDirection direction, int momentumUnits)
            : this(direction, momentumUnits, default) { }

        /// <summary>方向索引在 <c>[0,12)</c> 且动量严格为正。</summary>
        public bool IsValid
            => MomentumUnits > 0 && MomentumUnits <= MomentumLimits.MaxMomentumUnitsPerIntent
               && GridDirectionInfo.IsValidIndex((int)Direction);

        /// <summary>
        /// 校验并返回：不合法的动量以 <see cref="MomentumCodes.MOMENTUM_OUT_OF_RANGE"/> 稳定拒绝，
        /// 绝不钳制或回绕（任务包 08:99）。
        /// </summary>
        public static MomentumPacket Require(GridDirection direction, int momentumUnits, ImpactProfileId impactProfileId = default)
        {
            if (momentumUnits <= 0 || momentumUnits > MomentumLimits.MaxMomentumUnitsPerIntent)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "momentumUnits=" + momentumUnits.ToString(CultureInfo.InvariantCulture));
            if (!GridDirectionInfo.IsValidIndex((int)direction))
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "direction=" + ((int)direction).ToString(CultureInfo.InvariantCulture));
            return new MomentumPacket(direction, momentumUnits, impactProfileId);
        }

        public bool Equals(MomentumPacket other)
            => Direction == other.Direction && MomentumUnits == other.MomentumUnits
               && ImpactProfileId == other.ImpactProfileId;

        public override bool Equals(object obj) => obj is MomentumPacket other && Equals(other);

        public override int GetHashCode() => HashCode.Combine((int)Direction, MomentumUnits, ImpactProfileId);

        public override string ToString()
            => ((int)Direction).ToString(CultureInfo.InvariantCulture) + ":" +
               MomentumUnits.ToString(CultureInfo.InvariantCulture) + ":" +
               (ImpactProfileId.Value ?? string.Empty);

        public static bool operator ==(MomentumPacket left, MomentumPacket right) => left.Equals(right);

        public static bool operator !=(MomentumPacket left, MomentumPacket right) => !left.Equals(right);
    }
}
