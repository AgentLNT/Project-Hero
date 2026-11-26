using ProjectHero.Logic.Determinism;

namespace ProjectHero.Logic.AI
{
    public sealed record AiSamplingMemory(string OpportunityKey, string AdmissionKey, int DrawCount)
    {
        public void Encode(CanonicalEncoder e)
        {
            e.WriteString(AiSamplingSettings.Version); e.WriteString(OpportunityKey);
            e.WriteString(AdmissionKey); e.WriteInt32(DrawCount);
        }
    }
}
