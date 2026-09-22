using System;
using ProjectHero.Logic;

namespace ProjectHero.Logic.Grid
{
    /// <summary>
    /// 三角格三角（纯值，不需要独立 ID）。
    /// 合法点必须满足 T ∈ {-1, 1} 且 X + Y + T 为偶数（主方案 2.3.1）。
    /// 该奇偶约束保证 60° 整数展开公式的两个除法均整除。
    /// </summary>
    public readonly struct TrianglePoint : IEquatable<TrianglePoint>, IComparable<TrianglePoint>
    {
        public const string INVALID = "TRIANGLE_POINT_INVALID";

        public readonly int X;
        public readonly int Y;
        public readonly int T; // 1 为 Up，-1 为 Down

        public TrianglePoint(int x, int y, int t)
        {
            if (!IsValid(x, y, t))
                throw new LogicDefinitionException(INVALID, $"X={x}, Y={y}, T={t}");
            X = x;
            Y = y;
            T = t;
        }

        public static bool IsValid(int x, int y, int t)
            => (t == 1 || t == -1) && (((x + y + t) & 1) == 0);

        public static bool TryCreate(int x, int y, int t, out TrianglePoint point)
        {
            if (IsValid(x, y, t))
            {
                point = new TrianglePoint(x, y, t);
                return true;
            }
            point = default;
            return false;
        }

        /// <summary>受检整数平移（运行时查询服务只允许平移，不做旋转）。</summary>
        public TrianglePoint Translate(int dx, int dy)
        {
            int nx = checked(X + dx);
            int ny = checked(Y + dy);
            if (!IsValid(nx, ny, T))
                throw new LogicDefinitionException(INVALID, $"X={nx}, Y={ny}, T={T}");
            return new TrianglePoint(nx, ny, T);
        }

        public bool Equals(TrianglePoint other) => X == other.X && Y == other.Y && T == other.T;

        public override bool Equals(object obj) => obj is TrianglePoint other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, T);

        /// <summary>规范排序键：按 (X, Y, T) 升序（主方案 2.3.1 的去重与稳定排序）。</summary>
        public int CompareTo(TrianglePoint other)
        {
            int byX = X.CompareTo(other.X);
            if (byX != 0) return byX;
            int byY = Y.CompareTo(other.Y);
            return byY != 0 ? byY : T.CompareTo(other.T);
        }

        public static bool operator ==(TrianglePoint left, TrianglePoint right) => left.Equals(right);

        public static bool operator !=(TrianglePoint left, TrianglePoint right) => !left.Equals(right);

        public override string ToString() => $"({X}, {Y}, {T})";
    }
}
