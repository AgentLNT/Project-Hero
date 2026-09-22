using System.Collections.Generic;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Encounter
{
    /// <summary>
    /// 固定出场槽位纯数据（主方案 2.2.3 / 00 号规则 31）。
    /// 携带稳定且首版不可变的 FactionId；SlotId 非空且唯一；
    /// 战斗初始化按 <see cref="EncounterSlotOrdering"/> 的 Ordinal 顺序分配 UnitId。
    /// </summary>
    public sealed record EncounterUnitSlot(
        EncounterSlotId SlotId,
        UnitDefinitionId DefinitionId,
        FactionId FactionId,
        GridPoint InitialPosition,
        GridDirection InitialFacing);

    public static class EncounterSlotCodes
    {
        public const string ENCOUNTER_SLOT_ID_EMPTY = "ENCOUNTER_SLOT_ID_EMPTY";
        public const string ENCOUNTER_SLOT_ID_INVALID = "ENCOUNTER_SLOT_ID_INVALID";
        public const string ENCOUNTER_SLOT_ID_DUPLICATE = "ENCOUNTER_SLOT_ID_DUPLICATE";
        public const string ENCOUNTER_SLOT_FACTION_MISSING = "ENCOUNTER_SLOT_FACTION_MISSING";
        public const string ENCOUNTER_SLOT_DEFINITION_MISSING = "ENCOUNTER_SLOT_DEFINITION_MISSING";
    }

    public static class EncounterSlotValidation
    {
        /// <summary>单槽位校验：SlotId 非空合法、FactionId 不可空、DefinitionId 不可空。</summary>
        public static string ValidateSlot(EncounterUnitSlot slot)
        {
            string idError = DefinitionIdValidation.ValidateFormat(slot.SlotId.Value);
            if (idError == DefinitionIdValidation.ID_EMPTY) return EncounterSlotCodes.ENCOUNTER_SLOT_ID_EMPTY;
            if (idError != null) return EncounterSlotCodes.ENCOUNTER_SLOT_ID_INVALID;
            if (DefinitionIdValidation.ValidateFormat(slot.FactionId.Value) != null)
                return EncounterSlotCodes.ENCOUNTER_SLOT_FACTION_MISSING;
            if (DefinitionIdValidation.ValidateFormat(slot.DefinitionId.Value) != null)
                return EncounterSlotCodes.ENCOUNTER_SLOT_DEFINITION_MISSING;
            return null;
        }

        /// <summary>槽位集合校验：逐个校验 + SlotId 在集合内唯一（Ordinal 比较）。</summary>
        public static string ValidateSlots(IReadOnlyList<EncounterUnitSlot> slots)
        {
            if (slots == null) return EncounterSlotCodes.ENCOUNTER_SLOT_ID_EMPTY;
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var slot in slots)
            {
                string error = ValidateSlot(slot);
                if (error != null) return error;
                if (!seen.Add(slot.SlotId.Value)) return EncounterSlotCodes.ENCOUNTER_SLOT_ID_DUPLICATE;
            }
            return null;
        }
    }

    /// <summary>
    /// Encounter 槽位排序契约（主方案 2.2.3）：按 SlotId 使用 StringComparer.Ordinal 排序，
    /// 依次分配 UnitId(1)、UnitId(2)……。排序不得依赖加载顺序或文化敏感比较。
    /// </summary>
    public static class EncounterSlotOrdering
    {
        public static readonly IComparer<EncounterUnitSlot> OrdinalSlotIdComparer =
            System.Collections.Generic.Comparer<EncounterUnitSlot>.Create(
                (a, b) => System.StringComparer.Ordinal.Compare(a.SlotId.Value, b.SlotId.Value));

        public static List<EncounterUnitSlot> OrderBySlotIdOrdinal(IEnumerable<EncounterUnitSlot> slots)
        {
            var list = new List<EncounterUnitSlot>(slots);
            list.Sort(OrdinalSlotIdComparer);
            return list;
        }
    }
}
