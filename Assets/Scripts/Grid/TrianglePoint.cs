using System;

namespace ProjectHero.Core.Grid
{
    [Serializable]
    public struct TrianglePoint : IEquatable<TrianglePoint>
    {
        public int X;
        public int Y;
        public int T; // 1 for Up, -1 for Down

        public TrianglePoint(int x, int y, int t)
        {
            X = x;
            Y = y;
            T = t;
        }

        public bool Equals(TrianglePoint other)
        {
            return X == other.X && Y == other.Y && T == other.T;
        }

        public override bool Equals(object obj)
        {
            return obj is TrianglePoint other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, T);
        }

        public override string ToString()
        {
            return $"({X}, {Y}, {T})";
        }

        public static bool operator ==(TrianglePoint left, TrianglePoint right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TrianglePoint left, TrianglePoint right)
        {
            return !left.Equals(right);
        }
    }
}
