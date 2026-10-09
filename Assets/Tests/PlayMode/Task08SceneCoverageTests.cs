using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    public class Task08SceneCoverageTests : RuntimeOwnershipTestBase
    {
        private static readonly BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private object Bridge => FindProductionComponent("ProjectHero.Core.Compatibility.Authoring.ShadowSceneInputBridge");
        private static object Field(object target, string name) => target.GetType().GetField(name, Fields).GetValue(target);
        private static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name).GetValue(target);
        private void StartShadow()
        {
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True);
            Bootstrap.enabled = false;
            Bootstrap.DriveFrameForTests(0, 0);
            Assert.That(Bootstrap.Adapters.Shadow.CurrentSnapshot.Tick, Is.EqualTo(0));
        }
        private void ScheduleLegacyAttack(bool enemy = false)
        {
            var source = FindSimulationSourceFactory();
            object unit = Field(source, enemy ? "_enemyUnit" : "_heroUnit");
            object library = Field(unit, "ActionLibrary");
            object entry = ((IList)Field(library, "Actions"))[0];
            object action = Field(entry, "Data");
            object timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            ResolveProductionType("ProjectHero.Core.Actions.ActionScheduler").GetMethod("ScheduleAttack")
                .Invoke(null, new[] { timeline, unit, action, (object)0f, null, 0L });
        }

        [UnityTest]
        public IEnumerator ActualScenePlayerConfirmationEntersShadowExactlyOnce()
        {
            yield return LoadHiddenValidationScene(); StartShadow();
            var units = FindLegacyUnits();
            float heroHealth = ReadLegacyFloat(units.Hero, "CurrentHealth");
            float enemyHealth = ReadLegacyFloat(units.Enemy, "CurrentHealth");
            ScheduleLegacyAttack();
            Assert.That(Property<int>(Bridge, "SubmittedCount"), Is.EqualTo(1));
            Assert.That(Bootstrap.Adapters.Shadow.LivePlayerRequestCount, Is.EqualTo(1));
            Assert.That(ReadLegacyFloat(units.Hero, "CurrentHealth"), Is.EqualTo(heroHealth));
            Assert.That(ReadLegacyFloat(units.Enemy, "CurrentHealth"), Is.EqualTo(enemyHealth));
            Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
            var authority = Bootstrap.ShadowAuthorityInput;
            Assert.That(authority.AuthorityCommands.Count, Is.EqualTo(1));
            Assert.That(authority.CommandOutcomes.Count, Is.EqualTo(1));
            Assert.That(Bootstrap.Adapters.Shadow.CurrentSnapshot.Plans.Count, Is.EqualTo(1),
                "Must reach command processing and create a plan, not just accept at ingress");
            for (int i = 0; i < 3; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
            Assert.That(authority.AuthorityCommands.Count, Is.EqualTo(1));
            Assert.That(Bootstrap.ShadowWrites.Total, Is.Zero);
        }
        [UnityTest]
        public IEnumerator SceneShadowInputDropsPauseAndExcludesLegacyAiRequests()
        {
            yield return LoadHiddenValidationScene(); StartShadow();
            ScheduleLegacyAttack(enemy: true);
            Assert.That(Bootstrap.Adapters.Shadow.LivePlayerRequestCount, Is.Zero);
            Bootstrap.SetPaused(true); ScheduleLegacyAttack();
            Assert.That(Property<string>(Bridge, "LastRejection"), Is.EqualTo("SHADOW_INPUT_PAUSED"));
            Assert.That(Bootstrap.Adapters.Shadow.LivePlayerRequestCount, Is.Zero);
            Bootstrap.SetPaused(false);
            Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
            Assert.That(Bootstrap.ShadowAuthorityInput.AuthorityCommands, Is.Empty);
        }
        [UnityTest]
        public IEnumerator MainSceneKeepsLegacyAndExplicitShadowBindings()
        {
            yield return LoadMainScene();
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.Legacy));
            Assert.That(Bridge, Is.Not.Null);
            Assert.That(Field(Bridge, "_bootstrap"), Is.SameAs(Bootstrap));
            Assert.That(Field(Bridge, "_source"), Is.SameAs(FindSimulationSourceFactory()));
            Assert.That(Bootstrap.ShadowAuthorityInput.AuthorityCommands, Is.Empty);
        }

        // These are independent new-rule golden streams. Legacy has no multi-body/partial-payload
        // equivalent. Both sides run real production Steps; fixtures only supply definitions/commands.
        [UnityTest]
        public IEnumerator ArbitrationDamageAndDefenseShadowProfileHasNoUnclassifiedDifference()
        {
            yield return LoadHiddenValidationScene();
            string[] profiles = { "dodge", "still-hit", "block-partial", "aoe", "aoe-block", "guard", "clash", "clash-residual",
                "three-way-clash", "flanking", "reservation", "mutual-death", "simultaneous-displacement", "intercept", "lethal-displacement", "dodge-dependency" };
            foreach (string profile in profiles)
            {
                using var left = BuildProfile(profile); using var right = BuildProfile(profile);
                Assert.That(left.Sim, Is.Not.SameAs(right.Sim));
                Assert.That(left.Result.Snapshot, Is.Not.SameAs(right.Result.Snapshot));
                Assert.That(left.Result.Snapshot.ComputeHash(), Is.EqualTo(right.Result.Snapshot.ComputeHash()));
                AssertGolden(profile, left);
                var config = ShadowComparisonConfig.Default();
                var policy = ShadowCasePolicy.CreateDefault("task08-golden-" + profile, left.Sim.RulesVersion,
                    compareScheduleFacts: true, compareMovementFacts: true, compareTurnWindowFacts: true, compareTask08ProfileFacts: true);
                var report = ShadowDifferenceDetector.Compare(new ShadowComparisonInput(left.Sim.BattleDefinitionHash,
                    left.Sim.EncounterId.Value, BattleRuntimeMode.Shadow, profile, left.Sim.RulesVersion, config,
                    new[] { left.Result.Snapshot }, new[] { right.Result.Snapshot }), policy);
                Assert.That(report.ComparedCheckpoints, Is.EqualTo(1));
                Assert.That(report.ComparedFieldObservations, Is.GreaterThan(0));
                Assert.That(report.HasUnexpectedDifference, Is.False, report.Describe());
                Assert.That(EventText(left.Result.Events.Events), Is.EqualTo(EventText(right.Result.Events.Events)),
                    "Every semantic event field, including staged-only payloads, must compare");
                var corruptedUnits = right.Result.Snapshot.Units.ToArray();
                // The comparer must reject a concrete changed observation; a green report alone is insufficient.
                var mutated = CopySnapshot(right.Result.Snapshot, corruptedUnits.Select((u, i) => i == 0 ? u with { HealthQ10 = u.HealthQ10 - 1 } : u).ToArray());
                var probe = ShadowDifferenceDetector.Compare(new ShadowComparisonInput(left.Sim.BattleDefinitionHash,
                    left.Sim.EncounterId.Value, BattleRuntimeMode.Shadow, profile, left.Sim.RulesVersion, config,
                    new[] { left.Result.Snapshot }, new[] { mutated }), policy);
                Assert.That(probe.HasUnexpectedDifference, Is.True, "One health bit cannot disappear into an allowlist");
                TestContext.Out.WriteLine("TASK08_GOLDEN|" + profile + "|tick=" + left.Sim.Tick + "|events=" + left.Result.Events.Count + "|fields=" + report.ComparedFieldObservations);
            }
            Assert.That(Bootstrap.ShadowWrites.Total, Is.Zero);
        }

        private sealed class Profile : IDisposable
        {
            private readonly IDisposable _rig;
            public Profile(object rig) { _rig = (IDisposable)rig; Sim = (BattleSimulation)Field(rig, "Sim"); Result = (StepResult)Field(rig, "Result"); }
            public BattleSimulation Sim { get; } public StepResult Result { get; }
            public void Dispose() => _rig.Dispose();
        }
        private static Type Fixture(string suffix)
            => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ProjectHero.Logic.Tests." + suffix)).First(t => t != null);
        private static object Invoke(Type type, string method, params object[] args)
            => type.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, args);
        private static Profile BuildProfile(string name)
        {
            var defense = Fixture("Task08DefenseCoverageTests");
            if (name == "three-way-clash" || name == "flanking")
                return new Profile(Invoke(Fixture("Task08MultiPartyCoverageTests"), "Multi", name == "three-way-clash"));
            if (name == "reservation") return new Profile(Invoke(Fixture("Task08MultiPartyCoverageTests"), "Reservation"));
            if (name == "guard") return new Profile(Invoke(Fixture("Task08CommitCoverageTests"), "Guard"));
            if (name == "clash" || name == "clash-residual" || name == "mutual-death" || name == "simultaneous-displacement")
                return new Profile(Invoke(Fixture("Task08RemainingCoverageTests"), "Duel", name == "mutual-death",
                    name == "clash" || name == "clash-residual", name == "clash-residual" ? 20f : 10f, name == "simultaneous-displacement" ? 1f : 0f));
            if (name == "dodge-dependency") return new Profile(Invoke(Fixture("Task08CommitCoverageTests"), "MovingTarget", true, false, false));
            if (name == "intercept" || name == "lethal-displacement")
                return new Profile(Invoke(Fixture("Task08CommitCoverageTests"), "MovingTarget", false, false, name == "lethal-displacement"));
            object definition = Invoke(defense, "Definition", name == "still-hit", name == "aoe" || name == "aoe-block", name == "block-partial", false, 2000, 1f, false);
            return new Profile(Invoke(defense, "ReactionRig", name == "block-partial" || name == "aoe-block" ? ReactionCommandKind.Block : ReactionCommandKind.Dodge, definition, null));
        }
        private static void AssertGolden(string profile, Profile p)
        {
            var events = p.Result.Events.Events;
            if (profile == "dodge" || profile == "aoe") Assert.That(events.OfType<DodgeResolvedEvent>().Single().RewardedSuccess, Is.True);
            if (profile == "still-hit") Assert.That(p.Sim.LastDamageCommitReport.Units.Single().DamageQ10, Is.EqualTo(10240));
            if (profile == "block-partial") Assert.That(p.Sim.LastDamageCommitReport.Units.Single().DamageQ10, Is.EqualTo(3072));
            if (profile == "aoe") Assert.That(p.Sim.LastDamageCommitReport.Units.Single().UnitId.Value, Is.EqualTo(5));
            if (profile == "aoe-block") { Assert.That(events.OfType<BlockResolvedEvent>().Count(), Is.EqualTo(1)); Assert.That(p.Sim.LastDamageCommitReport.Units.Single().UnitId.Value, Is.EqualTo(5)); }
            if (profile == "guard") Assert.That(p.Sim.LastDamageCommitReport.Units.Single().DamageQ10, Is.EqualTo(5120));
            if (profile == "clash-residual") { Assert.That(events.OfType<ClashResidualImpactResolvedEvent>().Sum(e => e.ResidualMomentumUnits), Is.EqualTo(10000)); Assert.That(p.Sim.LastDamageCommitReport.Units.Single().DamageQ10, Is.EqualTo(10240)); }
            if (profile == "clash") Assert.That(events.OfType<ActionPlanTerminatedEvent>().Count(), Is.EqualTo(2));
            if (profile == "three-way-clash") Assert.That(events.OfType<ClashParticipantResolvedEvent>().Count(), Is.EqualTo(3));
            if (profile == "flanking") { Assert.That(p.Sim.StagedResolution.Aggregations.Single().TotalDamageQ10, Is.EqualTo(20480)); Assert.That(p.Sim.StagedResolution.Aggregations.Single().ResultantMomentumUnits, Is.Zero); }
            if (profile == "reservation") Assert.That(events.OfType<ForcedDisplacementResolvedEvent>().Single().InvalidatedPlanIds.Count, Is.EqualTo(1));
            if (profile == "mutual-death") Assert.That(events.OfType<UnitDiedEvent>().Count(), Is.EqualTo(2));
            if (profile == "simultaneous-displacement") Assert.That(events.OfType<ForcedDisplacementResolvedEvent>().Count(), Is.EqualTo(2));
            if (profile == "dodge-dependency") Assert.That(events.OfType<ActionPlanTerminatedEvent>().Count(), Is.EqualTo(2));
            if (profile == "intercept") Assert.That(events.OfType<MoveContactResolvedEvent>().Count(), Is.EqualTo(1));
            if (profile == "lethal-displacement") { Assert.That(events.OfType<UnitDiedEvent>().Count(), Is.EqualTo(1)); Assert.That(events.OfType<ForcedDisplacementResolvedEvent>().Single().AppliedSteps, Is.EqualTo(1)); }
        }
        private static string EventText(object value)
        {
            if (value == null) return "null";
            if (value is string s) return s;
            if (value is IEnumerable list) return "[" + string.Join(";", list.Cast<object>().Select(EventText)) + "]";
            var type = value.GetType();
            if (type.IsEnum || type.IsPrimitive || value is decimal) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            return type.FullName + "{" + string.Join(";", type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + "=" + EventText(f.GetValue(value))))
                + "|" + string.Join(";", type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + EventText(p.GetValue(value)))) + "}";
        }
        private static LogicSnapshot CopySnapshot(LogicSnapshot s, IReadOnlyList<UnitSnapshot> units)
            => new LogicSnapshot(s.Tick, s.RulesVersion, s.BattleDefinitionHash, s.EncounterId,
                s.BattleEnd, units, s.Effects, s.WindowManager, s.ConcurrentAction, s.Resources,
                s.ScheduleRevision, s.Plans, s.ReactionOpportunities, s.ActorLanes, s.Intents,
                s.MovementSegments, s.Reservations, s.AiControllers, s.CommandIngresses, s.Rng,
                s.NextUnitId, s.NextActionPlanId, s.NextReactionOpportunityId, s.NextWindowId,
                s.NextEffectId, s.NextCommandSequence, s.NextIntentSequence, s.NextResolutionSequence,
                s.NextEventSequence, s.NextEffectSequence, s.History, s.CommandSourcePriorityMappingVersion,
                s.TerminalPlanRecordCount, s.TerminalPlanDigest, s.NextReactionOptionSequence,
                s.ConflictGroups, s.Contacts);
    }
}
