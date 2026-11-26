using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Replay;

namespace ProjectHero.Editor
{
    static class Task22Mechanisms
    {
        public static void Run()
        {
            string root = "优化任务/执行记录/温度采样与磨血防御-2026-11-13/mechanisms";
            Directory.CreateDirectory(root);
            foreach (var input in Bloodlines())
            {
                var result = AiBloodlineExperiment.Run(input);
                string path = root + "/blood-H" + input.Health + "-F" + input.FundingDamage + "-D" + input.ThreatDamage + "-S" + input.ThreatStart + "-" + input.Policy;
                File.WriteAllLines(path + ".canonical.txt", AiPressureEvidence.Canonical(result));
                using (var file = File.Create(path + ".heroReplay")) ReplayFile.Save(file, result.Replay);
                File.WriteAllText(path + ".json", JsonSerializer.Serialize(new { input, hash = result.Definition.BattleDefinitionHashValue,
                    decisions = result.Decisions, events = result.Events.Select(e => e.ToString()).ToArray(), finalUnits = result.FinalUnits }));
            }
            foreach (var situation in Enum.GetValues<AiPolicySituation>())
                foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
                    foreach (int temperature in new[] { 0, 250, 1000, 2000 })
                    {
                        var input = new AiPolicyExperimentInput(policy, situation,
                            Sampling: new AiSamplingSettings(temperature, SignedDamage: true, HealthDefense: true));
                        var result = AiPolicyExperiment.Run(input); AiPolicyExperiment.VerifyReplay(result);
                        string path = root + "/" + situation + "-" + policy + "-T" + temperature;
                        File.WriteAllLines(path + ".canonical.txt", AiPressureEvidence.Canonical(result));
                        using (var file = File.Create(path + ".heroReplay")) ReplayFile.Save(file, result.Replay);
                        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new { input, hash = result.Definition.BattleDefinitionHashValue,
                            decisions = result.Decisions, events = result.Events.Select(e => e.ToString()).ToArray(), finalUnits = result.FinalUnits }));
                        Console.WriteLine("MECHANISM_PASS " + path);
                    }
        }
        static System.Collections.Generic.IEnumerable<AiBloodlineInput> Bloodlines()
        {
            foreach (var policy in new[] { AiPolicyKind.SustainedOffense, AiPolicyKind.SeekInterrupt, AiPolicyKind.CautiousDefense })
                foreach (var input in new[] { new AiBloodlineInput(28), new AiBloodlineInput(18), new AiBloodlineInput(6),
                    new AiBloodlineInput(18,16), new AiBloodlineInput(18, ThreatStart:521), new AiBloodlineInput(6, ThreatDamage:8, ThreatStart:521) })
                    yield return input with { Policy = policy };
        }
    }
}
