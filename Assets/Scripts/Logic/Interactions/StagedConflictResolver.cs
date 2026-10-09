using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 任务包 08「必须产出」8–11 的<strong>分阶段求解器</strong>：冲突组 → 五阶段结果。
    ///
    /// <para>
    /// <strong>固定顺序</strong>（任务包 08:36、验收 :386）：
    /// <list type="number">
    /// <item><see cref="ConflictResolutionPhase.DodgeSpatialRecheck"/>：Dodge <strong>原子位置提交</strong>
    /// 与空间复核。位置提交本身发生在阶段 9（构图<strong>之前</strong>，主方案明确规定的唯一世界写入）；
    /// 本阶段只用阶段 9 冻结的<b>提交前</b>/<b>提交后</b>两份快照 + 冻结 Intent 复核四格表与标签。</item>
    /// <item><see cref="ConflictResolutionPhase.BlockFullResistance"/>：Block 合格载荷完全抵抗
    /// （TriggerTick 内全部入射接触同时处理）。</item>
    /// <item><see cref="ConflictResolutionPhase.ClashSimultaneousSolve"/>：全部 Clash <strong>同时</strong>求解
    /// （<see cref="MomentumClashSolver"/>：每个攻击从原始动量一次性累计全部反向损耗）。</item>
    /// <item><see cref="ConflictResolutionPhase.MoveIntercept"/>：Move 拦截/逃脱（按覆盖标记）。</item>
    /// <item><see cref="ConflictResolutionPhase.RemainingHitsGuardAndAggregate"/>：Remaining Hits 中应用
    /// Guard/被动抵抗并聚合（<see cref="TargetAggregator"/>）；<c>CanReceiveDirectHit</c> 只在这里读取。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>世界写入边界（结构保证）</strong>：本类是<strong>纯函数</strong>——输入是
    /// <see cref="StagedResolutionContext"/> 里的冻结值，输出是不可变结果；
    /// 它<strong>没有</strong>任何写接口（不持有 <c>BattleSimulation</c>/<c>LogicGrid</c>/计划注册表/
    /// 事件队列），因此"查询/求解阶段不得修改世界"不可能被违反：
    /// 唯一的世界写入是阶段 9 的 Dodge 提交（已经发生）与阶段 11/13/14 的提交器。
    /// 终态也<strong>不</strong>在这里改：结果只携带"哪些计划参与 Clash"这一事实，
    /// 由提交器经<strong>统一终态协调器</strong>按稳定顺序提交原因。
    /// </para>
    ///
    /// <para>
    /// <strong>Clash 有效性的唯一门槛</strong>（裁定 3）：构图期<strong>不</strong>收紧
    /// <c>Attack↔Attack</c> 连边，有效性只由求解器的 <c>OppositionFactorQ10[d] &gt; 0</c> 判定
    /// （<c>d ≤ 3</c> 系数为 0 ⇒ 非有效 Clash）。为了让"边存在但无效"不产生退化解，
    /// 本类把每个冲突组的 <c>AttackAttack</c> 边按该门槛过滤成<strong>有效 Clash 子图</strong>，
    /// 只在<strong>连通分量</strong>内调用 <see cref="MomentumClashSolver"/>（求解器自身对
    /// 无效边以 <c>CLASH_INPUT_INVALID</c> 稳定拒绝，绝不静默忽略）。
    /// </para>
    /// </summary>
    public static class StagedConflictResolver
    {
        /// <summary>按冻结阶段序求解；输入不合法/构图失败以稳定码抛出。</summary>
        public static StagedConflictResolution Resolve(StagedResolutionContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (context.GraphErrorCode != null)
            {
                // 冻结口径：整组失败（不丢边、不拆组、不截断目标）⇒ 本 Tick 不产生任何 Resolution。
                return new StagedConflictResolution(
                    context.Graph == null ? 0L : context.Graph.Tick,
                    context.GraphErrorCode, context.GraphFailingGroupKey,
                    Array.Empty<DodgeContactResolution>(), Array.Empty<DodgePlanResolution>(),
                    Array.Empty<BlockPlanResolution>(), Array.Empty<ClashComponentResolution>(),
                    Array.Empty<MoveContactResolution>(), Array.Empty<RemainingHitResolution>());
            }

            ConflictGraph graph = context.Graph;
            if (graph == null)
            {
                throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID, "graph=null");
            }

            var factsByPlan = new Dictionary<long, InteractionPlanFacts>();
            for (int i = 0; i < context.Plans.Count; i++)
            {
                InteractionPlanFacts facts = context.Plans[i];
                if (facts == null) continue;
                if (factsByPlan.ContainsKey(facts.ActionPlanId.Value))
                {
                    throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                        "duplicatePlanFacts=" + facts.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
                }
                factsByPlan[facts.ActionPlanId.Value] = facts;
            }

            var unitSnapshotByUnit = new Dictionary<long, UnitSnapshot>();
            for (int i = 0; i < context.Units.Count; i++)
            {
                UnitSnapshot snapshot = context.Units[i];
                if (snapshot == null) continue;
                if (unitSnapshotByUnit.ContainsKey(snapshot.UnitId))
                {
                    throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                        "duplicateUnitSnapshot=" + snapshot.UnitId.ToString(CultureInfo.InvariantCulture));
                }
                unitSnapshotByUnit[snapshot.UnitId] = snapshot;
            }

            var definitionById = new Dictionary<string, UnitDefinition>(StringComparer.Ordinal);
            for (int i = 0; i < context.UnitDefinitions.Count; i++)
            {
                UnitDefinition definition = context.UnitDefinitions[i];
                if (definition == null || definition.UnitDefinitionId.Value == null) continue;
                definitionById[definition.UnitDefinitionId.Value] = definition;
            }

            var intentsByPlan = new Dictionary<long, CombatIntent>();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                CombatIntent intent = graph.Nodes[i].Intent;
                intentsByPlan[intent.ActionPlanId.Value] = intent;
            }

            // 各阶段先各自成列，最后按阶段序拼装。
            var dodgeContacts = new List<DodgeContactResolution>();
            var blockContacts = new List<BlockContactResolution>();
            var moveContacts = new List<MoveContactResolution>();
            var remaining = new List<RemainingHitResolution>();
            var blockedKeys = new HashSet<string>(StringComparer.Ordinal);
            var dodgedKeys = new HashSet<string>(StringComparer.Ordinal);

            // —— 阶段 1：Dodge 复核（只读；同时标记"被回避"的接触键）——
            ResolveDodgeStage(context, graph, factsByPlan, dodgedKeys, dodgeContacts);
            var dodgePlans = BuildDodgePlanResolutions(context, dodgeContacts);

            // —— 阶段 2：Block 完全抵抗 ——
            ResolveBlockStage(context, graph, factsByPlan, intentsByPlan, definitionById,
                blockedKeys, blockContacts);
            var blockPlans = GroupBlockPlans(blockContacts);

            // —— 阶段 3：Clash 同时求解 ——
            var clashes = ResolveClashStage(context, graph, intentsByPlan);
            var clashTerminated = new HashSet<long>();
            for (int i = 0; i < clashes.Count; i++)
            {
                IReadOnlyList<ActionPlanId> terminated = clashes[i].TerminatedActionPlanIds;
                for (int t = 0; t < terminated.Count; t++) clashTerminated.Add(terminated[t].Value);
            }

            // —— 阶段 4：Move 拦截/逃脱 ——
            ResolveMoveStage(graph, moveContacts);

            // —— 阶段 5：Remaining Hits 中应用 Guard/被动抵抗并聚合 ——
            ResolveRemainingHitsStage(context, graph, factsByPlan, intentsByPlan, unitSnapshotByUnit,
                definitionById, dodgedKeys, blockedKeys, clashTerminated, clashes, moveContacts, remaining);
            var remainingHits = CanonicalizeRemainingHits(remaining);

            dodgeContacts.Sort(CompareDodgeContacts);
            blockContacts.Sort(CompareBlockContacts);
            moveContacts.Sort((x, y) => x.Key.CompareTo(y.Key));

            return new StagedConflictResolution(
                graph.Tick, null, 0L,
                dodgeContacts.AsReadOnly(),
                dodgePlans,
                blockPlans,
                clashes.AsReadOnly(),
                moveContacts.AsReadOnly(),
                remainingHits,
                // 图本身随求解结果一起冻结（构图后不再变更）：消费者（语义事件、
                // 位移请求、伤害提交）据此把接触/聚合投影回冲突组键，而**不**需要
                // 各自再扫一遍图或保留第二份"计划 → 组"的权威。
                graph);
        }

        // ==================================================================
        // 阶段 1：Dodge 复核
        // ==================================================================

        private static void ResolveDodgeStage(
            StagedResolutionContext context,
            ConflictGraph graph,
            Dictionary<long, InteractionPlanFacts> factsByPlan,
            HashSet<string> dodgedKeys,
            List<DodgeContactResolution> results)
        {
            var commitsByPlan = new Dictionary<long, DodgeCommitResult>();
            if (context.DodgeReport != null && context.DodgeReport.Results != null)
            {
                for (int i = 0; i < context.DodgeReport.Results.Count; i++)
                {
                    DodgeCommitResult commit = context.DodgeReport.Results[i];
                    if (commit == null) continue;
                    commitsByPlan[commit.ReactionPlanId.Value] = commit;
                }
            }

            // 成功换位（Committed 且 From != Destination）的防守者单位集合。
            var movedDefenders = new HashSet<long>();
            foreach (KeyValuePair<long, DodgeCommitResult> pair in commitsByPlan)
            {
                if (pair.Value.Moved) movedDefenders.Add(pair.Value.UnitId.Value);
            }

            for (int i = 0; i < graph.Contacts.Count; i++)
            {
                InteractionContact contact = graph.Contacts[i];
                ContactType type = contact.Key.Type;
                if (type != ContactType.AttackDodge && type != ContactType.AttackTarget
                    && type != ContactType.AttackGuard && type != ContactType.AttackMove)
                {
                    continue;
                }

                ActionPlanId attackPlanId = ResolveAttackPlanId(contact.Key, factsByPlan);
                ActionPlanId counterpartyPlanId = ResolveCounterpartyPlanId(contact.Key, attackPlanId);
                if (!factsByPlan.TryGetValue(attackPlanId.Value, out InteractionPlanFacts attackFacts))
                {
                    throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                        "planFacts missing for attack plan="
                        + attackPlanId.Value.ToString(CultureInfo.InvariantCulture));
                }

                CombatIntent intent = FindIntent(graph, attackPlanId);
                if (intent == null)
                {
                    throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                        "intent missing for attack plan="
                        + attackPlanId.Value.ToString(CultureInfo.InvariantCulture));
                }

                UnitId defenderUnitId = DenySideIsAttacker(contact.Key, attackPlanId)
                    ? contact.Key.SecondUnitId
                    : contact.Key.FirstUnitId;

                // 只有"该防守者发生了真实换位"才存在可复核的旧/新格差异。
                if (!movedDefenders.Contains(defenderUnitId.Value)) continue;

                bool dodgeable = (intent.Tags & AttackTagMask.Dodgeable) != 0;
                bool coveredBefore = contact.CoveredBefore;
                bool coveredAfter = contact.CoveredAfter;
                if (!coveredBefore && !coveredAfter) continue;

                DodgeContactOutcome outcome;
                if (!coveredBefore)
                {
                    // 旧格不命中、新格命中：换位"闪进"了新攻击，照常求解（不是回避）。
                    outcome = DodgeContactOutcome.NotApplicable;
                }
                else if (!coveredAfter && dodgeable)
                {
                    // 旧格成立、新格失效的 Dodgeable ⇒ 唯一算作"被回避"的格子。
                    outcome = DodgeContactOutcome.Dodged;
                    dodgedKeys.Add(ContactKeyText(attackPlanId, defenderUnitId));
                }
                else if (!coveredAfter && !dodgeable)
                {
                    // Undodgeable：保留旧接触（不因换位失效，也不追踪两格都不命中的攻击）。
                    outcome = DodgeContactOutcome.RetainedUndodgeable;
                }
                else
                {
                    outcome = DodgeContactOutcome.StillHit;
                }

                ActionPlanId dodgePlanId = type == ContactType.AttackDodge
                    ? counterpartyPlanId
                    : default(ActionPlanId);
                commitsByPlan.TryGetValue(dodgePlanId.Value, out DodgeCommitResult commit);
                if (commit == null) dodgePlanId = default(ActionPlanId);

                results.Add(new DodgeContactResolution(
                    contact.Key, attackPlanId, attackFacts.OwnerUnitId, dodgePlanId, defenderUnitId,
                    coveredBefore, coveredAfter,
                    commit != null && commit.Committed,
                    commit == null ? null : commit.Code,
                    outcome));
            }
        }

        private static IReadOnlyList<DodgePlanResolution> BuildDodgePlanResolutions(
            StagedResolutionContext context, List<DodgeContactResolution> contacts)
        {
            var results = new List<DodgePlanResolution>();
            if (context.DodgeReport == null || context.DodgeReport.Results == null) return results.AsReadOnly();

            var ordered = new List<DodgeCommitResult>();
            for (int i = 0; i < context.DodgeReport.Results.Count; i++)
            {
                if (context.DodgeReport.Results[i] != null) ordered.Add(context.DodgeReport.Results[i]);
            }
            ordered.Sort((a, b) => a.ReactionPlanId.Value.CompareTo(b.ReactionPlanId.Value));

            for (int i = 0; i < ordered.Count; i++)
            {
                DodgeCommitResult commit = ordered[i];
                var own = new List<DodgeContactResolution>();
                for (int c = 0; c < contacts.Count; c++)
                {
                    if (contacts[c].DodgePlanId == commit.ReactionPlanId) own.Add(contacts[c]);
                }
                own.Sort(CompareDodgeContacts);
                results.Add(new DodgePlanResolution(
                    commit.ReactionPlanId, commit.UnitId, commit.From, commit.Destination,
                    commit.Committed, commit.Code, own.AsReadOnly()));
            }

            return results.AsReadOnly();
        }

        private static int CompareDodgeContacts(DodgeContactResolution x, DodgeContactResolution y)
        {
            int byKey = x.Key.CompareTo(y.Key);
            return byKey != 0 ? byKey : x.DodgePlanId.Value.CompareTo(y.DodgePlanId.Value);
        }

        // ==================================================================
        // 阶段 2：Block 完全抵抗
        // ==================================================================

        private static void ResolveBlockStage(
            StagedResolutionContext context,
            ConflictGraph graph,
            Dictionary<long, InteractionPlanFacts> factsByPlan,
            Dictionary<long, CombatIntent> intentsByPlan,
            Dictionary<string, UnitDefinition> definitionById,
            HashSet<string> blockedKeys,
            List<BlockContactResolution> results)
        {
            var blockPayloads = new Dictionary<string, BlockPayloadSpec>(StringComparer.Ordinal);

            for (int i = 0; i < graph.Contacts.Count; i++)
            {
                InteractionContact contact = graph.Contacts[i];
                if (contact.Key.Type != ContactType.AttackBlock) continue;

                ActionPlanId attackPlanId = ResolveAttackPlanId(contact.Key, factsByPlan);
                ActionPlanId blockPlanId = ResolveCounterpartyPlanId(contact.Key, attackPlanId);
                if (blockPlanId.Value <= 0L) continue;

                if (!intentsByPlan.TryGetValue(attackPlanId.Value, out CombatIntent intent))
                {
                    throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                        "intent missing for attack plan="
                        + attackPlanId.Value.ToString(CultureInfo.InvariantCulture));
                }

                InteractionPlanFacts blockFacts = RequireFacts(factsByPlan, blockPlanId);
                BlockPayloadSpec block = RequireBlockPayload(blockPayloads, context, blockPlanId);
                UnitId defenderUnitId = blockFacts.OwnerUnitId;

                var components = new List<DamageComponentSpec>(intent.DamageComponents);
                var targetKey = new TargetContactKey(TargetContactType.DirectHit, intent.OwnerUnitId,
                    attackPlanId, defenderUnitId, blockPlanId);

                // Block 判据只用"是否真的降低过合格载荷"，因此控制阻力取中性值 1：
                // 它同时把 TotalImpactUnits 变成"抵抗后动量之和"，与伤害一起构成判定面
                // （Block 的伤害与动量抵抗都是 1024，两者都要为 0 才算完全抵抗）。
                const int neutralControlResistance = 1;
                TargetAggregateResolution undefended = TargetAggregator.Aggregate(new TargetAggregationInput(
                    defenderUnitId,
                    EmptyResistance(),
                    neutralControlResistance,
                    new[] { TargetAggregator.BuildDefendedContact(targetKey, intent.Momentum.Direction,
                        intent.Momentum.MomentumUnits, components, intent.Tags, null, null) }));

                TargetAggregateResolution defended = TargetAggregator.Aggregate(new TargetAggregationInput(
                    defenderUnitId,
                    EmptyResistance(),
                    neutralControlResistance,
                    new[] { TargetAggregator.BuildDefendedContact(targetKey, intent.Momentum.Direction,
                        intent.Momentum.MomentumUnits, components, intent.Tags, null, block) }));

                long raw = undefended.TotalDamageQ10;
                long after = defended.TotalDamageQ10;
                bool reducedSomething = after < raw
                    || defended.TotalImpactUnits < undefended.TotalImpactUnits;

                BlockContactOutcome outcome;
                if (!reducedSomething)
                {
                    outcome = BlockContactOutcome.BlockIneffective;
                }
                else if (after == 0L && defended.TotalImpactUnits == 0L)
                {
                    outcome = BlockContactOutcome.Blocked;
                }
                else
                {
                    outcome = BlockContactOutcome.PartiallyBlocked;
                }

                if (outcome == BlockContactOutcome.Blocked)
                {
                    blockedKeys.Add(ContactKeyText(attackPlanId, defenderUnitId));
                }

                results.Add(new BlockContactResolution(
                    contact.Key, attackPlanId, intent.OwnerUnitId, blockPlanId, defenderUnitId,
                    outcome, raw, after,
                    undefended.TotalImpactUnits > int.MaxValue ? int.MaxValue : (int)undefended.TotalImpactUnits,
                    defended.TotalImpactUnits > int.MaxValue ? int.MaxValue : (int)defended.TotalImpactUnits));
            }
        }

        private static BlockPayloadSpec RequireBlockPayload(
            Dictionary<string, BlockPayloadSpec> cache,
            StagedResolutionContext context,
            ActionPlanId planId)
        {
            string specId = RequireSpecId(context, planId);
            if (cache.TryGetValue(specId, out BlockPayloadSpec cached)) return cached;

            BlockPayloadSpec payload = null;
            for (int i = 0; i < context.BlockPayloads.Count; i++)
            {
                BlockPayloadEntry entry = context.BlockPayloads[i];
                if (entry == null || !StringComparer.Ordinal.Equals(entry.ActionSpecId, specId)) continue;
                payload = entry.Payload;
                break;
            }
            if (payload == null)
            {
                throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                    "block payload missing for spec=" + specId);
            }
            cache[specId] = payload;
            return payload;
        }

        /// <summary>计划 → 动作定义 ID（唯一来源是装配侧投影的 <see cref="PlanSpecEntry"/> 表）。</summary>
        private static string RequireSpecId(StagedResolutionContext context, ActionPlanId planId)
        {
            for (int i = 0; i < context.PlanSpecs.Count; i++)
            {
                PlanSpecEntry entry = context.PlanSpecs[i];
                if (entry == null || entry.ActionPlanId != planId) continue;
                return entry.ActionSpecId ?? string.Empty;
            }
            throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                "plan spec missing for plan=" + planId.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static IReadOnlyList<BlockPlanResolution> GroupBlockPlans(List<BlockContactResolution> contacts)
        {
            var planIds = new List<long>();
            for (int i = 0; i < contacts.Count; i++)
            {
                long id = contacts[i].BlockPlanId.Value;
                if (!planIds.Contains(id)) planIds.Add(id);
            }
            planIds.Sort();

            var plans = new List<BlockPlanResolution>(planIds.Count);
            for (int p = 0; p < planIds.Count; p++)
            {
                var own = new List<BlockContactResolution>();
                for (int i = 0; i < contacts.Count; i++)
                {
                    if (contacts[i].BlockPlanId.Value == planIds[p]) own.Add(contacts[i]);
                }
                own.Sort(CompareBlockContacts);
                plans.Add(new BlockPlanResolution(new ActionPlanId(planIds[p]),
                    own.Count == 0 ? default(UnitId) : own[0].DefenderUnitId, own.AsReadOnly()));
            }
            return plans.AsReadOnly();
        }

        private static int CompareBlockContacts(BlockContactResolution x, BlockContactResolution y)
            => x.Key.CompareTo(y.Key);

        // ==================================================================
        // 阶段 3：Clash 同时求解（有效性子图 → 连通分量 → 求解器）
        // ==================================================================

        private static List<ClashComponentResolution> ResolveClashStage(
            StagedResolutionContext context,
            ConflictGraph graph,
            Dictionary<long, CombatIntent> intentsByPlan)
        {
            var results = new List<ClashComponentResolution>();

            for (int g = 0; g < graph.Groups.Count; g++)
            {
                ConflictGroup group = graph.Groups[g];

                // 本组参与 Clash 的攻击（有自身 Intent 的节点）。
                var participants = new List<ClashParticipant>();
                var planIds = new List<long>();
                for (int n = 0; n < group.NodeIndices.Count; n++)
                {
                    CombatIntent intent = graph.Nodes[group.NodeIndices[n]].Intent;
                    if (intentsByPlan.ContainsKey(intent.ActionPlanId.Value))
                    {
                        participants.Add(new ClashParticipant(intent.OwnerUnitId, intent.ActionPlanId, intent.Momentum));
                        planIds.Add(intent.ActionPlanId.Value);
                    }
                }

                // 有效 Clash 边：只由求解器门槛（OppositionFactorQ10[d] > 0）决定（裁定 3）。
                var pairs = new List<ClashPair>();
                for (int c = 0; c < group.ContactKeys.Count; c++)
                {
                    ContactKey key = group.ContactKeys[c];
                    if (key.Type != ContactType.AttackAttack) continue;
                    if (!intentsByPlan.ContainsKey(key.FirstPlanId.Value)
                        || !intentsByPlan.ContainsKey(key.SecondPlanId.Value)) continue;

                    CombatIntent left = intentsByPlan[key.FirstPlanId.Value];
                    CombatIntent right = intentsByPlan[key.SecondPlanId.Value];
                    int distance = MomentumRuleTable.MinimalRingDistance(
                        left.Momentum.Direction, right.Momentum.Direction);
                    if (MomentumRuleTable.OppositionFactorQ10(distance) <= 0) continue;   // d ≤ 3 ⇒ 非有效

                    pairs.Add(new ClashPair(key.FirstPlanId, key.SecondPlanId));
                }

                if (pairs.Count == 0) continue;

                // 连通分量：只在组内按有效边合并（求解器要求每个参与者至少有一条有效边）。
                int count = planIds.Count;
                var parent = new int[count];
                for (int i = 0; i < count; i++) parent[i] = i;
                for (int e = 0; e < pairs.Count; e++)
                {
                    int left = planIds.IndexOf(pairs[e].A.Value);
                    int right = planIds.IndexOf(pairs[e].B.Value);
                    if (left < 0 || right < 0) continue;
                    Union(parent, left, right);
                }

                var components = new List<List<int>>();
                for (int i = 0; i < count; i++)
                {
                    int root = Find(parent, i);
                    List<int> bucket = null;
                    for (int b = 0; b < components.Count; b++)
                    {
                        if (Find(parent, components[b][0]) == root) { bucket = components[b]; break; }
                    }
                    if (bucket == null)
                    {
                        bucket = new List<int>();
                        components.Add(bucket);
                    }
                    bucket.Add(i);
                }

                for (int b = 0; b < components.Count; b++)
                {
                    List<int> component = components[b];
                    if (component.Count < 2) continue;   // 孤立节点不构成 Clash（求解器同口径拒绝）

                    var componentParticipants = new List<ClashParticipant>(component.Count);
                    bool[] inComponent = new bool[count];
                    for (int i = 0; i < component.Count; i++)
                    {
                        inComponent[component[i]] = true;
                        componentParticipants.Add(participants[component[i]]);
                    }

                    var componentPairs = new List<ClashPair>();
                    for (int e = 0; e < pairs.Count; e++)
                    {
                        int left = planIds.IndexOf(pairs[e].A.Value);
                        int right = planIds.IndexOf(pairs[e].B.Value);
                        if (left < 0 || right < 0) continue;
                        if (inComponent[left] && inComponent[right]) componentPairs.Add(pairs[e]);
                    }

                    MomentumClashResolution clash = MomentumClashSolver.Solve(
                        new MomentumClashInput(componentParticipants, componentPairs), context.ClashQuota);
                    results.Add(new ClashComponentResolution(group.GroupKey, clash));
                }
            }

            results.Sort((a, b) => a.ConflictGroupKey.CompareTo(b.ConflictGroupKey));
            return results;
        }

        // ==================================================================
        // 阶段 4：Move 拦截 / 逃脱
        // ==================================================================

        private static void ResolveMoveStage(ConflictGraph graph, List<MoveContactResolution> results)
        {
            for (int i = 0; i < graph.Contacts.Count; i++)
            {
                InteractionContact contact = graph.Contacts[i];
                if (contact.Key.Type != ContactType.AttackMove) continue;
                if (!contact.CounterpartyPlanId.IsValid) continue;

                MoveContactOutcome outcome;
                if (contact.CoveredAfter) outcome = MoveContactOutcome.Intercepted;
                else if (contact.CoveredBefore) outcome = MoveContactOutcome.Escaped;
                else outcome = MoveContactOutcome.NoCoverage;

                results.Add(new MoveContactResolution(
                    contact.Key, contact.AttackerPlanId, contact.AttackerUnitId,
                    contact.CounterpartyPlanId, contact.CounterpartyUnitId,
                    contact.CoveredBefore, contact.CoveredAfter, outcome));
            }
        }

        // ==================================================================
        // 阶段 5：Remaining Hits + Guard/被动抵抗 + 聚合
        // ==================================================================

        private static void ResolveRemainingHitsStage(
            StagedResolutionContext context,
            ConflictGraph graph,
            Dictionary<long, InteractionPlanFacts> factsByPlan,
            Dictionary<long, CombatIntent> intentsByPlan,
            Dictionary<long, UnitSnapshot> unitSnapshotByUnit,
            Dictionary<string, UnitDefinition> definitionById,
            HashSet<string> dodgedKeys,
            HashSet<string> blockedKeys,
            HashSet<long> clashTerminated,
            IReadOnlyList<ClashComponentResolution> clashes,
            List<MoveContactResolution> moveContacts,
            List<RemainingHitResolution> results)
        {
            // 目标单位 → 其接触列表（先全部收集，再一次性聚合；"一次量化"只发生在聚合器内部）。
            var byTarget = new List<long>();
            var contactsByTarget = new List<List<RemainingHitResolution>>();

            for (int i = 0; i < graph.Contacts.Count; i++)
            {
                InteractionContact contact = graph.Contacts[i];
                ContactType type = contact.Key.Type;
                if (type != ContactType.AttackTarget && type != ContactType.AttackGuard
                    && type != ContactType.AttackMove && type != ContactType.AttackDodge
                    && type != ContactType.AttackBlock) continue;

                ActionPlanId attackPlanId = ResolveAttackPlanId(contact.Key, factsByPlan);
                ActionPlanId counterpartyPlanId = ResolveCounterpartyPlanId(contact.Key, attackPlanId);

                if (clashTerminated.Contains(attackPlanId.Value)) continue;      // Clash 参与者的直接 Hit 失效
                if (!intentsByPlan.TryGetValue(attackPlanId.Value, out CombatIntent intent)) continue;

                if (!factsByPlan.TryGetValue(attackPlanId.Value, out InteractionPlanFacts attackFacts)) continue;
                UnitId targetUnitId = contact.TargetUnitId;

                if (dodgedKeys.Contains(ContactKeyText(attackPlanId, targetUnitId))) continue;
                if (blockedKeys.Contains(ContactKeyText(attackPlanId, targetUnitId))) continue;

                // 目标不在本 Tick 的空间投影里（已死亡注销 / 未注册）⇒ 不产生接触结算。
                if (!unitSnapshotByUnit.TryGetValue(targetUnitId.Value, out UnitSnapshot targetSnapshot)) continue;
                if (!targetSnapshot.IsAlive) continue;

                GuardPayloadSpec guard = null;
                ActionPlanId guardPlanId = default(ActionPlanId);
                string guardSpecId = null;
                string blockSpecId = null;
                ActionPlanId blockPlanId = default;
                if (type == ContactType.AttackBlock && counterpartyPlanId.IsValid
                    && factsByPlan.TryGetValue(counterpartyPlanId.Value, out InteractionPlanFacts blockFacts)
                    && blockFacts.TriggerTick == graph.Tick)
                {
                    blockSpecId = RequireSpecId(context, counterpartyPlanId);
                    blockPlanId = counterpartyPlanId;
                }
                if (type == ContactType.AttackGuard && counterpartyPlanId.Value > 0L
                    && factsByPlan.TryGetValue(counterpartyPlanId.Value, out InteractionPlanFacts guardFacts)
                    && guardFacts.IsActiveAt(graph.Tick))
                {
                    guard = FindGuardPayload(context, counterpartyPlanId);
                    guardSpecId = RequireSpecId(context, counterpartyPlanId);
                    guardPlanId = guardFacts.ActionPlanId;
                }

                // Remaining Hit 只有在**特殊阶段全部结束**之后才查询 CanReceiveDirectHit
                // （任务包 08:76）。返回 false ⇒ 不产生直接伤害，但接触事实仍被保留（审计面）。
                var resolution = new RemainingHitResolution(
                    contact.Key, attackPlanId, attackFacts.OwnerUnitId,
                    RequireSpecId(context, attackPlanId),
                    intent.Momentum.Direction, intent.Momentum.MomentumUnits, intent.DamageComponents, intent.Tags,
                    guardPlanId, guardSpecId, targetSnapshot.CanReceiveDirectHit,
                    !targetSnapshot.CanReceiveDirectHit, null, blockSpecId, blockPlanId);

                int index = byTarget.IndexOf(targetUnitId.Value);
                if (index < 0)
                {
                    byTarget.Add(targetUnitId.Value);
                    contactsByTarget.Add(new List<RemainingHitResolution> { resolution });
                }
                else
                {
                    contactsByTarget[index].Add(resolution);
                }
            }

            // Residual impact is a distinct contact payload. Participating attacks lose all direct
            // hits, but their positive remainder must still reach the same aggregate/commit path.
            foreach (ClashComponentResolution component in clashes)
            {
                foreach (ClashResidualImpact residual in component.Clash.ResidualImpacts)
                {
                    if (!unitSnapshotByUnit.TryGetValue(residual.RecipientUnitId.Value, out var target) || !target.IsAlive) continue;
                    var key = ContactKey.Create(ContactType.AttackAttack, residual.SourceUnitId, residual.SourceActionPlanId,
                        residual.RecipientUnitId, residual.RecipientActionPlanId, residual.RecipientUnitId);
                    var resolution = new RemainingHitResolution(key, residual.SourceActionPlanId, residual.SourceUnitId,
                        RequireSpecId(context, residual.SourceActionPlanId), residual.SourceDirection, residual.ResidualMomentumUnits,
                        new[] { MomentumQuantizer.ClashResidualComponent(residual.ResidualMomentumUnits) }, AttackTagMask.None,
                        default, null, target.CanReceiveDirectHit, false, null, ContactKind: TargetContactType.ClashResidualImpact,
                        RecipientPlanId: residual.RecipientActionPlanId);
                    int index = byTarget.IndexOf(residual.RecipientUnitId.Value);
                    if (index < 0)
                    {
                        byTarget.Add(residual.RecipientUnitId.Value);
                        contactsByTarget.Add(new List<RemainingHitResolution> { resolution });
                    }
                    else contactsByTarget[index].Add(resolution);
                }
            }

            // 同一攻击计划对同一目标出现多条未消解接触 ⇒ 会重复结算，稳定拒绝。
            for (int t = 0; t < contactsByTarget.Count; t++)
            {
                List<RemainingHitResolution> own = contactsByTarget[t];
                for (int a = 0; a < own.Count; a++)
                {
                    for (int b = a + 1; b < own.Count; b++)
                    {
                        if (own[a].AttackPlanId == own[b].AttackPlanId && own[a].ContactKind == own[b].ContactKind)
                        {
                            throw new LogicDefinitionException(
                                StagedResolutionCodes.STAGED_RESOLUTION_DUPLICATE_DIRECT_HIT,
                                "plan=" + own[a].AttackPlanId.Value.ToString(CultureInfo.InvariantCulture)
                                + " target=" + byTarget[t].ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
            }

            // 每个目标一次性聚合（"同一目标的接触先按 ContactKey → DamageChannelId 排序"由聚合器负责）。
            for (int t = 0; t < contactsByTarget.Count; t++)
            {
                List<RemainingHitResolution> own = contactsByTarget[t];
                UnitSnapshot targetSnapshot = unitSnapshotByUnit[byTarget[t]];

                var targetUnitId = new UnitId(targetSnapshot.UnitId);

                var inputs = new List<TargetContact>(own.Count);
                foreach (RemainingHitResolution hit in own)
                {
                    if (hit.IsDirectHitSuppressed) continue;
                    GuardPayloadSpec guardPayload = hit.GuardSpecId == null
                        ? null
                        : FindGuardPayloadBySpecId(context, hit.GuardSpecId);
                    BlockPayloadSpec blockPayload = hit.BlockSpecId == null ? null
                        : RequireBlockPayload(new Dictionary<string, BlockPayloadSpec>(StringComparer.Ordinal), context, hit.BlockPlanId);
                    inputs.Add(TargetAggregator.BuildDefendedContact(
                        new TargetContactKey(hit.ContactKind, hit.AttackerUnitId,
                            hit.AttackPlanId, targetUnitId, hit.RecipientPlanId.IsValid ? hit.RecipientPlanId
                                : hit.BlockPlanId.IsValid ? hit.BlockPlanId : hit.GuardPlanId),
                        hit.IncomingDirection, hit.IncomingMomentumUnits,
                        hit.DamageComponents, hit.AttackTags, guardPayload, blockPayload));
                }

                TargetAggregateResolution aggregate = TargetAggregator.Aggregate(new TargetAggregationInput(
                    targetUnitId,
                    PassiveResistanceOf(definitionById, targetSnapshot),
                    ControlResistanceOf(definitionById, targetSnapshot),
                    inputs));

                for (int i = 0; i < own.Count; i++)
                {
                    RemainingHitResolution hit = own[i];
                    results.Add(hit with { Aggregate = aggregate });
                }
            }
        }

        private static IReadOnlyList<RemainingHitResolution> CanonicalizeRemainingHits(            List<RemainingHitResolution> hits)
        {
            var ordered = new List<RemainingHitResolution>(hits);
            ordered.Sort((x, y) => x.Key.CompareTo(y.Key));
            return ordered.AsReadOnly();
        }

        private static GuardPayloadSpec FindGuardPayload(StagedResolutionContext context, ActionPlanId planId)
            => FindGuardPayloadBySpecId(context, RequireSpecId(context, planId));

        private static GuardPayloadSpec FindGuardPayloadBySpecId(StagedResolutionContext context, string specId)
        {
            for (int i = 0; i < context.GuardPayloads.Count; i++)
            {
                GuardPayloadEntry entry = context.GuardPayloads[i];
                if (entry == null || !StringComparer.Ordinal.Equals(entry.ActionSpecId, specId)) continue;
                return entry.Payload;
            }
            throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                "guard payload missing for spec=" + (specId ?? string.Empty));
        }

        private static IReadOnlyDictionary<DamageChannelId, int> PassiveResistanceOf(
            Dictionary<string, UnitDefinition> definitionById, UnitSnapshot snapshot)
            => definitionById.TryGetValue(snapshot.DefinitionId ?? string.Empty, out UnitDefinition definition)
               && definition.BaseDamageResistanceQ10 != null
                ? definition.BaseDamageResistanceQ10
                : EmptyResistance();

        private static int ControlResistanceOf(
            Dictionary<string, UnitDefinition> definitionById, UnitSnapshot snapshot)
        {
            if (!definitionById.TryGetValue(snapshot.DefinitionId ?? string.Empty, out UnitDefinition definition))
            {
                throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                    "unit definition missing for unit=" + snapshot.UnitId.ToString(CultureInfo.InvariantCulture)
                    + " definition=" + (snapshot.DefinitionId ?? string.Empty));
            }
            return MomentumQuantizer.QuantizeControlResistanceUnits(definition.Mass, definition.MomentumSpeed);
        }

        // ==================================================================
        // 规范键助手
        // ==================================================================

        /// <summary>
        /// 接触的"攻击侧计划"：<c>Intent ↔ Intent</c> 边（AttackAttack / SharedTarget）两侧都是攻击，
        /// 因此对它们返回 0（调用方在阶段 1/2/5 里已经按类型过滤掉了这两类）。
        /// </summary>
        private static ActionPlanId ResolveAttackPlanId(
            ContactKey key, Dictionary<long, InteractionPlanFacts> factsByPlan)
        {
            if (factsByPlan.TryGetValue(key.FirstPlanId.Value, out InteractionPlanFacts first)
                && first.ActionType == ActionType.Attack)
            {
                return key.FirstPlanId;
            }
            if (factsByPlan.TryGetValue(key.SecondPlanId.Value, out InteractionPlanFacts second)
                && second.ActionType == ActionType.Attack)
            {
                return key.SecondPlanId;
            }
            throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                "no attack side in contact=" + key.CanonicalText);
        }

        private static ActionPlanId ResolveCounterpartyPlanId(ContactKey key, ActionPlanId attackPlanId)
            => key.FirstPlanId == attackPlanId ? key.SecondPlanId : key.FirstPlanId;

        private static bool DenySideIsAttacker(ContactKey key, ActionPlanId attackPlanId)
            => key.FirstPlanId == attackPlanId;

        /// <summary>攻击 × 目标的稳定文本键（去重与交付判据都用它，不使用发现顺序）。</summary>
        private static string ContactKeyText(ActionPlanId attackPlanId, UnitId targetUnitId)
            => attackPlanId.Value.ToString(CultureInfo.InvariantCulture) + "->"
               + targetUnitId.Value.ToString(CultureInfo.InvariantCulture);

        private static InteractionPlanFacts RequireFacts(
            Dictionary<long, InteractionPlanFacts> factsByPlan, ActionPlanId planId)
        {
            if (factsByPlan.TryGetValue(planId.Value, out InteractionPlanFacts facts)) return facts;
            throw new LogicDefinitionException(StagedResolutionCodes.STAGED_RESOLUTION_INPUT_INVALID,
                "planFacts missing for plan=" + planId.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static CombatIntent FindIntent(ConflictGraph graph, ActionPlanId planId)
        {
            int index = graph.NodeIndexOf(planId);
            return index < 0 ? null : graph.Nodes[index].Intent;
        }

        private static IReadOnlyDictionary<DamageChannelId, int> EmptyResistance()
            => EmptyResistances;

        private static readonly IReadOnlyDictionary<DamageChannelId, int> EmptyResistances =
            new Dictionary<DamageChannelId, int>();

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
            if (leftRoot < rightRoot) parent[rightRoot] = leftRoot;
            else parent[leftRoot] = rightRoot;
        }
    }

    /// <summary>计划 → 动作定义 ID 的只读条目（装配侧从已验证定义投影）。</summary>
    public sealed record PlanSpecEntry(ActionPlanId ActionPlanId, string ActionSpecId);

    /// <summary>Block 载荷的只读条目（装配侧从已验证定义投影；求解器不解析 ActionSpec）。</summary>
    public sealed record BlockPayloadEntry(string ActionSpecId, BlockPayloadSpec Payload);

    /// <summary>Guard 载荷的只读条目（同上）。</summary>
    public sealed record GuardPayloadEntry(string ActionSpecId, GuardPayloadSpec Payload);
}
