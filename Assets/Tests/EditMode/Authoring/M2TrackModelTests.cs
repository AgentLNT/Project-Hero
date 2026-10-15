using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.UnityView;

namespace ProjectHero.Authoring.Tests
{
    public sealed class M2TrackModelTests
    {
        private sealed class Relations : IFactionRelationResolver
        {
            public UnitRelation Classify(UnitId source, UnitId target) => target.Value == source.Value ? UnitRelation.Self
                : target.Value == 2 ? UnitRelation.Allied : target.Value == 3 ? UnitRelation.Neutral : UnitRelation.Hostile;
            public bool Allows(TargetRelationMask mask, UnitId source, UnitId target) => true;
        }
        private static DecisionSnapshot Decision() => new DecisionSnapshot(0, "m2", "m2",
            Enumerable.Range(1, 6).Reverse().Select(id => new UnitSnapshot(id, "unit", "faction", id * 2, 0, 0, 1024, true, 2, 0)).ToArray(),
            Array.Empty<ActionPlanSnapshot>(), 0, BattleEndSnapshot.Active(), new Relations())
            .ForController(new ControllerId("player"), new[] { new UnitId(1) }, null);
        [Test] public void SelectionUsesTrackOrderHistoryAndNeverMovesPrimaryToTop()
        {
            var s = new TrackSelectionModel(); var d = Decision();
            s.Click(4, false); s.Click(1, true); s.Click(3, true); s.Click(2, true);
            Assert.That(s.Ordered(d).Select(u => u.UnitId), Is.EqualTo(new long[] {1, 2, 3, 4}));
            Assert.That(s.Primary, Is.EqualTo(2));
            s.Cycle(d, false); Assert.That(s.Primary, Is.EqualTo(3));
            s.Cycle(d, true); Assert.That(s.Primary, Is.EqualTo(2));
            s.Click(2, false); Assert.That(s.Primary, Is.EqualTo(3));
            s.Click(6, false); Assert.That(s.Ordered(d).Select(u => u.UnitId), Is.EqualTo(new long[] {6}));
            s.Click(6, true); Assert.That(s.Primary, Is.Zero); Assert.That(s.Ordered(d), Is.Empty);
        }
        [Test] public void TemporaryThreatTracksDisappearUnlessExplicitlyFocusedAndSelectionHasNoLimit()
        {
            var s = new TrackSelectionModel(); var d = Decision(); s.Focus(1); s.AddTemporary(4); s.AddTemporary(5);
            Assert.That(s.Primary, Is.EqualTo(1)); s.Focus(4); s.SyncTemporary(Array.Empty<long>());
            Assert.That(s.Contains(4), Is.True); Assert.That(s.IsTemporary(4), Is.False); Assert.That(s.Contains(5), Is.False);
            for (int i = 1; i <= 6; i++) s.Focus(i);
            Assert.That(s.Ordered(d).Count, Is.EqualTo(6)); s.Reset(); Assert.That(s.Primary, Is.Zero);
        }
        [Test] public void DynamicTracksUseOrdinalSpawnIdInsteadOfArrivalOrClickOrder()
        {
            var s = new TrackSelectionModel(); var d = Decision();
            s.Layout.ObserveSpawn(4, "z.later"); s.Layout.ObserveSpawn(6, "a.later");
            s.Focus(4); s.Focus(6); s.Focus(5);
            Assert.That(s.Ordered(d).Select(u => u.UnitId), Is.EqualTo(new long[] {5,6,4}));
            s.Cycle(d, false); Assert.That(s.Primary, Is.EqualTo(6));
        }
        [Test] public void SnapAlignsActualImpactTicksWithinLegalWindowAndLeavesInvalidPositionsVisible()
        {
            var moving = new ActionPlanSnapshot(1, 1, "attack", StartTick: 20, EndTick: 100, ImpactTick: 80);
            var other = new ActionPlanSnapshot(2, 4, "attack", StartTick: 5, EndTick: 110, ImpactTick: 90);
            var snap = TimelineSnap.Resolve(moving, 28, new[] {moving, other}, 1, 179, 3, _ => true);
            Assert.That(snap.Snapped, Is.True); Assert.That(snap.StartTick, Is.EqualTo(30)); Assert.That(snap.AnchorTick, Is.EqualTo(90));
            Assert.That(TimelineSnap.Resolve(moving, 180, new[] {other}, 1, 179, 9, _ => true).Snapped, Is.False);
            Assert.That(TimelineSnap.Resolve(moving, 28, new[] {moving}, 1, 179, 9, _ => true).Snapped, Is.False);
            Assert.That(TimelineSnap.Resolve(moving, 28, new[] {other}, 31, 179, 3, _ => true).Snapped, Is.False);
        }
    }
}
