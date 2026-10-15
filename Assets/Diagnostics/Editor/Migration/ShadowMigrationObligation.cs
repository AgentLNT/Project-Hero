using System.Collections.Generic;

namespace ProjectHero.Core.Compatibility.Runtime
{
    public sealed class ShadowMigrationObservation
    {
        public ShadowMigrationObservation(long tick, string field, string legacyValue, string shadowValue, string obligationId)
        { Tick = tick; Field = field; LegacyValue = legacyValue; ShadowValue = shadowValue; ObligationId = obligationId; }
        public long Tick { get; }
        public string Field { get; }
        public string LegacyValue { get; }
        public string ShadowValue { get; }
        public string ObligationId { get; }
    }
    /// <summary>Accepted migration contract: executable rule evidence is separate from real Legacy observations.</summary>
    public sealed class ShadowMigrationObligation
    {
        public ShadowMigrationObligation(string id, string category, string caseId, string rulesVersion,
            string definitionHash, string reason, IReadOnlyList<string> executedCases)
        {
            Id = id; Category = category; CaseId = caseId; RulesVersion = rulesVersion;
            DefinitionHash = definitionHash; Reason = reason;
            ExecutedCases = executedCases == null ? null : new List<string>(executedCases).AsReadOnly();
        }
        public string Id { get; }
        public string Category { get; }
        public string CaseId { get; }
        public string RulesVersion { get; }
        public string DefinitionHash { get; }
        public string Reason { get; }
        public IReadOnlyList<string> ExecutedCases { get; }
    }
}
