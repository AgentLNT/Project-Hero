using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    // Classification is an audit of compatibility boundaries. It never approves a
    // difference or changes the production comparison policy.
    public sealed class Task11ShadowClassificationTests : RuntimeOwnershipTestBase
    {
        [Serializable] private sealed class Catalog { public string caseId, sceneCaseId, rulesVersion, definitionHash; public Entry[] entries; }
        [Serializable] private sealed class Entry
        {
            public string field, legacyObjectPath, category, reason, legacySource, newSource;
            public string[] tests;
        }

        [Test] public void EveryFullShadowRegistrationHasExactClassificationAndExecutableEvidence()
        {
            var policy = ShadowCasePolicy.CreateDefault("task12C-final-classification", "battle-def-v4-defense-duration", true, true, true, true);
            Assert.That(policy.TemporarilyUncomparable.Count, Is.EqualTo(73));
            Assert.That(policy.Approvals, Is.Empty, "Classifying new structures cannot approve old/new equality.");
            var catalog = JsonUtility.FromJson<Catalog>(File.ReadAllText("优化任务/执行记录/12C-shadow-classification.json"));
            Assert.That(catalog.caseId, Is.EqualTo(policy.CaseId));
            Assert.That(catalog.sceneCaseId, Is.EqualTo(ShadowBattleRunner.DefaultCaseId));
            Assert.That(catalog.rulesVersion, Is.EqualTo(policy.RulesVersion));
            Assert.That(catalog.definitionHash, Is.EqualTo("e04b3c0946ad3035"));
            Assert.That(catalog.entries.Length, Is.EqualTo(policy.TemporarilyUncomparable.Count));
            var byKey = catalog.entries.ToDictionary(e => e.legacyObjectPath + "#" + e.field, StringComparer.Ordinal);
            Assert.That(typeof(ProjectHero.Logic.Snapshots.UnitSnapshot).GetProperties()
                .Any(p => p.Name.IndexOf("stamina", StringComparison.OrdinalIgnoreCase) >= 0), Is.False,
                "The removed resource must not survive in the authoritative unit projection.");
            Assert.That(typeof(ProjectHero.Logic.Snapshots.BattleResourceSnapshot).GetProperties()
                .Any(p => p.Name.IndexOf("stamina", StringComparison.OrdinalIgnoreCase) >= 0), Is.False);
            foreach (var field in policy.TemporarilyUncomparable)
            {
                var entry = byKey[field.LegacyObjectPath + "#" + field.Field];
                Assert.That(new[] { "NewRule", "ReplacedRepresentation", "RemovedResource", "RemovedRepresentation", "DiagnosticOnly" }, Does.Contain(entry.category));
                Assert.That(entry.reason, Is.Not.Empty); Assert.That(entry.tests.Length, Is.GreaterThan(0));
                Assert.That(File.Exists(entry.legacySource), Is.True, entry.field + " legacy source");
                Assert.That(File.Exists(entry.newSource), Is.True, entry.field + " new source");
                foreach (var test in entry.tests)
                    Assert.That(Directory.EnumerateFiles("Assets/Tests", "*.cs", SearchOption.AllDirectories)
                        .Any(path => File.ReadAllText(path).Contains(" " + test + "(")), Is.True, entry.field + " evidence=" + test);
            }
            TestContext.WriteLine("Classification rows=73; approvals=0; production temporary registrations retained=73; this audit does not claim Legacy/New equivalence.");
        }

        [UnityTest] public IEnumerator MainSceneShadowRetainsRealLegacyObservationsAndWritesNothingToPresentation()
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("classification-prepare"); Bootstrap.ReleaseBattle();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True, Bootstrap.StartupRejection);
            long before = LegacyTimelineAdvanceTimeCalls();
            for (int tick = 0; tick < 120; tick++) { Bootstrap.DriveFrameForTests(1f / 60, 1f / 60); Bootstrap.RunCheckpointForTests(); }
            Assert.That(LegacyTimelineAdvanceTimeCalls() - before, Is.EqualTo(120));
            Assert.That(Bootstrap.NewDriver.AdvanceCallCount, Is.Zero);
            Assert.That(Bootstrap.ShadowWrites.Total, Is.Zero);
            Assert.That(Bootstrap.LegacyObservations.Count, Is.GreaterThan(0));
            Assert.That(Bootstrap.ShadowReports.All(r => r.ComparedCheckpoints > 0 && r.InfrastructureDifferences == 0
                && r.UnalignedCheckpoints == 0 && r.Rejections.Count == 0), Is.True);
            var report = Bootstrap.LastShadowReport;
            File.WriteAllText("优化任务/执行记录/12C-shadow-scene-observations.txt",
                "scene=LegacyComparisonScene (Editor-only diagnostic copy)\nmode=Shadow\nframes=120\nlegacyAdvanceDelta=120\nnewDriverAdvance=0\nshadowWrites="
                + Bootstrap.ShadowWrites.Total + "\nobservations=" + Bootstrap.LegacyObservations.Count
                + "\n" + report.Describe() + "\n" + string.Join("\n", report.Differences.Select(d => d.ToString())));
            TestContext.WriteLine(report.Describe());
            Bootstrap.StopBattle("classification-complete"); Bootstrap.ReleaseBattle();
        }
    }
}
