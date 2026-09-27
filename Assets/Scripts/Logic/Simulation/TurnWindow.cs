using System;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Turns;

namespace ProjectHero.Logic.Simulation
{
    /// <summary>
    /// 窗口管理器所需的<strong>只读世界事实</strong>（任务 07「必须产出」2）。
    ///
    /// 管理器<strong>不</strong>持有单位状态、也不查询网格或计划：它只问两件事——
    /// "这个单位的拥有者还活着吗""战斗是否已经结束"。两个答案都由本场模拟提供，
    /// 因此窗口系统与单位/胜负模型保持单向依赖（Logic 内部无环）。
    /// </summary>
    internal sealed class SimulationTurnWindowWorld : ITurnWindowWorldView
    {
        private readonly Func<UnitId, bool> _isUnitAlive;
        private readonly Func<bool> _isBattleEnded;

        public SimulationTurnWindowWorld(Func<UnitId, bool> isUnitAlive, Func<bool> isBattleEnded)
        {
            _isUnitAlive = isUnitAlive;
            _isBattleEnded = isBattleEnded;
        }

        /// <summary>
        /// 窗口拥有者是否仍存活（待打开窗口在打开阶段前死亡 ⇒ 按稳定顺序跳过，
        /// 不产生该单位的窗口打开事件，也不产生预算）。
        /// </summary>
        public bool IsUnitAliveForWindow(UnitId unitId)
            => _isUnitAlive != null && _isUnitAlive(unitId);

        /// <summary>战斗是否已经结束（结束后不再打开、也不排定任何窗口）。</summary>
        public bool IsBattleEnded => _isBattleEnded != null && _isBattleEnded();
    }
}
