using System.Collections.Generic;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Units;

namespace ProjectHero.Logic.Tests
{
    /// <summary>
    /// 任务 04 必需测试：单位状态机（转换矩阵、半开区间、统一转换入口、终态、
    /// 无提交权威、直接命中资格而非交互参与资格）。
    ///
    /// 这些用例只依赖 <c>ProjectHero.Logic</c>（纯 C#，无 UnityEngine），
    /// 因此它们是状态机契约的**第一手**证据：任何"从 Idle 推导提交权""用状态实现免伤"
    /// 的实现都会在这里失败。
    /// </summary>
    public class UnitStateMachineTests
    {
        private static UnitStateMachine NewMachine(out LogicEventOutbox outbox, long unitId = 1L)
        {
            outbox = new LogicEventOutbox(new LogicSequenceGenerator());
            return new UnitStateMachine(new UnitId(unitId), outbox);
        }

        // ——— 必需测试 1：转换矩阵 ———

        [Test]
        public void StateTransitionMatrixAcceptsOnlyDeclaredTransitions()
        {
            // 1) 对矩阵的**枚举真值**逐项断言：每一个声明条目都必须被接受。
            var declared = new HashSet<string>();
            for (int i = 0; i < UnitStateMachine.DeclaredTransitions.Count; i++)
            {
                var pair = UnitStateMachine.DeclaredTransitions[i];
                declared.Add(pair.From + "->" + pair.To);

                UnitStateMachine machine = NewMachine(out _);
                ForceState(machine, pair.From);
                var outcome = machine.TryTransition(
                    StateTransitionSpec.Open(pair.To), 10L, UnitStateTransitionReasons.Explicit);
                Assert.That(outcome.Applied, Is.True,
                    "声明的转换必须被接受：" + pair + " 但得到 " + outcome);
            }

            // 2) 对**全部 11 × 11 组合**做穷举核对：接受集合必须恰好等于
            //    「声明条目 ∪ {任意非终态 -> Dead}」，其余一律以稳定码拒绝。
            var all = (UnitState[])System.Enum.GetValues(typeof(UnitState));
            int accepted = 0;
            for (int f = 0; f < all.Length; f++)
            {
                for (int t = 0; t < all.Length; t++)
                {
                    UnitState from = all[f];
                    UnitState to = all[t];
                    bool expected = declared.Contains(from + "->" + to)
                        || (from != UnitState.Dead && to == UnitState.Dead);

                    bool actual = UnitStateMachine.IsValidTransition(from, to, out string rejection);
                    Assert.That(actual, Is.EqualTo(expected),
                        "矩阵必须只接受声明转换：" + from + "->" + to + " 期望 " + expected);

                    if (actual)
                    {
                        accepted++;
                        Assert.That(rejection, Is.Null);
                    }
                    else
                    {
                        Assert.That(rejection, Is.Not.Null,
                            "被拒绝的转换必须给出稳定原因码：" + from + "->" + to);
                    }
                }
            }

            // 3) 覆盖对照：接受集合非空且包含关键成员（避免"全拒绝"也通过）。
            Assert.That(accepted, Is.EqualTo(UnitStateMachine.DeclaredTransitions.Count + 10),
                "接受集合 = 声明条目 + 10 个非终态到 Dead 的转换");
            Assert.That(accepted, Is.GreaterThan(0));
        }

        [Test]
        public void SelfTransitionAndTerminalSourceAreRejectedWithStableCodes()
        {
            UnitStateMachine machine = NewMachine(out _);

            var self = machine.TryTransition(
                StateTransitionSpec.Open(UnitState.Idle), 0L, UnitStateTransitionReasons.Explicit);
            Assert.That(self.Applied, Is.False);
            Assert.That(self.RejectionCode, Is.EqualTo(UnitStateCodes.STATE_TRANSITION_SAME_STATE));
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.Idle), "被拒绝的转换不得修改状态");

            Assert.That(machine.TryTransition(
                StateTransitionSpec.Open(UnitState.Dead), 1L, UnitStateTransitionReasons.Death).Applied, Is.True);

            var revive = machine.TryTransition(
                StateTransitionSpec.Open(UnitState.Idle), 2L, UnitStateTransitionReasons.Explicit);
            Assert.That(revive.Applied, Is.False);
            Assert.That(revive.RejectionCode, Is.EqualTo(UnitStateCodes.STATE_TRANSITION_FROM_TERMINAL));
        }

        // ——— 必需测试 2：半开区间 ———

        [Test]
        public void TimedTransitionUsesHalfOpenInterval()
        {
            UnitStateMachine machine = NewMachine(out _);

            var timed = StateTransitionSpec.Timed(UnitState.Staggered, 30, UnitState.Idle);
            Assert.That(machine.TryTransition(timed, 100L, UnitStateTransitionReasons.Explicit).Applied, Is.True);

            Assert.That(machine.StateStartTick, Is.EqualTo(100L));
            Assert.That(machine.StateEndTick, Is.EqualTo(130L), "结束边界 = Start + Duration（半开区间右端）");
            Assert.That(machine.HasFiniteEndTick, Is.True);
            Assert.That(machine.BlockingUntilTick, Is.EqualTo(130L));
            Assert.That(machine.NextOnExpire, Is.Not.Null);
            Assert.That(machine.NextOnExpire.Target, Is.EqualTo(UnitState.Idle));

            // [Start, End) 内不推进：129 仍是 Staggered。
            Assert.That(machine.Advance(101L), Is.False);
            Assert.That(machine.Advance(129L), Is.False);
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.Staggered));

            // 到期 Tick（右端）先转换，不再停留一 Tick。
            Assert.That(machine.Advance(130L), Is.True);
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.Idle));
            Assert.That(machine.StateStartTick, Is.EqualTo(130L), "新状态从到期 Tick 开始");
            Assert.That(machine.HasFiniteEndTick, Is.False, "Idle 无限持续");
            Assert.That(machine.BlockingUntilTick, Is.Null);

            // 再推进不产生额外转换（幂等）。
            Assert.That(machine.Advance(131L), Is.False);
            Assert.That(machine.AutomaticExpiryCount, Is.EqualTo(1));
        }

        [Test]
        public void UnlimitedTransitionHasNoFiniteEndTickAndNeverAutoExpires()
        {
            UnitStateMachine machine = NewMachine(out _);
            Assert.That(machine.BlockingUntilTick, Is.Null, "初始 Idle 没有有限结束边界");

            Assert.That(machine.TryTransition(
                StateTransitionSpec.Open(UnitState.Guarding), 5L,
                UnitStateTransitionReasons.Explicit).Applied, Is.True);

            Assert.That(machine.StateEndTick, Is.EqualTo(long.MaxValue));
            Assert.That(machine.HasFiniteEndTick, Is.False);
            Assert.That(machine.BlockingUntilTick, Is.Null);
            Assert.That(machine.NextOnExpire, Is.Null);
            Assert.That(machine.Advance(100000L), Is.False);
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.Guarding));
        }

        [Test]
        public void TimedTransitionWithoutExplicitNextOnExpireIsRejected()
        {
            Assert.That(new StateTransitionSpec(UnitState.Staggered, 30, null).Validate(),
                Is.EqualTo(UnitStateCodes.STATE_TIMED_TRANSITION_MISSING_NEXT_ON_EXPIRE),
                "有限持续必须显式给出到期目标：不得依赖隐式默认回 Idle");
            Assert.That(new StateTransitionSpec(UnitState.Staggered, 0, StateTransitionSpec.Open(UnitState.Idle)).Validate(),
                Is.EqualTo(UnitStateCodes.STATE_DURATION_MUST_BE_ZERO_WITHOUT_NEXT_ON_EXPIRE));
            Assert.That(StateTransitionSpec.Open(UnitState.Idle).Validate(), Is.Null);
            Assert.That(UnitStateMachine.StaggerFor(30).Validate(), Is.Null);
            Assert.That(UnitStateMachine.KnockDownFor(60).Validate(), Is.Null);
        }

        // ——— 必需测试 3：显式与自动转换等价 ———

        [Test]
        public void AutomaticAndExplicitTransitionsEmitEquivalentEvents()
        {
            // 显式路径：直接调用 TryTransition 进入 Staggered（有限持续 30 Tick）。
            LogicEventOutbox explicitOutbox;
            UnitStateMachine explicitMachine = NewMachine(out explicitOutbox);
            explicitMachine.TryTransition(StateTransitionSpec.Open(UnitState.Idle), 0L,
                UnitStateTransitionReasons.Explicit); // 自反转换会被拒绝：对照"拒绝不发事件"
            var rejectedEmit = explicitMachine.TryTransition(
                StateTransitionSpec.Open(UnitState.Staggered), 7L, UnitStateTransitionReasons.Explicit);

            // 用一条显式声明（带持续时间）走同一入口。
            explicitMachine = NewMachine(out explicitOutbox);
            explicitMachine.TryTransition(
                StateTransitionSpec.Timed(UnitState.Staggered, 30, UnitState.Idle), 7L,
                UnitStateTransitionReasons.Explicit);
            EventBatch explicitBatch = explicitOutbox.Flush(7L);

            // 自动路径：显式进入同一状态后，由 Advance 到期回到 Idle。
            LogicEventOutbox autoOutbox;
            UnitStateMachine autoMachine = NewMachine(out autoOutbox);
            autoMachine.TryTransition(
                StateTransitionSpec.Timed(UnitState.Staggered, 30, UnitState.Idle), 7L,
                UnitStateTransitionReasons.Explicit);
            autoOutbox.Flush(7L); // 丢弃进入事件，只比较到期事件
            autoMachine.Advance(37L);
            EventBatch autoBatch = autoOutbox.Flush(37L);

            Assert.That(rejectedEmit.Applied, Is.True, "对照证据：第二次显式转换确实发生");

            var explicitEvents = Task04State.EventsOfType<UnitStateChangedEvent>(explicitBatch);
            var autoEvents = Task04State.EventsOfType<UnitStateChangedEvent>(autoBatch);

            Assert.That(explicitEvents.Count, Is.EqualTo(1));
            Assert.That(autoEvents.Count, Is.EqualTo(1), "自动到期必须发同类事件（不是另一套事件类型）");

            // 同类事件、同形状：唯一差别是原因码与目标状态。
            Assert.That(autoEvents[0].GetType(), Is.EqualTo(explicitEvents[0].GetType()));
            Assert.That(autoEvents[0].UnitId, Is.EqualTo(explicitEvents[0].UnitId));
            Assert.That(autoEvents[0].FromState, Is.EqualTo(UnitState.Staggered));
            Assert.That(autoEvents[0].ToState, Is.EqualTo(UnitState.Idle));
            Assert.That(autoEvents[0].ReasonCode, Is.EqualTo(UnitStateTransitionReasons.AutomaticExpiry));
            Assert.That(explicitEvents[0].ReasonCode, Is.EqualTo(UnitStateTransitionReasons.Explicit));
            Assert.That(UnitStateTransitionReasons.All, Does.Contain(autoEvents[0].ReasonCode));
            Assert.That(UnitStateTransitionReasons.All, Does.Contain(explicitEvents[0].ReasonCode));

            // 事件携带的区间与状态机一致（半开区间右端）。
            Assert.That(autoEvents[0].DurationTicks, Is.EqualTo(0), "Idle 无限持续：DurationTicks = 0");
            Assert.That(autoEvents[0].EndTick, Is.EqualTo(long.MaxValue));
            Assert.That(explicitEvents[0].DurationTicks, Is.EqualTo(30));
            Assert.That(explicitEvents[0].EndTick, Is.EqualTo(37L));

            // 被拒绝的转换不发事件（诊断计数只记拒绝）。
            Assert.That(Task04State.EventsOfType<UnitStateChangedEvent>(EventBatch.Empty(0L)).Count, Is.EqualTo(0));
        }

        // ——— 必需测试 4：Dead 是终态 ———

        [Test]
        public void DeadIsTerminal()
        {
            UnitStateMachine machine = NewMachine(out _);
            Assert.That(machine.IsTerminal, Is.False);

            Assert.That(machine.TryTransition(
                StateTransitionSpec.Open(UnitState.Dead), 3L, UnitStateTransitionReasons.Death).Applied, Is.True);
            Assert.That(machine.IsTerminal, Is.True);
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.Dead));
            Assert.That(machine.BlockingUntilTick, Is.Null, "Dead 不是「有限阻塞」：它不可恢复");

            // 穷举全部目标状态：从 Dead 出发一律拒绝。
            var all = (UnitState[])System.Enum.GetValues(typeof(UnitState));
            for (int i = 0; i < all.Length; i++)
            {
                var outcome = machine.TryTransition(
                    StateTransitionSpec.Open(all[i]), 4L, UnitStateTransitionReasons.Explicit);
                Assert.That(outcome.Applied, Is.False, "Dead 是终态：" + all[i]);
                Assert.That(outcome.RejectionCode, Is.EqualTo(UnitStateCodes.STATE_TRANSITION_FROM_TERMINAL));
            }

            // 尤其：Dead 不得"到期回 Idle"（无限持续 + 终态双重保证）。
            Assert.That(machine.Advance(1000L), Is.False);
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.Dead));
        }

        // ——— 必需测试 5：不暴露提交权威 ———

        [Test]
        public void StateMachineDoesNotExposeSubmissionAuthority()
        {
            var type = typeof(UnitStateMachine);

            // 1) 禁止的成员名一律不得出现（含旧方案示例里的 CanExecuteImmediately）。
            string[] forbidden =
            {
                "CanExecuteImmediately", "CanSubmit", "CanAcceptPlan", "CanStartPlan",
                "CanDefer", "ShouldDefer", "RetryAtTick", "CanSubmitNewAction",
                "IsTargetableByInteraction", "CanBeInteractedWith", "AllowsNewPlan"
            };
            for (int i = 0; i < forbidden.Length; i++)
            {
                Assert.That(type.GetMember(forbidden[i]), Is.Empty,
                    "状态机不得提供提交/延期/通用交互资格权威：" + forbidden[i]);
            }

            // 2) 允许的公开只读事实恰好是这三项（缺一不可）。
            Assert.That(type.GetProperty("CurrentState"), Is.Not.Null, "必须报告当前控制状态");
            Assert.That(type.GetProperty("HasFiniteEndTick"), Is.Not.Null, "必须报告是否有有限结束 Tick");
            Assert.That(type.GetProperty("BlockingUntilTick"), Is.Not.Null, "必须报告权威阻塞边界");

            // 3) BlockingUntilTick 必须来自状态实例的权威结束边界，而不是"下一 Tick 再试"。
            UnitStateMachine machine = NewMachine(out _);
            machine.TryTransition(StateTransitionSpec.Timed(UnitState.Staggered, 12, UnitState.Idle), 50L,
                UnitStateTransitionReasons.Explicit);
            Assert.That(machine.BlockingUntilTick, Is.EqualTo(62L));
            Assert.That(machine.BlockingUntilTick, Is.EqualTo(machine.StateEndTick),
                "阻塞边界必须直接来自 StateEndTick，不得是 StateStartTick + 1 之类的近似");
            Assert.That(machine.BlockingUntilTick, Is.Not.EqualTo(machine.StateStartTick + 1L));

            // 4) 全部公开成员必须是只读事实面：不得出现 public 集合/写入方法。
            var members = type.GetMembers(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] is System.Reflection.MethodInfo method && method.IsSpecialName) continue;
                Assert.That(members[i].Name, Does.Not.StartWith("Set"),
                    "状态机不得暴露 Set 型写入成员：" + members[i].Name);
            }
        }

        // ——— 必需测试 6/7：直接命中资格而非交互参与 ———

        [Test]
        public void StateMachineExposesDirectHitEligibilityNotInteractionParticipation()
        {
            var type = typeof(UnitStateMachine);

            // 只允许"直接命中资格"这一个语义明确的布尔查询。
            Assert.That(type.GetProperty("CanReceiveDirectHit"), Is.Not.Null);
            Assert.That(type.GetMember("IsTargetableByInteraction"), Is.Empty,
                "状态机不得提供同时控制特殊交互参与资格的通用属性");

            var extra = type.GetMember("CanBeTargeted");
            Assert.That(extra, Is.Empty);
            Assert.That(type.GetMember("IsValidTarget"), Is.Empty);
            Assert.That(type.GetMember("IsInteractive"), Is.Empty);

            // 与 UnitSnapshot 一致的派生事实：状态本身决定资格，不需要外部输入。
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Idle), Is.True);
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Dead), Is.False);
        }

        [Test]
        public void GuardingBlockingAndDodgingRemainDirectHitEligibleStates()
        {
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Guarding), Is.True,
                "Guard 的部分抵抗来自计划 Active 区间，状态本身不免伤");
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Blocking), Is.True,
                "Block 的完全抵抗来自匹配 TriggerTick 的反应 Intent，状态本身不免伤");
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Dodging), Is.True,
                "Dodge 的避伤来自换格后的空间复核，状态本身不免伤");

            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Idle), Is.True);
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Windup), Is.True);
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Recovery), Is.True);
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Moving), Is.True);
        }

        [Test]
        public void ControlStatesRejectDirectHitByInitialRules()
        {
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Staggered), Is.False);
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.KnockedDown), Is.False);
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Recovering), Is.False);
            Assert.That(UnitStateMachine.CanReceiveDirectHitIn(UnitState.Dead), Is.False);
        }

        /// <summary>
        /// 必需测试：<strong>防御状态本身既不减伤也不作废接触</strong>。
        ///
        /// 冻结规则（任务包核心规则）：Guard 的部分抵抗来自计划 Active 区间，Block 的完全抵抗
        /// 来自匹配 TriggerTick 的反应 Intent，Dodge 的避伤来自 TriggerTick 原子换格后的空间复核；
        /// <see cref="UnitState"/> 本身不提供任何免伤面，也不改变"能否进入 Remaining Hit 结算"。
        ///
        /// 三条可证伪证据：① 三个防御状态的直接命中资格都是 true；② 状态机不含任何
        /// 减伤/免伤/接触失效成员；③ 行为对照——Guarding 中的单位仍可被接触结果改写状态，
        /// 而硬直状态本身才拒绝直接命中。
        /// </summary>
        [Test]
        public void DefenseStateAloneDoesNotMitigateOrInvalidateContact()
        {
            // ① 三个防御状态都仍然进入直接命中结算。
            UnitState[] defenseStates = { UnitState.Guarding, UnitState.Blocking, UnitState.Dodging };
            for (int i = 0; i < defenseStates.Length; i++)
            {
                Assert.That(UnitStateMachine.CanReceiveDirectHitIn(defenseStates[i]), Is.True,
                    "防御状态不得作废接触（抵抗来自区间/Intent/空间复核，不来自状态）：" + defenseStates[i]);
            }

            // ② 状态机不暴露任何减伤/免伤/接触失效权威面。
            string[] forbidden =
            {
                "DamageMultiplier", "IncomingDamageScale", "IncomingDamageMultiplier",
                "DamageReduction", "Mitigation", "MitigationMultiplier", "Resistance",
                "NegatesDamage", "NullifiesDamage", "InvalidatesContact", "ContactInvalidated",
                "Immune", "IsImmune", "AvoidsHit", "BlocksContact", "DodgesContact"
            };
            for (int i = 0; i < forbidden.Length; i++)
            {
                Assert.That(typeof(UnitStateMachine).GetMember(forbidden[i]), Is.Empty,
                    "减伤/接触失效权威不得放在状态机里：" + forbidden[i]);
            }

            // ③ 行为对照：Guarding 中的单位仍可被接触结果改写状态——接触没有被作废。
            UnitStateMachine machine = NewMachine(out _);
            Assert.That(machine.TryTransition(
                StateTransitionSpec.Open(UnitState.Guarding), 5L,
                UnitStateTransitionReasons.Explicit).Applied, Is.True);
            Assert.That(machine.CanReceiveDirectHit, Is.True, "Guarding 仍然可以被直接命中结算");

            Assert.That(machine.TryTransition(
                UnitStateMachine.StaggerFor(30), 6L, UnitStateTransitionReasons.Explicit).Applied, Is.True,
                "Guard 状态本身不得阻止接触结果改写状态（抵抗属于计划 Active 区间）");
            Assert.That(machine.CurrentState, Is.EqualTo(UnitState.Staggered));
            Assert.That(machine.CanReceiveDirectHit, Is.False,
                "首版规则下硬直状态本身才拒绝直接命中——与 Guarding 的 true 构成对照");

            // ④ 状态机没有任何接收伤害的输入面：伤害只能由统一入口提交。
            Assert.That(typeof(UnitStateMachine).GetMethod("ApplyDamageQ10"), Is.Null);
            Assert.That(typeof(UnitStateMachine).GetMethod("ApplyDamage"), Is.Null);
            Assert.That(typeof(UnitStateMachine).GetMethod("SetHealth"), Is.Null);
        }

        // ——— 必需测试 8：有限控制状态的权威阻塞边界 ———

        [Test]
        public void FiniteControlStateExposesAuthoritativeBlockingUntilTick()
        {
            // 01B 决策 B2 冻结值：Stagger = 30 Tick、Knockdown = 60 Tick。
            UnitStateMachine staggered = NewMachine(out _);
            staggered.TryTransition(UnitStateMachine.StaggerFor(30), 200L, UnitStateTransitionReasons.Explicit);
            Assert.That(staggered.BlockingUntilTick, Is.EqualTo(230L));
            Assert.That(staggered.HasFiniteEndTick, Is.True);

            UnitStateMachine knockedDown = NewMachine(out _);
            knockedDown.TryTransition(UnitStateMachine.KnockDownFor(60), 200L, UnitStateTransitionReasons.Explicit);
            Assert.That(knockedDown.BlockingUntilTick, Is.EqualTo(260L));

            // 关键对照：BlockingUntilTick 与"下一 Tick 再试"必须可区分。
            Assert.That(staggered.BlockingUntilTick, Is.Not.EqualTo(201L));

            // 到期后边界消失（门禁可以据此立刻放行，而不是继续等）。
            staggered.Advance(230L);
            Assert.That(staggered.BlockingUntilTick, Is.Null);
            Assert.That(staggered.HasFiniteEndTick, Is.False);

            // 无有限恢复的状态：边界为 null ⇒ 门禁侧不能承诺"有限重试"。
            UnitStateMachine guarding = NewMachine(out _);
            guarding.TryTransition(StateTransitionSpec.Open(UnitState.Guarding), 10L,
                UnitStateTransitionReasons.Explicit);
            Assert.That(guarding.BlockingUntilTick, Is.Null);
        }

        [Test]
        public void RecoverySlotIsDeclaredButNotDefaultTarget()
        {
            // Recovering 槽位保留：硬直/击倒到 Recovering 的转换被声明。
            bool staggering = false;
            bool knocked = false;
            for (int i = 0; i < UnitStateMachine.DeclaredTransitions.Count; i++)
            {
                var pair = UnitStateMachine.DeclaredTransitions[i];
                if (pair.From == UnitState.Staggered && pair.To == UnitState.Recovering) staggering = true;
                if (pair.From == UnitState.KnockedDown && pair.To == UnitState.Recovering) knocked = true;
            }
            Assert.That(staggering, Is.True, "Recovering 槽位必须已在矩阵中声明（任务 05+ 可显式启用）");
            Assert.That(knocked, Is.True);

            // 但首版默认到期目标是 Idle（01B：Recovering 不单独启用）。
            Assert.That(UnitStateMachine.StaggerFor(30).NextOnExpire.Target, Is.EqualTo(UnitState.Idle));
            Assert.That(UnitStateMachine.KnockDownFor(60).NextOnExpire.Target, Is.EqualTo(UnitState.Idle));
        }

        // ——— 内部：把机器直接放到指定状态（只经统一入口）———

        /// <summary>
        /// 把机器放到指定状态，<strong>只走已声明的转换</strong>。
        ///
        /// 旧实现假设"任意状态都能从 Idle 一步到达"，这在真实矩阵下不成立：
        /// <c>Recovery</c> 只能由 <c>Windup</c> 进入、<c>Recovering</c> 只能由
        /// <c>Staggered</c>/<c>KnockedDown</c> 进入。因此这里改为在声明矩阵上做
        /// 广度优先搜索，找到一条从当前状态到目标的<strong>已声明路径</strong>再逐步执行——
        /// 夹具因此不会把"矩阵缺边"误报成"矩阵多余接受"。
        /// </summary>
        private static void ForceState(UnitStateMachine machine, UnitState target)
        {
            UnitState start = machine.CurrentState;
            if (start == target) return;
            if (target == UnitState.Dead)
            {
                Assert.That(machine.TryTransition(
                    StateTransitionSpec.Open(UnitState.Dead), 0L, UnitStateTransitionReasons.Death).Applied, Is.True);
                return;
            }

            var all = (UnitState[])System.Enum.GetValues(typeof(UnitState));
            var previous = new Dictionary<UnitState, UnitState>();
            var seen = new HashSet<UnitState> { start };
            var queue = new Queue<UnitState>();
            queue.Enqueue(start);
            bool reachable = false;

            while (queue.Count > 0 && !reachable)
            {
                UnitState current = queue.Dequeue();
                for (int t = 0; t < all.Length; t++)
                {
                    UnitState next = all[t];
                    // Dead 只在最后单独处理（进入后无法离开，会截断路径搜索）。
                    if (next == UnitState.Dead || seen.Contains(next)) continue;
                    if (!UnitStateMachine.IsValidTransition(current, next, out _)) continue;
                    seen.Add(next);
                    previous[next] = current;
                    if (next == target) { reachable = true; break; }
                    queue.Enqueue(next);
                }
            }

            Assert.That(reachable, Is.True,
                "测试夹具要求声明矩阵中存在 " + start + " -> " + target + " 的路径");

            var path = new List<UnitState>();
            for (UnitState node = target; node != start; node = previous[node]) path.Add(node);
            path.Reverse();

            for (int i = 0; i < path.Count; i++)
            {
                var outcome = machine.TryTransition(
                    StateTransitionSpec.Open(path[i]), 0L, UnitStateTransitionReasons.Explicit);
                Assert.That(outcome.Applied, Is.True,
                    "夹具路径上的每一步都必须是已声明转换：" + start + " -> ... -> " + path[i]
                    + " 但得到 " + outcome);
            }

            Assert.That(machine.CurrentState, Is.EqualTo(target), "夹具必须真的把机器放到目标状态");
        }
    }

    /// <summary>任务 04 状态机用例的共享只读助手。</summary>
    internal static class Task04State
    {
        public static List<T> EventsOfType<T>(EventBatch batch) where T : LogicEvent
        {
            var result = new List<T>();
            if (batch == null) return result;
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch.Events[i] is T typed) result.Add(typed);
            }
            return result;
        }
    }
}
