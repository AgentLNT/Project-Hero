using System.Collections.Generic;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Timeline
{
    /// <summary>Immutable, Logic-evaluated timing, budget and path; no mutable ActionPlan reaches a View.</summary>
    public sealed record SchedulePlanPreview(ActionPlanSnapshot Plan, IReadOnlyList<GridPoint> Path);
}
