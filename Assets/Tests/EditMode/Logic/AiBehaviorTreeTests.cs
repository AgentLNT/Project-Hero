using NUnit.Framework;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Determinism;

namespace ProjectHero.Logic.Tests
{
    public sealed class AiBehaviorTreeTests
    {
        [Test]
        public void RunningSequenceResumesLeafAndFailureAllowsSelectorFallback()
        {
            var memory = new AiBehaviorTask(); int submits = 0, waits = 0, fallback = 0;
            AiBehaviorStatus Submit(ref AiBehaviorTask task) { submits++; return AiBehaviorStatus.Success; }
            AiBehaviorStatus Await(ref AiBehaviorTask task) { waits++; return waits == 1 ? AiBehaviorStatus.Running : AiBehaviorStatus.Failure; }
            Assert.That(AiBehaviorNodes.Sequence(ref memory, Submit, Await), Is.EqualTo(AiBehaviorStatus.Running));
            Assert.That(memory.Cursor, Is.EqualTo(1));
            var status = AiBehaviorNodes.Selector(() => AiBehaviorNodes.Sequence(ref memory, Submit, Await),
                () => { fallback++; return AiBehaviorStatus.Success; });
            Assert.That(status, Is.EqualTo(AiBehaviorStatus.Success)); Assert.That(submits, Is.EqualTo(1));
            Assert.That(waits, Is.EqualTo(2)); Assert.That(fallback, Is.EqualTo(1)); Assert.That(memory.Cursor, Is.Zero);
        }

        [Test]
        public void EveryPersistentTaskFieldAndBothChannelsAreEncoded()
        {
            var empty = AiBehaviorMemory.Empty;
            string Hash(AiBehaviorMemory m) { var encoder = new CanonicalEncoder(); m.Encode(encoder); return encoder.ToDigestHex(); }
            string baseline = Hash(empty);
            foreach (var task in new[] { empty.Ordinary with { Cursor = 1 }, empty.Ordinary with { RequestTick = 2 },
                empty.Ordinary with { ProducerOrdinal = 3 }, empty.Ordinary with { PlanId = 4 },
                empty.Ordinary with { Status = AiBehaviorStatus.Running }, empty.Ordinary with { Reason = "other" },
                empty.Ordinary with { PredecessorPlanId = 9 } })
            {
                Assert.That(Hash(empty with { Ordinary = task }), Is.Not.EqualTo(baseline));
                Assert.That(Hash(empty with { Reaction = task }), Is.Not.EqualTo(baseline));
                Assert.That(Hash(empty with { Ordinary = task }), Is.Not.EqualTo(Hash(empty with { Reaction = task })));
            }
        }
    }
}
