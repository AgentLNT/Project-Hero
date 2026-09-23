using System.Collections.Generic;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Definitions
{
    /// <summary>
    /// Encounter 合法网格边界（主方案 3.8 / 00 号规则 32）。
    ///
    /// 这是首版<strong>唯一</strong>的空间搜索边界：不定义独立 <c>MaxSearchDistance</c>，
    /// 也不允许沿用旧 Pathfinder 的 <c>±100</c> 坐标窗口。
    ///
    /// 语义为"顶点包围盒 + 奇偶过滤"：<c>GridPoint</c> 的合法点必须满足 X + Y 为偶数，
    /// 因此 <see cref="EnumerateValidPoints"/> 只枚举满足奇偶约束的点。
    /// <see cref="Contains"/> 只判断包围盒与奇偶，不做三角形级几何裁剪——
    /// 三角形级可站立性属于任务 06 的 LogicGrid。
    /// </summary>
    public sealed record GridBoundaryDefinition(GridPoint Min, GridPoint Max)
    {
        public const string GRID_BOUNDARY_INVALID = "GRID_BOUNDARY_INVALID";

        /// <summary>
        /// 边界合法性：必须非退化（两个分量均为严格最小值 → 最大值），且包围盒内
        /// <strong>至少含一个</strong>合法顶点。
        ///
        /// 不要求两个端点本身满足顶点奇偶约束：边界是"包围盒"，其语义由
        /// <see cref="Contains"/> 的奇偶过滤与 <see cref="EnumerateValidPoints"/> 的枚举共同定义，
        /// 强制端点奇偶只会让 Authoring 无谓地改写边界。
        /// </summary>
        public string Validate()
        {
            if (Min.X >= Max.X || Min.Y >= Max.Y) return GRID_BOUNDARY_INVALID;
            return ValidPointCount() >= 1L ? null : GRID_BOUNDARY_INVALID;
        }

        public bool Contains(GridPoint point)
        {
            if (point.X < Min.X || point.X > Max.X) return false;
            if (point.Y < Min.Y || point.Y > Max.Y) return false;
            return GridPoint.IsValidParity(point.X, point.Y);
        }

        /// <summary>
        /// 稳定枚举全部合法 doubled-coordinate 顶点：按 X 升序、X 相同按 Y 升序。
        /// 顺序固定，因此不依赖任何资产枚举顺序。
        /// </summary>
        public IReadOnlyList<GridPoint> EnumerateValidPoints()
        {
            var result = new List<GridPoint>();
            for (int x = Min.X; x <= Max.X; x++)
            {
                for (int y = Min.Y; y <= Max.Y; y++)
                {
                    if (GridPoint.IsValidParity(x, y)) result.Add(new GridPoint(x, y));
                }
            }
            return result;
        }

        /// <summary>
        /// 合法顶点总数（不实际枚举，供余量测量与一致性校验使用）。
        ///
        /// 在闭区间 [min, max] 上枚举满足 X + Y 为偶数的格点数等价于在
        /// [min, max+1) 这个半开区间上的计数：共 <c>w * h</c> 个格点，
        /// 起点 <c>(minX + minY)</c> 为偶数时偶数奇偶的点比奇数奇偶的多一个，
        /// 因此结果恰为 <c>(w * h + 1) / 2</c>（起点偶）或 <c>w * h / 2</c>（起点奇）。
        /// 该公式与 <see cref="EnumerateValidPoints"/> 的结果严格一致。
        /// </summary>
        public long ValidPointCount()
        {
            long width = (long)Max.X - Min.X + 1L;
            long height = (long)Max.Y - Min.Y + 1L;
            long total = width * height;
            bool startParityEven = (((long)Min.X + Min.Y) & 1L) == 0L;
            return startParityEven ? (total + 1L) / 2L : total / 2L;
        }

        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("grid_boundary.min_x", Min.X);
            writer.Write("grid_boundary.min_y", Min.Y);
            writer.Write("grid_boundary.max_x", Max.X);
            writer.Write("grid_boundary.max_y", Max.Y);
        }
    }

    /// <summary>
    /// 消灭型胜负定义（主方案 2.3.2 第 7 条 / 00 号规则 31）。
    /// 它不是第二套敌我查询：Allied/Hostile 两个目标集合必须非空、互不相交、只引用已知阵营，
    /// 且满足"Allied 组内部互为 Allied、每个 Allied—Hostile 跨组对互为 Hostile"。
    /// Hostile 组内部关系不受目标分组覆盖。
    /// 未列入两组的"目标外阵营"不参与消灭条件，但仍保留矩阵中的真实关系——
    /// 不得被一概改写为 Neutral。
    /// </summary>
    public sealed record VictoryDefinition(
        IReadOnlyList<FactionId> AlliedFactionIds,
        IReadOnlyList<FactionId> HostileFactionIds,
        string VictoryResultCode,
        string DefeatResultCode,
        string DrawResultCode);

    /// <summary>
    /// 一个 Encounter：稳定 ID、唯一空间边界、固定出场槽位、控制者绑定与胜负定义。
    /// </summary>
    public sealed record EncounterDefinition(
        EncounterDefinitionId EncounterId,
        GridBoundaryDefinition GridBoundary,
        IReadOnlyList<EncounterUnitSlot> Slots,
        IReadOnlyList<ControllerBinding> Controllers,
        VictoryDefinition Victory)
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("encounter.id", EncounterId.Value ?? string.Empty);
            GridBoundary.WriteHashComponents(writer);

            if (Slots != null)
            {
                foreach (var slot in Slots)
                {
                    writer.Write("encounter.slot",
                        (slot.SlotId.Value ?? string.Empty) + "|" +
                        (slot.DefinitionId.Value ?? string.Empty) + "|" +
                        (slot.FactionId.Value ?? string.Empty) + "|" +
                        slot.InitialPosition.X + "," + slot.InitialPosition.Y + "|" +
                        (int)slot.InitialFacing);
                }
            }

            if (Controllers != null)
            {
                foreach (var controller in Controllers)
                {
                    controller.WriteHashComponents(writer);
                }
            }

            if (Victory != null)
            {
                if (Victory.AlliedFactionIds != null)
                {
                    foreach (var faction in Victory.AlliedFactionIds)
                        writer.Write("victory.allied_faction", faction.Value ?? string.Empty);
                }
                if (Victory.HostileFactionIds != null)
                {
                    foreach (var faction in Victory.HostileFactionIds)
                        writer.Write("victory.hostile_faction", faction.Value ?? string.Empty);
                }
                writer.Write("victory.result_code.victory", Victory.VictoryResultCode ?? string.Empty);
                writer.Write("victory.result_code.defeat", Victory.DefeatResultCode ?? string.Empty);
                writer.Write("victory.result_code.draw", Victory.DrawResultCode ?? string.Empty);
            }
        }
    }
}
