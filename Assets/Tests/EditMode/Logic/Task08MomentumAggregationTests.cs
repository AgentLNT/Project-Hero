using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 目标聚合（「必须产出」11）离线用例：主方案 0.4.2.1 五步。
    ///
    /// 口径（父代理裁定 P0-1 / P0-2 / P1-3）：
    /// <c>RawAmount</c> 是创作态生命点，在应用边界**一次性**量化为 <c>int</c> Q10（1 生命点 = 1024 Q10）；
    /// 抵抗按"被动 → 动作"依次乘算并复用既有唯一舍入原语 <c>TickQuantization.RoundHalfUpDivide</c>。
    /// 每条期望值都手算并写在注释里。
    /// </summary>
    [TestFixture]
    public class Task08MomentumAggregationTests
    {
        [Test]
        public void PassiveAndActionResistanceMultiplyInDeclaredOrder()
        {
            // 分量：physical.blunt、RawAmount = 1.5 生命点 ⇒ RawQ10 = 1536。
            // 被动抵抗 512（因子 1024-512 = 512）、动作抵抗 887（因子 1024-887 = 137）。
            //   被动后 = RoundHalfUp(1536 × 512 / 1024) = 768（恰好整除）
            //   动作后 = RoundHalfUp(768 × 137 / 1024) = RoundHalfUp(102.75) = 103
            // **判别点 1（不相加）**：若把两层抵抗相加（512+887 = 1399 > 1024）会得到 0（夹到 0）或 1536（忽略抵抗），
            // 都不是 103 —— 抵抗必须是"依次乘算"。
            // **判别点 2（顺序）**：见本用例末尾的专用见证（R=6、被动 100、动作 256 ⇒ 4 vs 5）。
            // 会让它失败的缺陷：把 (1024-r) 写成 r（会得到 768×887/1024 = 665）、抵抗相加、或漏掉一次舍入。
            var components = new List<DamageComponentSpec>
            {
                new DamageComponentSpec(DamageChannels.PhysicalBlunt, 1.5f, DamageTagMask.Blockable | DamageTagMask.Guardable)
            };
            var passive = new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 512 } };
            var action = new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 887 } };
            var contact = new TargetContact(Key(TargetContactType.DirectHit, 1, 11, 9), GridDirection.East, 0, 0,
                action, components);
            TargetAggregateResolution resolution = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), passive, 1000, new List<TargetContact> { contact }));

            AggregatedContactComponent component = resolution.Contacts[0].Components[0];
            Assert.That(component.RawQ10, Is.EqualTo(1536L), "1.5 生命点 = 1536 Q10（唯一一次量化）");
            Assert.That(component.AfterPassiveResistanceQ10, Is.EqualTo(768L));
            Assert.That(component.AfterActionResistanceQ10, Is.EqualTo(103L),
                "768 × (1024-887) / 1024 = 102.75 ⇒ 103；把因子写成 887 会给 665，相加会给 0");
            Assert.That(component.PassiveResistanceQ10, Is.EqualTo(512));
            Assert.That(component.ActionResistanceQ10, Is.EqualTo(887));
            Assert.That(resolution.TotalDamageQ10, Is.EqualTo(103L));

            // 抵抗必须是乘法（0.5 × 0.5 = 0.25）：1536 × 512 × 512 两步 ⇒ 384。
            var half = new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 512 } };
            var doubleHalf = new TargetContact(Key(TargetContactType.DirectHit, 1, 11, 9), GridDirection.East, 0, 0,
                half, components);
            TargetAggregateResolution multiplicative = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), half, 1000, new List<TargetContact> { doubleHalf }));
            Assert.That(multiplicative.TotalDamageQ10, Is.EqualTo(384L), "1536 × 0.5 × 0.5 = 384（相乘不相加）");

            // —— 顺序见证（穷举 R、抵抗组合找到的最小判别样本，手算复核）——
            // RawAmount = 6/1024 生命点 = 0.005859375（二进制精确）⇒ RawQ10 = 6；被动 100（因子 924）、动作 256（因子 768）。
            //   被动 → 动作（声明顺序）：RoundHalfUp(6×924/1024) = 5.414 ⇒ 5；RoundHalfUp(5×768/1024) = 3.75 ⇒ **4**
            //   动作 → 被动（颠倒顺序）：RoundHalfUp(6×768/1024) = 4.5 ⇒ 5（half-up）；RoundHalfUp(5×924/1024) = 4.51 ⇒ **5**
            // ⇒ 4 ≠ 5：实现若把两层顺序写反，本断言必然失败。
            var orderComponents = new List<DamageComponentSpec>
            {
                new DamageComponentSpec(DamageChannels.PhysicalBlunt, 6f / 1024f,
                    DamageTagMask.Blockable | DamageTagMask.Guardable)
            };
            var orderContact = new TargetContact(Key(TargetContactType.DirectHit, 1, 11, 9), GridDirection.East, 0, 0,
                new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 256 } }, orderComponents);
            TargetAggregateResolution ordered = TargetAggregator.Aggregate(new TargetAggregationInput(
                new UnitId(9), new Dictionary<DamageChannelId, int> { { DamageChannels.PhysicalBlunt, 100 } }, 1000,
                new List<TargetContact> { orderContact }));
            Assert.That(ordered.Contacts[0].Components[0].RawQ10, Is.EqualTo(6L));
            Assert.That(ordered.TotalDamageQ10, Is.EqualTo(4L),
                "声明顺序（被动→动作）给 4；颠倒顺序会给 5");
        }

        [Test]
        public void TrueDamageBypassesPassiveGuardAndBlockResistance()
        {
            // true 通道默认同时带 BypassPassiveResistance | BypassActionResistance（DamageChannelCatalog），
            // 且不可 Block/Guard。即使被动与动作抵抗都配成 1024（完全抵抗），真伤也必须原样通过。
            // 会让它失败的缺陷：先乘以抵抗再判断绕过（结果是 0）、或把 true 通道当普通通道处理。
            const float raw = 10f;   // 10 生命点 = 10240 Q10
            var components = new List<DamageComponentSpec>
            {
                new DamageComponentSpec(DamageChannels.True, raw, DamageChannelCatalog.GetDefaultTags(DamageChannels.True))
            };
            var fullResistance = new Dictionary<DamageChannelId, int> { { DamageChannels.True, 1024 } };
            var contact = new TargetContact(Key(TargetContactType.DirectHit, 1, 11, 9), GridDirection.East, 0, 0,
                fullResistance, components);
            TargetAggregateResolution resolution = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), fullResistance, 1000, new List<TargetContact> { contact }));

            AggregatedContactComponent component = resolution.Contacts[0].Components[0];
            Assert.That(component.RawQ10, Is.EqualTo(10240L));
            Assert.That(component.AfterPassiveResistanceQ10, Is.EqualTo(10240L), "绕过被动抵抗");
            Assert.That(component.AfterActionResistanceQ10, Is.EqualTo(10240L), "绕过动作抵抗");
            Assert.That(component.PassiveResistanceQ10, Is.EqualTo(0), "绕过分量记录的抵抗来源为 0（审计）");
            Assert.That(component.ActionResistanceQ10, Is.EqualTo(0));
            Assert.That(resolution.TotalDamageQ10, Is.EqualTo(10240L));

            // Block 只能作用于 Blockable 分量：true 分量不可格挡 ⇒ BuildDefendedContact 不得给它 1024。
            // 会让它失败的缺陷：让 Block 变成"删除全部载荷"（连 Unblockable 分量一起吞掉）。
            var block = new BlockPayloadSpec(DefenseTagMask.Blockable | DefenseTagMask.Guardable);
            var guarded = new GuardPayloadSpec(new Dictionary<DamageChannelId, int> { { DamageChannels.True, 1024 } },
                1024, DefenseTagMask.Guardable);
            TargetContact defended = TargetAggregator.BuildDefendedContact(
                Key(TargetContactType.DirectHit, 1, 11, 9), GridDirection.East, 500, components,
                AttackTagMask.Blockable | AttackTagMask.Reactable, guarded, block);
            Assert.That(defended.ActionDamageResistanceQ10[DamageChannels.True], Is.EqualTo(0),
                "true 分量既不可 Block 也不可 Guard ⇒ 动作抵抗为 0");
            // 但 Block 对**可格挡动量**仍然生效（动量与通道正交：真伤不豁免动量抵抗）。
            Assert.That(defended.ActionMomentumResistanceQ10, Is.EqualTo(BattleRules.FullBlockResistanceQ10));

            // 普通通道的分量在 Block 下必须完全归零（Blockable 且攻击可格挡）。
            var blunt = new List<DamageComponentSpec>
            {
                new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable | DamageTagMask.Guardable)
            };
            TargetContact blocked = TargetAggregator.BuildDefendedContact(
                Key(TargetContactType.DirectHit, 1, 11, 9), GridDirection.East, 500, blunt,
                AttackTagMask.Blockable, null, block);
            TargetAggregateResolution blockedResolution = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), null, 1000, new List<TargetContact> { blocked }));
            Assert.That(blockedResolution.TotalDamageQ10, Is.EqualTo(0L), "Block 对合格分量严格为 0");
            Assert.That(blockedResolution.TotalImpactUnits, Is.EqualTo(0L), "Block 对可格挡动量严格为 0");
        }

        [Test]
        public void DamageAggregationUsesContactThenChannelCanonicalOrder()
        {
            // 主方案 0.4.2.1 :421：全部接触先按 ContactKey → DamageChannelId 排序，再分通道求和。
            // 会让它失败的缺陷：按容器发现顺序累加（输出顺序随输入排列变化）、
            // 或把不同通道合并成一个标量（7 个通道必须各自成条）。
            var contacts = new List<TargetContact>
            {
                Contact(TargetContactType.DirectHit, 2, 22, new[]
                {
                    Component(DamageChannels.ElementalFire, 1f),
                    Component(DamageChannels.PhysicalBlunt, 2f)
                }),
                Contact(TargetContactType.DirectHit, 1, 11, new[]
                {
                    Component(DamageChannels.PhysicalSlash, 3f),
                    Component(DamageChannels.PhysicalBlunt, 1f)
                }),
                Contact(TargetContactType.ClashResidualImpact, 1, 11, new[]
                {
                    Component(DamageChannels.PhysicalBlunt, 4f)
                })
            };
            TargetAggregateResolution resolution = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), null, 1000, contacts));

            // 接触顺序：DirectHit(0) 先于 ClashResidualImpact(1)；同类型内按来源 UnitId 升序 ⇒ 1、2。
            Assert.That(resolution.Contacts.Count, Is.EqualTo(3));
            Assert.That(resolution.Contacts[0].Key.SourceUnitId, Is.EqualTo(1L));
            Assert.That(resolution.Contacts[1].Key.SourceUnitId, Is.EqualTo(2L));
            Assert.That(resolution.Contacts[2].Key.ContactType, Is.EqualTo(TargetContactType.ClashResidualImpact));

            // 通道总数按通道 ID 序数升序：elemental.fire < physical.blunt < physical.slash。
            Assert.That(resolution.ChannelTotals.Count, Is.EqualTo(3));
            Assert.That(resolution.ChannelTotals[0].ChannelId, Is.EqualTo(DamageChannels.ElementalFire));
            Assert.That(resolution.ChannelTotals[0].RawQ10, Is.EqualTo(1024L));
            Assert.That(resolution.ChannelTotals[1].ChannelId, Is.EqualTo(DamageChannels.PhysicalBlunt));
            Assert.That(resolution.ChannelTotals[1].RawQ10, Is.EqualTo(7168L), "1 + 2 + 4 = 7 生命点 = 7168 Q10");
            Assert.That(resolution.ChannelTotals[2].ChannelId, Is.EqualTo(DamageChannels.PhysicalSlash));
            Assert.That(resolution.ChannelTotals[2].RawQ10, Is.EqualTo(3072L));
            Assert.That(resolution.TotalDamageQ10, Is.EqualTo(11264L), "1024 + 7168 + 3072");

            // 排列不变性：接触顺序与分量顺序全部打乱后，逐字节相同。
            var shuffled = new List<TargetContact>
            {
                Contact(TargetContactType.ClashResidualImpact, 1, 11, new[] { Component(DamageChannels.PhysicalBlunt, 4f) }),
                Contact(TargetContactType.DirectHit, 2, 22, new[]
                {
                    Component(DamageChannels.PhysicalBlunt, 2f),
                    Component(DamageChannels.ElementalFire, 1f)
                }),
                Contact(TargetContactType.DirectHit, 1, 11, new[]
                {
                    Component(DamageChannels.PhysicalBlunt, 1f),
                    Component(DamageChannels.PhysicalSlash, 3f)
                })
            };
            Assert.That(Dump(TargetAggregator.Aggregate(
                    new TargetAggregationInput(new UnitId(9), null, 1000, shuffled))),
                Is.EqualTo(Dump(resolution)), "任意排列必须给出逐位相同的聚合结果");

            // 同一接触键重复出现 ⇒ 稳定拒绝（"同一攻击对同一目标只结算一次"）。
            var duplicated = new List<TargetContact> { contacts[1], contacts[1] };
            var duplicateEx = Assert.Throws<LogicDefinitionException>(() => TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), null, 1000, duplicated)));
            Assert.That(duplicateEx.ErrorCode, Is.EqualTo(MomentumCodes.TARGET_AGGREGATE_INVALID));
        }

        [Test]
        public void ControlThresholdsAndKnockbackUseQuantizedResistance()
        {
            // 主方案 0.4.2.1 :423-434：ControlResistanceUnits = max(1, RoundHalfUp(Mass × MomentumSpeed × 100))；
            // 硬直阈值 = CRU；击倒阈值 = ceil(CRU × 3 / 2)；击退 = floor(强度 / CRU)，无方向则 0。
            // 会让它失败的缺陷：漏掉 max(1,·)（半质量单位会得到 0 ⇒ 除零/无限击退）、
            // 击倒阈值写成 floor(3n/2)（奇数 CRU 会少 1）、或用未量化的浮点阻力做除法。
            Assert.That(MomentumQuantizer.QuantizeControlResistanceUnits(50f, 4f), Is.EqualTo(20000), "50×4×100");
            Assert.That(MomentumQuantizer.QuantizeControlResistanceUnits(1.5f, 1f), Is.EqualTo(150));
            Assert.That(MomentumQuantizer.QuantizeControlResistanceUnits(0.001f, 1f), Is.EqualTo(1),
                "0.1 舍入为 0 ⇒ 必须夹到 1（不得出现 0 阻力）");

            // 非法配置稳定拒绝（不得钳制）：0 质量、NaN、无穷都不允许。
            var ex = Assert.Throws<LogicDefinitionException>(
                () => MomentumQuantizer.QuantizeControlResistanceUnits(0f, 1f));
            Assert.That(ex.ErrorCode, Is.EqualTo(MomentumCodes.MOMENTUM_OUT_OF_RANGE));
            Assert.Throws<LogicDefinitionException>(
                () => MomentumQuantizer.QuantizeControlResistanceUnits(1f, float.PositiveInfinity));

            // 12 个接触 × 100 动量（全部 East），CRU = 1000：
            //   TotalImpactUnits = 1200 ≥ 1000 ⇒ 硬直；击倒阈值 = ceil(3000/2) = 1500 > 1200 ⇒ 未击倒
            //   合力 X = 1024 × 1200 = 1228800；maxDot（East）= 1024 × 1228800 = 1258291200；
            //   强度 = maxDot/1024²（裁定 5 · 读法 B）= 1258291200/1048576 = 1200
            //        = Σ m·cosθ = 12 × 100 × cos0° = 1200（与动量同域）；
            //   步数 = floor(1200/1000) = 1
            var contacts = new List<TargetContact>();
            for (int i = 0; i < 12; i++)
                contacts.Add(Contact(TargetContactType.DirectHit, i + 1, 100 + i, new[] { Component(DamageChannels.PhysicalBlunt, 0f) }));
            TargetAggregateResolution resolution = WithMomentum(contacts, 100, 1000);
            Assert.That(resolution.TotalImpactUnits, Is.EqualTo(1200L));
            Assert.That(resolution.ControlResistanceUnits, Is.EqualTo(1000));
            Assert.That(resolution.StaggerThresholdUnits, Is.EqualTo(1000L));
            Assert.That(resolution.KnockdownThresholdUnits, Is.EqualTo(1500L), "ceil(1000×3/2) = 1500");
            Assert.That(resolution.IsStaggered, Is.True);
            Assert.That(resolution.IsKnockedDown, Is.False, "1200 < 1500");
            Assert.That(resolution.ResultantMomentumUnits, Is.EqualTo(1200L),
                "maxDot/1024² = 1258291200/1048576 = 1200 = Σm·cosθ（读法 A 会给 1228800）");
            Assert.That(resolution.KnockbackSteps, Is.EqualTo(1), "floor(1200/1000) = 1");

            // 奇数 CRU 的 ceil：CRU = 1001 ⇒ 3003/2 = 1501.5 ⇒ 1502（floor 会给 1501）。
            TargetAggregateResolution odd = WithMomentum(contacts, 100, 1001);
            Assert.That(odd.KnockdownThresholdUnits, Is.EqualTo(1502L));

            // 合力强度小于 CRU ⇒ 步数为 0（向下取整），即使已经造成冲击。
            // 注意口径（裁定 5 · 读法 B）：击退强度 = maxDot/1024²，而 maxDot = 1024² × Σ(m·cosθ)
            // （方向向量是 1024 标度、动量是已量化的 MomentumUnits）⇒ 同向对齐动量的强度 = Σm。
            // 单个 500 动量接触的强度是 500（= 1 × 500 × cos0°），**不是** 512000。
            // 要让 floor(强度/CRU) = 0，需要 CRU > 500。
            // 会让它失败的缺陷：漏掉第二次 /1024（读法 A 会给 512000 = 1024 × Σm，
            // 本断言与上面 12×100 用例的 1200 会立刻失败）、或在除法前把强度截断成 int。
            TargetAggregateResolution weak = WithMomentum(new List<TargetContact>
            {
                Contact(TargetContactType.DirectHit, 1, 11, new[] { Component(DamageChannels.PhysicalBlunt, 0f) })
            }, 500, 1000000);
            Assert.That(weak.TotalImpactUnits, Is.EqualTo(500L));
            Assert.That(weak.ResultantMomentumUnits, Is.EqualTo(500L),
                "maxDot/1024² = 524288000/1048576 = 500 = Σm·cosθ（读法 A 会给 512000）");
            Assert.That(weak.KnockbackSteps, Is.EqualTo(0), "floor(500/1000000) = 0");
            Assert.That(weak.IsStaggered, Is.False, "500 < 1000000");
        }

        [Test]
        public void TwoAttackersFlankingOneTargetBothDealDamage()
        {
            // 夹击：两个攻击各自独立产伤，目标统一聚合（主方案 0.4.5 :488）。
            // A（UnitId 1）East 300 动量 + blunt 10 生命点；B（UnitId 2）West 300 + blunt 20 生命点。
            //   伤害 = 10 + 20 = 30 生命点 = 30720 Q10（两条接触都必须产伤）
            //   总冲击 = 300 + 300 = 600；反向合力抵消 ⇒ X = 1024×300 - 1024×300 = 0 ⇒ 无击退方向、0 步
            // 会让它失败的缺陷：按"每目标每 Tick 只结算一条接触"去重（只算一个攻击者的伤害）、
            // 或用合力为零反推伤害为零。
            var contacts = new List<TargetContact>
            {
                Contact(TargetContactType.DirectHit, 1, 11, new[] { Component(DamageChannels.PhysicalBlunt, 10f) },
                    GridDirection.East, 300),
                Contact(TargetContactType.DirectHit, 2, 12, new[] { Component(DamageChannels.PhysicalBlunt, 20f) },
                    GridDirection.West, 300)
            };
            TargetAggregateResolution resolution = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), null, 1000, contacts));

            Assert.That(resolution.Contacts.Count, Is.EqualTo(2));
            Assert.That(resolution.ChannelTotals.Count, Is.EqualTo(1), "两条接触都是 physical.blunt ⇒ 合并成一条通道总数");
            Assert.That(resolution.ChannelTotals[0].RawQ10, Is.EqualTo(30720L));
            Assert.That(resolution.TotalDamageQ10, Is.EqualTo(30720L), "30720 = (10+20) × 1024");
            Assert.That(resolution.TotalImpactUnits, Is.EqualTo(600L));
            Assert.That(resolution.ResultantDirection, Is.Null);
            Assert.That(resolution.ResultantMomentumUnits, Is.EqualTo(0L));
            Assert.That(resolution.KnockbackSteps, Is.EqualTo(0));
        }

        [Test]
        public void EqualOppositeImpulsesCancelKnockbackButNotDamageOrImpact()
        {
            // 主方案 0.4.2.1 :436 + 任务包 08:111：硬直/击倒读 TotalImpactUnits，击退读有向合力。
            // 「不把相反合力为零解释为伤害或总冲击为零」（禁止事项）。
            // A East 400 + blunt 5；B West 400 + blunt 7；CRU = 500。
            //   伤害 = (5+7) × 1024 = 12288；总冲击 = 800
            //   合力 X = 0、Y = 0 ⇒ maxDot = 0（不大于 0）⇒ 无方向、强度 0、步数 0
            //   硬直：800 ≥ 500 ⇒ true；击倒：ceil(500×3/2) = 750 ≤ 800 ⇒ true
            // 会让它失败的缺陷：用合力（而不是总冲击）判硬直 —— 那样夹击永远不会硬直；
            // 或者把"合力为零"当成"没有接触"从而丢掉伤害。
            var contacts = new List<TargetContact>
            {
                Contact(TargetContactType.DirectHit, 1, 11, new[] { Component(DamageChannels.PhysicalBlunt, 5f) },
                    GridDirection.East, 400),
                Contact(TargetContactType.DirectHit, 2, 12, new[] { Component(DamageChannels.PhysicalBlunt, 7f) },
                    GridDirection.West, 400)
            };
            TargetAggregateResolution resolution = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), null, 500, contacts));

            Assert.That(resolution.TotalDamageQ10, Is.EqualTo(12288L));
            Assert.That(resolution.TotalImpactUnits, Is.EqualTo(800L));
            Assert.That(resolution.ResultantVectorXQ10, Is.EqualTo(0L));
            Assert.That(resolution.ResultantVectorYQ10, Is.EqualTo(0L));
            Assert.That(resolution.ResultantDirection, Is.Null);
            Assert.That(resolution.ResultantMomentumUnits, Is.EqualTo(0L));
            Assert.That(resolution.KnockbackSteps, Is.EqualTo(0));
            Assert.That(resolution.IsStaggered, Is.True, "800 ≥ 500");
            Assert.That(resolution.IsKnockedDown, Is.True, "800 ≥ ceil(750)");

            // 对照：同一批接触但方向改成 120° 夹角（WestNorth 而非 West）⇒ 合力非零
            // ⇒ 有方向、强度非零（CRU = 500 下仍不足 1 步；见下方步数断言）。
            // 合力用 **Q10 向量**（不是对立系数）：
            //   East 400 ⇒ (1024·400, 0) = (409600, 0)
            //   WestNorth(5) = (-887, 512)，400 ⇒ (-354800, 204800)
            //   X = 409600-354800 = 54800；Y = 204800
            // 逐方向点积（手算全部 12 项后取最大）：
            //   East 1024·54800 = 56115200
            //   EastNorth 887·54800 + 512·204800 = 48607600 + 104857600 = 153465200
            //   NorthEast 512·54800 + 887·204800 = 28057600 + 181657600 = **209715200**
            //   North     0·54800 + 1024·204800 = **209715200**   ← 与 NorthEast **精确并列**
            //   NorthWest -512·54800 + 887·204800 = 153600000；其余更小
            // ⇒ 并列取枚举值较小者 ⇒ NorthEast(2)；
            //   强度 = maxDot/1024²（裁定 5 · 读法 B）= 209715200/1048576 = 200
            //        = Σ m·cosθ = 400 × cos(East→NorthEast) + 400 × cos(WestNorth→NorthEast)
            //        = 400 × (512·1024/1024²) + 400 × ((512·(-887) + 887·512)/1024²) = 400×0.5 + 400×0 = 200；
            //   步数 = floor(200/500) = 0。
            // 会让它失败的缺陷：并列时取了枚举值较大者（会得到 North）、或用浮点 atan2 求角度再最近邻
            // （在并列点会不稳定）、或把对立系数当成方向向量用。
            var angledContacts = new List<TargetContact>
            {
                Contact(TargetContactType.DirectHit, 1, 11, new[] { Component(DamageChannels.PhysicalBlunt, 5f) },
                    GridDirection.East, 400),
                Contact(TargetContactType.DirectHit, 2, 12, new[] { Component(DamageChannels.PhysicalBlunt, 7f) },
                    GridDirection.WestNorth, 400)
            };
            TargetAggregateResolution angled = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), null, 500, angledContacts));
            Assert.That(angled.ResultantVectorXQ10, Is.EqualTo(54800L));
            Assert.That(angled.ResultantVectorYQ10, Is.EqualTo(204800L));
            Assert.That(angled.ResultantDirection, Is.EqualTo(GridDirection.NorthEast),
                "NorthEast 与 North 点积精确并列 ⇒ 必须取枚举值较小者（NorthEast = 2）");
            Assert.That(angled.ResultantMomentumUnits, Is.EqualTo(200L),
                "maxDot/1024² = 209715200/1048576 = 200 = Σm·cosθ（读法 A 会给 204800）");
            Assert.That(angled.KnockbackSteps, Is.EqualTo(0), "floor(200/500) = 0");
        }

        // ———————————————————————————————————————————————————————————————— 辅助

        private static TargetContactKey Key(TargetContactType type, long sourceUnitId, long sourcePlanId, long targetUnitId)
            => new TargetContactKey(type, new UnitId(sourceUnitId), new ActionPlanId(sourcePlanId),
                new UnitId(targetUnitId), default);

        private static DamageComponentSpec Component(DamageChannelId channel, float rawAmount)
            => new DamageComponentSpec(channel, rawAmount, DamageChannelCatalog.GetDefaultTags(channel));

        private static TargetContact Contact(TargetContactType type, long sourceUnitId, long sourcePlanId,
            IReadOnlyList<DamageComponentSpec> components)
            => new TargetContact(Key(type, sourceUnitId, sourcePlanId, 9), GridDirection.East, 0, 0, null, components);

        private static TargetContact Contact(TargetContactType type, long sourceUnitId, long sourcePlanId,
            IReadOnlyList<DamageComponentSpec> components, GridDirection direction, int momentumUnits)
            => new TargetContact(Key(type, sourceUnitId, sourcePlanId, 9), direction, momentumUnits, 0, null, components);

        /// <summary>把一批接触统一改成给定动量（用于控制阈值/击退用例）。</summary>
        private static TargetAggregateResolution WithMomentum(List<TargetContact> template, int momentumUnits,
            int controlResistanceUnits)
        {
            var contacts = new List<TargetContact>(template.Count);
            for (int i = 0; i < template.Count; i++)
            {
                TargetContact source = template[i];
                contacts.Add(new TargetContact(source.Key, source.IncomingDirection, momentumUnits,
                    source.ActionMomentumResistanceQ10, source.ActionDamageResistanceQ10, source.DamageComponents));
            }
            return TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(9), null, controlResistanceUnits, contacts));
        }

        private static string Dump(TargetAggregateResolution resolution)
        {
            var builder = new StringBuilder();
            builder.Append(resolution.TotalDamageQ10).Append('|').Append(resolution.TotalImpactUnits).Append('|')
                .Append(resolution.ResultantVectorXQ10).Append(',').Append(resolution.ResultantVectorYQ10).Append('|')
                .Append(resolution.ResultantDirection.HasValue ? ((int)resolution.ResultantDirection.Value).ToString() : "none")
                .Append('|').Append(resolution.ResultantMomentumUnits).Append('|').Append(resolution.KnockbackSteps)
                .Append("||");
            for (int i = 0; i < resolution.Contacts.Count; i++)
            {
                TargetContactResolution contact = resolution.Contacts[i];
                builder.Append(contact.Key.SourceUnitId).Append(':').Append((int)contact.Key.ContactType)
                    .Append(':').Append(contact.IncomingMomentumUnits).Append(':')
                    .Append(contact.AfterMomentumResistanceUnits).Append('(');
                for (int k = 0; k < contact.Components.Count; k++)
                {
                    builder.Append(contact.Components[k].ChannelId.Value).Append('/')
                        .Append(contact.Components[k].RawQ10).Append('/')
                        .Append(contact.Components[k].AfterPassiveResistanceQ10).Append('/')
                        .Append(contact.Components[k].AfterActionResistanceQ10).Append(',');
                }
                builder.Append(");");
            }
            builder.Append("||");
            for (int i = 0; i < resolution.ChannelTotals.Count; i++)
            {
                builder.Append(resolution.ChannelTotals[i].ChannelId.Value).Append('/')
                    .Append(resolution.ChannelTotals[i].RawQ10).Append('/')
                    .Append(resolution.ChannelTotals[i].AfterPassiveResistanceQ10).Append('/')
                    .Append(resolution.ChannelTotals[i].AfterActionResistanceQ10).Append(',');
            }
            return builder.ToString();
        }
    }
}
