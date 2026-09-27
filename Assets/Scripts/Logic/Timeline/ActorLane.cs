using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>
    /// 排程规模上限（任务包「必须产出」3 的最后一段）。它们与 <c>IsSubmissionLocked</c>、
    /// 计划状态一起决定"是否接受/编辑计划"，<strong>不</strong>决定计划能否执行。
    ///
    /// 冻结首版取值（版本化常量；进入诊断与快照摘要，不另行进入 <c>BattleDefinitionHash</c>——
    /// 后者已在任务 02B 冻结，本任务不重算定义哈希）：
    /// <list type="bullet">
    /// <item><see cref="MaxQueuedPlansPerLane"/> = 16：单条 Lane 的最大排队计划数。</item>
    /// <item><see cref="MaxBatchOperations"/> = 64：单批排程编辑的最大操作数。</item>
    /// <item><see cref="MaxDependencyClosurePlans"/> = 256：一次求值允许触及的最大依赖闭包规模。</item>
    /// <item><see cref="MaxPreScheduleHorizonTicks"/>：最大预排 Tick 视野，取任务 02B 冻结的
    /// <c>BattleRules.MaxScheduleHorizonTicks</c>（600 秒 @ 60 Tick/s）。</item>
    /// </list>
    /// </summary>
    public sealed record ScheduleLimits(
        int MaxQueuedPlansPerLane,
        int MaxBatchOperations,
        int MaxDependencyClosurePlans,
        long MaxPreScheduleHorizonTicks)
    {
        /// <summary>首版冻结上限。</summary>
        public static readonly ScheduleLimits FrozenV1 = new ScheduleLimits(
            MaxQueuedPlansPerLane: 16,
            MaxBatchOperations: 64,
            MaxDependencyClosurePlans: 256,
            MaxPreScheduleHorizonTicks: BattleRules.FrozenMaxScheduleHorizonTicks);
    }

    /// <summary>
    /// 每单位一条的全局 <c>ActorLane</c>（任务包「必须产出」3）。
    ///
    /// 契约（<strong>Lane 是新增与编辑的唯一动作队列权威来源</strong>）：
    /// <list type="bullet">
    /// <item>计划按 <c>StartTick -&gt; ActionPlanId</c> 排序（<see cref="Plans"/> 即该规范顺序）。</item>
    /// <item>Locked/Running 普通计划与固定反应区间是<strong>不可变障碍</strong>
    /// （<see cref="Obstacles"/>）；Editable 普通计划由权威 ScheduleEditor 放置、移动、删除、
    /// 排序并只向右避让。</item>
    /// <item><see cref="IsSubmissionLocked"/> 只阻止<strong>新提交</strong>，
    /// <strong>不清除</strong>已有计划（死亡锁 Lane 与战斗结束锁全部 Lane 都走这一条）。</item>
    /// <item>同单位默认<strong>串行</strong>：Lane 内任一时刻最多一个计划占据区间
    /// （<see cref="ValidateNonOverlapping"/> 是运行时不变量检查）。不同单位的 Lane 相互独立，
    /// 因此不同单位的计划可以在连续时间轴上重叠。</item>
    /// <item>Lane 自身<strong>不</strong>判断"当前状态是否 Idle"，也不读
    /// <c>UnitStateMachine</c>：单位当前不是 Idle 不代表 Lane 拒绝排在未来的计划，
    /// 当前是 Idle 也不代表一定可提交（那是 ScheduleEditor 与启动门禁的职责）。</item>
    /// </list>
    ///
    /// 写入口（<c>internal</c>）：只有 <c>ActionScheduleAuthority</c>、
    /// <c>ScheduleEditor</c> 事务与统一终态协调器在<strong>同一程序集内</strong>调用。
    /// </summary>
    public sealed class ActorLane
    {
        /// <summary>按 <c>(StartTick, ActionPlanId)</c> 升序的规范顺序。</summary>
        private readonly List<ActionPlan> _plans = new List<ActionPlan>();

        public ActorLane(UnitId unitId)
        {
            if (!unitId.IsValid)
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_LANE_UNKNOWN_UNIT, "unitId=0");
            UnitId = unitId;
        }

        public UnitId UnitId { get; }

        /// <summary>
        /// 是否锁定<strong>新提交</strong>（死亡锁 Lane / 战斗结束锁全部 Lane）。
        /// 它<strong>不</strong>使已有计划失效，也不删除任何 Lane 项。
        /// </summary>
        public bool IsSubmissionLocked { get; private set; }

        /// <summary>锁定原因（稳定码；未锁定时为 null）。</summary>
        public string SubmissionLockReason { get; private set; }

        public int Count => _plans.Count;

        /// <summary>规范顺序的只读视图（按 <c>StartTick -&gt; ActionPlanId</c>）。</summary>
        public IReadOnlyList<ActionPlan> Plans => _plans;

        /// <summary>
        /// Lane 尾（本 Lane 全部计划的最大 <c>EndTick</c>；空 Lane 为 0）。
        /// 它是<strong>只读诊断与放置提示</strong>，不是"下一个可提交 Tick"的权威判据。
        /// </summary>
        public long LaneTailTick
        {
            get
            {
                long tail = 0L;
                for (int i = 0; i < _plans.Count; i++)
                {
                    if (_plans[i].EndTick > tail) tail = _plans[i].EndTick;
                }
                return tail;
            }
        }

        /// <summary>不可变障碍：Locked/Running 普通计划与全部固定反应区间。</summary>
        public List<ActionPlan> CollectObstacles()
        {
            var obstacles = new List<ActionPlan>();
            for (int i = 0; i < _plans.Count; i++)
            {
                if (_plans[i].IsLaneObstacle) obstacles.Add(_plans[i]);
            }
            return obstacles;
        }

        /// <summary>当前处于 Editable 的普通计划（规范顺序）。</summary>
        public List<ActionPlan> CollectEditablePlans()
        {
            var editable = new List<ActionPlan>();
            for (int i = 0; i < _plans.Count; i++)
            {
                if (_plans[i].IsEditable) editable.Add(_plans[i]);
            }
            return editable;
        }

        public bool Contains(ActionPlanId actionPlanId)
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                if (_plans[i].ActionPlanId == actionPlanId) return true;
            }
            return false;
        }

        /// <summary>按 ID 取本 Lane 内的计划；不在本 Lane 返回 null。</summary>
        public ActionPlan Find(ActionPlanId actionPlanId)
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                if (_plans[i].ActionPlanId == actionPlanId) return _plans[i];
            }
            return null;
        }

        /// <summary>锁定新提交（幂等；原因以第一次为准，不被后续覆盖）。</summary>
        internal void LockSubmissions(string reason)
        {
            if (IsSubmissionLocked) return;
            IsSubmissionLocked = true;
            SubmissionLockReason = string.IsNullOrEmpty(reason) ? "SCHEDULE_LANE_LOCKED" : reason;
        }

        /// <summary>解除新提交锁定（只供测试夹具与显式复位使用；正常运行时不调用）。</summary>
        internal void UnlockSubmissions()
        {
            IsSubmissionLocked = false;
            SubmissionLockReason = null;
        }

        /// <summary>
        /// 按 <c>(StartTick, ActionPlanId)</c> 规范插入。调用方（事务）已保证不重叠；
        /// 本方法只维护顺序，不做避让（避让由 <c>ScheduleEvaluator</c> 统一完成）。
        /// </summary>
        internal void InsertCanonical(ActionPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (plan.OwnerUnitId != UnitId)
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_LANE_OWNER_MISMATCH,
                    "lane=" + UnitId.Value.ToString(CultureInfo.InvariantCulture) +
                    " plan=" + plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
            if (Contains(plan.ActionPlanId))
                throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_PLAN_ALREADY_IN_LANE,
                    plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));

            int index = 0;
            while (index < _plans.Count && Compare(plan, _plans[index]) > 0) index++;
            _plans.Insert(index, plan);
        }

        /// <summary>按 ID 移除并返回是否移除。</summary>
        internal bool RemovePlan(ActionPlanId actionPlanId)
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                if (_plans[i].ActionPlanId != actionPlanId) continue;
                _plans.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 整体替换投影（事务回滚与规范化重排都走这一条）。
        /// 入参会被复制并按规范键排序，因此调用方持有的列表不会被别名改写。
        /// </summary>
        internal void ReplaceProjection(IReadOnlyList<ActionPlan> ordered)
        {
            _plans.Clear();
            if (ordered == null) return;

            for (int i = 0; i < ordered.Count; i++)
            {
                ActionPlan plan = ordered[i];
                if (plan == null) continue;
                if (plan.OwnerUnitId != UnitId)
                    throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_LANE_OWNER_MISMATCH,
                        plan.ActionPlanId.Value.ToString(CultureInfo.InvariantCulture));
                _plans.Add(plan);
            }
            _plans.Sort(Compare);
        }

        /// <summary>规范比较：<c>StartTick -&gt; ActionPlanId</c>。</summary>
        public static int Compare(ActionPlan left, ActionPlan right)
        {
            int byStart = left.StartTick.CompareTo(right.StartTick);
            if (byStart != 0) return byStart;
            return left.ActionPlanId.Value.CompareTo(right.ActionPlanId.Value);
        }

        /// <summary>
        /// 查找 <c>[startTick, startTick + lengthTicks)</c> 与 Lane 内既有计划（可选忽略一个）
        /// 的第一个重叠。半开区间：相邻不算重叠。
        ///
        /// 它对 Editable 与障碍一律视为占位：因为 Editable 计划在被移动之前就已经占据了
        /// 它的<strong>当前</strong>投影，避让必须基于当前投影而不是"先删后放"。
        /// </summary>
        public bool TryFindOverlap(long startTick, long lengthTicks, ActionPlanId? ignore,
            out ActionPlan conflict)
        {
            long endTick = startTick + (lengthTicks < 0L ? 0L : lengthTicks);
            for (int i = 0; i < _plans.Count; i++)
            {
                ActionPlan plan = _plans[i];
                if (ignore.HasValue && plan.ActionPlanId == ignore.Value) continue;
                if (plan.StartTick < endTick && startTick < plan.EndTick)
                {
                    conflict = plan;
                    return true;
                }
            }
            conflict = null;
            return false;
        }

        /// <summary>
        /// 求"从 <paramref name="desiredStartTick"/> 起、长度为
        /// <paramref name="lengthTicks"/> 的最早无重叠起点"。
        ///
        /// 只向<strong>右</strong>避让：绝不返回小于 <paramref name="desiredStartTick"/> 的值。
        /// 忽略项（通常是正在被直接移动的那个计划）不参与占位。
        /// </summary>
        public long FindEarliestFreeStartTick(long desiredStartTick, long lengthTicks, ActionPlanId? ignore)
        {
            long length = lengthTicks < 0L ? 0L : lengthTicks;
            long candidate = desiredStartTick < 0L ? 0L : desiredStartTick;

            // 反复推进直到稳定：每次找到一个重叠就把候选推到该重叠项的右端之后。
            // 最坏情况是 O(n^2)，而单条 Lane 的计划数受 MaxQueuedPlansPerLane 限制，代价可控。
            bool moved = true;
            int guard = 0;
            while (moved)
            {
                moved = false;
                if (++guard > _plans.Count + 2)
                {
                    // 迭代次数上界与计划数同量级：超出说明占位集合自相矛盾（装配错误）。
                    throw new LogicDefinitionException(ScheduleCodes.SCHEDULE_LANE_OVERLAP,
                        "lane=" + UnitId.Value.ToString(CultureInfo.InvariantCulture));
                }

                for (int i = 0; i < _plans.Count; i++)
                {
                    ActionPlan plan = _plans[i];
                    if (ignore.HasValue && plan.ActionPlanId == ignore.Value) continue;
                    if (plan.StartTick >= candidate + length) continue;
                    if (plan.EndTick <= candidate) continue;

                    candidate = plan.EndTick;
                    moved = true;
                }
            }

            return candidate;
        }

        /// <summary>
        /// 运行时不变量检查：Lane 内任意两个计划的区间不重叠，且不存在终态计划残留。
        /// 返回失败描述（null = 通过）。
        /// </summary>
        public string ValidateNonOverlapping()
        {
            var sorted = new List<ActionPlan>(_plans);
            sorted.Sort(Compare);
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i - 1].EndTick > sorted[i].StartTick)
                {
                    return "overlap lane=" + UnitId.Value.ToString(CultureInfo.InvariantCulture) +
                           " a=" + sorted[i - 1].ActionPlanId.Value.ToString(CultureInfo.InvariantCulture) +
                           " b=" + sorted[i].ActionPlanId.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].IsTerminal)
                {
                    return "terminal-in-lane lane=" + UnitId.Value.ToString(CultureInfo.InvariantCulture) +
                           " plan=" + sorted[i].ActionPlanId.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
            return null;
        }

        /// <summary>Lane 快照（<c>PendingPlanCount</c> 只计非终态计划）。</summary>
        public ActorLaneSnapshot BuildSnapshot()
        {
            int pending = 0;
            for (int i = 0; i < _plans.Count; i++)
            {
                if (!_plans[i].IsTerminal) pending++;
            }
            return new ActorLaneSnapshot(UnitId.Value, pending, IsSubmissionLocked);
        }
    }
}
