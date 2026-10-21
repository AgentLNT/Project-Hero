# Project Hero

《时沙编年史：凡人诗篇》的 Unity 战术战斗原型。战斗以连续整数时间轴、三角网格、固定行动窗口与肾上腺素为核心，采用纯 C# 逻辑和 Unity 表现分层。

## 从这里开始

- [游戏设计文档](设计文档/游戏设计文档.md)：当前玩法、范围、界面目标与旧规则处理。
- [文档导航](设计文档/README.md)：概念、HUD、历史稿和维护记录。
- [架构优化方案](优化方案-逻辑表现解耦与逻辑帧判定.md)：冻结规则、公式与架构契约。
- [实施任务导航](优化任务/README.md)：任务包、最新收尾状态与原始证据入口。
- [项目结构与整理记录](设计文档/维护/项目结构与整理记录-2026-10-13.md)：目录职责、迁移映射与验证边界。

## 打开项目

在 Unity Hub 中添加当前仓库根目录，使用 [ProjectVersion.txt](ProjectSettings/ProjectVersion.txt) 指定的 **6000.6.2f1**。生产战斗入口为 [CombatSampleScene](Assets/Scenes/CombatSampleScene.unity)。依赖配置位于 Packages；不要按旧 README 的 Unity 2022.3 建议打开。

## 目录

| 路径 | 内容 |
|---|---|
| 设计文档/ | 当前设计、概念叙事、界面契约、历史与维护说明 |
| 优化任务/ | 实施任务包与执行记录；证据路径保持稳定 |
| Assets/Scripts/Logic/ | 纯 C# 战斗内核，程序集禁止 UnityEngine 引用 |
| Assets/Scripts/Authoring/、UnityAuthoring/ | 编辑配置、兼容导入与场景配置接入 |
| Assets/Scripts/UnityView/ | 唯一时钟驱动、输入投递、表现投影与开发者回放 |
| Assets/Scripts/Grid/ | 原有共享网格类型；权威逻辑网格位于 Logic/Grid/ |
| Assets/Scripts/Editor/、Core/、Demos/、UI/、Visuals/ | 编辑器工具及保留的旧诊断代码，按源码编译边界使用 |
| Assets/Materials/ | 项目材质，原 Material/ 已合并至此 |
| Assets/Prefab/ | 原型 Prefab；现有诊断工具使用此稳定路径 |
| Assets/Scenes/、Diagnostics/Editor/ | 生产场景与编辑器旧系统对照场景 |
| Assets/Tests/ | EditMode / PlayMode 验证，读取各自程序集定义 |
| Assets/Settings/、TextMesh Pro/、Resources/ | 渲染设置、第三方资源与 Resources 约定入口 |
| Packages/、ProjectSettings/ | Unity 包依赖和项目设置 |

Library、Logs、UserSettings 等为本地生成目录，沿用现有忽略规则，不手动移动或提交。

## 当前状态

截至 **2026-10-17**，生产默认 New、Legacy 编辑器隔离和开发者重演已完成，基线见 [非性能收尾记录](优化任务/执行记录/11-非性能收尾验收-2026-10-10.md)。

HUD M1～M4 已实现：[M1 暂停与草稿交互](优化任务/执行记录/M1-HUD验收-2026-10-15.md)已完成鼠标主流程、原生键盘链路与用户物理键盘复核；**M2～M4 真实操作及完整 Windows Player 构建、独立运行补验已完成**，最新结论与原始证据见 [2026-10-17 补验完成记录](优化任务/执行记录/M2-M4-补验完成-2026-10-17.md)。

**仍未完成：正式性能、分配与 GC 验收继续延期；全量 PlayMode 尚未取得全部通过的结论。** 上述补验完成不代表整体发布验收通过。
