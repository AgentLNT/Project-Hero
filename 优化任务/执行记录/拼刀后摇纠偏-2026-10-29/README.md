# 拼刀后摇纠偏（2026-10-29）

范围：[实施方案第 5.1 节](../../方案/战斗数值与AI实施验证方案-v0.1-2026-10-28.md#clash-recovery)。钝击多档后摇扫描属于第 5.2 节，仍待实施；本次不确定最终平衡值。

**状态：第 5.1 节拼刀后摇纠偏已完成并通过本轮验收。全量 EditMode 1027/1027，新增后摇回归 21/21；生产/Shadow 定向 14 项按最新逐项结果全部通过；79 场固定实验导出并逐场重放通过。** 汇总与源码匹配检查见[acceptance-audit.json](acceptance-audit.json)。

## 行为与实现

- 拼刀在命中 Tick 结束原攻击效果并关闭来源威胁，保留当前 Tick 冻结的结算事实；原直接攻击不再生成后续 Intent，残余只结算一次。
- 每个参与者的原计划保持 Running，Lane 和原 EndTick 不变；到期后才由统一终态协调器提交 `InterruptedByClash`。死亡或战斗结束仍可提前清理。
- 硬直、击倒及起身控制状态与后摇并行；控制不终止保留的后摇计划，动作阶段刷新不覆盖控制。恢复行动同时满足控制解除与原后摇结束。
- `ActionPlan.ClashResolvedAtTick` 默认 -1，进入活动快照、缓存失效检查、规范哈希及终态归档；HUD 预览的计划副本保留同一字段。玩家和 AI 继续使用原排程/反应入口。
- `ActionPlanTerminationRequestedEvent` 仍表示拼刀效果结束请求，不能当作 Lane 已释放；实际 `ActionPlanTerminatedEvent` 在原 EndTick 发出。未拼刀动作继续使用原自然完成与打断路径。

## 版本与锚点

| 项目 | 修复前 | 本次 |
|---|---|---|
| 生产规则 | battle-def-v4-defense-duration | battle-def-v5-clash-recovery |
| 生产定义哈希 | e04b3c0946ad3035 | de4be447760a2266 |
| 独立 v0.5 实验规则 | combat_values.v05.candidate.1 | combat_values.v05.candidate.1.clash-recovery.1 |
| ReplayFormat | 6 | 7 |
| 非生产装配初始快照哈希 | 2046e404a6fb708f | 5f18e7c81833be75 |
| 生产装配初始快照哈希 | 63e751856fe458e8 | f9ae2c5d11ebf2d0 |

主战斗锚点由真实 Authoring 构建与重复构建验证，初始条件为既有任务 03 的 RNG 20260922、局外资源 10。格式 7 增加计划字段；旧格式 6 明确拒绝，不用新实现重现旧缺陷。公式、招式强度及本轮生产资产参数没有随纠偏切换。

Shadow 的 v5 策略保留原 R2/R3 的 11 项登记，并增加 `plans[i].clashResolvedAtTick` 的独立规则义务；临时字段共 74 项，不将其伪装为旧/新相等。Logic 对 Logic 比较同步检查该字段。历史 v4 的 73 项分类记录保留。

## 已验证范围

| 验证 | 证据与结论 |
|---|---|
| Unity 全量 EditMode | [unity-editmode-complete.xml](unity-editmode-complete.xml)：1027/1027，通过 |
| 新增后摇回归 | 上述原生报告中的 `ClashRecoveryTests`：21/21，通过 |
| 生产/Shadow 定向 PlayMode | [首次报告](unity-production-playmode.xml)：13/14 通过，冷启动集成项触及默认 180 秒上限；保留所有断言并显式设置 360 秒后，[单项补验](unity-production-cold-retry.xml) 1/1 通过（约 214.5 秒）；最新逐项覆盖 14/14 |
| 固定实验导出及逐场回放 | [本轮汇总](experiments/run-20261029-085542Z/summary.csv)：79 场均完成导出和序列化后逐 Tick 重放；64 场 victory，15 场到观察时限，0 命令拒绝、0 缺失产物 |

21 项新增用例覆盖胜/负/等量零残余、最小合法 1 Tick 后摇、双人 30/45 Tick 与三方 30/45/60 Tick 各自截止、Attack/Move/Guard 的排程与启动边界、伤害收入支付的 Block/Dodge、同 Tick 硬直/击倒及后续控制、窗口末尾/跨窗口、死亡和战斗结束、字段哈希及旧格式拒绝。使用正常生产窗口与注册命令入口构造拼刀；控制扩展点仅用于后续控制和死亡清理的隔离用例，其余输入可从 Tick 0 序列化重放。

既有多方拼刀和终态用例已改为检查“先保留占用、到期后清空”，保留终态唯一性、残余伤害归属及非拼刀回归。旧测试方法名保留，供历史证据引用。

生产冷启动补验包含四次全新 New 战斗、真实 Legacy 对照、100 次完整文件重放，以及一次结束反馈、结束后幂等与旧调度器零推进断言；只增加该集成测试的执行时限，没有降低检查次数或改变战斗规则。全量 EditMode 通过后，仅对此 PlayMode 测试的超时标注做了调整，逻辑实现未再改动。

## 固定拼刀的首次行为差异

对比原批次 `数值AI-2026-10-28/run-20261028-094904Z` 的 `script_Clash` 与本轮同名输入：首次拼刀均在 Tick 212，原实现当 Tick 就发出两条计划终止事件；本次 Tick 212 只结束攻击效果并发出请求，两个计划各自到原 EndTick=242 才终止。首次残余伤害仍为 4300 HealthQ10，原直接攻击不重扣。该差异由后摇纠偏产生，没有额外增加钝击后摇。

规则/格式已变化，规范哈希从初始状态就不同；上述 Tick 212 指首次生命周期行为差异，不是首次哈希差异。各场 `.input.json`、`.heroReplay`、`.events.jsonl`、`.decisions.jsonl`、`.damage-income.csv` 与定义哈希均保存在本轮独立目录。[源码清单](source-manifest.json)记录 Scripts、Diagnostics 与 Tests 的逐文件 SHA-256；批次源码指纹 `a094076f92a26c1c0551cefa823dd76b79179e7b48f287d9200bf10294411071` 与最终 Scripts 源码匹配。

测试生成的 23 份旧路径输出已另存到 `regression-artifacts/`，旧文件恢复为执行前的 Git 内容；诊断场景生成变化已恢复，原数值批次及其 latest 指针保留。详见[产物保全记录](artifact-preservation.json)。历史证据没有改写为新版本结果。

## 复现与限制

```powershell
unity test . --editor-version 6000.6.2f1 --mode EditMode --filter ClashRecoveryTests --output "优化任务/执行记录/拼刀后摇纠偏-2026-10-29/unity-clash-rerun.xml" --timeout 300 --format json
unity test . --editor-version 6000.6.2f1 --mode EditMode --output "优化任务/执行记录/拼刀后摇纠偏-2026-10-29/unity-editmode-rerun.xml" --timeout 600 --format json
```

Unity 6000.6.2f1、CLI 1.0.0-beta.13。沙箱内首次测试因本地 HttpListener/套接字限制触发 Unity 原生崩溃；后续原生运行使用沙箱外执行。CLI 提示 beta.14 可用，本轮未升级。

过程报告保留早期失败及当轮源码范围，最终 EditMode 结论以 `unity-editmode-complete.xml` 为准。全量 PlayMode、正式性能/分配/GC、完整 Player 构建、人工操作及钝击平衡不在本次验收范围。
