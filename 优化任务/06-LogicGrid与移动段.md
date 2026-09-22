# 06 - LogicGrid、移动段与 Reservation

状态：待执行  
前置任务：03、05  
建议规模：3-5 天

## 执行目标

把占位、区域查询、寻路输入、移动时间段和路径预留迁入纯 C# 逻辑层。命中与移动交互只读取离散逻辑数据，不再依赖 Transform 或视觉插值位置；同时为任务 05 的执行前门禁提供可证明有限的空间阻塞 `RetryAtTick`，而不是让动作每 Tick 盲重试。

## 必读范围

- 主方案：`3.3.3`、`3.4.3` 的命令前边界、`3.8`
- [00 - 执行规则与依赖](00-执行规则与依赖.md)
- 任务 03B 交接的隐藏验证场景、Shadow 比较配置和零写入约束
- 任务 05 的 ActionPlan 事务和动作边界接口

## 必须产出

1. 纯数据 `GridPoint`、`TrianglePoint`、方向和单位体积定义；不引用 Unity 数学或场景对象。单位体积直接消费任务 02B 已生成的 `VolumeSpec.Directions[0..11]` 规范整数表，不在注册、转向或查询时旋转点集。
2. `LogicGrid`：单位注册/注销、占位、稳定区域查询和预留查询；合法 `GridPoint` 满足 doubled-coordinate 的 `X + Y` 为偶数；提供 `GetNeighborsOrdered(GridPoint)`，严格按 `GridDirection` 冻结底层值 `0 -> 11` 枚举合法邻居，跳过非法候选时不得改变其余方向的相对顺序。占位和区域查询只做方向索引、整数平移与集合查询。
3. `MovementSegment(ActionPlanId, StepIndex, UnitId, From, To, Direction, StepWeightUnits, StartTick, EndTick)`；使用 `(ActionPlanId, StepIndex)` 定位，不创建独立 SegmentId。`StepWeightUnits` 只能由权威方向表导出，不能由调用方填写。
4. 首版离散提交语义：
   - `StartTick <= tick < EndTick` 时单位仍占用 `From`，`To` 保持 Reservation。
   - 到 `EndTick` 的命令前边界原子提交到 `To` 并释放对应 Reservation。
5. 多步路径每一步独立 MovementSegment；预留冲突只服从任务 03 网关生成的 `CommandSequence`。网关先按派生 `SourcePriority -> ControllerId Ordinal -> ProducerOrdinal` 规范化并拒绝重复/回退键；先成功提交的有效命令取得 Reservation，后续冲突命令稳定拒绝，不能抢占此前已提交的计划。
6. 预留与 ActionPlan ScheduleEdit 使用同一事务；Editable 计划持有可由同一原子批次整体替换的权威未来 Reservation，仍会阻挡其他计划但不能被外部抢占。失败不留下任何局部状态；任务 07 接入窗口预算后，再验证预算/空间/路径一起回滚。
   - 同一纯空间求值接口必须同时服务 ScheduleEdit 与任务 05 的系统自动延期候选；后者只能整体替换到期计划及其 Editable 依赖闭包的未来 MovementSegment/Reservation。
   - 启动门禁查询必须区分 `Free`、`RetryableTimedBlock(ReleaseTick)` 与 `TerminalOrUnknownBlock`。只有来自权威占位/Reservation 区间且 ReleaseTick 有限、严格晚于当前 Tick 的阻塞可返回 Retryable；静态非法格、无结束边界的占位或内部所有权矛盾不能伪造成“下一 Tick再试”。
7. LogicGrid、MovementSegment 和 Reservation 的规范化快照。
8. 接入任务 05 的统一终态协调器，提供固定顺序的 MovementSegment/Reservation 清理参与者：任意终态原因都按 `(ActionPlanId, StepIndex)` 移除该计划的活动及未来移动段，按稳定空间键释放该计划全部 Reservation，并保留已完成段与最终逻辑位置的只读审计记录。
9. 战斗结束不得另建移动清理路径；任务 03 Finalizer 按 ActionPlanId 调用同一终态协调器后，只验证不存在活动 MovementSegment 或 Reservation。
10. 扩展任务 03B 的 Shadow 检查点，覆盖占位、移动提交、路径 Reservation、冲突回滚和移动终态；比较逻辑格与规范化段，不比较 Transform 插值位置。
11. 普通 Move 与高阶 Dodge 使用不同空间提交协议：
   - Move 首次进入 Editable 时验证确定路径，把当时 MoveSpeed 量化为固定 `ResolvedBaseStepTicks`；MoveSpeed 不进入 Pathfinder。若自身 Destination、Lane 顺序或更早 Move 改变预测起点，ScheduleEvaluator 必须重算受影响 Editable 链的路径、`ResolvedPathEdgeCount`、`ResolvedPathWeightUnits`、绝对 Tick、MovementSegment 和 Reservation；每段持续时间严格为 `StepWeightUnits * ResolvedBaseStepTicks`。Locked 后只读取冻结结果，不得按新路径输入、属性或视觉速度重算。
   - Dodge 不生成持续移动段，也不逐步插值逻辑位置。反应接受事务按 `ReactionOpportunityId` / `ActionPlanId` 为 TriggerTick 预留合法目的格；在任务 08 构图前的固定阶段原子把占位从 From 提交到 Destination，再释放该目的格预留。
   - Dodge 目的格必须满足 `DodgePayloadSpec` 的距离/Pattern、边界、占位和时间区间冲突规则。来源威胁触发前取消时释放预留；防御者自身原因导致触发失败时以 `TargetInvalid` 终止，由任务 07 按不退款处理。
   - 为任务 08 提供任何 Dodge 提交前的统一只读空间快照，以及全部提交后的新位置与 From/Destination/提交结果；任务 08 用同一批冻结 Intent 建立旧/新接触并集、保留 Undodgeable 旧接触并去重。本任务不能直接把攻击标为 Dodged，不能只提供新格而丢失旧接触证据。
12. 实现唯一纯逻辑 Pathfinder 与版本化成本/搜索协议：
   - `PathCostRules` 沿用现有玩法：偶数方向 `East/NorthEast/NorthWest/West/SouthWest/SouthEast` 权重 1，奇数方向 `EastNorth/North/WestNorth/WestSouth/South/EastSouth` 权重 2。路径成本是 checked `long` 的权重和；不使用“直行/斜向”命名，也不按单位速度、动画距离或世界坐标改权重。
   - `HeuristicWeightUnits = dy + max(0, (dx-dy)/2)`，其中 `dx/dy` 是 doubled-coordinate 绝对差，整数除法向下；坐标减法/绝对值先扩展为 checked `long`。A* 的 `g/h/f` 都使用 checked `long`；开放集按 `(f, h, GridPoint.X, GridPoint.Y)` 升序取节点，不增加 `g` 或 `DiscoveryOrdinal/InsertSequence` 末级键。相同坐标只有更小的 `g` 才更新，相同 `g` 保留已有父节点。
   - 只在 `LogicGrid` / Encounter 明确声明的合法网格边界内搜索。首版 `PathSearchRules` 固定为 `MaxExpandedNodes = 4096`、`MaxPathWeightUnits = 256`、`MaxPathEdges = 192`；三项使用包含式边界，值 `<=` 上限合法，只有严格 `>` 才失败。不设置独立坐标距离上限。每次取出非过期开放集节点时先 checked 增加 ExpandedNodes，起点计 1，过期队列项不计数；若结果 `> 4096`，必须在目标检查和邻居展开之前返回节点上限失败。禁止保留旧 `maxIterations = 1000`、距起点 `±100` 或返回 `null` 的隐式语义。
   - 建立开放集前按起点、终点顺序预检。非法 doubled-coordinate、越过 Encounter 边界或不满足相应占位/目的地体积条件分别返回 `PATH_INVALID_START`、`PATH_INVALID_DESTINATION`；两者同时非法时起点优先，不得进入搜索后伪装为无路。搜索阶段使用稳定判别结果：`PATH_NOT_FOUND`、`PATH_SEARCH_NODE_LIMIT_EXCEEDED`、`PATH_WEIGHT_LIMIT_EXCEEDED`、`PATH_EDGE_LIMIT_EXCEEDED`、`PATH_COST_OVERFLOW`。候选边先做 checked 坐标/成本运算，再计算 EdgeCount 和 PathWeight，随后检查边界/占位；checked 溢出立即终止整个搜索。`CandidateEdgeCount > 192` 或 `CandidatePathWeightUnits > 256` 只丢弃该候选并记录原因，等于上限继续松弛；其他合法分支继续搜索。开放集耗尽后按 `PATH_EDGE_LIMIT_EXCEEDED -> PATH_WEIGHT_LIMIT_EXCEEDED -> PATH_NOT_FOUND` 选择已记录的最高原因。进入搜索后的全局优先级固定为 `PATH_COST_OVERFLOW > PATH_SEARCH_NODE_LIMIT_EXCEEDED > PATH_EDGE_LIMIT_EXCEEDED > PATH_WEIGHT_LIMIT_EXCEEDED > PATH_NOT_FOUND`。ScheduleEvaluator、AI 和只读预览必须消费同一结果，不得各自猜测失败原因。
13. 为任务 08 提供强制位移专用的原子占位提交原语，但本任务不实现同时求解器：
   - `BatchRelocation(UnitId, ExpectedFrom, To)` 与 `LogicGrid.ApplyBatchRelocation(IReadOnlyList<BatchRelocation>)`；输入先按 UnitId 检查唯一性，仅用于验证/规范诊断，不用于选择赢家。
   - 调用前，任务 08 已在不可变快照和临时空间确定全部最终位置，并已通过任务 05 的终态协调器释放冲突移动计划的未来 Segment/Reservation。本原语不得调用 Pathfinder、MoveTimingSpec、MovementSegment 推进、`TryReservePath` 或普通 Reservation 仲裁。
   - 方法先验证全部单位仍处于 `ExpectedFrom`、来源 footprint 与当前权威占位一致、目标锚点/完整 footprint 合法、批次目标彼此不重叠、移除批次内全部旧 footprint 后不与静止单位相交，且待抢占 Reservation 已清理。全部验证通过后，先统一移除所有旧 footprint，再统一写入全部新 footprint。
   - 任一验证失败报告 `InvariantViolation` 且真实 LogicGrid 零写入；禁止按 UnitId 部分提交、现场重算落点或改为单单位移动。该方法不产生表现事件，任务 08 在成功提交后发射规范位移事件。

### Dodge 换位与移动依赖的原子提交

按主方案 3.1.2，在候选中先预检落点与任务 05 返回的当前移动依赖闭包；仅实际换位成功时，将后续 Editable Move 的 `MovementOriginInvalidatedByDodge` 终态、全部未来段/预留清理、任务 07 的预算释放和 Occupancy 变化作为一个原子提交。不得先终止真实 Move 再尝试换位；任一内部提交失败应零局部写入并报告 InvariantViolation。失败/取消/未换位不因本 Dodge 清理移动。

接受和等待 Dodge 不改变普通移动的位置预测；未来 Move 仍基于当前权威移动链，成功换位时统一清理失效依赖，不从预期 Dodge 落点自动改路。单纯未来起点依赖不拒绝 Dodge，但既有真实 Reservation/占位冲突仍遵守原校验，Dodge 不获得强制位移式的抢占权限。提供同源只读条件预检，列出受影响 Move PlanId；任务 07 加入按原窗口分组的拟释放预算，供 UI/AI 消费。触发时重新求值，记录实际 InvalidatedPlanIds/释放额；预检不得写入状态或分配 ID。

## 逻辑与表现边界

- `WorldToGrid`、`GridToWorld`、射线检测、Gizmo 与视觉插值留在 UnityView。
- Logic 只接收离散坐标和体积。
- Pattern/Volume 的朝向点集必须来自任务 02B 的完整 12 向规范表；LogicGrid、Pathfinder 和 Dodge 校验只能按 `GridDirection` 查表并做整数平移，不得调用旧 `GridMath.Rotate`、`Mathf`、三角函数或浮点舍入。
- 区域查询返回的 UnitId 必须稳定排序。
- Pathfinder 必须只消费 `GetNeighborsOrdered` 的规范邻居序列；开放集只按 `(f, h, GridPoint.X, GridPoint.Y)` 取节点，相同坐标、相同 `g` 时保留已有父节点，不得再以发现序号或普通容器枚举顺序补充平局规则。`GridDirection` 数值/顺序、GridPoint 的 `X → Y` 字典序、1/2 权重、启发函数、平局规则、搜索上限和 Encounter 网格边界全部进入 `BattleDefinitionHash`。
- 所有活单位默认按体积参与 Occupancy/Reservation，不因 Allied/Neutral/Hostile、Controller 或玩家标志而穿透；FactionRelationResolver 只供后续动作目标资格使用。未来若需要友军穿越，必须由显式移动/占位规则版本化，不能把关系矩阵当碰撞开关。
- 仲裁所需的移动上下文以只读查询提供；本任务不决定 Attack 对移动是 Escape 还是 Intercept。
- `Dodging` 状态不影响占位或命中资格；只有成功执行 TriggerTick 原子提交才改变逻辑位置。仍覆盖新格的 AOE、新格新增接触、旧格已成立而被保留的 Undodgeable 接触或提交失败的 Dodge 继续由任务 08 正常求解；旧/新都无接触时不凭空追踪。换位成功不等于成功避伤。
- 移动计划若在当前段 `EndTick` 前进入终态，单位保留最后一次已提交的逻辑格（通常为当前段 `From`），目的格与未来路径 Reservation 立即释放，原 `EndTick` 不得再提交到 `To`。若命令前边界已经先完成该段提交，则保留已提交的 `To`；结果只由 Step 阶段顺序决定，不读取视觉插值进度。
- Editable Move 尚未开始，不修改当前真实 Occupancy；其路径和结束格是排程预测。ScheduleEdit 删除/移动前序计划时，从最后一个 Locked/Running/已提交位置重新计算受影响链；无路、超步数、预算或 Reservation 冲突使整批失败，不能只保留部分下游旧预测。
- 系统自动延期同样从最后一个不可变位置重算候选链，并遵守只向右、禁止移动 Locked/Running/固定反应、不左吸与最大视野规则。成功前旧 Segment/Reservation 保持权威；失败时旧候选不做局部替换，随后由任务 05 的终态协调器释放到期 Editable 计划持有的全部空间预留。
- 强制位移是 Resolution 提交的独立逻辑结果，不得伪装成“让已终态移动段继续到达 To”，也不能复用被清理的 Reservation。任务 06 只提供 `ApplyBatchRelocation` 原子提交原语；逐格临时求解、争抢/交换/依赖图/停止原因、Reservation 抢占和位移事件全部由任务 08 负责。

## 工作步骤

1. 从现有 `GridManager`、`GridMath`、`TrianglePoint`、`UnitVolume` 和 `Pathfinder` 分离纯数据规则；把 12 向邻居遍历、1/2 边权、整数启发函数、搜索限制和失败结果收敛到唯一实现，禁止各调用方自行排列方向或计算成本。将占位/目的格校验改为消费 `VolumeSpec` 的预展开表；旧 `GridMath.Rotate` 只留在 Legacy 兼容路径，New/Shadow Logic 源码和程序集均不得引用。
2. 建立占位与预留不变量，明确重叠、忽略自身和非法路径的错误。
3. 接入任务 05 的 ScheduleEvaluator/显式编辑与系统自动延期事务、Step 命令前边界和统一终态协调器；提供纯候选路径求值、有限空间阻塞 RetryAtTick 查询与整批 Reservation 替换，终态参与者必须显式装配在任务 05 冻结的固定位置。
4. 保留 `GridManager` 作为过渡期坐标/场景桥，但停止它作为新 Logic 路径的权威占位源。
5. 添加权重 1/2、整数启发、开放集平局、搜索边界/上限/溢出、速度与选路正交、原始命令批次枚举顺序、规范化命令顺序、路径冲突和快照回归测试。
6. 将 Dodge 目的格预留接入任务 05 反应计划事务和统一终态清理；建立 TriggerTick 原子提交 API，并证明该提交早于任务 08 同 Tick 接触复核。
7. 实现并单测 `ApplyBatchRelocation`：先完成全批验证，再统一移除来源 footprint、统一写入目标 footprint；为任务 08 冻结调用前置条件与 `InvariantViolation` 零写入语义，不在本任务加入强制位移求解规则。

## 允许改动

- `Assets/Scripts/Logic/Grid/**`
- `Assets/Scripts/Logic/Movement/**`
- `Assets/Scripts/Logic/Actions/**` 中事务扩展
- `Assets/Scripts/Logic/Simulation/**` 中命令前边界接入
- `Assets/Scripts/Logic/Snapshots/**`
- `Assets/Tests/EditMode/**`
- `Assets/Tests/PlayMode/**`，仅限任务 03B 隐藏场景和 Shadow 比较用例
- 兼容调整：
  - `Assets/Scripts/Core/Grid/**`
  - `Assets/Scripts/Core/Pathfinding/Pathfinder.cs`
  - 与移动 Intent 直接相关的旧文件

## 必需测试

- `AreaQueryReturnsUnitsOrderedByUnitId`
- `LogicGridUsesCanonicalVolumeTableForEveryDirection`
- `VolumeOccupancyOnlyIndexesAndTranslatesIntegerPoints`
- `RuntimeGridHasNoLegacyFloatRotationDependency`
- `NeighborEnumerationUsesCanonicalGridDirectionOrder`
- `EvenGridDirectionsHaveOneWeightUnit`
- `OddGridDirectionsHaveTwoWeightUnits`
- `PathWeightEqualsCheckedSumOfEdgeWeights`
- `IntegerHeuristicMatchesUnobstructedOneTwoCostLowerBound`
- `PathfinderUsesNoFloatingPointCostOrHeuristic`
- `EqualCostPathUsesCanonicalNeighborOrder`
- `EqualFAndHPathUsesGridPointLexicographicOrder`
- `OpenSetTieDoesNotDependOnInsertionSequence`
- `EqualTentativeGKeepsFirstCanonicalParent`
- `MoveSpeedDoesNotChangeChosenPath`
- `MoveSpeedOnlyChangesResolvedBaseStepTicks`
- `PathfinderSearchesOnlyInsideEncounterGridBoundary`
- `InvalidStartIsRejectedBeforeInvalidDestinationAndSearch`
- `InvalidDestinationIsDistinctFromPathNotFound`
- `PathSearchAllowsExactly4096ExpandedNodes`
- `PathSearchNodeLimitReturnsStableFailure`
- `PathSearchAllowsExactly256WeightUnits`
- `PathWeightLimitReturnsStableFailure`
- `PathSearchAllowsExactly192Edges`
- `PathEdgeLimitReturnsStableFailure`
- `PathCostOverflowReturnsStableFailureWithoutMutation`
- `PathFailurePriorityIsOverflowThenEdgeThenWeight`
- `NoPathIsDistinctFromSearchLimitFailure`
- `MovingUnitOccupiesFromUntilEndTick`
- `MovementCommitsToDestinationAtEndTickBeforeCommands`
- `DestinationRemainsReservedDuringMovementSegment`
- `MultiStepPathUsesIndependentSegments`
- `ReservationConflictWinnerFollowsCanonicalCommandOrder`
- `ReservationConflictIgnoresRawBatchEnumerationOrder`
- `DuplicateProducerOrdinalCannotChooseReservationWinner`
- `LaterCommandCannotPreemptCommittedReservation`
- `FailedPathReservationRollsBackAllSegments`
- `WindowSwitchDoesNotChangeMovementOrReservation`
- `GridStateAppearsCanonicallyInSnapshot`
- `FactionRelationDoesNotImplicitlyDisableOccupancyOrReservation`
- `PlanTerminalBeforeMovementEndKeepsLastCommittedCell`
- `TerminalPlanNeverCommitsDestinationAtFormerEndTick`
- `PlanTerminalReleasesAllOwnedReservations`
- `PlanTerminalRemovesActiveAndFutureMovementSegments`
- `RepeatedPlanTerminalCleanupIsIdempotent`
- `VisualInterpolationDoesNotAffectInterruptedMovementResult`
- `NoActiveMovementArtifactReferencesTerminalPlanAtStepEnd`
- `BattleEndReleasesReservationsAndRemovesActiveMovementSegmentsInStableOrder`
- `MoveSegmentsUsePlanResolvedBaseStepTicksWithoutRecalculation`
- `WeightTwoSegmentTakesTwiceWeightOneSegmentTicks`
- `MoveEndTickAndBudgetUsePathWeightNotEdgeCount`
- `MoveTimingOverflowRejectsWholeCandidateWithoutClamp`
- `NewMoveTimingDoesNotApplyLegacyPointTwoToFourSecondEdgeClamp`
- `EditableMoveChainRecomputesPathCountFromProjectedPredecessorPosition`
- `EditableMoveReorderAtomicallyReplacesSegmentsAndReservations`
- `FailedEditableDependencyRecalculationRollsBackWholeScheduleBatch`
- `TimedReservationBlockerExposesFiniteReleaseTick`
- `StaticOrUnknownSpatialBlockerIsNotRetryable`
- `AutoDeferralReusesMovePathAndReservationEvaluator`
- `AutoDeferralAtomicallyReplacesEditableSegmentsAndReservations`
- `FailedAutoDeferralLeavesNoPartialReservationReplacement`
- `AutoDeferralCannotMoveLockedOrReactionReservation`
- `LockedMovePathAndSegmentsCannotBeRecomputed`
- `MoveSegmentCountMatchesLockedPlanResolvedPathEdgeCount`
- `MoveSegmentWeightSumMatchesLockedPlanResolvedPathWeightUnits`
- `DodgeDestinationIsReservedForDerivedTriggerTick`
- `DodgeCommitsDestinationAtomicallyBeforeContactRecheck`
- `DodgeRelocationAndDependentMoveCleanupCommitAtomically`
- `DodgeCommitFailureLeavesDependentMovesReservationsAndBudgetsUnchanged`
- `CancelledDodgePreservesFutureMovementChain`
- `DodgeWithoutPositionChangeDoesNotInvalidateMoves`
- `DodgeDependencyPreviewIsReadOnlyAndUsesCommitDependencyQuery`
- `DodgeDoesNotCreateContinuousMovementOrInvulnerability`
- `SourceThreatCancellationReleasesDodgeDestinationReservation`
- `DodgeDestinationConflictFollowsCanonicalCommandOrder`
- `InvalidDodgeDestinationTerminatesReactionWithoutMovingUnit`
- `AreaAttackCanStillHitCommittedDodgeDestination`
- `BatchRelocationValidatesAllSourcesBeforeMutation`
- `BatchRelocationValidatesWholeMultiCellFootprint`
- `BatchRelocationRemovesAllOldFootprintsBeforeAddingNewFootprints`
- `BatchRelocationRejectsOverlapWithStationaryOccupancy`
- `BatchRelocationFailureLeavesGridUnchanged`
- `BatchRelocationRequiresPreemptedReservationsToBeCleared`
- `BatchRelocationDoesNotUsePathfinderSegmentsOrReservationArbitration`
- `LogicGridMovementAndReservationShadowProfileHasNoUnclassifiedDifference`

## 验收标准

- LogicGrid 不引用 `GridManager.Instance`、Transform、Collider 或 Vector3。
- 相同 `VolumeSpec`、逻辑位置和 12 个方向始终得到对应规范表平移后的占位；点的旧资产排列或运行平台不会改变结果，运行时不存在重新旋转或舍入步骤。
- 所有寻路入口使用同一规范邻居序列、偶数/奇数方向 1/2 权重、整数启发和 `(f, h, GridPoint.X, GridPoint.Y)` 开放集规则；改变 Dictionary/HashSet 插入顺序、调用方集合顺序、开放集插入顺序或单位 MoveSpeed 不改变路径赢家。修改方向、GridPoint 字典序、权重、启发函数、平局规则、搜索上限或网格边界会改变定义哈希并要求更新规则版本。
- `PathWeightUnits` 是 A*、Move 总时长和预算成本的共同尺度；权重 2 的边持续时间严格是同计划权重 1 边的两倍。`ResolvedPathEdgeCount` 只用于 Segment 数量/索引，不能用于计费。
- 搜索越界、无路、节点上限、权重上限、边数上限与算术溢出分别得到冻结失败码；`4096` 个展开节点、`256` 权重和 `192` 条边本身均合法，只有严格超出才失败，失败选择遵守 `Overflow > Node > Edge > Weight > NotFound`。所有失败在 ScheduleEdit/预览/自动延期事务中零局部写入，且不回退旧 `1000`/`±100` 隐式保护。
- 相同单位位置与体积在改变 Controller/FactionDisposition 后仍得到相同 Occupancy/Reservation；阵营关系不会隐式制造穿透。
- 同一 Tick 的位置提交早于新命令校验。
- 同一组带有相同可信来源键的请求以不同原始枚举顺序输入时，网关生成的命令顺序、Reservation 赢家、拒绝结果、事件和快照一致；重复生产者键整组拒绝，不产生 Reservation 赢家。
- 后处理的冲突命令只回滚自身事务，不得撤销或修改先前命令拥有的 ActionPlan、ActorLane 和 Reservation；ScheduleEdit 只能在单个原子批次内整体替换该批可控 Editable 计划自己的预测与 Reservation。
- 旧 GridManager 不再是新逻辑路径的双写权威源。
- 任务 08 可以只通过只读 LogicGrid 和 MovementSegment 查询完成交互判定。
- 任务 08 可以把已同时求解的全局最终位置交给一个 `ApplyBatchRelocation` 调用；该调用在任何来源/footprint/Reservation 不变量失败时零写入，成功时真实网格不存在可观察的逐单位中间状态。
- 任务 08 能在单一固定阶段先冻结统一旧格接触、调用 Dodge 原子提交，再合并新占位接触；支持 Undodgeable 保留且不重复结算，没有基于 Dodging 状态的免伤旁路。
- 任意计划终态后都不存在该计划的活动/未来 MovementSegment 或 Reservation；移动中断停在最后已提交逻辑格，且不会在旧 EndTick 发生迟到位置提交。
- Editable 移动链的时间线重排会同步重算预测起点、路径、边数、路径权重、结束 Tick、预算接缝、Segment 与 Reservation，但不重采样 MoveSpeed；Locked/Running/反应计划和真实 Occupancy 不被该重算改写。
- 门禁只把具有权威有限 ReleaseTick 的空间阻塞报告为 Retryable；系统延期与显式编辑复用同一空间求值器，成功时整批替换 Editable 依赖闭包，失败时无局部空间写入并由统一终态路径清理。
- 战斗结束复用相同终态参与者；最终快照中不存在活动 Reservation 或仍会在未来 Tick 提交位置的 MovementSegment。
- 对应隐藏场景/Shadow 用例已重跑；逻辑格、MovementSegment、Reservation 和终态差异全部可追溯，视觉插值不进入等价比较。

## 禁止事项

- 不引入连续子格坐标或基于视觉插值的命中判定。
- 不在 New/Shadow Logic 的占位、区域、Dodge 或寻路路径中调用 `GridMath.Rotate`、Unity 数学、三角函数或浮点舍入；预展开只属于任务 02/02B 的定义构建边界。
- 不用 Faction/Controller/玩家标志跳过占位；友军穿越若进入范围必须是独立显式规则。
- 不在窗口切换时提交移动或释放 Reservation。
- 不由未规范化的路径、邻居、开放集插入顺序或容器枚举顺序决定同成本路径或 Reservation 冲突胜者；同成本路径只允许由固定邻居序列、`f → h → GridPoint.X → GridPoint.Y` 和“相同坐标、相同 g 不替换父节点”共同决定，Reservation 赢家仍只服从规范化 `CommandSequence`。
- 不使用浮点 `g/h/f`、单位速度、Transform 距离或“直行/斜向”标签改变 1/2 路径权重；不把 EdgeCount 当作 PathWeightUnits。
- 不使用硬编码迭代次数、距起点坐标窗口、无界搜索或 `null` 混淆无路与资源上限失败。
- 不为 Reservation 另设抢占优先级，也不允许后处理命令回滚此前已经提交的 Reservation。
- 不让 UnityView 自己计算并写入移动链路径/时长；预览和提交必须复用 Logic 的 ScheduleEvaluator。
- 不重算 Locked Move，不因删除前序计划把未直接移动的后续 Move 自动左移。
- 不为未知/永久空间冲突编造 `tick + 1` RetryAtTick，不以轮询掩盖非法占位或 Reservation 所有权矛盾。
- 不由移动系统、Intercept Resolution 或 BattleEndFinalizer 直接改写 ActionPlan 终态，也不各自复制一套 Segment/Reservation 清理。
- 不把终态清理当作强制位移，或依据 Transform/动画进度选择 From/To。
- 不在本任务实现强制位移的争抢赢家、依赖图、停止原因、Reservation 抢占或事件；不让 `ApplyBatchRelocation` 调用 Pathfinder、MoveTiming、MovementSegment 或普通 Reservation 竞争逻辑。
- 不把批量换位拆成按 UnitId 循环调用单单位 `Unregister/Register` 的可观察提交，也不在部分验证或部分写入失败后保留半批结果。
- 不在本任务实现完整攻击仲裁或表现层网格重绘。
- 不把 Dodge 实现为普通 Move 的加速版本、连续穿越路径或持续无敌区间；逻辑只在 TriggerTick 从 From 原子切换到 Destination。

## 交接重点

交接必须冻结 `GridDirection 0 -> 11` 的规范邻居枚举顺序、非法邻居跳过语义、偶数/奇数方向 1/2 `StepWeightUnits`、整数启发公式、checked long 成本、`(f, h, GridPoint.X, GridPoint.Y)` 平局与相同 `g` 不替换父节点规则、Encounter 网格边界、三个搜索上限及五类稳定失败码，任务 02B 预展开 12 向 Pattern/Volume 表的只读消费接口与“只索引、平移、不旋转”边界，占位半开区间、FactionRelation 不影响 Occupancy/Reservation 的边界、`Free/RetryableTimedBlock/TerminalOrUnknownBlock` 空间门禁结果与有限 ReleaseTick、首次量化的 Move 每权重单位 Tick、显式编辑/系统自动延期共用的 Editable 移动依赖闭包预测位置/路径/边数/路径权重/Segment/Reservation 整批重算、Locked 后冻结边界、Dodge TriggerTick 目的格预留与原子提交阶段、复用任务 03 网关 `CommandSequence` 的处理顺序与“先成功提交者持有 Reservation”规则、仅回滚当前事务的路径失败语义、统一终态清理参与者及其排序、移动中断保留最后已提交逻辑格的规则、`BatchRelocation` / `ApplyBatchRelocation` 的全批预检、统一移除/统一写入、失败零写入和“只提交不求解”边界、本任务新增的 Shadow 检查点与精确批准差异，以及供任务 08 使用的新位置/移动查询接口。任务 08 的 Intercept 只能请求计划终态和独立强制位移 Resolution，不能直接删除 Segment/Reservation；任务 08 拥有同时强制位移求解、Reservation 抢占与事件并只调用一次批量换位原语，Dodge 结果也必须来自空间复核，不能从状态推断。
