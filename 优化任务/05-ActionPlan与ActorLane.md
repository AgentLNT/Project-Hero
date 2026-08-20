# 05 - 全局 ActionPlan 与 ActorLane

状态：待执行  
前置任务：04  
建议规模：4-6 天

## 执行目标

实现独立于回合窗口的全局动作轨道、每单位唯一的 `ActorLane`、普通计划的滚动排程、执行前启动门禁与原子锁定/启动，以及由已公开 AttackPlan 生成的 `ReactionOpportunity` 生命周期。本任务解决“普通计划何时可编辑、如何原子重排、到期阻塞何时延期或终止、何时冻结并执行、固定时机反应如何插入、何时进入终态”；窗口预算、肾上腺素账户和外部命令入口分别由任务 07/09 接入。

## 必读范围

- 主方案：`0.2.1`、`3.1.2-3.1.5`、`3.2.2`、`3.4.3`
- [00 - 执行规则与依赖](00-执行规则与依赖.md)
- 任务 03 交接的 Step 阶段、ID、事件和快照契约
- 任务 03B 交接的隐藏验证场景、Shadow 比较配置和零写入约束
- 任务 04 交接的状态查询与死亡清理通知
- 任务 02B/03 交接的不可变 FactionModelDefinition、唯一 FactionRelationResolver 与单位 FactionId

## 必须产出

1. 全局 `ActionPlan` 数据与生命周期，至少包含：
   - `ActionPlanId`（强类型 `long` 实例 ID，不与 `ActionSpecId` 混用）
   - 可空 `SubmittedWindowId`：普通计划用于审计，高阶反应为空
   - 可空 `ReactionOpportunityId`、`SourceThreatPlanId` 与固定 `TriggerTick`：Block/Dodge 必填
   - `OwnerUnitId` / `ActionSpecId`
   - 首次 Add 时固定的 `Facing` 与可选 `PrimaryTargetUnitId`，以及只可由直接 Move 操作更新的 `LastRequestedStartTick`；系统自动延期不得覆盖该字段。PrimaryTargetOnly 动作在创建、排程候选和启动门禁时都必须用 Attack 的 `AllowedTargetRelations` 与唯一 FactionRelationResolver 权威校验
   - 当前权威投影的 `StartTick` / `EndTick`，以及 Guard 的 `ActiveStartTick` / `ActiveEndTick`；Locked 后冻结
   - Attack 的 `ResolvedWindupTicks` / `ImpactTick` / `RecoveryTicks`，Move 的 `ResolvedPathEdgeCount` / `ResolvedPathWeightUnits` / `ResolvedBaseStepTicks`，Block/Dodge 的固定 `TriggerTick` / ReactionWindup / Recovery
   - Move/Dodge 使用的可选 `Destination`
   - `BudgetCostTicks` / `ReservedTurnBudgetTicks`
   - `CreatedAtTick`、`LastEditedScheduleRevision`、`AutomaticDeferralCount`、`LockedAtTick`
   - `Editable`、`Locked`、`Running`、自然终态 `Completed` 和带原因的非自然终态 `Terminated`
   - `TerminalTick`
   - 独立的 `ActionTerminationReason`；至少区分 `CancelledByCommand`、`TargetInvalid`、`ActorUnavailableAtStart`、`AutoDeferralLimitExceeded`、`SourceThreatCancelled`、`InterruptedByClash`、`InterruptedByIntercept`、`InterruptedByControl`、`ReservationPreemptedByForcedDisplacement`、`MovementOriginInvalidated`、`OwnerDied` 和 `BattleEnded`
2. 全局 ActionPlan 注册表，按稳定键访问和快照化，不按 WindowId 分桶执行。
3. 每单位一条全局 `ActorLane`：
   - 计划按 `StartTick -> ActionPlanId` 排序。
   - Locked/Running 普通计划与固定反应区间是不可变障碍；Editable 普通计划可由权威 ScheduleEditor 放置、移动、删除、排序并只向右避让。
   - `IsSubmissionLocked`、计划状态、最大队列数量、单批规模、依赖闭包和最大预排 Tick 共同决定是否接受/编辑计划。
4. 唯一 `ActionPlanTerminalCoordinator`（或等价 `ActionLifecycle.EnterTerminal` 入口）：
   - 自然完成、RemoveEditablePlan/显式生命周期取消、启动失败或自动延期越界、目标失效、来源威胁取消、交互/控制打断、强制位移破坏旧起点或 Reservation、Owner 死亡和战斗结束都只能经该入口改变计划终态。
   - 第一次终态请求胜出；重复或冲突请求幂等，不覆盖状态、原因或 TerminalTick，不重复发事件。
   - 使用显式、固定顺序的清理参与者，先阻止计划继续调度，再清除未冻结/未来 Intent、Lane 项、机会绑定和活动索引；任务 06/07 在同一入口接入 MovementSegment/空间 Reservation、TurnBudget 账本与肾上腺素 Reservation，不得另建清理入口。
   - 历史记录保留；每个 Step 返回前可统一检查没有活动对象引用终态计划。
5. 动作生命周期接入 Step：命令前边界、排程编辑、执行前启动门禁、到期计划原子锁定/启动、动作后边界和统一终态清理均有固定阶段。目标 Tick=T 的状态到期和编辑先于 StartTick=T 的门禁。
   - 冻结闭合 `ActionStartGateResult`：`Startable`、`Retryable(StartBlockerReason, RetryAtTick)`、`Terminal(ActionTerminationReason)`、`InvariantViolation(errorCode)`。RetryAtTick 必须有限且严格大于当前 Tick。
   - Startable 的 Timing/Path/Reservation 冻结、任务 07 的 TurnBudget `Reserved -> Spent`、`LockedAtTick`/锁定事件与 `Running` 起始状态必须通过一个提交端口原子完成；失败时不得留下 Locked-but-not-Running、部分资源消费或 Telegraph。
   - Retryable 只能用于带确定结束 Tick 的 Staggered/KnockedDown/Recovering 或任务 06 给出的限时空间阻塞；复用同一 `ScheduleEvaluator` 把到期计划移至 RetryAtTick，并只向右 ripple 后续 Editable 依赖闭包。PrimaryTarget 不满足动作关系掩码属于永久 `TargetInvalid`，不能自动延期或改选目标。
   - 系统延期成功保持 Editable，`AutomaticDeferralCount` 和 ScheduleRevision 各 +1、`LastRequestedStartTick` 不变，并发 `ActionPlanAutoDeferredEvent`；无有限恢复、候选失败、次数/视野超限或永久目标/路径失效使用冻结原因经统一协调器终止，释放锁定前预留且不增加修订。
   - Locked/Running/终态与固定反应区间不可移动。Block/Dodge 永不自动延期；控制失效按 `InterruptedByControl` 终止，目标失效按 `TargetInvalid` 终止。
6. 权威 `ScheduleEditor` / 纯 `ScheduleEvaluator` 与原子事务接口：支持 AddOrdinaryPlan、MoveEditablePlan、RemoveEditablePlan、显式插入锚点、全局 `ScheduleRevision` 和稳定拒绝码；计划、Lane、Timing、依赖预测、预算/Intent/Reservation 扩展项要么全部提交，要么全部回滚。
7. 排程规范化规则：只有直接移动的计划可早于旧 StartTick；重叠只向右 ripple，删除不自动左吸；受影响 Move 链的预测起止位置、路径、边数、路径权重、绝对 Tick 和预算/Reservation 接缝一起重算。预览与提交必须复用同一求值器。
8. ActionPlan、ActorLane 与 ScheduleRevision 的快照和语义事件；每个成功命令排程事务或成功系统自动延期事务只增加一个修订号，失败/预览/锁定/自然推进不增加。`ActionPlanAutoDeferredEvent` 必须包含阻塞原因、旧/新 StartTick、RetryAtTick、次数、稳定 ripple PlanId 列表和提交后修订号。
9. 接入任务 04 的死亡通知：Dead 锁定 Lane 的新提交，并按 ActionPlanId 将该单位全部非终态 Editable/Locked/Running 计划以 `OwnerDied` 原因交给统一协调器；该清理不依赖窗口。
10. 接入任务 03 的战斗结束 Finalizer：按 UnitId 锁定全部 Lane，按 ActionPlanId 将全部非终态计划通过同一协调器转为 `Terminated(BattleEnded)`；从活动索引和 Lane 队列移除，但在只读历史注册表与最终快照中保留计划记录。Finalizer 不得复制一套计划清理逻辑。
11. `ReactionOpportunitySystem`：
   - 来源 AttackPlan 在执行 Tick 锁定、启动并进入 `TelegraphTick`（首版等于 StartTick）后，才按 `SourceAttackPlanId -> DefenderUnitId` 稳定顺序生成机会；攻击仍为 Editable 时不能公开或响应。
   - 只为带 `Reactable` 且满足 `ImpactTick - TelegraphTick >= MinimumReactionLeadTicks` 的攻击创建机会；PrimaryTargetOnly 按通过 `AllowedTargetRelations` 的固定目标、区域攻击按 TelegraphTick 的逻辑威胁区域和同一关系掩码生成防御者候选，不读取 `IsPlayerControlled`、敌我 Controller 类型或任何默认敌方标志。显式允许友军伤害时，友军威胁与反应使用完全相同流程。
   - 每个机会只包含 ActionSet 与来源标签都允许的 Block/Dodge：来源至少有一个可格挡伤害分量或动量才提供 Block，接触为 Dodgeable 才提供 Dodge。选项按 `ActionSpecId` 排序，每项自己的 `ResponseDeadlineTick = ImpactTick - ReactionWindupTicks`；命令不提供 Tick。仅公开满足 `ResponseDeadlineTick >= TelegraphTick + CommandIngressLeadTicks` 的选项，没有相容且可达选项时不创建机会。
   - `ReactionPlanner` 校验机会、选项截止、ActionSet、状态和固定 Lane 区间，创建带 TriggerBinding、直接处于 Locked 的统一 ActionPlan；先冻结资源/目的格事务接缝，实际资源与空间参与者由任务 07/06 接入。
   - 来源攻击在 TriggerTick 前终止时关闭机会，并以 `SourceThreatCancelled` 终止绑定反应；该原因向任务 07 发出“释放未消费预留”通知。其他反应终态不触发该例外。
   - 状态固定为 `Open/Accepted/Triggered/Expired/SourceCancelled/BattleEnded`，只有 Open 接收命令；每个选项在其截止 Tick 命令阶段结束后只发一次过期事件，最后一项过期才关闭机会。第一次离开 Open 只发一次关闭事件，Accepted 后的触发/来源取消由专用触发或计划终态事件记录。
   - 机会状态、选项截止/过期、接受/关闭原因、`NextReactionOpportunityId` 和绑定计划进入事件/规范化快照。

## 核心规则

- 同一单位默认串行，不同单位的计划可以在连续时间轴上重叠。
- 普通计划只有一个 ActionPlan 对象：创建即获得稳定 ID 并进入 Editable，执行门禁通过时在同一原子提交中建立 Locked 边界并立即 Running；禁止另建 DraftPlan/ScheduledAction 后在执行时复制字段，也禁止可观察的 Locked-but-not-Running 普通计划。反应计划复用同一类型但接受后直接 Locked。
- 已完成 Tick N 的快照将锁定线显示在 N+1。Step N+1 先推进状态到期并处理目标 N+1 的冻结排程命令，再按 `UnitId -> StartTick -> ActionPlanId` 评估 StartTick=N+1 的计划；批次冻结后到达的输入只能目标 N+2。
- Guard 使用 `Windup -> Active -> Recovery` 半开区间。Move 首次创建时以 `ReferenceMoveSpeed = 20` 一次性解析 `ResolvedBaseStepTicks`；MoveSpeed 不参与选路。Editable 依赖变化可以按任务 06 的同一 1/2 权重 Pathfinder 重算路径、`ResolvedPathEdgeCount` 与 `ResolvedPathWeightUnits`，每段时长为 `StepWeightUnits * ResolvedBaseStepTicks`；Locked 后逐步推进且不再重算。两者在任务 07 中先预留窗口预算，启动门禁成功时原子消费；系统自动延期期间保持 Reserved。
- 首版攻击在首次进入 Editable 时从当时的单位只读属性解析一次相对时序：`ResolvedWindupTicks = max(1, RoundHalfUp(BaseWindupTicks * ReferenceActionSpeed / Actor.ActionSpeed))`，其中 `ReferenceActionSpeed = 20`；ScheduleEditor 依据当前投影 `StartTick` 重绑 `ImpactTick/EndTick`。
- `RecoveryTicks` 来自 `AttackTimingSpec`，兼容资产缺省值为 30 Tick；`ActionSpeed` 只修正首次量化的前摇，不缩短后摇。后续速度 Buff/Debuff、窗口切换和视觉时间缩放不得重新采样既有计划；显式编辑与系统自动延期只改变绝对 Tick，不重新采样相对时序。
- 攻击阶段固定为 `Windup [StartTick, ImpactTick)`、在 `ImpactTick` 进入 `Recovery` 并恰好产生一次 `AttackIntent`、`Recovery [ImpactTick, EndTick)`。Intent 有效性由计划与 Intent 生命周期决定，不能因 `ImpactTick` 时当前状态已经是 `Recovery` 而被过滤。
- 计划若在 `ImpactTick` 前进入终态，不产生攻击 Intent；已进入 `ImpactTick` 仲裁的 Intent 先参与本 Tick 求解，再接受 Resolution 产生的动作终态。首版不实现多段命中。
- Block/Dodge 只能由有效 ReactionOpportunity 创建，`TriggerTick = SourceAttack.ImpactTick`、`StartTick = TriggerTick - ReactionWindupTicks`、`EndTick = TriggerTick + RecoveryTicks`、`BudgetCostTicks = 0`。若当前处理 Tick 晚于所选项截止或固定区间与 Lane 投影重叠，稳定拒绝；首版不抢占，也不因反应插入而自动重排普通计划。
- Block/Dodge 的 Windup/Recovery 只驱动状态和 Lane，占用期间不提供抵抗或无敌；特殊 Intent 只在 TriggerTick 生成一次。
- `_intentQueue.DrainOrdered(tick)` 已冻结并交给冲突图的本 Tick Intent 继续完成本 Tick 求解；终态协调器只能删除尚未冻结的本 Tick Intent 和未来 Intent，不能追溯修改冲突图输入或已提交结果。
- `ActorLane.RemoveTerminalPlan`、计划内部终态写入和各清理参与者只允许由统一协调器调用；删除或任何终态都不能通过重新计算把未直接编辑的后续计划提前。
- Lane 锁定只阻止新提交，不清除已有计划。
- 单位当前不是 `Idle` 不代表 Lane 拒绝排在未来的计划；当前是 `Idle` 也不代表一定可提交。
- 计划启动和产生 Intent 时不检查 `SubmittedWindowId`；门禁启动提交只把该计划已有的预算预留转为消费，不二次收费。系统延期可按同源求值原子调整预留，但不能转移来源或重开关闭窗口。
- Editable 普通计划经 Remove 或在锁定前因目标/死亡等进入终态时释放尚未消费的 TurnBudget 预留；Locked/Running 后的自然完成、交互打断、强制位移失效、死亡或战斗结束不退款。强制位移终态中，实际被换位且 Move 依赖旧起点时使用 `MovementOriginInvalidated`，否则因最终 footprint 抢占空间承诺时使用 `ReservationPreemptedByForcedDisplacement`；同一计划同时满足两者时前者优先。所有路径都不回卷或复用 ID/Sequence、不撤销已提交伤害/状态/移动，也不自动前移后续计划。Editable 的 Reserved 释放只是未消费预留清理，不重开窗口或转移额度；`SourceThreatCancelled` 继续请求任务 07 按 Cycle 释放反应预留。
- 终态请求按 Step 阶段与其既有稳定键提交；同一计划第一次成功请求确定最终结果，后续死亡或战斗结束请求不得覆盖。`Completed` 可以没有终止原因，`Terminated` 必须把明确原因写入事件和快照。
- Block 的载荷归零与 Dodge 的空间失效都不终止来源攻击；反应计划在 TriggerTick 结算后继续 Recovery，并在固定 EndTick 经统一入口自然完成。
- `ScheduleRevision` 是全局乐观并发版本；Expected 值与 Step 冻结时的 BatchBaseScheduleRevision 比较，不与本批前一事务递增后的实时值比较。互不相交 Lane 可按 CommandSequence 各成功并各 +1；触及本批已改写 Plan/Lane 依赖闭包的后处理事务以 `SCHEDULE_EDIT_CONFLICT_IN_BATCH` 拒绝。显式命令阶段完成后，成功的系统自动延期按稳定到期顺序读取当前排程并各 +1；预览、任意失败、窗口切换、锁定与自然推进不增加修订号，跨 Tick 旧修订以 `STALE_SCHEDULE_REVISION` 拒绝。
- ScheduleEvaluator 从当前 Lane 投影开始，只对直接操作和其向后依赖闭包求值；显式编辑与系统自动延期必须调用同一实现。它把 Locked/Running/反应区间视为障碍，只向右避让。删除只移除目标并重算必要的空间依赖，不自动压缩绝对时间空隙。
- `BattleRules.MaxAutomaticDeferralsPerPlan` 与最大排程视野共同限制重试；未知/无限状态不能返回 Retryable。门禁发现 Lane 投影与当前动作所有权矛盾、RetryAtTick 非法或原子启动提交不一致时必须以稳定不变量错误令 Step 失败，不能终止计划后继续。

## 工作步骤

1. 从任务 02 的各类 `ActionTimingSpec` 建立 ActionPlan 工厂；普通动作首次进入 Editable 时解析属性相关的相对 Tick/成本字段，反应动作创建时解析并直接锁定固定 Tick。
2. 实现全局计划注册表与稳定枚举 API。
3. 实现 ActorLane 新增/编辑检查、Editable 与不可变区间查询、投影整体替换、执行前启动门禁/原子锁定端口，以及只供终态协调器调用的移除接口。
4. 实现统一终态协调器、固定清理参与者顺序和 Step 末无孤儿引用检查；本任务先接入调度器、测试 Intent、Lane 和活动计划索引，保留任务 06 的 MovementSegment/空间 Reservation 与任务 07 的 TurnBudget/肾上腺素账本参与者槽位。
5. 将自然完成、RemoveEditablePlan、目标失效、来源威胁取消、死亡和 BattleEndFinalizer 接入该协调器；为任务 08 冻结 Resolution 终态请求接口，以及 `MovementOriginInvalidated` / `ReservationPreemptedByForcedDisplacement` 的原因优先级、按 ActionPlanId 去重顺序和同一协调器调用边界。排程删除与强制位移都不得旁路清理。
6. 将生命周期接入任务 03 的 Step 扩展点，明确“状态到期 -> 命令编辑 -> 启动门禁 -> 原子锁定/启动”的固定顺序；本任务没有真实交互时可用确定的测试 Intent。
7. 建立 ScheduleEvaluator、ScheduleEditor、Add/Move/Remove 操作、插入锚点、只向右 ripple、ScheduleRevision、系统自动延期与原子事务，保留任务 06/07 接入 RetryAtTick 空间阻塞、空间 Reservation 和预算预留/消费的扩展点。
8. 实现同一纯求值器的只读预览结果；结果列出受影响计划、规范 Tick、移动链预测与失败原因，预览不得分配正式 ID 或修改修订号。
9. 将计划、Lane、ScheduleRevision、生命周期状态、创建/编辑/锁定 Tick、`AutomaticDeferralCount`、TerminalTick 以及投影/冻结时序字段纳入规范化快照。
10. 扩展任务 03B 的 Shadow 检查点，覆盖排程编辑、跨窗口不变、执行前门禁/自动延期/原子启动、Lane 串行、Impact/Recovery 和全部终态；玩法差异必须按具体用例、RulesVersion 与字段登记。
11. 实现机会在来源 Attack 锁定/启动后的 TelegraphTick 打开、逐选项截止、接受、来源取消与战斗结束关闭；任务 09 只能调用公开 ReactionPlanner，不得重建机会或推导 Tick。

## 允许改动

- `Assets/Scripts/Logic/Actions/**`
- `Assets/Scripts/Logic/Timeline/**`
- `Assets/Scripts/Logic/Simulation/**` 中动作阶段接入点
- `Assets/Scripts/Logic/Snapshots/**`
- `Assets/Scripts/Logic/Events/**` 中动作事实事件
- `Assets/Tests/EditMode/**`
- `Assets/Tests/PlayMode/**`，仅限任务 03B 隐藏场景和 Shadow 比较用例
- 旧 `Assets/Scripts/Core/Actions/ActionScheduler.cs` 仅限增加兼容转发或停用新写入路径

## 必需测试

- `SameActorPlansAreSerializedByLane`
- `DifferentActorPlansMayOverlap`
- `OrdinaryPlanUsesSingleIdentityAcrossEditableLockedAndRunning`
- `ReactionPlanIsCreatedLockedWithoutEditablePhase`
- `AttackRelativeTimingResolvesOnceWhenPlanBecomesEditable`
- `ActionSpeedChangesWindupButNotRecovery`
- `MovingEditableAttackRebindsAbsoluteTicksWithoutResamplingSpeed`
- `AttackImpactOccursAtResolvedWindupEnd`
- `AttackBudgetCostEqualsResolvedWindupPlusRecovery`
- `GuardTimingResolvesWindupActiveRecoveryAndBudgetOnce`
- `MoveStepTicksResolveOnceButEditablePathCountCanRecompute`
- `MoveChainRecomputesProjectedStartPathDurationAndBudget`
- `AcceptedAttackTimingDoesNotChangeAfterSpeedMutation`
- `AttackInterruptedBeforeImpactEmitsNoIntent`
- `PrimaryTargetMustSatisfyAllowedRelationAtAddAndStartGate`
- `InvalidPrimaryTargetRelationTerminatesAsTargetInvalidWithoutRetargeting`
- `ImpactTickTransitionsToRecoveryAndStillEmitsIntent`
- `RemovingEditablePlanDoesNotPullLaterPlansForward`
- `ScheduleEditTargetsFutureTickAndRunsBeforeDuePlanStartGate`
- `RequestedStartBeforeCommandTargetTickRejectsWholeBatch`
- `TickNPlusOnePlanRemainsEditableUntilTickNPlusOneCommandPhase`
- `InputAfterBatchFreezeCannotEditPlanLockedThisTick`
- `ControlExpiringAtStartTickAllowsPlanToStartWithoutDelay`
- `TimedControlBlockerAutoDefersDueEditablePlan`
- `NoFiniteRetryTickTerminatesPlanAsActorUnavailableAtStart`
- `AutoDeferralLimitOrHorizonTerminatesWithExplicitReason`
- `AutoDeferralRipplesOnlyEditableDependencyClosureRight`
- `AutoDeferralNeverMovesLockedRunningOrReactionPlan`
- `AutoDeferralPreservesLastRequestedStartTick`
- `AutoDeferralReusesCanonicalScheduleEvaluator`
- `SuccessfulSystemAutoDeferralIncrementsRevisionExactlyOnce`
- `FailedSystemAutoDeferralDoesNotIncrementRevision`
- `FailedAutoDeferralTerminatesEditableAndReleasesPreLockArtifacts`
- `AutomaticDeferralCountAppearsInCanonicalSnapshot`
- `ActionPlanAutoDeferredEventContainsCanonicalRippleAndRevision`
- `StartGateLockAndRunningCommitIsAtomic`
- `ActionStateLaneDivergenceIsInvariantViolation`
- `LockedPlanCannotBeMovedOrRemoved`
- `ReactionPlanCannotBeMovedOrRemoved`
- `ScheduleRippleMovesOnlyEditablePlansRight`
- `OnlyDirectlyMovedPlanMayStartEarlierThanItsOldProjection`
- `LockedAndReactionIntervalsRemainFixedObstacles`
- `SchedulePreviewAndCommitUseSameCanonicalEvaluator`
- `SuccessfulScheduleEditIncrementsRevisionExactlyOnce`
- `DisjointScheduleEditsFromSameFrozenRevisionMayBothSucceed`
- `OverlappingScheduleEditLaterInBatchIsRejectedByCommandOrder`
- `FailedOrPreviewedScheduleEditDoesNotIncrementRevision`
- `StaleScheduleRevisionRejectsWholeBatch`
- `DuplicateOperationOrCyclicInsertionAnchorRejectsWholeBatch`
- `ScheduleEditFailureLeavesNoPartialPlanOrLaneMutation`
- `AddedPlanReceivesIdOnlyAfterTransactionValidation`
- `AllTerminalTransitionsUseSingleLifecycleEntry`
- `TerminalPlanCannotEmitUnfrozenOrFutureIntent`
- `FrozenCurrentTickIntentResolvesBeforeTerminalCleanup`
- `TerminalCleanupIsIdempotentAndFirstReasonWins`
- `TerminalCleanupDoesNotRewindIdsOrSequences`
- `TerminalCleanupEmitsLifecycleEventOnce`
- `ForcedDisplacementPlanInvalidationUsesSingleLifecycleEntry`
- `MovementOriginInvalidatedWinsOverReservationPreemptionForSamePlan`
- `ForcedDisplacementTerminationDoesNotPullLaterPlansForward`
- `SubmissionLockDoesNotDeleteExistingPlans`
- `NonIdleStateDoesNotByItselfRejectFuturePlan`
- `SubmittedWindowIdDoesNotFilterPlanExecution`
- `ReactionPlanUsesOpportunityBindingAndNullSubmittedWindowId`
- `AttackCannotOpenReactionOpportunityBeforeTelegraphTick`
- `EditableAttackCannotOpenReactionOpportunityBeforeLockAndStart`
- `UnreactableAttackDoesNotOpenReactionOpportunity`
- `OpportunityCandidatesDoNotDependOnPlayerOrAiControllerKind`
- `AreaOpportunityCandidatesUseSameFactionRelationMaskAsAttack`
- `ExplicitFriendlyFireCanCreateFriendlyReactionOpportunity`
- `ReactionOptionDeadlineIsDerivedFromImpactAndReactionWindup`
- `ReactionOptionWithoutCommandIngressLeadIsNotPublished`
- `ReactionOptionsRequireCompatibleSourceTags`
- `ReactionOptionDeadlineIsInclusiveAndExpiresAfterCommandPhase`
- `EachReactionOptionEmitsExpiryOnceAndLastOptionClosesOpportunity`
- `ReactionOpportunityLeavesOpenAndEmitsCloseOnlyOnce`
- `ReactionCommandCannotProvideTriggerTick`
- `ReactionFixedIntervalCannotBeShiftedByLaneTail`
- `ReactionLaneOverlapIsRejectedWithoutPreemption`
- `LockedReactionCannotAutoDefer`
- `ControlInvalidatesLockedReactionAsInterruptedByControl`
- `SourceThreatCancelledTerminatesBoundReactionAndRequestsReservationRelease`
- `ReactionOpportunityStateAndNextIdAppearInCanonicalSnapshot`
- `DeathLocksLaneAndTerminatesAllNonTerminalPlansInActionPlanIdOrder`
- `PlanAndLaneAppearInCanonicalSnapshot`
- `PlanTimingAppearsInCanonicalSnapshot`
- `ScheduleRevisionAndPlanEditLockTicksAppearInCanonicalSnapshot`
- `SchedulingFailureRollsBackPlanAndLane`
- `BattleEndTerminatesPlansInActionPlanIdOrderAndLocksAllLanes`
- `BattleEndUsesSamePlanTerminalCoordinator`
- `BattleEndedPlansRemainInHistoryButLeaveActiveIndexes`
- `StepEndHasNoActiveArtifactReferencingTerminalPlan`
- `ActionPlanAndLaneShadowProfileHasNoUnclassifiedDifference`

## 验收标准

- ActionPlan 存在于单一全局注册表，代码中没有“切换窗口时遍历并结算计划”的入口。
- Lane 与 ScheduleEditor 是新增和编辑的唯一动作队列权威来源；`ActionStartGate` 是普通计划锁定/启动的唯一入口，反应计划只经 `ReactionPlanner` 创建为固定 Locked；不存在 DraftPlan/ScheduledAction 到执行对象的复制转换。
- 到期普通计划按稳定顺序得到 Startable/Retryable/Terminal/InvariantViolation 之一；不存在静默跳过或每 Tick 无界轮询，相同输入重复运行结果一致。
- PrimaryTarget、区域 Telegraph 候选和 ReactionOpportunity 都读取同一 FactionRelationResolver 与 ActionSpec AllowedTargetRelations；Controller 变化不改变资格，关系不合法时不自动改选目标。
- Tick N+1 的状态到期与冻结排程编辑先于同 Tick 启动门禁；Startable 的锁定、预算消费端口和 Running 原子，Locked 边界有事件/字段可审计，迟到输入不能改写。成功命令事务和成功系统延期各令修订号恰好 +1，失败零局部写入。
- 有限控制/空间阻塞只延期 Editable 普通计划及其向右依赖闭包，保留 LastRequestedStartTick 和预算来源；次数/视野、无有限恢复、永久目标/路径和候选失败都有冻结终态，Locked/Running/反应不重排。
- 首版攻击只在 `ImpactTick` 产生一次 Intent；速度只改变首次进入 Editable 时解析的前摇，后摇不变，预算成本严格等于 `ResolvedWindupTicks + RecoveryTicks`。重排只重绑绝对 Tick，不重采样速度。`ImpactTick` 的 Recovery 状态不抑制同 Tick Intent，Impact 前终止则不产生 Intent。
- Guard 的相对时序固定；Move 的 BaseStepTicks 样本固定而 Editable 路径边数/路径权重可按依赖链重算，预算和 EndTick 必须使用路径权重而非边数。Block/Dodge 只有来源攻击锁定、启动并公开后才可创建，TriggerTick 严格等于来源 ImpactTick，固定区间既不被 Lane/编辑器平移也不靠状态提供持续免伤。
- 首版保留未来放置、拖动重排、删除、向右避让与移动链重算；删除不自动左吸，UI 预览和权威提交没有两套算法。
- 事务失败后不存在孤立计划、错误 `NextAvailableTick` 或残留测试 Intent；计划进入权威注册表后的任意终态也不会留下活动索引、Lane 项或未冻结/未来 Intent。
- 任务 07 可以在不改变 ActionPlan 生命周期的前提下接入新增授权、Editable 预算预留、系统延期预算调整与启动门禁的原子消费。
- 终态清理幂等，第一次状态/原因/TerminalTick 保持；Editable 普通计划释放未消费预算，Locked 后不退款，反应仅保留当前周期 `SourceThreatCancelled` 释放例外；强制位移两种原因按冻结优先级进入同一入口并完整清理空间从属对象，不回卷 ID/Sequence、不撤销已提交结果或前移后续计划。
- 战斗结束复用普通终态协调器；结束后不存在仍可启动或产生 Intent 的活动计划，最终快照仍能审计每个计划的原始 Tick、状态和终止原因。
- 对应隐藏场景/Shadow 用例已重跑；计划、Lane、攻击时序和终态差异全部可追溯，新模拟仍零 Unity/旧状态/反馈写入。

## 禁止事项

- 不创建“当前回合动作列表”或把 WindowId 用作计划执行查询条件。
- 不让 `UnitStateMachine` 决定 Lane 是否接受未来动作。
- 不创建独立草稿计划类型，不在锁定时复制成第二个 ActionPlan，也不把行动面板开关当作提交/执行阶段。
- 不移动 Locked/Running/终态或反应计划，不自动向左压缩；允许的自动重排仅是显式权威排程事务，或执行前门禁系统自动延期事务，对 Editable 普通计划的确定性向右避让。
- 不把 StartTick 当成创建后永久固定值，不以“本 Tick 跳过、下 Tick 再看”实现临时阻塞，也不在没有有限 RetryAtTick 时保持计划无限 Editable。
- 不用 Controller、玩家标志、名称/Tag 或“默认敌方”替代 FactionRelationResolver；不让非法 PrimaryTarget 在启动时自动换成另一个单位。
- 不把普通计划先标为 Locked 再分别扣预算/尝试 Running；启动提交必须原子，内部矛盾不能降级为普通终态。
- 不把 Editable 预算预留释放误实现为 Locked 后退款；不允许释放旧窗口预留后重开窗口或转移预算。
- 不允许 Resolution、死亡系统、BattleEndFinalizer、命令处理器或 ActorLane 直接改写计划终态或各自删除一部分从属对象。
- 不允许强制位移求解器或 `ApplyBatchRelocation` 直接改写计划终态；任务 08 必须按 ActionPlanId 请求本任务的统一协调器。
- 不通过事件总线、反射发现或不稳定注册顺序执行终态清理参与者。
- 不实现完整 `TurnWindowManager`、肾上腺素账户、并发行动或 AI；但必须提供任务 06/07/09 可原子接入的目的格、资源与命令端口。

## 交接重点

交接必须冻结 `Editable -> Locked -> Running -> 终态` 状态图、N+1 状态到期/编辑先于启动门禁的阶段顺序、`ActionStartGateResult` 四分结果、StartBlockerReason 与终态映射、有限 RetryAtTick、`MaxAutomaticDeferralsPerPlan`、原子锁定/预算消费/启动端口、系统延期的稳定顺序/修订/事件/快照语义、ActionPlan 单一身份、ScheduleEdit 操作/Scope/Revision/拒绝码、向右避让与不左吸算法、预览/命令提交/系统延期同源求值器、依赖闭包上限、计划创建/编辑/自动延期/锁定快照字段（含 Move 的 EdgeCount/PathWeightUnits/ResolvedBaseStepTicks）、PrimaryTarget 与 FactionRelationResolver/AllowedTargetRelations 的 Add/候选/门禁校验、`ActionTerminationReason`（含 `MovementOriginInvalidated` / `ReservationPreemptedByForcedDisplacement`）、两者的优先级与 ActionPlanId 去重顺序、统一终态入口、第一次请求胜出与幂等语义、清理参与者固定顺序、冻结 Intent 边界、Step 末无孤儿引用检查、五类动作解析 Tick/成本字段、反应固定区间插入/不延期规则、按关系掩码生成 ReactionOpportunity 的状态/选项截止/TriggerBinding/关闭原因、本任务新增的 Shadow 检查点与精确批准差异，以及任务 06/07/08/09 如何分别接入空间 RetryAtTick/Reservation、预算预留/调整/原子消费与肾上腺素、Resolution 和 ScheduleEdit/ReactionCommand。任务 08 只能消费 Locked 计划已固定的 Impact/Trigger 时刻并调用统一终态入口，不得自行重新解释速度、动作时长、目标关系或直接清理计划。
