# 阶段四数据读出：tactical-public-kernel-v2

以下来自原生规则决策器对战汇总；每组均包含独立交换的双先手、双出生位置。HP 差为钝击方减对手，单位为生命点。2026-11-04 用户纠正后的解释：敌方命中自身之后我方尽早命中，沿用普通交互结果；不以目标动作阶段设独立成功门槛。现有候选尚无该专门时机，本批不构成动作卡预设验收。胜负和超时必须结合具体策略及地图阅读。

## 匹配的 31/40 对照

共 16 个固定输入比较块；其中 0 块终局类别改变，13 块最终生命差相同。完整逐块变化见 paired-31-vs-40.csv。

## 按场景与策略分组

| 场景 | 钝击策略 / 对手策略 | 种子 / 窗口 | 后摇 | 钝击胜 / 对手胜 / 超时 | 平均 HP 差（范围） | 结束 Tick 均值 |
|---|---|---|---:|---|---|---:|
| HeavyQuickOpen | SustainedOffense / SeekInterrupt | 31 / 180 | 30 | 0 / 0 / 4 | 2.09961 (-2～6.19922) | 1079 |
| HeavyQuickOpen | SustainedOffense / SeekInterrupt | 31 / 180 | 31 | 0 / 0 / 4 | 2.09961 (-2～6.19922) | 1079 |
| HeavyQuickOpen | SustainedOffense / SeekInterrupt | 31 / 180 | 32 | 0 / 0 / 4 | 2.09961 (-2～6.19922) | 1079 |
| HeavyQuickOpen | SustainedOffense / SeekInterrupt | 31 / 180 | 39 | 0 / 0 / 4 | 4.09961 (-2～10.1992) | 1079 |
| HeavyQuickOpen | SustainedOffense / SeekInterrupt | 31 / 180 | 40 | 0 / 0 / 4 | 4.09961 (-2～10.1992) | 1079 |
| HeavyQuickOpen | SustainedOffense / SeekInterrupt | 31 / 180 | 41 | 0 / 0 / 4 | 4.09961 (-2～10.1992) | 1079 |
| HeavyQuickWall | CautiousDefense / SustainedOffense | 31 / 180 | 30 | 0 / 0 / 4 | 0.75 (-4～4) | 1079 |
| HeavyQuickWall | CautiousDefense / SustainedOffense | 31 / 180 | 31 | 1 / 0 / 3 | 1 (-4～4) | 1043.5 |
| HeavyQuickWall | CautiousDefense / SustainedOffense | 31 / 180 | 32 | 1 / 0 / 3 | 1 (-4～4) | 1043.5 |
| HeavyQuickWall | CautiousDefense / SustainedOffense | 31 / 180 | 39 | 1 / 0 / 3 | 1 (-4～4) | 1045 |
| HeavyQuickWall | CautiousDefense / SustainedOffense | 31 / 180 | 40 | 1 / 0 / 3 | 1 (-4～4) | 1045 |
| HeavyQuickWall | CautiousDefense / SustainedOffense | 31 / 180 | 41 | 1 / 0 / 3 | 1 (-4～4) | 1045 |
| ProbeOpen | SustainedOffense / SustainedOffense | 31 / 180 | 30 | 0 / 4 / 0 | -7.5 (-12～-3) | 961 |
| ProbeOpen | SustainedOffense / SustainedOffense | 31 / 180 | 31 | 0 / 4 / 0 | -7.5 (-12～-3) | 961 |
| ProbeOpen | SustainedOffense / SustainedOffense | 31 / 180 | 32 | 0 / 4 / 0 | -7.5 (-12～-3) | 961 |
| ProbeOpen | SustainedOffense / SustainedOffense | 31 / 180 | 39 | 0 / 4 / 0 | -7.5 (-12～-3) | 961 |
| ProbeOpen | SustainedOffense / SustainedOffense | 31 / 180 | 40 | 0 / 4 / 0 | -7.5 (-12～-3) | 961 |
| ProbeOpen | SustainedOffense / SustainedOffense | 31 / 180 | 41 | 0 / 4 / 0 | -7.5 (-12～-3) | 961 |
| TeamSweepWall | CautiousDefense / SeekInterrupt | 31 / 180 | 30 | 0 / 0 / 4 | -5.58765 (-12～7.64941) | 1439 |
| TeamSweepWall | CautiousDefense / SeekInterrupt | 31 / 180 | 31 | 0 / 0 / 4 | -5.58765 (-12～7.64941) | 1439 |
| TeamSweepWall | CautiousDefense / SeekInterrupt | 31 / 180 | 32 | 0 / 0 / 4 | -5.58765 (-12～7.64941) | 1439 |
| TeamSweepWall | CautiousDefense / SeekInterrupt | 31 / 180 | 39 | 0 / 0 / 4 | -4.83765 (-12～7.64941) | 1439 |
| TeamSweepWall | CautiousDefense / SeekInterrupt | 31 / 180 | 40 | 0 / 0 / 4 | -4.83765 (-12～7.64941) | 1439 |
| TeamSweepWall | CautiousDefense / SeekInterrupt | 31 / 180 | 41 | 0 / 0 / 4 | -4.83765 (-12～7.64941) | 1439 |

## 有变化的匹配块

下表保留具体先手、站位及种子，便于复现。正的生命差变化表示 40 Tick 时钝击方最终生命差更高；不自动解释为更平衡。

| 输入块 | 31 → 40 结果 | HP 差变化 | 结束 Tick 变化 |
|---|---|---:|---:|
| HeavyQuickOpen-SustainedOffense-SeekInterrupt-firstA-swap0-seed31-w180 | timeout → timeout | 4 | 0 |
| HeavyQuickOpen-SustainedOffense-SeekInterrupt-firstA-swap1-seed31-w180 | timeout → timeout | 4 | 0 |
| HeavyQuickWall-CautiousDefense-SustainedOffense-firstA-swap0-seed31-w180 | victory → victory | 0 | 6 |
| TeamSweepWall-CautiousDefense-SeekInterrupt-firstA-swap1-seed31-w180 | timeout → timeout | 3 | 0 |

## 统计口径

- 真实伤害取提交前后生命变化，名义直接伤害另列；后摇生命损失是总生命损失的子集，不再加到总收益。
- 资源请求量、实际入账、溢出、预留、消费、退款、周期过期及战斗结束清空分别记录，逐场核对生命与可用余额守恒。
- 原始导出中的目标动作阶段字段只标记普通命中发生时的状态，不形成特殊收益、独立命中类别或成功门槛；按真实接触、伤害及应对评价。
- 连续拼刀为同一单位连续攻击效果中以拼刀结算的最长序列；非拼刀攻击效果会清零。
- 汇总中的 CancelledAttackBeforeImpact 保留原生导出的命中前取消总数；根据 plans.csv 的 Locked 边界，另分 CancelledBeforeStart 与 CancelledWindups，控制及死亡原因单列。不能把旧 AI 撤销草稿当成控制收益。
- 新策略的 WAIT_NO_POSITIVE_CANDIDATE 另按是否有合法候选分组；Legacy 没有新策略评分明细，其 EligibleCandidates/ResourceBlockedDecisionTicks 的零值不能解释为无合法候选或无资源问题。旧策略拒绝以权威事件为准。
- 原始决策和全部候选见压缩 CSV；事件和回放保存完整时序。窗口占用包含 Guard、移动和后摇，不能把全部等待解释为空转。
- 本轮只解释其登记的载荷/地图/策略组合；V1 为封存的旧预演基线，V2 为修复预演后的 96 场回归，不能合并成同一策略版本的平衡样本。未覆盖所有策略×地图组合，也没有人工试玩。
