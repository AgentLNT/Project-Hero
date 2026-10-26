# 任务 07 裁定 B「排队 Move 链」修复：证据包

范围：`Assets/Scripts/Logic/Grid/LogicGrid.cs` 与
`Assets/Scripts/Logic/Movement/LogicGridMovementAuthority.cs` 两个文件。
本目录**不在 Unity 仓库内**（位于仓库的父目录），因此不参与 Unity 编译、不进入 git。

## 1. 结论

| 阶段 | 产物 | 结果 |
| --- | --- | --- |
| 修复前（真实树，测试作者刚加入 (B) 回程链段时） | `results/t07-now.xml` | 目标用例红：`rejection=LOGIC_GRID_RESERVED_BY_OTHER` |
| 仅"批级释放"（HEAD 占用判定 + 本修复的权威层改动） | `results/t07-frozen-results.xml` | 222/223，**唯一红**就是该目标用例 ⇒ 证明"只做批级释放不够" |
| 最终修复（批级释放 + 同单位不相交时间窗共存） | `results/t07-chain-fix.xml` | **223/223 全绿** |
| 最终修复下的环形围栏探针 | `results/t07-probe-final.xml` | 13/13 |
| 最终修复下的调度层探针（含仓库测试副本） | `results/t07-rigprobe-fixed.xml` | 8/8 |
| 同一批探针跑在 HEAD 占用判定上 | `results/t07-rigprobe-frozen.xml` | 6/8（差异仅两处，见下） |

`results/t07-rigprobe-frozen.xml` 与 `t07-rigprobe-fixed.xml` 的逐用例差异：

| 用例 | HEAD 占用判定 | 最终修复 |
| --- | --- | --- |
| `QueuedMoveChainCommitsBothPlansThroughRealSpacePort` | Failed | **Passed** |
| `ProbeReentrantMoveAfterRunningPredecessorIsAccepted` | Failed | **Passed** |
| `ProbeSameUnitDuplicateDestinationSecondTransactionIsRejected`（裁决围栏） | Passed | **Passed（逐字相同）** |
| `QueuedMoveChainOriginMismatchIsStillRejectedForNonChainedStart`（起点负控制） | Passed | **Passed（逐字相同）** |
| 其余 4 条 Dodge 容忍用例 | Passed | Passed |

## 2. 探针说明

`probes/GridReservationProbeTests.cs`（8 条，网格层）
1. 同单位 + 同格 + 同一时间窗 ⇒ 拒绝；
2. 同单位 + 时间窗重叠（含 [9,10) 与 [0,10) 的半开区间相邻）⇒ 拒绝；
3. **其他单位 + 时间窗完全不相交 ⇒ 仍然拒绝**（"他人的占位照旧阻挡"）；
4. 同单位 + 同格 + 不相交 ⇒ 共存；`ReservationAt` 给最晚释放边界；释放顺序不影响读数；
5. 倒序建立（回滚重放）同样共存；
6. 启动门禁提示的释放边界取最晚（保守，不会提示 10 而让门禁过早重试）；
7. `ApplyBatchRelocation` 的"待抢占预留必须已清理"预检在多持有者下仍然失败；
8. 既有预留错误码（非法区间 / 重复键 / null）逐字不变。

`probes/AuthorityReservationProbeTests.cs`（5 条，权威层）
回程链两条计划都提交成功且段/预留完整；同格重叠复用被拒且零局部写入；跨单位不相交复用仍被拒；
同单位不相交跨事务复用放行；`expectedOrigin` 缺失/错误的起点校验仍然拒绝。

`probes/ScratchRigProbeTests.cs`（`Task07QueueAndReactionLaneTests.cs` 的 TEMP 副本，类名已改）
额外两条调度层探针：
* `ProbeSameUnitDuplicateDestinationSecondTransactionIsRejected`：复现 Authoring 用例
  `Task06ScheduleSpaceTests.ReservationConflictWinnerFollowsCanonicalCommandOrder` 的机制
  （同单位、同目的格的第二条事务）——**修复前后同为 `LOGIC_GRID_RESERVED_BY_OTHER`**；
* `ProbeReentrantMoveAfterRunningPredecessorIsAccepted`：前序计划已 Locked/Running（不再参与本批次重建）时，
  后一条重新经过 (0,2) 也必须能提交——这是"同单位不相交时间窗共存"相对"仅同批次白名单"的必要性证据。

`probes/LogicGrid.HEAD.cs`：`git show HEAD:Assets/Scripts/Logic/Grid/LogicGrid.cs`
（修复前的"一格一持有者"占用判定），用于构建对照程序集。

## 3. 复现命令（Windows PowerShell 5.1）

```powershell
# 正式验收：全量纯 Logic 套件
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& '.\优化任务\执行记录\07-offline-harness.ps1' -ProjectRoot 'C:\Users\1\repos\Project Hero\Project-Hero' `
    -ResultsFile "$env:TEMP\t07-chain-fix.xml"
# 目标类
& '.\优化任务\执行记录\07-offline-harness.ps1' -ProjectRoot 'C:\Users\1\repos\Project Hero\Project-Hero' `
    -ResultsFile "$env:TEMP\t07-chain-filtered.xml" -Filter 'Task07QueueAndReactionLaneTests'
# Authoring 证据链（只能离线编译；执行需要 Unity Test Runner）
& '.\优化任务\执行记录\07-offline-harness.ps1' -ProjectRoot 'C:\Users\1\repos\Project Hero\Project-Hero' `
    -ResultsFile "$env:TEMP\t07-authoring.xml" -AuthoringCompile -CompileOnly
```

探针两个程序集按 harness 的同一套 Roslyn/Mono 参数编译（见 `%TEMP%\t07-probe\probe.rsp`、
`%TEMP%\t07-rigprobe\fixed.rsp`），运行：

```powershell
mono "$env:TEMP\t07-harness\T07Runner.exe" "$env:TEMP\t07-harness\T07Probe.dll" out.xml 'ProbeTests'
mono "$env:TEMP\t07-harness\T07Runner.exe" "$env:TEMP\t07-harness\T07RigProbe.dll" out.xml 'ScratchRigProbeTests'
# 对照（HEAD 占用判定）：把 ProjectHero.Logic.dll 换成用 probes/LogicGrid.HEAD.cs 构建的那个程序集
```

## 4. 未能验证的部分（诚实清单）

* `Assets/Tests/EditMode/Authoring/Task06/*`（含 `Task06ScheduleSpaceTests`、
  `Task06CommitTerminalSemanticsTests`）**无法离线执行**：夹具经
  `LegacyAssetResolver → UnityEngine.Resources.LoadAll` 取资产，Mono 下必然
  `MissingMethodException`（已实测，见提交说明）。本修复对它们只有"离线编译通过"这一层证据，
  外加"同单位同目的格第二条事务仍被拒"的调度层等价探针。
* `ReservationAt` 在多持有者格上取"最晚释放"的那条。因为共存只对**同一单位**开放，
  所有涉及"他人预留"的读路径（寻路、启动门禁、Dodge 目的格、强制位移预检）在该格上
  仍然只有唯一持有者，读数与冻结版本一致；这一点是推理 + 探针（③⑥⑦）共同支撑的，
  没有 Authoring 侧的端到端执行证据。
