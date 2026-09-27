# 任务 07 子代理实施记录：ScheduleEditor / ActionPlanCommandProcessor 预算接入

- 归属：任务 07「必须产出」4（统一 ScheduleEdit 预算事务）、12（两类资源参与者），
  以及「原子性与关闭语义」中"计划、Lane、Timing、路径、空间 Reservation 和预算变化
  要么全部提交，要么全部回滚"。
- 文件所有权：**只**改了
  `Assets/Scripts/Logic/Timeline/ScheduleEditor.cs`（+424 / -15）与
  `Assets/Scripts/Logic/Timeline/ActionPlanCommandProcessor.cs`（+44 / -4，其中 8 行为父代理
  并行的 `WindowCommandHandler`，本子代理只贡献 expectedWindowId/issuer 转发与 `using Ids`）。
- 未改动（按所有权要求）：`Simulation/**`、`Turns/**`、`Resources/**`、`Snapshots/**`、`Tests/**`。
- 临时验证脚手架（不属交付物，可随时删除）：
  `优化任务/执行记录/07-sub-verify-logic.ps1`。

---

## 1. 改动清单

### 1.1 `ScheduleEditor.cs`

| # | 位置 | 改动 |
|---|---|---|
| 1 | usings | 新增 `ProjectHero.Logic.Resources`、`ProjectHero.Logic.Turns` |
| 2 | `BudgetContextFactory`（新属性） | `Func<ResourceChangeSource, long, WindowId?, ControllerId, ScheduleBudgetContext>`；null/未设置 ⇒ 不建模预算（任务 05/06 语义） |
| 3 | `Apply(...)` | 追加可选尾参 `preview`、`expectedWindowId`、`issuer`，透传给 `ApplyInternal`（源兼容：既有调用点全部为 4~5 参） |
| 4 | `ApplyInternal(...)` 头部 | 纯校验前取一次 `ScheduleBudgetContext`，并把 `CurrentWindowId` 缓存为 `currentWindowId` |
| 5 | `ApplyInternal` 阶段 1 | `ResolveAdd(..., currentWindowId)`：新计划把当前窗口写进 `SubmittedWindowId`（预算来源与审计） |
| 6 | `ApplyInternal` 阶段 7 捕获段 | 求值前捕获 `oldReserved`（求值覆盖的普通计划 + Add 候选 + Remove 候选），并同时建立 `projectionSnapshots` |
| 7 | `ApplyInternal` 预算校验段 | `ValidateSubmissionAuthority`（**仅** `Source == ExplicitScheduleEdit`）→ `ValidateBudget`；任一非 null ⇒ `RestoreProjections` + `Rejected(code, baseRevision)` |
| 8 | `ApplyInternal` 空间端口段 | 保持原位（校验之后、注册表/Lane 之前）；失败时 `RestoreProjections` + 整批拒绝（此时预算尚未应用，无需回滚） |
| 9 | `ApplyInternal` 预算应用段 | 空间成功后立即 `ApplyBudget`；其异常 = 不变量错误 ⇒ 恢复投影并**重抛** |
| 10 | `ApplyInternal` 写入段 | `ReserveActionPlanId` / `RegisterPlan` / `RemoveFromLane` / `CommitLaneProjections` / `IncrementRevision` 包进 try/catch；捕获时 `RollbackBudget` + `RestoreProjections` + **重抛原异常** |
| 11 | `ApplyInternal` 成功尾部 | Remove 的普通计划 `ReservedTurnBudgetTicks = 0`（计划字段与账本一致） |
| 12 | `ApplySystemAutoDeferral` | 新增 `SystemAutoDeferral` 上下文（`expectedWindowId = null`、`issuer = default`）、差额候选、校验失败/空间失败/应用失败的全部局部回滚；成功保持 Editable + Reserved，绝不产生 `ConsumedAtLock` |
| 13 | 新增私有成员 | `BuildBudgetChanges`、`CompareBudgetChanges`、`ExpectedReservedOf`、`WindowIdOf`、`RestoreProjections` |

### 1.2 `ActionPlanCommandProcessor.cs`

| # | 位置 | 改动 |
|---|---|---|
| 1 | usings | 新增 `ProjectHero.Logic.Ids`（`WindowId`） |
| 2 | `ProcessOrdered` 的 `ScheduleEditPayload` 分支 | `Apply(..., preview: false, expectedWindowId: ExpectedWindowIdOf(scope), issuer: envelope.ControllerId)` |
| 3 | 新增 `ExpectedWindowIdOf(CommandScope)` | scope 判别不匹配 ⇒ `null`（"没有声明窗口"），**不**回退到当前窗口；同一条命令的 `ExpectedScheduleRevision` 已是哨兵值，必先以 `STALE_SCHEDULE_REVISION` 稳定拒绝 |

### 1.3 冻结契约的逐条落点

- Add 候选：`IsNewReservation = true`、`WindowId = CurrentWindowId`（null ⇒ 无效窗口 ⇒
  端口以 `WINDOW_BUDGET_SOURCE_CLOSED_OR_MISMATCH` 整批拒绝）、`NewCost = Delta = BudgetCostTicks`、
  **`ExpectedReserved = 0`**、`Kind = Reserved`。
- 既有普通计划且成本变化：`WindowId = SubmittedWindowId`（null ⇒ 整批拒绝，**绝不**回退到当前窗口）、
  `Delta = 新成本 - 捕获的旧预留`、`ExpectedReserved = 旧预留`、`Kind = ReservationAdjusted`。
- Remove 且旧预留 > 0：`WindowId = SubmittedWindowId`（null ⇒ 跳过释放并继续）、
  `Delta = -旧预留`、`Kind = ReleasedBeforeLock`。
- 反应计划一律不参与窗口预算（`IsOrdinary` 判定，`IsReaction` 天然被排除）。
- 候选按 `ActionPlanId` 升序稳定排序（平局再按窗口、类别），提交顺序与操作/求值/字典枚举顺序无关。
- 预览：`preview == true` 时**不**调用 ValidateSubmissionAuthority / ValidateBudget / ApplyBudget /
  RollbackBudget，且 `CurrentWindowId` 不作为写入使用。

---

## 2. "整批回滚零局部写入"的论证

失败点按顺序覆盖：

| 失败点 | 账本侧 | 排程投影 | 注册表 / Lane / 修订号 | 空间侧 |
|---|---|---|---|---|
| 校验拒绝（授权/预算） | 未写 | `projectionSnapshots.Restore()` | 未动 | 未动（端口尚未调用） |
| 空间端口失败 | 未写（应用在空间成功之后） | 编辑器恢复；端口自恢复自己的空间写入 | 未动 | 端口契约：自身零局部写入 |
| `ApplyBudget` 抛异常 | 不变量错误；接口契约要求其自身原子 | 恢复 | 未动 | 已提交（无法撤销，故预算校验被前置到端口之前） |
| 写入段异常 | `RollbackBudget` | 恢复 | 部分写入由异常上抛暴露（不吞） | 已提交 |

关键设计取舍（必须由父代理/验证者复核）：
空间端口一旦成功**无法撤销空间侧**，因此 `ValidateBudget` 被显式放在端口调用**之前**；
这与任务书第 3 段"第 6 条：空间端口成功后立即应用预算"完全一致，但意味着
"先空间成功、再预算拒绝"这一顺序在结构上不可能出现。

---

## 3. 验证证据

### 3.1 离线编译（Unity 自带 Roslyn，与 07 脚手架同一引用集）

```
& '优化任务\执行记录\07-sub-verify-logic.ps1' -Exclude @()
Logic sources: 103 total, 0 excluded, 103 compiled
SUBCHECK COMPILE: OK
```

说明：`ProjectHero.Logic` **全量 103 个源文件**（含父代理正在补写的 `BattleSimulation.cs`）
在当前工作树上编译通过。该脚本不写回 `Assets/**`。

### 3.2 离线 NUnit（Mono + Unity nunit.framework，同一套用例）

```
& '优化任务\执行记录\07-sub-verify-logic.ps1' -Exclude @() -RunTests
test sources: 15 compiled
total=183 passed=183 failed=0 skipped=0 inconclusive=0
```

包含 `Task07TurnWindowAndBudgetTests`（36 条）与全部既有 147 条 Logic 用例。
> 注：2026 轮次中曾出现 `OldCycleReservationSurvivesToTriggerButCannotRefundIntoNewCycle` 失败，
> 重跑即通过——那是同批代理正在重写该测试文件造成的读写竞争，不是源码缺陷。

### 3.3 独立冒烟程序（%TEMP%，不入库）

`%TEMP%\t07-sub\SubAgentBudgetSmoke.cs`：用公开 API 装配
`ScheduleEditor + TurnWindowManager + TurnWindowBudgetAuthority + 提交授权` 的真实接缝，
覆盖 7 个场景 62 条断言，**62/62 通过**：

1. `Add` 从当前窗口预留、`SubmittedWindowId = 当前窗口`、计划字段与账本一致、Spent 恒为 0；
2. 不受控单位 / 非拥有者且无并发授权 / 过期 `ExpectedWindowId` 三种整批拒绝且零局部写入；
3. `Remove` 恰好释放一次，回到原窗口账本，修订号恰 +1；
4. `Available` 不足 ⇒ `WINDOW_INSUFFICIENT_BUDGET` 整批拒绝，账本/修订号/注册表不变；
5. 系统自动延期：保持 Editable + Reserved、+1 修订号、不覆盖 `LastRequestedStartTick`、
   在"发行者没有任何提交授权"时仍然成功（证明系统延期不走授权校验）；
6. 成本上升不得从非当前来源窗口追加（`BUDGET_SOURCE_CLOSED_OR_MISMATCH`）；
   对已关闭窗口的释放合法且不重开、不转移；
7. 预览零写入（预算/注册表/Lane/修订号全不变）。

### 3.4 未覆盖

- Unity EditMode / PlayMode 权威套件未运行（本子代理无 Editor 会话；工作树在父代理完工前
  也不满足 Unity 全量编译）。
- `Task07TurnWindowAndBudgetTests` 只覆盖预算权威与窗口管理，**不覆盖**
  `ScheduleEditor` 的预算接入；上述冒烟程序是目前唯一的端到端证据，且它不是仓库测试。
