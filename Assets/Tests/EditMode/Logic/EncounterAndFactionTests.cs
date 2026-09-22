using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>Encounter 槽位与阵营/关系稳定编码（主方案 2.2.3 / 2.3 / 00 号规则 30/31）。</summary>
    public class EncounterAndFactionTests
    {
        private static EncounterUnitSlot MakeSlot(string slotId, string factionId = "hero")
            => new EncounterUnitSlot(
                new EncounterSlotId(slotId),
                new UnitDefinitionId("unit.test"),
                new FactionId(factionId),
                new GridPoint(0, 0),
                GridDirection.East);

        [Test]
        public void EncounterSlotOrderingUsesStringComparerOrdinal()
        {
            // 故意混入大小写与数字：文化敏感/忽略大小写比较会给出不同顺序，Ordinal 必须精确。
            var slots = new[]
            {
                MakeSlot("slot.b"), MakeSlot("slot.A"), MakeSlot("slot.10"),
                MakeSlot("slot.2"), MakeSlot("slot.a")
            };

            var ordered = EncounterSlotOrdering.OrderBySlotIdOrdinal(slots);

            string[] expected = System.Array.ConvertAll(
                slots.Select(s => s.SlotId.Value).OrderBy(v => v, System.StringComparer.Ordinal).ToArray(),
                v => v);
            Assert.That(ordered.Select(s => s.SlotId.Value).ToArray(),
                Is.EqualTo(expected), "排序必须与 StringComparer.Ordinal 完全一致");
            // 显式期望：'.' < '1' < '2' < 'A' < 'a' < 'b'（ASCII 序）
            Assert.That(ordered.Select(s => s.SlotId.Value).ToArray(),
                Is.EqualTo(new[] { "slot.10", "slot.2", "slot.A", "slot.a", "slot.b" }));

            // 比较器本体即 Ordinal 语义。
            Assert.That(EncounterSlotOrdering.OrdinalSlotIdComparer.Compare(MakeSlot("slot.a"), MakeSlot("slot.b")),
                Is.LessThan(0));
            Assert.That(EncounterSlotOrdering.OrdinalSlotIdComparer.Compare(MakeSlot("slot.a"), MakeSlot("slot.A")),
                Is.GreaterThan(0), "Ordinal 序：'a' > 'A'");
        }

        [Test]
        public void EncounterRejectsDuplicateSlotId()
        {
            var slots = new[] { MakeSlot("hero"), MakeSlot("monster"), MakeSlot("hero") };
            Assert.That(EncounterSlotValidation.ValidateSlots(slots),
                Is.EqualTo(EncounterSlotCodes.ENCOUNTER_SLOT_ID_DUPLICATE));
            Assert.That(EncounterSlotValidation.ValidateSlots(new[] { MakeSlot("hero"), MakeSlot("monster") }),
                Is.Null);
        }

        [Test]
        public void EncounterSlotRequiresFactionId()
        {
            var missing = new EncounterUnitSlot(
                new EncounterSlotId("hero"),
                new UnitDefinitionId("unit.test"),
                default, // FactionId.Value == null
                new GridPoint(0, 0),
                GridDirection.East);
            Assert.That(EncounterSlotValidation.ValidateSlot(missing),
                Is.EqualTo(EncounterSlotCodes.ENCOUNTER_SLOT_FACTION_MISSING));

            var empty = MakeSlot("hero", factionId: "");
            Assert.That(EncounterSlotValidation.ValidateSlot(empty),
                Is.EqualTo(EncounterSlotCodes.ENCOUNTER_SLOT_FACTION_MISSING));

            var invalid = MakeSlot("hero", factionId: "Hero");
            Assert.That(EncounterSlotValidation.ValidateSlot(invalid),
                Is.EqualTo(EncounterSlotCodes.ENCOUNTER_SLOT_FACTION_MISSING));

            Assert.That(EncounterSlotValidation.ValidateSlot(MakeSlot("hero", "hero")), Is.Null);
            Assert.That(EncounterSlotValidation.ValidateSlot(MakeSlot("monster", "monster")), Is.Null);
        }

        [Test]
        public void TargetRelationMaskUsesStableExplicitBitValues()
        {
            Assert.That((int)TargetRelationMask.None, Is.EqualTo(0));
            Assert.That((int)TargetRelationMask.Self, Is.EqualTo(1 << 0));
            Assert.That((int)TargetRelationMask.Allied, Is.EqualTo(1 << 1));
            Assert.That((int)TargetRelationMask.Neutral, Is.EqualTo(1 << 2));
            Assert.That((int)TargetRelationMask.Hostile, Is.EqualTo(1 << 3));
        }

        [Test]
        public void TargetRelationMaskRejectsEmptyOrUnknownBits()
        {
            Assert.That(TargetRelationMasks.IsValid(TargetRelationMask.None), Is.False, "空掩码非法");
            Assert.That(TargetRelationMasks.IsValid(TargetRelationMask.Hostile), Is.True);
            Assert.That(TargetRelationMasks.IsValid(TargetRelationMask.Hostile | TargetRelationMask.Allied), Is.True);
            Assert.That(TargetRelationMasks.IsValid((TargetRelationMask)(1 << 4)), Is.False, "未定义位非法");
        }

        [Test]
        public void FactionDispositionAndUnitRelationUseStableExplicitValues()
        {
            Assert.That((int)FactionDisposition.Allied, Is.EqualTo(0));
            Assert.That((int)FactionDisposition.Neutral, Is.EqualTo(1));
            Assert.That((int)FactionDisposition.Hostile, Is.EqualTo(2));

            Assert.That((int)UnitRelation.Self, Is.EqualTo(0));
            Assert.That((int)UnitRelation.Allied, Is.EqualTo(1));
            Assert.That((int)UnitRelation.Neutral, Is.EqualTo(2));
            Assert.That((int)UnitRelation.Hostile, Is.EqualTo(3));
        }
    }
}
