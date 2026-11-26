using System;
using System.IO;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Experiments;

namespace ProjectHero.Editor
{
    public static class Task22NativeValidation
    {
        public static void Run()
        {
            const string root = "优化任务/执行记录/温度采样与磨血防御-2026-11-13/native-mechanisms";
            Directory.CreateDirectory(root);
            foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
                foreach (var setup in new[] { new AiBloodlineInput(28), new AiBloodlineInput(18), new AiBloodlineInput(6), new AiBloodlineInput(18,16),
                    new AiBloodlineInput(18, ThreatStart:521), new AiBloodlineInput(6, ThreatDamage:8, ThreatStart:521) })
                {
                    var input = setup with { Policy = policy }; var result = AiBloodlineExperiment.Run(input);
                    File.WriteAllLines(root + "/blood-H" + input.Health + "-F" + input.FundingDamage + "-D" + input.ThreatDamage + "-S" + input.ThreatStart + "-" + input.Policy + ".canonical.txt",
                        AiPressureEvidence.Canonical(result));
                }
            foreach (AiPolicySituation situation in Enum.GetValues(typeof(AiPolicySituation)))
                foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
                    foreach (int temperature in new[] { 0, 250, 1000, 2000 })
                    {
                        var input = new AiPolicyExperimentInput(policy, situation,
                            Sampling: new AiSamplingSettings(temperature, SignedDamage: true, HealthDefense: true));
                        var result = AiPolicyExperiment.Run(input); AiPolicyExperiment.VerifyReplay(result);
                        File.WriteAllLines(root + "/" + situation + "-" + policy + "-T" + temperature + ".canonical.txt", AiPressureEvidence.Canonical(result));
                    }
            UnityEngine.Debug.Log("TASK22_NATIVE_MECHANISMS_PASS cases=114");
        }
    }
}
