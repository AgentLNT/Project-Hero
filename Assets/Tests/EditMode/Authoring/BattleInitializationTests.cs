using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 02B 必需测试（第三批）：纯数据战斗初始化的确定性、槽位顺序、控制映射与阵营复制。
    /// </summary>
    public class BattleInitializationTests
    {
        private static BattleInitializationResult Initialize()
            => BattleInitializer.BuildInitialState(
                BattleDefinitionFixture.Definition,
                new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId),
                BattleDefinitionFixture.RuntimeInputs);

        [Test]
        public void EncounterInitializationUsesOrdinalSlotOrder()
        {
            var encounter = BattleDefinitionFixture.MainEncounter;
            var result = Initialize();

            // 唯一权威顺序 = SlotId 的 StringComparer.Ordinal 升序。
            var orderedSlots = encounter.Slots
                .OrderBy(s => s.SlotId.Value, System.StringComparer.Ordinal)
                .ToArray();

            Assert.That(result.InitialUnits.Count, Is.EqualTo(orderedSlots.Length));

            for (int i = 0; i < orderedSlots.Length; i++)
            {
                var unitId = result.SlotToUnitId[orderedSlots[i].SlotId];
                Assert.That(unitId.Value, Is.EqualTo(i + 1), $"UnitId({i + 1}) 必须分配给第 {i} 个槽位");
                Assert.That(result.InitialUnits[i].DefinitionId, Is.EqualTo(orderedSlots[i].DefinitionId));
                Assert.That(result.InitialUnits[i].InitialPosition, Is.EqualTo(orderedSlots[i].InitialPosition));
                Assert.That(result.InitialUnits[i].InitialFacing, Is.EqualTo(orderedSlots[i].InitialFacing));
            }

            // 主战斗场景的两个槽位：enemy < hero（Ordinal），因此 UnitId(1) = enemy。
            Assert.That(orderedSlots[0].SlotId.Value, Is.EqualTo(LegacyIdMigrationManifest.SlotEnemy));
            Assert.That(result.SlotToUnitId[new EncounterSlotId(LegacyIdMigrationManifest.SlotEnemy)].Value,
                Is.EqualTo(1));
            Assert.That(result.SlotToUnitId[new EncounterSlotId(LegacyIdMigrationManifest.SlotHero)].Value,
                Is.EqualTo(2));

            // 下一个 UnitId 计数器的值必须落在 N+1（进入任务 03 的规范化快照）。
            Assert.That(result.NextUnitIdValue, Is.EqualTo(orderedSlots.Length + 1));

            // 生成器实例本身从 1 开始、单调递增、不复用。
            Assert.That(result.IdGenerator.NextUnitId().Value, Is.EqualTo(orderedSlots.Length + 1));
        }

        [Test]
        public void EncounterInitializationIgnoresSceneRegistrationOrder()
        {
            var definition = BattleDefinitionFixture.Definition;
            var encounter = BattleDefinitionFixture.MainEncounter;
            var inputs = BattleDefinitionFixture.RuntimeInputs;

            // 把槽位列表整体倒序后重建 Encounter：结果必须逐字段一致。
            var reversedEncounter = encounter with
            {
                Slots = encounter.Slots.Reverse().ToList(),
                Controllers = encounter.Controllers.Reverse().ToList()
            };
            var reversedDefinition = definition with
            {
                Encounters = new List<EncounterDefinition> { reversedEncounter }
            };

            var baseline = Initialize();
            var reversed = BattleInitializer.BuildInitialState(
                reversedDefinition, encounter.EncounterId, inputs);

            Assert.That(reversed.InitialUnits.Count, Is.EqualTo(baseline.InitialUnits.Count));
            for (int i = 0; i < baseline.InitialUnits.Count; i++)
            {
                Assert.That(reversed.InitialUnits[i].DefinitionId, Is.EqualTo(baseline.InitialUnits[i].DefinitionId));
                Assert.That(reversed.InitialUnits[i].FactionId, Is.EqualTo(baseline.InitialUnits[i].FactionId));
                Assert.That(reversed.InitialUnits[i].InitialPosition, Is.EqualTo(baseline.InitialUnits[i].InitialPosition));
            }

            foreach (var slot in encounter.Slots)
            {
                Assert.That(reversed.SlotToUnitId[slot.SlotId], Is.EqualTo(baseline.SlotToUnitId[slot.SlotId]),
                    $"槽位 {slot.SlotId.Value} 的 UnitId 不得依赖枚举顺序");
            }

            Assert.That(reversed.NextUnitIdValue, Is.EqualTo(baseline.NextUnitIdValue));

            // 初始化器不读取 GameObject 名 / 注册顺序 / GetInstanceID / 随机 GUID：
            // 公开 API 只接受 (BattleDefinition, EncounterDefinitionId, BattleRuntimeInputs)。
            var method = typeof(BattleInitializer).GetMethod("BuildInitialState");
            Assert.That(method, Is.Not.Null);
            var parameters = method.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(3));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(BattleDefinition)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(EncounterDefinitionId)));
            Assert.That(parameters[2].ParameterType, Is.EqualTo(typeof(BattleRuntimeInputs)));

            // 一次性分配后计数器推进到 N+1（单场单调递增、不复用）。
            var fresh = new LogicIdGenerator();
            Assert.That(fresh.NextUnitId().Value, Is.EqualTo(1));
            Assert.That(fresh.NextUnitId().Value, Is.EqualTo(2));
        }

        [Test]
        public void ControllerBindingsMapSlotsToDeterministicUnitIds()
        {
            var result = Initialize();
            var encounter = BattleDefinitionFixture.MainEncounter;

            Assert.That(result.ControllerToUnitIds.Count, Is.EqualTo(encounter.Controllers.Count));

            foreach (var binding in encounter.Controllers)
            {
                Assert.That(result.ControllerToUnitIds.ContainsKey(binding.ControllerId), Is.True,
                    binding.ControllerId.Value);

                var expected = binding.ControlledSlots
                    .Select(slotId => result.SlotToUnitId[slotId].Value)
                    .OrderBy(v => v)
                    .ToArray();
                var actual = result.ControllerToUnitIds[binding.ControllerId]
                    .Select(u => u.Value)
                    .ToArray();

                Assert.That(actual, Is.EqualTo(expected), binding.ControllerId.Value);
                Assert.That(actual, Is.Ordered, "控制映射内的 UnitId 必须升序");
            }

            // 控制映射不得包含 System 来源（构建期已拒绝）。
            Assert.That(encounter.Controllers.Any(c => c.SourceKind == CommandSourceKind.System), Is.False);
        }

        [Test]
        public void EncounterSlotsMapFactionIdsToRuntimeUnits()
        {
            var result = Initialize();
            var encounter = BattleDefinitionFixture.MainEncounter;

            Assert.That(result.SlotToFaction.Count, Is.EqualTo(encounter.Slots.Count));

            foreach (var slot in encounter.Slots)
            {
                // 槽位阵营原样复制进运行时单位。
                Assert.That(result.SlotToFaction[slot.SlotId], Is.EqualTo(slot.FactionId), slot.SlotId.Value);

                var unitId = result.SlotToUnitId[slot.SlotId];
                Assert.That(result.FactionResolver.Classify(unitId, unitId), Is.EqualTo(UnitRelation.Self));

                var snapshot = result.InitialUnits.Single(u => u.DefinitionId == slot.DefinitionId);
                Assert.That(snapshot.FactionId, Is.EqualTo(slot.FactionId), slot.SlotId.Value);
            }

            // 关系解析器对两个运行时单位给出 Hostile（hero vs monster）。
            var heroSlot = new EncounterSlotId(LegacyIdMigrationManifest.SlotHero);
            var enemySlot = new EncounterSlotId(LegacyIdMigrationManifest.SlotEnemy);
            var heroUnit = result.SlotToUnitId[heroSlot];
            var enemyUnit = result.SlotToUnitId[enemySlot];

            Assert.That(result.FactionResolver.Classify(heroUnit, enemyUnit), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(result.FactionResolver.Classify(enemyUnit, heroUnit), Is.EqualTo(UnitRelation.Hostile));

            // 未知槽位必须显式失败（不返回默认 UnitId）。
            Assert.That(result.SlotToUnitId.ContainsKey(new EncounterSlotId("slot.unknown")), Is.False);
        }

        [Test]
        public void FactionMappingDoesNotDependOnControllerKind()
        {
            var definition = BattleDefinitionFixture.Definition;
            var encounter = BattleDefinitionFixture.MainEncounter;

            // 把两个 Controller 的来源与控制范围整体对调（Player 控 enemy、Ai 控 hero）：
            // 槽位阵营与运行时单位阵营必须完全不变。
            var swappedControllers = new List<ControllerBinding>
            {
                new ControllerBinding(new ControllerId(LegacyIdMigrationManifest.ControllerPlayer),
                    CommandSourceKind.Player,
                    new List<EncounterSlotId> { new EncounterSlotId(LegacyIdMigrationManifest.SlotEnemy) }),
                new ControllerBinding(new ControllerId(LegacyIdMigrationManifest.ControllerEnemyAi),
                    CommandSourceKind.Ai,
                    new List<EncounterSlotId> { new EncounterSlotId(LegacyIdMigrationManifest.SlotHero) })
            };

            var swappedEncounter = encounter with { Controllers = swappedControllers };
            var swappedDefinition = definition with
            {
                Encounters = new List<EncounterDefinition> { swappedEncounter }
            };

            var baseline = Initialize();
            var swapped = BattleInitializer.BuildInitialState(
                swappedDefinition, encounter.EncounterId, BattleDefinitionFixture.RuntimeInputs);

            foreach (var slot in encounter.Slots)
            {
                Assert.That(swapped.SlotToFaction[slot.SlotId], Is.EqualTo(baseline.SlotToFaction[slot.SlotId]),
                    "阵营只来自槽位，与 Controller 类型无关");
                Assert.That(swapped.SlotToUnitId[slot.SlotId], Is.EqualTo(baseline.SlotToUnitId[slot.SlotId]),
                    "UnitId 分配只看 SlotId 的 Ordinal 顺序");
            }

            // 控制范围确实跟着绑定走了（控制权与阵营正交）。
            var heroUnit = baseline.SlotToUnitId[new EncounterSlotId(LegacyIdMigrationManifest.SlotHero)].Value;
            var playerUnits = swapped.ControllerToUnitIds[
                new ControllerId(LegacyIdMigrationManifest.ControllerPlayer)].ToArray();
            Assert.That(playerUnits.Length, Is.EqualTo(1), "Player 仍然恰好控制一个槽位");
            Assert.That(playerUnits[0].Value, Is.Not.EqualTo(heroUnit),
                "控制范围变化不得影响阵营与胜负分组");        }

        [Test]
        public void RepeatedInitializationIsByteIdenticalAcrossOneHundredRuns()
        {
            var definition = BattleDefinitionFixture.Definition;
            var encounterId = new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId);
            var inputs = BattleDefinitionFixture.RuntimeInputs;

            var baseline = BattleInitializer.BuildInitialState(definition, encounterId, inputs);
            string baselineSignature = Signature(baseline);

            for (int run = 2; run <= 100; run++)
            {
                var result = BattleInitializer.BuildInitialState(definition, encounterId, inputs);
                Assert.That(Signature(result), Is.EqualTo(baselineSignature), $"第 {run} 次初始化必须完全一致");
            }
        }

        [Test]
        public void InitializationRejectsUnknownEncounterAndInvalidRuntimeInputs()
        {
            var definition = BattleDefinitionFixture.Definition;

            var exEncounter = Assert.Throws<LogicDefinitionException>(() =>
                BattleInitializer.BuildInitialState(
                    definition, new EncounterDefinitionId("encounter.does_not_exist"),
                    BattleDefinitionFixture.RuntimeInputs));
            Assert.That(exEncounter.ErrorCode, Is.EqualTo(BattleInitializer.INITIALIZE_ENCOUNTER_NOT_FOUND));

            var exInputs = Assert.Throws<LogicDefinitionException>(() =>
                BattleInitializer.BuildInitialState(
                    definition,
                    new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId),
                    new BattleRuntimeInputs(1UL, -1)));
            Assert.That(exInputs.ErrorCode, Is.EqualTo(DefinitionCodes.META_RESOURCE_NEGATIVE));

            var exNull = Assert.Throws<LogicDefinitionException>(() =>
                BattleInitializer.BuildInitialState(definition,
                    new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId), null));
            Assert.That(exNull.ErrorCode, Is.EqualTo(BattleInitializer.INITIALIZE_RUNTIME_INPUT_INVALID));
        }

        private static string Signature(BattleInitializationResult result)
        {
            // 读取"下一个 UnitId"必须无副作用：结果里的生成器是真实对象，
            // 这里用初始值 + 已分配数量推导，避免测试本身推进计数器。
            var parts = new List<string>
            {
                result.EncounterId.Value,
                result.RuntimeInputs.InitialRngSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.RuntimeInputs.InitialMetaResource.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "next_unit_id=" + result.NextUnitIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };

            foreach (var unit in result.InitialUnits)
            {
                parts.Add(string.Join("|",
                    unit.DefinitionId.Value, unit.FactionId.Value,
                    unit.InitialPosition.X + "," + unit.InitialPosition.Y,
                    ((int)unit.InitialFacing).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    unit.InitialHealth.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    unit.AvailableAdrenaline.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    unit.AdrenalineCycleId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            foreach (var slot in result.SlotToUnitId.OrderBy(p => p.Key.Value, System.StringComparer.Ordinal))
                parts.Add("slot=" + slot.Key.Value + "->" + slot.Value.Value);

            foreach (var slot in result.SlotToFaction.OrderBy(p => p.Key.Value, System.StringComparer.Ordinal))
                parts.Add("faction=" + slot.Key.Value + "->" + slot.Value.Value);

            foreach (var controller in result.ControllerToUnitIds.OrderBy(p => p.Key.Value, System.StringComparer.Ordinal))
                parts.Add("controller=" + controller.Key.Value + "->" +
                          string.Join(",", controller.Value.Select(u => u.Value)));

            // 关系解析器语义也纳入签名。
            foreach (var source in result.InitialUnits.Select((_, i) => new UnitId(i + 1)))
            {
                foreach (var target in result.InitialUnits.Select((_, i) => new UnitId(i + 1)))
                {
                    parts.Add("relation=" + source.Value + "->" + target.Value + ":" +
                              result.FactionResolver.Classify(source, target));
                }
            }

            return string.Join(";", parts);
        }
    }
}
