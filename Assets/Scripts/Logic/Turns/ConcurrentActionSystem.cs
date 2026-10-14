using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Resources;

namespace ProjectHero.Logic.Turns
{
    public interface IActionAuthority
    {
        /// <summary>
        /// 发行者（由命令网关绑定，<strong>不</strong>来自命令载荷）能否在本窗口内为
        /// <paramref name="unitId"/> 提交一个普通动作。
        /// </summary>
        bool CanSubmitOrdinaryAction(ControllerId issuer, UnitId unitId, WindowId windowId, ActionType actionType);
    }

    public sealed partial class ConcurrentActionSystem : IActionAuthority
    {
        private readonly ConcurrentActionDefinition _definition;
        private readonly TurnWindowManager _windows;
        private Func<int> _metaResourceReader;
        private Action<int> _metaResourceWriter;

        public ConcurrentActionSystem(ConcurrentActionDefinition definition, TurnWindowManager windows)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        }

        /// <summary>本场可激活并发行动的主角单位（未配置时为 null ⇒ 激活以稳定码拒绝）。</summary>
        public UnitId? HeroUnitId { get; set; }

        /// <summary>激活事件端口（由模拟方接到 Outbox）。</summary>
        public Action<WindowId, UnitId, UnitId> ActivatedSink { get; set; }

        /// <summary>撤销事件端口（由模拟方接到 Outbox）。</summary>
        public Action<WindowId, UnitId> DeactivatedSink { get; set; }

        /// <summary>局外资源的读写端口（由模拟方绑定到本场唯一的计数；费用不得由命令提供）。</summary>
        public void BindMetaResource(Func<int> reader, Action<int> writer)
        {
            _metaResourceReader = reader ?? throw new ArgumentNullException(nameof(reader));
            _metaResourceWriter = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public bool IsActive { get; private set; }

        public WindowId ActiveWindowId { get; private set; }

        /// <summary>权威能力费用（命令载荷永远不能覆盖它）。</summary>
        public int AuthorityCost => _definition.MetaResourceCost;

        /// <summary>当前授权的受控单位（未激活时为 0）。</summary>
        public UnitId ActivePlayerUnitId { get; private set; }

        /// <summary>
        /// 激活并发行动。返回 null = 已激活（恰好消费一次权威费用）；
        /// 否则返回稳定拒绝码且<strong>零副作用</strong>。
        /// </summary>
        public string TryActivate(ControllerId issuer, UnitId heroUnitId, WindowId expectedWindowId)
        {
            // 0. 权威能力配置（fail-closed：没有定义就不收费、不授权）。
            if (_definition.MetaResourceCost < 0) return TurnWindowCodes.CONCURRENT_ABILITY_NOT_CONFIGURED;
            if (!HeroUnitId.HasValue || HeroUnitId.Value != heroUnitId)
                return TurnWindowCodes.INVALID_CONCURRENT_ACTOR;
            if (!_windows.CanControl(issuer, heroUnitId))
                return TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT;

            // 1. 当前窗口状态与期望窗口。
            TurnWindow window = _windows.OpenWindow;
            if (window == null) return TurnWindowCodes.NO_OPEN_WINDOW;
            if (window.WindowId != expectedWindowId) return TurnWindowCodes.STALE_OR_CLOSED_WINDOW;

            // 2. 自身窗口天然持有提交权，不需要（也不允许）购买并发授权。
            if (window.OwnerUnitId == heroUnitId) return TurnWindowCodes.PLAYER_OWNS_WINDOW;
            if (IsActive) return TurnWindowCodes.ALREADY_ACTIVE;

            // 3. 权威费用消费（唯一来源是 ConcurrentActionDefinition）。
            if (_metaResourceReader == null || _metaResourceWriter == null)
                return TurnWindowCodes.CONCURRENT_ABILITY_NOT_CONFIGURED;
            int available = _metaResourceReader();
            if (available < _definition.MetaResourceCost)
                return TurnWindowCodes.INSUFFICIENT_META_RESOURCE;
            _metaResourceWriter(available - _definition.MetaResourceCost);

            IsActive = true;
            ActiveWindowId = window.WindowId;
            ActivePlayerUnitId = heroUnitId;
            ActivatedSink?.Invoke(window.WindowId, heroUnitId, window.OwnerUnitId);
            return null;
        }

        /// <summary>
        /// 撤销当前授权（窗口关闭 / 战斗结束 / 拥有者死亡）。它<strong>不</strong>取消、移动或结算任何计划。
        /// 幂等：对非当前窗口的撤销请求是无操作。
        /// </summary>
        public void RevokeForWindow(WindowId windowId)
        {
            if (!IsActive || ActiveWindowId != windowId) return;
            UnitId playerUnitId = ActivePlayerUnitId;
            IsActive = false;
            ActivePlayerUnitId = default;
            ActiveWindowId = default;
            DeactivatedSink?.Invoke(windowId, playerUnitId);
        }

        /// <summary>撤销任何仍然有效的授权（战斗结束 Finalizer 的无条件入口）。</summary>
        public void RevokeAll()
        {
            if (!IsActive) return;
            RevokeForWindow(ActiveWindowId);
        }

        /// <inheritdoc />
        public bool CanSubmitOrdinaryAction(ControllerId issuer, UnitId unitId, WindowId windowId, ActionType actionType)
        {
            // 高阶反应不是窗口动作：本系统既不授权也不阻止它的唯一合法路径（ReactionOpportunity）。
            if (actionType == ActionType.Block || actionType == ActionType.Dodge) return false;
            if (actionType != ActionType.Attack && actionType != ActionType.Guard && actionType != ActionType.Move)
                return false;

            TurnWindow window = _windows.CurrentWindow;
            if (window == null || !window.IsOpen || !window.IsAcceptingSubmissions) return false;
            if (window.WindowId != windowId) return false;

            if (window.OwnerUnitId == unitId)
                return _windows.CanControl(issuer, unitId);
            if (!IsActive || ActiveWindowId != windowId || ActivePlayerUnitId != unitId) return false;
            return _windows.CanControl(issuer, unitId);
        }

        /// <summary>只读快照：授权是否存在、属于哪个窗口与哪个单位。</summary>
        public Snapshots.ConcurrentActionSnapshot BuildSnapshot()
            => IsActive
                ? new Snapshots.ConcurrentActionSnapshot(true, ActiveWindowId.Value, ActivePlayerUnitId.Value)
                : Snapshots.ConcurrentActionSnapshot.None();
    }
}
