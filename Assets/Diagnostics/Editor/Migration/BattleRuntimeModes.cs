using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Runtime
{
    public static class BattleRuntimeModes
    {
        /// <summary>三个模式的冻结枚举顺序（诊断输出与调用矩阵按此顺序）。</summary>
        public static readonly BattleRuntimeMode[] All =
        {
            BattleRuntimeMode.Legacy,
            BattleRuntimeMode.Shadow,
            BattleRuntimeMode.New
        };

        /// <summary>该模式是否启用已登记的 Legacy 从属写入组（不变量 21）。</summary>
        public static bool EnablesLegacyWriterGroup(BattleRuntimeMode mode)
            => mode != BattleRuntimeMode.New;

        /// <summary>该模式是否必须调用 Legacy 顶层推进入口。</summary>
        public static bool CallsLegacyAdvance(BattleRuntimeMode mode)
            => mode == BattleRuntimeMode.Legacy || mode == BattleRuntimeMode.Shadow;

        /// <summary>该模式是否推进新模拟。</summary>
        public static bool AdvancesNewSimulation(BattleRuntimeMode mode)
            => mode == BattleRuntimeMode.Shadow || mode == BattleRuntimeMode.New;

        public static string Describe(BattleRuntimeMode mode)
        {
            switch (mode)
            {
                case BattleRuntimeMode.Legacy: return "Legacy";
                case BattleRuntimeMode.Shadow: return "Shadow";
                case BattleRuntimeMode.New: return "New";
                default: return "Unknown(" + (int)mode + ")";
            }
        }
    }
}
