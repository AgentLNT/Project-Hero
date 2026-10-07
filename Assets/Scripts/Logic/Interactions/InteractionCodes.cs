namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 任务 08「全局 Intent 队列 → 接触候选 → 冲突图」的稳定原因码。
    /// 全部为字符串常量并进入事件/诊断；不得改名、复用或本地化。
    /// </summary>
    public static class InteractionCodes
    {
        // ---------------- Intent 唯一性契约（任务包 1 / 08-多方仲裁与伤害.md:26）----------------

        /// <summary>计划在 <c>ImpactTick</c> 之外产生攻击 Intent。</summary>
        public const string INTENT_TICK_MISMATCH = "COMBAT_INTENT_TICK_MISMATCH";

        /// <summary>已终态计划仍试图产生攻击 Intent。</summary>
        public const string INTENT_PLAN_TERMINAL = "COMBAT_INTENT_PLAN_TERMINAL";

        /// <summary>非攻击动作产生了攻击 Intent（动作族与载荷必须闭合匹配）。</summary>
        public const string INTENT_NOT_ATTACK = "COMBAT_INTENT_NOT_ATTACK";

        /// <summary>同一计划在同一 ImpactTick 产生了多个攻击 Intent。</summary>
        public const string INTENT_DUPLICATE = "COMBAT_INTENT_DUPLICATE";

        /// <summary><c>IntentSequence</c> 重复，稳定键退化为偏序。</summary>
        public const string INTENT_SEQUENCE_DUPLICATE = "COMBAT_INTENT_SEQUENCE_DUPLICATE";

        /// <summary>计划/所有者/序号等标识非法。</summary>
        public const string INTENT_IDENTITY_INVALID = "COMBAT_INTENT_IDENTITY_INVALID";

        /// <summary>攻击 Pattern 的 12 向规范表缺失、条目为空或方向下标错位。</summary>
        public const string INTENT_PATTERN_DIRECTIONS_DEGENERATE = "COMBAT_INTENT_PATTERN_DIRECTIONS_DEGENERATE";

        /// <summary><c>Directions[(int)Facing]</c> 的 <c>Direction</c> 与查询朝向不一致。</summary>
        public const string INTENT_PATTERN_DIRECTION_MISMATCH = "COMBAT_INTENT_PATTERN_DIRECTION_MISMATCH";

        /// <summary><c>PrimaryTargetOnly</c> 缺少计划固定的 <c>PrimaryTargetUnitId</c>。</summary>
        public const string INTENT_PRIMARY_TARGET_MISSING = "COMBAT_INTENT_PRIMARY_TARGET_MISSING";

        /// <summary><c>AllowedTargetRelations</c> 为空或含未定义位。</summary>
        public const string INTENT_RELATION_MASK_INVALID = "COMBAT_INTENT_RELATION_MASK_INVALID";

        // 动量域的稳定原因码（<c>MOMENTUM_OUT_OF_RANGE</c>）与 <c>MaxMomentumUnitsPerIntent</c>
        // 的唯一归属是 ProjectHero.Logic.Combat（MomentumCodes / MomentumLimits）。
        // 依赖方向固定为 Interactions → Combat，本类不得镜像这些常量。

        // ---------------- 接触候选与冲突图 ----------------

        /// <summary>接触候选输入不自洽（Tick 越界、单位表重复、缺失阵营解析器等）。</summary>
        public const string CONTACT_INPUT_INVALID = "INTERACTION_CONTACT_INPUT_INVALID";

        /// <summary>同一单位在同一空间快照里出现两次。</summary>
        public const string CONTACT_UNIT_DUPLICATE = "INTERACTION_UNIT_DUPLICATE";

        /// <summary>冲突组超过节点/边/目标上限，整组失败（不得丢边、拆组或截断目标）。</summary>
        public const string CONFLICT_GROUP_LIMIT_EXCEEDED = "CONFLICT_GROUP_LIMIT_EXCEEDED";
    }

    /// <summary>
    /// 冲突组冻结上限（主方案 0.4.2.1）。数值属于版本化玩法规则并进入 <c>BattleDefinitionHash</c>；
    /// 本文件只是唯一实现引用点，哈希写入由装配侧统一完成（不得在 Logic 内另建第二份常量）。
    /// </summary>
    public static class ConflictGraphLimits
    {
        /// <summary>单个冲突组最多节点数（= 本 Tick 入图 Intent 数）。</summary>
        public const int MaxConflictGroupNodes = 256;

        /// <summary>
        /// 单个冲突组最多 <strong>Intent↔Intent</strong> 边数：<c>256 * 255 / 2 = 32640</c>，
        /// 即 256 个节点的完全图。数值本身证明该上限针对 Intent 之间的边，
        /// 而不是锚定在单个 Intent 上的叶子接触。
        /// </summary>
        public const int MaxConflictGroupEdges = 32640;

        /// <summary>单个冲突组最多不同目标单位数。</summary>
        public const int MaxTargetsPerConflictGroup = 256;
    }
}
