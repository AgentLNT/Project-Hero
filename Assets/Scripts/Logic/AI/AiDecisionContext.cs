using System;
using System.Collections.Generic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;

namespace ProjectHero.Logic.AI
{
    /// <summary>
    /// <strong>一次 AI 决策的只读输入上下文</strong>（任务 09 产出 9）。
    ///
    /// 每个字段都来自<strong>与玩家同时公开</strong>的只读面：
    /// <list type="bullet">
    /// <item><see cref="Snapshot"/>：本 Tick 的 <see cref="DecisionSnapshot"/>（已按 Controller 过滤）；</item>
    /// <item><see cref="ControlledUnits"/>：该控制者现在可控制的单位（权威控制权投影）；</item>
    /// <item><see cref="EditablePlans"/>：该控制者自己仍为 <c>Editable</c> 的普通计划快照；</item>
    /// <item><see cref="OwnWindow"/>：该控制者当前窗口的只读快照（<c>null</c> = 没有窗口）；</item>
    /// <item><see cref="Opportunities"/>：其受控单位当前<strong>已公开</strong>的反应机会；</item>
    /// <item><see cref="PathCalculator"/>：任务 06 的<strong>同一个</strong>路径重算端口
    /// （与玩家预览、<c>ScheduleEditor</c> 共用；<c>null</c> 时 <c>Move</c> 候选 fail-closed）；</item>
    /// <item><see cref="PlanLookup"/>：权威计划的只读检索面（<c>Move</c> 候选用它取
    /// <c>Recompute</c> 的输入；<c>null</c> 时 <c>Move</c> 候选同样 fail-closed）。</item>
    /// </list>
    /// 它<strong>不</strong>提供任何写入入口，也不携带 Envelope、机会或任何 ID/序号分配器。
    /// </summary>
    public sealed record AiDecisionContext(
        DecisionSnapshot Snapshot,
        ControllerId ControllerId,
        IReadOnlyList<UnitId> ControlledUnits,
        IReadOnlyList<ActionPlanSnapshot> EditablePlans,
        TurnWindowSnapshot OwnWindow,
        IReadOnlyList<AiOpportunityView> Opportunities,
        IMovementPathCalculator PathCalculator,
        IAiActionPlanLookup PlanLookup,
        Func<ControllerId, CommandRequest, string> NormalPreview = null)
    {
        /// <summary>本决策允许使用的目标 Tick（= 快照 Tick 的下一 Tick）。</summary>
        public long NextTick => Snapshot == null ? 0L : Snapshot.Tick + 1L;

        /// <summary>与处理器<strong>同一个</strong>关系解析器的候选筛面（唯一构造入口）。</summary>
        public TargetCandidateQuery Candidates => TargetCandidateQuery.From(Snapshot);

        /// <summary>本控制者现在是否有仍接收普通提交的窗口（产出 10 的第一条）。</summary>
        public bool HasSubmissionWindow
            => OwnWindow != null && OwnWindow.IsOpen && OwnWindow.IsAcceptingSubmissions;

        /// <summary>本次事务的窗口前提（Add/Move/Remove 都要把它带进 scope）。</summary>
        public WindowId? ExpectedWindowId
            => OwnWindow == null ? (WindowId?)null : new WindowId(OwnWindow.WindowId);
    }
}
