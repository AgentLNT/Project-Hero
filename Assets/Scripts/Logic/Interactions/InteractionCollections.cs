using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ProjectHero.Logic.Grid;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 交互集合的规范化助手（内部）。所有公开集合都在这里冻结为只读包装，
    /// 保证 <see cref="CombatIntent"/> 等值数据"构造后不可写"。
    /// 这里不出现 <c>HashSet</c>/<c>Dictionary</c> 的枚举顺序依赖：去重靠排序后线性扫描。
    /// </summary>
    internal static class InteractionCollections
    {
        /// <summary>拷贝 + 按 <c>(X, Y, T)</c> 升序排序 + 去重的规范三角形点集。</summary>
        public static TrianglePoint[] CanonicalTriangles(IReadOnlyList<TrianglePoint> points)
        {
            if (points == null || points.Count == 0) return Array.Empty<TrianglePoint>();

            var buffer = new TrianglePoint[points.Count];
            for (int i = 0; i < points.Count; i++) buffer[i] = points[i];
            Array.Sort(buffer);

            int write = 1;
            for (int read = 1; read < buffer.Length; read++)
            {
                if (buffer[read].CompareTo(buffer[write - 1]) != 0) buffer[write++] = buffer[read];
            }

            if (write == buffer.Length) return buffer;

            var result = new TrianglePoint[write];
            Array.Copy(buffer, result, write);
            return result;
        }

        /// <summary>不可外部改写的只读包装（<c>IReadOnlyList</c> 可被反向强转回数组，故不直接暴露数组）。</summary>
        public static IReadOnlyList<T> Freeze<T>(T[] items)
            => items == null || items.Length == 0
                ? (IReadOnlyList<T>)Array.Empty<T>()
                : new ReadOnlyCollection<T>(items);

        /// <summary>把非空元素拷贝成冻结只读列表（保持输入顺序，排序由调用方决定）。</summary>
        public static IReadOnlyList<T> FreezeList<T>(IReadOnlyList<T> items) where T : class
        {
            if (items == null || items.Count == 0) return Array.Empty<T>();
            var buffer = new List<T>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null) buffer.Add(items[i]);
            }
            return Freeze(buffer.ToArray());
        }
    }
}
