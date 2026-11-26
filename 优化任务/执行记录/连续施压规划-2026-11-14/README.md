# 连续施压规划与防御交换

2026-11-14，承接[任务23](../短组合规划-2026-11-13/README.md)。本轮实现已作为独立实验开关接入，**行为验收不通过，不采用正式默认配置，也不据此增加更多模板**。

## 结果与对照

沿用同一正式内容、种子20260922、资源10、9种有序策略对、3种布局和2种镜像，每组54场，最长3600 Tick。温度、基础效用权重、64HP施压项及移动规划均保持一致。

| 指标 | P：任务23 | V：只改防御估值 | C：V加连续攻击 |
|---|---:|---:|---:|
| 自然结束 / 超时 | 2 / 52 | 0 / 54 | 0 / 54 |
| 正伤害接触 | 455 | 401 | 419 |
| 实际有效扣血 HP | 9985.5 | 9123 | 9345.5 |
| 普通攻击启动 | 1284 | 1159 | 1234 |
| 普通移动启动 | 817 | 615 | 594 |
| 实际防御资源消耗，点 | 912 | 867 | 963 |
| 实战命令拒绝 | 0 | 0 | 0 |

C相对V增加18次伤害接触、222.5 HP扣血（约2.44%），54组配对中28组扣血增加、23组减少、3组相同；结束率没有改善。C相对P有18组扣血增加、35组减少、1组相同，总扣血下降约6.4%。这支持“该模板有局部收益，但本轮实现与估值不足以提升长局表现”，不支持“短组合方向整体无用”或“再多列几个模板就能解决”。单个种子、有限对手回复与54场配对也不能给出普遍统计保证。

P→V同时改变了防御评分方式和代表性回复协议，不能把这部分变化归因于其中某一项。V→C仅增加新模板，是模板增量的主要对照。

## 347次记录的修正

使用生产内核重演P的全部54场、194260 Tick，逐Tick验证旧事件和快照，再读取实际冻结攻击Intent、冲突图及位置轨迹，随后以封存的原始事件CSV独立确认：

| 原“没有DamageChannelResolved记录”的347次 | 数量 |
|---|---:|
| 有Block接触，BlockResolved明确完全抵抗且伤害为0 | 228 |
| 实际命中时无接触，目标在攻击期间发生普通移动 | 119 |

全部347次均确实产生了命中Intent，没有证据表明攻击被窗口边界吞掉。119次无接触中，99次提交时覆盖目标、20次提交时不覆盖目标；后者的正伤害预测依赖未来状态，不能简单算作提交当时就对空乱打。本次没有为新C/V的所有未命中逐一重建空间轨迹，不能将P的成因分布直接套用过去。

原始观察在[miss-diagnosis.json](miss-diagnosis.json)，事件复核及权威分类在[miss-diagnosis-confirmed.json](miss-diagnosis-confirmed.json)，可运行[analyze_misses.py](analyze_misses.py)。这是对旧分类的补充，不修改封存旧证据。

## 实现与能力边界

新增可选 `AiContinuationSettings`。空配置省略所有新哈希分量，任务23的旧定义与重演保持兼容。V使用完整实际结果的有限分支最小效用；C另生成至多两个“攻击A→可选一次接近→攻击B”候选，每个A搭配当前同演员、同目标可用的最快B。原基础权重和施压奖励不变。

A、接近、B及防御回复全部经隔离生产内核的真实命令入口。A或接近动作完成后，等待自己真实可提交的窗口与6 Tick思考节拍，重新读取公开位置、朝向、资源和排程修订。最多一次接近复用规范攻击体积反推、生产A*与真实Move预览。不存在虚构计划、未来窗口外强插动作或直接写资源。

每一击采用最多四个已准入的代表性防御：首个Block、稳定键两端的不同Dodge、首个Guard；另含不回应。第一击防御后，在该分支真实资源、奖励、移动和周期清零状态中，再收集第二击的回复。因此包含不防、只防A、只防B和防A后再防B的分支。**这是有限对手应对协议，不是全局最坏情形搜索。**未公开的敌方未来动作不会读入；敌方未来自主普通移动也未新增为搜索分支。

真实执行只提交A，后续仍独立重规划。C中连续攻击候选评估3048次、整段覆盖1708次；1586次评估包含连续两次防御的分支。新模板被选252次，252个A均启动、243个完成；237次观察到后续攻击，152次直接接攻击、57次先接一次移动再攻击；81次后续攻击采用原提议B技能，48次后续攻击造成正伤害。后续动作可能在多条先前提议中重叠，这些数字不是固定连招的执行成功率。

跨窗能力保持：C普通启动1858次，1014次规划结束点超出来源窗口，962次已观察到实际越过窗口、52次被3600 Tick截断；攻击1234次中803次规划结束点跨窗、690次命中点位于来源窗口结束或之后。这证明跨窗行动仍可被选择，不等于已解决敌方未来行动风险判断。

## 验证与复核

- 完整EditMode：1225/1225，包括8个新增机制测试；生产/Shadow定向PlayMode：22/22。
- 8个新增机制覆盖：真实第二击与窗口/思考节拍、第一击收入资助第二击防御、周期清零后资金消失、闪避后接近再攻击、连续两次防御的真实扣账和预留、覆盖不足不得采样、禁用模板及配置哈希。
- 当前新源码关闭开关后重演P：54场、194260 Tick，定义哈希、事件和快照全部一致，见[legacy-p-audit.txt](legacy-p-audit.txt)。
- V/C新长局108场、388800 Tick；独立审计重新生成事件、快照、候选、决策、资源、动作和摘要，不能只读摘要宣称通过，见[formal/disk-audit.txt](formal/disk-audit.txt)。
- 54组机制原生/.NET规范输出逐字节一致；8场原生正式内容600 Tick参照共4800 Tick，事件、快照、候选、决策及指标均独立重建一致，见[cross-runtime-check.json](cross-runtime-check.json)和[formal/unity-reference-audit.txt](formal/unity-reference-audit.txt)。

源码在长局前冻结为478个C#文件，指纹 `62ebb2a6a0c675571cfc3dbb5bb9beb7a305c554dc7d1723f2b9fbcba80a55e0`，全部最终验证使用相同Assets源码。原始失败的定向测试日志保留为探索记录；它们最初使用的4HP/半点资源不足以购买防御，最终修正夹具后由完整回归验收。正式性能仍未验收，墙钟时间只作诊断。

`before-source.zip`保存运行验证前的源码/内容；`before-protected.json`记录历史材料的全部初始哈希，`before-tracked.zip`在恢复后仅保留被重生成文件的验证前字节，`validation-regenerated-files.zip`保留测试产生的新版本。`restoration.json`列出已恢复文件。Task22/23封存文件必须逐字节保持，最终校验见[previous-evidence-preservation.json](previous-evidence-preservation.json)。

Unity运行还自动向Standalone编译标志追加了`SENTIS_ANALYTICS_ENABLED`；该项目设置在本轮开始时干净，已恢复为原先的`APP_UI_EDITOR_ONLY`并逐字节核对HEAD。Assets中的战斗源码及测试没有使用该标志，原生与不含该标志的独立Logic运行器已核对一致。说明见`project-settings-restoration.json`。

## 复现

Windows PowerShell中，在独立项目副本使用冻结Assets源码、相同正式内容和本目录harness。`harness/build.ps1`编译实际Logic源码，不是另一套战斗模拟器；`FormalAiSourceExport.cs`保存正式场景的Logic定义值，`Shims.cs`只提供宿主序列化/日志适配。

本机编译宿主为Codex随附的PowerShell7.6.9、.NET10及`$PSHOME`中的Roslyn/ref程序集；Windows PowerShell5.1不能直接运行该编译脚本。`dotnet`或`python`未在PATH时，使用本机可执行文件的绝对路径。

```powershell
$record = '优化任务/执行记录/连续施压规划-2026-11-14'
$dll = & "$record/harness/build.ps1"
dotnet $dll mechanisms
dotnet $dll legacy-p
dotnet $dll run
dotnet $dll audit
dotnet $dll reference
python "$record/analyze_misses.py"
python "$record/analyze.py"
python "$record/pressure_contribution.py"
python "$record/analyze.py" cross-runtime
```

封存记录不要原地重跑写入。`legacy-p`需要任务23原始P回放，诊断复核需要任务23事件CSV；原生核对使用 `ProjectHero.Editor.ContinuationNativeValidation.Run`。所有最终原始证据、分析脚本和源码归档均纳入 `sealed-manifest.json`，末尾以 `seal-result.json`给出状态。

## 后续判断

先保留上一轮P作为实验参照，本轮V/C不采用。下一轮应优先区分“逼出防御但对方资源随后重置/获奖”和“确实制造后续机会”，并在有限对手分支中加入有依据的普通移动避让。还要量化“按整段估值、执行首步后完全重选”带来的偏差；改变后续技能本身不等于错误，但不能把尚未执行的B收益直接当作已经兑现。

C选中的1234次攻击中，541次所选最差分支的伤害效用为0，却仍获得固定64HP施压项；其中连续攻击候选有40/252次在加施压项前总分不大于0。这些动作可能有防御资源或位置价值，不能再称为非法或空挥，但固定奖励是否准确对应后续机会需要单独消融。计数可由[pressure_contribution.py](pressure_contribution.py)重建。确认评分能识别真实机会后，再考虑扩大短组合数量。
