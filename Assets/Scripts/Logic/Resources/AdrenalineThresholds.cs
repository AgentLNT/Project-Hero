using System;
using System.Collections.Generic;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.Resources
{
    public static class AdrenalineThresholds
    {
        public static readonly IReadOnlyList<int> ReceivedPercentages = Array.AsReadOnly(new[] { 75, 50, 25 });
        public static readonly IReadOnlyList<int> DealtPercentages = Array.AsReadOnly(new[] { 50, 0 });

        internal static int Crossed(int before, int after, int maximum, IReadOnlyList<int> percentages)
        {
            if (maximum <= 0 || before <= after) return 0;
            int crossed = 0;
            for (int i = 0; i < percentages.Count; i++)
            {
                long threshold = (long)maximum * percentages[i];
                if ((long)before * 100 > threshold && (long)after * 100 <= threshold)
                    crossed |= 1 << i;
            }
            return crossed;
        }
    }

    public sealed partial class AdrenalineLedger
    {
        private readonly SortedDictionary<long, int> _dealtThresholdClaims = new SortedDictionary<long, int>();
        internal int ReceivedThresholdClaims { get; private set; }

        internal int ClaimDamageReceived(int before, int after, int maximum)
        {
            int crossed = AdrenalineThresholds.Crossed(before, after, maximum, AdrenalineThresholds.ReceivedPercentages);
            int fresh = crossed & ~ReceivedThresholdClaims;
            ReceivedThresholdClaims |= fresh;
            return CreditThresholds(fresh);
        }

        internal int ClaimDamageDealt(UnitId target, int before, int after, int maximum)
        {
            int crossed = AdrenalineThresholds.Crossed(before, after, maximum, AdrenalineThresholds.DealtPercentages);
            _dealtThresholdClaims.TryGetValue(target.Value, out int previous);
            int fresh = crossed & ~previous;
            if (fresh == 0) return 0;
            _dealtThresholdClaims[target.Value] = previous | fresh;
            return CreditThresholds(fresh);
        }

        internal IReadOnlyList<AdrenalineTargetThresholdSnapshot> BuildDealtThresholdClaims()
        {
            if (_dealtThresholdClaims.Count == 0) return null;
            var claims = new List<AdrenalineTargetThresholdSnapshot>(_dealtThresholdClaims.Count);
            foreach (var entry in _dealtThresholdClaims)
                claims.Add(new AdrenalineTargetThresholdSnapshot(entry.Key, entry.Value));
            return claims.AsReadOnly();
        }

        private int CreditThresholds(int crossed)
        {
            if (crossed == 0) return 0;
            int points = 0;
            for (int remaining = crossed; remaining != 0; remaining >>= 1)
                points += remaining & 1;

            int before = AvailableAdrenaline;
            int gain = checked(points * _rules.UnitsPerPoint);
            AvailableAdrenaline = checked(before + gain);
            Changed?.Invoke(new AdrenalineLedgerChange(UnitId, CycleId, AdrenalineChangeKind.Accrued,
                before, AvailableAdrenaline, null, gain));
            return points;
        }
    }
}
