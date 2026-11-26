using System;
using System.Collections.Generic;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    public interface IAiForecast : IDisposable
    {
        AiActionForecast Evaluate(CommandRequest request, AiForecastSettings settings = null);
    }

    public interface IAiDefenseForecast : IAiForecast
    {
        IReadOnlyList<AiDefenseForecast> EvaluateDefenses(CommandRequest attack, AiForecastSettings settings);
    }

    /// <summary>One conditional reply, submitted after a committed public boundary in the isolated world.</summary>
    public sealed record AiDefenseForecast(string Key, Ids.ControllerId Defender, long SubmittedAfterTick,
        CommandRequest Request, AiActionForecast Outcome)
    {
        public long DefenderUnitId { get; init; }
        public bool IsOrdinaryEvasion { get; init; }
    }

    /// <summary>Conditional public-world outcomes from the production kernel, never a second combat model.</summary>
    public sealed record AiActionForecast(string RejectionCode, long HorizonTick,
        IReadOnlyList<UnitSnapshot> Units, long FirstHostileDamageTick, int InterruptedThreats, int Clashes,
        long PlanStartTick = 0, long PlanImpactTick = 0, long PlanEndTick = 0,
        AiForecastCoverage Coverage = AiForecastCoverage.Unspecified, long ObservedThroughTick = -1,
        long RequiredThroughTick = 0, int CandidateTerminalReason = 0)
    {
        internal bool HasAdmittedPlan { get; init; }
        public IReadOnlyList<AiResourceExpiry> ResourceExpiries { get; init; } = Array.Empty<AiResourceExpiry>();
        public long FirstStepEndTick { get; init; }
        public long FollowUpRequestTick { get; init; } = -1;
        public long FollowUpStartTick { get; init; } = -1;
        public bool SequenceCompleted { get; init; }
        public long FirstPlanId { get; init; }
        public long FollowUpPlanId { get; init; }
        public long BridgePlanId { get; init; }
        public IReadOnlyList<AiResourceExchange> ResourceExchanges { get; init; } = Array.Empty<AiResourceExchange>();
        public int ScalarInterruptions { get; init; }
        public int KnockdownWindowTicks { get; init; }
    }
    public sealed record AiResourceExpiry(long UnitId, long ResetTick);
    public sealed record AiResourceExchange(long Tick,long UnitId,int ChangeKind,int AvailableBefore,int AvailableAfter,int Amount);
}
