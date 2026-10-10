using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 冲突图节点：一个本 Tick 有效 Intent。
    /// 节点身份 = <c>IntentSequence</c>（组键、规范排序与输出的唯一依据）。
    /// </summary>
    public sealed class ConflictNode
    {
        internal ConflictNode(int index, CombatIntent intent)
        {
            Index = index;
            Intent = intent;
        }

        /// <summary>节点在规范节点序列中的下标（按 <c>IntentSequence</c> 升序）。</summary>
        public int Index { get; }

        public CombatIntent Intent { get; }

        public long IntentSequence => Intent.IntentSequence;

        public ActionPlanId ActionPlanId => Intent.ActionPlanId;

        public UnitId OwnerUnitId => Intent.OwnerUnitId;
    }

    /// <summary>
    /// 冲突组：冲突图的一个连通分量（主方案 0.4.3）。
    ///
    /// <strong>组键 = 组内最小 <c>IntentSequence</c></strong>。
    /// 为什么它与遍历顺序无关：<see cref="ConflictGroup.NodeIndices"/> 按节点下标（= <c>IntentSequence</c>）
    /// 升序，组键取该序列首元素；"集合的最小值"是集合自身的性质，
    /// 与生成该集合的 BFS/DFS 起点、邻接表顺序、并查集合并顺序都无关。
    /// 由于 <c>IntentSequence</c> 全局唯一，不同组的最小值必然不同 ⇒ 组键是组集合的全序键。
    /// </summary>
    public sealed class ConflictGroup
    {
        private readonly int[] _nodeIndices;
        private readonly ContactKey[] _contactKeys;
        private readonly UnitId[] _targetUnitIds;

        internal ConflictGroup(long groupKey, int[] nodeIndices, ContactKey[] contactKeys, UnitId[] targetUnitIds, int edgeCount)
        {
            GroupKey = groupKey;
            _nodeIndices = nodeIndices;
            _contactKeys = contactKeys;
            _targetUnitIds = targetUnitIds;
            EdgeCount = edgeCount;
        }

        /// <summary>组键 = 组内最小 <c>IntentSequence</c>。</summary>
        public long GroupKey { get; }

        /// <summary>组内节点下标，升序。</summary>
        public IReadOnlyList<int> NodeIndices => _nodeIndices;

        /// <summary>组内全部接触键（含锚定在本组节点上的叶子接触），按键升序。</summary>
        public IReadOnlyList<ContactKey> ContactKeys => _contactKeys;

        /// <summary>组内全部目标单位（升序、去重；不含无目标单位的 Clash 接触）。</summary>
        public IReadOnlyList<UnitId> TargetUnitIds => _targetUnitIds;

        /// <summary>组内 <strong>Intent↔Intent</strong> 边数（上限 <c>MaxConflictGroupEdges</c> 的计量对象）。</summary>
        public int EdgeCount { get; }

        public int NodeCount => _nodeIndices.Length;

        public bool ContainsNode(int nodeIndex)
        {
            for (int i = 0; i < _nodeIndices.Length; i++)
            {
                if (_nodeIndices[i] == nodeIndex) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 规范化冲突图（任务 08「必须产出」6 + 冲突图契约全节）。
    ///
    /// 不可变快照：节点、接触、组划分都在构建时一次性冻结，<strong>不持有</strong>调用方的
    /// 可变列表或计划引用 —— 因此本 Tick 求解期间发生的计划终态（Clash/Move/Intercept）
    /// 不会追溯改变已经冻结的本 Tick 输入。
    ///
    /// 确定性：所有集合按稳定键规范排序；不依赖 <c>Dictionary</c>/<c>HashSet</c> 枚举顺序；
    /// 不使用运行时随机、不使用 <c>GetHashCode()</c> 参与任何判定。
    /// </summary>
    public sealed class ConflictGraph
    {
        private readonly ConflictNode[] _nodes;
        private readonly InteractionContact[] _contacts;
        private readonly ConflictGroup[] _groups;
        private readonly string _canonicalText;

        internal ConflictGraph(
            long tick,
            ConflictNode[] nodes,
            InteractionContact[] contacts,
            ConflictGroup[] groups)
        {
            Tick = tick;
            _nodes = nodes ?? Array.Empty<ConflictNode>();
            _contacts = contacts ?? Array.Empty<InteractionContact>();
            _groups = groups ?? Array.Empty<ConflictGroup>();
            _canonicalText = BuildCanonicalText(tick, _nodes, _contacts, _groups);
        }

        public long Tick { get; }
        internal static ConflictGraph Empty(long tick) => new ConflictGraph(tick,
            Array.Empty<ConflictNode>(), Array.Empty<InteractionContact>(), Array.Empty<ConflictGroup>());

        /// <summary>节点：按 <c>IntentSequence</c> 升序。</summary>
        public IReadOnlyList<ConflictNode> Nodes => _nodes;

        /// <summary>接触（带类型边）：按 <see cref="ContactKey"/> 升序、已去重。</summary>
        public IReadOnlyList<InteractionContact> Contacts => _contacts;

        /// <summary>冲突组：按 <see cref="ConflictGroup.GroupKey"/> 升序。</summary>
        public IReadOnlyList<ConflictGroup> Groups => _groups;

        /// <summary>
        /// 规范化序列化：节点 + 接触键 + 组划分的完整文本。
        /// 相同输入集合的任意排列必须得到逐字节相同的文本（排列不变性用例的抓手）。
        /// </summary>
        public string CanonicalText => _canonicalText;

        /// <summary>节点下标查询（按计划 ID）。</summary>
        public int NodeIndexOf(ActionPlanId actionPlanId)
        {
            for (int i = 0; i < _nodes.Length; i++)
            {
                if (_nodes[i].ActionPlanId == actionPlanId) return i;
            }
            return -1;
        }

        /// <summary>节点所属组的组键；节点不存在时返回 <c>0</c>。</summary>
        public long GroupKeyOfNode(int nodeIndex)
        {
            for (int g = 0; g < _groups.Length; g++)
            {
                if (_groups[g].ContainsNode(nodeIndex)) return _groups[g].GroupKey;
            }
            return 0L;
        }

        /// <summary>存在指定键的接触时返回它，否则返回 null。</summary>
        public InteractionContact FindContact(ContactKey key)
        {
            int low = 0;
            int high = _contacts.Length - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                int byKey = _contacts[mid].Key.CompareTo(key);
                if (byKey == 0) return _contacts[mid];
                if (byKey < 0) low = mid + 1;
                else high = mid - 1;
            }
            return null;
        }

        private static string BuildCanonicalText(
            long tick, ConflictNode[] nodes, InteractionContact[] contacts, ConflictGroup[] groups)
        {
            var sb = new StringBuilder();
            sb.Append("conflictGraph|tick=").Append(tick.ToString(CultureInfo.InvariantCulture))
              .Append("|nodes=").Append(nodes.Length.ToString(CultureInfo.InvariantCulture))
              .Append("|contacts=").Append(contacts.Length.ToString(CultureInfo.InvariantCulture))
              .Append("|groups=").Append(groups.Length.ToString(CultureInfo.InvariantCulture));

            for (int i = 0; i < nodes.Length; i++)
            {
                sb.Append('\n').Append("node|idx=").Append(i.ToString(CultureInfo.InvariantCulture))
                  .Append('|').Append(nodes[i].Intent.CanonicalText);
            }

            for (int i = 0; i < contacts.Length; i++)
            {
                sb.Append('\n').Append(contacts[i].CanonicalText);
            }

            for (int g = 0; g < groups.Length; g++)
            {
                ConflictGroup group = groups[g];
                sb.Append('\n').Append("group|key=").Append(group.GroupKey.ToString(CultureInfo.InvariantCulture))
                  .Append("|edges=").Append(group.EdgeCount.ToString(CultureInfo.InvariantCulture))
                  .Append("|nodes=");
                for (int i = 0; i < group.NodeIndices.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(group.NodeIndices[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append("|targets=");
                for (int i = 0; i < group.TargetUnitIds.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(group.TargetUnitIds[i].Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>构图结果：失败时携带稳定原因码与失败的组键（组上限失败是"整组失败"，不是丢边/拆组）。</summary>
    public sealed record ConflictGraphBuildResult(ConflictGraph Graph, string ErrorCode, long FailingGroupKey)
    {
        public bool Succeeded => ErrorCode == null;
    }

    /// <summary>
    /// 冲突图的<strong>只读投影</strong>：计划 → 冲突组键。
    ///
    /// 语义事件、位移请求与伤害提交都需要"这条接触属于哪个冲突组"，
    /// 但它们拿到的输入是<strong>求解读数</strong>（接触键、聚合结果），不是图本身。
    /// 因此这里提供唯一一份投影，避免每个消费者各自扫一遍图、
    /// 也避免"图构建后不再变更"这条保证被绕过。
    ///
    /// <list type="bullet">
    /// <item>未入图的计划（本 Tick 没有 Intent 或构图失败）<strong>不</strong>出现在映射里，
    /// 调用方按 <c>0</c> 处理（= 没有对应冲突组，与既有事件口径一致）；</item>
    /// <item>一个计划只可能属于一个连通分量，因此同一键出现两个不同组键是构图矛盾，
    /// 以稳定码 <c>CONFLICT_GRAPH_INCONSISTENT</c> 拒绝，绝不"取第一个/取最小"。</item>
    /// </list>
    /// </summary>
    public static class ConflictGroupKeys
    {
        /// <summary>同一计划被投影到两个不同组键 ⇒ 构图自相矛盾（稳定拒绝，不静默取一个）。</summary>
        public const string CONFLICT_GRAPH_INCONSISTENT = "CONFLICT_GRAPH_INCONSISTENT";

        /// <summary>按 <c>ActionPlanId</c> 升序的可查询映射（同键重复时稳定拒绝）。</summary>
        public static IReadOnlyDictionary<long, long> BuildByPlan(ConflictGraph graph)
        {
            var map = new Dictionary<long, long>();
            if (graph == null || graph.Groups == null) return map;

            for (int g = 0; g < graph.Groups.Count; g++)
            {
                ConflictGroup group = graph.Groups[g];
                if (group == null) continue;
                IReadOnlyList<int> indices = group.NodeIndices;
                for (int i = 0; i < indices.Count; i++)
                {
                    int index = indices[i];
                    if (index < 0 || index >= graph.Nodes.Count) continue;
                    long planId = graph.Nodes[index].ActionPlanId.Value;
                    if (planId <= 0L) continue;
                    if (map.TryGetValue(planId, out long existing) && existing != group.GroupKey)
                        throw new LogicDefinitionException(CONFLICT_GRAPH_INCONSISTENT,
                            "plan=" + planId.ToString(CultureInfo.InvariantCulture) +
                            " groups=" + existing.ToString(CultureInfo.InvariantCulture) + "," +
                            group.GroupKey.ToString(CultureInfo.InvariantCulture));
                    map[planId] = group.GroupKey;
                }
            }
            return map;
        }

        /// <summary>查表；未入图时返回 <c>0</c>（= 没有对应冲突组）。</summary>
        public static long Of(IReadOnlyDictionary<long, long> byPlan, ActionPlanId planId)
            => byPlan != null && planId.IsValid && byPlan.TryGetValue(planId.Value, out long key) ? key : 0L;
    }
}
