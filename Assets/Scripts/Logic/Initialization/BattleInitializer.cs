using System;
using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Initialization
{
    /// <summary>
    /// 纯数据战斗初始化入口（主方案 2.3 / 任务包「必须产出」6）。
    ///
    /// 输入只有三样：已验证的 <see cref="BattleDefinition"/>、Encounter ID、显式运行时输入。
    /// 它<strong>绝不</strong>读取 GameObject 名、场景注册顺序、<c>IsPlayerControlled</c>、
    /// <c>GetInstanceID()</c>、运行时随机 GUID 或外部 Singleton。
    ///
    /// 确定性契约：
    /// <list type="bullet">
    /// <item>按 <see cref="EncounterSlotId"/> 的 <see cref="StringComparer.Ordinal"/> 顺序分配
    /// <c>UnitId(1)</c>、<c>UnitId(2)</c>……；相同定义 + 相同输入必得相同分配。</item>
    /// <item>槽位 FactionId 原样复制进运行时单位；首版创建后不可变。</item>
    /// <item>控制关系来自已验证定义中的 <c>ControllerBinding</c>，不是命令。</item>
    /// </list>
    /// </summary>
    public static class BattleInitializer
    {
        public const string INITIALIZE_ENCOUNTER_NOT_FOUND = DefinitionCodes.ENCOUNTER_NOT_FOUND;
        public const string INITIALIZE_DEFINITION_INVALID = "INITIALIZE_DEFINITION_INVALID";
        public const string INITIALIZE_SLOT_ORDER_INVALID = "INITIALIZE_SLOT_ORDER_INVALID";
        public const string INITIALIZE_RUNTIME_INPUT_INVALID = "INITIALIZE_RUNTIME_INPUT_INVALID";

        /// <summary>
        /// 从定义 + Encounter + 运行时输入构建初始逻辑世界。
        /// 定义或 Encounter 无法解析时以稳定的 <see cref="LogicDefinitionException"/> 拒绝，
        /// 不做任何"缺省补洞"。
        /// </summary>
        public static BattleInitializationResult BuildInitialState(
            BattleDefinition definition,
            EncounterDefinitionId encounterId,
            BattleRuntimeInputs runtimeInputs)
        {
            if (definition == null)
                throw new LogicDefinitionException(INITIALIZE_DEFINITION_INVALID, "definition is null");
            if (runtimeInputs == null)
                throw new LogicDefinitionException(INITIALIZE_RUNTIME_INPUT_INVALID, "runtimeInputs is null");

            string inputError = runtimeInputs.Validate();
            if (inputError != null)
                throw new LogicDefinitionException(inputError, runtimeInputs.ToString());

            var encounter = definition.FindEncounter(encounterId);
            if (encounter == null)
                throw new LogicDefinitionException(INITIALIZE_ENCOUNTER_NOT_FOUND, encounterId.Value);

            if (encounter.Slots == null || encounter.Slots.Count == 0)
                throw new LogicDefinitionException(DefinitionCodes.ENCOUNTER_EMPTY, encounterId.Value);

            string slotError = EncounterSlotValidation.ValidateSlots(encounter.Slots);
            if (slotError != null)
                throw new LogicDefinitionException(slotError, encounterId.Value);

            // 1. 唯一权威顺序：SlotId 的 Ordinal 升序。加载顺序不得影响结果。
            var orderedSlots = EncounterSlotOrdering.OrderBySlotIdOrdinal(encounter.Slots);
            for (int i = 1; i < orderedSlots.Count; i++)
            {
                if (StringComparer.Ordinal.Compare(
                        orderedSlots[i - 1].SlotId.Value, orderedSlots[i].SlotId.Value) >= 0)
                    throw new LogicDefinitionException(INITIALIZE_SLOT_ORDER_INVALID,
                        orderedSlots[i].SlotId.Value);
            }

            var idGenerator = new LogicIdGenerator();

            var snapshots = new List<UnitInitialSnapshot>(orderedSlots.Count);
            var slotToUnitId = new Dictionary<EncounterSlotId, UnitId>();
            var slotToFaction = new Dictionary<EncounterSlotId, FactionId>();
            var unitToFaction = new Dictionary<UnitId, FactionId>();

            foreach (var slot in orderedSlots)
            {
                var unitDefinition = definition.FindUnit(slot.DefinitionId);
                if (unitDefinition == null)
                    throw new LogicDefinitionException(DefinitionCodes.DEFINITION_REFERENCE_DANGLING,
                        "unit:" + slot.DefinitionId.Value);

                if (!definition.FactionModel.ContainsFaction(slot.FactionId))
                    throw new LogicDefinitionException(DefinitionCodes.ENCOUNTER_SLOT_FACTION_UNKNOWN,
                        slot.FactionId.Value);
                if (encounter.GridBoundary == null || !encounter.GridBoundary.Contains(slot.InitialPosition))
                    throw new LogicDefinitionException(DefinitionCodes.ENCOUNTER_SLOT_OUT_OF_BOUNDARY,
                        slot.SlotId.Value);

                UnitId unitId = idGenerator.NextUnitId();

                snapshots.Add(new UnitInitialSnapshot(
                    slot.DefinitionId,
                    slot.FactionId,
                    slot.InitialPosition,
                    slot.InitialFacing,
                    unitDefinition.InitialHealth,
                    AvailableAdrenaline: 0,
                    AdrenalineCycleId: 0));

                slotToUnitId[slot.SlotId] = unitId;
                slotToFaction[slot.SlotId] = slot.FactionId;
                unitToFaction[unitId] = slot.FactionId;
            }

            // 2. 不可变关系解析器（唯一关系查询入口）。
            var resolver = new FactionRelationResolver(definition.FactionModel, unitToFaction);

            // 3. ControllerId → UnitId 集合：来自已验证定义，独立于阵营与胜负。
            var controllerToUnitIds = new Dictionary<ControllerId, IReadOnlyList<UnitId>>();
            if (encounter.Controllers != null)
            {
                foreach (var binding in encounter.Controllers)
                {
                    var units = new List<UnitId>();
                    if (binding.ControlledSlots != null)
                    {
                        foreach (var slotId in binding.ControlledSlots)
                        {
                            if (!slotToUnitId.TryGetValue(slotId, out var unitId))
                                throw new LogicDefinitionException(DefinitionCodes.CONTROLLER_SLOT_DANGLING,
                                    binding.ControllerId.Value + "|" + slotId.Value);
                            units.Add(unitId);
                        }
                    }
                    units.Sort((a, b) => a.Value.CompareTo(b.Value));
                    controllerToUnitIds[binding.ControllerId] = units;
                }
            }

            return new BattleInitializationResult(
                encounterId,
                snapshots,
                slotToUnitId,
                slotToFaction,
                controllerToUnitIds,
                resolver,
                runtimeInputs,
                idGenerator);
        }
    }
}
