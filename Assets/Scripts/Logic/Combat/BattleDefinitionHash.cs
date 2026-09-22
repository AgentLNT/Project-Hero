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
    }
}
