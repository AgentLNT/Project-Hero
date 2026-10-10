using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task11MigrationContractTests : RuntimeOwnershipTestBase
    {
        [UnityTest] public IEnumerator MigrationShadowWithConfirmedFutureAttackKeepsRealCommonFieldsAndResolvesObligations()
        {
            const string caseId = "task11-shadow-confirmed-future-attack";
            yield return LoadLegacyComparisonScene(); Bootstrap.StopBattle("migration-prepare"); Bootstrap.ReleaseBattle();
            var source = (IBattleSimulationSource)typeof(BattleRuntimeBootstrap)
                .GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Bootstrap);
            var seed = source.BuildSeed();
            var evidenceType = ResolveProductionType("ProjectHero.Editor.RuntimeOwnership.Task11MigrationEvidence");
            var evidence = (System.Collections.Generic.IReadOnlyList<ShadowMigrationObligation>)evidenceType.GetMethod("Load")
                .Invoke(null, new object[] { caseId, seed.RulesVersion, seed.BattleDefinitionHash });
            Bootstrap.SetShadowMigrationEvidenceForDiagnostics(evidence);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True, Bootstrap.StartupRejection);
            Bootstrap.Shadow.CaseId = caseId;
            Bootstrap.DriveFrameForTests(1f / 60, 1f / 60); Bootstrap.RunCheckpointForTests();
            var hero = FindLegacyUnits().Hero;
            var library = hero.GetType().GetField("ActionLibrary").GetValue(hero);
            var entries = ((IEnumerable)library.GetType().GetField("Actions").GetValue(library)).Cast<object>();
            var action = entries.Select(e => e.GetType().GetField("Data").GetValue(e))
                .First(a => a.GetType().GetField("Type").GetValue(a).ToString() == "Attack");
            var scheduler = ResolveProductionType("ProjectHero.Core.Actions.ActionScheduler");
            scheduler.GetMethod("ScheduleAttack").Invoke(null, new object[] {
                FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline"), hero, action, 5f, null, 0L });
            long before = LegacyTimelineAdvanceTimeCalls();
            for (int frame = 0; frame < 25; frame++)
            { Bootstrap.DriveFrameForTests(1f / 60, 1f / 60); Bootstrap.RunCheckpointForTests(); }
            var bridge = FindProductionComponent("ProjectHero.Core.Compatibility.Authoring.ShadowSceneInputBridge");
            Assert.That((int)bridge.GetType().GetProperty("SubmittedCount").GetValue(bridge), Is.EqualTo(1));
            Assert.That((int)bridge.GetType().GetProperty("RejectedCount").GetValue(bridge), Is.Zero);
            Assert.That(Bootstrap.Shadow.CurrentSnapshot.Plans.Count, Is.EqualTo(1), "Confirmed input must reach the real independent Logic world.");
            Assert.That(LegacyTimelineAdvanceTimeCalls() - before, Is.EqualTo(25));
            Assert.That(Bootstrap.ShadowWrites.Total, Is.Zero);
            var report = Bootstrap.LastShadowReport;
            Assert.That(report.ResolvedMigrationObligations.Count, Is.EqualTo(60));
            Assert.That(report.TemporarilyUncomparable, Is.Empty);
            Assert.That(report.HasCompleteMigrationCoverage, Is.True, report.Describe());
            Assert.That(report.MigrationObservations.Any(o => o.Field == "scheduledEventCount"
                && o.LegacyValue != o.ShadowValue), Is.True, "Different representations must retain actual values, never pretend to be equal.");
            Assert.That(NewCutoverGate.Rejections(report, seed.BattleDefinitionHash, seed.RulesVersion, seed.EncounterId.Value), Is.Empty);
            File.WriteAllText("优化任务/执行记录/11-migration-shadow-accepted.txt", report.Describe() + "\nframes=26; confirmedFutureAttack=1; writes=0\n"
                + "Case covers actual confirmed scheduling before attack execution; attack/defense/forced movement rules use independent explicit evidence.\n"
                + string.Join("\n", report.MigrationObservations.Select(o => o.Tick + "|" + o.Field + "|legacy=" + o.LegacyValue + "|new=" + o.ShadowValue + "|" + o.ObligationId)));
            Bootstrap.StopBattle("migration-complete"); Bootstrap.ReleaseBattle();
        }
    }
}
