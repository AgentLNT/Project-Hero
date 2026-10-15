using System;
using System.Collections.Generic;

namespace ProjectHero.Core.Compatibility.Runtime
{
    /// <summary>Strict cutover evidence check, distinct from migration-time Shadow diagnostic equivalence.</summary>
    public static class NewCutoverGate
    {
        public static IReadOnlyList<string> Rejections(ShadowComparisonReport report, string definitionHash,
            string rulesVersion, string encounter)
        {
            var errors = new List<string>();
            if (report == null) { errors.Add("CUTOVER_SHADOW_REPORT_MISSING"); return errors.AsReadOnly(); }
            if (report.Mode != BattleRuntimeMode.Shadow) errors.Add("CUTOVER_REPORT_MODE_INVALID");
            if (!string.Equals(report.BattleDefinitionHash, definitionHash, StringComparison.Ordinal)
                || !string.Equals(report.RulesVersion, rulesVersion, StringComparison.Ordinal)
                || !string.Equals(report.Encounter, encounter, StringComparison.Ordinal))
                errors.Add("CUTOVER_REPORT_INPUT_MISMATCH");
            if (!report.CanClaimEquivalence || report.ComparedFieldObservations == 0)
                errors.Add("CUTOVER_SHADOW_RUN_INVALID");
            if (report.DroppedCheckpoints != 0) errors.Add("CUTOVER_CHECKPOINTS_DROPPED");
            foreach (var temporary in report.TemporarilyUncomparable)
                errors.Add("CUTOVER_TEMPORARY_FIELD|" + temporary.Id);
            foreach (var difference in report.Differences)
                if (difference.Kind == ShadowDifferenceKind.TemporarilyUncomparable)
                    errors.Add("CUTOVER_TEMPORARY_DIFFERENCE|" + difference.FieldPath);
            return errors.AsReadOnly();
        }
    }
}
