using System;
using System.Collections.Generic;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>
    /// Shadow 比较结果的四类归属（任务 03B「必须产出」7；不变量 22）。
    /// 这四类是<strong>互斥且穷尽</strong>的：任何一条观察都必须落到其中一类，
    /// 不允许"整类事件/全部血量/全部位置"式的宽泛忽略。
    /// </summary>
    public enum ShadowDifferenceKind
    {
        /// <summary>必须相等的基础设施事实（Tick、定义哈希、ID、阵营、集合计数……）。永不批准差异。</summary>
        InfrastructureFact = 0,

        /// <summary>按新规则验证的事实（已被任务 04–09 覆盖的字段）。差异即回归。</summary>
        NewRuleVerifiedFact = 1,

        /// <summary>限期清零的暂不可比较字段。必须携带 Legacy 对象路径、字段、原因、负责任务与最迟清零门槛。</summary>
        TemporarilyUncomparable = 2,

        /// <summary>逐用例批准的差异。必须精确到用例 ID + RulesVersion + 字段 + 原因。</summary>
        ApprovedDifference = 3
    }

    /// <summary>
    /// 比较结果模式（行为开关）。<strong>不是</strong>忽略整类差异的白名单：
    /// 每一类内部仍然逐字段比较，差异照常进入 <see cref="ShadowComparisonReport.Differences"/>。
    /// </summary>
    public enum ShadowComparisonMode
    {
        /// <summary>严格模式：任何字段差异都记为非预期差异（任务 03B 首次交付的默认值）。</summary>
        Strict = 0,

        /// <summary>暂不可比较模式：仅按已登记的 <see cref="TemporarilyUncomparableField"/> 判定相关字段。</summary>
        TemporarilyUncomparable = 1
    }

    /// <summary>
    /// 一条字段级差异观察。字段路径必须精确（<c>units[2].healthQ10</c> 这样的形式），
    /// 便于报告"首个非预期差异"。
    /// </summary>
    public sealed class ShadowFieldDifference
    {
        public ShadowFieldDifference(
            ShadowDifferenceKind kind, long logicalTick, string checkpoint, string fieldPath,
            string legacyValue, string shadowValue, string reason, long relatedEventSequence)
        {
            Kind = kind;
            LogicalTick = logicalTick;
            Checkpoint = checkpoint ?? string.Empty;
            FieldPath = fieldPath ?? string.Empty;
            LegacyValue = legacyValue ?? string.Empty;
            ShadowValue = shadowValue ?? string.Empty;
            Reason = reason ?? string.Empty;
            RelatedEventSequence = relatedEventSequence;
        }

        public ShadowDifferenceKind Kind { get; }

        /// <summary>逻辑 Tick（对齐键，不是 Unity 帧序号）。</summary>
        public long LogicalTick { get; }

        /// <summary>具名检查点（例如 <c>LateUpdate</c>）。</summary>
        public string Checkpoint { get; }

        public string FieldPath { get; }

        public string LegacyValue { get; }

        public string ShadowValue { get; }

        public string Reason { get; }

        /// <summary>相关事件序号（-1 表示该差异不绑定事件）。</summary>
        public long RelatedEventSequence { get; }

        /// <summary>该差异是否属于"非预期差异"（即阻断等价声明的差异）。</summary>
        public bool IsUnexpected => Kind == ShadowDifferenceKind.NewRuleVerifiedFact;

        public override string ToString()
            => Kind + " tick=" + LogicalTick + " @" + Checkpoint + " " + FieldPath
               + " legacy=" + LegacyValue + " shadow=" + ShadowValue;
    }

    /// <summary>
    /// 限期清零的暂不可比较字段记录。
    ///
    /// <strong>不可构造出"缺字段"的记录</strong>：对象路径、字段、原因、负责任务与最迟清零门槛
    /// 五者缺一即被 <see cref="TryCreate"/> 拒绝（拒绝码
    /// <see cref="ShadowComparisonCodes.TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate"/>）。
    /// </summary>
    public sealed class TemporarilyUncomparableField
    {
        private TemporarilyUncomparableField(
            string legacyObjectPath, string field, string reason, string ownerTask, string removalGate)
        {
            LegacyObjectPath = legacyObjectPath;
            Field = field;
            Reason = reason;
            OwnerTask = ownerTask;
            RemovalGate = removalGate;
        }

        /// <summary>具体 Legacy 对象路径，例如 <c>CombatSampleScene/Player#CombatUnit.CurrentAdrenaline</c>。</summary>
        public string LegacyObjectPath { get; }

        /// <summary>具体字段名。</summary>
        public string Field { get; }

        public string Reason { get; }

        /// <summary>负责迁移的任务号（例如 <c>07</c>、<c>09</c>）。</summary>
        public string OwnerTask { get; }

        /// <summary>最迟清零门槛（例如 <c>任务 10 切换前</c>）。</summary>
        public string RemovalGate { get; }

        public string Id => LegacyObjectPath + "#" + Field;

        public static bool TryCreate(
            string legacyObjectPath, string field, string reason, string ownerTask, string removalGate,
            out TemporarilyUncomparableField created, out string rejection)
        {
            created = null;
            if (string.IsNullOrWhiteSpace(legacyObjectPath))
            {
                rejection = ShadowComparisonCodes.TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate
                    + "|legacyObjectPath";
                return false;
            }
            if (string.IsNullOrWhiteSpace(field))
            {
                rejection = ShadowComparisonCodes.TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate + "|field";
                return false;
            }
            if (string.IsNullOrWhiteSpace(reason))
            {
                rejection = ShadowComparisonCodes.TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate + "|reason";
                return false;
            }
            if (string.IsNullOrWhiteSpace(ownerTask))
            {
                rejection = ShadowComparisonCodes.TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate + "|ownerTask";
                return false;
            }
            if (string.IsNullOrWhiteSpace(removalGate))
            {
                rejection = ShadowComparisonCodes.TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate
                    + "|removalGate";
                return false;
            }

            rejection = null;
            created = new TemporarilyUncomparableField(legacyObjectPath, field, reason, ownerTask, removalGate);
            return true;
        }

        public override string ToString()
            => Id + " owner=" + OwnerTask + " gate=" + RemovalGate + " reason=" + Reason;
    }

    /// <summary>
    /// 逐用例批准差异。<strong>精确到用例 ID + RulesVersion + 字段 + 原因</strong>，
    /// 任一为空或含通配符即被拒绝——这条规则让"宽泛白名单"在结构上无法存在
    /// （任务包禁止事项第 7 条；必需测试 <c>BroadDifferenceAllowlistIsRejected</c>）。
    /// </summary>
    public sealed class ShadowComparisonApproval
    {
        private ShadowComparisonApproval(
            string caseId, string rulesVersion, string fieldPath, string reason, string checkpoint)
        {
            CaseId = caseId;
            RulesVersion = rulesVersion;
            FieldPath = fieldPath;
            Reason = reason;
            Checkpoint = checkpoint ?? string.Empty;
        }

        /// <summary>用例 ID（例如 <c>03B-shadow-equivalent-empty-ticks</c>）。</summary>
        public string CaseId { get; }

        /// <summary>被批准差异所属的 RulesVersion（必须与定义一致）。</summary>
        public string RulesVersion { get; }

        /// <summary>精确字段路径（不得含通配符）。</summary>
        public string FieldPath { get; }

        public string Reason { get; }

        /// <summary>可选的具名检查点限定；空表示该用例的全部检查点。</summary>
        public string Checkpoint { get; }

        public static bool TryCreate(
            string caseId, string rulesVersion, string fieldPath, string reason, string checkpoint,
            out ShadowComparisonApproval created, out string rejection)
        {
            created = null;
            if (string.IsNullOrWhiteSpace(caseId))
            {
                rejection = ShadowComparisonCodes.ApprovalRequiresCaseIdRulesVersionFieldAndReason + "|caseId";
                return false;
            }
            if (string.IsNullOrWhiteSpace(rulesVersion))
            {
                rejection = ShadowComparisonCodes.ApprovalRequiresCaseIdRulesVersionFieldAndReason + "|rulesVersion";
                return false;
            }
            if (string.IsNullOrWhiteSpace(fieldPath))
            {
                rejection = ShadowComparisonCodes.ApprovalRequiresCaseIdRulesVersionFieldAndReason + "|fieldPath";
                return false;
            }
            if (string.IsNullOrWhiteSpace(reason))
            {
                rejection = ShadowComparisonCodes.ApprovalRequiresCaseIdRulesVersionFieldAndReason + "|reason";
                return false;
            }
            if (ShadowAllowlistRules.IsBroadPattern(caseId)
                || ShadowAllowlistRules.IsBroadPattern(fieldPath)
                || ShadowAllowlistRules.IsBroadPattern(rulesVersion))
            {
                rejection = ShadowComparisonCodes.BroadDifferenceAllowlistRejected
                    + "|" + caseId + "|" + fieldPath;
                return false;
            }

            rejection = null;
            created = new ShadowComparisonApproval(caseId, rulesVersion, fieldPath, reason, checkpoint);
            return true;
        }

        public override string ToString()
            => CaseId + "|" + RulesVersion + "|" + FieldPath + "|" + Reason;
    }

    /// <summary>批准差异与宽泛白名单的结构规则。</summary>
    public static class ShadowAllowlistRules
    {
        private static readonly string[] BroadTokens =
        {
            "*", "?", "all", "any", "everything", "wildcard"
        };

        /// <summary>
        /// 该模式串是否是"宽泛"模式（含通配符或整类关键词）。
        /// 判定为大小写不敏感的子串匹配，并对 <c>all</c> 做词边界保护
        /// （<c>Shallow</c> 不命中，<c>all</c> / <c>all units</c> 命中）。
        /// </summary>
        public static bool IsBroadPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return true;
            string lowered = pattern.ToLowerInvariant();
            for (int i = 0; i < BroadTokens.Length; i++)
            {
                string token = BroadTokens[i];
                int index = lowered.IndexOf(token, StringComparison.Ordinal);
                while (index >= 0)
                {
                    bool leftOk = index == 0 || !char.IsLetter(lowered[index - 1]);
                    int end = index + token.Length;
                    bool rightOk = end >= lowered.Length || !char.IsLetter(lowered[end]);
                    if (leftOk && rightOk) return true;
                    index = lowered.IndexOf(token, index + 1, StringComparison.Ordinal);
                }
            }
            return false;
        }
    }

    /// <summary>Shadow 比较的稳定拒绝码。</summary>
    public static class ShadowComparisonCodes
    {
        public const string TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate =
            "TEMPORARILY_UNCOMPARABLE_FIELD_REQUIRES_OWNER_TASK_AND_REMOVAL_GATE";

        public const string ApprovalRequiresCaseIdRulesVersionFieldAndReason =
            "APPROVAL_REQUIRES_CASE_ID_RULES_VERSION_FIELD_AND_REASON";

        public const string BroadDifferenceAllowlistRejected = "BROAD_DIFFERENCE_ALLOWLIST_REJECTED";

        public const string ShadowBudgetOverrun = "SHADOW_BUDGET_OVERRUN";

        public const string ShadowCheckpointTickMismatch = "SHADOW_CHECKPOINT_TICK_MISMATCH";

        public const string ShadowCheckpointWithoutCheckpointRun = "SHADOW_CHECKPOINT_WITHOUT_CHECKPOINT_RUN";

        /// <summary>
        /// 逐用例策略的开关与登记项自相矛盾（任务 09/C2b）。
        ///
        /// 触发条件：调用方在 <c>temporarilyUncomparable</c> 里传入了任务 08 画像的登记项
        /// （<c>intents[…]</c> / <c>conflictGroups[…]</c> / <c>contacts[…]</c> /
        /// <c>aiControllers[…]</c> / <c>stagedResolution.*</c>）却<strong>没有</strong>打开
        /// <c>compareTask08ProfileFacts</c>。
        ///
        /// 为什么必须显式拒绝而不是"自动开启"或"静默退化"：
        /// <list type="bullet">
        /// <item>自动开启 = 用字符串 ID 猜调用方意图，"静默开启"与"静默关闭"是同一类缺陷；</item>
        /// <item>静默退化 = 报告里出现 08 的登记项，却根本没有做 08 的逐条比较 ——
        /// 这正是"用宽泛/空洞的登记制造假绿"的一种形态。</item>
        /// </list>
        /// 拒绝后策略进入既有拒绝通道（<c>report.Rejections</c>），因此
        /// <c>CanClaimEquivalence</c> 恒为 false，等价声明不可能建立在自相矛盾的策略上。
        /// </summary>
        public const string ShadowPolicySwitchInconsistent = "SHADOW_POLICY_SWITCH_INCONSISTENT";
    }

    /// <summary>
    /// 一次性冻结的比较配置（进入报告的"比较配置版本"字段）。
    /// </summary>
    public sealed class ShadowComparisonConfig
    {
        /// <summary>比较配置版本：字段集合、分类与对齐规则变化时必须提升。</summary>
        public const string CurrentVersion = "shadow-compare-v1";

        public ShadowComparisonConfig(
            int comparisonConfigVersion,
            int maxStepsPerComparison,
            int maxCheckpoints,
            ShadowComparisonMode mode)
        {
            ComparisonConfigVersion = comparisonConfigVersion;
            MaxStepsPerComparison = maxStepsPerComparison;
            MaxCheckpoints = maxCheckpoints;
            Mode = mode;
        }

        public int ComparisonConfigVersion { get; }

        /// <summary>单次比较允许覆盖的最大检查点数（超出即判预算超限）。</summary>
        public int MaxStepsPerComparison { get; }

        /// <summary>本场战斗允许持有的最大检查点数（超出即丢弃最旧，并记录丢弃数）。</summary>
        public int MaxCheckpoints { get; }

        public ShadowComparisonMode Mode { get; }

        /// <summary>任务 03B 交付默认值：严格模式、每场最多 4096 检查点、单次比较最多覆盖 64 个。</summary>
        public static ShadowComparisonConfig Default()
            => new ShadowComparisonConfig(1, 64, 4096, ShadowComparisonMode.Strict);

        /// <summary>严格模式配置（单次比较覆盖上限显式给出，便于预算用例）。</summary>
        public static ShadowComparisonConfig Strict(int maxStepsPerComparison = 64, int maxCheckpoints = 4096)
            => new ShadowComparisonConfig(1, maxStepsPerComparison, maxCheckpoints, ShadowComparisonMode.Strict);
    }

    /// <summary>
    /// 定义侧槽位顺序条目（任务 03B 第二收尾轮 R1）。
    ///
    /// 唯一权威顺序来自 Encounter 定义：<c>SlotId</c> 按 <see cref="StringComparer.Ordinal"/>
    /// 升序分配 <c>UnitId(1..N)</c>（<c>BattleInitializer</c>）。比较器用它把"新侧单位的 UnitId"
    /// 解析回"槽位 ID"，<strong>不读取 Legacy 观测</strong>——因此旧侧把单位绑错槽位仍会被发现。
    /// </summary>
    public sealed class LegacySlotOrderEntry
    {
        public LegacySlotOrderEntry(string slotId, long unitId)
        {
            SlotId = slotId ?? string.Empty;
            UnitId = unitId;
        }

        public string SlotId { get; }

        public long UnitId { get; }

        public override string ToString() => SlotId + "->" + UnitId;
    }

    /// <summary>
    /// 比较输入的只读集合（Bootstrap 在检查点构造，测试可直接构造）。
    /// </summary>
    public sealed class ShadowComparisonInput
    {
        public ShadowComparisonInput(
            string battleDefinitionHash,
            string encounter,
            BattleRuntimeMode mode,
            string inputSummary,
            string rulesVersion,
            ShadowComparisonConfig configuration,
            IReadOnlyList<LogicSnapshot> legacyCheckpoints,
            IReadOnlyList<LogicSnapshot> shadowCheckpoints,
            IReadOnlyList<ShadowCheckpointEventBinding> checkpointEvents = null,
            IReadOnlyList<LegacyLogicObservation> legacyObservations = null,
            IReadOnlyList<LegacySlotOrderEntry> slotOrder = null,
            int shadowTickDeficit = 0)
        {
            BattleDefinitionHash = battleDefinitionHash ?? string.Empty;
            Encounter = encounter ?? string.Empty;
            Mode = mode;
            InputSummary = inputSummary ?? string.Empty;
            RulesVersion = rulesVersion ?? string.Empty;
            Configuration = configuration ?? ShadowComparisonConfig.Default();
            LegacyCheckpoints = legacyCheckpoints ?? Array.Empty<LogicSnapshot>();
            ShadowCheckpoints = shadowCheckpoints ?? Array.Empty<LogicSnapshot>();
            CheckpointEvents = checkpointEvents ?? Array.Empty<ShadowCheckpointEventBinding>();
            LegacyObservations = legacyObservations ?? Array.Empty<LegacyLogicObservation>();
            SlotOrder = slotOrder ?? Array.Empty<LegacySlotOrderEntry>();
            ShadowTickDeficit = shadowTickDeficit;
        }

        public string BattleDefinitionHash { get; }

        public string Encounter { get; }

        public BattleRuntimeMode Mode { get; }

        public string InputSummary { get; }

        public string RulesVersion { get; }

        public ShadowComparisonConfig Configuration { get; }

        public IReadOnlyList<LogicSnapshot> LegacyCheckpoints { get; }

        public IReadOnlyList<LogicSnapshot> ShadowCheckpoints { get; }

        /// <summary>
        /// 可选的"检查点 → 事件序号"绑定：某个逻辑 Tick 内已提交事件的首个序号与条数。
        /// 空集合表示本次比较不携带事件绑定（差异的 <c>RelatedEventSequence</c> 为 -1）。
        /// </summary>
        public IReadOnlyList<ShadowCheckpointEventBinding> CheckpointEvents { get; }

        /// <summary>
        /// 可选的 Legacy 侧只读观测序列（任务 03B 第二收尾轮 R1）。
        ///
        /// 非空时比较器只走"真实可比较字段"通道：逐检查点按 <c>Tick</c> 对齐后，
        /// 只比较 <see cref="LegacyLogicObservation.ComparableFieldPaths"/> 列出的字段；
        /// 其余字段由 <c>TemporarilyUncomparableField</c> 登记表逐条披露，绝不填默认值冒充"已比较"。
        /// 为空表示本次比较不使用 Legacy 观测（纯 Logic 世界对 Logic 世界的用例）。
        /// </summary>
        public IReadOnlyList<LegacyLogicObservation> LegacyObservations { get; }

        /// <summary>
        /// 定义侧槽位顺序（<c>SlotId</c> Ordinal 升序 ⇒ <c>UnitId</c>），用于把新侧 <c>UnitId</c>
        /// 独立解析回槽位 ID。为空表示本次比较不做槽位映射核对。
        /// </summary>
        public IReadOnlyList<LegacySlotOrderEntry> SlotOrder { get; }

        /// <summary>
        /// 新模拟未能追上的旧时间线 Tick 数（0 = 两侧已对齐）。
        ///
        /// 非 0 表示本次比较**没有覆盖全部检查点**：比较器会据此登记一条基础设施事实差异
        /// 并阻断等价声明——既不静默丢 Tick，也不假装对齐。
        /// </summary>
        public int ShadowTickDeficit { get; }

        /// <summary>
        /// 某个逻辑 Tick 的差异所绑定的事件序号：该 Tick 内首个已提交事件的序号；
        /// 该 Tick 没有事件（或未提供绑定）时为 -1。绝不猜测、绝不借用邻近 Tick 的序号。
        /// </summary>
        public long EventSequenceAt(long logicalTick)
        {
            for (int i = 0; i < CheckpointEvents.Count; i++)
            {
                var binding = CheckpointEvents[i];
                if (binding == null || binding.LogicalTick != logicalTick) continue;
                return binding.RelatedEventSequence;
            }
            return -1L;
        }
    }

    /// <summary>
    /// 检查点事件绑定：把"某个逻辑 Tick 上生成的事件"变成可比较的事实，
    /// 从而让差异报告能定位到<b>具体事件序号</b>，而不是只给一个 Tick。
    ///
    /// 生产者：任务 04–09 的 Shadow 检查点（来自 <c>StepResult.Events</c> 的序号，
    /// 或经 <c>IShadowMirroredInputSource</c> 镜像进来的可信请求所产生的事件）。
    /// </summary>
    public sealed class ShadowCheckpointEventBinding
    {
        public ShadowCheckpointEventBinding(long logicalTick, long firstEventSequence, int eventCount)
        {
            LogicalTick = logicalTick;
            FirstEventSequence = firstEventSequence;
            EventCount = eventCount;
        }

        public long LogicalTick { get; }

        /// <summary>该 Tick 内首个已提交事件的序号。</summary>
        public long FirstEventSequence { get; }

        public int EventCount { get; }

        /// <summary>差异绑定的事件序号：无事件时为 -1（表示"该 Tick 不绑定事件"）。</summary>
        public long RelatedEventSequence => EventCount > 0 ? FirstEventSequence : -1L;

        public override string ToString()
            => "events@" + LogicalTick + " first=" + RelatedEventSequence + " count=" + EventCount;
    }

    /// <summary>
    /// Shadow 比较报告（任务 03B「必须产出」7）。
    ///
    /// 报告同时承载四类结果、首个非预期差异、比较配置版本与预算失效状态。
    /// <see cref="CanClaimEquivalence"/> 只有在"没有非预期差异、没有预算失效、
    /// 没有被拒绝的批准差异"时才为 true——任何一条不成立都不宣称等价。
    /// </summary>
    public sealed class ShadowComparisonReport
    {
        private readonly List<ShadowFieldDifference> _differences = new List<ShadowFieldDifference>();
        private readonly List<TemporarilyUncomparableField> _temporarilyUncomparable =
            new List<TemporarilyUncomparableField>();
        private readonly List<ShadowComparisonApproval> _approvals = new List<ShadowComparisonApproval>();
        private readonly List<string> _rejections = new List<string>();
        private readonly List<ShadowMigrationObligation> _resolvedMigrationObligations = new List<ShadowMigrationObligation>();
        public IReadOnlyList<ShadowMigrationObligation> ResolvedMigrationObligations => _resolvedMigrationObligations;
        internal void AddResolvedMigrationObligation(ShadowMigrationObligation proof) => _resolvedMigrationObligations.Add(proof);
        private readonly List<ShadowMigrationObservation> _migrationObservations = new List<ShadowMigrationObservation>();
        public IReadOnlyList<ShadowMigrationObservation> MigrationObservations => _migrationObservations;
        internal void AddMigrationObservation(ShadowMigrationObservation observation) => _migrationObservations.Add(observation);

        public ShadowComparisonReport(
            string battleDefinitionHash,
            string encounter,
            BattleRuntimeMode mode,
            string inputSummary,
            string rulesVersion,
            int comparisonConfigVersion)
        {
            BattleDefinitionHash = battleDefinitionHash ?? string.Empty;
            Encounter = encounter ?? string.Empty;
            Mode = mode;
            InputSummary = inputSummary ?? string.Empty;
            RulesVersion = rulesVersion ?? string.Empty;
            ComparisonConfigVersion = comparisonConfigVersion;
        }

        public string BattleDefinitionHash { get; }

        public string Encounter { get; }

        public BattleRuntimeMode Mode { get; }

        public string InputSummary { get; }

        public string RulesVersion { get; }

        public int ComparisonConfigVersion { get; }

        /// <summary>按检查点冻结顺序（逻辑 Tick 升序、同 Tick 按注册顺序）的全部差异观察。</summary>
        public IReadOnlyList<ShadowFieldDifference> Differences => _differences;

        public IReadOnlyList<TemporarilyUncomparableField> TemporarilyUncomparable => _temporarilyUncomparable;

        public IReadOnlyList<ShadowComparisonApproval> Approvals => _approvals;

        /// <summary>被拒绝的批准差异/暂不可比较登记（拒绝码 + 细节）。</summary>
        public IReadOnlyList<string> Rejections => _rejections;

        /// <summary>比较过的检查点总数。</summary>
        public int ComparedCheckpoints { get; internal set; }

        /// <summary>
        /// 实际执行过的字段级比较次数（每个检查点上"必须相等的基础设施事实"逐字段计数）。
        ///
        /// 它与 <see cref="ComparedCheckpoints"/> 一起构成"比较不是空转"的可证伪证据：
        /// 只断言 <c>ComparedCheckpoints == 0</c> 会掩盖空转，只断言 <c>&gt; 0</c> 也不能排除
        /// "走了一遍循环但一个字段都没比"。因此报告同时记录**比较内容量**。
        /// </summary>
        public int ComparedFieldObservations { get; internal set; }

        /// <summary>被丢弃的检查点数（超出 <see cref="ShadowComparisonConfig.MaxCheckpoints"/>）。</summary>
        public int DroppedCheckpoints { get; internal set; }

        /// <summary>因逻辑 Tick 无法对齐而跳过的配对数（<strong>绝不</strong>按帧序号强行配对）。</summary>
        public int UnalignedCheckpoints { get; internal set; }

        /// <summary>诊断预算是否超限。</summary>
        public bool BudgetOverrun { get; internal set; }

        public string BudgetOverrunReason { get; internal set; }

        /// <summary>预算超限时本次比较被标记为无效。</summary>
        public bool InvalidatedByBudget => BudgetOverrun;

        /// <summary>首个非预期差异（无则为 null）。</summary>
        public ShadowFieldDifference FirstUnexpectedDifference { get; internal set; }

        public bool HasUnexpectedDifference => FirstUnexpectedDifference != null;

        /// <summary>"必须相等的基础设施事实"的差异条数（Tick/定义哈希/ID/集合计数/RNG 状态……）。</summary>
        public int InfrastructureDifferences { get; private set; }

        /// <summary>首个被违反的基础设施事实的字段路径（无则 null）。</summary>
        public string FirstInfrastructureDifferenceField { get; private set; }

        /// <summary>
        /// 是否允许宣称"本用例等价"。
        ///
        /// 六个条件缺一不可：
        /// <list type="number">
        /// <item>没有非预期差异（按新规则验证的事实差异）。</item>
        /// <item>本次比较没有被诊断预算作废。</item>
        /// <item>没有被拒绝的批准差异。</item>
        /// <item><b>没有未对齐检查点</b>——无法按逻辑 Tick 配对时，本次比较根本没有覆盖
        /// 全部检查点，"等价"是未经验证的结论（也绝不允许按帧序号强行配对）。</item>
        /// <item><b>没有基础设施事实差异</b>——"必须相等"的事实出现差异时，
        /// 两侧根本不是同一个世界。</item>
        /// <item><b>至少真的比较过一个检查点</b>——零比较的"没有差异"是空转，
        /// 不是等价（任务 03B 第二收尾轮 R1.3：不得因"无对齐检查点"就宣称等价）。</item>
        /// </list>
        /// </summary>
        public bool CanClaimEquivalence =>
            !HasUnexpectedDifference && !InvalidatedByBudget && _rejections.Count == 0
            && UnalignedCheckpoints == 0 && InfrastructureDifferences == 0
            && ComparedCheckpoints > 0;

        /// <summary>Comparable-surface equality alone never authorizes production migration.</summary>
        public bool HasCompleteMigrationCoverage => Mode == BattleRuntimeMode.Shadow && CanClaimEquivalence
            && ComparedFieldObservations > 0 && DroppedCheckpoints == 0
            && _temporarilyUncomparable.Count == 0 && CountOf(ShadowDifferenceKind.TemporarilyUncomparable) == 0;

        public string MigrationCoverageClaim
        {
            get
            {
                if (Mode != BattleRuntimeMode.Shadow) return "INVALID_MODE";
                if (!CanClaimEquivalence) return "INVALID_RUN:" + EquivalenceClaim;
                if (ComparedFieldObservations == 0) return "NO_FIELD_OBSERVATIONS";
                if (DroppedCheckpoints != 0) return "CHECKPOINTS_DROPPED:" + DroppedCheckpoints;
                if (_temporarilyUncomparable.Count != 0) return "INCOMPLETE_TEMPORARY_FIELDS:" + _temporarilyUncomparable.Count;
                if (CountOf(ShadowDifferenceKind.TemporarilyUncomparable) != 0) return "INCOMPLETE_TEMPORARY_OBSERVATIONS";
                return "COMPLETE";
            }
        }

        /// <summary>不允许宣称等价时的稳定理由码（<c>EQUIVALENT</c> 表示可以宣称）。</summary>
        public string EquivalenceClaim
        {
            get
            {
                if (InvalidatedByBudget) return "INVALID:" + BudgetOverrunReason;
                if (_rejections.Count > 0) return "REJECTED:" + _rejections[0];
                if (HasUnexpectedDifference) return "DIFFERENT:" + FirstUnexpectedDifference.FieldPath;
                if (InfrastructureDifferences > 0)
                    return "INFRASTRUCTURE_DIFFERS:" + FirstInfrastructureDifferenceField;
                if (UnalignedCheckpoints > 0) return "UNALIGNED:" + UnalignedCheckpoints;
                if (ComparedCheckpoints == 0) return "NO_COMPARABLE_CHECKPOINT";
                if (ResolvedMigrationObligations.Count > 0) return "COMPARABLE_SURFACE_EQUAL;NEW_RULE_EVIDENCE_VERIFIED";
                return "EQUIVALENT";
            }
        }

        public int CountOf(ShadowDifferenceKind kind)
        {
            int count = 0;
            for (int i = 0; i < _differences.Count; i++)
            {
                if (_differences[i].Kind == kind) count++;
            }
            return count;
        }

        internal void AddDifference(ShadowFieldDifference difference)
        {
            if (difference == null) return;
            _differences.Add(difference);

            if (difference.Kind == ShadowDifferenceKind.InfrastructureFact)
            {
                InfrastructureDifferences++;
                if (FirstInfrastructureDifferenceField == null)
                    FirstInfrastructureDifferenceField = difference.FieldPath;
            }

            if (difference.IsUnexpected && FirstUnexpectedDifference == null)
                FirstUnexpectedDifference = difference;
        }

        internal void AddTemporarilyUncomparable(TemporarilyUncomparableField field)
        {
            if (field != null) _temporarilyUncomparable.Add(field);
        }

        internal void AddApproval(ShadowComparisonApproval approval)
        {
            if (approval != null) _approvals.Add(approval);
        }

        internal void AddRejection(string rejection)
        {
            if (!string.IsNullOrEmpty(rejection)) _rejections.Add(rejection);
        }

        internal void MarkBudgetOverrun(string reason)
        {
            BudgetOverrun = true;
            BudgetOverrunReason = reason;
        }

        /// <summary>稳定文本输出（诊断/测试断言用；不参与任何逻辑判断）。</summary>
        public string Describe()
        {
            return "shadow-report hash=" + BattleDefinitionHash
                + " encounter=" + Encounter
                + " mode=" + BattleRuntimeModes.Describe(Mode)
                + " rules=" + RulesVersion
                + " cfg=" + ComparisonConfigVersion
                + " migrationCoverage=" + MigrationCoverageClaim
                + " ruleObligations=" + ResolvedMigrationObligations.Count
                + " representationObservations=" + MigrationObservations.Count
                + " checkpoints=" + ComparedCheckpoints
                + " comparedFields=" + ComparedFieldObservations
                + " dropped=" + DroppedCheckpoints
                + " unaligned=" + UnalignedCheckpoints
                + " infra=" + CountOf(ShadowDifferenceKind.InfrastructureFact)
                + " newRule=" + CountOf(ShadowDifferenceKind.NewRuleVerifiedFact)
                + " temp=" + CountOf(ShadowDifferenceKind.TemporarilyUncomparable)
                + " approved=" + CountOf(ShadowDifferenceKind.ApprovedDifference)
                + " firstUnexpected="
                + (FirstUnexpectedDifference == null ? "<none>" : FirstUnexpectedDifference.ToString())
                + " firstInfra=" + (FirstInfrastructureDifferenceField ?? "<none>")
                + " claim=" + EquivalenceClaim;
        }
    }
}
