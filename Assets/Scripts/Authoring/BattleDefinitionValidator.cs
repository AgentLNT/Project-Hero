using System;
using System.Collections.Generic;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring
{
    /// <summary>
    /// 定义级校验集合。每个方法只<strong>记录</strong>错误、从不抛异常、
    /// 也从不修改输入，因此调用顺序不影响错误内容；错误最终由收集器统一排序。
    ///
    /// 这里实现的是"错误模型"层面的拒绝，
    /// 与 <c>Logic</c> 侧的类型级校验（ActionDefinitionValidation 等）互补：
    /// Logic 负责单个定义自洽，Authoring 负责跨定义引用与闭合性。
    /// </summary>
    public static class BattleDefinitionValidator
    {
        // ================= Faction 矩阵 =================

        /// <summary>
        /// 校验 N 个阵营恰好有 N*(N-1)/2 条规范无序关系：
        /// 拒绝空/非法 ID、重复阵营、自关系、反向重复（B-A）、缺失、重复与悬空 Faction。
        /// 缺失不得用 Hostile/Neutral 补洞。
        /// </summary>
        public static void ValidateFactionModel(
            IReadOnlyList<FactionDefinition> factions,
            IReadOnlyList<FactionRelationDefinition> relations,
            DefinitionErrorCollector errors)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            if (factions != null)
            {
                foreach (var faction in factions)
                {
                    string idError = DefinitionIdValidation.ValidateFormat(faction.FactionId.Value);
                    if (idError != null)
                    {
                        errors.Add(DefinitionCodes.DEFINITION_ID_INVALID, faction.FactionId.Value ?? "<null>", "faction");
                        continue;
                    }
                    if (!known.Add(faction.FactionId.Value))
                        errors.Add(DefinitionCodes.DEFINITION_ID_DUPLICATE, faction.FactionId.Value, "faction");
                }
            }

            var seenPairs = new HashSet<string>(StringComparer.Ordinal);
            if (relations != null)
            {
                foreach (var relation in relations)
                {
                    string a = relation.FactionAId.Value;
                    string b = relation.FactionBId.Value;

                    if (!known.Contains(a) || !known.Contains(b))
                    {
                        errors.Add(FactionCodes.FACTION_RELATION_UNKNOWN_ID, a + "|" + b, "relation");
                        continue;
                    }
                    if (string.Equals(a, b, StringComparison.Ordinal))
                    {
                        errors.Add(FactionCodes.FACTION_RELATION_NOT_CANONICAL, a + "|" + b, "relation");
                        continue;
                    }
                    if (StringComparer.Ordinal.Compare(a, b) > 0)
                    {
                        errors.Add(FactionCodes.FACTION_RELATION_NOT_CANONICAL, a + "|" + b, "relation");
                        continue;
                    }
                    if (!seenPairs.Add(a + "|" + b))
                        errors.Add(FactionCodes.FACTION_RELATION_DUPLICATE, a + "|" + b, "relation");
                }
            }

            // 完备性：每个无序对都必须恰好出现一次。
            var expected = new List<string>();
            var knownList = new List<string>(known);
            knownList.Sort(StringComparer.Ordinal);
            for (int i = 0; i < knownList.Count; i++)
            {
                for (int j = i + 1; j < knownList.Count; j++)
                {
                    expected.Add(knownList[i] + "|" + knownList[j]);
                }
            }

            foreach (var pair in expected)
            {
                if (!seenPairs.Contains(pair))
                    errors.Add(FactionCodes.FACTION_RELATION_MISSING, pair, "relation");
            }

            if (relations != null && relations.Count != expected.Count)
                errors.Add(FactionCodes.FACTION_RELATION_MISSING,
                    "expected " + expected.Count + " relations, got " + relations.Count, "relation");
        }

        /// <summary>
        /// 校验胜负定义的 Allied/Hostile 两个目标集合：
        /// 非空、互斥、无重复、只引用已知阵营，且
        /// Allied 组内部互为 Allied、每个 Allied—Hostile 跨组对互为 Hostile。
        /// Hostile 组内部不附加关系约束（保持矩阵原值）。
        /// </summary>
        public static void ValidateVictory(
            VictoryDefinition victory,
            FactionModelDefinition model,
            DefinitionErrorCollector errors,
            string origin)
        {
            if (victory == null)
            {
                errors.Add(DefinitionCodes.VICTORY_FACTION_SET_EMPTY, "victory is null", origin);
                return;
            }

            var allied = new List<string>();
            var hostile = new List<string>();
            var alliedSet = new HashSet<string>(StringComparer.Ordinal);
            var hostileSet = new HashSet<string>(StringComparer.Ordinal);

            CollectFactionSet(victory.AlliedFactionIds, allied, alliedSet, "allied", errors, origin);
            CollectFactionSet(victory.HostileFactionIds, hostile, hostileSet, "hostile", errors, origin);

            if (allied.Count == 0 || hostile.Count == 0)
                errors.Add(DefinitionCodes.VICTORY_FACTION_SET_EMPTY,
                    "allied=" + allied.Count + ", hostile=" + hostile.Count, origin);

            foreach (var faction in allied)
            {
                if (hostileSet.Contains(faction))
                    errors.Add(DefinitionCodes.VICTORY_FACTION_SET_OVERLAP, faction, origin);
            }

            foreach (var faction in allied)
            {
                if (model != null && !model.ContainsFaction(new FactionId(faction)))
                    errors.Add(DefinitionCodes.VICTORY_FACTION_SET_DANGLING, faction, origin);
            }
            foreach (var faction in hostile)
            {
                if (model != null && !model.ContainsFaction(new FactionId(faction)))
                    errors.Add(DefinitionCodes.VICTORY_FACTION_SET_DANGLING, faction, origin);
            }

            // Allied 组内部必须互为 Allied。
            for (int i = 0; i < allied.Count; i++)
            {
                for (int j = i + 1; j < allied.Count; j++)
                {
                    if (!HasRelation(model, allied[i], allied[j], FactionDisposition.Allied))
                        errors.Add(FactionCodes.VICTORY_FACTION_RELATION_CONFLICT,
                            allied[i] + "|" + allied[j] + " must be Allied", origin);
                }
            }

            // 每个 Allied—Hostile 跨组对必须互为 Hostile。
            foreach (var a in allied)
            {
                foreach (var h in hostile)
                {
                    if (!HasRelation(model, a, h, FactionDisposition.Hostile))
                        errors.Add(FactionCodes.VICTORY_FACTION_RELATION_CONFLICT,
                            a + "|" + h + " must be Hostile", origin);
                }
            }

            if (string.IsNullOrEmpty(victory.VictoryResultCode) ||
                string.IsNullOrEmpty(victory.DefeatResultCode) ||
                string.IsNullOrEmpty(victory.DrawResultCode))
                errors.Add(DefinitionCodes.VICTORY_RESULT_CODE_MISSING, "victory/defeat/draw", origin);
        }

        // ================= Encounter =================

        public static void ValidateEncounter(
            EncounterDefinition encounter,
            FactionModelDefinition model,
            DefinitionErrorCollector errors)
        {
            if (encounter == null)
            {
                errors.Add(DefinitionCodes.ENCOUNTER_EMPTY, "encounter is null");
                return;
            }

            string origin = encounter.EncounterId.Value ?? "<null>";
            string idError = DefinitionIdValidation.ValidateFormat(encounter.EncounterId.Value);
            if (idError != null)
                errors.Add(DefinitionCodes.DEFINITION_ID_INVALID, encounter.EncounterId.Value ?? "<null>", "encounter");

            string boundaryError = encounter.GridBoundary == null
                ? GridBoundaryDefinition.GRID_BOUNDARY_INVALID
                : encounter.GridBoundary.Validate();
            if (boundaryError != null)
                errors.Add(boundaryError, "grid boundary", origin);

            string slotError = ProjectHero.Logic.Encounter.EncounterSlotValidation.ValidateSlots(encounter.Slots);
            if (slotError != null)
                errors.Add(slotError, "slots", origin);

            if (encounter.Slots == null || encounter.Slots.Count == 0)
                errors.Add(DefinitionCodes.ENCOUNTER_EMPTY, "no slots", origin);

            if (encounter.Slots != null)
            {
                foreach (var slot in encounter.Slots)
                {
                    if (model == null || !model.ContainsFaction(slot.FactionId))
                        errors.Add(DefinitionCodes.ENCOUNTER_SLOT_FACTION_UNKNOWN,
                            slot.FactionId.Value ?? "<null>", slot.SlotId.Value);

                    if (encounter.GridBoundary != null && !encounter.GridBoundary.Contains(slot.InitialPosition))
                        errors.Add(DefinitionCodes.ENCOUNTER_SLOT_OUT_OF_BOUNDARY,
                            slot.InitialPosition.ToString(), slot.SlotId.Value);
                }
            }

            ValidateControllerBindings(encounter, errors);
            ValidateVictory(encounter.Victory, model, errors, origin);
        }

        /// <summary>
        /// ControllerBinding 校验：
        /// ControllerId 合法且唯一；<c>System</c> 来源（外部可伪造）必须拒绝；
        /// 绑定集合非空；槽位必须存在（悬空拒绝）；同一槽位不得被多个 Controller 控制（歧义拒绝）。
        /// </summary>
        public static void ValidateControllerBindings(
            EncounterDefinition encounter,
            DefinitionErrorCollector errors)
        {
            string origin = encounter.EncounterId.Value ?? "<null>";
            var slotIds = new HashSet<string>(StringComparer.Ordinal);
            if (encounter.Slots != null)
            {
                foreach (var slot in encounter.Slots) slotIds.Add(slot.SlotId.Value);
            }

            var controllerIds = new HashSet<string>(StringComparer.Ordinal);
            var claimedSlots = new Dictionary<string, string>(StringComparer.Ordinal);

            if (encounter.Controllers == null || encounter.Controllers.Count == 0) return;

            foreach (var binding in encounter.Controllers)
            {
                string controllerOrigin = binding.ControllerId.Value ?? "<null>";

                string idError = DefinitionIdValidation.ValidateFormat(binding.ControllerId.Value);
                if (idError != null)
                {
                    errors.Add(DefinitionCodes.CONTROLLER_ID_INVALID, controllerOrigin, origin);
                    continue;
                }
                if (!controllerIds.Add(controllerOrigin))
                    errors.Add(DefinitionCodes.CONTROLLER_ID_DUPLICATE, controllerOrigin, origin);

                if (binding.SourceKind == CommandSourceKind.System)
                    errors.Add(DefinitionCodes.CONTROLLER_SYSTEM_SOURCE_REJECTED, controllerOrigin, origin);

                if (binding.ControlledSlots == null || binding.ControlledSlots.Count == 0)
                {
                    errors.Add(DefinitionCodes.CONTROLLER_BINDING_EMPTY, controllerOrigin, origin);
                    continue;
                }

                foreach (var slotId in binding.ControlledSlots)
                {
                    string slot = slotId.Value ?? "<null>";
                    if (!slotIds.Contains(slot))
                    {
                        errors.Add(DefinitionCodes.CONTROLLER_SLOT_DANGLING, slot, controllerOrigin);
                        continue;
                    }
                    if (claimedSlots.TryGetValue(slot, out var owner))
                    {
                        errors.Add(DefinitionCodes.CONTROLLER_SLOT_AMBIGUOUS,
                            slot + " claimed by " + owner + " and " + controllerOrigin, origin);
                        continue;
                    }
                    claimedSlots[slot] = controllerOrigin;
                }
            }
        }

        // ================= 动作集合 / 单位 / 反应闭合 =================

        /// <summary>
        /// 同 ID 内容冲突检测（主方案 2.3 / 任务包 ID 迁移规则）：
        /// 一个最终 ID 只能对应一个规范化定义；发现同 ID 不同内容时以
        /// <see cref="DefinitionCodes.DEFINITION_ID_CONTENT_CONFLICT"/> 稳定失败。
        ///
        /// <paramref name="elements"/> 的每一项是 (最终 ID, 规范内容签名)。
        /// 签名必须只由<strong>规范内容</strong>构成（ID、数值、规范化后的表），
        /// 不含资产路径、显示名或发现顺序——这样"逐字相同的重复"不会被误判为冲突。
        /// </summary>
        public static void DetectContentConflicts(
            IReadOnlyList<(string Id, string[] Signature)> elements,
            DefinitionErrorCollector errors)
        {
            if (elements == null) return;

            var firstSignatureById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in elements)
            {
                string id = element.Id ?? string.Empty;
                string signature = string.Join("|", element.Signature ?? new string[0]);

                if (firstSignatureById.TryGetValue(id, out string existing))
                {
                    if (!string.Equals(existing, signature, StringComparison.Ordinal))
                        errors.Add(DefinitionCodes.DEFINITION_ID_CONTENT_CONFLICT, id, "content");
                    continue;
                }
                firstSignatureById[id] = signature;
            }
        }

        /// <summary>动作集合必须非空、无重复、且只引用已定义动作。</summary>
        public static void ValidateActionSets(
            IReadOnlyDictionary<string, ActionSetDefinition> actionSets,
            IReadOnlyDictionary<string, ActionSpec> actions,
            DefinitionErrorCollector errors)
        {
            if (actionSets == null) return;
            foreach (var pair in actionSets)
            {
                var set = pair.Value;
                string origin = set.ActionSetId.Value ?? "<null>";

                string idError = DefinitionIdValidation.ValidateFormat(set.ActionSetId.Value);
                if (idError != null)
                    errors.Add(DefinitionCodes.DEFINITION_ID_INVALID, origin, "action_set");

                if (set.ActionSpecIds == null || set.ActionSpecIds.Count == 0)
                {
                    errors.Add(DefinitionCodes.ACTION_SET_EMPTY, origin, "action_set");
                    continue;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var actionId in set.ActionSpecIds)
                {
                    if (!seen.Add(actionId.Value ?? string.Empty))
                        errors.Add(DefinitionCodes.ACTION_SET_DUPLICATE_ACTION, actionId.Value, origin);
                    if (actions == null || !actions.ContainsKey(actionId.Value ?? string.Empty))
                        errors.Add(DefinitionCodes.ACTION_SET_REFERENCES_UNKNOWN_ACTION, actionId.Value, origin);
                }
            }
        }

        /// <summary>单位定义必须合法，且其动作集合引用已定义。</summary>
        public static void ValidateUnits(
            IReadOnlyDictionary<string, UnitDefinition> units,
            IReadOnlyDictionary<string, ActionSetDefinition> actionSets,
            DefinitionErrorCollector errors)
        {
            if (units == null) return;
            foreach (var pair in units)
            {
                var unit = pair.Value;
                string origin = unit.UnitDefinitionId.Value ?? "<null>";

                string unitError = UnitDefinitionValidation.Validate(unit);
                if (unitError != null) errors.Add(unitError, origin, "unit");

                if (actionSets == null || !actionSets.ContainsKey(unit.ActionSetId.Value ?? string.Empty))
                    errors.Add(DefinitionCodes.DEFINITION_REFERENCE_DANGLING,
                        "action_set:" + (unit.ActionSetId.Value ?? "<null>"), origin);
            }
        }

        /// <summary>每个 AttackPatternId 引用必须存在（悬空拒绝）。</summary>
        public static void ValidateReferences(
            IReadOnlyDictionary<string, ActionSpec> actions,
            IReadOnlyDictionary<string, AttackPatternSpec> patterns,
            IReadOnlyDictionary<string, VolumeSpec> volumes,
            IReadOnlyDictionary<string, MovementPatternSpec> movementPatterns,
            DefinitionErrorCollector errors)
        {
            if (actions == null) return;
            foreach (var pair in actions)
            {
                string origin = pair.Key;
                var spec = pair.Value;

                switch (spec.Payload)
                {
                    case AttackPayloadSpec attack:
                        if (patterns == null || attack.Pattern == null ||
                            !patterns.ContainsKey(attack.Pattern.AttackPatternId.Value ?? string.Empty))
                            errors.Add(DefinitionCodes.DEFINITION_REFERENCE_DANGLING,
                                "attack_pattern:" + (attack.Pattern?.AttackPatternId.Value ?? "<null>"), origin);
                        break;
                    case MovePayloadSpec move:
                        ValidateMovementPatternReference(move.Pattern, movementPatterns, errors, origin);
                        break;
                    case DodgePayloadSpec dodge:
                        ValidateMovementPatternReference(dodge.Pattern, movementPatterns, errors, origin);
                        break;
                }
            }

            if (volumes != null)
            {
                foreach (var pair in volumes)
                {
                    if (pair.Value == null || pair.Value.Directions == null || pair.Value.Directions.Count == 0)
                        errors.Add(DefinitionCodes.DEFINITION_REFERENCE_DANGLING,
                            "volume:" + pair.Key, "volume");
                }
            }
        }

        /// <summary>
        /// 反应闭合校验：
        /// ① 每个可反应（Reactable）攻击必须有足够的 MinimumReactionLeadTicks，
        ///    否则必须显式标为不可反应；
        /// ② 每个 Block/Dodge 动作必须位于至少一个单位的 ActionSet 中；
        /// ③ 反应动作的费用与标签必须闭合（费用为正、奖励小于费用在 AdrenalineRules 侧校验）。
        /// ActionSet 不因单位由玩家还是 AI 控制而不同（结构上只依赖内容配置）。
        /// </summary>
        public static void ValidateReactionClosure(
            IReadOnlyDictionary<string, ActionSpec> actions,
            IReadOnlyDictionary<string, ActionSetDefinition> actionSets,
            AdrenalineRules adrenalineRules,
            ReactionRules reactionRules,
            DefinitionErrorCollector errors)
        {
            if (actions != null)
            {
                foreach (var pair in actions)
                {
                    if (!(pair.Value?.Payload is AttackPayloadSpec attack)) continue;
                    if (pair.Value.Type != ProjectHero.Logic.Actions.ActionType.Attack) continue;

                    bool reactable = (attack.Tags & AttackTagMask.Reactable) != 0;
                    bool hasDefensiveOption =
                        (attack.Tags & (AttackTagMask.Blockable | AttackTagMask.Dodgeable)) != 0;

                    if (!reactable) continue; // Unreactable 攻击不需要提前量。

                    if (!hasDefensiveOption)
                    {
                        errors.Add(ActionDefinitionCodes.ATTACK_TAGS_INVALID,
                            "Reactable attack declares no Blockable/Dodgeable option", pair.Key);
                        continue;
                    }

                    if (!(pair.Value.Timing is AttackTimingSpec attackTiming)) continue;

                    int requiredLead = reactionRules?.MinimumReactionLeadTicks ?? 0;
                    if (attackTiming.BaseWindupTicks < requiredLead)
                        errors.Add(DefinitionCodes.REACTABLE_ATTACK_REQUIRES_MINIMUM_LEAD,
                            "windup=" + attackTiming.BaseWindupTicks + " < required=" + requiredLead, pair.Key);
                }
            }

            // ② Block/Dodge 必须至少出现在一个动作集合中；费用与奖励必须闭合。
            var availableAnywhere = new HashSet<string>(StringComparer.Ordinal);
            if (actionSets != null)
            {
                foreach (var set in actionSets.Values)
                {
                    if (set.ActionSpecIds == null) continue;
                    foreach (var id in set.ActionSpecIds) availableAnywhere.Add(id.Value ?? string.Empty);
                }
            }

            if (actions != null)
            {
                foreach (var pair in actions)
                {
                    var spec = pair.Value;
                    if (spec == null) continue;
                    if (spec.Type != ProjectHero.Logic.Actions.ActionType.Block &&
                        spec.Type != ProjectHero.Logic.Actions.ActionType.Dodge) continue;

                    if (!availableAnywhere.Contains(spec.ActionSpecId.Value ?? string.Empty))
                        errors.Add(DefinitionCodes.REACTION_ACTION_NOT_IN_ANY_ACTION_SET,
                            spec.ActionSpecId.Value, "reaction");

                    string costError = adrenalineRules?.ValidateReactionCost(spec.Type, spec.AdrenalineCost);
                    if (costError != null) errors.Add(costError, spec.ActionSpecId.Value, "reaction");
                }
            }
        }

        // ================= 私有助手 =================

        private static void ValidateMovementPatternReference(
            MovementPatternSpec pattern,
            IReadOnlyDictionary<string, MovementPatternSpec> movementPatterns,
            DefinitionErrorCollector errors,
            string origin)
        {
            if (pattern == null ||
                movementPatterns == null ||
                !movementPatterns.ContainsKey(pattern.MovementPatternId.Value ?? string.Empty))
                errors.Add(DefinitionCodes.DEFINITION_REFERENCE_DANGLING,
                    "movement_pattern:" + (pattern?.MovementPatternId.Value ?? "<null>"), origin);
        }

        private static void CollectFactionSet(
            IReadOnlyList<FactionId> source,
            List<string> ordered,
            HashSet<string> set,
            string label,
            DefinitionErrorCollector errors,
            string origin)
        {
            if (source == null) return;
            foreach (var faction in source)
            {
                string id = faction.Value ?? "<null>";
                if (!set.Add(id))
                {
                    errors.Add(DefinitionCodes.DEFINITION_ID_DUPLICATE, label + ":" + id, origin);
                    continue;
                }
                ordered.Add(id);
            }
            ordered.Sort(StringComparer.Ordinal);
        }

        private static bool HasRelation(
            FactionModelDefinition model, string a, string b, FactionDisposition expected)
        {
            if (model == null) return false;
            return model.TryGetDisposition(new FactionId(a), new FactionId(b), out var disposition) &&
                   disposition == expected;
        }
    }
}
