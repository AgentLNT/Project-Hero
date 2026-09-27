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
            bool compareScheduleFacts = false, bool compareMovementFacts = false)
        {
            CaseId = caseId;
            RulesVersion = rulesVersion;
            TemporarilyUncomparable = temporarilyUncomparable;
            Approvals = approvals;
            Rejections = rejections;
            CompareScheduleFacts = compareScheduleFacts;
            CompareMovementFacts = compareMovementFacts;
        }

        public string CaseId { get; }

        public string RulesVersion { get; }

        public IReadOnlyList<TemporarilyUncomparableField> TemporarilyUncomparable { get; }

        public IReadOnlyList<ShadowComparisonApproval> Approvals { get; }

        public IReadOnlyList<string> Rejections { get; }

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
        /// 任务 03B 的冻结用例策略。
        ///
        /// 暂不可比较字段逐条给出对象路径/字段/原因/负责任务/最迟清零门槛。
        /// 批准差异清单为空——<strong>03B 不批准任何差异</strong>；宽泛批准在这里就该被拒绝。
        /// </summary>
        public static ShadowCasePolicy CreateDefault(
            string caseId, string rulesVersion,
            bool compareScheduleFacts = false, bool compareMovementFacts = false)
        {
            var temporarilyUncomparable = new List<TemporarilyUncomparableField>();
            var approvals = new List<ShadowComparisonApproval>();
            var rejections = new List<string>();

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/Player#CombatUnit.CurrentAdrenaline",
                "availableAdrenaline",
                "旧实现用浮点 CurrentAdrenaline 逐帧衰减（CombatUnit.Update，5/s），新内核用整数 AvailableAdrenaline + 个人 CycleId；量纲与生命周期都不同，在只读检查点无法稳定采样。",
                "07",
                "任务 10 切换主场景到 New 之前");

            RegisterTemporarilyUncomparable(
                temporarilyUncomparable, rejections,
                "CombatSampleScene/Player#CombatUnit.CurrentStamina",
                "staminaQ10",
                "旧体力是 CombatUnit 浮点字段，新方案 1.2 已定移除；在新资源模型落地前没有对应字段可比较。",
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

            return new ShadowCasePolicy(caseId ?? string.Empty, rulesVersion ?? string.Empty,
                temporarilyUncomparable, approvals, rejections, compareScheduleFacts, compareMovementFacts);
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

        /// <summary>用显式给出的暂不可比较字段、批准差异与既有拒绝项构造策略。</summary>
        public static ShadowCasePolicy CreateWithApprovals(
            string caseId, string rulesVersion,
            IReadOnlyList<TemporarilyUncomparableField> temporarilyUncomparable,
            IReadOnlyList<ShadowComparisonApproval> approvals,
            IReadOnlyList<string> rejections)
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
                fields, accepted, rejected);
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
