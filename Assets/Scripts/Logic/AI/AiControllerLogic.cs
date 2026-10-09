using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong><c>AiControllerLogic</c></strong>（任务 09「必须产出」9/10/15）：
    /// 一个 AI 控制者的<strong>唯一</strong>决策与提交实现。
    ///
    /// 冻结语义：
    /// <list type="number">
    /// <item><strong>入参即权限</strong>：它只实现 <see cref="IDecisionObserver"/>，因此唯一被调用点
    /// 是阶段 18 的 AI/系统观察接缝（<c>SimulationPhaseSystems</c> 的既有接缝，本轮
    /// <strong>不</strong>新增任何 Step 阶段）。它拿到的是与玩家<strong>同一份</strong>只读决策快照，
    /// 以及 <c>nextTick</c>。</item>
    /// <item><strong>只投下一 Tick</strong>：命令一律经 <c>SubmitAtDefaultTick</c> 提交，目标 Tick 由
    /// 入口按 <c>FrozenThroughTick + CommandIngressLeadTicks</c> 决定。AI 在类型上<strong>无法</strong>
    /// 声明提前量，也<strong>无法</strong>读取 N+1 的新机会后回写 N+1。</item>
    /// <item><strong>只能产出 <see cref="CommandRequest"/></strong>：不创建 Envelope、不创建机会/选项/计划、
    /// 不分配 <c>ProducerOrdinal</c>/<c>CommandSequence</c>、不绕过 <c>ScheduleRevision</c>。</item>
    /// <item><strong>对窗口只提交命令</strong>：窗口权威（<c>TurnWindowManager</c>）与排程写入
    /// （<c>ScheduleEditor</c>）都不是它的依赖——"AI 直接排计划/直接关窗"在实现上不可达。</item>
    /// <item><strong>未来状态进快照</strong>：<see cref="CaptureRuntimeStatesOrdered"/> 暴露
    /// （决策计数、最后决策 Tick、下一次思考 Tick、RNG 状态），由 <c>LogicSnapshot</c> 全部哈希，
    /// 因此回放时"由相同初始输入与 RNG 从 Tick 0 重新生成"是可验证的。</item>
    /// </list>
    /// </summary>
    public sealed class AiControllerLogic : IDecisionObserver, IAiRuntimeStateSource
    {
        public const int MaximumDiagnosticDecisions = 512;
        private readonly ulong _battleSeed;
        private readonly List<string> _registrationOrder = new List<string>(4);
        private readonly Dictionary<string, AiRuntimeState> _states =
            new Dictionary<string, AiRuntimeState>(StringComparer.Ordinal);
        private readonly List<AiDecision> _decisions = new List<AiDecision>();
        private readonly List<CommandIngressRejection> _rejections = new List<CommandIngressRejection>();

        private IAiReactionOpportunitySource _opportunities;
        private IAiActionPlanLookup _planLookup;
        private IMovementPathCalculator _pathCalculator;

        public AiControllerLogic(
            ulong battleSeed,
            IAiReactionOpportunitySource opportunities = null,
            IAiActionPlanLookup planLookup = null,
            IMovementPathCalculator pathCalculator = null)
        {
            _battleSeed = battleSeed;
            _opportunities = opportunities;
            _planLookup = planLookup;
            _pathCalculator = pathCalculator;
        }

        /// <summary>本场 RNG 种子（与快照 <c>Rng</c> 同源；决策种子由它与 ControllerId/Tick 派生）。</summary>
        public ulong BattleSeed => _battleSeed;

        /// <summary>已公开反应机会的只读来源（未装配时为 <c>null</c> = 没有反应候选）。</summary>
        public IAiReactionOpportunitySource Opportunities => _opportunities;

        /// <summary>权威计划的只读检索面（未装配时为 <c>null</c> = <c>Move</c> 候选 fail-closed）。</summary>
        public IAiActionPlanLookup PlanLookup => _planLookup;

        /// <summary>任务 06 的路径重算端口（与玩家预览、<c>ScheduleEditor</c> 共用同一实例）。</summary>
        public IMovementPathCalculator PathCalculator => _pathCalculator;

        /// <summary>
        /// <strong>后置接线</strong>：装配方在创建模拟之后把它自己的只读端口接上
        /// （<c>BattleSimulation.AiReactionOpportunities</c> / <c>AiActionPlanLookup</c> /
        /// <c>MovementPathCalculator</c>）。
        ///
        /// 它<strong>只</strong>做引用赋值：端口本身没有任何写入面，因此"AI 现场给自己加权限"
        /// 在类型上不成立；未接线时反应候选为空、<c>Move</c> 候选 fail-closed（不是静默降级）。
        /// </summary>
        public void AttachPorts(
            IAiReactionOpportunitySource opportunities,
            IAiActionPlanLookup planLookup,
            IMovementPathCalculator pathCalculator)
        {
            _opportunities = opportunities;
            _planLookup = planLookup;
            _pathCalculator = pathCalculator;
        }

        /// <summary>已注册的 AI 控制者（注册顺序；只增不改）。</summary>
        public IReadOnlyList<string> RegisteredControllerIds => _registrationOrder;

        /// <summary>
        /// <strong>注册一个受信 AI Controller</strong>。注册入口<strong>不是</strong>公共命令面：
        /// 调用者必须给出已验证定义里的 <c>ControllerBinding</c>，并只能是
        /// <c>CommandSourceKind.Ai</c>；ControllerId 直接取该绑定的值，调用者无法自报身份。
        /// </summary>
        public void RegisterController(Definitions.ControllerBinding binding)
        {
            if (binding == null)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID, "AI binding is null");
            if (binding.SourceKind != Definitions.CommandSourceKind.Ai)
                throw new ProjectHero.Logic.LogicDefinitionException(
                    CommandCodes.COMMAND_INGRESS_CONTROLLER_INVALID,
                    "AI controller must be registered from an Ai binding; got " + binding.SourceKind);

            string key = binding.ControllerId.Value;
            if (key == null || _states.ContainsKey(key)) return;
            _registrationOrder.Add(key);
            _states[key] = new AiRuntimeState(key);
        }

        /// <summary>是否已注册该 Controller（只读）。</summary>
        public bool IsRegistered(ControllerId controllerId)
            => controllerId.Value != null && _states.ContainsKey(controllerId.Value);

        /// <summary>
        /// 本观察者代表<strong>哪个</strong>受信 Controller（<c>null</c> = 本 Tick 不代表任何 Controller）。
        ///
        /// 判定只读自己的未来状态：
        /// <list type="bullet">
        /// <item>尚未注册任何控制者 ⇒ <c>null</c>（本场没有 AI，收到的就是 Canonical 视野）；</item>
        /// <item>本 Tick <strong>已经决策过</strong> ⇒ <c>null</c>（本轮不需要过滤视图）；</item>
        /// <item>本 Tick <strong>还没到</strong>下一次思考 Tick ⇒ <c>null</c>（观察者整个 Tick 沉默）；</item>
        /// <item>否则取 <c>ControllerId</c>（Ordinal）最小的那个注册控制者——
        /// 一次投递只服务一个控制者，多个 AI 的投递在数组顺序下逐个完成，
        /// 因此"某个 AI 看到另一个 AI 的私有计划"在投递路径上不可能发生。</item>
        /// </list>
        /// </summary>
        public ControllerId ObserverControllerId(DecisionSnapshot snapshot)
        {
            if (snapshot == null) return default;
            string key = FirstThinkingController(snapshot.Tick);
            return key == null ? default : new ControllerId(key);
        }

        private string FirstThinkingController(long tick)
        {
            string best = null;
            for (int i = 0; i < _registrationOrder.Count; i++)
            {
                string key = _registrationOrder[i];
                AiRuntimeState state = _states[key];
                if (state.LastDecisionTick == tick) continue;
                if (state.NextThinkTick > tick) continue;
                if (best == null || string.CompareOrdinal(key, best) < 0) best = key;
            }
            return best;
        }

        // ================= IDecisionObserver =================

        /// <summary>
        /// 阶段 18 的唯一回调：读取与玩家同时公开的只读快照，按<strong>自己注册的入口</strong>
        /// 把请求投到 <paramref name="nextTick"/>。它是<strong>纯提交</strong>：不写任何逻辑状态。
        /// </summary>
        public void ObserveOrdered(DecisionSnapshot snapshot, CommandIngressRegistry ingress, long nextTick)
        {
            if (snapshot == null || ingress == null) return;

            for (int i = 0; i < _registrationOrder.Count; i++)
            {
                string key = _registrationOrder[i];
                AiRuntimeState state = _states[key];

                // 思考节奏只由自己的未来状态驱动；未到点就整个 Tick 沉默（不消耗、不抢跑）。
                if (state.LastDecisionTick == snapshot.Tick) continue;
                if (nextTick != snapshot.Tick + 1L) continue;
                if (state.NextThinkTick > snapshot.Tick) continue;

                CommandIngressEntry entry = ingress.FindEntry(new ControllerId(key));
                if (entry == null) continue;

                AiDecisionContext context = BuildContext(snapshot, new ControllerId(key));
                AiDecision decision = AiDecisionFunction.Decide(context, _battleSeed);

                if (decision.HasSelection)
                {
                    CommandIngressRejection rejection = entry.SubmitAtDefaultTick(
                        decision.Selected.Scope, decision.Selected.Payload);
                    if (rejection != null) _rejections.Add(rejection);
                }

                state.DecisionCount++;
                state.LastDecisionTick = snapshot.Tick;
                // "下一次允许决策的 Tick" = 本条命令的目标 Tick（= nextTick）：
                // 因此在**每一个** Tick 的投递上都恰好决策一次，而同一 Tick 内重复投递
                // （或快照 Tick 回退）都会因 NextThinkTick > tick 而沉默——不会抢跑，也不会漏拍。
                state.NextThinkTick = nextTick;
                state.LastDecision = decision;
                _decisions.Add(decision);
                if (_decisions.Count > MaximumDiagnosticDecisions) _decisions.RemoveAt(0);
            }
        }

        // ================= 只读观察面 =================

        /// <summary>按 <c>ControllerId</c>（Ordinal）升序的全部 AI 运行态。</summary>
        public IReadOnlyList<AiControllerRuntimeState> CaptureRuntimeStatesOrdered()
        {
            var ordered = new List<AiControllerRuntimeState>(_registrationOrder.Count);
            for (int i = 0; i < _registrationOrder.Count; i++)
            {
                ordered.Add(_states[_registrationOrder[i]].ToSnapshot());
            }
            ordered.Sort((a, b) => string.CompareOrdinal(a.ControllerId, b.ControllerId));
            return ordered;
        }

        /// <summary>全部已作出的决策（决策顺序；只读诊断面）。</summary>
        /// <summary>Recent diagnostic decisions, capped at 512; authoritative DecisionCount remains complete.</summary>
        public IReadOnlyList<AiDecision> Decisions => _decisions;

        /// <summary>入口级拒绝（只读诊断面；入口拒绝<strong>不</strong>消耗 <c>ProducerOrdinal</c>）。</summary>
        public IReadOnlyList<CommandIngressRejection> IngressRejections => _rejections;

        /// <summary>该控制者最后一次决策（未决策过返回 <c>null</c>）。</summary>
        public AiDecision LastDecisionOf(string controllerId)
            => controllerId != null && _states.TryGetValue(controllerId, out AiRuntimeState state)
                ? state.LastDecision
                : null;

        /// <summary>
        /// 在快照 Tick = <paramref name="tick"/> 上作出的那次决策（未决策过返回 <c>null</c>）。
        /// 它让"某一次决策到底看到了什么"可被逐 Tick 断言（决策顺序与投递顺序一一对应）。
        /// </summary>
        public AiDecision DecisionAt(long tick)
        {
            for (int i = _decisions.Count - 1; i >= 0; i--)
            {
                AiDecision decision = _decisions[i];
                if (decision == null) continue;
                if (decision.DecisionTick == tick) return decision;
            }
            return null;
        }

        /// <summary>该控制者当前的规范化运行态（未注册返回 <c>null</c>）。</summary>
        public AiControllerRuntimeState RuntimeStateOf(ControllerId controllerId)
            => controllerId.Value != null && _states.TryGetValue(controllerId.Value, out AiRuntimeState state)
                ? state.ToSnapshot()
                : null;

        /// <summary>
        /// 构建一次决策的只读上下文：单位、可编辑计划、自己的窗口与已公开机会全部来自
        /// <paramref name="snapshot"/>（已按 Controller 过滤）与只读端口，<strong>不</strong>触碰权威写入面。
        /// </summary>
        public AiDecisionContext BuildContext(DecisionSnapshot snapshot, ControllerId controllerId)
            => AiDecisionContextFactory.Build(
                snapshot, controllerId, _opportunities, _planLookup, _pathCalculator);

        /// <summary>诊断文本（不参与逻辑与哈希）。</summary>
        public string Describe()
            => "ai-controllers=" + _registrationOrder.Count.ToString(CultureInfo.InvariantCulture) +
               " decisions=" + _decisions.Count.ToString(CultureInfo.InvariantCulture) +
               " ingressRejections=" + _rejections.Count.ToString(CultureInfo.InvariantCulture);
    }
}
