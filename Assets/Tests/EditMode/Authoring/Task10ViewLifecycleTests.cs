using System;
using System.Linq;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;
using ProjectHero.UnityView;
using UnityEngine;

namespace ProjectHero.Authoring.Tests
{
    // Native Unity component tests. Offline compilation is not evidence that these executed.
    public sealed class Task10ViewLifecycleTests
    {
        private GameObject _root, _unit;
        private GridView _grid;
        private CombatUnitView _view;
        private UnitSnapshot _snapshot;
        private BattleInitializationResult _mapping;
        [SetUp] public void Create()
        {
            _root = new GameObject("Task10VisualFixture"); _grid = _root.AddComponent<GridView>();
            _unit = new GameObject("NameDoesNotAuthorizeBinding"); _view = _unit.AddComponent<CombatUnitView>();
            _view.Configure("hero", _grid);
            _mapping = BattleInitializer.BuildInitialState(Task03.Task03.Definition, Task03.Task03.EncounterId, Task03.Task03.Inputs);
            using (var sim = Task03.Task03.NewSim()) _snapshot = sim.CurrentSnapshot.Units.Single(u => u.UnitId == Task03.Task03.HeroUnitId.Value);
            _view.Bind(_mapping, _snapshot);
        }
        [TearDown] public void Destroy()
        { UnityEngine.Object.DestroyImmediate(_unit); UnityEngine.Object.DestroyImmediate(_root); }
        private LogicSnapshot Movement(long tick, long start, long end)
            => new LogicSnapshot(tick, "test", "", "", BattleEndSnapshot.Active(), new[] { _snapshot }, null,
                null, null, null, 0, new[] { new ActionPlanSnapshot(9, _snapshot.UnitId,
                    State: (int)ActionPlanState.Running, StartTick: 0, EndTick: 100) }, null, null, null,
                new[] { new MovementSegmentSnapshot(9, 3, 0, 0, 2, 0, end, start) }, null, null, null, null,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, HistorySummary.Empty(), "test");
        [Test] public void MovementInterpolationUsesCurrentSegmentStartAfterEarlierSegmentsDisappear()
        {
            _view.ApplyMovementSnapshot(Movement(85, 80, 90));
            _view.ApplySnapshot(_snapshot with { X = 0, Y = 0 });
            _view.AdvanceVisual(0);
            Assert.That((_view.transform.position - Vector3.Lerp(_grid.GridToWorld(new GridPoint(0, 0)),
                _grid.GridToWorld(new GridPoint(2, 0)), .5f)).sqrMagnitude, Is.LessThan(.000001f));
        }
        [Test] public void MovementInterpolationDoesNotCommitLogicPosition()
        {
            using (var sim = Task03.Task03.NewSim())
            {
                ulong hash = sim.CurrentSnapshot.ComputeHash();
                _view.ApplyMovementSnapshot(Movement(85, 80, 90)); _view.ApplySnapshot(_snapshot with { X = 0, Y = 0 });
                _view.AdvanceVisual(1);
                Assert.That(sim.CurrentSnapshot.ComputeHash(), Is.EqualTo(hash));
                Assert.That(sim.Tick, Is.EqualTo(-1));
            }
        }
        [Test] public void InterruptedMovementStopsVisualDestinationAndReconcilesToSnapshot()
        {
            _view.ApplyMovementSnapshot(Movement(85, 80, 90)); _view.AdvanceVisual(.01f);
            _view.Consume(new ActionPlanTerminatedEvent(85, 1, 9, _snapshot.UnitId,
                (int)ActionTerminationReason.CancelledByCommand, "CancelledByCommand", 85, 0));
            var committed = _snapshot with { X = 0, Y = 0 };
            _view.ApplySnapshot(committed); _view.AdvanceVisual(1);
            Assert.That(_view.IsInterpolating, Is.False);
            Assert.That(_view.transform.position, Is.EqualTo(_grid.GridToWorld(new GridPoint(0, 0))));
        }
        [Test] public void RenamingGameObjectDoesNotChangeEncounterSlotOrUnitBinding()
        {
            _unit.name = "Unrelated changed name";
            Assert.That(_view.UnitId, Is.EqualTo(Task03.Task03.HeroUnitId));
            Assert.That(_view.FactionId.Value, Is.EqualTo(_snapshot.FactionId));
        }
        [Test] public void VisualReleaseRestoresAuthoredTransformAndClearsOldIdentity()
        {
            _view.ApplyMovementSnapshot(Movement(85, 80, 90)); _view.AdvanceVisual(.01f);
            _view.Unbind(); _view.Unbind();
            Assert.That(_view.UnitId.IsValid, Is.False);
            Assert.That(_view.LatestSnapshot, Is.Null);
            Assert.That(_view.IsInterpolating, Is.False);
            Assert.That(_unit.transform.position, Is.EqualTo(Vector3.zero));
            _view.Bind(_mapping, _snapshot);
            Assert.That(_view.UnitId, Is.EqualTo(Task03.Task03.HeroUnitId));
        }
    }
}
