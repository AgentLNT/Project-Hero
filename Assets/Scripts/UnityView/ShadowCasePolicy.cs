using System;
using System.Collections.Generic;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// 一次 Shadow 比较的纯数据种子（Bootstrap 契约与适配器之间的载体）。
    ///
    /// 它只搬送任务 02B/03 公开的只读类型，因此契约程序集不需要引用
    /// <c>ProjectHero.Authoring</c>，也不需要引用 <c>Assembly-CSharp</c>。
    /// </summary>
    public sealed class ShadowCaseSeed
    {
        public ShadowCaseSeed(BattleSimulationSeed seed)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            Definition = seed.Definition;
            EncounterId = seed.EncounterId;
            RuntimeInputs = seed.RuntimeInputs;
            InputSummary = seed.InputSummary;
        }

        public BattleDefinition Definition { get; }

        public EncounterDefinitionId EncounterId { get; }

        public BattleRuntimeInputs RuntimeInputs { get; }

        public string InputSummary { get; }

        public string BattleDefinitionHash => Definition != null ? Definition.BattleDefinitionHashValue : string.Empty;

        public string RulesVersion => Definition != null ? Definition.RulesVersion : string.Empty;

        public string Validate()
        {
            if (Definition == null) return "SIMULATION_SOURCE_MISSING_DEFINITION";
            if (RuntimeInputs == null) return "SIMULATION_SOURCE_MISSING_RUNTIME_INPUTS";
            return null;
        }
    }

    /// <summary>测试与工具用的种子构造辅助。</summary>
    public static class ShadowCaseSeedFactory
    {
        /// <summary>用不同的 RNG 种子派生出"应当产生差异"的对照种子。</summary>
        public static ShadowCaseSeed DeriveWithRngOffset(ShadowCaseSeed seed, ulong offset, string summarySuffix)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            var inputs = new BattleRuntimeInputs(
                seed.RuntimeInputs.InitialRngSeed + offset, seed.RuntimeInputs.InitialMetaResource);
            return new ShadowCaseSeed(new BattleSimulationSeed(
                seed.Definition, seed.EncounterId, inputs, seed.InputSummary + summarySuffix));
        }
    }

    /// <summary>
    /// 逐用例比较策略（任务 03B「必须产出」7）。
    ///
    /// 策略是<strong>逐用例</strong>的：每个用例显式声明自己的暂不可比较字段与批准差异，
    /// 且创建时即被结构校验（缺责任任务/门槛、宽泛模式一律拒绝并写入
    /// <see cref="ShadowComparisonReport.Rejections"/>，从而让 <c>CanClaimEquivalence</c> 为 false）。
    /// </summary>
    public sealed class ShadowCasePolicy
    {
        private const string BroadRejectionPrefix = ShadowComparisonCodes.BroadDifferenceAllowlistRejected;

        private ShadowCasePolicy(
            string caseId, string rulesVersion, List<TemporarilyUncomparableField> temporarilyUncomparable,
            List<ShadowComparisonApproval> approvals, List<string> rejections,
            bool compareScheduleFacts = false, bool compareMovementFacts = false,
            bool compareTurnWindowFacts = false, bool compareTask08ProfileFacts = false)
        {
            CaseId = caseId;
            RulesVersion = rulesVersion;
            TemporarilyUncomparable = temporarilyUncomparable;
            Approvals = approvals;
            Rejections = rejections;
            CompareScheduleFacts = compareScheduleFacts;
            CompareMovementFacts = compareMovementFacts;
            CompareTurnWindowFacts = compareTurnWindowFacts;
            CompareTask08ProfileFacts = compareTask08ProfileFacts;
        }

        public string CaseId { get; }

        public string RulesVersion { get; }

        public IReadOnlyList<TemporarilyUncomparableField> TemporarilyUncomparable { get; }

        public IReadOnlyList<ShadowComparisonApproval> Approvals { get; }

        public IReadOnlyList<string> Rejections { get; }
        public IReadOnlyList<ShadowMigrationObligation> ResolvedMigrationObligations { get; private set; }
            = Array.Empty<ShadowMigrationObligation>();

        public static ShadowCasePolicy ResolveMigrationObligations(string caseId, string rulesVersion,
            string definitionHash, IReadOnlyList<ShadowMigrationObligation> evidence)
        {
            var baseline = CreateDefault(caseId, rulesVersion, true, true, true, true);
            var unresolved = new List<TemporarilyUncomparableField>(baseline.TemporarilyUncomparable);
            var resolved = new List<ShadowMigrationObligation>();
            var errors = new List<string>(baseline.Rejections);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proof in evidence ?? Array.Empty<ShadowMigrationObligation>())
            {
                int index = proof == null ? -1 : unresolved.FindIndex(f => f.Id == proof.Id);
                bool valid = proof != null && ids.Add(proof.Id) && index >= 0
                    && proof.CaseId == caseId && proof.RulesVersion == rulesVersion && proof.DefinitionHash == definitionHash
                    && !string.IsNullOrWhiteSpace(proof.Reason) && proof.ExecutedCases != null && proof.ExecutedCases.Count > 0
                    && (proof.Category == "NewRule" || proof.Category == "ReplacedRepresentation"
                        || proof.Category == "RemovedResource" || proof.Category == "DiagnosticOnly");
                if (valid) foreach (var test in proof.ExecutedCases)
                    if (string.IsNullOrWhiteSpace(test) || test.IndexOf('*') >= 0 || test.IndexOf('.') < 0) valid = false;
                if (!valid) { errors.Add("SHADOW_MIGRATION_EVIDENCE_INVALID|" + (proof?.Id ?? "<null>")); continue; }
                unresolved.RemoveAt(index); resolved.Add(proof);
            }
            var policy = new ShadowCasePolicy(caseId, rulesVersion, unresolved,
                new List<ShadowComparisonApproval>(), errors, true, true, true, true);
            policy.ResolvedMigrationObligations = resolved.AsReadOnly();
            return policy;
        }

        /// <summary>
        /// <strong>逐用例开启</strong>的任务 05 排程检查点（默认 <c>false</c>）。
        ///
        /// 为 <c>true</c> 时：报告额外比较 <c>plans[i].*</c> / <c>actorLanes[i].*</c> /
        /// <c>reactionOpportunities[i].*</c> / <c>scheduleRevision</c> 等逐条事实，
        /// 并额外登记本任务在生产（旧侧观测）通道上<strong>无法采样</strong>的字段。
        /// 既有用例使用默认值 ⇒ 它们的报告逐字节不变（不引入任何新差异类别）。
        /// </summary>
        public bool CompareScheduleFacts { get; }

        /// <summary>
        /// <strong>逐用例开启</strong>的任务 06 LogicGrid 占位 / 移动段 / Reservation 检查点
        /// （默认 <c>false</c>）。
        ///
        /// 为 <c>true</c> 时：报告额外比较 <c>occupancy[unitId].*</c> /
        /// <c>movementSegments[planId/stepIndex].*</c> / <c>reservations[planId].*</c> /
        /// <c>movementCommit[planId].*</c> / <c>movementTerminal[...]</c> 逐条事实，
        /// 并额外登记本任务在生产（旧侧观测）通道上<strong>无法采样</strong>的字段——
        /// 旧权威里不存在 MovementSegment/Reservation 这两类对象，占位也只存在于
        /// <c>GridManager.OccupancyMap</c> 与 Transform 位置，不可能经
        /// <c>BattleSimulationSourceFactory.Observe()</c> 真读。
        ///
        /// 既有用例使用默认值 ⇒ 它们的报告逐字节不变（不引入任何新差异类别）。
        /// </summary>
        public bool CompareMovementFacts { get; }

        /// <summary>
        /// <strong>逐用例开启</strong>的任务 07 TurnWindow / 整数预算 / 并发授权 / 肾上腺素周期
        /// 检查点（默认 <c>false</c>）。
        ///
        /// 为 <c>true</c> 时：报告额外比较以下逐条事实（Logic 世界对 Logic 世界通道）——
        /// <list type="bullet">
        /// <item><c>currentWindowId</c>/<c>nextWindowTick</c>/<c>nextWindowOrdinal</c>/
        /// <c>lastClosedWindowId</c>/<c>windows.count</c> 与逐窗口
        /// <c>windows[i].*</c>（拥有者、打开 Tick、整数预算四项、打开/接受提交位、关闭原因、
        /// 按计划归属的预留明细、<c>budgetIdentity</c> 与预留明细合计一致性）；</item>
        /// <item><c>resources.turnBudgetAvailable/Reserved/Spent</c> 与
        /// <c>resources.turnBudgetIdentity</c>、<c>resources.metaResource</c>；</item>
        /// <item><c>concurrentAction.hasActiveAuthorization/windowId/playerUnitId</c> 与
        /// "授权必须指向仍开放的当前窗口且不是拥有者本人"这一派生不变量；</item>
        /// <item><c>adrenaline[unitId].available/cycleId/reservedTotal/reservations</c> 与
        /// 单位只读镜像一致性（窗口打开清零、跨其他单位窗口保留的<b>证据字段</b>）；</item>
        /// <item><c>plans[i].budgetCostTicks</c>/<c>reservedTurnBudgetTicks</c>/
        /// <c>submittedWindowLedger</c>/<c>budgetLedgerLinked</c>——即"计划跨窗口切换时
        /// 身份、状态、排程与预算归属逐字不变"的检查点。</item>
        /// </list>
        ///
        /// 同时额外登记本任务在<strong>生产旧侧观测通道</strong>上无法采样的字段：
        /// 旧权威里根本没有"提交窗口""整数 Tick 预算账本""并发提交授权""肾上腺素周期账本"
        /// 这四类对象（旧侧只有逐帧时间累加器与浮点 <c>CurrentAdrenaline</c>）。
        ///
        /// 既有用例使用默认值 ⇒ 它们的报告逐字节不变（不引入任何新差异类别）。
        /// </summary>
        public bool CompareTurnWindowFacts { get; }

        /// <summary>
        /// <strong>逐用例开启</strong>的任务 08 多方仲裁与伤害画像检查点（默认 <c>false</c>）。
        ///
        /// 为 <c>true</c> 时：报告额外比较以下四类逐条事实（Logic 世界对 Logic 世界通道）——
        /// <list type="bullet">
        /// <item><c>intents[…]</c>：冻结 Intent 的<strong>完整载荷</strong>（序号、计划、拥有者、动作、
        /// 目标策略、主目标、关系掩码、标签、朝向、ImpactTick、交互优先级、动量偏移、提交窗口、
        /// 动量三元组、区域点集、伤害分量根数）——任务 05/06/07 只比较计划/Lane/机会与移动、
        /// 窗口账本，从未比较过 Intent 载荷本身；任务 08 的 AOE/夹击/聚合伤害差异<strong>只能</strong>
        /// 从 Intent 载荷看出来，因此这里逐字段比较；</item>
        /// <item><c>conflictGroups[…]</c>：本 Tick 冲突图的<strong>组划分</strong>
        /// （组键、组内节点 Intent 序号集合、组内接触键集合、目标单位集合、Intent↔Intent 边数）；</item>
        /// <item><c>contacts[…]</c>：本 Tick 冲突图的<strong>全部接触键</strong>
        /// （类型 + 双方单位/计划 + 目标单位）；</item>
        /// <item><c>aiControllers[…]</c>：任务 09 新增的 AI <strong>未来决策状态</strong>
        /// （<c>NextThinkTick</c>/<c>DecisionCount</c>/<c>LastDecisionTick</c>/<c>Rng</c>）——
        /// 这是"Command<strong>AndAi</strong>ShadowProfile"里 AI 那一半的直接对象。</item>
        /// </list>
        ///
        /// 同时额外登记本任务在<strong>生产旧侧观测通道</strong>上无法采样的字段：
        /// 旧权威里没有"冻结 Intent 队列""冲突图/冲突组""接触键"这三类对象，
        /// 也没有"AI 未来决策状态"（旧 AI 是 MonoBehaviour 上的即时行为，
        /// 没有决策计数/下一次思考 Tick/独立 RNG 状态可采样）。
        ///
        /// 既有用例使用默认值 ⇒ 它们的报告逐字节不变（不引入任何新差异类别）。
        /// </summary>
        public bool CompareTask08ProfileFacts { get; }

        /// <summary>
        /// 任务 03B 的冻结用例策略。
        ///
        /// 暂不可比较字段逐条给出对象路径/字段/原因/负责任务/最迟清零门槛。
        /// 批准差异清单为空——<strong>03B 不批准任何差异</strong>；宽泛批准在这里就该被拒绝。
        /// </summary>
        public static ShadowCasePolicy CreateDefault(
            string caseId, string rulesVersion,
            bool compareScheduleFacts = false, bool compareMovementFacts = false,
            bool compareTurnWindowFacts = false, bool compareTask08ProfileFacts = false)
        {
            var temporarilyUncomparable = new List<TemporarilyUncomparableField>();
            var approvals = new List<ShadowComparisonApproval>();
            var rejections = new List<string>();

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/Player#CombatUnit.CurrentAdrenaline",
                "availableAdrenaline",
                "旧实现用浮点 CurrentAdrenaline 逐帧衰减（CombatUnit.Update，5/s），新内核用整数 AvailableAdrenaline + 个人 CycleId；"
                + "量纲与生命周期都不同，在只读检查点无法稳定采样。"
                + "任务 07 已把新侧模型落定（每单位 AdrenalineLedger：Available 不衰减、跨其他单位窗口保留、"
                + "自己窗口打开时先递增 CycleId 再清零、预留按 ReservationCycleId 归属、Tick 末只接受规范聚合事实入账），"
                + "但旧侧浮点衰减仍在逐帧改变取值 ⇒ 本条**仍**不可做字段级相等比较（不是被忽略，也不是被批准）。"
                + "逐窗口/逐周期的真实比较在 Logic 对 Logic 通道上完成（Task07TurnWindowFacts，"
                + "逐用例由 ShadowCasePolicy.CompareTurnWindowFacts 开启）；"
                + "本条的迁移归属仍是任务 10（旧表现层最后一次读取旧字段之前）。",
                "07",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/Player#CombatUnit.CurrentStamina",
                "staminaQ10",
                "旧体力是 CombatUnit 浮点字段，新方案 1.2 已定移除；在新资源模型落地前没有对应字段可比较。"
                + "任务 07 的整数预算与肾上腺素账本**都不映射**旧体力/专注（旧体力不再被任何 Logic 路径读取），"
                + "因此本条不会因任务 07 而消失：它随旧字段一起在任务 10 的场景迁移中清零。",
                "07",
                "任务 10 切换主场景到 New 之前");

            // —— 第二收尾轮 R1.2 补登记：新内核尚未模拟、但旧侧确有对应可变状态的字段 ——
            // 这些字段在"真实可比较字段"集合（LegacyLogicObservation.ComparableFieldPaths）之外，
            // 因此每份报告都会为它们留下一条 TemporarilyUncomparable 观察；
            // 它们**不得**被填成默认值冒充"已比较"，也不得转成永久批准差异。
            //
            // —— 第三收尾轮 R2.3 的硬约束（适用于下表全部条目）——
            // 任一登记项被移入 ComparableFieldPaths 时，其**旧侧取值必须来自旧场景/旧运行时的
            // 真实活动状态**，经 BattleSimulationSourceFactory.Observe()（Assembly-CSharp 内，
            // 唯一能命名旧类型的宿主）读取；**禁止**用 Encounter 定义槽位等定义派生事实充当旧侧事实
            // ——那会得到"定义常量对定义常量"的伪比较。下列 9 条即【任务 04 必须切换为实时读取】：
            //   units[i].healthQ10 / units[i].position / units[i].facing /
            //   battleEnd.isEnded / battleEnd.resultCode / effects.count / intents.count /
            //   availableAdrenaline / staminaQ10
            // 另 2 条（plans.count / rng.state）同样禁止用定义侧推导填充，但其旧侧来源
            // 不是"活动单位状态"。逐条类型/属性/文件:行见 03B-交接记录.md §23.2。

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/Manager#BattleManager._battleEnded",
                "battleEnd.isEnded",
                "旧胜负由 BattleManager 每帧轮询 + timeScale 慢放表达，新胜负由唯一 Finalizer 在 Step 阶段 2/16 原子提交；"
                + "任务 04 已把旧侧结束标志真读（BattleManager.BattleEnded）并与 BattleEndSnapshot.IsEnded 做字段级比较，"
                + "本登记项保留的是「旧侧结束时机（逐帧轮询、含 timeScale 慢放）与新侧阶段边界不同」这一语义差异，"
                + "由任务 10 迁移旧胜负表现时收口。",
                "10",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/Player#CombatUnit.IsStaggered / IsKnockedDown / InWindup / InRecovery / IsMoving / CurrentHealth",
                "units[i].state",
                "旧侧状态由多个独立 bool 表达（无等价状态机），任务 04 已把它们按冻结规则推导为状态族标签并真读比较；"
                + "本登记项保留的是覆盖边界：旧侧没有 Guarding/Blocking/Dodging 的等价事实（属任务 05/08 的计划状态），"
                + "三者一旦出现在新侧就会与本标签不一致，届时必须把它们纳入比较并更新策略。",
                "05",
                "任务 10 切换主场景到 New 之前");


            // 任务 04 收口记录：三条计数口径（修订轮 R4 统一标注判据，数字因此可比且不矛盾）。
            // 03B-交接记录 §23.2 把 9 条登记项标注为「任务 04 必须切换为实时读取」。
            //
            // 判据 A「已移出登记表」= 3 条（登记表 11 → 10 条净变化中删除的正是这 3 条）：
            //   units[i].position / units[i].facing / units[i].healthQ10
            //   （测试侧由 AssertSwitchedFieldIsNotRegistered 钉死：RuntimeOwnershipTests.cs ⑤）
            //
            // 判据 B「本轮新切换为读旧场景活动事实并移入 ComparableFieldPaths」= 4 条
            // （= A 的 3 条 + battleEnd.isEnded；后者是新真读，但登记项按**覆盖边界**保留）：
            //   units[i].position  ← CombatUnit.GridPosition（活动组件属性）
            //   units[i].facing    ← CombatUnit.FacingDirection（活动组件字段）
            //   battleEnd.isEnded  ← BattleManager.BattleEnded（活动组件字段）
            //   units[i].healthQ10 ← CombatUnit.CurrentHealth（活动组件字段）
            //
            // 判据 C「已真读并进入字段级比较（含保留为覆盖边界者）」= 5 条
            // （= B 的 4 条 + battleEnd.resultCode ← BattleManager.EndMessage 派生的旧结束事实）：
            //   03B §23.2 的 9 条里，任务 04 真正接上旧侧实时事实的就是这 5 条；
            //   其余 4 条（effects.count / intents.count / availableAdrenaline / staminaQ10）仍未切换。
            //
            // A 的 3 条不再出现在登记表里——「登记为暂不可比较」与「已真的在比较」不能同时成立。
            // 迁移实现位于 BattleSimulationSourceFactory.Observe()（Assembly-CSharp 内，
            // 唯一能命名旧类型的宿主），真值读取用 FindObjectsInactive.Exclude 限定为活动实例。
            //
            // 上面两条（battleEnd.isEnded / units[i].state）是**覆盖边界**登记项，而不是"尚未切换"：
            // 它们的字段已真读并已比较，登记的是"旧侧结构表达力不足"这一语义差异，以及
            // "Guarding/Blocking/Dodging 无旧侧等价事实"这一覆盖边界。
            //
            // 其余登记项逐条给出"为什么现在仍不可字段级比较"：
            //   · battleEnd.resultCode ← 旧侧只有结束显示文本，没有结果码字段；
            //     派生值只能证明"旧结束事实被读过"，不足以作为"两侧语义等价"的比较；任务 10 收口。
            //   · effects.count / plans.count / intents.count ← 旧 BattleTimeline 排程表
            //     （动作 Intent 与状态改变 Intent 混合）与新内核 StatusEffect/ActionPlan/Intent
            //     不是同一结构，计数相等是巧合而非事实；分别由任务 04（本任务的持续效果内核，
            //     旧侧无对应实体）/05/08 收口。
            //   · staminaQ10 / availableAdrenaline ← 旧侧浮点逐帧变化，与新内核整数 Tick 边界不对齐；任务 07。
            //   · rng.state ← 旧运行时没有可采样的显式 RNG 状态；任务 09。
            //   · scheduledEventCount ← 任务 04 新增：旧排程计数已真读，但新侧对应实体尚不存在，
            //     因此它只留下"旧侧排程事实确实被真读"的观察，供负控制使用。

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/Manager#BattleManager.EndMessage",
                "battleEnd.resultCode",
                "覆盖边界登记项（与 battleEnd.isEnded / units[i].state 同类）：旧侧只有结束显示文本"
                + "（BattleManager.EndMessage），没有与 Finalizer 对齐的结果码字段；任务 04 已真读该文本、"
                + "按冻结规则派生结果码并把它纳入字段级比较（“无结果”的两种表示——旧侧空串、新侧 null——"
                + "已归一为同一缺席事实，见 ShadowComparisonDetector.NormalizeAbsentCode），"
                + "因此本条比较的鉴别力是「旧结束事实被真读且派生码与新侧一致」，"
                + "而不是「两侧结束语义等价」。任务 10 迁移旧 UI 胜负表现时一并收口。",
                "10",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/CombatDemo#BattleTimeline._events",
                "effects.count",
                "旧侧没有「效果」这一实体：它只有 BattleTimeline 的排程 Intent 列表，与新内核的 StatusEffect（任务 04）不是同一结构，计数不可比。",
                "04",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/CombatDemo#BattleTimeline._events",
                "plans.count",
                "旧侧计划由排程 Intent + CombatUnit 状态表达，新内核计划是 ActionPlan（任务 05）的显式实体；在任务 05 落地前计数不可对齐。",
                "05",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/CombatDemo#BattleTimeline._timeAccumulator",
                "rng.state",
                "旧运行时没有显式 RNG 状态可采样（随机性分散在旧表现与特效路径）；新内核 RNG 状态是确定性基础设施事实，但旧侧无对应可比较值。",
                "09",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/CombatDemo#BattleTimeline._events",
                "intents.count",
                "旧侧 Intent 只以 BattleTimeline 排程条目存在且无独立序号空间，新内核 Intent 由任务 08 的事件/意图系统产生；两者计数不可比。",
                "08",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/CombatDemo#BattleTimeline._events",
                "scheduledEventCount",
                "旧侧排程表条目数（任务 04 已真读 BattleTimeline.ScheduledEventCount）。"
                + "它与新内核 StatusEffect/ActionPlan/Intent 不是同一结构，因此不进入字段级相等比较；"
                + "本登记项的作用是留下「旧侧排程事实确实被真读」的观察，供负控制使用。",
                "05",
                "任务 10 切换主场景到 New 之前");

            // —— 任务 05：排程/计划/Lane/机会的生产通道（旧侧观测）登记项 ——
            //
            // 为什么必须登记而不是"转成批准差异"或"塞进可比较字段"：
            // 旧权威里根本不存在 ActionPlan / ActorLane / ReactionOpportunity 这三类对象
            // （旧侧只有 BattleTimeline 排程条目与 CombatUnit 的若干 bool），
            // 因此它们**不可能**经 BattleSimulationSourceFactory.Observe() 真读；
            // 按 03B 观测契约，这类字段必须逐条声明"暂不可比较 + 负责任务 + 清零门槛"，
            // 绝不允许用定义派生常量冒充旧侧事实。
            // 逐计划/逐 Lane/逐机会的**真正比较**在 Logic 对 Logic 通道上完成
            // （见 ShadowDifferenceDetector.Task05ScheduleFacts，逐用例由
            // ShadowCasePolicy.CompareScheduleFacts 开启）。
            if (compareScheduleFacts)
            {
                RegisterTask05ScheduleRegistrations(temporarilyUncomparable, rejections);
            }

            // —— 任务 06：LogicGrid 占位 / 移动段 / Reservation 的生产通道（旧侧观测）登记项 ——
            //
            // 为什么必须登记而不是"转成批准差异"或"塞进可比较字段"：
            // 旧权威里根本没有 MovementSegment / Reservation 这两类对象，占位也只存在于
            // GridManager.OccupancyMap 与 Transform 位置（视觉插值），**不可能**经
            // BattleSimulationSourceFactory.Observe() 真读——旧侧唯一与空间有关的事实是
            // CombatUnit.GridPosition（已在 units[i].position 上真读比较）。
            // 按 03B 观测契约，这类字段必须逐条声明"暂不可比较 + 负责任务 + 清零门槛"，
            // 绝不允许用定义派生常量或 Transform 位置冒充旧侧事实。
            // 真正的逐段/逐预留比较在 Logic 对 Logic 通道上完成
            // （见 ShadowDifferenceDetector.Task06MovementFacts，逐用例由本开关开启）。
            if (compareMovementFacts)
            {
                RegisterTask06MovementRegistrations(temporarilyUncomparable, rejections);
            }

            // —— 任务 07：TurnWindow / 整数预算 / 并发授权 / 肾上腺素周期的生产通道
            //    （旧侧观测）登记项 ——
            //
            // 为什么必须登记而不是"转成批准差异"或"塞进可比较字段"：
            // 旧权威里根本没有"提交窗口""整数 Tick 预算账本""并发提交授权""个人肾上腺素周期账本"
            // 这四类对象——旧玩法里玩家单位可以随时直接下达动作（TacticsController），
            // 时间是逐帧浮点累加器（BattleTimeline._timeAccumulator），
            // 肾上腺素是 CombatUnit 的浮点 CurrentAdrenaline 且逐帧衰减。
            // 它们**不可能**经 BattleSimulationSourceFactory.Observe() 真读；
            // 按 03B 观测契约，这类字段必须逐条声明"暂不可比较 + 负责任务 + 清零门槛"，
            // 绝不允许用定义派生常量冒充旧侧事实，也绝不允许整体忽略。
            // 真正的逐窗口/逐账本比较在 Logic 对 Logic 通道上完成
            // （见 ShadowDifferenceDetector.Task07TurnWindowFacts，逐用例由本开关开启）。
            if (compareTurnWindowFacts)
            {
                RegisterTask07TurnWindowRegistrations(temporarilyUncomparable, rejections);
            }

            // —— 任务 08：冻结 Intent 载荷 / 冲突组 / 接触键 / AI 决策状态的生产通道
            //    （旧侧观测）登记项 ——
            //
            // 为什么必须登记而不是"转成批准差异"或"塞进可比较字段"：
            // 旧权威里根本没有"冻结 Intent 队列""冲突图/冲突组""接触键"这三类对象
            // （旧侧只有 BattleTimeline 的排程条目与逐帧 Transform 位置），也没有"AI 未来决策状态"
            // （旧 AI 是 MonoBehaviour 上的即时行为：没有决策计数、下一次思考 Tick、
            // 也没有属于该 AI 自己的版本化 RNG 状态）——它们**不可能**经
            // BattleSimulationSourceFactory.Observe() 真读。
            // 按 03B 观测契约，这类字段必须逐条声明"暂不可比较 + 负责任务 + 清零门槛"，
            // 绝不允许用定义派生常量冒充旧侧事实，也绝不允许整体忽略。
            // 真正的逐 Intent/逐组/逐接触/逐 AI 决策者比较在 Logic 对 Logic 通道上完成
            // （见 ShadowDifferenceDetector.Task08AiAndArbitrationFacts，逐用例由本开关开启）。
            if (compareTask08ProfileFacts)
            {
                RegisterTask08ProfileRegistrations(temporarilyUncomparable, rejections);
            }

            return new ShadowCasePolicy(caseId ?? string.Empty, rulesVersion ?? string.Empty,
                temporarilyUncomparable, approvals, rejections, compareScheduleFacts, compareMovementFacts,
                compareTurnWindowFacts, compareTask08ProfileFacts);
        }

        /// <summary>
        /// 任务 06 的登记项：覆盖「占位 / 移动提交 / 路径 Reservation / 冲突回滚 / 移动终态」
        /// 五类事实在<strong>生产旧侧观测通道</strong>上的覆盖边界。
        /// 每条都带旧侧对象路径 + 字段 + 原因 + 负责任务 + 最迟清零门槛。
        /// </summary>
        private static void RegisterTask06MovementRegistrations(
            List<TemporarilyUncomparableField> fields, List<string> rejections)
        {
            const string owner = "06";
            const string gate = "任务 10 切换主场景到 New 之前";
            const string legacyOccupancy = "CombatSampleScene/Manager#GridManager.OccupancyMap";
            const string legacyMovement = "CombatSampleScene/Manager#Pathfinder";
            const string legacyUnitMovement = "CombatSampleScene/Player#UnitMovement";

            RegisterTemporarilyUncomparable(fields, rejections, legacyOccupancy, "occupancy[i].anchor",
                "旧侧没有 LogicGrid 占位权威：占位由 GridManager.OccupancyMap 与 Transform 位置共同表达，"
                + "单位锚点的权威值只存在于新逻辑层。旧侧可比较的空间事实是 CombatUnit.GridPosition"
                + "（已作为 units[i].position 真读比较），而不是逐单位逻辑格锚点。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyMovement, "movementSegments[i].from",
                "旧侧没有 MovementSegment 对象：移动由 UnitMovement 的逐帧插值表示，"
                + "没有 (ActionPlanId, StepIndex) 定位的离散段与 From/To 半开区间。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyMovement, "movementSegments[i].to",
                "旧侧没有段的目的格这一权威事实：落点由逐帧插值与 transform 位置表达，"
                + "无法在只读检查点上与离散段的 To 做字段级比较。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyMovement, "movementSegments[i].endTick",
                "旧侧移动的结束时刻由动画/插值进度决定，没有与新内核同口径的绝对 EndTick，"
                + "因此'命令前边界在 EndTick 原子提交'这一事实在旧侧不可采样。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyMovement, "reservations[i].cell",
                "旧侧没有空间 Reservation 这一对象：同时到达同一格的冲突由逐帧执行顺序与 Transform 距离隐式决定，"
                + "没有'一格一持有者、先成功提交者持有'的权威结构可采样。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyUnitMovement, "movementCommit[i].expectedFrom",
                "旧侧没有'未提交段'这一概念，因此没有'剩余路径起点必须等于当前锚点'的可采样事实。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyMovement, "movementTerminal[i].segments",
                "旧侧没有'计划终态后清理其移动段'这一结构：旧移动是逐帧插值，计划被打断即不再继续，"
                + "不存在可逐条核对的残留段集合。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyMovement, "movementTerminal[i].reservations",
                "旧侧没有 Reservation 表，因此'计划终态后其全部预留已释放'在旧侧没有对应事实可采样。",
                owner, gate);
        }

        /// <summary>
        /// 任务 05 的登记项：覆盖「排程编辑 / 跨窗口不变 / 门禁与延期 / 原子启动 /
        /// Lane 串行 / Impact 与 Recovery / 全部终态」七类事实在生产通道上的<strong>覆盖边界</strong>。
        /// 每条都带对象路径 + 字段 + 原因 + 负责任务 + 最迟清零门槛。
        /// </summary>
        private static void RegisterTask05ScheduleRegistrations(
            List<TemporarilyUncomparableField> fields, List<string> rejections)
        {
            const string owner = "05";
            const string gate = "任务 10 切换主场景到 New 之前";
            const string legacyPlans = "CombatSampleScene/CombatDemo#BattleTimeline._events";
            const string legacyUnits = "CombatSampleScene/Player#CombatUnit";

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].state",
                "旧侧没有 ActionPlan 对象，只有 BattleTimeline 排程条目与 CombatUnit 的若干独立 bool；"
                + "Editable/Locked/Running/Completed/Terminated 这套生命周期状态在旧侧没有等价事实可采样。"
                + "逐计划状态在 Logic 对 Logic 通道上真比较（Task05ScheduleFacts）。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].startTick",
                "旧侧排程条目的时间语义是「队列位置 + 逐帧推进」，没有与新内核同口径的 StartTick/绝对 Tick 投影，"
                + "因此无法在只读检查点上做字段级相等比较。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].lockedAtTick",
                "旧侧没有「锁定」这一原子边界（动作是逐帧开始执行的），"
                + "原子锁定/启动是任务 05 的新语义，旧侧不存在可比较事实。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].automaticDeferralCount",
                "旧侧被控制状态阻塞时不做「确定性向右延期」，而是直接跳过本帧；"
                + "系统自动延期次数是新语义，旧侧无对应计数。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].impactTick",
                "旧侧攻击没有显式 ImpactTick 字段（命中时刻由动画事件与逐帧逻辑共同决定），"
                + "Impact 与 Recovery 的绝对 Tick 边界在本任务才成为权威事实。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyUnits, "plans[i].submittedWindowId",
                "旧侧窗口归属由 BattleManager 的占用状态表达，不挂在动作上；"
                + "新内核把窗口归属记录为计划的审计字段且不参与执行过滤，两侧不可比。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].terminationReason",
                "旧侧动作没有终态原因（被打断就是不再继续执行），"
                + "13 项 ActionTerminationReason 是新内核的显式语义。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyUnits, "actorLanes[i].pendingPlanCount",
                "旧侧每单位的动作队列分散在 BattleTimeline 事件表与 CombatUnit 状态里，"
                + "不存在「每单位一条全局 Lane」这一权威结构。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyUnits, "actorLanes[i].locked",
                "旧侧没有「Lane 提交锁」（死亡后由控制流短路），"
                + "锁定只阻止新提交、不清除已有计划这一语义在旧侧没有对应事实。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "reactionOpportunities[i].state",
                "旧侧没有 ReactionOpportunity 对象：Block/Dodge 是直接播放的动作，"
                + "没有机会状态机、逐选项截止与关闭原因可采样。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "nextReactionOpportunityId",
                "旧侧没有机会 ID 空间；新内核的机会 ID 由权威排程器分配并进入规范化快照。", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "terminalPlanRecordCount",
                "旧侧没有「终态计划历史 + 增量摘要」这一结构（任务 03 归档协议是新的）。", owner, gate);
        }

        /// <summary>
        /// 任务 07 的登记项：覆盖「窗口打开/关闭、整数 Tick 预算、并发提交授权、肾上腺素周期账本、
        /// 跨窗口计划不变性」五类事实在<strong>生产旧侧观测通道</strong>上的覆盖边界。
        ///
        /// 每条都带旧侧对象路径 + 字段 + 原因 + 负责任务 + 最迟清零门槛；
        /// 逐条登记而不是整类忽略——"新侧存在、旧侧不存在"这一事实本身就是差异，
        /// 必须写进登记表，而不是塞进批准差异（本策略<strong>不批准任何差异</strong>）。
        /// </summary>
        private static void RegisterTask07TurnWindowRegistrations(
            List<TemporarilyUncomparableField> fields, List<string> rejections)
        {
            const string owner = "07";
            const string gate = "任务 10 切换主场景到 New 之前";
            // 旧侧的「战斗所有者」：它没有任何回合窗口/提交权限对象（旧玩法随时可下令）。
            const string legacyBattleOwner = "CombatSampleScene/Manager#BattleManager";
            // 旧侧唯一的时间事实：逐帧浮点累加器（不是「按动作预留的整数 Tick 预算」）。
            const string legacyTime = "CombatSampleScene/CombatDemo#BattleTimeline._timeAccumulator";
            // 旧侧唯一的玩家输入路径：直接执行动作，没有「授权」这一中间对象。
            const string legacyInput = "CombatSampleScene/Core/Gameplay#TacticsController";
            // 旧侧肾上腺素：浮点字段（与既有 availableAdrenaline 登记项同一对象路径）。
            const string legacyAdrenaline = "CombatSampleScene/Player#CombatUnit.CurrentAdrenaline";
            // 旧侧计划/排程：BattleTimeline 的事件表（无计划对象，也没有窗口归属字段）。
            const string legacyPlans = "CombatSampleScene/CombatDemo#BattleTimeline._events";

            const string whyNoWindow =
                "旧权威里不存在 TurnWindow 对象：提交权限与「预算归属」在旧玩法中不是显式状态"
                + "（玩家可随时经 TacticsController 下达动作，系统按逐帧时间线推进），"
                + "因此该字段在只读检查点上没有可采样的旧侧事实。"
                + "逐窗口事实在 Logic 对 Logic 通道上真比较（Task07TurnWindowFacts）。";
            const string whyNoIntegerBudget =
                "旧侧时间只有逐帧浮点累加器（BattleTimeline._timeAccumulator），"
                + "既没有「按动作预留」的整数 Tick 预算，也没有 Available/Reserved/Spent 三态账本与恒等式"
                + "（Reserved + Spent + Available == Total）；整数预算是任务 07 的新语义，旧侧无对应事实。";
            const string whyNoAuthority =
                "旧侧没有「并发提交授权」这一对象：非窗口拥有者提交普通动作在旧玩法里不存在该概念，"
                + "也没有由权威定义定价、按窗口撤销的一次性授权；旧侧无对应可采样事实。";
            const string whyNoCycle =
                "旧侧肾上腺素是 CombatUnit 的浮点 CurrentAdrenaline（逐帧衰减、没有个人周期号），"
                + "新侧是每单位 AdrenalineLedger（整数 Available + CycleId + 按 ReservationCycleId 归属的预留）；"
                + "周期号、预留明细与「自己窗口打开先递增周期再清零」这一时机在旧侧都没有对应事实。";

            // —— ① 窗口打开/关闭 ——
            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "currentWindowId",
                whyNoWindow + "（窗口 ID 计数器 nextWindowId 仍作为基础设施事实逐检查点比较；"
                + "这里登记的是「哪个窗口是当前窗口」这一权限状态）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows.count",
                whyNoWindow + "（含已关闭窗口的可审计账本，旧侧没有窗口对象可计数）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].openedAtTick",
                whyNoWindow + "（旧侧没有「窗口在哪个 Tick 打开」的权威边界）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].isOpen",
                whyNoWindow + "（旧侧没有「已请求关闭/已正式关闭」这两个分离的窗口位）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].isAcceptingSubmissions",
                whyNoWindow + "（旧侧没有「立即停止接受新增提交，但本 Tick 后续命令仍按稳定码拒绝」这一语义）",
                owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].closeReason",
                whyNoWindow + "（OwnerRequested/BudgetExhausted/OwnerDied/BattleEnded 四类关闭原因是新内核的显式语义）",
                owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].totalBudgetTicks",
                whyNoIntegerBudget, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].reservedBudgetTicks",
                whyNoIntegerBudget, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].spentBudgetTicks",
                whyNoIntegerBudget, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].availableBudgetTicks",
                whyNoIntegerBudget, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].reservations",
                whyNoIntegerBudget + "（按 ActionPlanId 归属的预留明细尤其不存在：旧侧动作没有「从哪个窗口预留了多少 Tick」这一事实）",
                owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyBattleOwner, "windows[i].budgetIdentity",
                whyNoIntegerBudget + "（恒等式 Reserved + Spent + Available == Total 与预留明细合计一致性都是新侧不变量）",
                owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyTime, "nextWindowTick",
                whyNoWindow + "（旧侧没有「下一个窗口最早在哪一 Tick 打开」的排程事实）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyTime, "lastClosedWindowId",
                whyNoWindow + "（旧侧没有「最近关闭的窗口」这一审计事实）", owner, gate);

            // —— ② 整数预算的全局聚合（战斗资源快照）——
            RegisterTemporarilyUncomparable(fields, rejections, legacyTime, "resources.turnBudgetAvailable",
                whyNoIntegerBudget, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyTime, "resources.turnBudgetReserved",
                whyNoIntegerBudget, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyTime, "resources.turnBudgetSpent",
                whyNoIntegerBudget, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyTime, "resources.turnBudgetIdentity",
                whyNoIntegerBudget + "（全部窗口聚合的恒等式同样是新侧不变量）", owner, gate);

            // —— ③ 并发提交授权 ——
            RegisterTemporarilyUncomparable(fields, rejections, legacyInput, "concurrentAction.hasActiveAuthorization",
                whyNoAuthority, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyInput, "concurrentAction.windowId",
                whyNoAuthority + "（授权绑定到哪一个窗口同样是新侧事实）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyInput, "concurrentAction.playerUnitId",
                whyNoAuthority + "（「激活者」必须等于装配显式注入的主角单位，旧侧没有该身份）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyInput, "resources.metaResource",
                "旧侧局外资源没有被「按窗口一次、由权威定义定价」的消费通道："
                + "新侧并发激活的费用只来自 ConcurrentActionDefinition，并原子扣减唯一计数；"
                + "旧侧没有对应的事实可采样（旧输入路径不消费该资源）。", owner, gate);

            // —— ④ 肾上腺素周期账本（清零时机与跨窗口保留）——
            RegisterTemporarilyUncomparable(fields, rejections, legacyAdrenaline, "adrenaline[unitId].cycleId",
                whyNoCycle, owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyAdrenaline, "adrenaline[unitId].reservations",
                whyNoCycle + "（旧侧没有「按计划归属、带 ReservationCycleId」的预留明细）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyAdrenaline, "adrenaline[unitId].reservedTotal",
                whyNoCycle + "（预留合计同样不存在）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyAdrenaline, "units[i].adrenalineCycleId",
                whyNoCycle + "（单位只读镜像里的周期号在旧侧没有对应字段）", owner, gate);

            // —— ⑤ 跨窗口计划不变性（计划的预算投影与来源窗口账本的联系）——
            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].budgetCostTicks",
                whyNoIntegerBudget + "（动作的整数预算成本在旧侧由动画/时间线隐式决定，没有可比较字段）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].reservedTurnBudgetTicks",
                whyNoIntegerBudget + "（计划当前持有的预留额同样是新侧投影）", owner, gate);

            RegisterTemporarilyUncomparable(fields, rejections, legacyPlans, "plans[i].submittedWindowLedger",
                whyNoWindow + "（「计划跨窗口切换时其来源窗口账本逐字不变」这条不变量的旧侧对应事实不存在："
                + "旧侧动作根本没有窗口归属；plans[i].submittedWindowId 本身已由任务 05 的登记项覆盖）",
                owner, gate);
        }

        /// <summary>
        /// 任务 08 的登记项：本任务在<strong>两条通道上都不能做字段级比较</strong>的那一项事实。
        ///
        /// <para>
        /// <strong>为什么这里只有一条（这是刻意的结构决定，不是登记不全）</strong>：
        /// 策略级不变量是「<strong>「登记为暂不可比较」与「已真的在比较」不能同时成立</strong>」
        /// （见本类 <c>RegisterTemporarilyUncomparable</c> 上方的说明与
        /// <c>ShadowComparisonDetector.Task08AiAndArbitrationFacts</c>）。
        /// <c>intents[…]</c> / <c>conflictGroups[…]</c> / <c>contacts[…]</c> / <c>aiControllers[…]</c>
        /// 这四族在<strong>快照对快照通道</strong>上由 <c>Task08AiAndArbitrationFacts</c> 逐字段真比较
        /// （篡改探针可证伪）；把它们同时登记成"暂不可比较"就等于用一个登记把真比较掩盖掉。
        /// 因此四族**不**在这里登记——它们的"旧侧无对应对象"这一覆盖边界由
        /// <c>Task07</c> 的既有 <c>intents.count</c> 登记项与
        /// <c>Task08AiAndArbitrationFacts</c> 的类注释共同披露。
        /// </para>
        ///
        /// <para>
        /// 反过来，<c>StagedResolution</c>（<c>RemainingHits</c> /
        /// <c>Aggregate.TotalDamageQ10</c>）是<strong>只读诊断面、不进快照哈希</strong>
        /// （08 交接 §4.5），因此它<strong>在两条通道上都不进可比面</strong>：
        /// 快照里根本没有它，旧侧也没有对应对象 ⇒ 必须逐条登记，绝不允许整体忽略。
        /// </para>
        /// </summary>
        private static void RegisterTask08ProfileRegistrations(
            List<TemporarilyUncomparableField> fields, List<string> rejections)
        {
            const string owner = "08";
            const string gate = "任务 10 切换主场景到 New 之前";
            // 旧侧唯一的"伤害"事实：CombatUnit 的活动生命（没有"按目标聚合的分阶段求解结果"）。
            const string legacyUnits = "CombatSampleScene/Player#CombatUnit.CurrentHealth";

            RegisterTemporarilyUncomparable(fields, rejections, legacyUnits, "stagedResolution.remainingHits",
                "旧权威里没有「按目标聚合的分阶段求解结果」这一对象：伤害在旧实现里逐次直接扣减"
                + "CombatUnit 生命，没有「同 Tick 多个来源指向同一目标后按通道聚合」的可采样中间事实。"
                + "新侧的 StagedResolution 是**只读诊断面、不进快照哈希**（08 交接 §4.5），"
                + "因此它同样不进快照对快照通道的可比面——两条通道都不比较，故必须逐条登记："
                + "不是「被忽略」，也不是「被批准」。"
                + "四类真事实（Intent 载荷 / 冲突组 / 接触键 / AI 决策状态）**不在此登记**，"
                + "因为它们在快照通道上已被 Task08AiAndArbitrationFacts 逐字段真比较。",
                owner, gate);

            // 防回归不变量：本方法登记的任何字段都**不得**属于 Task08AiAndArbitrationFacts 的比较面
            // （否则策略自相矛盾：同一 CaseId 上既比较又登记）。
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] == null) continue;
                if (!IsTask08ProfileField(fields[i].Field)) continue;
                rejections.Add(ShadowComparisonCodes.ShadowPolicySwitchInconsistent
                    + "|registered-but-compared|" + fields[i].Id);
            }
        }

        /// <summary>用显式给出的暂不可比较字段、批准差异与既有拒绝项构造策略。</summary>
        public static ShadowCasePolicy CreateWithApprovals(
            string caseId, string rulesVersion,
            IReadOnlyList<TemporarilyUncomparableField> temporarilyUncomparable,
            IReadOnlyList<ShadowComparisonApproval> approvals,
            IReadOnlyList<string> rejections,
            bool compareTask08ProfileFacts = false)
        {
            var fields = new List<TemporarilyUncomparableField>();
            if (temporarilyUncomparable != null)
            {
                for (int i = 0; i < temporarilyUncomparable.Count; i++) fields.Add(temporarilyUncomparable[i]);
            }

            var accepted = new List<ShadowComparisonApproval>();
            var rejected = new List<string>();
            if (rejections != null)
            {
                for (int i = 0; i < rejections.Count; i++) rejected.Add(rejections[i]);
            }

            // 开关与登记项自相矛盾 ⇒ 显式拒绝（fail-closed，绝不静默开启或静默退化）。
            // 详见 ShadowComparisonCodes.ShadowPolicySwitchInconsistent 的注释。
            if (!compareTask08ProfileFacts)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    TemporarilyUncomparableField candidate = fields[i];
                    if (candidate == null || !IsTask08ProfileField(candidate.Field)) continue;
                    rejected.Add(ShadowComparisonCodes.ShadowPolicySwitchInconsistent
                        + "|compareTask08ProfileFacts=false|" + candidate.Id);
                }
            }

            if (approvals != null)
            {
                for (int i = 0; i < approvals.Count; i++)
                {
                    var approval = approvals[i];
                    if (approval == null) continue;
                    if (IsUsableApproval(approval, caseId, rulesVersion)) accepted.Add(approval);
                    else rejected.Add(BroadRejectionPrefix + "|" + approval);
                }
            }

            return new ShadowCasePolicy(caseId ?? string.Empty, rulesVersion ?? string.Empty,
                fields, accepted, rejected, compareTask08ProfileFacts: compareTask08ProfileFacts);
        }

        /// <summary>
        /// 该字段名是否属于任务 08 画像的登记项族（唯一权威 = <see cref="RegisterTask08ProfileRegistrations"/>）。
        ///
        /// 它只用于 <see cref="CreateWithApprovals"/> 的"开关与登记项自相矛盾"判据，
        /// <strong>不</strong>参与任何比较或忽略判定：比较面由
        /// <c>ShadowDifferenceDetector.Task08AiAndArbitrationFacts</c> 的显式字段清单决定。
        /// </summary>
        private static bool IsTask08ProfileField(string field)
        {
            if (string.IsNullOrEmpty(field)) return false;
            // **只**覆盖"在快照通道上被 Task08AiAndArbitrationFacts 逐字段真比较"的四族：
            // 它们**不得**出现在暂不可比较登记里（否则同一 CaseId 上既比较又登记 ⇒ 自相矛盾）。
            //
            // ⚠️ `stagedResolution.` **不在**此列（这是一处真实缺陷的修正）：它是**唯一两条通道
            // 都不比较**的只读诊断面，因此**必须**被逐条登记；把它算进"已比较族"会让
            // `RegisterTask08ProfileRegistrations` 末尾那道防回归循环**拒绝它自己的合法登记**
            // （实测：`SHADOW_POLICY_SWITCH_INCONSISTENT|registered-but-compared|
            // CombatSampleScene/Player#CombatUnit.CurrentHealth#stagedResolution.remainingHits`）。
            return field.StartsWith("intents[", StringComparison.Ordinal)
                || field.StartsWith("conflictGroups", StringComparison.Ordinal)
                || field.StartsWith("contacts[", StringComparison.Ordinal)
                || field.StartsWith("aiControllers", StringComparison.Ordinal);
        }

        private static bool IsUsableApproval(
            ShadowComparisonApproval approval, string caseId, string rulesVersion)
        {
            if (!string.Equals(approval.CaseId, caseId, StringComparison.Ordinal)) return false;
            if (!string.Equals(approval.RulesVersion, rulesVersion, StringComparison.Ordinal)) return false;
            if (ShadowAllowlistRules.IsBroadPattern(approval.FieldPath)) return false;
            if (string.IsNullOrWhiteSpace(approval.Reason)) return false;
            return true;
        }

        private static void RegisterTemporarilyUncomparable(
            List<TemporarilyUncomparableField> target, List<string> rejections,
            string objectPath, string field, string reason, string ownerTask, string removalGate)
        {
            TemporarilyUncomparableField created;
            string rejection;
            if (TemporarilyUncomparableField.TryCreate(
                    objectPath, field, reason, ownerTask, removalGate, out created, out rejection))
            {
                target.Add(created);
            }
            else
            {
                rejections.Add(rejection);
            }
        }

        /// <summary>注册一条批准差异；被拒绝时写入 <paramref name="rejections"/> 并返回 false。</summary>
        public static bool TryRegisterApproval(
            List<ShadowComparisonApproval> approvals, List<string> rejections,
            string caseId, string rulesVersion, string fieldPath, string reason, string checkpoint = null)
        {
            ShadowComparisonApproval created;
            string rejection;
            if (ShadowComparisonApproval.TryCreate(
                    caseId, rulesVersion, fieldPath, reason, checkpoint, out created, out rejection))
            {
                approvals.Add(created);
                return true;
            }

            rejections.Add(rejection);
            return false;
        }

        /// <summary>只用一组"候选批准差异"构造策略（用于证明宽泛白名单被拒绝）。</summary>
        public static ShadowCasePolicy CreateWithCandidateApprovals(
            string caseId, string rulesVersion,
            IEnumerable<KeyValuePair<string, string>> candidateFieldPathsAndReasons)
        {
            var approvals = new List<ShadowComparisonApproval>();
            var rejections = new List<string>();

            if (candidateFieldPathsAndReasons != null)
            {
                foreach (var candidate in candidateFieldPathsAndReasons)
                {
                    TryRegisterApproval(approvals, rejections, caseId, rulesVersion,
                        candidate.Key, candidate.Value);
                }
            }

            return new ShadowCasePolicy(caseId ?? string.Empty, rulesVersion ?? string.Empty,
                new List<TemporarilyUncomparableField>(), approvals, rejections);
        }

        internal bool RejectionStartsWithBroadAllowlist
        {
            get
            {
                for (int i = 0; i < Rejections.Count; i++)
                {
                    if (Rejections[i].StartsWith(BroadRejectionPrefix, StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }
    }
}
