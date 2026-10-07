using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 动量域（「必须产出」2 + 3）离线用例：Q10 方向/对立表、动量量化器、冻结除数、
    /// 大动量与冲突组不溢出、哈希参与。
    ///
    /// 全部期望值都是<strong>手算</strong>并写在注释里（不是把实现常量抄一遍）：
    /// 每条用例都注明"哪种实现缺陷会让它失败"。
    /// </summary>
    [TestFixture]
    public class Task08MomentumTests
    {
        // ————————————————————————————————————————————————————————————————
        // 「必须产出」3：0.4.2 方向向量与 OppositionFactorQ10 查表
        // ————————————————————————————————————————————————————————————————

        [Test]
        public void DirectionAndOppositionTablesMatchCanonicalQ10Values()
        {
            // 期望值逐字来自主方案 0.4.2 :302-317（与实现完全独立的第二份字面量）。
            // 会让它失败的缺陷：任一方向向量的 x/y 抄错、方向顺序被重排（East 必须 = 0）、
            // 用邻居平移表 (+2,0)/(+3,+1) 冒充数学向量、或对向量做"归一化到 1024 长度"的近似。
            int[][] expected =
            {
                new[] { 1024, 0 }, new[] { 887, 512 }, new[] { 512, 887 }, new[] { 0, 1024 },
                new[] { -512, 887 }, new[] { -887, 512 }, new[] { -1024, 0 }, new[] { -887, -512 },
                new[] { -512, -887 }, new[] { 0, -1024 }, new[] { 512, -887 }, new[] { 887, -512 }
            };

            Assert.That(MomentumRuleTable.DirectionCount, Is.EqualTo(12));
            for (int i = 0; i < 12; i++)
            {
                DirectionVectorQ10 vector = MomentumRuleTable.DirectionVectorQ10At(i);
                Assert.That(vector.X, Is.EqualTo(expected[i][0]), "direction " + i + " x");
                Assert.That(vector.Y, Is.EqualTo(expected[i][1]), "direction " + i + " y");
                Assert.That(MomentumRuleTable.DirectionVectorQ10((GridDirection)i), Is.EqualTo(vector));
            }

            // —— 与实现无关的结构不变量（独立于上面的字面量）——
            for (int i = 0; i < 12; i++)
            {
                DirectionVectorQ10 a = MomentumRuleTable.DirectionVectorQ10At(i);
                DirectionVectorQ10 opposite = MomentumRuleTable.DirectionVectorQ10At((i + 6) % 12);
                Assert.That(opposite.X, Is.EqualTo(-a.X), "i=" + i + " 反向向量必须是精确取反");
                Assert.That(opposite.Y, Is.EqualTo(-a.Y), "i=" + i);

                // Q10 长度必须≈1024：1024² = 1048576 ≤ |v|² ≤ 1025² = 1050625
                // （(887,512) = 786769 + 262144 = 1048913，偏差 0.03%，是 round(1024·cos30°) 的正常结果）。
                Assert.That(a.SquaredLengthQ10, Is.GreaterThanOrEqualTo(1048576L), "i=" + i);
                Assert.That(a.SquaredLengthQ10, Is.LessThanOrEqualTo(1050625L), "i=" + i);
            }
            for (int i = 0; i < 12; i++)
            {
                // 相邻 30°：dot = 1024·887 = 908288（对全部 i 精确成立，因为 (1024,0)·(887,512) = 1024·887
                // 且 90° 旋转 (x,y)→(-y,x) 在 Q10 表上是精确的）。
                DirectionVectorQ10 v0 = MomentumRuleTable.DirectionVectorQ10At(i);
                Assert.That(MomentumRuleTable.DotQ10(v0, MomentumRuleTable.DirectionVectorQ10At((i + 1) % 12)),
                    Is.EqualTo(1024L * 887L), "30° dot i=" + i);
                // 90°：Q10 表上的 90° 旋转 (x,y)→(-y,x) 是**精确**的 ⇒ 点积精确为 0。
                Assert.That(MomentumRuleTable.DotQ10(v0, MomentumRuleTable.DirectionVectorQ10At((i + 3) % 12)),
                    Is.EqualTo(0L), "90° dot i=" + i);
                // 180°：v[i+6] == -v[i] 是精确的 ⇒ 点积 = -|v[i]|²（基数方向正好 -1024²，
                // 斜向是 -(887²+512²) = -1048913，不能用 -1024² 冒充）。
                Assert.That(MomentumRuleTable.DotQ10(v0, MomentumRuleTable.DirectionVectorQ10At((i + 6) % 12)),
                    Is.EqualTo(-v0.SquaredLengthQ10), "180° dot i=" + i);
                // 60°/120°：理想值 ±0.5·1024² = ±524288，但圆整后的 Q10 向量**不是**精确旋转
                // （例：v2·v4 = 512·(-512) + 887·887 = 524625 ≠ 524288），因此这里只钉"落在理想值 ±337 的圆整带内"。
                // 会让它失败的缺陷：向量分量写反符号、把 887 与 512 互换、或用邻居平移表冒充。
                long dot60 = MomentumRuleTable.DotQ10(v0, MomentumRuleTable.DirectionVectorQ10At((i + 2) % 12));
                Assert.That(dot60, Is.InRange(524288L, 524625L), "60° dot i=" + i);
                long dot120 = MomentumRuleTable.DotQ10(v0, MomentumRuleTable.DirectionVectorQ10At((i + 4) % 12));
                Assert.That(dot120, Is.InRange(-524625L, -524288L), "120° dot i=" + i);
            }

            // —— 两张方向表不得互相推导 ——
            // 占位平移表（doubled coordinates）的 East = (+2,0)、EastNorth = (+3,+1)；
            // Q10 表 East = (1024,0)、EastNorth = (887,512)。若存在统一整数缩放 k 使两表逐向相等，
            // 则 East 要求 2k = 1024 ⇒ k = 512，而 EastNorth 会变成 (1536,512) ≠ (887,512)，矛盾。
            // 穷举 k ∈ [1,4096] 确认不存在这样的 k。
            // 会让它失败的缺陷：把 Q10 向量"实现"成 GridNeighborTable 的缩放（例如 ×512）。
            bool anyUniformScaleMapsNeighborTableToQ10 = false;
            for (int k = 1; k <= 4096 && !anyUniformScaleMapsNeighborTableToQ10; k++)
            {
                bool all12 = true;
                for (int i = 0; i < 12; i++)
                {
                    DirectionVectorQ10 v = MomentumRuleTable.DirectionVectorQ10At(i);
                    if (v.X != GridNeighborTable.OffsetX((GridDirection)i) * k ||
                        v.Y != GridNeighborTable.OffsetY((GridDirection)i) * k)
                    {
                        all12 = false;
                        break;
                    }
                }
                anyUniformScaleMapsNeighborTableToQ10 = all12;
            }
            Assert.That(anyUniformScaleMapsNeighborTableToQ10, Is.False,
                "Q10 方向向量表与占位平移表不是同一张表，任何统一缩放都不相等");
            // 斜率判据（交叉相乘，避免浮点）：邻居表 EastNorth 斜率 1/3，Q10 表 512/887。
            Assert.That(887L * GridNeighborTable.OffsetY(GridDirection.EastNorth),
                Is.Not.EqualTo(512L * GridNeighborTable.OffsetX(GridDirection.EastNorth)));

            // —— OppositionFactorQ10 逐值 + 玩法规则 ——
            // 会让它失败的缺陷：把 d≤3 的系数"顺手"改成非零（那会凭空造出近距离拼刀）、
            // 或把 887/512 写反。
            int[] expectedFactors = { 0, 0, 0, 0, 512, 887, 1024 };
            Assert.That(MomentumRuleTable.OppositionFactorCount, Is.EqualTo(7));
            for (int d = 0; d < 7; d++)
                Assert.That(MomentumRuleTable.OppositionFactorQ10(d), Is.EqualTo(expectedFactors[d]), "d=" + d);

            Assert.That(MomentumRuleTable.IsEffectiveClashPair(GridDirection.East, GridDirection.North),
                Is.False, "d=3（90°）系数为 0 ⇒ 不是有效 Clash（玩法规则，不是笔误）");
            Assert.That(MomentumRuleTable.IsEffectiveClashPair(GridDirection.East, GridDirection.NorthWest),
                Is.True, "d=4（120°）系数 512 ⇒ 有效 Clash");
            Assert.That(MomentumRuleTable.IsEffectiveClashPair(GridDirection.East, GridDirection.West),
                Is.True, "d=6（180°）系数 1024 ⇒ 有效 Clash");

            // —— 最小环形差 d ∈ [0,6]（手算）——
            Assert.That(MomentumRuleTable.MinimalRingDistance(GridDirection.East, GridDirection.East), Is.EqualTo(0));
            Assert.That(MomentumRuleTable.MinimalRingDistance(GridDirection.East, GridDirection.EastSouth), Is.EqualTo(1));
            Assert.That(MomentumRuleTable.MinimalRingDistance(GridDirection.East, GridDirection.WestSouth), Is.EqualTo(5));
            Assert.That(MomentumRuleTable.MinimalRingDistance(GridDirection.WestSouth, GridDirection.East), Is.EqualTo(5));
            Assert.That(MomentumRuleTable.MinimalRingDistance(GridDirection.East, GridDirection.West), Is.EqualTo(6));
            Assert.That(MomentumRuleTable.MinimalRingDistance(GridDirection.North, GridDirection.South), Is.EqualTo(6));

            // —— 单条 Clash 损耗式（主方案 :326-332）与 half-up 边界 ——
            // B=1 动量、d=4（512）：(1·512 + 512)/1024 = 1 ⇒ 1（0.5 向上）
            Assert.That(MomentumRuleTable.LossFromBToA(1, 4), Is.EqualTo(1L));
            // B=1、d=6（1024）：(1024+512)/1024 = 1 ⇒ 1（不是 1.5 截断）
            Assert.That(MomentumRuleTable.LossFromBToA(1, 6), Is.EqualTo(1L));
            // B=3、d=4：3·512 = 1536；(1536+512)/1024 = 2 ⇒ 2
            Assert.That(MomentumRuleTable.LossFromBToA(3, 4), Is.EqualTo(2L));
            // half-up 的临界：r = 511 向下、r = 512 向上。
            // d=6 时 n = B·1024 ⇒ r 恒为 0；用 d=5（887）构造：B=511 ⇒ 511·887 = 453257 = 1024·442 + 649
            // ⇒ (453257+512)/1024 = 443.13 ⇒ 443（余数 649 ≥ 512 向上）
            Assert.That(MomentumRuleTable.LossFromBToA(511, 5), Is.EqualTo(443L));
            // B=510 ⇒ 510·887 = 452370 = 1024·441 + 786 ⇒ (452370+512)/1024 = 442.46 ⇒ 442
            Assert.That(MomentumRuleTable.LossFromBToA(510, 5), Is.EqualTo(442L));
            // 与既有一半舍入原语逐值等价（n = 1024k + r；r = 0 与 r = 1023 两侧）
            Assert.That(MomentumRuleTable.LossFromBToA(1024, 6), Is.EqualTo(1024L));
            Assert.That(MomentumRuleTable.LossFromBToA(2047, 6), Is.EqualTo(2047L));
        }

        [Test]
        public void MomentumRuleTablesParticipateInBattleDefinitionHash()
        {
            // 「必须产出」3 + 验收标准 :392：方向表、对立系数、动量比例、伤害除数与控制阈值规则
            // 必须进入 BattleDefinitionHash。
            // 会让它失败的缺陷：WriteHashComponents 漏写某张表；表值改了但哈希不变（= 确定性覆盖缺口）；
            // 或写入的键名不稳定（插入顺序依赖导致跨进程哈希漂移）。
            string digest = BattleDefinitionHash.OfMomentumRules();
            Assert.That(digest, Is.Not.Null.And.Length.EqualTo(16));
            Assert.That(BattleDefinitionHash.OfMomentumRules(), Is.EqualTo(digest), "同输入必须同摘要");

            var writer = new CanonicalHashWriter();
            MomentumRuleTable.WriteHashComponents(writer);
            string canonical = writer.ToCanonicalText();
            Assert.That(canonical, Does.Contain("momentum.direction_vector=0:1024,0;"));
            Assert.That(canonical, Does.Contain("momentum.direction_vector=4:-512,887;"));
            Assert.That(canonical, Does.Contain("momentum.direction_vector=11:887,-512;"));
            Assert.That(canonical, Does.Contain("momentum.opposition_factor=4:512;"));
            Assert.That(canonical, Does.Contain("momentum.opposition_factor=5:887;"));
            Assert.That(canonical, Does.Contain("momentum.opposition_factor=6:1024;"));
            Assert.That(canonical, Does.Contain("momentum.units_per_legacy_momentum=100;"));
            Assert.That(canonical, Does.Contain("momentum.normal_impact_damage_divisor=5000;"));
            Assert.That(canonical, Does.Contain("momentum.clash_residual_damage_divisor=1000;"));
            Assert.That(canonical, Does.Contain("momentum.control_resistance_per_unit=100;"));
            Assert.That(canonical, Does.Contain("momentum.max_momentum_units_per_intent=2147483647;"));
            Assert.That(canonical, Does.Contain("momentum.table_version=" + MomentumRuleTable.TableVersion + ";"));

            // 敏感性：同键名、只改一个方向向量的值 ⇒ 摘要必须变
            // （证明写入的**值**参与摘要，而不只是键名存在）。
            var mutated = new CanonicalHashWriter();
            MomentumRuleTable.WriteHashComponents(mutated);
            mutated.Write("momentum.direction_vector", "1:886,512");
            Assert.That(mutated.ToDigestHex(), Is.Not.EqualTo(digest));

            // 冻结基准：表版本 momentum-q10-12dir-v1 的规范摘要。它由**独立实现**（PowerShell 的
            // FNV-1a 64，对下面这串规范文本逐字符计算）算出，不是从本实现回抄：
            //   momentum.table_version=momentum-q10-12dir-v1;momentum.q10_one=1024;momentum.direction_count=12;
            //   momentum.direction_vector=0..11;momentum.opposition_factor_count=7;momentum.opposition_factor=0..6;
            //   momentum.units_per_legacy_momentum=100;momentum.normal_impact_damage_divisor=5000;
            //   momentum.clash_residual_damage_divisor=1000;momentum.control_resistance_per_unit=100;
            //   momentum.max_momentum_units_per_intent=2147483647;
            // 会让它失败的缺陷：任一表值/常量被改动、键名被改名、或写入顺序变化（都会让摘要漂移）。
            Assert.That(digest, Is.EqualTo(FrozenMomentumRulesDigest));
        }

        /// <summary>冻结的动量规则摘要（独立实现计算；表/常量变化必须同步更新并说明理由）。</summary>
        private const string FrozenMomentumRulesDigest = "c58e500c8ff466ef";

        // ————————————————————————————————————————————————————————————————
        // 「必须产出」2：动量量化器
        // ————————————————————————————————————————————————————————————————

        [Test]
        public void MomentumQuantizationMatchesLegacyImpactProfileScaleAndHalfUp()
        {
            // Mass=1、MomentumSpeed=1、Force=1 ⇒ 基准 1.0，传递百分数 100/60/30 ⇒ 100/60/30。
            // 会让它失败的缺陷：把 ImpactTransferPercent 当成伤害类型/通道系数、用 0.6/0.3 的 float
            // 乘完后截断、或对 Blunt 漏乘 100。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(1f, 1f, 1f, ImpactProfiles.Blunt), Is.EqualTo(100));
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(1f, 1f, 1f, ImpactProfiles.Slash), Is.EqualTo(60));
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(1f, 1f, 1f, ImpactProfiles.Pierce), Is.EqualTo(30));

            // Mass=0.5、MomentumSpeed=0.5、Force=1 ⇒ 基准 0.25（二进制精确）。
            // Blunt 0.25·100 = 25；Slash 0.25·60 = 15；Pierce 0.25·30 = 7.5 ⇒ half-up 必须给 8（截断会给 7）。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(0.5f, 0.5f, 1f, ImpactProfiles.Blunt), Is.EqualTo(25));
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(0.5f, 0.5f, 1f, ImpactProfiles.Slash), Is.EqualTo(15));
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(0.5f, 0.5f, 1f, ImpactProfiles.Pierce), Is.EqualTo(8),
                "7.5 必须 half-up 到 8；向下取整实现会给 7");

            // max(1, ...)：Mass=0.001、Speed=1、Force=1、Pierce ⇒ 0.03 ⇒ 舍入 0 ⇒ 冻结为 1。
            // 会让它失败的缺陷：返回 0 动量（后续除零/零动量接触）。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(0.001f, 1f, 1f, ImpactProfiles.Pierce), Is.EqualTo(1));

            // 旧兼容量化：MomentumUnits == RoundHalfUp(legacyDeliveredMomentum · 100)（验收标准 :393）。
            Assert.That(MomentumQuantizer.QuantizeLegacyMomentumUnits(1f), Is.EqualTo(100));
            Assert.That(MomentumQuantizer.QuantizeLegacyMomentumUnits(2.5f), Is.EqualTo(250));
            // 2.505f 的 double 值为 2.505000114441…，×100 = 250.5000114… ⇒ half-up 251（截断会给 250）。
            Assert.That(MomentumQuantizer.QuantizeLegacyMomentumUnits(2.505f), Is.EqualTo(251));
            Assert.That(MomentumQuantizer.QuantizeLegacyMomentumUnits(2.5049f), Is.EqualTo(250));
            // Blunt（100%）与旧 100 倍比例必须一致：Mass·Speed·Force = 1.5 ⇒ 两路都是 150。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(1.5f, 1f, 1f, ImpactProfiles.Blunt),
                Is.EqualTo(MomentumQuantizer.QuantizeLegacyMomentumUnits(1.5f)));

            // 非法输入稳定拒绝（任务包 08:99），不得钳制/饱和。
            AssertMomentumOutOfRange(() => MomentumQuantizer.QuantizeMomentumUnits(0f, 1f, 1f, ImpactProfiles.Blunt));
            AssertMomentumOutOfRange(() => MomentumQuantizer.QuantizeMomentumUnits(-1f, 1f, 1f, ImpactProfiles.Blunt));
            AssertMomentumOutOfRange(() => MomentumQuantizer.QuantizeMomentumUnits(1f, float.NaN, 1f, ImpactProfiles.Blunt));
            AssertMomentumOutOfRange(() => MomentumQuantizer.QuantizeMomentumUnits(1f, 1f, float.PositiveInfinity, ImpactProfiles.Blunt));
            AssertMomentumOutOfRange(() => MomentumQuantizer.QuantizeMomentumUnits(1f, 1f, 1f, default(ImpactProfileId)),
                "未知冲击 Profile 也必须用同一冻结码");
        }

        [Test]
        public void MomentumQuantizationOccursOnceBeforeArbitration()
        {
            // 结构性保证：量化只发生一次，之后全链路只有整数。
            // 会让它失败的缺陷：把 Mass/MomentumSpeed/ForceMultiplier 传进 Clash 求解或目标聚合
            // （= 仲裁期间读浮点属性 ⇒ 平台浮点差异会改变战斗结果）、或让 MomentumPacket 携带 float。
            AssertNoFloatingPoint(typeof(MomentumPacket));
            AssertNoFloatingPoint(typeof(ClashParticipant));
            AssertNoFloatingPoint(typeof(MomentumClashInput));
            AssertNoFloatingPoint(typeof(ClashParticipantResolution));
            AssertNoFloatingPoint(typeof(ClashResidualImpact));
            AssertNoFloatingPoint(typeof(MomentumClashResolution));
            AssertNoFloatingPoint(typeof(TargetContact));
            AssertNoFloatingPoint(typeof(TargetContactResolution));
            AssertNoFloatingPoint(typeof(AggregatedContactComponent));
            AssertNoFloatingPoint(typeof(AggregatedChannelDamage));
            AssertNoFloatingPoint(typeof(TargetAggregateResolution));

            AssertNoFloatingPointParameters(typeof(MomentumClashSolver));
            AssertNoFloatingPointParameters(typeof(TargetAggregator));

            // 唯一允许读浮点的地方是量化器本身（且只在参数上）。
            bool quantizerHasFloatParameter = false;
            foreach (MethodInfo method in typeof(MomentumQuantizer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (parameter.ParameterType == typeof(float) || parameter.ParameterType == typeof(double))
                        quantizerHasFloatParameter = true;
                }
            }
            Assert.That(quantizerHasFloatParameter, Is.True,
                "动量量化器是唯一允许接触浮点的边界；它若不再有浮点参数，说明量化点被搬走了");

            // 一次量化：RawAmount 0.1001（创作态生命点）⇒ RoundHalfUp(0.1001 × 1024) = RoundHalfUp(102.5024) = 103。
            // 会让它失败的缺陷：截断（给 102）、或者在抵抗之后再量化一次（会产生 102/103 之外的第三个值）。
            Assert.That(MomentumQuantizer.QuantizeDamageQ10(0.1001f), Is.EqualTo(103));
            Assert.That(MomentumQuantizer.QuantizeDamageQ10(1.5f), Is.EqualTo(1536));

            // 量化后的载荷本身只有整数：动量包在往返后逐位相等，且 0 动量被拒绝。
            MomentumPacket packet = MomentumPacket.Require(GridDirection.East, 1000, ImpactProfiles.Blunt);
            Assert.That(packet.MomentumUnits, Is.EqualTo(1000));
            Assert.That(packet.IsValid, Is.True);
            AssertMomentumOutOfRange(() => MomentumPacket.Require(GridDirection.East, 0, ImpactProfiles.Blunt));
            AssertMomentumOutOfRange(() => MomentumPacket.Require(GridDirection.East, -1, ImpactProfiles.Blunt));

            // 方向：Normalize12(Facing + OffsetSteps)，环绕且接受负数。
            Assert.That(MomentumRuleTable.Normalize12(12), Is.EqualTo(0));
            Assert.That(MomentumRuleTable.Normalize12(13), Is.EqualTo(1));
            Assert.That(MomentumRuleTable.Normalize12(-1), Is.EqualTo(11));
            Assert.That(MomentumRuleTable.Normalize12(-13), Is.EqualTo(11));
            Assert.That(MomentumRuleTable.ResolveMomentumDirection(GridDirection.EastSouth, 2),
                Is.EqualTo(GridDirection.EastNorth), "11 + 2 = 13 ⇒ 1");
            MomentumPacket quantized = MomentumQuantizer.QuantizePacket(GridDirection.North, 3,
                1f, 1f, 1f, ImpactProfiles.Blunt);
            Assert.That((int)quantized.Direction, Is.EqualTo(6), "3 + 3 = 6 ⇒ West");
            Assert.That(quantized.MomentumUnits, Is.EqualTo(100));
            Assert.That(quantized.ImpactProfileId, Is.EqualTo(ImpactProfiles.Blunt));
        }

        [Test]
        public void DamageChannelDoesNotChangeMomentumProfileAndViceVersa()
        {
            // 00 号规则 24：DamageChannelId 与 ImpactProfileId 正交，不得重新合并。
            // 会让它失败的缺陷：用伤害通道推导传递百分数（例如"blunt 通道 ⇒ 100"），
            // 或者让量化器接受/读取 DamageComponents。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(2f, 3f, 1f, ImpactProfiles.Blunt), Is.EqualTo(600));
            // 同通道不同 Profile ⇒ 动量不同（600 / 360 / 180）。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(2f, 3f, 1f, ImpactProfiles.Slash), Is.EqualTo(360));
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(2f, 3f, 1f, ImpactProfiles.Pierce), Is.EqualTo(180));
            // 量化器签名里没有通道：反射确认它不接收 DamageChannelId / DamageComponentSpec。
            foreach (MethodInfo method in typeof(MomentumQuantizer).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(DamageChannelId)),
                        method.Name + " 不得按伤害通道量化动量");
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(DamageComponentSpec)),
                        method.Name + " 不得消费伤害分量");
                }
            }

            // 反向：聚合路径完全不携带 ImpactProfileId（通道与动量在聚合侧也不耦合）。
            foreach (FieldInfo field in typeof(TargetContact).GetFields())
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(ImpactProfileId)));
            foreach (PropertyInfo property in typeof(TargetContact).GetProperties())
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(ImpactProfileId)));

            // 同一份 DamageComponents 在"不同动量"下给出完全相同的伤害（伤害与动量是两个独立结果）。
            var components = new List<DamageComponentSpec>
            {
                new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable | DamageTagMask.Guardable)
            };
            TargetAggregateResolution fast = TargetAggregator.Aggregate(new TargetAggregationInput(
                new UnitId(9), null, 1000,
                new List<TargetContact> { Contact(1, 11, 9, 0, GridDirection.East, 600, components) }));
            TargetAggregateResolution slow = TargetAggregator.Aggregate(new TargetAggregationInput(
                new UnitId(9), null, 1000,
                new List<TargetContact> { Contact(1, 11, 9, 0, GridDirection.East, 180, components) }));
            // 伤害侧：10 生命点 = 10 × 1024 = 10240 Q10，两个接触完全相同。
            Assert.That(fast.TotalDamageQ10, Is.EqualTo(10240L));
            Assert.That(slow.TotalDamageQ10, Is.EqualTo(10240L), "伤害只由 DamageComponents 决定，与动量/Profile 无关");
            // 冲击侧：只由动量决定 —— 用它证明上一条不是"两边都没算"的空断言。
            Assert.That(fast.TotalImpactUnits, Is.EqualTo(600L));
            Assert.That(slow.TotalImpactUnits, Is.EqualTo(180L));
        }

        [Test]
        public void NormalAndResidualDamageUseFrozenDivisors()
        {
            // 主方案 0.4.2.1 :400-402：NormalImpactDamageDivisor = 5000、ClashResidualDamageDivisor = 1000，
            // 整数除法（向下取整）。
            // 会让它失败的缺陷：把 5000/1000 写成 500/100 或 1000/100、或改用浮点除法后四舍五入
            // （4999/5000 会被抬成 1）。
            Assert.That(MomentumQuantizer.NormalImpactDamageLifePoints(0), Is.EqualTo(0));
            Assert.That(MomentumQuantizer.NormalImpactDamageLifePoints(4999), Is.EqualTo(0));
            Assert.That(MomentumQuantizer.NormalImpactDamageLifePoints(5000), Is.EqualTo(1));
            Assert.That(MomentumQuantizer.NormalImpactDamageLifePoints(9999), Is.EqualTo(1));
            Assert.That(MomentumQuantizer.NormalImpactDamageLifePoints(10000), Is.EqualTo(2));
            Assert.That(MomentumQuantizer.NormalImpactDamageLifePoints(25000), Is.EqualTo(5));

            Assert.That(MomentumQuantizer.ClashResidualDamageLifePoints(999), Is.EqualTo(0));
            Assert.That(MomentumQuantizer.ClashResidualDamageLifePoints(1000), Is.EqualTo(1));
            Assert.That(MomentumQuantizer.ClashResidualDamageLifePoints(1999), Is.EqualTo(1));
            Assert.That(MomentumQuantizer.ClashResidualDamageLifePoints(2000), Is.EqualTo(2));

            // 生成的分量：通道固定 physical.blunt、默认标签（Blockable|Guardable）、单一分量。
            DamageComponentSpec impact = MomentumQuantizer.NormalImpactComponent(5000);
            Assert.That(impact.ChannelId, Is.EqualTo(DamageChannels.PhysicalBlunt));
            Assert.That(impact.RawAmount, Is.EqualTo(1f));
            Assert.That(impact.Tags, Is.EqualTo(DamageChannelCatalog.GetDefaultTags(DamageChannels.PhysicalBlunt)));

            // ClashResidualImpact 只构造剩余动量分量，不得重复附加攻击基础分量（必须恰好 1 个分量）。
            DamageComponentSpec residual = MomentumQuantizer.ClashResidualComponent(3000);
            Assert.That(residual.ChannelId, Is.EqualTo(DamageChannels.PhysicalBlunt));
            Assert.That(residual.RawAmount, Is.EqualTo(3f));
            Assert.That(DamageComponentValidation.Validate(impact), Is.Null);
            Assert.That(DamageComponentValidation.Validate(residual), Is.Null);

            // 负动量是非法输入（不是"取绝对值"）。
            AssertMomentumOutOfRange(() => MomentumQuantizer.NormalImpactDamageLifePoints(-1));
            AssertMomentumOutOfRange(() => MomentumQuantizer.ClashResidualDamageLifePoints(-1));
        }

        [Test]
        public void LargeMomentumAndConflictGroupCannotOverflowOrWrap()
        {
            // 任务包 08:105：单 Intent 动量是正 int；乘法、TotalOppositionLoss、Q10 合力与 TotalImpactUnits
            // 全部使用 checked long；越界稳定失败，不允许溢出回绕或静默饱和。
            // 会让它失败的缺陷：用 int 累加多方损耗（3221225471 会回绕成负数 ⇒ RemainingMomentum 变成巨额正数）、
            // 或对超上界的量化结果做 unchecked 转换。
            const int max = int.MaxValue;
            var participants = new List<ClashParticipant>
            {
                new ClashParticipant(new UnitId(1), new ActionPlanId(11), MomentumPacket.Require(GridDirection.East, max)),
                new ClashParticipant(new UnitId(2), new ActionPlanId(12), MomentumPacket.Require(GridDirection.West, max)),
                new ClashParticipant(new UnitId(3), new ActionPlanId(13), MomentumPacket.Require(GridDirection.NorthWest, max))
            };
            var pairs = new List<ClashPair>
            {
                new ClashPair(new ActionPlanId(11), new ActionPlanId(12)),   // d=6 ⇒ 1024
                new ClashPair(new ActionPlanId(11), new ActionPlanId(13)),   // d=4 ⇒ 512
                new ClashPair(new ActionPlanId(12), new ActionPlanId(14))    // 未知端点 ⇒ 必须拒绝
            };
            AssertClashInvalid(() => MomentumClashSolver.Solve(new MomentumClashInput(participants,
                new List<ClashPair> { pairs[2] })), "unknownEndpoint");
            AssertClashInvalid(() => MomentumClashSolver.Solve(new MomentumClashInput(participants,
                new List<ClashPair> { new ClashPair(new ActionPlanId(11), new ActionPlanId(11)) })), "selfPair");

            var resolution = MomentumClashSolver.Solve(new MomentumClashInput(participants,
                new List<ClashPair> { pairs[0], pairs[1] }));

            // 手算：A 承受 B 的全部 2147483647（d=6）与 C 的一半 1073741824（d=4：2147483647·512 = 1099511627264；
            // (1099511627264 + 512)/1024 = 1073741824.0 ⇒ 1073741824）。
            // 总和 3221225471 > int.MaxValue：用它验证 checked long（int 实现会回绕）。
            ClashParticipantResolution a = Find(resolution, 11);
            Assert.That(a.TotalOppositionLossUnits, Is.EqualTo(3221225471L));
            Assert.That(a.RemainingMomentumUnits, Is.EqualTo(0), "损耗超过原始动量 ⇒ 剩余必须夹到 0，不得为负");

            // B：只承受 A 的 2147483647 ⇒ 剩余 0；C：只承受 A 的 1073741824 ⇒ 剩余 1073741823。
            Assert.That(Find(resolution, 12).RemainingMomentumUnits, Is.EqualTo(0));
            Assert.That(Find(resolution, 13).RemainingMomentumUnits, Is.EqualTo(max - 1073741824));
            // 守恒：分配总和 == 剩余总和 ⇒ 不丢失、不凭空增加。
            Assert.That(resolution.TotalRemainingMomentumUnits, Is.EqualTo(1073741823L));
            Assert.That(resolution.TotalResidualAllocatedUnits, Is.EqualTo(resolution.TotalRemainingMomentumUnits));
            Assert.That(Find(resolution, 13).ResidualImpacts.Count, Is.EqualTo(1));
            Assert.That(Find(resolution, 13).ResidualImpacts[0].ResidualMomentumUnits, Is.EqualTo(1073741823));

            // 量化侧的上界：乘积 > int.MaxValue ⇒ 稳定拒绝（不回绕成负数动量）。
            // 手算：float(21474838) = 21474838（偶数可精确表示），× 100 = 2147483800 > 2147483647 ⇒ 拒绝。
            // 注意 21474836f × 100 = 2147483600 **仍在** int 域内，不能拿它当越界样本。
            AssertMomentumOutOfRange(() => MomentumQuantizer.QuantizeMomentumUnits(1f, 1f, 21474838f, ImpactProfiles.Blunt));
            AssertMomentumOutOfRange(() => MomentumQuantizer.QuantizeMomentumUnits(1e9f, 1e9f, 1e9f, ImpactProfiles.Blunt));
            // 上界内的大值必须精确通过：2.0e7 · 100 = 2000000000（int 域内）。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(20000000f, 1f, 1f, ImpactProfiles.Blunt),
                Is.EqualTo(2000000000));
            // 21474836f × 100 = 2147483600 也必须在域内精确通过（越界判定不能过早触发）。
            Assert.That(MomentumQuantizer.QuantizeMomentumUnits(21474836f, 1f, 1f, ImpactProfiles.Blunt),
                Is.EqualTo(2147483600));

            // 配额：数值与错误码由调用方传入（Combat **不**声明、**不**镜像图上限），超限整组失败。
            // 这里就以构图流的唯一权威常量作为调用方取值（引用而非复制）。
            // 会让它失败的缺陷：把上限硬编码进求解器（今天同值、明天分叉）、或丢边/截断后继续求解。
            var authoritative = new MomentumClashQuota(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED,
                ConflictGraphLimits.MaxConflictGroupNodes, ConflictGraphLimits.MaxConflictGroupEdges);
            Assert.That(MomentumClashSolver.Solve(
                new MomentumClashInput(participants, new List<ClashPair> { pairs[0], pairs[1] }), authoritative),
                Is.Not.Null, "权威配额下必须正常求解");

            var nodeQuota = new MomentumClashQuota(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED, 2,
                ConflictGraphLimits.MaxConflictGroupEdges);
            var ex = Assert.Throws<LogicDefinitionException>(() => MomentumClashSolver.Solve(
                new MomentumClashInput(participants, new List<ClashPair> { pairs[0], pairs[1] }), nodeQuota));
            Assert.That(ex.ErrorCode, Is.EqualTo(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED),
                "错误码必须逐字来自调用方（这里就是构图流的冻结码）");
            var edgeQuota = new MomentumClashQuota(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED,
                ConflictGraphLimits.MaxConflictGroupNodes, 1);
            var edgeEx = Assert.Throws<LogicDefinitionException>(() => MomentumClashSolver.Solve(
                new MomentumClashInput(participants, new List<ClashPair> { pairs[0], pairs[1] }), edgeQuota));
            Assert.That(edgeEx.ErrorCode, Is.EqualTo(InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED));

            // 聚合侧大数：256 个接触 × 1000 动量（全部 East）——
            // TotalImpactUnits = 256000；合力向量 X = 1024 × 256000 = 262144000；
            // maxDot（East）= 1024 × 262144000 = 268435456000（> int.MaxValue ⇒ 点积必须用 long；
            //   截断成 int 会得到 int.MinValue ⇒ hasDirection=false ⇒ 下面 ResultantDirection 断言立刻失败）；
            // 强度 = maxDot/1024²（裁定 5 · 读法 B）= 268435456000/1048576 = 256000 = Σm·cosθ；
            // KnockbackSteps = floor(256000/1000) = 256。
            var contacts = new List<TargetContact>(256);
            var zeroDamage = new List<DamageComponentSpec>
            {
                new DamageComponentSpec(DamageChannels.PhysicalBlunt, 0f, DamageTagMask.Blockable)
            };
            for (int i = 0; i < 256; i++)
                contacts.Add(Contact(i + 1, 1000 + i, 999, 0, GridDirection.East, 1000, zeroDamage));
            TargetAggregateResolution large = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(999), null, 1000, contacts));
            Assert.That(large.TotalImpactUnits, Is.EqualTo(256000L));
            Assert.That(large.ResultantVectorXQ10, Is.EqualTo(262144000L));
            Assert.That(large.ResultantVectorYQ10, Is.EqualTo(0L));
            Assert.That(large.ResultantDirection, Is.EqualTo(GridDirection.East));
            Assert.That(large.ResultantMomentumUnits, Is.EqualTo(256000L),
                "maxDot/1024² = 268435456000/1048576 = 256000 = Σm·cosθ（读法 A 会给 262144000）");
            Assert.That(large.KnockbackSteps, Is.EqualTo(256), "floor(256000/1000) = 256");

            // 同上但控制阻力=100 ⇒ 步数 = floor(256000/100) = 2560，仍在 int 域内；
            // 强度本身已落在 int 域内，但 maxDot = 268435456000 不落 ⇒ 点积写成 int 会回绕（见上）。
            TargetAggregateResolution huge = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(999), null, 100, contacts));
            Assert.That(huge.KnockbackSteps, Is.EqualTo(2560));

            // 256 个接触 × int.MaxValue 动量：
            //   TotalImpactUnits  = 256 × 2147483647 = 549755813632（> int.MaxValue ⇒ 累计必须用 long）
            //   合力向量 X        = 1024 × 549755813632 = 562949953159168（long 域）
            //   maxDot（East）    = 1024 × 562949953159168 = 576460752034988032（long 域）
            //   合力强度（maxDot/1024²，裁定 5 · 读法 B）= 576460752034988032/1048576 = 549755813632
            //                     = Σm·cosθ（256 个接触全部同向 ⇒ 与 TotalImpactUnits 同值）
            var maxContacts = new List<TargetContact>(256);
            for (int i = 0; i < 256; i++)
                maxContacts.Add(Contact(i + 1, 1000 + i, 999, 0, GridDirection.East, int.MaxValue, zeroDamage));
            TargetAggregateResolution maxAggregate = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(999), null, 1000000, maxContacts));
            Assert.That(maxAggregate.TotalImpactUnits, Is.EqualTo(549755813632L));
            Assert.That(maxAggregate.ResultantVectorXQ10, Is.EqualTo(562949953159168L));
            Assert.That(maxAggregate.ResultantMomentumUnits, Is.EqualTo(549755813632L),
                "maxDot/1024² = 576460752034988032/1048576 = 549755813632（读法 A 会给 562949953159168）");
            // 击退步数 = 549755813632 / 1000000 = 549755.813632 ⇒ 向下取整 549755（long 除法）。
            Assert.That(maxAggregate.KnockbackSteps, Is.EqualTo(549755));
            Assert.That(maxAggregate.IsStaggered, Is.True);
            Assert.That(maxAggregate.IsKnockedDown, Is.True);

            // 同一批输入但控制阻力 = 1 ⇒ 549755813632 步 > int.MaxValue ⇒
            // 必须稳定拒绝（MOMENTUM_OUT_OF_RANGE），不得 (int) 强转回绕成负数请求步数。
            var overflowEx = Assert.Throws<LogicDefinitionException>(() => TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(999), null, 1, maxContacts)));
            Assert.That(overflowEx.ErrorCode, Is.EqualTo(MomentumCodes.MOMENTUM_OUT_OF_RANGE),
                "越界击退步数必须稳定拒绝，而不是 (int) 强转回绕");
        }

        // ———————————————————————————————————————————————————————————————— 辅助

        private static ClashParticipantResolution Find(MomentumClashResolution resolution, long actionPlanId)
        {
            for (int i = 0; i < resolution.Participants.Count; i++)
            {
                if (resolution.Participants[i].ActionPlanId.Value == actionPlanId) return resolution.Participants[i];
            }
            Assert.Fail("participant not found: " + actionPlanId);
            return null;
        }

        private static TargetContact Contact(long sourceUnitId, long sourcePlanId, long targetUnitId,
            long targetPlanId, GridDirection direction, int momentumUnits,
            IReadOnlyList<DamageComponentSpec> components)
            => new TargetContact(new TargetContactKey(TargetContactType.DirectHit,
                    new UnitId(sourceUnitId), new ActionPlanId(sourcePlanId),
                    new UnitId(targetUnitId), new ActionPlanId(targetPlanId)),
                direction, momentumUnits, 0, null, components);

        private static void AssertMomentumOutOfRange(TestDelegate action, string because = null)
        {
            var ex = Assert.Throws<LogicDefinitionException>(action);
            Assert.That(ex.ErrorCode, Is.EqualTo(MomentumCodes.MOMENTUM_OUT_OF_RANGE), because);
        }

        private static void AssertClashInvalid(TestDelegate action, string because)
        {
            var ex = Assert.Throws<LogicDefinitionException>(action);
            Assert.That(ex.ErrorCode, Is.EqualTo(MomentumCodes.CLASH_INPUT_INVALID), because);
        }

        /// <summary>
        /// 仲裁路径类型不得暴露任何 float/double 成员：这是"量化一次之后全程整数"的结构性守护。
        /// </summary>
        private static void AssertNoFloatingPoint(Type type)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(float)), type.Name + "." + field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(double)), type.Name + "." + field.Name);
            }
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(float)), type.Name + "." + property.Name);
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(double)), type.Name + "." + property.Name);
            }
            foreach (ConstructorInfo constructor in type.GetConstructors())
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(float)), type.Name + "." + parameter.Name);
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(double)), type.Name + "." + parameter.Name);
                }
            }
        }

        /// <summary>仲裁路径的公开方法不得接收 float/double 参数。</summary>
        private static void AssertNoFloatingPointParameters(Type type)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                                                          | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(float)),
                        type.Name + "." + method.Name + "(" + parameter.Name + ")");
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(double)),
                        type.Name + "." + method.Name + "(" + parameter.Name + ")");
                }
            }
        }
    }
}
