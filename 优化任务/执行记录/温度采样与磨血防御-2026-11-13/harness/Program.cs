using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectHero.Editor;
using ProjectHero.Logic.Combat;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var d = FormalAiSourceExport.Definition();
            string hash = BattleDefinitionHash.Compute(d.RulesVersion, d.TicksPerSecond, FormalAiSourceExport.LibraryVolumeBindingCount,
                d.Rules, d.ConcurrentAction, d.ReactionRules, d.AdrenalineRules, d.FactionModel, d.DamageChannels, d.ImpactProfiles,
                d.Units, d.Actions, d.AttackPatterns, d.Volumes, d.MovementPatterns, d.ActionSets, d.StatusEffects, d.Encounters, d.DefaultDynamicSpawnPolicy);
            if (hash != d.BattleDefinitionHashValue) throw new InvalidOperationException("EXPORTED_FORMAL_DEFINITION_HASH_MISMATCH");
            string mode = args.Length == 0 ? "info" : args[0];
            Console.WriteLine("MATCHUP_STANDALONE runtime=" + RuntimeInformation.FrameworkDescription + " source=" + hash + " mode=" + mode);
            if (mode == "reference") Task22MatchupTools.AuditUnityReference();
            else if (mode == "pilot") Task22MatchupTools.Pilot();
            else if (mode == "scan") Task22MatchupTools.Scan();
            else if (mode == "mechanisms") Task22Mechanisms.Run();
            else if (mode == "legacy-audit") FormalAiMatchupTools.AuditUnityReference();
            else if (mode == "run") Task22MatchupTools.Run();
            else if (mode == "audit") Task22MatchupTools.Audit();
            else if (mode == "info")
            {
                var info = new { runtime = RuntimeInformation.FrameworkDescription, sourceHash = hash, ticksPerSecond = d.TicksPerSecond,
                    seed = FormalAiSourceExport.Seed().RuntimeInputs, workers = Environment.GetEnvironmentVariable("HERO_MATCHUP_WORKERS") ?? "8",
                    units = d.Units.Select(u => u.ToString()).ToArray(), actions = d.Actions.Select(a => a.ToString()).ToArray(),
                    contentFingerprint = FormalAiBaselineTools.ContentFingerprint(),
                    factorySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes("优化任务/执行记录/温度采样与磨血防御-2026-11-13/harness/FormalAiSourceExport.cs"))).ToLowerInvariant() };
                File.WriteAllText(Task22MatchupTools.Root + "/runner.json", JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            }
            else throw new ArgumentException("Unknown mode: " + mode);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
