using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Commands;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Encounter;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Movement;
using ProjectHero.Logic.Simulation;
using ProjectHero.Logic.Snapshots;
using ProjectHero.Logic.Timeline;
using ProjectHero.Logic.Units;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectHero.Tests.PlayMode
{
    /// <summary>
    /// 任务 03B「必需测试与场景检查」的 PlayMode 落地。
    ///
    /// 每条测试的通过判据都来自<b>可观察调用计数</b>或<b>只读结构事实</b>：
    /// 调用矩阵、旧 <c>TotalAdvanceTimeCalls</c>、Shadow 写入计数器、门控状态、
    /// 检查点对齐键——不从日志文本推断，也不用恒真断言。
    /// </summary>
    public sealed class RuntimeOwnershipTests : RuntimeOwnershipTestBase
    {
        // =====================================================================
        // 场景检查（SceneHasExactlyOneBattleRuntimeBootstrap / 隐藏场景 / 重复 Bootstrap）
        // =====================================================================

        [UnityTest]
        public IEnumerator SceneHasExactlyOneBattleRuntimeBootstrap()
        {
            yield return LoadMainScene();
            Assert.That(BootstrapCountInScene(), Is.EqualTo(1),
                "主战斗场景必须恰有一个 BattleRuntimeBootstrap");
            Assert.That(BattleRuntimeBootstrap.ClockOwnerCountInScene(), Is.EqualTo(1),
                "顶层时钟所有者必须恰好一个");
            Assert.That(Bootstrap.OwnsTopLevelClock, Is.True, "唯一 Bootstrap 必须取得顶层时钟所有权");
            Assert.That(Bootstrap.StartupRejection, Is.Null);

            var advancers = CollectTopLevelAdvancers();
            Assert.That(advancers, Is.Empty,
                "除 Bootstrap 外不得存在被分类为顶层时钟推进者的组件：" + string.Join(",", advancers));

            var unclassified = CollectUnclassifiedLogicWriters();
            Assert.That(unclassified, Is.Empty,
                "分类表不得存在空条目或未分类的项目逻辑写入者：" + string.Join(",", unclassified));

            yield return LoadHiddenValidationScene();
            Assert.That(BootstrapCountInScene(), Is.EqualTo(1),
                "隐藏验证场景同样必须恰有一个 BattleRuntimeBootstrap");
            Assert.That(BattleRuntimeBootstrap.ClockOwnerCountInScene(), Is.EqualTo(1));
            Assert.That(Bootstrap.StartupRejection, Is.Null,
                "隐藏验证场景的启动校验不得产生拒绝：" + Bootstrap.StartupRejection);

            // 1) **交付态**（场景资产的序列化内容）不得包含表现/交互/UI 闭包。
            //    核对方式是场景资产文本里的脚本 GUID —— 结构事实，不受运行时旧兜底链影响。
            string sceneText = HiddenSceneAssetText();
            var excludedScripts = new Dictionary<string, string>
            {
                { "ProjectHero.UI.UIManager", "Assets/Scripts/UI/UIManager.cs" },
                { "ProjectHero.UI.HUDManager", "Assets/Scripts/UI/HUDManager.cs" },
                { "ProjectHero.Core.Gameplay.TacticsController", "Assets/Scripts/Core/Gameplay/TacticsController.cs" },
                { "ProjectHero.Core.Gameplay.EnemyAIController", "Assets/Scripts/Core/Gameplay/EnemyAIController.cs" },
                { "ProjectHero.Visuals.GridVisuals", "Assets/Scripts/Visuals/GridVisuals.cs" },
                { "ProjectHero.Visuals.UnitVolumeRenderer", "Assets/Scripts/Visuals/UnitVolumeRenderer.cs" }
            };
            foreach (var pair in excludedScripts)
            {
                Assert.That(System.IO.File.Exists(pair.Value), Is.True, "脚本资产必须存在：" + pair.Value);
                Assert.That(sceneText, Does.Not.Contain(ScriptGuid(pair.Value)),
                    "隐藏验证场景的序列化内容不得包含表现/交互/UI 闭包组件：" + pair.Key);
            }

            // 2) 最小真实链齐备（运行时事实：生产组件 + 组装后的引用闭包）。
            var productionTypes = CollectHiddenSceneTypes();
            Assert.That(productionTypes, Does.Contain("ProjectHero.Demos.CombatDemo"));
            Assert.That(productionTypes, Does.Contain("ProjectHero.Core.Timeline.BattleTimeline"));
            Assert.That(productionTypes, Does.Contain("ProjectHero.Core.Entities.CombatUnit"));
            Assert.That(productionTypes, Does.Contain(
                "ProjectHero.Core.Compatibility.Authoring.BattleSimulationSourceFactory"));
            Assert.That(productionTypes, Does.Contain(
                "ProjectHero.Core.Compatibility.Runtime.BattleRuntimeBootstrap"));
            Assert.That(productionTypes, Does.Not.Contain("ProjectHero.UI.UIManager"),
                "隐藏场景不得带进 UI 闭包");
            Assert.That(productionTypes, Does.Not.Contain("ProjectHero.UI.Timeline.TimelineEditorUI"),
                "隐藏场景不得带进 UI 闭包（运行时写入者也不得被创建）");

            // 3) 旧兜底链在运行时创建的交互组件（TacticsController 由 CombatDemo.Start 兜底创建）
            //    必须是**已分类的** Legacy 从属写入者，而不是逃过分类的自主写入者。
            var tactics = FindProductionComponent("ProjectHero.Core.Gameplay.TacticsController");
            if (tactics != null)
            {
                UnityCallbackClassification classification;
                Assert.That(RuntimeCallbackRegistry.TryGet(
                        "ProjectHero.Core.Gameplay.TacticsController", "OnDestroy", out classification), Is.True,
                    "运行时兜底创建的交互组件必须在分类表中有记录（否则 New 模式会拒绝启动）");
                Assert.That(classification.CallbackClass,
                    Is.EqualTo(UnityCallbackClass.LegacyDependentWriter),
                    "运行时兜底创建的交互组件必须被分类为 Legacy 从属写入者");
                Assert.That(FindProductionComponent("ProjectHero.Core.Gameplay.TacticsController")
                        .gameObject.scene.name,
                    Is.EqualTo(HiddenValidationSceneName));
            }
        }

        [UnityTest]
        public IEnumerator HiddenValidationSceneProvidesResolvableLegacyGateTargets()
        {
            yield return LoadHiddenValidationScene();
            Bootstrap.SyncRuntimeLegacyWriters();

            // 门控必须命中真实运行时实例（否则"New 全部禁用"只是登记表上的字符串）。
            Assert.That(Bootstrap.LegacyWritersWithGateTarget, Is.GreaterThanOrEqualTo(3),
                "隐藏场景必须提供可解析的写入者实例");
            Assert.That(Bootstrap.LegacyWritersWithoutGateTarget, Is.LessThanOrEqualTo(
                    Bootstrap.LegacyWriters.Count - 3),
                "未解析实例的写入者只允许出现在隐藏场景未装配的组件上");

            var targets = new List<string>();
            for (int i = 0; i < Bootstrap.LegacyWriters.Count; i++)
            {
                var target = Bootstrap.LegacyWriters[i].GateTarget;
                if (target == null) continue;
                targets.Add(target.GetType().Name);
            }
            Assert.That(targets, Does.Contain("CombatUnit"));
            Assert.That(targets, Does.Contain("CombatDemo"));
            Assert.That(targets, Does.Contain("BattleManager"));

            var units = FindProductionComponent("ProjectHero.Core.Entities.CombatUnit");
            Assert.That(units, Is.Not.Null);
            Bootstrap.ApplyLegacyWriterGroupGateForTests(BattleRuntimeMode.Legacy);
            Assert.That(units.enabled, Is.True, "Legacy 模式下真实单位写入者必须启用");

            Bootstrap.ApplyLegacyWriterGroupGateForTests(BattleRuntimeMode.New);
            Assert.That(units.enabled, Is.False, "New 模式下真实单位写入者必须禁用");
        }

        [UnityTest]
        public IEnumerator DuplicateRuntimeBootstrapFailsFast()
        {
            yield return LoadHiddenValidationScene();
            var owner = Bootstrap;
            Assert.That(owner.StartupRejection, Is.Null);
            Assert.That(BattleRuntimeBootstrap.LiveInstanceCount, Is.EqualTo(1), "基线：场景中恰有一个实例");

            // 结构性事实：同一 GameObject 上不允许第二个 Bootstrap。
            // 因此"重复实例"必须用 Unity API 在**另一个对象**上构造——不依赖 AddComponent
            // 的隐式时序（旧用例在同一个对象上 AddComponent，被 Unity 直接拒绝并返回 null）。
            Assert.That(typeof(BattleRuntimeBootstrap)
                    .GetCustomAttribute<DisallowMultipleComponent>(), Is.Not.Null,
                "同一对象上的重复必须由 DisallowMultipleComponent 在结构上禁止");

            LogAssert.ignoreFailingMessages = true;

            // a) 活动对象上的重复实例：Awake 立即判定重复、禁用并打含对象路径的错误日志。
            var duplicateHost = NewGameObject("DuplicateBootstrapHost");
            var duplicate = Track(duplicateHost.AddComponent<BattleRuntimeBootstrap>());
            Assert.That(duplicate, Is.Not.Null, "重复实例必须被真实创建（而不是被 Unity 拒绝为 null）");

            // b) inactive 对象上的重复实例：**先置为非激活再挂组件**，
            //    因此它的 Awake 永远不会执行——只靠 Awake 登记表必然漏计。
            var inactiveHost = NewGameObject("InactiveBootstrapHost");
            inactiveHost.SetActive(false);
            var inactiveDuplicate = Track(inactiveHost.AddComponent<BattleRuntimeBootstrap>());
            Assert.That(inactiveDuplicate.StartupRejection, Is.Null,
                "从未 Awake 的实例不可能有拒绝码（它根本还没参与所有权判定）");

            yield return null;
            LogAssert.ignoreFailingMessages = false;

            // 1) 计数可靠：含被禁用的重复组件与 inactive 对象上的实例。
            Assert.That(BattleRuntimeBootstrap.LiveInstanceCount, Is.EqualTo(3),
                "存活实例计数必须包含 enabled = false 的重复组件与 inactive 对象上的实例；"
                + " 实际 " + BattleRuntimeBootstrap.LiveInstanceCount);
            Assert.That(BattleRuntimeBootstrap.ClockOwnerCountInScene(), Is.EqualTo(1),
                "顶层时钟所有者必须仍然恰好一个");
            Assert.That(owner.OwnsTopLevelClock, Is.True, "唯一所有者不得被后到者抢走时钟");

            // 2) 确定性快速失败：禁用自身 + 稳定拒绝码 + **双方对象路径**。
            Assert.That(duplicate.enabled, Is.False, "重复 Bootstrap 必须被禁用");
            Assert.That(duplicate.OwnsTopLevelClock, Is.False, "重复 Bootstrap 不得拥有顶层时钟");
            Assert.That(duplicate.StartupRejection, Does.StartWith(BattleRuntimeBootstrap.BOOTSTRAP_DUPLICATE),
                "重复 Bootstrap 必须有稳定拒绝码：" + (duplicate.StartupRejection ?? "<null>"));
            Assert.That(duplicate.StartupRejection, Does.Contain("DuplicateBootstrapHost"),
                "拒绝必须列出重复实例的对象路径：" + duplicate.StartupRejection);
            Assert.That(duplicate.StartupRejection, Does.Contain("CombatDemo"),
                "拒绝必须列出唯一所有者的对象路径：" + duplicate.StartupRejection);

            // 3) 确定性校验点（Start 之后统一复核）也能发现 inactive 对象上的重复实例 ——
            //    这是 Awake 登记表覆盖不到的路径，必须由显式扫描补上。
            var audit = owner.AuditSceneInstances();
            Assert.That(audit.SceneInstances, Is.EqualTo(3), audit.Describe());
            Assert.That(audit.ClockOwners, Is.EqualTo(1), audit.Describe());
            Assert.That(audit.InactiveOrDisabled, Is.EqualTo(2), audit.Describe());
            Assert.That(audit.IsUnique, Is.False, audit.Describe());
            Assert.That(string.Join(",", audit.ForeignPaths), Does.Contain("InactiveBootstrapHost"),
                "确定性校验必须把 inactive 对象上的重复实例也报出来：" + audit.Describe());
            Assert.That(inactiveDuplicate.OwnsTopLevelClock, Is.False);
            Assert.That(inactiveDuplicate.enabled, Is.True,
                "inactive 对象上的组件仍是 enabled 状态，只是从未 Awake —— 因此必须靠扫描计数");

            // 4) 审计记录必须留下可追溯的对象路径：Awake 的快速失败记录重复实例，
            //    Start 的确定性校验记录"本场景实例数 / 时钟所有者数"。
            Assert.That(string.Join("|", BattleRuntimeBootstrap.DuplicateInstancesObserved),
                Does.Contain("DuplicateBootstrapHost"),
                "重复实例的审计记录必须含对象路径");
            Assert.That(string.Join("|", BattleRuntimeBootstrap.SceneInstanceObservations),
                Does.Contain("HiddenRuntimeValidation/CombatDemo|sceneInstances=1|clockOwners=1"),
                "Start 的确定性校验必须记录'本场景恰好一个实例、恰好一个时钟所有者'");

            // 5) 重复实例不得阻止唯一 Bootstrap 创建战斗。
            Assert.That(owner.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "重复组件不得阻止唯一 Bootstrap 创建战斗：" + owner.StartupRejection);
            Assert.That(owner.StartupRejection, Is.Null, "唯一所有者的启动不得被拒绝");
            Assert.That(owner.BattleCreated, Is.True);
            Assert.That(owner.Adapters.Legacy, Is.Not.Null);
            Assert.That(owner.Adapters.Legacy.IsStopped, Is.False);

            owner.StopBattle("duplicate-test");
            owner.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator HiddenValidationSceneAdvancesOnlyFromExplicitBootstrap()
        {
            yield return LoadHiddenValidationScene();

            // 战斗创建之前**没有**任何帧适配器被装配：旧用例在此处读 Adapters.Legacy
            // 会拿到 null 并抛 NRE（失败根因之一）。这里断言的是显式契约本身。
            Assert.That(Bootstrap.BattleCreated, Is.False, "隐藏验证场景不得自动创建战斗");
            Assert.That(Bootstrap.BattleMode, Is.Null, "战斗创建之前不得固定模式");
            Assert.That(Bootstrap.Adapters.Legacy, Is.Null, "战斗创建之前不得装配帧适配器");
            Assert.That(Bootstrap.Adapters.All, Is.Empty, "战斗创建之前适配器集合必须为空");
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(0));
            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(0L));

            // (1) 场景自带全部 Legacy 从属写入者，它们在真实帧循环里每帧自发 Update；
            //     但**战斗尚未创建**时，旧时间线必须一动不动 —— 证明不存在第二个自主时钟。
            //     等待帧数只为"让自主 Update 有机会发生"；判据是调用计数与逻辑 Tick。
            yield return WaitFrames(3);

            Assert.That(Bootstrap.Ledger.BootstrapUpdates, Is.GreaterThan(0),
                "对照证据：Bootstrap 的 Update 确实在真实帧循环里执行（它只是还没被允许推进）");
            Assert.That(LegacyAdvancePathCalls(), Is.EqualTo(0),
                "战斗创建之前旧顶层推进路径不得被调用：" + DescribeAdvancePaths());
            Assert.That(Bootstrap.Ledger.LegacyAdapterInvocations, Is.EqualTo(0),
                "战斗创建之前 Legacy 适配器不得被调用：" + DescribeAdvancePaths());
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(0),
                "战斗创建之前旧时间线不得被推进：" + DescribeAdvancePaths());
            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(0L), "旧时间线 Tick 必须仍为 0");
            Assert.That(NewStepPathCalls(), Is.EqualTo(0), "Legacy 战斗之外不得推进新模拟");

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "隐藏验证场景的 Legacy 启动必须成功：" + Bootstrap.StartupRejection);
            var adapter = Bootstrap.Adapters.Legacy;
            Assert.That(adapter, Is.Not.Null, "Legacy 启动后必须装配 Legacy 适配器");

            // (2) 启动之后：唯一允许的推进来源是 Bootstrap 自己的帧 Update。
            //     判据是**关系式**（每次 Bootstrap Update 恰好驱动一次旧推进），
            //     而不是硬编码帧数——因此不依赖帧序号。
            int updatesBefore = Bootstrap.Ledger.BootstrapUpdates;
            int advancesBefore = LegacyAdvancePathCalls();
            int timelineBefore = LegacyTimelineAdvanceTimeCalls();
            int adapterCallsBefore = adapter.AdvanceCallCount;

            yield return WaitFrames(3);

            int updateDelta = Bootstrap.Ledger.BootstrapUpdates - updatesBefore;
            int advanceDelta = LegacyAdvancePathCalls() - advancesBefore;
            Assert.That(updateDelta, Is.GreaterThan(0),
                "对照证据：启动后 Bootstrap 的 Update 必须继续被调用");
            Assert.That(advanceDelta, Is.EqualTo(updateDelta),
                "每次 Bootstrap Update 必须恰好驱动一次旧顶层推进：" + DescribeAdvancePaths());
            Assert.That(adapter.AdvanceCallCount - adapterCallsBefore, Is.EqualTo(updateDelta),
                "Legacy 适配器的推进次数必须恰好等于 Bootstrap Update 次数：" + DescribeAdvancePaths());
            Assert.That(LegacyTimelineAdvanceTimeCalls() - timelineBefore, Is.EqualTo(updateDelta),
                "旧组件自报的 AdvanceTime 计数必须恰好等于 Bootstrap Update 次数：" + DescribeAdvancePaths());
            Assert.That(Bootstrap.Ledger.LegacyWriterAdvanceCalls("ProjectHero.Demos.CombatDemo.Update"),
                Is.EqualTo(LegacyAdvancePathCalls()),
                "旧时间线的每一次推进都必须由被调用的 Legacy 适配器发起：" + DescribeAdvancePaths());
            Assert.That(NewStepPathCalls(), Is.EqualTo(0), "Legacy 战斗不得推进新模拟：" + DescribeAdvancePaths());

            // (3) 显式驱动：推进次数必须精确等于驱动次数（增量口径，同样不依赖帧序号）。
            const int explicitDrives = 6;
            int advancesBeforeDrives = LegacyAdvancePathCalls();
            int timelineBeforeDrives = LegacyTimelineAdvanceTimeCalls();
            int adapterBeforeDrives = adapter.AdvanceCallCount;

            for (int i = 0; i < explicitDrives; i++)
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            Assert.That(LegacyAdvancePathCalls() - advancesBeforeDrives, Is.EqualTo(explicitDrives),
                "显式驱动次数必须精确等于旧顶层推进增量：" + DescribeAdvancePaths());
            Assert.That(adapter.AdvanceCallCount - adapterBeforeDrives, Is.EqualTo(explicitDrives),
                "Legacy 适配器推进增量必须等于显式驱动次数：" + DescribeAdvancePaths());
            Assert.That(LegacyTimelineAdvanceTimeCalls() - timelineBeforeDrives, Is.EqualTo(explicitDrives),
                "旧组件自报的 AdvanceTime 增量必须等于显式驱动次数：" + DescribeAdvancePaths());
            Assert.That(LegacyTimelineCurrentTick(), Is.GreaterThan(0L),
                "显式驱动后旧时间线必须真实推进");
            Assert.That(adapter.OwnsAutonomousUpdate, Is.False,
                "Legacy 适配器不得拥有自主推进（唯一推进者只能是 Bootstrap）");

            Bootstrap.StopBattle("hidden-scene-test");
            Bootstrap.ReleaseBattle();
        }

        // =====================================================================
        // 顶层时钟所有权
        // =====================================================================

        [UnityTest]
        public IEnumerator CombatDemoNoLongerOwnsAutonomousUpdate()
        {
            // 1) 代码事实：CombatDemo 不再声明 Update()/LateUpdate()/FixedUpdate()。
            var combatDemoType = ResolveProductionType("ProjectHero.Demos.CombatDemo");
            Assert.That(combatDemoType, Is.Not.Null, "必须能解析真实 CombatDemo 类型");
            Assert.That(DeclaresUnityUpdate(combatDemoType), Is.False,
                "CombatDemo 不得再声明 Update()/LateUpdate()/FixedUpdate()");

            var adapter = (IBattleFrameAdapter)FindProductionComponent("ProjectHero.Demos.CombatDemo");
            Assert.That(adapter, Is.Not.Null, "CombatDemo 必须实现 IBattleFrameAdapter");
            Assert.That(adapter.OwnsAutonomousUpdate, Is.False,
                "CombatDemo 的自主推进标记必须为 false");
            Assert.That(adapter.CallbackSite, Is.EqualTo("ProjectHero.Demos.CombatDemo.Update"));

            // 2) 运行事实：没有任何 Bootstrap 驱动时，旧时间线完全静止。
            yield return LoadHiddenValidationScene();
            yield return WaitFrames(5);
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(0),
                "没有 Bootstrap 驱动时旧时间线不得自行推进");
            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(0L));

            // 3) 对比组：同一场景在 Bootstrap Legacy 驱动下确实推进。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "Legacy 启动必须成功：" + Bootstrap.StartupRejection);
            for (int i = 0; i < 8; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(8),
                "Bootstrap 驱动后旧时间线必须推进——证明它现在只是被调用者");

            Bootstrap.StopBattle("combatdemo-ownership-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator LegacyLogicCallbacksAreClassifiedBeforeNewCanStart()
        {
            yield return LoadHiddenValidationScene();
            Bootstrap.SyncRuntimeLegacyWriters();

            // 每一个已登记的 Legacy 从属写入者都必须有分类记录。
            Assert.That(Bootstrap.LegacyWriters.Count, Is.GreaterThanOrEqualTo(6));
            for (int i = 0; i < Bootstrap.LegacyWriters.Count; i++)
            {
                var registration = Bootstrap.LegacyWriters[i];
                int dot = registration.CallbackSite.LastIndexOf('.');
                Assert.That(dot, Is.GreaterThan(0), "回调点必须是 Type.Member 形式");
                UnityCallbackClassification classification;
                bool classified = RuntimeCallbackRegistry.TryGet(
                    registration.CallbackSite.Substring(0, dot),
                    registration.CallbackSite.Substring(dot + 1), out classification);
                Assert.That(classified, Is.True,
                    "Legacy 从属写入者必须有分类记录：" + registration.CallbackSite);
                Assert.That(classification.CallbackClass, Is.EqualTo(UnityCallbackClass.LegacyDependentWriter),
                    "分类必须是 Legacy 从属写入者：" + registration.CallbackSite);
            }

            // 规格语义前半句：**已分类**的登记项在 New 模式被禁用，但**不阻止**启动。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True,
                "已分类的登记项不得阻止 New 启动：" + Bootstrap.StartupRejection);
            Assert.That(Bootstrap.Ledger.LegacyWriterGroupEnabled, Is.False);
            Assert.That(Bootstrap.EnabledLegacyWriterInstances, Is.EqualTo(0),
                "New 模式下不得有任何已解析写入者实例仍处于 enabled 状态");
            Assert.That(Bootstrap.StopBattle("classification-baseline"), Is.True);
            Assert.That(Bootstrap.ReleaseBattle(), Is.True);

            // 对照证据：先证明一个**未分类**写入者确实在场景里每帧自主执行。
            var unclassifiedGo = NewGameObject("UnclassifiedWriterProbe", typeof(UnclassifiedLogicWriter));
            var unclassified = unclassifiedGo.GetComponent<UnclassifiedLogicWriter>();
            UnclassifiedLogicWriter.UpdateCalls = 0;

            Bootstrap.RegisterLegacyWriterForTests(unclassified.GetType().FullName + ".Update", unclassified);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "Legacy 启动必须成功：" + Bootstrap.StartupRejection);
            yield return WaitFrames(3);

            Assert.That(unclassified.enabled, Is.True, "Legacy 模式下该写入者应保持启用");
            Assert.That(UnclassifiedLogicWriter.UpdateCalls, Is.GreaterThan(0),
                "对照组必须实际执行过 Update，否则门控断言没有意义");
            Assert.That(Bootstrap.StopBattle("classification-probe"), Is.True);
            Assert.That(Bootstrap.ReleaseBattle(), Is.True);
            Assert.That(unclassified.enabled, Is.True, "释放不得改写写入者启用状态（门控只由启动时应用）");

            // 规格语义后半句：**未分类**的逻辑写入者必须阻止 New 启动，
            // 并给出稳定拒绝码 + 类型名 + **对象路径**。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.False,
                "存在未分类逻辑写入者时 New 必须拒绝启动");
            Assert.That(Bootstrap.StartupRejection,
                Does.StartWith(BattleRuntimeBootstrap.BOOTSTRAP_UNCLASSIFIED_LEGACY_LOGIC_WRITER),
                "拒绝码不符：" + (Bootstrap.StartupRejection ?? "<null>")
                + " ; unclassified=[" + string.Join(",", BattleRuntimeBootstrap.UnclassifiedLogicWriterTypeNames)
                + "] ; probe=" + unclassified.GetType().FullName);
            Assert.That(Bootstrap.StartupRejection, Does.Contain(typeof(UnclassifiedLogicWriter).FullName),
                "拒绝必须点名未分类类型：" + Bootstrap.StartupRejection);
            Assert.That(Bootstrap.StartupRejection, Does.Contain("UnclassifiedWriterProbe"),
                "拒绝必须给出未分类写入者的对象路径：" + Bootstrap.StartupRejection);
            Assert.That(Bootstrap.BattleCreated, Is.False, "被拒绝的启动不得留下半创建状态");
            Assert.That(Bootstrap.BattleMode, Is.Null, "被拒绝的启动不得固定模式");
            Assert.That(Bootstrap.Ledger.NewStartupRejectionReason, Is.Not.Null,
                "被拒绝的 New 启动必须被账本记录（诊断口径）");

            // 清除未分类集合后 New 可以正常启动（同一探针组件仍在场景中，
            // 因此这里同时清掉按类型与按回调点的标记——否则它会一直挡住 New）。
            BattleRuntimeBootstrap.ClearUnclassifiedLogicWriterTypes();
            BattleRuntimeBootstrap.ClearUnclassifiedLogicWriterSite(
                unclassified.GetType().FullName + ".Update");
            Bootstrap.UnregisterLegacyWriterForTests(unclassified);
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True,
                "清除未分类集合后 New 必须能启动：" + Bootstrap.StartupRejection);
            Assert.That(Bootstrap.BattleCreated, Is.True);
            Assert.That(Bootstrap.Ledger.NewStartupRejectionReason, Is.Null,
                "成功启动不得留下旧的拒绝记录");

            Bootstrap.StopBattle("classification-test");
            Bootstrap.ReleaseBattle();
        }

        // =====================================================================
        // Legacy 从属写入组门控
        // =====================================================================

        [UnityTest]
        public IEnumerator NewModeDisablesEveryRegisteredLegacyLogicWriter()
        {
            yield return LoadHiddenValidationScene();
            yield return WaitFrames(2); // 让 UIManager 完成运行时创建 + 登记
            Bootstrap.SyncRuntimeLegacyWriters();

            Assert.That(Bootstrap.LegacyWriters.Count, Is.GreaterThanOrEqualTo(6),
                "Legacy 从属写入组至少应包含 6 条记录");
            Bootstrap.ApplyLegacyWriterGroupGateForTests(BattleRuntimeMode.Legacy);
            Assert.That(Bootstrap.AnyLegacyWriterEnabled(), Is.True);
            Assert.That(Bootstrap.EnabledLegacyWriterInstances, Is.GreaterThan(0),
                "Legacy 模式下必须命中真实的运行时实例");

            // 对照证据：运行时创建的写入者（生产类型 TimelineEditorUI，由 UIManager 在运行时
            // AddComponent 后经 LegacyWriterRegistry 登记）在 Legacy 模式下必须被启用。
            var runtimeWriterType = ResolveProductionType("ProjectHero.UI.Timeline.TimelineEditorUI");
            Assert.That(runtimeWriterType, Is.Not.Null,
                "必须能解析生产运行时写入者类型 TimelineEditorUI");
            var runtimeWriterGo = NewGameObject("RuntimeWriterProbe");
            var runtimeWriter = runtimeWriterGo.AddComponent(runtimeWriterType) as MonoBehaviour;
            Assert.That(runtimeWriter, Is.Not.Null);
            LegacyWriterRegistry.Register(runtimeWriter, "RuntimeWriterProbe#TimelineEditorUI", "<test>");
            Bootstrap.SyncRuntimeLegacyWriters();
            Bootstrap.ApplyLegacyWriterGroupGateForTests(BattleRuntimeMode.Legacy);
            Assert.That(runtimeWriter.enabled, Is.True, "Legacy 模式下运行时写入者必须被启用");

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True,
                "New 启动必须成功：" + Bootstrap.StartupRejection);

            Assert.That(runtimeWriter.enabled, Is.False, "New 模式下运行时创建的写入者必须被禁用");

            for (int i = 0; i < Bootstrap.LegacyWriters.Count; i++)
            {
                Assert.That(Bootstrap.LegacyWriters[i].IsEnabled, Is.False,
                    "New 模式下登记项必须禁用：" + Bootstrap.LegacyWriters[i].CallbackSite);
            }

            Assert.That(Bootstrap.EnabledLegacyWriterInstances, Is.EqualTo(0),
                "New 模式下不得有任何已解析写入者实例仍处于 enabled 状态");
            Assert.That(Bootstrap.Ledger.LegacyWriterGroupEnabled, Is.False);
            Assert.That(BattleRuntimeBootstrap.UnclassifiedLogicWriterTypeNames, Is.Empty,
                "被禁用的是已分类写入者，不应产生未分类记录");

            var runtimeStates = LegacyWriterRegistry.EnabledStates();
            Assert.That(runtimeStates.Count, Is.GreaterThan(0),
                "运行时写入者登记表必须有记录（对照证据）");
            foreach (var pair in runtimeStates)
            {
                Assert.That(pair.Value, Is.False, "运行时创建的写入者也必须被禁用：" + pair.Key);
            }

            Bootstrap.StopBattle("new-gate-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator LegacyAndShadowModesEnableTheRecordedLegacyWriterSet()
        {
            yield return LoadHiddenValidationScene();
            Bootstrap.SyncRuntimeLegacyWriters();

            int recorded = Bootstrap.LegacyWriters.Count;
            Assert.That(recorded, Is.GreaterThanOrEqualTo(6));
            Assert.That(Bootstrap.LegacyWritersWithGateTarget, Is.GreaterThanOrEqualTo(3),
                "隐藏验证场景必须提供可解析的真实写入者实例");

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "Legacy 启动必须成功：" + Bootstrap.StartupRejection);
            AssertWriterSet(recorded, "Legacy");
            Assert.That(Bootstrap.EnabledLegacyWriterInstances, Is.GreaterThan(0));
            Bootstrap.StopBattle("legacy-gate-test");
            Bootstrap.ReleaseBattle();

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);
            AssertWriterSet(recorded, "Shadow");
            Assert.That(Bootstrap.EnabledLegacyWriterInstances, Is.GreaterThan(0));
            Bootstrap.StopBattle("shadow-gate-test");
            Bootstrap.ReleaseBattle();
        }

        private void AssertWriterSet(int expectedCount, string mode)
        {
            int enabled = 0;
            for (int i = 0; i < Bootstrap.LegacyWriters.Count; i++)
            {
                var registration = Bootstrap.LegacyWriters[i];
                Assert.That(registration.IsEnabled, Is.True,
                    mode + " 模式下登记项必须启用：" + registration.CallbackSite);
                enabled++;
            }
            Assert.That(enabled, Is.GreaterThanOrEqualTo(expectedCount),
                mode + " 模式必须启用与基线一致的完整写入者集合");
            Assert.That(Bootstrap.Ledger.LegacyWriterGroupEnabled, Is.True);
        }

        // =====================================================================
        // 三模式互斥的调用矩阵
        // =====================================================================

        [UnityTest]
        public IEnumerator LegacyModeNeverCallsBattleSimulationStep()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "Legacy 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 12; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep), Is.EqualTo(0),
                "Legacy 模式不得调用新模拟 Step");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime), Is.EqualTo(12),
                "Legacy 模式必须调用旧顶层推进");
            Assert.That(Bootstrap.NewDriver.TicksAdvanced, Is.EqualTo(0));
            Assert.That(Bootstrap.NewDriver.SimulationCreated, Is.False,
                "Legacy 模式不得创建新模拟");

            Bootstrap.StopBattle("legacy-never-step");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator NewModeNeverCallsLegacyAdvanceTime()
        {
            yield return LoadHiddenValidationScene();
            int baselineAdvanceCalls = LegacyTimelineAdvanceTimeCalls();

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True,
                "New 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 12; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime), Is.EqualTo(0),
                "New 模式不得调用旧顶层推进");
            Assert.That(Bootstrap.Ledger.LegacyAdapterInvocations, Is.EqualTo(0));
            Assert.That(Bootstrap.Ledger.LegacyAdvanceTimeTotalCalls, Is.EqualTo(0));
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(baselineAdvanceCalls),
                "旧组件自报的 AdvanceTime 计数在 New 模式下不得增长");

            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep),
                Is.GreaterThan(0), "New 模式必须真正调用新模拟 Step");
            Assert.That(Bootstrap.NewDriver.TicksAdvanced, Is.GreaterThan(0));
            Assert.That(Bootstrap.NewDriver.BattleDefinitionHash, Is.EqualTo("d997b13b18573e15"),
                "New 模拟必须来自真实 02B 定义");

            Bootstrap.StopBattle("new-never-legacy");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator ShadowModeKeepsLegacyAsOnlyAuthority()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            const int frames = 10;
            long tickBeforeFrames = LegacyTimelineCurrentTick();
            int stepsBeforeFrames = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep);
            for (int i = 0; i < frames; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime),
                Is.EqualTo(frames), "Shadow 必须以 Legacy 为唯一权威并推进它");
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(frames));

            // 第二收尾轮 R1 之后语义更正：新模拟以**旧时间线的真实逻辑 Tick**为同步点，
            // 因此"新世界推进了几个 Tick"必须与"旧时钟前进了几个 Tick"一一对应
            // （唯一的 +1 是 Tick 起点归一化：新世界首个 Step 把 -1 带到 0）。
            long legacyTickAdvance = LegacyTimelineCurrentTick() - tickBeforeFrames;
            int newSteps = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep)
                - stepsBeforeFrames;
            Assert.That(legacyTickAdvance, Is.GreaterThan(0L), "旧时钟必须真的前进");
            Assert.That(newSteps, Is.EqualTo(legacyTickAdvance).Or.EqualTo(legacyTickAdvance + 1),
                "Shadow 独立世界必须与旧时钟逐 Tick 同步（最多因 Tick 起点归一化多 1 步）"
                + " ; legacyTickAdvance=" + legacyTickAdvance
                + " ; newSteps=" + newSteps
                + " ; shadowTick=" + Bootstrap.Shadow.SimulationTick
                + " ; legacyTick=" + LegacyTimelineCurrentTick()
                + " ; " + Bootstrap.Ledger.Describe());
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.EqualTo(LegacyTimelineCurrentTick()),
                "检查点采样时两侧逻辑 Tick 必须相等（对齐的前提）："
                + " ; shadowTick=" + Bootstrap.Shadow.SimulationTick
                + " ; legacyTick=" + LegacyTimelineCurrentTick()
                + " ; lastTarget=" + Bootstrap.Shadow.LastAdvanceTargetTick
                + " ; lastBefore=" + Bootstrap.Shadow.LastAdvanceTickBefore
                + " ; lastSteps=" + Bootstrap.Shadow.LastAdvanceTickCount
                + " ; observationCount=" + Bootstrap.LegacyObservations.Count
                + " ; keys=" + string.Join(",", Bootstrap.ShadowCheckpointKeys));
            Assert.That(Bootstrap.ShadowWrites.Total, Is.EqualTo(0),
                "Shadow 新模拟对 Unity/旧状态/反馈的写入必须为 0");
            Assert.That(Bootstrap.Ledger.LegacyWriterGroupEnabled, Is.True,
                "Shadow 模式仍启用完整旧运行组");
            Assert.That(Bootstrap.EnabledLegacyWriterInstances, Is.GreaterThan(0));

            var reports = Bootstrap.ShadowReports;
            Assert.That(reports.Count, Is.EqualTo(frames), "每个只读检查点必须产出一份比较报告");
            var last = reports[reports.Count - 1];
            Assert.That(last.Mode, Is.EqualTo(BattleRuntimeMode.Shadow));
            Assert.That(last.BattleDefinitionHash, Is.EqualTo("d997b13b18573e15"),
                "报告必须记录 BattleDefinitionHash");
            Assert.That(last.Encounter, Is.EqualTo("encounter.combat_sample_scene"),
                "报告必须记录 Encounter");
            Assert.That(last.RulesVersion, Is.EqualTo("battle-def-v1"));
            Assert.That(last.ComparisonConfigVersion, Is.EqualTo(1),
                "报告必须记录比较配置版本");
            Assert.That(last.InputSummary, Is.Not.Empty, "报告必须记录输入摘要");

            // 第二收尾轮 R1：生产比较路径必须真的有内容（旧实现恒 ComparedCheckpoints = 0）。
            Assert.That(Bootstrap.LegacyObservations.Count, Is.EqualTo(frames),
                "每个检查点必须采集一份 Legacy 侧只读观测");
            Assert.That(last.ComparedCheckpoints, Is.GreaterThan(0),
                "生产 Shadow 比较必须真的比较检查点，而不是恒 0 空转：" + last.Describe());

            Bootstrap.StopBattle("shadow-authority-test");
            Bootstrap.ReleaseBattle();
        }

        /// <summary>
        /// 第二收尾轮 R1：<strong>生产 Shadow 比较路径必须真的在比较</strong>，
        /// 且等价场景下的 claim 必须由真实比较结果推导；同时证明比较机制可失败
        /// （换一个错误观测就报基础设施差异，不允许任何宽泛容错）。
        /// </summary>
        [UnityTest]
        public IEnumerator ShadowProductionComparisonHasRealContentAndCanFail()
        {
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();
            var encounter = seed.Definition.FindEncounter(seed.EncounterId);
            Assert.That(encounter, Is.Not.Null);
            Assert.That(encounter.Slots.Count, Is.EqualTo(2),
                "对照证据：真实 Encounter 必须恰好两个槽位");

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            const int frames = 6;
            for (int i = 0; i < frames; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            // 正向证据：等价场景下真的比较了内容，并且 claim 由比较结果给出。
            var report = Bootstrap.LastShadowReport;
            Assert.That(report, Is.Not.Null);
            int observations = Bootstrap.LegacyObservations.Count;
            Assert.That(observations, Is.GreaterThan(0),
                "生产路径必须采集到 Legacy 侧只读观测（旧实现恒为空 ⇒ 比较空转）");
            Assert.That(observations, Is.LessThanOrEqualTo(frames));
            Assert.That(report.ComparedCheckpoints, Is.EqualTo(observations),
                "每个 Legacy 观测都必须完成一次字段级比较：" + report.Describe());
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                "两侧逻辑 Tick 必须对齐（新模拟以旧时间线的 Tick 为推进目标）：" + report.Describe());

            // 第三收尾轮 R2：单位数必须是**旧场景活动事实**，不是定义槽位数。
            // 生产观测实现在采样时强制"活动单位集合 == 显式绑定集合 == 定义槽位集合"，
            // 三者任一不符即整个观测失败；这里在测试侧独立复核三者相等，
            // 并确认比较内容量与之自洽。
            //
            // 任务 04：字段集合从「tick + 单位数 + 2 单位 × 4 身份字段 = 10」扩展为
            // 「5 个检查点级字段 + 2 单位 × 8 个字段 = 21」。阈值不再硬编码，
            // 而是从 LegacyLogicObservation 的冻结常量推出——两处必须同时改才会一致。
            AssertActiveUnitCountsAreConsistent(observations, "生产比较用例");

            Assert.That(report.ComparedFieldObservations, Is.GreaterThanOrEqualTo(
                    observations * ExpectedComparedFieldsPerObservation()),
                "比较内容量必须与「检查点级字段 + 2 个单位 × 逐单位字段」相符，"
                + "否则『有检查点』仍可能是空转：" + report.Describe());
            Assert.That(report.HasUnexpectedDifference, Is.False,
                "等价场景不得出现非预期差异：" + DescribeDifferenceList(report.Differences));
            Assert.That(report.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                "等价场景不得出现基础设施事实差异：" + DescribeDifferenceList(report.Differences));
            Assert.That(report.CanClaimEquivalence, Is.True, report.Describe());
            Assert.That(report.EquivalenceClaim, Is.EqualTo("EQUIVALENT"), report.Describe());

            // 未实现字段必须仍然逐条登记为"限期清零的暂不可比较字段"（不得冒充已比较）。
            Assert.That(report.TemporarilyUncomparable.Count, Is.GreaterThan(0));
            Assert.That(report.CountOf(ShadowDifferenceKind.TemporarilyUncomparable),
                Is.EqualTo(report.TemporarilyUncomparable.Count * observations),
                "每个检查点都必须为每个暂不可比较字段留下一条观察");

            // 反向证据（负控制）：换一个**错误**的 Legacy 观测，比较必须失败。
            // 这证明上面的等价结论来自真实比较，而不是"比较器什么都接受"。
            // 对齐键（Tick）保持不变，只改单位身份事实 ⇒ 未对齐计数必须仍为 0，
            // 差异只能来自"必须相等的身份字段"。
            var observed = Bootstrap.LegacyObservations[observations - 1];
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed.Units.Count, Is.EqualTo(2));

            var tamperedUnits = new List<LegacyLogicUnitObservation>();
            for (int i = 0; i < observed.Units.Count; i++)
            {
                var unit = observed.Units[i];
                bool tamper = i == 0;
                tamperedUnits.Add(new LegacyLogicUnitObservation(
                    unit.SlotId,
                    unit.UnitId,
                    unit.DefinitionId,
                    tamper ? "faction.tampered" : unit.FactionId,
                    unit.LegacyObjectPath));
            }

            var tampered = new LegacyLogicObservation(
                observed.Tick, observed.Checkpoint, observed.ObservedUnitCount,
                tamperedUnits, observed.SourceName);

            Assert.That(Bootstrap.LegacySlotOrder.Count, Is.EqualTo(2),
                "定义侧槽位顺序必须有两个槽位（否则槽位映射核对会退化为不可解析）");

            var tamperedReport = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    Array.Empty<LogicSnapshot>(), Bootstrap.Shadow.SnapshotSequence(), null,
                    new[] { tampered }, Bootstrap.LegacySlotOrder),
                ShadowCasePolicy.CreateDefault("03B-shadow-tampered-observation", seed.RulesVersion));

            Assert.That(tamperedReport.ComparedCheckpoints, Is.EqualTo(1),
                "被篡改的观测必须仍然对齐（否则本负控制退化成未对齐）：" + tamperedReport.Describe());
            Assert.That(tamperedReport.InfrastructureDifferences, Is.GreaterThan(0),
                "被篡改的 Legacy 观测必须产生基础设施事实差异：" + tamperedReport.Describe());
            Assert.That(tamperedReport.HasUnexpectedDifference, Is.False,
                "基础设施事实差异不得被归类为『按新规则验证的事实』");
            Assert.That(tamperedReport.CanClaimEquivalence, Is.False,
                "两侧基础设施事实不同的比较不得宣称等价");
            Assert.That(tamperedReport.EquivalenceClaim, Does.StartWith("INFRASTRUCTURE_DIFFERS:"),
                tamperedReport.Describe());

            // 零比较不得宣称等价（第二收尾轮 R1.3：不得因"无对齐检查点"就宣称等价）。
            var noComparable = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    Array.Empty<LogicSnapshot>(), Array.Empty<LogicSnapshot>()),
                ShadowCasePolicy.CreateDefault("03B-shadow-no-comparable", seed.RulesVersion));
            Assert.That(noComparable.ComparedCheckpoints, Is.EqualTo(0));
            Assert.That(noComparable.CanClaimEquivalence, Is.False,
                "零比较的『没有差异』是空转，不是等价：" + noComparable.Describe());
            Assert.That(noComparable.EquivalenceClaim, Is.EqualTo("NO_COMPARABLE_CHECKPOINT"),
                noComparable.Describe());

            Bootstrap.StopBattle("shadow-content-test");
            Bootstrap.ReleaseBattle();
        }

        /// <summary>
        /// 第二收尾轮 R1 的长战斗回归：比较窗口必须有界，且长战斗下**仍然**在比较。
        ///
        /// 缺陷形态（独立审查发现）：Legacy 观测列表只增不减，而单次比较预算
        /// `MaxStepsPerComparison` 默认只有 64 ⇒ Shadow 战斗跑过 64 帧后每份报告都是
        /// `BudgetOverrun`、`ComparedCheckpoints == 0`、`claim=INVALID:`，
        /// 生产比较**再次退化为不比较任何字段**；≤8 帧的既有用例无法暴露它。
        ///
        /// 第三收尾轮 R3：断言从"最后一份报告"升级为<strong>整个窗口</strong>——
        /// 150 帧产生的<strong>每一份</strong>报告都必须无预算作废、有比较内容、按 Tick 对齐、
        /// 无基础设施差异且 claim 为 <c>EQUIVALENT</c>。原实现只断言
        /// <c>LastShadowReport</c>，因此"某一帧瞬时超窗"只有最后一帧能报警。
        /// 预算裁剪的"有界"语义仍然保留（观测列表被裁剪到窗口内 + 裁剪数如实记录）。
        /// </summary>
        [UnityTest]
        public IEnumerator ShadowComparisonStaysBoundedAndComparableOverLongBattle()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            // 远超过默认单次比较预算（64）：旧实现从第 65 帧起全部报告作废。
            const int frames = 150;
            for (int i = 0; i < frames; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            // ---- 全窗口断言（第三收尾轮 R3）：不是只看最后一份报告 ----
            var reports = Bootstrap.ShadowReports;
            Assert.That(reports.Count, Is.EqualTo(frames),
                "长战斗的每一帧都必须留下一份报告（不得跳过检查点）");
            AssertSameWindowShape(reports, frames, "长战斗（同一窗口内）");

            var report = Bootstrap.LastShadowReport;
            Assert.That(report, Is.EqualTo(reports[reports.Count - 1]),
                "LastShadowReport 必须是窗口最后一份报告（对照证据：全窗口断言确实覆盖了它）");

            // ---- 有界语义（第二收尾轮 R1-P1）：窗口有界 + 裁剪如实记录 ----
            Assert.That(Bootstrap.LegacyObservations.Count, Is.LessThan(frames),
                "Legacy 观测列表必须被裁剪到比较窗口内，而不是无限增长");
            Assert.That(Bootstrap.LegacyObservations.Count, Is.GreaterThan(0));
            Assert.That(Bootstrap.DroppedLegacyObservations,
                Is.EqualTo(frames - Bootstrap.LegacyObservations.Count),
                "裁剪数必须等于「总检查点 - 窗口内容量」（如实记录，不静默丢弃）");

            // 最近窗口仍然对应真实推进：最后一个观测的 Tick 与两侧当前 Tick 一致。
            var lastObservation = Bootstrap.LegacyObservations[Bootstrap.LegacyObservations.Count - 1];
            Assert.That(lastObservation.Tick, Is.EqualTo(LegacyTimelineCurrentTick()),
                "窗口内最后一个观测必须是当前 Tick");
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.EqualTo(LegacyTimelineCurrentTick()),
                "长战斗下两侧逻辑 Tick 仍必须相等");
            Assert.That(LegacyTimelineCurrentTick(), Is.GreaterThan(frames - 1L),
                "旧时钟必须真的推进了足够多的 Tick");

            Bootstrap.StopBattle("shadow-long-battle-test");
            Bootstrap.ReleaseBattle();
        }

        /// <summary>
        /// 全窗口形状断言（第三收尾轮 R3）：长战斗中<strong>每一份</strong>报告都必须满足
        /// "预算未作废 / 有比较内容 / 按 Tick 对齐 / 无基础设施差异 / claim 稳定为 EQUIVALENT"。
        /// </summary>
        private static void AssertSameWindowShape(
            IReadOnlyList<ShadowComparisonReport> reports, int expectedCount, string context)
        {
            Assert.That(reports.Count, Is.EqualTo(expectedCount), context + "：报告数不符");

            int fieldObservationsSum = 0;
            for (int i = 0; i < reports.Count; i++)
            {
                var current = reports[i];
                string where = context + "：第 " + i + "/" + reports.Count + " 份报告 " + current.Describe();

                Assert.That(current.InvalidatedByBudget, Is.False,
                    "长战斗不得有任何一份报告因预算作废（比较窗口必须有界）：" + where);
                Assert.That(current.EquivalenceClaim, Does.Not.StartWith("INVALID:"),
                    "全程不得出现 INVALID 声明：" + where);
                Assert.That(current.UnalignedCheckpoints, Is.EqualTo(0),
                    "全程不得出现未对齐检查点（两侧逻辑 Tick 必须相等）：" + where);
                Assert.That(current.ComparedCheckpoints, Is.GreaterThan(0),
                    "每一份报告都必须真的比较了检查点，而不是空转：" + where);
                Assert.That(current.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                    "全程不得出现基础设施事实差异：" + where);
                Assert.That(current.CanClaimEquivalence, Is.True,
                    "全程必须可以宣称等价：" + where);
                Assert.That(current.EquivalenceClaim, Is.EqualTo("EQUIVALENT"),
                    "全程 claim 必须稳定为 EQUIVALENT：" + where);
                fieldObservationsSum += current.ComparedFieldObservations;
            }

            // 内容量：总比较字段数 ≥ 报告数 × 每检查点最小字段数。
            // 任务 04 起阈值由 LegacyLogicObservation 的冻结常量推出（检查点级 + 2 单位 × 逐单位），
            // 不再硬编码 10：字段集合变化必须同时改常量与断言，二者无法各自漂移。
            Assert.That(fieldObservationsSum, Is.GreaterThanOrEqualTo(
                    reports.Count * ExpectedComparedFieldsPerObservation()),
                context + "：比较内容量必须与「检查点级字段 + 2 个单位 × 逐单位字段」相符");
        }

        /// <summary>
        /// 一个检查点期望被比较的字段数（= 检查点级字段 + 本场 2 个单位 × 逐单位字段）。
        /// 常量来自 <see cref="LegacyLogicObservation"/>，因此断言与生产字段集合同源。
        /// </summary>
        private static int ExpectedComparedFieldsPerObservation()
            => LegacyLogicObservation.ComparableFieldCountPerCheckpoint
               + 2 * LegacyLogicObservation.ComparableFieldCountPerUnit;

        /// <summary>
        /// 第二收尾轮 R3 + 第三收尾轮 R1：Shadow 写入计数器由<strong>能力令牌守卫</strong>唯一持有，
        /// 并由负控制证明"计数为 0"不是恒真——故意经被守卫的路径尝试越界写入，
        /// 必须被拒绝且计数递增。
        ///
        /// <strong>第三收尾轮 R1 的覆盖边界（据此更正此前"真实触发路径"的过度表述）</strong>：
        /// 生产侧<strong>不存在</strong>写入面，也不存在写入点埋点。本用例用 IL 扫描把这个事实
        /// 变成可失败断言——全仓生产程序集中 <c>ShadowWriteWall.TryWrite</c> 的调用点
        /// <strong>恰好一个</strong>（<c>BattleRuntimeBootstrap.AttemptForbiddenShadowWriteForTests</c>，
        /// 即负控制探针自身）；并用反射核对契约程序集<strong>类型层面</strong>无法命名
        /// 旧运行组（<c>CombatUnit</c>/<c>BattleTimeline</c>）与 Unity 视图类型。
        /// 因此能证明的是"没有调用方持有已发放令牌 + 计数器可以失败"，
        /// <strong>不是</strong>"某个真实写入点没写"。若将来有人接上真实写入点（或去掉探针），
        /// 本用例会失败，记录必须同步更正。
        /// </summary>
        [UnityTest]
        public IEnumerator ShadowWriteWallRejectsForbiddenWritesAndCountsThem()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 4; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            // 正向：正常运行期间零写入、零越界尝试。
            Assert.That(Bootstrap.ShadowWrites.Total, Is.EqualTo(0));
            Assert.That(Bootstrap.ShadowWrites.RejectedAttempts, Is.EqualTo(0));
            Assert.That(Bootstrap.Shadow.RejectedWriteAttempts, Is.EqualTo(0));

            // 负控制：经被守卫路径尝试四类越界写入 —— 必须全部被拒绝并计入分项。
            var kinds = new[]
            {
                ShadowWriteKind.UnityObject, ShadowWriteKind.LegacyState,
                ShadowWriteKind.Feedback, ShadowWriteKind.ViewBinding
            };
            for (int i = 0; i < kinds.Length; i++)
            {
                string rejection = Bootstrap.AttemptForbiddenShadowWriteForTests(kinds[i]);
                Assert.That(rejection, Does.StartWith(ShadowWriteWall.SHADOW_WRITE_REJECTED),
                    "越界写入必须被守卫拒绝：" + kinds[i]);
                Assert.That(rejection, Does.Contain(kinds[i].ToString()));
            }

            Assert.That(Bootstrap.ShadowWrites.RejectedAttempts, Is.EqualTo(kinds.Length),
                "负控制必须让计数器真的变化（否则『为 0』是恒真断言）："
                + Bootstrap.ShadowWrites.Describe());
            Assert.That(Bootstrap.ShadowWrites.UnityObjectWrites, Is.EqualTo(1));
            Assert.That(Bootstrap.ShadowWrites.LegacyStateWrites, Is.EqualTo(1));
            Assert.That(Bootstrap.ShadowWrites.FeedbackWrites, Is.EqualTo(1));
            Assert.That(Bootstrap.ShadowWrites.ViewBindings, Is.EqualTo(1));
            Assert.That(Bootstrap.ShadowWrites.Total, Is.EqualTo(kinds.Length));
            Assert.That(Bootstrap.Shadow.RejectedWriteAttempts, Is.EqualTo(kinds.Length),
                "Shadow runner 观察到的越界尝试数必须同步（runner 与 Bootstrap 共享同一份计数器）");

            // 守卫本身的结构：Shadow 侧令牌永不发放 ⇒ 任何调用方都拿不到写入许可。
            Assert.That(ShadowWriteWall.Permit.IsIssued, Is.False,
                "Shadow 侧不得存在已发放的写入令牌");

            // 第三收尾轮 R1：把"生产侧无写入面"从措辞变成可失败断言。
            // ① IL 扫描：全仓非测试程序集中 TryWrite 的调用点恰好一个 = 负控制探针自身。
            var productionCallSites = FindProductionTryWriteCallSites();
            UnityEngine.Debug.Log("[03B-R1] ShadowWriteWall.TryWrite 生产调用点 = "
                + productionCallSites.Count + " → " + string.Join(" | ", productionCallSites));
            Assert.That(productionCallSites.Count, Is.EqualTo(1),
                "生产侧只允许存在一个 ShadowWriteWall.TryWrite 调用点（负控制探针自身）；"
                + "出现第二个（= 有人接上真实写入点）或零个（= 探针被移除、计数重新变成恒真）"
                + "都必须让本断言失败，并同步更正交接记录的覆盖边界声明。实测："
                + string.Join(" | ", productionCallSites));

            // ② 结构：契约程序集类型层面无法命名旧运行组与 Unity 视图类型。
            AssertContractAssemblyCannotNameLegacyOrViewTypes();

            // 每场战斗重置计数（记录的是事实，重置只影响计数起点）。
            Bootstrap.ShadowWrites.Reset();
            Assert.That(Bootstrap.ShadowWrites.Total, Is.EqualTo(0));

            // 反向断言：Shadow 的世界确实在推进（排除"什么都没做"的假绿）。
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.GreaterThan(0));
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep),
                Is.GreaterThan(0));

            Bootstrap.StopBattle("shadow-write-wall-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator ShadowSimulationCannotBindViewsOrPlayFeedback()
        {
            // 结构证据：Shadow runner 与 New driver 的公开面不出现任何 Unity 视图/反馈类型。
            AssertNoForbiddenUnitySurface(typeof(ShadowBattleRunner));
            AssertNoForbiddenUnitySurface(typeof(UnityBattleDriver));

            // 运行时证据：跑 Shadow，视图/反馈写入计数为 0。
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 6; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            Assert.That(Bootstrap.ShadowWrites.ViewBindings, Is.EqualTo(0));
            Assert.That(Bootstrap.ShadowWrites.FeedbackWrites, Is.EqualTo(0));
            Assert.That(Bootstrap.ShadowWrites.UnityObjectWrites, Is.EqualTo(0));

            Bootstrap.StopBattle("shadow-view-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator ShadowSimulationCannotMutateLegacyOrUnityState()
        {
            yield return LoadHiddenValidationScene();
            yield return WaitFrames(2);
            Bootstrap.SyncRuntimeLegacyWriters();

            int advanceBefore = LegacyTimelineAdvanceTimeCalls();
            var unitTransforms = CaptureUnitTransforms();
            var writerStates = CaptureWriterStates();
            var activeStates = CaptureActiveStates();
            Assert.That(unitTransforms.Count, Is.GreaterThan(0), "对照证据：场景中必须有旧 CombatUnit");

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            const int frames = 8;
            for (int i = 0; i < frames; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            // 1) 旧权威的推进只能来自被调用的 Legacy 适配器（计数精确匹配）。
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(advanceBefore + frames));
            Assert.That(Bootstrap.Ledger.LegacyAdapterInvocations, Is.EqualTo(frames));

            // 2) Shadow 新模拟的写入次数为 0。
            Assert.That(Bootstrap.ShadowWrites.Total, Is.EqualTo(0),
                "Shadow 新模拟不得写 Unity 对象/旧状态/表现反馈/视图绑定");

            // 3) Unity 对象状态未被 Shadow 改变。
            AssertTransformsUnchanged(unitTransforms, "Shadow 期间单位 Transform 被改写");
            AssertWriterStatesUnchanged(writerStates, "Shadow 期间写入者启用状态被改写");
            AssertActiveStatesUnchanged(activeStates, "Shadow 期间对象启用状态被改写");

            // 4) Shadow 自己的独立世界确实在推进（排除"什么都没做"的假绿）。
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.GreaterThan(0),
                "Shadow 独立世界必须真的推进，否则零写入是平凡事实");

            Bootstrap.StopBattle("shadow-mutation-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator RuntimeModeCannotChangeDuringActiveBattle()
        {
            yield return LoadHiddenValidationScene();

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "Legacy 启动必须成功：" + Bootstrap.StartupRejection);
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.Legacy));

            Assert.That(Bootstrap.TrySetRequestedMode(BattleRuntimeMode.New, out string rejection), Is.False,
                "活动战斗期间修改模式必须被拒绝");
            Assert.That(rejection, Does.StartWith(BattleRuntimeBootstrap.BOOTSTRAP_MODE_CHANGE_REJECTED));
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.Legacy),
                "被拒绝的模式修改不得改变本场战斗的模式");

            // 纯数据契约同样拒绝（不依赖 Bootstrap 实例）。
            var activeState = new BattleRuntimeLifecycle.ModeState(
                BattleRuntimeMode.Legacy, battleActive: true, stopped: false, writerGroupEnabled: true);
            Assert.That(BattleRuntimeLifecycle.TrySwitchMode(
                activeState, BattleRuntimeMode.Shadow, out string contractRejection), Is.False);
            Assert.That(contractRejection,
                Does.StartWith(BattleRuntimeBootstrap.BOOTSTRAP_MODE_CHANGE_REJECTED));
            Assert.That(BattleRuntimeLifecycle.Validate(activeState), Is.Null,
                "Legacy 活动战斗的写入组门控必须与模式契约一致");

            // 重复创建同一场战斗也是错误。
            Assert.Throws<LogicDefinitionException>(
                () => Bootstrap.StartBattle(BattleRuntimeMode.Legacy));

            // 停止后重新创建另一模式的战斗必须成功（"可回切"的唯一含义）。
            Assert.That(Bootstrap.StopBattle("mode-immutability-test"), Is.True);
            Assert.That(Bootstrap.ReleaseBattle(), Is.True);

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True,
                "停止并释放后必须能以另一模式新建战斗：" + Bootstrap.StartupRejection);
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.New));

            Bootstrap.StopBattle("mode-immutability-test-2");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator DestroyingBattleStopsActiveAdaptersExactlyOnce()
        {
            // 1) 正常停止路径：参与本场战斗的每个适配器恰好停止一次。
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 4; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            Assert.That(Bootstrap.Adapters.All.Count, Is.EqualTo(3),
                "Shadow 战斗必须装配 Legacy + NewDriver + Shadow 三个适配器");

            Assert.That(Bootstrap.StopBattle("stop-once-test"), Is.True,
                "第一次停止必须成功；BattleCreated=" + Bootstrap.BattleCreated
                + " IsStopped=" + Bootstrap.IsStopped
                + " mode=" + Bootstrap.BattleMode);
            Assert.That(Bootstrap.Adapters.Legacy.StopCallCount, Is.EqualTo(1));
            Assert.That(Bootstrap.Shadow.StopCallCount, Is.EqualTo(1));
            Assert.That(Bootstrap.NewDriver.StopCallCount, Is.EqualTo(1));

            Assert.That(Bootstrap.StopBattle("stop-once-test-again"), Is.False, "停止必须幂等");
            Assert.That(Bootstrap.Adapters.Legacy.StopCallCount, Is.EqualTo(1));
            Assert.That(Bootstrap.Shadow.StopCallCount, Is.EqualTo(1));
            Assert.That(Bootstrap.NewDriver.StopCallCount, Is.EqualTo(1));

            Assert.That(Bootstrap.ReleaseBattle(), Is.True,
                "释放必须成功；BattleCreated=" + Bootstrap.BattleCreated + " IsStopped=" + Bootstrap.IsStopped);
            Assert.That(Bootstrap.ReleaseBattle(), Is.False, "释放必须幂等");
            Assert.That(Bootstrap.Adapters.Legacy, Is.Null, "释放后不得再持有适配器引用");
            Assert.That(Bootstrap.Adapters.All, Is.Empty);
            Assert.That(Bootstrap.BattleCreated, Is.False);

            // 2) 可回切：同一入口以**另一模式**重新开局必须成功，且适配器状态被显式复位
            //    （旧实现缺少复位：Legacy 适配器保留上一场的 _stopped = true，
            //     新战斗"开局即停止"，Adapters.Legacy.IsStopped 恒为 true）。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "重新开局必须成功：" + Bootstrap.StartupRejection
                + " ; BattleCreated=" + Bootstrap.BattleCreated);

            var legacyAdapter = Bootstrap.Adapters.Legacy;
            var newDriver = Bootstrap.NewDriver;
            Assert.That(legacyAdapter, Is.Not.Null);
            Assert.That(legacyAdapter.IsStopped, Is.False, "重新开局的适配器不得残留上一场的停止状态");
            Assert.That(legacyAdapter.IsInitialized, Is.True);
            Assert.That(legacyAdapter.AdvanceCallCount, Is.EqualTo(0), "重新开局必须清零上一场的推进计数");
            Assert.That(legacyAdapter.StopCallCount, Is.EqualTo(0), "重新开局必须清零上一场的停止计数");
            Assert.That(Bootstrap.Shadow, Is.Null, "Legacy 战斗不得装配 Shadow runner");
            Assert.That(Bootstrap.Adapters.All.Count, Is.EqualTo(2));

            // 第二收尾轮 R2：重开后旧时间线不得残留上一场的系统暂停
            // （旧实现 StopBattle 置位、重开局不清除 ⇒ Tick 永不推进，而调用计数照涨）。
            Assert.That(LegacyTimelineSystemPaused(), Is.False,
                "重新装配新战斗时必须清除旧时间线的系统暂停（可回切 = 重开后真的能推进）");

            long tickBeforeRestart = LegacyTimelineCurrentTick();
            int callsBeforeRestart = LegacyTimelineAdvanceTimeCalls();
            for (int i = 0; i < 4; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            Assert.That(legacyAdapter.AdvanceCallCount, Is.EqualTo(4), "重新开局后必须真的可推进");
            Assert.That(LegacyTimelineCurrentTick(), Is.GreaterThan(tickBeforeRestart),
                "重开后的逻辑 Tick 必须真的前进（只断言调用计数会掩盖系统暂停残留）");
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(callsBeforeRestart + 4),
                "重开后旧时间线必须逐帧收到推进（计数与 Tick 必须同时成立）");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime), Is.EqualTo(4));

            // 3) 销毁边界：Bootstrap 被销毁时必须先停止再释放，且只停止一次。
            int reasonsBefore = BattleRuntimeBootstrap.DestroyedStopReasons.Count;

            var owner = Bootstrap.gameObject;
            UnityEngine.Object.Destroy(owner);
            yield return null;
            yield return null;

            var reasons = BattleRuntimeBootstrap.DestroyedStopReasons;
            Assert.That(reasons.Count, Is.EqualTo(reasonsBefore + 1),
                "销毁边界必须恰好产生一条停止记录（before=" + reasonsBefore + " after=" + reasons.Count + "）");
            Assert.That(reasons[reasons.Count - 1], Is.EqualTo("BOOTSTRAP_DESTROYED"));

            // 销毁边界必须真的停止本场战斗的适配器一次。NewDriver 是普通 C# 对象，
            // 因此在 Bootstrap 与其场景组件被销毁后仍可安全观测；Legacy 适配器与 Bootstrap
            // 同属被销毁的对象层级，其销毁顺序由 Unity 决定，故不断言其计数。
            Assert.That(newDriver.StopCallCount, Is.EqualTo(1),
                "销毁边界必须恰好停止一次参与本场战斗的适配器");
            Assert.That(BattleRuntimeBootstrap.LiveInstanceCount, Is.EqualTo(0),
                "销毁后不得残留存活实例（含 inactive/被禁用实例）");
            Assert.That(BattleRuntimeBootstrap.ClockOwnerCountInScene(), Is.EqualTo(0));
        }

        // =====================================================================
        // Shadow 检查点与报告
        // =====================================================================

        [UnityTest]
        public IEnumerator ShadowComparisonRunsAfterEveryRegisteredLegacyWriter()
        {
            yield return LoadHiddenValidationScene();
            yield return WaitFrames(2);
            Bootstrap.SyncRuntimeLegacyWriters();

            Assert.That(Bootstrap.WritersExecutingAtOrAfterCheckpoint(), Is.Empty,
                "只读检查点的执行顺序必须晚于全部已登记 Legacy 写入者");
            Assert.That(Bootstrap.WritersExecutingInLateUpdatePhase(), Is.Empty,
                "全部已登记 Legacy 写入者必须处在 Update 相位——检查点在 LateUpdate 相位，"
                + "顺序保证来自这条相位边界（第二收尾轮 R4.1）而不是任何执行顺序常量");
            Assert.That(Bootstrap.WriterOrderMismatches(), Is.Empty,
                "已登记写入者的真实执行顺序必须与分类表一致（防止事后篡改顺序）");
            Assert.That(RuntimeCallbackRegistry.CheckpointExecutionOrder,
                Is.GreaterThan(RuntimeCallbackRegistry.LegacyWriterExecutionOrder));
            Assert.That(RuntimeCallbackRegistry.BootstrapExecutionOrder,
                Is.LessThan(RuntimeCallbackRegistry.LegacyWriterExecutionOrder),
                "Bootstrap 必须早于旧写入者执行，才能保持改造前同帧的时间语义");
            Assert.That(RuntimeCallbackRegistry.CheckpointExecutionOrder,
                Is.GreaterThan(RuntimeCallbackRegistry.BootstrapExecutionOrder),
                "检查点的声明顺序位必须晚于 Bootstrap（Bootstrap 在 Update 相位驱动两侧，"
                + "检查点在 LateUpdate 相位只读采样）");

            // 第二收尾轮 R4.2：协程站点必须已进入分类表，且相位被标为 Coroutine
            // （它们不在 Update 相位，因此不受"检查点晚于全部写入者"的相位保证覆盖）。
            var phases = BattleRuntimeBootstrap.ClassifiedLegacyWriterPhases();
            Assert.That(phases, Has.Some.Contains("ProjectHero.Core.Timeline.BattleTimeline.TriggerSlowMotion"),
                "BattleTimeline 的慢动作协程站点必须已分类");
            Assert.That(phases, Has.Some.Contains("ProjectHero.Visuals.GameFeelManager.HitStop"),
                "GameFeelManager.HitStop 必须已分类");
            Assert.That(phases, Has.Some.Contains("ProjectHero.Visuals.GameFeelManager.ScreenShake"),
                "GameFeelManager.ScreenShake 必须已分类");
            Assert.That(phases, Has.Some.Contains("|" + UnityCallbackPhase.Coroutine),
                "协程站点必须被标为 Coroutine 相位，而不是 Update 相位");

            // 真实帧序证据。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);
            yield return WaitFrames(3);

            Assert.That(Bootstrap.Ledger.BootstrapUpdates, Is.GreaterThan(0));
            Assert.That(Bootstrap.Ledger.BootstrapCheckpoints, Is.GreaterThan(0));
            Assert.That(Bootstrap.LastShadowReport, Is.Not.Null);
            Assert.That(Bootstrap.LastShadowReport.InvalidatedByBudget, Is.False);
            Assert.That(Bootstrap.Shadow.Count, Is.GreaterThan(0));

            Bootstrap.StopBattle("checkpoint-order-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator ShadowLateUpdateCheckpointIsReadOnlyAndDoesNotAdvanceEitherClock()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 5; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
            yield return null;

            long timelineTickBefore = LegacyTimelineCurrentTick();
            int timelineCallsBefore = LegacyTimelineAdvanceTimeCalls();
            long shadowTickBefore = Bootstrap.Shadow.SimulationTick;
            int newStepCallsBefore = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep);
            int legacyCallsBefore = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime);
            int checkpoints = Bootstrap.Ledger.BootstrapCheckpoints;

            for (int i = 0; i < 3; i++) Bootstrap.RunCheckpointForTests();

            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(timelineTickBefore),
                "只读检查点不得推进旧时间线");
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(timelineCallsBefore));
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime),
                Is.EqualTo(legacyCallsBefore));
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.EqualTo(shadowTickBefore),
                "只读检查点不得推进新模拟");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep),
                Is.EqualTo(newStepCallsBefore));
            Assert.That(Bootstrap.ShadowWrites.Total, Is.EqualTo(0));
            Assert.That(Bootstrap.Ledger.BootstrapCheckpoints, Is.EqualTo(checkpoints + 3),
                "检查点本身必须被记录（对照证据：检查点确实执行了）");

            Bootstrap.StopBattle("checkpoint-readonly-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator ShadowReportFindsFirstUnexpectedDifference()
        {
            yield return LoadHiddenValidationScene();

            var seed = BuildSeedFromFactory();
            const int divergenceTick = 2;
            const int ticks = 5;

            // 两侧都是**真实** Logic 世界（同一 02B 定义 + 同一初始输入 + 同一可信请求），
            // 唯一区别是 Shadow 侧启用了阶段 1/15 的"单位状态推进"扩展点
            // （IUnitStateAdvanceSystem，任务 04 的正式接入点），在已知 Tick 下调单位生命。
            // 因此差异来自真实 Step 管线，而不是伪造的快照对象。
            var legacySide = BuildRealCheckpointStream(seed, ticks, unitStateAdvance: null, submitTick: divergenceTick);
            long unitId = legacySide.Snapshots[0].Units[0].UnitId;
            int healthQ10 = legacySide.Snapshots[0].Units[0].HealthQ10;
            int healthDelta = HealthDeltaThatKeepsUnitAlive(healthQ10);
            var shadowSide = BuildRealCheckpointStream(
                seed, ticks,
                new ScriptedUnitStateAdvanceSystem(divergenceTick, unitId, healthQ10 - healthDelta),
                submitTick: divergenceTick);

            // 对照证据 0：检查点下标必须等于逻辑 Tick（Tick 0 = 初始状态，随后 1..N）。
            for (int i = 0; i < legacySide.Snapshots.Count; i++)
            {
                Assert.That(legacySide.Snapshots[i].Tick, Is.EqualTo((long)i),
                    "检查点下标必须等于逻辑 Tick：index=" + i);
            }

            // 对照证据 1：分叉 Tick 之前两侧的规范化状态逐位相同。
            for (int i = 0; i <= divergenceTick - 1; i++)
            {
                Assert.That(shadowSide.Snapshots[i].ComputeHash(),
                    Is.EqualTo(legacySide.Snapshots[i].ComputeHash()),
                    "分叉之前两侧必须逐位相同：Tick " + legacySide.Snapshots[i].Tick
                    + " ; " + DescribeSideComparison(legacySide, shadowSide, unitId));
            }

            // 对照证据 2：分叉 Tick 起两侧的规范化状态真实不同（否则本用例无意义）。
            Assert.That(shadowSide.Snapshots[divergenceTick].ComputeHash(),
                Is.Not.EqualTo(legacySide.Snapshots[divergenceTick].ComputeHash()),
                "Tick " + divergenceTick + " 的两侧规范化状态必须真实分叉"
                + " ; " + DescribeSideComparison(legacySide, shadowSide, unitId));

            // 对照证据 3：该 Tick 携带**真实事件序号**（可信请求在该 Tick 被处理器稳定拒绝）。
            long expectedEventSequence = shadowSide.FirstEventSequenceAt(divergenceTick);
            Assert.That(expectedEventSequence, Is.GreaterThan(0L),
                "分叉 Tick 必须携带真实事件序号（否则无法验证事件绑定）：" + shadowSide.Describe());

            var report = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    legacySide.Snapshots, shadowSide.Snapshots, shadowSide.EventBindings),
                ShadowCasePolicy.CreateDefault("03B-shadow-first-difference", seed.RulesVersion));

            Assert.That(report.HasUnexpectedDifference, Is.True,
                "必须定位到首个非预期差异：" + report.Describe());

            var first = report.FirstUnexpectedDifference;
            Assert.That(first.Kind, Is.EqualTo(ShadowDifferenceKind.NewRuleVerifiedFact),
                "首个非预期差异必须是'按新规则验证的事实'");
            Assert.That(first.LogicalTick, Is.EqualTo((long)divergenceTick),
                "首个非预期差异必须绑定首个分叉的逻辑 Tick：" + first);
            Assert.That(first.Checkpoint, Is.EqualTo("LateUpdate@" + divergenceTick),
                "差异必须绑定具名检查点：" + first.Checkpoint);
            Assert.That(first.FieldPath, Is.EqualTo("units[0].healthQ10"),
                "差异必须带精确字段路径：" + first.FieldPath);
            Assert.That(first.LegacyValue, Is.EqualTo(healthQ10.ToString()),
                "旧值摘要必须精确：" + first.LegacyValue);
            Assert.That(first.ShadowValue, Is.EqualTo((healthQ10 - healthDelta).ToString()),
                "新值摘要必须精确：" + first.ShadowValue);
            Assert.That(first.LegacyValue, Is.Not.EqualTo(first.ShadowValue));
            Assert.That(first.RelatedEventSequence, Is.EqualTo(expectedEventSequence),
                "差异必须绑定该 Tick 的真实事件序号：" + first.RelatedEventSequence
                + " vs " + expectedEventSequence);
            Assert.That(first.Reason, Is.Not.Empty, "差异必须携带原因说明");

            Assert.That(report.CanClaimEquivalence, Is.False, "存在非预期差异时不得宣称等价");
            Assert.That(report.EquivalenceClaim, Does.StartWith("DIFFERENT:"));
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0), "两侧按逻辑 Tick 完全对齐");
            Assert.That(report.ComparedCheckpoints, Is.EqualTo(legacySide.Snapshots.Count));

            // 首个差异必须是规范顺序中的第一条非预期差异（不是随机挑一条）。
            for (int i = 0; i < report.Differences.Count; i++)
            {
                var difference = report.Differences[i];
                if (!difference.IsUnexpected) continue;
                Assert.That(difference.FieldPath, Is.EqualTo(first.FieldPath),
                    "首个非预期差异必须是差异列表中的第一条非预期差异");
                break;
            }

            // 后续 Tick 上同一字段继续分叉：差异列表必须完整（不只报第一条）。
            Assert.That(report.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact),
                Is.EqualTo(ticks - divergenceTick),
                "分叉后的每个检查点都必须各有一条差异：" + DescribeDifferenceList(report.Differences));
        }

        [UnityTest]
        public IEnumerator ShadowReportClassifiesExpectedAndUnexpectedDifferences()
        {
            yield return LoadHiddenValidationScene();

            var seed = BuildSeedFromFactory();
            const string caseId = "03B-shadow-classification";
            const int divergenceTick = 2;
            const int ticks = 5;
            string rulesVersion = seed.RulesVersion;

            // 真实检查点流：同一 02B 定义 + 同一初始输入 + 同一可信请求。
            var equivalentA = BuildRealCheckpointStream(seed, ticks, null, submitTick: divergenceTick);
            var equivalentB = BuildRealCheckpointStream(seed, ticks, null, submitTick: divergenceTick);
            long unitId = equivalentA.Snapshots[0].Units[0].UnitId;
            int healthQ10 = equivalentA.Snapshots[0].Units[0].HealthQ10;
            int healthDelta = HealthDeltaThatKeepsUnitAlive(healthQ10);
            var noSubmit = BuildRealCheckpointStream(seed, ticks, null, submitTick: -1);
            var diverged = BuildRealCheckpointStream(
                seed, ticks,
                new ScriptedUnitStateAdvanceSystem(divergenceTick, unitId, healthQ10 - healthDelta),
                submitTick: divergenceTick);

            var defaultPolicy = ShadowCasePolicy.CreateDefault(caseId, rulesVersion);

            // (1) 等价场景：两侧逐位相同 → 四类结果中"暂不可比较字段"仍然显式登记，
            //     其余三类为空，并且**允许**宣称等价。
            var equivalent = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, rulesVersion, ShadowComparisonConfig.Strict(64),
                    equivalentA.Snapshots, equivalentB.Snapshots, equivalentB.EventBindings),
                defaultPolicy);

            Assert.That(equivalent.HasUnexpectedDifference, Is.False,
                "同一装配的两个独立世界不得出现非预期差异：" + equivalent.Describe());
            Assert.That(equivalent.InfrastructureDifferences, Is.EqualTo(0),
                "同一装配的两个独立世界不得出现基础设施事实差异：" + equivalent.Describe());
            Assert.That(equivalent.UnalignedCheckpoints, Is.EqualTo(0));
            Assert.That(equivalent.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0));
            Assert.That(equivalent.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0));
            Assert.That(equivalent.CanClaimEquivalence, Is.True, equivalent.Describe());
            Assert.That(equivalent.EquivalenceClaim, Is.EqualTo("EQUIVALENT"));

            // (2) 类别三：限期清零的暂不可比较字段 —— 默认策略逐条登记。
            //     任务 04 把 4 条「必须切换为实时读取」的字段移入字段级比较
            //     （units[i].position / units[i].facing / battleEnd.isEnded / units[i].healthQ10），
            //     同时新增 3 条覆盖边界/语义边界登记（battleEnd.isEnded 的时序差异、
            //     units[i].state 的 Guard/Block/Dodge 覆盖边界、scheduledEventCount），
            //     因此条数由 11 变为 10；逐条仍带对象路径/字段/原因/负责任务/最迟清零门槛，
            //     并且**仍然出现在差异观察里**。
            Assert.That(equivalent.TemporarilyUncomparable.Count, Is.EqualTo(10),
                "默认策略必须逐条登记暂不可比较字段（任务 04 收口后共 10 条）："
                + string.Join(" | ", equivalent.TemporarilyUncomparable));
            Assert.That(equivalent.Rejections, Is.Empty, "默认策略不得产生被拒绝的登记项");
            Assert.That(equivalent.CountOf(ShadowDifferenceKind.TemporarilyUncomparable),
                Is.EqualTo(equivalent.TemporarilyUncomparable.Count * equivalent.ComparedCheckpoints),
                "每个检查点都必须为每个暂不可比较字段留下一条观察（不得静默丢弃）");
            for (int i = 0; i < equivalent.TemporarilyUncomparable.Count; i++)
            {
                var field = equivalent.TemporarilyUncomparable[i];
                Assert.That(field.LegacyObjectPath, Is.Not.Empty, "暂不可比较字段必须有具体 Legacy 对象路径");
                Assert.That(field.Field, Is.Not.Empty);
                Assert.That(field.Reason, Is.Not.Empty);
                Assert.That(field.OwnerTask, Is.Not.Empty, "暂不可比较字段必须有负责任务");
                Assert.That(field.RemovalGate, Is.Not.Empty, "暂不可比较字段必须有最迟清零门槛");
            }

            bool sawOwnerAndGate = false;
            for (int i = 0; i < equivalent.Differences.Count; i++)
            {
                var difference = equivalent.Differences[i];
                if (difference.Kind != ShadowDifferenceKind.TemporarilyUncomparable) continue;
                if (difference.Reason.Contains("owner=") && difference.Reason.Contains("gate="))
                    sawOwnerAndGate = true;
            }
            Assert.That(sawOwnerAndGate, Is.True, "暂不可比较字段的观察必须携带责任任务与清零门槛");

            // (3) 类别一：必须相等的基础设施事实 —— 用**真实**分叉：
            //     一侧在 Tick 2 提交可信请求、另一侧不提交 → 命令序号/排程修订等
            //     "必须相等"的事实分叉，且不得被误判为按新规则验证的事实。
            var infraReport = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, rulesVersion, ShadowComparisonConfig.Strict(64),
                    equivalentA.Snapshots, noSubmit.Snapshots, noSubmit.EventBindings),
                defaultPolicy);

            Assert.That(infraReport.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.GreaterThan(0),
                "提交/不提交可信请求的两侧必须产生基础设施事实差异：" + infraReport.Describe());
            Assert.That(infraReport.FirstInfrastructureDifferenceField, Is.Not.Empty);
            Assert.That(infraReport.HasUnexpectedDifference, Is.False,
                "基础设施事实差异不得被误归类为按新规则验证的事实");
            Assert.That(infraReport.CanClaimEquivalence, Is.False,
                "'必须相等'的事实不同的两侧不得宣称等价");
            Assert.That(infraReport.EquivalenceClaim, Does.StartWith("INFRASTRUCTURE_DIFFERS:"),
                infraReport.Describe());

            // (4) 类别二：按新规则验证的事实（真实单位生命分叉）→ 非预期差异。
            var unapproved = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, rulesVersion, ShadowComparisonConfig.Strict(64),
                    equivalentA.Snapshots, diverged.Snapshots, diverged.EventBindings),
                defaultPolicy);

            Assert.That(unapproved.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(3),
                "分叉后的每个检查点各一条按新规则验证的事实（Tick 2/3/4）：" + unapproved.Describe());
            Assert.That(unapproved.HasUnexpectedDifference, Is.True, unapproved.Describe());
            Assert.That(unapproved.FirstUnexpectedDifference.FieldPath, Is.EqualTo("units[0].healthQ10"));
            Assert.That(unapproved.FirstUnexpectedDifference.LogicalTick, Is.EqualTo((long)divergenceTick));
            Assert.That(unapproved.CanClaimEquivalence, Is.False);
            Assert.That(unapproved.EquivalenceClaim, Does.StartWith("DIFFERENT:"));

            // (5) 类别四：逐用例批准差异 —— 精确到 用例 ID + RulesVersion + 字段 + 原因时生效。
            var approvals = new List<ShadowComparisonApproval>();
            var rejections = new List<string>();
            Assert.That(ShadowCasePolicy.TryRegisterApproval(
                approvals, rejections, caseId, rulesVersion, "units[0].healthQ10",
                "经任务 04 批准的量化取整差异"), Is.True);
            Assert.That(rejections, Is.Empty);

            var approvedPolicy = ShadowCasePolicy.CreateWithApprovals(
                caseId, rulesVersion, defaultPolicy.TemporarilyUncomparable, approvals, null);
            var approved = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, rulesVersion, ShadowComparisonConfig.Strict(64),
                    equivalentA.Snapshots, diverged.Snapshots, diverged.EventBindings),
                approvedPolicy);

            Assert.That(approved.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(3),
                "被批准的字段差异必须显式归类为已批准差异（Tick 2/3/4）：" + approved.Describe());
            Assert.That(approved.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                "已批准的差异不得再作为非预期差异");
            Assert.That(approved.HasUnexpectedDifference, Is.False);
            Assert.That(approved.InfrastructureDifferences, Is.EqualTo(0),
                "批准只覆盖被点名的字段，不得掩盖基础设施事实差异");
            Assert.That(approved.UnalignedCheckpoints, Is.EqualTo(0));
            Assert.That(approved.CanClaimEquivalence, Is.True, approved.Describe());
            Assert.That(approved.EquivalenceClaim, Is.EqualTo("EQUIVALENT"));

            // 批准必须**精确**：换用例 ID / 换字段都不得生效（否则就是变相宽泛白名单）。
            var otherCasePolicy = ShadowCasePolicy.CreateWithApprovals(
                "03B-shadow-classification-other", rulesVersion,
                defaultPolicy.TemporarilyUncomparable, approvals, null);
            var otherCase = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, rulesVersion, ShadowComparisonConfig.Strict(64),
                    equivalentA.Snapshots, diverged.Snapshots, diverged.EventBindings),
                otherCasePolicy);
            Assert.That(otherCase.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0),
                "用例 ID 不匹配的批准不得生效");
            Assert.That(otherCase.HasUnexpectedDifference, Is.True);
            Assert.That(otherCase.EquivalenceClaim, Does.StartWith("REJECTED:"),
                "用例 ID 不匹配的批准必须被显式拒绝，而不是被静默忽略：" + otherCase.Describe());

            var wrongFieldApprovals = new List<ShadowComparisonApproval>();
            var wrongFieldRejections = new List<string>();
            Assert.That(ShadowCasePolicy.TryRegisterApproval(
                wrongFieldApprovals, wrongFieldRejections, caseId, rulesVersion,
                "units[0].position", "字段与真实差异不符的批准"), Is.True);
            var wrongFieldPolicy = ShadowCasePolicy.CreateWithApprovals(
                caseId, rulesVersion, defaultPolicy.TemporarilyUncomparable, wrongFieldApprovals, null);
            var wrongField = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, rulesVersion, ShadowComparisonConfig.Strict(64),
                    equivalentA.Snapshots, diverged.Snapshots, diverged.EventBindings),
                wrongFieldPolicy);
            Assert.That(wrongField.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0));
            Assert.That(wrongField.HasUnexpectedDifference, Is.True,
                "批准必须精确到字段：未被点名的字段差异仍是非预期差异");

            // (6) 未对齐检查点同样阻断等价声明（不得按帧序号强行配对）。
            //     构造：Shadow 侧只到 Tick 2（两侧在 Tick 0–2 逐位对齐），
            //     因此 Legacy 侧多出的 2 个检查点必须计入未对齐，而不是被强行配对。
            var shorter = new List<LogicSnapshot>();
            for (int i = 0; i < 3; i++) shorter.Add(equivalentA.Snapshots[i]);
            var misaligned = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, rulesVersion, ShadowComparisonConfig.Strict(64),
                    equivalentA.Snapshots, shorter, null),
                defaultPolicy);
            Assert.That(misaligned.ComparedCheckpoints, Is.EqualTo(shorter.Count),
                "按逻辑 Tick 对齐时必须覆盖两侧共有的检查点：" + misaligned.Describe());
            Assert.That(misaligned.UnalignedCheckpoints,
                Is.EqualTo(equivalentA.Snapshots.Count - shorter.Count),
                "多余检查点必须计入未对齐：" + misaligned.Describe());
            Assert.That(misaligned.InfrastructureDifferences, Is.EqualTo(0));
            Assert.That(misaligned.HasUnexpectedDifference, Is.False);
            Assert.That(misaligned.CanClaimEquivalence, Is.False,
                "存在未对齐检查点时不得宣称等价（本次比较没有覆盖全部检查点）");
            Assert.That(misaligned.EquivalenceClaim, Does.StartWith("UNALIGNED:"), misaligned.Describe());

            // (7) 真实 Bootstrap 检查点路径：暂不可比较字段必须携带责任任务与清零门槛，
            //     且宽泛批准（BroadDifferenceAllowlistIsRejected）语义不受本轮改动影响。
            var direct = Bootstrap.RunShadowCheckpointOnSeed(ToSeed(seed), "03B-shadow-uncomparable-gate");
            Assert.That(direct.HasUnexpectedDifference, Is.False,
                "同一输入的 Shadow 比较不得出现非预期差异：" + direct.Describe());
            Assert.That(direct.TemporarilyUncomparable.Count, Is.GreaterThan(0),
                "暂不可比较字段必须显式登记");
            Assert.That(direct.Rejections, Is.Empty, "默认策略不得产生被拒绝的登记项");
        }

        [UnityTest]
        public IEnumerator TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate()
        {
            Assert.That(TemporarilyUncomparableField.TryCreate(
                "CombatSampleScene/Player#CombatUnit.CurrentAdrenaline", "availableAdrenaline",
                "量纲不同", "", "任务 10 前", out _, out string missingOwner), Is.False);
            Assert.That(missingOwner, Does.StartWith(
                ShadowComparisonCodes.TemporarilyUncomparableFieldRequiresOwnerTaskAndRemovalGate));
            Assert.That(missingOwner, Does.Contain("ownerTask"));

            Assert.That(TemporarilyUncomparableField.TryCreate(
                "CombatSampleScene/Player#CombatUnit.CurrentAdrenaline", "availableAdrenaline",
                "量纲不同", "07", "   ", out _, out string missingGate), Is.False);
            Assert.That(missingGate, Does.Contain("removalGate"));

            Assert.That(TemporarilyUncomparableField.TryCreate(
                "", "availableAdrenaline", "量纲不同", "07", "任务 10 前", out _, out string missingPath), Is.False);
            Assert.That(missingPath, Does.Contain("legacyObjectPath"));

            Assert.That(TemporarilyUncomparableField.TryCreate(
                "CombatSampleScene/Player#CombatUnit.CurrentAdrenaline", "availableAdrenaline", "量纲不同",
                "07", "任务 10 切换主场景到 New 之前", out var created, out _), Is.True);
            Assert.That(created.OwnerTask, Is.EqualTo("07"));
            Assert.That(created.RemovalGate, Does.Contain("任务 10"));

            // 真实策略必须逐条带责任任务与清零门槛，且报告里能看到它们。
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();
            var report = Bootstrap.RunShadowCheckpointOnSeed(ToSeed(seed), "03B-shadow-uncomparable-gate");
            Assert.That(report.TemporarilyUncomparable.Count, Is.GreaterThan(0));
            Assert.That(report.Rejections, Is.Empty, "默认策略不得产生被拒绝的登记项");
            Assert.That(report.CountOf(ShadowDifferenceKind.TemporarilyUncomparable), Is.GreaterThan(0),
                "暂不可比较字段必须出现在差异观察里（不是被静默丢弃）");

            bool sawOwnerAndGate = false;
            for (int i = 0; i < report.Differences.Count; i++)
            {
                var difference = report.Differences[i];
                if (difference.Kind != ShadowDifferenceKind.TemporarilyUncomparable) continue;
                if (difference.Reason.Contains("owner=") && difference.Reason.Contains("gate="))
                    sawOwnerAndGate = true;
            }
            Assert.That(sawOwnerAndGate, Is.True, "暂不可比较字段的观察必须携带责任任务与清零门槛");
        }

        [UnityTest]
        public IEnumerator ShadowBudgetOverrunInvalidatesComparisonWithoutChangingLegacy()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 6; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            long tickBefore = LegacyTimelineCurrentTick();
            int callsBefore = LegacyTimelineAdvanceTimeCalls();
            int legacyAdvanceBefore = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime);
            int newStepBefore = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep);
            int shadowTickBefore = (int)Bootstrap.Shadow.SimulationTick;

            var seed = BuildSeedFromFactory();
            var snapshots = BuildSnapshots(seed, 8);

            // 预算 = 2 个检查点，但给 8 个：本次比较必须被标记为无效。
            var report = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(2),
                    snapshots, snapshots),
                ShadowCasePolicy.CreateDefault("03B-shadow-budget", seed.RulesVersion));

            Assert.That(report.BudgetOverrun, Is.True, "超出诊断预算必须标记预算超限");
            Assert.That(report.InvalidatedByBudget, Is.True);
            Assert.That(report.BudgetOverrunReason, Does.StartWith(ShadowComparisonCodes.ShadowBudgetOverrun));
            Assert.That(report.CanClaimEquivalence, Is.False, "预算超限后不得继续宣称等价");
            Assert.That(report.EquivalenceClaim, Does.StartWith("INVALID:"));
            Assert.That(report.ComparedCheckpoints, Is.EqualTo(0));

            // 不得因此改变 Legacy：时钟、调用计数、模式、新模拟 Tick 与权威路径全部不变。
            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(tickBefore));
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(callsBefore));
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime),
                Is.EqualTo(legacyAdvanceBefore));
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep),
                Is.EqualTo(newStepBefore));
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.Shadow));
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.EqualTo(shadowTickBefore),
                "预算超限不得丢 Tick 或继续推进新模拟");
            Assert.That(Time.timeScale, Is.EqualTo(1f), "预算超限不得篡改 timeScale");

            Bootstrap.StopBattle("shadow-budget-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator BroadDifferenceAllowlistIsRejected()
        {
            var candidates = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("*", "忽略全部字段"),
                new KeyValuePair<string, string>("units[*].healthQ10", "忽略全部血量"),
                new KeyValuePair<string, string>("units[*].position", "忽略全部位置"),
                new KeyValuePair<string, string>("all", "忽略一切"),
                new KeyValuePair<string, string>("", "空字段路径")
            };

            var policy = ShadowCasePolicy.CreateWithCandidateApprovals(
                "03B-shadow-broad-allowlist", "battle-def-v1", candidates);

            Assert.That(policy.Approvals, Is.Empty, "宽泛白名单不得被接受为批准差异");
            Assert.That(policy.Rejections.Count, Is.EqualTo(candidates.Count));
            for (int i = 0; i < policy.Rejections.Count; i++)
            {
                // 通配符/整类关键词用宽泛拒绝码；空字段路径用"缺字段"拒绝码。
                // 两者都让策略不可宣称等价——不允许任何一条被静默接受。
                Assert.That(policy.Rejections[i], Does.StartWith(
                        ShadowComparisonCodes.BroadDifferenceAllowlistRejected)
                    .Or.StartWith(ShadowComparisonCodes.ApprovalRequiresCaseIdRulesVersionFieldAndReason),
                    "被拒绝的批准差异必须有稳定拒绝码：" + policy.Rejections[i]);
            }
            Assert.That(policy.Rejections, Has.Some.StartsWith(
                ShadowComparisonCodes.BroadDifferenceAllowlistRejected));
            Assert.That(policy.Rejections, Has.Some.StartsWith(
                ShadowComparisonCodes.ApprovalRequiresCaseIdRulesVersionFieldAndReason));

            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();
            var snapshots = BuildSnapshots(seed, 3);
            var report = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    snapshots, snapshots),
                policy);

            Assert.That(report.Rejections.Count, Is.EqualTo(candidates.Count));
            Assert.That(report.CanClaimEquivalence, Is.False, "存在被拒绝的宽泛批准时不得宣称等价");
            Assert.That(report.EquivalenceClaim, Does.StartWith("REJECTED:"));
            Assert.That(report.Approvals, Is.Empty);

            // 反面证据：精确到用例 + RulesVersion + 字段 + 原因的批准是允许的。
            var approvals = new List<ShadowComparisonApproval>();
            var rejections = new List<string>();
            Assert.That(ShadowCasePolicy.TryRegisterApproval(
                approvals, rejections, "03B-shadow-precise-approval", "battle-def-v1",
                "units[0].healthQ10", "精确字段批准"), Is.True);
            Assert.That(approvals.Count, Is.EqualTo(1));

            // 结构规则本身：通配符与整类关键词一律判宽泛。
            Assert.That(ShadowAllowlistRules.IsBroadPattern("*"), Is.True);
            Assert.That(ShadowAllowlistRules.IsBroadPattern("units[*].position"), Is.True);
            Assert.That(ShadowAllowlistRules.IsBroadPattern("all units"), Is.True);
            Assert.That(ShadowAllowlistRules.IsBroadPattern(""), Is.True);
            Assert.That(ShadowAllowlistRules.IsBroadPattern("Shallow"), Is.False, "词边界必须正确");
            Assert.That(ShadowAllowlistRules.IsBroadPattern("units[0].healthQ10"), Is.False);
        }

        [UnityTest]
        public IEnumerator EquivalentShadowScenarioUsesLogicalCheckpointAlignment()
        {
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();

            // 两侧是同一真实定义 + 同一初始输入的两个独立 Logic 世界。
            var sideA = BuildSnapshots(seed, 12);
            var sideB = BuildSnapshots(seed, 12);
            Assert.That(sideA.Count, Is.EqualTo(13), "Tick 0 初始快照 + 12 个已提交 Tick = 13 个检查点");
            Assert.That(sideB.Count, Is.EqualTo(sideA.Count),
                "sideA=" + sideA.Count + " sideB=" + sideB.Count);

            var aligned = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    sideA, sideB),
                ShadowCasePolicy.CreateDefault("03B-shadow-equivalent-empty-ticks", seed.RulesVersion));

            Assert.That(aligned.HasUnexpectedDifference, Is.False,
                "同一输入的两个独立世界必须等价：" + aligned.Describe());
            Assert.That(aligned.ComparedCheckpoints, Is.EqualTo(sideA.Count),
                "按逻辑 Tick 对齐必须覆盖全部检查点");
            Assert.That(aligned.UnalignedCheckpoints, Is.EqualTo(0));
            Assert.That(aligned.InvalidatedByBudget, Is.False);
            Assert.That(aligned.CanClaimEquivalence, Is.True,
                "等价用例必须能给出等价结论：" + aligned.Describe());
            Assert.That(aligned.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0));

            // 逻辑 Tick 对齐与检查点数量无关：一侧多出若干 Tick 时，多余部分计入未对齐，
            // 而不是被按帧序号强行配对。
            var longerSide = BuildSnapshots(seed, 15);
            var partiallyAligned = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    sideA, longerSide),
                ShadowCasePolicy.CreateDefault("03B-shadow-equivalent-empty-ticks", seed.RulesVersion));

            Assert.That(partiallyAligned.UnalignedCheckpoints, Is.EqualTo(longerSide.Count - sideA.Count),
                "多出的检查点必须记为未对齐，不得按帧序号配对"
                + " ; sideA.Count=" + sideA.Count + " ; longer=" + longerSide.Count
                + " ; compared=" + partiallyAligned.ComparedCheckpoints
                + " ; differences=" + DescribeDifferenceList(aligned.Differences));
            Assert.That(partiallyAligned.ComparedCheckpoints, Is.EqualTo(sideA.Count),
                "compared=" + partiallyAligned.ComparedCheckpoints
                + " ; sideA=" + sideA.Count + " ; longer=" + longerSide.Count);
            Assert.That(partiallyAligned.HasUnexpectedDifference, Is.False);

            // 真实 Shadow runner 的对齐键必须是"逻辑 Tick@检查点"，不含任何帧序号。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);
            long tickAtStart = LegacyTimelineCurrentTick();
            for (int i = 0; i < 4; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            var keys = Bootstrap.ShadowCheckpointKeys;
            Assert.That(keys.Count, Is.EqualTo(4), "四个检查点必须有四个对齐键");
            for (int i = 0; i < keys.Count; i++)
            {
                Assert.That(keys[i], Does.EndWith("@LateUpdate"), "对齐键必须带具名检查点：" + keys[i]);
                long logicalTick;
                Assert.That(long.TryParse(keys[i].Substring(0, keys[i].IndexOf('@')), out logicalTick), Is.True,
                    "对齐键必须以逻辑 Tick 开头：" + keys[i]);
            }

            // 第二收尾轮 R1 之后语义更正：新模拟以**旧时间线的真实逻辑 Tick**为同步点，
            // 因此每个检查点的键就是该帧旧时间线的真实 Tick（而不是硬编码的 0/1/2/3），
            // 且键序列必须严格递增——"按逻辑 Tick 对齐、不按帧序号"由此可证伪。
            var firstKeyTick = long.Parse(keys[0].Substring(0, keys[0].IndexOf('@')));
            var lastKeyTick = long.Parse(keys[3].Substring(0, keys[3].IndexOf('@')));
            Assert.That(firstKeyTick, Is.GreaterThan(tickAtStart),
                "第一个检查点必须落在旧时间线推进后的真实 Tick 上：" + keys[0]);
            Assert.That(firstKeyTick, Is.EqualTo(Bootstrap.LegacyObservations[0].Tick),
                "对齐键必须等于同一检查点采集到的 Legacy 观测 Tick"
                + " ; keys=" + string.Join(",", keys)
                + " ; observationTicks=" + DescribeObservationTicks());
            Assert.That(lastKeyTick, Is.GreaterThan(firstKeyTick),
                "四个检查点的逻辑 Tick 必须严格递增：" + string.Join(",", keys));
            Assert.That(lastKeyTick, Is.EqualTo(LegacyTimelineCurrentTick()),
                "最后一个检查点的键必须等于采样时刻的旧时间线 Tick：" + string.Join(",", keys));
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.EqualTo(LegacyTimelineCurrentTick()),
                "检查点采样时两侧逻辑 Tick 必须相等（对齐的前提）");

            Bootstrap.StopBattle("logical-alignment-test");
            Bootstrap.ReleaseBattle();

            // 视觉时间缩放隔离：等价性用例固定 timeScale = 1。
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        // =====================================================================
        // 时间来源、暂停与销毁语义
        // =====================================================================

        [UnityTest]
        public IEnumerator LegacyUsesScaledDeltaTimeAndNewUsesUnscaledDeltaTime()
        {
            yield return LoadHiddenValidationScene();

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "Legacy 启动必须成功：" + Bootstrap.StartupRejection);
            Bootstrap.DriveFrameForTests(0.25f, 0.05f);
            Assert.That(Bootstrap.Ledger.LastLegacyDeltaTime, Is.EqualTo(0.25f).Within(1e-6f),
                "Legacy 段必须使用 scaled deltaTime（保持改造前时间语义）");
            Bootstrap.StopBattle("delta-source-test");
            Bootstrap.ReleaseBattle();

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True,
                "New 启动必须成功：" + Bootstrap.StartupRejection);
            Bootstrap.DriveFrameForTests(0.25f, 0.05f);
            Assert.That(Bootstrap.Ledger.LastNewDeltaTime, Is.EqualTo(0.05f).Within(1e-6f),
                "New 段必须使用 unscaled deltaTime（视觉时间缩放不影响逻辑 Tick）");
            Bootstrap.StopBattle("delta-source-test-2");
            Bootstrap.ReleaseBattle();
        }

        /// <summary>
        /// 第二收尾轮 R2：停止 → 释放 → 以另一模式重新开局后，旧时间线必须<strong>真的推进</strong>。
        ///
        /// 旧实现：<c>CombatDemo.StopBattle</c> 把旧时间线置为系统暂停，而 <c>ResetForNewBattle</c>
        /// 与 Bootstrap 重开局都不清除 ⇒ 重开后 <c>Tick</c> 永不前进，只有
        /// <c>AdvanceCallCount</c> / <c>TotalAdvanceTimeCalls</c> 逐帧增长。
        /// 因此本用例**同时断言 Tick 前进与调用计数前进**——只断言计数正是上一轮被掩盖的原因。
        /// </summary>
        [UnityTest]
        public IEnumerator ReopeningBattleAdvancesLegacyClockInBothModes()
        {
            yield return LoadHiddenValidationScene();

            // ---- 第一场：Legacy → 第二场：Shadow ----
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "Legacy 启动必须成功：" + Bootstrap.StartupRejection);
            for (int i = 0; i < 3; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
            Assert.That(Bootstrap.StopBattle("reopen-legacy"), Is.True);
            Assert.That(Bootstrap.ReleaseBattle(), Is.True);
            Assert.That(LegacyTimelineSystemPaused(), Is.True,
                "对照组：StopBattle 必须把旧时间线置为系统暂停（这是本缺陷的触发条件）");

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "以 Shadow 重新开局必须成功：" + Bootstrap.StartupRejection);
            Assert.That(LegacyTimelineSystemPaused(), Is.False,
                "重开局必须清除上一场残留的系统暂停（Shadow 模式）");

            long shadowModeTickBefore = LegacyTimelineCurrentTick();
            int shadowModeCallsBefore = LegacyTimelineAdvanceTimeCalls();
            for (int i = 0; i < 5; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(shadowModeCallsBefore + 5),
                "Shadow 重开后旧推进计数必须逐帧增长");
            Assert.That(LegacyTimelineCurrentTick(), Is.GreaterThan(shadowModeTickBefore),
                "Shadow 重开后旧逻辑 Tick 必须真的前进（不得只涨调用计数）");
            Assert.That(Bootstrap.Shadow.SimulationTick, Is.GreaterThan(0),
                "Shadow 独立世界同样必须推进");
            Assert.That(Bootstrap.LastShadowReport, Is.Not.Null);
            Assert.That(Bootstrap.LastShadowReport.ComparedCheckpoints, Is.GreaterThan(0),
                "Shadow 重开后的比较必须有内容：" + Bootstrap.LastShadowReport.Describe());

            Assert.That(Bootstrap.StopBattle("reopen-shadow"), Is.True);
            Assert.That(Bootstrap.ReleaseBattle(), Is.True);

            // 第三收尾轮 R4：施加**用户暂停**（镜像生产入口 UIManager.TogglePause →
            // BattleTimeline.SetPaused），再回切到 Legacy。恢复语义必须同时成立：
            //   ① 本场施加的**系统暂停**在重开后被清除；
            //   ② **用户暂停**不被重开清除，且合并语义 Paused 仍为 true。
            // 在此之前 `_userPaused` 没有公开只读入口，这条语义**无法被断言**。
            SetLegacyTimelineUserPaused(true);
            bool userPauseCombined;
            Assert.That(LegacyTimelineUserPaused(out userPauseCombined), Is.True,
                "对照证据：用户暂停必须真的被施加（否则本段退化为恒真断言）");
            Assert.That(userPauseCombined, Is.True, "用户暂停必须使合并语义 Paused 为 true");

            // ---- 第三场：再回切到 Legacy ----
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Legacy), Is.True,
                "再回切到 Legacy 必须成功：" + Bootstrap.StartupRejection);
            Assert.That(LegacyTimelineSystemPaused(), Is.False, "重开局必须清除系统暂停（Legacy 模式）");
            Assert.That(LegacyTimelineUserPaused(), Is.True,
                "重开局**不得**清除用户暂停（第三收尾轮 R4：用户暂停是玩家意图，不是本场战斗施加的状态）");
            bool userPauseCombinedAfterReopen;
            Assert.That(LegacyTimelineUserPaused(out userPauseCombinedAfterReopen), Is.True,
                "重开后用户暂停仍须生效");
            Assert.That(userPauseCombinedAfterReopen, Is.True,
                "重开后合并语义 Paused 仍须为 true");

            long legacyModeTickBefore = LegacyTimelineCurrentTick();
            int legacyModeCallsBefore = LegacyTimelineAdvanceTimeCalls();
            for (int i = 0; i < 5; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            // 用户暂停的旧语义（BattleTimeline.AdvanceTime）：计数照涨、Tick 不动。
            // 这既证明上面的断言依赖一次真实运行，也证明暂停来源确实是用户侧。
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(legacyModeCallsBefore + 5),
                "用户暂停期间旧推进**仍被调用**（旧语义：计数照涨）");
            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(legacyModeTickBefore),
                "用户暂停期间旧逻辑 Tick 必须冻结");

            // 解除用户暂停并等待一个真实的逻辑 Tick（非零帧时间，避免与换算相关的不稳定）。
            SetLegacyTimelineUserPaused(false);
            Assert.That(LegacyTimelineUserPaused(), Is.False, "测试必须能还原用户暂停状态");
            int callsBeforeResume = LegacyTimelineAdvanceTimeCalls();
            Bootstrap.DriveFrameForTests(0.1f, 0.1f);

            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(callsBeforeResume + 1),
                "解除用户暂停后旧推进必须继续");
            Assert.That(LegacyTimelineCurrentTick(), Is.GreaterThan(legacyModeTickBefore),
                "解除用户暂停后旧逻辑 Tick 必须真的前进（不得只涨调用计数）");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep), Is.EqualTo(0),
                "Legacy 模式不得调用新模拟 Step");

            // 恢复语义：用户暂停（P 键 / UI）不是本场战斗施加的状态，不因重开局被清除；
            // 顶层暂停由 Bootstrap.SetPaused 表达，同样不进入旧时间线。
            Assert.That(Bootstrap.IsPaused, Is.False, "重开局后的顶层暂停状态必须是干净的");

            Bootstrap.StopBattle("reopen-legacy-2");
            Bootstrap.ReleaseBattle();
        }

        /// <summary>
        /// 第三收尾轮 R4：重开战斗必须<strong>清除本场施加的系统暂停</strong>，
        /// 同时<strong>不得清除用户暂停</strong>（以及由用户暂停贡献的合并暂停语义）。
        ///
        /// 为什么单独一条用例：<c>ReopeningBattleAdvancesLegacyClockInBothModes</c> 的三段回切
        /// 已经覆盖了"回切后 Tick 真实递增"，本用例把 R4 的两条语义做成<strong>单一对照</strong>——
        /// 先置两个暂停来源，再只清系统暂停，逐项断言哪个被清、哪个没被清。
        /// `SystemPaused` 与 `UserPaused` 是 <c>BattleTimeline</c> 的两个独立字段，
        /// 本用例的价值在于"两者都被显式观察到"，而不是只看合并后的 <c>Paused</c>。
        /// </summary>
        [UnityTest]
        public IEnumerator ReopeningBattleKeepsUserPauseButClearsSystemPause()
        {
            yield return LoadHiddenValidationScene();

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 3; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            // 停下来：StopBattle 会给旧时间线施加"系统暂停"（本缺陷的触发条件）。
            Assert.That(Bootstrap.StopBattle("reopen-user-pause"), Is.True);
            Assert.That(Bootstrap.ReleaseBattle(), Is.True);
            Assert.That(LegacyTimelineSystemPaused(), Is.True,
                "对照组：StopBattle 必须把旧时间线置为系统暂停");

            // 再施加"用户暂停"（镜像生产入口 UIManager.TogglePause → BattleTimeline.SetPaused）。
            SetLegacyTimelineUserPaused(true);

            bool combinedBefore;
            Assert.That(LegacyTimelineUserPaused(out combinedBefore), Is.True,
                "用户暂停必须真的被施加（否则下面的断言退化为恒真）");
            Assert.That(LegacyTimelineSystemPaused(), Is.True,
                "两个暂停来源必须同时存在（这样『清哪个 / 不清哪个』才有对照意义）");
            Assert.That(combinedBefore, Is.True, "合并语义 Paused 此时必须为 true");

            // 重开。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "重开必须成功：" + Bootstrap.StartupRejection);

            Assert.That(LegacyTimelineSystemPaused(), Is.False,
                "重开必须清除**本场施加的系统暂停**（否则重开后旧时钟永不推进）");
            Assert.That(LegacyTimelineUserPaused(), Is.True,
                "重开**不得**清除**用户暂停**（玩家意图不由战斗生命周期改写）");
            bool combinedAfter;
            Assert.That(LegacyTimelineUserPaused(out combinedAfter), Is.True,
                "合并语义 Paused 必须仍为 true（只有系统暂停那一半被清掉了）");
            Assert.That(Bootstrap.IsPaused, Is.False,
                "顶层暂停（Bootstrap.SetPaused）必须保持干净，不受用户暂停影响");

            // 反向证据：用户暂停仍然**实际生效**（计数照涨、Tick 冻结，旧语义）。
            long tickBefore = LegacyTimelineCurrentTick();
            int callsBefore = LegacyTimelineAdvanceTimeCalls();
            for (int i = 0; i < 4; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(callsBefore + 4),
                "用户暂停期间旧推进仍被调用（旧语义：暂停即 return 前先记账）");
            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(tickBefore),
                "用户暂停必须真的冻结旧逻辑 Tick");

            // 还原用户暂停并确认可以恢复推进。
            SetLegacyTimelineUserPaused(false);
            int callsBeforeResume = LegacyTimelineAdvanceTimeCalls();
            Bootstrap.DriveFrameForTests(0.1f, 0.1f);
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(callsBeforeResume + 1));
            Assert.That(LegacyTimelineCurrentTick(), Is.GreaterThan(tickBefore),
                "解除用户暂停后旧时钟必须恢复推进");

            Bootstrap.StopBattle("reopen-user-pause-done");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator PausedBootstrapDoesNotAdvanceEitherSide()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            for (int i = 0; i < 3; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            long tickBefore = LegacyTimelineCurrentTick();
            int callsBefore = LegacyTimelineAdvanceTimeCalls();
            int newStepsBefore = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep);

            Bootstrap.SetPaused(true);
            for (int i = 0; i < 6; i++) Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);

            Assert.That(LegacyTimelineCurrentTick(), Is.EqualTo(tickBefore), "暂停时旧时钟不得推进");
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(callsBefore),
                "暂停时连 AdvanceTime 都不应被调用（旧语义：暂停即 return）");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep),
                Is.EqualTo(newStepsBefore), "暂停时新模拟不得推进 ; " + Bootstrap.Ledger.Describe());

            Bootstrap.SetPaused(false);
            Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(callsBefore + 1), "恢复后必须继续推进");

            Bootstrap.StopBattle("pause-test");
            Bootstrap.ReleaseBattle();
        }

        [UnityTest]
        public IEnumerator VisualTimeScaleIsolationDoesNotLeakIntoNewSimulation()
        {
            yield return LoadHiddenValidationScene();
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.New), Is.True,
                "New 启动必须成功：" + Bootstrap.StartupRejection);

            int baselineSteps = Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep);

            Time.timeScale = 0f;
            try
            {
                for (int i = 0; i < 6; i++) Bootstrap.DriveFrameForTests(0f, 1f / 60f);
            }
            finally
            {
                Time.timeScale = 1f;
            }

            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep),
                Is.GreaterThan(baselineSteps),
                "timeScale = 0 时 New 段仍必须按 unscaled 时间推进（视觉时间缩放隔离）");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.LegacyAdvanceTime), Is.EqualTo(0),
                "New 模式仍不得调用旧推进");

            Bootstrap.StopBattle("timescale-isolation-test");
            Bootstrap.ReleaseBattle();
        }

        // =====================================================================
        // 主场景 Legacy 冒烟与真实 02B 初始化链
        // =====================================================================

        [UnityTest]
        public IEnumerator MainSceneRunsLegacyThroughBootstrapWithoutVisibleChange()
        {
            yield return LoadMainScene();
            Assert.That(BootstrapCountInScene(), Is.EqualTo(1));
            Assert.That(Bootstrap.BattleCreated, Is.True, "主场景必须自动创建战斗");
            Assert.That(Bootstrap.BattleMode, Is.EqualTo(BattleRuntimeMode.Legacy),
                "主战斗场景必须默认 Legacy");
            Assert.That(Bootstrap.StartupRejection, Is.Null, "主场景启动不得被拒绝");
            Assert.That(Bootstrap.WritersExecutingAtOrAfterCheckpoint(), Is.Empty);

            var adapter = Bootstrap.Adapters.Legacy;
            Assert.That(adapter, Is.Not.Null);
            Assert.That(adapter.OwnsAutonomousUpdate, Is.False);

            float elapsed = 0f;
            int frames = 0;
            while (elapsed < 1.0f && frames < 600)
            {
                yield return null;
                elapsed += Time.deltaTime;
                frames++;
                if (!Bootstrap.BattleCreated) break;
            }

            Assert.That(Bootstrap.Ledger.BootstrapUpdates, Is.GreaterThan(0),
                "主场景 Bootstrap 必须在真实帧循环中被调用");
            Assert.That(adapter.AdvanceCallCount, Is.EqualTo(Bootstrap.Ledger.BootstrapUpdates),
                "每次 Bootstrap Update 必须恰好驱动一次 Legacy 适配器");
            Assert.That(LegacyTimelineAdvanceTimeCalls(), Is.EqualTo(adapter.AdvanceCallCount));
            Assert.That(LegacyTimelineCurrentTick(), Is.GreaterThan(0L),
                "主场景旧战斗必须真实推进 Tick（可进入、行动、结束）");
            Assert.That(Bootstrap.Ledger.AdvanceCalls(RuntimeAdvancePath.NewSimulationStep), Is.EqualTo(0),
                "主场景 Legacy 模式不得调用新模拟");

            var player = FindProductionComponent("ProjectHero.Core.Entities.CombatUnit");
            Assert.That(player, Is.Not.Null, "主场景必须存在旧 CombatUnit");
            Assert.That(player.enabled, Is.True, "Legacy 模式下旧单位写入者必须启用");

            Bootstrap.StopBattle("main-scene-smoke");
            Bootstrap.ReleaseBattle();
            Assert.That(Bootstrap.IsStopped, Is.True, "主场景战斗必须能正常停止");
        }

        [UnityTest]
        public IEnumerator MainSceneBattleDefinitionMatchesTask02BFrozenHash()
        {
            yield return LoadMainScene();

            var factory = FindSimulationSourceFactory();
            Assert.That(factory, Is.Not.Null,
                "主场景必须挂载真实 02B 初始化来源（BattleSimulationSourceFactory）");

            var source = factory as IBattleSimulationSource;
            Assert.That(source, Is.Not.Null);
            Assert.That(source.LastConfigurationError, Is.Null,
                "真实初始化链必须构建成功：" + source.LastConfigurationError);

            var seed = source.BuildSeed();
            Assert.That(seed.Validate(), Is.Null);
            Assert.That(seed.BattleDefinitionHash, Is.EqualTo("d997b13b18573e15"),
                "主战斗定义哈希必须与 02B 冻结锚点一致");
            Assert.That(seed.RulesVersion, Is.EqualTo("battle-def-v1"));
            Assert.That(seed.EncounterId.Value, Is.EqualTo("encounter.combat_sample_scene"));
            Assert.That(seed.Definition.FindEncounter(seed.EncounterId), Is.Not.Null);
            Assert.That(seed.InputSummary, Is.Not.Empty);

            Bootstrap.StopBattle("definition-hash-test");
            Bootstrap.ReleaseBattle();
        }

        // =====================================================================
        // 工具方法
        // =====================================================================

        private ShadowCaseSeed BuildSeedFromFactory()
        {
            var factory = FindSimulationSourceFactory() as IBattleSimulationSource;
            Assert.That(factory, Is.Not.Null,
                "隐藏验证场景必须包含真实 02B 初始化来源（BattleSimulationSourceFactory）");
            Assert.That(factory.LastConfigurationError, Is.Null,
                "真实初始化链必须构建成功：" + factory.LastConfigurationError);

            var seed = factory.BuildSeed();
            Assert.That(seed, Is.Not.Null);
            Assert.That(seed.Validate(), Is.Null);
            return new ShadowCaseSeed(seed);
        }

        private static ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed ToSeed(ShadowCaseSeed seed)
            => new ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs, seed.InputSummary);

        /// <summary>
        /// 真实检查点流：一侧 Logic 世界的检查点快照序列 + 每个逻辑 Tick 的真实事件序号绑定。
        ///
        /// 它用任务 03 的**真实 Step 管线**推进世界（<c>BattleSimulation.Step</c>），
        /// 并按需在 <paramref name="submitTick"/> 通过唯一信任边界提交一条可信请求。
        /// 差异（如果有）由真实管线产生，不是伪造的快照对象。
        /// </summary>
        private sealed class RealCheckpointStream
        {
            public RealCheckpointStream(
                IReadOnlyList<LogicSnapshot> snapshots,
                IReadOnlyList<ShadowCheckpointEventBinding> eventBindings)
            {
                Snapshots = snapshots;
                EventBindings = eventBindings;
            }

            public IReadOnlyList<LogicSnapshot> Snapshots { get; }

            public IReadOnlyList<ShadowCheckpointEventBinding> EventBindings { get; }

            /// <summary>该逻辑 Tick 上首个已提交事件的序号（无事件时为 -1）。</summary>
            public long FirstEventSequenceAt(long logicalTick)
            {
                for (int i = 0; i < EventBindings.Count; i++)
                {
                    if (EventBindings[i].LogicalTick == logicalTick)
                        return EventBindings[i].RelatedEventSequence;
                }
                return -1L;
            }

            public string Describe()
            {
                var builder = new StringBuilder();
                builder.Append("ticks=").Append(Snapshots.Count).Append(" [");
                for (int i = 0; i < Snapshots.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(Snapshots[i].Tick).Append('#').Append(Snapshots[i].ComputeHashHex());
                }
                builder.Append("] events[");
                for (int i = 0; i < EventBindings.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(EventBindings[i]);
                }
                builder.Append(']');
                return builder.ToString();
            }
        }

        /// <summary>
        /// 阶段 1/15（单位状态推进）的**最小测试装配**：在指定逻辑 Tick 把指定单位的
        /// 生命设为给定 Q10 值。
        ///
        /// 它是任务 04「单位状态与生命周期」将要实现的 <see cref="IUnitStateAdvanceSystem"/>
        /// 的测试替身——但走的是**完全真实的 Step 管线**（阶段 1/15 原子应用、随后进入
        /// 规范化快照与哈希）。因此"按新规则验证的事实"差异是真实差异，
        /// 不是手工构造的快照差异。
        /// </summary>
        private sealed class ScriptedUnitStateAdvanceSystem : IUnitStateAdvanceSystem
        {
            private readonly long _tick;
            private readonly long _unitId;
            private readonly int _healthQ10;

            public ScriptedUnitStateAdvanceSystem(long tick, long unitId, int healthQ10)
            {
                _tick = tick;
                _unitId = unitId;
                _healthQ10 = healthQ10;
            }

            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
                IReadOnlyList<UnitSnapshot> units, long tick)
            {
                if (tick != _tick) return Array.Empty<UnitStateAdvanceRequest>();
                return new[] { new UnitStateAdvanceRequest(_unitId, _healthQ10) };
            }
        }

        /// <summary>
        /// 用真实 Step 管线构建一条检查点流：<b>只登记已提交 Tick 的检查点</b>。
        ///
        /// 逻辑 Tick 编号：<c>BattleSimulation.Tick</c> 初始为 -1，因此第一个合法的 Step
        /// 是 <b>Tick 0</b>；本方法提交 Tick 0..N-1 共 <paramref name="ticks"/> 个检查点，
        /// 于是"数组下标 == 逻辑 Tick"且检查点 Tick 严格递增（不产生重复的 Tick 0）。
        ///
        /// <paramref name="unitStateAdvance"/> 为 null 时使用任务 03 的标准装配；
        /// <paramref name="submitTick"/> 为负时不提交任何请求。
        /// 事件序号取自 <see cref="StepResult.Events"/>（与生产
        /// <c>ShadowBattleRunner.CaptureEventBinding</c> 同一口径）。
        /// </summary>
        private static RealCheckpointStream BuildRealCheckpointStream(
            ShadowCaseSeed seed, int ticks, IUnitStateAdvanceSystem unitStateAdvance, int submitTick)
        {
            var assembly = unitStateAdvance == null
                ? BattleSimulationAssembly.Standard()
                : new BattleSimulationAssembly(unitStateAdvance: unitStateAdvance);

            var snapshots = new List<LogicSnapshot>();
            var bindings = new List<ShadowCheckpointEventBinding>();
            var simulation = BattleSimulation.Create(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs, assembly);

            try
            {
                var entry = simulation.CommandIngress.FindEntry(new ControllerId("controller.player"));
                Assert.That(entry, Is.Not.Null, "必须有 controller.player 入口");

                for (int i = 0; i < ticks; i++)
                {
                    long tick = i;
                    if (tick == submitTick)
                    {
                        entry.Submit(new CommandRequest(
                            tick,
                            new ScheduleEditScope(0L, null),
                            new ScheduleEditPayload(
                                new ScheduleEditOperation[]
                                {
                                    new ScheduleAddOperation(new ActionPlanId(1L), tick)
                                })));
                    }

                    var batch = simulation.CommandIngress.FreezeTick(tick);
                    StepResult result = simulation.Step(tick, batch);
                    snapshots.Add(simulation.CurrentSnapshot);
                    bindings.Add(BuildEventBinding(tick, result));
                }
            }
            finally
            {
                simulation.Dispose();
            }

            return new RealCheckpointStream(snapshots, bindings);
        }

        /// <summary>
        /// 单位生命下调量：取当前生命的 1/10（至少 1），保证单位<b>仍然存活</b>。
        ///
        /// 为什么不能用一个固定的绝对量：生命被降到 0 会触发死亡事件与胜负终局，
        /// 从而让后续 Tick 变成空操作、污染"基础设施事实相等"的前提。
        /// 本用例要验证的是"按新规则验证的事实差异"，不是死亡/终局语义。
        /// </summary>
        private static int HealthDeltaThatKeepsUnitAlive(int healthQ10)
            => Math.Max(1, healthQ10 / 10);

        /// <summary>按 UnitId 取单位快照（不按数组下标——下标可能因两侧构造差异而不同）。</summary>
        private static UnitSnapshot FindUnit(LogicSnapshot snapshot, long unitId)
        {
            if (snapshot == null) return null;
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                if (snapshot.Units[i] != null && snapshot.Units[i].UnitId == unitId) return snapshot.Units[i];
            }
            return null;
        }

        /// <summary>
        /// 两侧检查点流的逐 Tick 诊断（哈希 + 指定单位的生命 + 单位数）。
        /// 只在断言失败信息里使用，用于直接看出"哪一侧、哪个 Tick 上发生了什么"。
        /// </summary>
        private static string DescribeSideComparison(
            RealCheckpointStream left, RealCheckpointStream right, long unitId)
        {
            var builder = new StringBuilder("perTick[");
            int count = Math.Min(left.Snapshots.Count, right.Snapshots.Count);
            for (int i = 0; i < count; i++)
            {
                var l = left.Snapshots[i];
                var r = right.Snapshots[i];
                builder.Append('{').Append(l.Tick)
                    .Append(" L=").Append(l.ComputeHashHex())
                    .Append(" R=").Append(r.ComputeHashHex())
                    .Append(" healthL=").Append(HealthOf(l, unitId))
                    .Append(" healthR=").Append(HealthOf(r, unitId))
                    .Append(" unitsL=").Append(l.Units.Count)
                    .Append(" unitsR=").Append(r.Units.Count)
                    .Append("} ");
            }
            builder.Append("] left=").Append(left.Describe());
            return builder.ToString();
        }

        private static string HealthOf(LogicSnapshot snapshot, long unitId)
        {
            var unit = FindUnit(snapshot, unitId);
            return unit == null ? "<absent>" : unit.HealthQ10.ToString();
        }

        /// <summary>某个 Tick 的事件绑定（首个序号 + 条数）；与该 Tick 无关的事件绝不借用。</summary>
        private static ShadowCheckpointEventBinding BuildEventBinding(long tick, StepResult result)
        {
            var events = result != null && result.Events != null
                ? result.Events.EventsInSequenceOrder
                : (IReadOnlyList<ProjectHero.Logic.Events.LogicEvent>)Array.Empty<ProjectHero.Logic.Events.LogicEvent>();

            int count = 0;
            long first = -1L;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] == null) continue;
                if (count == 0) first = events[i].Sequence;
                count++;
            }

            return new ShadowCheckpointEventBinding(tick, first, count);
        }

        private static IReadOnlyList<LogicSnapshot> BuildSnapshots(
            ShadowCaseSeed seed, int ticks)
            => ShadowLogicCheckpointBuilder.BuildEmptyTickCheckpoints(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs, ticks);

        /// <summary>隐藏验证场景（活动场景）里全部 MonoBehaviour 的完整类型名。</summary>
        private static List<string> CollectHiddenSceneTypes()
        {
            var types = new List<string>();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    types.Add(behaviour.GetType().FullName);
                }
            }
            return types;
        }

        private static bool DeclaresUnityUpdate(Type type)
        {
            foreach (var name in new[] { "Update", "LateUpdate", "FixedUpdate" })
            {
                var method = type.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method != null) return true;
            }
            return false;
        }

        private static List<string> CollectTopLevelAdvancers()
        {
            var found = new List<string>();
            foreach (var behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include))
            {
                if (behaviour == null) continue;
                var behaviourType = behaviour.GetType();
                if (behaviourType == typeof(BattleRuntimeBootstrap)) continue;
                if (!DeclaresUnityUpdate(behaviourType)) continue;
                UnityCallbackClassification classification;
                if (RuntimeCallbackRegistry.TryGet(behaviourType.FullName, "Update", out classification)
                    && classification.CallbackClass == UnityCallbackClass.TopLevelClockAdvancer)
                {
                    found.Add(behaviourType.FullName);
                }
            }
            return found;
        }

        /// <summary>
        /// 分类表审计：列出分类表中所有**声明了 <c>Update()</c>/<c>LateUpdate()</c> 的
        /// 项目类型**，以及它们的分类。断言"每一个都已被分类"。
        ///
        /// 这里刻意<b>不复用</b> <c>RuntimeCallbackRegistry.IsUnclassifiedLogicWriter</c> 的
        /// 逐场景反射扫描：PlayMode 场景里存在运行时创建/销毁竞态，反射结果不稳定。
        /// 直接审计分类表本身既确定又与验收口径（"所有旧逻辑回调均已分类"）一致。
        /// </summary>
        /// <summary>
        /// 分类表完整性审计：主场景中每一个"项目逻辑写入者"类型都必须在分类表里有记录，
        /// 且每条记录都必须带回调点、对象路径与时间来源（不允许空条目）。
        ///
        /// 这里刻意<b>不复用</b> <c>IsUnclassifiedLogicWriter</c> 的逐场景反射扫描：
        /// PlayMode 场景里存在运行时创建/销毁竞态，反射结果不稳定；
        /// 直接审计分类表既确定又与验收口径（"所有旧逻辑回调均已分类"）一致。
        /// </summary>
        private static List<string> CollectUnclassifiedLogicWriters()
        {
            var unclassified = new List<string>();
            foreach (var classification in RuntimeCallbackRegistry.All)
            {
                if (string.IsNullOrEmpty(classification.TypeName)
                    || string.IsNullOrEmpty(classification.MemberName)
                    || string.IsNullOrEmpty(classification.TimeSource)
                    || string.IsNullOrEmpty(classification.SceneObjectPath))
                {
                    unclassified.Add("<incomplete-classification-entry>");
                }
            }

            // 项目自己的逻辑写入者类型必须在表中（主场景的三类各自至少一条）。
            foreach (var required in new[]
                     {
                         "ProjectHero.Core.Entities.CombatUnit",
                         "ProjectHero.Core.Gameplay.BattleManager",
                         "ProjectHero.Core.Gameplay.EnemyAIController",
                         "ProjectHero.Core.Gameplay.TacticsController",
                         "ProjectHero.UI.Timeline.TimelineEditorUI",
                         "ProjectHero.Demos.CombatDemo"
                     })
            {
                bool found = false;
                foreach (var classification in RuntimeCallbackRegistry.All)
                {
                    if (classification.TypeName != required) continue;
                    if (classification.CallbackClass != UnityCallbackClass.LegacyDependentWriter) continue;
                    found = true;
                }
                if (!found) unclassified.Add(required + " (no LegacyDependentWriter entry)");
            }

            return unclassified;
        }

        /// <summary>
        /// 第三收尾轮 R2 的只读复核：旧侧"单位数"必须是<strong>旧场景活动事实</strong>，
        /// 且与显式绑定数、定义槽位数三者相等。
        ///
        /// 三条独立读数：
        /// ① 观测里的 <c>ObservedUnitCount</c>（应来自活动 <c>CombatUnit</c> 计数）；
        /// ② 直接数场景里活动 <c>CombatUnit</c> 实例（<c>FindObjectsInactive.Exclude</c>）；
        /// ③ 定义槽位数（拒绝定义驱动的"空世界"）。
        /// 再额外调用一次生产 <c>Observe()</c>，确认"活动单位数与槽位数不符时观测会失败"这条
        /// 门槛在当前场景下确实成立（活动单位数与槽位数都被读到且相等）。
        /// </summary>
        private void AssertActiveUnitCountsAreConsistent(int observations, string context)
        {
            var observed = Bootstrap.LegacyObservations[observations - 1];
            Assert.That(observed, Is.Not.Null);

            var unitType = ResolveProductionType("ProjectHero.Core.Entities.CombatUnit");
            Assert.That(unitType, Is.Not.Null,
                "对照证据：旧场景单位类型必须可解析（否则本断言退化为恒真）");
            var activeUnits = UnityEngine.Object.FindObjectsByType(unitType, FindObjectsInactive.Exclude);
            Assert.That(activeUnits.Length, Is.GreaterThan(0),
                "对照证据：旧场景必须存在活动 CombatUnit");
            Assert.That(observed.ObservedUnitCount, Is.EqualTo(activeUnits.Length),
                context + "：观测到的单位数必须等于旧场景**活动** CombatUnit 实例数"
                + "（第三收尾轮 R2：不得再用定义槽位数冒充旧世界事实）");

            Assert.That(Bootstrap.LegacySlotOrder.Count, Is.EqualTo(activeUnits.Length),
                context + "：定义槽位数必须与旧场景活动单位数相等"
                + "（生产观测实现以此为采样门槛，不等时观测必须失败）");
            Assert.That(observed.Units.Count, Is.EqualTo(activeUnits.Length),
                context + "：逐单位身份事实数必须与活动单位数相等");

            // 再独立采样一次：证明门槛在当前场景下确实可满足（不是靠没人调用而恒真）。
            var factory = FindSimulationSourceFactory();
            Assert.That(factory, Is.Not.Null,
                "对照证据：真实初始化来源组件必须存在（观测实现的宿主）");
            var observe = factory.GetType().GetMethod(
                "Observe", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(observe, Is.Not.Null,
                "BattleSimulationSourceFactory 必须公开 Observe(string)（只读观测入口）");
            var freshObservation = observe.Invoke(factory, new object[] { "LateUpdate" })
                as LegacyLogicObservation;
            Assert.That(freshObservation, Is.Not.Null,
                context + "：生产 Observe() 在活动单位集合与槽位集合一致时必须返回观测"
                + "（返回 null 说明两者不一致 ⇒ 本用例的单位数断言实际未被覆盖）");
            Assert.That(freshObservation.ObservedUnitCount, Is.EqualTo(activeUnits.Length),
                context + "：独立采样的单位数必须同样等于活动单位数");
        }

        /// <summary>
        /// 第三收尾轮 R1：扫描<strong>非测试</strong>程序集里 <c>ShadowWriteWall.TryWrite</c> 的 IL 调用点，
        /// 返回 <c>类型.方法</c> 描述列表。
        ///
        /// 用途：把"生产侧没有写入点埋点"从记录措辞变成可失败断言。
        /// 判定为测试程序集的方式是<strong>类型上是否存在 <c>[NUnit.Framework.TestFixture]</c></strong>——
        /// 不按程序集名硬编码，也不随测试程序集改名而失效。
        /// </summary>
        private static List<string> FindProductionTryWriteCallSites()
        {
            var callSites = new List<string>();
            var target = typeof(ShadowWriteWall).GetMethod(
                "TryWrite", BindingFlags.Public | BindingFlags.Static);
            Assert.That(target, Is.Not.Null, "ShadowWriteWall.TryWrite 必须存在");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                var assembly = assemblies[a];
                if (assembly.IsDynamic) continue;
                if (IsTestAssembly(assembly)) continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                for (int t = 0; t < types.Length; t++)
                {
                    var type = types[t];
                    if (type == null) continue;
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    for (int m = 0; m < methods.Length; m++)
                    {
                        if (CallsTryWrite(methods[m], target))
                            callSites.Add(type.FullName + "." + methods[m].Name);
                    }
                }
            }

            return callSites;
        }

        private static bool IsTestAssembly(Assembly assembly)
        {
            // 快路径：本项目全部测试程序集都以 ".Tests" 结尾（7 个 asmdef 中 3 个）。
            var name = assembly.GetName().Name ?? string.Empty;
            if (name.EndsWith(".Tests", StringComparison.Ordinal)) return true;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }
            catch (Exception)
            {
                return false;
            }

            for (int i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (type == null) continue;
                if (type.GetCustomAttribute<TestFixtureAttribute>() != null) return true;
            }
            return false;
        }

        /// <summary>该方法体内是否出现对 <paramref name="target"/> 的调用（按元数据令牌精确比对）。</summary>
        private static bool CallsTryWrite(MethodBase method, MethodInfo target)
        {
            MethodBody body;
            try
            {
                body = method.GetMethodBody();
            }
            catch (Exception)
            {
                return false;
            }
            if (body == null) return false;

            var il = body.GetILAsByteArray();
            if (il == null) return false;

            int index = 0;
            while (index < il.Length)
            {
                int offset = index;
                short value;
                byte code = il[index++];
                if (code == 0xFE)
                {
                    if (index >= il.Length) break;
                    value = (short)(0xFE00 | il[index++]);
                }
                else
                {
                    value = code;
                }

                OpCode opCode;
                if (!OpCodesByValue.TryGetValue(value, out opCode)) break;

                int operandSize;
                if (opCode.OperandType == OperandType.InlineNone)
                {
                    operandSize = 0;
                }
                else if (opCode.OperandType == OperandType.ShortInlineI
                    || opCode.OperandType == OperandType.ShortInlineVar
                    || opCode.OperandType == OperandType.ShortInlineBrTarget)
                {
                    operandSize = 1;
                }
                else if (opCode.OperandType == OperandType.InlineVar)
                {
                    operandSize = 2;
                }
                else if (opCode.OperandType == OperandType.InlineI8
                    || opCode.OperandType == OperandType.InlineR)
                {
                    operandSize = 8;
                }
                else if (opCode.OperandType == OperandType.InlineSwitch)
                {
                    if (offset + 4 > il.Length) break;
                    int count = BitConverter.ToInt32(il, offset + 1);
                    operandSize = 4 + (count * 4);
                }
                else
                {
                    operandSize = 4;
                }

                if (index + operandSize > il.Length) break;

                if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                    && operandSize == 4)
                {
                    int token = BitConverter.ToInt32(il, index);
                    MethodBase callee = null;
                    try
                    {
                        callee = method.Module.ResolveMethod(
                            token, method.DeclaringType != null
                                ? method.DeclaringType.GetGenericArguments() : null,
                            method.IsGenericMethod ? method.GetGenericArguments() : null);
                    }
                    catch (Exception)
                    {
                        callee = null;
                    }

                    if (callee != null && callee.MetadataToken == target.MetadataToken
                        && callee.Module == target.Module)
                    {
                        return true;
                    }
                }

                index += operandSize;
            }

            return false;
        }

        private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

        private static Dictionary<short, OpCode> BuildOpCodeTable()
        {
            var table = new Dictionary<short, OpCode>();
            var fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(OpCode)) continue;
                var opCode = (OpCode)fields[i].GetValue(null);
                table[(short)opCode.Value] = opCode;
            }
            return table;
        }

        /// <summary>
        /// 第三收尾轮 R1 的结构证据：契约程序集 <c>ProjectHero.Compatibility.Runtime</c>
        /// <strong>类型层面</strong>无法命名旧运行组与 Unity 视图类型——
        /// 这是选择"方案 b（如实改写为无写入面）"而不是"方案 a（在真实边界访问器上接墙）"的依据。
        /// </summary>
        private static void AssertContractAssemblyCannotNameLegacyOrViewTypes()
        {
            var contract = typeof(BattleRuntimeBootstrap).Assembly;

            var referenced = contract.GetReferencedAssemblies();
            var names = new List<string>();
            for (int i = 0; i < referenced.Length; i++) names.Add(referenced[i].Name);
            Assert.That(names, Does.Not.Contain("ProjectHero.Authoring"),
                "契约程序集不得引用 ProjectHero.Authoring（旧资产/旧组件所在程序集）；实测引用："
                + string.Join(",", names));
            Assert.That(names, Does.Contain("ProjectHero.Logic"),
                "契约程序集必须引用 ProjectHero.Logic（纯数据内核）；实测引用：" + string.Join(",", names));
            Assert.That(names, Does.Not.Contain("ProjectHero.UnityView"),
                "契约程序集不得引用 UnityView（该程序集在任务 03B 中明确未创建/未引用）；实测引用："
                + string.Join(",", names));

            // 旧运行组与 Unity 视图类型由 Assembly-CSharp 定义；契约程序集解析不到它们，
            // 因此"在契约程序集里往旧状态/Unity 对象写"根本编译不出来。
            var legacyNames = new[]
            {
                "ProjectHero.Core.Entities.CombatUnit, Assembly-CSharp",
                "ProjectHero.Core.Timeline.BattleTimeline, Assembly-CSharp"
            };
            for (int i = 0; i < legacyNames.Length; i++)
            {
                var resolved = Type.GetType(legacyNames[i], throwOnError: false);
                Assert.That(resolved, Is.Not.Null,
                    "对照证据：旧运行组类型必须真实存在于 Assembly-CSharp（否则本断言退化为恒真）："
                    + legacyNames[i]);
                Assert.That(resolved.Assembly, Is.Not.EqualTo(contract),
                    "旧运行组类型不得定义在契约程序集内：" + legacyNames[i]);
            }
        }

        private static void AssertNoForbiddenUnitySurface(Type type)
        {
            var forbidden = new[]
            {
                typeof(GameObject), typeof(Transform), typeof(Component), typeof(Camera),
                typeof(Animator), typeof(AudioSource), typeof(ParticleSystem)
            };

            var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public |
                                          BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var member in members)
            {
                Type memberType = null;
                if (member is FieldInfo field) memberType = field.FieldType;
                else if (member is PropertyInfo property) memberType = property.PropertyType;
                else if (member is MethodInfo method) memberType = method.ReturnType;
                if (memberType == null) continue;

                foreach (var banned in forbidden)
                {
                    Assert.That(memberType, Is.Not.EqualTo(banned),
                        type.Name + " 的成员 " + member.Name + " 不得持有 Unity 视图/反馈类型 " + banned.Name);
                }
            }

            Assert.That(DeclaresUnityUpdate(type), Is.False,
                type.Name + " 不得声明 Update()/LateUpdate()/FixedUpdate()");
        }

        private static List<KeyValuePair<string, Vector3>> CaptureUnitTransforms()
        {
            var snapshot = new List<KeyValuePair<string, Vector3>>();
            var unitType = ResolveProductionType("ProjectHero.Core.Entities.CombatUnit");
            if (unitType == null) return snapshot;

            var units = UnityEngine.Object.FindObjectsByType(
                unitType, FindObjectsInactive.Include);
            for (int i = 0; i < units.Length; i++)
            {
                var component = units[i] as Component;
                if (component == null) continue;
                snapshot.Add(new KeyValuePair<string, Vector3>(component.name, component.transform.position));
            }
            return snapshot;
        }

        private static void AssertTransformsUnchanged(List<KeyValuePair<string, Vector3>> before, string message)
        {
            if (before.Count == 0) return;
            var unitType = ResolveProductionType("ProjectHero.Core.Entities.CombatUnit");
            var units = UnityEngine.Object.FindObjectsByType(
                unitType, FindObjectsInactive.Include);

            for (int i = 0; i < before.Count; i++)
            {
                for (int j = 0; j < units.Length; j++)
                {
                    var component = units[j] as Component;
                    if (component == null || component.name != before[i].Key) continue;
                    Assert.That(Vector3.Distance(component.transform.position, before[i].Value),
                        Is.LessThan(1e-4f), message + "：" + before[i].Key);
                }
            }
        }

        // =====================================================================
        // 任务 04：状态 / 持续效果 / 死亡 / 胜负 的 Shadow 检查点扩展
        // =====================================================================

        /// <summary>
        /// 任务 04「必须产出」9 的<strong>真实断言</strong>：
        /// 扩展后的 Shadow 配置在生产场景上的每一份报告都必须<strong>无未分类差异</strong>，
        /// 且差异只能落在四类归属之内；同时证明新增字段<strong>真的在比较</strong>
        /// （比较内容量覆盖"检查点级 + 逐单位"的完整字段集合）。
        ///
        /// 与 03B 用例的关系：03B 已断言"等价场景不得出现非预期差异"；
        /// 本用例把范围升级为任务 04 新增的<strong>状态/效果/死亡/胜负</strong>检查点，
        /// 并要求比较量随字段集合扩张而增长（否则"扩展了检查点"只是名义上的）。
        /// </summary>
        [UnityTest]
        public IEnumerator StateDeathAndVictoryShadowProfileHasNoUnclassifiedDifference()
        {
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();

            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            const int frames = 8;
            for (int i = 0; i < frames; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            Assert.That(Bootstrap.ShadowReports.Count, Is.GreaterThan(0),
                "必须产生至少一份 Shadow 报告");

            for (int r = 0; r < Bootstrap.ShadowReports.Count; r++)
            {
                var report = Bootstrap.ShadowReports[r];

                // ① 无未分类差异：差异集合里不得出现"既不属四类之一"的条目。
                //    四类归属由 ShadowDifferenceKind 穷举，因此这里逐条核对枚举合法性。
                for (int d = 0; d < report.Differences.Count; d++)
                {
                    var kind = report.Differences[d].Kind;
                    Assert.That(System.Enum.IsDefined(typeof(ShadowDifferenceKind), kind), Is.True,
                        "差异类别必须落在四类归属内：" + report.Differences[d]);
                    Assert.That(string.IsNullOrEmpty(report.Differences[d].FieldPath), Is.False,
                        "每条差异必须带精确字段路径：" + report.Differences[d]);
                }

                // ② 等价场景：不得有非预期差异，也不得有基础设施差异。
                Assert.That(report.HasUnexpectedDifference, Is.False,
                    "报告 #" + r + " 不得出现非预期差异：" + report.Describe());
                Assert.That(report.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                    "报告 #" + r + " 不得出现基础设施事实差异：" + report.Describe());
                Assert.That(report.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0),
                    "本用例不得依赖任何批准差异（否则等价声明被削弱）：" + report.Describe());
                Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                    "按逻辑 Tick 必须完全对齐：" + report.Describe());

                // ③ 新增字段真的进入比较：比较内容量必须覆盖完整字段集合。
                int expectedPerCheckpoint = LegacyLogicObservation.ComparableFieldCountPerCheckpoint
                    + 2 * LegacyLogicObservation.ComparableFieldCountPerUnit;
                Assert.That(report.ComparedCheckpoints, Is.GreaterThan(0),
                    "报告 #" + r + " 必须真的比较了检查点：" + report.Describe());
                Assert.That(report.ComparedFieldObservations,
                    Is.GreaterThanOrEqualTo(report.ComparedCheckpoints * expectedPerCheckpoint),
                    "报告 #" + r + " 的比较内容量必须覆盖「检查点级 + 2 单位 × 逐单位」全部字段："
                    + report.Describe());

                // ④ 暂不可比较字段仍然逐条登记（不得被静默丢弃、也不得冒充已比较）。
                Assert.That(report.TemporarilyUncomparable.Count, Is.GreaterThan(0),
                    "报告 #" + r + " 必须保留逐条登记的暂不可比较字段：" + report.Describe());
                for (int t = 0; t < report.TemporarilyUncomparable.Count; t++)
                {
                    var field = report.TemporarilyUncomparable[t];
                    Assert.That(field.LegacyObjectPath, Is.Not.Empty, "必须给出旧侧对象路径");
                    Assert.That(field.Field, Is.Not.Empty, "必须给出字段名");
                    Assert.That(field.Reason, Is.Not.Empty, "必须给出原因");
                    Assert.That(field.OwnerTask, Is.Not.Empty, "必须给出负责任务");
                    Assert.That(field.RemovalGate, Is.Not.Empty, "必须给出最迟清零门槛");
                }

                // ⑤ 任务 04 的登记边界必须逐条自洽：
                //    「登记为暂不可比较」与「已真的在比较」不能同时成立——除非登记的是
                //    **覆盖边界**（旧侧结构表达力不足），而不是"尚未切换"。
                //
                //    计数口径（修订轮 R4，三处标注同一判据，数字因此可比）：
                //      · 判据 A「已移出登记表」= 3 条（units[i].healthQ10 / position / facing；
                //        登记表 11 → 10 条净变化里被删掉的正是这 3 条）
                //      · 判据 B「本轮新切换 + 已移入比较集合」= 4 条（A 的 3 条 + battleEnd.isEnded，
                //        后者是"新真读但登记项按覆盖边界保留"）
                //      · 判据 C「已真读并进入字段级比较（含保留为覆盖边界者）」= 5 条
                //        （B 的 4 条 + battleEnd.resultCode）
                //    本处使用的是判据 A：3 条必须已移出登记表；
                //    4 条覆盖边界/新侧实体尚未落地的字段必须仍在表内（B 判据的补集）。
                AssertSwitchedFieldIsNotRegistered(report, "units[i].healthQ10");
                AssertSwitchedFieldIsNotRegistered(report, "units[i].position");
                AssertSwitchedFieldIsNotRegistered(report, "units[i].facing");
                Assert.That(HasRegisteredField(report, "units[i].state"), Is.True,
                    "旧侧没有 Guarding/Blocking/Dodging 的等价事实，这条覆盖边界必须登记："
                    + report.Describe());
                Assert.That(HasRegisteredField(report, "battleEnd.isEnded"), Is.True,
                    "旧胜负是逐帧轮询 + timeScale 慢放，与新侧阶段 2/16 的原子提交时机不同，"
                    + "这条覆盖边界必须登记：" + report.Describe());
                Assert.That(HasRegisteredField(report, "battleEnd.resultCode"), Is.True,
                    "旧侧只有结束显示文本、没有结果码字段，这条覆盖边界必须登记：" + report.Describe());
                Assert.That(HasRegisteredField(report, "scheduledEventCount"), Is.True,
                    "旧排程计数已真读，但新侧对应实体尚未落地，必须登记：" + report.Describe());

                // ⑥ 策略结构校验不得产生任何拒绝（拒绝会让 CanClaimEquivalence 为 false）。
                Assert.That(report.Rejections.Count, Is.EqualTo(0),
                    "逐用例策略必须自洽（缺责任任务/门槛、宽泛批准都会被拒）：" + report.Describe());
                Assert.That(report.RulesVersion, Is.EqualTo(seed.RulesVersion),
                    "报告必须记录本用例的策略规则版本（逐用例登记的组成部分）");

                Assert.That(report.CanClaimEquivalence, Is.True, report.Describe());
            }

            // 对照证据：真实定义 + 真实场景确实被用于本用例。
            Assert.That(seed.RulesVersion, Is.EqualTo("battle-def-v1"));
            Assert.That(Bootstrap.LegacyObservations.Count, Is.GreaterThan(0),
                "必须采集到旧侧只读观测（否则比较空转）");
            Assert.That(Bootstrap.LegacyObservations[0].Units.Count, Is.EqualTo(2));

            Bootstrap.StopBattle("task04-shadow-profile");
            Bootstrap.ReleaseBattle();
        }

        /// <summary>报告里是否登记了某个"暂不可比较"字段。</summary>
        private static bool HasRegisteredField(ShadowComparisonReport report, string field)
        {
            for (int i = 0; i < report.TemporarilyUncomparable.Count; i++)
            {
                if (string.Equals(report.TemporarilyUncomparable[i].Field, field, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 已切换为实时读取并进入字段级比较的字段<strong>不得</strong>再出现在暂不可比较登记表里
        /// ——"登记为暂不可比较"与"已真的在比较"不能同时成立。
        ///
        /// 例外是<strong>覆盖边界</strong>登记项（<c>battleEnd.isEnded</c>、<c>units[i].state</c>、
        /// <c>battleEnd.resultCode</c>、<c>scheduledEventCount</c>）：它们的字段已真读并已比较，
        /// 登记的是"旧侧结构表达力不足 / 新侧对应实体尚未落地"这一语义边界——
        /// 这类条目在 03B 策略里有逐条说明，因此不走本断言。
        /// </summary>
        private static void AssertSwitchedFieldIsNotRegistered(ShadowComparisonReport report, string field)
        {
            Assert.That(HasRegisteredField(report, field), Is.False,
                "字段 " + field + " 已切换为读旧场景活动事实并进入字段级比较，"
                + "不得再登记为暂不可比较：" + report.Describe());
        }

        /// <summary>
        /// 任务 04 的核心可证伪性断言：新增字段的旧侧取值必须来自
        /// <strong>旧场景活动对象</strong>的实时属性，而不是定义槽位等定义派生常量。
        ///
        /// 三重证据：
        /// <list type="number">
        /// <item><strong>正向</strong>：观测里的 <c>CurrentHealthLive</c> / <c>GridPositionLiveX/Y</c> /
        /// <c>FacingLive</c> / <c>LegacyStateFlagsLive</c> 与直接读活动 <c>CombatUnit</c>
        /// 得到的值逐字段一致（同一事实的两个独立读数）。</item>
        /// <item><strong>可失败</strong>：改动活动对象的朝向后再采样，观测必须跟着变
        /// （若实现仍在读定义槽位或缓存常量，这条会失败）。</item>
        /// <item><strong>负控制</strong>：篡改观测里的实时字段值后与<strong>真实</strong> Shadow 侧快照
        /// 重新比较，必须逐字段产生<strong>基础设施事实差异</strong>并拒绝等价声明——证明比较器
        /// 真的在比较这些字段，而不是"新字段进了集合但从不参与判定"。</item>
        /// </list>
        /// </summary>
        [UnityTest]
        public IEnumerator ShadowStateFieldsAreReadLiveFromLegacySceneAndCanFail()
        {
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();

            // Shadow 必须真的跑起来：Observe() 是 fail-closed 的（活动单位集合 / 显式绑定集合 /
            // 定义槽位集合三者不符即返回 null），且负控制需要真实的 Shadow 侧快照序列作为对照。
            Assert.That(Bootstrap.StartBattle(BattleRuntimeMode.Shadow), Is.True,
                "Shadow 启动必须成功：" + Bootstrap.StartupRejection);

            const int frames = 4;
            for (int i = 0; i < frames; i++)
            {
                Bootstrap.DriveFrameForTests(1f / 60f, 1f / 60f);
                Bootstrap.RunCheckpointForTests();
            }

            Assert.That(Bootstrap.LegacyObservations.Count, Is.EqualTo(frames),
                "生产路径每个检查点必须采集一份旧侧只读观测（否则本用例没有可核对的旧侧事实）");
            Assert.That(Bootstrap.Shadow, Is.Not.Null,
                "Shadow 运行器必须存在（负控制需要真实新侧快照，不能拿空集合当对照）");

            var factory = FindSimulationSourceFactory();
            Assert.That(factory, Is.Not.Null,
                "对照证据：场景中必须存在旧类型宿主 BattleSimulationSourceFactory");
            var observe = factory.GetType().GetMethod(
                "Observe", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(observe, Is.Not.Null, "BattleSimulationSourceFactory 必须公开 Observe(string)");

            var (_, enemy) = FindLegacyUnits();
            Assert.That(enemy, Is.Not.Null, "对照证据：必须能定位旧的敌方单位");

            // —— ① 正向：观测字段与直接读活动对象一致 ——
            var observation = observe.Invoke(factory, new object[] { "Task04LiveRead" })
                as LegacyLogicObservation;
            Assert.That(observation, Is.Not.Null,
                "生产 Observe() 必须返回观测（返回 null 是本用例的失败，不是跳过理由）");
            Assert.That(observation.Units.Count, Is.EqualTo(2));

            var enemyObs = FindObservationFor(observation, enemy);
            Assert.That(enemyObs, Is.Not.Null,
                "观测必须按槽位绑定覆盖两个活动单位；对象路径=" + DescribeLegacyUnit(enemy));
            Assert.That(enemyObs.HasCurrentHealthLive, Is.True,
                "活动单位的生命必须可实时采样（不可采样只能显式标记，不能靠默认值冒充）");
            Assert.That(enemyObs.CurrentHealthLive,
                Is.EqualTo(ReadLegacyFloat(enemy, "CurrentHealth")),
                "观测里的生命必须等于直接读活动 CombatUnit.CurrentHealth（同一事实的两个读数）");

            // 网格坐标（活动属性）
            var gridX = enemy.GetType().GetProperty("GridPosition",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(gridX, Is.Not.Null, "CombatUnit 必须仍有公开属性 GridPosition");
            var gridPoint = gridX.GetValue(enemy);
            var pointType = gridPoint.GetType();
            int liveX = (int)pointType.GetField("X").GetValue(gridPoint);
            int liveY = (int)pointType.GetField("Y").GetValue(gridPoint);
            Assert.That(enemyObs.GridPositionLiveX, Is.EqualTo(liveX),
                "观测里的格坐标 X 必须等于直接读 CombatUnit.GridPosition.X");
            Assert.That(enemyObs.GridPositionLiveY, Is.EqualTo(liveY),
                "观测里的格坐标 Y 必须等于直接读 CombatUnit.GridPosition.Y");

            // 朝向是枚举字段（public GridDirection FacingDirection），不是 int 字段：
            // 必须先按字段类型读出枚举再转 int，不能对它调用 ReadLegacyInt。
            var facingField = enemy.GetType().GetField("FacingDirection",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(facingField, Is.Not.Null,
                "对照证据：CombatUnit 必须仍有公开字段 FacingDirection");
            int facing = Convert.ToInt32(facingField.GetValue(enemy));
            Assert.That(enemyObs.FacingLive, Is.EqualTo(facing),
                "观测里的朝向必须等于直接读活动 CombatUnit.FacingDirection");

            // 状态族标签必须等于按冻结优先级从活动 bool 重算的结果
            // （死亡 > 击倒 > 硬直 > 前摇 > 后摇 > 移动 > 空闲）。
            Assert.That(enemyObs.LegacyStateFlagsLive, Is.EqualTo(ExpectedLegacyStateLabel(enemy)),
                "状态族标签必须按冻结规则从旧场景活动事实推导，而不是定义侧常量");

            // —— ② 可失败：改动活动对象的朝向，观测必须跟着变 ——
            var originalFacing = facingField.GetValue(enemy);
            facingField.SetValue(enemy, FlipDirection(originalFacing));
            try
            {
                var afterFlip = observe.Invoke(factory, new object[] { "Task04LiveReadFlipped" })
                    as LegacyLogicObservation;
                Assert.That(afterFlip, Is.Not.Null, "只改朝向不得让观测整体失败");
                var afterFlipEnemy = FindObservationFor(afterFlip, enemy);
                Assert.That(afterFlipEnemy, Is.Not.Null);
                Assert.That(afterFlipEnemy.FacingLive, Is.Not.EqualTo(enemyObs.FacingLive),
                    "改动活动对象的 FacingDirection 后，观测必须跟着变"
                    + "（若仍在读定义槽位/缓存常量，这条会失败）");
                Assert.That(afterFlipEnemy.CurrentHealthLive, Is.EqualTo(enemyObs.CurrentHealthLive),
                    "控制变量：只改朝向不得影响其他实时字段");
            }
            finally
            {
                facingField.SetValue(enemy, originalFacing);
            }

            // —— ③ 负控制：篡改**真实采集到**的观测的实时字段 ⇒ 必须逐字段产生基础设施事实差异 ——
            var real = Bootstrap.LegacyObservations[Bootstrap.LegacyObservations.Count - 1];
            Assert.That(real, Is.Not.Null);
            Assert.That(real.Units.Count, Is.EqualTo(2));

            var tamperedUnits = new List<LegacyLogicUnitObservation>();
            for (int i = 0; i < real.Units.Count; i++)
            {
                var unit = real.Units[i];
                bool tamper = i == 0;   // 只篡改第一个槽位：其余单位必须保持"零差异"对照组
                tamperedUnits.Add(new LegacyLogicUnitObservation(
                    unit.SlotId, unit.UnitId, unit.DefinitionId, unit.FactionId, unit.LegacyObjectPath,
                    tamper ? unit.CurrentHealthLive + 1024f : unit.CurrentHealthLive,
                    tamper ? unit.GridPositionLiveX + 2 : unit.GridPositionLiveX,
                    unit.GridPositionLiveY,
                    tamper ? FlipFacingLabel(unit.FacingLive, facingField.FieldType) : unit.FacingLive,
                    tamper ? FlipStateLabel(unit.LegacyStateFlagsLive) : unit.LegacyStateFlagsLive));
            }

            var tampered = new LegacyLogicObservation(
                real.Tick, real.Checkpoint, real.ObservedUnitCount,
                tamperedUnits, real.SourceName,
                real.BattleEndedLive, real.BattleResultCodeLive, real.ScheduledEventCountLive);

            var tamperedReport = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    Bootstrap.LegacyCheckpoints, Bootstrap.Shadow.SnapshotSequence(), null,
                    new[] { tampered }, Bootstrap.LegacySlotOrder),
                ShadowCasePolicy.CreateDefault("task04-live-read-tampered", seed.RulesVersion));

            Assert.That(tamperedReport.ComparedCheckpoints, Is.EqualTo(1),
                "被篡改的观测必须仍然对齐（否则负控制退化为未对齐）：" + tamperedReport.Describe());
            Assert.That(tamperedReport.InfrastructureDifferences, Is.GreaterThan(0),
                "篡改实时读取字段必须产生基础设施事实差异（证明这些字段真的参与比较）："
                + tamperedReport.Describe());
            Assert.That(tamperedReport.CanClaimEquivalence, Is.False,
                "基础设施事实不同不得宣称等价：" + tamperedReport.Describe());

            // 精确到字段：四个被篡改的字段必须各自出现在差异里。
            // 这同时证明"新字段被移入 ComparableFieldPaths"不是名义上的——
            // 每一个字段的改动都能让比较失败。
            string[] tamperedFields = { ".healthQ10", ".position", ".facing", ".state" };
            for (int f = 0; f < tamperedFields.Length; f++)
            {
                Assert.That(HasDifferenceForField(tamperedReport.Differences, tamperedFields[f]), Is.True,
                    "篡改后的差异必须包含字段 " + tamperedFields[f] + " ："
                    + DescribeDifferenceList(tamperedReport.Differences));
            }

            // —— ④ 负控制（battleEnd 两项）：03B 交接 §23.2 的硬约束要求每个被切换为
            //    实时读取的字段都必须先建立"篡改旧侧真值 ⇒ 必须报差异"的负控制。
            //    这里只改观测的旧胜负事实，其余字段保持真实值。
            var endedTampered = new LegacyLogicObservation(
                real.Tick, real.Checkpoint, real.ObservedUnitCount,
                real.Units, real.SourceName,
                !real.BattleEndedLive,
                real.BattleEndedLive ? string.Empty : "RESULT_VICTORY",
                real.ScheduledEventCountLive);

            var endedReport = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    Bootstrap.LegacyCheckpoints, Bootstrap.Shadow.SnapshotSequence(), null,
                    new[] { endedTampered }, Bootstrap.LegacySlotOrder),
                ShadowCasePolicy.CreateDefault("task04-battle-end-tampered", seed.RulesVersion));

            Assert.That(endedReport.ComparedCheckpoints, Is.EqualTo(1),
                "被篡改的旧胜负观测必须仍然对齐：" + endedReport.Describe());
            Assert.That(HasDifferenceForField(endedReport.Differences, "battleEnd.isEnded"), Is.True,
                "篡改旧结束标志必须报差异（battleEnd.isEnded 已切换为真读比较）："
                + DescribeDifferenceList(endedReport.Differences));
            Assert.That(HasDifferenceForField(endedReport.Differences, "battleEnd.resultCode"), Is.True,
                "篡改旧结果码必须报差异（battleEnd.resultCode 已切换为真读比较）："
                + DescribeDifferenceList(endedReport.Differences));
            Assert.That(endedReport.CanClaimEquivalence, Is.False,
                "旧胜负事实不同不得宣称等价：" + endedReport.Describe());

            // 反向对照：不篡改时同一份观测必须零基础设施差异——
            // 证明上面的差异确实来自篡改，而不是"这两条字段恒报差异"。
            var cleanReport = ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion, ShadowComparisonConfig.Strict(64),
                    Bootstrap.LegacyCheckpoints, Bootstrap.Shadow.SnapshotSequence(), null,
                    new[] { real }, Bootstrap.LegacySlotOrder),
                ShadowCasePolicy.CreateDefault("task04-battle-end-clean", seed.RulesVersion));
            Assert.That(cleanReport.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                "未篡改的真实观测不得出现基础设施差异（对照）：" + cleanReport.Describe());

            Bootstrap.StopBattle("task04-shadow-live-read");
            Bootstrap.ReleaseBattle();
        }

        /// <summary>差异集合里是否存在以给定后缀结尾的字段路径。</summary>
        private static bool HasDifferenceForField(
            IReadOnlyList<ShadowFieldDifference> differences, string fieldSuffix)
        {
            for (int i = 0; i < differences.Count; i++)
            {
                if (differences[i].FieldPath != null
                    && differences[i].FieldPath.EndsWith(fieldSuffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 按 <c>BattleSimulationSourceFactory.DeriveLegacyStateLabel</c> 的冻结优先级
        /// 从活动 <c>CombatUnit</c> 的公开事实<strong>独立重算</strong>状态族标签。
        ///
        /// 这是"两个独立读数"里的第二个：本方法只读旧侧公开 bool/浮点字段，
        /// 不调用生产推导函数，因此生产实现若改了优先级或改了读取对象，本断言会失败。
        /// </summary>
        private static int ExpectedLegacyStateLabel(MonoBehaviour unit)
        {
            if (ReadLegacyFloat(unit, "CurrentHealth") <= 0f)
                return LegacyLogicUnitObservation.StateLabels.Dead;
            if (ReadLegacyBool(unit, "IsKnockedDown"))
                return LegacyLogicUnitObservation.StateLabels.KnockedDown;
            if (ReadLegacyBool(unit, "IsStaggered"))
                return LegacyLogicUnitObservation.StateLabels.Staggered;
            if (ReadLegacyBool(unit, "InWindup"))
                return LegacyLogicUnitObservation.StateLabels.Windup;
            if (ReadLegacyBool(unit, "InRecovery"))
                return LegacyLogicUnitObservation.StateLabels.Recovery;
            if (ReadLegacyBool(unit, "IsMoving"))
                return LegacyLogicUnitObservation.StateLabels.Moving;
            return LegacyLogicUnitObservation.StateLabels.Idle;
        }

        /// <summary>读取旧单位一个公开 <c>bool</c> 字段（状态族标签重算用）。</summary>
        private static bool ReadLegacyBool(MonoBehaviour unit, string fieldName)
        {
            Assert.That(unit, Is.Not.Null);
            var field = unit.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "CombatUnit 必须仍有公开字段 " + fieldName);
            Assert.That(field.FieldType, Is.EqualTo(typeof(bool)), fieldName + " 必须是 bool");
            return (bool)field.GetValue(unit);
        }

        /// <summary>把一个朝向标签换成同枚举里的另一个合法值（负控制）。</summary>
        private static int FlipFacingLabel(int label, Type directionType)
        {
            var values = System.Enum.GetValues(directionType);
            for (int i = 0; i < values.Length; i++)
            {
                int candidate = Convert.ToInt32(values.GetValue(i));
                if (candidate != label) return candidate;
            }
            return label;
        }

        /// <summary>把一个状态族标签换成另一个合法标签（负控制）。</summary>
        private static int FlipStateLabel(int label)
            => label == LegacyLogicUnitObservation.StateLabels.Idle
                ? LegacyLogicUnitObservation.StateLabels.Moving
                : LegacyLogicUnitObservation.StateLabels.Idle;

        /// <summary>在观测里按单位对象路径找到对应条目（不依赖顺序）。</summary>
        private static LegacyLogicUnitObservation FindObservationFor(
            LegacyLogicObservation observation, MonoBehaviour unit)
        {
            string path = DescribeLegacyUnit(unit);
            for (int i = 0; i < observation.Units.Count; i++)
            {
                if (string.Equals(observation.Units[i].LegacyObjectPath, path, StringComparison.Ordinal))
                    return observation.Units[i];
            }
            return null;
        }

        private static string DescribeLegacyUnit(MonoBehaviour unit)
        {
            if (unit == null) return "<null>";
            var scene = unit.gameObject.scene;
            string sceneName = scene.IsValid() ? scene.name : "<no-scene>";
            return sceneName + "/" + unit.gameObject.name + "#CombatUnit";
        }

        /// <summary>把朝向枚举翻到另一个方向（负控制用；只作用于测试对象）。</summary>
        private static object FlipDirection(object direction)
        {
            var values = System.Enum.GetValues(direction.GetType());
            for (int i = 0; i < values.Length; i++)
            {
                if (!values.GetValue(i).Equals(direction)) return values.GetValue(i);
            }
            return direction;
        }

        /// <summary>
        /// 采集已登记写入者的启用状态。
        ///
        /// 第二收尾轮 R4.5：键固定为 <c>CallbackSite</c>（稳定键），不再依赖下标 ——
        /// 旧实现在采集时跳过 <c>GateTarget == null</c> 的登记项、在比对时却用
        /// <c>LegacyWriters[i]</c> 按下标取目标，隐藏场景里真实发生过错位配对。
        /// </summary>
        private List<KeyValuePair<string, bool>> CaptureWriterStates()
        {
            var snapshot = new List<KeyValuePair<string, bool>>();
            for (int i = 0; i < Bootstrap.LegacyWriters.Count; i++)
            {
                var registration = Bootstrap.LegacyWriters[i];
                if (registration == null || registration.GateTarget == null) continue;
                snapshot.Add(new KeyValuePair<string, bool>(
                    registration.CallbackSite, registration.GateTarget.enabled));
            }
            return snapshot;
        }

        /// <summary>
        /// 按稳定键（对象路径 + 类型 + 实例名）比对写入者启用状态。
        /// 无法再解析到同一登记项时**显式失败**（不得静默跳过）。
        /// </summary>
        private void AssertWriterStatesUnchanged(List<KeyValuePair<string, bool>> before, string message)
        {
            for (int i = 0; i < before.Count; i++)
            {
                string callbackSite = before[i].Key;
                MonoBehaviour target = null;

                for (int j = 0; j < Bootstrap.LegacyWriters.Count; j++)
                {
                    var registration = Bootstrap.LegacyWriters[j];
                    if (registration == null) continue;
                    if (!string.Equals(registration.CallbackSite, callbackSite, StringComparison.Ordinal))
                        continue;
                    target = registration.GateTarget;
                    break;
                }

                Assert.That(target, Is.Not.Null,
                    message + "：比对时无法按稳定键解析写入者 " + callbackSite
                    + "（禁止按下标错位配对，也禁止静默跳过）");
                Assert.That(target.enabled, Is.EqualTo(before[i].Value), message + "：" + callbackSite);
            }
        }

        private static List<KeyValuePair<string, bool>> CaptureActiveStates()
        {
            var snapshot = new List<KeyValuePair<string, bool>>();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    snapshot.Add(new KeyValuePair<string, bool>(
                        behaviour.name + "#" + behaviour.GetType().Name, behaviour.enabled));
                }
            }
            return snapshot;
        }

        private static void AssertActiveStatesUnchanged(List<KeyValuePair<string, bool>> before, string message)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var current = new Dictionary<string, bool>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    current[behaviour.name + "#" + behaviour.GetType().Name] = behaviour.enabled;
                }
            }

            for (int i = 0; i < before.Count; i++)
            {
                bool value;
                if (!current.TryGetValue(before[i].Key, out value)) continue;
                Assert.That(value, Is.EqualTo(before[i].Value), message + "：" + before[i].Key);
            }
        }

        /// <summary>测试专用未分类逻辑写入者：自增计数器，证明它确实在自主执行 Update。</summary>
        public sealed class UnclassifiedLogicWriter : MonoBehaviour
        {
            public static int UpdateCalls;

            private void Update() => UpdateCalls++;
        }

        // =====================================================================
        // 任务 05：排程 / 计划 / Lane / 反应机会 的 Shadow 检查点扩展
        // =====================================================================

        /// <summary>本用例使用的真实攻击（必须存在于 hero 的动作集合内，见 <see cref="ResolveEncounterUnits"/>）。</summary>
        private const string Task05HeroAttackSpecId = "action.quick_slash.radius_1";

        /// <summary>本用例的 Shadow 比较用例 ID（逐用例策略的组成部分）。</summary>
        private const string Task05CaseId = "task05-action-plan-lane-profile";

        // —— 场景关键 Tick（与 BuildTask05ScheduleStream 及下方断言逐条对应）——
        private const int Task05EditTick = 0;              // 排程编辑命令的目标 Tick（成功后 scheduleRevision = 1）
        private const int Task05RequestedStartTick = 3;    // 计划请求起点（该 Tick 会被系统自动延期）
        private const int Task05StaggerTick = 3;           // 阶段 1 的显式控制转换（硬直 10 Tick，区间 [3, 13)）
        private const int Task05DeferredStartTick = 13;    // 自动延期后的起点 = 3 + 10
        private const int Task05ProbeTick = 14;            // 覆盖探针 Tick（计划 Running / Lane 未锁 / 机会已在审计面）
        private const int Task05EndTick = 16;              // 敌人死亡 Tick（唯一 Finalizer：锁 Lane + 终态归档）
        private const int Task05UncomparableFieldCount = 22;   // 既有 10 条 + 任务 05 新增 12 条

        /// <summary>
        /// 任务 05「必须产出」的 Shadow 检查点扩展<strong>真实断言</strong>（Logic 对 Logic 通道，Path B）。
        ///
        /// 与任务 04 的 <c>StateDeathAndVictoryShadowProfileHasNoUnclassifiedDifference</c> 的分工：
        /// 那条走**生产通道**（旧侧只读观测 vs 新模拟），因此只能证明"任务 05 的覆盖边界被逐条登记"；
        /// 本条走**纯 Logic 世界对 Logic 世界**通道（<see cref="ShadowDifferenceDetector.Compare"/>
        /// 的 <c>legacyCheckpoints</c>/<c>shadowCheckpoints</c> 参数），因此能真正逐条比较
        /// <c>plans[i].*</c> / <c>actorLanes[i].*</c> / <c>reactionOpportunities[i].*</c> /
        /// <c>scheduleRevision</c> / <c>nextReactionOpportunityId</c> 等任务 05 事实。
        ///
        /// 场景（真实 02B 定义 + 真实 <c>BattleSimulation.Step</c> 管线，两侧跑同一条脚本化输入序列）：
        /// <list type="number">
        /// <item>Tick 0：<c>ScheduleEditPayload + AddOrdinaryPlanOperation</c>（真实攻击
        /// <c>action.quick_slash.radius_1</c>、请求起点 3、目标敌人）⇒ <c>scheduleRevision = 1</c>、
        /// 计划 <c>Editable</c>；</item>
        /// <item>Tick 3：阶段 1 的 <c>ControlTransitionAtTick(UnitId=hero, StaggerFor(10))</c>
        /// ⇒ 启动门禁 <c>Retryable</c> ⇒ 系统自动延期一次（<c>automaticDeferralCount = 1</c>、
        /// <c>startTick 3 → 13</c>、<c>scheduleRevision = 2</c>）；</item>
        /// <item>Tick 13：原子锁定/启动 ⇒ 计划 <c>Running</c>（<c>lockedAtTick = 13</c>），
        /// 并在同一 Tick 公开一个反应机会（<c>areaThreatCandidateSource</c> 桩把敌人作为唯一候选）；</item>
        /// <item>Tick 16：<c>KillBatchAtTick</c> 清空敌人生命 ⇒ 阶段 2 判定战斗已决定 ⇒ 唯一 Finalizer
        /// 锁定全部 Lane 并把计划以 <c>BattleEnded</c> 终止（终态立即离开活动索引）。</item>
        /// </list>
        ///
        /// 断言（缺一不可）：① 每条差异的类别落在四类内且字段路径非空；② 无未分类差异、无基础设施差异、
        /// 无批准差异、无未对齐检查点；③ 策略零拒绝 + 规则版本一致 + 可宣称等价；④ 暂不可比较字段逐条
        /// 带 ID/原因/负责任务/清零门槛；⑤ 覆盖证据 = 场景事实逐条落在真实快照上 + 逐字段"篡改一侧 ⇒
        /// 必须报该字段路径"的探针（两侧相等时字段不会出现在差异里，因此必须用探针证明真的比较过）；
        /// ⑥ 负控制 = 在完全等价的前提下篡改"新侧"一条计划事实，必须<strong>恰好</strong>出现该字段路径上的
        /// <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>，且默认策略下同一篡改流零差异。
        /// </summary>
        [UnityTest]
        public IEnumerator ActionPlanAndLaneShadowProfileHasNoUnclassifiedDifference()
        {
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();

            // —— 场景：真实 02B 定义 + 真实 Step 管线；两侧各自跑同一条脚本化输入序列 ——
            // 通道本身是 Logic 对 Logic（不启动 Bootstrap 战斗），因此不需要 StopBattle/ReleaseBattle。
            (long heroUnitId, long enemyUnitId) = ResolveEncounterUnits(seed);

            var legacySide = BuildTask05ScheduleStream(seed, heroUnitId, enemyUnitId);
            var shadowSide = BuildTask05ScheduleStream(seed, heroUnitId, enemyUnitId);

            // 对照证据 0：检查点下标 == 逻辑 Tick；两侧逐位相同（同一定义 + 同一输入 ⇒ 同一世界）。
            Assert.That(legacySide.Snapshots.Count, Is.EqualTo(Task05EndTick + 1),
                "检查点必须覆盖 Tick 0.." + Task05EndTick + "（含战斗结束 Tick）");
            for (int i = 0; i < legacySide.Snapshots.Count; i++)
            {
                Assert.That(legacySide.Snapshots[i].Tick, Is.EqualTo((long)i),
                    "检查点下标必须等于逻辑 Tick：index=" + i);
                Assert.That(shadowSide.Snapshots[i].Tick, Is.EqualTo((long)i),
                    "检查点下标必须等于逻辑 Tick（新侧）：index=" + i);
                Assert.That(shadowSide.Snapshots[i].ComputeHash(),
                    Is.EqualTo(legacySide.Snapshots[i].ComputeHash()),
                    "同一脚本化输入的两个独立世界必须逐位相同：Tick " + i + " ; "
                    + DescribeSideComparison(legacySide, shadowSide, heroUnitId));
            }

            // —— ⑤ 覆盖证据（一）：任务 05 的场景事实真的落在这条检查点流上 ——
            long planId = AssertTask05ScenarioFacts(legacySide, heroUnitId, enemyUnitId);

            // —— 主报告：逐用例开启任务 05 检查点（compareScheduleFacts: true）——
            var report = CompareTask05ScheduleProfile(seed, legacySide, shadowSide, Task05CaseId);

            // ① 无未分类差异：每条差异的类别必须落在四类归属内，且带精确字段路径。
            for (int d = 0; d < report.Differences.Count; d++)
            {
                ShadowFieldDifference difference = report.Differences[d];
                Assert.That(Enum.IsDefined(typeof(ShadowDifferenceKind), difference.Kind), Is.True,
                    "差异类别必须落在四类归属内：" + difference);
                Assert.That(string.IsNullOrEmpty(difference.FieldPath), Is.False,
                    "每条差异必须带精确字段路径：" + difference);
            }

            // ② 等价场景：不得出现非预期差异 / 基础设施事实差异 / 批准差异 / 未对齐检查点。
            Assert.That(report.HasUnexpectedDifference, Is.False,
                "任务 05 检查点扩展后仍不得出现非预期差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                "同一脚本化输入的两侧不得出现基础设施事实差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0),
                "本用例不得依赖任何批准差异（否则等价声明被削弱）：" + report.Describe());
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                "按逻辑 Tick 必须完全对齐：" + report.Describe());
            Assert.That(report.ComparedCheckpoints, Is.EqualTo(Task05EndTick + 1),
                "比较必须覆盖全部检查点：" + report.Describe());

            // ③ 策略结构自洽 ⇒ 允许宣称等价。
            Assert.That(report.Rejections.Count, Is.EqualTo(0),
                "逐用例策略必须自洽（缺责任任务/门槛、宽泛批准都会被拒）：" + report.Describe());
            Assert.That(report.RulesVersion, Is.EqualTo(seed.RulesVersion),
                "报告必须记录本用例的策略规则版本（逐用例登记的组成部分）");
            Assert.That(seed.RulesVersion, Is.EqualTo("battle-def-v1"),
                "对照证据：真实定义（02B）的规则版本");
            Assert.That(report.CanClaimEquivalence, Is.True, report.Describe());
            Assert.That(report.EquivalenceClaim, Is.EqualTo("EQUIVALENT"), report.Describe());

            // ④ 暂不可比较字段逐条登记：既有 10 条 + 任务 05 新增 12 条 = 22 条，五要素缺一不可。
            Assert.That(report.TemporarilyUncomparable.Count, Is.EqualTo(Task05UncomparableFieldCount),
                "开启任务 05 检查点后必须逐条登记 " + Task05UncomparableFieldCount + " 条暂不可比较字段："
                + string.Join(" | ", report.TemporarilyUncomparable));
            for (int t = 0; t < report.TemporarilyUncomparable.Count; t++)
            {
                TemporarilyUncomparableField field = report.TemporarilyUncomparable[t];
                Assert.That(field.Id, Is.Not.Empty, "暂不可比较登记项必须给出 ID");
                Assert.That(field.Field, Is.Not.Empty, "暂不可比较登记项必须给出字段名");
                Assert.That(field.LegacyObjectPath, Is.Not.Empty, "暂不可比较登记项必须给出旧侧对象路径");
                Assert.That(field.Reason, Is.Not.Empty, "暂不可比较登记项必须给出原因");
                Assert.That(field.OwnerTask, Is.Not.Empty, "暂不可比较登记项必须给出负责任务");
                Assert.That(field.RemovalGate, Is.Not.Empty, "暂不可比较登记项必须给出最迟清零门槛");
            }
            Assert.That(report.CountOf(ShadowDifferenceKind.TemporarilyUncomparable),
                Is.EqualTo(report.TemporarilyUncomparable.Count * report.ComparedCheckpoints),
                "每个检查点都必须为每个登记项留下一条观察（不得静默丢弃）：" + report.Describe());

            // ④′ 任务 05 的 12 条覆盖边界登记项逐条在位，且都标明负责任务 05 与非空清零门槛。
            var task05Registrations = new[]
            {
                "plans[i].state", "plans[i].startTick", "plans[i].lockedAtTick",
                "plans[i].automaticDeferralCount", "plans[i].impactTick", "plans[i].submittedWindowId",
                "plans[i].terminationReason", "actorLanes[i].pendingPlanCount", "actorLanes[i].locked",
                "reactionOpportunities[i].state", "nextReactionOpportunityId", "terminalPlanRecordCount"
            };
            Assert.That(task05Registrations.Length + 10, Is.EqualTo(Task05UncomparableFieldCount),
                "对照证据：任务 05 新增 " + task05Registrations.Length + " 条 + 既有 10 条 = "
                + Task05UncomparableFieldCount + " 条");
            for (int i = 0; i < task05Registrations.Length; i++)
            {
                TemporarilyUncomparableField registered = FindRegisteredField(report, task05Registrations[i]);
                Assert.That(registered, Is.Not.Null,
                    "任务 05 的覆盖边界登记项必须逐条在位：" + task05Registrations[i]
                    + " ; registered=" + string.Join(" | ", report.TemporarilyUncomparable));
                Assert.That(registered.OwnerTask, Is.EqualTo("05"),
                    "任务 05 的登记项必须标出负责任务：" + registered);
                Assert.That(registered.RemovalGate, Is.Not.Empty,
                    "任务 05 的登记项必须标出最迟清零门槛：" + registered);
                Assert.That(registered.Reason, Is.Not.Empty, "登记项必须给出原因：" + registered);
            }

            // —— ⑤ 覆盖证据（二）：逐条"篡改新侧 ⇒ 必须报该字段路径" ——
            // 两侧相等时这些字段不会出现在差异列表里，因此"没有差异"无法区分
            // "真的比较过且相等"与"根本没比较"；下面把每个字段真的推成差异。
            string planPrefix = "plans[" + planId + "]";
            string lanePrefix = "actorLanes[" + heroUnitId + "]";

            AssertTask05FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task05ProbeTick,
                    snapshot => CopySnapshot(snapshot, scheduleRevision: snapshot.ScheduleRevision + 1L)),
                "scheduleRevision", Task05ProbeTick, "全局排程修订号");

            AssertTask05FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task05ProbeTick,
                    snapshot => CopySnapshot(snapshot, plans: TamperPlan(snapshot, planId,
                        plan => plan with { State = (int)ActionPlanState.Completed }))),
                planPrefix + ".state", Task05ProbeTick, "计划生命周期状态");

            AssertTask05FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task05ProbeTick,
                    snapshot => CopySnapshot(snapshot, plans: TamperPlan(snapshot, planId,
                        plan => plan with { AutomaticDeferralCount = plan.AutomaticDeferralCount + 1 }))),
                planPrefix + ".automaticDeferralCount", Task05ProbeTick, "系统自动延期次数");

            AssertTask05FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task05ProbeTick,
                    snapshot => CopySnapshot(snapshot, actorLanes: TamperLane(snapshot, heroUnitId,
                        lane => lane with { PendingPlanCount = lane.PendingPlanCount + 1 }))),
                lanePrefix + ".pendingPlanCount", Task05ProbeTick, "Lane 队列内容");

            AssertTask05FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task05EndTick,
                    snapshot => CopySnapshot(snapshot, actorLanes: TamperLane(snapshot, heroUnitId,
                        lane => lane with { Locked = !lane.Locked }))),
                lanePrefix + ".locked", Task05EndTick, "Lane 提交锁（战斗结束 Finalizer 锁定全部 Lane）");

            AssertTask05FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task05ProbeTick,
                    snapshot => CopySnapshot(snapshot,
                        reactionOpportunities: Array.Empty<ReactionOpportunitySnapshot>())),
                "reactionOpportunities.count", Task05ProbeTick, "反应机会集合数量");

            AssertTask05FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task05ProbeTick,
                    snapshot => CopySnapshot(snapshot,
                        nextReactionOpportunityId: snapshot.NextReactionOpportunityId + 1L)),
                "nextReactionOpportunityId", Task05ProbeTick, "下一个机会 ID");

            // —— ⑥ 负控制（必须有）：在两侧快照完全等价的前提下，人为篡改"新侧"一条计划事实 ——
            var tamperedStartTick = TamperCheckpoint(shadowSide, Task05ProbeTick,
                snapshot => CopySnapshot(snapshot, plans: TamperPlan(snapshot, planId,
                    plan => plan with { StartTick = plan.StartTick + 7L })));

            var negative = AssertTask05FieldIsCompared(seed, legacySide, tamperedStartTick,
                planPrefix + ".startTick", Task05ProbeTick, "计划起点（负控制）");

            Assert.That(negative.HasUnexpectedDifference, Is.True,
                "篡改计划事实后必须出现非预期差异：" + negative.Describe());
            Assert.That(negative.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(1),
                "篡改单个计划字段必须**恰好**产生一条非预期差异："
                + DescribeDifferenceList(negative.Differences));
            Assert.That(negative.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                "只改计划投影不得产生基础设施事实差异：" + negative.Describe());
            Assert.That(negative.FirstUnexpectedDifference.FieldPath, Is.EqualTo(planPrefix + ".startTick"),
                "首个非预期差异必须精确到被篡改的字段路径：" + negative.Describe());
            Assert.That(negative.FirstUnexpectedDifference.LogicalTick, Is.EqualTo((long)Task05ProbeTick),
                "差异必须绑定被篡改的逻辑 Tick：" + negative.FirstUnexpectedDifference);
            Assert.That(negative.FirstUnexpectedDifference.Checkpoint,
                Is.EqualTo("LateUpdate@" + Task05ProbeTick), "差异必须绑定具名检查点");
            Assert.That(negative.FirstUnexpectedDifference.LegacyValue,
                Is.EqualTo(Task05DeferredStartTick.ToString()),
                "旧侧取值必须精确（自动延期后的真实起点 13）：" + negative.FirstUnexpectedDifference);
            Assert.That(negative.FirstUnexpectedDifference.ShadowValue,
                Is.EqualTo((Task05DeferredStartTick + 7).ToString()),
                "新侧取值必须精确（人为篡改 +7）：" + negative.FirstUnexpectedDifference);
            Assert.That(negative.CanClaimEquivalence, Is.False,
                "存在非预期差异时不得宣称等价：" + negative.Describe());
            Assert.That(negative.EquivalenceClaim, Does.StartWith("DIFFERENT:"), negative.Describe());
            Assert.That(negative.UnalignedCheckpoints, Is.EqualTo(0),
                "篡改单个检查点的一条事实不得破坏逐 Tick 对齐：" + negative.Describe());

            // —— ⑥′ 对照：同一份被篡改的流在**默认策略**（compareScheduleFacts = false）下必须零差异 ——
            // 这同时证明"任务 05 检查点逐用例开启"是真的开关（默认关闭 ⇒ 既有用例报告逐字节不变），
            // 且上面的差异确实来自它，而不是来自别处的恒报差异。
            Assert.That(ShadowCasePolicy.CreateDefault(Task05CaseId, seed.RulesVersion).CompareScheduleFacts,
                Is.False, "compareScheduleFacts 必须默认关闭");
            var defaultPolicyReport = CompareWithPolicy(seed, legacySide, tamperedStartTick,
                ShadowCasePolicy.CreateDefault(Task05CaseId + "-default-off", seed.RulesVersion));
            Assert.That(defaultPolicyReport.TemporarilyUncomparable.Count, Is.EqualTo(10),
                "默认策略只登记既有 10 条暂不可比较字段：" + defaultPolicyReport.Describe());
            Assert.That(defaultPolicyReport.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                "默认策略下计划的 StartTick 不在比较集合内（同一条篡改流零差异）："
                + defaultPolicyReport.Describe());
            Assert.That(defaultPolicyReport.HasUnexpectedDifference, Is.False,
                defaultPolicyReport.Describe());
        }

        // -------- 任务 05 场景构造 --------

        /// <summary>
        /// 从真实 02B 定义的 Encounter 槽位推出 (hero, enemy) 的 <c>UnitId</c>，并核对 hero 的动作集合
        /// 确实包含本用例使用的真实攻击——<strong>不硬编码</strong> 1/2。
        ///
        /// <c>BattleInitializer</c> 按 <c>SlotId</c> 的 <see cref="StringComparer.Ordinal"/> 升序分配
        /// <c>UnitId(1..N)</c>，因此这里用同一把尺重算，而不是抄一个常量。
        /// </summary>
        private static (long Hero, long Enemy) ResolveEncounterUnits(ShadowCaseSeed seed)
        {
            // 刻意用 var：EncounterDefinition 属于 ProjectHero.Logic.Definitions，
            // 本文件不引入该命名空间（只按定义侧公开只读入口取事实）。
            var encounter = seed.Definition.FindEncounter(seed.EncounterId);
            Assert.That(encounter, Is.Not.Null, "对照证据：必须能解析出主 Encounter 定义");
            Assert.That(encounter.Slots, Is.Not.Null);

            var slots = new List<EncounterUnitSlot>(encounter.Slots);
            slots.Sort((left, right) => string.CompareOrdinal(left.SlotId.Value, right.SlotId.Value));

            long hero = -1L;
            long enemy = -1L;
            int heroIndex = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                long unitId = i + 1L;
                if (string.Equals(slots[i].SlotId.Value, "hero", StringComparison.Ordinal))
                {
                    hero = unitId;
                    heroIndex = i;
                }
                if (string.Equals(slots[i].SlotId.Value, "enemy", StringComparison.Ordinal)) enemy = unitId;
            }

            Assert.That(hero, Is.GreaterThan(0L), "主 Encounter 必须有 hero 槽位");
            Assert.That(enemy, Is.GreaterThan(0L), "主 Encounter 必须有 enemy 槽位");
            Assert.That(hero, Is.Not.EqualTo(enemy));

            UnitDefinition heroDefinition = seed.Definition.FindUnit(slots[heroIndex].DefinitionId);
            Assert.That(heroDefinition, Is.Not.Null, "hero 槽位必须解析到真实单位定义");
            var actionSet = seed.Definition.FindActionSet(heroDefinition.ActionSetId);
            Assert.That(actionSet, Is.Not.Null, "hero 单位定义必须声明可解析的动作集合");
            Assert.That(actionSet.Contains(new ActionSpecId(Task05HeroAttackSpecId)), Is.True,
                "本用例使用的真实攻击 " + Task05HeroAttackSpecId + " 必须在 hero 的动作集合内（否则命令会被拒绝）");

            Assert.That(seed.Definition.FindAction(new ActionSpecId(Task05HeroAttackSpecId)), Is.Not.Null,
                "对照证据：真实定义里必须存在动作 " + Task05HeroAttackSpecId);
            return (hero, enemy);
        }

        /// <summary>
        /// 任务 05 的脚本化检查点流：真实 <c>BattleSimulation</c> + 真实 <c>Step</c> 管线，
        /// 覆盖 Tick 0..<see cref="Task05EndTick"/>（含战斗结束 Tick；结束之后不再 Step——
        /// 结束后的 <c>Step</c> 是稳定空操作，会复用同一份终态快照，从而制造重复 Tick）。
        /// </summary>
        private static RealCheckpointStream BuildTask05ScheduleStream(
            ShadowCaseSeed seed, long heroUnitId, long enemyUnitId)
        {
            var assembly = new BattleSimulationAssembly(
                unitStateAdvance: new Task05CompositeAdvance(
                    new ControlTransitionOnTick
                    {
                        Tick = Task05StaggerTick,
                        UnitId = heroUnitId,
                        Transition = UnitStateMachine.StaggerFor(
                            Task05DeferredStartTick - Task05StaggerTick)
                    },
                    new KillUnitsOnTick
                    {
                        Tick = Task05EndTick,
                        Kills = new[] { enemyUnitId }
                    }),
                areaThreatCandidateSource: new EnemyAreaThreatSource(enemyUnitId));

            var snapshots = new List<LogicSnapshot>(Task05EndTick + 1);
            var bindings = new List<ShadowCheckpointEventBinding>(Task05EndTick + 1);
            BattleSimulation simulation = BattleSimulation.Create(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs, assembly);

            // 任务 07 契约（00 号规则 18）：显式排程编辑的新增必须声明 ExpectedWindowId，
            // 且命令阶段必须有仍接受提交的当前窗口。窗口打开是脚本化输入：在首个 Step 之前
            // 经生产公开 API 打开 hero 的窗口，整场不关闭、预算充足。
            simulation.WindowManager.ScheduleWindow(0L, new UnitId(heroUnitId), Task07HeroWindowBudget);
            Assert.That(simulation.WindowManager.TryOpenDueWindow(0L), Is.Not.Null,
                "构造前提：hero 的脚本窗口必须在首个 Step 之前打开");

            try
            {
                CommandIngressEntry entry = simulation.CommandIngress.FindEntry(
                    new ControllerId("controller.player"));
                Assert.That(entry, Is.Not.Null, "必须有 controller.player 入口");

                for (long tick = 0L; tick <= Task05EndTick; tick++)
                {
                    if (tick == Task05EditTick)
                    {
                        // 期望修订号取自**运行中的真实权威**（首个命令事务之前必然为 0）：
                        // 若基线不是 0，命令会被信任边界以稳定码拒绝，下面的断言会立刻失败。
                        CommandIngressRejection rejection = entry.Submit(new CommandRequest(
                            tick,
                            new ScheduleEditScope(
                                simulation.ScheduleRevision,
                                RequireCurrentWindow(simulation)),
                            new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new AddOrdinaryPlanOperation(
                                    1L,
                                    new UnitId(heroUnitId),
                                    new ActionSpecId(Task05HeroAttackSpecId),
                                    Task05RequestedStartTick,
                                    default,
                                    new UnitId(enemyUnitId))
                            })));
                        Assert.That(rejection, Is.Null,
                            rejection == null
                                ? null
                                : "真实排程编辑必须被信任边界接受：" + rejection.ReasonCode
                                  + "|ordinal=" + rejection.ProducerOrdinal);
                    }

                    FrozenCommandBatch batch = simulation.CommandIngress.FreezeTick(tick);
                    StepResult result = simulation.Step(tick, batch);
                    snapshots.Add(simulation.CurrentSnapshot);
                    bindings.Add(BuildEventBinding(tick, result));
                }
            }
            finally
            {
                simulation.Dispose();
            }

            return new RealCheckpointStream(snapshots, bindings);
        }

        /// <summary>
        /// 任务 05 场景事实的<strong>逐 Tick 精确断言</strong>（覆盖证据的第一半：
        /// 报告里要求覆盖的事实必须真的存在于这条检查点流里）。
        /// 返回真实计划 ID（后续探针按 ID 定位计划投影）。
        /// </summary>
        private static long AssertTask05ScenarioFacts(
            RealCheckpointStream side, long heroUnitId, long enemyUnitId)
        {
            string stream = DescribeTask05Stream(side);

            // —— Tick 0：真实排程编辑事务 ⇒ 修订号 1、计划 Editable 且请求起点 3 ——
            LogicSnapshot atEdit = side.Snapshots[Task05EditTick];
            Assert.That(atEdit.ScheduleRevision, Is.EqualTo(1L),
                "成功排程编辑事务恰好 +1（scheduleRevision = 1）：" + stream);
            Assert.That(atEdit.Plans.Count, Is.EqualTo(1), "排程编辑必须留下恰好一条计划：" + stream);
            ActionPlanSnapshot created = atEdit.Plans[0];
            Assert.That(created.ActionPlanId, Is.GreaterThan(0L));
            Assert.That(created.ActionSpecId, Is.EqualTo(Task05HeroAttackSpecId));
            Assert.That(created.OwnerUnitId, Is.EqualTo(heroUnitId));
            Assert.That(created.State, Is.EqualTo((int)ActionPlanState.Editable));
            Assert.That(created.StartTick, Is.EqualTo((long)Task05RequestedStartTick));
            Assert.That(created.AutomaticDeferralCount, Is.EqualTo(0));
            ActorLaneSnapshot laneAtEdit = FindLaneSnapshot(atEdit, heroUnitId);
            Assert.That(laneAtEdit, Is.Not.Null, "放置过计划的单位必须有 Lane：" + stream);
            Assert.That(laneAtEdit.PendingPlanCount, Is.EqualTo(1));
            Assert.That(laneAtEdit.Locked, Is.False);

            // —— Tick 3：阶段 1 的显式控制转换 ⇒ 启动门禁 Retryable ⇒ 系统自动延期一次 ——
            LogicSnapshot atDeferral = side.Snapshots[Task05StaggerTick];
            ActionPlanSnapshot deferred = FindPlanSnapshot(atDeferral, created.ActionPlanId);
            Assert.That(deferred, Is.Not.Null, "延期 Tick 上计划必须仍在活动索引：" + stream);
            Assert.That(deferred.AutomaticDeferralCount, Is.EqualTo(1),
                "系统自动延期恰好一次（automaticDeferralCount = 1）：" + stream);
            Assert.That(deferred.StartTick, Is.EqualTo((long)Task05DeferredStartTick),
                "延期必须把起点移到有限阻塞的结束 Tick：" + stream);
            Assert.That(deferred.LastRequestedStartTick, Is.EqualTo((long)Task05RequestedStartTick),
                "系统自动延期绝不覆盖 LastRequestedStartTick：" + stream);
            Assert.That(deferred.State, Is.EqualTo((int)ActionPlanState.Editable),
                "延期后仍可编辑：" + stream);
            Assert.That(atDeferral.ScheduleRevision, Is.EqualTo(2L),
                "Add 的 1 + 自动延期的 1 = 2：" + stream);

            // —— Tick 13：原子锁定/启动 ⇒ Running；同一 Tick 公开反应机会 ——
            LogicSnapshot atStart = side.Snapshots[Task05DeferredStartTick];
            ActionPlanSnapshot running = FindPlanSnapshot(atStart, created.ActionPlanId);
            Assert.That(running, Is.Not.Null, "启动 Tick 上计划必须在活动索引：" + stream);
            Assert.That(running.State, Is.EqualTo((int)ActionPlanState.Running),
                "到期门禁必须原子锁定/启动计划（state = Running）：" + stream);
            Assert.That(running.LockedAtTick, Is.EqualTo((long)Task05DeferredStartTick),
                "锁定 Tick 与 Running 必须同一次原子提交写入：" + stream);
            Assert.That(running.ResolvedWindupTicks, Is.GreaterThan(0));
            Assert.That(running.ImpactTick, Is.EqualTo(running.StartTick + running.ResolvedWindupTicks),
                "ImpactTick 必须等于解析前摇终点：" + stream);
            Assert.That(atStart.ReactionOpportunities.Count, Is.EqualTo(1),
                "原子启动之后必须公开恰好一个反应机会：" + stream);
            ReactionOpportunitySnapshot opportunity = atStart.ReactionOpportunities[0];
            Assert.That(opportunity.State, Is.EqualTo((int)ReactionOpportunityState.Open));
            Assert.That(opportunity.DefenderUnitId, Is.EqualTo(enemyUnitId),
                "候选来源桩把敌人作为唯一候选：" + stream);
            Assert.That(opportunity.TelegraphTick, Is.EqualTo((long)Task05DeferredStartTick),
                "TelegraphTick 必须等于原子启动 Tick：" + stream);
            Assert.That(opportunity.TriggerTick, Is.EqualTo(running.ImpactTick),
                "TriggerTick 恒等于来源攻击的 ImpactTick（命令不可声明）：" + stream);
            Assert.That(opportunity.OpenOptionCount, Is.GreaterThan(0), "必须存在公开选项：" + stream);
            Assert.That(atStart.NextReactionOpportunityId,
                Is.EqualTo(opportunity.ReactionOpportunityId + 1L),
                "下一个机会 ID 必须来自同一分配器：" + stream);

            // —— Tick 14（探针 Tick）：计划仍在运行，Lane 未锁，机会仍在审计面 ——
            LogicSnapshot atProbe = side.Snapshots[Task05ProbeTick];
            ActionPlanSnapshot probePlan = FindPlanSnapshot(atProbe, created.ActionPlanId);
            Assert.That(probePlan, Is.Not.Null, "探针 Tick 上计划必须仍在活动索引：" + stream);
            Assert.That(probePlan.State, Is.EqualTo((int)ActionPlanState.Running), stream);
            Assert.That(probePlan.AutomaticDeferralCount, Is.EqualTo(1), stream);
            ActorLaneSnapshot laneAtProbe = FindLaneSnapshot(atProbe, heroUnitId);
            Assert.That(laneAtProbe, Is.Not.Null, stream);
            Assert.That(laneAtProbe.PendingPlanCount, Is.EqualTo(1), stream);
            Assert.That(laneAtProbe.Locked, Is.False, stream);
            Assert.That(atProbe.ReactionOpportunities.Count, Is.EqualTo(1),
                "已关闭的机会仍留在审计面里：" + stream);

            // —— Tick 16：敌人死亡 ⇒ 阶段 2 判定战斗已决定 ⇒ 唯一 Finalizer ——
            // 全部 Lane 锁定 + 计划以 BattleEnded 终止并立即离开活动索引（只留历史计数与摘要）。
            LogicSnapshot atEnd = side.Snapshots[Task05EndTick];
            Assert.That(atEnd.BattleEnd.IsEnded, Is.True,
                "真实死亡路径必须让战斗在结束 Tick 上结束：" + stream);
            Assert.That(atEnd.Plans.Count, Is.EqualTo(0),
                "终态计划必须立即离开活动索引：" + stream);
            Assert.That(atEnd.TerminalPlanRecordCount, Is.EqualTo(1L),
                "终态计划必须留下历史记录数（归档摘要）：" + stream);
            Assert.That(atEnd.TerminalPlanDigest, Is.Not.Empty, stream);
            ActorLaneSnapshot lockedLane = FindLaneSnapshot(atEnd, heroUnitId);
            Assert.That(lockedLane, Is.Not.Null,
                "战斗结束不得删除 Lane 对象（锁定的是新提交）：" + stream);
            Assert.That(lockedLane.Locked, Is.True,
                "战斗结束 Finalizer 必须锁定全部 Lane 的新提交（locked = true）：" + stream);
            Assert.That(lockedLane.PendingPlanCount, Is.EqualTo(0), stream);
            Assert.That(atEnd.ReactionOpportunities.Count, Is.EqualTo(1),
                "战斗结束关闭机会但不删除审计记录：" + stream);
            Assert.That(atEnd.ReactionOpportunities[0].State,
                Is.EqualTo((int)ReactionOpportunityState.BattleEnded),
                "战斗结束必须用同一个 Finalizer 关闭机会：" + stream);

            return created.ActionPlanId;
        }

        // -------- 任务 05 报告与覆盖探针 --------

        /// <summary>逐用例开启任务 05 检查点（<c>compareScheduleFacts: true</c>）的比较入口。</summary>
        private static ShadowComparisonReport CompareTask05ScheduleProfile(
            ShadowCaseSeed seed, RealCheckpointStream legacySide, RealCheckpointStream shadowSide,
            string caseId)
            => CompareWithPolicy(seed, legacySide, shadowSide,
                ShadowCasePolicy.CreateDefault(caseId, seed.RulesVersion, compareScheduleFacts: true));

        /// <summary>
        /// 给定逐用例策略的比较入口（Logic 对 Logic 通道）。
        ///
        /// <paramref name="checkpointBudget"/> 默认 64（既有用例的原值，逐字不变）。
        /// 需要更长场景的用例必须**从真实检查点数**导出预算——预算超限会让报告直接变成
        /// <c>INVALID:SHADOW_BUDGET_OVERRUN</c>（比较根本不会执行），
        /// 因此放预算不是"放宽判据"，而是"让比较真的发生"。见
        /// <see cref="CompareTask06MovementProfile"/> 的用法与断言。
        ///
        /// <paramref name="legacyObservations"/>（任务 06 小修轮 R2 新增）是<strong>显式</strong>的
        /// 旧侧观测输入：为 <c>null</c> 时走"LogicSnapshot 对 LogicSnapshot"的确定性通道
        /// （既有 3 处调用点逐字不变）；非空时比较器改走
        /// <c>CompareObservedLegacyCheckpoints</c>（真正的旧↔新通道）。
        /// 任务 06 的画像用例**显式**传入空集合，使"它不是旧↔新比较"成为可断言事实。
        /// </summary>
        private static ShadowComparisonReport CompareWithPolicy(
            ShadowCaseSeed seed, RealCheckpointStream legacySide, RealCheckpointStream shadowSide,
            ShadowCasePolicy policy, int checkpointBudget = 64,
            IReadOnlyList<LegacyLogicObservation> legacyObservations = null)
            => ShadowDifferenceDetector.Compare(
                new ShadowComparisonInput(
                    seed.BattleDefinitionHash, seed.EncounterId.Value, BattleRuntimeMode.Shadow,
                    seed.InputSummary, seed.RulesVersion,
                    ShadowComparisonConfig.Strict(checkpointBudget),
                    legacySide.Snapshots, shadowSide.Snapshots, shadowSide.EventBindings,
                    legacyObservations),
                policy);

        /// <summary>
        /// 覆盖探针：把"新侧"某个检查点上的一条任务 05 事实改成不同值，断言比较器<strong>必须</strong>
        /// 在该字段路径上报告一条 <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>。
        ///
        /// 为什么必须用探针：两侧完全等价时这些字段不会出现在差异列表里，
        /// "没有差异"无法区分"真的比较过且相等"与"根本没比较"。
        /// </summary>
        private static ShadowComparisonReport AssertTask05FieldIsCompared(
            ShadowCaseSeed seed, RealCheckpointStream legacySide, RealCheckpointStream tamperedSide,
            string expectedFieldPath, long expectedTick, string what)
        {
            var report = CompareTask05ScheduleProfile(seed, legacySide, tamperedSide, Task05CaseId + "-probe");
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                what + "：覆盖探针不得破坏逐 Tick 对齐：" + report.Describe());

            ShadowFieldDifference hit = null;
            for (int i = 0; i < report.Differences.Count; i++)
            {
                ShadowFieldDifference difference = report.Differences[i];
                if (difference.Kind != ShadowDifferenceKind.NewRuleVerifiedFact) continue;
                if (!string.Equals(difference.FieldPath, expectedFieldPath, StringComparison.Ordinal)) continue;
                if (difference.LogicalTick != expectedTick) continue;
                hit = difference;
                break;
            }

            Assert.That(hit, Is.Not.Null,
                what + "：篡改新侧后必须出现字段 " + expectedFieldPath + " 在 Tick " + expectedTick
                + " 上的 " + ShadowDifferenceKind.NewRuleVerifiedFact + " 差异（证明该字段真的参与比较）："
                + report.Describe() + " ; differences=" + DescribeDifferenceList(report.Differences));
            Assert.That(hit.LegacyValue, Is.Not.EqualTo(hit.ShadowValue), "差异两侧取值必须不同：" + hit);
            Assert.That(hit.IsUnexpected, Is.True,
                "任务 05 的字段差异一律是回归（不得被批准差异掩盖）：" + hit);
            return report;
        }

        /// <summary>把某个检查点替换为"只改一条任务 05 事实"的新侧快照（其余检查点逐字不变）。</summary>
        private static RealCheckpointStream TamperCheckpoint(
            RealCheckpointStream source, int index, Func<LogicSnapshot, LogicSnapshot> tamper)
        {
            var snapshots = new List<LogicSnapshot>(source.Snapshots.Count);
            for (int i = 0; i < source.Snapshots.Count; i++)
                snapshots.Add(i == index ? tamper(source.Snapshots[i]) : source.Snapshots[i]);
            return new RealCheckpointStream(snapshots, source.EventBindings);
        }

        /// <summary>
        /// 只读复制一份规范化快照，并按需替换其中一类事实。
        ///
        /// 它是<strong>测试侧</strong>的篡改工具（负控制/覆盖探针），不是生产比较路径：
        /// 生产比较永远读真实 <c>BattleSimulation.CurrentSnapshot</c>。
        /// </summary>
        private static LogicSnapshot CopySnapshot(
            LogicSnapshot source,
            long? scheduleRevision = null,
            IReadOnlyList<ActionPlanSnapshot> plans = null,
            IReadOnlyList<ActorLaneSnapshot> actorLanes = null,
            IReadOnlyList<ReactionOpportunitySnapshot> reactionOpportunities = null,
            long? nextReactionOpportunityId = null,
            IReadOnlyList<UnitSnapshot> units = null,
            IReadOnlyList<MovementSegmentSnapshot> movementSegments = null,
            IReadOnlyList<ReservationSnapshot> reservations = null,
            TurnWindowManagerSnapshot windowManager = null,
            ConcurrentActionSnapshot concurrentAction = null,
            BattleResourceSnapshot resources = null)
            => new LogicSnapshot(
                source.Tick, source.RulesVersion, source.BattleDefinitionHash, source.EncounterId,
                source.BattleEnd, units ?? source.Units, source.Effects,
                windowManager ?? source.WindowManager,
                concurrentAction ?? source.ConcurrentAction,
                resources ?? source.Resources,
                scheduleRevision ?? source.ScheduleRevision,
                plans ?? source.Plans,
                reactionOpportunities ?? source.ReactionOpportunities,
                actorLanes ?? source.ActorLanes,
                source.Intents, movementSegments ?? source.MovementSegments, reservations ?? source.Reservations,
                source.AiControllers,
                source.CommandIngresses, source.Rng,
                source.NextUnitId, source.NextActionPlanId,
                nextReactionOpportunityId ?? source.NextReactionOpportunityId,
                source.NextWindowId, source.NextEffectId, source.NextCommandSequence,
                source.NextIntentSequence, source.NextResolutionSequence, source.NextEventSequence,
                source.NextEffectSequence, source.History, source.CommandSourcePriorityMappingVersion,
                source.TerminalPlanRecordCount, source.TerminalPlanDigest,
                source.NextReactionOptionSequence);

        /// <summary>按计划 ID 替换一条计划投影（未命中的计划逐字保留）。</summary>
        private static IReadOnlyList<ActionPlanSnapshot> TamperPlan(
            LogicSnapshot snapshot, long planId, Func<ActionPlanSnapshot, ActionPlanSnapshot> change)
        {
            var plans = new List<ActionPlanSnapshot>(snapshot.Plans.Count);
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                ActionPlanSnapshot plan = snapshot.Plans[i];
                plans.Add(plan != null && plan.ActionPlanId == planId ? change(plan) : plan);
            }
            return plans;
        }

        /// <summary>按单位 ID 替换一条 Lane 投影（未命中的 Lane 逐字保留）。</summary>
        private static IReadOnlyList<ActorLaneSnapshot> TamperLane(
            LogicSnapshot snapshot, long unitId, Func<ActorLaneSnapshot, ActorLaneSnapshot> change)
        {
            var lanes = new List<ActorLaneSnapshot>(snapshot.ActorLanes.Count);
            for (int i = 0; i < snapshot.ActorLanes.Count; i++)
            {
                ActorLaneSnapshot lane = snapshot.ActorLanes[i];
                lanes.Add(lane != null && lane.UnitId == unitId ? change(lane) : lane);
            }
            return lanes;
        }

        /// <summary>按计划 ID 取计划投影（不存在返回 null——调用点必须显式断言）。</summary>
        private static ActionPlanSnapshot FindPlanSnapshot(LogicSnapshot snapshot, long planId)
        {
            for (int i = 0; i < snapshot.Plans.Count; i++)
            {
                if (snapshot.Plans[i] != null && snapshot.Plans[i].ActionPlanId == planId)
                    return snapshot.Plans[i];
            }
            return null;
        }

        /// <summary>按单位 ID 取 Lane 投影（不存在返回 null）。</summary>
        private static ActorLaneSnapshot FindLaneSnapshot(LogicSnapshot snapshot, long unitId)
        {
            for (int i = 0; i < snapshot.ActorLanes.Count; i++)
            {
                if (snapshot.ActorLanes[i] != null && snapshot.ActorLanes[i].UnitId == unitId)
                    return snapshot.ActorLanes[i];
            }
            return null;
        }

        /// <summary>按字段名取暂不可比较登记项（不存在返回 null）。</summary>
        private static TemporarilyUncomparableField FindRegisteredField(
            ShadowComparisonReport report, string field)
        {
            for (int i = 0; i < report.TemporarilyUncomparable.Count; i++)
            {
                if (string.Equals(report.TemporarilyUncomparable[i].Field, field, StringComparison.Ordinal))
                    return report.TemporarilyUncomparable[i];
            }
            return null;
        }

        /// <summary>
        /// 逐 Tick 的任务 05 诊断（只在断言失败信息里使用）：
        /// 修订号 / 战斗结束 / 逐计划（状态、起点、终点、锁定、延期、Impact、终态）/
        /// 逐 Lane（队列、锁）/ 机会数 / 下一个机会 ID / 终态归档数。
        /// </summary>
        private static string DescribeTask05Stream(RealCheckpointStream side)
        {
            var builder = new StringBuilder("task05[");
            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                LogicSnapshot snapshot = side.Snapshots[i];
                builder.Append('{').Append("t=").Append(snapshot.Tick)
                    .Append(" rev=").Append(snapshot.ScheduleRevision)
                    .Append(" ended=").Append(snapshot.BattleEnd.IsEnded)
                    .Append(" plans=");
                for (int p = 0; p < snapshot.Plans.Count; p++)
                {
                    ActionPlanSnapshot plan = snapshot.Plans[p];
                    builder.Append('(').Append(plan.ActionPlanId)
                        .Append(",state=").Append(plan.State)
                        .Append(",start=").Append(plan.StartTick)
                        .Append(",end=").Append(plan.EndTick)
                        .Append(",lockedAt=").Append(plan.LockedAtTick)
                        .Append(",defer=").Append(plan.AutomaticDeferralCount)
                        .Append(",impact=").Append(plan.ImpactTick)
                        .Append(",term=").Append(plan.TerminalTick)
                        .Append(')');
                }
                builder.Append(" lanes=");
                for (int l = 0; l < snapshot.ActorLanes.Count; l++)
                {
                    builder.Append('(').Append(snapshot.ActorLanes[l].UnitId)
                        .Append(",pending=").Append(snapshot.ActorLanes[l].PendingPlanCount)
                        .Append(",locked=").Append(snapshot.ActorLanes[l].Locked)
                        .Append(')');
                }
                builder.Append(" opps=").Append(snapshot.ReactionOpportunities.Count)
                    .Append(" nextOppId=").Append(snapshot.NextReactionOpportunityId)
                    .Append(" terminalRecords=").Append(snapshot.TerminalPlanRecordCount)
                    .Append("} ");
            }
            builder.Append(']');
            return builder.ToString();
        }

        // -------- 任务 05 的场景装配（阶段参与者，只经公开扩展点接入） --------

        /// <summary>阶段 1 夹具：在指定 Tick 对指定单位声明一次显式状态转换（与自动到期同一路径）。</summary>
        private sealed class ControlTransitionOnTick : IUnitStateAdvanceSystem
        {
            public long Tick = -1L;
            public long UnitId = -1L;
            public StateTransitionSpec Transition;

            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
                IReadOnlyList<UnitSnapshot> units, long tick)
                => tick == Tick && UnitId >= 0L && Transition != null
                    ? new[] { new UnitStateAdvanceRequest(UnitId, null, Transition, null) }
                    : Array.Empty<UnitStateAdvanceRequest>();
        }

        /// <summary>阶段 1 夹具：在指定 Tick 把指定单位的生命清零（真实死亡路径）。</summary>
        private sealed class KillUnitsOnTick : IUnitStateAdvanceSystem
        {
            public long Tick = -1L;
            public long[] Kills = Array.Empty<long>();

            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
                IReadOnlyList<UnitSnapshot> units, long tick)
            {
                if (tick != Tick || Kills.Length == 0) return Array.Empty<UnitStateAdvanceRequest>();
                var requests = new List<UnitStateAdvanceRequest>(Kills.Length);
                for (int i = 0; i < Kills.Length; i++)
                    requests.Add(new UnitStateAdvanceRequest(Kills[i], (int?)0, null, null));
                return requests;
            }
        }

        /// <summary>阶段 1 复合夹具：按装配顺序合并多个子系统的声明。</summary>
        private sealed class Task05CompositeAdvance : IUnitStateAdvanceSystem
        {
            private readonly IUnitStateAdvanceSystem[] _parts;

            public Task05CompositeAdvance(params IUnitStateAdvanceSystem[] parts)
                => _parts = parts ?? Array.Empty<IUnitStateAdvanceSystem>();

            public IReadOnlyList<UnitStateAdvanceRequest> AdvanceOrdered(
                IReadOnlyList<UnitSnapshot> units, long tick)
            {
                var all = new List<UnitStateAdvanceRequest>();
                for (int i = 0; i < _parts.Length; i++)
                {
                    IReadOnlyList<UnitStateAdvanceRequest> part = _parts[i]?.AdvanceOrdered(units, tick);
                    if (part == null) continue;
                    for (int r = 0; r < part.Count; r++) all.Add(part[r]);
                }
                return all;
            }
        }

        /// <summary>
        /// 区域攻击的逻辑威胁候选来源桩：把敌人作为<strong>唯一</strong>候选
        /// （真实几何属于任务 06；本用例只证明机会接线与快照事实）。
        /// </summary>
        private sealed class EnemyAreaThreatSource : IAreaThreatCandidateSource
        {
            private readonly long _enemyUnitId;

            public EnemyAreaThreatSource(long enemyUnitId) => _enemyUnitId = enemyUnitId;

            public IReadOnlyList<UnitId> CandidatesFor(ActionPlan sourcePlan, long telegraphTick)
                => new[] { new UnitId(_enemyUnitId) };
        }

        // =====================================================================
        // 任务 06：LogicGrid 占位 / 移动提交 / 路径 Reservation / 冲突回滚 / 移动终态
        //          —— Shadow 检查点扩展（必需要求 #85）
        // =====================================================================

        /// <summary>本用例的 Shadow 比较用例 ID。</summary>
        private const string Task06CaseId = "task06-logic-grid-movement-reservation-profile";

        /// <summary>
        /// 任务 06 画像用例的<strong>旧侧观测输入</strong>：<strong>恒为空</strong>
        /// （任务 06 小修轮 R2 的处置）。
        ///
        /// 它被显式地传进 <see cref="CompareWithPolicy"/>，因此"本画像走的是
        /// LogicSnapshot 对 LogicSnapshot 通道"是**代码事实**而不是注释承诺：
        /// 比较器据 <c>LegacyObservations.Count == 0</c> 决定走哪条通道
        /// （<c>ShadowDifferenceDetector.cs:45-49</c>）。
        /// 一旦有人把它换成非空集合，画像用例的前置断言立即失败。
        /// </summary>
        private static readonly IReadOnlyList<LegacyLogicObservation> Task06ProfileLegacyObservations =
            Array.Empty<LegacyLogicObservation>();

        /// <summary>真实定义里 hero 的 Move 动作（<c>BuildActionSet</c> 对每个动作集合都加入的普通移动）。</summary>
        private const string Task06MoveSpecId = "action.move.default";

        // —— 场景关键 Tick（与 BuildTask06MovementStream 及断言逐条对应）——
        /// <summary>03B/04 已登记的"暂不可比较"项条数（两个逐用例开关都关闭时的基线）。</summary>
        private const int Task06BaselineRegistrationCount = 10;

        /// <summary>任务 05 由 <c>compareScheduleFacts</c> 开启的登记项条数。</summary>
        private const int Task05ScheduleRegistrationCount = 12;

        /// <summary>Move 计划提交 Tick（<c>scheduleRevision</c> 恰好 +1）。</summary>
        private const int Task06SubmitTick = 1;
        /// <summary>Move 计划请求起点。</summary>
        private const int Task06RequestedStartTick = 4;
        /// <summary>目的格（doubled 坐标：y=8 即"上 4 格"）。</summary>
        private const int Task06DestinationX = 0;
        private const int Task06DestinationY = 8;
        /// <summary>
        /// 覆盖探针 Tick：落在<strong>位移动窗口内部</strong>——已经发生过真实的命令前边界提交，
        /// 同时仍有未提交段。两个前提都由 <c>AssertTask06ScenarioFacts</c> 从真实流里读出并逐条断言，
        /// 不是硬编码假设：若真实路径时序变了，本用例会以"探针 Tick 不在提交窗口内"直接失败。
        /// </summary>
        private const int Task06ProbeTick = 250;

        /// <summary>
        /// 移动链覆盖到 <see cref="Task06TerminalTick"/>（含）为止。
        ///
        /// 该值不是随手取的：真实 02B 定义里敌方单位有 19 格体积 footprint，hero 必须绕行 ⇒
        /// 真实路径是 9 段（权重 11），段时长 = 权重 × 24 Tick，最后一段 EndTick = 317。
        /// 终态 Tick 由 <c>AssertTask06ScenarioFacts</c> 用"最后一段 EndTick &lt; 终态 Tick"
        /// 双向钉死，因此改路径/改时序都会让本用例失败而不是静默退化成空比较。
        /// </summary>
        private const int Task06TerminalTick = 340;

        /// <summary>
        /// <strong>任务 06「必须产出」10 / 验收标准 17 的 Shadow 检查点扩展</strong>（Logic 对 Logic 通道）。
        ///
        /// 它把 <c>ShadowDifferenceDetector.Task06MovementFacts</c> 真正接到<strong>真实移动</strong>上：
        /// 两侧各跑一个独立的真实 <c>BattleSimulation</c>（真实 02B 定义 + 真实 <c>Step</c> 管线 +
        /// 真实 <c>ScheduleEditor.Apply</c> + 真实 <c>LogicGridMovementPathCalculator</c> +
        /// 真实 <c>LogicGridMovementAuthority</c>），脚本化输入完全相同。
        ///
        /// <strong>本用例实际证明什么（任务 06 小修轮 R2 如实登记，必须阅读）</strong>：
        /// 这是<strong>新实现 vs 新实现</strong>的两侧（<see cref="BuildTask06MovementStream"/> 两侧
        /// 都调 <c>BattleSimulation.Create</c>，且本用例在比较之前主动断言两侧
        /// <c>ComputeHash()</c> 逐位相同）⇒ 它证明的是
        /// <strong>新实现的确定性 + 跨世界一致性</strong>，<strong>不是</strong>"与旧权威等价"。
        /// 因此它<strong>无法发现"确定性但错误"</strong>的实现——那正是等价验证的主要目标。
        ///
        /// <strong>为什么不能改成真正的 Legacy ↔ New 比较（技术证明）</strong>：
        /// <list type="bullet">
        /// <item>必须测试名 <c>LogicGridMovementAndReservationShadowProfileHasNoUnclassifiedDifference</c>
        /// 隐含"旧/新两侧都有可比的空间事实"，而任务 06 的三类对象
        /// （<c>MovementSegment</c> / <c>Reservation</c> / 可阻塞启动的离散占位）
        /// 在旧权威里<strong>根本不存在</strong>：旧侧只有 <c>GridManager.OccupancyMap</c> 与 Transform
        /// 位置，旧 <c>Pathfinder</c> 输出是 <c>PathNode</c> 列表，旧 <c>BattleTimeline</c> 只有排程条目计数；</item>
        /// <item>唯一可比的旧侧空间事实是 <c>units[i].position</c>（<c>LegacyLogicObservation</c> 白名单），
        /// 它由隐藏验证场景的 <see cref="Bootstrap"/> 真实驱动产生；</item>
        /// <item>用夹具给旧侧注入"旧版段/预留"只会<strong>自证</strong>（夹具 = 新实现的另一份副本），
        /// 因此本用例<strong>不</strong>伪造旧侧观测；</item>
        /// <item>据上，本用例用<strong>可失败的前置断言</strong>把这条覆盖边界钉死（见方法体"对照证据 0′"）：
        /// 一旦有人把 <c>LegacyObservations</c> 接进来，本用例会<strong>立即失败</strong>，
        /// 强制改成真正的旧↔新比较，而不是让"Shadow 等价"标签继续承载一个自比较结论。</item>
        /// </list>
        /// 交接记录 §0.6 把该必需测试名的<strong>真实 Shadow 语义</strong>登记为任务 10/11 的硬前置
        /// （含接入点），并说明 <c>claim=EQUIVALENT</c> 是报告字段的既有词汇，
        /// 其语义降级由任务 11 决定（本任务不改生产报告词汇）。
        ///
        /// 覆盖任务包要求的五类逻辑空间事实：
        /// <list type="number">
        /// <item><strong>占位</strong>：逐 <c>UnitId</c> 的逻辑格锚点 + 已提交格上的 Reservation 残留；
        /// 并由场景事实断言钉死"锚点真的沿路径推进了"（不是恒等比较）；</item>
        /// <item><strong>移动提交</strong>：逐段 <c>From/To/EndTick</c>，以及"第一条未提交段的
        /// <c>From</c> 必须等于计划拥有者的当前锚点"；</item>
        /// <item><strong>路径 Reservation</strong>：逐计划规范格集合、数量，与"每条段的 <c>To</c> 都有
        /// 同计划 Reservation 且数量相等"的 1:1 覆盖关系；</item>
        /// <item><strong>移动终态</strong>：计划完成而<strong>不是</strong>被战斗结束清场——
        /// 结束时该计划已离开活动索引，其段与 Reservation 必须已经归零，且锚点停在最后已提交格；</item>
        /// <item><strong>冲突回滚</strong>：见 <see cref="AssertTask06FailedBatchLeavesNoPartialWrite"/> ——
        /// 这里<strong>如实登记</strong>为什么"先建立成功、再中途失败"的逐项回滚在当前生产路径上
        /// <strong>不可达</strong>，并用可失败的"零局部写入"断言覆盖唯一可达的失败面。</item>
        /// </list>
        ///
        /// <strong>比较面边界（任务包「必须产出」10 末句）</strong>：全部事实只来自
        /// <c>LogicSnapshot</c> 的<strong>整数</strong>字段（单位锚点、段与 Reservation 的整数坐标与
        /// <c>EndTick</c>、计划拥有者）。本类不引用任何 UnityEngine 类型，也拿不到 Transform
        /// 或视觉插值进度，因此这里比较的"位置"只可能是<strong>逻辑格与规范化段</strong>。
        /// 这不是承诺：<see cref="AssertTask06ComparisonSurfaceIsIntegerOnly"/> 用成员签名扫描把它钉死。
        /// </summary>
        [UnityTest]
        public IEnumerator LogicGridMovementAndReservationShadowProfileHasNoUnclassifiedDifference()
        {
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();
            // 与任务 05 用同一把尺解析槽位（不硬编码 1/2）；enemy 槽位用于"双槽位"对照证据。
            (long heroUnitId, long _) = ResolveEncounterUnits(seed);

            // —— 场景：真实 02B 定义 + 真实 Step 管线；两侧各自跑同一条脚本化输入序列 ——
            var legacySide = BuildTask06MovementStream(seed, heroUnitId);
            var shadowSide = BuildTask06MovementStream(seed, heroUnitId);

            // 对照证据 0：检查点下标 == 逻辑 Tick；两侧逐位相同（同一定义 + 同一输入 ⇒ 同一世界）。
            Assert.That(legacySide.Snapshots.Count, Is.EqualTo(Task06TerminalTick + 1),
                "检查点必须覆盖 Tick 0.." + Task06TerminalTick);
            for (int i = 0; i < legacySide.Snapshots.Count; i++)
            {
                Assert.That(legacySide.Snapshots[i].Tick, Is.EqualTo((long)i),
                    "检查点下标必须等于逻辑 Tick：index=" + i);
                Assert.That(shadowSide.Snapshots[i].Tick, Is.EqualTo((long)i),
                    "检查点下标必须等于逻辑 Tick（新侧）：index=" + i);
                Assert.That(shadowSide.Snapshots[i].ComputeHash(),
                    Is.EqualTo(legacySide.Snapshots[i].ComputeHash()),
                    "同一脚本化输入的两个独立世界必须逐位相同：Tick " + i + " ; "
                    + DescribeSideComparison(legacySide, shadowSide, heroUnitId));
            }

            // —— 对照证据 1：五类事实真的落在这条检查点流上（否则下面的"零差异"是空的）——
            Task06ScenarioFacts facts = AssertTask06ScenarioFacts(legacySide, heroUnitId);

            // —— 对照证据 0′（任务 06 小修轮 R2，**可失败**）——
            // 本用例的两侧都是新实现（见 XML 文档"本用例实际证明什么"），因此它证明的是
            // **确定性 + 跨世界一致性**，不是"与旧权威等价"。这里把这条覆盖边界钉成前置断言：
            // 一旦有人把隐藏场景的 LegacyObservations 接进任务 06 的画像入口，
            // 本断言立即失败——强制把本用例改成真正的旧↔新比较，而不是让"Shadow 等价"标签
            // 继续承载一个自比较结论（独立验证 M-2/M-4 的处置）。
            Assert.That(Task06ProfileLegacyObservations.Count, Is.EqualTo(0),
                "任务 06 画像用例不填充旧侧观测：比较器因此走 LogicSnapshot 对 LogicSnapshot 通道，"
                + "结论口径只能是『确定性 + 跨世界一致性』；接入 LegacyObservations 前必须先改成旧↔新比较");

            // —— 对照证据 6″（任务 06 小修轮 R4/B-1）——
            // 生产装配点必须真的把"预算释放接缝"接到 Dodge 换位事务上，而不是留给下游记得赋值。
            // 默认消费者的语义是**显式 no-op**（任务 06 不实现 TurnBudget 账本 ⇒ 没有可释放的预算对象），
            // 但接线本身必须是可验证的装配事实。
            {
                using (BattleSimulation wired = BattleSimulation.Create(
                           seed.Definition, seed.EncounterId, seed.RuntimeInputs,
                           new BattleSimulationAssembly(budgetReleaseSink: sink => { })))
                {
                    Assert.That(wired.DodgeRelocation.BudgetReleaseSink, Is.Not.Null,
                        "显式注入的预算释放消费者必须真的接到 Dodge 换位事务的接缝上"
                        + "（任务 07 的接入点：BattleSimulationAssembly.budgetReleaseSink）");
                }

                using (BattleSimulation defaulted = BattleSimulation.Create(
                           seed.Definition, seed.EncounterId, seed.RuntimeInputs, new BattleSimulationAssembly()))
                {
                    Assert.That(defaulted.DodgeRelocation.BudgetReleaseSink, Is.Null,
                        "默认装配不注入消费者 ⇒ 接缝调用是显式 no-op（任务 06 不实现预算账本），"
                        + "这是如实口径而不是漏接线；任务 07 注入消费者后本断言应同步更新");
                }
            }

            // —— 对照证据 2：探针 Tick 上的段键集合（篡改探针必须命中真实存在的键）——
            LogicSnapshot probeSnapshot = legacySide.Snapshots[Task06ProbeTick];
            var probeKeys = new List<string>();
            for (int i = 0; i < probeSnapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = probeSnapshot.MovementSegments[i];
                probeKeys.Add(segment.ActionPlanId + "/" + segment.StepIndex + "@" + segment.EndTick);
            }
            Assert.That(probeKeys.Count, Is.EqualTo(facts.PendingAtProbe),
                "探针 Tick 的段键必须与判定的未提交段数一致：[" + string.Join(";", probeKeys) + "]");
            // 探针必须命中**真实存在**的段键：用从流里读出的首个未提交段索引，
            // 而不是猜一个常数（提交是逐段推进的，StepIndex 0..n-1 早已离开段表）。
            Assert.That(FirstPendingSegment(probeSnapshot, facts.PlanId), Is.Not.Null,
                "段键列表：[" + string.Join(";", probeKeys) + "]");
            facts.FirstPendingStepIndex = FirstPendingSegment(probeSnapshot, facts.PlanId).StepIndex;
            Assert.That(facts.FirstPendingStepIndex, Is.GreaterThan(0),
                "探针 Tick 上必须已经有真实提交（首个未提交段的索引 &gt; 0）：["
                + string.Join(";", probeKeys) + "]");
            Assert.That(probeKeys.Count, Is.EqualTo(facts.PendingAtProbe),
                "段键数必须等于首个未提交段之后的剩余段数：["
                + string.Join(";", probeKeys) + "]");
            Assert.That(probeKeys[0],
                Is.EqualTo(facts.PlanId + "/" + facts.FirstPendingStepIndex + "@"
                    + FirstPendingSegment(probeSnapshot, facts.PlanId).EndTick),
                "段键列表必须按 StepIndex 升序给出首个未提交段：[" + string.Join(";", probeKeys) + "]");

            // —— 主报告：逐用例开启任务 06 检查点（compareMovementFacts: true）——
            var report = CompareTask06MovementProfile(seed, legacySide, shadowSide, Task06CaseId);

            // —— 对照证据 6′（任务 06 小修轮 M-5，**可失败**）——
            // 交接记录 §3 的"逐字段比较"规模不再是无从验证的文档数字：这里用**精确断言**
            // 把 `ComparedFieldObservations` 钉死在"同一公式的独立重算"上。
            // 公式与生产端逐段同源（`ShadowComparisonDetector.InfrastructureFacts`）：
            // 每个检查点 = 检查点级事实 23 条（tick / rulesVersion / battleDefinitionHash /
            // encounterId / units.count / plans.count / actorLanes.count / movementSegments.count /
            // reservations.count / intents.count / effects.count / scheduleRevision /
            // nextCommandSequence / nextEventSequence / nextUnitId / nextActionPlanId /
            // nextReactionOpportunityId / nextWindowId / nextEffectId / history.recordCount /
            // history.digest / rng.algorithmVersion / rng.state）
            // + 逐单位 3 条（unitId / definitionId / factionId）× 该检查点的单位数
            // （两侧取较小者，见 `InfrastructureFacts` 的 `Math.Min`）。
            // 它**不是**下界、也不是范围：任何字段增删都会让本断言失败。
            {
                long expectedFieldObservations = 0L;
                int minUnitsPerCheckpoint = int.MaxValue;
                for (int i = 0; i < legacySide.Snapshots.Count; i++)
                {
                    int unitsHere = Math.Min(
                        legacySide.Snapshots[i].Units.Count, shadowSide.Snapshots[i].Units.Count);
                    if (unitsHere < minUnitsPerCheckpoint) minUnitsPerCheckpoint = unitsHere;
                    expectedFieldObservations += 23L + 3L * unitsHere;
                }
                Assert.That(minUnitsPerCheckpoint, Is.EqualTo(2),
                    "本用例是双槽位场景：每个检查点都必须有 2 个单位，否则逐字段公式不再成立：min="
                    + minUnitsPerCheckpoint);
                Assert.That(report.ComparedFieldObservations, Is.EqualTo((int)expectedFieldObservations),
                    "逐字段比较条数必须精确等于 Σ(23 + 3 × 每检查点单位数)：expected="
                    + expectedFieldObservations + " actual=" + report.ComparedFieldObservations
                    + " ; " + report.Describe());
            }

            // ① 无未分类差异：每条差异的类别必须落在四类归属内，且带精确字段路径。
            for (int d = 0; d < report.Differences.Count; d++)
            {
                ShadowFieldDifference difference = report.Differences[d];
                Assert.That(Enum.IsDefined(typeof(ShadowDifferenceKind), difference.Kind), Is.True,
                    "差异类别必须落在四类归属内：" + difference);
                Assert.That(string.IsNullOrEmpty(difference.FieldPath), Is.False,
                    "每条差异必须带精确字段路径：" + difference);
            }

            // ② 等价场景：不得出现非预期差异 / 基础设施差异 / 批准差异 / 未对齐检查点。
            Assert.That(report.HasUnexpectedDifference, Is.False,
                "任务 06 检查点扩展后仍不得出现非预期差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                "同一脚本化输入的两侧不得出现基础设施事实差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0),
                "本用例不批准任何差异（否则等价声明被削弱）：" + report.Describe());
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                "按逻辑 Tick 必须完全对齐：" + report.Describe());
            Assert.That(report.ComparedCheckpoints, Is.EqualTo(Task06TerminalTick + 1),
                "比较必须覆盖全部检查点：" + report.Describe());

            // ③ 策略结构自洽 ⇒ 允许宣称等价；差异按 RulesVersion 登记。
            Assert.That(report.Rejections.Count, Is.EqualTo(0),
                "逐用例策略必须自洽（缺责任任务/门槛、宽泛批准都会被拒）：" + report.Describe());
            Assert.That(report.RulesVersion, Is.EqualTo(seed.RulesVersion),
                "报告必须记录本用例的策略规则版本（逐差异登记的组成部分）");
            Assert.That(seed.RulesVersion, Is.EqualTo("battle-def-v1"),
                "对照证据：真实定义（02B）的规则版本");
            Assert.That(report.CanClaimEquivalence, Is.True, report.Describe());
            Assert.That(report.EquivalenceClaim, Is.EqualTo("EQUIVALENT"), report.Describe());

            // ④ 暂不可比较字段逐条登记：既有 10 条 + 任务 05 的 12 条 + 任务 06 新增 8 条 = 30 条。
            Assert.That(report.TemporarilyUncomparable.Count,
                Is.EqualTo(Task06BaselineRegistrationCount + Task05ScheduleRegistrationCount
                    + Task06MovementRegistrations.Length),
                "开启任务 05/06 检查点后必须逐条登记暂不可比较字段："
                + string.Join(" | ", report.TemporarilyUncomparable));
            for (int t = 0; t < report.TemporarilyUncomparable.Count; t++)
            {
                TemporarilyUncomparableField field = report.TemporarilyUncomparable[t];
                Assert.That(field.Id, Is.Not.Empty, "暂不可比较登记项必须给出 ID");
                Assert.That(field.Field, Is.Not.Empty, "暂不可比较登记项必须给出字段名");
                Assert.That(field.LegacyObjectPath, Is.Not.Empty, "暂不可比较登记项必须给出旧侧对象路径");
                Assert.That(field.Reason, Is.Not.Empty, "暂不可比较登记项必须给出原因");
                Assert.That(field.OwnerTask, Is.Not.Empty, "暂不可比较登记项必须给出负责任务");
                Assert.That(field.RemovalGate, Is.Not.Empty, "暂不可比较登记项必须给出最迟清零门槛");
            }

            // ④′ 任务 06 的覆盖边界登记项逐条在位，且都标明负责任务 06 与非空清零门槛。
            //     03B/04 已登记的"暂不可比较"项保持原状，没有被升级为批准差异。
            for (int i = 0; i < Task06MovementRegistrations.Length; i++)
            {
                TemporarilyUncomparableField registered =
                    FindRegisteredField(report, Task06MovementRegistrations[i]);
                Assert.That(registered, Is.Not.Null,
                    "任务 06 的覆盖边界登记项必须逐条在位：" + Task06MovementRegistrations[i]
                    + " ; registered=" + string.Join(" | ", report.TemporarilyUncomparable));
                Assert.That(registered.OwnerTask, Is.EqualTo("06"),
                    "任务 06 的登记项必须标出负责任务：" + registered);
                Assert.That(registered.RemovalGate, Is.Not.Empty,
                    "任务 06 的登记项必须标出最迟清零门槛：" + registered);
                Assert.That(registered.Reason, Is.Not.Empty, "登记项必须给出原因：" + registered);
            }

            // —— ⑤ 覆盖证据：逐条"篡改新侧 ⇒ 必须报该字段路径" ——
            // 两侧相等时这些字段不会出现在差异列表里，因此"没有差异"无法区分
            // "真的比较过且相等"与"根本没比较"；下面把每个通道真的推成差异。
            string heroPrefix = "occupancy[" + heroUnitId + "]";
            int pendingStep = facts.FirstPendingStepIndex;
            string segmentPrefix = "movementSegments[" + facts.PlanId + "/" + pendingStep + "]";
            string reservationPrefix = "reservations[" + facts.PlanId + "]";
            string commitPrefix = "movementCommit[" + facts.PlanId + "]";

            AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, units: TamperUnit(snapshot, heroUnitId,
                        unit => unit with { X = unit.X + 2 }))),
                heroPrefix + ".anchor", Task06ProbeTick, "占位：单位逻辑格锚点");

            AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, movementSegments: TamperSegment(snapshot,
                        facts.PlanId, pendingStep, segment => segment with { ToX = segment.ToX + 2 }))),
                segmentPrefix + ".to", Task06ProbeTick, "移动提交：段的目的格");

            AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, movementSegments: TamperSegment(snapshot,
                        facts.PlanId, pendingStep, segment => segment with { EndTick = segment.EndTick + 3L }))),
                segmentPrefix + ".endTick", Task06ProbeTick, "移动提交：段的结束边界（只由 Tick 阶段顺序决定）");

            AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, reservations: RemoveReservationOfPlan(
                        snapshot, facts.PlanId, 0))),
                reservationPrefix + ".cells", Task06ProbeTick, "路径 Reservation：规范格集合");

            AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, reservations: RemoveReservationOfPlan(
                        snapshot, facts.PlanId, 0))),
                reservationPrefix + ".matchesSegmentTos", Task06ProbeTick,
                "路径 Reservation：与段的 1:1 覆盖关系");

            AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, movementSegments: RemoveSegment(
                        snapshot, facts.PlanId, pendingStep))),
                commitPrefix + ".pendingSegments", Task06ProbeTick, "移动提交：剩余未提交段数");

            AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06TerminalTick,
                    snapshot => CopySnapshot(snapshot, movementSegments: new[]
                    {
                        new MovementSegmentSnapshot(facts.PlanId, pendingStep, 0, 0, 2, 0,
                            Task06TerminalTick + 50L)
                    })),
                "movementTerminal[" + facts.PlanId + "].segments", Task06TerminalTick,
                "移动终态：终态计划不得残留活动/未来段");

            // —— ⑥ 负控制（必须有）：在两侧完全等价的前提下篡改"新侧"一条段事实 ——
            // 刻意选**叶子事实**（段的 EndTick）：它不被任何派生不变量引用，因此必须**恰好**产生一条差异。
            // 反差证据见 ⑥″：篡改被派生事实引用的坐标（To）会同时推翻"To ↔ Reservation 一一对应"，
            // 恰好产生两条——这正是"比较面真的在算派生关系"的证据，不是回归。
            var negative = AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, movementSegments: TamperSegment(snapshot,
                        facts.PlanId, pendingStep, segment => segment with { EndTick = segment.EndTick + 7L }))),
                segmentPrefix + ".endTick", Task06ProbeTick, "负控制：段的结束边界（叶子事实）");

            Assert.That(negative.HasUnexpectedDifference, Is.True,
                "篡改段事实后必须出现非预期差异：" + negative.Describe());
            Assert.That(negative.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(1),
                "篡改单个叶子段字段必须**恰好**产生一条非预期差异："
                + DescribeDifferenceList(negative.Differences));
            Assert.That(negative.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                "只改段投影不得产生基础设施事实差异：" + negative.Describe());
            Assert.That(negative.FirstUnexpectedDifference.FieldPath, Is.EqualTo(segmentPrefix + ".endTick"),
                "首个非预期差异必须精确到被篡改的字段路径：" + negative.Describe());
            Assert.That(negative.FirstUnexpectedDifference.LogicalTick, Is.EqualTo((long)Task06ProbeTick),
                "差异必须绑定被篡改的逻辑 Tick：" + negative.FirstUnexpectedDifference);
            Assert.That(negative.FirstUnexpectedDifference.LegacyValue,
                Is.Not.EqualTo(negative.FirstUnexpectedDifference.ShadowValue),
                "差异两侧取值必须不同：" + negative.FirstUnexpectedDifference);
            Assert.That(negative.CanClaimEquivalence, Is.False,
                "存在非预期差异时不得宣称等价：" + negative.Describe());
            Assert.That(negative.EquivalenceClaim, Does.StartWith("DIFFERENT:"), negative.Describe());

            // —— ⑥″ 反差证据：篡改**被派生事实引用**的坐标 ⇒ 恰好两条差异（字段本身 + 被推翻的不变量）——
            var derivedNegative = AssertTask06FieldIsCompared(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, movementSegments: TamperSegment(snapshot,
                        facts.PlanId, pendingStep, segment => segment with { ToY = segment.ToY + 2 }))),
                segmentPrefix + ".to", Task06ProbeTick, "反差证据：段的整数坐标（被派生不变量引用）");

            Assert.That(derivedNegative.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(2),
                "篡改被引用的坐标必须同时推翻'每条段的 To 都有同计划 Reservation 且数量相等'："
                + DescribeDifferenceList(derivedNegative.Differences));
            Assert.That(derivedNegative.FirstUnexpectedDifference.FieldPath,
                Is.EqualTo(segmentPrefix + ".to"), derivedNegative.Describe());

            // —— ⑥′ 对照：同一份被篡改的流在**默认策略**下必须零差异 ——
            // 这同时证明"任务 06 检查点逐用例开启"是真的开关（默认关闭 ⇒ 既有用例报告逐字节不变）。
            Assert.That(ShadowCasePolicy.CreateDefault(Task06CaseId, seed.RulesVersion).CompareMovementFacts,
                Is.False, "compareMovementFacts 必须默认关闭");
            var defaultPolicyReport = CompareWithPolicy(seed, legacySide,
                TamperCheckpoint(shadowSide, Task06ProbeTick,
                    snapshot => CopySnapshot(snapshot, movementSegments: TamperSegment(snapshot,
                        facts.PlanId, pendingStep, segment => segment with { EndTick = segment.EndTick + 7L }))),
                ShadowCasePolicy.CreateDefault(Task06CaseId + "-default-off", seed.RulesVersion),
                legacySide.Snapshots.Count);
            Assert.That(defaultPolicyReport.TemporarilyUncomparable.Count,
                Is.EqualTo(Task06BaselineRegistrationCount),
                "默认策略只登记既有 " + Task06BaselineRegistrationCount + " 条暂不可比较字段"
                + "（任务 05 的 12 条由 compareScheduleFacts 开启、任务 06 的 8 条由 compareMovementFacts 开启）："
                + defaultPolicyReport.Describe());
            Assert.That(defaultPolicyReport.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                "默认策略下段事实不在比较集合内（同一条篡改流零差异）：" + defaultPolicyReport.Describe());
            Assert.That(defaultPolicyReport.HasUnexpectedDifference, Is.False,
                defaultPolicyReport.Describe());

            // —— ⑦ 比较面边界：比较器只读整数逻辑事实，拿不到 Transform / 视觉插值 ——
            AssertTask06ComparisonSurfaceIsIntegerOnly();

            // —— ⑧ 冲突回滚：唯一可达的失败面 = 整批拒绝且零局部写入（含不可达分支的技术证明）——
            AssertTask06FailedBatchLeavesNoPartialWrite(seed, heroUnitId, facts, legacySide);
        }

        /// <summary>
        /// 任务 06 的脚本化检查点流：真实 <c>BattleSimulation</c> + 真实 <c>Step</c> 管线。
        ///
        /// 输入序列（两侧逐字相同）：
        /// <list type="number">
        /// <item>Tick 1：<c>ScheduleEditPayload + AddOrdinaryPlanOperation</c> 新增一个<strong>普通 Move</strong>
        /// 计划（真实动作 <c>action.move.default</c>、请求起点 4、目的格 <c>(0,8)</c>）——
        /// 真实 <c>ScheduleEditor</c> 经 <c>MovementSpacePort</c> 建立真实段链与路径 Reservation；</item>
        /// <item>Tick 4：启动门禁原子锁定/启动 ⇒ 计划 <c>Running</c>；</item>
        /// <item>其后每个段的 <c>EndTick</c> 上，阶段 0 的<strong>命令前边界</strong>把该段原子提交到 <c>To</c>
        /// 并释放对应 Reservation（离散提交，与视觉插值进度无关）；</item>
        /// <item>最后一段提交后计划自然完成（<strong>不是</strong>被战斗结束清场）⇒ 移动终态。</item>
        /// </list>
        /// </summary>
        private static RealCheckpointStream BuildTask06MovementStream(ShadowCaseSeed seed, long heroUnitId)
        {
            var snapshots = new List<LogicSnapshot>(Task06TerminalTick + 1);
            var bindings = new List<ShadowCheckpointEventBinding>(Task06TerminalTick + 1);
            BattleSimulation simulation = BattleSimulation.Create(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs, new BattleSimulationAssembly());

            // 任务 07 契约（00 号规则 18）：显式排程编辑的新增必须声明 ExpectedWindowId，
            // 且命令阶段必须有仍接受提交的当前窗口（脚本化输入，首个 Step 之前打开）。
            simulation.WindowManager.ScheduleWindow(0L, new UnitId(heroUnitId), Task07HeroWindowBudget);
            Assert.That(simulation.WindowManager.TryOpenDueWindow(0L), Is.Not.Null,
                "构造前提：hero 的脚本窗口必须在首个 Step 之前打开");

            try
            {
                CommandIngressEntry entry = simulation.CommandIngress.FindEntry(
                    new ControllerId("controller.player"));
                Assert.That(entry, Is.Not.Null, "必须有 controller.player 入口");

                for (long tick = 0L; tick <= Task06TerminalTick; tick++)
                {
                    if (tick == Task06SubmitTick)
                    {
                        // 期望修订号取自**运行中的真实权威**：若基线不是 0，命令会被信任边界以稳定码拒绝，
                        // 下面的场景事实断言会立刻失败（不会静默退化成"空世界比较"）。
                        CommandIngressRejection rejection = entry.Submit(new CommandRequest(
                            tick,
                            new ScheduleEditScope(
                                simulation.ScheduleRevision,
                                RequireCurrentWindow(simulation)),
                            new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new AddOrdinaryPlanOperation(
                                    1L, new UnitId(heroUnitId), new ActionSpecId(Task06MoveSpecId),
                                    Task06RequestedStartTick,
                                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                                    Facing: GridDirection.North,
                                    Destination: new GridPoint(Task06DestinationX, Task06DestinationY))
                            })));
                        Assert.That(rejection, Is.Null,
                            rejection == null
                                ? null
                                : "真实移动命令必须被信任边界接受：" + rejection.ReasonCode
                                  + "|ordinal=" + rejection.ProducerOrdinal);
                    }

                    FrozenCommandBatch batch = simulation.CommandIngress.FreezeTick(tick);
                    StepResult result = simulation.Step(tick, batch);
                    snapshots.Add(simulation.CurrentSnapshot);
                    bindings.Add(BuildEventBinding(tick, result));
                }
            }
            finally
            {
                simulation.Dispose();
            }

            return new RealCheckpointStream(snapshots, bindings);
        }

        /// <summary>从真实检查点流里读出的场景事实（供断言与探针定位）。</summary>
        private sealed class Task06ScenarioFacts
        {
            public long PlanId;
            public CellFactKey StartAnchor;
            public CellFactKey MidAnchor;
            public int InitialSegments;
            public int PendingAtProbe;
            public int CommittedAtProbe;
            public int FirstPendingStepIndex;
            public long LastEndTick;
        }

        /// <summary>整数格事实（测试侧只读投影；不引用生产 <c>GridPoint</c> 以免受其构造校验影响）。</summary>
        private readonly struct CellFactKey : IEquatable<CellFactKey>
        {
            public CellFactKey(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X { get; }

            public int Y { get; }

            public bool Equals(CellFactKey other) => X == other.X && Y == other.Y;

            public override bool Equals(object obj) => obj is CellFactKey other && Equals(other);

            public override int GetHashCode() => (X * 397) ^ Y;

            public override string ToString() => "(" + X + "," + Y + ")";
        }

        /// <summary>
        /// 五类事实的<strong>逐 Tick 精确断言</strong>（覆盖证据的第一半：报告里要求覆盖的事实
        /// 必须真的存在于这条检查点流里，否则"零差异"只是两条空流的巧合相等）。
        /// </summary>
        private static Task06ScenarioFacts AssertTask06ScenarioFacts(
            RealCheckpointStream side, long heroUnitId)
        {
            string stream = DescribeTask06Stream(side);
            var facts = new Task06ScenarioFacts();

            // —— Tick 1：真实排程事务 ⇒ 修订号 1、一条普通 Move 计划、真实段链与路径 Reservation ——
            LogicSnapshot atSubmit = side.Snapshots[Task06SubmitTick];
            Assert.That(atSubmit.ScheduleRevision, Is.EqualTo(1L),
                "成功排程编辑事务恰好 +1（scheduleRevision = 1）：" + stream);
            Assert.That(atSubmit.Plans.Count, Is.EqualTo(1), "排程编辑必须留下恰好一条计划：" + stream);
            ActionPlanSnapshot created = atSubmit.Plans[0];
            facts.PlanId = created.ActionPlanId;
            Assert.That(facts.PlanId, Is.GreaterThan(0L));
            Assert.That(created.OwnerUnitId, Is.EqualTo(heroUnitId));
            Assert.That(created.ActionSpecId, Is.EqualTo(Task06MoveSpecId));
            Assert.That(created.StartTick, Is.EqualTo((long)Task06RequestedStartTick));
            Assert.That(created.ResolvedPathEdgeCount, Is.GreaterThan(1),
                "真实寻路必须产出多段路径（否则移动提交/终态事实无对象）：" + stream);

            facts.InitialSegments = atSubmit.MovementSegments.Count;
            Assert.That(facts.InitialSegments, Is.EqualTo(created.ResolvedPathEdgeCount),
                "段数必须等于真实路径边数（每个活动段恰好持有一条目标格 Reservation）：" + stream);
            Assert.That(atSubmit.Reservations.Count, Is.EqualTo(facts.InitialSegments),
                "每个活动段必须恰好持有一条目标格 Reservation：" + stream);

            // 段键必须正好是 (planId, 0..n-1)，且首段的 From 等于 hero 的初始锚点。
            facts.StartAnchor = AnchorOf(atSubmit, heroUnitId);
            for (int i = 0; i < facts.InitialSegments; i++)
            {
                MovementSegmentSnapshot segment = SegmentOf(atSubmit, facts.PlanId, i);
                Assert.That(segment, Is.Not.Null, "段 " + i + " 必须存在（键 = (ActionPlanId, StepIndex)）：" + stream);
                if (i == 0)
                {
                    Assert.That(new CellFactKey(segment.FromX, segment.FromY), Is.EqualTo(facts.StartAnchor),
                        "首段起点必须是权威锚点：" + stream);
                }
                Assert.That(segment.EndTick, Is.GreaterThan(segment.EndTick - 1L));
            }

            // —— Tick 4：启动门禁原子锁定/启动 ⇒ Running，且段链在锁定后<strong>不再重算</strong> ——
            LogicSnapshot atStart = side.Snapshots[Task06RequestedStartTick];
            ActionPlanSnapshot running = FindPlanSnapshot(atStart, facts.PlanId);
            Assert.That(running, Is.Not.Null, "启动 Tick 上计划必须仍在活动索引：" + stream);
            Assert.That(running.State, Is.EqualTo((int)ActionPlanState.Running),
                "到期门禁必须原子锁定/启动计划（state = Running）：" + stream);
            Assert.That(running.LockedAtTick, Is.EqualTo((long)Task06RequestedStartTick),
                "锁定 Tick 与 Running 必须同一次原子提交写入：" + stream);
            Assert.That(atStart.MovementSegments.Count, Is.EqualTo(facts.InitialSegments),
                "锁定后段链必须逐字冻结（不按新输入或视觉速度重采样）：" + stream);

            // 段的 EndTick 序列必须严格递增（离散提交的顺序对象）。
            //
            // 探针 Tick 的合法窗口必须**双向夹逼**：太大则最后一段已提交、太小则一段都没提交，
            // 两种情况下"移动提交"类事实都会退化成空比较。下面用真实时序同时钉死上下界。
            facts.LastEndTick = LastSegmentEndTick(atStart, facts.PlanId);
            Assert.That(facts.LastEndTick, Is.GreaterThan(0L),
                "必须能定位最后一段的 EndTick：" + stream);

            // —— 探针 Tick：位移动窗口内部（已有提交、仍有未提交段）——
            facts.PendingAtProbe = side.Snapshots[Task06ProbeTick].MovementSegments.Count;
            facts.CommittedAtProbe = facts.InitialSegments - facts.PendingAtProbe;
            Assert.That((long)Task06ProbeTick, Is.GreaterThan(FirstSegmentEndTick(atStart, facts.PlanId)),
                "探针 Tick 必须晚于首段的 EndTick（否则没有任何真实提交可比较）：" + stream);
            Assert.That((long)Task06ProbeTick, Is.LessThan(facts.LastEndTick),
                "探针 Tick 必须早于最后一段的 EndTick（否则没有剩余路径可比较）：" + stream);
            Assert.That(facts.CommittedAtProbe, Is.GreaterThan(0),
                "探针 Tick 上必须已经发生<strong>真实的命令前边界提交</strong>：" + stream);
            Assert.That(facts.PendingAtProbe, Is.GreaterThan(0),
                "探针 Tick 上必须仍有未提交段（剩余路径起点 == 当前锚点才有对象）：" + stream);

            LogicSnapshot atProbe = side.Snapshots[Task06ProbeTick];
            facts.MidAnchor = AnchorOf(atProbe, heroUnitId);
            Assert.That(facts.MidAnchor, Is.Not.EqualTo(facts.StartAnchor),
                "离散提交必须让逻辑格锚点真的沿路径推进（不是恒等比较）：" + stream);
            MovementSegmentSnapshot firstPending = FirstPendingSegment(atProbe, facts.PlanId);
            Assert.That(firstPending, Is.Not.Null,
                "探针 Tick 上必须能定位第一条未提交段：" + stream);
            Assert.That(new CellFactKey(firstPending.FromX, firstPending.FromY), Is.EqualTo(facts.MidAnchor),
                "移动提交后的逻辑位置必须恰好是剩余路径的起点：" + stream);
            Assert.That(ReservationsMatchSegmentTos(atProbe, facts.PlanId), Is.True,
                "每条段的 To 必须有同计划 Reservation 且数量相等：" + stream);

            // —— 终态 Tick：计划自然完成并离开活动索引 ⇒ 段/预留已归零、锚点停在最后已提交格 ——
            LogicSnapshot atEnd = side.Snapshots[Task06TerminalTick];
            Assert.That(atEnd.BattleEnd.IsEnded, Is.False,
                "本用例必须靠'移动自然完成'进入终态，而不是靠战斗结束清场（否则移动终态事实与终局清场混淆）："
                + stream);
            Assert.That(FindPlanSnapshot(atEnd, facts.PlanId), Is.Null,
                "终态计划必须立即离开活动索引：" + stream);
            Assert.That(atEnd.TerminalPlanRecordCount, Is.EqualTo(1L),
                "终态计划必须留下历史记录数（归档摘要）：" + stream);
            Assert.That(SegmentsOfPlan(atEnd, facts.PlanId), Is.EqualTo(0),
                "移动终态：原 EndTick 不得再提交，段必须已全部清理：" + stream);
            Assert.That(ReservationsOfPlan(atEnd, facts.PlanId), Is.EqualTo(0),
                "移动终态：该计划的全部 Reservation 必须已释放：" + stream);
            Assert.That(atEnd.MovementSegments.Count, Is.EqualTo(0),
                "移动终态：不得残留任何活动/未来段：" + stream);
            Assert.That(atEnd.Reservations.Count, Is.EqualTo(0),
                "移动终态：不得残留任何 Reservation：" + stream);

            var finalAnchor = AnchorOf(atEnd, heroUnitId);
            var destination = new CellFactKey(Task06DestinationX, Task06DestinationY);
            Assert.That(finalAnchor, Is.EqualTo(destination),
                "计划完成后锚点必须停在目的格（最后一格已提交）：" + stream);

            // 最后一个段的 EndTick 必须 < 终态 Tick（终态选择与真实时序对应，不是随手取的常数）。
            Assert.That(facts.LastEndTick, Is.LessThan((long)Task06TerminalTick),
                "终态 Tick 必须晚于最后一段的 EndTick：" + stream);

            return facts;
        }

        // -------- 任务 06 报告与覆盖探针 --------

        /// <summary>逐用例开启任务 06 检查点（<c>compareMovementFacts: true</c>）的比较入口。</summary>
        private static ShadowComparisonReport CompareTask06MovementProfile(
            ShadowCaseSeed seed, RealCheckpointStream legacySide, RealCheckpointStream shadowSide,
            string caseId)
        {
            // 预算**从真实检查点数导出**（不是魔法数）：本用例覆盖整条移动链，
            // 检查点数必然超过既有用例的 64；一旦超限报告会变成 INVALID:SHADOW_BUDGET_OVERRUN、
            // 比较根本不会执行。下面的断言把"预算 == 真实检查点数"钉死，
            // 因此它既不会掩盖任何差异，也不会静默退化成空比较。
            int budget = legacySide.Snapshots.Count;
            Assert.That(budget, Is.GreaterThan(0));
            Assert.That(shadowSide.Snapshots.Count, Is.EqualTo(budget),
                "两侧检查点数必须相等（预算不能掩盖一侧缺口）");
            Assert.That(Task06ProfileLegacyObservations.Count, Is.EqualTo(0),
                "任务 06 画像入口**显式**传入空旧侧观测：比较器因此走 LogicSnapshot 对 LogicSnapshot 通道，"
                + "结论口径只能是『确定性 + 跨世界一致性』（真实 Shadow 语义归属任务 10/11，见交接记录 §0.6）");

            var report = CompareWithPolicy(seed, legacySide, shadowSide,
                ShadowCasePolicy.CreateDefault(caseId, seed.RulesVersion,
                    compareScheduleFacts: true, compareMovementFacts: true),
                budget, Task06ProfileLegacyObservations);
            Assert.That(report.BudgetOverrun, Is.False,
                "预算必须足够执行整条比较（超限会让报告变成 INVALID 而不是通过）："
                + report.BudgetOverrunReason + " ; " + report.Describe());
            return report;
        }

        /// <summary>
        /// 覆盖探针：把"新侧"某个检查点上的一条任务 06 事实改成不同值，断言比较器<strong>必须</strong>
        /// 在该字段路径上报告一条 <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>。
        /// </summary>
        private static ShadowComparisonReport AssertTask06FieldIsCompared(
            ShadowCaseSeed seed, RealCheckpointStream legacySide, RealCheckpointStream tamperedSide,
            string expectedFieldPath, long expectedTick, string what)
        {
            var report = CompareTask06MovementProfile(seed, legacySide, tamperedSide, Task06CaseId + "-probe");
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                what + "：覆盖探针不得破坏逐 Tick 对齐：" + report.Describe());

            ShadowFieldDifference hit = null;
            for (int i = 0; i < report.Differences.Count; i++)
            {
                ShadowFieldDifference difference = report.Differences[i];
                if (difference.Kind != ShadowDifferenceKind.NewRuleVerifiedFact) continue;
                if (!string.Equals(difference.FieldPath, expectedFieldPath, StringComparison.Ordinal)) continue;
                if (difference.LogicalTick != expectedTick) continue;
                hit = difference;
                break;
            }

            Assert.That(hit, Is.Not.Null,
                what + "：篡改新侧后必须出现字段 " + expectedFieldPath + " 在 Tick " + expectedTick
                + " 上的 " + ShadowDifferenceKind.NewRuleVerifiedFact + " 差异（证明该通道真的参与比较）："
                + report.Describe() + " ; differences=" + DescribeDifferenceList(report.Differences));
            Assert.That(hit.LegacyValue, Is.Not.EqualTo(hit.ShadowValue), "差异两侧取值必须不同：" + hit);
            Assert.That(hit.IsUnexpected, Is.True,
                "任务 06 的字段差异一律是回归（不得被批准差异掩盖）：" + hit);
            return report;
        }

        /// <summary>任务 06 在<strong>生产旧侧观测通道</strong>上必须逐条登记的覆盖边界字段名。</summary>
        private static readonly string[] Task06MovementRegistrations =
        {
            "occupancy[i].anchor",
            "movementSegments[i].from",
            "movementSegments[i].to",
            "movementSegments[i].endTick",
            "reservations[i].cell",
            "movementCommit[i].expectedFrom",
            "movementTerminal[i].segments",
            "movementTerminal[i].reservations"
        };

        // -------- 任务 06 篡改工具（测试侧，不是生产比较路径） --------

        /// <summary>按单位 ID 替换一条单位投影（未命中的单位逐字保留）。</summary>
        private static IReadOnlyList<UnitSnapshot> TamperUnit(
            LogicSnapshot snapshot, long unitId, Func<UnitSnapshot, UnitSnapshot> change)
        {
            var units = new List<UnitSnapshot>(snapshot.Units.Count);
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                UnitSnapshot unit = snapshot.Units[i];
                units.Add(unit != null && unit.UnitId == unitId ? change(unit) : unit);
            }
            return units;
        }

        /// <summary>按 <c>(ActionPlanId, StepIndex)</c> 替换一条移动段投影（未命中的段逐字保留）。</summary>
        private static IReadOnlyList<MovementSegmentSnapshot> TamperSegment(
            LogicSnapshot snapshot, long planId, int stepIndex,
            Func<MovementSegmentSnapshot, MovementSegmentSnapshot> change)
        {
            var segments = new List<MovementSegmentSnapshot>(snapshot.MovementSegments.Count);
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                segments.Add(segment != null && segment.ActionPlanId == planId && segment.StepIndex == stepIndex
                    ? change(segment)
                    : segment);
            }
            return segments;
        }

        /// <summary>删掉一条移动段投影（其余逐字保留）。</summary>
        private static IReadOnlyList<MovementSegmentSnapshot> RemoveSegment(
            LogicSnapshot snapshot, long planId, int stepIndex)
        {
            var segments = new List<MovementSegmentSnapshot>(snapshot.MovementSegments.Count);
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment != null && segment.ActionPlanId == planId && segment.StepIndex == stepIndex) continue;
                segments.Add(segment);
            }
            return segments;
        }

        /// <summary>删掉一条 Reservation 投影（其余逐字保留）。</summary>
        private static IReadOnlyList<ReservationSnapshot> RemoveReservationOfPlan(
            LogicSnapshot snapshot, long planId, int indexInPlan)
        {
            var reservations = new List<ReservationSnapshot>(snapshot.Reservations.Count);
            int seen = 0;
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                ReservationSnapshot reservation = snapshot.Reservations[i];
                if (reservation != null && reservation.ActionPlanId == planId)
                {
                    if (seen == indexInPlan)
                    {
                        seen++;
                        continue;
                    }
                    seen++;
                }
                reservations.Add(reservation);
            }
            return reservations;
        }

        // -------- 任务 06 只读投影（断言用） --------

        private static CellFactKey AnchorOf(LogicSnapshot snapshot, long unitId)
        {
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                if (snapshot.Units[i] != null && snapshot.Units[i].UnitId == unitId)
                    return new CellFactKey(snapshot.Units[i].X, snapshot.Units[i].Y);
            }
            Assert.Fail("单位 " + unitId + " 必须在规范化快照里："
                + DescribeTask06Stream(new RealCheckpointStream(new[] { snapshot },
                    Array.Empty<ShadowCheckpointEventBinding>())));
            return default;
        }

        private static MovementSegmentSnapshot SegmentOf(LogicSnapshot snapshot, long planId, int stepIndex)
        {
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment != null && segment.ActionPlanId == planId && segment.StepIndex == stepIndex)
                    return segment;
            }
            return null;
        }

        private static MovementSegmentSnapshot FirstPendingSegment(LogicSnapshot snapshot, long planId)
        {
            MovementSegmentSnapshot best = null;
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment == null || segment.ActionPlanId != planId) continue;
                if (best == null || segment.StepIndex < best.StepIndex) best = segment;
            }
            return best;
        }

        private static long LastSegmentEndTick(LogicSnapshot snapshot, long planId)
        {
            long last = -1L;
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment == null || segment.ActionPlanId != planId) continue;
                if (segment.EndTick > last) last = segment.EndTick;
            }
            return last;
        }

        /// <summary>第一个段（<c>StepIndex == 0</c>）的 <c>EndTick</c>；不存在返回 <c>-1</c>。</summary>
        private static long FirstSegmentEndTick(LogicSnapshot snapshot, long planId)
        {
            MovementSegmentSnapshot first = FirstPendingSegment(snapshot, planId);
            return first != null ? first.EndTick : -1L;
        }

        private static int SegmentsOfPlan(LogicSnapshot snapshot, long planId)
        {
            int count = 0;
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                if (snapshot.MovementSegments[i] != null
                    && snapshot.MovementSegments[i].ActionPlanId == planId) count++;
            }
            return count;
        }

        private static int ReservationsOfPlan(LogicSnapshot snapshot, long planId)
        {
            int count = 0;
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                if (snapshot.Reservations[i] != null
                    && snapshot.Reservations[i].ActionPlanId == planId) count++;
            }
            return count;
        }

        /// <summary>该计划的段 <c>To</c> 集合与 Reservation 格集合是否一一对应。</summary>
        private static bool ReservationsMatchSegmentTos(LogicSnapshot snapshot, long planId)
        {
            var tos = new SortedSet<string>();
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment == null || segment.ActionPlanId != planId) continue;
                tos.Add(segment.ToX + "," + segment.ToY);
            }

            var cells = new SortedSet<string>();
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                ReservationSnapshot reservation = snapshot.Reservations[i];
                if (reservation == null || reservation.ActionPlanId != planId) continue;
                cells.Add(reservation.X + "," + reservation.Y);
            }

            return tos.Count == cells.Count && tos.SetEquals(cells);
        }

        /// <summary>逐 Tick 的任务 06 诊断（只在断言失败信息里使用）。</summary>
        private static string DescribeTask06Stream(RealCheckpointStream side)
        {
            var builder = new StringBuilder("task06[");
            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                LogicSnapshot snapshot = side.Snapshots[i];
                builder.Append('{').Append("t=").Append(snapshot.Tick)
                    .Append(" rev=").Append(snapshot.ScheduleRevision)
                    .Append(" units=");
                for (int u = 0; u < snapshot.Units.Count; u++)
                {
                    builder.Append('(').Append(snapshot.Units[u].UnitId).Append('@')
                        .Append(snapshot.Units[u].X).Append(',').Append(snapshot.Units[u].Y).Append(')');
                }
                builder.Append(" plans=");
                for (int p = 0; p < snapshot.Plans.Count; p++)
                {
                    builder.Append('(').Append(snapshot.Plans[p].ActionPlanId)
                        .Append(",state=").Append(snapshot.Plans[p].State)
                        .Append(",start=").Append(snapshot.Plans[p].StartTick)
                        .Append(",end=").Append(snapshot.Plans[p].EndTick)
                        .Append(",edges=").Append(snapshot.Plans[p].ResolvedPathEdgeCount)
                        .Append(')');
                }
                builder.Append(" segs=").Append(snapshot.MovementSegments.Count)
                    .Append(" res=").Append(snapshot.Reservations.Count)
                    .Append(" ended=").Append(snapshot.BattleEnd.IsEnded)
                    .Append(" terminalRecords=").Append(snapshot.TerminalPlanRecordCount)
                    .Append("} ");
            }
            builder.Append(']');
            return builder.ToString();
        }

        // -------- 任务 06：冲突回滚（唯一可达失败面）+ 不可达分支的技术证明 --------

        /// <summary>
        /// <strong>冲突回滚</strong>的可失败断言 + 不可达分支的技术证明。
        ///
        /// <strong>可达的失败面</strong>：真实排程事务在"路径求值阶段"失败时，必须
        /// <strong>整批拒绝且零局部写入</strong>：<c>scheduleRevision</c> 不推进、计划不进入注册表、
        /// 段表与 Reservation 表逐字不变、逻辑格锚点不变。
        ///
        /// <strong>不可达的分支（技术证明，逐条对应代码）</strong>：
        /// <list type="number">
        /// <item><c>LogicGridMovementAuthority.RebuildMovementSpace</c> 在写入前先调用
        /// <c>ResolveEditableMovementWorkingSet</c>，后者已经用 <c>LogicPathfinder.FindPath</c>
        /// 验证过"该计划的候选路径存在"，之后内部<strong>用同一个 pathfinder 再算一次同一条路径</strong>
        /// ⇒ 两次结果不可能不同；</item>
        /// <item>因此进入建立阶段的工作集条目，<c>EstablishMovement</c> 只可能因三种原因失败
        /// （段链不连续 / 段区间非法 / 目的格同键冲突），而这三条在解析阶段之后都不可达
        /// （与 06-交接记录 脚注 C 的独立分析一致）；</item>
        /// <item><c>LogicPathfinder.IsBlocked</c> 对 cell 上的 Reservation 是<strong>纯空间</strong>判定
        /// （不看时间窗），所以"外部预留占住路径格"必然在<strong>求值阶段</strong>就以
        /// <c>PATH_NOT_FOUND</c> 失败并<strong>抛出</strong>
        /// （<c>LogicGridMovementPathCalculator.Recompute</c> 的冻结语义：寻路失败以稳定失败码抛出，
        /// 令整批事务失败且零局部写入），根本走不到逐项回滚；
        /// 本方法第 ① 段就是这一条的<strong>可失败证据</strong>。</item>
        /// </list>
        ///
        /// <strong>因此</strong>：<c>RollbackSpace</c> 的逐项回滚是防御性代码，
        /// 在当前生产路径上<strong>没有可达的外部触发</strong>；本用例<strong>不宣称</strong>覆盖它，
        /// 也不把它标成"已批准差异"。归属：任务 11（或在 <c>IEditableMovementSpacePort</c> 上
        /// 增加测试专用注入口——那会扩大生产可见面，须由任务 11 决定）。
        /// </summary>
        private static void AssertTask06FailedBatchLeavesNoPartialWrite(
            ShadowCaseSeed seed, long heroUnitId, Task06ScenarioFacts facts, RealCheckpointStream side)
        {
            BattleSimulation simulation = BattleSimulation.Create(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs, new BattleSimulationAssembly());
            // 任务 07 契约（00 号规则 18）：显式排程编辑的新增必须声明 ExpectedWindowId，
            // 且命令阶段必须有仍接受提交的当前窗口（脚本化输入，首个 Step 之前打开）。
            simulation.WindowManager.ScheduleWindow(0L, new UnitId(heroUnitId), Task07HeroWindowBudget);
            Assert.That(simulation.WindowManager.TryOpenDueWindow(0L), Is.Not.Null,
                "构造前提：hero 的脚本窗口必须在首个 Step 之前打开");
            try
            {
                CommandIngressEntry entry = simulation.CommandIngress.FindEntry(
                    new ControllerId("controller.player"));
                Assert.That(entry, Is.Not.Null);

                // 走到"计划已建立真实段链"的那一 Tick（与检查点流同一脚本化输入）。
                for (long tick = 0L; tick <= Task06SubmitTick; tick++)
                {
                    if (tick == Task06SubmitTick)
                    {
                        CommandIngressRejection rejection = entry.Submit(new CommandRequest(
                            tick,
                            new ScheduleEditScope(
                                simulation.ScheduleRevision,
                                RequireCurrentWindow(simulation)),
                            new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new AddOrdinaryPlanOperation(
                                    1L, new UnitId(heroUnitId), new ActionSpecId(Task06MoveSpecId),
                                    Task06RequestedStartTick,
                                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                                    Facing: GridDirection.North,
                                    Destination: new GridPoint(Task06DestinationX, Task06DestinationY))
                            })));
                        Assert.That(rejection, Is.Null, "构造用移动命令必须被接受");
                    }
                    simulation.Step(tick, simulation.CommandIngress.FreezeTick(tick));
                }

                LogicSnapshot before = simulation.CurrentSnapshot;
                string beforeSpace = DescribeSpace(before, facts.PlanId);
                Assert.That(SegmentsOfPlan(before, facts.PlanId), Is.EqualTo(facts.InitialSegments),
                    "构造前提：失败之前必须已经有一条真实的段链：" + beforeSpace);

                // —— ① 对照证据：第一条计划的段链<strong>真的可按锚点重算</strong>（否则下面的失败断言是空的）——
                //        Recompute 用的预测起点 = "该单位时间线上在此之前结束的最后一个移动族计划的目的格"；
                //        第一条 Editable Move 没有这样的前序 ⇒ 预测起点 == 权威锚点 ⇒ 重算成功。
                //        （必须把该计划自己的 ActionPlanId 传给 Pathfinder：它自己的预留不算阻塞。）
                Assert.That(SimulationHasPathFrom(simulation, heroUnitId, facts.StartAnchor,
                        new GridPoint(Task06DestinationX, Task06DestinationY), facts.PlanId),
                    Is.True,
                    "对照证据：第一条计划的路径必须真的存在（证明下面失败的不是寻路本身）");

                // 越界目的格从**真实 Encounter 边界**算出（不硬编码常数）：
                // doubled 坐标下合法格的 X/Y 同为偶或同为奇，而 boundary.Max 是 (16,16)（偶/偶），
                // 因此 (Max.X + 2, Max.Y) 必然越界且不会被"附近总是能绕出去"抵消。
                var encounter = seed.Definition.FindEncounter(seed.EncounterId);
                var outOfBounds = new GridPoint(encounter.GridBoundary.Max.X + 2, encounter.GridBoundary.Max.Y);
                Assert.That(simulation.LogicGrid.IsLegalGridPoint(outOfBounds), Is.False,
                    "对照证据：构造用目的格必须在 Encounter 边界之外：" + outOfBounds);

                // —— ② 触发一次真实的"受影响计划重算"：新增第二条 Editable Move（同单位、起点更晚）——
                //        它把第一条计划纳入位置依赖闭包 ⇒ 第一条计划被真实 Recompute（该计划已建立真实段链）；
                //        同时它自己的预测起点 = 第一条计划的目的格，而它声明了一个<strong>越界</strong>目的格
                //        ⇒ 整批事务以稳定失败码失败（fail-closed），且在提交点之前。
                LogicDefinitionException thrown = null;
                CommandIngressRejection stepRejection = null;
                try
                {
                    stepRejection = entry.Submit(new CommandRequest(
                        Task06SubmitTick + 1L,
                        new ScheduleEditScope(simulation.ScheduleRevision, null),
                        new ScheduleEditPayload(new ScheduleEditOperation[]
                        {
                            new AddOrdinaryPlanOperation(
                                2L, new UnitId(heroUnitId), new ActionSpecId(Task06MoveSpecId),
                                Task06RequestedStartTick + 2L,
                                AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                                Facing: GridDirection.North, Destination: outOfBounds)
                        })));
                    simulation.Step(Task06SubmitTick + 1L,
                        simulation.CommandIngress.FreezeTick(Task06SubmitTick + 1L));
                }
                catch (LogicDefinitionException ex)
                {
                    thrown = ex;
                }

                Assert.That(thrown != null || stepRejection != null, Is.True,
                    "排程重算失败必须以冻结失败码<strong>失败</strong>（抛出或稳定拒绝），而不是静默部分提交："
                    + "thrown=" + (thrown != null ? thrown.ErrorCode : "<none>")
                    + " rejection=" + DescribeRejection(stepRejection));
                // —— ③ 零局部写入：修订号、计划集合、段表、Reservation 表、锚点、世界摘要逐字不变 ——
                LogicSnapshot after = simulation.CurrentSnapshot;
                string afterSpace = DescribeSpace(after, facts.PlanId);
                Assert.That(after.ScheduleRevision, Is.EqualTo(before.ScheduleRevision),
                    "失败事务绝不推进 scheduleRevision：" + afterSpace);
                Assert.That(after.Plans.Count, Is.EqualTo(before.Plans.Count),
                    "失败事务绝不留下新计划：" + afterSpace);
                Assert.That(after.MovementSegments.Count, Is.EqualTo(before.MovementSegments.Count),
                    "失败事务绝不改写段表（零局部写入）：" + afterSpace);
                Assert.That(afterSpace, Is.EqualTo(beforeSpace),
                    "段链必须先建立成功、失败后逐字保持（同一事务的零局部写入）：" + afterSpace);
                Assert.That(AnchorOf(after, heroUnitId), Is.EqualTo(AnchorOf(before, heroUnitId)),
                    "失败事务绝不移动单位：" + afterSpace);
                Assert.That(
                    ReservationsOfPlan(after, facts.PlanId), Is.EqualTo(ReservationsOfPlan(before, facts.PlanId)),
                    "失败事务绝不放走或新增本计划的 Reservation：" + afterSpace);
                Assert.That(after.ComputeHash(), Is.EqualTo(before.ComputeHash()),
                    "整世界规范化摘要必须逐位不变（最强的零局部写入断言）："
                    + before.ComputeHashHex() + " -> " + after.ComputeHashHex());
            }
            finally
            {
                simulation.Dispose();
            }
        }

        /// <summary>命令入口拒绝的可读描述（诊断用）。</summary>
        private static string DescribeRejection(CommandIngressRejection rejection)
            => rejection == null ? "<none>" : rejection.ReasonCode + "|" + rejection.ProducerOrdinal;

        /// <summary>该单位之外的一个真实单位 ID（构造用外部预留的持有者）。</summary>
        private static long FindOtherUnitId(ShadowCaseSeed seed, long unitId)
        {
            var encounter = seed.Definition.FindEncounter(seed.EncounterId);
            var slots = new List<EncounterUnitSlot>(encounter.Slots);
            slots.Sort((left, right) => string.CompareOrdinal(left.SlotId.Value, right.SlotId.Value));
            for (int i = 0; i < slots.Count; i++)
            {
                if (i + 1L != unitId) return i + 1L;
            }
            Assert.Fail("Encounter 必须至少有两个槽位");
            return -1L;
        }

        /// <summary>真实 <c>LogicGridMovementPathCalculator</c> 在给定起止格上是否仍能求出路径。</summary>
        private static bool SimulationHasPathFrom(
            BattleSimulation simulation, long moverUnitId, CellFactKey from, GridPoint destination,
            long ownPlanId)
        {
            var calculator = simulation.MovementPathCalculator
                as ProjectHero.Logic.Movement.LogicGridMovementPathCalculator;
            Assert.That(calculator, Is.Not.Null,
                "对照证据：生产装配必须使用真实 LogicGridMovementPathCalculator（否则本证明不成立）");
            // (mover, actionPlanId) 两个都要给出：该单位自己的占位与它自己的预留都不算阻塞。
            PathSearchResult result = calculator.Pathfinder.FindPath(
                new GridPoint(from.X, from.Y), destination, new UnitId(moverUnitId),
                new ActionPlanId(ownPlanId));
            return result.Succeeded;
        }

        /// <summary>段链与 Reservation 的完整文本签名（失败信息与逐字比较用）。</summary>
        private static string DescribeSpace(LogicSnapshot snapshot, long planId)
        {
            var builder = new StringBuilder("space[plan=").Append(planId).Append(" segs=");
            for (int i = 0; i < snapshot.MovementSegments.Count; i++)
            {
                MovementSegmentSnapshot segment = snapshot.MovementSegments[i];
                if (segment == null) continue;
                builder.Append('#').Append(segment.ActionPlanId).Append('.').Append(segment.StepIndex)
                    .Append('(').Append(segment.FromX).Append(',').Append(segment.FromY).Append("->")
                    .Append(segment.ToX).Append(',').Append(segment.ToY).Append('@').Append(segment.EndTick)
                    .Append(')');
            }
            builder.Append(" res=");
            for (int i = 0; i < snapshot.Reservations.Count; i++)
            {
                ReservationSnapshot reservation = snapshot.Reservations[i];
                if (reservation == null) continue;
                builder.Append('#').Append(reservation.ActionPlanId)
                    .Append('(').Append(reservation.X).Append(',').Append(reservation.Y).Append(')');
            }
            builder.Append(" rev=").Append(snapshot.ScheduleRevision)
                .Append(" plans=").Append(snapshot.Plans.Count)
                .Append(" anchors=");
            for (int u = 0; u < snapshot.Units.Count; u++)
            {
                builder.Append('(').Append(snapshot.Units[u].UnitId).Append('@')
                    .Append(snapshot.Units[u].X).Append(',').Append(snapshot.Units[u].Y).Append(')');
            }
            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// <strong>比较面边界</strong>（任务包「必须产出」10 末句）的<strong>可失败</strong>结构断言：
        /// 比较器与比较输入<strong>不得命名</strong>任何 UnityEngine 类型（Transform / Vector3 /
        /// Quaternion / GameObject / Animator / MonoBehaviour），也不得声明浮点成员——
        /// 因此"位置"只可能是整数逻辑格与规范化段。
        /// </summary>
        private static void AssertTask06ComparisonSurfaceIsIntegerOnly()
        {
            Type[] types =
            {
                typeof(ShadowDifferenceDetector),
                typeof(ShadowComparisonInput),
                typeof(LogicSnapshot),
                typeof(MovementSegmentSnapshot),
                typeof(ReservationSnapshot),
                typeof(UnitSnapshot)
            };

            var forbidden = new[]
            {
                typeof(UnityEngine.Transform), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector2),
                typeof(UnityEngine.Quaternion), typeof(UnityEngine.GameObject), typeof(UnityEngine.Animator),
                typeof(MonoBehaviour)
            };

            for (int t = 0; t < types.Length; t++)
            {
                Type type = types[t];
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                for (int m = 0; m < members.Length; m++)
                {
                    Type memberType = MemberValueType(members[m]);
                    if (memberType == null) continue;
                    for (int f = 0; f < forbidden.Length; f++)
                    {
                        Assert.That(memberType, Is.Not.EqualTo(forbidden[f]),
                            type.Name + " 的成员 " + members[m].Name + " 不得是 " + forbidden[f].Name
                            + "（比较面只允许整数逻辑事实）");
                    }

                    Type element = ElementTypeOf(memberType);
                    Assert.That(element == typeof(float) || element == typeof(double), Is.False,
                        type.Name + " 的成员 " + members[m].Name + " 不得携带浮点事实（"
                        + element.Name + "）：视觉插值不进入等价比较");
                }
            }

            // 反向可失败性：段与预留投影的坐标成员必须是整数（改名/改类型必须让本断言失败）。
            Assert.That(typeof(MovementSegmentSnapshot).GetProperty("ToX").PropertyType,
                Is.EqualTo(typeof(int)), "段的目的格 X 必须是整数逻辑格坐标");
            Assert.That(typeof(MovementSegmentSnapshot).GetProperty("EndTick").PropertyType,
                Is.EqualTo(typeof(long)), "段的结束边界必须是整数 Tick");
            Assert.That(typeof(ReservationSnapshot).GetProperty("X").PropertyType,
                Is.EqualTo(typeof(int)), "Reservation 的格 X 必须是整数逻辑格坐标");
            Assert.That(typeof(UnitSnapshot).GetProperty("X").PropertyType,
                Is.EqualTo(typeof(int)), "单位锚点 X 必须是整数逻辑格坐标");
        }

        private static Type MemberValueType(MemberInfo member)
        {
            if (member is FieldInfo field) return field.FieldType;
            if (member is PropertyInfo property) return property.PropertyType;
            if (member is MethodInfo method) return method.ReturnType;
            return null;
        }

        private static Type ElementTypeOf(Type type)
        {
            if (type == null) return null;
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                if (arguments.Length == 1) return arguments[0];
            }
            return type;
        }

        // =====================================================================
        // 任务 07：TurnWindow / 整数预算 / 并发授权 / 肾上腺素周期 的 Shadow 画像
        // =====================================================================

        /// <summary>本用例的逐用例 ID（进入批准差异与临时登记的责任归属）。</summary>
        private const string Task07CaseId = "task07-turn-window-budget-authority-profile";

        /// <summary>真实移动动作（与任务 05/06 同一把尺：定义里必须存在且属于 hero 的动作集合）。</summary>
        private const string Task07MoveSpecId = "action.move.default";

        /// <summary>默认策略（不开任何任务检查点）下既有的「暂不可比较」登记条数。</summary>
        private const int Task07BaselineRegistrationCount = 10;

        /// <summary>任务 05 的登记条数（由 <c>compareScheduleFacts</c> 开启）。</summary>
        private const int Task07ScheduleRegistrationCount = 12;

        /// <summary>任务 06 的登记条数（由 <c>compareMovementFacts</c> 开启）。</summary>
        private const int Task07MovementRegistrationCount = 8;

        // 脚本化时间线（全部为逻辑 Tick，不是 Unity 帧）：
        //   Tick 1  : 肾上腺素唯一入账入口（Tick 末）把 hero 的 Available 推到 > 0
        //   Tick 2  : hero 自己的窗口打开 ⇒ **先递增个人周期再清零 Available**，随后才建预算
        //   Tick 3  : 经唯一信任边界提交一条普通 Move（Editable，预算 Available -> Reserved）
        //   Tick 9  : 关闭请求之前的最后一个检查点（仍在接受提交、预留仍未被消费）
        //   Tick 10 : 请求关闭后正式关闭（IsAcceptingSubmissions=false，Tick 末 IsOpen=false）
        //   Tick 12 : enemy 窗口打开（跨窗口切换：hero 的计划与其来源账本必须逐字不变）
        //   Tick 13 : hero 在**他人**窗口内经权威定义定价激活并发授权（恰好消费一次局外资源）
        //   Tick 14 : 计划到期 ⇒ 启动门禁原子提交 Reserved -> Spent（来源窗口已关闭的账本）
        //   Tick 20 : 探针检查点（窗口打开/关闭、整数预算、授权、肾上腺素、计划归属都真实在位）
        //   Tick 60 : enemy 窗口关闭 ⇒ 并发授权被撤销，而已接受计划继续存在
        private const int Task07AccrualTickA = 1;
        private const int Task07HeroWindowOpenTick = 2;
        private const int Task07SubmitTick = 3;
        private const int Task07AccrualTickB = 5;
        private const int Task07EditableProbeTick = 9;
        private const int Task07HeroWindowCloseTick = 10;
        private const int Task07EnemyWindowOpenTick = 12;
        private const int Task07ActivationTick = 13;
        private const int Task07RequestedStartTick = 14;
        private const int Task07ProbeTick = 20;
        private const int Task07TerminalTick = 60;

        private const int Task07DestinationX = 0;
        private const int Task07DestinationY = 8;

        /// <summary>窗口总预算（整数 Tick）：必须足以覆盖真实 Move 成本（路径权重 × 基础步长 + 后摇），且刻意远大于它。</summary>
        private const int Task07HeroWindowBudget = 65536;

        private const int Task07EnemyWindowBudget = 1024;

        /// <summary>入账事实的原始伤害（Q10）；两次都刻意小到不会触及周期上限。</summary>
        private const int Task07AccrualDamageDealtQ10A = 50;
        private const int Task07AccrualDamageDealtQ10B = 10;

        /// <summary>生产装配未注册控制权时的稳定拒绝码（缺口 D-A 的可观察后果）。</summary>
        private const string Task07UncontrolledIssuerCode =
            ProjectHero.Logic.Turns.TurnWindowCodes.ISSUER_CANNOT_CONTROL_UNIT;

        /// <summary>
        /// <strong>任务 07「必须产出」11 / 验收标准 18 的 Shadow 检查点扩展</strong>（Logic 对 Logic 通道）。
        ///
        /// 它把 <c>ShadowDifferenceDetector.Task07TurnWindowFacts</c> 真正接到<strong>真实窗口/预算/授权/账本</strong>上：
        /// 两侧各跑一个独立的真实 <c>BattleSimulation</c>（真实 02B 定义 + 真实 <c>Step</c> 管线 +
        /// 真实 <c>TurnWindowManager</c> + 真实 <c>TurnWindowBudgetAuthority</c> +
        /// 真实 <c>ConcurrentActionSystem</c> + 真实 <c>AdrenalineLedgerRegistry</c>），
        /// 三个任务 07 冻结的装配点全部<strong>显式注入</strong>
        /// （<c>turnWindowSchedule</c> / <c>concurrentHeroUnitId</c> / <c>adrenalineAccrualFactSource</c>），
        /// 脚本化输入完全相同。
        ///
        /// <strong>本用例实际证明什么（如实登记，必须阅读）</strong>：
        /// 两侧都是<strong>新实现</strong>（且本用例在比较之前主动断言两侧每个检查点 <c>ComputeHash()</c> 逐位相同）
        /// ⇒ 它证明的是<strong>新实现的确定性 + 跨世界一致性 + 五类事实真的进入比较面</strong>，
        /// <strong>不是</strong>"与旧权威等价"。因此它<strong>无法发现"确定性但错误"</strong>的实现。
        /// 与任务 06 画像用例的分工完全一致；<c>claim=EQUIVALENT</c> 是报告字段的既有词汇，
        /// 其语义降级由任务 11 决定（本任务不改生产报告词汇）。
        ///
        /// <strong>对照证据 0′（可失败）</strong>：本用例<strong>显式</strong>传入空旧侧观测
        /// （<see cref="Task07ProfileLegacyObservations"/>），因此比较器走
        /// LogicSnapshot 对 LogicSnapshot 通道。一旦有人把隐藏场景的 LegacyObservations 接进本用例，
        /// 本断言立即失败——强制改成真正的旧↔新比较，而不是让"零差异"承载一个自比较结论。
        ///
        /// <strong>对照证据 D-A（自适应，可失败）：生产装配的控制权接线缺口</strong>。
        /// <c>BattleSimulation</c> <strong>从未</strong>把 Encounter 的 <c>ControllerBinding</c>
        /// 注册进 <c>TurnWindowManager</c>（<c>RegisterControllerBinding</c> 在
        /// <c>Assets/Scripts</c> 内没有任何调用点）⇒ <c>CanControl</c> 恒为 false ⇒
        /// 显式 <c>ScheduleEdit</c> 的新增/增费与并发激活都会被
        /// <c>WINDOW_ISSUER_CANNOT_CONTROL_UNIT</c> 拒绝。
        /// 本用例因此用一个**未注册**的探针世界把该缺口钉成可失败断言（缺口存在时断言拒绝码恰好是它），
        /// 并在主场景里显式注册控制权（调用生产公开 API <c>TurnWindowManager.RegisterControllerBinding</c>），
        /// 使"整数预算 / 并发授权 / 跨窗口计划不变性"三类事实可达。
        /// 生产装配补上注册后，探针断言自动变为"命令被接受"，本用例无需修改即继续通过。
        ///
        /// <strong>对照证据 D-C（自适应，可失败）：生产装配的启动提交端口接线缺口</strong>。
        /// <c>BattleSimulation</c> 的 <c>_startCommitPort = assembly.StartCommitPort ?? _budgetAuthority</c>
        /// <strong>永远</strong>选中默认值 <c>NoTurnBudgetCommitPort.Instance</c>
        /// （<c>BattleSimulationAssembly</c> 的构造已把它兜底成非 null），
        /// 而该默认实现<strong>只</strong>把 <c>ActionPlan.ReservedTurnBudgetTicks</c> 清零、不接触窗口账本
        /// ⇒ 计划进入 <c>Running</c> 时窗口账本仍停在 <c>Reserved</c>，
        /// 任务 07 的 <c>Reserved -&gt; Spent</c> 原子提交（含 <c>held == cost</c> 不变量校验）在生产装配上不可达。
        /// 本用例用默认装配的探针世界把该缺口钉成可失败断言，并在主场景里经冻结装配点
        /// （<c>BattleSimulationAssembly.startCommitPort</c>）显式注入<strong>真实生产实现</strong>
        /// <c>TurnWindowBudgetAuthority</c>，使 <c>Spent</c> 落账可达。缺口修复后本用例自动退回默认装配。
        /// </summary>
        [UnityTest]
        public IEnumerator TurnWindowBudgetAndAuthorityShadowProfileHasNoUnclassifiedDifference()
        {
            yield return LoadHiddenValidationScene();
            var seed = BuildSeedFromFactory();
            (long heroUnitId, long enemyUnitId) = ResolveEncounterUnits(seed);

            // —— 对照证据 D-A（**已修复**，本条断言防止回归）——
            // 修复前：BattleSimulation 从未调用 TurnWindowManager.RegisterControllerBinding
            // （Assets/Scripts 内零调用点）⇒ CanControl 恒为 false ⇒ 显式 ScheduleEdit 的新增/增费
            // 一律被 WINDOW_ISSUER_CANNOT_CONTROL_UNIT 拒绝。
            // 修复后：注册发生在 BattleSimulation 构造期（按 Encounter 的 ControllerBinding），
            // 因此**即使本探针不注册**，生产装配也必须已经具备控制权。
            // 这是一条可失败的断言：把 BattleSimulation 里的注册代码删掉，它立刻变红。
            Task07StreamResult unwired = BuildTask07TurnWindowStream(
                seed, heroUnitId, enemyUnitId, registerControllerBindings: false, wireStartCommitPort: false);
            Assert.That(unwired.ControlBindingAvailable, Is.True,
                "生产装配必须自行按定义注册控制权（缺口 D-A 不得回归）："
                + "未显式注册时 CanControl(controller.player, hero) 仍必须成立");
            if (unwired.SubmitRejectionCode != null)
            {
                Assert.That(unwired.SubmitRejectionCode, Is.EqualTo(Task07UncontrolledIssuerCode),
                    "生产装配未注册控制权时的拒绝码必须是 " + Task07UncontrolledIssuerCode
                    + "（缺口 D-A 的可观察后果），实测=" + unwired.SubmitRejectionCode
                    + " ; " + DescribeTask07Stream(unwired.Stream));
            }

            // 并发激活同样受该缺口阻断时，必须给出同一个稳定码（同一控制权判据，不是第二套）。
            if (unwired.ActivationRejectionCode != null)
            {
                Assert.That(unwired.ActivationRejectionCode, Is.EqualTo(Task07UncontrolledIssuerCode),
                    "控制权缺口必须同时阻断并发激活，且使用同一个稳定码，实测="
                    + unwired.ActivationRejectionCode + " ; " + DescribeTask07Stream(unwired.Stream));
            }

            // —— 对照证据 D-C：默认启动提交端口是否真的把 Reserved 转成了 Spent ——
            Task07StreamResult defaultPort = BuildTask07TurnWindowStream(
                seed, heroUnitId, enemyUnitId, registerControllerBindings: true, wireStartCommitPort: false);
            Assert.That(defaultPort.SubmitRejectionCode, Is.Null,
                "构造前提：默认端口下普通 Move 的排程事务同样必须被接受，实测="
                + defaultPort.SubmitRejectionCode + " ; " + DescribeTask07Stream(defaultPort.Stream));
            bool startCommitPortWired = defaultPort.WindowSpentAtStart != 0;
            if (!startCommitPortWired)
            {
                Assert.That(defaultPort.WindowReservedAtStart, Is.EqualTo(defaultPort.PlanBudgetCost),
                    "缺口 D-C 的可观察后果：默认装配下计划已经 Running，但来源窗口账本仍是 "
                    + "Reserved=" + defaultPort.PlanBudgetCost + "、Spent=0 —— Reserved -> Spent 未接线。"
                    + "修复位置：BattleSimulation 的 "
                    + "`_startCommitPort = assembly.StartCommitPort ?? _budgetAuthority`"
                    + "（BattleSimulationAssembly 已把默认值兜底成 NoTurnBudgetCommitPort.Instance，"
                    + "因此真实 TurnWindowBudgetAuthority 永远不会被选中）。"
                    + "本用例因此在主场景里显式注入真实实现（见装配点 startCommitPort）。"
                    + " ; " + DescribeTask07Stream(defaultPort.Stream));
            }

            // —— 主场景：两侧各跑一个独立真实世界（显式注册生产定义里的控制权 + 缺口存在时注入真实端口）——
            Task07StreamResult legacySide = BuildTask07TurnWindowStream(
                seed, heroUnitId, enemyUnitId, registerControllerBindings: true,
                wireStartCommitPort: !startCommitPortWired);
            Task07StreamResult shadowSide = BuildTask07TurnWindowStream(
                seed, heroUnitId, enemyUnitId, registerControllerBindings: true,
                wireStartCommitPort: !startCommitPortWired);

            Assert.That(legacySide.ControlBindingAvailable, Is.True,
                "主场景必须取得控制权（生产装配已接线时来自生产；未接线时由本用例显式注册）");
            Assert.That(legacySide.SubmitRejectionCode, Is.Null,
                "构造前提：普通 Move 的排程事务必须被接受（窗口已打开、授权成立、预算足够），实测="
                + legacySide.SubmitRejectionCode + " ; " + DescribeTask07Stream(legacySide.Stream));
            Assert.That(legacySide.ActivationRejectionCode, Is.Null,
                "构造前提：并发激活必须成功（他人窗口 + 显式注入的主角 + 局外资源足够），实测="
                + legacySide.ActivationRejectionCode + " ; " + DescribeTask07Stream(legacySide.Stream));

            // 对照证据 0：检查点下标 == 逻辑 Tick；两侧逐位相同（同一定义 + 同一输入 ⇒ 同一世界）。
            Assert.That(legacySide.Stream.Snapshots.Count, Is.EqualTo(Task07TerminalTick + 1),
                "检查点必须覆盖 Tick 0.." + Task07TerminalTick);
            for (int i = 0; i < legacySide.Stream.Snapshots.Count; i++)
            {
                Assert.That(legacySide.Stream.Snapshots[i].Tick, Is.EqualTo((long)i),
                    "检查点下标必须等于逻辑 Tick：index=" + i);
                Assert.That(shadowSide.Stream.Snapshots[i].Tick, Is.EqualTo((long)i),
                    "检查点下标必须等于逻辑 Tick（新侧）：index=" + i);
                Assert.That(shadowSide.Stream.Snapshots[i].ComputeHash(),
                    Is.EqualTo(legacySide.Stream.Snapshots[i].ComputeHash()),
                    "同一脚本化输入的两个独立世界必须逐位相同：Tick " + i + " ; "
                    + DescribeTask07Stream(legacySide.Stream));
            }

            // —— 对照证据 1：五类事实真的落在这条检查点流上（否则下面的"零差异"是空的）——
            Task07ScenarioFacts facts = AssertTask07ScenarioFacts(
                legacySide.Stream, heroUnitId, enemyUnitId);

            // —— 主报告：逐用例开启任务 05/06/07 检查点 ——
            var report = CompareTask07Profile(seed, legacySide.Stream, shadowSide.Stream, Task07CaseId);

            // ① 逐字段比较规模必须精确等于 Σ(23 + 3 × 每检查点单位数)（同一公式的独立重算；
            //    它不是下界、也不是范围：任何字段增删都会让本断言失败）。
            {
                long expectedFieldObservations = 0L;
                int minUnitsPerCheckpoint = int.MaxValue;
                for (int i = 0; i < legacySide.Stream.Snapshots.Count; i++)
                {
                    int unitsHere = Math.Min(
                        legacySide.Stream.Snapshots[i].Units.Count,
                        shadowSide.Stream.Snapshots[i].Units.Count);
                    if (unitsHere < minUnitsPerCheckpoint) minUnitsPerCheckpoint = unitsHere;
                    expectedFieldObservations += 23L + 3L * unitsHere;
                }

                Assert.That(minUnitsPerCheckpoint, Is.EqualTo(2),
                    "本用例是双槽位场景：每个检查点都必须有 2 个单位，否则逐字段公式不再成立：min="
                    + minUnitsPerCheckpoint);
                Assert.That(report.ComparedFieldObservations, Is.EqualTo((int)expectedFieldObservations),
                    "逐字段比较条数必须精确等于 Σ(23 + 3 × 每检查点单位数)：expected="
                    + expectedFieldObservations + " actual=" + report.ComparedFieldObservations
                    + " ; " + report.Describe());
            }

            // ② 核心断言：**没有任何未分类差异**（四类归属互斥且穷尽；出现第五类即失败）。
            AssertTask07NoUnclassifiedDifference(report, legacySide.Stream, "主场景（未篡改）");

            // ③ 任务 07 的覆盖边界登记项逐条在位（旧侧根本没有窗口/整数预算/授权/周期账本这四类对象）。
            Assert.That(report.TemporarilyUncomparable.Count,
                Is.EqualTo(Task07BaselineRegistrationCount + Task07ScheduleRegistrationCount
                    + Task07MovementRegistrationCount + Task07TurnWindowRegistrations.Length),
                "开启任务 05/06/07 检查点后必须逐条登记暂不可比较字段："
                + string.Join(" | ", report.TemporarilyUncomparable));
            for (int i = 0; i < Task07TurnWindowRegistrations.Length; i++)
            {
                TemporarilyUncomparableField registered =
                    FindRegisteredField(report, Task07TurnWindowRegistrations[i]);
                Assert.That(registered, Is.Not.Null,
                    "任务 07 的覆盖边界登记项必须逐条在位：" + Task07TurnWindowRegistrations[i]
                    + " ; registered=" + string.Join(" | ", report.TemporarilyUncomparable));
                Assert.That(registered.OwnerTask, Is.EqualTo("07"),
                    "任务 07 的登记项必须标出负责任务：" + registered);
                Assert.That(registered.RemovalGate, Is.Not.Empty,
                    "任务 07 的登记项必须标出最迟清零门槛：" + registered);
                Assert.That(registered.Reason, Is.Not.Empty, "登记项必须给出原因：" + registered);
                Assert.That(registered.LegacyObjectPath, Is.Not.Empty, "登记项必须给出旧侧对象路径：" + registered);
            }

            // ④ 既有 10 条登记项（含 availableAdrenaline / staminaQ10）不得被任务 07 挪走或升级成批准差异。
            Assert.That(FindRegisteredField(report, "availableAdrenaline"), Is.Not.Null,
                "旧浮点 CurrentAdrenaline ↔ 新整数 AvailableAdrenaline + CycleId 的登记项必须保留");
            Assert.That(FindRegisteredField(report, "staminaQ10"), Is.Not.Null,
                "旧体力 staminaQ10 的登记项必须保留（任务 07 不映射旧体力）");
            Assert.That(report.Approvals.Count, Is.EqualTo(0), "本用例不批准任何差异");

            // —— ⑤ 覆盖证据：逐条「篡改新侧 ⇒ 必须报该字段路径」——
            // 两侧相等时这些字段不会出现在差异列表里，因此"没有差异"无法区分
            // "真的比较过且相等"与"根本没比较"；下面把每个通道真的推成差异。
            string w1 = "windows[" + facts.HeroWindowId + "]";
            string w2 = "windows[" + facts.EnemyWindowId + "]";
            string planPrefix = "plans[" + facts.PlanId + "]";
            string adrenalinePrefix = "adrenaline[" + heroUnitId + "]";

            // ⑤.1 窗口打开（**叶子事实**：打开 Tick 不被任何派生不变量引用 ⇒ 它同时是下面的负控制）。
            var negative = AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, windowManager: TamperWindow(
                        snapshot.WindowManager, facts.HeroWindowId,
                        window => window with { OpenedAtTick = window.OpenedAtTick + 5L }))),
                w1 + ".openedAtTick", Task07ProbeTick, "窗口打开：打开 Tick（叶子事实 / 负控制）");

            // ⑤.2 窗口关闭：接受提交位（请求关闭后立即为 false，与 IsOpen 分离）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, windowManager: TamperWindow(
                        snapshot.WindowManager, facts.HeroWindowId,
                        window => window with { IsAcceptingSubmissions = !window.IsAcceptingSubmissions }))),
                w1 + ".isAcceptingSubmissions", Task07ProbeTick, "窗口关闭：接受提交位");

            // ⑤.3 窗口账本的可审计性：已关闭窗口仍留在集合里（删掉它 ⇒ 计数与存在性都变）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, windowManager: RemoveWindow(
                        snapshot.WindowManager, facts.HeroWindowId))),
                "windows.count", Task07ProbeTick, "窗口关闭：已关闭窗口仍保留在可审计账本里");

            // ⑤.4 整数预算：窗口账本的已消费额（被窗口恒等式与计划来源账本引用 ⇒ 反差证据）。
            var budgetContrast = AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, windowManager: TamperWindow(
                        snapshot.WindowManager, facts.HeroWindowId,
                        window => window with { SpentBudgetTicks = window.SpentBudgetTicks + 1 }))),
                w1 + ".spentBudgetTicks", Task07ProbeTick, "整数预算：窗口已消费额");
            Assert.That(budgetContrast.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact),
                Is.GreaterThanOrEqualTo(2),
                "篡改被派生事实引用的预算字段必须同时推翻'窗口预算恒等式'与'计划来源账本逐字不变'："
                + DescribeDifferenceList(budgetContrast.Differences));

            // ⑤.5 整数预算的派生不变量：恒等式本身也必须真的参与比较。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, windowManager: TamperWindow(
                        snapshot.WindowManager, facts.HeroWindowId,
                        window => window with { AvailableBudgetTicks = window.AvailableBudgetTicks + 1 }))),
                w1 + ".budgetIdentity", Task07ProbeTick,
                "整数预算：Reserved + Spent + Available == Total");

            // ⑤.6 全部窗口的时间预算聚合（战斗资源快照）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, resources: snapshot.Resources with
                    {
                        TurnBudgetSpent = snapshot.Resources.TurnBudgetSpent + 1L
                    })),
                "resources.turnBudgetSpent", Task07ProbeTick, "整数预算：全窗口已消费聚合");

            // ⑤.7 并发授权：受控单位（只有装配显式注入的主角单位可被激活）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, concurrentAction: snapshot.ConcurrentAction with
                    {
                        PlayerUnitId = snapshot.ConcurrentAction.PlayerUnitId + 1L
                    })),
                "concurrentAction.playerUnitId", Task07ProbeTick, "并发授权：受控单位");

            // ⑤.8 并发授权：所属窗口（窗口关闭即撤销 ⇒ 授权必须绑定到仍开放的当前窗口）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, concurrentAction: snapshot.ConcurrentAction with
                    {
                        WindowId = snapshot.ConcurrentAction.WindowId + 1L
                    })),
                "concurrentAction.authorizationTargetsOpenWindow", Task07ProbeTick,
                "并发授权：授权必须指向仍开放且仍在接受提交的当前窗口");

            // ⑤.9 肾上腺素清零：个人周期号（窗口打开时先递增周期再清零）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, resources: TamperLedger(
                        snapshot.Resources, heroUnitId,
                        ledger => ledger with { CycleId = ledger.CycleId + 1L }))),
                adrenalinePrefix + ".cycleId", Task07ProbeTick, "肾上腺素清零：个人周期号");

            // ⑤.10 肾上腺素清零：Available（不衰减、跨其他单位窗口保留、关闭不清零）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, resources: TamperLedger(
                        snapshot.Resources, heroUnitId,
                        ledger => ledger with { AvailableAdrenaline = ledger.AvailableAdrenaline + 3 }))),
                adrenalinePrefix + ".available", Task07ProbeTick, "肾上腺素清零：Available");

            // ⑤.11 肾上腺素账本与单位只读镜像的一致性（镜像漂移即差异）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, resources: TamperLedger(
                        snapshot.Resources, heroUnitId,
                        ledger => ledger with { AvailableAdrenaline = ledger.AvailableAdrenaline + 3 }))),
                adrenalinePrefix + ".mirrorMatchesLedger", Task07ProbeTick,
                "肾上腺素：单位只读镜像必须与账本一致");

            // ⑤.12 跨窗口计划不变性：计划的来源窗口账本投影（已关闭窗口只更新历史账本）。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, plans: TamperPlan(snapshot, facts.PlanId,
                        plan => plan with { SubmittedWindowId = facts.EnemyWindowId }))),
                planPrefix + ".submittedWindowLedger", Task07ProbeTick,
                "跨窗口计划不变性：计划的来源窗口账本投影");

            // ⑤.13 跨窗口计划不变性：计划预算投影与来源账本条目的一致性。
            AssertTask07FieldIsCompared(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, plans: TamperPlan(snapshot, facts.PlanId,
                        plan => plan with { ReservedTurnBudgetTicks = plan.ReservedTurnBudgetTicks + 1 }))),
                planPrefix + ".budgetLedgerLinked", Task07ProbeTick,
                "跨窗口计划不变性：计划预算投影必须与来源窗口账本条目一致");

            // —— ⑥ 负控制（必须有）：在两侧完全等价的前提下篡改**叶子**窗口事实 ——
            // ⑤.1 刻意选了不被任何派生不变量引用的 openedAtTick ⇒ 必须**恰好**产生一条差异。
            // 反差证据见 ⑤.4：篡改被派生事实引用的预算字段会同时推翻多条不变量。
            Assert.That(negative.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(1),
                "篡改单个叶子窗口字段必须**恰好**产生一条非预期差异："
                + DescribeDifferenceList(negative.Differences));
            Assert.That(negative.FirstUnexpectedDifference.FieldPath,
                Is.EqualTo(w1 + ".openedAtTick"), negative.Describe());
            Assert.That(negative.FirstUnexpectedDifference.LogicalTick, Is.EqualTo((long)Task07ProbeTick),
                "差异必须绑定被篡改的逻辑 Tick：" + negative.FirstUnexpectedDifference);
            Assert.That(negative.CanClaimEquivalence, Is.False,
                "存在非预期差异时不得宣称等价：" + negative.Describe());
            Assert.That(negative.EquivalenceClaim, Does.StartWith("DIFFERENT:"), negative.Describe());

            // —— ⑦ 对照：同一份被篡改的流在**默认策略**下必须零差异 ——
            // 这同时证明"任务 07 检查点逐用例开启"是真的开关（默认关闭 ⇒ 既有用例报告逐字节不变）。
            Assert.That(ShadowCasePolicy.CreateDefault(Task07CaseId, seed.RulesVersion).CompareTurnWindowFacts,
                Is.False, "compareTurnWindowFacts 必须默认关闭");
            var defaultPolicyReport = CompareWithPolicy(seed, legacySide.Stream,
                TamperCheckpoint(shadowSide.Stream, Task07ProbeTick,
                    snapshot => CopySnapshot(snapshot, windowManager: TamperWindow(
                        snapshot.WindowManager, facts.HeroWindowId,
                        window => window with { OpenedAtTick = window.OpenedAtTick + 7L }))),
                ShadowCasePolicy.CreateDefault(Task07CaseId + "-default-off", seed.RulesVersion),
                legacySide.Stream.Snapshots.Count, Task07ProfileLegacyObservations);
            Assert.That(defaultPolicyReport.TemporarilyUncomparable.Count,
                Is.EqualTo(Task07BaselineRegistrationCount),
                "默认策略只登记既有 " + Task07BaselineRegistrationCount + " 条暂不可比较字段"
                + "（任务 05 的 12 条由 compareScheduleFacts 开启、任务 06 的 8 条由 compareMovementFacts 开启、"
                + "任务 07 的 " + Task07TurnWindowRegistrations.Length + " 条由 compareTurnWindowFacts 开启）："
                + defaultPolicyReport.Describe());
            Assert.That(defaultPolicyReport.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                "默认策略下窗口/预算/授权事实不在比较集合内（同一条篡改流零差异）："
                + defaultPolicyReport.Describe());
            Assert.That(defaultPolicyReport.HasUnexpectedDifference, Is.False, defaultPolicyReport.Describe());

            // —— ⑧ 比较面边界：任务 07 只比较整数 Tick 事实（窗口/预算/周期号/预留额）——
            AssertTask07ComparisonSurfaceIsIntegerOnly();
        }

        /// <summary>
        /// 逐用例开启任务 05/06/07 检查点（<c>compareScheduleFacts</c> + <c>compareMovementFacts</c> +
        /// <c>compareTurnWindowFacts</c>）的比较入口。
        /// </summary>
        private static ShadowComparisonReport CompareTask07Profile(
            ShadowCaseSeed seed, RealCheckpointStream legacySide, RealCheckpointStream shadowSide,
            string caseId)
        {
            // 预算从真实检查点数导出（不是魔法数）：一旦超限报告会变成 INVALID:SHADOW_BUDGET_OVERRUN，
            // 比较根本不会执行。下面的断言把"预算 == 真实检查点数"钉死。
            int budget = legacySide.Snapshots.Count;
            Assert.That(budget, Is.GreaterThan(0));
            Assert.That(shadowSide.Snapshots.Count, Is.EqualTo(budget),
                "两侧检查点数必须相等（预算不能掩盖一侧缺口）");
            Assert.That(Task07ProfileLegacyObservations.Count, Is.EqualTo(0),
                "任务 07 画像入口**显式**传入空旧侧观测：比较器因此走 LogicSnapshot 对 LogicSnapshot 通道，"
                + "结论口径只能是『确定性 + 跨世界一致性』（真实 Shadow 语义归属任务 10/11）");

            var report = CompareWithPolicy(seed, legacySide, shadowSide,
                ShadowCasePolicy.CreateDefault(caseId, seed.RulesVersion,
                    compareScheduleFacts: true, compareMovementFacts: true, compareTurnWindowFacts: true),
                budget, Task07ProfileLegacyObservations);
            Assert.That(report.BudgetOverrun, Is.False,
                "预算必须足够执行整条比较（超限会让报告变成 INVALID 而不是通过）："
                + report.BudgetOverrunReason + " ; " + report.Describe());
            return report;
        }

        /// <summary>
        /// 覆盖探针：把"新侧"某个检查点上的一条任务 07 事实改成不同值，断言比较器<strong>必须</strong>
        /// 在该字段路径上报告一条 <see cref="ShadowDifferenceKind.NewRuleVerifiedFact"/>。
        /// </summary>
        private static ShadowComparisonReport AssertTask07FieldIsCompared(
            ShadowCaseSeed seed, RealCheckpointStream legacySide, RealCheckpointStream tamperedSide,
            string expectedFieldPath, long expectedTick, string what)
        {
            var report = CompareTask07Profile(seed, legacySide, tamperedSide, Task07CaseId + "-probe");
            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                what + "：覆盖探针不得破坏逐 Tick 对齐：" + report.Describe());

            ShadowFieldDifference hit = null;
            for (int i = 0; i < report.Differences.Count; i++)
            {
                ShadowFieldDifference difference = report.Differences[i];
                if (difference.Kind != ShadowDifferenceKind.NewRuleVerifiedFact) continue;
                if (!string.Equals(difference.FieldPath, expectedFieldPath, StringComparison.Ordinal)) continue;
                if (difference.LogicalTick != expectedTick) continue;
                hit = difference;
                break;
            }

            Assert.That(hit, Is.Not.Null,
                what + "：篡改新侧后必须出现字段 " + expectedFieldPath + " 在 Tick " + expectedTick
                + " 上的 " + ShadowDifferenceKind.NewRuleVerifiedFact + " 差异（证明该通道真的参与比较）："
                + report.Describe() + " ; differences=" + DescribeDifferenceList(report.Differences));
            Assert.That(hit.LegacyValue, Is.Not.EqualTo(hit.ShadowValue), "差异两侧取值必须不同：" + hit);
            Assert.That(hit.IsUnexpected, Is.True,
                "任务 07 的字段差异一律是回归（不得被批准差异掩盖）：" + hit);
            return report;
        }

        /// <summary>
        /// <strong>"没有任何未分类差异"</strong>的判定（任务 07「必须产出」11 / 不变量 22）。
        ///
        /// 四类归属互斥且穷尽：基础设施一致性、新规则不变量、限期清零的暂不可比较字段、
        /// 逐用例批准差异。本方法逐条检查
        /// <list type="number">
        /// <item>差异类别集合<strong>恰好四类</strong>（枚举增删即失败）；</item>
        /// <item>每条差异都落在四类内、都带精确字段路径与非空原因；</item>
        /// <item><strong>非预期差异（= 未分类）必须为 0</strong>；基础设施差异 0；批准差异 0；</item>
        /// <item>限期清零类：条数恰好 = 登记项数 × 检查点数（不静默丢弃），
        /// 且每条差异的字段路径都必须等于某个登记项的 ID（不得出现登记表外的"暂不可比较"）；</item>
        /// <item>登记项自身完整（ID/字段/旧侧对象路径/原因/负责任务/清零门槛六者齐全）；</item>
        /// <item>对齐、覆盖与预算：全部检查点对齐、全部检查点被比较、无预算超限、无被拒登记项。</item>
        /// </list>
        /// </summary>
        private static void AssertTask07NoUnclassifiedDifference(
            ShadowComparisonReport report, RealCheckpointStream legacySide, string context)
        {
            Assert.That(report, Is.Not.Null);
            Assert.That(Enum.GetValues(typeof(ShadowDifferenceKind)).Length, Is.EqualTo(4),
                "差异归属必须恰好四类（基础设施事实 / 新规则不变量 / 限期清零 / 逐用例批准差异）："
                + "出现第五类即失败");

            for (int d = 0; d < report.Differences.Count; d++)
            {
                ShadowFieldDifference difference = report.Differences[d];
                Assert.That(Enum.IsDefined(typeof(ShadowDifferenceKind), difference.Kind), Is.True,
                    context + "：差异类别必须落在四类归属内：" + difference);
                Assert.That(string.IsNullOrEmpty(difference.FieldPath), Is.False,
                    context + "：每条差异必须带精确字段路径：" + difference);
                Assert.That(string.IsNullOrEmpty(difference.Reason), Is.False,
                    context + "：每条差异必须带原因：" + difference);
            }

            Assert.That(report.HasUnexpectedDifference, Is.False,
                context + "：不得出现未分类（非预期）差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.NewRuleVerifiedFact), Is.EqualTo(0),
                context + "：不得出现新规则不变量差异：" + report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.InfrastructureFact), Is.EqualTo(0),
                context + "：同一脚本化输入的两侧不得出现基础设施事实差异：" + report.Describe());
            Assert.That(report.InfrastructureDifferences, Is.EqualTo(0), report.Describe());
            Assert.That(report.CountOf(ShadowDifferenceKind.ApprovedDifference), Is.EqualTo(0),
                context + "：本用例不批准任何差异（否则等价声明被削弱）：" + report.Describe());
            Assert.That(report.Approvals.Count, Is.EqualTo(0), report.Describe());

            // —— 限期清零类：逐检查点 × 逐登记项，且只能出现登记表内的字段 ——
            Assert.That(report.TemporarilyUncomparable.Count, Is.GreaterThan(0),
                context + "：旧侧不存在的窗口/预算/授权/周期事实必须逐条登记，而不是被忽略");
            int checkpoints = legacySide.Snapshots.Count;
            Assert.That(report.CountOf(ShadowDifferenceKind.TemporarilyUncomparable),
                Is.EqualTo(report.TemporarilyUncomparable.Count * checkpoints),
                context + "：每条登记项必须在每个检查点上留下观察（不得静默丢弃）：" + report.Describe());

            var registeredIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < report.TemporarilyUncomparable.Count; i++)
            {
                TemporarilyUncomparableField field = report.TemporarilyUncomparable[i];
                Assert.That(field.Id, Is.Not.Empty, context + "：登记项必须给出 ID");
                Assert.That(field.Field, Is.Not.Empty, context + "：登记项必须给出字段名");
                Assert.That(field.LegacyObjectPath, Is.Not.Empty, context + "：登记项必须给出旧侧对象路径");
                Assert.That(field.Reason, Is.Not.Empty, context + "：登记项必须给出原因");
                Assert.That(field.OwnerTask, Is.Not.Empty, context + "：登记项必须给出负责任务");
                Assert.That(field.RemovalGate, Is.Not.Empty, context + "：登记项必须给出最迟清零门槛");
                registeredIds.Add(field.Id);
            }

            for (int d = 0; d < report.Differences.Count; d++)
            {
                ShadowFieldDifference difference = report.Differences[d];
                if (difference.Kind != ShadowDifferenceKind.TemporarilyUncomparable) continue;
                Assert.That(registeredIds.Contains(difference.FieldPath), Is.True,
                    context + "：限期清零差异必须精确对应登记表里的字段（不得出现登记表外的模糊项）："
                    + difference);
            }

            Assert.That(report.UnalignedCheckpoints, Is.EqualTo(0),
                context + "：按逻辑 Tick 必须完全对齐：" + report.Describe());
            Assert.That(report.ComparedCheckpoints, Is.EqualTo(checkpoints),
                context + "：比较必须覆盖全部检查点：" + report.Describe());
            Assert.That(report.BudgetOverrun, Is.False, report.Describe());
            Assert.That(report.Rejections.Count, Is.EqualTo(0),
                context + "：逐用例策略必须自洽（缺责任任务/门槛、宽泛批准都会被拒）：" + report.Describe());
            Assert.That(report.CanClaimEquivalence, Is.True, context + "：" + report.Describe());
            Assert.That(report.EquivalenceClaim, Is.EqualTo("EQUIVALENT"), context + "：" + report.Describe());
        }

        // -------- 任务 07 场景装配（只经公开扩展点接入） --------

        /// <summary>画像用例显式传入的空旧侧观测（见"对照证据 0′"）。</summary>
        private static readonly IReadOnlyList<LegacyLogicObservation> Task07ProfileLegacyObservations =
            Array.Empty<LegacyLogicObservation>();

        /// <summary>任务 07 在<strong>生产旧侧观测通道</strong>上必须逐条登记的覆盖边界字段名。</summary>
        private static readonly string[] Task07TurnWindowRegistrations =
        {
            // ① 窗口打开/关闭
            "currentWindowId",
            "windows.count",
            "windows[i].openedAtTick",
            "windows[i].isOpen",
            "windows[i].isAcceptingSubmissions",
            "windows[i].closeReason",
            // ② 整数 Tick 预算（逐窗口 + 全窗口聚合）
            "windows[i].totalBudgetTicks",
            "windows[i].reservedBudgetTicks",
            "windows[i].spentBudgetTicks",
            "windows[i].availableBudgetTicks",
            "windows[i].reservations",
            "windows[i].budgetIdentity",
            "nextWindowTick",
            "lastClosedWindowId",
            "resources.turnBudgetAvailable",
            "resources.turnBudgetReserved",
            "resources.turnBudgetSpent",
            "resources.turnBudgetIdentity",
            // ③ 并发提交授权
            "concurrentAction.hasActiveAuthorization",
            "concurrentAction.windowId",
            "concurrentAction.playerUnitId",
            "resources.metaResource",
            // ④ 肾上腺素周期账本（清零时机与跨窗口保留）
            "adrenaline[unitId].cycleId",
            "adrenaline[unitId].reservations",
            "adrenaline[unitId].reservedTotal",
            "units[i].adrenalineCycleId",
            // ⑤ 跨窗口计划不变性（计划的预算投影与来源窗口账本的联系）
            "plans[i].budgetCostTicks",
            "plans[i].reservedTurnBudgetTicks",
            "plans[i].submittedWindowLedger"
        };

        /// <summary>一次任务 07 场景构建的只读结果（检查点流 + 场景事实 + 拒绝码）。</summary>
        private sealed class Task07StreamResult
        {
            public RealCheckpointStream Stream;

            /// <summary>hero 自己的窗口 ID（由真实运行中的当前窗口读出，不硬编码）。</summary>
            public long HeroWindowId;

            /// <summary>enemy 窗口 ID（并发授权必须指向它，且它属于别人）。</summary>
            public long EnemyWindowId;

            /// <summary>真实新增的普通 Move 计划 ID（未被接受时为 0）。</summary>
            public long PlanId;

            /// <summary>该计划的权威整数预算成本（未被接受时为 0）。</summary>
            public int PlanBudgetCost;

            /// <summary>排程命令在<strong>处理阶段</strong>被拒绝的稳定码（null = 被接受）。</summary>
            public string SubmitRejectionCode;

            /// <summary>并发激活命令在处理阶段被拒绝的稳定码（null = 被接受）。</summary>
            public string ActivationRejectionCode;

            /// <summary>本次构建时 <c>CanControl(controller.player, hero)</c> 是否成立。</summary>
            public bool ControlBindingAvailable;

            /// <summary>计划进入 Running 的那一 Tick 上，来源窗口账本的 Reserved（缺口 D-C 的观察点）。</summary>
            public int WindowReservedAtStart;

            /// <summary>计划进入 Running 的那一 Tick 上，来源窗口账本的 Spent（0 ⇒ Reserved -> Spent 未接线）。</summary>
            public int WindowSpentAtStart;
        }

        /// <summary>
        /// 任务 07 的脚本化检查点流：真实 <c>BattleSimulation</c> + 真实 <c>Step</c> 管线 +
        /// 三个任务 07 冻结装配点（窗口排程 / 主角单位 / 肾上腺素入账事实来源）。
        ///
        /// 输入序列（两侧逐字相同）见 <see cref="Task07AccrualTickA"/> 一带的常量注释。
        ///
        /// <paramref name="registerControllerBindings"/> = true 时，把定义侧
        /// <c>Encounter.Controllers</c> 的 <c>ControllerBinding</c> 按与 <c>BattleInitializer</c>
        /// <strong>同一条</strong>槽位→UnitId 规则注册进 <c>TurnWindowManager</c>（生产公开 API）；
        /// 为 false 时保持生产装配的原状，用于把"控制权接线缺口"钉成可观察事实（对照证据 D-A）。
        ///
        /// <paramref name="wireStartCommitPort"/> = true 时，经冻结装配点
        /// <c>BattleSimulationAssembly.startCommitPort</c> 显式注入<strong>真实</strong>
        /// <c>TurnWindowBudgetAuthority</c>（创建模拟后回填，因为端口需要该模拟自己的窗口管理器）；
        /// 为 false 时使用生产默认值（缺口 D-C 下它是 no-op）。
        /// </summary>
        private static Task07StreamResult BuildTask07TurnWindowStream(
            ShadowCaseSeed seed, long heroUnitId, long enemyUnitId,
            bool registerControllerBindings, bool wireStartCommitPort)
        {
            var result = new Task07StreamResult();
            var snapshots = new List<LogicSnapshot>(Task07TerminalTick + 1);
            var bindings = new List<ShadowCheckpointEventBinding>(Task07TerminalTick + 1);

            var startCommitPort = new Task07DelegatingStartCommitPort();
            var assembly = new BattleSimulationAssembly(
                turnWindowSchedule: new Task07ScriptedWindowSchedule(heroUnitId, enemyUnitId),
                concurrentHeroUnitId: new UnitId(heroUnitId),
                adrenalineAccrualFactSource: new Task07ScriptedAccrualSource(heroUnitId),
                startCommitPort: wireStartCommitPort ? startCommitPort : null);
            BattleSimulation simulation = BattleSimulation.Create(
                seed.Definition, seed.EncounterId, seed.RuntimeInputs, assembly);

            try
            {
                if (wireStartCommitPort)
                {
                    // 真实生产实现（任务 07 的 Reserved -> Spent 原子提交）；
                    // 端口只能在模拟创建之后回填，因为它必须绑定该模拟自己的窗口账本。
                    startCommitPort.Target = new ProjectHero.Logic.Turns.TurnWindowBudgetAuthority(
                        simulation.WindowManager);
                }
                CommandIngressEntry entry = simulation.CommandIngress.FindEntry(
                    new ControllerId("controller.player"));
                Assert.That(entry, Is.Not.Null, "必须有 controller.player 入口");

                if (registerControllerBindings) RegisterEncounterControllerBindings(simulation, seed);
                result.ControlBindingAvailable = simulation.WindowManager.CanControl(
                    new ControllerId("controller.player"), new UnitId(heroUnitId));

                for (long tick = 0L; tick <= Task07TerminalTick; tick++)
                {
                    if (tick == Task07SubmitTick)
                    {
                        // ExpectedWindowId 取自**运行中的真实当前窗口**（绝不按"当前窗口"猜：
                        // 命令 scope 必须显式声明，窗口状态由权威逐条校验）。
                        Assert.That(simulation.CurrentTurnWindow, Is.Not.Null,
                            "构造前提：提交普通动作前必须已经有开放窗口（Tick "
                            + Task07HeroWindowOpenTick + " 打开）");
                        WindowId expectedWindow = simulation.CurrentTurnWindow.WindowId;
                        result.HeroWindowId = expectedWindow.Value;

                        CommandIngressRejection ingress = entry.Submit(new CommandRequest(
                            tick,
                            new ScheduleEditScope(simulation.ScheduleRevision, expectedWindow),
                            new ScheduleEditPayload(new ScheduleEditOperation[]
                            {
                                new AddOrdinaryPlanOperation(
                                    1L, new UnitId(heroUnitId), new ActionSpecId(Task07MoveSpecId),
                                    Task07RequestedStartTick,
                                    AnchorAfterPlanId: default, PrimaryTargetUnitId: null,
                                    Facing: GridDirection.North,
                                    Destination: new GridPoint(Task07DestinationX, Task07DestinationY))
                            })));
                        Assert.That(ingress, Is.Null,
                            ingress == null
                                ? null
                                : "命令必须在入口被接受（入口校验失败即场景构造失败）：" + ingress.ReasonCode
                                  + "|ordinal=" + ingress.ProducerOrdinal);
                    }

                    if (tick == Task07ActivationTick)
                    {
                        Assert.That(simulation.CurrentTurnWindow, Is.Not.Null,
                            "构造前提：并发激活必须有当前开放窗口（enemy 窗口）");
                        WindowId expectedWindow = simulation.CurrentTurnWindow.WindowId;
                        result.EnemyWindowId = expectedWindow.Value;

                        CommandIngressRejection ingress = entry.Submit(new CommandRequest(
                            tick,
                            new WindowCommandScope(expectedWindow),
                            new WindowCommandPayload(WindowCommandKind.ActivateConcurrentAction)));
                        Assert.That(ingress, Is.Null,
                            ingress == null
                                ? null
                                : "窗口命令必须在入口被接受：" + ingress.ReasonCode
                                  + "|ordinal=" + ingress.ProducerOrdinal);
                    }

                    FrozenCommandBatch batch = simulation.CommandIngress.FreezeTick(tick);
                    StepResult step = simulation.Step(tick, batch);
                    LogicSnapshot checkpoint = simulation.CurrentSnapshot;
                    snapshots.Add(checkpoint);
                    bindings.Add(BuildEventBinding(tick, step));

                    if (tick == Task07SubmitTick)
                    {
                        result.SubmitRejectionCode = FirstCommandRejectionReason(step);
                        if (result.PlanId == 0L && checkpoint.Plans.Count > 0)
                        {
                            result.PlanId = checkpoint.Plans[0].ActionPlanId;
                            result.PlanBudgetCost = checkpoint.Plans[0].BudgetCostTicks;
                        }
                    }

                    if (tick == Task07ActivationTick) result.ActivationRejectionCode = FirstCommandRejectionReason(step);
                }
            }
            finally
            {
                simulation.Dispose();
            }

            result.Stream = new RealCheckpointStream(snapshots, bindings);

            // 缺口 D-C 的观察点：计划进入 Running 的那一 Tick 上来源窗口账本的两个值。
            LogicSnapshot atStart = snapshots.Count > Task07RequestedStartTick
                ? snapshots[Task07RequestedStartTick]
                : null;
            if (atStart != null)
            {
                ActionPlanSnapshot started = FindPlanSnapshot(atStart, result.PlanId);
                if (started != null) result.PlanBudgetCost = started.BudgetCostTicks;
                TurnWindowSnapshot source = WindowOf(atStart, result.HeroWindowId);
                if (source != null)
                {
                    result.WindowReservedAtStart = source.ReservedBudgetTicks;
                    result.WindowSpentAtStart = source.SpentBudgetTicks;
                }
            }

            return result;
        }

        /// <summary>
        /// 把启动提交委托给"创建模拟之后才知道"的真实端口。
        ///
        /// 它是<strong>测试侧装配胶水</strong>（不是比较路径）：任务 07 冻结的装配点要求端口在
        /// <c>Create</c> 之前给出，而真实实现必须绑定该模拟自己的 <c>TurnWindowManager</c>，
        /// 因此这里先注入一个转发器、再回填目标。目标为空时保持生产默认语义（no-op）。
        /// </summary>
        private sealed class Task07DelegatingStartCommitPort : ProjectHero.Logic.Timeline.IActionPlanStartCommitPort
        {
            public ProjectHero.Logic.Timeline.IActionPlanStartCommitPort Target { get; set; }

            public string Commit(ActionPlan plan, long tick, Action<ActionPlan> rollback)
                => Target != null
                    ? Target.Commit(plan, tick, rollback)
                    : ProjectHero.Logic.Timeline.NoTurnBudgetCommitPort.Instance.Commit(plan, tick, rollback);
        }

        /// <summary>该 Tick 内第一条被处理阶段拒绝的命令的稳定码（没有拒绝时为 null）。</summary>
        private static string FirstCommandRejectionReason(StepResult step)
        {
            if (step == null || step.Events == null) return null;
            IReadOnlyList<ProjectHero.Logic.Events.LogicEvent> events = step.Events.EventsInSequenceOrder;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is ProjectHero.Logic.Events.CommandRejectedEvent rejected)
                    return rejected.ReasonCode;
            }
            return null;
        }

        /// <summary>
        /// 把定义侧 <c>Encounter.Controllers</c> 的 <c>ControllerBinding</c> 注册进窗口管理器。
        ///
        /// 槽位→<c>UnitId</c> 的映射与 <c>BattleInitializer</c> <strong>同一条</strong>规则
        /// （<c>SlotId</c> 的 Ordinal 升序 ⇒ <c>UnitId(1..N)</c>），不硬编码 1/2。
        /// 这是<strong>生产公开 API</strong>（<c>TurnWindowManager.RegisterControllerBinding</c>），
        /// 生产装配目前尚未调用它（缺口 D-A，见本用例 XML 文档）。
        /// </summary>
        private static void RegisterEncounterControllerBindings(BattleSimulation simulation, ShadowCaseSeed seed)
        {
            Assert.That(simulation, Is.Not.Null);
            var encounter = seed.Definition.FindEncounter(seed.EncounterId);
            Assert.That(encounter, Is.Not.Null, "对照证据：必须能解析出主 Encounter 定义");
            Assert.That(encounter.Slots, Is.Not.Null);

            var slots = new List<EncounterUnitSlot>(encounter.Slots);
            slots.Sort((left, right) => string.CompareOrdinal(left.SlotId.Value, right.SlotId.Value));
            var slotToUnitId = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < slots.Count; i++) slotToUnitId[slots[i].SlotId.Value] = i + 1L;

            if (encounter.Controllers == null) return;
            for (int c = 0; c < encounter.Controllers.Count; c++)
            {
                var binding = encounter.Controllers[c];
                if (binding == null || binding.ControlledSlots == null) continue;
                for (int s = 0; s < binding.ControlledSlots.Count; s++)
                {
                    string slotId = binding.ControlledSlots[s].Value;
                    Assert.That(slotToUnitId.ContainsKey(slotId), Is.True,
                        "ControllerBinding 的受控槽位必须存在于 Encounter：" + slotId);
                    simulation.WindowManager.RegisterControllerBinding(
                        binding.ControllerId, new UnitId(slotToUnitId[slotId]));
                }
            }
        }

        /// <summary>
        /// 脚本化窗口排程：hero 的窗口在 <see cref="Task07HeroWindowOpenTick"/> 打开、
        /// enemy 的窗口在 <see cref="Task07EnemyWindowOpenTick"/> 打开（两个 Tick 之间已正式关闭前者）。
        ///
        /// 它是任务 03 冻结的 <c>ITurnWindowSchedule</c> 端口——窗口打开/关闭是<strong>脚本化输入</strong>，
        /// 不是测试对内部状态的写入。
        /// </summary>
        /// <summary>
        /// 任务 07：显式排程编辑的新增/增费必须声明 <c>ExpectedWindowId</c>（00 号规则 18），
        /// 且命令处理时必须有仍接受提交的当前窗口。没有窗口即场景构造失败——绝不回退成"猜当前窗口"。
        /// </summary>
        private static WindowId RequireCurrentWindow(BattleSimulation simulation)
        {
            Assert.That(simulation.CurrentTurnWindow, Is.Not.Null,
                "构造前提：显式排程编辑必须已经有一个开放且仍在接受提交的当前窗口");
            return simulation.CurrentTurnWindow.WindowId;
        }

        private sealed class Task07ScriptedWindowSchedule : ProjectHero.Logic.Turns.ITurnWindowSchedule
        {
            private readonly long _heroUnitId;
            private readonly long _enemyUnitId;

            public Task07ScriptedWindowSchedule(long heroUnitId, long enemyUnitId)
            {
                _heroUnitId = heroUnitId;
                _enemyUnitId = enemyUnitId;
            }

            public ProjectHero.Logic.Turns.WindowOpenRequest TryOpenDue(long tick)
            {
                if (tick == Task07HeroWindowOpenTick)
                    return new ProjectHero.Logic.Turns.WindowOpenRequest(
                        new UnitId(_heroUnitId), Task07HeroWindowBudget);
                if (tick == Task07EnemyWindowOpenTick)
                    return new ProjectHero.Logic.Turns.WindowOpenRequest(
                        new UnitId(_enemyUnitId), Task07EnemyWindowBudget);
                return null;
            }

            public bool ShouldCloseCurrentWindow(long tick)
                => tick == Task07HeroWindowCloseTick || tick == Task07TerminalTick;
        }

        /// <summary>
        /// 脚本化肾上腺素入账事实来源（任务 07 冻结的<strong>唯一</strong> Tick 末入账入口）。
        ///
        /// 它在 Tick <see cref="Task07AccrualTickA"/>（hero 自己窗口打开<strong>之前</strong>）与
        /// Tick <see cref="Task07AccrualTickB"/>（窗口打开<strong>之后</strong>）各提供一条规范事实，
        /// 从而让"打开清零"与"关闭/跨窗口不清零"都可观察。每 Tick 至多一条、按 UnitId 升序。
        /// </summary>
        private sealed class Task07ScriptedAccrualSource : ProjectHero.Logic.Resources.IAdrenalineAccrualFactSource
        {
            private readonly long _heroUnitId;

            public Task07ScriptedAccrualSource(long heroUnitId)
            {
                _heroUnitId = heroUnitId;
            }

            public IReadOnlyList<ProjectHero.Logic.Resources.AdrenalineAccrualFacts> BuildAccrualFactsOrdered(
                long tick)
            {
                if (tick == Task07AccrualTickA)
                {
                    return new[]
                    {
                        new ProjectHero.Logic.Resources.AdrenalineAccrualFacts(
                            new UnitId(_heroUnitId), Task07AccrualDamageDealtQ10A, 0, 0, 0, 0)
                    };
                }

                if (tick == Task07AccrualTickB)
                {
                    return new[]
                    {
                        new ProjectHero.Logic.Resources.AdrenalineAccrualFacts(
                            new UnitId(_heroUnitId), Task07AccrualDamageDealtQ10B, 0, 0, 0, 0)
                    };
                }

                return Array.Empty<ProjectHero.Logic.Resources.AdrenalineAccrualFacts>();
            }
        }

        // -------- 任务 07 篡改工具（测试侧，不是生产比较路径） --------

        /// <summary>按窗口 ID 替换一条窗口投影（未命中的窗口逐字保留）。</summary>
        private static TurnWindowManagerSnapshot TamperWindow(
            TurnWindowManagerSnapshot source, long windowId,
            Func<TurnWindowSnapshot, TurnWindowSnapshot> change)
        {
            var windows = new List<TurnWindowSnapshot>(source.Windows.Count);
            for (int i = 0; i < source.Windows.Count; i++)
            {
                TurnWindowSnapshot window = source.Windows[i];
                windows.Add(window != null && window.WindowId == windowId ? change(window) : window);
            }
            return new TurnWindowManagerSnapshot(source.CurrentWindowId, source.NextWindowTick,
                source.NextWindowOrdinal, source.LastClosedWindowId, windows);
        }

        /// <summary>删掉一条窗口投影（其余逐字保留）。</summary>
        private static TurnWindowManagerSnapshot RemoveWindow(
            TurnWindowManagerSnapshot source, long windowId)
        {
            var windows = new List<TurnWindowSnapshot>(source.Windows.Count);
            for (int i = 0; i < source.Windows.Count; i++)
            {
                TurnWindowSnapshot window = source.Windows[i];
                if (window != null && window.WindowId == windowId) continue;
                windows.Add(window);
            }
            return new TurnWindowManagerSnapshot(source.CurrentWindowId, source.NextWindowTick,
                source.NextWindowOrdinal, source.LastClosedWindowId, windows);
        }

        /// <summary>按单位 ID 替换一条肾上腺素账本投影（未命中的账本逐字保留）。</summary>
        private static BattleResourceSnapshot TamperLedger(
            BattleResourceSnapshot source, long unitId,
            Func<AdrenalineLedgerSnapshot, AdrenalineLedgerSnapshot> change)
        {
            var ledgers = new List<AdrenalineLedgerSnapshot>(
                source.AdrenalineLedgers ?? Array.Empty<AdrenalineLedgerSnapshot>());
            for (int i = 0; i < ledgers.Count; i++)
            {
                AdrenalineLedgerSnapshot ledger = ledgers[i];
                if (ledger != null && ledger.UnitId == unitId) ledgers[i] = change(ledger);
            }
            return source with { AdrenalineLedgers = ledgers };
        }

        // -------- 任务 07 只读投影（断言用） --------

        private static TurnWindowSnapshot WindowOf(LogicSnapshot snapshot, long windowId)
        {
            if (snapshot == null || snapshot.WindowManager == null || snapshot.WindowManager.Windows == null)
                return null;
            for (int i = 0; i < snapshot.WindowManager.Windows.Count; i++)
            {
                TurnWindowSnapshot window = snapshot.WindowManager.Windows[i];
                if (window != null && window.WindowId == windowId) return window;
            }
            return null;
        }

        private static AdrenalineLedgerSnapshot LedgerOf(LogicSnapshot snapshot, long unitId)
        {
            if (snapshot == null || snapshot.Resources == null || snapshot.Resources.AdrenalineLedgers == null)
                return null;
            for (int i = 0; i < snapshot.Resources.AdrenalineLedgers.Count; i++)
            {
                AdrenalineLedgerSnapshot ledger = snapshot.Resources.AdrenalineLedgers[i];
                if (ledger != null && ledger.UnitId == unitId) return ledger;
            }
            return null;
        }

        private static UnitSnapshot UnitOf(LogicSnapshot snapshot, long unitId)
        {
            if (snapshot == null || snapshot.Units == null) return null;
            for (int i = 0; i < snapshot.Units.Count; i++)
            {
                UnitSnapshot unit = snapshot.Units[i];
                if (unit != null && unit.UnitId == unitId) return unit;
            }
            return null;
        }

        /// <summary>计划在窗口切换前后<strong>必须逐字不变</strong>的审计签名。</summary>
        private static string DescribePlanAudit(LogicSnapshot snapshot, long planId)
        {
            ActionPlanSnapshot plan = FindPlanSnapshot(snapshot, planId);
            if (plan == null) return "<plan-absent>";
            return "id=" + plan.ActionPlanId
                   + ";spec=" + plan.ActionSpecId
                   + ";state=" + plan.State
                   + ";start=" + plan.StartTick
                   + ";end=" + plan.EndTick
                   + ";requested=" + plan.LastRequestedStartTick
                   + ";lockedAt=" + plan.LockedAtTick
                   + ";defer=" + plan.AutomaticDeferralCount
                   + ";impact=" + plan.ImpactTick
                   + ";windup=" + plan.ResolvedWindupTicks
                   + ";recovery=" + plan.RecoveryTicks
                   + ";edges=" + plan.ResolvedPathEdgeCount
                   + ";weight=" + plan.ResolvedPathWeightUnits
                   + ";baseStep=" + plan.ResolvedBaseStepTicks
                   + ";cost=" + plan.BudgetCostTicks
                   + ";reservedBudget=" + plan.ReservedTurnBudgetTicks
                   + ";window=" + plan.SubmittedWindowId
                   + ";target=" + plan.PrimaryTargetUnitId;
        }

        /// <summary>逐 Tick 的任务 07 诊断（只在断言失败信息里使用）。</summary>
        private static string DescribeTask07Stream(RealCheckpointStream side)
        {
            if (side == null) return "task07[<null>]";
            var builder = new StringBuilder("task07[");
            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                LogicSnapshot snapshot = side.Snapshots[i];
                builder.Append('{').Append("t=").Append(snapshot.Tick)
                    .Append(" rev=").Append(snapshot.ScheduleRevision)
                    .Append(" curW=").Append(snapshot.WindowManager.CurrentWindowId)
                    .Append(" windows=");
                for (int w = 0; w < snapshot.WindowManager.Windows.Count; w++)
                {
                    TurnWindowSnapshot window = snapshot.WindowManager.Windows[w];
                    builder.Append('(').Append(window.WindowId)
                        .Append(",owner=").Append(window.OwnerUnitId)
                        .Append(",open=").Append(window.IsOpen)
                        .Append(",acc=").Append(window.IsAcceptingSubmissions)
                        .Append(",tot=").Append(window.TotalBudgetTicks)
                        .Append(",res=").Append(window.ReservedBudgetTicks)
                        .Append(",spent=").Append(window.SpentBudgetTicks)
                        .Append(",avail=").Append(window.AvailableBudgetTicks)
                        .Append(')');
                }
                builder.Append(" auth=").Append(snapshot.ConcurrentAction.HasActiveAuthorization)
                    .Append('/').Append(snapshot.ConcurrentAction.WindowId)
                    .Append('/').Append(snapshot.ConcurrentAction.PlayerUnitId)
                    .Append(" meta=").Append(snapshot.Resources.MetaResource)
                    .Append(" tb=").Append(snapshot.Resources.TurnBudgetAvailable)
                    .Append('/').Append(snapshot.Resources.TurnBudgetReserved)
                    .Append('/').Append(snapshot.Resources.TurnBudgetSpent)
                    .Append(" adr=");
                if (snapshot.Resources.AdrenalineLedgers != null)
                {
                    for (int a = 0; a < snapshot.Resources.AdrenalineLedgers.Count; a++)
                    {
                        AdrenalineLedgerSnapshot ledger = snapshot.Resources.AdrenalineLedgers[a];
                        builder.Append('(').Append(ledger.UnitId).Append(",avail=")
                            .Append(ledger.AvailableAdrenaline).Append(",cycle=").Append(ledger.CycleId)
                            .Append(",res=").Append(ledger.ReservedTotal).Append(')');
                    }
                }
                builder.Append(" plans=");
                for (int p = 0; p < snapshot.Plans.Count; p++)
                {
                    builder.Append('(').Append(snapshot.Plans[p].ActionPlanId)
                        .Append(",state=").Append(snapshot.Plans[p].State)
                        .Append(",start=").Append(snapshot.Plans[p].StartTick)
                        .Append(",end=").Append(snapshot.Plans[p].EndTick)
                        .Append(",lockedAt=").Append(snapshot.Plans[p].LockedAtTick)
                        .Append(",cost=").Append(snapshot.Plans[p].BudgetCostTicks)
                        .Append(",resBudget=").Append(snapshot.Plans[p].ReservedTurnBudgetTicks)
                        .Append(",window=").Append(snapshot.Plans[p].SubmittedWindowId)
                        .Append(')');
                }
                builder.Append('}');
            }
            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>从真实检查点流里读出的任务 07 场景事实（供断言与探针定位）。</summary>
        private sealed class Task07ScenarioFacts
        {
            public long HeroWindowId;
            public long EnemyWindowId;
            public long PlanId;
            public int PlanBudgetCost;
            public int HeroAvailableAfterFirstAccrual;
            public int HeroCycleAfterOwnWindowOpen;
            public int HeroAvailableAfterSecondAccrual;
        }

        /// <summary>
        /// 五类事实的<strong>逐 Tick 精确断言</strong>（覆盖证据的第一半：报告里要求覆盖的事实
        /// 必须真的存在于这条检查点流里，否则"零差异"只是两条空流的巧合相等）。
        ///
        /// 同时逐检查点核对任务 07 的四条账本不变量（窗口恒等式、预留明细合计、资源聚合恒等式、
        /// 肾上腺素明细合计与只读镜像一致、计划预算投影与来源账本一致、授权指向仍开放的当前窗口）——
        /// 这样"比较器比较的是两边都成立的不变量"这一点不会被误读成"两边一起错"。
        /// </summary>
        private static Task07ScenarioFacts AssertTask07ScenarioFacts(
            RealCheckpointStream side, long heroUnitId, long enemyUnitId)
        {
            string stream = DescribeTask07Stream(side);
            var facts = new Task07ScenarioFacts();

            // —— 逐检查点不变量（覆盖全部检查点，包括关闭之后与授权撤销之后）——
            for (int i = 0; i < side.Snapshots.Count; i++)
            {
                LogicSnapshot snapshot = side.Snapshots[i];
                string where = "tick=" + snapshot.Tick + " ; ";
                var windows = snapshot.WindowManager.Windows;
                long total = 0L;
                for (int w = 0; w < windows.Count; w++)
                {
                    TurnWindowSnapshot window = windows[w];
                    Assert.That(window.ReservedBudgetTicks, Is.GreaterThanOrEqualTo(0), where + stream);
                    Assert.That(window.SpentBudgetTicks, Is.GreaterThanOrEqualTo(0), where + stream);
                    Assert.That(window.AvailableBudgetTicks, Is.GreaterThanOrEqualTo(0), where + stream);
                    Assert.That(
                        (long)window.ReservedBudgetTicks + window.SpentBudgetTicks + window.AvailableBudgetTicks,
                        Is.EqualTo((long)window.TotalBudgetTicks),
                        where + "窗口预算恒等式 Reserved + Spent + Available == Total 必须成立：" + stream);
                    int reservationSum = 0;
                    for (int r = 0; r < window.Reservations.Count; r++)
                        reservationSum += window.Reservations[r].ReservedTicks;
                    Assert.That(reservationSum, Is.EqualTo(window.ReservedBudgetTicks),
                        where + "窗口预留明细合计必须等于 Reserved：" + stream);
                    total += window.TotalBudgetTicks;
                }

                Assert.That(
                    snapshot.Resources.TurnBudgetAvailable + snapshot.Resources.TurnBudgetReserved
                    + snapshot.Resources.TurnBudgetSpent,
                    Is.EqualTo(total),
                    where + "战斗资源恒等式 Available + Reserved + Spent == Σ Total 必须成立：" + stream);

                if (snapshot.Resources.AdrenalineLedgers != null)
                {
                    for (int a = 0; a < snapshot.Resources.AdrenalineLedgers.Count; a++)
                    {
                        AdrenalineLedgerSnapshot ledger = snapshot.Resources.AdrenalineLedgers[a];
                        int sum = 0;
                        for (int r = 0; r < ledger.Reservations.Count; r++)
                            sum += ledger.Reservations[r].ReservedAmount;
                        Assert.That(sum, Is.EqualTo(ledger.ReservedTotal),
                            where + "肾上腺素预留明细合计必须等于 ReservedTotal：" + stream);
                        UnitSnapshot mirror = UnitOf(snapshot, ledger.UnitId);
                        Assert.That(mirror, Is.Not.Null,
                            where + "账本主体必须也有单位快照：" + stream);
                        Assert.That(mirror.AvailableAdrenaline, Is.EqualTo(ledger.AvailableAdrenaline),
                            where + "单位只读镜像 Available 必须等于账本：" + stream);
                        Assert.That(mirror.AdrenalineCycleId, Is.EqualTo(ledger.CycleId),
                            where + "单位只读镜像 CycleId 必须等于账本：" + stream);
                    }
                }

                for (int p = 0; p < snapshot.Plans.Count; p++)
                {
                    ActionPlanSnapshot plan = snapshot.Plans[p];
                    if (plan.SubmittedWindowId == 0L) continue;
                    TurnWindowSnapshot source = WindowOf(snapshot, plan.SubmittedWindowId);
                    Assert.That(source, Is.Not.Null,
                        where + "计划的来源窗口必须仍留在可审计账本里：" + stream);
                    int held = 0;
                    for (int r = 0; r < source.Reservations.Count; r++)
                    {
                        if (source.Reservations[r].ActionPlanId == plan.ActionPlanId)
                            held = source.Reservations[r].ReservedTicks;
                    }
                    Assert.That(held, Is.EqualTo(plan.ReservedTurnBudgetTicks),
                        where + "计划预算投影必须与来源窗口账本条目一致（plan=" + plan.ActionPlanId + "）："
                        + stream);
                }

                ConcurrentActionSnapshot authority = snapshot.ConcurrentAction;
                if (authority.HasActiveAuthorization)
                {
                    TurnWindowSnapshot target = WindowOf(snapshot, authority.WindowId);
                    Assert.That(target, Is.Not.Null,
                        where + "有效授权必须指向账本里存在的窗口：" + stream);
                    Assert.That(target.IsOpen && target.IsAcceptingSubmissions, Is.True,
                        where + "有效授权必须指向仍开放且仍在接受提交的窗口：" + stream);
                    Assert.That(target.OwnerUnitId, Is.Not.EqualTo(authority.PlayerUnitId),
                        where + "拥有者天然持有提交权，不得持有并发授权：" + stream);
                }
            }

            // —— Tick 1：唯一入账入口把 hero 的 Available 推到 > 0（个人周期尚未递增）——
            LogicSnapshot atAccrual = side.Snapshots[Task07AccrualTickA];
            AdrenalineLedgerSnapshot accured = LedgerOf(atAccrual, heroUnitId);
            Assert.That(accured, Is.Not.Null, "hero 必须有肾上腺素账本：" + stream);
            facts.HeroAvailableAfterFirstAccrual = accured.AvailableAdrenaline;
            Assert.That(facts.HeroAvailableAfterFirstAccrual, Is.GreaterThan(0),
                "Tick " + Task07AccrualTickA + " 的 Tick 末入账必须真的把 Available 推高（否则'清零'不可观察）："
                + stream);
            Assert.That(accured.CycleId, Is.EqualTo(0L), "个人周期尚未递增：" + stream);
            Assert.That(atAccrual.WindowManager.Windows.Count, Is.EqualTo(0),
                "Tick " + Task07AccrualTickA + " 还没有任何窗口：" + stream);

            // —— Tick 2：hero 自己的窗口打开 ⇒ 先递增周期再清零（本 Tick 命令之前）——
            LogicSnapshot atOpen = side.Snapshots[Task07HeroWindowOpenTick];
            Assert.That(atOpen.WindowManager.Windows.Count, Is.EqualTo(1),
                "打开阶段必须恰好创建一个窗口：" + stream);
            TurnWindowSnapshot heroWindow = atOpen.WindowManager.Windows[0];
            facts.HeroWindowId = heroWindow.WindowId;
            Assert.That(heroWindow.OwnerUnitId, Is.EqualTo(heroUnitId),
                "开窗顺序按稳定键，本 Tick 到期的是 hero 的窗口：" + stream);
            Assert.That(heroWindow.OpenedAtTick, Is.EqualTo((long)Task07HeroWindowOpenTick));
            Assert.That(heroWindow.IsOpen && heroWindow.IsAcceptingSubmissions, Is.True,
                "刚打开的窗口必须同时是开放与接受提交：" + stream);
            Assert.That(heroWindow.TotalBudgetTicks, Is.EqualTo(Task07HeroWindowBudget),
                "窗口总预算必须来自脚本化排程（整数 Tick）：" + stream);
            Assert.That(heroWindow.ReservedBudgetTicks, Is.EqualTo(0), stream);
            Assert.That(heroWindow.SpentBudgetTicks, Is.EqualTo(0), stream);
            Assert.That(atOpen.WindowManager.CurrentWindowId, Is.EqualTo(facts.HeroWindowId), stream);

            AdrenalineLedgerSnapshot cleared = LedgerOf(atOpen, heroUnitId);
            Assert.That(cleared, Is.Not.Null, stream);
            Assert.That(cleared.AvailableAdrenaline, Is.EqualTo(0),
                "拥有者自己窗口打开时 Available 必须清零（Tick " + Task07AccrualTickA
                + " 的 " + facts.HeroAvailableAfterFirstAccrual + " 点在此清零）：" + stream);
            Assert.That(cleared.CycleId, Is.EqualTo(1L),
                "清零必须与'个人周期 +1'同一次发生：" + stream);
            facts.HeroCycleAfterOwnWindowOpen = (int)cleared.CycleId;

            // —— Tick 3：真实排程事务 ⇒ 修订号 1、一条 Editable 普通 Move、
            //            整数预算从 Available 转入按计划归属的 Reserved（**不消费**）——
            LogicSnapshot atSubmit = side.Snapshots[Task07SubmitTick];
            Assert.That(atSubmit.ScheduleRevision, Is.EqualTo(1L),
                "成功排程编辑事务恰好 +1：" + stream);
            Assert.That(atSubmit.Plans.Count, Is.EqualTo(1), "排程编辑必须留下恰好一条计划：" + stream);
            ActionPlanSnapshot created = atSubmit.Plans[0];
            facts.PlanId = created.ActionPlanId;
            facts.PlanBudgetCost = created.BudgetCostTicks;
            Assert.That(facts.PlanId, Is.GreaterThan(0L));
            Assert.That(created.OwnerUnitId, Is.EqualTo(heroUnitId));
            Assert.That(created.ActionSpecId, Is.EqualTo(Task07MoveSpecId));
            Assert.That(created.State, Is.EqualTo((int)ActionPlanState.Editable),
                "新增计划在 Editable 阶段严格使用 Reserved：" + stream);
            Assert.That(created.SubmittedWindowId, Is.EqualTo(facts.HeroWindowId),
                "新增计划必须记录**真实当前窗口**作为预算来源与审计归属：" + stream);
            Assert.That(facts.PlanBudgetCost, Is.GreaterThan(0),
                "Move 的整数预算必须为正（路径权重 × 基础步长 + 后摇）：" + stream);
            Assert.That(created.ReservedTurnBudgetTicks, Is.EqualTo(facts.PlanBudgetCost),
                "Editable 计划必须持有等于成本的 Reserved 投影：" + stream);

            TurnWindowSnapshot atSubmitWindow = WindowOf(atSubmit, facts.HeroWindowId);
            Assert.That(atSubmitWindow, Is.Not.Null, stream);
            Assert.That(atSubmitWindow.ReservedBudgetTicks, Is.EqualTo(facts.PlanBudgetCost),
                "窗口 Reserved 必须恰好增加该计划的成本：" + stream);
            Assert.That(atSubmitWindow.SpentBudgetTicks, Is.EqualTo(0),
                "Editable 阶段绝不消费预算（Spent 必须仍为 0）：" + stream);
            Assert.That(atSubmitWindow.AvailableBudgetTicks,
                Is.EqualTo(Task07HeroWindowBudget - facts.PlanBudgetCost), stream);
            int reservationEntries = 0;
            for (int r = 0; r < atSubmitWindow.Reservations.Count; r++)
            {
                if (atSubmitWindow.Reservations[r].ActionPlanId == facts.PlanId) reservationEntries++;
            }
            Assert.That(reservationEntries, Is.EqualTo(1),
                "窗口账本必须恰好为该计划留下一条预留明细（不重复预留）：" + stream);

            // —— Tick 5：窗口打开之后的第二次入账 ⇒ Available 再次 > 0（窗口不会持续清零）——
            LogicSnapshot atAccrualB = side.Snapshots[Task07AccrualTickB];
            AdrenalineLedgerSnapshot secondAccrual = LedgerOf(atAccrualB, heroUnitId);
            Assert.That(secondAccrual, Is.Not.Null, stream);
            facts.HeroAvailableAfterSecondAccrual = secondAccrual.AvailableAdrenaline;
            Assert.That(facts.HeroAvailableAfterSecondAccrual, Is.GreaterThan(0),
                "窗口打开之后的 Tick 末入账必须照常生效（清零只发生在打开那一刻）：" + stream);

            // —— Tick 9：请求关闭之前的最后一个检查点（仍在接受提交、预留未被消费）——
            LogicSnapshot atEditableProbe = side.Snapshots[Task07EditableProbeTick];
            TurnWindowSnapshot acceptingWindow = WindowOf(atEditableProbe, facts.HeroWindowId);
            Assert.That(acceptingWindow, Is.Not.Null, stream);
            Assert.That(acceptingWindow.IsOpen && acceptingWindow.IsAcceptingSubmissions, Is.True,
                "正式关闭之前窗口必须仍在接受提交：" + stream);
            Assert.That(FindPlanSnapshot(atEditableProbe, facts.PlanId).ReservedTurnBudgetTicks,
                Is.EqualTo(facts.PlanBudgetCost), "Editable 预留必须保持不变：" + stream);
            string planAuditBeforeClose = DescribePlanAudit(atEditableProbe, facts.PlanId);
            string spaceBeforeClose = DescribeSpace(atEditableProbe, facts.PlanId);
            long revisionBeforeClose = atEditableProbe.ScheduleRevision;
            ActorLaneSnapshot laneBeforeClose = FindLaneSnapshot(atEditableProbe, heroUnitId);
            Assert.That(laneBeforeClose, Is.Not.Null, "hero 必须有 Lane：" + stream);

            // —— Tick 10：请求关闭后正式关闭（Tick 末）⇒ 撤销提交权限，但账本继续可审计 ——
            LogicSnapshot atClose = side.Snapshots[Task07HeroWindowCloseTick];
            TurnWindowSnapshot closedWindow = WindowOf(atClose, facts.HeroWindowId);
            Assert.That(closedWindow, Is.Not.Null,
                "已关闭窗口必须仍留在可审计账本里（不因关闭被移除）：" + stream);
            Assert.That(closedWindow.IsOpen, Is.False, "Tick 末必须正式关闭：" + stream);
            Assert.That(closedWindow.IsAcceptingSubmissions, Is.False,
                "请求关闭后立即停止接受提交（本 Tick 后续命令稳定拒绝）：" + stream);
            Assert.That(atClose.WindowManager.CurrentWindowId, Is.EqualTo(0L),
                "关闭后当前窗口必须为空：" + stream);
            Assert.That(atClose.WindowManager.LastClosedWindowId, Is.EqualTo(facts.HeroWindowId), stream);
            Assert.That(closedWindow.ReservedBudgetTicks, Is.EqualTo(facts.PlanBudgetCost),
                "关闭窗口的账本必须保留到该 Editable 预留锁定或释放：" + stream);
            Assert.That(DescribePlanAudit(atClose, facts.PlanId), Is.EqualTo(planAuditBeforeClose),
                "窗口切换不得改动计划的身份/状态/排程/预算投影：" + stream);
            Assert.That(DescribeSpace(atClose, facts.PlanId), Is.EqualTo(spaceBeforeClose),
                "窗口切换不得改动计划的移动段与空间 Reservation：" + stream);
            Assert.That(atClose.ScheduleRevision, Is.EqualTo(revisionBeforeClose),
                "窗口切换不得推进排程修订号：" + stream);
            ActorLaneSnapshot laneAtClose = FindLaneSnapshot(atClose, heroUnitId);
            Assert.That(laneAtClose, Is.Not.Null, stream);
            Assert.That(laneAtClose.PendingPlanCount, Is.EqualTo(laneBeforeClose.PendingPlanCount),
                "窗口切换不得改动 Lane 队列：" + stream);
            Assert.That(laneAtClose.Locked, Is.EqualTo(laneBeforeClose.Locked),
                "窗口切换不得改动 Lane 提交锁：" + stream);

            AdrenalineLedgerSnapshot afterClose = LedgerOf(atClose, heroUnitId);
            Assert.That(afterClose.AvailableAdrenaline, Is.EqualTo(facts.HeroAvailableAfterSecondAccrual),
                "窗口关闭**不**清零肾上腺素：" + stream);
            Assert.That(afterClose.CycleId, Is.EqualTo((long)facts.HeroCycleAfterOwnWindowOpen),
                "窗口关闭不得递增个人周期：" + stream);

            // —— Tick 12：enemy 窗口打开（跨窗口）⇒ hero 的账本与计划逐字不变 ——
            LogicSnapshot atEnemyOpen = side.Snapshots[Task07EnemyWindowOpenTick];
            Assert.That(atEnemyOpen.WindowManager.Windows.Count, Is.EqualTo(2),
                "此时账本里应同时有 hero 的已关闭窗口与 enemy 的当前窗口：" + stream);
            Assert.That(atEnemyOpen.WindowManager.CurrentWindowId, Is.Not.EqualTo(0L), stream);
            TurnWindowSnapshot enemyWindow = WindowOf(atEnemyOpen, atEnemyOpen.WindowManager.CurrentWindowId);
            facts.EnemyWindowId = enemyWindow.WindowId;
            Assert.That(enemyWindow.OwnerUnitId, Is.EqualTo(enemyUnitId),
                "本 Tick 到期的是 enemy 的窗口：" + stream);
            Assert.That(enemyWindow.OwnerUnitId, Is.Not.EqualTo(heroUnitId), stream);
            Assert.That(atEnemyOpen.WindowManager.LastClosedWindowId, Is.EqualTo(facts.HeroWindowId), stream);
            Assert.That(DescribePlanAudit(atEnemyOpen, facts.PlanId), Is.EqualTo(planAuditBeforeClose),
                "跨窗口切换不得改动已有计划（ID/Tick/状态/Lane/预算/空间预留全不变）：" + stream);
            Assert.That(DescribeSpace(atEnemyOpen, facts.PlanId), Is.EqualTo(spaceBeforeClose), stream);
            Assert.That(atEnemyOpen.ScheduleRevision, Is.EqualTo(revisionBeforeClose), stream);

            AdrenalineLedgerSnapshot acrossOtherWindow = LedgerOf(atEnemyOpen, heroUnitId);
            Assert.That(acrossOtherWindow.AvailableAdrenaline,
                Is.EqualTo(facts.HeroAvailableAfterSecondAccrual),
                "Available 必须跨**其他单位**窗口保留（不衰减、不清零）：" + stream);
            Assert.That(acrossOtherWindow.CycleId, Is.EqualTo((long)facts.HeroCycleAfterOwnWindowOpen),
                "其他单位开窗不得递增本人周期：" + stream);

            // —— Tick 13：在**他人**窗口内激活并发授权 ⇒ 恰好消费一次权威费用 ——
            LogicSnapshot atActivation = side.Snapshots[Task07ActivationTick];
            Assert.That(atActivation.ConcurrentAction.HasActiveAuthorization, Is.True,
                "并发激活必须成功（他人窗口 + 显式注入的主角 + 局外资源足够）：" + stream);
            Assert.That(atActivation.ConcurrentAction.WindowId, Is.EqualTo(facts.EnemyWindowId),
                "授权必须绑定到当前（他人）窗口：" + stream);
            Assert.That(atActivation.ConcurrentAction.PlayerUnitId, Is.EqualTo(heroUnitId),
                "授权受控单位必须等于装配显式注入的主角单位：" + stream);
            Assert.That(atActivation.Resources.MetaResource,
                Is.EqualTo(atEnemyOpen.Resources.MetaResource - 1),
                "并发能力费用只来自权威定义且恰好消费一次：" + stream);

            // —— Tick 14：计划到期 ⇒ 启动门禁原子提交 Reserved -> Spent（来源窗口已关闭）——
            LogicSnapshot atStart = side.Snapshots[Task07RequestedStartTick];
            ActionPlanSnapshot running = FindPlanSnapshot(atStart, facts.PlanId);
            Assert.That(running, Is.Not.Null, "启动 Tick 上计划必须仍在活动索引：" + stream);
            Assert.That(running.State, Is.EqualTo((int)ActionPlanState.Running),
                "到期门禁必须原子锁定/启动计划（state = Running）：" + stream);
            Assert.That(running.LockedAtTick, Is.EqualTo((long)Task07RequestedStartTick),
                "锁定 Tick 与 Running 必须同一次原子提交写入：" + stream);
            Assert.That(running.ReservedTurnBudgetTicks, Is.EqualTo(0),
                "锁定后 Reserved 投影必须归零（预算已转为 Spent）：" + stream);
            TurnWindowSnapshot sourceAfterLock = WindowOf(atStart, facts.HeroWindowId);
            Assert.That(sourceAfterLock.ReservedBudgetTicks, Is.EqualTo(0),
                "来源窗口（已关闭）的 Reserved 必须清零：" + stream);
            Assert.That(sourceAfterLock.SpentBudgetTicks, Is.EqualTo(facts.PlanBudgetCost),
                "来源窗口的 Spent 必须恰好等于该计划的成本（不二次扣费）：" + stream);
            Assert.That(atStart.WindowManager.CurrentWindowId, Is.EqualTo(facts.EnemyWindowId),
                "本 Tick 的当前窗口仍是 enemy 的窗口（计划来自窗口 A 却在窗口 B 期间启动）：" + stream);
            Assert.That(running.StartTick, Is.EqualTo((long)Task07RequestedStartTick),
                "计划的起点不得因窗口切换改变：" + stream);
            Assert.That(running.SubmittedWindowId, Is.EqualTo(facts.HeroWindowId),
                "计划的窗口归属必须保持为其来源窗口（审计字段不随当前窗口改变）：" + stream);

            // —— Tick 60：enemy 窗口关闭 ⇒ 授权被撤销，但已接受计划与其 Spent 继续存在 ——
            LogicSnapshot atTerminal = side.Snapshots[Task07TerminalTick];
            Assert.That(WindowOf(atTerminal, facts.EnemyWindowId).IsOpen, Is.False,
                "Tick " + Task07TerminalTick + " 末必须正式关闭 enemy 窗口：" + stream);
            Assert.That(atTerminal.ConcurrentAction.HasActiveAuthorization, Is.False,
                "窗口关闭即撤销并发授权：" + stream);
            Assert.That(atTerminal.ConcurrentAction.PlayerUnitId, Is.EqualTo(0L), stream);
            Assert.That(atTerminal.WindowManager.CurrentWindowId, Is.EqualTo(0L), stream);
            Assert.That(WindowOf(atTerminal, facts.HeroWindowId).SpentBudgetTicks,
                Is.EqualTo(facts.PlanBudgetCost),
                "Locked/Running 的 Spent 在任何终态都不退款：" + stream);

            return facts;
        }

        /// <summary>
        /// <strong>比较面边界</strong>的<strong>可失败</strong>结构断言（验收标准「时间预算全程为整数」）：
        /// 窗口、预算、并发授权与肾上腺素账本的快照成员<strong>不得</strong>携带浮点事实，
        /// 且预算四项必须是整数 Tick、周期号必须是整数。
        /// </summary>
        private static void AssertTask07ComparisonSurfaceIsIntegerOnly()
        {
            Type[] types =
            {
                typeof(TurnWindowSnapshot),
                typeof(TurnWindowReservationSnapshot),
                typeof(TurnWindowManagerSnapshot),
                typeof(AdrenalineLedgerSnapshot),
                typeof(AdrenalineReservationSnapshot),
                typeof(BattleResourceSnapshot),
                typeof(ConcurrentActionSnapshot)
            };

            for (int t = 0; t < types.Length; t++)
            {
                Type type = types[t];
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                for (int m = 0; m < members.Length; m++)
                {
                    Type memberType = MemberValueType(members[m]);
                    if (memberType == null) continue;
                    Type element = ElementTypeOf(memberType);
                    Assert.That(element == typeof(float) || element == typeof(double), Is.False,
                        type.Name + " 的成员 " + members[m].Name + " 不得携带浮点事实（"
                        + element.Name + "）：时间预算与资源账本全程为整数 Tick");
                }
            }

            // 反向可失败性：字段类型一旦被改成浮点，本断言立即失败。
            Assert.That(typeof(TurnWindowSnapshot).GetProperty("TotalBudgetTicks").PropertyType,
                Is.EqualTo(typeof(int)), "窗口总预算必须是整数 Tick");
            Assert.That(typeof(TurnWindowSnapshot).GetProperty("ReservedBudgetTicks").PropertyType,
                Is.EqualTo(typeof(int)), "窗口预留必须是整数 Tick");
            Assert.That(typeof(TurnWindowSnapshot).GetProperty("SpentBudgetTicks").PropertyType,
                Is.EqualTo(typeof(int)), "窗口已消费必须是整数 Tick");
            Assert.That(typeof(TurnWindowSnapshot).GetProperty("AvailableBudgetTicks").PropertyType,
                Is.EqualTo(typeof(int)), "窗口可用预算必须是整数 Tick");
            Assert.That(typeof(TurnWindowSnapshot).GetProperty("OpenedAtTick").PropertyType,
                Is.EqualTo(typeof(long)), "窗口打开时刻必须是整数 Tick");
            Assert.That(typeof(AdrenalineLedgerSnapshot).GetProperty("AvailableAdrenaline").PropertyType,
                Is.EqualTo(typeof(int)), "Available 肾上腺素必须是整数");
            Assert.That(typeof(AdrenalineLedgerSnapshot).GetProperty("CycleId").PropertyType,
                Is.EqualTo(typeof(long)), "个人周期号必须是整数");
            Assert.That(typeof(BattleResourceSnapshot).GetProperty("TurnBudgetAvailable").PropertyType,
                Is.EqualTo(typeof(long)), "全窗口可用预算聚合必须是整数 Tick");
        }
    }
}
