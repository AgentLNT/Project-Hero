using System;
using ProjectHero.Core.Actions;
using ProjectHero.Core.Physics;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Core.Compatibility.Authoring
{
    /// <summary>
    /// 旧攻击 Action（ActionLibrarySO.ActionEntry.Data）→ 纯逻辑 ActionSpec 的兼容转换垂直切片
    /// （任务 02「必须产出」3）：
    /// - 旧 BaseTime（秒）在加载边界只量化一次 → BaseWindupTicks = RoundHalfUp(BaseTime × TicksPerSecond)；
    /// - 旧资产没有独立后摇字段 → RecoveryTicks 使用 BattleRules.DefaultAttackRecoveryTicks = 30，
    ///   不得继续由运行时调度器硬编码 0.5f；
    /// - 旧 ImpactType 拆为 DamageChannelId（伤害分量）+ ImpactProfileId（冲击 Profile）两个独立字段；
    /// - AllowedTargetRelations 由迁移清单显式提供（垂直切片用 01B 拍板 A3 的 {Hostile}）；
    /// - 转换结果做定义级校验，非法配置以稳定原因码拒绝，不钳制。
    /// 本适配器只证明接口可用，不替代任务 02B 的全资产迁移。
    /// </summary>
    public sealed class LegacyAttackDefinitionConverter
    {
        private readonly BattleRules _rules;

        /// <summary>秒→Tick 量化调用计数（证据：每个秒值在边界恰好量化一次）。</summary>
        public int QuantizationCallCount { get; private set; }

        public LegacyAttackDefinitionConverter(BattleRules rules)
        {
            _rules = rules ?? BattleRules.FrozenV1;
            QuantizationCallCount = 0;
        }

        /// <summary>把一条旧攻击动作转换为 Attack 类型的 ActionSpec。</summary>
        public ActionSpec ConvertAttack(
            string actionSpecId,
            string patternSpecId,
            ProjectHero.Core.Actions.Action action,
            TargetRelationMask allowedTargetRelations)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (action.Type != ProjectHero.Core.Actions.ActionType.Attack)
                throw new LogicDefinitionException(LegacyAuthoringCodes.LEGACY_ACTION_NOT_ATTACK,
                    action.Type.ToString());
            if (action.Pattern == null)
                throw new LogicDefinitionException(LegacyAuthoringCodes.LEGACY_PATTERN_MISSING, action.Name);

            // 1. BaseTime（秒）→ BaseWindupSeconds 语义；加载边界只量化一次。
            int baseWindupTicks = QuantizeSecondsOnce(action.BaseTime);

            // 2. 旧资产无独立后摇字段 → 版本化默认 30 Tick（替代运行时硬编码 0.5f）。
            int recoveryTicks = _rules.DefaultAttackRecoveryTicks;

            var timing = new AttackTimingSpec(baseWindupTicks, recoveryTicks);

            // 3. 旧 ImpactType 拆为伤害通道 + 冲击 Profile（正交）。
            DamageChannelId channel = LegacyDamageMapping.ChannelForImpactType(action.ImpactType);
            ImpactProfileId profile = LegacyDamageMapping.ProfileForImpactType(action.ImpactType);
            var components = new[]
            {
                new DamageComponentSpec(channel, action.BaseDamage, DamageChannelCatalog.GetDefaultTags(channel))
            };

            // 4. Pattern 经受检整数 60° 展开为规范 12 向表。
            var pattern = LegacyGridDefinitionConverter.ConvertPattern(patternSpecId, action.Pattern);

            var payload = new AttackPayloadSpec(
                components,
                profile,
                action.ForceMultiplier,
                TargetPolicy.AllTargetsInArea,      // 旧命中候选 = 区域内任意非自身单位（区域语义）
                allowedTargetRelations,             // 显式迁移清单提供；垂直切片 = {Hostile}（01B A3）
                MomentumDirectionOffsetSteps: 0,   // 普通直线攻击
                pattern,
                AttackTagMask.Reactable | AttackTagMask.Blockable | AttackTagMask.Dodgeable);

            var spec = new ActionSpec(
                new ActionSpecId(actionSpecId),
                ProjectHero.Logic.Actions.ActionType.Attack,
                timing,
                payload,
                AdrenalineCost: 0);

            // 5. 定义级校验：非法配置以稳定原因码拒绝（不钳制后继续模拟）。
            string error = ActionDefinitionValidation.ValidateActionSpec(
                spec, _rules, actionSpeed: null, moveSpeed: null);
            if (error != null)
                throw new LogicDefinitionException(error, $"actionSpecId={actionSpecId}");

            return spec;
        }

        /// <summary>加载边界唯一的秒→Tick 量化入口；每个调用恰好转换一个秒值。</summary>
        public int QuantizeSecondsOnce(double seconds)
        {
            QuantizationCallCount++;
            return LegacyTimingQuantization.SecondsToTicks(seconds, _rules);
        }
    }
}
