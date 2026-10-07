using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Interactions
{
    /// <summary>
    /// 接触候选构建器（任务 08「必须产出」5 的<strong>构图部分</strong> /
    /// 主方案 0.4 目标资格分层 / 08-多方仲裁与伤害.md:30、:62-:65）。
    ///
    /// 职责边界（诚实声明）：
    /// <list type="bullet">
    /// <item><strong>本类型负责</strong>：节点资格过滤、空间相交（旧格/新格两次查询）、
    /// 唯一关系解析器 + <c>AllowedTargetRelations</c> 验证、共享目标、Attack↔Attack 直接交互、
    /// 按 <see cref="ContactKey"/> 去重与规范排序。</item>
    /// <item><strong>本类型不负责</strong>：Dodge 四格表的分支判定（Dodged / 保留 Undodgeable 接触）、
    /// Block/Guard 载荷抵抗、Clash 动量求解与三类 Resolution。它只把
    /// <see cref="InteractionContact.CoveredBefore"/>/<see cref="InteractionContact.CoveredAfter"/>
    /// 这一对原始空间事实交给求解器。</item>
    /// </list>
    ///
    /// 禁止项（全部在结构上满足）：
    /// <list type="bullet">
    /// <item>不读取 <c>Guarding/Blocking/Dodging</c> 状态、<c>CanReceiveDirectHit</c>、<c>UnitState</c>；
    /// 输入类型里没有这些字段。</item>
    /// <item>不读取 Controller 或玩家标志推断敌我：<c>ControllerId</c> 只作为审计字段被拷贝，
    /// 不参与任何比较、过滤与键。</item>
    /// <item>不存在控制器差异导致的连边：唯一的 Intent↔Intent 边来自区域相交与共享目标。</item>
    /// <item>不使用 <c>HashSet</c>/<c>Dictionary</c> 的枚举顺序：全部查找用排序 + 二分，
    /// 全部输出用规范排序。</item>
    /// </list>
    /// </summary>
    public static class InteractionCandidateBuilder
    {
        /// <summary>
        /// 构建接触候选集合。输入排列（Intent 顺序、计划表顺序、两个空间表顺序）不得改变输出：
        /// 全部查找先按稳定键排序再二分，全部输出最终按 <c>(IntentSequence)</c> / <c>ContactKey</c> 规范排序。
        /// </summary>
        public static InteractionCandidateSet Build(ConflictGraphInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Factions == null)
            {
                throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID, "factions=null");
            }

            long tick = input.Tick;

            InteractionPlanFacts[] plans = CanonicalPlans(input.Plans);
            UnitOccupancy[] before = CanonicalUnits(input.UnitsBefore, "before");
            UnitOccupancy[] after = CanonicalUnits(input.UnitsAfter, "after");
            ValidateSameUnitSets(before, after);

            CombatIntent[] nodes = EligibleNodes(input.FrozenIntents, plans, tick);
            CombatIntentContract.ValidateProducedOnce(nodes);

            var contacts = new List<InteractionContact>();

            // ---- 1) Attack → Unit：固定 PrimaryTarget / 区域全部目标，两次空间查询的并集 ----
            for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            {
                CombatIntent intent = nodes[nodeIndex];
                for (int u = 0; u < after.Length; u++)
                {
                    UnitId targetUnitId = after[u].UnitId;

                    // PrimaryTargetOnly：目标由计划固定，仲裁期不得临时改选或自动替换。
                    if (intent.TargetPolicy == TargetPolicy.PrimaryTargetOnly
                        && (!intent.PrimaryTargetUnitId.HasValue || intent.PrimaryTargetUnitId.Value != targetUnitId))
                    {
                        continue;
                    }

                    // 唯一关系解析器 + 该攻击自己的 AllowedTargetRelations（单体/AOE/反应共用同一分类）。
                    if (!input.Factions.Allows(intent.AllowedTargetRelations, intent.OwnerUnitId, targetUnitId))
                    {
                        continue;
                    }

                    bool coveredBefore = before[u].IntersectsCanonical(intent.AreaPoints);
                    bool coveredAfter = after[u].IntersectsCanonical(intent.AreaPoints);
                    if (!coveredBefore && !coveredAfter) continue;

                    AppendUnitContacts(contacts, plans, nodeIndex, intent, targetUnitId, coveredBefore, coveredAfter, tick);
                }
            }

            // ---- 2) Attack ↔ Attack：两个 Intent 的有效区域相交（直接交互）----
            // 不由 Controller 不同自动生成，也不因 Allied 自动跳过：这里只做整数点集相交。
            for (int i = 0; i < nodes.Length; i++)
            {
                for (int j = i + 1; j < nodes.Length; j++)
                {
                    if (!nodes[i].AreaIntersects(nodes[j])) continue;
                    contacts.Add(BuildNodeToNodeContact(ContactType.AttackAttack, nodes, i, j, default(UnitId)));
                }
            }

            // ---- 3) 共享目标：两个或更多攻击同 Tick 指向同一目标单位 ----
            AppendSharedTargetContacts(contacts, nodes, before, after, input.Factions);

            InteractionContact[] canonical = CanonicalContacts(contacts);
            return new InteractionCandidateSet(tick, nodes, canonical);
        }

        // ------------------------------------------------------------------ 节点资格

        /// <summary>
        /// 节点资格：只有本 Tick <strong>有效</strong> Intent 入图。
        ///
        /// 有效 = 计划事实存在 且 计划<strong>非终态</strong> 且 <c>ImpactTick == tick</c>
        /// 且本 Tick 落在计划有效区间内。
        ///
        /// 刻意<strong>不</strong>读取计划的 Recovery/Active 阶段或单位状态：
        /// 计划在 <c>ImpactTick</c> 上可以已进入 Recovery，合法 Intent 必须照常入图
        /// （08-多方仲裁与伤害.md:59、验收 :385）。
        /// </summary>
        public static CombatIntent[] EligibleNodes(
            IReadOnlyList<CombatIntent> frozenIntents, InteractionPlanFacts[] canonicalPlans, long tick)
        {
            if (frozenIntents == null || frozenIntents.Count == 0) return Array.Empty<CombatIntent>();

            var nodes = new List<CombatIntent>(frozenIntents.Count);
            for (int i = 0; i < frozenIntents.Count; i++)
            {
                CombatIntent intent = frozenIntents[i];
                if (intent == null) continue;

                int planIndex = IndexOfPlan(canonicalPlans, intent.ActionPlanId);
                if (planIndex < 0)
                {
                    throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID,
                        "planFacts missing for plan=" + intent.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
                }

                InteractionPlanFacts facts = canonicalPlans[planIndex];
                if (facts.IsTerminal) continue;          // 已终态：不入图
                if (intent.ImpactTick != tick) continue; // 不是本 Tick 到期：不入图
                if (!facts.CoversTick(tick)) continue;   // 不在有效时间区间：不入图

                nodes.Add(intent);
            }

            CombatIntent[] result = nodes.ToArray();
            Array.Sort(result, GlobalIntentQueue.CompareByStableKey);
            return result;
        }

        // ------------------------------------------------------------------ Attack → Unit 接触

        private static void AppendUnitContacts(
            List<InteractionContact> contacts,
            InteractionPlanFacts[] plans,
            int nodeIndex,
            CombatIntent intent,
            UnitId targetUnitId,
            bool coveredBefore,
            bool coveredAfter,
            long tick)
        {
            bool anyCounterparty = false;

            for (int p = 0; p < plans.Length; p++)
            {
                InteractionPlanFacts facts = plans[p];
                if (facts.OwnerUnitId != targetUnitId) continue;
                if (facts.IsTerminal) continue;

                ContactType type;
                if (!TryClassifyCounterparty(facts, intent, tick, out type)) continue;

                anyCounterparty = true;
                ContactKey key = ContactKey.Create(
                    type, intent.OwnerUnitId, intent.ActionPlanId, targetUnitId, facts.ActionPlanId, targetUnitId);

                contacts.Add(new InteractionContact(
                    key,
                    nodeIndex,
                    -1,
                    intent.OwnerUnitId,
                    intent.ActionPlanId,
                    targetUnitId,
                    facts.ActionPlanId,
                    facts.ActionType,
                    targetUnitId,
                    coveredBefore,
                    coveredAfter));
            }

            if (anyCounterparty) return;

            // 无对手计划：普通命中接触。
            ContactKey plainKey = ContactKey.Create(
                ContactType.AttackTarget, intent.OwnerUnitId, intent.ActionPlanId,
                targetUnitId, default(ActionPlanId), targetUnitId);

            contacts.Add(new InteractionContact(
                plainKey,
                nodeIndex,
                -1,
                intent.OwnerUnitId,
                intent.ActionPlanId,
                targetUnitId,
                default(ActionPlanId),
                ActionType.None,
                targetUnitId,
                coveredBefore,
                coveredAfter));
        }

        /// <summary>
        /// 对手计划与本 Tick 该攻击是否形成接触：
        /// Block/Dodge 必须 <c>TriggerTick == 攻击 ImpactTick</c>；Guard 必须处于 Active 区间；
        /// Move 必须仍占用本 Tick（<c>[StartTick, EndTick)</c>）。
        /// 只读生命周期事实，不读任何状态布尔值。
        /// </summary>
        private static bool TryClassifyCounterparty(
            InteractionPlanFacts facts, CombatIntent intent, long tick, out ContactType type)
        {
            switch (facts.ActionType)
            {
                case ActionType.Dodge:
                    if (facts.TriggerTick > 0 && facts.TriggerTick == intent.ImpactTick)
                    {
                        type = ContactType.AttackDodge;
                        return true;
                    }
                    break;
                case ActionType.Block:
                    if (facts.TriggerTick > 0 && facts.TriggerTick == intent.ImpactTick)
                    {
                        type = ContactType.AttackBlock;
                        return true;
                    }
                    break;
                case ActionType.Guard:
                    if (facts.IsActiveAt(tick))
                    {
                        type = ContactType.AttackGuard;
                        return true;
                    }
                    break;
                case ActionType.Move:
                    if (facts.OccupiesTick(tick))
                    {
                        type = ContactType.AttackMove;
                        return true;
                    }
                    break;
            }

            type = ContactType.AttackTarget;
            return false;
        }

        // ------------------------------------------------------------------ 共享目标

        /// <summary>
        /// 共享目标边：两个或更多攻击在同 Tick 指向同一目标单位时，即使彼此没有 Clash，
        /// 也必须进入包含该目标的同一求解组（主方案 0.4.3 第 3 条）。
        ///
        /// 判定口径：目标单位通过了固定目标/区域相交/关系掩码三重资格（即存在候选接触）。
        /// 宁可多分组不少分组 —— 接触最终是否结算由求解器的四格表与标签决定，
        /// 而"同组求解"是构图期的硬要求。
        /// </summary>
        private static void AppendSharedTargetContacts(
            List<InteractionContact> contacts,
            CombatIntent[] nodes,
            UnitOccupancy[] before,
            UnitOccupancy[] afterUnits,
            IFactionRelationResolver factions)
        {
            for (int u = 0; u < afterUnits.Length; u++)
            {
                UnitId targetUnitId = afterUnits[u].UnitId;

                var attackers = new List<int>();
                for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                {
                    CombatIntent intent = nodes[nodeIndex];
                    if (intent.TargetPolicy == TargetPolicy.PrimaryTargetOnly
                        && (!intent.PrimaryTargetUnitId.HasValue || intent.PrimaryTargetUnitId.Value != targetUnitId))
                    {
                        continue;
                    }
                    if (!factions.Allows(intent.AllowedTargetRelations, intent.OwnerUnitId, targetUnitId)) continue;

                    bool covered = before[u].IntersectsCanonical(intent.AreaPoints)
                                   || afterUnits[u].IntersectsCanonical(intent.AreaPoints);
                    if (covered) attackers.Add(nodeIndex);
                }

                for (int i = 0; i < attackers.Count; i++)
                {
                    for (int j = i + 1; j < attackers.Count; j++)
                    {
                        contacts.Add(BuildNodeToNodeContact(
                            ContactType.SharedTarget, nodes, attackers[i], attackers[j], targetUnitId));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Intent ↔ Intent 边

        private static InteractionContact BuildNodeToNodeContact(
            ContactType type, CombatIntent[] nodes, int indexA, int indexB, UnitId targetUnitId)
        {
            CombatIntent a = nodes[indexA];
            CombatIntent b = nodes[indexB];

            // 与 ContactKey.Create 相同的规范规则：按 (UnitId, ActionPlanId) 升序决定 First/Second，
            // 于是 Key.FirstUnitId 恒等于 AttackerUnitId，且 A/B 传参顺序不影响输出。
            bool aFirst = a.OwnerUnitId.Value < b.OwnerUnitId.Value
                          || (a.OwnerUnitId.Value == b.OwnerUnitId.Value
                              && a.ActionPlanId.Value <= b.ActionPlanId.Value);

            CombatIntent first = aFirst ? a : b;
            CombatIntent second = aFirst ? b : a;
            int firstIndex = aFirst ? indexA : indexB;
            int secondIndex = aFirst ? indexB : indexA;

            ContactKey key = ContactKey.Create(
                type,
                a.OwnerUnitId, a.ActionPlanId,
                b.OwnerUnitId, b.ActionPlanId,
                targetUnitId);

            return new InteractionContact(
                key,
                firstIndex,
                secondIndex,
                first.OwnerUnitId,
                first.ActionPlanId,
                second.OwnerUnitId,
                second.ActionPlanId,
                ActionType.Attack,
                targetUnitId,
                coveredBefore: false,
                coveredAfter: false);
        }

        // ------------------------------------------------------------------ 规范化助手

        /// <summary>计划事实按 <c>ActionPlanId</c> 升序规范排序（重复 ID 以不变量错误拒绝）。</summary>
        public static InteractionPlanFacts[] CanonicalPlans(IReadOnlyList<InteractionPlanFacts> plans)
        {
            if (plans == null || plans.Count == 0) return Array.Empty<InteractionPlanFacts>();
            var buffer = new List<InteractionPlanFacts>(plans.Count);
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i] != null) buffer.Add(plans[i]);
            }

            InteractionPlanFacts[] result = buffer.ToArray();
            Array.Sort(result, (a, b) => a.ActionPlanId.Value.CompareTo(b.ActionPlanId.Value));
            for (int i = 1; i < result.Length; i++)
            {
                if (result[i - 1].ActionPlanId == result[i].ActionPlanId)
                {
                    throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID,
                        "duplicate planFacts=" + result[i].ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            return result;
        }

        /// <summary>单位空间表按 <c>UnitId</c> 升序规范排序（重复单位以不变量错误拒绝）。</summary>
        public static UnitOccupancy[] CanonicalUnits(IReadOnlyList<UnitOccupancy> units, string phase)
        {
            if (units == null || units.Count == 0) return Array.Empty<UnitOccupancy>();
            var buffer = new List<UnitOccupancy>(units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                UnitOccupancy occupancy = units[i];
                if (occupancy == null)
                {
                    throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID, "unit=null@" + phase);
                }
                buffer.Add(occupancy);
            }

            UnitOccupancy[] result = buffer.ToArray();
            Array.Sort(result, (a, b) => a.UnitId.Value.CompareTo(b.UnitId.Value));
            for (int i = 1; i < result.Length; i++)
            {
                if (result[i - 1].UnitId == result[i].UnitId)
                {
                    throw new LogicDefinitionException(InteractionCodes.CONTACT_UNIT_DUPLICATE,
                        result[i].UnitId.Value.ToString(CultureInfo.InvariantCulture) + "@" + phase);
                }
            }
            return result;
        }

        /// <summary>两次空间查询必须覆盖同一批单位，否则"新格缺失"会被静默当成"不成立"。</summary>
        private static void ValidateSameUnitSets(UnitOccupancy[] before, UnitOccupancy[] after)
        {
            if (before.Length != after.Length)
            {
                throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID,
                    "unitCount before=" + before.Length.ToString(CultureInfo.InvariantCulture)
                    + " after=" + after.Length.ToString(CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i].UnitId != after[i].UnitId)
                {
                    throw new LogicDefinitionException(InteractionCodes.CONTACT_INPUT_INVALID,
                        "unitSet mismatch at " + i.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>接触按规范键排序并按键去重（保留旧/新覆盖标记的并集）。</summary>
        public static InteractionContact[] CanonicalContacts(List<InteractionContact> contacts)
        {
            if (contacts == null || contacts.Count == 0) return Array.Empty<InteractionContact>();

            InteractionContact[] sorted = contacts.ToArray();
            Array.Sort(sorted, (a, b) => a.Key.CompareTo(b.Key));

            var result = new List<InteractionContact>(sorted.Length);
            for (int i = 0; i < sorted.Length; i++)
            {
                InteractionContact current = sorted[i];
                if (result.Count > 0 && result[result.Count - 1].Key == current.Key)
                {
                    InteractionContact previous = result[result.Count - 1];
                    bool before = previous.CoveredBefore || current.CoveredBefore;
                    bool after = previous.CoveredAfter || current.CoveredAfter;
                    if (before != previous.CoveredBefore || after != previous.CoveredAfter)
                    {
                        result[result.Count - 1] = new InteractionContact(
                            previous.Key, previous.AttackerNodeIndex, previous.CounterpartyNodeIndex,
                            previous.AttackerUnitId, previous.AttackerPlanId,
                            previous.CounterpartyUnitId, previous.CounterpartyPlanId,
                            previous.CounterpartyActionType, previous.TargetUnitId, before, after);
                    }
                    continue;
                }
                result.Add(current);
            }
            return result.ToArray();
        }

        private static int IndexOfPlan(InteractionPlanFacts[] plans, ActionPlanId actionPlanId)
        {
            int low = 0;
            int high = plans.Length - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                long value = plans[mid].ActionPlanId.Value;
                if (value == actionPlanId.Value) return mid;
                if (value < actionPlanId.Value) low = mid + 1;
                else high = mid - 1;
            }
            return -1;
        }
    }
}
