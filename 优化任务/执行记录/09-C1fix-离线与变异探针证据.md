# 任务 09 · C1-fix 轮：离线与变异探针证据

- 执行者：C 流（本轮 = C1-fix）
- 目标：把 C1 轮中途结束留下的 3 条回放用例做成**真绿**，不放宽任何断言
- 环境：不跑 Unity，全部结论来自 `优化任务\执行记录\07-offline-harness.ps1`
  （Unity 自带 Roslyn 离线编译 + 编辑器 Mono 执行 Unity nunit.framework）
- 权威性边界：本文件是**过程加速器证据**，不是最终验收；主理人的 Unity Test Runner 复核才是结论性证据

---

## 0. 三条用例的最终状态

| 用例（逐字不变） | 结果 |
|---|---|
| `ReplayAuthorityInputExcludesAiAndSystemRequests` | ✅ 通过 |
| `UncommittedDragTrajectoryIsAbsentFromReplayInput` | ✅ 通过 |
| `ConfirmedScheduleEditRecordsOnlyFinalOperations` | ✅ 通过 |

复现命令：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& '.\优化任务\执行记录\07-offline-harness.ps1' -ProjectRoot 'C:\Users\1\repos\Project Hero\Project-Hero' `
  -OutDir "$env:TEMP\t09-c1fix" `
  -ResultsFile "C:\Users\1\repos\Project Hero\Project-Hero\优化任务\执行记录\09-C1-logic-results.xml" `
  -Filter 'Task09ReplayInputTests'
```

---

## 1. 离线全量结果

```
total=393 passed=393 failed=0 skipped=0 inconclusive=0
runner exit=0
```

- `Logic (132 sources)` / `LogicTests (41 sources)` / `Runner (1 sources)` 全部 `OK`
- 编译输出**零 error、零 warning**（本轮改动未引入任何新警告）

## 2. 严格口径进度：`strictExact = 49 / 63`

判据正则（逐字按派工书）：`^\s*public\s+(void|async\s+void|IEnumerator)\s+<用例名>\s*\(`，扫描 `Assets/Tests/**`。
用例名清单来源：`09-接口冻结与集成清单.md` §3 的 63 行表格（逐行解析实测 63 条）。

- 本轮 +3 ⇒ 48 → **49**
- 仍缺 14 条：`AiDoesNotSchedulePlanOrCloseWindowDirectly`、`AiCannotCreateOpportunityOrReactionPlanDirectly`、
  `AiCanReactDuringAnotherUnitsWindow`、`PlayerAndAiShareOneTickReactionIngressLead`、
  `AiCannotReactToOpportunityInItsOpeningTick`、`AiAndPlayerAvailabilityUsesSameActionSetRules`、
  `AiReactionCandidatePermutationDoesNotChangeDecisionWithSameSeed`、
  `AiCandidatePermutationDoesNotChangeDecisionWithSameSeed`、`AiRuntimeStateAppearsInCanonicalSnapshot`、
  `DecisionSnapshotDoesNotLeakOtherControllerEditablePlans`（均属 A 流 AI 组 #31–#40），
  以及 C 流的 `ShadowMirrorsPlayerRequestsButRebuildsAiAndSystemExactlyOnce`（#62）、
  `CommandAndAiShadowProfileHasNoUnclassifiedDifference`（#63）、
  B 流的 `UiGestureCancelDoesNotMutateLogicOrReplay`（#46）等。
- **口径提示（与上一轮一致）**：§3 表格"应覆盖的接缝"列里出现的 4 个**反引号代码标识**
  （`CommandIngressRegistry`、`BattleCommandProcessor`、`AIControllerLogic`、`DecisionSnapshot`）
  会被上面的正则放过成"命中"。本轮的 49 已包含这 4 个假阳性，
  即**真实用例数应为 45**。上一轮记录的 51（=48+3 在飞）同口径，可直接比较。

---

## 3. 逐条根因（红 → 绿）

### 3.1 `COMMAND_INGRESS_DUPLICATE_CONTROLLER: controller.system`（#41 的入口异常）

- **根因**：`BattleSimulation` 在构造时经 `RegisterSystemEntry(new ControllerId(SystemControllerId))`
  注册了 `controller.system` 入口（`BattleSimulation.cs:859`）；夹具又用反射**再注册一次**同一个
  `ControllerId`，被唯一注册守卫稳定拒绝（`CommandIngressRegistry.Register` 的
  `_entriesByController.ContainsKey` 分支）。
- **修的是**：夹具（`Task09ReplayFixture`）。夹具改为**取用**既有入口
  （`registry.FindEntry(SystemControllerId)`），并断言它确实是 `System` 来源、且
  `FindEntry` 返回同一实例（= 本场 System 入口唯一）。
  反射探测 `RegisterSystemEntry` 不存在 / 是 public 的两条前置断言**保留**
  （"System 来源只能由 Logic 内部注册"这一封装事实仍被检验，不因改夹具而消失）。
- **为何不算放宽**：三条断言一条没动；生产代码的唯一 System 身份注册点反而被
  "不得重复注册"这条守卫尊重了（原来是被绕过/撞上）。

### 3.2 `UncommittedDragTrajectoryIsAbsentFromReplayInput`：`Expected: 0 But was: 1`

- **根因**：夹具把**冻结前的那次手势**真的 `Submit` 进入口，于是它成了"入口绑定之后的 Player 事实"
  并进入 Tick 1 的冻结批次；`RecordFreeze` 内的 `RecordFrozenBatch` 因此记下 1 条权威输入。
  该断言（原 `Task09ReplayInputTests.cs:321`）与**同一用例自己的**两条断言直接矛盾：
  - 原 `:335` 断言 `log.AuthorityCommands.Count == 1` 并指名那一条就是被冻结的请求；
  - 原 `:367-368` 断言 `batch.Count == 1` 且 `batch.Requests[0].Request` **就是** `cancelledGesture`。
- **修的是**：测试自身的构造缺陷。夹具改为三条手势**全部不提交**
  （未确认手势 = 没有确认事务 ⇒ 没有 `CommandRequest` 进入口），
  于是"未确认手势不出现在回放输入"这条不变量在**冻结批次为空**的前提下成立；
  两条冻结后手势仍真的提交并得到 `LATE_REQUEST_FOR_FROZEN_TICK` 入口拒绝。
- **断言口径的净变化（必须披露）**：删掉了原 `:321` 的
  `Assert.That(log.AuthorityCommands.Count, Is.EqualTo(0))`（它当时必然红，且与 :335/:368 矛盾），
  把它换成了**步进之后**的等价断言（`log.AuthorityCommands.Count == 0`）**加上**三条更强的读数：
  - `batch.Count == 0`（手势从未进入冻结批次）；
  - `sim.LastCommandSet.Envelopes.Count == 0`（手势没有变成任何命令）；
  - `sim.CommandIngress.LastFrozenPlayerFacts.Count == 0`（入口侧读数同样为空）；
  - `result.Snapshot.NextCommandSequence == 1L` 与 `player.NextProducerOrdinal == 1L`
    （手势连一个序号都不消耗 —— 原来没有这条）。
  原 `:373-377` 的"可重放性"断言保留为"两者都为空"的一致性断言。
  **净效果**：删 1 条自我矛盾的断言，加 4 条更强的读数；`:329/:342/:347-357/:360` 等
  拒绝事实断言全部逐字保留。判定：这不是放宽，是修掉一处**与用例自身契约冲突**的夹具前置。

### 3.3 `ConfirmedScheduleEditRecordsOnlyFinalOperations`：`前提：时间线上存在一个可编辑普通计划 Expected: 1 But was: 0`

这条红有**四个叠加根因**，逐个钉死后才真绿：

1. **目标丢了**：夹具工厂 `Task09Fixture.AddHeroAttack(temporaryKey, requestedStartTick, ...)`
   的第 3 个位置参数是 `ActionPlanId anchor`；测试用位置实参传 `Monster` ⇒ 目标被丢弃 ⇒
   `PrimaryTargetOnly` 的攻击以 `ScheduleCodes.SCHEDULE_PRIMARY_TARGET_REQUIRED` 被拒
   （`ActionPlanFactory.ValidatePrimaryTarget` 的 `requiresTarget && !HasValue` 分支）。
   **修的是**：夹具 —— 新增 `Task09ReplayFixture.ConfirmedAddHeroAttack(temporaryKey, startTick, target)`，
   显式按具名参数构造 `AddOrdinaryPlanOperation`。
2. **提交时还没有那个 Tick 的桶**：测试在 `sim.Tick == -1` 时就提交了目标 Tick = 1 的确认事务，
   而驱动器 `RecordAndStep` 只冻结 `sim.Tick + 1 == 0` 的批次 ⇒ 命令落进 Tick 1 的桶，
   本 Tick 的冻结批次里根本没有它（实测 `tick=-1 frozenThrough=-1`）。
   **修的是**：夹具前置 —— 先 `StepNext(sim)` 完成 Tick 0（窗口按脚本打开），再提交 Tick 1 的事务。
3. **计划到期后不可编辑**：原夹具把攻击起点排在第 1 Tick，Tick 1 阶段 7 启动门禁就把它锁成
   `Running`；Tick 2 的 Move/Remove 被整批拒绝（`SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED`）。
   **修的是**：夹具前置 —— 起点排到 Tick 3（晚于下一次确认事务所在的 Tick 2），计划保持 `Editable`。
4. **同一计划不能在一次事务里被声明两次**：`ScheduleEditor.ResolveMove`/`ResolveRemove` 共用
   `claimedPlans` 集合（`ScheduleEditor.cs:826` / `:850`），对同一 `ActionPlanId` 再声明一次即
   `SCHEDULE_EDIT_CONFLICT_IN_BATCH`。因此"对**同一个**计划先 Move 再 Remove"在权威事务语义下
   **不可表达**（与 Running 计划的情形一样，只需整批拒绝）。
   **修的是**：夹具前置 —— 第一条确认事务一次建**两个**计划（临时键 1 → Tick 3、临时键 2 → Tick 4），
   第二条确认事务做 `Move(A, 6)` + `Remove(B, CancelledByCommand)`。
   唯一被调整的夹具断言：`ActivePlans.Count` 由 1 改为 2（并新增"两个都仍 `IsEditable`"）。

- **为何四个都不算放宽**：三条用例名逐字未动；"只记最终 Operations + TargetTick +
  `ExpectedScheduleRevision`"这条口径的断言**一条没删**（Operations 数量/顺序/Kind/PlanId/
  RequestedStartTick/TerminationReason、`Is.SameAs(confirmed)`、修订号、窗口 ID、
  `SubmittedAtTick`、两条 Accepted 结果 + `CommandSequence {1,2}` + `ProducerOrdinal {1,2}`、
  未确认手势的临时键 9101–9103 不出现 —— 全部原样）。

---

## 4. 生产代码实现缺陷（我新写的 `ReplayAuthorityInput.cs`，共 2 个）

### 4.1 "处理器级拒绝的来源筛选"在类型上永远失效（**这是本轮最关键的发现**）

- **现象**：索引里三条命令解析出来源全是 `Player`（实测 `gwKeys=[1:controller.player|1:Player;
  2:controller.enemy_ai|1:Player;3:controller.system|1:Player]`），
  于是 AI/System 的 `CommandRejectedEvent` 也被折叠进 `CommandOutcomes`（实测 `outcomes=4`），
  `CommandOutcomes.Count` 断言拿到 4 而不是 1。
- **根因**：索引当初存的是 `ReplayAuthorityCommand`，而它的 `SourceKind` 是
  **恒为 `Player` 的只读派生属性**（`ReplayAuthorityCommand.SourceKind => CommandSourceKind.Player`）。
  用它当"按序号解析来源"的结果 ⇒ `command.SourceKind != Player` 这条判定恒为假。
  **"AI/System 不入权威输入"因此在运行时是纸面防线。**
- **修法**：索引元素类型改为私有的 `GatewayIssuedFact`（保存入口绑定的**真实**
  `CommandSourceKind`）；`ReplayAuthorityCommand` 保持"恒 Player"的权威输入不变量，
  继续只由 `RecordFrozenBatch` 产出。

### 4.2 非 Player 的 Envelope 无法登记 ⇒ 折叠时误判为"序号未知"

- **现象**：`REPLAY_AUTHORITY_UNKNOWN_COMMAND_SEQUENCE: 未在案的入口事实：controller.enemy_ai|1`。
- **根因**：登记用的"在案键"集合只由 `AppendCommand`（只收 Player）填充，
  于是 AI/System 的 Envelope 一律登记失败 —— 而它们的拒绝事件同样携带 `CommandSequence`，
  被折叠时无法与真正的"序号未知（驱动契约违规）"区分。
- **修法**：把两个概念拆开：
  - `_recordedCommands`：**权威 Player 事实**（唯一写入点是 `RecordFrozenBatch` → `AppendCommand`）；
  - `_allFrozenFactKeys`：**全部入口绑定事实**（含 AI/System）的规范键，只用于登记解析索引。
  只有前者决定"哪些事实进了权威回放输入"，只有后者决定"序号能不能解析出来源"。

---

## 5. 变异探针（5 个，逐个跑完并逐字还原）

每条探针只改一处，跑 `-Filter 'Task09ReplayInputTests'`，记录"红的恰好哪几条"，随后**逐字还原**。
原始 XML：`09-C1fix-m1.xml` … `09-C1fix-m5.xml`。

| # | 改动 | 红的恰好哪几条 | 还原后 |
|---|---|---|---|
| M1 | `RecordFrozenBatch`：把 `if (fact.SourceKind != CommandSourceKind.Player)` 改成 `if (false)`（不再排除 AI/System） | **仅** `ReplayAuthorityInputExcludesAiAndSystemRequests`（`Expected: 1 But was: 3`） | 3/3 绿 |
| M2 | `TryRegisterGatewayIssued`：把索引里的来源种类硬写成 `CommandSourceKind.Player`（= 复现 4.1 的真实缺陷） | **仅** `ReplayAuthorityInputExcludesAiAndSystemRequests`（`Expected: 1 But was: 3`） | 3/3 绿 |
| M3 | `RecordTickEvents`：删掉 `if (command.SourceKind != CommandSourceKind.Player) continue;` | **仅** `ReplayAuthorityInputExcludesAiAndSystemRequests`（`Expected: 1 But was: 3`） | 3/3 绿 |
| M4 | 夹具：第一条确认事务的攻击起点由 Tick 3 改回 Tick 1（计划在 Tick 2 前已被锁） | **仅** `ConfirmedScheduleEditRecordsOnlyFinalOperations`（`Expected: True But was: False`，即"两个计划仍可编辑"的前置） | 3/3 绿 |
| M5 | 夹具驱动器：跳过"记录被接受命令"那一步（`if (false && set != null)`） | `ReplayAuthorityInputExcludesAiAndSystemRequests`（`Expected: 1 But was: 0`）与 `ConfirmedScheduleEditRecordsOnlyFinalOperations`（`Expected: 2 But was: 0`） | 3/3 绿 |

- 还原后的总数：`total=3 passed=3 failed=0`；随后全量 `total=393 passed=393 failed=0`。
- **没有一条探针让全 3 条同时红**，即三条用例各自锚定不同的事实面（M4 只打 #45，M5 打 #41+#45，#44 无探针变红 —— 它的红是本轮最常见的"夹具构造缺陷"形态，见 §3.2）。

---

## 6. 关键契约（产出 11 的最终形态）

1. **"AI/System 不入权威输入"在类型上成立**：
   `AuthorityCommands` 的元素类型 `ReplayAuthorityCommand` **没有** `SourceKind` 字段可填，
   其 `SourceKind` 是只读派生属性且恒为 `Player`（用例断言 `CanWrite == false`）。
   记录"AI 权威输入"在类型上无法表达。
2. **"AI/System 不入权威输入"在运行时成立**（本轮修好的部分）：判定依据是
   **入口绑定事实的真实来源种类**（`GatewayIssuedFact.SourceKind`），不是
   "序号在不在索引里"，也不是恒 Player 的派生属性。两条事件族各自的门控：
   入口级拒绝读 `CommandIngressRejectedEvent.SourceKind`；
   处理器级拒绝读 `CommandSequence → GatewayIssuedFact.SourceKind` 解析结果。
3. **权威输入的唯一写入点**：`ReplayAuthorityInput.AuthorityCommands` 只在
   `RecordFrozenBatch → AppendCommand` 追加（唯一）。`RecordEnvelope` / `RecordAccepted` /
   `RecordTickEvents` / `RecordAuthorizerRejection` **都不写** `_commands`。
4. **结果事实的唯一性**：一条命令在一个 Tick 只产生**一个**最终处置
   （Accepted / IngressRejected / ProcessorRejected）。驱动器契约 =
   先 `RecordEnvelope`（登记序号→来源）→ 再 `RecordTickEvents`（折叠最终处置）→
   最后只对"本 Tick 未被拒绝"的 Envelope 调 `RecordAccepted`。
   双记会让同一条命令同时出现 `Accepted` 与 `ProcessorRejected`，故驱动器负责去重。

---

## 7. 改动范围

**生产（新写文件，本轮改实现）**
- `Assets/Scripts/Logic/Replay/ReplayAuthorityInput.cs`（466 行）
  - 新增私有 `GatewayIssuedFact`（入口绑定事实的**真实**来源种类 + 序号解析索引）
  - `_recordedCommands`（权威 Player 事实）与 `_allFrozenFactKeys`（全部入口事实）拆分
  - `RecordEnvelope` / `RecordAccepted` → 共用 `TryRegisterGatewayIssued` + `CanonicalKeyOf`
  - `RecordTickEvents` 的 `CommandRejectedEvent` 分支：按解析出的真实来源筛选
  - `ReplayAuthorityCodes.REPLAY_AUTHORITY_UNKNOWN_COMMAND_SEQUENCE` 的文档收敛为
    "序号在网关输出里也解析不出来"，明确它**不代表**"来源是 AI/System"

**测试（`Assets/Tests/EditMode/Logic/Task09ReplayInputTests.cs`，538 行）**
- 夹具：`SystemEntry(registry)` 取代 `RegisterSystemEntry(...)`；新增 `ConfirmedAddHeroAttack(...)`
- 夹具驱动器 `RecordAndStep`：登记 → 折叠 → 只对未被拒绝者记 Accepted
- 三条用例的**夹具前置**修正（详见 §3）；三条用例名逐字未改

**未触碰**：`BattleSimulation.cs`、`Logic/Commands/**`、`Logic/AI/**`、`Logic/Snapshots/**`、
`Core/**`、`UI/**`、`Assets/Tests/PlayMode/**`、`Assets/Tests/EditMode/{Authoring,UnityView}/**`
（`git status` 里这些路径的改动全部来自 A 流，与本轮无关）。
