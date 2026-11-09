# 阶段四：后摇与规则策略首轮对照（2026-11-03）

**首轮 304 场及 96 场纠偏回归均已通过独立进程审计（2026-11-04）。** [任务 15](../../15-阶段四后摇与规则策略对照.md)。V1 共 336,956 Tick，V2 共 108,796 Tick；最终逻辑检查 1099/1099，生产/Shadow 场景检查 14/14。读出时发现 AI 预演与真实命令提交有差异，因此封存 V1，另存 [V2 修复及回归](corrected-preview-v2/README.md)。修复消除了 5 场里的 40 个启动失败草稿，同名场次的实际出手、命中及终局没有改变。正式后摇值尚未选定。

**2026-11-04 用户语义纠正：**反击时机表示敌方命中自身之后，我方尽早命中；所有命中沿用普通规则。此前按目标某一动作阶段内命中与否作出补场、奖励及定值判断的解释撤回，见[纠正记录](semantic-correction-2026-11-04/README.md)。原始战斗数据和审计保留。

本轮固定的是策略版本、参数和输入，不是双方动作序列。早期 6572 场由固定脚本驱动；阶段三加入三种规则决策器；本轮 304 场为双方规则策略或旧 AI 对战，96 场为修复后的规则策略对战。正式定义默认旧 AI，尚未切换到三种实验策略；这些策略也尚无专门的“受击后尽早命中”候选。

## 固定内容与实验输入

304 场使用阶段三 `tactical-public-kernel-v1`，原有 169 个 Logic 源文件逐文件冻结校验；当时只增加实验定义、诊断和测试。`frozen-existing-logic.json` 记录原路径及 SHA-256；每场输入包含冻结摘要、完整源码指纹、定义哈希及策略/场景版本。原始 C# 及其元数据已封存于 `source-archive.zip`。当前工作区已进入 V2，不能用当前版本重放 V1 并声称输入相同；分析脚本会验证封存源码摘要。

规则/数值版本为 `combat_values.v05.candidate.1.clash-recovery.1`，场景版本为 `recovery_policy_long_matches.1`。主矩阵源码指纹为 `0685e4cb26ca75d7106dc35b2aa50998b1509e743257669706c7d8882b1a8616`，原有 Logic 冻结摘要为 `0ec39c04178cd5ea518b5981d1f6e273c808cff1f4a68b65a1caa66216d170fc`。

核心档位 **30/31/32/39/40/41**。保留场景另比较 **31/40/42/43/44/58/59/60**。每个匹配块固定策略、地图、先手、出生位置、种子和窗口设置，仅改变钝击后摇；挥砍/穿刺仍为 30。双方基础属性相同，24 点生命，共享 Guard/Move/Block/Dodge；攻击使用原 v0.5 合成实验载荷及整数几何。

本轮仅覆盖基础属性档。合成攻击采用各朝向相同的对称覆盖；正式定向攻击的前后方差异、装备成长和人工手感需要后续验证。2 对 2 使用当前 AI 的逐窗口普通动作和公开反应，没有另外加入协同集火或主动并发授权策略。

双方力量/敏捷/体质均为 1，装备生命加成为 0，稳定度 10，行动速度及移动速度均为 20。钝击方在探针场使用 30 Tick 前摇的钝击探针，在重击场使用 90 Tick 前摇的重击；对手分别使用 30 Tick 前摇的挥砍探针/快攻或 60 Tick 前摇的范围挥砍。每次实际解析的起点、命中及后摇截止保存在 `plans.csv`。核心起点为左右两列 X=0/6，2 对 2 两行 Y=0/6；对角保留场为 (0,0)/(10,4)，错列保留场为 X=0/10、行距 8、右列 Y 再加 4；站位交换独立翻转左右，先手通过槽位顺序独立交换。

完整矩阵见运行时生成的 `matrix.csv`：

| 部分 | 内容 | 场次 |
|---|---|---:|
| 核心 | 8 组场景/策略×双先手×双站位×6 档 | 192 |
| 旧 AI 固定版本对照 | 三种新策略对 Legacy，双先手/站位，31/40 | 24 |
| 保留布局 | 对角单挑、错列 2 对 2，双先手/站位，8 档 | 64 |
| 随机对手种子复核 | 固定旧对手增加种子 97，双先手/站位，31/40 | 8 |
| 窗口敏感性 | 179/181 Tick 窗口，双先手/站位，31/40 | 16 |

普通单挑上限为 6 个窗口（通常 1080 Tick），2 对 2 为 8 个窗口（1440 Tick），真实胜负发生后结束。窗口敏感性组同时影响窗口时长及思考相位，不能解释成单独的相位效应；每块内保持窗口设置相同。旧对手随机选择使用固定种子 31/97，其余首轮场次使用 31。

## 已完成预检

- 6 场长局预检通过，包含单挑、墙边和 2 对 2；生命与可用资源守恒复核通过。看到多次出手、资源消费及跨窗口循环。
- 补充 2 场主动对齐拼刀预检通过。31/40 均发生真实拼刀，部分时序相差 6 Tick，未据此选定参数。
- 新增 16 项测试，最终全量 EditMode **1096/1096**；[full-editmode.xml](full-editmode.xml)。包含匹配块唯一变化、先手/站位独立交换、长局全量决策捕获、生命和资源守恒，以及两份独立并行世界的完整回放逐字节一致。

八个确定性批次分别保存完成回执。每个独立世界内仍按原顺序逐 Tick 推进，最多并行四个独立场次；每场另建世界重新生成 AI 并比较事件/快照。JSON DTO 各自独立，后台 JSON 支持依据 [Unity JSON serialization 文档](https://docs.unity.com/en-us/engine/6000.0/manual/programming-environment/code-reload-serialization/script-serialization/json-serialization)。本轮不修改场景、渲染或运行时并发调度。

## 产物与指标

### 首轮普通交互结果

完整分组见 [analysis-report.md](analysis-report.md)、[summary.csv](summary.csv) 和 [31/40 匹配块](paired-31-vs-40.csv)。64 个匹配块中，51 个最终生命差相同、8 个在 40 Tick 时提高、5 个降低；3 个胜负类别变化全部来自固定 Legacy 对手。核心新策略的 32 个匹配块没有胜负翻转，不能把混合胜率当成 31/40 的优劣顺序。

272 场新策略互战没有命令拒绝；32 场固定 Legacy 对手共 10,690 次拒绝，单独报告。304 场中 147 场达到时限；这表示本轮时限内未分胜负，不能直接判为死循环。墙边重击/快攻存在相邻档差异：固定输入下 30 超时，31/32 获胜于 937，39/40/41 获胜于 943；其余先手/站位没有同样结果。

共有 18 次拼刀（36 个参与者），已经启动的前摇没有被控制取消。另有 40 个攻击草稿因 `ACTOR_UNAVAILABLE_AT_START` 在启动前终止，全部出现在墙边重击/快攻组。按目标所处动作阶段导出的命中与生命分项仅用于解释普通交互，不构成单独成功条件；此前据该分项为零认定需要补特定惩罚场景的结论撤回。

其中一个反例：222 Tick 的 AI 预演认为下一 Tick 提交的快攻可造成 4 点伤害；真实 223 Tick 提交后，单位仍被控制至 247，计划立即终止。旧预演直接写入计划，绕过真实命令批次的占用标记，错误允许了自动延期。同一路径还在窗口重置资源之前提前接纳反应。定位及原始时序见 [preview-start-gate-counterexample.json](preview-start-gate-counterexample.json)。实际战斗的启动校验正常；此次修复调整预演路径，并将策略版本提升至 V2。

普通出手序列的一个例子：开阔地“持续进攻/寻找打断”、先手 A、未交换站位的一组，31 档拼刀于 247，快攻下一次开始 277、命中 307；40 档拼刀于 253，快攻下一次开始 283、命中 313。有效性取决于真实接触、对方应对、伤害和资源结果，目标动作何时结束不构成独立判断边界。全部拼刀参与者的后续出手见 [post-clash-followups.csv](post-clash-followups.csv)。

例如开阔地重击/快攻的一组 31/40 对照，40 让钝击方多保留 4 点生命，来自窗口内快攻由三次变两次。这是普通攻击序列改变产生的实际换血差异，按完整出手时间与生命结果解释。

每场保存 `.input.json`、`.heroReplay`、`.plans.csv`、`.decisions.csv.gz`、`.candidates.csv.gz`、`.events.txt.gz`、`.metrics.csv`。决策按每 Tick 流式记录，包含完整长局，不受 AI 内部最近 512 条诊断缓存限制。事件与候选日志压缩保存，权威回放完整保存。

指标包含：真实生命损失、名义直接接触、攻击启动/效果/提前取消、Guard/Move/Block/Dodge、连续拼刀、控制占用、实际位移、资源请求/入账/溢出/预留/消费/退款/过期、窗口占用及无正收益等待。原始导出保留按目标动作阶段标记的生命损失分项以便追溯；它属于普通命中，不重复计入总收益，不作为独立验收项。

原生导出的 `CancelledWindups` 包含命中前取消总数。最终汇总按计划的 Locked 边界重新分为“启动前取消”和“真正的前摇取消”，并单列控制、死亡和命令撤销；原始总数保留为 `CancelledAttackBeforeImpact`。Legacy 无新策略评分明细，其合法候选/资源不足评分计数为零不代表没有候选或资源问题，拒绝须看权威事件。

`analyze.py` 从原生指标重建分组及匹配块对照，并检查源码与生命/余额守恒。`--corrected` 单独分析 V2 的 96 场回归。两个版本不合并计算平衡结论。没有覆盖所有策略×所有地图组合，正式参数仍须后续试玩与复核。

## 历史 V1 复跑

下列旧入口只适用于恢复 `source-archive.zip` 的独立副本。当前 V2 工作区会在冻结校验时拒绝运行，避免覆盖历史证据。

```powershell
unity run . --editor-version 6000.6.2f1 --timeout 1800 --no-tail --format json -- -executeMethod ProjectHero.Editor.Stage4RecoveryTools.RunShard -stage4Shard 0
# 对 1～7 重复同一命令；全部完成后另开进程审计
unity run . --editor-version 6000.6.2f1 --timeout 1800 --no-tail --format json -- -executeMethod ProjectHero.Editor.Stage4RecoveryTools.AuditSaved
```

开工前备份的 **813 个历史证据、场景及字体文件已全部恢复原始字节并逐一核对 SHA-256**；本轮被测试改写的 23 个文件另存 `validation-regenerated-files.zip`，没有新增历史路径文件。[恢复回执](baseline-restoration.json)记录核对结果。V1/V2 各自的 `sealed-evidence-manifest.json` 保存全部主矩阵原始文件及关键验证文件摘要。

## 代码与交接范围

| 文件 | 职责 |
|---|---|
| [Stage4RecoveryExperiment.cs](../../../Assets/Scripts/Logic/Experiments/Stage4RecoveryExperiment.cs) | 独立输入/定义、304 场登记矩阵、真实双 AI 长局、计划和生命/资源统计 |
| [Stage4RecoveryTools.cs](../../../Assets/Scripts/Editor/Stage4RecoveryTools.cs) | 分批原生运行、完整诊断、输入/源码冻结与磁盘回放；V2 单独输出 96 场 |
| [Stage4RecoveryExperimentTests.cs](../../../Assets/Tests/EditMode/Logic/Stage4RecoveryExperimentTests.cs) | 定义唯一变化、先手/站位、绑定、统计与并行独立世界确定性 |
| [AiForecastIngressTests.cs](../../../Assets/Tests/EditMode/Logic/AiForecastIngressTests.cs) | 三项预演与真实入口一致性反例回归 |
| [AiForecastWorld.cs](../../../Assets/Scripts/Logic/Snapshots/AiForecastWorld.cs) | V2 预演改走隔离世界的真实注册命令入口 |
| [AiTacticalPolicy.cs](../../../Assets/Scripts/Logic/AI/AiTacticalPolicy.cs) | 仅提升策略版本为 V2；权重未调整 |

本轮结果为首轮实验与正确性纠偏完成。未完成项是正式参数选择、V2 全保留矩阵重跑、正式定向攻击几何、更长对局及人工试玩；不以本轮合成场景替代这些验证。没有更新正式数值资产或默认策略，也没有引入另一套战斗规则。后续使用 V2，共同对照所有入围档；V1 用封存源码和回放复核。
