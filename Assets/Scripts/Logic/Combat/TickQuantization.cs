using System;
using ProjectHero.Logic;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 秒→Tick 与整数 Tick 量化（Authoring 边界与定义解析共用）。
    /// 所有影响战斗结果的时间均为整数 Tick；秒只存在于配置导入边界（00 号规则 6）。
    /// 非法输入以稳定原因码拒绝，不钳制、不回绕。
    /// </summary>
    public static class TickQuantization
    {
        public const string TICK_QUANTIZATION_INVALID_INPUT = "TICK_QUANTIZATION_INVALID_INPUT";
        public const string TICK_QUANTIZATION_OUT_OF_RANGE = "TICK_QUANTIZATION_OUT_OF_RANGE";

        /// <summary>
        /// 整数 RoundHalfUp：round(n / d) = (2n + d) / (2d)（n ≥ 0，d > 0）。
        /// 与主方案 3.1.2 的 RoundHalfUp 语义一致（半值向上取整）。
        /// </summary>
        public static long RoundHalfUpDivide(long numerator, long denominator)
        {
            if (numerator < 0 || denominator <= 0)
                throw new LogicDefinitionException(TICK_QUANTIZATION_INVALID_INPUT,
                    $"numerator={numerator}, denominator={denominator}");
            try
            {
                return checked((numerator * 2 + denominator) / (denominator * 2));
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(TICK_QUANTIZATION_OUT_OF_RANGE, ex.Message);
            }
        }

        /// <summary>
        /// 边界量化：RoundHalfUp(seconds × ticksPerSecond)，要求有限正秒数且结果 ≥ 1 Tick。
        /// 正秒值舍入后小于 1 Tick 时以 <see cref="TICK_QUANTIZATION_OUT_OF_RANGE"/> 拒绝。
        /// </summary>
        public static int SecondsToTicks(double seconds, int ticksPerSecond)
        {
            if (ticksPerSecond <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0d)
                throw new LogicDefinitionException(TICK_QUANTIZATION_INVALID_INPUT,
                    $"seconds={seconds}, ticksPerSecond={ticksPerSecond}");

            double scaled = seconds * ticksPerSecond;
            long ticks;
            try
            {
                ticks = checked((long)Math.Round(scaled, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(TICK_QUANTIZATION_OUT_OF_RANGE, ex.Message);
            }

            if (ticks < 1L || ticks > int.MaxValue)
                throw new LogicDefinitionException(TICK_QUANTIZATION_OUT_OF_RANGE,
                    $"seconds={seconds}, ticks={ticks}");
            return (int)ticks;
        }

        /// <summary>
        /// 攻击前摇解析（主方案 3.1.2）：
        /// ResolvedWindupTicks = max(1, RoundHalfUp(BaseWindupTicks × ReferenceActionSpeed / ActionSpeed))。
        /// ActionSpeed 只修正前摇；后摇不受速度影响。仅在计划首次进入 Editable 时采样一次。
        /// </summary>
        public static int ResolveWindupTicks(int baseWindupTicks, int referenceActionSpeed, double actionSpeed)
        {
            if (baseWindupTicks <= 0 || referenceActionSpeed <= 0 ||
                double.IsNaN(actionSpeed) || double.IsInfinity(actionSpeed) || actionSpeed <= 0d)
                throw new LogicDefinitionException(TICK_QUANTIZATION_INVALID_INPUT,
                    $"baseWindupTicks={baseWindupTicks}, referenceActionSpeed={referenceActionSpeed}, actionSpeed={actionSpeed}");

            double ratio = ((double)baseWindupTicks * referenceActionSpeed) / actionSpeed;
            long rounded;
            try
            {
                rounded = checked((long)Math.Round(ratio, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(TICK_QUANTIZATION_OUT_OF_RANGE, ex.Message);
            }
            long result = Math.Max(1L, rounded);
            if (result > int.MaxValue)
                throw new LogicDefinitionException(TICK_QUANTIZATION_OUT_OF_RANGE, $"result={result}");
            return (int)result;
        }

        /// <summary>
        /// 移动每权重单位 Tick 解析（主方案 3.1.2）：
        /// ResolvedBaseStepTicks = max(1, RoundHalfUp(BaseStepTicks × ReferenceMoveSpeed / MoveSpeed))。
        /// MoveSpeed 只做时长量化，不参与选路（00 号规则 32）。
        /// </summary>
        public static int ResolveMoveBaseStepTicks(int baseStepTicks, int referenceMoveSpeed, double moveSpeed)
        {
            if (baseStepTicks <= 0 || referenceMoveSpeed <= 0 ||
                double.IsNaN(moveSpeed) || double.IsInfinity(moveSpeed) || moveSpeed <= 0d)
                throw new LogicDefinitionException(TICK_QUANTIZATION_INVALID_INPUT,
                    $"baseStepTicks={baseStepTicks}, referenceMoveSpeed={referenceMoveSpeed}, moveSpeed={moveSpeed}");

            double ratio = ((double)baseStepTicks * referenceMoveSpeed) / moveSpeed;
            long rounded;
            try
            {
                rounded = checked((long)Math.Round(ratio, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException ex)
            {
                throw new LogicDefinitionException(TICK_QUANTIZATION_OUT_OF_RANGE, ex.Message);
            }
            long result = Math.Max(1L, rounded);
            if (result > int.MaxValue)
                throw new LogicDefinitionException(TICK_QUANTIZATION_OUT_OF_RANGE, $"result={result}");
            return (int)result;
        }
    }
}
