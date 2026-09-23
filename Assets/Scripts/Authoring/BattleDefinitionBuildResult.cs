using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;

namespace ProjectHero.Authoring
{
    /// <summary>
    /// 一次完整构建的结果。
    ///
    /// 契约：只要 <see cref="Errors"/> 非空，<see cref="Definition"/> 必为 null——
    /// 不存在"带着错误仍然产出可运行定义"的路径（任务包禁止"跳过后继续运行"）。
    /// 错误列表已按稳定键排序，因此与资产发现顺序无关。
    /// </summary>
    public sealed class BattleDefinitionBuildResult
    {
        private static readonly string[] EmptyCodes = new string[0];

        private BattleDefinitionBuildResult(
            BattleDefinition definition,
            IReadOnlyList<DefinitionError> errors,
            IReadOnlyList<string> warnings)
        {
            Definition = definition;
            Errors = errors ?? new List<DefinitionError>();
            Warnings = warnings ?? new List<string>();
        }

        public BattleDefinition Definition { get; }
        public IReadOnlyList<DefinitionError> Errors { get; }
        public IReadOnlyList<string> Warnings { get; }

        public bool Succeeded => Definition != null && Errors.Count == 0;

        /// <summary>稳定排序后的错误码列表（只含 Code，便于断言）。</summary>
        public string[] ErrorCodes()
        {
            if (Errors.Count == 0) return EmptyCodes;
            var codes = new string[Errors.Count];
            for (int i = 0; i < Errors.Count; i++) codes[i] = Errors[i].Code;
            return codes;
        }

        /// <summary>定位辅助：全部错误的稳定键（含 detail / origin）。</summary>
        public string[] ErrorKeys()
        {
            if (Errors.Count == 0) return EmptyCodes;
            var keys = new string[Errors.Count];
            for (int i = 0; i < Errors.Count; i++) keys[i] = Errors[i].Key;
            return keys;
        }

        public static BattleDefinitionBuildResult Failed(DefinitionErrorCollector collector, IReadOnlyList<string> warnings)
        {
            var sorted = collector.Sorted();
            collector.Clear();
            return new BattleDefinitionBuildResult(null, sorted, warnings);
        }

        public static BattleDefinitionBuildResult Success(BattleDefinition definition, IReadOnlyList<string> warnings)
            => new BattleDefinitionBuildResult(definition, new List<DefinitionError>(), warnings);
    }

    /// <summary>构建过程中累计的"转换报告"事实（不参与哈希，只进 02B 迁移记录）。</summary>
    public sealed class DefinitionBuildReport
    {
        private readonly List<string> _notes = new List<string>();

        public void Add(string note) => _notes.Add(note ?? string.Empty);

        public IReadOnlyList<string> Notes => _notes;
    }
}
