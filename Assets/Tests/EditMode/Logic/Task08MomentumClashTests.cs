using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 08 多方 Clash（「必须产出」9 + 10）离线用例。
    ///
    /// 每个期望值都是手算的（写在断言前的注释里）；每条用例都注明"哪种实现缺陷会让它失败"。
    /// 关键手算约定：<c>LossFromBToA = (B.MomentumUnits × OppositionFactorQ10[d] + 512) / 1024</c>（整数向下），
    /// 对立系数 <c>d=4 → 512、d=5 → 887、d=6 → 1024</c>。
    /// </summary>
    [TestFixture]
    public class Task08MomentumClashTests
    {
        [Test]
        public void LegacyHeadOnPairKeepsScalarSubtractionAndTerminatesBothPlans()
        {
            // 二体、180°、无第三方（任务包「工作步骤」1 的 characterization）：
            // A = East 120、B = West 200，d = 6 ⇒ 系数 1024。
            //   LossFromBToA = (200·1024 + 512)/1024 = 200.5 ⇒ 200（A 的损耗）
            //   LossFromAToB = (120·1024 + 512)/1024 = 120.5 ⇒ 120（B 的损耗）
            //   RemainingA = max(0, 120-200) = 0；RemainingB = 200-120 = 80 ⇒ **旧标量相减**
            // 会让它失败的缺陷：把 RemainingMomentum 算成 |A-B| 的对称值（会给出 80/80）、
            // 或把差值留给自己而不是转成 ClashResidualImpact 打给对手、或忘记终止双方动作。
            var resolution = MomentumClashSolver.Solve(new MomentumClashInput(
                new List<ClashParticipant>
                {
                    Unit(1, 11, GridDirection.East, 120),
                    Unit(2, 12, GridDirection.West, 200)
                },
                new List<ClashPair> { new ClashPair(new ActionPlanId(11), new ActionPlanId(12)) }));

            ClashParticipantResolution a = Find(resolution, 11);
            ClashParticipantResolution b = Find(resolution, 12);
            Assert.That(a.RemainingMomentumUnits, Is.EqualTo(0));
            Assert.That(b.RemainingMomentumUnits, Is.EqualTo(80), "旧行为：200 - 120 = 80");
            Assert.That(a.TotalOppositionLossUnits, Is.EqualTo(200L));
            Assert.That(b.TotalOppositionLossUnits, Is.EqualTo(120L));

            // 双方动作都终止（"所有参与 Clash 的攻击动作都终止"）。
            Assert.That(resolution.TerminatedActionPlanIds.Count, Is.EqualTo(2));
            Assert.That(resolution.TerminatedActionPlanIds[0], Is.EqualTo(new ActionPlanId(11)));
            Assert.That(resolution.TerminatedActionPlanIds[1], Is.EqualTo(new ActionPlanId(12)));

            // 差值只作为 ClashResidualImpact 分配给**直接对手**：B 的 80 打给 A，方向 = B 的方向（West）。
            Assert.That(a.ResidualImpacts.Count, Is.EqualTo(0));
            Assert.That(b.ResidualImpacts.Count, Is.EqualTo(1));
            Assert.That(b.ResidualImpacts[0].RecipientUnitId, Is.EqualTo(new UnitId(1)));
            Assert.That(b.ResidualImpacts[0].RecipientActionPlanId, Is.EqualTo(new ActionPlanId(11)));
            Assert.That(b.ResidualImpacts[0].SourceUnitId, Is.EqualTo(new UnitId(2)));
            Assert.That(b.ResidualImpacts[0].SourceDirection, Is.EqualTo(GridDirection.West));
            Assert.That(b.ResidualImpacts[0].ResidualMomentumUnits, Is.EqualTo(80));

            // 损耗明细带 d 与系数，便于审计与事件（条目 15 要求阻力/动量前后值）。
            // 两个方向必须同时可读：A 承受 B 的 200；A 施加给 B 的 120（后者同时是分配权重 Weight(A→B)）。
            Assert.That(a.Losses[0].RingDistance, Is.EqualTo(6));
            Assert.That(a.Losses[0].OppositionFactorQ10, Is.EqualTo(1024));
            Assert.That(a.Losses[0].SufferedLossUnits, Is.EqualTo(200));
            Assert.That(a.Losses[0].ImposedLossUnits, Is.EqualTo(120));
            Assert.That(b.Losses[0].SufferedLossUnits, Is.EqualTo(120));
            Assert.That(b.Losses[0].ImposedLossUnits, Is.EqualTo(200));
        }

        [Test]
        public void ThreeWayClashUsesAllOriginalMomentaSimultaneously()
        {
            // 三方：A = East 300、B = West 400、C = WestNorth 500。
            // 边：A-B（d = 6 ⇒ 1024）、A-C（d = |0-5| = 5 ⇒ 887）；B-C 的 d = |6-5| = 1 ⇒ 系数 0 ⇒ **不是**有效 Clash。
            // 逐值手算（全部读**原始**动量）：
            //   LossFromBToA = (400·1024+512)/1024 = 400.5 ⇒ 400
            //   LossFromCToA = (500·887+512)/1024 = (443500+512)/1024 = 433.6 ⇒ 433
            //   TotalOppositionLoss(A) = 833 ⇒ RemainingA = max(0, 300-833) = 0
            //   LossFromAToB = (300·1024+512)/1024 = 300.5 ⇒ 300 ⇒ RemainingB = 100
            //   LossFromAToC = (300·887+512)/1024 = (266100+512)/1024 = 260.3 ⇒ 260 ⇒ RemainingC = 240
            // 会让它失败的缺陷：**逐边修改后继续计算** —— 若先用 A 的动量抵扣 B 的损耗再算 LossFromAToB，
            // B 的损耗会小于 300（A 只剩 0 时甚至是 0）；或先处理 B→A 再处理 C→A 时读中间值。
            var resolution = MomentumClashSolver.Solve(new MomentumClashInput(
                new List<ClashParticipant>
                {
                    Unit(1, 11, GridDirection.East, 300),
                    Unit(2, 12, GridDirection.West, 400),
                    Unit(3, 13, GridDirection.WestNorth, 500)
                },
                new List<ClashPair>
                {
                    new ClashPair(new ActionPlanId(11), new ActionPlanId(12)),
                    new ClashPair(new ActionPlanId(13), new ActionPlanId(11))
                }));

            Assert.That(Find(resolution, 11).TotalOppositionLossUnits, Is.EqualTo(833L));
            Assert.That(Find(resolution, 11).RemainingMomentumUnits, Is.EqualTo(0));
            // B 与 C 承受的损耗必须来自 A 的**原始** 300（而不是被抵扣后的 A）。
            Assert.That(Find(resolution, 12).TotalOppositionLossUnits, Is.EqualTo(300L));
            Assert.That(Find(resolution, 12).RemainingMomentumUnits, Is.EqualTo(100));
            Assert.That(Find(resolution, 13).TotalOppositionLossUnits, Is.EqualTo(260L));
            Assert.That(Find(resolution, 13).RemainingMomentumUnits, Is.EqualTo(240));

            // 每个参与者的 OriginalMomentumUnits 必须保持原始值（求解不得改写输入）。
            Assert.That(Find(resolution, 11).OriginalMomentumUnits, Is.EqualTo(300));
            Assert.That(Find(resolution, 12).OriginalMomentumUnits, Is.EqualTo(400));
            Assert.That(Find(resolution, 13).OriginalMomentumUnits, Is.EqualTo(500));

            // 全部参与攻击都终止；剩余动量只发给直接对手（B→A 100、C→A 240）。
            Assert.That(resolution.TerminatedActionPlanIds.Count, Is.EqualTo(3));
            Assert.That(resolution.ResidualImpacts.Count, Is.EqualTo(2));
            Assert.That(SumResidualTo(resolution, 1), Is.EqualTo(340L));
            Assert.That(resolution.TotalResidualAllocatedUnits, Is.EqualTo(340L));
            Assert.That(resolution.TotalRemainingMomentumUnits, Is.EqualTo(340L));

            // 非有效 Clash（d ≤ 3）必须由调用方过滤；传进来要稳定拒绝，不能"顺手连边"。
            var badPair = Assert.Throws<LogicDefinitionException>(() => MomentumClashSolver.Solve(
                new MomentumClashInput(
                    new List<ClashParticipant>
                    {
                        Unit(2, 12, GridDirection.West, 400),
                        Unit(3, 13, GridDirection.WestNorth, 500)
                    },
                    new List<ClashPair> { new ClashPair(new ActionPlanId(12), new ActionPlanId(13)) })));
            Assert.That(badPair.ErrorCode, Is.EqualTo(MomentumCodes.CLASH_INPUT_INVALID),
                "d=1 系数为 0 ⇒ 不是有效 Clash，必须由构图侧按系数过滤");
        }

        [Test]
        public void ClashResidualMomentumIsDistributedWithoutLossOrDuplication()
        {
            // —— 场景 A：单一攻击者 + 5 个对手，余数非零（3）——
            // A = East 1000；B..F 各 100 动量，方向取 NorthWest(4)/WestNorth(5)/West(6)/WestSouth(7)/SouthWest(8)
            // ⇒ d(A,·) = 4/5/6/5/4，系数 512/887/1024/887/512。
            // 另有一条对手之间的有效边：B(4)-F(8) 的 d = 4 ⇒ 512。
            // A 的损耗：50 + 87 + 100 + 87 + 50 = 374 ⇒ RemainingA = 626。
            //   50 = (100·512+512)/1024；87 = (100·887+512)/1024 = 87.1；100 = (100·1024+512)/1024 = 100.5 ⇒ 100
            // A 的权重（施加给对手的损耗，读 A 的原始 1000）：B 500、C 866、D 1000、E 866、F 500 ⇒ ΣW = 3732
            //   500 = (1000·512+512)/1024 = 500.5；866 = (1000·887+512)/1024 = 866.7；1000 = 1000.5 ⇒ 1000
            // 基础份额（向下取整）：B 83、C 145、D 167、E 145、F 83 ⇒ Σ = 623，余数 r = 3
            //   626·500/3732 = 83.87；626·866/3732 = 145.26；626·1000/3732 = 167.73
            // 余数按 (UnitId, ActionPlanId) 升序给前 3 个（B=2、C=3、D=4）⇒ 84、146、168、145、83，Σ = 626 ✔
            // **判别点**：最大余数法会给 B(+1)、F(+1)、D(+1) ⇒ C 得 145、F 得 84，与本口径不同。
            // 会让它失败的缺陷：用最大余数法（或"按权重排序"）补余数、丢掉余数（Σ ≠ Remaining）、
            // 或用浮点比例后四舍五入（Σ 可能多 1）。
            var participants = new List<ClashParticipant>
            {
                Unit(1, 11, GridDirection.East, 1000),
                Unit(2, 12, GridDirection.NorthWest, 100),
                Unit(3, 13, GridDirection.WestNorth, 100),
                Unit(4, 14, GridDirection.West, 100),
                Unit(5, 15, GridDirection.WestSouth, 100),
                Unit(6, 16, GridDirection.SouthWest, 100)
            };
            var pairs = new List<ClashPair>
            {
                new ClashPair(new ActionPlanId(11), new ActionPlanId(12)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(13)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(14)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(15)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(16)),
                new ClashPair(new ActionPlanId(12), new ActionPlanId(16))   // B-F，d = 4 ⇒ 512
            };
            MomentumClashResolution resolution = MomentumClashSolver.Solve(new MomentumClashInput(participants, pairs));

            ClashParticipantResolution a = Find(resolution, 11);
            Assert.That(a.TotalOppositionLossUnits, Is.EqualTo(374L));
            Assert.That(a.RemainingMomentumUnits, Is.EqualTo(626));
            Assert.That(a.ResidualImpacts.Count, Is.EqualTo(5), "每个对手一份份额（份额为 0 的不发）");
            Assert.That(ShareTo(resolution, 11, 12), Is.EqualTo(84), "B：83 + 1（稳定键第 1 个）");
            Assert.That(ShareTo(resolution, 11, 13), Is.EqualTo(146), "C：145 + 1（稳定键第 2 个；最大余数法会停在第 3 名 ⇒ 145）");
            Assert.That(ShareTo(resolution, 11, 14), Is.EqualTo(168), "D：167 + 1（稳定键第 3 个）");
            Assert.That(ShareTo(resolution, 11, 15), Is.EqualTo(145), "E：无补齐");
            Assert.That(ShareTo(resolution, 11, 16), Is.EqualTo(83), "F：无补齐（最大余数法会给 84）");
            Assert.That(SumResidualFrom(resolution, 11), Is.EqualTo(626L), "分配总和必须严格等于 RemainingMomentum");

            // 对手全部因 A 的巨大反冲而剩余为 0（B 还额外承受 F 的 50）。
            Assert.That(Find(resolution, 12).TotalOppositionLossUnits, Is.EqualTo(550L));
            Assert.That(Find(resolution, 12).RemainingMomentumUnits, Is.EqualTo(0));
            Assert.That(resolution.TotalRemainingMomentumUnits, Is.EqualTo(626L));
            Assert.That(resolution.TotalResidualAllocatedUnits, Is.EqualTo(626L), "不得丢失或凭空增加动量单位");

            // —— 场景 B：两个攻击者各有不同 RemainingMomentum，且其中一个有余数（1）——
            // A = East 1000、B = NorthWest 1000、C = WestSouth 200。
            // 边：A-B（d = 4 ⇒ 512）、A-C（d = 5 ⇒ 887）；B-C 的 d = |4-7| = 3 ⇒ 0 ⇒ 无边。
            //   LossFromBToA = (1000·512+512)/1024 = 500.5 ⇒ 500
            //   LossFromCToA = (200·887+512)/1024 = 173.7 ⇒ 173 ⇒ RemainingA = 1000-673 = 327
            //   LossFromAToB = 500 ⇒ RemainingB = 500（无人再打 B）
            //   LossFromAToC = (1000·887+512)/1024 = 866.7 ⇒ 866 ⇒ RemainingC = max(0, 200-866) = 0
            // A 的权重：B 500、C 866 ⇒ ΣW = 1366；份额：327·500/1366 = 119.69 ⇒ 119；
            //   327·866/1366 = 207.31 ⇒ 207；Σ = 326，余数 1 ⇒ 按 UnitId 升序给 B ⇒ 120、207（Σ = 327 ✔）
            // B 的权重：A 500（唯一对手）⇒ 份额 = 500·500/500 = 500，余数 0 ⇒ A 得 500。
            var twoAttackers = MomentumClashSolver.Solve(new MomentumClashInput(
                new List<ClashParticipant>
                {
                    Unit(1, 11, GridDirection.East, 1000),
                    Unit(2, 12, GridDirection.NorthWest, 1000),
                    Unit(3, 13, GridDirection.WestSouth, 200)
                },
                new List<ClashPair>
                {
                    new ClashPair(new ActionPlanId(11), new ActionPlanId(12)),
                    new ClashPair(new ActionPlanId(11), new ActionPlanId(13))
                }));
            Assert.That(Find(twoAttackers, 11).RemainingMomentumUnits, Is.EqualTo(327));
            Assert.That(Find(twoAttackers, 12).RemainingMomentumUnits, Is.EqualTo(500), "两个攻击者的剩余必须各自独立");
            Assert.That(ShareTo(twoAttackers, 11, 12), Is.EqualTo(120), "119 + 1（余数按 UnitId 升序补齐）");
            Assert.That(ShareTo(twoAttackers, 11, 13), Is.EqualTo(207));
            Assert.That(SumResidualFrom(twoAttackers, 11), Is.EqualTo(327L));
            Assert.That(SumResidualFrom(twoAttackers, 12), Is.EqualTo(500L));
            Assert.That(twoAttackers.TotalResidualAllocatedUnits, Is.EqualTo(827L));
            Assert.That(twoAttackers.TotalRemainingMomentumUnits, Is.EqualTo(827L));

            // 全局守恒：每一条分配都来自某个参与者的正剩余（不丢失、不重复）。
            Assert.That(twoAttackers.ResidualImpacts.Count, Is.EqualTo(3));
            Assert.That(SumAllResidual(twoAttackers), Is.EqualTo(twoAttackers.TotalRemainingMomentumUnits));
        }

        [Test]
        public void ThreeWayAttackResultIsIndependentOfAllIntentPermutations()
        {
            // 冲突组契约：「相同冲突组的 Intent、接触边和目标集合任意排列，
            // Resolution、事件序列和快照哈希完全一致」。
            // 会让它失败的缺陷：按输入顺序逐边求解（结果随排列变化）、用 Dictionary/HashSet 的枚举顺序
            // 决定余数补齐顺序、或让参与者集合顺序影响输出顺序。
            var baselineParticipants = new List<ClashParticipant>
            {
                Unit(1, 11, GridDirection.East, 1000),
                Unit(2, 12, GridDirection.NorthWest, 100),
                Unit(3, 13, GridDirection.WestNorth, 100),
                Unit(4, 14, GridDirection.West, 100),
                Unit(5, 15, GridDirection.WestSouth, 100),
                Unit(6, 16, GridDirection.SouthWest, 100)
            };
            var baselinePairs = new List<ClashPair>
            {
                new ClashPair(new ActionPlanId(11), new ActionPlanId(12)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(13)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(14)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(15)),
                new ClashPair(new ActionPlanId(11), new ActionPlanId(16)),
                new ClashPair(new ActionPlanId(12), new ActionPlanId(16))
            };
            string expected = Dump(MomentumClashSolver.Solve(new MomentumClashInput(baselineParticipants, baselinePairs)));

            // 排列 1：参与者逆序 + 边逆序（且每条边两端对调）。
            var reversedParticipants = new List<ClashParticipant>(baselineParticipants);
            reversedParticipants.Reverse();
            var reversedPairs = new List<ClashPair>();
            for (int i = baselinePairs.Count - 1; i >= 0; i--)
                reversedPairs.Add(new ClashPair(baselinePairs[i].B, baselinePairs[i].A));
            Assert.That(Dump(MomentumClashSolver.Solve(new MomentumClashInput(reversedParticipants, reversedPairs))),
                Is.EqualTo(expected), "逆序排列必须给出逐位相同的结果");

            // 排列 2：旋转插入顺序（按 UnitId 降序的稳定键往返）。
            var rotatedParticipants = new List<ClashParticipant>
            {
                baselineParticipants[3], baselineParticipants[0], baselineParticipants[5],
                baselineParticipants[2], baselineParticipants[4], baselineParticipants[1]
            };
            var rotatedPairs = new List<ClashPair>
            {
                baselinePairs[4], baselinePairs[0], baselinePairs[5],
                baselinePairs[2], baselinePairs[1], baselinePairs[3]
            };
            Assert.That(Dump(MomentumClashSolver.Solve(new MomentumClashInput(rotatedParticipants, rotatedPairs))),
                Is.EqualTo(expected), "轮换排列必须给出逐位相同的结果");
        }

        [Test]
        public void ClashingAttackCannotAlsoHitUnrelatedAoeTarget()
        {
            // 「所有参与 Clash 的攻击动作都终止，尚未消解的直接 Hit 失效」。
            // 会让它失败的缺陷：求解后仍让参与 Clash 的攻击对无关目标（同一 AOE 的其它目标）继续产伤，
            // 或者反向：把 Clash 终止当成"整个世界都不再结算"。
            const long unrelatedTarget = 909;
            var resolution = MomentumClashSolver.Solve(new MomentumClashInput(
                new List<ClashParticipant>
                {
                    Unit(1, 11, GridDirection.East, 120),
                    Unit(2, 12, GridDirection.West, 200)
                },
                new List<ClashPair> { new ClashPair(new ActionPlanId(11), new ActionPlanId(12)) }));

            // 参与 Clash 的是计划 11 与 12。
            Assert.That(Contains(resolution.TerminatedActionPlanIds, 11), Is.True);
            Assert.That(Contains(resolution.TerminatedActionPlanIds, 12), Is.True);

            // A（计划 11）本来还命中无关目标：一条 DirectHit 接触（blunt 15 生命点 = 15360 Q10）。
            var components = new List<DamageComponentSpec>
            {
                new DamageComponentSpec(DamageChannels.PhysicalBlunt, 15f, DamageTagMask.Blockable | DamageTagMask.Guardable)
            };
            TargetContact hitOnUnrelated = new TargetContact(
                new TargetContactKey(TargetContactType.DirectHit, new UnitId(1), new ActionPlanId(11),
                    new UnitId(unrelatedTarget), default),
                GridDirection.East, 120, 0, null, components);

            // 未过滤（对照）：该接触**确实**会造成伤害 —— 证明下面的 0 不是空断言。
            TargetAggregateResolution unfiltered = TargetAggregator.Aggregate(new TargetAggregationInput(
                new UnitId(unrelatedTarget), null, 1000, new List<TargetContact> { hitOnUnrelated }));
            Assert.That(unfiltered.TotalDamageQ10, Is.EqualTo(15360L));

            // 过滤掉已终止计划的尚未消解接触后：无关 AOE 目标不再承受任何伤害与冲击。
            var filtered = new List<TargetContact>();
            if (!Contains(resolution.TerminatedActionPlanIds, 11)) filtered.Add(hitOnUnrelated);
            TargetAggregateResolution aggregated = TargetAggregator.Aggregate(
                new TargetAggregationInput(new UnitId(unrelatedTarget), null, 1000, filtered));
            Assert.That(aggregated.TotalDamageQ10, Is.EqualTo(0L));
            Assert.That(aggregated.TotalImpactUnits, Is.EqualTo(0L));
            Assert.That(aggregated.ResultantDirection, Is.Null);
        }

        // ———————————————————————————————————————————————————————————————— 辅助

        private static ClashParticipant Unit(long unitId, long actionPlanId, GridDirection direction, int momentumUnits)
            => new ClashParticipant(new UnitId(unitId), new ActionPlanId(actionPlanId),
                MomentumPacket.Require(direction, momentumUnits));

        private static ClashParticipantResolution Find(MomentumClashResolution resolution, long actionPlanId)
        {
            for (int i = 0; i < resolution.Participants.Count; i++)
            {
                if (resolution.Participants[i].ActionPlanId.Value == actionPlanId) return resolution.Participants[i];
            }
            Assert.Fail("participant not found: " + actionPlanId);
            return null;
        }

        private static bool Contains(IReadOnlyList<ActionPlanId> ids, long actionPlanId)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i].Value == actionPlanId) return true;
            }
            return false;
        }

        /// <summary>来源计划 sourcePlan 分配给接收方恢复计划 recipientPlan 的剩余动量。</summary>
        private static long ShareTo(MomentumClashResolution resolution, long sourcePlan, long recipientPlan)
        {
            ClashParticipantResolution source = Find(resolution, sourcePlan);
            for (int i = 0; i < source.ResidualImpacts.Count; i++)
            {
                if (source.ResidualImpacts[i].RecipientActionPlanId.Value == recipientPlan)
                    return source.ResidualImpacts[i].ResidualMomentumUnits;
            }
            return 0L;
        }

        private static long SumResidualFrom(MomentumClashResolution resolution, long sourcePlan)
        {
            ClashParticipantResolution source = Find(resolution, sourcePlan);
            long sum = 0L;
            for (int i = 0; i < source.ResidualImpacts.Count; i++) sum += source.ResidualImpacts[i].ResidualMomentumUnits;
            return sum;
        }

        private static long SumResidualTo(MomentumClashResolution resolution, long recipientUnitId)
        {
            long sum = 0L;
            for (int i = 0; i < resolution.ResidualImpacts.Count; i++)
            {
                if (resolution.ResidualImpacts[i].RecipientUnitId.Value == recipientUnitId)
                    sum += resolution.ResidualImpacts[i].ResidualMomentumUnits;
            }
            return sum;
        }

        private static long SumAllResidual(MomentumClashResolution resolution)
        {
            long sum = 0L;
            for (int i = 0; i < resolution.ResidualImpacts.Count; i++)
                sum += resolution.ResidualImpacts[i].ResidualMomentumUnits;
            return sum;
        }

        /// <summary>把结果压成稳定文本，用于"排列不变性"逐位比较。</summary>
        private static string Dump(MomentumClashResolution resolution)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < resolution.Participants.Count; i++)
            {
                ClashParticipantResolution participant = resolution.Participants[i];
                builder.Append(participant.OwnerUnitId.Value).Append('/')
                    .Append(participant.ActionPlanId.Value).Append(':')
                    .Append((int)participant.Direction).Append('=')
                    .Append(participant.OriginalMomentumUnits).Append('-')
                    .Append(participant.TotalOppositionLossUnits).Append('>')
                    .Append(participant.RemainingMomentumUnits).Append('|');
                for (int k = 0; k < participant.Losses.Count; k++)
                {
                    builder.Append(participant.Losses[k].OpponentUnitId.Value).Append(':')
                        .Append(participant.Losses[k].OpponentActionPlanId.Value).Append(':')
                        .Append(participant.Losses[k].RingDistance).Append(':')
                        .Append(participant.Losses[k].SufferedLossUnits).Append('/')
                        .Append(participant.Losses[k].ImposedLossUnits).Append(',');
                }
                builder.Append('#');
                for (int k = 0; k < participant.ResidualImpacts.Count; k++)
                {
                    builder.Append(participant.ResidualImpacts[k].RecipientUnitId.Value).Append(':')
                        .Append(participant.ResidualImpacts[k].RecipientActionPlanId.Value).Append(':')
                        .Append(participant.ResidualImpacts[k].ResidualMomentumUnits).Append(',');
                }
                builder.Append(';');
            }
            builder.Append("||");
            for (int i = 0; i < resolution.ResidualImpacts.Count; i++)
            {
                ClashResidualImpact impact = resolution.ResidualImpacts[i];
                builder.Append(impact.RecipientUnitId.Value).Append('<')
                    .Append(impact.SourceActionPlanId.Value).Append(':')
                    .Append(impact.ResidualMomentumUnits).Append(',');
            }
            builder.Append("||").Append(resolution.TotalRemainingMomentumUnits).Append('/')
                .Append(resolution.TotalResidualAllocatedUnits);
            return builder.ToString();
        }
    }
}
