using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Physics;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using LogicGridDirection = ProjectHero.Logic.Grid.GridDirection;

namespace ProjectHero.Authoring.Tests
{
    /// <summary>
    /// 02B 必需测试（第二批）：Encounter / 槽位 / 控制器 / 阵营矩阵 / 目标掩码 / 胜负 /
    /// 路径与搜索规则 / 强制位移协议 / 伤害迁移 / 反应闭合。
    /// </summary>
    public class AuthoringInstanceRuleTests
    {
        private static readonly FactionModelDefinition DefaultModel =
            BattleDefinitionBuilder.BuildDefaultFactionModel(new DefinitionErrorCollector());

        /// <summary>
        /// 主战斗定义哈希的冻结锚点（<c>02B-配置迁移记录.md</c> §11 / <c>02B-交接记录.md</c> §7）。
        /// 仅由 <see cref="MainEncounterDefinitionHashMatchesFrozenAnchor"/> 消费；
        /// 改动此常量等于宣布一次<b>有意的</b>玩法定义变更。
        /// </summary>
        private const string FrozenRulesVersion = "battle-def-v1";

        /// <inheritdoc cref="FrozenRulesVersion"/>
        private const string FrozenMainEncounterHash = "d9324383b2622148";

        // ============================================================
        // 1. ControllerBinding
        // ============================================================

        [Test]
        public void DuplicateOrDanglingControllerBindingIsRejected()
        {
            var model = DefaultModel;
            var slots = new List<EncounterUnitSlot>
            {
                new EncounterUnitSlot(new EncounterSlotId("hero"), new UnitDefinitionId("unit.hero.radius_1"),
                    FactionIds.Hero, new ProjectHero.Logic.Grid.GridPoint(-5, -5), LogicGridDirection.East)
            };

            // ① 同一 ControllerId 重复 → CONTROLLER_ID_DUPLICATE。
            var duplicate = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe_duplicate"), Boundary(), slots,
                new List<ControllerBinding>
                {
                    new ControllerBinding(new ControllerId("controller.dup"), CommandSourceKind.Player,
                        new List<EncounterSlotId> { new EncounterSlotId("hero") }),
                    new ControllerBinding(new ControllerId("controller.dup"), CommandSourceKind.Ai,
                        new List<EncounterSlotId> { new EncounterSlotId("hero") })
                },
                Victory());
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateControllerBindings(duplicate, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.CONTROLLER_ID_DUPLICATE));

            // ② 悬空槽位 → CONTROLLER_SLOT_DANGLING。
            var dangling = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe_dangling"), Boundary(), slots,
                new List<ControllerBinding>
                {
                    new ControllerBinding(new ControllerId("controller.dangling"), CommandSourceKind.Player,
                        new List<EncounterSlotId> { new EncounterSlotId("slot.does_not_exist") })
                },
                Victory());
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateControllerBindings(dangling, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.CONTROLLER_SLOT_DANGLING));

            // ③ 同一槽位被两个 Controller 控制 → CONTROLLER_SLOT_AMBIGUOUS。
            var ambiguous = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe_ambiguous"), Boundary(), slots,
                new List<ControllerBinding>
                {
                    new ControllerBinding(new ControllerId("controller.a"), CommandSourceKind.Player,
                        new List<EncounterSlotId> { new EncounterSlotId("hero") }),
                    new ControllerBinding(new ControllerId("controller.b"), CommandSourceKind.Ai,
                        new List<EncounterSlotId> { new EncounterSlotId("hero") })
                },
                Victory());
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateControllerBindings(ambiguous, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.CONTROLLER_SLOT_AMBIGUOUS));

            // ④ 空绑定集合 → CONTROLLER_BINDING_EMPTY。
            var empty = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe_empty"), Boundary(), slots,
                new List<ControllerBinding>
                {
                    new ControllerBinding(new ControllerId("controller.empty"), CommandSourceKind.Ai,
                        new List<EncounterSlotId>())
                },
                Victory());
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateControllerBindings(empty, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.CONTROLLER_BINDING_EMPTY));

            // 真实主战斗 Encounter 的两个绑定必须干净。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateControllerBindings(BattleDefinitionFixture.MainEncounter, errors);
            Assert.That(errors.HasErrors, Is.False, string.Join(",", errors.SortedCodes()));
        }

        [Test]
        public void ExternalSystemControllerBindingIsRejected()
        {
            var slots = new List<EncounterUnitSlot>
            {
                new EncounterUnitSlot(new EncounterSlotId("hero"), new UnitDefinitionId("unit.hero.radius_1"),
                    FactionIds.Hero, new ProjectHero.Logic.Grid.GridPoint(-5, -5), LogicGridDirection.East)
            };

            var systemBound = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe_system"), Boundary(), slots,
                new List<ControllerBinding>
                {
                    new ControllerBinding(new ControllerId("controller.system"), CommandSourceKind.System,
                        new List<EncounterSlotId> { new EncounterSlotId("hero") })
                },
                Victory());

            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateControllerBindings(systemBound, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.CONTROLLER_SYSTEM_SOURCE_REJECTED),
                "外部可伪造的 System 来源必须被拒绝");

            // 真实定义的来源只有 Player / Ai，不含 System。
            var encounter = BattleDefinitionFixture.MainEncounter;
            Assert.That(encounter.Controllers.Any(c => c.SourceKind == CommandSourceKind.System), Is.False);
            Assert.That(encounter.Controllers.Select(c => c.SourceKind).OrderBy(k => (int)k).ToArray(),
                Is.EqualTo(new[] { CommandSourceKind.Player, CommandSourceKind.Ai }));
        }

        // ============================================================
        // 2. Faction 矩阵与目标掩码
        // ============================================================

        [Test]
        public void BuilderRejectsMissingOrUnknownSlotFaction()
        {
            var model = DefaultModel;

            // 槽位引用未知阵营 → ENCOUNTER_SLOT_FACTION_UNKNOWN。
            var unknown = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe_unknown_faction"), Boundary(),
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(new EncounterSlotId("hero"), new UnitDefinitionId("unit.hero.radius_1"),
                        new FactionId("faction.not_defined"), new ProjectHero.Logic.Grid.GridPoint(-5, -5),
                        LogicGridDirection.East)
                },
                new List<ControllerBinding>(),
                Victory());
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateEncounter(unknown, model, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.ENCOUNTER_SLOT_FACTION_UNKNOWN));

            // 槽位缺少阵营（空字符串）→ ENCOUNTER_SLOT_FACTION_MISSING。
            var missing = new EncounterDefinition(
                new EncounterDefinitionId("encounter.probe_missing_faction"), Boundary(),
                new List<EncounterUnitSlot>
                {
                    new EncounterUnitSlot(new EncounterSlotId("hero"), new UnitDefinitionId("unit.hero.radius_1"),
                        default, new ProjectHero.Logic.Grid.GridPoint(-5, -5), LogicGridDirection.East)
                },
                new List<ControllerBinding>(),
                Victory());
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateEncounter(missing, model, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(EncounterSlotCodes.ENCOUNTER_SLOT_FACTION_MISSING));

            // 真实 Encounter 的每个槽位都有显式阵营。
            foreach (var slot in BattleDefinitionFixture.MainEncounter.Slots)
                Assert.That(model.ContainsFaction(slot.FactionId), Is.True, slot.SlotId.Value);
        }

        [Test]
        public void FactionMatrixCanonicalizesEveryUnorderedPair()
        {
            var model = DefaultModel;

            // N = 2 → 恰好 1 条无序对。
            Assert.That(model.Factions.Count, Is.EqualTo(2));
            Assert.That(model.ExpectedRelationCount, Is.EqualTo(1));
            Assert.That(model.Relations.Count, Is.EqualTo(1));

            var relation = model.Relations[0];
            Assert.That(relation.FactionAId, Is.EqualTo(FactionIds.Hero));
            Assert.That(relation.FactionBId, Is.EqualTo(FactionIds.Monster));
            Assert.That(relation.Disposition, Is.EqualTo(FactionDisposition.Hostile));
            Assert.That(string.CompareOrdinal(relation.FactionAId.Value, relation.FactionBId.Value), Is.LessThan(0),
                "FactionAId 必须按 Ordinal 严格小于 FactionBId");

            // N = 3 时 ExpectedRelationCount 必须是 3（首版只用 2，但契约必须成立）。
            var threeFactions = new FactionModelDefinition(
                new List<FactionDefinition>
                {
                    new FactionDefinition(new FactionId("a")),
                    new FactionDefinition(new FactionId("b")),
                    new FactionDefinition(new FactionId("c"))
                },
                new List<FactionRelationDefinition>());
            Assert.That(threeFactions.ExpectedRelationCount, Is.EqualTo(3));

            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateFactionModel(threeFactions.Factions, threeFactions.Relations, errors);
            Assert.That(errors.SortedCodes().Count(c => c == FactionCodes.FACTION_RELATION_MISSING),
                Is.EqualTo(3 + 1), "三条无序对全部缺失，另外报一条数量不符");
        }

        [Test]
        public void FactionMatrixHashIgnoresInputEnumerationOrder()
        {
            // 同一组"内容"以不同枚举顺序装配，规范结果必须逐字一致。
            var forward = FactionModelFrom(
                new[] { FactionIds.Hero, FactionIds.Monster },
                new[]
                {
                    new FactionRelationDefinition(FactionIds.Hero, FactionIds.Monster, FactionDisposition.Hostile)
                });

            var backward = FactionModelFrom(
                new[] { FactionIds.Monster, FactionIds.Hero },
                new[]
                {
                    new FactionRelationDefinition(FactionIds.Hero, FactionIds.Monster, FactionDisposition.Hostile)
                });

            var writerForward = new CanonicalHashWriter();
            forward.WriteHashComponents(writerForward);
            var writerBackward = new CanonicalHashWriter();
            backward.WriteHashComponents(writerBackward);

            Assert.That(writerBackward.ToDigestHex(), Is.EqualTo(writerForward.ToDigestHex()),
                "阵营矩阵的输入枚举顺序不得影响哈希");

            // 与 Builder 的默认模型一致。
            var defaultModel = BattleDefinitionBuilder.BuildDefaultFactionModel(new DefinitionErrorCollector());
            var writerDefault = new CanonicalHashWriter();
            defaultModel.WriteHashComponents(writerDefault);
            Assert.That(writerDefault.ToDigestHex(), Is.EqualTo(writerForward.ToDigestHex()));

            // 反向查询必须命中原生无序对（无序对的规范顺序与查询方向无关）。
            Assert.That(forward.TryGetDisposition(FactionIds.Monster, FactionIds.Hero, out var disposition), Is.True);
            Assert.That(disposition, Is.EqualTo(FactionDisposition.Hostile));
            Assert.That(forward.Relations.Count, Is.EqualTo(1));
            Assert.That(backward.Relations.Count, Is.EqualTo(1));
        }

        /// <summary>按 Builder 的规范化顺序装配阵营模型（与生产路径共用同一装配代码）。</summary>
        private static FactionModelDefinition FactionModelFrom(
            IEnumerable<FactionId> factions, IEnumerable<FactionRelationDefinition> relations)
        {
            var canonicalFactions = BattleDefinitionAssembler.CanonicalizeFactions(
                factions.Select(f => new FactionDefinition(f)));
            var canonicalRelations = BattleDefinitionAssembler.CanonicalizeRelations(relations);
            return new FactionModelDefinition(canonicalFactions, canonicalRelations);
        }

        [Test]
        public void BuilderRejectsMissingDuplicateReverseDuplicateOrDanglingFactionRelation()
        {
            var factions = new List<FactionDefinition>
            {
                new FactionDefinition(new FactionId("a")),
                new FactionDefinition(new FactionId("b"))
            };

            // ① 缺失（空关系列表）。
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateFactionModel(factions, new List<FactionRelationDefinition>(), errors);
            Assert.That(errors.SortedCodes(), Does.Contain(FactionCodes.FACTION_RELATION_MISSING));

            // ② 重复（同一规范对出现两次）。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateFactionModel(factions, new List<FactionRelationDefinition>
            {
                new FactionRelationDefinition(new FactionId("a"), new FactionId("b"), FactionDisposition.Hostile),
                new FactionRelationDefinition(new FactionId("a"), new FactionId("b"), FactionDisposition.Neutral)
            }, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(FactionCodes.FACTION_RELATION_DUPLICATE));

            // ③ 反向重复（B-A）+ 非规范顺序。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateFactionModel(factions, new List<FactionRelationDefinition>
            {
                new FactionRelationDefinition(new FactionId("b"), new FactionId("a"), FactionDisposition.Hostile)
            }, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(FactionCodes.FACTION_RELATION_NOT_CANONICAL));

            // ④ 自关系。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateFactionModel(factions, new List<FactionRelationDefinition>
            {
                new FactionRelationDefinition(new FactionId("a"), new FactionId("a"), FactionDisposition.Hostile)
            }, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(FactionCodes.FACTION_RELATION_NOT_CANONICAL));

            // ⑤ 悬空阵营。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateFactionModel(factions, new List<FactionRelationDefinition>
            {
                new FactionRelationDefinition(new FactionId("a"), new FactionId("zzz"), FactionDisposition.Hostile)
            }, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(FactionCodes.FACTION_RELATION_UNKNOWN_ID));

            // ⑥ 真实定义必须是完备且规范的。
            errors = new DefinitionErrorCollector();
            var model = BattleDefinitionFixture.Definition.FactionModel;
            BattleDefinitionValidator.ValidateFactionModel(model.Factions, model.Relations, errors);
            Assert.That(errors.HasErrors, Is.False, string.Join(",", errors.SortedCodes()));
        }

        [Test]
        public void SameUnitIsSelfAndSameFactionOtherUnitIsAllied()
        {
            var definition = BattleDefinitionFixture.Definition;
            var resolver = StubResolver.Resolve(
                definition,
                new Dictionary<UnitId, FactionId>
                {
                    [new UnitId(1)] = FactionIds.Hero,
                    [new UnitId(2)] = FactionIds.Hero,
                    [new UnitId(3)] = FactionIds.Monster
                });

            Assert.That(resolver.Classify(new UnitId(1), new UnitId(1)), Is.EqualTo(UnitRelation.Self));
            Assert.That(resolver.Classify(new UnitId(1), new UnitId(2)), Is.EqualTo(UnitRelation.Allied),
                "同阵营的不同单位固定 Allied");
            Assert.That(resolver.Classify(new UnitId(2), new UnitId(1)), Is.EqualTo(UnitRelation.Allied),
                "关系是静态对称的");
            Assert.That(resolver.Classify(new UnitId(1), new UnitId(3)), Is.EqualTo(UnitRelation.Hostile));
            Assert.That(resolver.Classify(new UnitId(3), new UnitId(1)), Is.EqualTo(UnitRelation.Hostile));

            // 未知 UnitId 不得退回 Neutral/Hostile，而是显式不变量错误。
            var ex = Assert.Throws<LogicDefinitionException>(() =>
                resolver.Classify(new UnitId(1), new UnitId(99)));
            Assert.That(ex.ErrorCode, Is.EqualTo(FactionCodes.FACTION_RELATION_UNKNOWN_ID));
        }

        [Test]
        public void FactionResolverReturnsConfiguredAlliedNeutralAndHostile()
        {
            // 三阵营模型：a↔b = Allied、a↔c = Neutral、b↔c = Hostile，
            // 逐对验证解析器返回配置值（矩阵是唯一真相）。
            var model = new FactionModelDefinition(
                new List<FactionDefinition>
                {
                    new FactionDefinition(new FactionId("a")),
                    new FactionDefinition(new FactionId("b")),
                    new FactionDefinition(new FactionId("c"))
                },
                new List<FactionRelationDefinition>
                {
                    new FactionRelationDefinition(new FactionId("a"), new FactionId("b"), FactionDisposition.Allied),
                    new FactionRelationDefinition(new FactionId("a"), new FactionId("c"), FactionDisposition.Neutral),
                    new FactionRelationDefinition(new FactionId("b"), new FactionId("c"), FactionDisposition.Hostile)
                });

            var resolver = new FactionRelationResolver(model, new Dictionary<UnitId, FactionId>
            {
                [new UnitId(1)] = new FactionId("a"),
                [new UnitId(2)] = new FactionId("b"),
                [new UnitId(3)] = new FactionId("c")
            });

            Assert.That(resolver.Classify(new UnitId(1), new UnitId(2)), Is.EqualTo(UnitRelation.Allied));
            Assert.That(resolver.Classify(new UnitId(1), new UnitId(3)), Is.EqualTo(UnitRelation.Neutral));
            Assert.That(resolver.Classify(new UnitId(2), new UnitId(3)), Is.EqualTo(UnitRelation.Hostile));

            // Allows 只检查对应位。
            Assert.That(resolver.Allows(TargetRelationMask.Neutral, new UnitId(1), new UnitId(3)), Is.True);
            Assert.That(resolver.Allows(TargetRelationMask.Hostile, new UnitId(1), new UnitId(3)), Is.False);
            Assert.That(resolver.Allows(TargetRelationMask.Self, new UnitId(1), new UnitId(1)), Is.True);
            Assert.That(resolver.Allows(TargetRelationMask.Allied, new UnitId(1), new UnitId(1)), Is.False,
                "Self 不是 Allied");
        }

        [Test]
        public void BuilderRejectsEmptyOrUnknownTargetRelationMaskBits()
        {
            // 掩码为空 / 含未定义位都由 ActionDefinitionValidation 拒绝。
            var spec = MakeAttackSpec(TargetRelationMask.None);
            Assert.That(ActionDefinitionValidation.ValidateAttack(spec),
                Is.EqualTo(ActionDefinitionCodes.ATTACK_TARGET_RELATION_MASK_EMPTY));

            var unknownBits = MakeAttackSpec((TargetRelationMask)(1 << 7));
            Assert.That(ActionDefinitionValidation.ValidateAttack(unknownBits),
                Is.EqualTo(ActionDefinitionCodes.ATTACK_TARGET_RELATION_MASK_INVALID));

            // 真实定义的每个攻击都必须带非空且只含已知位的掩码。
            foreach (var action in BattleDefinitionFixture.Definition.Actions)
            {
                if (!(action.Payload is AttackPayloadSpec attack)) continue;
                Assert.That(TargetRelationMasks.IsValid(attack.AllowedTargetRelations), Is.True, action.ActionSpecId.Value);
                Assert.That(attack.AllowedTargetRelations, Is.EqualTo(TargetRelationMask.Hostile),
                    "01B 拍板 A3：当前 5 个攻击（两库同名同掩码）全部 Hostile-only");
            }
        }

        [Test]
        public void AttackTargetRelationMasksAffectDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;
            var target = definition.Actions.First(a => a.Payload is AttackPayloadSpec);

            var mutated = definition.Actions.Select(a =>
            {
                if (a.ActionSpecId != target.ActionSpecId) return a;
                var payload = (AttackPayloadSpec)a.Payload;
                return new ActionSpec(a.ActionSpecId, a.Type, a.Timing,
                    new AttackPayloadSpec(payload.DamageComponents, payload.ImpactProfileId,
                        payload.ForceMultiplier, payload.TargetPolicy,
                        TargetRelationMask.Hostile | TargetRelationMask.Allied,   // 显式开启友伤
                        payload.MomentumDirectionOffsetSteps, payload.Pattern, payload.Tags),
                    a.AdrenalineCost);
            }).ToList();

            string hash = Rehash(definition, mutated);
            Assert.That(hash, Is.Not.EqualTo(definition.BattleDefinitionHashValue),
                "AllowedTargetRelations 必须参与哈希");
        }

        [Test]
        public void DynamicSpawnFactionComesOnlyFromFixedOrExplicitInheritancePolicy()
        {
            var definition = BattleDefinitionFixture.Definition;
            var policy = definition.DefaultDynamicSpawnPolicy;

            Assert.That(policy, Is.Not.Null);
            Assert.That(policy.Validate(), Is.Null);

            // 只有两种可解析形态：显式继承，或固定阵营。
            if (policy.InheritSourceFaction)
            {
                Assert.That(policy.FixedFactionId.Value, Is.Null.Or.Empty,
                    "继承策略下不得同时携带固定阵营");
            }
            else
            {
                Assert.That(string.IsNullOrEmpty(policy.FixedFactionId.Value), Is.False,
                    "固定策略必须给出非空 FactionId");
            }

            // 固定策略但阵营为空 → 定义级拒绝（不存在"缺省 = Hostile/Neutral"）。
            var invalid = new DynamicSpawnFactionPolicy(false, default);
            Assert.That(invalid.Validate(), Is.EqualTo(DynamicSpawnFactionPolicy.DYNAMIC_SPAWN_POLICY_INVALID));

            // 策略参与哈希。
            var writerA = new CanonicalHashWriter();
            policy.WriteHashComponents(writerA);
            var writerB = new CanonicalHashWriter();
            DynamicSpawnFactionPolicy.Fixed(FactionIds.Monster).WriteHashComponents(writerB);
            Assert.That(writerB.ToDigestHex(), Is.Not.EqualTo(writerA.ToDigestHex()));
        }

        [Test]
        public void BuilderRejectsEmptyOverlappingOrDanglingVictoryFactions()
        {
            var model = DefaultModel;

            // ① 空集合。
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateVictory(
                new VictoryDefinition(new List<FactionId>(), new List<FactionId>(), "v", "d", "draw"),
                model, errors, "probe");
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.VICTORY_FACTION_SET_EMPTY));

            // ② 重叠（同一阵营同时属于 Allied 与 Hostile）。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateVictory(
                new VictoryDefinition(
                    new List<FactionId> { FactionIds.Hero },
                    new List<FactionId> { FactionIds.Hero },
                    "v", "d", "draw"),
                model, errors, "probe");
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.VICTORY_FACTION_SET_OVERLAP));

            // ③ 悬空阵营。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateVictory(
                new VictoryDefinition(
                    new List<FactionId> { new FactionId("faction.ghost") },
                    new List<FactionId> { FactionIds.Monster },
                    "v", "d", "draw"),
                model, errors, "probe");
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.VICTORY_FACTION_SET_DANGLING));

            // ④ 结果码为空。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateVictory(
                new VictoryDefinition(
                    new List<FactionId> { FactionIds.Hero },
                    new List<FactionId> { FactionIds.Monster },
                    "", "d", "draw"),
                model, errors, "probe");
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.VICTORY_RESULT_CODE_MISSING));

            // 真实定义：Allied={hero}、Hostile={monster}、三个结果码齐全。
            var victory = BattleDefinitionFixture.MainEncounter.Victory;
            Assert.That(victory.AlliedFactionIds.Select(f => f.Value).ToArray(), Is.EqualTo(new[] { "hero" }));
            Assert.That(victory.HostileFactionIds.Select(f => f.Value).ToArray(), Is.EqualTo(new[] { "monster" }));
            Assert.That(victory.VictoryResultCode, Is.Not.Empty);
            Assert.That(victory.DefeatResultCode, Is.Not.Empty);
            Assert.That(victory.DrawResultCode, Is.Not.Empty);
        }

        [Test]
        public void BuilderRejectsVictoryGroupsContradictingFactionMatrix()
        {
            // 关系矩阵里 hero↔monster = Allied，但 Victory 要求跨组 Hostile → 冲突。
            var alliedModel = new FactionModelDefinition(
                new List<FactionDefinition>
                {
                    new FactionDefinition(FactionIds.Hero),
                    new FactionDefinition(FactionIds.Monster)
                },
                new List<FactionRelationDefinition>
                {
                    new FactionRelationDefinition(FactionIds.Hero, FactionIds.Monster, FactionDisposition.Allied)
                });

            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateVictory(
                new VictoryDefinition(
                    new List<FactionId> { FactionIds.Hero },
                    new List<FactionId> { FactionIds.Monster },
                    "victory", "defeat", "draw"),
                alliedModel, errors, "probe");
            Assert.That(errors.SortedCodes(), Does.Contain(FactionCodes.VICTORY_FACTION_RELATION_CONFLICT));

            // Allied 组内部必须是 Allied：把 monster 放进 Allied 组但矩阵是 Hostile → 冲突。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateVictory(
                new VictoryDefinition(
                    new List<FactionId> { FactionIds.Hero, FactionIds.Monster },
                    new List<FactionId> { FactionIds.Hero },
                    "victory", "defeat", "draw"),
                DefaultModel, errors, "probe");
            Assert.That(errors.SortedCodes(), Does.Contain(FactionCodes.VICTORY_FACTION_RELATION_CONFLICT));

            // 真实定义必须零冲突。
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateVictory(
                BattleDefinitionFixture.MainEncounter.Victory,
                BattleDefinitionFixture.Definition.FactionModel, errors, "real");
            Assert.That(errors.HasErrors, Is.False, string.Join(",", errors.SortedCodes()));
        }

        [Test]
        public void VictoryDefinitionAndAutomaticDeferralLimitAffectDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;

            // ① 自动延期上限进哈希。
            string baseline = BattleDefinitionHash.Compute(
                definition.RulesVersion, definition.TicksPerSecond, definition.Rules,
                definition.ConcurrentAction, definition.ReactionRules, definition.AdrenalineRules,
                definition.FactionModel, definition.DamageChannels, definition.ImpactProfiles,
                definition.Units, definition.Actions, definition.AttackPatterns, definition.Volumes,
                definition.MovementPatterns, definition.ActionSets, definition.StatusEffects,
                definition.Encounters, definition.DefaultDynamicSpawnPolicy);

            var mutatedRules = definition.Rules with { MaxAutomaticDeferralsPerPlan = 9 };
            string mutatedHash = BattleDefinitionHash.Compute(
                definition.RulesVersion, definition.TicksPerSecond, mutatedRules,
                definition.ConcurrentAction, definition.ReactionRules, definition.AdrenalineRules,
                definition.FactionModel, definition.DamageChannels, definition.ImpactProfiles,
                definition.Units, definition.Actions, definition.AttackPatterns, definition.Volumes,
                definition.MovementPatterns, definition.ActionSets, definition.StatusEffects,
                definition.Encounters, definition.DefaultDynamicSpawnPolicy);

            Assert.That(mutatedHash, Is.Not.EqualTo(baseline), "MaxAutomaticDeferralsPerPlan 必须参与哈希");
            Assert.That(definition.Rules.MaxAutomaticDeferralsPerPlan,
                Is.EqualTo(BattleRules.FrozenMaxAutomaticDeferralsPerPlan));
            Assert.That(definition.Rules.MaxAutomaticDeferralsPerPlan, Is.GreaterThanOrEqualTo(0));
            Assert.That(definition.Rules.Validate(), Is.Null);

            // 负值必须被拒绝。
            Assert.That((definition.Rules with { MaxAutomaticDeferralsPerPlan = -1 }).Validate(),
                Is.EqualTo(BattleRules.MAX_AUTOMATIC_DEFERRALS_INVALID));

            // ② Victory 分组进哈希：改 Hostile 目标组集合必须改变 Encounter 的哈希分量。
            var encounter = BattleDefinitionFixture.MainEncounter;
            var writerOriginal = new CanonicalHashWriter();
            encounter.WriteHashComponents(writerOriginal);

            var mutatedVictory = new EncounterDefinition(
                encounter.EncounterId, encounter.GridBoundary, encounter.Slots, encounter.Controllers,
                new VictoryDefinition(
                    new List<FactionId> { FactionIds.Hero },
                    new List<FactionId> { FactionIds.Monster },
                    "victory.changed", "defeat", "draw"));
            var writerMutated = new CanonicalHashWriter();
            mutatedVictory.WriteHashComponents(writerMutated);
            Assert.That(writerMutated.ToDigestHex(), Is.Not.EqualTo(writerOriginal.ToDigestHex()),
                "Victory 结果码必须参与哈希");
        }

        // ============================================================
        // 3. 路径 / 搜索 / Encounter 边界
        // ============================================================

        [Test]
        public void PathCostRulesPreserveLegacyOneTwoWeights()
        {
            var rules = BattleDefinitionFixture.Definition.Rules.PathCostRules;

            Assert.That(rules.EvenDirectionStepWeightUnits, Is.EqualTo(1));
            Assert.That(rules.OddDirectionStepWeightUnits, Is.EqualTo(2));
            Assert.That(rules.StepWeightUnits(LogicGridDirection.East), Is.EqualTo(1));
            Assert.That(rules.StepWeightUnits(LogicGridDirection.EastNorth), Is.EqualTo(2));
            Assert.That(rules.StepWeightUnits(LogicGridDirection.NorthEast), Is.EqualTo(1));
            Assert.That(rules.StepWeightUnits(LogicGridDirection.EastSouth), Is.EqualTo(2));
            Assert.That(rules.StepWeightUnits(LogicGridDirection.SouthEast), Is.EqualTo(1),
                "SouthEast = 方向索引 10（偶数方向 → 权重 1）");
            Assert.That(rules.StepWeightUnits(LogicGridDirection.South), Is.EqualTo(2),
                "South = 方向索引 9（奇数方向 → 权重 2）");
            Assert.That(rules.ValidateFrozenRules(), Is.Null);

            // 与旧 Pathfinder 的边权逐向对照：偶数方向（0,2,4,…）权重 1，奇数方向权重 2。
            for (int i = 0; i < GridDirectionInfo.DirectionCount; i++)
            {
                var direction = (LogicGridDirection)i;
                int expected = i % 2 == 0 ? 1 : 2;
                Assert.That(rules.StepWeightUnits(direction), Is.EqualTo(expected), direction.ToString());
            }

            // 非冻结取值必须被拒绝（不得悄悄接受 1/3 之类的改值）。
            Assert.That(new PathCostRules(2, 3).ValidateFrozenRules(),
                Is.EqualTo(PathCostRules.PATH_COST_RULE_INVALID));
            Assert.That(new PathCostRules(1, 3).ValidateFrozenRules(),
                Is.EqualTo(PathCostRules.PATH_COST_RULE_INVALID));
            Assert.That(new PathCostRules(0, 2).ValidateFrozenRules(),
                Is.EqualTo(PathCostRules.PATH_COST_WEIGHT_INVALID));
            Assert.That(PathCostRules.IsSupportedHeuristicVersion(PathCostRules.HeuristicVersion), Is.True);
            Assert.That(PathCostRules.IsSupportedHeuristicVersion("other"), Is.False);

            // 启发函数版本进哈希。
            var writer = new CanonicalHashWriter();
            rules.WriteHashComponents(writer);
            Assert.That(writer.ToCanonicalText(), Does.Contain(PathCostRules.HeuristicVersion));
        }

        [Test]
        public void PathSearchRulesUseFrozen4096Node256Weight192EdgeLimits()
        {
            var rules = BattleDefinitionFixture.Definition.Rules.PathSearchRules;

            Assert.That(rules.MaxExpandedNodes, Is.EqualTo(4096));
            Assert.That(rules.MaxPathWeightUnits, Is.EqualTo(256));
            Assert.That(rules.MaxPathEdges, Is.EqualTo(192));
            Assert.That(rules.MaxExpandedNodes, Is.EqualTo(PathSearchRules.FrozenMaxExpandedNodes));
            Assert.That(rules.MaxPathWeightUnits, Is.EqualTo(PathSearchRules.FrozenMaxPathWeightUnits));
            Assert.That(rules.MaxPathEdges, Is.EqualTo(PathSearchRules.FrozenMaxPathEdges));
            Assert.That(rules.Validate(), Is.Null);

            // 不得沿用旧实现的 1000 次迭代或 ±100 坐标窗口。
            Assert.That(rules.MaxExpandedNodes, Is.Not.EqualTo(1000),
                "旧 maxIterations = 1000 不得作为未声明默认值沿用");

            // TicksPerSecond 与规则入口一致。
            Assert.That(BattleDefinitionFixture.Definition.TicksPerSecond, Is.EqualTo(60));
            Assert.That(BattleDefinitionFixture.Definition.TicksPerSecond,
                Is.EqualTo(BattleDefinitionFixture.Definition.Rules.TicksPerSecond));
        }

        [Test]
        public void PathSearchLimitsAllowEqualityAndRejectOnlyGreaterValues()
        {
            var rules = PathSearchRules.FrozenV1;

            Assert.That(rules.ExceedsNodeLimit(4096), Is.False, "恰好等于上限必须合法");
            Assert.That(rules.ExceedsNodeLimit(4097), Is.True);
            Assert.That(rules.ExceedsWeightLimit(256), Is.False);
            Assert.That(rules.ExceedsWeightLimit(257), Is.True);
            Assert.That(rules.ExceedsEdgeLimit(192), Is.False);
            Assert.That(rules.ExceedsEdgeLimit(193), Is.True);

            // 非正上限必须拒绝。
            Assert.That(new PathSearchRules(0, 256, 192).Validate(),
                Is.EqualTo(PathSearchCodes.PATH_SEARCH_RULE_INVALID));
            Assert.That(new PathSearchRules(4096, -1, 192).Validate(),
                Is.EqualTo(PathSearchCodes.PATH_SEARCH_RULE_INVALID));
        }

        [Test]
        public void PathRulesAndEncounterGridBoundaryAffectDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;
            var encounter = BattleDefinitionFixture.MainEncounter;

            // 边界必须是唯一的空间搜索边界：没有独立的 MaxSearchDistance。
            Assert.That(encounter.GridBoundary, Is.Not.Null);
            Assert.That(encounter.GridBoundary.Validate(), Is.Null);
            Assert.That(typeof(PathSearchRules).GetProperty("MaxSearchDistance"), Is.Null,
                "不得定义独立 MaxSearchDistance");
            Assert.That(encounter.GridBoundary.Contains(new ProjectHero.Logic.Grid.GridPoint(5, 5)), Is.True);
            Assert.That(encounter.GridBoundary.Contains(new ProjectHero.Logic.Grid.GridPoint(-5, -5)), Is.True);
            Assert.That(encounter.GridBoundary.Contains(new ProjectHero.Logic.Grid.GridPoint(18, 18)), Is.False);
            Assert.That(encounter.GridBoundary.Contains(new ProjectHero.Logic.Grid.GridPoint(18, 0)), Is.False,
                "X 超出上界不属于边界");

            // 稳定枚举只产生满足顶点奇偶约束（X + Y 为偶数）的合法点，顺序固定（X 升序、Y 升序）。
            var enumerated = encounter.GridBoundary.EnumerateValidPoints();
            Assert.That(enumerated.Count, Is.EqualTo((int)encounter.GridBoundary.ValidPointCount()));
            foreach (var point in enumerated)
            {
                Assert.That(ProjectHero.Logic.Grid.GridPoint.IsValidParity(point.X, point.Y), Is.True,
                    point.ToString());
                Assert.That(encounter.GridBoundary.Contains(point), Is.True, point.ToString());
            }
            for (int i = 1; i < enumerated.Count; i++)
            {
                Assert.That(enumerated[i - 1].CompareTo(enumerated[i]), Is.LessThan(0),
                    "枚举顺序必须是 (X, Y) 升序且无重复");
            }

            // 全部初始占位都在边界内（构建期已校验，这里做独立复核）。
            foreach (var slot in encounter.Slots)
                Assert.That(encounter.GridBoundary.Contains(slot.InitialPosition), Is.True, slot.SlotId.Value);

            // 边界进哈希：改一个分量必须改变 Encounter 的哈希分量。
            var writerOriginal = new CanonicalHashWriter();
            encounter.WriteHashComponents(writerOriginal);

            var mutatedBoundary = encounter with
            {
                GridBoundary = new GridBoundaryDefinition(
                    new ProjectHero.Logic.Grid.GridPoint(-17, -17),
                    new ProjectHero.Logic.Grid.GridPoint(17, 17))
            };
            var writerMutated = new CanonicalHashWriter();
            mutatedBoundary.WriteHashComponents(writerMutated);
            Assert.That(writerMutated.ToDigestHex(), Is.Not.EqualTo(writerOriginal.ToDigestHex()));

            // 退化边界必须被拒绝：某个分量上 Min 不严格小于 Max。
            Assert.That(new GridBoundaryDefinition(
                    new ProjectHero.Logic.Grid.GridPoint(0, 0),
                    new ProjectHero.Logic.Grid.GridPoint(0, 2)).Validate(),
                Is.EqualTo(GridBoundaryDefinition.GRID_BOUNDARY_INVALID));
            Assert.That(new GridBoundaryDefinition(
                    new ProjectHero.Logic.Grid.GridPoint(0, 0),
                    new ProjectHero.Logic.Grid.GridPoint(2, 0)).Validate(),
                Is.EqualTo(GridBoundaryDefinition.GRID_BOUNDARY_INVALID));

            // 包围盒内不含任何合法顶点时同样拒绝（单点边界必然退化）。
            Assert.That(new GridBoundaryDefinition(
                    new ProjectHero.Logic.Grid.GridPoint(2, 0),
                    new ProjectHero.Logic.Grid.GridPoint(2, 0)).Validate(),
                Is.EqualTo(GridBoundaryDefinition.GRID_BOUNDARY_INVALID));
            Assert.That(new GridBoundaryDefinition(
                    new ProjectHero.Logic.Grid.GridPoint(2, 0),
                    new ProjectHero.Logic.Grid.GridPoint(2, 0)).ValidPointCount(), Is.EqualTo(1L));

            // 非退化且含合法顶点的边界（端点奇偶不要求）必须通过。
            Assert.That(new GridBoundaryDefinition(
                    new ProjectHero.Logic.Grid.GridPoint(0, 0),
                    new ProjectHero.Logic.Grid.GridPoint(3, 3)).Validate(), Is.Null);
            Assert.That(new GridBoundaryDefinition(
                    new ProjectHero.Logic.Grid.GridPoint(0, 0),
                    new ProjectHero.Logic.Grid.GridPoint(3, 3)).ValidPointCount(), Is.EqualTo(8L));
        }

        [Test]
        public void BuilderRejectsMoveSpeedAsPathCostCoefficient()
        {
            // 路径成本规则与搜索规则都不接受任何"单位速度"输入：
            // 公开 API 里不得出现 MoveSpeed / Swiftness / UnitDefinition 参数。
            foreach (var type in new[] { typeof(PathCostRules), typeof(PathSearchRules) })
            {
                foreach (var method in type.GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        Assert.That(parameter.Name, Does.Not.Contain("Speed"), $"{type.Name}.{method.Name}");
                        Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(UnitDefinition)),
                            $"{type.Name}.{method.Name}");
                    }
                }
            }

            // 定义里的 MoveSpeed 只出现在单位定义上（动作时序用），不进入路径规则。
            Assert.That(typeof(PathCostRules).GetProperty("MoveSpeed"), Is.Null);
            Assert.That(typeof(PathSearchRules).GetProperty("MoveSpeed"), Is.Null);

            // Builder 的路径规则来自冻结常量，与单位速度无关：
            // 把两个单位的 MoveSpeed 改大不会改变路径规则的哈希分量。
            var definition = BattleDefinitionFixture.Definition;
            var writer = new CanonicalHashWriter();
            definition.Rules.PathCostRules.WriteHashComponents(writer);
            var writerAgain = new CanonicalHashWriter();
            new PathCostRules(1, 2).WriteHashComponents(writerAgain);
            Assert.That(writerAgain.ToDigestHex(), Is.EqualTo(writer.ToDigestHex()));
        }

        // ============================================================
        // 4. 强制位移协议
        // ============================================================

        [Test]
        public void ForcedDisplacementProtocolVersionAndStopReasonsAffectDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;
            var protocol = definition.Rules.ForcedDisplacementProtocolVersion;

            Assert.That(protocol.Value, Is.EqualTo(ForcedDisplacementProtocolVersion.SimultaneousStepV1));
            Assert.That(protocol.IsValid, Is.True);

            // 7 个停止原因编码进哈希（数值冻结）。
            var reasons = (ForcedDisplacementStopReason[])Enum.GetValues(typeof(ForcedDisplacementStopReason));
            Assert.That(reasons.Length, Is.EqualTo(7));
            Assert.That((int)ForcedDisplacementStopReason.Completed, Is.EqualTo(0));
            Assert.That((int)ForcedDisplacementStopReason.Boundary, Is.EqualTo(1));
            Assert.That((int)ForcedDisplacementStopReason.StaticObstacle, Is.EqualTo(2));
            Assert.That((int)ForcedDisplacementStopReason.OccupiedUnit, Is.EqualTo(3));
            Assert.That((int)ForcedDisplacementStopReason.DestinationContention, Is.EqualTo(4));
            Assert.That((int)ForcedDisplacementStopReason.DependencyCycle, Is.EqualTo(5));
            Assert.That((int)ForcedDisplacementStopReason.VolumeOverlap, Is.EqualTo(6));

            var writer = new CanonicalHashWriter();
            ForcedDisplacementEncoding.WriteHashComponents(writer);
            var text = writer.ToCanonicalText();
            foreach (var reason in reasons)
                Assert.That(text, Does.Contain("forced_displacement.stop_reason." + reason));

            // 协议版本变化必须改变哈希。
            var writerV1 = new CanonicalHashWriter();
            protocol.WriteHashComponents(writerV1);
            var writerV2 = new CanonicalHashWriter();
            new ForcedDisplacementProtocolVersion(2).WriteHashComponents(writerV2);
            Assert.That(writerV2.ToDigestHex(), Is.Not.EqualTo(writerV1.ToDigestHex()));

            // 规则级：FrozenV1 携带正确协议。
            Assert.That(BattleRules.FrozenV1.ForcedDisplacementProtocolVersion.Value,
                Is.EqualTo(ForcedDisplacementProtocolVersion.SimultaneousStepV1));
        }

        [Test]
        public void BuilderRejectsUnsupportedForcedDisplacementProtocolVersion()
        {
            // 版本 0（未声明/缺省协议）必须被拒绝——任务 08 不得用 Legacy 行为补洞。
            var invalid = new ForcedDisplacementProtocolVersion(0);
            Assert.That(invalid.IsValid, Is.False);

            var rules = BattleRules.FrozenV1 with { ForcedDisplacementProtocolVersion = invalid };
            Assert.That(rules.Validate(), Is.EqualTo(BattleRules.FORCED_DISPLACEMENT_PROTOCOL_INVALID));

            // 负版本同样拒绝。
            Assert.That((BattleRules.FrozenV1 with
            {
                ForcedDisplacementProtocolVersion = new ForcedDisplacementProtocolVersion(-1)
            }).Validate(), Is.EqualTo(BattleRules.FORCED_DISPLACEMENT_PROTOCOL_INVALID));

            // 冻结实例必须有效。
            Assert.That(BattleRules.FrozenV1.Validate(), Is.Null);
            Assert.That(BattleDefinitionFixture.Definition.Rules.Validate(), Is.Null);
        }

        // ============================================================
        // 5. 伤害迁移
        // ============================================================

        [Test]
        public void DamageChannelsImpactProfilesAndResistanceMapsAffectDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;

            Assert.That(definition.DamageChannels.Count, Is.EqualTo(8));
            Assert.That(definition.ImpactProfiles.Count, Is.EqualTo(3));

            // 通道与 Profile 的稳定 ID。
            Assert.That(definition.DamageChannels.Select(c => c.DamageChannelId.Value).ToArray(),
                Is.EquivalentTo(new[]
                {
                    "physical.blunt", "physical.slash", "physical.pierce",
                    "elemental.fire", "elemental.frost", "elemental.lightning", "arcane", "true"
                }));
            Assert.That(definition.ImpactProfiles.Select(p => p.ImpactProfileId.Value).ToArray(),
                Is.EquivalentTo(new[] { "impact.blunt", "impact.slash", "impact.pierce" }));

            // 传递系数与旧 Kw 逐位等价。
            Assert.That(definition.ImpactProfiles.Single(p => p.ImpactProfileId == ImpactProfiles.Blunt).TransferPercent,
                Is.EqualTo(100));
            Assert.That(definition.ImpactProfiles.Single(p => p.ImpactProfileId == ImpactProfiles.Slash).TransferPercent,
                Is.EqualTo(60));
            Assert.That(definition.ImpactProfiles.Single(p => p.ImpactProfileId == ImpactProfiles.Pierce).TransferPercent,
                Is.EqualTo(30));

            // true 通道默认绕过两层抵抗且不可 Block/Guard。
            var trueChannel = definition.DamageChannels.Single(c => c.DamageChannelId == DamageChannels.True);
            Assert.That((trueChannel.DefaultTags & DamageTagMask.BypassPassiveResistance) != 0, Is.True);
            Assert.That((trueChannel.DefaultTags & DamageTagMask.BypassActionResistance) != 0, Is.True);
            Assert.That((trueChannel.DefaultTags & (DamageTagMask.Blockable | DamageTagMask.Guardable)) == 0, Is.True);

            // 被动抵抗按通道落在单位定义上（7 个明确通道，无通用 DamageReduction）。
            foreach (var unit in definition.Units)
            {
                Assert.That(unit.BaseDamageResistanceQ10.Count, Is.EqualTo(7), unit.UnitDefinitionId.Value);
                Assert.That(unit.BaseDamageResistanceQ10[DamageChannels.Arcane], Is.EqualTo(0));
            }

            // 哈希随通道目录变化。
            var mutatedChannels = definition.DamageChannels
                .Select(c => c.DamageChannelId == DamageChannels.PhysicalBlunt
                    ? new DamageChannelDefinition(c.DamageChannelId, DamageTagMask.None)
                    : c)
                .ToList();
            string mutated = BattleDefinitionHash.Compute(
                definition.RulesVersion, definition.TicksPerSecond, definition.Rules,
                definition.ConcurrentAction, definition.ReactionRules, definition.AdrenalineRules,
                definition.FactionModel, mutatedChannels, definition.ImpactProfiles,
                definition.Units, definition.Actions, definition.AttackPatterns, definition.Volumes,
                definition.MovementPatterns, definition.ActionSets, definition.StatusEffects,
                definition.Encounters, definition.DefaultDynamicSpawnPolicy);
            Assert.That(mutated, Is.Not.EqualTo(definition.BattleDefinitionHashValue));
        }

        [Test]
        public void LegacyImpactTypeMigratesToExplicitDamageAndMomentumFields()
        {
            // Blunt → physical.blunt + impact.blunt；Slash → physical.slash + impact.slash；
            // Pierce → physical.pierce + impact.pierce。
            Assert.That(LegacyDamageMapping.ChannelForImpactType(ImpactType.Blunt), Is.EqualTo(DamageChannels.PhysicalBlunt));
            Assert.That(LegacyDamageMapping.ChannelForImpactType(ImpactType.Slash), Is.EqualTo(DamageChannels.PhysicalSlash));
            Assert.That(LegacyDamageMapping.ChannelForImpactType(ImpactType.Pierce), Is.EqualTo(DamageChannels.PhysicalPierce));
            Assert.That(LegacyDamageMapping.ProfileForImpactType(ImpactType.Blunt), Is.EqualTo(ImpactProfiles.Blunt));
            Assert.That(LegacyDamageMapping.ProfileForImpactType(ImpactType.Slash), Is.EqualTo(ImpactProfiles.Slash));
            Assert.That(LegacyDamageMapping.ProfileForImpactType(ImpactType.Pierce), Is.EqualTo(ImpactProfiles.Pierce));

            // 旧资产事实：QuickSlash=Slash、HeavySmash=Blunt、WideCleave=Slash、
            // SpearThrust=Pierce、Whirlwind=Slash，数值在迁移后逐位保持。
            var definition = BattleDefinitionFixture.Definition;
            AssertAttackDamage(definition, "action.quick_slash.radius_1", DamageChannels.PhysicalSlash, ImpactProfiles.Slash, 15f);
            AssertAttackDamage(definition, "action.heavy_smash.radius_1", DamageChannels.PhysicalBlunt, ImpactProfiles.Blunt, 40f);
            AssertAttackDamage(definition, "action.wide_cleave.radius_1", DamageChannels.PhysicalSlash, ImpactProfiles.Slash, 20f);
            AssertAttackDamage(definition, "action.spear_thrust.radius_1", DamageChannels.PhysicalPierce, ImpactProfiles.Pierce, 25f);
            AssertAttackDamage(definition, "action.whirlwind.radius_1", DamageChannels.PhysicalSlash, ImpactProfiles.Slash, 30f);

            // 每个攻击只有一个伤害分量，且不带"通用 DamageReduction"。
            foreach (var action in definition.Actions)
            {
                if (!(action.Payload is AttackPayloadSpec attack)) continue;
                Assert.That(attack.DamageComponents.Count, Is.EqualTo(1), action.ActionSpecId.Value);
            }

            // ArmorDefense → 物理三通道：R = RoundHalfUp(1024 × d / (d + 100))。
            Assert.That(LegacyDamageMapping.ResistanceQ10FromLegacyDefense(0f), Is.EqualTo(0));
            Assert.That(LegacyDamageMapping.ResistanceQ10FromLegacyDefense(100f), Is.EqualTo(512));
            Assert.That(LegacyDamageMapping.ResistanceQ10FromLegacyDefense(300f), Is.EqualTo(768));
        }

        [Test]
        public void AmbiguousLegacyDamageTypeStopsDefinitionBuild()
        {
            // 旧 ImpactType 数值越界（无对应通道/Profile）必须阻止构建并进入迁移报告。
            var unknown = (ImpactType)99;
            var exChannel = Assert.Throws<LogicDefinitionException>(() =>
                LegacyDamageMapping.ChannelForImpactType(unknown));
            Assert.That(exChannel.ErrorCode, Is.EqualTo(LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID));

            var exProfile = Assert.Throws<LogicDefinitionException>(() =>
                LegacyDamageMapping.ProfileForImpactType(unknown));
            Assert.That(exProfile.ErrorCode, Is.EqualTo(LegacyAuthoringCodes.LEGACY_MOMENTUM_INPUT_INVALID));

            // 防御数值非法（NaN / 负数）同样拒绝，不得猜成 0。
            Assert.Throws<LogicDefinitionException>(() =>
                LegacyDamageMapping.ResistanceQ10FromLegacyDefense(float.NaN));
            Assert.Throws<LogicDefinitionException>(() =>
                LegacyDamageMapping.ResistanceQ10FromLegacyDefense(-1f));

            // 稳定失败码常量存在（供迁移报告引用）。
            Assert.That(DefinitionCodes.LEGACY_DAMAGE_TYPE_AMBIGUOUS, Is.EqualTo("LEGACY_DAMAGE_TYPE_AMBIGUOUS"));
            Assert.That(DefinitionCodes.LEGACY_FOLDED_DAMAGE_FIELD, Is.EqualTo("LEGACY_FOLDED_DAMAGE_FIELD"));
        }

        // ============================================================
        // 6. 反应闭合与肾上腺素
        // ============================================================

        [Test]
        public void ReactableAttackRequiresMinimumReactionLead()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 冻结值下的闭合校验必须零错误。
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateReactionClosure(
                ToActionMap(definition.Actions), ToActionSetMap(definition.ActionSets),
                definition.AdrenalineRules, definition.ReactionRules, errors);
            Assert.That(errors.HasErrors, Is.False, string.Join(",", errors.SortedCodes()));

            // 每个可反应攻击的 BaseWindupTicks 都必须 ≥ MinimumReactionLeadTicks。
            foreach (var action in definition.Actions)
            {
                if (!(action.Payload is AttackPayloadSpec attack)) continue;
                if ((attack.Tags & AttackTagMask.Reactable) == 0) continue;
                var timing = (AttackTimingSpec)action.Timing;
                Assert.That(timing.BaseWindupTicks,
                    Is.GreaterThanOrEqualTo(definition.ReactionRules.MinimumReactionLeadTicks),
                    action.ActionSpecId.Value);
                Assert.That((attack.Tags & (AttackTagMask.Blockable | AttackTagMask.Dodgeable)) != 0, Is.True,
                    "可反应攻击必须给出至少一个 Block/Dodge 选项");
            }

            // 反例：把 MinimumReactionLeadTicks 抬到超过最短前摇（QuickSlash 30 Tick）→ 稳定拒绝。
            var strictRules = definition.ReactionRules with { MinimumReactionLeadTicks = 60 };
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateReactionClosure(
                ToActionMap(definition.Actions), ToActionSetMap(definition.ActionSets),
                definition.AdrenalineRules, strictRules, errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.REACTABLE_ATTACK_REQUIRES_MINIMUM_LEAD));
        }

        [Test]
        public void UnreactableAttackDoesNotRequireReactionLead()
        {
            // 显式标为不可反应的攻击（无 Reactable 位）不需要提前量。
            var unreactable = new ActionSpec(
                new ActionSpecId("action.unreactable_probe"),
                ProjectHero.Logic.Actions.ActionType.Attack,
                new AttackTimingSpec(BaseWindupTicks: 1, RecoveryTicks: 30),
                new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable) },
                    ImpactProfiles.Blunt, 1f, TargetPolicy.AllTargetsInArea, TargetRelationMask.Hostile,
                    0, FirstPattern(), AttackTagMask.None),
                AdrenalineCost: 0);

            var actions = new Dictionary<string, ActionSpec>(StringComparer.Ordinal)
            {
                [unreactable.ActionSpecId.Value] = unreactable
            };

            // 提前量远大于其前摇（1 Tick）也不得报错，因为它根本不可反应。
            var errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateReactionClosure(
                actions, new Dictionary<string, ActionSetDefinition>(StringComparer.Ordinal),
                AdrenalineRules.FrozenV1, new ReactionRules(1, 60), errors);
            Assert.That(errors.SortedCodes(),
                Does.Not.Contain(DefinitionCodes.REACTABLE_ATTACK_REQUIRES_MINIMUM_LEAD));

            // 反面对照：加上 Reactable 位后同样的前摇就会被拒绝。
            var reactable = unreactable with
            {
                Payload = new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable) },
                    ImpactProfiles.Blunt, 1f, TargetPolicy.AllTargetsInArea, TargetRelationMask.Hostile,
                    0, FirstPattern(), AttackTagMask.Reactable | AttackTagMask.Blockable)
            };
            errors = new DefinitionErrorCollector();
            BattleDefinitionValidator.ValidateReactionClosure(
                new Dictionary<string, ActionSpec>(StringComparer.Ordinal) { [reactable.ActionSpecId.Value] = reactable },
                new Dictionary<string, ActionSetDefinition>(StringComparer.Ordinal),
                AdrenalineRules.FrozenV1, new ReactionRules(1, 60), errors);
            Assert.That(errors.SortedCodes(), Does.Contain(DefinitionCodes.REACTABLE_ATTACK_REQUIRES_MINIMUM_LEAD));
        }

        [Test]
        public void ReactionCostsTimingsTagsAndAdrenalineRulesAffectDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 冻结值落实（01B 拍板 B3）。
            var adrenaline = definition.AdrenalineRules;
            Assert.That(adrenaline.MaxAvailablePerCycle, Is.EqualTo(100));
            Assert.That(adrenaline.DamageDealtGainQ10, Is.EqualTo(102));
            Assert.That(adrenaline.DamageReceivedGainQ10, Is.EqualTo(205));
            Assert.That(adrenaline.SuccessfulBlockReward, Is.EqualTo(1));
            Assert.That(adrenaline.SuccessfulDodgeReward, Is.EqualTo(0));
            Assert.That(adrenaline.ClashReward, Is.EqualTo(50));

            var block = definition.FindAction(new ActionSpecId(LegacyIdMigrationManifest.ActionBlock));
            var dodge = definition.FindAction(new ActionSpecId(LegacyIdMigrationManifest.ActionDodge));
            Assert.That(block.AdrenalineCost, Is.EqualTo(2));
            Assert.That(dodge.AdrenalineCost, Is.EqualTo(1));
            Assert.That(((BlockReactionTimingSpec)block.Timing).ReactionWindupTicks, Is.EqualTo(60));
            Assert.That(((DodgeReactionTimingSpec)dodge.Timing).ReactionWindupTicks, Is.EqualTo(30));

            // Attack/Guard/Move 的肾上腺素费用必须为 0。
            foreach (var action in definition.Actions)
            {
                if (action.Type == ProjectHero.Logic.Actions.ActionType.Attack ||
                    action.Type == ProjectHero.Logic.Actions.ActionType.Guard ||
                    action.Type == ProjectHero.Logic.Actions.ActionType.Move)
                    Assert.That(action.AdrenalineCost, Is.Zero, action.ActionSpecId.Value);
            }

            // Guard 抵抗严格 ∈ [1,1023]，Block 由规则固定 1024。
            var guard = definition.FindAction(new ActionSpecId(LegacyIdMigrationManifest.ActionGuard));
            var guardPayload = (GuardPayloadSpec)guard.Payload;
            Assert.That(guardPayload.MomentumResistanceQ10, Is.InRange(1, 1023));
            foreach (var pair in guardPayload.DamageResistanceQ10)
                Assert.That(pair.Value, Is.InRange(1, 1023), pair.Key.Value);
            Assert.That(BattleRules.FullBlockResistanceQ10, Is.EqualTo(1024));

            // 全部进哈希：改一个反应时序必须改变哈希。
            string baseline = HashWithAdrenaline(definition, adrenaline, definition.ReactionRules);
            Assert.That(HashWithAdrenaline(definition, adrenaline with { ClashReward = 51 }, definition.ReactionRules),
                Is.Not.EqualTo(baseline), "肾上腺素规则必须参与哈希");
            Assert.That(HashWithAdrenaline(definition, adrenaline, definition.ReactionRules with { CommandIngressLeadTicks = 2 }),
                Is.Not.EqualTo(baseline), "反应规则必须参与哈希");

            var mutatedActions = definition.Actions.Select(a =>
                a.ActionSpecId.Value == LegacyIdMigrationManifest.ActionBlock
                    ? a with { Timing = new BlockReactionTimingSpec(61, 30) }
                    : a).ToList();
            Assert.That(HashWithActions(definition, mutatedActions, adrenaline, definition.ReactionRules),
                Is.Not.EqualTo(baseline), "Block 反应时序必须参与哈希");
        }

        [Test]
        public void BlockAndDodgeRewardsMustBeLowerThanEveryCorrespondingCost()
        {
            var rules = AdrenalineRules.FrozenV1;

            // 冻结值满足约束。
            Assert.That(rules.ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Block, 2), Is.Null);
            Assert.That(rules.ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Dodge, 1), Is.Null);

            // Block 奖励 ≥ 费用 → 拒绝。
            Assert.That((rules with { SuccessfulBlockReward = 2 })
                    .ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Block, 2),
                Is.EqualTo(AdrenalineRules.ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST));
            Assert.That((rules with { SuccessfulBlockReward = 5 })
                    .ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Block, 2),
                Is.EqualTo(AdrenalineRules.ADRENALINE_BLOCK_REWARD_NOT_BELOW_COST));

            // Dodge 奖励 ≥ 费用 → 拒绝（Dodge 费 1 时必须奖励 0）。
            Assert.That((rules with { SuccessfulDodgeReward = 1 })
                    .ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Dodge, 1),
                Is.EqualTo(AdrenalineRules.ADRENALINE_DODGE_REWARD_NOT_BELOW_COST));

            // 对真实定义的全部反应动作做闭合校验（"奖励小于所有对应动作费用"）。
            var definition = BattleDefinitionFixture.Definition;
            Assert.That(definition.AdrenalineRules.ValidateAgainstActionCosts(definition.Actions), Is.Null);
        }

        [Test]
        public void ReactionCostCannotExceedCycleMaximum()
        {
            var rules = AdrenalineRules.FrozenV1;

            Assert.That(rules.MaxAvailablePerCycle, Is.EqualTo(100));
            Assert.That(rules.ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Block, 100), Is.Null,
                "恰好等于上限必须合法");
            Assert.That(rules.ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Block, 101),
                Is.EqualTo(AdrenalineRules.ADRENALINE_COST_EXCEEDS_CYCLE_MAXIMUM));

            // 降低上限后 Block 费 2 仍然合法，费 101 必须被拒绝。
            var tiny = rules with { MaxAvailablePerCycle = 1 };
            Assert.That(tiny.ValidateReactionCost(ProjectHero.Logic.Actions.ActionType.Block, 2),
                Is.EqualTo(AdrenalineRules.ADRENALINE_COST_EXCEEDS_CYCLE_MAXIMUM));

            // 非法基础值。
            Assert.That((rules with { MaxAvailablePerCycle = 0 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_MAX_AVAILABLE_INVALID));
            Assert.That((rules with { DamageDealtGainQ10 = -1 }).Validate(),
                Is.EqualTo(AdrenalineRules.ADRENALINE_GAIN_NEGATIVE));
        }

        [Test]
        public void ActionSetReactionAvailabilityDoesNotDependOnControllerKind()
        {
            var definition = BattleDefinitionFixture.Definition;

            // 两个动作集合的规则结构只由内容配置决定：
            // 结构必须完全相同（同样的 9 个动作 ID 形态），只有半径变体动作不同。
            var sets = definition.ActionSets.OrderBy(s => s.ActionSetId.Value, StringComparer.Ordinal).ToArray();
            Assert.That(sets.Length, Is.EqualTo(2));

            Assert.That(sets[0].ActionSpecIds.Count, Is.EqualTo(sets[1].ActionSpecIds.Count),
                "两个单位的动作集合结构（可用动作数量）必须一致");

            foreach (var set in sets)
            {
                Assert.That(set.Contains(new ActionSpecId(LegacyIdMigrationManifest.ActionGuard)), Is.True);
                Assert.That(set.Contains(new ActionSpecId(LegacyIdMigrationManifest.ActionMove)), Is.True);
                Assert.That(set.Contains(new ActionSpecId(LegacyIdMigrationManifest.ActionBlock)), Is.True);
                Assert.That(set.Contains(new ActionSpecId(LegacyIdMigrationManifest.ActionDodge)), Is.True);
            }

            // 每个 Block/Dodge 都在至少一个 ActionSet 中（闭合）。
            var available = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in sets)
                foreach (var id in set.ActionSpecIds) available.Add(id.Value);
            Assert.That(available.Contains(LegacyIdMigrationManifest.ActionBlock), Is.True);
            Assert.That(available.Contains(LegacyIdMigrationManifest.ActionDodge), Is.True);

            // 单位定义引用动作集合，且与控制者类型无关（Player/Ai 绑定同样的结构）。
            foreach (var unit in definition.Units)
                Assert.That(definition.FindActionSet(unit.ActionSetId), Is.Not.Null, unit.UnitDefinitionId.Value);

            // 真实定义里不存在"因为控制者是玩家才可用"的结构差异：
            // 两个集合的 Guard/Move/Block/Dodge 可用性完全一致。
            foreach (var reactionId in new[]
                     {
                         LegacyIdMigrationManifest.ActionGuard, LegacyIdMigrationManifest.ActionMove,
                         LegacyIdMigrationManifest.ActionBlock, LegacyIdMigrationManifest.ActionDodge
                     })
            {
                bool first = sets[0].Contains(new ActionSpecId(reactionId));
                bool second = sets[1].Contains(new ActionSpecId(reactionId));
                Assert.That(second, Is.EqualTo(first), reactionId);
            }
        }

        [Test]
        public void MainEncounterBuildsWithoutFallbackOrSkippedAsset()
        {
            var result = BattleDefinitionFixture.Build();

            Assert.That(result.Succeeded, Is.True,
                "主战斗 Encounter 必须零错误构建：" + string.Join(",", result.ErrorCodes()));
            Assert.That(result.Errors.Count, Is.Zero);
            Assert.That(result.Definition, Is.Not.Null);

            var definition = result.Definition;
            var encounter = definition.FindEncounter(
                new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId));

            Assert.That(encounter, Is.Not.Null);
            Assert.That(encounter.Slots.Count, Is.EqualTo(2));
            Assert.That(encounter.Controllers.Count, Is.EqualTo(2));

            // 每个槽位引用的单位定义必须存在（不是回退/占位）。
            foreach (var slot in encounter.Slots)
                Assert.That(definition.FindUnit(slot.DefinitionId), Is.Not.Null, slot.SlotId.Value);

            // 全部 10 个 Pattern 与 3 个 Volume 都被转换（没有一个被静默跳过）。
            Assert.That(definition.AttackPatterns.Count, Is.EqualTo(10));
            Assert.That(definition.Volumes.Count, Is.EqualTo(3));

            // 全部 14 个被消费资产用它们的最终 ID 出现在定义中。
            foreach (var patternMigration in LegacyIdMigrationManifest.Patterns)
                Assert.That(definition.AttackPatterns.Any(p =>
                        p.AttackPatternId.Value == patternMigration.FinalAttackPatternId), Is.True,
                    patternMigration.FinalAttackPatternId);
            foreach (var volumeMigration in LegacyIdMigrationManifest.Volumes)
                Assert.That(definition.Volumes.Any(v => v.VolumeSpecId.Value == volumeMigration.FinalVolumeSpecId),
                    Is.True, volumeMigration.FinalVolumeSpecId);

            // 未消费资产也被显式保留（ForRadius3 空库不产生动作，Radius_3 有最终 ID）。
            Assert.That(definition.Volumes.Any(v => v.VolumeSpecId.Value == "unit_volume.hex.radius_3"), Is.True);
            Assert.That(definition.Actions.Any(a => a.ActionSpecId.Value.Contains("radius_3")), Is.False);

            // 哈希是 64 位十六进制且非空。
            Assert.That(definition.BattleDefinitionHashValue, Has.Length.EqualTo(16));
            Assert.That(definition.BattleDefinitionHashValue, Does.Match("^[0-9a-f]{16}$"));
        }

        /// <summary>
        /// 哈希绝对值锚点（防漂移）：把迁移记录 §11 冻结的 RulesVersion 与主战斗定义哈希
        /// 固定为字面量。其它哈希断言全部是<b>相对</b>的（"改 X → 哈希变"或
        /// "等价输入 → 哈希不变"），只能证明确定性与敏感性，无法发现一次同时改变了
        /// 输入与期望的意外玩法定义漂移；本测试用独立重算的绝对値封住这个缺口。
        /// 有意变更玩法定义时，必须先更新迁移记录 §11 再更新此处的常量。
        /// </summary>
        [Test]
        public void MainEncounterDefinitionHashMatchesFrozenAnchor()
        {
            // 不使用夹具缓存，从真实资产独立重新构建一次（防止读到上一次构建的残留）。
            var result = BattleDefinitionFixture.Build();
            Assert.That(result.Succeeded, Is.True,
                "主战斗 Encounter 必须零错误构建：" + string.Join(",", result.ErrorCodes()));
            var definition = result.Definition;

            Assert.That(definition.RulesVersion, Is.EqualTo(FrozenRulesVersion),
                "RulesVersion 必须与迁移记录 §11 冻结值一致");
            Assert.That(definition.BattleDefinitionHashValue, Is.EqualTo(FrozenMainEncounterHash),
                "主战斗定义哈希漂移：真实值若已确认无误，须同步更新 02B-配置迁移记录.md §11 与本常量");

            // 二次独立构建必须复现同一绝对值（排除构建顺序/缓存导致的偶发差异）。
            Assert.That(BattleDefinitionFixture.Build().Definition.BattleDefinitionHashValue,
                Is.EqualTo(FrozenMainEncounterHash), "重复构建必须复现冻结哈希");
        }

        [Test]
        public void RenamingDisplayNameDoesNotChangeLogicIdOrHash()
        {
            // 显示名（Action.Name）与资产名都不参与 Logic ID 或哈希：
            // 复制一份库资产、只改显示名，构建结果必须与基线完全一致。
            var assets = BattleDefinitionFixture.Assets;
            var originalLibrary = assets.Library(LegacyIdMigrationManifest.ForRadius1Guid);
            var renamedLibrary = UnityEngine.ScriptableObject.CreateInstance<ActionLibrarySO>();
            renamedLibrary.name = "RenamedLibrary_DisplayOnly";
            try
            {
                foreach (var entry in originalLibrary.Actions)
                {
                    var original = entry.Data;
                    var copy = new ProjectHero.Core.Actions.Action(
                        original.Name + " 改过的显示名", original.Type, original.BaseTime,
                        original.BaseDamage, original.ImpactType, original.StaminaCost,
                        original.ForceMultiplier, original.Pattern);
                    renamedLibrary.Actions.Add(new ActionLibrarySO.ActionEntry { ID = entry.ID, Data = copy });
                }

                // 保留原 GUID（用原键替换），只是内容里的显示名不同。
                var libraries = assets.LibrariesByGuid
                    .Select(p => p.Key == LegacyIdMigrationManifest.ForRadius1Guid
                        ? new KeyValuePair<string, ActionLibrarySO>(p.Key, renamedLibrary)
                        : p)
                    .ToList();
                var mutatedSet = new LegacyAssetSet(
                    libraries, assets.PatternsByGuid.ToList(), assets.VolumesByGuid.ToList());

                var renamed = BattleDefinitionBuilder.BuildMainBattleDefinition(
                    mutatedSet, BattleDefinitionFixture.UnitSource);

                Assert.That(renamed.Succeeded, Is.True, string.Join(",", renamed.ErrorCodes()));
                Assert.That(renamed.Definition.BattleDefinitionHashValue,
                    Is.EqualTo(BattleDefinitionFixture.Definition.BattleDefinitionHashValue),
                    "显示名不得影响定义哈希");

                for (int i = 0; i < renamed.Definition.Actions.Count; i++)
                {
                    Assert.That(renamed.Definition.Actions[i].ActionSpecId.Value,
                        Is.EqualTo(BattleDefinitionFixture.Definition.Actions[i].ActionSpecId.Value),
                        "显示名不得影响 Logic ID");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(renamedLibrary);
            }
        }

        [Test]
        public void RuntimeInputsProvideMetaResourceWithoutExternalSingleton()
        {
            // 局外资源与 RNG 种子只来自 BattleRuntimeInputs，且不得为负。
            var inputs = BattleDefinitionFixture.RuntimeInputs;
            Assert.That(inputs.Validate(), Is.Null);
            Assert.That(inputs.InitialMetaResource,
                Is.EqualTo(BattleDefinitionBuilder.MainEncounterInitialMetaResource));
            Assert.That(inputs.InitialRngSeed, Is.Not.Zero, "RNG 种子必须显式给出");

            Assert.That(new Logic.Initialization.BattleRuntimeInputs(1UL, -1).Validate(),
                Is.EqualTo(DefinitionCodes.META_RESOURCE_NEGATIVE));

            // 并发行动费用来自定义，不是调用方参数。
            var concurrent = BattleDefinitionFixture.Definition.ConcurrentAction;
            Assert.That(concurrent.MetaResourceCost, Is.EqualTo(ConcurrentActionDefinition.FrozenV1.MetaResourceCost));
            Assert.That(concurrent.Validate(), Is.Null);
            Assert.That(new ConcurrentActionDefinition(-1).Validate(),
                Is.EqualTo(ConcurrentActionDefinition.CONCURRENT_ACTION_COST_INVALID));
        }

        [Test]
        public void ConcurrentActionCostIsDefinedAndAffectsDefinitionHash()
        {
            var definition = BattleDefinitionFixture.Definition;
            Assert.That(definition.ConcurrentAction, Is.Not.Null);
            Assert.That(definition.ConcurrentAction.MetaResourceCost, Is.GreaterThanOrEqualTo(0));
            Assert.That(definition.ConcurrentAction.MetaResourceCost,
                Is.EqualTo(ConcurrentActionDefinition.FrozenV1.MetaResourceCost));

            // 改费用必须改变哈希。
            var writerA = new CanonicalHashWriter();
            definition.ConcurrentAction.WriteHashComponents(writerA);
            var writerB = new CanonicalHashWriter();
            new ConcurrentActionDefinition(definition.ConcurrentAction.MetaResourceCost + 1)
                .WriteHashComponents(writerB);
            Assert.That(writerB.ToDigestHex(), Is.Not.EqualTo(writerA.ToDigestHex()));

            string mutated = BattleDefinitionHash.Compute(
                definition.RulesVersion, definition.TicksPerSecond, definition.Rules,
                new ConcurrentActionDefinition(definition.ConcurrentAction.MetaResourceCost + 1),
                definition.ReactionRules, definition.AdrenalineRules, definition.FactionModel,
                definition.DamageChannels, definition.ImpactProfiles, definition.Units, definition.Actions,
                definition.AttackPatterns, definition.Volumes, definition.MovementPatterns,
                definition.ActionSets, definition.StatusEffects, definition.Encounters,
                definition.DefaultDynamicSpawnPolicy);
            Assert.That(mutated, Is.Not.EqualTo(definition.BattleDefinitionHashValue));
        }

        // ============================================================
        // 私有助手
        // ============================================================

        private static GridBoundaryDefinition Boundary()
            => new GridBoundaryDefinition(
                new ProjectHero.Logic.Grid.GridPoint(-16, -16),
                new ProjectHero.Logic.Grid.GridPoint(16, 16));

        private static VictoryDefinition Victory()
            => new VictoryDefinition(
                new List<FactionId> { FactionIds.Hero },
                new List<FactionId> { FactionIds.Monster },
                "victory", "defeat", "draw");

        private static ActionSpec MakeAttackSpec(TargetRelationMask mask)
        {
            return new ActionSpec(
                new ActionSpecId("action.mask_probe"),
                ProjectHero.Logic.Actions.ActionType.Attack,
                new AttackTimingSpec(30, 30),
                new AttackPayloadSpec(
                    new[] { new DamageComponentSpec(DamageChannels.PhysicalBlunt, 10f, DamageTagMask.Blockable) },
                    ImpactProfiles.Blunt, 1f, TargetPolicy.AllTargetsInArea, mask, 0, FirstPattern(),
                    AttackTagMask.Reactable | AttackTagMask.Blockable),
                AdrenalineCost: 0);
        }

        private static ProjectHero.Logic.Grid.AttackPatternSpec FirstPattern()
            => BattleDefinitionFixture.Definition.AttackPatterns[0];

        private static void AssertAttackDamage(
            BattleDefinition definition, string actionSpecId,
            DamageChannelId channel, ImpactProfileId profile, float rawAmount)
        {
            var action = definition.FindAction(new ActionSpecId(actionSpecId));
            Assert.That(action, Is.Not.Null, actionSpecId);
            var payload = (AttackPayloadSpec)action.Payload;
            Assert.That(payload.DamageComponents[0].ChannelId, Is.EqualTo(channel), actionSpecId);
            Assert.That(payload.ImpactProfileId, Is.EqualTo(profile), actionSpecId);
            Assert.That(payload.DamageComponents[0].RawAmount, Is.EqualTo(rawAmount), actionSpecId);
        }

        private static Dictionary<string, ActionSpec> ToActionMap(IEnumerable<ActionSpec> actions)
        {
            var map = new Dictionary<string, ActionSpec>(StringComparer.Ordinal);
            foreach (var action in actions) map[action.ActionSpecId.Value] = action;
            return map;
        }

        private static Dictionary<string, ActionSetDefinition> ToActionSetMap(
            IEnumerable<ActionSetDefinition> sets)
        {
            var map = new Dictionary<string, ActionSetDefinition>(StringComparer.Ordinal);
            foreach (var set in sets) map[set.ActionSetId.Value] = set;
            return map;
        }

        private static string HashWithAdrenaline(
            BattleDefinition definition, AdrenalineRules adrenaline, ReactionRules reactionRules)
            => BattleDefinitionHash.Compute(
                definition.RulesVersion, definition.TicksPerSecond, definition.Rules,
                definition.ConcurrentAction, reactionRules, adrenaline, definition.FactionModel,
                definition.DamageChannels, definition.ImpactProfiles, definition.Units, definition.Actions,
                definition.AttackPatterns, definition.Volumes, definition.MovementPatterns,
                definition.ActionSets, definition.StatusEffects, definition.Encounters,
                definition.DefaultDynamicSpawnPolicy);

        private static string HashWithActions(
            BattleDefinition definition, IReadOnlyList<ActionSpec> actions,
            AdrenalineRules adrenaline, ReactionRules reactionRules)
            => BattleDefinitionHash.Compute(
                definition.RulesVersion, definition.TicksPerSecond, definition.Rules,
                definition.ConcurrentAction, reactionRules, adrenaline, definition.FactionModel,
                definition.DamageChannels, definition.ImpactProfiles, definition.Units, actions,
                definition.AttackPatterns, definition.Volumes, definition.MovementPatterns,
                definition.ActionSets, definition.StatusEffects, definition.Encounters,
                definition.DefaultDynamicSpawnPolicy);

        private static string Rehash(BattleDefinition source, IReadOnlyList<ActionSpec> actions)
            => BattleDefinitionHash.Compute(
                source.RulesVersion, source.TicksPerSecond, source.Rules, source.ConcurrentAction,
                source.ReactionRules, source.AdrenalineRules, source.FactionModel, source.DamageChannels,
                source.ImpactProfiles, source.Units, actions, source.AttackPatterns, source.Volumes,
                source.MovementPatterns, source.ActionSets, source.StatusEffects, source.Encounters,
                source.DefaultDynamicSpawnPolicy);

        /// <summary>测试专用的显式关系解析器构造（与生产初始化器共用同一实现）。</summary>
        private static class StubResolver
        {
            public static FactionRelationResolver Resolve(
                BattleDefinition definition, Dictionary<UnitId, FactionId> unitFactions)
                => new FactionRelationResolver(definition.FactionModel, unitFactions);
        }
    }
}
