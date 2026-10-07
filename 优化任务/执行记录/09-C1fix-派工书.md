# 任务 09 · C1-fix 轮派工书（把在飞的 3 条回放用例做成真绿）

> 主理人下发。执行者：C 流。你上一轮（C1）**中途异常结束**，留下了可编译但失败的半成品。
> 本轮 = C1-fix，**只做这一件事**：把 3 条在飞用例做成"真的通过"，不许放宽断言。

## 0. 当前实测状态（主理人复现，证据 `%TEMP%\t09-b2\editmode-full.xml`）

```
ProjectHero.Logic.Tests.Task09ReplayInputTests.ConfirmedScheduleEditRecordsOnlyFinalOperations
    前提：时间线上存在一个可编辑普通计划
    Expected: 1
    But was:  0

ProjectHero.Logic.Tests.Task09ReplayInputTests.ReplayAuthorityInputExcludesAiAndSystemRequests
    System.Reflection.TargetInvocationException : Exception has been thrown by the target of an invocation.
    ----> ProjectHero.Logic.LogicDefinitionException : COMMAND_INGRESS_DUPLICATE_CONTROLLER: controller.system

ProjectHero.Logic.Tests.Task09ReplayInputTests.UncommittedDragTrajectoryIsAbsentFromReplayInput
    Expected: 0
    But was:  1
```

已存在的在飞产物（都在你的所有权内）：
- `Assets/Scripts/Logic/Replay/ReplayAuthorityInput.cs`（新增生产文件）
- `Assets/Tests/EditMode/Logic/Task09ReplayInputTests.cs`（33 KB，含 `Task09ReplayFixture` 与 3 条用例）

## 1. 诊断线索（主理人初步判断，供你验证而不是照抄）

1. `COMMAND_INGRESS_DUPLICATE_CONTROLLER: controller.system`：`BattleSimulation` 自己已经注册过 System 入口（`BattleSimulation.SystemControllerId`），你的夹具又用反射 `RegisterSystemEntry` 注册了同一个 `ControllerId` ⇒ 重复注册抛异常。**先查清生产代码里 System 入口到底由谁注册、`SubmitSystemInternal` 该拿哪个 entry**，再决定夹具怎么取用（不要绕过重复注册守卫）。
2. `ConfirmedScheduleEditRecordsOnlyFinalOperations` 的"前提：时间线上存在一个可编辑普通计划 Expected: 1 But was: 0"：夹具**没有真的建出**可编辑计划（前置断言自己红了）。请假手"先 Add 一条普通计划并经入口提交 → 推进到下一 Tick → 回到做编辑的那个 Tick"这条链，确认每一步的返回值与拒绝码。
3. `UncommittedDragTrajectoryIsAbsentFromReplayInput` "Expected: 0 But was: 1"：未确认手势**进了**权威输入，或者断言方向反了。先确认权威输入记录的**唯一写入点**在哪、写的是哪一类事实。

## 2. 硬约束

1. **不许放宽/改写断言来变绿**。允许修的是：夹具前置、测试自身的构造缺陷、以及你新写的生产代码 `ReplayAuthorityInput.cs` 的实现缺陷。
2. 若你判断某条用例的**断言口径本身**与任务包冲突（`优化任务\09-命令AI与输入.md`「必须产出」11 + `09-接口冻结与集成清单.md` §5 R-4），必须在回报里引原文论证，**不得静默改断言**。
3. 三条用例名**逐字不变**：
   - `ReplayAuthorityInputExcludesAiAndSystemRequests`
   - `UncommittedDragTrajectoryIsAbsentFromReplayInput`
   - `ConfirmedScheduleEditRecordsOnlyFinalOperations`
4. 所有权（独占）：`Assets/Scripts/Logic/Replay/**`、`Assets/Scripts/Logic/Determinism/ReplayFormatVersion.cs`、`Assets/Tests/EditMode/Logic/Task09Replay*Tests.cs`。
   **禁区**：`Assets/Scripts/Logic/Simulation/BattleSimulation.cs`（A 流正在改）、`Logic/Commands/**`、`Logic/AI/**`、`Logic/Snapshots/**`、`Core/**`、`UI/**`、`Assets/Tests/PlayMode/**`、`Assets/Tests/EditMode/{Authoring,UnityView}/**`。
   若确诊必须改 `BattleSimulation.cs`：**停下**，把最小补丁（精确到行）写进回报，由主理人落。
5. 并行提醒：A 流正在修 3 条 `Authoring.Tests.Task05` 回归，可能临时弄脏 `Logic/Commands/**` 与 `BattleSimulation.cs`。离线编译若报错在这些路径，**等一下重跑，不要替他修**。

## 3. 验证

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& '.\优化任务\执行记录\07-offline-harness.ps1' -ProjectRoot 'C:\Users\1\repos\Project Hero\Project-Hero' -OutDir "$env:TEMP\t09-c1fix" -ResultsFile "C:\Users\1\repos\Project Hero\Project-Hero\优化任务\执行记录\09-C1-logic-results.xml"
# 单类：追加 -Filter 'Task09ReplayInputTests'
```
**不要跑 Unity**（主理人统一跑；A 流本轮要跑 Unity）。收口时给出离线全量 `total=/passed=/failed=` 与 `strictExact / 63`（正则 `^\s*public\s+(void|async\s+void|IEnumerator)\s+<用例名>\s*\(`，扫 `Assets/Tests/**`）。

## 4. 纪律

**禁止用 PowerShell `Set-Content`/`Out-File` 改写 `.cs`**（只用文件工具）；变异探针逐字还原；`.cs`/`.cs.meta` 成对；不得 git commit；若遇工具/环境错误**不要静默结束**——把已完成文件、最后一条成功命令与错误原文写进回报（哪怕结论只是「阻塞」）。

## 5. 回报格式（中文，末尾不要客套）

```
任务：09 - C1-fix
结果：完成 / 部分完成 / 阻塞
根因（逐条）：
- 每条红：真正根因 + 修的是夹具/测试/生产代码 + 为什么这样修不算放宽断言
已实现：
- 文件 → 改动摘要（精确到签名）
关键契约：
- （产出 11 的最终形态：「AI/System 不入权威输入」如何**在类型/结构上**成立；权威输入的写入点唯一性）
验证：
- 离线全量：实际 total=/passed=/failed=
- 三条用例逐条：名称 → 通过/失败 + "哪种实现缺陷会让它红"
- 变异探针：编号 → 改动 → 红的恰好哪几条 → 还原后总数
- strictExact / 63
改动范围：文件清单
未完成或未验证：
对 C2 / 主理人的约束：
- （Shadow 请求镜像需要的读取面与镜像点；`ShadowCasePolicy`/`ShadowComparisonDetector` 的切入点）
```
