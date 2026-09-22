using System;

namespace ProjectHero.Logic.Combat
{
    /// <summary>
    /// 强制位移协议版本（主方案 0.4.6 版本标识）。
    /// SimultaneousStepV1 = 1：全 Tick 单批、逐格临时同时求解、每步一次规范方向增量且
    /// 不读取 PathWeight、部分距离保留、同落点全败、依赖链只通向空格、不连锁推人、
    /// 强制位移优先 Reservation、批量换位先于死亡。协议版本进入 BattleDefinitionHash；
    /// 求解器与运行时请求/结果由任务 08 实现。
    /// 注：Unity 6000.6.2f1 默认 C# 9，record struct 为 C# 10 特性，故手写只读值类型。
    /// </summary>
    public readonly struct ForcedDisplacementProtocolVersion
    {
        public const int SimultaneousStepV1 = 1;

        public readonly int Value;

        public ForcedDisplacementProtocolVersion(int value) { Value = value; }

        public bool IsValid => Value > 0;

        public bool Equals(ForcedDisplacementProtocolVersion other) => Value == other.Value;

        public override bool Equals(object obj) => obj is ForcedDisplacementProtocolVersion other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool operator ==(ForcedDisplacementProtocolVersion left, ForcedDisplacementProtocolVersion right) => left.Equals(right);

        public static bool operator !=(ForcedDisplacementProtocolVersion left, ForcedDisplacementProtocolVersion right) => !left.Equals(right);

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("forced_displacement.protocol_version", Value);
        }
    }

    /// <summary>
    /// 强制位移停止原因稳定数值编码（主方案 0.4.6 冻结；数值不得更改）。
    /// </summary>
    public enum ForcedDisplacementStopReason
    {
        Completed = 0,
        Boundary = 1,
        StaticObstacle = 2,
        OccupiedUnit = 3,
        DestinationContention = 4,
        DependencyCycle = 5,
        VolumeOverlap = 6
    }

    /// <summary>
    /// 停止原因编码的规范哈希分量（名称 + 数值，声明顺序固定）。
    /// 与协议版本一起参与 BattleDefinitionHash。
    /// </summary>
    public static class ForcedDisplacementEncoding
    {
        public static void WriteHashComponents(CanonicalHashWriter writer)
        {
            var values = (ForcedDisplacementStopReason[])Enum.GetValues(typeof(ForcedDisplacementStopReason));
            foreach (var reason in values)
            {
                writer.Write("forced_displacement.stop_reason." + reason, (int)reason);
            }
        }
    }
}
