using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>路径成本/搜索规则与移动时序契约（主方案 3.8 / 00 号规则 32）。</summary>
    public class PathRulesTests
    {
        private static MovementPatternSpec MakeMovementPattern()
            => new MovementPatternSpec(
                new MovementPatternId("pattern.move.test"),
                DirectionalGeometry.ExpandFromBases(
                    new[] { new TrianglePoint(3, 0, 1) },
                    new[] { new TrianglePoint(4, 1, -1) }));

        private static ActionSpec MakeMove(int maxPathWeightUnits, int baseStepTicks = 30)
            => new ActionSpec(
                new ActionSpecId("action.move.test"),
                ActionType.Move,
                new MoveTimingSpec(baseStepTicks, 10),
                new MovePayloadSpec(maxPathWeightUnits, MakeMovementPattern()),
                AdrenalineCost: 0);

        [Test]
        public void PathCostRulesFreezeEvenOddDirectionWeightsAtOneAndTwo()
        {
            var rules = PathCostRules.FrozenV1;
            Assert.That(rules.EvenDirectionStepWeightUnits, Is.EqualTo(1));
            Assert.That(rules.OddDirectionStepWeightUnits, Is.EqualTo(2));

            for (int i = 0; i < 12; i++)
            {
                var direction = (GridDirection)i;
                int expected = (i % 2 == 0) ? 1 : 2;
                Assert.That(rules.StepWeightUnits(direction), Is.EqualTo(expected),
                    $"{direction}（索引 {i}）权重必须为 {expected}");
            }

            Assert.That(rules.Validate(), Is.Null);
            Assert.That(new PathCostRules(0, 2).Validate(), Is.EqualTo(PathCostRules.PATH_COST_WEIGHT_INVALID));
            Assert.That(new PathCostRules(1, -1).Validate(), Is.EqualTo(PathCostRules.PATH_COST_WEIGHT_INVALID));
        }

        [Test]
        public void PathSearchRulesRejectNonPositiveLimits()
        {
            Assert.That(new PathSearchRules(0, 256, 192).Validate(),
                Is.EqualTo(PathSearchCodes.PATH_SEARCH_LIMIT_INVALID));
            Assert.That(new PathSearchRules(4096, 0, 192).Validate(),
                Is.EqualTo(PathSearchCodes.PATH_SEARCH_LIMIT_INVALID));
            Assert.That(new PathSearchRules(4096, 256, 0).Validate(),
                Is.EqualTo(PathSearchCodes.PATH_SEARCH_LIMIT_INVALID));
            Assert.That(new PathSearchRules(-1, 256, 192).Validate(),
                Is.EqualTo(PathSearchCodes.PATH_SEARCH_LIMIT_INVALID));

            var frozen = PathSearchRules.FrozenV1;
            Assert.That(frozen.Validate(), Is.Null);
            Assert.That(frozen.MaxExpandedNodes, Is.EqualTo(4096));
            Assert.That(frozen.MaxPathWeightUnits, Is.EqualTo(256));
            Assert.That(frozen.MaxPathEdges, Is.EqualTo(192));

            // 包含式边界：恰好等于上限合法，严格大于才失败。
            Assert.That(frozen.ExceedsNodeLimit(4096), Is.False);
            Assert.That(frozen.ExceedsNodeLimit(4097), Is.True);
            Assert.That(frozen.ExceedsWeightLimit(256), Is.False);
            Assert.That(frozen.ExceedsWeightLimit(257), Is.True);
            Assert.That(frozen.ExceedsEdgeLimit(192), Is.False);
            Assert.That(frozen.ExceedsEdgeLimit(193), Is.True);
        }

        [Test]
        public void MoveReferenceSpeedIsFrozenAtTwenty()
        {
            Assert.That(BattleRules.FrozenReferenceMoveSpeed, Is.EqualTo(20));
            Assert.That(BattleRules.FrozenV1.ReferenceMoveSpeed, Is.EqualTo(20));
            Assert.That(BattleRules.FrozenV1.Validate(), Is.Null);
            Assert.That((BattleRules.FrozenV1 with { ReferenceMoveSpeed = 0 }).Validate(),
                Is.EqualTo(BattleRules.REFERENCE_MOVE_SPEED_INVALID));
            Assert.That((BattleRules.FrozenV1 with { ReferenceMoveSpeed = -1 }).Validate(),
                Is.EqualTo(BattleRules.REFERENCE_MOVE_SPEED_INVALID));
        }

        [Test]
        public void MoveSpeedIsNotAPathCostInput()
        {
            foreach (var type in new[] { typeof(PathCostRules), typeof(PathSearchRules) })
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(property.Name, Does.Not.Contain("Speed").And.Not.Contain("MoveSpeed"),
                        $"{type.Name}.{property.Name} 不得携带速度输入");
                }
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(method.Name, Does.Not.Contain("Speed"),
                        $"{type.Name}.{method.Name} 不得携带速度输入");
                    foreach (var parameter in method.GetParameters())
                    {
                        Assert.That(parameter.Name, Does.Not.Contain("Speed"),
                            $"{type.Name}.{method.Name}({parameter.Name}) 不得携带速度输入");
                    }
                }
            }

            // 启发函数签名只有两个 GridPoint。
            var heuristic = typeof(PathCostRules).GetMethod("HeuristicWeightUnits");
            Assert.That(heuristic, Is.Not.Null);
            Assert.That(heuristic.GetParameters().Select(p => p.ParameterType).ToArray(),
                Is.EqualTo(new[] { typeof(GridPoint), typeof(GridPoint) }));
            Assert.That(heuristic.ReturnType, Is.EqualTo(typeof(long)));

            // 行为面：MoveSpeed 变化只改变每权重单位 Tick，不改变路径权重。
            var rules = PathCostRules.FrozenV1;
            var from = new GridPoint(0, 0);
            var to = new GridPoint(4, 4);
            long weight = rules.StepWeightUnits(GridDirection.NorthEast) +
                          rules.StepWeightUnits(GridDirection.North);
            Assert.That(weight, Is.EqualTo(3), "权重与速度无关");
            Assert.That(rules.HeuristicWeightUnits(from, to), Is.EqualTo(4),
                "启发值 = dy + max(0, (dx - dy) / 2) = 4 + 0，与速度无关");
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 10), Is.EqualTo(60));
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 20), Is.EqualTo(30));
            Assert.That(rules.HeuristicWeightUnits(from, to), Is.EqualTo(4), "速度不改变启发值");
        }

        [Test]
        public void PathCostAndSearchRulesParticipateInDefinitionHash()
        {
            var baseline = BattleRules.FrozenV1;
            string digest = BattleDefinitionHash.OfBattleRules(baseline);

            string changedNodes = BattleDefinitionHash.OfBattleRules(
                baseline with { PathSearchRules = new PathSearchRules(4097, 256, 192) });
            Assert.That(changedNodes, Is.Not.EqualTo(digest), "MaxExpandedNodes 必须参与哈希");

            string changedWeight = BattleDefinitionHash.OfBattleRules(
                baseline with { PathSearchRules = new PathSearchRules(4096, 255, 192) });
            Assert.That(changedWeight, Is.Not.EqualTo(digest), "MaxPathWeightUnits 必须参与哈希");

            string changedEdges = BattleDefinitionHash.OfBattleRules(
                baseline with { PathSearchRules = new PathSearchRules(4096, 256, 191) });
            Assert.That(changedEdges, Is.Not.EqualTo(digest), "MaxPathEdges 必须参与哈希");

            string changedCost = BattleDefinitionHash.OfBattleRules(
                baseline with { PathCostRules = new PathCostRules(1, 3) });
            Assert.That(changedCost, Is.Not.EqualTo(digest), "方向权重必须参与哈希");

            var writer = new CanonicalHashWriter();
            baseline.WriteHashComponents(writer);
            string text = writer.ToCanonicalText();
            Assert.That(text, Does.Contain("path_cost.heuristic_version=3.8-nonnegative-integer-v1"));
            Assert.That(text, Does.Contain("path_search.max_expanded_nodes=4096"));
        }

        [Test]
        public void LegacyPerEdgeDurationClampIsNotANewTimingRule()
        {
            // 1. 结构面：新 Move 时序与规则没有任何 Clamp/秒字段。
            foreach (var type in new[] { typeof(MoveTimingSpec), typeof(BattleRules), typeof(PathCostRules) })
            {
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                       BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.That(member.Name, Does.Not.Contain("Clamp"),
                        $"{type.Name}.{member.Name} 不得出现旧逐边 Clamp");
                }
            }
            foreach (var field in typeof(MoveTimingSpec).GetFields(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(field.FieldType != typeof(float) && field.FieldType != typeof(double),
                    "MoveTimingSpec 只允许整数 Tick 字段");
            }

            // 2. 行为面：每权重单位只应用 max(1, RoundHalfUp(·))，无 0.2-4.0s 浮点 Clamp。
            // 旧 Clamp 上限 4.0s = 240 Tick；MoveSpeed=1 时每权重单位 600 Tick 不再被截断。
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 1), Is.EqualTo(600));
            // 旧 Clamp 下限 0.2s = 12 Tick；极小结果只取 max(1, ·)。
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(1, 20, 1000), Is.EqualTo(1));
            // RoundHalfUp 语义。
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 13), Is.EqualTo(46),
                "RoundHalfUp(600/13) = RoundHalfUp(46.15...) = 46");
            Assert.That(TickQuantization.ResolveMoveBaseStepTicks(30, 20, 20), Is.EqualTo(30));
        }

        [Test]
        public void MoveMaxPathWeightCannotExceedSearchRuleLimit()
        {
            var rules = BattleRules.FrozenV1;

            Assert.That(ActionDefinitionValidation.ValidateMove(MakeMove(0), rules),
                Is.EqualTo(ActionDefinitionCodes.MOVE_MAX_PATH_WEIGHT_INVALID));
            Assert.That(ActionDefinitionValidation.ValidateMove(MakeMove(-1), rules),
                Is.EqualTo(ActionDefinitionCodes.MOVE_MAX_PATH_WEIGHT_INVALID));
            Assert.That(ActionDefinitionValidation.ValidateMove(MakeMove(257), rules),
                Is.EqualTo(ActionDefinitionCodes.MOVE_MAX_PATH_WEIGHT_EXCEEDS_SEARCH_LIMIT),
                "257 > 搜索上限 256 必须拒绝");
            Assert.That(ActionDefinitionValidation.ValidateMove(MakeMove(256), rules), Is.Null,
                "恰好等于上限合法");
            Assert.That(ActionDefinitionValidation.ValidateMove(MakeMove(1), rules), Is.Null);
        }
    }
}
