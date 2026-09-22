using System;
using ProjectHero.Logic;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 三角格 doubled-coordinate 网格点（纯值，不需要独立 ID）。
    /// 合法点必须满足 X + Y 为偶数（主方案 3.8 / 00 号规则 32）。
    /// 构造期校验奇偶；非法点以稳定原因码 <see cref="INVALID_PARITY"/> 拒绝。
    /// </summary>
    public readonly struct GridPoint : IEquatable<GridPoint>, IComparable<GridPoint>
    {
        public const string INVALID_PARITY = "GRID_POINT_INVALID_PARITY";

        public readonly int X;
        public readonly int Y;

        public GridPoint(int x, int y)
        {
            if (!IsValidParity(x, y))
                throw new LogicDefinitionException(INVALID_PARITY, $"X={x}, Y={y}");
            X = x;
            Y = y;
        }

        /// <summary>doubled coordinates 合法点：X + Y 为偶数。</summary>
        public static bool IsValidParity(int x, int y) => ((x + y) & 1) == 0;

        public static bool TryCreate(int x, int y, out GridPoint point)
        {
            if (IsValidParity(x, y))
            {
                point = new GridPoint(x, y);
                return true;
            }
            point = default;
            return false;
        }

        /// <summary>受检整数平移（运行时查询服务只允许平移，不做旋转）。</summary>
        public GridPoint Translate(int dx, int dy)
        {
            int nx = checked(X + dx);
            int ny = checked(Y + dy);
            if (!IsValidParity(nx, ny))
                throw new LogicDefinitionException(INVALID_PARITY, $"X={nx}, Y={ny}");
            return new GridPoint(nx, ny);
        }

        public bool Equals(GridPoint other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is GridPoint other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        /// <summary>稳定排序键：先 X 后 Y 升序（与主方案开放集平局键 (f, h, X, Y) 的坐标部分一致）。</summary>
        public int CompareTo(GridPoint other)
        {
            int byX = X.CompareTo(other.X);
            return byX != 0 ? byX : Y.CompareTo(other.Y);
        }

        public static bool operator ==(GridPoint left, GridPoint right) => left.Equals(right);

        public static bool operator !=(GridPoint left, GridPoint right) => !left.Equals(right);

        public override string ToString() => $"({X}, {Y})";
    }
}
