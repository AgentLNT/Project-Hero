using System.IO;
using NUnit.Framework;
using ProjectHero.Logic.Determinism;
using ProjectHero.Logic.Simulation;

namespace ProjectHero.Authoring.Tests
{
    public sealed class Task12BHashEvidenceTests
    {
        [Test] public void FrozenR2DefinitionAndInitialStateHashesAreReproducible()
        {
            using var first = Task03.Task03.NewSim(); using var second = Task03.Task03.NewSim();
            using var production = ProductionBattleComposition.Create(first.Definition, Task03.Task03.EncounterId, Task03.Task03.Inputs);
            File.WriteAllText("优化任务/执行记录/12B-hash-anchors.json",
                "{\n  \"oldRulesVersion\": \"battle-def-v3-window180\",\n  \"oldDefinitionHash\": \"c5d1058b7b19720a\",\n"
                + "  \"oldInitialStateHash\": \"e6f9af498bff9600\",\n  \"oldProductionInitialStateHash\": \"29c03922dab5c689\",\n"
                + "  \"oldEvidence\": \"12A-hash-anchors.json (archived native R1 execution)\",\n"
                + "  \"rulesVersion\": \"" + first.RulesVersion + "\",\n  \"definitionHash\": \"" + first.Definition.BattleDefinitionHashValue
                + "\",\n  \"initialStateHash\": \"" + first.InitialStateHashHex
                + "\",\n  \"productionInitialStateHash\": \"" + production.InitialStateHashHex
                + "\",\n  \"ticksPerSecond\": 60,\n  \"initialRngSeed\": 20260922,\n  \"initialMetaResource\": 10,\n  \"replayFormatVersion\": " + ReplayFormat.Version + "\n}\n");
            Assert.That(first.Definition.BattleDefinitionHashValue, Is.EqualTo("e04b3c0946ad3035"));
            Assert.That(first.InitialStateHash, Is.EqualTo(second.InitialStateHash));
            Assert.That(first.InitialStateHashHex, Is.EqualTo("2046e404a6fb708f"));
            var bytes = first.CurrentSnapshot.CanonicalBytesForDiagnostics(ReplayFormat.Version);
            Assert.That(CanonicalHash.OfBytes(bytes), Is.EqualTo(first.InitialStateHash));
            bytes[0] ^= 1;
            Assert.That(first.CurrentSnapshot.ComputeHash(), Is.EqualTo(first.InitialStateHash), "Diagnostic bytes cannot alias authoritative state.");
            File.WriteAllBytes("优化任务/执行记录/12B-initial-encoding-v6.bin",
                first.CurrentSnapshot.CanonicalBytesForDiagnostics(ReplayFormat.Version));
            Assert.That(production.InitialStateHashHex, Is.EqualTo("63e751856fe458e8"));
            File.WriteAllBytes("优化任务/执行记录/12B-production-initial-encoding-v6.bin",
                production.CurrentSnapshot.CanonicalBytesForDiagnostics(ReplayFormat.Version));
        }
    }
}
