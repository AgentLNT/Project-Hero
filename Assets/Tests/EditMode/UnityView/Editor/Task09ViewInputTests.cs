using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;
using ProjectHero.Core.Timeline;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.UI.Timeline;
using UnityEngine;

namespace ProjectHero.Tests.UnityView.Editor
{
    /// <summary>
    /// 任务 09 / B 流必需用例（任务包 §3 第 43/46/47 行，逐字同名，归属 Assembly-CSharp-Editor）。
    ///
    /// <strong>三条用例的证明口径完全一致</strong>：在<strong>真实</strong>
    /// <c>BattleSimulation</c>（真实 02B 配置链）上取"规范化快照的逐字段读数"，
    /// 操作前后<strong>逐字比较</strong>；再叠加入口水位与未来 Tick 桶的比较。
    /// 它们**不**只断言"我的对象没变"——任何写纪律的破坏（改计划/预算/位置/预留/
    /// 修订号/入口水位）都会让指纹不同而失败。
    /// </summary>
    public class Task09ViewInputTests
    {
        /// <summary>装配：真实模拟 + 真实入口 + 注入同一真实模拟的只读端口。</summary>
        private sealed class Rig
        {
            public BattleSimulation Simulation;
            public SimulationViewLogicPort Port;
            public ViewCommandFactory Commands;
            public ViewInputController Controller;
            public CommandIngressEntry Entry;

            public static Rig Create()
            {
                var simulation = Task09Fixture.NewSimulation();
                var port = new SimulationViewLogicPort(simulation, Task09Fixture.PlayerControllerId);
                var rig = new Rig
                {
                    Simulation = simulation,
                    Port = port,
                    Commands = new ViewCommandFactory(port),
                    Controller = new ViewInputController(new ViewInputPorts(port, new ViewCommandFactory(port))),
                    Entry = Task09Fixture.PlayerEntry(simulation)
                };
                return rig;
            }

            /// <summary>带脚本窗口的装配（B2：真实可编辑计划需要窗口 scope）。</summary>
            public static Rig CreateWithWindow()
            {
                var simulation = Task09Fixture.NewSimulation(Task09Fixture.WindowAtZero());
                var port = new SimulationViewLogicPort(simulation, Task09Fixture.PlayerControllerId);
                return new Rig
                {
                    Simulation = simulation,
                    Port = port,
                    Commands = new ViewCommandFactory(port),
                    Controller = new ViewInputController(new ViewInputPorts(port, new ViewCommandFactory(port))),
                    Entry = Task09Fixture.PlayerEntry(simulation)
                };
            }

            public string Fingerprint() => Task09Fixture.AuthorityFingerprint(Simulation);

            /// <summary>入口读数：下一个 ProducerOrdinal + 未来桶（证明"没有命令被提交"）。</summary>
            public string IngressFingerprint()
            {
                CommandIngressRegistrySnapshot snapshot = Simulation.CommandIngress.CaptureSnapshot();
                var builder = new System.Text.StringBuilder();
                builder.Append("frozen=").Append(snapshot.FrozenThroughTick.ToString(System.Globalization.CultureInfo.InvariantCulture));
                for (int i = 0; i < snapshot.Entries.Count; i++)
                {
                    builder.Append("|").Append(snapshot.Entries[i].ControllerId).Append(':')
                           .Append(snapshot.Entries[i].NextProducerOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                           .Append(snapshot.Entries[i].FrozenProducerOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                for (int i = 0; i < snapshot.FutureBuckets.Count; i++)
                {
                    builder.Append("|b").Append(snapshot.FutureBuckets[i].TargetTick.ToString(System.Globalization.CultureInfo.InvariantCulture))
                           .Append(':').Append(snapshot.FutureBuckets[i].PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }

            public void Dispose() => Simulation.Dispose();
        }

        // ————————————————————————————————————————————————————————————————
        // 产出 8 / 43：选择与拖拽模式不写逻辑
        // ————————————————————————————————————————————————————————————————

        /// <summary>
        /// 选择单位/选择动作/进入放置与重排手势/多次鼠标移动与吸附预览
        /// <strong>都不</strong>产生任何逻辑写入，也<strong>不</strong>产生任何命令提交。
        ///
        /// 会让它红的实现缺陷：
        /// (a) 任一手势步骤直接调用 ScheduleEditor/TerminalCoordinator/预算/位置写入；
        /// (b) 逐帧预览被包成 ScheduleEditCommand 提交（入口水位/未来桶会变）；
        /// (c) 预览改变 ScheduleRevision 或计划 State/StartTick；
        /// (d) 视图侧建立了"第二套权威状态"并把它写回逻辑。
        /// </summary>
        [Test]
        public void SelectionAndDragModesDoNotMutateLogic()
        {
            Rig rig = Rig.Create();
            try
            {
                Task09Fixture.AdvanceTo(rig.Simulation, 2);
                long tickBefore = rig.Simulation.Tick;
                long revisionBefore = rig.Simulation.ScheduleRevision;
                long nextOrdinalBefore = rig.Entry.NextProducerOrdinal;
                string authorityBefore = rig.Fingerprint();
                string ingressBefore = rig.IngressFingerprint();

                // 1) 选择单位 → 选择动作 → 选择方向/目标。
                rig.Controller.SelectUnit(Task09Fixture.HeroUnitId);
                rig.Controller.SelectAction("action.quick_slash.radius_1");
                rig.Controller.EnterDirectionTargetSelection();
                Assert.That(rig.Controller.HasSelectedUnit, Is.True);
                Assert.That(rig.Controller.SelectedActionSpecId, Is.EqualTo("action.quick_slash.radius_1"),
                    "视图侧只保存动作规格 ID，不复制动作表");

                // 2) 进入放置手势（本地草稿；不提交）。
                bool began = rig.Controller.BeginPlacement(
                    Task09Fixture.HeroUnitId, "action.quick_slash.radius_1",
                    requestedStartTick: 10L, insertAfterPlanId: null,
                    primaryTargetUnitId: Task09Fixture.EnemyUnitId.Value);
                Assert.That(began, Is.True, "放置手势应能建立本地草稿");
                Assert.That(rig.Controller.ActiveGesture, Is.EqualTo(ViewGestureKind.PlaceOrdinaryPlan));

                // 3) 多次鼠标移动 / 吸附 / 预览：每一次都只改视图状态。
                for (int i = 0; i < 24; i++)
                {
                    rig.Controller.PreviewPlacementTick(10L + i);
                }
                Assert.That(rig.Controller.PreviewStepCount, Is.EqualTo(24),
                    "逐帧预览必须被计数（证明真的发生了多次鼠标移动解析）");
                Assert.That(rig.Controller.LastOutcome.ReasonCode, Is.EqualTo(ViewInputCodes.LOCAL_DRAFT_ONLY),
                    "逐帧预览是纯本地事实");

                // 4) 吸附辅助线：越线候选被夹到锁定线（CurrentTick + 1），只是视图反馈。
                long snapped = rig.Controller.SnapToEditableBoundary(0L);
                Assert.That(snapped, Is.EqualTo(tickBefore + 1L),
                    "锁定线显示 = 快照 CurrentTick + 1（UI 只做提前反馈）");
                Assert.That(rig.Controller.SnapToEditableBoundary(tickBefore + 5L), Is.EqualTo(tickBefore + 5L),
                    "锁定线之上的候选不被改写");

                // 5) 视图侧对越线确认的提前反馈。
                Assert.That(rig.Controller.EvaluatePlacement(0L),
                    Is.EqualTo(ViewInputCodes.TARGET_AT_OR_BEFORE_LOCKED_LINE));

                // 6) 重排手势：目标必须是 Editable 普通计划。当前没有计划，因此手势被拒绝。
                Assert.That(rig.Controller.BeginReorder(new ActionPlanId(1L), 10L), Is.False,
                    "不存在仍为 Editable 的普通计划时不得进入重排手势");
                Assert.That(rig.Controller.BeginRemoval(new ActionPlanId(1L)), Is.False,
                    "不存在仍为 Editable 的普通计划时不得进入删除手势");

                // ——— 权威读数：逐字比较 ———
                Assert.That(rig.Fingerprint(), Is.EqualTo(authorityBefore),
                    "选择/拖拽/吸附/预览不得改变任何逻辑权威状态字段（含规范化哈希）");
                Assert.That(rig.IngressFingerprint(), Is.EqualTo(ingressBefore),
                    "上述操作不得提交任何命令（入口水位与未来桶必须原样）");
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(0), "没有任何请求被发到入口");
                Assert.That(rig.Controller.ConfirmedGestureCount, Is.EqualTo(0), "没有任何手势被确认");

                Assert.That(rig.Simulation.Tick, Is.EqualTo(tickBefore));
                Assert.That(rig.Simulation.ScheduleRevision, Is.EqualTo(revisionBefore));
                Assert.That(rig.Entry.NextProducerOrdinal, Is.EqualTo(nextOrdinalBefore),
                    "未提交请求不得消耗 ProducerOrdinal");
            }
            finally
            {
                rig.Dispose();
            }

            // ——— B2 扩展段（冻结件 §8.4 第 2 条 / 派工书 §3.4）—————————————
            // 端口接线后逐个调用旧入口，断言旧时间线写入「结构性不可达」。
            // 这一段是 3.1 的守卫的牙齿：去掉守卫 ⇒ 本用例必红。
            LegacyEntriesAreStructurallyUnreachableAfterPortsAreBound();
        }

        // ════════════════════════════════════════════════════════════════════
        // B2 扩展段（同一用例内追加）：旧入口在接线后不得触达旧时间线写入
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 让旧写入的<strong>全部前提</strong>都成立，只把守卫留成唯一变量：
        /// <list type="bullet">
        /// <item>真实窗口 + 真实命令入口 ⇒ 真实 <c>Editable</c> 普通计划，
        /// 于是 <c>_playerBlocks</c> / <c>_placementsByGroupId</c> 里确实存在该块
        /// （由 Logic 投影产生，不再是"接线后恰好为空"的隐式前提）；</item>
        /// <item><c>placement.Schedule</c> 上挂可观测计数委托 ⇒ 旧排程通道可观测；</item>
        /// <item>探针 <c>BattleTimeline</c> 上挂同 id 的组事件 ⇒
        /// <c>CancelGroup</c> 通道可观测（事件数会掉到 0）；</item>
        /// <item><c>FinalizePlacement</c> 拿到非空 ghost 与非空待确认 placement。</item>
        /// </list>
        /// 然后逐个调用旧入口，断言：两个探测通道零变化、Logic 权威指纹逐字段不变、
        /// 入口水位与未来桶不变；最后证明"接线后删除确实改走新命令路径"。
        /// </summary>
        private static void LegacyEntriesAreStructurallyUnreachableAfterPortsAreBound()
        {
            var rig = Rig.CreateWithWindow();
            var viewRoot = new GameObject("Task09B2ViewRoot", typeof(RectTransform));
            var timelineGo = new GameObject("Task09B2ProbeTimeline", typeof(BattleTimeline));
            var unitGo = new GameObject("Task09B2ProbeUnit", typeof(CombatUnit));
            viewRoot.hideFlags = HideFlags.HideAndDontSave;
            timelineGo.hideFlags = HideFlags.HideAndDontSave;
            unitGo.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                // ——— 1) 真实路径造一个"仍为 Editable 的普通计划" ———
                Assert.That(rig.Entry.Submit(Task09Fixture.AddHeroAttackCommand(
                    0L, 10L, 0L, new WindowId(1L))), Is.Null, "夹具前提：新增必须被入口接受");
                Task09Fixture.StepNext(rig.Simulation);   // Tick 0：窗口 W1 打开 + Add 落地
                Task09Fixture.StepNext(rig.Simulation);   // Tick 1

                LogicSnapshot snapshot = rig.Simulation.CurrentSnapshot;
                Assert.That(snapshot.Plans.Count, Is.EqualTo(1), "夹具前提：真实计划必须存在");
                long planId = snapshot.Plans[0].ActionPlanId;
                Assert.That(snapshot.Plans[0].State,
                    Is.EqualTo((int)ProjectHero.Logic.Actions.ActionPlanState.Editable),
                    "夹具前提：计划仍为 Editable（旧入口的全部旧前提因此成立）");

                // ——— 2) 视图装配：接线 = 注入端口（与本 Rig 的端口同一实例，读数才可比）———
                var probeTimeline = timelineGo.GetComponent<BattleTimeline>();
                var probeHero = unitGo.GetComponent<CombatUnit>();

                var ui = viewRoot.AddComponent<TimelineEditorUI>();
                ui.Timeline = probeTimeline;
                ui.PlayerLane = NewLane(viewRoot.transform, "PlayerLane");
                ui.ObservedLane = NewLane(viewRoot.transform, "ObservedLane");
                ui.ViewUnitResolver = unitId => unitId == Task09Fixture.HeroUnitId.Value ? probeHero : null;
                ui.BindInputPorts(new ViewInputPorts(rig.Port, rig.Commands));

                Assert.That(ui.IsInputPortsBound, Is.True, "夹具前提：端口已接线");
                Assert.That(ui.LegacyTimelineWritesEnabled, Is.False,
                    "结构性守卫：接线后 LegacyTimelineWritesEnabled 必须恒为 false");
                Assert.That(ui.SyncPlayerBlocksFromLogic(), Is.EqualTo(1),
                    "块表必须由 Logic 只读投影产生（键 = ActionPlanId）");
                Assert.That(ui.CanRequestDelete(planId), Is.True,
                    "接线后 CanRequestDelete 必须为 true（B1 的旧 GroupId 语义缺陷已消除）");

                // ——— 3) 把旧写入的**三条通道**都变成可观测 ———
                // 通道 A：投影出的块 placement 上的旧排程委托（Recompute / EndDrag / 取消回滚会走它）。
                TimelineActionPlacement placement = LegacyPlacementOf(ui, planId);
                Assert.That(placement, Is.Not.Null, "接线后 UI 仍持有该块的 placement（标签/时长，Schedule 恒 null）");
                Assert.That(placement.Schedule, Is.Null, "接线后不存在任何旧时间线调度委托");
                int legacyScheduleCalls = 0;
                placement.Schedule = (delaySeconds, blockId) => legacyScheduleCalls++;

                // 通道 B：待确认放置自己的旧排程委托（FinalizePlacement 的旧放置路径会走它）。
                var pendingPlacement = new TimelineActionPlacement
                {
                    Owner = probeHero,
                    Kind = TimelineActionKind.Attack,
                    Label = "task09-b2-probe-placement",
                    DurationSeconds = 1f,
                    Lane = TimelineLane.Player
                };
                int pendingScheduleCalls = 0;
                pendingPlacement.Schedule = (delaySeconds, blockId) => pendingScheduleCalls++;
                ui.BeginPlacement(pendingPlacement);
                TimelineBlockView pendingGhost = PrivateField<TimelineBlockView>(ui, "_pendingGhost");
                Assert.That(pendingGhost, Is.Not.Null, "夹具前提：存在待确认的旧放置 ghost");

                // 通道 C：CancelGroup 的独立探针——探针时间线上挂同 id 的组事件。
                long probeEventId = probeTimeline.Schedule(
                    0f, new ProbeIntent(probeHero), "task09-b2-probe", planId, 0);
                Assert.That(probeEventId, Is.GreaterThan(0L));
                Assert.That(probeTimeline.ScheduledEventCount, Is.EqualTo(1),
                    "探针前提：CancelGroup 若被调用，事件数会掉到 0（而非静默通过）");

                // 通道 D：旧 ReserveGroupId 水位（FinalizePlacement 的旧放置会消耗它）。
                long groupIdWatermark = probeTimeline.ReserveGroupId();

                TimelineBlockView block = NewBlockView(planId);
                TimelineBlockView foreignBlock = NewBlockView(planId + 900L);

                string authorityBefore = rig.Fingerprint();
                string ingressBefore = rig.IngressFingerprint();
                long nextOrdinalBefore = rig.Entry.NextProducerOrdinal;
                long revisionBefore = rig.Simulation.ScheduleRevision;
                int blockedBefore = ui.LegacyTimelineWritesBlockedCount;

                // ——— 4) 逐个调用旧入口 ———
                ui.FinalizePlacement(pendingGhost);                                     // 旧放置确认
                ui.RequestReposition(block);                                            // 旧重排（接线后 = 本地手势）
                InvokeLegacy(ui, "BeginDragExisting", block);
                InvokeLegacy(ui, "EndDragExisting", block);
                InvokeLegacy(ui, "RecomputePlayerBlocksForLane", probeHero, TimelineLane.Player);
                ui.RequestDelete(foreignBlock);                                         // 非 Editable 目标：UI 侧拒绝
                ui.RequestDelete(null);                                                 // 空引用不得抛

                // ——— 5) 断言：旧写入零发生 + Logic 逐字段不变 ———
                Assert.That(legacyScheduleCalls, Is.EqualTo(0),
                    "旧排程通道（块 placement.Schedule）在接线后必须零调用");
                Assert.That(pendingScheduleCalls, Is.EqualTo(0),
                    "旧排程通道（待确认 placement.Schedule）在接线后必须零调用");
                Assert.That(probeTimeline.ScheduledEventCount, Is.EqualTo(1),
                    "旧 CancelGroup 通道在接线后必须零调用（探针组事件必须原样保留）");
                Assert.That(probeTimeline.ReserveGroupId(), Is.EqualTo(groupIdWatermark + 1L),
                    "旧 ReserveGroupId 水位在接线后不得被消耗（旧放置路径必须整体不可达）");
                Assert.That(rig.Fingerprint(), Is.EqualTo(authorityBefore),
                    "接线后旧入口不得改变任何 Logic 权威字段（含规范化哈希、入口水位、未来桶）");
                Assert.That(rig.IngressFingerprint(), Is.EqualTo(ingressBefore),
                    "接线后旧入口不得提交任何命令");
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(0));
                Assert.That(rig.Entry.NextProducerOrdinal, Is.EqualTo(nextOrdinalBefore));
                Assert.That(rig.Simulation.ScheduleRevision, Is.EqualTo(revisionBefore));
                Assert.That(ui.LegacyTimelineWritesBlockedCount, Is.GreaterThan(blockedBefore),
                    "守卫必须真的被走到——不是'前提不成立所以恰好没写'");
                Assert.That(ui.Controller.HasReorderDraft, Is.True,
                    "接线后重排改走新的本地草稿路径（纯视图状态）");
                Assert.That(ui.IsInputPortsBound, Is.True);

                // ——— 6) 正向对照：接线后删除确实改走唯一命令入口 ———
                ui.RequestDelete(block);
                Assert.That(legacyScheduleCalls, Is.EqualTo(0),
                    "即使走新路径，旧排程通道仍然零调用");
                Assert.That(pendingScheduleCalls, Is.EqualTo(0),
                    "即使走新路径，旧排程通道仍然零调用");
                Assert.That(probeTimeline.ScheduledEventCount, Is.EqualTo(1),
                    "即使走新路径，旧 CancelGroup 通道仍然零调用");
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(1),
                    "接线后的删除必须编码为 RemoveEditablePlanOperation 经唯一入口提交");
                Assert.That(rig.Port.LastSubmitted, Is.Not.Null);
                Assert.That(CommandScopes.KindOf(rig.Port.LastSubmitted.Scope),
                    Is.EqualTo(CommandScopeKind.ScheduleEdit), "删除走 ScheduleEdit scope，不走任何旧时间线写入");

                Task09Fixture.StepNext(rig.Simulation);   // Tick 2：统一终态协调器收口
                Assert.That(rig.Simulation.CurrentSnapshot.Plans.Count, Is.EqualTo(0),
                    "接线后的删除由 Logic 的统一终态协调器完成（不再有旧 CancelGroup 路径）");
                Assert.That(ui.CanRequestDelete(planId), Is.False,
                    "计划离开 Editable 集合后 UI 侧即不可删");
            }
            finally
            {
                rig.Dispose();
                UnityEngine.Object.DestroyImmediate(viewRoot);
                UnityEngine.Object.DestroyImmediate(timelineGo);
                UnityEngine.Object.DestroyImmediate(unitGo);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // 夹具与文档化反射接缝
        // ════════════════════════════════════════════════════════════════════

        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static RectTransform NewLane(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(600f, 60f);
            return rect;
        }

        private static TimelineBlockView NewBlockView(long actionPlanId)
        {
            var go = new GameObject("Task09B2ProbeBlock", typeof(RectTransform), typeof(TimelineBlockView));
            go.hideFlags = HideFlags.HideAndDontSave;
            var view = go.GetComponent<TimelineBlockView>();
            view.SetModel(actionPlanId, 0L, 0f, 1f, string.Empty, false);
            return view;
        }

        /// <summary>
        /// 文档化反射接缝：<c>BeginDragExisting</c> / <c>EndDragExisting</c> 是 <c>internal</c>、
        /// <c>RecomputePlayerBlocksForLane</c> 是 <c>private</c>，而
        /// <c>Assembly-CSharp-Editor</c> 不能访问 <c>Assembly-CSharp</c> 的 internal 成员
        /// （无 <c>InternalsVisibleTo</c>）。与任务 07/08 的既有做法一致，用反射调用
        /// <strong>真实</strong>旧入口，而不是为了测试放宽生产可见性。
        /// </summary>
        private static object InvokeLegacy(object target, string name, params object[] args)
        {
            MethodInfo[] candidates = target.GetType().GetMethods(InstanceMembers);
            for (int i = 0; i < candidates.Length; i++)
            {
                MethodInfo method = candidates[i];
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != args.Length) continue;

                bool matches = true;
                for (int p = 0; p < parameters.Length; p++)
                {
                    if (args[p] == null) continue;
                    if (!parameters[p].ParameterType.IsInstanceOfType(args[p])) { matches = false; break; }
                }
                if (!matches) continue;
                return method.Invoke(target, args);
            }
            Assert.Fail("反射接缝：找不到旧入口 " + name + "（" + args.Length + " 个参数）");
            return null;
        }

        private static T PrivateField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "反射接缝：私有字段 " + name + " 必须存在");
            return (T)field.GetValue(target);
        }

        private static TimelineActionPlacement LegacyPlacementOf(TimelineEditorUI ui, long blockId)
        {
            var map = PrivateField<Dictionary<long, TimelineActionPlacement>>(ui, "_placementsByGroupId");
            TimelineActionPlacement placement;
            return map.TryGetValue(blockId, out placement) ? placement : null;
        }

        /// <summary>探针意图：只用于给探针时间线挂一个可被 <c>CancelGroup</c> 命中的组事件。</summary>
        private sealed class ProbeIntent : CombatIntent
        {
            public ProbeIntent(CombatUnit owner)
                : base(owner, ProjectHero.Core.Actions.ActionType.Attack)
            {
            }

            public override void ExecuteSuccess()
            {
            }
        }

        // ————————————————————————————————————————————————————————————————
        // 产出 14 / 46：手势取消不写逻辑、不影响回放输入、不暂停执行
        // ————————————————————————————————————————————————————————————————

        /// <summary>
        /// 取消手势（右键/返回/关闭面板）只是视图状态：
        /// 逻辑字段逐字不变、没有命令提交、没有拒绝事件、战斗<strong>继续推进</strong>。
        ///
        /// 会让它红的实现缺陷：
        /// (a) 取消时回滚/删除计划、Intent、MovementSegment 或 Reservation；
        /// (b) 取消时提交一个"补偿"命令（入口水位会变）；
        /// (c) 取消时暂停执行/冻结时钟（后续 Step 不再推进 Tick）；
        /// (d) 取消把视图状态泄漏进回放输入记录（真实模拟中体现为入口 Player 事实被写入）。
        /// </summary>
        [Test]
        public void UiGestureCancelDoesNotMutateLogicOrReplay()
        {
            Rig rig = Rig.Create();
            try
            {
                Task09Fixture.AdvanceTo(rig.Simulation, 2);

                // 先建立一个真实的本地草稿（含多次预览），确认取消丢弃的是"草稿"而不是逻辑对象。
                rig.Controller.SelectUnit(Task09Fixture.HeroUnitId);
                rig.Controller.SelectAction("action.quick_slash.radius_1");
                Assert.That(rig.Controller.BeginPlacement(
                    Task09Fixture.HeroUnitId, "action.quick_slash.radius_1", 10L,
                    null, Task09Fixture.EnemyUnitId.Value), Is.True);
                rig.Controller.OpenActionPanel();
                rig.Controller.PreviewPlacementTick(11L);
                rig.Controller.PreviewPlacementTick(12L);
                rig.Controller.PreviewPlacementTick(13L);
                Assert.That(rig.Controller.HasPlacementDraft, Is.True);

                string authorityBefore = rig.Fingerprint();
                string ingressBefore = rig.IngressFingerprint();
                long tickBefore = rig.Simulation.Tick;

                // ——— 取消手势 ———
                rig.Controller.CancelGesture();

                Assert.That(rig.Controller.HasPlacementDraft, Is.False, "取消后本地草稿被丢弃");
                Assert.That(rig.Controller.HasActiveDraft, Is.False);
                Assert.That(rig.Controller.IsPanelOpen, Is.False, "取消同时关闭面板（仍是视图状态）");
                Assert.That(rig.Controller.SelectedActionSpecId, Is.Empty);

                // ——— 权威读数：逐字比较 ———
                Assert.That(rig.Fingerprint(), Is.EqualTo(authorityBefore),
                    "取消手势不得改变任何逻辑权威状态字段（含规范化哈希）");
                Assert.That(rig.IngressFingerprint(), Is.EqualTo(ingressBefore),
                    "取消手势不得提交命令，也不得进入回放输入记录");
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(0));
                Assert.That(rig.Entry.NextProducerOrdinal, Is.EqualTo(1L), "入口从未分配 ProducerOrdinal");

                // ——— 执行没有被暂停：唯一合法驱动序列仍然推进 Tick ———
                Task09Fixture.StepNext(rig.Simulation);
                Task09Fixture.StepNext(rig.Simulation);

                Assert.That(rig.Simulation.Tick, Is.EqualTo(tickBefore + 2L),
                    "取消手势不得暂停执行：Step 必须继续推进 Tick");
                Assert.That(rig.Fingerprint(), Is.Not.EqualTo(authorityBefore),
                    "Tick 推进后权威状态确实改变（证明前一次比较不是'什么都没在跑'的假绿）");

                // ——— 没有安静吞掉的拒绝：取消不产生任何入口级拒绝 ———
                CommandIngressRegistrySnapshot snapshot = rig.Simulation.CommandIngress.CaptureSnapshot();
                Assert.That(snapshot.PendingRejectionCount, Is.EqualTo(0),
                    "取消手势不产生任何命令拒绝记录");
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(0));
            }
            finally
            {
                rig.Dispose();
            }
        }

        // ————————————————————————————————————————————————————————————————
        // 产出 14 / 47：打开行动面板不建逻辑草稿、不暂停执行
        // ————————————————————————————————————————————————————————————————

        /// <summary>
        /// 打开行动面板是纯视图状态：不建立逻辑草稿、不提交任何命令、不暂停执行。
        ///
        /// 会让它红的实现缺陷：
        /// (a) 打开面板即创建/预登记计划、Lane 项或预算预留；
        /// (b) 打开面板即提交一个 Add 草稿（入口水位会变）；
        /// (c) 打开面板暂停时钟或 Step 门禁（后续 Step 不再推进 Tick）。
        /// </summary>
        [Test]
        public void OpeningActionPanelDoesNotCreateLogicalDraftOrPauseExecution()
        {
            Rig rig = Rig.Create();
            try
            {
                Task09Fixture.AdvanceTo(rig.Simulation, 1);
                long tickBefore = rig.Simulation.Tick;
                LogicSnapshot snapshotBefore = rig.Simulation.CurrentSnapshot;
                int planCountBefore = snapshotBefore.Plans.Count;
                int laneCountBefore = snapshotBefore.ActorLanes.Count;
                int intentCountBefore = snapshotBefore.Intents.Count;
                int segmentCountBefore = snapshotBefore.MovementSegments.Count;
                int reservationCountBefore = snapshotBefore.Reservations.Count;

                string authorityBefore = rig.Fingerprint();
                string ingressBefore = rig.IngressFingerprint();

                // 选中单位后打开面板（典型 UI 顺序）。
                rig.Controller.SelectUnit(Task09Fixture.HeroUnitId);
                rig.Controller.OpenActionPanel();

                Assert.That(rig.Controller.IsPanelOpen, Is.True);
                Assert.That(rig.Controller.PanelEverOpened, Is.True);
                Assert.That(rig.Controller.HasActiveDraft, Is.False, "打开面板不得建立任何本地草稿之外的逻辑草稿");
                Assert.That(rig.Controller.HasPlacementDraft, Is.False);
                Assert.That(rig.Controller.HasReorderDraft, Is.False);
                Assert.That(rig.Controller.HasRemoveDraft, Is.False);
                Assert.That(rig.Controller.ActiveGesture, Is.EqualTo(ViewGestureKind.None));

                // ——— 权威读数：逐字比较 ———
                Assert.That(rig.Fingerprint(), Is.EqualTo(authorityBefore),
                    "打开面板不得改变任何逻辑权威状态字段（含规范化哈希）");
                Assert.That(rig.IngressFingerprint(), Is.EqualTo(ingressBefore),
                    "打开面板不得提交任何命令");
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(0));

                LogicSnapshot snapshotAfter = rig.Simulation.CurrentSnapshot;
                Assert.That(snapshotAfter.Plans.Count, Is.EqualTo(planCountBefore),
                    "打开面板不得添加计划");
                Assert.That(snapshotAfter.ActorLanes.Count, Is.EqualTo(laneCountBefore),
                    "打开面板不得添加 Lane");
                Assert.That(snapshotAfter.Intents.Count, Is.EqualTo(intentCountBefore),
                    "打开面板不得添加 Intent");
                Assert.That(snapshotAfter.MovementSegments.Count, Is.EqualTo(segmentCountBefore),
                    "打开面板不得添加 MovementSegment");
                Assert.That(snapshotAfter.Reservations.Count, Is.EqualTo(reservationCountBefore),
                    "打开面板不得添加 Reservation");
                Assert.That(rig.Entry.NextProducerOrdinal, Is.EqualTo(1L));

                // ——— 执行没有被暂停 ———
                Task09Fixture.StepNext(rig.Simulation);
                Assert.That(rig.Simulation.Tick, Is.EqualTo(tickBefore + 1L),
                    "打开面板不得暂停执行");
                Assert.That(rig.Controller.IsPanelOpen, Is.True, "面板状态与执行无关，仍然打开");
            }
            finally
            {
                rig.Dispose();
            }
        }
    }
}
