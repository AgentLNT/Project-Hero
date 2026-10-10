using System;
using System.Collections.Generic;
using ProjectHero.Logic.Movement;

namespace ProjectHero.Logic.Snapshots
{
    /// <summary>
    /// <strong>移动段 / 空间 Reservation 的规范化快照投影</strong>（任务 06「必须产出」7）。
    ///
    /// 它是"权威空间表 → <see cref="LogicSnapshot"/> 字段"的<strong>唯一</strong>映射：
    /// <c>BattleSimulation</c> 与测试都调用同一份实现，因此"快照里看到的段/预留"
    /// 不可能与"权威表里的段/预留"分叉。
    ///
    /// 边界：
    /// <list type="bullet">
    /// <item>输入必须是各自的<strong>规范顺序</strong>来源
    /// （<c>LogicGridMovementAuthority.AllSegmentsOrdered()</c>、
    /// <c>LogicGrid.AllReservationsOrdered()</c>）；本类不再排序，
    /// 因为 <see cref="LogicSnapshot"/> 在写入哈希前会按各自的冻结键重新规范排序，
    /// 构造顺序本来就无关。</item>
    /// <item>已提交段的**只读审计**不投影（审计只描述过去，不影响未来结果，不进哈希）。</item>
    /// <item>Dodge 目的格预留不在此列（它属于 <c>DodgeRelocationAuthority</c> 的独立表）。</item>
    /// </list>
    /// </summary>
    public static class MovementSnapshotProjection
    {
        /// <summary>活动/未来移动段 → 快照段（以 <c>(ActionPlanId, StepIndex)</c> 定位）。</summary>
        public static IReadOnlyList<MovementSegmentSnapshot> Segments(IReadOnlyList<MovementSegment> segments)
        {
            if (segments == null || segments.Count == 0) return Array.Empty<MovementSegmentSnapshot>();
            var result = new MovementSegmentSnapshot[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                MovementSegment segment = segments[i];
                result[i] = new MovementSegmentSnapshot(
                    segment.ActionPlanId.Value, segment.StepIndex,
                    segment.From.X, segment.From.Y, segment.To.X, segment.To.Y, segment.EndTick, segment.StartTick);
            }
            return result;
        }

        /// <summary>空间 Reservation → 快照预留（归属计划 + 目标格）。</summary>
        public static IReadOnlyList<ReservationSnapshot> Reservations(IReadOnlyList<Reservation> reservations)
        {
            if (reservations == null || reservations.Count == 0) return Array.Empty<ReservationSnapshot>();
            var result = new ReservationSnapshot[reservations.Count];
            for (int i = 0; i < reservations.Count; i++)
            {
                Reservation reservation = reservations[i];
                result[i] = new ReservationSnapshot(
                    reservation.ActionPlanId.Value, reservation.Cell.X, reservation.Cell.Y);
            }
            return result;
        }
    }
}
