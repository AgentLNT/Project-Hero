using System;
using System.Collections.Generic;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Interactions;

namespace ProjectHero.Logic.Snapshots
{
    /// <summary>
    /// <strong>Intent 队列与冲突图的规范化快照投影</strong>（任务 08 快照契约，
    /// 与 <see cref="MovementSnapshotProjection"/> 同一模式）。
    ///
    /// 它是"冻结输入 → <see cref="LogicSnapshot"/> 字段"的<strong>唯一</strong>映射：
    /// <c>BattleSimulation.BuildSnapshot</c> 与测试都调用这一份实现，
    /// 因此"快照里看到的 Intent / 冲突组 / 接触"不可能与"本 Tick 冻结队列与构图产物"分叉。
    ///
    /// <list type="bullet">
    /// <item><strong>只投影冻结的结构</strong>：Intent 载荷、组划分（组键 + 节点集合 + 目标集合 + 边数）、
    /// 接触键集合。分阶段求解的<strong>聚合数值</strong>（<c>RemainingHits</c> 等）是
    /// "已入哈希状态的纯函数"，只留在只读诊断面（<c>BattleSimulation.StagedResolution</c>），
    /// 不经本类进入快照——避免制造"两份必须同步的真值"。</item>
    /// <item><strong>无浮点判定</strong>：<c>DamageComponentSpec.RawAmount</c> 只按
    /// <see cref="BitConverter.SingleToInt32Bits"/> 搬运二进制位，本类不对它做任何算术或浮点比较。</item>
    /// <item><strong>稳定排序</strong>：本类输出的每个集合都按各自的稳定键升序
    /// （Intent 按 <c>IntentSequence</c>、冲突组按 <c>GroupKey</c>、接触按
    /// <see cref="ContactKey.CompareTo"/>、目标单位与区域点按整数升序）。
    /// 即使如此，<c>LogicSnapshot.ComputeHash()</c> <strong>仍会</strong>在写哈希前
    /// 重新规范排序一遍——两层都做，是因为构造顺序必须与摘要无关。</item>
    /// <item><strong>空输入合法</strong>：<c>null</c> 队列 / <c>null</c> 图（构造期 Tick 0、本 Tick 无到期 Intent、
    /// 或本 Tick 构图失败）一律投影成空集合，<strong>不</strong>抛异常。</item>
    /// <item>不使用 <c>Dictionary</c>/<c>HashSet</c> 的枚举顺序、不使用 <c>GetHashCode()</c>、
    /// 不使用随机数、不使用浮点比较。</item>
    /// </list>
    /// </summary>
    public static class InteractionSnapshotProjection
    {
        /// <summary>
        /// 本 Tick 冻结队列 → Intent 快照（按 <c>IntentSequence</c> 升序）。
        /// </summary>
        public static IReadOnlyList<IntentSnapshot> Intents(IReadOnlyList<CombatIntent> frozenIntents)
        {
            if (frozenIntents == null || frozenIntents.Count == 0) return Array.Empty<IntentSnapshot>();

            var result = new List<IntentSnapshot>(frozenIntents.Count);
            for (int i = 0; i < frozenIntents.Count; i++)
            {
                CombatIntent intent = frozenIntents[i];
                // 冻结队列（GlobalIntentQueue.Freeze）本身不会含 null；这里只是防御，
                // 跳过不携带任何事实的空槽，而不是用占位值污染哈希。
                if (intent == null) continue;
                result.Add(From(intent));
            }

            result.Sort((a, b) => a.IntentSequence.CompareTo(b.IntentSequence));
            return result.Count == 0 ? Array.Empty<IntentSnapshot>() : result.ToArray();
        }

        /// <summary>冻结队列入口（<c>null</c> 队列 = 空集合；与列表重载共用同一实现）。</summary>
        public static IReadOnlyList<IntentSnapshot> Intents(FrozenIntentQueue queue)
            => queue == null ? Array.Empty<IntentSnapshot>() : Intents(queue.Intents);

        /// <summary>
        /// 单个 Intent → 快照。<c>PrimaryTargetUnitId</c> 与 <c>SubmittedWindowId</c> 的"无"
        /// 一律写成 <c>0</c>（两者的 ID 分配器都保留 <c>0</c> 为无效值）。
        /// </summary>
        public static IntentSnapshot From(CombatIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));

            IReadOnlyList<DamageComponentSpec> components = intent.DamageComponents;
            var damage = new DamageComponentSnapshot[components.Count];
            for (int i = 0; i < damage.Length; i++)
            {
                DamageComponentSpec component = components[i];
                damage[i] = new DamageComponentSnapshot(
                    component.ChannelId.Value,
                    // 唯一的浮点处理：按二进制位搬运（无算术、无比较、无舍入）。
                    BitConverter.SingleToInt32Bits(component.RawAmount),
                    (int)component.Tags);
            }

            return new IntentSnapshot(
                intent.IntentSequence,
                intent.ActionPlanId.Value,
                intent.OwnerUnitId.Value,
                intent.ActionSpecId.Value,
                (int)intent.TargetPolicy,
                intent.PrimaryTargetUnitId.HasValue ? intent.PrimaryTargetUnitId.Value.Value : 0L,
                (int)intent.AllowedTargetRelations,
                (int)intent.Tags,
                (int)intent.Facing,
                intent.ImpactTick,
                intent.InteractionPriority,
                intent.MomentumDirectionOffsetSteps,
                intent.SubmittedWindowId.HasValue ? intent.SubmittedWindowId.Value.Value : 0L,
                new IntentMomentumSnapshot(
                    (int)intent.Momentum.Direction,
                    intent.Momentum.MomentumUnits,
                    intent.Momentum.ImpactProfileId.Value),
                // 区域点集来自唯一规范化助手（查表 + 整数平移 + 升序去重），这里只是再冻结一份只读拷贝。
                Freeze(InteractionCollections.CanonicalTriangles(intent.AreaPoints)),
                Freeze(damage));
        }

        /// <summary>
        /// 冲突图 → 冲突组快照（按 <c>GroupKey</c> 升序）。
        /// <paramref name="graph"/> 为 <c>null</c>（构造期 Tick 0，或本 Tick 构图失败/未构图）时返回空集合。
        /// </summary>
        public static IReadOnlyList<ConflictGroupSnapshot> ConflictGroups(ConflictGraph graph)
        {
            if (graph == null) return Array.Empty<ConflictGroupSnapshot>();

            IReadOnlyList<ConflictGroup> groups = graph.Groups;
            if (groups == null || groups.Count == 0) return Array.Empty<ConflictGroupSnapshot>();

            IReadOnlyList<ConflictNode> nodes = graph.Nodes;
            var result = new List<ConflictGroupSnapshot>(groups.Count);
            for (int g = 0; g < groups.Count; g++)
            {
                ConflictGroup group = groups[g];
                if (group == null) continue;

                // 节点集合 = 组内节点下标 → IntentSequence（节点身份的唯一稳定键）。
                IReadOnlyList<int> indices = group.NodeIndices;
                var sequences = new long[indices.Count];
                for (int i = 0; i < indices.Count; i++)
                {
                    int index = indices[i];
                    sequences[i] = index >= 0 && index < nodes.Count ? nodes[index].IntentSequence : 0L;
                }
                Array.Sort(sequences);

                // 接触键：先按 ContactKey.CompareTo 排序（接触全序的唯一实现），再投影。
                IReadOnlyList<ContactKey> groupKeys = group.ContactKeys;
                var keys = new ContactKey[groupKeys.Count];
                for (int i = 0; i < keys.Length; i++) keys[i] = groupKeys[i];
                Array.Sort(keys);

                var contacts = new ContactSnapshot[keys.Length];
                for (int i = 0; i < keys.Length; i++) contacts[i] = From(keys[i]);

                result.Add(new ConflictGroupSnapshot(
                    group.GroupKey,
                    Freeze(sequences),
                    Freeze(contacts),
                    Freeze(CanonicalUnitIds(group.TargetUnitIds)),
                    group.EdgeCount));
            }

            result.Sort((a, b) => a.GroupKey.CompareTo(b.GroupKey));
            return result.Count == 0 ? Array.Empty<ConflictGroupSnapshot>() : result.ToArray();
        }

        /// <summary>
        /// 冲突图 → 全部接触键快照（含 Intent↔Intent 边与锚在节点上的叶子接触），
        /// 按 <see cref="ContactKey.CompareTo"/> 升序、按键去重。
        /// </summary>
        public static IReadOnlyList<ContactSnapshot> Contacts(ConflictGraph graph)
        {
            if (graph == null) return Array.Empty<ContactSnapshot>();

            IReadOnlyList<InteractionContact> contacts = graph.Contacts;
            if (contacts == null || contacts.Count == 0) return Array.Empty<ContactSnapshot>();

            var sorted = new List<InteractionContact>(contacts.Count);
            for (int i = 0; i < contacts.Count; i++)
            {
                if (contacts[i] != null) sorted.Add(contacts[i]);
            }
            sorted.Sort((a, b) => a.Key.CompareTo(b.Key));

            var result = new List<ContactSnapshot>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                ContactSnapshot snapshot = From(sorted[i].Key);
                // 同一键不得出现两次（接触集合的契约）；去重只看规范键，与发现顺序无关。
                if (result.Count > 0 && result[result.Count - 1].CompareTo(snapshot) == 0) continue;
                result.Add(snapshot);
            }
            return result.Count == 0 ? Array.Empty<ContactSnapshot>() : result.ToArray();
        }

        /// <summary>规范接触键 → 快照（只搬运键的六个分量，不含发现顺序与节点下标）。</summary>
        public static ContactSnapshot From(ContactKey key)
            => new ContactSnapshot(
                (int)key.Type,
                key.FirstUnitId.Value,
                key.FirstPlanId.Value,
                key.SecondUnitId.Value,
                key.SecondPlanId.Value,
                key.TargetUnitId.Value);

        /// <summary>单位集合：拷贝 + 升序 + 去重（稳定键 = <c>UnitId</c> 数值）。</summary>
        private static long[] CanonicalUnitIds(IReadOnlyList<UnitId> unitIds)
        {
            if (unitIds == null || unitIds.Count == 0) return Array.Empty<long>();

            var buffer = new long[unitIds.Count];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = unitIds[i].Value;
                // 构图契约：组内目标单位只来自有效 UnitId（0 保留为无效值）。
                // 出现 0 说明图本身已损坏，稳定拒绝而不是把占位值写进哈希。
                if (buffer[i] <= 0L)
                {
                    throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID,
                        "targetUnitId=" + buffer[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            Array.Sort(buffer);

            int write = 1;
            for (int read = 1; read < buffer.Length; read++)
            {
                if (buffer[read] != buffer[write - 1]) buffer[write++] = buffer[read];
            }
            if (write == buffer.Length) return buffer;

            var result = new long[write];
            Array.Copy(buffer, result, write);
            return result;
        }

        /// <summary>不可外部改写的只读包装（与 <c>LogicSnapshot.Freeze</c> 同一口径）。</summary>
        private static IReadOnlyList<T> Freeze<T>(T[] items)
            => items == null || items.Length == 0
                ? (IReadOnlyList<T>)Array.Empty<T>()
                : Array.AsReadOnly(items);
    }
}
