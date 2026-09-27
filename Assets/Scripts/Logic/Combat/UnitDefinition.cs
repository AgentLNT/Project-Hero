using System.Collections.Generic;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 单位定义（主方案 3.1.1）。Mass × MomentumSpeed 只服务动量/硬直/击倒/击退量化；
    /// ActionSpeed 只修正攻击前摇；MoveSpeed 只修正普通 Move 每权重单位 Tick，不参与选路。
    /// 被动抵抗按 DamageChannelId 索引，范围 [0, 1024]（Q10），缺失通道视为 0；
    /// 不存在通用 DamageReduction。
    ///
    /// <see cref="InitialHealth"/> 是 Authoring 在加载边界由旧 <c>MaxHealth</c>
    /// （Constitution × 20）一次性量化得到的首版初始生命：它属于配置数据并进入
    /// BattleDefinitionHash，Logic 侧不得再实现第二套生命派生公式。
    /// <see cref="ActionSetId"/> 指向该单位理论可用的动作集合；它与单位的控制者类型无关。
    ///
    /// <see cref="VolumeSpecId"/>（任务 06 补上的绑定）指向该单位参与占位/区域查询时消费的
    /// 任务 02B 规范 12 向体积表。它是<strong>定义级</strong>绑定：Logic 侧不再从资产 GUID、
    /// 资产名或旧组件推断体积，装配期只做一次 ID 解析，运行时只按方向索引 + 整数平移。
    ///
    /// <strong>哈希参与（任务 02B 定义哈希修订）</strong>：本字段<strong>进入</strong>
    /// <see cref="WriteHashComponents"/>，组件名 <c>unit.volume_spec_id</c>。
    /// 理由是它<strong>直接决定</strong>单位占位 footprint 与命中资格，属于"会影响玩法结果的
    /// 定义"；不进哈希的话，"两个定义只有单位↔体积表绑定不同"就无法被
    /// <c>BattleDefinitionHash</c> 区分。该修订把主战斗定义摘要从
    /// <c>d9324383b2622148</c> 更新为 <c>a10fcfb98357418c</c>（同一次修订还把
    /// 库↔体积显式绑定条数 <c>definition.library_volume_binding_count</c> 纳入哈希），
    /// 影响面与理由见 <c>06-交接记录.md</c> §10.3 与 <c>02B-配置迁移记录.md</c> §11。
    /// </summary>
    public sealed record UnitDefinition(
        UnitDefinitionId UnitDefinitionId,
        float Mass,
        float MomentumSpeed,
        float ActionSpeed,
        float MoveSpeed,
        IReadOnlyDictionary<DamageChannelId, int> BaseDamageResistanceQ10,
        float InitialHealth = 200f,
        Ids.ActionSetId ActionSetId = default,
        Ids.VolumeSpecId VolumeSpecId = default)
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("unit.definition_id", UnitDefinitionId.Value ?? string.Empty);
            writer.Write("unit.mass", (double)Mass);
            writer.Write("unit.momentum_speed", (double)MomentumSpeed);
            writer.Write("unit.action_speed", (double)ActionSpeed);
            writer.Write("unit.move_speed", (double)MoveSpeed);
            writer.Write("unit.initial_health", (double)InitialHealth);
            writer.Write("unit.action_set_id", ActionSetId.Value ?? string.Empty);

            // 任务 02B 定义哈希修订：单位 → 体积规范表的绑定进哈希。
            // 该绑定直接决定占位 footprint 与命中资格，属"会影响玩法结果的定义"。
            // 写入的是 Logic ID（不写资产路径/显示名/加载顺序），因此改写 Builder
            // 但产出同一规范绑定时哈希稳定。
            writer.Write("unit.volume_spec_id", VolumeSpecId.Value ?? string.Empty);

            if (BaseDamageResistanceQ10 != null)
            {
                var channels = new List<DamageChannelId>(BaseDamageResistanceQ10.Keys);
                channels.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.Value, b.Value));
                foreach (var channel in channels)
                {
                    writer.Write("unit.base_resistance." + channel.Value, BaseDamageResistanceQ10[channel]);
                }
            }
        }
    }

    /// <summary>
    /// 单位初始快照（主方案 3.1.1 / 00 号规则 31）：
    /// 携带首版不可变的 FactionId、初始位置/朝向、AvailableAdrenaline 与 AdrenalineCycleId。
    /// </summary>
    public sealed record UnitInitialSnapshot(
        UnitDefinitionId DefinitionId,
        FactionId FactionId,
        GridPoint InitialPosition,
        GridDirection InitialFacing,
        float InitialHealth,
        int AvailableAdrenaline,
        long AdrenalineCycleId);

    public static class UnitDefinitionCodes
    {
        public const string UNIT_ID_INVALID = "UNIT_ID_INVALID";
        public const string UNIT_MASS_INVALID = "UNIT_MASS_INVALID";
        public const string UNIT_MOMENTUM_SPEED_INVALID = "UNIT_MOMENTUM_SPEED_INVALID";
        public const string UNIT_ACTION_SPEED_INVALID = "UNIT_ACTION_SPEED_INVALID";
        public const string UNIT_MOVE_SPEED_INVALID = "UNIT_MOVE_SPEED_INVALID";
        public const string UNIT_RESISTANCE_OUT_OF_RANGE = "UNIT_RESISTANCE_OUT_OF_RANGE";
        public const string UNIT_RESISTANCE_CHANNEL_INVALID = "UNIT_RESISTANCE_CHANNEL_INVALID";
        public const string UNIT_INITIAL_HEALTH_INVALID = "UNIT_INITIAL_HEALTH_INVALID";
        public const string UNIT_ACTION_SET_ID_INVALID = "UNIT_ACTION_SET_ID_INVALID";

        /// <summary>任务 06：体积规范表引用格式非法（空表示"未绑定"，由悬空引用校验负责）。</summary>
        public const string UNIT_VOLUME_SPEC_ID_INVALID = "UNIT_VOLUME_SPEC_ID_INVALID";
    }

    public static class UnitDefinitionValidation
    {
        /// <summary>
        /// 单位定义校验：Mass/MomentumSpeed/ActionSpeed/MoveSpeed 有限正数；
        /// 分通道被动抵抗位于 [0, 1024]；通道 ID 格式合法。
        /// </summary>
        public static string Validate(UnitDefinition definition)
        {
            if (DefinitionIdValidation.ValidateFormat(definition.UnitDefinitionId.Value) != null)
                return UnitDefinitionCodes.UNIT_ID_INVALID;

            if (!IsFinitePositive(definition.Mass)) return UnitDefinitionCodes.UNIT_MASS_INVALID;
            if (!IsFinitePositive(definition.MomentumSpeed)) return UnitDefinitionCodes.UNIT_MOMENTUM_SPEED_INVALID;
            if (!IsFinitePositive(definition.ActionSpeed)) return UnitDefinitionCodes.UNIT_ACTION_SPEED_INVALID;
            if (!IsFinitePositive(definition.MoveSpeed)) return UnitDefinitionCodes.UNIT_MOVE_SPEED_INVALID;

            // 被动抵抗范围优先于 02B 新增字段校验：保持任务 02 已冻结的错误优先级
            // （越过 Q10 范围必须报 UNIT_RESISTANCE_OUT_OF_RANGE）。
            if (definition.BaseDamageResistanceQ10 != null)
            {
                foreach (var pair in definition.BaseDamageResistanceQ10)
                {
                    if (DefinitionIdValidation.ValidateFormat(pair.Key.Value) != null)
                        return UnitDefinitionCodes.UNIT_RESISTANCE_CHANNEL_INVALID;
                    if (pair.Value < 0 || pair.Value > 1024)
                        return UnitDefinitionCodes.UNIT_RESISTANCE_OUT_OF_RANGE;
                }
            }

            if (!IsFinitePositive(definition.InitialHealth)) return UnitDefinitionCodes.UNIT_INITIAL_HEALTH_INVALID;

            // ActionSetId 在本任务引入；为空表示"该定义未绑定动作集合"，
            // 该情形由 Builder 的跨定义引用校验（DEFINITION_REFERENCE_DANGLING）负责拒绝，
            // 不属于单定义自洽性。非空时必须是合法 ID 格式。
            if (!string.IsNullOrEmpty(definition.ActionSetId.Value) &&
                DefinitionIdValidation.ValidateFormat(definition.ActionSetId.Value) != null)
                return UnitDefinitionCodes.UNIT_ACTION_SET_ID_INVALID;

            // 任务 06：体积规范表引用。空 = 未绑定（由 Builder 的跨定义引用校验拒绝），
            // 非空必须是合法 ID 格式。
            if (!string.IsNullOrEmpty(definition.VolumeSpecId.Value) &&
                DefinitionIdValidation.ValidateFormat(definition.VolumeSpecId.Value) != null)
                return UnitDefinitionCodes.UNIT_VOLUME_SPEC_ID_INVALID;

            return null;
        }

        private static bool IsFinitePositive(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
