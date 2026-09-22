using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace ProjectHero.Logic.Tests
{
    /// <summary>任务 02「必须产出」4 的自动化边界检查：Logic 不得引用 UnityEngine。</summary>
    public class AssemblyBoundaryTests
    {
        private static Assembly LogicAssembly => typeof(ProjectHero.Logic.Combat.BattleRules).Assembly;

        [Test]
        public void LogicAssemblyHasNoUnityEngineReference()
        {
            var asm = LogicAssembly;
            Assert.That(asm.GetName().Name, Is.EqualTo("ProjectHero.Logic"), "Logic 程序集名称");

            // 1. 程序集级引用：不得引用任何 UnityEngine 模块。
            var referenced = asm.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Assert.That(referenced, Has.None.Matches("^UnityEngine(\\..*)?"),
                "ProjectHero.Logic 不得引用任何 UnityEngine 模块程序集");

            // 2. 类型级扫描：导出类型全名与成员签名（字段/属性/方法参数与返回）不得出现 UnityEngine 命名空间。
            foreach (var type in asm.GetExportedTypes())
            {
                Assert.That(type.FullName ?? string.Empty, Does.Not.StartWith("UnityEngine"),
                    $"类型 {type.FullName} 不得位于 UnityEngine 命名空间");
                AssertMembersDoNotUseUnityEngine(type);
            }
        }

        [Test]
        public void LogicDefinitionContainsNoUnityObjectReference()
        {
            var asm = LogicAssembly;
            foreach (var type in asm.GetExportedTypes())
            {
                // 1. 继承链不得出现 UnityEngine 基类（更不得继承 UnityEngine.Object）。
                var baseType = type.BaseType;
                while (baseType != null)
                {
                    Assert.That(baseType.FullName ?? string.Empty, Does.Not.StartWith("UnityEngine"),
                        $"类型 {type.FullName} 的基类 {baseType.FullName} 不得来自 UnityEngine");
                    baseType = baseType.BaseType;
                }

                // 2. 不得以 Vector3/GameObject/MonoBehaviour/ScriptableObject 等包装规避引擎依赖。
                Assert.That(type.Name,
                    Does.Not.Contain("Vector3").And.Not.Contain("UnityObject").And.Not.Contain("GameObject"),
                    $"类型 {type.FullName} 不得包装 Unity 对象类型");

                AssertMembersDoNotUseUnityEngine(type);
            }
        }

        private static void AssertMembersDoNotUseUnityEngine(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var field in type.GetFields(flags))
                AssertTypeDoesNotUseUnityEngine(field.FieldType, $"字段 {type.FullName}.{field.Name}");

            foreach (var property in type.GetProperties(flags))
                AssertTypeDoesNotUseUnityEngine(property.PropertyType, $"属性 {type.FullName}.{property.Name}");

            foreach (var method in type.GetMethods(flags))
            {
                AssertTypeDoesNotUseUnityEngine(method.ReturnType, $"方法 {type.FullName}.{method.Name} 返回");
                foreach (var parameter in method.GetParameters())
                    AssertTypeDoesNotUseUnityEngine(parameter.ParameterType,
                        $"方法 {type.FullName}.{method.Name} 参数 {parameter.Name}");
            }
        }

        private static void AssertTypeDoesNotUseUnityEngine(Type memberType, string context)
        {
            if (memberType.IsGenericType)
            {
                foreach (var argument in memberType.GetGenericArguments())
                    AssertTypeDoesNotUseUnityEngine(argument, context);
            }
            var ns = memberType.Namespace ?? string.Empty;
            var declaring = memberType.FullName ?? string.Empty;
            Assert.That(ns, Does.Not.StartWith("UnityEngine"), context);
            Assert.That(declaring, Does.Not.StartWith("UnityEngine."), context);
        }
    }
}
