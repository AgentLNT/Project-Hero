using System;
using System.Collections.Generic;
using System.Linq;
using ProjectHero.Logic.Determinism;

namespace ProjectHero.Logic.AI
{
    public sealed record AiSampleItem(string Key, string Group, long Score);
    public sealed record AiSampleProbability(string Key, long BeforeQ32, long AfterQ32);
    public sealed record AiSampleResult(string Key, IReadOnlyList<AiSampleProbability> Probabilities);

    /// <summary>Integer softmax: range reduction followed by a fixed decimal Taylor polynomial.
    /// Mean exp within an owner/action group removes destination-count bias. Nucleus operates on
    /// groups, followed by conditional parameter selection. This differs from flat softmax.</summary>
    public static class AiSoftmax
    {
        const long One = 1L << 32;
        public static AiSampleResult Sample(IReadOnlyList<AiSampleItem> items, AiSamplingSettings settings, DeterministicRng rng)
        {
            if (settings.Validate() != null || items.Count == 0) throw new ArgumentException("AI_SAMPLING_INPUT_INVALID");
            var sorted = items.OrderBy(i => i.Key, StringComparer.Ordinal).ToArray();
            long max = sorted.Max(i => i.Score);
            if (settings.TemperatureMilli == 0)
            {
                string best = sorted.First(i => i.Score == max).Key;
                return new AiSampleResult(best, sorted.Select(i => new AiSampleProbability(i.Key, i.Key == best ? One : 0, i.Key == best ? One : 0)).ToArray());
            }
            var weights = sorted.Select(i => ExpWeight(((decimal)i.Score - max) * 1000 / settings.ScoreScale / settings.TemperatureMilli)).ToArray();
            var groups = sorted.Select((v, i) => new { v, i }).GroupBy(x => x.v.Group, StringComparer.Ordinal)
                .Select(g => new { Key = g.Key, Indices = g.Select(x => x.i).ToArray(), Weight = g.Sum(x => (decimal)weights[x.i]) / g.Count() })
                .OrderByDescending(g => g.Weight).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
            decimal total = groups.Sum(g => g.Weight), cumulative = 0;
            var kept = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in groups) { kept.Add(g.Key); cumulative += g.Weight; if (cumulative * 1000 >= total * settings.TopPMilli) break; }
            decimal retained = groups.Where(g => kept.Contains(g.Key)).Sum(g => g.Weight);
            var probabilities = new List<AiSampleProbability>();
            foreach (var g in groups)
            {
                decimal parameterTotal = g.Indices.Sum(i => (decimal)weights[i]);
                foreach (int i in g.Indices)
                {
                    decimal joint = parameterTotal == 0 ? 0 : weights[i] / parameterTotal * g.Weight;
                    probabilities.Add(new AiSampleProbability(sorted[i].Key, (long)(joint / total * One),
                        kept.Contains(g.Key) ? (long)(joint / retained * One) : 0));
                }
            }
            var active = groups.Where(g => kept.Contains(g.Key)).ToArray();
            decimal draw = (decimal)rng.NextUInt32() / One * retained;
            var chosen = active.Last(); cumulative = 0;
            foreach (var g in active) { cumulative += g.Weight; if (draw < cumulative) { chosen = g; break; } }
            decimal mass = chosen.Indices.Sum(i => (decimal)weights[i]);
            draw = (decimal)rng.NextUInt32() / One * mass; cumulative = 0;
            int selected = chosen.Indices.Last();
            foreach (int i in chosen.Indices) { cumulative += weights[i]; if (draw < cumulative) { selected = i; break; } }
            return new AiSampleResult(sorted[selected].Key, probabilities.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
        }

        static long ExpWeight(decimal x)
        {
            // Tiny tails remain representable; no floating point/libm runtime dependency.
            if (x <= -40) return 1;
            decimal y = x / 256, term = 1, sum = 1;
            for (int k = 1; k <= 16; k++) { term = term * y / k; sum += term; }
            for (int k = 0; k < 8; k++) sum *= sum;
            return Math.Max(1, (long)(sum * (1L << 56)));
        }
    }
}
