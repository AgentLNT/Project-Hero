using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Grid;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// Q10 数学方向向量（主方案 0.4.2 <c>:302-317</c>）。分量是<strong>整数</strong>：
    /// <c>1024</c> 表示单位长度（Q10 基准），因此 <c>(1024, 0)</c> 是 0°、<c>(0, 1024)</c> 是 90°。
    /// </summary>
    public readonly struct DirectionVectorQ10
    {
        public readonly int X;
        public readonly int Y;

        public DirectionVectorQ10(int x, int y)
        {
            X = x;
            Y = y;
        }

        /// <summary>Q10 长度平方（用于表的结构自检：应≈1024²）。</summary>
        public long SquaredLengthQ10 => (long)X * X + (long)Y * Y;

        public bool Equals(DirectionVectorQ10 other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is DirectionVectorQ10 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public override string ToString()
            => "(" + X.ToString(CultureInfo.InvariantCulture) + "," + Y.ToString(CultureInfo.InvariantCulture) + ")";

        public static bool operator ==(DirectionVectorQ10 left, DirectionVectorQ10 right) => left.Equals(right);

        public static bool operator !=(DirectionVectorQ10 left, DirectionVectorQ10 right) => !left.Equals(right);
    }

    /// <summary>
    /// 任务包「必须产出」3：主方案 <c>0.4.2</c> 的 Q10 方向向量表与 <c>OppositionFactorQ10</c> 对立系数表，
    /// 以及单条 Clash 接触的损耗公式。
    ///
    /// <strong>与邻居表的关系（必须记住的坑）</strong>：本表是<strong>数学方向向量</strong>；
    /// <c>Grid.GridNeighborTable</c> 的 <c>(+2,0) / (+3,+1) / …</c> 是<strong>占位平移表</strong>
    /// （doubled coordinates，用于 footprint/寻路/Dodge）。两者<strong>不是同一张表</strong>：
    /// 向右的平移量是 <c>(+2,0)</c> 而向右的 Q10 向量是 <c>(1024,0)</c>（差 512 倍），
    /// 但 30° 方向平移 <c>(+3,+1)</c> 的斜率是 <c>1/3</c>，Q10 向量 <c>(887,512)</c> 的斜率是
    /// <c>512/887 ≈ 0.577</c>（即 <c>cos30°</c>）——<strong>没有任何统一缩放</strong>能把一张表变成另一张。
    /// 从邻居表"推导"Q10 向量会引入确定性缺陷，因此本表是逐值独立抄录的冻结表。
    ///
    /// <strong>对立系数的玩法规则（口径裁定，不是笔误）</strong>：最小环形方向差 <c>d ∈ [0,6]</c>，
    /// <c>OppositionFactorQ10 = 0,0,0,0,512,887,1024</c>。因此 <c>d ≤ 3</c>（夹角 ≤ 90°）
    /// 系数为 0 ⇒ <strong>不构成有效 Clash</strong>（主方案 <c>0.4.5 :477</c>：
    /// "只有方向损耗系数大于 0 的 Attack-Attack 接触才是有效 Clash"）。
    ///
    /// 表内容、单位换算常量与上限全部进入 <c>BattleDefinitionHash</c>（<see cref="WriteHashComponents"/>）。
    /// 不得运行时调用 <c>sin/cos</c>，不得使用 float/double。
    /// </summary>
    public static class MomentumRuleTable
    {
        /// <summary>方向向量表/对立系数表版本（进入 <c>BattleDefinitionHash</c>）。</summary>
        public const string TableVersion = "momentum-q10-12dir-v1";

        /// <summary>12 向。</summary>
        public const int DirectionCount = GridDirectionInfo.DirectionCount;

        /// <summary>最小环形差 <c>d ∈ [0,6]</c> 的取值个数。</summary>
        public const int OppositionFactorCount = 7;

        /// <summary>Q10 单位长度（1.0 = 1024；与 <c>HealthQ10</c> 同基准的分母）。</summary>
        public const int Q10One = 1024;

        // —— 主方案 0.4.2 :302-317 逐值抄录（索引 = (int)GridDirection）——
        private static readonly DirectionVectorQ10[] Vectors =
        {
            new DirectionVectorQ10(1024, 0),     // 0  East        0°
            new DirectionVectorQ10(887, 512),    // 1  EastNorth   30°
            new DirectionVectorQ10(512, 887),    // 2  NorthEast   60°
            new DirectionVectorQ10(0, 1024),     // 3  North       90°
            new DirectionVectorQ10(-512, 887),   // 4  NorthWest   120°
            new DirectionVectorQ10(-887, 512),   // 5  WestNorth   150°
            new DirectionVectorQ10(-1024, 0),    // 6  West        180°
            new DirectionVectorQ10(-887, -512),  // 7  WestSouth   210°
            new DirectionVectorQ10(-512, -887),  // 8  SouthWest   240°
            new DirectionVectorQ10(0, -1024),    // 9  South       270°
            new DirectionVectorQ10(512, -887),   // 10 SouthEast   300°
            new DirectionVectorQ10(887, -512)    // 11 EastSouth   330°
        };

        // —— 主方案 0.4.2 :319-324 逐值抄录（索引 = 最小环形差 d）——
        private static readonly int[] OppositionFactorsQ10 = { 0, 0, 0, 0, 512, 887, 1024 };

        private static readonly IReadOnlyList<DirectionVectorQ10> ReadOnlyVectors =
            Array.AsReadOnly(Vectors);

        private static readonly IReadOnlyList<int> ReadOnlyOppositionFactors =
            Array.AsReadOnly(OppositionFactorsQ10);

        /// <summary>12 项 Q10 方向向量（索引 = 方向数值；只读）。</summary>
        public static IReadOnlyList<DirectionVectorQ10> DirectionVectorsQ10 => ReadOnlyVectors;

        /// <summary><c>d = 0..6</c> 的 <c>OppositionFactorQ10</c>（只读）。</summary>
        public static IReadOnlyList<int> OppositionFactorsQ10Table => ReadOnlyOppositionFactors;

        /// <summary>按方向取 Q10 向量（越界以稳定码拒绝，不静默回落）。</summary>
        public static DirectionVectorQ10 DirectionVectorQ10(GridDirection direction)
        {
            int index = (int)direction;
            if (!GridDirectionInfo.IsValidIndex(index))
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "direction=" + index.ToString(CultureInfo.InvariantCulture));
            return Vectors[index];
        }

        /// <summary>按索引 <c>0..11</c> 取 Q10 向量（表自检/哈希用）。</summary>
        public static DirectionVectorQ10 DirectionVectorQ10At(int index)
        {
            if (index < 0 || index >= DirectionCount)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "directionIndex=" + index.ToString(CultureInfo.InvariantCulture));
            return Vectors[index];
        }

        /// <summary>按最小环形差 <c>d ∈ [0,6]</c> 取对立系数（越界以稳定码拒绝）。</summary>
        public static int OppositionFactorQ10(int ringDistance)
        {
            if (ringDistance < 0 || ringDistance >= OppositionFactorCount)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "ringDistance=" + ringDistance.ToString(CultureInfo.InvariantCulture));
            return OppositionFactorsQ10[ringDistance];
        }

        /// <summary>
        /// 首版固定动量方向：<c>Normalize12((int)Facing + MomentumDirectionOffsetSteps)</c>
        /// （主方案 0.4.2 <c>:298</c>；普通直线攻击偏移为 0）。
        /// </summary>
        public static GridDirection ResolveMomentumDirection(GridDirection facing, int momentumDirectionOffsetSteps)
            => (GridDirection)Normalize12((int)facing + momentumDirectionOffsetSteps);

        /// <summary>12 向环绕归一化（接受任意整数步数，含负数）。</summary>
        public static int Normalize12(int steps)
        {
            int normalized = steps % DirectionCount;
            return normalized < 0 ? normalized + DirectionCount : normalized;
        }

        /// <summary>两个方向的<strong>最小环形差</strong> <c>d ∈ [0,6]</c>（12 向枚举上的环绕距离）。</summary>
        public static int MinimalRingDistance(GridDirection a, GridDirection b)
        {
            int delta = (int)a - (int)b;
            if (delta < 0) delta = -delta;
            return delta > DirectionCount / 2 ? DirectionCount - delta : delta;
        }

        /// <summary>
        /// 该方向对是否构成<strong>有效 Clash</strong>：对立系数严格大于 0
        /// （<c>d ≤ 3</c> 时为 false，这是玩法规则）。
        /// </summary>
        public static bool IsEffectiveClashPair(GridDirection a, GridDirection b)
            => OppositionFactorQ10(MinimalRingDistance(a, b)) > 0;

        /// <summary>
        /// 单条 Clash 接触损耗（主方案 0.4.2 <c>:326-332</c>）：
        /// <c>LossFromBToA = (B.MomentumUnits * OppositionFactorQ10[d] + 512) / 1024</c>。
        ///
        /// 实现说明（口径裁定 P1-3）：<c>+512</c> 的整数 half-up 与既有唯一舍入原语
        /// <c>TickQuantization.RoundHalfUpDivide(n, 1024) = (2n + 1024) / 2048</c> <strong>逐值等价</strong>
        /// （<c>n = 1024k + r</c> 时两者都等于 <c>k + (r ≥ 512 ? 1 : 0)</c>），
        /// 因此这里<strong>复用</strong>既有原语，不写第二份舍入实现。乘法用 checked long。
        /// </summary>
        public static long LossFromBToA(int opponentMomentumUnits, int ringDistance)
        {
            if (opponentMomentumUnits < 0)
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE,
                    "opponentMomentumUnits=" + opponentMomentumUnits.ToString(CultureInfo.InvariantCulture));
            int factor = OppositionFactorQ10(ringDistance);
            long product;
            try
            {
                product = checked((long)opponentMomentumUnits * factor);
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE, ex.Message);
            }
            return TickQuantization.RoundHalfUpDivide(product, Q10One);
        }

        /// <summary>Q10 向量点积（checked long；表自检与合力方向选择共用）。</summary>
        public static long DotQ10(DirectionVectorQ10 a, DirectionVectorQ10 b)
        {
            try
            {
                return checked((long)a.X * b.X + (long)a.Y * b.Y);
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE, ex.Message);
            }
        }

        /// <summary>
        /// 合力方向选择（主方案 0.4.5 <c>:489</c> + 任务包 08:110）：
        /// 取与合力向量点积<strong>最大</strong>的 <see cref="GridDirection"/>，<strong>并列取枚举值较小者</strong>
        /// （严格 <c>&gt;</c> 比较 + 升序遍历即等价）；最大点积<strong>不大于 0</strong> 时返回 false（无击退方向）。
        /// <paramref name="maxDot"/> 为最大点积（量纲 = <c>1024² × Σ(MomentumUnits × cosθ)</c>，
        /// 因为方向向量本身已是 1024 标度），其 <c>/1024²</c> 向下取整即合力强度
        /// （= <c>Σ 动量·cosθ</c>，与动量/CRU 同域；裁定 5 · 读法 B）。
        /// <strong>不得</strong>按字面只除一次 1024 —— 那会给 1024× 偏大的强度与荒谬的击退步数。
        /// </summary>
        public static bool TrySelectResultantDirection(long resultantXQ10, long resultantYQ10,
            out GridDirection direction, out long maxDot)
        {
            direction = default;
            maxDot = long.MinValue;
            for (int i = 0; i < DirectionCount; i++)
            {
                DirectionVectorQ10 vector = Vectors[i];
                long dot;
                try
                {
                    dot = checked(vector.X * resultantXQ10 + vector.Y * resultantYQ10);
                }
                catch (OverflowException ex)
                {
                    throw new LogicDefinitionException(MomentumCodes.MOMENTUM_OUT_OF_RANGE, ex.Message);
                }
                if (dot > maxDot)
                {
                    maxDot = dot;
                    direction = (GridDirection)i;
                }
            }
            return maxDot > 0L;
        }

        /// <summary>
        /// 把方向表、对立系数表、Q10 基准与单位换算/上限常量写入规范哈希。
        ///
        /// <strong>装配侧插入点（由集成方决定，本流不改既有摘要）</strong>：
        /// <c>BattleDefinitionHash.Compute(...)</c> 中 "grid.direction / grid.direction_count" 之后、
        /// "factionModel?.WriteHashComponents" 之前调用 <c>MomentumRuleTable.WriteHashComponents(writer)</c>。
        /// 一旦插入，全量定义摘要会变化，5 个文件 7 处冻结摘要需同步重基线（见 08 集成台账）。
        /// </summary>
        public static void WriteHashComponents(CanonicalHashWriter writer)
        {
            if (writer == null) return;

            writer.Write("momentum.table_version", TableVersion);
            writer.Write("momentum.q10_one", Q10One);
            writer.Write("momentum.direction_count", DirectionCount);
            for (int i = 0; i < DirectionCount; i++)
                writer.Write("momentum.direction_vector",
                    i.ToString(CultureInfo.InvariantCulture) + ":" +
                    Vectors[i].X.ToString(CultureInfo.InvariantCulture) + "," +
                    Vectors[i].Y.ToString(CultureInfo.InvariantCulture));

            writer.Write("momentum.opposition_factor_count", OppositionFactorCount);
            for (int d = 0; d < OppositionFactorCount; d++)
                writer.Write("momentum.opposition_factor",
                    d.ToString(CultureInfo.InvariantCulture) + ":" +
                    OppositionFactorsQ10[d].ToString(CultureInfo.InvariantCulture));

            writer.Write("momentum.units_per_legacy_momentum", MomentumQuantizer.MomentumUnitsPerLegacyMomentum);
            writer.Write("momentum.normal_impact_damage_divisor", MomentumQuantizer.NormalImpactDamageDivisor);
            writer.Write("momentum.clash_residual_damage_divisor", MomentumQuantizer.ClashResidualDamageDivisor);
            writer.Write("momentum.control_resistance_per_unit", MomentumQuantizer.ControlResistancePerUnit);
            writer.Write("momentum.max_momentum_units_per_intent", MomentumLimits.MaxMomentumUnitsPerIntent);
            // 冲突图域的三个上限（节点/边/目标）归 Interactions.ConflictGraphLimits（构图流唯一权威），
            // 由装配侧在那里写入；本流不复制数值、不反向引用。
        }

        /// <summary>
        /// 动量规则分量<strong>独立的</strong>规范摘要（16 位小写十六进制）。
        /// 它让"表内容参与哈希"可在不改动全量 <c>BattleDefinitionHash.Compute</c> 与其冻结摘要的前提下被测试与审计。
        /// </summary>
        public static string ComputeDigest()
        {
            var writer = new CanonicalHashWriter();
            WriteHashComponents(writer);
            return writer.ToDigestHex();
        }
    }
}
