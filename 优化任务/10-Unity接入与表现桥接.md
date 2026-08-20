# 10 - New 模式切换、UnityView 与场景迁移

状态：待执行  
前置任务：02B、03B、07、09  
建议规模：7-12 天

## 执行目标

在任务 03B 已建立的唯一 `BattleRuntimeBootstrap` 和三模式壳下，补全 New 被调用适配器、UnityView 边界与表现桥接，先用完整 Shadow 场景验证，再把主战斗场景默认模式从 Legacy 切换为 New。Unity 生命周期只负责采样、由 Bootstrap 调度 Step、消费只读快照和语义事件、进行视觉插值与反馈；不得新增平行的 `Update()` 推进器。主战斗场景在迁移后仍可进入、行动和结束。

## 工作量拆分

1. **New 驱动补全与模式切换（2-4 天）**：扩展任务 03B 的被调用 `UnityBattleDriver`，接入完整命令批次、事件/快照消费、单位与网格绑定；完成全场景 Shadow 证据后，把唯一 Bootstrap 的主场景默认模式切到 New。
2. **场景、UI、表现迁移与回归（3-6 天）**：迁移现有场景和 Prefab 引用，接回时间线、HUD、移动插值、动画与战斗反馈，清除重复权威写入，并完成 PlayMode 与人工场景检查。

第二部分必须建立在第一部分可编译、最小场景可运行的基础上，但两部分共同构成本任务的完成范围；不能只完成 New 适配器或保留主场景 Legacy 默认值就宣称任务 10 完成。实际场景或 Prefab 规模超过任务 01 基线时，应由调度者上调估算，不得通过省略回归、放宽 Shadow 忽略项或保留双写路径压缩工期。

## 必读范围

- 主方案：目标架构、`2.1.1`、`3.3`、`3.4.0`、`3.7`、`3.8.1` 的 GridView、第三与第六阶段路线
- [00 - 执行规则与依赖](00-执行规则与依赖.md)
- [02B - 完整配置、ID 迁移与战斗初始化](02B-完整配置与战斗初始化.md)的全资产迁移记录、`BattleDefinitionBuilder`、初始化 API 与槽位映射契约
- [03B - Unity 运行时所有权壳与 Shadow 验证](03B-运行时所有权与影子验证.md)的自主推进清单、Bootstrap/模式契约、隐藏场景、比较配置与批准差异
- 任务 03 的 Step/事件/快照接口、任务 04-09 的 Shadow 扩展证据、任务 09 的命令入口与暂停契约

## 必须产出

1. **唯一运行时调度保持不变**：
   - `BattleRuntimeBootstrap` 仍是场景中唯一通过 `Update()` 选择并推进顶层战斗时钟的组件。
   - `UnityBattleDriver` 是 Bootstrap 在 New 模式调用的普通适配器，自身不得实现 `Update()`、Coroutine 或其他自主推进。
   - Legacy 与 Shadow 适配器也只能被 Bootstrap 调用；模式在创建本场战斗前固定。
   - Bootstrap、运行时适配器契约和 New/Shadow 实现保留 `.meta` 搬入 `ProjectHero.UnityView`。Legacy 具体适配器暂留 `Assembly-CSharp`，实现 UnityView 拥有的接口，并通过显式序列化 `MonoBehaviour` 槽位在启动时校验和注入；UnityView 不能引用 Legacy 具体类型，也不能复制一份旧类型规避依赖。
2. `UnityBattleDriver`：
   - 使用 `unscaledDeltaTime` 累积逻辑 Tick。
   - 只在 Tick 边界调用 BattleSimulation.Step。
   - 冻结并提交当前 Tick 命令批次。
   - 用户暂停时停止 Step，并禁止生成或积压战斗命令。
   - 收到 `StepStatus.BattleEnded` 后立即跳出本帧 Tick 累积循环并永久停止推进，但继续消费该结束 Tick 的最终事件与快照以播放结束表现。
   - 若防御性地收到 `StepStatus.AlreadyEnded`，不得重复播放结束反馈或推进本地 Tick。
3. **Shadow 完整化与切换门槛**：
   - 把任务 04-09 已实现的真实玩家请求、AI/System 重建、状态、计划、网格、窗口、仲裁和胜负检查点接入 Shadow。
   - 复核任务 03B 的 Legacy 从属写入清单；已经迁入 Step 的旧回调删除或只读化，仍保留的旧写入者在 New 全部禁用。任何未分类逻辑写入阻止主场景切换。
   - 切换前将任务 03B 报告中的“暂不可比较字段”清零；只读 LateUpdate 检查点顺序测试必须覆盖所有仍登记的 Legacy 写入者。
   - 切换前运行主场景对应的完整场景集；基础设施非预期差异为 0，玩法差异只能使用经评审的逐用例、逐字段规则。
   - 主场景默认模式切到 New 后，旧 `BattleTimeline.AdvanceTime()` 和 Legacy 写入调用数为 0；Legacy 启动适配器暂时保留，只供任务 11 重新开局式回切演练。
   - 不支持战斗中热切换。切换或回切都必须先销毁当前战斗，再用另一模式和自身初始化流程创建新战斗。
4. 场景初始化桥：
   - 场景只选择任务 02B 已定义的稳定 Encounter，并保存各 View 的 `EncounterSlotId`。
   - 只调用任务 02B 的 `BattleDefinitionBuilder` / 初始化 API，不在运行时扫描名称、补默认值、生成配置 ID 或重新转换旧 ActionLibrary。
   - 通过显式入口一次性取得 `BattleRuntimeInputs`（包括初始 RNG 种子和本场局外资源快照），原样传给初始化器并交给回放 Header 保存；Header 使用任务 03 冻结的 `ReplayFormatVersion`，并保存初始化完成、首个 `Step()` 前的 `InitialStateHash`。Logic 不得读取当前存档或外部 Singleton。
   - 构建或初始化失败时阻止战斗启动并显示完整、稳定错误；不得在同一次启动中回退到 Legacy 或跳过配置。运维回切只能显式结束并新建一场 Legacy 战斗。
5. `CombatUnitView` 或等价桥：初始化器通过任务 02B 返回的槽位映射解析本场 `(UnitId, FactionId)` 后完成绑定；View 只读取快照和事件，不持有逻辑写权限，也不手填运行时数字 ID/FactionId。阵营颜色、图标和本地文案可由 View 元数据映射，但不能反向成为 Logic 关系来源。
6. `GridView`：负责世界坐标与离散网格坐标转换、射线和调试绘制；LogicGrid 保持权威。
7. `CombatFeedbackMapper`：把分通道 Damage、Guard、Dodge、Block、Clash、StateChanged、ForcedDisplacementResolved 等语义事件映射到伤害数字、震屏、顿帧、动画和音效策略；表现可以合并显示，但调试层必须能展示 Raw/抵抗后伤害、抵抗前后动量，以及强制位移的 Requested/AppliedSteps、StopReason 和 InvalidatedPlanIds。
8. `UnitMovement` 改为纯视觉插值；不得在插值完成回调中提交逻辑位置。收到移动计划终态事件或发现最新快照仍位于最后已提交逻辑格时，必须停止旧目的地插值并向权威快照位置收敛，不能让表现继续暗示计划将在原 EndTick 到达。收到 `ForcedDisplacementResolvedEvent` 时只从事件 From 向最终 To 播放一次位移表现；不得逐格回写、重新做碰撞/寻路/Reservation 判定，零步结果只播放可选受阻反馈并保持快照位置。
9. UI/时间线以只读快照为权威，但保留普通计划编辑能力，不能直接写全局集合：
   - 显示 `CurrentTick + 1` 锁定线及 Editable/Locked/Running/终态差异；支持未来放置、整数 Tick/关键帧吸附、拖动重排、删除、向右避让预览，以及移动链预测位置/路径/路径权重/时长/预算变化。路径、时长与预算必须完整来自 Logic 预览；UI 不按世界距离、MoveSpeed 或 EdgeCount 自行估算。
   - 一次拖拽过程只修改本地草稿，并调用 Logic 的纯 SchedulePreview API；确认时仅发送一个带 ExpectedScheduleRevision 的原子 ScheduleEditCommand。收到旧修订/锁定/路径/预算拒绝后丢弃本地候选并基于最新快照重预览，不做局部合并。
   - 收到 `ActionPlanAutoDeferredEvent` 或发现快照 ScheduleRevision 因系统延期变化时，取消涉及受影响依赖闭包的本地拖拽候选，按新 Editable 投影重绘时间线并可表现“因控制/空间阻塞延期”；UI 不回发第二个 ScheduleEdit，也不把视觉动画当作权威 StartTick。
   - 只有当前 Controller 决策快照中获准显示/控制的 Editable 普通计划可操作；Locked/Running/终态与 Block/Dodge 反应计划只读。不得从开发者 Canonical Snapshot 泄露其他 Controller 的未公开 Editable 细节。删除不自动左吸后续计划，压缩必须由玩家显式移动。
   - 选择攻击目标时只用 DecisionSnapshot 提供的只读 `FactionRelationResolver + ActionSpec.AllowedTargetRelations` 结果展示 Self/Allied/Neutral/Hostile 候选；显式友伤/中立目标按动作掩码展示。UI 显示过滤不是授权，不能用 GameObject Tag/Layer、颜色、Controller 或 `IsPlayerControlled` 自建敌人列表。
   - 收到 `ReactionOpportunityOpenedEvent` 后显示来源威胁、Impact/截止 Tick、可选 Block/Dodge、费用与合法 Dodge 落点；玩家只选择机会/动作/落点，UI 不生成或拖拽 BlockTick/EvadeTick。单个选项过期时只移除该选项，机会关闭、接受或来源取消时再撤销整个提示。
   - TurnBudget HUD 区分 Available/Reserved/Spent；肾上腺素 HUD 区分 Available 与计划预留。二者只读取快照/事件，不在本地预测权威奖励、退款、消费或周期清零。
10. 单位注册表与明确绑定替换战斗热路径中的 `FindObjectsByType` 和重复 `GetComponent`。
11. 迁移 `BattleManager`、`CombatUnit`、`PhysicsEngine`、`CombatDemo` 等所有运行时入口和旧写入点；场景中保持一个 Bootstrap、一个当前模式和一条权威写入路径。

## 时间与表现边界

- 修改 `Time.timeScale`、动画速度、震屏或视觉顿帧不能改变相同命令下的逻辑 Tick、快照或事件。
- Unity Update/LateUpdate 可以刷新 UI、相机、特效和插值，但不能修改生命、资源、状态、逻辑坐标、计划、Intent 或胜负。
- 逻辑事件不要求具体表现；映射器可按本地策略选择忽略、合并或延后播放，但不能回写模拟。

## 允许改动

- `Assets/Scripts/UnityView/**`
- `Assets/Scripts/Core/Compatibility/Runtime/**`，仅限扩展任务 03B Bootstrap/模式适配器与切换证据
- `Assets/Tests/PlayMode/**`
- 兼容迁移：
  - `Assets/Scripts/Core/Gameplay/BattleManager.cs`
  - `Assets/Scripts/Core/Entities/CombatUnit.cs`
  - `Assets/Scripts/Core/Grid/GridManager.cs`
  - `Assets/Scripts/Core/Physics/PhysicsEngine.cs`
  - `Assets/Scripts/Demos/CombatDemo.cs`
  - `Assets/Scripts/UI/**`
  - `Assets/Scripts/Visuals/**`
- 与主战斗场景直接相关的场景/Prefab 绑定；必须避免无关资源重写

## 必需测试与场景检查

- `DriverAdvancesExpectedTicksFromUnscaledTime`
- `SceneContainsExactlyOneBattleRuntimeBootstrap`
- `UnityBattleDriverHasNoAutonomousUpdate`
- `UnityViewAssemblyDoesNotReferenceAssemblyCSharpLegacyTypes`
- `LegacyAdapterCanBeInjectedThroughUnityViewOwnedContract`
- `LegacyModeNeverCallsBattleSimulationStep`
- `NewModeNeverCallsLegacyAdvanceTime`
- `NewModeDisablesEveryLegacyLogicWriter`
- `UnclassifiedLogicUpdateBlocksNewCutover`
- `ShadowModeDoesNotWriteUnityOrPlayFeedback`
- `RuntimeModeIsFixedForBattleLifetime`
- `MainSceneDefaultsToNewMode`
- `NewBattleCanRestartInLegacyForRollbackRehearsal`
- `RestartBetweenModesDoesNotTransferRuntimeState`
- `UnexpectedShadowDifferenceBlocksNewCutover`
- `TemporarilyUncomparableShadowFieldBlocksNewCutover`
- `InvalidOrBudgetExceededShadowRunCannotAuthorizeNewCutover`
- `BroadShadowDifferenceAllowlistIsRejected`
- `ChangingTimeScaleDoesNotChangeLogicSnapshot`
- `PauseStopsStepsAndDropsCombatInputAtBoundary`
- `ViewCannotMutateLogicState`
- `MainSceneBuildsBattleDefinitionWithoutFallback`
- `SceneInitializationUsesTask02BSlotMap`
- `ScenePassesExplicitRuntimeInputsToInitializerAndReplayHeader`
- `ReplayHeaderUsesFormatVersionAndPreStepInitialStateHash`
- `MissingOrInvalidDefinitionPreventsBattleStart`
- `RenamingGameObjectDoesNotChangeEncounterSlotOrUnitBinding`
- `DuplicateOrMissingEncounterSlotBindingFailsClearly`
- `MovementInterpolationDoesNotCommitLogicPosition`
- `InterruptedMovementStopsVisualDestinationAndReconcilesToSnapshot`
- `ForcedDisplacementEventInterpolatesOnlyFromCommittedFromTo`
- `ForcedDisplacementVisualNeverRecomputesCollisionPathOrWinner`
- `ZeroStepForcedDisplacementKeepsSnapshotPositionAndMayPlayBlockedFeedback`
- `SemanticEventsMapToFeedbackWithoutLogicWriteback`
- `ReactionUiUsesOpportunityAndNeverConstructsTriggerTick`
- `ReactionUiClosesOnAcceptedExpiredOrSourceCancelledEvent`
- `DodgePreviewUsesLogicValidatedDestinationsButDoesNotCommitPosition`
- `AdrenalineHudSeparatesAvailableAndReservedWithoutLocalMutation`
- `TimelineShowsLockLineAtSnapshotTickPlusOne`
- `TimelineCanPlaceMoveAndRemoveOnlyEditableOrdinaryPlans`
- `TimelinePreviewShowsRightRippleWithoutLeftCompaction`
- `TimelineMoveChainPreviewComesFromLogicEvaluator`
- `TimelineDragProducesOneAtomicScheduleEditOnConfirm`
- `TimelineDragTrajectoryDoesNotProduceCombatCommands`
- `OpeningOrClosingActionPanelDoesNotCreateOrCommitDraftPhase`
- `StaleRevisionOrLockRejectionRebasesFromLatestSnapshot`
- `SystemAutoDeferralCancelsAffectedDraftAndRebasesTimeline`
- `TimelineNeverReinjectsAutoDeferralAsScheduleEdit`
- `TimelineCannotEditReactionLockedRunningOrTerminalPlans`
- `TimelineDoesNotRevealOtherControllerUnpublishedEditablePlans`
- `TurnBudgetHudSeparatesAvailableReservedAndSpent`
- `DamageDebugViewShowsChannelAndMomentumMitigation`
- `DriverStopsAccumulatedTickLoopImmediatelyAfterBattleEndedResult`
- `AlreadyEndedResultDoesNotReplayBattleEndFeedback`
- `OneInputProducesExactlyOneCommand`
- `UnitViewFactionBindingComesFromInitializationMapping`
- `TargetPickerUsesDecisionSnapshotRelationMaskInsteadOfGameObjectTags`
- `FriendlyFireTargetVisibilityFollowsActionSpecMask`
- 主战斗场景：可进入、可提交 Attack/Guard/Move、可响应已公开威胁使用 Block/Dodge、AI 单位可走同一路径反应、可跨窗口继续执行、可死亡并结束
- 场景中无 Missing Script、重复 Bootstrap、自推进适配器或重复单位注册

## 验收标准

- 新逻辑路径不再调用 GameFeelManager、Animator、Camera、Transform 或 MonoBehaviour。
- 主场景恰有一个 `BattleRuntimeBootstrap` 并默认以 New 启动；`UnityBattleDriver` 不拥有自主顶层时钟生命周期，旧 Timeline 推进调用数为 0，全部登记的 Legacy 从属写入者已删除、只读化或禁用。
- Bootstrap/New Driver 位于 UnityView 单向依赖边界；保留的 Legacy 适配器可通过 UnityView 拥有的契约注入，但 UnityView 不引用 `Assembly-CSharp` 具体类型。
- 切换前完整 Shadow 场景运行有效、未超预算、无未分类差异且无暂不可比较字段；Shadow 新模拟对 Unity、旧逻辑状态和表现反馈零写入。
- 主场景只通过任务 02B 的完整定义和初始化 API 创建逻辑世界；任务 10 没有第二套 ID 映射、默认值或旧资产转换代码。
- 逻辑状态只由 Step 修改；旧组件若保留，只做绑定、转发或只读兼容。
- 相同命令下改变表现速度不改变最终快照哈希和事件序列。
- 强制位移表现只消费已提交的 From/To/AppliedSteps/StopReason；动画速度、碰撞表现、补间中间格或受阻反馈均不能改变 LogicGrid、计划终态、Reservation、事件或快照。
- 普通时间线在不持有可变 Logic 引用的前提下恢复未来放置、重排、删除、向右避让和移动链重算；所有确认操作可追溯为 ScheduleEditCommand，预览与提交使用同一 Logic 求值器。
- 系统自动延期后 UI 只消费事件/快照并重基线，不直接改 Plan/Lane、不伪造 ScheduleEdit，Locked/Running/固定反应仍保持只读。
- 反应 UI 只消费机会事件/快照并构造 ReactionCommand；没有预测敌方 Impact 后直接写 Tick、资源、状态或逻辑位置的旁路。
- 主场景关键功能可用，Console 无新增异常。
- UnitView/目标选择器展示的阵营与目标资格来自初始化映射和 DecisionSnapshot；改变 GameObject Tag、颜色或 Controller 类型不会改变 Logic 接受结果。
- 战斗结束 Tick 的最终事件和快照被完整消费，之后驱动器不再调用活动模拟路径。
- 战斗热路径不再依赖全场扫描或每帧重复组件查找。

## 禁止事项

- 不让动画事件决定命中、移动完成或状态结束。
- 不新增第二个 `Update()`、Coroutine、Timeline 回调或其他自主战斗推进入口。
- 不让 `ProjectHero.UnityView` 反向引用 `Assembly-CSharp` 的 Legacy 具体类型，也不复制旧类或新建循环 asmdef 来保留回切。
- 不让 Shadow 新模拟绑定 View、播放反馈、修改场景/旧状态或成为胜负权威。
- 不在活动战斗中切换 Legacy/Shadow/New，也不复制运行中状态到另一内核。
- 不在 New 初始化失败时静默回退 Legacy；回切必须是显式结束并重新创建战斗。
- 不在运行时从显示名、GameObject 名、Asset 路径、加载顺序或 GUID 生成 Logic 配置 ID。
- 不在任务 10 临时修补任务 02B 未完成的配置语义；发现缺失映射、悬空引用或非法定义时必须回到 02B 修复并重跑全资产验证。
- 不让 View 持有可修改集合或调用 Logic 内部写方法。
- 不用动画事件、进度条或本地计时器决定 Block/Dodge Trigger；不让 UI 把防御拖放到任意时间线 Tick。
- 不把普通时间线退化为完全只读，也不让 UI 直接改 Plan/Lane、自己提交 ripple/路径结果、编辑越过锁定线，或把每帧拖拽写入命令/回放。
- 不用 `Time.timeScale` 直接控制逻辑 Tick 频率。
- 不为修复表现而复制第二份位置、状态或动作权威数据。
- 不在 View 中重演强制位移逐格求解、选冲突赢家、执行连锁推人或根据 Collider 修正 To；事件与最新快照是唯一位置事实。
- 不让 View 写入/推断 FactionId，不用 GameObject Tag/Layer、材质颜色、玩家标志或 Controller 类型决定敌我和可选目标。
- 不在本任务扩展新玩法或重做无关 UI 美术。

## 交接重点

交接必须列出使用的 EncounterDefinition、BattleDefinitionHash、`BattleRuntimeInputs` 的显式来源、`ReplayFormatVersion` 与首个 Step 前 `InitialStateHash` 的回放保存证据、场景槽位绑定及其 `UnitId/FactionId` 映射证据、Faction 显示元数据与 Logic 关系/目标资格的单向边界、目标选择器使用 DecisionSnapshot/AllowedTargetRelations 的证据、唯一 Bootstrap 与三模式调用计数、主场景 New 默认值证据、完整 Shadow 报告及全部批准差异、重新开局式回切演练步骤、场景绑定变化、旧组件剩余职责、普通时间线的锁定线/Editable 操作/预览/原子提交/修订冲突与 AutoDeferred 重基线映射、ReactionOpportunity UI/命令映射、TurnBudget/肾上腺素 HUD、分通道伤害/动量事件和 `ForcedDisplacementResolvedEvent` 到纯表现插值/受阻反馈的映射表、暂停与时间累积语义、已移除的热路径查询，以及任务 11 可删除的 Legacy 兼容壳候选。
