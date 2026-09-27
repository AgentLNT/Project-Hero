using System.Runtime.CompilerServices;

// 任务 03 修复（R3）：记录事实注入（CommandIngressEntry.InjectRecordedFact）从 public
// 收窄为 internal，使"UI/AI 可见 API 中不存在可填写生产者序号的入口"成为编译期事实。
//
// 最小必要开放：只有真正调用该能力的测试程序集被授权，且仅此一个。
// - ProjectHero.Authoring.Tests：Task03 的 CommandIngressTests / ReplayContractTests 需要
//   构造"重复 ProducerOrdinal 碰撞组"与"序号回退"两种回放场景（Submit 会自动分配严格递增
//   序号，没有注入面就无法触发这两条路径）。
// - ProjectHero.Logic.Tests 未获授权：该程序集当前没有任何调用点，授权会无谓扩大可见面。
// 业务程序集（ProjectHero.Authoring / Assembly-CSharp / UnityView）一律不可见。
[assembly: InternalsVisibleTo("ProjectHero.Authoring.Tests")]
