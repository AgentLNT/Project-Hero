# 07 - TurnWindow、预算与提交授权

状态：待执行  
前置任务：05、06  
建议规模：3-4 天

## 执行目标

实现只负责“谁能新增/增费普通动作、还能预留多少预算、何时切换授权”的回合窗口系统，并把普通 ScheduleEdit 与并发行动授权统一接入任务 05 的全局 ActionPlan/ActorLane；同时实现独立的肾上腺素账本供威胁绑定 Block/Dodge 使用。成本中性的 Move/Remove 由计划控制权与 Editable 状态授权，窗口不得成为动作容器或执行边界，高阶反应不得伪装成窗口命令。

## 必读范围

- 主方案：`0.1`、`0.2-0.5`、`3.1.3-3.1.4`、`3.2`、`3.6.2`、第五阶段路线
- [00 - 执行规则与依赖](00-执行规则与依赖.md)
- 任务 03B 交接的隐藏验证场景、Shadow 比较配置和零写入约束
- 任务 02B 的 ControllerBinding / ConcurrentActionDefinition 与任务 03 的命令来源、序号和授权扩展点
- 任务 05 交接的 ActionPlan、ActorLane 与排程事务
- 任务 06 交接的 Reservation 事务接入点

## 必须产出

1. `TurnWindow`：WindowId、拥有者、打开 Tick、总/预留/已消费/可用预算、按 ActionPlanId 的预留明细、打开状态、是否接受提交和关闭原因。窗口关闭后账本仍保留到全部 Editable 预留锁定或释放，但不能重新接受新增消费。
2. `TurnWindowManager`：稳定窗口顺序、打开、请求关闭、Tick 末正式关闭和最早下一 Tick 打开。
3. `ActionAuthority`：窗口拥有者天然可提交 Attack/Guard/Move 等普通动作；额外授权通过独立系统授予，不修改 Lane 或计划。Block/Dodge 不走该授权。
4. 统一 ScheduleEdit 预算事务：
   - 校验 `ExpectedScheduleRevision`；Add 或预算增加时再校验 `ExpectedWindowId`、当前窗口、提交授权与 Available。
   - 创建普通 Editable ActionPlan 时把整数预算从 Available 转入按 Plan 归属的 Reserved；Move 预算严格使用任务 06 的 `ResolvedPathWeightUnits * ResolvedBaseStepTicks + RecoveryTicks`，不能用 EdgeCount 计费。Move 重排本身成本不变，Move 链重算导致成本下降时释放到 SubmittedWindowId 账本，成本上升时只允许从同一个仍为当前且开放的来源窗口追加预留。首版不跨窗口转移或拆分计划预算来源。
   - 任务 05 的 Startable 启动提交必须把该计划 Reserved 原子转为 Spent，并与执行数据冻结、LockedAtTick、锁定事件和 Running 共用一个提交边界；不得二次扣费，也不得先消费后启动失败。
   - Retryable 系统自动延期成功时计划保持 Editable/Reserved；预算差额与 Plan/Lane/Timing/路径/空间 Reservation 同批原子重算。成本增加仍只允许从同一个当前开放来源窗口追加，关闭窗口不因系统延期重开；无法满足时整个候选失败，由任务 05 按冻结终态处理。
   - Remove 或锁定前终态释放 Reserved；若来源窗口已关闭，只更新历史账本，不重开窗口。Locked/Running 终态不修改 Spent。
   - 计划、Lane、Timing、路径、空间 Reservation 和预算变化要么全部提交，要么全部回滚。
5. `ConcurrentActionSystem`：只给主角当前窗口提交 Attack/Guard/Move 等普通动作的授权；激活入口接收由命令网关绑定的 `ControllerId`、受控 `PlayerUnitId` 和 `ExpectedWindowId`，必须验证发行者可控制该单位且期望窗口就是当前开放窗口。窗口关闭即撤销，但已接受计划继续存在；该系统不能授权 Block/Dodge。
6. 并发能力费用只从任务 02B 的权威 `ConcurrentActionDefinition` 读取并原子消费；命令载荷和调用参数不得携带或覆盖费用。
7. 逻辑资源迁移：时间预算归窗口；肾上腺素使用每单位 `AdrenalineLedger`：
   - `AvailableAdrenaline` 不随 Tick 衰减，跨其他单位窗口保留。
   - 接受 Block/Dodge 时按权威 ActionSpec 费用从 Available 原子转入带 `ReservationCycleId` 的计划预留；TriggerTick 删除预留并视为已消费。
   - 来源威胁在 TriggerTick 前取消时，若预留仍属于当前个人周期则返还 Available；若已经跨过拥有者自己的下一窗口打开边界，只删除旧周期预留，不把过期资源带入新周期。
   - 单位自己的窗口打开时先递增个人周期并把 Available 清零；已有合法反应预留继续存在并可触发，窗口关闭不清零。
   - 防御者主动取消、被控制/死亡、目的格失效或反应已经触发均不退款。
   - 任务 08 每 Tick Resolution 全部提交后提供按稳定键聚合的实际造成/承受最终伤害、每单位每 ConflictGroup 至多一次 Clash 成功、每反应计划至多一次 Block/Dodge 成功事实。账本严格按 `AdrenalineRules` 量化各伤害类别一次、加入固定奖励，并在 UnitId 顺序下把 Available 裁到 `MaxAvailablePerCycle`；不得按接触逐次舍入或让失败反应获得成功奖励。
8. 窗口、授权、资源和拒绝结果进入事件与快照；TurnBudget 事件必须区分 `Reserved/ReservationAdjusted/ConsumedAtLock/ReleasedBeforeLock/BattleEndCleared` 并记录 WindowId、PlanId 与 Available/Reserved/Spent 前后值。`ConsumedAtLock` 只能在任务 05 的原子启动提交成功时出现；`ReservationAdjusted` 还要区分显式 ScheduleEdit 与 SystemAutoDeferral 来源。肾上腺素事件必须区分 `Accrued/Reserved/Consumed/Refunded/CycleReset/BattleEndCleared`，记录 Cycle、Available 前后值、可选计划 ID 与预留量。
9. 战斗结束 Finalizer 接入：以 `BattleEnded` 原因关闭当前窗口、撤销并发授权并清空未来窗口排程；Editable 预留随统一计划清理归零但不得形成可继续使用的退款额度，Locked/Running 不退款；窗口系统不得自行遍历或终止 ActionPlan。
10. 将预算账本作为任务 05 统一终态协调器的显式、状态感知参与者：Editable 普通计划终态释放其 Reserved；Locked/Running/Completed 的 Spent 保持不变。只有尚未提交成功的 ScheduleEdit 可以调用事务回滚接口，终态协调器不得伪造回滚或重开窗口。
11. 扩展任务 03B 的 Shadow 检查点，覆盖窗口打开/关闭、整数预算、并发授权、肾上腺素清零和跨窗口计划不变性；旧资源模型与新规则差异必须逐用例登记。
12. 将窗口预算与肾上腺素作为两种独立资源参与者接入任务 05：普通计划使用 Editable Reserved -> Startable 原子提交中的 Locked Spent，Retryable 自动延期仍为 Reserved；反应使用 Available -> Reaction Reservation -> Trigger 消费。拒绝或事务失败只回滚本次候选变化。冻结一个只接受任务 08 规范 `AdrenalineAccrualFacts` 的 Tick 末批量入口，其他系统不得逐接触直接加 Available。

## 原子性与关闭语义

- 命令校验失败：不预留/消费预算，不创建或改变计划，不改变 Lane 或 ScheduleRevision。
- 并发激活按“来源控制权 -> ExpectedWindowId -> 当前窗口状态 -> 能力状态 -> 权威费用消费”执行；任一步失败都不得扣除局外资源或留下授权。
- 计划创建/编辑、Lane、全局注册、Timing、路径、Intent、预算或空间 Reservation 任一步骤失败：回滚本次 ScheduleEdit 的所有候选写入。
- 系统自动延期的预算/排程/空间候选任一步失败：回滚该候选的全部局部写入；随后若任务 05 令仍为 Editable 的到期计划进入终态，预算参与者只执行一次 ReleasedBeforeLock。自动延期失败本身不是 Locked 后退款。
- Startable 原子提交任一步发生内部错误：不得提交 `ConsumedAtLock`、Locked 或 Running 的任何子集；以不变量错误令 Step 失败，而不是把计划终止后继续。
- 同一冻结批次中的后处理命令若与已提交 Reservation 冲突，只拒绝并回滚自身事务；不得撤销先处理命令的预算、Lane、计划或 Reservation。
- 普通 ActionPlan 在 Editable 阶段终止会释放尚未消费的窗口预算预留；锁定时 Reserved 转为 Spent，此后无论自然完成或以何种原因终止均不退。反应计划只有 `SourceThreatCancelled` 且尚未触发时按预留周期规则释放；其余终态不退肾上腺素。Block 的载荷归零与 Dodge 的空间失效都不终止来源攻击计划。
- 请求关闭立即设置 `IsAcceptingSubmissions = false`，因此本 Tick 后续命令稳定拒绝；正式关闭在 Tick 末完成。
- 新窗口最早在下一 Tick 打开，且只能在该 Tick 的动作前边界、状态/持续效果、死亡和胜负阶段完成并确认战斗未结束后打开。
- 关闭时不查询、遍历、移动、锁定、取消或结算 ActionPlan、Intent、Lane、MovementSegment、Reservation；只把窗口设为不再接受新增/预算增加。既有预算预留账本继续可审计。
- 战斗结束后不再打开新窗口；待打开窗口的拥有者若在打开阶段前死亡，按稳定窗口顺序跳过，且不得产生该单位的窗口打开事件或预算。
- 战斗结束关闭与普通窗口切换不同：它不启动新个人周期；Finalizer 经统一计划终态入口清除 Editable 窗口预算预留，再关闭全部机会并清除 Adrenaline Available/Reservation，不产生可继续使用的退款额度，也不排定下一窗口。`BattleEndedEvent` 在窗口和其他活动系统清理完成后才发射。

## 稳定顺序

窗口顺序按主方案使用稳定键：`PlayerFirst -> ActionSpeed 降序 -> UnitId`。不得依赖注册顺序或 Dictionary 枚举。窗口预算耗尽、主动请求关闭和拥有者死亡都必须映射为明确关闭原因。

## 允许改动

- `Assets/Scripts/Logic/Turns/**`
- `Assets/Scripts/Logic/Resources/**`
- `Assets/Scripts/Logic/Commands/**` 中动作接受服务与拒绝结果
- `Assets/Scripts/Logic/Actions/**` 中排程事务接入
- `Assets/Scripts/Logic/Simulation/**` 中窗口阶段接入
- `Assets/Scripts/Logic/Events/**`、`Snapshots/**`
- `Assets/Tests/EditMode/**`
- `Assets/Tests/PlayMode/**`，仅限任务 03B 隐藏场景和 Shadow 比较用例
- 旧资源字段只允许增加兼容映射，不在本任务批量删除场景序列化字段

## 必需测试

- `EditablePlanReservesBudgetWithoutSpendingIt`
- `StartableCommitConvertsReservedBudgetToSpentExactlyOnce`
- `StartableCommitAtomicallyIncludesSpentLockedAndRunning`
- `FailedStartableCommitLeavesBudgetReservedAndRaisesInvariant`
- `SystemAutoDeferralKeepsBudgetReserved`
- `SystemAutoDeferralAdjustsBudgetWithScheduleAndReservationAtomically`
- `AutoDeferralCannotIncreaseCostFromClosedSourceWindow`
- `FailedAutoDeferralTerminalReleasesReservationExactlyOnce`
- `SystemAutoDeferralDoesNotReopenClosedWindow`
- `RejectedCommandDoesNotReserveOrSpendBudget`
- `ScheduleEditFailureRollsBackBudgetLanePlanAndReservation`
- `RemovingEditablePlanReleasesUnspentBudgetReservation`
- `PreLockTerminalReleasesUnspentBudgetReservation`
- `LockedOrRunningTerminalDoesNotRefundSpentBudget`
- `ClosedWindowLedgerPersistsUntilEditableReservationsSettle`
- `ReleasingClosedWindowReservationDoesNotReopenSubmissions`
- `EditableMoveCostDeltaAdjustsReservationAtomically`
- `MoveBudgetUsesPathWeightUnitsInsteadOfEdgeCount`
- `CostIncreaseRequiresValidOpenWindowAndAuthority`
- `CostIncreaseCannotTransferPlanBudgetToAnotherWindow`
- `BudgetNeverBecomesNegative`
- `CloseRequestRejectsLaterCommandsInSameTick`
- `NextWindowOpensNoEarlierThanNextTick`
- `ScheduledWindowOwnerDiesBeforeOpenDoesNotOpenWindow`
- `WindowSwitchDoesNotMutatePlanLaneIntentOrReservation`
- `PlanFromWindowAStartsAndEndsDuringLaterWindows`
- `ConcurrentAuthorityEndsWithWindowButScheduledPlanSurvives`
- `ConcurrentActivationRejectsStaleWindow`
- `IssuerCannotActivateForUncontrolledUnit`
- `ConcurrentActivationRejectsControlledNonHeroUnit`
- `ConcurrentCostComesFromAbilityDefinition`
- `RejectedConcurrentActivationDoesNotSpendMetaResource`
- `ConcurrentAuthorityCannotSubmitBlockOrDodge`
- `ReactionAcceptanceDoesNotRequireWindowOrConcurrentAuthority`
- `AdrenalinePersistsAcrossOtherUnitsWindowsAndOwnWindowClose`
- `OwnersNextWindowOpenClearsAvailableAdrenalineBeforeCommands`
- `ReactionAcceptanceReservesAdrenalineExactlyOnce`
- `ReactionTriggerConsumesReservationExactlyOnce`
- `SourceThreatCancellationRefundsOnlyCurrentCycleReservation`
- `OldCycleReservationSurvivesToTriggerButCannotRefundIntoNewCycle`
- `InterruptedOrInvalidReactionDoesNotRefundAdrenaline`
- `SuccessfulDodgeRewardIsLowerThanCostAndCannotSelfLoop`
- `SuccessfulBlockRewardIsLowerThanCostAndCannotSelfLoop`
- `AdrenalineDamageGainAggregatesOncePerUnitPerTick`
- `AdrenalineOutcomeRewardOccursOncePerPlanOrConflictGroup`
- `AvailableAdrenalineIsCappedWithoutChangingReservations`
- `AllLockedPlanTerminalReasonsPreserveSpentBudget`
- `RepeatedEditableTerminalCleanupCannotReleaseReservationTwice`
- `RepeatedLockedTerminalCleanupCannotInvokeBudgetRollback`
- `BattleEndClosesWindowRevokesAuthorityAndClearsFutureScheduleWithoutRefund`
- `TurnWindowBudgetAndAuthorityShadowProfileHasNoUnclassifiedDifference`

## 验收标准

- TurnWindow 内不存在 ActionPlan、Intent 或 Reservation 集合。
- 切换窗口前后，已有计划的 ID、Tick、状态、Lane、ScheduleRevision、预算与空间预留完全不变。
- 普通动作与并发授权动作走同一个 ScheduleEdit 入口、预算实现和拒绝码集合。
- 高阶反应只通过 ReactionOpportunity/ReactionPlanner，并使用肾上腺素事务；它不校验 ExpectedWindowId、不消费 TurnBudget，也不要求 ConcurrentActionSystem。
- 并发激活不能跨 Controller、不能用旧 `ExpectedWindowId` 授权当前窗口，且改变请求载荷不能改变权威能力费用。
- 时间预算全程为整数 Tick；Logic 中不出现 float 预算递减。
- Editable 阶段严格使用 Reserved；只有 Startable 原子提交可与 Locked/Running 一起转为 Spent，系统自动延期继续 Reserved。事务回滚只服务未提交成功的 ScheduleEdit/系统候选/启动候选；统一终态协调器只能按计划状态释放 Reserved 或保持 Spent，重复清理不改变任一值。
- 旧体力/专注玩法不再被新的 Logic 路径读取；字段若保留仅用于兼容。
- Available、每条反应预留、ReservationCycleId 和个人周期号进入规范化快照；伤害/结果奖励按冻结公式聚合且 Available 不超过周期上限，不存在逐接触舍入差异、重复奖励、重复消费、跨周期退款套利或窗口关闭误清零。
- 战斗结束后不存在打开或待打开窗口，也不存在仍有效的并发提交授权。
- 关闭窗口的账本不会因 Editable 计划删除而重开或把释放额转移到其他窗口；Reserved + Spent + Available 始终等于 Total，直到战斗结束清理。
- 对应隐藏场景/Shadow 用例已重跑；窗口、预算、授权和资源差异全部按用例与字段分类，新模拟仍零 Unity/旧状态/反馈写入。

## 禁止事项

- 不等待旧动作完成后才打开新窗口。
- 不在窗口关闭时结算 PendingAction 或释放动作相关预留。
- 不把并发行动实现成打断、替换窗口拥有者或窃取预算。
- 不让 ConcurrentActionSystem 直接调用调度器或单独实现一套动作接受流程。
- 不让 ConcurrentActionSystem 授权 Block/Dodge，不让 ReactionPlanner 借用当前窗口预算或 ExpectedWindowId。
- 不把 `ControllerId` 当作命令载荷中的自报权限证明，也不接受调用方提供的并发能力费用。
- 不把 Editable TurnBudget 预留释放实现成 Locked 后退款，也不让旧窗口释放额重开/转移；肾上腺素仍只实现“来源威胁触发前取消”的周期内返还。
- 不在启动门禁前预先消费预算，不允许普通计划处于 Locked 但预算仍 Reserved、或预算已 Spent 但未 Running。
- 不从死亡、Clash、删除或 BattleEnd 路径调用 ScheduleEdit 事务回滚接口；这些路径只能经统一终态协调器的预算参与者按计划状态结算。

## 交接重点

交接必须列出窗口状态流、关闭原因、普通动作 `ControllerId -> UnitId` 授权矩阵、TurnBudget `Available -> Reserved -> Spent` 状态机、Startable 的 Spent/Locked/Running 原子提交、Retryable 系统延期的 Reserved/差额/失败释放矩阵、Editable 释放/锁定后不退矩阵、关闭窗口账本保留与不可重开规则、ScheduleEdit 与 SystemAutoDeferral 成本差额事务边界、并发能力权威费用来源、肾上腺素 Available/Reservation/Cycle 状态机、来源取消释放矩阵、窗口打开清零时机、全部稳定拒绝码、本任务新增的 Shadow 检查点与精确批准差异，以及任务 09 的 ScheduleEdit 新增/增费必须验证 ExpectedWindowId、Move/Remove 必须验证控制权与 Editable/Revision、ReactionCommand 必须验证 ReactionOpportunityId 且不得要求窗口/并发授权的边界。
