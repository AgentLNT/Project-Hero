using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic.Actions;
using ProjectHero.UnityView;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Replay;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Events;
using ProjectHero.Logic.Definitions;
using System.IO;

namespace ProjectHero.Tests.PlayMode
{
    public sealed class Task10WiredPresentationTests : RuntimeOwnershipTestBase
    {
        private static object Field(object value, string name)
            => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        private static void Click(object button)
            => ((UnityEvent)button.GetType().GetProperty("onClick").GetValue(button)).Invoke();

        private static void CaptureView(BattlePresentationView presentation, string name)
        {
            if (UnityEngine.Rendering.GraphicsDeviceType.Null == SystemInfo.graphicsDeviceType) return;
            var camera = (Camera)Field(presentation, "_camera");
            var canvas = ((GameObject)Field(presentation, "_panel")).GetComponentInParent<Canvas>();
            var mode = canvas.renderMode; var worldCamera = canvas.worldCamera; var distance = canvas.planeDistance;
            var target = camera.targetTexture; var previous = RenderTexture.active;
            var texture = new RenderTexture(1920, 1080, 24);
            var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            try
            {
                texture.Create(); camera.targetTexture = texture;
                // Camera-space capture reproduces the overlay layout in the same viewport.
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = camera.nearClipPlane + 1;
                presentation.GetType().GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(presentation, null);
                Canvas.ForceUpdateCanvases();
                var requestType = Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipeline+SingleCameraRequest, Unity.RenderPipelines.Universal.Runtime", true);
                var request = Activator.CreateInstance(requestType); requestType.GetField("destination").SetValue(request, texture);
                typeof(UnityEngine.Rendering.RenderPipeline).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.Name == "SubmitRenderRequest" && m.IsGenericMethodDefinition)
                    .MakeGenericMethod(requestType).Invoke(null, new[] { camera, request });
                RenderTexture.active = texture; pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
                var path = Path.GetFullPath("优化任务/执行记录/" + name + ".png");
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                TestContext.WriteLine("Actual URP presentation capture: " + path + " device=" + SystemInfo.graphicsDeviceName);
            }
            finally
            {
                RenderTexture.active = previous; camera.targetTexture = target;
                canvas.renderMode = mode; canvas.worldCamera = worldCamera; canvas.planeDistance = distance;
                UnityEngine.Object.Destroy(pixels); UnityEngine.Object.Destroy(texture);
            }
        }

        private static bool Contact(DecisionSnapshot decision, UnitSnapshot owner, UnitSnapshot target,
            AttackPayloadSpec attack, GridDirection facing, GridPoint origin)
        {
            var volume = decision.Definition.FindVolume(decision.Definition.FindUnit(new UnitDefinitionId(target.DefinitionId)).VolumeSpecId);
            var targetCells = volume.Directions.Single(d => (int)d.Direction == target.Facing).Triangles;
            return attack.Pattern != null && attack.Pattern.Directions.Single(d => d.Direction == facing).Triangles
                .Any(a => targetCells.Any(b => origin.X + a.X == target.X + b.X && origin.Y + a.Y == target.Y + b.Y && a.T == b.T));
        }
        private static bool DraftCombatAction(BattlePresentationModel model)
        {
            var decision = model.Decision; var hero = decision.ControlledUnitIds.Single();
            var owner = decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            var origin = new GridPoint(owner.X, owner.Y);
            var attacks = decision.ActionSetOf(hero).Select(decision.FindAction).Where(s => s.Type == ActionType.Attack).ToArray();
            foreach (var spec in attacks)
                foreach (var target in model.Targets(hero, spec.ActionSpecId))
                    for (int f = 0; f < 12; f++)
                    {
                        var facing = (GridDirection)f;
                        if (!Contact(decision, owner, target, (AttackPayloadSpec)spec.Payload, facing, origin)) continue;
                        var result = model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, spec.ActionSpecId,
                            PrimaryTargetUnitId: new UnitId(target.UnitId), Facing: facing) });
                        if (result?.Succeeded == true) return true;
                    }
            var destinations = new System.Collections.Generic.HashSet<GridPoint>();
            foreach (var spec in attacks)
                foreach (var target in model.Targets(hero, spec.ActionSpecId))
                {
                    var attack = (AttackPayloadSpec)spec.Payload;
                    if (attack.Pattern == null) continue;
                    var volume = decision.Definition.FindVolume(decision.Definition.FindUnit(new UnitDefinitionId(target.DefinitionId)).VolumeSpecId);
                    foreach (var a in attack.Pattern.Directions.SelectMany(d => d.Triangles))
                        foreach (var b in volume.Directions.Single(d => (int)d.Direction == target.Facing).Triangles)
                            if (a.T == b.T && ((target.X + b.X - a.X + target.Y + b.Y - a.Y) & 1) == 0)
                                destinations.Add(new GridPoint(target.X + b.X - a.X, target.Y + b.Y - a.Y));
                }
            foreach (var spec in decision.ActionSetOf(hero).Select(decision.FindAction).Where(s => s.Type == ActionType.Move))
                foreach (var destination in destinations.OrderBy(p => Math.Abs(p.X - owner.X) + Math.Abs(p.Y - owner.Y)).ThenBy(p => p.X).ThenBy(p => p.Y))
                {
                    var result = model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, spec.ActionSpecId, Destination: destination) });
                    if (result?.Succeeded == true) return true;
                }
            model.CancelDraft(); return false;
        }

        private IEnumerator CompleteColdLegacyBattle()
        {
            yield return LoadMainScene();
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.Legacy));
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.False);
            var units = FindLegacyUnits();
            var hero = units.Hero; var enemy = units.Enemy;
            var unitType = hero.GetType();
            var timeline = FindProductionComponent("ProjectHero.Core.Timeline.BattleTimeline");
            var manager = FindProductionComponent("ProjectHero.Core.Gameplay.BattleManager");
            var grid = FindProductionComponent("ProjectHero.Core.Grid.GridManager");
            var scheduler = ResolveProductionType("ProjectHero.Core.Actions.ActionScheduler");
            var pathfinderType = ResolveProductionType("ProjectHero.Core.Pathfinding.Pathfinder");
            var pointType = unitType.GetProperty("GridPosition").PropertyType;
            var initialHero = unitType.GetProperty("GridPosition").GetValue(hero);
            Assert.That((int)pointType.GetField("X").GetValue(initialHero), Is.EqualTo(-5));
            Assert.That((int)pointType.GetField("Y").GetValue(initialHero), Is.EqualTo(-5));
            Assert.That((float)unitType.GetField("CurrentHealth").GetValue(hero), Is.EqualTo(200f));
            Assert.That((float)unitType.GetField("CurrentHealth").GetValue(enemy), Is.EqualTo(200f));
            var directionType = unitType.GetField("FacingDirection").FieldType;
            var library = unitType.GetField("ActionLibrary").GetValue(hero);
            var entries = (IEnumerable)library.GetType().GetField("Actions").GetValue(library);
            var actions = entries.Cast<object>().Select(e => e.GetType().GetField("Data").GetValue(e))
                .Where(a => a != null && a.GetType().GetField("Type").GetValue(a).ToString() == "Attack"
                    && a.GetType().GetField("Pattern").GetValue(a) != null).ToArray();
            Assert.That(actions, Is.Not.Empty);
            int ReadInt(object value, string name) => (int)value.GetType().GetField(name).GetValue(value);
            int attacks = 0, moves = 0;
            for (int frame = 0; frame < 12000 && !(bool)manager.GetType().GetProperty("BattleEnded").GetValue(manager); frame++)
            {
                if ((bool)unitType.GetProperty("CanAct").GetValue(hero))
                {
                    var origin = unitType.GetProperty("GridPosition").GetValue(hero);
                    var targets = ((IEnumerable)unitType.GetMethod("GetOccupiedTriangles").Invoke(enemy, null)).Cast<object>().ToArray();
                    bool scheduled = false;
                    var destinations = new System.Collections.Generic.Dictionary<(int, int), object>();
                    foreach (var action in actions)
                    {
                        var pattern = action.GetType().GetField("Pattern").GetValue(action);
                        var affected = pattern.GetType().GetMethod("GetAffectedTriangles");
                        for (int f = 0; f < 12 && !scheduled; f++)
                        {
                            var facing = Enum.ToObject(directionType, f);
                            var cells = ((IEnumerable)affected.Invoke(pattern, new[] { origin, facing })).Cast<object>().ToArray();
                            if (cells.Any(a => targets.Any(b => a.Equals(b))))
                            {
                                scheduler.GetMethod("ScheduleAttack").Invoke(null, new[] { timeline, hero, action, (object)0f, facing, 0L });
                                attacks++; scheduled = true; break;
                            }
                            var zero = Activator.CreateInstance(pointType, 0, 0);
                            var relative = ((IEnumerable)affected.Invoke(pattern, new[] { zero, facing })).Cast<object>();
                            foreach (var a in relative)
                                foreach (var b in targets)
                                    if (ReadInt(a, "T") == ReadInt(b, "T"))
                                    {
                                        int x = ReadInt(b, "X") - ReadInt(a, "X"), y = ReadInt(b, "Y") - ReadInt(a, "Y");
                                        if (((x + y) & 1) == 0) destinations[(x, y)] = Activator.CreateInstance(pointType, x, y);
                                    }
                        }
                        if (scheduled) break;
                    }
                    if (!scheduled)
                    {
                        var obstacles = grid.GetType().GetMethod("GetGlobalObstacles").Invoke(grid, new[] { hero });
                        var volume = unitType.GetField("UnitVolumeDefinition").GetValue(hero);
                        foreach (var destination in destinations.OrderBy(d => Math.Abs(d.Key.Item1 - ReadInt(origin, "X")) + Math.Abs(d.Key.Item2 - ReadInt(origin, "Y")))
                            .ThenBy(d => d.Key.Item1).ThenBy(d => d.Key.Item2))
                        {
                            var pathfinder = Activator.CreateInstance(pathfinderType);
                            var path = pathfinderType.GetMethod("FindPath").Invoke(pathfinder, new[] { origin, destination.Value, volume, obstacles }) as IList;
                            if (path == null || path.Count < 2) continue;
                            scheduler.GetMethod("ScheduleMoveTo").Invoke(null, new[] { timeline, hero, destination.Value, (object)0f, 0L });
                            moves++; break;
                        }
                    }
                }
                Bootstrap.DriveFrameForTests(1f / 60, 1f / 60);
                if (frame % 12 == 0) yield return null;
            }
            yield return null;
            Assert.That((bool)manager.GetType().GetProperty("BattleEnded").GetValue(manager), Is.True,
                "Cold Legacy battle must really finish; attacks=" + attacks + " moves=" + moves);
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.GreaterThan(0));
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.False);
            TestContext.WriteLine("Cold Legacy rehearsal completed through its own scheduler; attacks=" + attacks + " moves=" + moves
                + " result=" + manager.GetType().GetProperty("EndMessage").GetValue(manager));
            Bootstrap.StopBattle("cold-legacy-completed"); Bootstrap.ReleaseBattle();
            Time.timeScale = 1;
        }

        [UnityTest] public IEnumerator MainSceneNewCompletesThreeColdBattlesWithPlayerCommandsAndOneEndFeedback()
        {
            BattleRuntimeBootstrap previous = null;
            ulong? initialHash = null;
            for (int run = 0; run < 4; run++)
            {
                if (run == 3) yield return CompleteColdLegacyBattle();
                yield return LoadMainScene();
                Assert.That(ReferenceEquals(previous, Bootstrap), Is.False, "Each scene reload creates a fresh Bootstrap.");
                previous = Bootstrap;
                Bootstrap.StopBattle("complete-smoke-prepare"); Bootstrap.ReleaseBattle();
                long legacyBefore = LegacyTimelineAdvanceTimeCalls();
                Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
                Assert.That(Bootstrap.NewDriver.TicksAdvanced, Is.Zero);
                // The pre-Step canonical snapshot is labelled Tick 0; the driver has advanced zero Ticks.
                Assert.That(Bootstrap.NewDriver.CurrentSnapshot.Tick, Is.EqualTo(0));
                Assert.That(Bootstrap.NewDriver.CurrentSnapshot.Plans, Is.Empty);
                if (initialHash == null) initialHash = Bootstrap.NewDriver.ReplayHeader.InitialStateHash;
                Assert.That(Bootstrap.NewDriver.ReplayHeader.InitialStateHash, Is.EqualTo(initialHash.Value), "Cold mode changes never transfer the preceding world's state.");
                var presentation = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<BattlePresentationView>(true)).Single();
                var feedback = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<BattleFeedbackPlayer>(true)).Single();
                var model = presentation.Model;
                long lastClose = 0;
                for (int frame = 0; frame < 12000 && !model.Decision.BattleEnd.IsEnded; frame++)
                {
                    var own = model.Decision.ControlledUnitIds.Single();
                    if (model.Window?.OwnerUnitId == own.Value && model.Window.WindowId != lastClose)
                    {
                        bool hasPlan = model.Decision.OwnPlans.Any(p => p.OwnerUnitId == own.Value);
                        if (!hasPlan && DraftCombatAction(model)) Assert.That(model.ConfirmDraft(), Is.Null);
                        else { lastClose = model.Window.WindowId; Assert.That(model.CloseWindow(), Is.Null); }
                    }
                    Bootstrap.DriveFrameForTests(0, 1f / 60);
                    if (run == 0 && frame == 0) CaptureView(presentation, "10-main-new-start");
                    if (frame % 120 == 0) yield return null;
                }
                Assert.That(model.Decision.BattleEnd.IsEnded, Is.True,
                    "run=" + run + " tick=" + model.Decision.Tick + " window=" + model.Window + " units="
                    + string.Join(";", model.Decision.VisibleUnits) + " rejection=" + model.LastRejection);
                Assert.That(model.SubmittedCommands, Is.GreaterThan(0));
                Assert.That(feedback.EndFeedbackCount, Is.EqualTo(1));
                if (run == 0) CaptureView(presentation, "10-main-new-ended");
                var finalHash = Bootstrap.NewDriver.CurrentSnapshot.ComputeHash();
                for (int i = 0; i < 20; i++) Bootstrap.DriveFrameForTests(0, 1f / 60);
                Assert.That(Bootstrap.NewDriver.CurrentSnapshot.ComputeHash(), Is.EqualTo(finalHash));
                Assert.That(feedback.EndFeedbackCount, Is.EqualTo(1));
                Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(legacyBefore));
                TestContext.WriteLine("MainScene New cold run=" + run + " endedTick=" + model.Decision.BattleEnd.EndedAtTick
                    + " result=" + model.Decision.BattleEnd.ResultCode + " playerSubmissions=" + model.SubmittedCommands
                    + " oldAdvanceDelta=" + (LegacyTimelineAdvanceTimeCalls() - legacyBefore) + " endFeedback=" + feedback.EndFeedbackCount);
                if (run == 0)
                {
                    var definition = model.Decision.Definition;
                    using (var file = new MemoryStream())
                    {
                        var recorded = Bootstrap.NewDriver.RecordedReplay;
                        Assert.That(recorded.Records.Count, Is.GreaterThan(100));
                        ReplayFile.Save(file, recorded); file.Position = 0;
                        var loaded = ReplayFile.Load(file, definition);
                        using (var player = new ReplayPlayer(definition, h => ProductionBattleComposition.Create(definition, h.EncounterId, h.RuntimeInputs)))
                        {
                            player.Load(loaded);
                            for (int replay = 0; replay < 100; replay++)
                            {
                                if (replay > 0) player.Restart();
                                player.Play();
                                while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                                Assert.That(player.Deviation, Is.Null, "Production main file replay=" + replay);
                                Assert.That(player.IsComplete, Is.True);
                                Assert.That(player.CurrentSnapshot.ComputeHash(), Is.EqualTo(finalHash));
                            }
                        }
                        TestContext.WriteLine("MainScene complete file replay: 100 restarts from Tick 0; records=" + recorded.Records.Count + " bytes=" + file.Length);
                    }
                }
                Bootstrap.ReleaseBattle();
                // Loading Single destroys the previous scene and all its runtime objects before the next run.
            }
        }
        [UnityTest] public IEnumerator MainSceneNewPresentationBindsRealPlayerInputAndConfirmsOnlyOnce()
        {
            yield return LoadMainScene();
            Bootstrap.StopBattle("wired-input-prepare"); Bootstrap.ReleaseBattle();
            long legacyBefore = LegacyTimelineAdvanceTimeCalls();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            var presentation = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<BattlePresentationView>(true)).Single();
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            var model = presentation.Model;
            CaptureView(presentation, "10-main-new-start");
            var hero = model.Decision.ControlledUnitIds.Single();
            Assert.That(presentation.Registry.TryGetView(hero.Value, out var view), Is.True);
            Assert.That(view.UnitId, Is.EqualTo(hero));
            Assert.That(model.Window.TotalBudgetTicks, Is.EqualTo(180));
            var legacyHudRoots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true)).Where(t => t.name.StartsWith("HUD_", StringComparison.Ordinal))
                .Select(t => t.gameObject).ToArray();
            Assert.That(legacyHudRoots.Length, Is.EqualTo(2), "The real Legacy startup creates two resource HUDs.");
            Assert.That(legacyHudRoots.All(go => !go.activeSelf), Is.True, "New must hide old stamina/focus/adrenaline graphics.");
            var buttonType = Type.GetType("UnityEngine.UI.Button, UnityEngine.UI", true);
            var attempts = new System.Collections.Generic.List<string>();
            foreach (var attack in model.Decision.ActionSetOf(hero).Where(id => model.Decision.FindAction(id).Type == ActionType.Attack
                && model.Targets(hero, id).Count > 0))
            {
                presentation.SelectAction(attack);
                var targets = (IList)Field(presentation, "_targetButtons");
                Assert.That(targets.Count, Is.GreaterThan(0));
                Click(((GameObject)targets[0]).GetComponent(buttonType));
                attempts.Add(attack.Value + ":" + (model.Preview.RejectionCode ?? "available"));
                if (model.Preview.Succeeded) break;
                Assert.That(model.Preview.RejectionCode, Is.EqualTo("WINDOW_INSUFFICIENT_BUDGET"));
            }
            Assert.That(model.Preview?.Succeeded, Is.True, "The real 180 Tick window needs an affordable Attack: " + string.Join(",", attempts));
            Assert.That(model.SubmittedCommands, Is.Zero, "Target selection only creates a local draft.");
            Click(Field(presentation, "_confirm")); Click(Field(presentation, "_confirm"));
            Assert.That(model.SubmittedCommands, Is.EqualTo(1), "A repeated confirm cannot resubmit the consumed draft.");
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Timeline.Any(p => p.OwnerUnitId == hero.Value && p.State == (int)ActionPlanState.Running), Is.True);
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(legacyBefore));
            Bootstrap.StopBattle("wired-input-complete"); Bootstrap.ReleaseBattle();
            Assert.That(view.UnitId.IsValid, Is.False, "Release clears the preceding world's visual identity.");
            Assert.That(legacyHudRoots.All(go => go.activeSelf), Is.True, "Legacy visibility is restored on release.");
            yield return null;
        }

        [UnityTest] public IEnumerator MainSceneTimelineDragDeleteAndRevisionRebaseUseRealPlayerPort()
        {
            yield return LoadMainScene();
            Bootstrap.StopBattle("timeline-prepare"); Bootstrap.ReleaseBattle();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            var presentation = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<BattlePresentationView>(true)).Single();
            var model = presentation.Model; var hero = model.Decision.ControlledUnitIds.Single();
            var guard = model.Decision.ActionSetOf(hero).Single(id => model.Decision.FindAction(id).Type == ActionType.Guard);
            var move = model.Decision.ActionSetOf(hero).Single(id => model.Decision.FindAction(id).Type == ActionType.Move);
            var owner = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            GridPoint? destination = null;
            for (int x = -2; x <= 2 && destination == null; x++)
                for (int y = -2; y <= 2 && destination == null; y++)
                {
                    if (x == 0 && y == 0 || ((x + y) & 1) != 0) continue;
                    var candidate = new GridPoint(owner.X + x, owner.Y + y);
                    if (model.SetDraft(new ScheduleEditOperation[] {
                        new AddOrdinaryPlanOperation(1, hero, guard, 200),
                        new AddOrdinaryPlanOperation(2, hero, move, 290, Destination: candidate) }).Succeeded)
                        destination = candidate;
                }
            Assert.That(destination.HasValue, Is.True, "The real footprint needs a valid affordable future Move.");
            Assert.That(model.SubmittedCommands, Is.Zero);
            Click(Field(presentation, "_confirm")); Bootstrap.DriveFrameForTests(0, 1f / 60);
            var plans = model.Decision.OwnPlans.OrderBy(p => p.StartTick).ToArray();
            Assert.That(plans.Length, Is.EqualTo(2));
            long firstId = plans[0].ActionPlanId, secondId = plans[1].ActionPlanId;
            var row = presentation.GetComponentsInChildren<TimelinePlanView>(true).Single(r => (long)Field(r, "_id") == firstId);
            var pointer = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                { position = new Vector2(100, 100) };
            long revision = model.Decision.ScheduleRevision, submissions = model.SubmittedCommands;
            var original = Bootstrap.NewDriver.CurrentSnapshot;
            row.OnBeginDrag(pointer);
            for (int offset = 20; offset <= 440; offset += 20)
            { pointer.position = new Vector2(100 + offset, 100); row.OnDrag(pointer); }
            row.OnEndDrag(pointer);
            Assert.That(model.Preview.Succeeded, Is.True);
            Assert.That(model.SubmittedCommands, Is.EqualTo(submissions), "Every drag position remains a local preview.");
            Assert.That(Bootstrap.NewDriver.CurrentSnapshot, Is.SameAs(original));
            var preview = model.Preview.PreviewPlans.Select(p => p.Plan).ToArray();
            Assert.That(preview.Single(p => p.ActionPlanId == firstId).StartTick, Is.EqualTo(310));
            Assert.That(preview.Single(p => p.ActionPlanId == secondId).StartTick, Is.EqualTo(400), "Logic previews right ripple.");
            Click(Field(presentation, "_confirm")); Click(Field(presentation, "_confirm"));
            Assert.That(model.SubmittedCommands, Is.EqualTo(submissions + 1));
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Decision.ScheduleRevision, Is.EqualTo(revision + 1));
            Assert.That(model.Decision.OwnPlans.Single(p => p.ActionPlanId == secondId).StartTick, Is.EqualTo(400));

            pointer.button = UnityEngine.EventSystems.PointerEventData.InputButton.Right; row.OnPointerClick(pointer);
            Assert.That(model.HasDraft, Is.True); Assert.That(model.SubmittedCommands, Is.EqualTo(submissions + 1));
            Click(Field(presentation, "_confirm")); Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Decision.OwnPlans.Any(p => p.ActionPlanId == firstId), Is.False);
            Assert.That(model.Timeline.Single(p => p.ActionPlanId == firstId).State, Is.EqualTo((int)ActionPlanState.Terminated));
            Assert.That(model.Decision.OwnPlans.Single(p => p.ActionPlanId == secondId).StartTick, Is.EqualTo(400), "Delete never compacts left.");

            // An accepted transaction is still pending while the next local draft is made.
            Assert.That(model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(3, hero, guard, 500) }).Succeeded, Is.True);
            Click(Field(presentation, "_confirm"));
            model.SetDraft(new ScheduleEditOperation[] { new MoveEditablePlanOperation(new ActionPlanId(secondId), 420) });
            submissions = model.SubmittedCommands;
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.HasDraft, Is.False, "A new committed revision discards the preceding local candidate.");
            Assert.That(model.SubmittedCommands, Is.EqualTo(submissions));
            Assert.That(model.SetDraft(new ScheduleEditOperation[] { new MoveEditablePlanOperation(new ActionPlanId(secondId), 430) }).Succeeded, Is.True);
            Assert.That(model.Preview.BaseScheduleRevision, Is.EqualTo(model.Decision.ScheduleRevision));
            TestContext.WriteLine("Real main timeline: future Guard/Move, 22 drag previews, one atomic confirmation, right ripple, delete without compaction, revision rebase.");
            Bootstrap.StopBattle("timeline-complete"); Bootstrap.ReleaseBattle(); yield return null;
        }

        [UnityTest] public IEnumerator MainSceneTimelineDiscardsDraftWhenPlanStartsWithoutRevisionChange()
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("lock-prepare"); Bootstrap.ReleaseBattle();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True);
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            var presentation = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<BattlePresentationView>(true)).Single();
            var model = presentation.Model; var hero = model.Decision.ControlledUnitIds.Single();
            var guard = model.Decision.ActionSetOf(hero).Single(id => model.Decision.FindAction(id).Type == ActionType.Guard);
            model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, guard, 5) });
            Click(Field(presentation, "_confirm")); Bootstrap.DriveFrameForTests(0, 1f / 60);
            var plan = model.Decision.OwnPlans.Single(); long revision = model.Decision.ScheduleRevision;
            Assert.That(model.SetDraft(new ScheduleEditOperation[] { new MoveEditablePlanOperation(new ActionPlanId(plan.ActionPlanId), 100) }).Succeeded, Is.True);
            for (int tick = 2; tick <= 5; tick++) Bootstrap.DriveFrameForTests(0, 1f / 60);
            Assert.That(model.Decision.ScheduleRevision, Is.EqualTo(revision));
            Assert.That(model.Decision.OwnPlans.Single().State, Is.EqualTo((int)ActionPlanState.Running));
            Assert.That(model.CanEdit(plan.ActionPlanId), Is.False);
            Assert.That(model.HasDraft, Is.False, "Natural locking invalidates the local draft even with the same revision.");
            Assert.That(model.LastRejection, Is.EqualTo("PLAN_NOT_EDITABLE"));
            Assert.That(model.SubmittedCommands, Is.EqualTo(1));
            Bootstrap.StopBattle("lock-complete"); Bootstrap.ReleaseBattle(); yield return null;
        }

        [UnityTest] public IEnumerator MainSceneBlockButtonUsesRealThreatAndDamageEarnedAdrenaline()
        { yield return MainSceneReaction(ActionType.Block); }

        [UnityTest] public IEnumerator MainSceneDodgeSelectionIsLocalUntilConfirmAndUsesRealThreat()
        { yield return MainSceneReaction(ActionType.Dodge); }

        [UnityTest] public IEnumerator MainCanvasDodgeCancelsFutureMoveChainAndReleasesClosedOriginalWindow()
        { yield return MainSceneReaction(ActionType.Dodge, true); }

        [UnityTest] public IEnumerator MainCanvasPlayerAttackFundsAiReactionAndFeedbackThroughProductionPorts()
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("ai-reaction-prepare"); Bootstrap.ReleaseBattle();
            var original = ((IBattleSimulationSource)Field(Bootstrap, "_simulationSourceSlot")).BuildSeed();
            var source = NewGameObject("Task11AiReactionScenario").AddComponent<Task11SceneScenarioSource>();
            source.Seed = Task11SceneScenarioSource.Configure(original, true);
            TestContext.WriteLine(source.Seed.InputSummary);
            Bootstrap.GetType().GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Bootstrap, source);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            var presentation = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<BattlePresentationView>(true)).Single();
            var model = presentation.Model; var hero = model.Decision.ControlledUnitIds.Single();
            var enemy = new UnitId(model.Decision.VisibleUnits.Single(u => u.UnitId != hero.Value).UnitId);
            var facts = new System.Collections.Generic.List<LogicEvent>();
            presentation.Registry.Feedback.FeedbackRequested += cue => facts.Add(cue.Fact);
            Assert.That(model.Decision.VisibleUnits.Single(u => u.UnitId == enemy.Value).AvailableAdrenaline, Is.Zero);
            long oldAdvance = LegacyTimelineAdvanceTimeCalls();
            for (int tick = 0; tick < 6000 && !model.Decision.BattleEnd.IsEnded
                && !facts.OfType<ReactionTriggeredEvent>().Any(e => e.DefenderUnitId == enemy.Value); tick++)
            {
                if (model.Window?.OwnerUnitId == hero.Value && !model.Decision.OwnPlans.Any())
                {
                    if (DraftCombatAction(model)) { Click(Field(presentation, "_confirm")); }
                    else Assert.That(model.CloseWindow(), Is.Null);
                }
                Bootstrap.DriveFrameForTests(0, 1f / 60);
            }
            Assert.That(facts.OfType<ReactionTriggeredEvent>().Any(e => e.DefenderUnitId == enemy.Value), Is.True,
                "AI reaction required; tick=" + model.Decision.Tick + "; submissions=" + model.SubmittedCommands
                + "; units=" + string.Join(";", model.Decision.VisibleUnits) + "; damage=" + string.Join(";", facts.OfType<DamageChannelResolvedEvent>().TakeLast(3)));
            var triggered = facts.OfType<ReactionTriggeredEvent>().Single(e => e.DefenderUnitId == enemy.Value);
            Assert.That(facts.OfType<DamageChannelResolvedEvent>().Any(e => e.TargetUnitId == enemy && e.Tick < triggered.Tick), Is.True);
            Assert.That(facts.Any(e => e is DodgeResolvedEvent || e is BlockResolvedEvent), Is.True);
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(oldAdvance));
            var replay = Bootstrap.NewDriver.RecordedReplay;
            Assert.That(replay.Submissions.All(s => s.Fact.SourceKind == CommandSourceKind.Player), Is.True, "AI is rebuilt by production composition, never injected from a recording.");
            using (var file = new MemoryStream())
            {
                ReplayFile.Save(file, replay);
                File.WriteAllBytes(Path.GetFullPath("优化任务/执行记录/11-scene-ai-reaction.heroReplay"), file.ToArray());
                file.Position = 0; var loaded = ReplayFile.Load(file, source.Seed.Definition);
                using var player = new ReplayPlayer(source.Seed.Definition,
                    h => ProductionBattleComposition.Create(source.Seed.Definition, h.EncounterId, h.RuntimeInputs)); player.Load(loaded);
                for (int run = 0; run < 100; run++)
                {
                    if (run > 0) player.Restart(); player.Play();
                    while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                    Assert.That(player.Deviation, Is.Null, "AI scene replay run=" + run); Assert.That(player.IsComplete, Is.True);
                }
                TestContext.WriteLine("Actual Canvas / AI reaction fixture: damage-funded, one feedback trigger, 100 Tick-0 file restarts; ticks=" + replay.Records.Count + "; bytes=" + file.Length + "; definition=" + source.Seed.BattleDefinitionHash);
            }
            Bootstrap.StopBattle("ai-reaction-complete"); Bootstrap.ReleaseBattle(); yield return null;
        }

        private IEnumerator MainSceneReaction(ActionType kind, bool futureChain = false)
        {
            yield return LoadMainScene(); Bootstrap.StopBattle("reaction-prepare"); Bootstrap.ReleaseBattle();
            if (futureChain)
            {
                var original = ((IBattleSimulationSource)Field(Bootstrap, "_simulationSourceSlot")).BuildSeed();
                var configured = Task11SceneScenarioSource.Configure(original, false);
                var source = NewGameObject("Task11MinimumMomentumScenario").AddComponent<Task11SceneScenarioSource>();
                source.Seed = configured;
                Bootstrap.GetType().GetField("_simulationSourceSlot", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Bootstrap, source);
                TestContext.WriteLine("Canvas combination fixture definition=" + configured.Definition.BattleDefinitionHashValue + "; uniform per-direction pattern union; forceMultiplier=0.000001; attack timing=60/30; reaction timing=1/30; unchanged grid/body/180 Tick/hero; not the unchanged formal encounter.");
            }
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True, Bootstrap.StartupRejection);
            Bootstrap.DriveFrameForTests(0, 1f / 60);
            var presentation = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<BattlePresentationView>(true)).Single();
            var model = presentation.Model; var hero = model.Decision.ControlledUnitIds.Single();
            if (futureChain) Assert.That(model.Decision.Definition.Actions.Where(a => a.Type == ActionType.Attack)
                .All(a => ((AttackPayloadSpec)a.Payload).ForceMultiplier == 0.000001f), Is.True, "The explicit fixture definition must reach the actual Canvas/driver.");
            var owner = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            var enemy = model.Decision.VisibleUnits.Single(u => u.UnitId != hero.Value);
            var feedbackFacts = new System.Collections.Generic.List<LogicEvent>();
            presentation.Registry.Feedback.FeedbackRequested += cue => feedbackFacts.Add(cue.Fact);
            var move = model.Decision.ActionSetOf(hero).Single(id => model.Decision.FindAction(id).Type == ActionType.Move);
            var attacks = model.Decision.Definition.Actions.Where(s => s.Type == ActionType.Attack
                && model.Decision.ActionSetOf(new UnitId(enemy.UnitId)).Contains(s.ActionSpecId)).ToArray();
            var candidates = new System.Collections.Generic.List<(GridPoint Point, int Contacts)>();
            // Choose a legal destination in the production AI's published East-facing patterns.
            // Only the real Player Move command changes position; damage and resources are never injected.
            for (int x = -5; x <= 5; x++) for (int y = -5; y <= 5; y++)
            {
                if (((enemy.X + x + enemy.Y + y) & 1) != 0) continue;
                var point = new GridPoint(enemy.X + x, enemy.Y + y);
                var target = owner with { X = point.X, Y = point.Y };
                int count = attacks.Count(s => Contact(model.Decision, enemy, target, (AttackPayloadSpec)s.Payload,
                    GridDirection.East, new GridPoint(enemy.X, enemy.Y)));
                if (count > 0) candidates.Add((point, count));
            }
            GridPoint goal = default; bool validGoal = false;
            foreach (var candidate in candidates.OrderByDescending(c => c.Contacts)
                .ThenBy(c => Math.Abs(c.Point.X - owner.X) + Math.Abs(c.Point.Y - owner.Y)).ThenBy(c => c.Point.X).ThenBy(c => c.Point.Y))
            {
                var preview = model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, move, Destination: candidate.Point) });
                if (!preview.Succeeded && preview.RejectionCode != "WINDOW_INSUFFICIENT_BUDGET") continue;
                goal = candidate.Point; validGoal = true; break;
            }
            Assert.That(validGoal, Is.True, "A threat contact origin must also accommodate the full hero footprint.");
            int Distance(GridPoint point) => Math.Abs(point.X - goal.X) + Math.Abs(point.Y - goal.Y);
            int frames = 0;
            for (int leg = 0; leg < 8; leg++)
            {
                owner = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
                var origin = new GridPoint(owner.X, owner.Y); if (origin == goal) break;
                while (model.Window?.OwnerUnitId != hero.Value && frames++ < 3000) Bootstrap.DriveFrameForTests(0, 1f / 60);
                bool drafted = false;
                foreach (var point in model.Decision.Definition.FindEncounter(Bootstrap.NewDriver.ReplayHeader.EncounterId)
                    .GridBoundary.EnumerateValidPoints().Where(p => Distance(p) < Distance(origin))
                    .OrderBy(Distance).ThenBy(p => p.X).ThenBy(p => p.Y))
                    if (model.SetDraft(new ScheduleEditOperation[] { new AddOrdinaryPlanOperation(1, hero, move, Destination: point) }).Succeeded)
                    { drafted = true; break; }
                Assert.That(drafted, Is.True, "A real budget-limited Move must approach the production threat region: leg=" + leg
                    + " origin=" + origin + " goal=" + goal + " window=" + model.Window + " rejection=" + model.LastRejection);
                Click(Field(presentation, "_confirm")); Bootstrap.DriveFrameForTests(0, 1f / 60);
                var movement = model.Decision.OwnPlans.Single(p => p.ActionSpecId == move.Value);
                while (model.Decision.OwnPlans.Any(p => p.ActionPlanId == movement.ActionPlanId) && frames++ < 5000)
                    Bootstrap.DriveFrameForTests(0, 1f / 60);
                Assert.That(model.Timeline.Single(p => p.ActionPlanId == movement.ActionPlanId).State, Is.EqualTo((int)ActionPlanState.Completed));
                owner = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
                if (new GridPoint(owner.X, owner.Y) == goal) break;
                if (model.Window?.OwnerUnitId == hero.Value) Assert.That(model.CloseWindow(), Is.Null);
                Bootstrap.DriveFrameForTests(0, 1f / 60);
            }
            owner = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
            Assert.That(new GridPoint(owner.X, owner.Y), Is.EqualTo(goal), "Every leg uses the formal 180 Tick window.");
            Assert.That(model.CloseWindow(), Is.Null);
            long handledWindow = 0, acceptedOpportunity = 0; bool concurrent = false;
            long chainWindow = 0, chainBudget = 0, guardId = 0, guardStart = 0;
            long[] chainIds = Array.Empty<long>();
            void PlaceChain()
            {
                var current = model.Decision.VisibleUnits.Single(u => u.UnitId == hero.Value);
                var guard = model.Decision.ActionSetOf(hero).Single(id => model.Decision.FindAction(id).Type == ActionType.Guard);
                bool placed = false; long start = model.Decision.Tick + 800;
                foreach (var delta in new[] { new GridPoint(2, 0), new GridPoint(-2, 0), new GridPoint(0, 2), new GridPoint(0, -2) })
                {
                    var first = new GridPoint(current.X + delta.X, current.Y + delta.Y);
                    var second = new GridPoint(current.X + 2 * delta.X, current.Y + 2 * delta.Y);
                    if (!model.SetDraft(new ScheduleEditOperation[] {
                        new AddOrdinaryPlanOperation(101, hero, move, start, Destination: first),
                        new AddOrdinaryPlanOperation(102, hero, guard, start + 100),
                        new AddOrdinaryPlanOperation(103, hero, move, start + 250, Destination: second) }).Succeeded) continue;
                    placed = true; break;
                }
                Assert.That(placed, Is.True, "Real Move / Guard / Move chain: " + model.LastRejection);
                chainWindow = model.Window.WindowId;
                Click(Field(presentation, "_confirm")); Bootstrap.DriveFrameForTests(0, 1f / 60);
                var added = model.Decision.OwnPlans.Where(p => p.StartTick >= start).ToArray();
                chainIds = added.Where(p => p.ActionSpecId == move.Value).Select(p => p.ActionPlanId).OrderBy(id => id).ToArray();
                Assert.That(chainIds.Length, Is.EqualTo(2));
                var heldGuard = added.Single(p => p.ActionSpecId == guard.Value); guardId = heldGuard.ActionPlanId; guardStart = heldGuard.StartTick;
                chainBudget = added.Where(p => chainIds.Contains(p.ActionPlanId)).Sum(p => p.ReservedTurnBudgetTicks);
                Assert.That(chainBudget, Is.GreaterThan(0));
            }
            var buttonType = Type.GetType("UnityEngine.UI.Button, UnityEngine.UI", true);
            var textType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro", true);
            for (frames = 0; frames < 3000 && acceptedOpportunity == 0 && !model.Decision.BattleEnd.IsEnded; frames++)
            {
                var window = model.Window;
                if (window != null && window.OwnerUnitId != hero.Value && !concurrent)
                {
                    Click(Field(presentation, "_concurrent"));
                    Bootstrap.DriveFrameForTests(0, 1f / 60);
                    concurrent = Bootstrap.NewDriver.CurrentSnapshot.ConcurrentAction.HasActiveAuthorization;
                }
                // Keep a hero window open until a real incoming hit has earned the reaction cost;
                // closing it preserves the personal-cycle resource through the following AI window.
                if (model.Adrenaline.Single().AvailableAdrenaline >= (futureChain ? 1 : 2) && model.Window?.OwnerUnitId == hero.Value
                    && model.Window.WindowId != handledWindow)
                {
                    if (futureChain && chainWindow == 0) PlaceChain();
                    handledWindow = model.Window.WindowId; Assert.That(model.CloseWindow(), Is.Null);
                    Bootstrap.DriveFrameForTests(0, 1f / 60);
                }
                var buttons = ((IList)Field(presentation, "_reactionButtons")).Cast<GameObject>().ToArray();
                var label = kind.ToString();
                foreach (var go in buttons)
                {
                    var textComponent = go.GetComponentInChildren(textType);
                    var text = (string)textType.GetProperty("text").GetValue(textComponent);
                    if (!text.StartsWith(label + " ", StringComparison.Ordinal)) continue;
                    if (futureChain && model.Adrenaline.Single().AvailableAdrenaline < model.Decision.ActionSetOf(hero)
                        .Select(model.Decision.FindAction).Single(s => s.Type == kind).AdrenalineCost) continue;
                    long before = model.SubmittedCommands;
                    Click(go.GetComponent(buttonType));
                    if (kind == ActionType.Dodge)
                    {
                        Assert.That(model.SubmittedCommands, Is.EqualTo(before), "Dodge destination selection only previews.");
                        var preview = Field(presentation, "_selectedDodgePreview") as ProjectHero.Logic.Movement.DodgeCancellationPreview;
                        if (preview?.RejectionCode != null) continue;
                        if (futureChain)
                        {
                            Assert.That(chainWindow, Is.GreaterThan(0), "The chain must be created by a preceding real hero window.");
                            Assert.That(preview.AffectedPlanIds.Select(id => id.Value), Is.EqualTo(chainIds));
                            var release = preview.ReleasesByWindow.Single(r => r.WindowId.Value == chainWindow);
                            Assert.That(release.BudgetTicks, Is.EqualTo(chainBudget)); Assert.That(release.WindowIsOpen, Is.False);
                            var labelComponent = Field(presentation, "_previewText");
                            Assert.That((string)textType.GetProperty("text").GetValue(labelComponent), Does.Contain("historical ticks released (closed turn)"));
                            Assert.That(chainIds.All(id => model.Decision.OwnPlans.Any(p => p.ActionPlanId == id)), Is.True, "Preview preserves both future Moves.");
                        }
                        Click(Field(presentation, "_confirm")); Click(Field(presentation, "_confirm"));
                    }
                    if (model.SubmittedCommands != before + 1) continue;
                    Bootstrap.DriveFrameForTests(0, 1f / 60);
                    // The accepted plan is visible through the real controlled-unit projection.
                    var reactionPlan = model.Decision.OwnPlans.FirstOrDefault(p => p.Origin == (int)ActionPlanOrigin.Reaction
                        && model.Decision.FindAction(new ActionSpecId(p.ActionSpecId)).Type == kind);
                    if (reactionPlan != null) { acceptedOpportunity = reactionPlan.ReactionOpportunityId; break; }
                }
                if (frames % 120 == 0) yield return null;
                Bootstrap.DriveFrameForTests(0, 1f / 60);
            }
            Assert.That(acceptedOpportunity, Is.GreaterThan(0), "Real reaction was not accepted: " + kind + " tick=" + model.Decision.Tick
                + " resources=" + string.Join(";", model.Adrenaline) + " rejection=" + model.LastRejection
                + " units=" + string.Join(";", model.Decision.VisibleUnits) + " window=" + model.Window
                + " plans=" + string.Join(";", model.Decision.VisiblePlans)
                + " damage=" + string.Join(";", Bootstrap.NewDriver.RecordedReplay.Records.SelectMany(t => t.Events).OfType<DamageChannelResolvedEvent>())
                + " selected=" + string.Join(";", candidates.OrderByDescending(c => c.Contacts).Take(4)));
            long trigger = model.Reactions.Single(o => o.ReactionOpportunityId == acceptedOpportunity).TriggerTick;
            while (model.Decision.Tick <= trigger + 35 && !model.Decision.BattleEnd.IsEnded) Bootstrap.DriveFrameForTests(0, 1f / 60);
            var replay = Bootstrap.NewDriver.RecordedReplay;
            var facts = feedbackFacts.ToArray();
            Assert.That(facts.OfType<ReactionTriggeredEvent>().Count(e => e.ReactionOpportunityId == acceptedOpportunity), Is.EqualTo(1),
                "opportunity=" + acceptedOpportunity + " trigger=" + trigger + " concurrent=" + concurrent + " facts="
                + string.Join(";", facts.Where(e => e.Tick >= trigger - 60)));
            Assert.That(facts.OfType<DamageChannelResolvedEvent>().Any(e => e.TargetUnitId == hero), Is.True, "Resource came from real damage.");
            Assert.That(concurrent, Is.True, "The real main-scene Concurrent button must activate hero authorization in an AI window.");
            if (futureChain)
            {
                var terminated = model.Timeline.Where(p => chainIds.Contains(p.ActionPlanId)).OrderBy(p => p.ActionPlanId).ToArray();
                Assert.That(terminated.Select(e => e.ActionPlanId), Is.EqualTo(chainIds));
                Assert.That(terminated.All(e => e.State == (int)ActionPlanState.Terminated
                    && e.TerminationReason == (int)ActionTerminationReason.MovementOriginInvalidatedByDodge && e.TerminalTick == trigger), Is.True);
                foreach (var id in chainIds)
                    Assert.That(replay.Records.SelectMany(r => r.Events).Select(ReplayEventComparison.Canonical).Count(text =>
                        text.StartsWith(typeof(ActionPlanTerminatedEvent).FullName + "{", StringComparison.Ordinal)
                        && text.Contains("ActionPlanId=" + id + ";")), Is.EqualTo(1), "One authoritative terminal event per cancelled Move.");
                Assert.That(model.Decision.OwnPlans.Single(p => p.ActionPlanId == guardId).StartTick, Is.EqualTo(guardStart));
                var snapshot = Bootstrap.NewDriver.CurrentSnapshot;
                Assert.That(snapshot.MovementSegments.Any(s => chainIds.Contains(s.ActionPlanId)), Is.False);
                Assert.That(snapshot.Reservations.Any(s => chainIds.Contains(s.ActionPlanId)), Is.False);
                Assert.That(snapshot.WindowManager.Windows.Single(w => w.WindowId == chainWindow).IsOpen, Is.False);
                Assert.That(snapshot.WindowManager.Windows.Single(w => w.WindowId == chainWindow).ReservedBudgetTicks,
                    Is.EqualTo(model.Decision.OwnPlans.Single(p => p.ActionPlanId == guardId).ReservedTurnBudgetTicks));
                TestContext.WriteLine("Closed original window=" + chainWindow + "; released=" + chainBudget + "; invalidated=" + string.Join(",", chainIds) + "; unrelated Guard preserved.");
            }
            if (kind == ActionType.Block)
                Assert.That(facts.OfType<BlockResolvedEvent>().Any(e => e.DefenderUnitId == hero
                    && e.AfterBlockDamageQ10 == 0 && e.AfterBlockMomentumUnits == 0), Is.True);
            using (var file = new MemoryStream())
            {
                ReplayFile.Save(file, replay); file.Position = 0;
                if (futureChain) File.WriteAllBytes(Path.GetFullPath("优化任务/执行记录/11-scene-dodge-chain.heroReplay"), file.ToArray());
                var loaded = ReplayFile.Load(file, model.Decision.Definition);
                using (var player = new ReplayPlayer(model.Decision.Definition,
                    h => ProductionBattleComposition.Create(model.Decision.Definition, h.EncounterId, h.RuntimeInputs)))
                {
                    player.Load(loaded);
                    for (int run = 0; run < 100; run++)
                    {
                        if (run != 0) player.Restart(); player.Play();
                        while (!player.IsComplete && player.Deviation == null) player.AdvanceOneTick();
                        Assert.That(player.Deviation, Is.Null, kind + " replay run=" + run + ":" + player.Deviation);
                        Assert.That(player.IsComplete, Is.True);
                    }
                }
                TestContext.WriteLine((futureChain ? "Canvas fixture " : "Actual main ") + kind + " file replay restarts=100; ticks=" + replay.Records.Count + "; bytes=" + file.Length);
            }
            TestContext.WriteLine((futureChain ? "Canvas fixture " : "Real main ") + kind + ": damage-earned resource, published opportunity, Player UI input, one trigger, hero concurrent authorization; tick=" + model.Decision.Tick);
            Bootstrap.StopBattle("reaction-complete"); Bootstrap.ReleaseBattle(); yield return null;
        }
    }
}
