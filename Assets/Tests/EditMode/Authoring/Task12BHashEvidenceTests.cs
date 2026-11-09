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
            File.WriteAllText("优化任务/执行记录/防御应对核对-2026-11-09/hash-anchors.json",
                "{\n  \"oldRulesVersion\": \"battle-def-v5-clash-recovery\",\n  \"oldDefinitionHash\": \"de4be447760a2266\",\n"
                + "  \"oldInitialStateHash\": \"5f18e7c81833be75\",\n  \"oldProductionInitialStateHash\": \"f9ae2c5d11ebf2d0\",\n"
                + "  \"oldEvidence\": \"../拼刀后摇纠偏-2026-10-29/hash-anchors.json\",\n"
                + "  \"rulesVersion\": \"" + first.RulesVersion + "\",\n  \"definitionHash\": \"" + first.Definition.BattleDefinitionHashValue
                + "\",\n  \"initialStateHash\": \"" + first.InitialStateHashHex
                + "\",\n  \"productionInitialStateHash\": \"" + production.InitialStateHashHex
                + "\",\n  \"ticksPerSecond\": 60,\n  \"initialRngSeed\": 20260922,\n  \"initialMetaResource\": 10,\n  \"replayFormatVersion\": " + ReplayFormat.Version + "\n}\n");
            Assert.That(first.Definition.BattleDefinitionHashValue, Is.EqualTo("84116d595d222a72"));
            Assert.That(first.InitialStateHash, Is.EqualTo(second.InitialStateHash));
            Assert.That(first.InitialStateHashHex, Is.EqualTo("670e239ef2185451"));
            var bytes = first.CurrentSnapshot.CanonicalBytesForDiagnostics(ReplayFormat.Version);
            Assert.That(CanonicalHash.OfBytes(bytes), Is.EqualTo(first.InitialStateHash));
            bytes[0] ^= 1;
            Assert.That(first.CurrentSnapshot.ComputeHash(), Is.EqualTo(first.InitialStateHash), "Diagnostic bytes cannot alias authoritative state.");
            File.WriteAllBytes("优化任务/执行记录/防御应对核对-2026-11-09/initial-encoding-v7.bin",
                first.CurrentSnapshot.CanonicalBytesForDiagnostics(ReplayFormat.Version));
            Assert.That(production.InitialStateHashHex, Is.EqualTo("d049880d1f59f08c"));
            File.WriteAllBytes("优化任务/执行记录/防御应对核对-2026-11-09/production-initial-encoding-v7.bin",
                production.CurrentSnapshot.CanonicalBytesForDiagnostics(ReplayFormat.Version));
        }
    }
}
