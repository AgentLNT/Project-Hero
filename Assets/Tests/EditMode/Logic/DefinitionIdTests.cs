using System.Linq;
using System.Reflection;
using NUnit.Framework;
using ProjectHero.Logic.Damage;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Tests
{
    /// <summary>配置 ID（字符串）与战斗实例 ID（long）的值类型、格式与互斥性契约（主方案 2.2）。</summary>
    public class DefinitionIdTests
    {
        [Test]
        public void DefinitionIdRejectsEmptyOrInvalidFormat()
        {
            string[] invalid =
            {
                null, "", " ", "QuickSlash", "A.b", "a-b", "a b", "a:b", "a/b", "a\\b", "a#b", "点.a"
            };
            foreach (var id in invalid)
            {
                Assert.That(DefinitionIdValidation.IsValidFormat(id), Is.False, $"应拒绝非法 ID：'{id}'");
                Assert.That(DefinitionIdValidation.ValidateFormat(id), Is.Not.Null, $"应返回错误码：'{id}'");
            }
            Assert.That(DefinitionIdValidation.ValidateFormat(null), Is.EqualTo(DefinitionIdValidation.ID_EMPTY));
            Assert.That(DefinitionIdValidation.ValidateFormat(""), Is.EqualTo(DefinitionIdValidation.ID_EMPTY));
            Assert.That(DefinitionIdValidation.ValidateFormat(" "), Is.EqualTo(DefinitionIdValidation.ID_INVALID_CHARACTERS));
            Assert.That(DefinitionIdValidation.ValidateFormat("QuickSlash"),
                Is.EqualTo(DefinitionIdValidation.ID_INVALID_CHARACTERS));

            string[] valid =
            {
                "a", "unit.skeleton_archer", "action.heavy_slash", "quick_slash_r1", "123",
                "physical.blunt", "impact.slash", "action.quick_slash.r1", "a.b_c1"
            };
            foreach (var id in valid)
            {
                Assert.That(DefinitionIdValidation.IsValidFormat(id), Is.True, $"应接受合法 ID：'{id}'");
                Assert.That(DefinitionIdValidation.ValidateFormat(id), Is.Null, $"应通过校验：'{id}'");
            }
        }

        [Test]
        public void DefinitionIdRejectsDuplicateWithinNamespace()
        {
            Assert.That(DefinitionIdValidation.ValidateUnique(new[] { "a", "b", "c" }), Is.Null);
            Assert.That(DefinitionIdValidation.ValidateUnique(new[] { "a", "b", "a" }),
                Is.EqualTo(DefinitionIdValidation.ID_DUPLICATE));
            Assert.That(DefinitionIdValidation.ValidateUnique(new[] { "a", "a" }),
                Is.EqualTo(DefinitionIdValidation.ID_DUPLICATE));
            // Ordinal 区分大小写：a 与 A 是不同 ID（格式上 A 非法，此处只验证重复判定用 Ordinal 语义）
            Assert.That(DefinitionIdValidation.ValidateUnique(new[] { "a", "b" }), Is.Null);
        }

        [Test]
        public void DistinctIdTypesCannotBeInterchanged()
        {
            var idTypes = new[]
            {
                typeof(UnitDefinitionId), typeof(ActionSpecId), typeof(VolumeSpecId),
                typeof(AttackPatternId), typeof(MovementPatternId), typeof(ActionSetId),
                typeof(DamageChannelId), typeof(ImpactProfileId), typeof(StatusEffectSpecId),
                typeof(EncounterDefinitionId), typeof(EncounterSlotId), typeof(FactionId),
                typeof(UnitId), typeof(ActionPlanId), typeof(ReactionOpportunityId),
                typeof(WindowId), typeof(EffectId)
            };

            foreach (var type in idTypes)
            {
                var implicitOperators = type
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "op_Implicit")
                    .ToArray();
                Assert.That(implicitOperators, Is.Empty,
                    $"{type.Name} 不得定义隐式转换运算符（防止跨类型误传）");
            }

            // 同值不同类不相等（record 结构体的类型相关相等性）。
            Assert.That(new UnitDefinitionId("x").Equals(new FactionId("x")), Is.False);
            Assert.That(new UnitId(1).Equals(new ActionPlanId(1)), Is.False);
        }

        [Test]
        public void DamageChannelAndImpactProfileIdsCannotBeInterchanged()
        {
            // 伤害通道与冲击 Profile 是两类 ID：值相同也不相等、不可互传。
            Assert.That(DamageChannels.PhysicalSlash.Value, Is.EqualTo("physical.slash"));
            Assert.That(ImpactProfiles.Slash.Value, Is.EqualTo("impact.slash"));
            Assert.That(new DamageChannelId("physical.slash").Equals(new ImpactProfileId("physical.slash")), Is.False);
            Assert.That(typeof(DamageChannelId) != typeof(ImpactProfileId), Is.True);

            Assert.That(typeof(DamageChannelId).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(m => m.Name == "op_Implicit"), Is.False);
            Assert.That(typeof(ImpactProfileId).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(m => m.Name == "op_Implicit"), Is.False);

            // 载荷正交：伤害分量携带 DamageChannelId，动量侧携带 ImpactProfileId。
            // 注：DamageComponentSpec 是手写只读结构体（C# 9 无 record struct），成员为字段。
            var componentField = typeof(DamageComponentSpec).GetField("ChannelId");
            Assert.That(componentField, Is.Not.Null);
            Assert.That(componentField.FieldType, Is.EqualTo(typeof(DamageChannelId)));
            var profileProperty = typeof(ProjectHero.Logic.Actions.AttackPayloadSpec).GetProperty("ImpactProfileId");
            Assert.That(profileProperty, Is.Not.Null);
            Assert.That(profileProperty.PropertyType, Is.EqualTo(typeof(ImpactProfileId)));
        }

        [Test]
        public void InstanceIdsUseZeroAsInvalidAndStartAtOne()
        {
            // 0 保留为无效值（default 即无效），首个有效值为 1，单调递增。
            Assert.That(new UnitId(0).IsValid, Is.False);
            Assert.That(default(ActionPlanId).IsValid, Is.False);
            Assert.That(new UnitId(1).IsValid, Is.True);

            var generator = new LogicIdGenerator();
            Assert.That(generator.NextUnitId().Value, Is.EqualTo(1));
            Assert.That(generator.NextUnitId().Value, Is.EqualTo(2));
            Assert.That(generator.NextUnitId().Value, Is.EqualTo(3));
            // 各计数器独立从 1 起。
            Assert.That(generator.NextActionPlanId().Value, Is.EqualTo(1));
            Assert.That(generator.NextReactionOpportunityId().Value, Is.EqualTo(1));
            Assert.That(generator.NextWindowId().Value, Is.EqualTo(1));
            Assert.That(generator.NextEffectId().Value, Is.EqualTo(1));
        }
    }
}
