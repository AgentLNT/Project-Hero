using System;
using ProjectHero.Logic.Grid;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 版本化路径成本规则（主方案 3.8 / 00 号规则 32）：
    /// 偶数 GridDirection 的 StepWeightUnits = 1、奇数方向 = 2；
    /// 启发函数版本固定为非负整数公式（doubled coordinates）：
    /// <code>dy + max(0, (dx - dy) / 2)</code>，dx = |a.X - b.X|，dy = |a.Y - b.Y|。
    /// 全部使用 checked long；MoveSpeed 不属于路径成本输入。
    /// 权重、启发版本与公式属于版本化玩法规则并进入 BattleDefinitionHash。
    /// </summary>
    public sealed record PathCostRules(
        int EvenDirectionStepWeightUnits,
        int OddDirectionStepWeightUnits)
    {
        public const string PATH_COST_WEIGHT_INVALID = "PATH_COST_WEIGHT_INVALID";
        /// <summary>任务 02B 冻结：偶数方向权重必须恰好为 1、奇数方向恰好为 2。</summary>
        public const string PATH_COST_RULE_INVALID = "PATH_COST_RULE_INVALID";

        /// <summary>启发函数版本标识（主方案 3.8 冻结的非负整数公式）。</summary>
        public const string HeuristicVersion = "3.8-nonnegative-integer-v1";

        /// <summary>首版冻结：偶数 1 / 奇数 2。</summary>
        public static readonly PathCostRules FrozenV1 = new(1, 2);

        /// <summary>基本合法性：两个权重均为正。</summary>
        public string Validate()
            => EvenDirectionStepWeightUnits <= 0 || OddDirectionStepWeightUnits <= 0
                ? PATH_COST_WEIGHT_INVALID : null;

        /// <summary>
        /// 任务 02B 定义级校验：除"必须为正"外，还要求权重恰好等于冻结的
        /// 偶数 1 / 奇数 2（主方案 3.8、00 号规则 32），且启发函数版本受支持。
        /// 本方法不修改规则，也不接受 MoveSpeed 或任何单位速度作为输入。
        /// </summary>
        public string ValidateFrozenRules()
        {
            string basic = Validate();
            if (basic != null) return basic;
            if (EvenDirectionStepWeightUnits != FrozenV1.EvenDirectionStepWeightUnits ||
                OddDirectionStepWeightUnits != FrozenV1.OddDirectionStepWeightUnits)
                return PATH_COST_RULE_INVALID;
            if (!IsSupportedHeuristicVersion(HeuristicVersion)) return PATH_COST_RULE_INVALID;
            return null;
        }

        /// <summary>首版只支持 <see cref="HeuristicVersion"/> 一个启发函数/平局版本。</summary>
        public static bool IsSupportedHeuristicVersion(string version)
            => string.Equals(version, HeuristicVersion, System.StringComparison.Ordinal);

        /// <summary>偶数方向权重 1、奇数方向权重 2（冻结值经字段承载以便进哈希）。</summary>
        public int StepWeightUnits(GridDirection direction)
            => (((int)direction & 1) == 0)
                ? EvenDirectionStepWeightUnits
                : OddDirectionStepWeightUnits;

        /// <summary>
        /// 启发权重（下界，无障碍图）：dy + max(0, (dx - dy) / 2)，checked long、非负整数。
        /// 坐标差先扩展为 long 再取绝对值，禁止对 int.MinValue 直接取绝对值。
        /// </summary>
        public long HeuristicWeightUnits(GridPoint from, GridPoint to)
        {
            long dx = Math.Abs((long)to.X - from.X);
            long dy = Math.Abs((long)to.Y - from.Y);
            long delta = (dx - dy) / 2; // 整数向下整除
            return checked(dy + Math.Max(0L, delta));
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("path_cost.heuristic_version", HeuristicVersion);
            writer.Write("path_cost.even_step_weight", EvenDirectionStepWeightUnits);
            writer.Write("path_cost.odd_step_weight", OddDirectionStepWeightUnits);
        }
    }
}
