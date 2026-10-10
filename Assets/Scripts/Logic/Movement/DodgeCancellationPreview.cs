using System.Collections.Generic;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Movement
{
    public sealed record ConditionalBudgetRelease(WindowId WindowId, long BudgetTicks, bool WindowIsOpen);
    public sealed record DodgeCancellationPreview(string RejectionCode, IReadOnlyList<ActionPlanId> AffectedPlanIds,
        IReadOnlyList<ConditionalBudgetRelease> ReleasesByWindow);
}
