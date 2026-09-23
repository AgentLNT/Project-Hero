using System;
using System.Collections.Generic;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using LogicGridDirection = ProjectHero.Logic.Grid.GridDirection;
using LegacyGridDefinitionConverter = ProjectHero.Authoring.Compatibility.LegacyGridDefinitionConverter;

namespace ProjectHero.Authoring
{
    /// <summary>
    /// 唯一配置真相入口（主方案 2.3 / 任务包「必须产出」4）。
    ///
    /// 它把当前战斗引用的全部旧资产一次性转换成闭合、不可变、可哈希的
    /// <see cref="BattleDefinition"/>，并执行 ID / 引用 / 数值 / 时序 / 方向几何 /
    /// 路径规则 / Encounter 边界 / Faction 矩阵 / 目标掩码 / 胜负分组 / 反应闭合的全部校验。
    ///
    /// 两条硬性契约：
    /// <list type="number">
    /// <item><strong>整体拒绝</strong>：任一被引用的配置无法转换时不产出定义，只返回稳定错误列表
    /// （不存在"跳过该资产继续运行"的路径）。</item>
    /// <item><strong>发现顺序无关</strong>：资产集合在进入构建前已按 GUID 归一化；构建内部所有集合
    /// 按 ID 的 Ordinal 排序；错误列表按稳定键（CODE|detail|origin）排序。
    /// 改变输入枚举顺序不会改变定义、错误列表或哈希。</item>
    /// </list>
    /// </summary>
    public static class BattleDefinitionBuilder
    {
        public const string RulesVersionV1 = "battle-def-v1";

        /// <summary>
        /// 主战斗场景的设计网格边界（顶点包围盒，偶数坐标，包含两个初始站位）。
        /// 这是首版唯一空间搜索边界：不定义独立 MaxSearchDistance，也不沿用旧 ±100 窗口。
        /// 依据见 02B 配置迁移记录 §7。
        /// </summary>
        public static readonly ProjectHero.Core.Pathfinding.GridPoint MainSceneBoundaryMin =
            new ProjectHero.Core.Pathfinding.GridPoint(-16, -16);

        public static readonly ProjectHero.Core.Pathfinding.GridPoint MainSceneBoundaryMax =
            new ProjectHero.Core.Pathfinding.GridPoint(16, 16);

        public const int DefaultBlockReactionWindupTicks = FrozenDesignValues.BlockReactionWindupTicks;
        public const int DefaultBlockRecoveryTicks = 30;
        public const int DefaultDodgeReactionWindupTicks = FrozenDesignValues.DodgeReactionWindupTicks;
        public const int DefaultDodgeRecoveryTicks = 30;
        public const int DefaultGuardWindupTicks = 15;
        public const int DefaultGuardActiveTicks = 60;
        public const int DefaultGuardRecoveryTicks = 15;
        public const int DefaultGuardPartialResistanceQ10 = 512;
        public const int DefaultGuardMomentumResistanceQ10 = 512;
        public const int DefaultMoveBaseStepTicks = 12;
        public const int DefaultMoveRecoveryTicks = 1;
        public const int DefaultMoveMaxPathWeightUnits = 64;
        public const int DefaultDodgeMaxDistanceSteps = 2;
        public const int DefaultReactionCommandIngressLeadTicks = 1;
        public const int DefaultReactionMinimumLeadTicks = 1;
        public const int MainEncounterInitialMetaResource = 10;

        // ================= 公开入口 =================

        /// <summary>
        /// 从 Resources 读取全部旧配置资产并构建主战斗定义。
        /// 单位来源必须由调用方显式提供（场景扫描适配器留在旧程序集中，
        /// 见 <c>ProjectHero.Core.Compatibility.Authoring.LegacyBattleUnitSceneAdapter</c>）。
        /// </summary>
        public static BattleDefinitionBuildResult BuildMainBattleDefinition(LegacyBattleUnitSource unitsSource)
            => BuildMainBattleDefinition(LegacyAssetResolver.LoadAllFromResources(), unitsSource);

        /// <summary>
        /// 从显式资产集合与显式单位来源构建主战斗定义。
        /// 这是测试与 Editor 工具的唯一入口；<paramref name="assets"/> 的枚举顺序会被归一化，
        /// 因此改变发现顺序不会改变结果。
        /// </summary>
        public static BattleDefinitionBuildResult BuildMainBattleDefinition(
            LegacyAssetSet assets,
            LegacyBattleUnitSource unitsSource)
        {
            if (assets == null) throw new ArgumentNullException(nameof(assets));

            var errors = new DefinitionErrorCollector();
            var warnings = new List<string>();
            var report = new DefinitionBuildReport();

            // —— ① 版本化规则常量 ——
            var rules = BattleRules.FrozenV1;
            AddIfError(errors, rules.Validate(), nameof(BattleRules.FrozenV1));
            AddIfError(errors, rules.PathCostRules.ValidateFrozenRules(), "PathCostRules");
            AddIfError(errors, rules.PathSearchRules.Validate(), "PathSearchRules");
            AddIfError(errors, rules.ForcedDisplacementProtocolVersion.IsValid
                ? null : BattleRules.FORCED_DISPLACEMENT_PROTOCOL_INVALID, "ForcedDisplacementProtocolVersion");

            var concurrentAction = ConcurrentActionDefinition.FrozenV1;
            AddIfError(errors, concurrentAction.Validate(), nameof(ConcurrentActionDefinition));

            var reactionRules = new ReactionRules(
                DefaultReactionCommandIngressLeadTicks, DefaultReactionMinimumLeadTicks);
            AddIfError(errors, reactionRules.Validate(), nameof(ReactionRules));

            var adrenalineRules = AdrenalineRules.FrozenV1;
            AddIfError(errors, adrenalineRules.Validate(), nameof(AdrenalineRules));

            // —— ② 目录 ——
            var damageChannels = BattleDefinitionAssembler.BuildDamageChannelCatalog();
            var impactProfiles = BattleDefinitionAssembler.BuildImpactProfileCatalog();
            foreach (var profile in impactProfiles)
                AddIfError(errors, profile.Validate(), profile.ImpactProfileId.Value);

            // —— ③ Faction 模型（hero / monster，唯一无序对 = Hostile）——
            var factionModel = BuildDefaultFactionModel(errors);

            // —— ④ Pattern / Volume：全部资产都必须能转换 ——
            var patternSpecs = new SortedDictionary<string, AttackPatternSpec>(StringComparer.Ordinal);
            var volumeSpecs = new SortedDictionary<string, VolumeSpec>(StringComparer.Ordinal);

            foreach (var pair in assets.PatternsByGuid)
            {
                var migration = LegacyIdMigrationManifest.FindPattern(pair.Key);
                if (migration == null)
                {
                    errors.Add(DefinitionCodes.LEGACY_ID_MIGRATION_MISSING, "pattern asset", pair.Key);
                    continue;
                }
                try
                {
                    patternSpecs[migration.FinalAttackPatternId] =
                        LegacyGridDefinitionConverter.ConvertPattern(migration.FinalAttackPatternId, pair.Value);
                }
                catch (LogicDefinitionException ex)
                {
                    errors.Add(ex.ErrorCode, ex.Message, migration.FinalAttackPatternId);
                }
            }

            foreach (var pair in assets.VolumesByGuid)
            {
                var migration = LegacyIdMigrationManifest.FindVolume(pair.Key);
                if (migration == null)
                {
                    errors.Add(DefinitionCodes.LEGACY_ID_MIGRATION_MISSING, "volume asset", pair.Key);
                    continue;
                }
                try
                {
                    volumeSpecs[migration.FinalVolumeSpecId] =
                        LegacyGridDefinitionConverter.ConvertVolume(migration.FinalVolumeSpecId, pair.Value);
                }
                catch (LogicDefinitionException ex)
                {
                    errors.Add(ex.ErrorCode, ex.Message, migration.FinalVolumeSpecId);
                }
            }

            // —— ⑤ 动作定义：旧攻击（逐条人工映射）+ Guard/Move/Block/Dodge（本任务新增）——
            var actions = new SortedDictionary<string, ActionSpec>(StringComparer.Ordinal);
            var movementPatterns = BattleDefinitionAssembler.BuildMovementPatternCatalog();
            var movementPatternId = MovementPatternIds.Identity;

            BuildLegacyAttacks(assets, rules, actions, errors);
            BuildOrdinaryAndReactionActions(rules, actions, movementPatternId, errors);

            // —— ⑥ 动作集合（内容配置，与控制者类型无关）——
            var attackIdsR1 = new List<ActionSpecId>();
            var attackIdsR2 = new List<ActionSpecId>();
            foreach (var migration in LegacyIdMigrationManifest.Actions)
            {
                var id = new ActionSpecId(migration.FinalActionSpecId);
                if (string.Equals(migration.Key.AssetGuid, LegacyIdMigrationManifest.ForRadius1Guid,
                        StringComparison.Ordinal))
                    attackIdsR1.Add(id);
                else
                    attackIdsR2.Add(id);
            }

            var actionSets = new SortedDictionary<string, ActionSetDefinition>(StringComparer.Ordinal);
            actionSets[LegacyIdMigrationManifest.ActionSetRadius1] =
                BattleDefinitionAssembler.BuildActionSet(LegacyIdMigrationManifest.ActionSetRadius1, attackIdsR1);
            actionSets[LegacyIdMigrationManifest.ActionSetRadius2] =
                BattleDefinitionAssembler.BuildActionSet(LegacyIdMigrationManifest.ActionSetRadius2, attackIdsR2);

            // —— ⑦ 单位定义（旧 CombatUnit 字段一次性映射）——
            var units = new SortedDictionary<string, UnitDefinition>(StringComparer.Ordinal);
            BuildUnits(units, unitsSource, errors);

            // —— ⑧ Encounter ——
            var encounter = BuildMainEncounter(errors);

            // —— ⑨ 交叉校验 ——
            BattleDefinitionValidator.ValidateFactionModel(factionModel.Factions, factionModel.Relations, errors);
            BattleDefinitionValidator.ValidateActionSets(actionSets, actions, errors);
            BattleDefinitionValidator.ValidateUnits(units, actionSets, errors);
            BattleDefinitionValidator.ValidateReferences(actions, patternSpecs, volumeSpecs, ToMovementDictionary(movementPatterns), errors);
            BattleDefinitionValidator.ValidateEncounter(encounter, factionModel, errors);
            BattleDefinitionValidator.ValidateReactionClosure(actions, actionSets, adrenalineRules, reactionRules, errors);

            // —— ⑩ 定义 + 哈希 ——
            var orderedUnits = ToUnitList(units);
            var orderedActions = ToActionList(actions);
            var orderedPatterns = ToValueList(patternSpecs);
            var orderedVolumes = ToValueList(volumeSpecs);
            var orderedActionSets = ToValueList(actionSets);
            var orderedEncounters = new List<EncounterDefinition> { encounter };
            var statusEffects = new List<StatusEffectSpec>();
            var dynamicSpawnPolicy = DynamicSpawnFactionPolicy.InheritSource();
            AddIfError(errors, dynamicSpawnPolicy.Validate(), "DynamicSpawnFactionPolicy");

            if (errors.HasErrors)
                return BattleDefinitionBuildResult.Failed(errors, warnings);

            string hash = BattleDefinitionHash.Compute(
                RulesVersionV1,
                rules.TicksPerSecond,
                rules,
                concurrentAction,
                reactionRules,
                adrenalineRules,
                factionModel,
                damageChannels,
                impactProfiles,
                orderedUnits,
                orderedActions,
                orderedPatterns,
                orderedVolumes,
                movementPatterns,
                orderedActionSets,
                statusEffects,
                orderedEncounters,
                dynamicSpawnPolicy);

            var definition = new BattleDefinition(
                RulesVersionV1,
                rules.TicksPerSecond,
                rules,
                concurrentAction,
                reactionRules,
                adrenalineRules,
                factionModel,
                damageChannels,
                impactProfiles,
                orderedUnits,
                orderedActions,
                orderedPatterns,
                orderedVolumes,
                movementPatterns,
                orderedActionSets,
                statusEffects,
                orderedEncounters,
                dynamicSpawnPolicy,
                hash);

            return BattleDefinitionBuildResult.Success(definition, warnings);
        }

        // ================= 装配步骤 =================

        /// <summary>
        /// 首版阵营模型：01B 拍板 A1/A2 冻结的 hero / monster 两个阵营，
        /// 唯一无序对 hero↔monster = Hostile。同阵营 Allied、同单位 Self 由规则自动成立。
        /// </summary>
        public static FactionModelDefinition BuildDefaultFactionModel(DefinitionErrorCollector errors)
        {
            var factions = BattleDefinitionAssembler.CanonicalizeFactions(new List<FactionDefinition>
            {
                new FactionDefinition(FactionIds.Hero),
                new FactionDefinition(FactionIds.Monster)
            });

            var relations = BattleDefinitionAssembler.CanonicalizeRelations(new List<FactionRelationDefinition>
            {
                new FactionRelationDefinition(FactionIds.Hero, FactionIds.Monster, FactionDisposition.Hostile)
            });

            return new FactionModelDefinition(factions, relations);
        }

        /// <summary>
        /// 旧攻击动作：逐条沿用显式 ID 迁移清单。
        /// 局部 ID（例如 QuickSlash）只用于在"资产 GUID + 局部 ID"里定位旧条目，
        /// 绝不直接当作全局 <c>ActionSpecId</c>：清单没有记录时以
        /// <see cref="DefinitionCodes.LEGACY_ID_MIGRATION_MISSING"/> 拒绝。
        /// </summary>
        private static void BuildLegacyAttacks(
            LegacyAssetSet assets,
            BattleRules rules,
            SortedDictionary<string, ActionSpec> actions,
            DefinitionErrorCollector errors)
        {
            foreach (var migration in LegacyIdMigrationManifest.Actions)
            {
                var library = assets.Library(migration.Key.AssetGuid);
                if (library == null)
                {
                    errors.Add(DefinitionCodes.LEGACY_ID_MIGRATION_MISSING,
                        "action library", migration.Key.AssetGuid);
                    continue;
                }

                ProjectHero.Core.Actions.Action legacyAction = null;
                if (library.Actions != null)
                {
                    foreach (var entry in library.Actions)
                    {
                        if (entry != null && string.Equals(entry.ID, migration.Key.LocalId, StringComparison.Ordinal))
                        {
                            legacyAction = entry.Data;
                            break;
                        }
                    }
                }

                if (legacyAction == null)
                {
                    errors.Add(DefinitionCodes.LEGACY_ID_MIGRATION_MISSING,
                        migration.Key.ToString(), migration.FinalActionSpecId);
                    continue;
                }

                var patternMigration = LegacyIdMigrationManifest.FindPattern(migration.PatternAssetGuid);
                if (patternMigration == null)
                {
                    errors.Add(DefinitionCodes.LEGACY_ID_MIGRATION_MISSING,
                        "pattern for action", migration.FinalActionSpecId);
                    continue;
                }

                var patternAsset = assets.Pattern(migration.PatternAssetGuid);
                if (patternAsset == null)
                {
                    errors.Add(DefinitionCodes.DEFINITION_REFERENCE_DANGLING,
                        patternMigration.FinalAttackPatternId, migration.FinalActionSpecId);
                    continue;
                }

                AttackPatternSpec pattern;
                try
                {
                    pattern = LegacyGridDefinitionConverter.ConvertPattern(
                        patternMigration.FinalAttackPatternId, patternAsset);
                }
                catch (LogicDefinitionException ex)
                {
                    errors.Add(ex.ErrorCode, ex.Message, migration.FinalActionSpecId);
                    continue;
                }

                try
                {
                    // 01B 拍板 A3：当前 5 个攻击（R1/R2 两个库同名同掩码）全部 Hostile-only。
                    // 该值由迁移清单逐条人工确认；Logic 不提供"默认只打敌人"。
                    actions[migration.FinalActionSpecId] = LegacyTypeConversion.BuildAttackSpec(
                        migration.FinalActionSpecId, legacyAction, pattern,
                        TargetRelationMask.Hostile, rules);
                }
                catch (LogicDefinitionException ex)
                {
                    errors.Add(ex.ErrorCode, ex.Message, migration.FinalActionSpecId);
                }
            }
        }

        /// <summary>
        /// Guard / Move / Block / Dodge：旧系统没有对应资产，全部为本任务新增的显式定义。
        /// 数值来源见 02B 配置迁移记录 §9（秒值只在加载边界量化一次）。
        /// </summary>
        private static void BuildOrdinaryAndReactionActions(
            BattleRules rules,
            SortedDictionary<string, ActionSpec> actions,
            string movementPatternId,
            DefinitionErrorCollector errors)
        {
            var movementPattern = new MovementPatternSpec(
                new MovementPatternId(movementPatternId),
                BattleDefinitionAssembler.BuildMovementPatternCatalog()[0].Directions);

            // Guard：窗口型普通防御，部分抵抗严格位于 [1, 1023]。
            var guardResistances = new Dictionary<DamageChannelId, int>
            {
                [DamageChannels.PhysicalBlunt] = DefaultGuardPartialResistanceQ10,
                [DamageChannels.PhysicalSlash] = DefaultGuardPartialResistanceQ10,
                [DamageChannels.PhysicalPierce] = DefaultGuardPartialResistanceQ10,
                [DamageChannels.ElementalFire] = DefaultGuardPartialResistanceQ10,
                [DamageChannels.ElementalFrost] = DefaultGuardPartialResistanceQ10,
                [DamageChannels.ElementalLightning] = DefaultGuardPartialResistanceQ10
            };

            AddAction(actions, new ActionSpec(
                new ActionSpecId(LegacyIdMigrationManifest.ActionGuard),
                ProjectHero.Logic.Actions.ActionType.Guard,
                new GuardTimingSpec(DefaultGuardWindupTicks, DefaultGuardActiveTicks, DefaultGuardRecoveryTicks),
                new GuardPayloadSpec(guardResistances, DefaultGuardMomentumResistanceQ10,
                    DefenseTagMask.Blockable | DefenseTagMask.Guardable),
                AdrenalineCost: 0), rules, errors);

            AddAction(actions, new ActionSpec(
                new ActionSpecId(LegacyIdMigrationManifest.ActionMove),
                ProjectHero.Logic.Actions.ActionType.Move,
                new MoveTimingSpec(DefaultMoveBaseStepTicks, DefaultMoveRecoveryTicks),
                new MovePayloadSpec(DefaultMoveMaxPathWeightUnits, movementPattern),
                AdrenalineCost: 0), rules, errors);

            AddAction(actions, new ActionSpec(
                new ActionSpecId(LegacyIdMigrationManifest.ActionBlock),
                ProjectHero.Logic.Actions.ActionType.Block,
                new BlockReactionTimingSpec(DefaultBlockReactionWindupTicks, DefaultBlockRecoveryTicks),
                new BlockPayloadSpec(DefenseTagMask.Blockable | DefenseTagMask.Guardable),
                FrozenDesignValues.BlockAdrenalineCost), rules, errors);

            AddAction(actions, new ActionSpec(
                new ActionSpecId(LegacyIdMigrationManifest.ActionDodge),
                ProjectHero.Logic.Actions.ActionType.Dodge,
                new DodgeReactionTimingSpec(DefaultDodgeReactionWindupTicks, DefaultDodgeRecoveryTicks),
                new DodgePayloadSpec(DefaultDodgeMaxDistanceSteps, movementPattern),
                FrozenDesignValues.DodgeAdrenalineCost), rules, errors);
        }

        private static void BuildUnits(
            SortedDictionary<string, UnitDefinition> units,
            LegacyBattleUnitSource source,
            DefinitionErrorCollector errors)
        {
            if (source == null)
            {
                errors.Add(DefinitionCodes.DEFINITION_REFERENCE_DANGLING, "unit source", "LegacyBattleUnitSource");
                return;
            }

            TryAddUnit(units, LegacyIdMigrationManifest.UnitHeroRadius1,
                LegacyIdMigrationManifest.ActionSetRadius1, source.HeroStats,
                LegacyIdMigrationManifest.ForRadius1Guid, errors);

            TryAddUnit(units, LegacyIdMigrationManifest.UnitMonsterRadius2,
                LegacyIdMigrationManifest.ActionSetRadius2, source.EnemyStats,
                LegacyIdMigrationManifest.ForRadius2Guid, errors);
        }

        /// <summary>
        /// 构建主战斗 Encounter：稳定 ID、规范网格边界、两个固定槽位、两个 ControllerBinding、
        /// 以及 01B 拍板 A4 的 VictoryDefinition。
        /// 槽位 FactionId 显式写死，绝不从 Controller、玩家标志或单位定义推断。
        /// </summary>
        private static EncounterDefinition BuildMainEncounter(DefinitionErrorCollector errors)
        {
            var boundary = new GridBoundaryDefinition(
                LegacyTypeConversion.ToLogicGridPoint(MainSceneBoundaryMin),
                LegacyTypeConversion.ToLogicGridPoint(MainSceneBoundaryMax));

            var slots = new List<EncounterUnitSlot>
            {
                new EncounterUnitSlot(
                    new EncounterSlotId(LegacyIdMigrationManifest.SlotHero),
                    new UnitDefinitionId(LegacyIdMigrationManifest.UnitHeroRadius1),
                    FactionIds.Hero,
                    new ProjectHero.Logic.Grid.GridPoint(-5, -5),
                    ProjectHero.Logic.Grid.GridDirection.East),
                new EncounterUnitSlot(
                    new EncounterSlotId(LegacyIdMigrationManifest.SlotEnemy),
                    new UnitDefinitionId(LegacyIdMigrationManifest.UnitMonsterRadius2),
                    FactionIds.Monster,
                    new ProjectHero.Logic.Grid.GridPoint(5, 5),
                    ProjectHero.Logic.Grid.GridDirection.East)
            };

            var controllers = new List<ControllerBinding>
            {
                new ControllerBinding(
                    new ControllerId(LegacyIdMigrationManifest.ControllerPlayer),
                    CommandSourceKind.Player,
                    new List<EncounterSlotId> { new EncounterSlotId(LegacyIdMigrationManifest.SlotHero) }),
                new ControllerBinding(
                    new ControllerId(LegacyIdMigrationManifest.ControllerEnemyAi),
                    CommandSourceKind.Ai,
                    new List<EncounterSlotId> { new EncounterSlotId(LegacyIdMigrationManifest.SlotEnemy) })
            };

            var victory = new VictoryDefinition(
                new List<FactionId> { FactionIds.Hero },
                new List<FactionId> { FactionIds.Monster },
                VictoryResultCode: "victory",
                DefeatResultCode: "defeat",
                DrawResultCode: "draw");

            return new EncounterDefinition(
                new EncounterDefinitionId(LegacyIdMigrationManifest.MainEncounterId),
                boundary,
                BattleDefinitionAssembler.CanonicalizeSlots(slots),
                BattleDefinitionAssembler.CanonicalizeControllers(controllers),
                victory);
        }

        // ================= 私有助手 =================

        private static void TryAddUnit(
            SortedDictionary<string, UnitDefinition> units,
            string unitDefinitionId,
            string actionSetId,
            Compatibility.LegacyUnitStatInputs stats,
            string libraryGuid,
            DefinitionErrorCollector errors)
        {
            try
            {
                units[unitDefinitionId] =
                    LegacyTypeConversion.BuildUnitDefinition(unitDefinitionId, stats, actionSetId);
            }
            catch (LogicDefinitionException ex)
            {
                errors.Add(ex.ErrorCode, ex.Message, unitDefinitionId);
            }
        }

        private static void AddAction(
            SortedDictionary<string, ActionSpec> actions,
            ActionSpec spec,
            BattleRules rules,
            DefinitionErrorCollector errors)
        {
            string error = ActionDefinitionValidation.ValidateActionSpec(spec, rules, null, null);
            if (error != null)
            {
                errors.Add(error, spec.ActionSpecId.Value, "new-action");
                return;
            }
            actions[spec.ActionSpecId.Value] = spec;
        }

        private static List<UnitDefinition> ToUnitList(SortedDictionary<string, UnitDefinition> units)
        {
            var list = new List<UnitDefinition>(units.Values);
            list.Sort((a, b) => StringComparer.Ordinal.Compare(a.UnitDefinitionId.Value, b.UnitDefinitionId.Value));
            return list;
        }

        private static List<ActionSpec> ToActionList(SortedDictionary<string, ActionSpec> actions)
        {
            var list = new List<ActionSpec>(actions.Values);
            list.Sort((a, b) => StringComparer.Ordinal.Compare(a.ActionSpecId.Value, b.ActionSpecId.Value));
            return list;
        }

        private static List<T> ToValueList<T>(SortedDictionary<string, T> map)
            => new List<T>(map.Values);

        private static Dictionary<string, MovementPatternSpec> ToMovementDictionary(
            IReadOnlyList<MovementPatternSpec> patterns)
        {
            var result = new Dictionary<string, MovementPatternSpec>(StringComparer.Ordinal);
            if (patterns == null) return result;
            foreach (var pattern in patterns)
                result[pattern.MovementPatternId.Value] = pattern;
            return result;
        }

        private static void AddIfError(DefinitionErrorCollector errors, string error, string origin)
        {
            if (error != null) errors.Add(error, origin, "rules");
        }
    }
}
