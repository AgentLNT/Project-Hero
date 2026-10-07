using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Resources;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// <strong>开局肾上腺素的权威来源契约</strong>（任务 08 裁定 8 的可执行判据，
    /// 已按调度者复裁修订）。
    ///
    /// <para>
    /// <strong>裁定 8 的原始前提被推翻，本文件随之改写</strong>。原裁定要求
    /// "把 <c>BattleInitializer</c> 的开局肾上腺素改为从已验证的定义链读取"。逐字取证结论：
    /// </para>
    /// <list type="bullet">
    /// <item><c>UnitDefinition</c>（<c>UnitDefinition.cs:31-40</c>）与
    /// <c>EncounterUnitSlot</c>（<c>EncounterUnitSlot.cs:13-18</c>）里
    /// <strong>都没有</strong>肾上腺素字段；<c>AdrenalineRules</c> 也没有初始值；
    /// 全仓 grep <c>InitialAdrenaline|InitialAvailableAdrenaline</c> 在 <c>Assets/Scripts/**</c> 零命中。</item>
    /// <item>主方案 3.1.1（<c>优化方案-逻辑表现解耦与逻辑帧判定.md:1240</c>）的单位资源字面就是
    /// <c>public int AvailableAdrenaline = 0;</c> —— <strong>默认 0</strong>，且属于运行时单位状态。</item>
    /// <item><c>00 号规则</c> / <c>02-程序集边界与纯数据.md:46</c> 第 9 条要求的措辞是
    /// "在 Logic <strong>单位定义或初始快照</strong>中提供 <c>AvailableAdrenaline</c> 与
    /// <c>AdrenalineCycleId</c>"—— <c>UnitInitialSnapshot</c> 就是那个"初始快照"，
    /// 它<strong>已经存在</strong>且已经流到唯一账本。</item>
    /// <item>旧实现同样从 0 开局（<c>CombatUnit.cs:92</c> <c>CurrentAdrenaline = 0f</c>，
    /// 只在造成/承受伤害后增长）。</item>
    /// </list>
    ///
    /// <para>
    /// ⇒ <c>BattleInitializer.cs:102-103</c> 的 <c>AvailableAdrenaline: 0</c>
    /// <strong>符合规格</strong>；真正写错的是 <c>BattleRuntimeInputs</c> 的原注释
    /// （曾声称开局肾上腺素来自 <c>UnitDefinition</c>/<c>EncounterUnitSlot</c>）。
    /// 调度者复裁：<strong>维持开局 0</strong>，改注释、把断言无据的用例改写成
    /// "开局恒 0"契约 + "满足费用后真实预留成立"。
    /// </para>
    ///
    /// <para>
    /// <strong>本文件钉住的两件事</strong>：
    /// <list type="number">
    /// <item>开局权威值恒为 0（<c>UnitInitialSnapshot</c> → 唯一账本 → 快照镜像三者一致），
    /// 且只由定义链决定、与 <c>BattleRuntimeInputs</c> 无关；</item>
    /// <item>额度达到权威费用后，<strong>真实预留守卫</strong>必须接受并原子转移额度；
    /// 额度不足时必须精确拒绝且零副作用（防"把断言放宽成允许拒绝"）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>为什么必须用真实装配</strong>：<c>BattleInitializer</c> 的开局值经
    /// <c>BattleSimulation._adrenaline.Register</c> 直达唯一账本 <c>AdrenalineLedgerRegistry</c>，
    /// 再经 <c>ReactionPlanner.TryReserveForReaction</c> 的 <c>TryReserveForReaction</c>
    /// 决定"这条 Block 反应能不能被接受"。喂值对象（自己 <c>new AdrenalineLedger</c>）
    /// 会绕过整条装配，对上述任何缺陷都不敏感。
    /// </para>
    /// </summary>
    public class Task08OpeningAdrenalineContractTests
    {
        private const string BlockSpecId = Task08AoeTargetPolicyIntegrationTests.BlockSpecId;

        /// <summary>Block 的权威费用只来自定义：<c>ActionSpec.AdrenalineCost = 2</c>。</summary>
        private static int AuthoritativeBlockCost =>
            Task08AoeTargetPolicyIntegrationTests.BuildDefinition(Task08AoeTargetPolicyIntegrationTests.Slots())
                .FindAction(Task08AoeTargetPolicyIntegrationTests.Spec(BlockSpecId)).AdrenalineCost;

        /// <summary>
        /// 真实 <see cref="BattleSimulation.Create"/> 装配（定义 → Encounter → 运行时输入），
        /// <strong>不</strong>喂任何值对象、<strong>不</strong>调用任何初始化写入入口。
        /// </summary>
        private static BattleSimulation NewRealSim(
            BattleRuntimeInputs inputs = null, int monsterResistanceQ10 = 0, int monster2ResistanceQ10 = 0)
            => BattleSimulation.Create(
                Task08AoeTargetPolicyIntegrationTests.BuildDefinition(
                    Task08AoeTargetPolicyIntegrationTests.Slots(monsterResistanceQ10, monster2ResistanceQ10)),
                Task08AoeTargetPolicyIntegrationTests.EncounterId,
                inputs ?? Task08AoeTargetPolicyIntegrationTests.Inputs,
                BattleSimulationAssembly.Standard());

        /// <summary>开局快照里的 Available（定义链的产物，只读）。</summary>
        private static int OpeningAvailableOf(BattleSimulation sim, UnitId unitId)
            => sim.CurrentSnapshot.Units.First(u => u.UnitId == unitId.Value).AvailableAdrenaline;

        private static long OpeningCycleOf(BattleSimulation sim, UnitId unitId)
            => sim.CurrentSnapshot.Units.First(u => u.UnitId == unitId.Value).AdrenalineCycleId;

        /// <summary>
        /// 把防御者的开局额度安排到 <paramref name="target"/>（<strong>测试前置</strong>，不是被测行为）。
        ///
        /// <para>
        /// 它走的是<strong>公开</strong>生产入口（<c>AdrenalineLedgerOf</c> → 账本自身的规范入账方法），
        /// 不新开写入通道、不改任何生产契约。之所以必须"安排"而不是直接断言：开局额度按规格恒为 0，
        /// 而"真实 Block"要求额度 ≥ 权威费用（2）—— 在真实战斗里这份额度只能靠造成/承受伤害获得。
        /// 本夹具把"额度从哪来"与"额度到了之后事务是否成立"<strong>解耦</strong>，
        /// 因此被测行为（接受/预留/求解/抵抗）一个都没有被替身替换。
        /// </para>
        /// </summary>
        private static AdrenalineLedger ArrangeOpeningAdrenaline(BattleSimulation sim, UnitId unitId, int target)
        {
            AdrenalineLedger ledger = sim.AdrenalineLedgerOf(unitId);
            Assert.That(ledger, Is.Not.Null, "夹具前提：单位必须有唯一账本");
            Assert.That(ledger.AvailableAdrenaline, Is.Zero,
                "夹具前提：开局额度按规格恒为 0（本方法的起点）");

            // 承受伤害是真实存在的获得途径（AdrenalineRules.DamageReceivedGainQ10）；
            // 这里只把"确实承受了伤害"这一事实喂给账本的<strong>同一个</strong>聚合入账方法，
            // 由账本自己按规则量化增益（测试不自己算 gain）。
            ledger.ApplyAccrualFacts(new AdrenalineAccrualFacts(
                unitId,
                TotalFinalDamageDealtQ10: 0,
                TotalFinalDamageReceivedQ10: 10240,
                SuccessfulBlockCount: 0,
                SuccessfulDodgeCount: 0,
                ClashSuccessCount: 0));

            Assert.That(ledger.AvailableAdrenaline, Is.GreaterThanOrEqualTo(target),
                "夹具前提：安排后的额度必须够付目标费用；available=" + ledger.AvailableAdrenaline);
            return ledger;
        }

        /// <summary>
        /// <strong>开局额度契约</strong>：权威初始快照的开局 Available 与周期号恒为 0，
        /// 唯一账本与快照镜像三者一致（不存在第二套真值）。
        ///
        /// <para>
        /// <strong>会让它失败的实现缺陷</strong>：给 <c>UnitInitialSnapshot</c> 填入未受规格授权的
        /// 非零开局值；或让"分配了账本的单位"与"初始快照里的单位"分叉；
        /// 或把周期号与 Available 一起当成"装配方可填数"。
        /// </para>
        /// </summary>
        [Test]
        public void OpeningAvailableAndCycleAreZeroFromTheAuthoritativeSnapshot()
        {
            BattleSimulation sim = NewRealSim();

            Assert.That(sim.CurrentSnapshot.Units.Count, Is.EqualTo(4),
                "夹具前提：本 Encounter 有 4 个槽位（hero/hero2/mon/mon2）");

            for (int i = 0; i < sim.CurrentSnapshot.Units.Count; i++)
            {
                UnitSnapshot unit = sim.CurrentSnapshot.Units[i];
                Assert.That(unit.AvailableAdrenaline, Is.Zero,
                    "unit=" + unit.UnitId + "：开局 Available 必须恒为 0"
                    + "（主方案 3.1.1 的 AvailableAdrenaline = 0；额度只能靠战斗获得）");
                Assert.That(unit.AdrenalineCycleId, Is.Zero,
                    "unit=" + unit.UnitId + "：开局个人周期号为 0（该单位自己的窗口尚未打开）");

                AdrenalineLedger ledger = sim.AdrenalineLedgerOf(new UnitId(unit.UnitId));
                Assert.That(ledger, Is.Not.Null,
                    "unit=" + unit.UnitId + "：每个初始单位都必须有唯一账本（装配不得漏注册）");
                Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(unit.AvailableAdrenaline),
                    "unit=" + unit.UnitId + "：账本 Available 必须等于快照镜像（同一份真值的两个投影）");
                Assert.That(ledger.CycleId, Is.EqualTo(unit.AdrenalineCycleId),
                    "unit=" + unit.UnitId + "：账本 CycleId 必须等于快照镜像");
                Assert.That(ledger.ReservedAdrenaline, Is.Zero,
                    "unit=" + unit.UnitId + "：开局不得存在任何预留");
            }
        }

        /// <summary>
        /// <strong>额度达到权威费用后，真实预留守卫必须接受</strong>并原子地从 Available 转入
        /// 带个人周期的计划预留。
        ///
        /// <para>
        /// 它取代了原裁定那条"开局就该够付 Block"的断言 —— 那条断言<b>无规格依据</b>；
        /// 保留下来的是它真正想验证的东西：<strong>下游事务是可用的</strong>。
        /// </para>
        ///
        /// <para>
        /// <strong>会让它失败的实现缺陷</strong>：肾上腺素端口未接线到唯一账本（配额永远为 0）；
        /// 预留没有真正扣除 Available；扣了却没登记预留（半提交）；预留丢了个人周期号。
        /// </para>
        /// </summary>
        [Test]
        public void RealReservationSucceedsOnceAvailableCoversTheAuthoritativeCost()
        {
            int blockCost = AuthoritativeBlockCost;
            Assert.That(blockCost, Is.EqualTo(2),
                "夹具前提：Block 的权威费用来自 ActionSpec.AdrenalineCost（冻结值 2）");

            BattleSimulation sim = NewRealSim();
            UnitId defender = Task08AoeTargetPolicyIntegrationTests.Monster;
            AdrenalineLedger ledger = ArrangeOpeningAdrenaline(sim, defender, blockCost);
            int before = ledger.AvailableAdrenaline;
            long cycle = ledger.CycleId;

            var candidate = new ActionPlanId(1L);
            string reserveError = sim.ReactionOpportunities.Planner.AdrenalinePort
                .TryReserveForReaction(defender, candidate, blockCost);

            Assert.That(reserveError, Is.Null,
                "额度足够时，真实预留守卫必须接受（拒绝码=" + reserveError + "）");
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(before - blockCost),
                "预留必须原子地从 Available 扣除");
            Assert.That(ledger.ReservedAdrenaline, Is.EqualTo(blockCost),
                "被扣除的额度必须原样进入该计划的预留");
            Assert.That(ledger.ReservationCount, Is.EqualTo(1));

            AdrenalineReservationEntry reservation = ledger.ReservationOf(candidate);
            Assert.That(reservation, Is.Not.Null, "预留必须可按计划键查回");
            Assert.That(reservation.ReservedAmount, Is.EqualTo(blockCost));
            Assert.That(reservation.ReservationCycleId, Is.EqualTo(cycle),
                "预留必须携带接受时的个人周期号");
        }

        /// <summary>
        /// <strong>负控制（同一条真实路径）</strong>：额度严格小于费用时，预留必须以
        /// <c>ADRENALINE_INSUFFICIENT</c> 精确拒绝，且<strong>零副作用</strong>。
        ///
        /// <para>
        /// 这条用例的存在意义是"本文件没有把断言放宽成允许拒绝"：它同时要求
        /// ①精确错误码、②Available 不变、③预留数不增。若实现变成"先扣再失败"或
        /// "以任意理由拒绝"，两次运行的对照就会分叉。
        /// </para>
        /// </summary>
        [Test]
        public void InsufficientAvailableIsRejectedWithStableCodeAndZeroSideEffects()
        {
            BattleSimulation sim = NewRealSim();
            UnitId defender = Task08AoeTargetPolicyIntegrationTests.Monster;
            AdrenalineLedger ledger = sim.AdrenalineLedgerOf(defender);

            int blockCost = AuthoritativeBlockCost;
            int before = ledger.AvailableAdrenaline;
            int reservationsBefore = ledger.ReservationCount;
            Assert.That(before, Is.Zero, "夹具前提：本用例刻意在开局 0 额度下取拒绝分支");

            string reserveError = sim.ReactionOpportunities.Planner.AdrenalinePort
                .TryReserveForReaction(defender, new ActionPlanId(1L), before + 1);

            Assert.That(reserveError, Is.EqualTo(AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE),
                "额度不足必须给出稳定码 " + AdrenalineLedgerCodes.INSUFFICIENT_ADRENALINE
                + "，不得放宽成任意拒绝");
            Assert.That(ledger.AvailableAdrenaline, Is.EqualTo(before), "失败必须零副作用：Available 不变");
            Assert.That(ledger.ReservationCount, Is.EqualTo(reservationsBefore),
                "失败必须零副作用：不得留下预留条目");
            Assert.That(blockCost, Is.GreaterThan(0), "夹具前提：Block 费用为正");
        }

        /// <summary>
        /// <strong>同定义、不同<see cref="BattleRuntimeInputs"/> ⇒ 开局额度与周期号完全相同</strong>。
        ///
        /// <para>
        /// 它把"<strong>不采用</strong>每场一个值"变成可失败的断言：若有人把开局额度接到
        /// "每场一个值"的运行时输入上，两种子装配的值会分叉 ⇒ 红。肾上腺素是<strong>每单位</strong>资源，
        /// 它的归属只能来自定义链（<c>UnitInitialSnapshot</c>）。
        /// </para>
        /// </summary>
        [Test]
        public void OpeningValuesComeFromTheDefinitionChainNotFromRuntimeInputs()
        {
            BattleSimulation a = NewRealSim(new BattleRuntimeInputs(InitialRngSeed: 23UL, InitialMetaResource: 0));
            BattleSimulation b = NewRealSim(new BattleRuntimeInputs(InitialRngSeed: 99UL, InitialMetaResource: 7));

            for (int i = 0; i < a.CurrentSnapshot.Units.Count; i++)
            {
                long unitId = a.CurrentSnapshot.Units[i].UnitId;
                Assert.That(b.CurrentSnapshot.Units[i].AvailableAdrenaline,
                    Is.EqualTo(a.CurrentSnapshot.Units[i].AvailableAdrenaline),
                    "unit=" + unitId + "：开局额度必须只由定义链决定，与运行时输入（种子/局外资源）无关");
                Assert.That(b.CurrentSnapshot.Units[i].AdrenalineCycleId,
                    Is.EqualTo(a.CurrentSnapshot.Units[i].AdrenalineCycleId),
                    "unit=" + unitId + "：开局周期号同理");
            }

            Assert.That(a.CurrentSnapshot.Units.Count,
                Is.EqualTo(4), "夹具前提：本 Encounter 有 4 个槽位（hero/hero2/mon/mon2）");
        }
    }

    /// <summary>
    /// <strong>区域威胁候选来源的装配契约</strong>（任务 08 收尾裁定：补齐
    /// <c>IAreaThreatCandidateSource</c> 的生产实现）。
    ///
    /// <para>
    /// <strong>它补的洞</strong>：任务 05 只定义接入面，默认实现
    /// <c>NoAreaThreatCandidateSource</c> 返回空候选；而 <c>CollectDefenders</c> 在非
    /// <c>PrimaryTargetOnly</c> 策略下<strong>只能</strong>向该来源取候选 ⇒ 区域攻击
    /// <strong>永远不公开任何反应机会</strong>，使 AOE 的 Block/Dodge/Guard 覆盖在结构上不可达。
    /// 任务 05 交接把区域威胁来源留给任务 06/08；任务 06 未交付，
    /// <c>06-交接记录.md:808-810</c> 明写「归属任务 08」。
    /// </para>
    ///
    /// <para>
    /// <strong>默认装配必须绑真实现，而不是静默 fail-closed</strong>：本文件用
    /// <c>BattleSimulationAssembly.Standard()</c>（<strong>不注入</strong>任何候选来源）验证
    /// 真实区域攻击会公开覆盖几何内的全部合法目标。
    /// </para>
    /// </summary>
    public class Task08AreaThreatCandidateWiringTests
    {
        /// <summary>
        /// <strong>默认装配下，区域攻击对区域内的每个合法目标各公开一条开放反应机会</strong>。
        ///
        /// <para>
        /// 夹具几何（<c>Task08AoeTargetPolicyIntegrationTests</c> 类型注释）：hero 锚点 <c>(0,0)</c> 朝东，
        /// AOE 区域 = <c>{(2j+3, 0, 1) | j = 0..12}</c>，含 mon <c>(9,0,1)</c>、mon2 <c>(17,0,1)</c>、
        /// hero2 <c>(25,0,1)</c>（同阵营，被 <c>Hostile</c> 掩码拒绝）。掩码过滤发生在
        /// <c>CollectDefenders</c>（拿到候选<strong>之后</strong>），因此候选来源必须给出
        /// <strong>几何</strong>覆盖集，而不是"猜敌方"。
        /// </para>
        ///
        /// <para>
        /// <strong>会让它失败的实现缺陷</strong>：装配仍把候选来源留成 <c>NoAreaThreatCandidateSource</c>
        /// （机会数 0）；几何口径与接触判定分叉（漏掉目标、或把区域外的单位也算进来）；
        /// 候选来源自己按阵营/Controller 过滤（盟友被错误计入 ⇒ 多出机会，或敌方被判敌失败而漏）。
        /// </para>
        /// </summary>
        [Test]
        public void RealAssemblyOpensReactionOpportunitiesForEveryUnitInTheArea()
        {
            UnitId attacker = Task08AoeTargetPolicyIntegrationTests.Hero;
            Task08AoeTargetPolicyIntegrationTests.Rig rig = null;

            rig = Task08AoeTargetPolicyIntegrationTests.Arrange(
                Task08AoeTargetPolicyIntegrationTests.AttackSpecId,
                attacker, Task08AoeTargetPolicyIntegrationTests.Monster,
                stopBeforeImpact: true,
                afterFirstStep: null);

            Assert.That(rig.Plan.ActionSpecId,
                Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.Spec(
                    Task08AoeTargetPolicyIntegrationTests.AttackSpecId)),
                "夹具前提：载荷是区域策略（AllTargetsInArea）的攻击");
            Assert.That(rig.Plan.State, Is.EqualTo(ActionPlanState.Running),
                "夹具前提：攻击已原子启动（机会在启动那一步公开）");

            IReadOnlyList<ReactionOpportunityRuntime> opportunities =
                rig.Sim.ReactionOpportunities.ActiveOpportunities;
            List<long> defenderIds = opportunities.Select(o => o.DefenderUnitId.Value).ToList();
            defenderIds.Sort();

            Assert.That(defenderIds, Is.EqualTo(new List<long>
                {
                    Task08AoeTargetPolicyIntegrationTests.Monster.Value,
                    Task08AoeTargetPolicyIntegrationTests.Monster2.Value
                }),
                "区域几何覆盖 mon(9,0,1) 与 mon2(17,0,1)：必须各公开一条机会；"
                + "hero2 同阵营被 Hostile 掩码拒绝，hero（施法者自己）也不是目标。"
                + "实际=" + string.Join(",", defenderIds.ConvertAll(v => v.ToString())));

            for (int i = 0; i < opportunities.Count; i++)
            {
                Assert.That(opportunities[i].TriggerTick,
                    Is.EqualTo(Task08AoeTargetPolicyIntegrationTests.ImpactTick),
                    "机会的 TriggerTick 必须等于来源攻击的 ImpactTick（命令无法声明）");
                Assert.That(opportunities[i].Options.Select(o => o.ReactionActionSpecId.Value),
                    Contains.Item(Task08AoeTargetPolicyIntegrationTests.BlockSpecId),
                    "Block 必须是已公开的选项（攻击载荷带 Blockable 标签）");
            }
        }
    }
}
