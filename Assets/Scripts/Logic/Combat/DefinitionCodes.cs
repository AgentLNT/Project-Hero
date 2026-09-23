using System.Collections.Generic;
using System;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 任务 02B 冻结的稳定失败码（Builder 与运行时共用的定义级错误模型）。
    ///
    /// 约定：错误列表按"稳定键"排序（错误码 Ordinal 升序，同码按 detail Ordinal 升序），
    /// 因此改变 Unity 资产发现顺序不会改变错误列表本身。
    /// 全部失败都必须以这些码稳定拒绝，不得钳制、跳过或回退默认值继续构建。
    /// </summary>
    public static class DefinitionCodes
    {
        // —— ID 迁移与重复（主方案 2.3 / 任务包 ID 迁移规则）——
        public const string DEFINITION_ID_INVALID = "DEFINITION_ID_INVALID";
        public const string DEFINITION_ID_DUPLICATE = "DEFINITION_ID_DUPLICATE";
        public const string DEFINITION_ID_CONTENT_CONFLICT = "DEFINITION_ID_CONTENT_CONFLICT";
        /// <summary>旧局部 ID（例如 QuickSlash）不得被当作全局 ActionSpecId 直接使用。</summary>
        public const string LEGACY_LOCAL_ID_NOT_GLOBAL = "LEGACY_LOCAL_ID_NOT_GLOBAL";
        /// <summary>旧局部 ID 在多个库中出现且映射清单未显式声明区分方式。</summary>
        public const string LEGACY_LOCAL_ID_AMBIGUOUS = "LEGACY_LOCAL_ID_AMBIGUOUS";
        /// <summary>被引用的旧条目在迁移清单里没有任何映射。</summary>
        public const string LEGACY_ID_MIGRATION_MISSING = "LEGACY_ID_MIGRATION_MISSING";
        /// <summary>
        /// 构建器<strong>绝不</strong>使用的码：它只用于测试断言"被 Encounter 引用的资产
        /// 没有被静默跳过"。真实拒绝一律用上面那些具体码。
        /// </summary>
        public const string DEFINITION_SILENTLY_SKIPPED = "DEFINITION_SILENTLY_SKIPPED";

        // —— 悬空引用 ——
        public const string DEFINITION_REFERENCE_DANGLING = "DEFINITION_REFERENCE_DANGLING";
        public const string DEFINITION_CYCLE_DETECTED = "DEFINITION_CYCLE_DETECTED";

        // —— Python 之外的 Encounter / 槽位 / 控制器 ——
        public const string ENCOUNTER_SLOT_FACTION_UNKNOWN = "ENCOUNTER_SLOT_FACTION_UNKNOWN";
        public const string ENCOUNTER_SLOT_OUT_OF_BOUNDARY = "ENCOUNTER_SLOT_OUT_OF_BOUNDARY";
        public const string ENCOUNTER_EMPTY = "ENCOUNTER_EMPTY";
        public const string ENCOUNTER_NOT_FOUND = "ENCOUNTER_NOT_FOUND";

        public const string CONTROLLER_ID_INVALID = "CONTROLLER_ID_INVALID";
        public const string CONTROLLER_ID_DUPLICATE = "CONTROLLER_ID_DUPLICATE";
        public const string CONTROLLER_SYSTEM_SOURCE_REJECTED = "CONTROLLER_SYSTEM_SOURCE_REJECTED";
        public const string CONTROLLER_SLOT_DANGLING = "CONTROLLER_SLOT_DANGLING";
        public const string CONTROLLER_SLOT_AMBIGUOUS = "CONTROLLER_SLOT_AMBIGUOUS";
        public const string CONTROLLER_BINDING_EMPTY = "CONTROLLER_BINDING_EMPTY";

        // —— 胜负 ——
        public const string VICTORY_FACTION_SET_EMPTY = "VICTORY_FACTION_SET_EMPTY";
        public const string VICTORY_FACTION_SET_OVERLAP = "VICTORY_FACTION_SET_OVERLAP";
        public const string VICTORY_FACTION_SET_DANGLING = "VICTORY_FACTION_SET_DANGLING";
        public const string VICTORY_RESULT_CODE_MISSING = "VICTORY_RESULT_CODE_MISSING";

        // —— 反应 / 行动集合闭合 ——
        public const string REACTABLE_ATTACK_REQUIRES_MINIMUM_LEAD = "REACTABLE_ATTACK_REQUIRES_MINIMUM_LEAD";
        public const string REACTION_ACTION_NOT_IN_ANY_ACTION_SET = "REACTION_ACTION_NOT_IN_ANY_ACTION_SET";
        public const string ACTION_SET_REFERENCES_UNKNOWN_ACTION = "ACTION_SET_REFERENCES_UNKNOWN_ACTION";
        public const string ACTION_SET_EMPTY = "ACTION_SET_EMPTY";
        public const string ACTION_SET_DUPLICATE_ACTION = "ACTION_SET_DUPLICATE_ACTION";

        // —— 能量 / 资源 ——
        public const string META_RESOURCE_NEGATIVE = "META_RESOURCE_NEGATIVE";
        public const string RNG_SEED_REQUIRED = "RNG_SEED_REQUIRED";

        // —— 防回退护栏 ——
        /// <summary>Authoring 试图用单位速度改写路径边权（00 号规则 32 禁止）。</summary>
        public const string MOVE_SPEED_AS_PATH_COST_REJECTED = "MOVE_SPEED_AS_PATH_COST_REJECTED";
        /// <summary>定义中残留需要运行时求值的旋转配方或浮点坐标（主方案 2.3.1 禁止）。</summary>
        public const string RUNTIME_ROTATION_RECIPE_PRESENT = "RUNTIME_ROTATION_RECIPE_PRESENT";

        // —— 旧伤害迁移 ——
        public const string LEGACY_DAMAGE_TYPE_AMBIGUOUS = "LEGACY_DAMAGE_TYPE_AMBIGUOUS";
        public const string LEGACY_FOLDED_DAMAGE_FIELD = "LEGACY_FOLDED_DAMAGE_FIELD";
    }

    /// <summary>
    /// Builder 的一条错误/警告记录。<see cref="Key"/> 是稳定排序键
    /// （<c>CODE|detail</c>），<see cref="Origin"/> 便于人工定位到具体资产或槽位。
    /// </summary>
    public sealed class DefinitionError : IComparable<DefinitionError>
    {
        public DefinitionError(string code, string detail, string origin = null)
        {
            Code = code ?? string.Empty;
            Detail = detail ?? string.Empty;
            Origin = origin ?? string.Empty;
        }

        public string Code { get; }
        public string Detail { get; }
        public string Origin { get; }

        /// <summary>稳定排序键：先错误码、再 detail、最后 origin，全部 Ordinal。</summary>
        public string Key => Code + "|" + Detail + "|" + Origin;

        public int CompareTo(DefinitionError other)
            => other == null ? 1 : string.CompareOrdinal(Key, other.Key);

        public override string ToString() => $"{Code}: {Detail}" + (Origin.Length > 0 ? $" [{Origin}]" : string.Empty);
    }

    /// <summary>
    /// Builder 的稳定错误收集器：只追加、最后整体按稳定键排序，
    /// 因此输入（资产发现）顺序不会改变输出顺序。
    /// </summary>
    public sealed class DefinitionErrorCollector
    {
        private readonly System.Collections.Generic.List<DefinitionError> _errors =
            new System.Collections.Generic.List<DefinitionError>();

        public int Count => _errors.Count;
        public bool HasErrors => _errors.Count > 0;

        public void Add(string code, string detail, string origin = null)
            => _errors.Add(new DefinitionError(code, detail, origin));

        /// <summary>按稳定键排序后的只读错误列表（每次调用返回同一顺序）。</summary>
        public IReadOnlyList<DefinitionError> Sorted()
        {
            var list = new List<DefinitionError>(_errors);
            list.Sort();
            return list;
        }

        /// <summary>清空已收集的错误（构建失败返回后调用，避免同一收集器被复用污染）。</summary>
        public void Clear() => _errors.Clear();

        public string[] SortedCodes()
        {
            var sorted = Sorted();
            var codes = new string[sorted.Count];
            for (int i = 0; i < sorted.Count; i++) codes[i] = sorted[i].Code;
            return codes;
        }
    }
}
