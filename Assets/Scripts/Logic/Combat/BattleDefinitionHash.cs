using System.Globalization;
using System.Text;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 规范哈希写入器：以 <c>component=value;</c> 的规范化文本累积，最终输出
    /// FNV-1a 64 位十六进制摘要。所有多值组件（枚举、浮点、long）均用
    /// InvariantCulture 与稳定格式写入；组件顺序即写入顺序。
    /// 任务 02 只冻结规则组件的哈希参与方式；完整 BattleDefinitionHash 由任务 02B 组装。
    /// </summary>
    public sealed class CanonicalHashWriter
    {
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private readonly StringBuilder _builder = new StringBuilder(1024);

        public CanonicalHashWriter Write(string component, string value)
        {
            _builder.Append(component).Append('=').Append(value).Append(';');
            return this;
        }

        public CanonicalHashWriter Write(string component, long value)
            => Write(component, value.ToString(CultureInfo.InvariantCulture));

        public CanonicalHashWriter Write(string component, double value)
            => Write(component, value.ToString("R", CultureInfo.InvariantCulture));

        /// <summary>规范化文本（哈希输入；供 02B 组装与测试审计）。</summary>
        public string ToCanonicalText() => _builder.ToString();

        /// <summary>FNV-1a 64 位十六进制摘要。</summary>
        public string ToDigestHex()
        {
            ulong hash = FnvOffsetBasis;
            foreach (char c in _builder.ToString())
            {
                hash ^= (byte)c;
                hash *= FnvPrime;
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// BattleDefinitionHash 的分域组装助手。各规则记录通过 WriteHashComponents 贡献分量；
    /// 任务 02B 在此之上组装完整定义哈希（配置清单、关系矩阵、掩码、胜利分组等）。
    /// </summary>
    public static class BattleDefinitionHash
    {
        public static string Compute(CanonicalHashWriter writer) => writer.ToDigestHex();

        public static string OfBattleRules(BattleRules rules)
        {
            var writer = new CanonicalHashWriter();
            rules.WriteHashComponents(writer);
            return writer.ToDigestHex();
        }

        public static string OfAttackTimingRules(BattleRules rules, ProjectHero.Logic.Actions.AttackTimingSpec timing)
        {
            var writer = new CanonicalHashWriter();
            rules.WriteHashComponents(writer);
            timing.WriteHashComponents(writer);
            return writer.ToDigestHex();
        }

        public static string OfMoveTimingRules(BattleRules rules, ProjectHero.Logic.Actions.MoveTimingSpec timing)
        {
            var writer = new CanonicalHashWriter();
            rules.WriteHashComponents(writer);
            timing.WriteHashComponents(writer);
            return writer.ToDigestHex();
        }

        public static string OfReactionTimingRules(
            ReactionRules reactionRules,
            ProjectHero.Logic.Actions.BlockReactionTimingSpec blockTiming,
            ProjectHero.Logic.Actions.DodgeReactionTimingSpec dodgeTiming)
        {
            var writer = new CanonicalHashWriter();
            reactionRules.WriteHashComponents(writer);
            blockTiming.WriteHashComponents(writer);
            dodgeTiming.WriteHashComponents(writer);
            return writer.ToDigestHex();
        }

        public static string OfAdrenalineRules(AdrenalineRules rules)
        {
            var writer = new CanonicalHashWriter();
            rules.WriteHashComponents(writer);
            return writer.ToDigestHex();
        }

        /// <summary>
        /// 完整 <c>BattleDefinitionHash</c> 的组装（任务 02B「必须产出」5）。
        ///
        /// 覆盖范围（全部会改变玩法结果的定义、规则常量与显式初始配置）：
        /// 规则版本与 TicksPerSecond、BattleRules（含 PathCost/PathSearch/强制位移协议与停止原因编码、
        /// MaxAutomaticDeferralsPerPlan、最大排程视野）、并发行动定义（MetaResourceCost）、
        /// ReactionRules、AdrenalineRules、Faction 关系矩阵、伤害通道目录、冲击 Profile 目录、
        /// 单位定义、动作定义（含每个攻击的 AllowedTargetRelations 与 12 向最终整数表）、
        /// AttackPattern / Volume / MovementPattern 规范表、动作集合、状态效果、Encounter
        /// （含规范网格边界、槽位、控制者绑定、Victory 分组与显式结果码）、动态生成阵营策略。
        ///
        /// 不参与哈希：显示名、Asset 路径、导入/发现顺序、资产内点的原始排列、对象地址、
        /// Builder 实现细节。因此改写 Builder 但产生相同的规范表时哈希保持不变。
        ///
        /// 调用方必须传入已经规范化排序的集合（Authoring 的 Builder 负责），
        /// 本方法不做二次排序，以保证"同一规范输入必得同一哈希"。
        /// </summary>
        public static string Compute(
            string rulesVersion,
            int ticksPerSecond,
            BattleRules rules,
            Definitions.ConcurrentActionDefinition concurrentAction,
            ReactionRules reactionRules,
            AdrenalineRules adrenalineRules,
            Definitions.FactionModelDefinition factionModel,
            System.Collections.Generic.IReadOnlyList<Definitions.DamageChannelDefinition> damageChannels,
            System.Collections.Generic.IReadOnlyList<Definitions.ImpactProfileDefinition> impactProfiles,
            System.Collections.Generic.IReadOnlyList<UnitDefinition> units,
            System.Collections.Generic.IReadOnlyList<Actions.ActionSpec> actions,
            System.Collections.Generic.IReadOnlyList<Grid.AttackPatternSpec> attackPatterns,
            System.Collections.Generic.IReadOnlyList<Grid.VolumeSpec> volumes,
            System.Collections.Generic.IReadOnlyList<Grid.MovementPatternSpec> movementPatterns,
            System.Collections.Generic.IReadOnlyList<Definitions.ActionSetDefinition> actionSets,
            System.Collections.Generic.IReadOnlyList<Definitions.StatusEffectSpec> statusEffects,
            System.Collections.Generic.IReadOnlyList<Definitions.EncounterDefinition> encounters,
            Definitions.DynamicSpawnFactionPolicy defaultDynamicSpawnPolicy)
        {
            var writer = new CanonicalHashWriter();

            writer.Write("definition.rules_version", rulesVersion ?? string.Empty);
            writer.Write("definition.ticks_per_second", ticksPerSecond);

            // —— 版本化规则常量 ——
            rules?.WriteHashComponents(writer);
            concurrentAction?.WriteHashComponents(writer);
            reactionRules?.WriteHashComponents(writer);
            adrenalineRules?.WriteHashComponents(writer);

            // —— 方向枚举数值与顺序（GridDirection 0 → 11 冻结）——
            foreach (Grid.GridDirection direction in System.Enum.GetValues(typeof(Grid.GridDirection)))
            {
                writer.Write("grid.direction", direction.ToString() + ":" + (int)direction);
            }
            writer.Write("grid.direction_count", Grid.GridDirectionInfo.DirectionCount);

            // —— 阵营模型 ——
            factionModel?.WriteHashComponents(writer);

            // —— 目录 ——
            if (damageChannels != null)
            {
                foreach (var channel in damageChannels) channel?.WriteHashComponents(writer);
                writer.Write("damage_channel.count", damageChannels.Count);
            }
            if (impactProfiles != null)
            {
                foreach (var profile in impactProfiles) profile?.WriteHashComponents(writer);
                writer.Write("impact_profile.count", impactProfiles.Count);
            }

            // —— 单位 ——
            if (units != null)
            {
                foreach (var unit in units) unit?.WriteHashComponents(writer);
                writer.Write("unit.count", units.Count);
            }

            // —— 动作（含 AllowedTargetRelations 与 12 向最终表）——
            if (actions != null)
            {
                foreach (var action in actions) WriteActionHash(writer, action);
                writer.Write("action.count", actions.Count);
            }

            // —— 规范 12 向表 ——
            if (attackPatterns != null)
            {
                foreach (var pattern in attackPatterns)
                {
                    writer.Write("attack_pattern.id", pattern?.AttackPatternId.Value ?? string.Empty);
                    WriteDirections(writer, pattern?.Directions);
                }
                writer.Write("attack_pattern.count", attackPatterns.Count);
            }
            if (volumes != null)
            {
                foreach (var volume in volumes)
                {
                    writer.Write("volume.id", volume?.VolumeSpecId.Value ?? string.Empty);
                    WriteDirections(writer, volume?.Directions);
                }
                writer.Write("volume.count", volumes.Count);
            }
            if (movementPatterns != null)
            {
                foreach (var pattern in movementPatterns)
                {
                    writer.Write("movement_pattern.id", pattern?.MovementPatternId.Value ?? string.Empty);
                    WriteDirections(writer, pattern?.Directions);
                }
                writer.Write("movement_pattern.count", movementPatterns.Count);
            }

            // —— 动作集合 ——
            if (actionSets != null)
            {
                foreach (var set in actionSets) set?.WriteHashComponents(writer);
                writer.Write("action_set.count", actionSets.Count);
            }

            // —— 状态效果 ——
            if (statusEffects != null)
            {
                foreach (var effect in statusEffects) effect?.WriteHashComponents(writer);
                writer.Write("status_effect.count", statusEffects.Count);
            }

            // —— Encounter ——
            if (encounters != null)
            {
                foreach (var encounter in encounters) encounter?.WriteHashComponents(writer);
                writer.Write("encounter.count", encounters.Count);
            }

            // —— 动态生成阵营策略 ——
            defaultDynamicSpawnPolicy?.WriteHashComponents(writer);

            return writer.ToDigestHex();
        }

        /// <summary>动作定义的规范哈希分量：时序、载荷（含掩码与 Pattern）、费用。</summary>
        private static void WriteActionHash(CanonicalHashWriter writer, Actions.ActionSpec action)
        {
            if (action == null) return;

            writer.Write("action.id", action.ActionSpecId.Value ?? string.Empty);
            writer.Write("action.type", action.Type.ToString() + ":" + (int)action.Type);
            writer.Write("action.adrenaline_cost", action.AdrenalineCost);

            if (action.Timing != null)
                writer.Write("action.timing_spec", action.Timing.GetType().Name);

            switch (action.Timing)
            {
                case Actions.AttackTimingSpec attackTiming: attackTiming.WriteHashComponents(writer); break;
                case Actions.GuardTimingSpec guardTiming: guardTiming.WriteHashComponents(writer); break;
                case Actions.MoveTimingSpec moveTiming: moveTiming.WriteHashComponents(writer); break;
                case Actions.BlockReactionTimingSpec blockTiming: blockTiming.WriteHashComponents(writer); break;
                case Actions.DodgeReactionTimingSpec dodgeTiming: dodgeTiming.WriteHashComponents(writer); break;
            }

            switch (action.Payload)
            {
                case Actions.AttackPayloadSpec attack:
                    if (attack.DamageComponents != null)
                    {
                        foreach (var component in attack.DamageComponents)
                        {
                            writer.Write("action.damage_component",
                                (component.ChannelId.Value ?? string.Empty) + "|" +
                                component.RawAmount.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
                                (int)component.Tags);
                        }
                    }
                    writer.Write("action.impact_profile_id", attack.ImpactProfileId.Value ?? string.Empty);
                    writer.Write("action.force_multiplier",
                        attack.ForceMultiplier.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    writer.Write("action.target_policy", attack.TargetPolicy.ToString() + ":" + (int)attack.TargetPolicy);
                    writer.Write("action.allowed_target_relations", (int)attack.AllowedTargetRelations);
                    writer.Write("action.momentum_direction_offset_steps", attack.MomentumDirectionOffsetSteps);
                    writer.Write("action.attack_tags", (int)attack.Tags);
                    writer.Write("action.attack_pattern_id", attack.Pattern?.AttackPatternId.Value ?? string.Empty);
                    break;

                case Actions.GuardPayloadSpec guard:
                    if (guard.DamageResistanceQ10 != null)
                    {
                        var channels = new System.Collections.Generic.List<Ids.DamageChannelId>(guard.DamageResistanceQ10.Keys);
                        channels.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.Value, b.Value));
                        foreach (var channel in channels)
                            writer.Write("action.guard_resistance." + channel.Value, guard.DamageResistanceQ10[channel]);
                    }
                    writer.Write("action.guard_momentum_resistance_q10", guard.MomentumResistanceQ10);
                    writer.Write("action.guard_eligible_tags", (int)guard.EligibleTags);
                    break;

                case Actions.BlockPayloadSpec block:
                    // 合格抵抗由规则固定为 FullBlockResistanceQ10（已在 BattleRules 中哈希）。
                    writer.Write("action.block_eligible_tags", (int)block.EligibleTags);
                    writer.Write("action.block_resistance_q10", BattleRules.FullBlockResistanceQ10);
                    break;

                case Actions.MovePayloadSpec move:
                    writer.Write("action.move_max_path_weight_units", move.MaxPathWeightUnits);
                    writer.Write("action.move_pattern_id", move.Pattern?.MovementPatternId.Value ?? string.Empty);
                    break;

                case Actions.DodgePayloadSpec dodge:
                    writer.Write("action.dodge_max_distance_steps", dodge.MaxDistanceSteps);
                    writer.Write("action.dodge_pattern_id", dodge.Pattern?.MovementPatternId.Value ?? string.Empty);
                    break;
            }
        }

        /// <summary>规范 12 向表的哈希分量：方向索引 + 已排序的 (X, Y, T) 列表。</summary>
        private static void WriteDirections(
            CanonicalHashWriter writer,
            System.Collections.Generic.IReadOnlyList<Grid.DirectionalTriangleSet> directions)
        {
            if (directions == null)
            {
                writer.Write("directions.missing", "1");
                return;
            }

            writer.Write("directions.count", directions.Count);
            for (int i = 0; i < directions.Count; i++)
            {
                var set = directions[i];
                if (set == null)
                {
                    writer.Write("direction.null", i);
                    continue;
                }
                writer.Write("direction.index", (int)set.Direction);
                if (set.Triangles == null) continue;
                writer.Write("direction.point_count", set.Triangles.Count);
                foreach (var point in set.Triangles)
                {
                    writer.Write("direction.point", point.X + "," + point.Y + "," + point.T);
                }
            }
        }
    }
}
