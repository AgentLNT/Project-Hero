# AI 预演入口修复及后摇回归（2026-11-04）

**全量 EditMode 1099/1099；生产/Shadow 定向 PlayMode 14/14；96 场、108,796 Tick 逐场回放及独立进程审计全部通过。** [独立审计回执](native-disk-replay-audit.txt)、[场景检查](production-shadow-playmode.xml)。V1 的 304 场完整保留于上级目录。此处使用 `tactical-public-kernel-v2`，不覆盖旧数据，也不将两版合并计算平衡结论。

2026-11-04 用户纠正后的解释以[语义及实验驱动说明](../semantic-correction-2026-11-04/README.md)为准：本轮是双方规则决策器实时选动作，不是固定动作序列；敌方命中自身后的反击仍按普通命中评价，没有目标动作阶段附加条件。现有规则策略尚无专门的“后手”候选，本批不能当作该动作卡预设的验收。

## 修复原因及范围

V1 预演直接修改隔离世界的计划，反应也在下一 Tick 开始前直接预留资源。因此预演漏掉了两件真实提交行为：新计划在当 Tick 命令批次的占用标记；下一 Tick 换窗导致的资源重置。前者产生“真实无法开始，预演却延期后命中”的假收益，后者会高估跨窗口反应的合法性。原始反例见 [V1 反例](../preview-start-gate-counterexample.json)。

V2 在隔离世界通过已注册的同一控制器入口提交候选，再由真实 Step 处理换窗、命令接纳、启动门控及效果。候选的计划起点、预计命中和结束随隔离世界计划快照更新；实际是否启动、产生效果仍看权威事件。公开信息过滤、120 Tick 预演长度、6 Tick 普通思考节奏、候选生成及策略权重保持原样；实际战斗规则没有调整。

已有运行代码只改了 `AiForecastWorld.cs` 的预演路径和 `AiTacticalPolicy.Version`。默认 Legacy 策略保持原设置；新策略版本进入定义指纹，回放不会把 V1/V2 误当成同一输入。逐文件路径及摘要见 [provenance.json](provenance.json)。

三项回归在 V1 均失败（[before-fix.xml](before-fix.xml)），V2 均通过（[after-fix.xml](after-fix.xml)）：

1. 已知控制至 247 时，223 新提交且当 Tick 开始的快攻不能被预演算出 4 点假伤害。
2. 223 提交、230 才开始的未占用未来计划仍可合法延期至 247，在 277 命中；预测伤害与实际一致。
3. 179 有资源、180 自己换窗清零时，跨窗反应必须返回资源不足。

全量检查 [full-editmode.xml](full-editmode.xml) 通过 1099 项。`source-archive.zip` 保存该版本 419 个 C# 及元数据/程序集定义/包版本，共 857 文件。全源码指纹 `19c2ed3b036f615450dede2f60d155fe752843177e42f9634e10ceb0eedab502`；170 个 Logic 文件冻结摘要 `13ff3049343c495b66a3b3b5e16dc7fe1fc30e362bdf3fda6a1322d123cbaaa3`。

在相同墙边反例的第 222 Tick，快攻候选从 V1 的“预估伤害 4096 Q10、评分 32233、选中”变为 V2 的“预估伤害 0、评分 -480、等待”。评分规则没有修改；差异来自预演不再虚构延期命中。两版候选原行见 [counterexample-after-fix.json](counterexample-after-fix.json)。

## 96 场登记

四组固定场景/策略，每组双先手×双出生位置×六档后摇 **30/31/32/39/40/41**：

| 场景 | 钝击策略 | 对手策略 | 场次 |
|---|---|---|---:|
| ProbeOpen | 持续进攻 | 持续进攻 | 24 |
| HeavyQuickOpen | 持续进攻 | 寻找打断 | 24 |
| HeavyQuickWall | 谨慎防守 | 持续进攻 | 24 |
| TeamSweepWall | 谨慎防守 | 寻找打断 | 24 |

窗口 180、种子 31、场景定义与 V1 同名场次完全一致。包含 V1 所有 40 个“启动时单位不可用”的攻击草稿所在场次，并复核拼刀、2 对 2、基础探针。每场重新生成 AI 并逐 Tick 比较回放；全部完成后另起 Unity 进程从磁盘审计。

原始文件及统计口径与上级 V1 相同。此批次是修复回归，尚未重跑 V1 的全部保留布局、Legacy 对手、边界后摇及窗口敏感性矩阵。

## 回归读出

[版本逐场比较](v1-vs-v2.csv)与 [comparison.json](comparison.json)确认：同名 96 场的胜负、最终生命、结束 Tick、实际攻击启动数及效果数均与 V1 相同；5 场中原有的 40 个启动失败攻击草稿全部消除。新版本没有命令拒绝。此结果证明修复了已知假收益和无效尝试，不表示 AI 的全部未来预测都能命中。

| 场景（各 24 场） | 钝击胜 | 钝击负 | 达到时限 |
|---|---:|---:|---:|
| HeavyQuickOpen | 0 | 0 | 24 |
| HeavyQuickWall | 5 | 0 | 19 |
| ProbeOpen | 0 | 24 | 0 |
| TeamSweepWall | 0 | 0 | 24 |

逐档、策略、先手及站位见 [analysis-report.md](analysis-report.md)、[summary.csv](summary.csv) 和 [paired-31-vs-40.csv](paired-31-vs-40.csv)。16 个 31/40 比较块中没有胜负翻转；13 个最终生命差相同，3 个在 40 时提高；67 场达到本轮时限。按目标动作阶段导出的命中标签不形成独立收益或成功条件，也不据此判定需要额外补场。现有结果限定于登记的场景、动作及策略组合，没有据此选定正式值。

开阔地重击/快攻在 39/40/41 时部分窗口少一次快攻，最终生命差改善；墙边组 30 与 31 的相邻变化只在一个先手/站位组合改变终局。其余布局没有统一改善方向。不能把“后摇更长”直接解释为固定增加某次受击或统一削弱。

## 复跑与分析

```powershell
unity run . --editor-version 6000.6.2f1 --timeout 1800 --no-tail --format json -- -executeMethod ProjectHero.Editor.Stage4RecoveryTools.RunCorrectedShard -stage4Shard 0
# 对 1～3 重复；然后另起进程
unity run . --editor-version 6000.6.2f1 --timeout 1800 --no-tail --format json -- -executeMethod ProjectHero.Editor.Stage4RecoveryTools.AuditCorrectedSaved
# 使用本机 Python 运行上级 analyze.py --corrected，再运行 evidence_tools.py compare
```

下一轮固定 V2，按用户明确的普通命中时机比较真实出手、命中顺序、应对、换血及资源，并覆盖正式定向几何、长局与保留矩阵。动作卡“后手”候选须单独实现与验证，不能由当前对战推定已经具备。31/40 保留为待验证候选，相邻六档继续共同对照；当前不修改正式后摇默认值。
