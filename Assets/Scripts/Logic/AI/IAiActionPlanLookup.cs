using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>只读计划检索端口</strong>（Logic → AI）。
    ///
    /// 它只暴露<strong>权威计划对象本身的只读面</strong>（按 ID 查找 + 按所有者列出），
    /// <strong>不</strong>暴露注册表的写入/生命周期方法（提交、终态、删除、修订号推进），
    /// 因此 AI 无法经它删除计划、改终态或绕过 <c>ScheduleRevision</c>。
    /// <c>Move</c> 候选靠它取得 <c>IMovementPathCalculator.Recompute</c> 的输入。
    /// </summary>
    public interface IAiActionPlanLookup
    {
        /// <summary>按 <c>ActionPlanId</c> 查找计划；不存在返回 <c>null</c>（零写入）。</summary>
        ActionPlan FindPlan(ActionPlanId actionPlanId);

        /// <summary>该单位当前全部非终态活动计划（稳定顺序；无则空集合）。</summary>
        IReadOnlyList<ActionPlan> ActivePlansOf(UnitId ownerUnitId);
    }
}
