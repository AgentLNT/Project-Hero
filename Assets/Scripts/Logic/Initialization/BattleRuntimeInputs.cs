using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Initialization
{
    /// <summary>
    /// 战斗初始化的显式运行时输入（主方案 2.3）。
    ///
    /// 只有两类东西属于"运行时输入"：
    /// <list type="number">
    /// <item>本场 RNG 的初始种子（<see cref="InitialRngSeed"/>，0 合法，但必须显式给出）。</item>
    /// <item>本场需要的局外资源（<see cref="InitialMetaResource"/>——并发行动费用来自
    /// <c>ConcurrentActionDefinition.MetaResourceCost</c>，这里的数值是"开局持有量"）。</item>
    /// </list>
    /// 它们都<strong>不得</strong>由外部 Singleton、命令载荷或调用方参数在战斗中改写。
    ///
    /// 单位的初始生命、阵营与初始位置/朝向来自已验证的
    /// <c>UnitDefinition</c> 与 <c>EncounterUnitSlot</c>；它们的产物是
    /// <see cref="BattleInitializer"/> 构造的 <c>UnitInitialSnapshot</c>（权威初始快照），
    /// 不在这里重复声明。
    ///
    /// <strong>开局肾上腺素（任务 08 裁定 8 修订，2026-xx）</strong>：该快照里的
    /// <c>AvailableAdrenaline</c> 与 <c>AdrenalineCycleId</c> <strong>恒为 0</strong>，
    /// 且<strong>没有</strong>任何定义/Encounter 入参能给出非零开局值 —— <c>UnitDefinition</c>
    /// 与 <c>EncounterUnitSlot</c> 都不含肾上腺素字段。这不是"缺省补洞"：
    /// <list type="bullet">
    /// <item>主方案 3.1.1 的单位资源就是 <c>public int AvailableAdrenaline = 0;</c>（默认 0）；</item>
    /// <item>00 号规则 / <c>02-程序集边界与纯数据.md</c> 第 9 条要求的措辞是
    /// "在 Logic <strong>单位定义或初始快照</strong>中提供 <c>AvailableAdrenaline</c> 与
    /// <c>AdrenalineCycleId</c>"——本类型的 <c>UnitInitialSnapshot</c> 就是该"初始快照"；</item>
    /// <item>旧实现同样从 0 开局（<c>CombatUnit.CurrentAdrenaline = 0f</c>）。</item>
    /// </list>
    /// 运行时唯一写入通道仍是任务 07 的 <c>AdrenalineLedger</c>：额度只经
    /// <c>AdrenalineLedgerRegistry.ApplyTickEndAccrual</c>（真实造成/承受伤害 + 成功反应奖励）获得，
    /// 并在该单位<strong>自己的窗口打开时</strong>先递增个人周期、再清零 Available。
    /// 因此"开局即可用 Block/Dodge"不在本版语义内：反应额度必须先通过战斗挣得。
    /// </summary>
    public sealed record BattleRuntimeInputs(ulong InitialRngSeed, int InitialMetaResource)
    {
        public const string META_RESOURCE_NEGATIVE = Combat.DefinitionCodes.META_RESOURCE_NEGATIVE;

        public string Validate() => InitialMetaResource < 0 ? META_RESOURCE_NEGATIVE : null;
    }

    /// <summary>
    /// <see cref="BattleInitializer"/> 的结果：逻辑世界的最小初始事实。
    /// 全部集合均为只读、稳定顺序，可直接进入规范化快照（任务 03）。
    /// </summary>
    public sealed class BattleInitializationResult
    {
        public BattleInitializationResult(
            EncounterDefinitionId encounterId,
            IReadOnlyList<UnitInitialSnapshot> initialUnits,
            IReadOnlyDictionary<EncounterSlotId, UnitId> slotToUnitId,
            IReadOnlyDictionary<EncounterSlotId, FactionId> slotToFaction,
            IReadOnlyDictionary<ControllerId, IReadOnlyList<UnitId>> controllerToUnitIds,
            IFactionRelationResolver factionResolver,
            BattleRuntimeInputs runtimeInputs,
            LogicIdGenerator idGenerator)
        {
            EncounterId = encounterId;
            InitialUnits = initialUnits;
            SlotToUnitId = slotToUnitId;
            SlotToFaction = slotToFaction;
            ControllerToUnitIds = controllerToUnitIds;
            FactionResolver = factionResolver;
            RuntimeInputs = runtimeInputs;
            IdGenerator = idGenerator;
        }

        public EncounterDefinitionId EncounterId { get; }

        /// <summary>按 UnitId 升序排列的初始单位快照（与槽位 Ordinal 顺序一致）。</summary>
        public IReadOnlyList<UnitInitialSnapshot> InitialUnits { get; }

        /// <summary>EncounterSlotId → UnitId（View 绑定用；不得由场景手填）。</summary>
        public IReadOnlyDictionary<EncounterSlotId, UnitId> SlotToUnitId { get; }

        /// <summary>EncounterSlotId → FactionId（原样复制自槽位，创建后不可变）。</summary>
        public IReadOnlyDictionary<EncounterSlotId, FactionId> SlotToFaction { get; }

        /// <summary>ControllerId → UnitId 集合（只读；不含 System 来源）。</summary>
        public IReadOnlyDictionary<ControllerId, IReadOnlyList<UnitId>> ControllerToUnitIds { get; }

        /// <summary>不可变阵营关系解析器（Self / SameFaction / 跨阵营矩阵）。</summary>
        public IFactionRelationResolver FactionResolver { get; }

        public BattleRuntimeInputs RuntimeInputs { get; }

        /// <summary>
        /// 本场 ID 生成器。所有 <c>_next...</c> 计数器从 1 开始，首次分配前全部等于 1；
        /// 任务 03 必须把它们的当前值写入规范化快照与哈希。
        /// </summary>
        public LogicIdGenerator IdGenerator { get; }

        /// <summary>首个将被分配的 UnitId（初始分配完成后即 N+1）。</summary>
        public long NextUnitIdValue => InitialUnits.Count + 1;
    }
}
