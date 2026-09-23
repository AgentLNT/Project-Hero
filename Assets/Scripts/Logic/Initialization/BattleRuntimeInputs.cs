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
    /// 单位的初始生命、阵营、初始肾上腺素与周期 ID 全部来自已验证的
    /// <c>UnitDefinition</c> 与 <c>EncounterUnitSlot</c>，不在这里重复声明。
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
