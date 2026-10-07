using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 冲突图构建器（任务 08「必须产出」6 / 主方案 0.4.3 / 08-多方仲裁与伤害.md:56-66）。
    ///
    /// 流程：接触候选（<see cref="InteractionCandidateBuilder"/>）→ 连通分量 → 冲突组。
    ///
    /// <strong>确定性实现要点</strong>（对应冻结清单 §3.4）：
    /// <list type="number">
    /// <item>节点先按 <c>IntentSequence</c> 升序定下标；</item>
    /// <item>边按 <see cref="ContactKey"/> 升序处理，并查集合并顺序不影响最终分量集合；</item>
    /// <item>组内节点下标、接触键、目标单位全部升序输出；</item>
    /// <item>组按组键（= 组内最小 <c>IntentSequence</c>）升序输出；</item>
    /// <item>全程不使用 <c>HashSet</c>/<c>Dictionary</c> 的枚举顺序，不使用随机数与 <c>GetHashCode()</c>。</item>
    /// </list>
    /// 因此"输入排列"只影响中间数组的顺序，不影响任何输出集合。
    /// </summary>
    public static class ConflictGraphBuilder
    {
        /// <summary>构建冲突图；组超过冻结上限时抛 <c>CONFLICT_GROUP_LIMIT_EXCEEDED</c>。</summary>
        public static ConflictGraph Build(ConflictGraphInput input)
        {
            ConflictGraphBuildResult result = TryBuild(input, out ConflictGraph graph);
            if (result.Succeeded) return graph;

            throw new LogicDefinitionException(result.ErrorCode,
                "groupKey=" + result.FailingGroupKey.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 非抛出构建。上限失败时返回失败结果（含失败的组键），供上层选择整组失败而不丢边、不拆组、不截断目标。
        /// </summary>
        public static ConflictGraphBuildResult TryBuild(ConflictGraphInput input, out ConflictGraph graph)
        {
            graph = null;
            if (input == null) throw new ArgumentNullException(nameof(input));

            InteractionCandidateSet candidates = InteractionCandidateBuilder.Build(input);

            CombatIntent[] intents = ToArray(candidates.Nodes);
            var nodes = new ConflictNode[intents.Length];
            for (int i = 0; i < intents.Length; i++) nodes[i] = new ConflictNode(i, intents[i]);

            InteractionContact[] contacts = ToArray(candidates.Contacts);

            // ---- 连通分量：只用 Intent↔Intent 边（节点是 Intent；锚在单个 Intent 上的叶子接触不合并分量）----
            int[] parent = new int[nodes.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            for (int i = 0; i < contacts.Length; i++)
            {
                InteractionContact contact = contacts[i];
                if (!contact.IsNodeToNode) continue;
                Union(parent, contact.AttackerNodeIndex, contact.CounterpartyNodeIndex);
            }

            // ---- 组划分：按升序节点扫描，组内下标天然升序、组按组键天然升序 ----
            var groupsByRoot = new List<int>[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                int root = Find(parent, i);
                if (groupsByRoot[root] == null) groupsByRoot[root] = new List<int>();
                groupsByRoot[root].Add(i);
            }

            var groups = new List<ConflictGroup>();
            for (int root = 0; root < groupsByRoot.Length; root++)
            {
                List<int> members = groupsByRoot[root];
                if (members == null) continue;

                int[] nodeIndices = members.ToArray();          // 升序：i 递增扫描保证
                long groupKey = nodes[nodeIndices[0]].IntentSequence;

                var groupContactKeys = new List<ContactKey>();
                var groupTargets = new List<UnitId>();
                int edgeCount = 0;

                for (int c = 0; c < contacts.Length; c++)
                {
                    InteractionContact contact = contacts[c];
                    bool belongs = Contains(nodeIndices, contact.AttackerNodeIndex);
                    if (!belongs && contact.IsNodeToNode) belongs = Contains(nodeIndices, contact.CounterpartyNodeIndex);
                    if (!belongs) continue;

                    groupContactKeys.Add(contact.Key);
                    if (contact.IsNodeToNode) edgeCount++;
                    if (contact.TargetUnitId.IsValid && !ContainsUnit(groupTargets, contact.TargetUnitId))
                    {
                        groupTargets.Add(contact.TargetUnitId);
                    }
                }

                groupTargets.Sort((a, b) => a.Value.CompareTo(b.Value));

                string limitCode = CheckLimits(nodeIndices.Length, edgeCount, groupTargets.Count);
                if (limitCode != null)
                {
                    return new ConflictGraphBuildResult(null, limitCode, groupKey);
                }

                groups.Add(new ConflictGroup(
                    groupKey, nodeIndices, groupContactKeys.ToArray(), groupTargets.ToArray(), edgeCount));
            }

            groups.Sort((a, b) => a.GroupKey.CompareTo(b.GroupKey));

            graph = new ConflictGraph(input.Tick, nodes, contacts, groups.ToArray());
            return new ConflictGraphBuildResult(graph, null, 0L);
        }

        /// <summary>组上限检查：任何一项超限都是整组失败，<strong>不得</strong>丢边、拆组或截断目标。</summary>
        private static string CheckLimits(int nodeCount, int edgeCount, int targetCount)
        {
            if (nodeCount > ConflictGraphLimits.MaxConflictGroupNodes) return InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED;
            if (edgeCount > ConflictGraphLimits.MaxConflictGroupEdges) return InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED;
            if (targetCount > ConflictGraphLimits.MaxTargetsPerConflictGroup) return InteractionCodes.CONFLICT_GROUP_LIMIT_EXCEEDED;
            return null;
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        private static void Union(int[] parent, int left, int right)
        {
            int leftRoot = Find(parent, left);
            int rightRoot = Find(parent, right);
            if (leftRoot == rightRoot) return;

            // 固定"小下标为根"：合并顺序不影响最终根集合，也让 Find 结果与处理顺序无关。
            if (leftRoot < rightRoot) parent[rightRoot] = leftRoot;
            else parent[leftRoot] = rightRoot;
        }

        private static bool Contains(int[] values, int value)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == value) return true;
            }
            return false;
        }

        private static bool ContainsUnit(List<UnitId> values, UnitId unitId)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == unitId) return true;
            }
            return false;
        }

        private static CombatIntent[] ToArray(IReadOnlyList<CombatIntent> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<CombatIntent>();
            var result = new CombatIntent[source.Count];
            for (int i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }

        private static InteractionContact[] ToArray(IReadOnlyList<InteractionContact> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<InteractionContact>();
            var result = new InteractionContact[source.Count];
            for (int i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }
    }
}
