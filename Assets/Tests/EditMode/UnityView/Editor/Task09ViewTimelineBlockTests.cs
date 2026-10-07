using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime.Input;
using ProjectHero.Core.Entities;
using ProjectHero.Core.Timeline;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.UI.Timeline;
using UnityEngine;

namespace ProjectHero.Tests.UnityView.Editor
{
    /// <summary>
    /// 任务 09 / B2 轮：<strong>时间线块标识语义（§3.2）、秒→Tick 唯一换算点（§3.3）、
    /// UI 侧"不可删/不可拖"门槛（§3.5）</strong>。
    ///
    /// <para>
    /// 与 B1 的三条用例同一证明口径：全部前提都来自<strong>真实</strong>
    /// <c>BattleSimulation</c>（真实配置链 + 真实命令入口 + 真实统一终态协调器），
    /// 不建立第二套配置模型，也不用测试替身冒充 Logic。
    /// 唯一的"替身"是注入给视图的 <see cref="IViewLogicPort"/>（真实模拟的只读投影），
    /// 它本身就是任务 10 适配器要实现的同一个接口。
    /// </para>
    ///
    /// <para>
    /// <strong>反射接缝</strong>：<c>TimelineEditorUI</c> 在 <c>Assembly-CSharp</c>，
    /// 而本文件在 <c>Assembly-CSharp-Editor</c>（无 <c>InternalsVisibleTo</c>），
    /// 因此它的私有成员（<c>_playerBlocks</c>）只能经反射读取。
    /// 反射只用于<strong>读取视图私有状态作为读数</strong>，不改变被测实现的任何校验强度。
    /// </para>
    /// </summary>
    public class Task09ViewTimelineBlockTests
    {
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ════════════════════════════════════════════════════════════════════
        // §3.2 + §3.3：块标识 = Logic ActionPlanId；秒→Tick 只有一个换算点
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>块标识继续用旧 <c>BattleTimeline.ReserveGroupId()</c>（或
        /// <c>GetInstanceID()</c>/注册顺序）⇒ "块键 = Logic ActionPlanId" 与
        /// "键不等于旧组号键空间"两条断言红，且 <c>CanRequestDelete</c> 会因
        /// "拿旧组号去问 Logic"而恒为 false；</item>
        /// <item>投影把 Locked/Running/反应计划也放进来 ⇒ 块数断言红；</item>
        /// <item>秒→Tick 换算散落/取整口径不同 ⇒ 三个换算恒等式红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void ViewTimelineBlockIdentityComesFromLogicPlanId()
        {
            var rig = Rig.CreateWithWindow();
            var viewRoot = new GameObject("Task09B2IdentityRoot", typeof(RectTransform));
            var timelineGo = new GameObject("Task09B2IdentityTimeline", typeof(BattleTimeline));
            var unitGo = new GameObject("Task09B2IdentityUnit", typeof(CombatUnit));
            viewRoot.hideFlags = HideFlags.HideAndDontSave;
            timelineGo.hideFlags = HideFlags.HideAndDontSave;
            unitGo.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var probeTimeline = timelineGo.GetComponent<BattleTimeline>();
                var probeHero = unitGo.GetComponent<CombatUnit>();

                // 关键夹具设计：**先把旧键空间推远**（1..5），再走真实路径造计划。
                // 这样"旧 ReserveGroupId 组号"与"Logic ActionPlanId"在数值上不可能撞车：
                // 若实现误用旧 <c>ReserveGroupId()</c> 当块键，它拿到的一定是 6/7/…，
                // 而真实计划 ID 是 1 —— 断言因此有区分力。
                for (int i = 0; i < 5; i++) probeTimeline.ReserveGroupId();
                long legacyCandidateA = probeTimeline.ReserveGroupId();   // 6
                long legacyCandidateB = probeTimeline.ReserveGroupId();   // 7
                Assert.That(legacyCandidateA, Is.Not.EqualTo(legacyCandidateB));

                // ——— 真实路径：一个仍为 Editable 的普通计划 ———
                Assert.That(rig.Entry.Submit(Task09Fixture.AddHeroAttackCommand(
                    0L, 10L, 0L, new WindowId(1L))), Is.Null);
                Task09Fixture.StepNext(rig.Simulation);   // Tick 0
                Task09Fixture.StepNext(rig.Simulation);   // Tick 1

                LogicSnapshot snapshot = rig.Simulation.CurrentSnapshot;
                Assert.That(snapshot.Plans.Count, Is.EqualTo(1));
                long planId = snapshot.Plans[0].ActionPlanId;
                Assert.That(planId, Is.Not.EqualTo(legacyCandidateA),
                    "夹具前提：Logic 计划 ID 必须与'UI 若误用旧组号会拿到的那两个值'不同");
                Assert.That(planId, Is.Not.EqualTo(legacyCandidateB));
                Assert.That(snapshot.Plans[0].State,
                    Is.EqualTo((int)ProjectHero.Logic.Actions.ActionPlanState.Editable));
                Assert.That(snapshot.Plans[0].StartTick, Is.EqualTo(10L));

                var ui = NewView(viewRoot, probeTimeline, probeHero, rig);

                // ——— 投影：键必须是 ActionPlanId ———
                Assert.That(ui.SyncPlayerBlocksFromLogic(), Is.EqualTo(1),
                    "只投影'存在 + 属主受控 + 普通计划 + Editable'的块");
                IDictionary blocks = PrivateField<IDictionary>(ui, "_playerBlocks");
                Assert.That(blocks.Count, Is.EqualTo(1));
                Assert.That(blocks.Contains(planId), Is.True,
                    "块键必须**就是** Logic 的 ActionPlanId");
                Assert.That(blocks.Contains(legacyCandidateA), Is.False,
                    "块键不得是（投影时刻的）旧 ReserveGroupId 组号 " + legacyCandidateA);
                Assert.That(blocks.Contains(legacyCandidateB), Is.False,
                    "块键不得是旧 ReserveGroupId 组号 " + legacyCandidateB);

                // ——— UI 侧只读判定与 Logic 投影一致 ———
                Assert.That(ui.CanRequestDelete(planId), Is.True);
                Assert.That(ui.CanEditBlockInUi(planId), Is.True);
                Assert.That(ui.IsPlanEditableInUi(planId), Is.True);
                Assert.That(ui.CanRequestDelete(legacyCandidateA), Is.False,
                    "旧组号在 Logic 上查不到计划 ⇒ UI 侧必须不可删/不可拖");
                Assert.That(ui.CanRequestDelete(legacyCandidateB), Is.False);
                Assert.That(ui.CanRequestDelete(planId + 990L), Is.False);

                // ——— §3.3：秒 / Tick 只有一个换算点，tick 率来自旧时间线唯一常量 ———
                Assert.That(ui.ViewSecondsOfTick(BattleTimeline.TicksPerSecond), Is.EqualTo(1.0).Within(1e-9),
                    "视图换算点的 tick 率必须**就是** BattleTimeline.TicksPerSecond（唯一来源）");
                Assert.That(ui.ViewSecondsToTick(1.0), Is.EqualTo((long)BattleTimeline.TicksPerSecond),
                    "1 秒必须换回恰好 TicksPerSecond 个 Tick");
                Assert.That(ui.ViewSecondsToTick(ui.ViewSecondsOfTick(10L)), Is.EqualTo(10L),
                    "同一换算点的往返必须恒等（取整口径只有一处）");
                Assert.That(ui.ViewSecondsToTick(0.0), Is.EqualTo(0L));
                Assert.That(ui.ViewSecondsToTick(-5.0), Is.EqualTo(0L),
                    "Tick 上没有负数：非法输入夹到 0，绝不顺延到当前 Tick");
                Assert.That(ui.ViewSecondsOfTick(0L), Is.EqualTo(0.0));

                // 投影出的位点必须来自同一换算点（不是散落的秒值）。
                long projectedStartTick = 10L;
                Assert.That(ui.ViewSecondsToTick(ui.ViewSecondsOfTick(projectedStartTick)),
                    Is.EqualTo(snapshot.Plans[0].StartTick));
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
        // §3.5：UI 侧口径 —— 不可编辑计划在 UI 上就不可删；且 UI 不是权威
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// UI 侧口径（供主理人与 A2 对齐）：
        /// <list type="number">
        /// <item>真值来源只有一个：<c>IViewLogicPort.TryFindEditablePlan</c>
        /// = "存在 + 属主受控 + 普通计划 + <c>Editable</c>"。
        /// UI <strong>不</strong>自己判状态、不维护第二套口径；</item>
        /// <item>Locked / Running / 反应计划天然不在 <c>EditablePlansOf</c> 里，
        /// 因此既不出现在可拖拽块集合中，<c>CanRequestDelete</c> 也为 false
        /// （删除/重排入口直接不响应，连命令都不构造）；</item>
        /// <item>UI 判定<strong>不是</strong>授权：绕过 UI 谓词直接提交
        /// <c>RemoveEditablePlanOperation</c>，Logic 仍以稳定拒绝码拒绝。</item>
        /// </list>
        ///
        /// 会让它失败的实现缺陷：
        /// <list type="bullet">
        /// <item>UI 侧自己按状态字段判可删（而不是问端口）⇒ 谓词一致性断言红；</item>
        /// <item>Locked/Running 计划仍能被拖/被删 ⇒ 不可删断言红；</item>
        /// <item>UI 判定被当成权威（Logic 放行了本该拒绝的删除）⇒ 拒绝事件断言红。</item>
        /// </list>
        /// </summary>
        [Test]
        public void ViewTimelineUiRefusesNonEditablePlansWithoutBecomingAuthority()
        {
            var rig = Rig.CreateWithWindow();
            var viewRoot = new GameObject("Task09B2UiGateRoot", typeof(RectTransform));
            var timelineGo = new GameObject("Task09B2UiGateTimeline", typeof(BattleTimeline));
            var unitGo = new GameObject("Task09B2UiGateUnit", typeof(CombatUnit));
            viewRoot.hideFlags = HideFlags.HideAndDontSave;
            timelineGo.hideFlags = HideFlags.HideAndDontSave;
            unitGo.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                // Tick 0：窗口 W1 打开。
                Task09Fixture.StepNext(rig.Simulation);

                // Tick 1 目标的 Add：在 Tick 1 被创建并立刻被启动门禁锁定 ⇒ Running。
                Assert.That(rig.Entry.Submit(Task09Fixture.AddHeroAttackCommand(
                    1L, 1L, 0L, new WindowId(1L))), Is.Null);
                Task09Fixture.StepNext(rig.Simulation);   // Tick 1

                LogicSnapshot snapshot = rig.Simulation.CurrentSnapshot;
                Assert.That(snapshot.Plans.Count, Is.EqualTo(1), "夹具前提：计划已创建");
                long planId = snapshot.Plans[0].ActionPlanId;
                Assert.That(snapshot.Plans[0].State,
                    Is.EqualTo((int)ProjectHero.Logic.Actions.ActionPlanState.Running),
                    "夹具前提：该计划已被启动门禁锁定为 Running");

                var probeTimeline = timelineGo.GetComponent<BattleTimeline>();
                var probeHero = unitGo.GetComponent<CombatUnit>();
                var ui = NewView(viewRoot, probeTimeline, probeHero, rig);

                // ——— 1) Running 计划在 UI 上就不可删 / 不可拖 ———
                Assert.That(ui.CanRequestDelete(planId), Is.False,
                    "Running 计划在 UI 侧必须不可删（按钮/拖拽不可用）");
                Assert.That(ui.CanEditBlockInUi(planId), Is.False);
                Assert.That(ui.IsPlanEditableInUi(planId), Is.False);
                Assert.That(ui.SyncPlayerBlocksFromLogic(), Is.EqualTo(0),
                    "Running 计划不得进入可拖拽块集合");
                IDictionary blocks = PrivateField<IDictionary>(ui, "_playerBlocks");
                Assert.That(blocks.Contains(planId), Is.False,
                    "Locked/Running/反应计划天然不在 EditablePlansOf 里，因此不在块表里");

                // ——— 2) UI 与端口口径逐条一致（UI 没有第二套口径）———
                foreach (ActionPlanSnapshot plan in snapshot.Plans)
                {
                    ActionPlanSnapshot ignored;
                    bool portSaysEditable =
                        rig.Port.TryFindEditablePlan(new ActionPlanId(plan.ActionPlanId), out ignored);
                    Assert.That(ui.CanRequestDelete(plan.ActionPlanId), Is.EqualTo(portSaysEditable),
                        "UI 的 CanRequestDelete 必须逐字等于 IViewLogicPort.TryFindEditablePlan");
                    Assert.That(portSaysEditable, Is.False,
                        "夹具前提：本 Tick 的计划全部不可编辑");
                }

                // ——— 3) UI 判定不是权威：绕过它直接提交，Logic 仍然拒绝 ———
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(0),
                    "UI 侧拒绝时连命令都不构造");
                CommandRequest bypass = rig.Commands.CreateScheduleEdit(
                    ViewCommandFactory.EditPayload(new RemoveEditablePlanOperation(
                        new ActionPlanId(planId),
                        ProjectHero.Logic.Actions.ActionTerminationReason.CancelledByCommand)));
                Assert.That(bypass, Is.Not.Null);
                Assert.That(rig.Port.SubmitCommand(bypass), Is.Null,
                    "入口接受的是请求本身（结构合法），拒绝发生在 Logic 处理阶段");
                Assert.That(rig.Port.SubmissionCount, Is.EqualTo(1));

                StepResult stepAtTwo = Task09Fixture.StepNext(rig.Simulation);   // Tick 2
                List<CommandRejectedEvent> rejections =
                    stepAtTwo.Events.Events.OfType<CommandRejectedEvent>().ToList();
                Assert.That(rejections.Count, Is.EqualTo(1),
                    "绕过 UI 谓词的删除必须被 Logic 拒绝——UI 判断不是权威");
                string rejectionCode = rejections[0].ReasonCode;
                Assert.That(
                    rejectionCode == ScheduleCodes.SCHEDULE_PLAN_NOT_EDITABLE
                    || rejectionCode == ScheduleCodes.SCHEDULE_PLAN_LOCKED_CANNOT_BE_EDITED,
                    Is.True,
                    "不可编辑计划必须以既有的稳定码拒绝（不得新增第三套码）；实际=" + rejectionCode);

                LogicSnapshot afterRejection = rig.Simulation.CurrentSnapshot;
                Assert.That(afterRejection.Plans.Count, Is.EqualTo(1),
                    "被拒绝的删除必须零局部写入（计划仍在）");
                Assert.That(afterRejection.Plans[0].ActionPlanId, Is.EqualTo(planId));
                Assert.That(afterRejection.ScheduleRevision, Is.EqualTo(snapshot.ScheduleRevision),
                    "被拒绝的事务不推进排程修订号");
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
        // 夹具
        // ════════════════════════════════════════════════════════════════════

        /// <summary>真实模拟 + 真实入口 + 接线后的 <c>TimelineEditorUI</c>。</summary>
        private sealed class Rig
        {
            public BattleSimulation Simulation;
            public SimulationViewLogicPort Port;
            public ViewCommandFactory Commands;
            public CommandIngressEntry Entry;

            public static Rig CreateWithWindow()
            {
                var simulation = Task09Fixture.NewSimulation(Task09Fixture.WindowAtZero());
                var port = new SimulationViewLogicPort(simulation, Task09Fixture.PlayerControllerId);
                return new Rig
                {
                    Simulation = simulation,
                    Port = port,
                    Commands = new ViewCommandFactory(port),
                    Entry = Task09Fixture.PlayerEntry(simulation)
                };
            }

            public void Dispose() => Simulation.Dispose();
        }

        private static TimelineEditorUI NewView(
            GameObject root, BattleTimeline timeline, CombatUnit heroUnit, Rig rig)
        {
            var ui = root.AddComponent<TimelineEditorUI>();
            ui.Timeline = timeline;
            ui.PlayerLane = NewLane(root.transform, "PlayerLane");
            ui.ObservedLane = NewLane(root.transform, "ObservedLane");
            ui.ViewUnitResolver = unitId => unitId == Task09Fixture.HeroUnitId.Value ? heroUnit : null;
            ui.BindInputPorts(new ViewInputPorts(rig.Port, rig.Commands));
            return ui;
        }

        private static RectTransform NewLane(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(600f, 60f);
            return rect;
        }

        private static T PrivateField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "反射接缝：私有字段 " + name + " 必须存在");
            return (T)field.GetValue(target);
        }
    }
}
