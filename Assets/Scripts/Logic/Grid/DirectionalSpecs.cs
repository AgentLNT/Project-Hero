using System.Collections.Generic;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 攻击 Pattern 的规范 12 向整数表（主方案 2.3.1）。不保留旋转配方或浮点坐标。
    /// </summary>
    public sealed record AttackPatternSpec(
        AttackPatternId AttackPatternId,
        IReadOnlyList<DirectionalTriangleSet> Directions);

    /// <summary>
    /// 单位体积的规范 12 向整数表（主方案 2.3.1）。
    /// </summary>
    public sealed record VolumeSpec(
        VolumeSpecId VolumeSpecId,
        IReadOnlyList<DirectionalTriangleSet> Directions);

    /// <summary>
    /// 带朝向的离散移动 Pattern，复用与 Pattern/Volume 完全相同的 12 向规范表契约。
    /// </summary>
    public sealed record MovementPatternSpec(
        MovementPatternId MovementPatternId,
        IReadOnlyList<DirectionalTriangleSet> Directions);
}
