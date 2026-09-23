using System;
using System.Collections.Generic;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Core.Physics;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Authoring
{
    /// <summary>
    /// 旧资产类型 → Logic 定义的纯函数转换。
    /// 不改旧资产、不猜默认值；任何无法确认的输入都以稳定原因码拒绝。
    ///
    /// 命名说明：本文件里的 <c>GridPoint</c>/<c>TrianglePoint</c>/<c>GridDirection</c> 指
    /// <c>ProjectHero.Core.*</c>（旧共享网格程序集，即资产里序列化的类型）；
    /// Logic 侧同名类型一律全限定为 <c>ProjectHero.Logic.Grid.*</c>。
    /// </summary>
    public static class LegacyTypeConversion
    {
        public const string LEGACY_ACTION_NOT_ATTACK = "LEGACY_ACTION_NOT_ATTACK";
        public const string LEGACY_PATTERN_MISSING = "LEGACY_PATTERN_MISSING";

        public static ProjectHero.Logic.Grid.TrianglePoint ToLogicPoint(
            ProjectHero.Core.Grid.TrianglePoint point)
            => new ProjectHero.Logic.Grid.TrianglePoint(point.X, point.Y, point.T);

        public static List<ProjectHero.Logic.Grid.TrianglePoint> ToLogicPoints(
            IReadOnlyList<ProjectHero.Core.Grid.TrianglePoint> points)
        {
            var result = new List<ProjectHero.Logic.Grid.TrianglePoint>();
            if (points == null) return result;
            foreach (var point in points) result.Add(ToLogicPoint(point));
            return result;
        }

        public static ProjectHero.Logic.Grid.GridPoint ToLogicGridPoint(ProjectHero.Core.Pathfinding.GridPoint point)
            => new ProjectHero.Logic.Grid.GridPoint(point.X, point.Y);

        /// <summary>旧 ImpactType → 伤害通道（拍板 B1：只写明确通道，无通用 DamageReduction）。</summary>
        public static DamageChannelId ChannelFor(ImpactType impactType)
            => LegacyDamageMapping.ChannelForImpactType(impactType);

        /// <summary>旧 ImpactType → 冲击 Profile（正交的动量系数，与伤害通道分开配置）。</summary>
        public static ImpactProfileId ProfileFor(ImpactType impactType)
            => LegacyDamageMapping.ProfileForImpactType(impactType);

        /// <summary>
        /// 旧攻击 Action → Attack 类型 ActionSpec。
        /// 秒→Tick 只在加载边界量化一次（BaseTime）；旧资产没有后摇字段，
        /// 因此 RecoveryTicks 取版本化默认值（不是运行时硬编码 0.5f）。
        /// 伤害通道与冲击 Profile 由旧 ImpactType 显式拆分；
        /// <paramref name="allowedTargetRelations"/> 由迁移清单逐条人工确认后传入，
        /// 绝不由 Logic 补"默认只打敌人"。
        /// </summary>
        public static ProjectHero.Logic.Actions.ActionSpec BuildAttackSpec(
            string actionSpecId,
            ProjectHero.Core.Actions.Action action,
            ProjectHero.Logic.Grid.AttackPatternSpec pattern,
            TargetRelationMask allowedTargetRelations,
            BattleRules rules)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (action.Type != ProjectHero.Core.Actions.ActionType.Attack)
                throw new LogicDefinitionException(LEGACY_ACTION_NOT_ATTACK, action.Type.ToString());
            if (pattern == null)
                throw new LogicDefinitionException(LEGACY_PATTERN_MISSING, action.Name ?? actionSpecId);

            int baseWindupTicks = LegacyTimingQuantization.SecondsToTicks(action.BaseTime, rules);
            var timing = new AttackTimingSpec(baseWindupTicks, rules.DefaultAttackRecoveryTicks);

            DamageChannelId channel = ChannelFor(action.ImpactType);
            var components = new[]
            {
                new DamageComponentSpec(channel, action.BaseDamage, DamageChannelCatalog.GetDefaultTags(channel))
            };

            var payload = new AttackPayloadSpec(
                components,
                ProfileFor(action.ImpactType),
                action.ForceMultiplier,
                TargetPolicy.AllTargetsInArea,      // 旧命中候选 = 区域内任意非自身单位
                allowedTargetRelations,
                MomentumDirectionOffsetSteps: 0,
                pattern,
                AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable);

            return new ProjectHero.Logic.Actions.ActionSpec(new ActionSpecId(actionSpecId), ProjectHero.Logic.Actions.ActionType.Attack,
                timing, payload, AdrenalineCost: 0);
        }

        /// <summary>旧单位属性数值子集 → UnitDefinition（一次性映射，战斗开始后不回读旧组件）。</summary>
        public static UnitDefinition BuildUnitDefinition(
            string unitDefinitionId,
            Compatibility.LegacyUnitStatInputs stats,
            string actionSetId)
            => LegacyUnitStatsAdapter.ConvertStats(unitDefinitionId, stats, actionSetId);
    }
}
