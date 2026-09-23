using System;

namespace ProjectHero.Core.Pathfinding
{
    /// <summary>
    /// 三角格 doubled-coordinate 网格顶点（逻辑位置）。
    ///
    /// 任务 02B 前置拆分（任务 01 记录 §5.2 阻塞点 1）：本类型原先作为嵌套结构体定义在
    /// 运行时 Pathfinder 类内部（在旧代码中以 <c>Pathfinder.GridPoint</c> 限定名引用）。
    /// Authoring 程序集必须能引用网格顶点而不能引用整个运行时 Pathfinder，因此抽为顶层类型
    /// 并放入共享网格程序集 <c>ProjectHero.Grid</c>。
    ///
    /// 命名空间保持 <c>ProjectHero.Core.Pathfinding</c>：旧代码的 <c>Pathfinder.GridPoint</c>
    /// 限定写法必须逐处改为 <c>GridPoint</c>（C# 不允许用类型名前缀访问顶层类型），
    /// 但无需为每个消费点新增 using。
    ///
    /// 序列化契约与旧嵌套类型逐字段一致：字段名 <c>X</c>/<c>Y</c> 均为 <c>int</c>，
    /// 场景中 <c>InitialGridPosition</c> 等既有序列化数据不受影响。
    /// 本类型不校验奇偶（旧结构体也不校验）：Logic 侧约束由
    /// <c>ProjectHero.Logic.Grid.GridPoint</c> 承担。
    /// </summary>
    [Serializable]
    public struct GridPoint
    {
        public int X;
        public int Y;

        public GridPoint(int x, int y) { X = x; Y = y; }

        public override bool Equals(object obj) => obj is GridPoint other && X == other.X && Y == other.Y;

        public override int GetHashCode() => (X, Y).GetHashCode();

        public override string ToString() => $"({X}, {Y})";
    }
}
