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
            Assert.That(first.Definition.BattleDefinitionHashValue, Is.EqualTo("7a6d4a20ba6ae244"));
            Assert.That(first.InitialStateHash, Is.EqualTo(second.InitialStateHash));
            Assert.That(first.InitialStateHashHex, Is.EqualTo("b64a6e1047796dc5"));
            var bytes = first.CurrentSnapshot.CanonicalBytesForDiagnostics(ReplayFormat.Version);
            Assert.That(CanonicalHash.OfBytes(bytes), Is.EqualTo(first.InitialStateHash));
            bytes[0] ^= 1;
            Assert.That(first.CurrentSnapshot.ComputeHash(), Is.EqualTo(first.InitialStateHash), "Diagnostic bytes cannot alias authoritative state.");
            Assert.That(production.InitialStateHashHex, Is.EqualTo("c806f886b489d9c0"));
        }
    }
}
