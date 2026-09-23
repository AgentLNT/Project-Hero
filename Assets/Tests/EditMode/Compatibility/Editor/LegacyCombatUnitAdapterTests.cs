using System.Reflection;
using NUnit.Framework;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Core.Compatibility.Authoring;
using ProjectHero.Core.Entities;
using ProjectHero.Logic.Damage;
using UnityEngine;

namespace ProjectHero.Tests.Compatibility.Editor
{
    /// <summary>
    /// 唯一需要旧运行时类型（<c>CombatUnit</c>）的 Authoring 接缝用例。
    ///
    /// 该目录无 asmdef，编译进 Assembly-CSharp-Editor，因此可以引用 Assembly-CSharp 中的
    /// <c>CombatUnit</c>；自定义 asmdef 不允许引用 Assembly-CSharp（00 号规则 17），
    /// 所以这一条不能随 LegacyAdapterTests 一起搬进 ProjectHero.Authoring.Tests。
    ///
    /// 它验证的是"接缝本身"：旧组件字段 → <see cref="LegacyUnitStatInputs"/> 数值子集
    /// → Logic <c>UnitDefinition</c>，一次性映射、不回读、不生成通用 DamageReduction。
    /// </summary>
    public class LegacyCombatUnitAdapterTests
    {
        [Test]
        public void CombatUnitFieldsMapOnceIntoLogicUnitDefinition()
        {
            var go = new GameObject("LegacyStatsAdapterTestUnit", typeof(CombatUnit));
            go.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var unit = go.GetComponent<CombatUnit>();

                // 1) 组件 → 纯数值子集（唯一允许读取旧 MonoBehaviour 的位置）。
                var stats = LegacyCombatUnitStatsReader.Read(unit);
                // 默认字段：Strength/Constitution/ArmorWeight = 10
                // → TotalMass = 50 + 20 + 20 + 10 = 100；Swiftness = 7.5 + 2.5 = 10；
                //   MaxHealth = Constitution × 20 = 200。
                Assert.That(stats.TotalMass, Is.EqualTo(100f));
                Assert.That(stats.Swiftness, Is.EqualTo(10f));
                Assert.That(stats.MaxHealth, Is.EqualTo(200f));
                Assert.That(stats.ArmorDefense, Is.EqualTo(0f));
                Assert.That(stats.MagicResistance, Is.EqualTo(0f));

                // 2) 数值子集 → Logic 定义（含动作集合与初始生命，二者都是显式注入）。
                var definition = LegacyUnitStatsAdapter.ConvertStats(
                    "unit.from_combat_unit", stats, "action_set.radius_1");

                Assert.That(definition.Mass, Is.EqualTo(100f));
                Assert.That(definition.MomentumSpeed, Is.EqualTo(10f));
                Assert.That(definition.ActionSpeed, Is.EqualTo(10f));
                Assert.That(definition.MoveSpeed, Is.EqualTo(10f));
                Assert.That(definition.InitialHealth, Is.EqualTo(200f),
                    "旧 MaxHealth = Constitution × 20 只在 Authoring 边界量化一次");
                Assert.That(definition.ActionSetId.Value, Is.EqualTo("action_set.radius_1"),
                    "动作集合由内容配置决定，不按控制者类型挑选");
                Assert.That(definition.BaseDamageResistanceQ10[DamageChannels.PhysicalBlunt], Is.EqualTo(0));
                Assert.That(definition.BaseDamageResistanceQ10[DamageChannels.Arcane], Is.EqualTo(0));

                foreach (var member in typeof(ProjectHero.Logic.Combat.UnitDefinition).GetMembers(
                             BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(member.Name, Does.Not.Contain("DamageReduction"));
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
