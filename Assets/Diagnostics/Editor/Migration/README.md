# 迁移诊断

这里保留 Legacy/Shadow 的历史比对能力，正式战斗由 `Assets/Scripts/UnityView/BattleRuntimeBootstrap.cs` 单独启动。

`ProjectHero.Migration.Diagnostics` 单向引用 `ProjectHero.UnityView` 和 `ProjectHero.Logic`。程序集使用 `defineConstraints: ["UNITY_EDITOR"]` 排除 Player 编译。这里不能改成仅设置 `includePlatforms: ["Editor"]`：那会让 Unity 将宿主视为不可挂载的 Editor 脚本，破坏播放模式中的诊断场景。实际 Player 编译图和产物检查见本轮执行记录。

| 内容 | 入口 |
|---|---|
| 旧原型与双世界比较 | `MigrationBattleHost`，保留比较配置、采样、差异报告、写入墙和旧写入者门控 |
| 诊断场景 | `Assets/Diagnostics/Editor/LegacyComparisonScene.unity`、`Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity` |
| New 驱动的诊断观测 | `MigrationRuntimeContext.DriverContext` 注入可选 `IBattleRuntimeObserver`；正式 Bootstrap 不注入观测器 |
| 旧画布/视觉的隐藏与恢复 | `MigrationPresentationGate`、`LegacyVisualRegistry`，仅由诊断宿主控制 |
| 正式重演 | `UnityBattleDriver` 的 `ReplayRecorder`、Logic 的 Replay 文件与重演校验，继续属于 Runtime |

在 Unity Test Runner 的 PlayMode 中运行 `RuntimeOwnershipTests`、`Task09ShadowMirrorTests` 可复核隔离后的历史诊断。`ShadowAssemblyIsolationTests` 和 `ShadowSplitLifecycleTests` 分别检查发行边界与正式生命周期。历史比对结果不构成当前源码的自动通过凭证，涉及历史规则等价性时仍须按对应 case 的证据要求重新运行。

生产主场景没有诊断宿主、旧适配器或 Shadow 配置。`BattleRuntimeMode` 的旧数值仍保留，用来明确拒绝旧序列化配置与旧调用；它不会在正式 Bootstrap 中选择 Legacy 或 Shadow 执行路径。HUD 调整无需以历史迁移比对作为前置门槛。
