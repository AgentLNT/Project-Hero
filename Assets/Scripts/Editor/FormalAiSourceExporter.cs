using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Core.Compatibility.Runtime;

namespace ProjectHero.Editor
{
    /// <summary>Exports immutable, already-built formal inputs; does not translate gameplay rules.</summary>
    public static class FormalAiSourceExporter
    {
        public static void Export(BattleSimulationSeed source)
        {
            string folder = FormalAiMatchupTools.Root + "/harness"; Directory.CreateDirectory(folder);
            var code = new StringBuilder("// Generated from the real Unity scene. Do not hand-edit.\nnamespace ProjectHero.Editor {\npublic static class FormalAiSourceExport {\n");
            code.Append("public const int LibraryVolumeBindingCount = ").Append(LegacyIdMigrationManifest.LibraryVolumes.Count).Append(";\n");
            code.Append("public static global::ProjectHero.Logic.Definitions.BattleDefinition Definition() => ")
                .Append(Expression(source.Definition, source.Definition.GetType())).Append(";\n");
            code.Append("public static global::ProjectHero.Core.Compatibility.Runtime.BattleSimulationSeed Seed() => new(Definition(), ")
                .Append(Expression(source.EncounterId, source.EncounterId.GetType())).Append(", ")
                .Append(Expression(source.RuntimeInputs, source.RuntimeInputs.GetType())).Append(");\n}\n}\n");
            File.WriteAllText(folder + "/FormalAiSourceExport.cs", code.ToString(), Encoding.UTF8);
            UnityEngine.Debug.Log("MATCHUP_EXPORTED source=" + source.BattleDefinitionHash);
        }
        static string Expression(object value, Type expected)
        {
            if (value == null) return "null";
            var type = value.GetType();
            if (value is string text) return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
            if (value is bool flag) return flag ? "true" : "false";
            if (type.IsEnum) return "(" + Name(type) + ")" + Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture) + "F";
            if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture) + "D";
            if (value is long l) return l.ToString(CultureInfo.InvariantCulture) + "L";
            if (value is ulong ul) return ul.ToString(CultureInfo.InvariantCulture) + "UL";
            if (type.IsPrimitive || value is decimal) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is IEnumerable list)
            {
                var dictionary = expected.GetInterfaces().Append(expected).FirstOrDefault(t => t.IsGenericType
                    && (t.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IReadOnlyDictionary<,>)
                        || t.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IDictionary<,>)));
                if (dictionary != null)
                {
                    var arguments = dictionary.GetGenericArguments();
                    return "new global::System.Collections.Generic.Dictionary<" + Name(arguments[0]) + "," + Name(arguments[1]) + ">{"
                        + string.Join(",", list.Cast<object>().Select(item => "{" + Expression(item.GetType().GetProperty("Key").GetValue(item), arguments[0])
                            + "," + Expression(item.GetType().GetProperty("Value").GetValue(item), arguments[1]) + "}")) + "}";
                }
                var element = expected.IsArray ? expected.GetElementType() : expected.GetInterfaces().Append(expected)
                    .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>)).GetGenericArguments()[0];
                return "new " + Name(element) + "[]{" + string.Join(",", list.Cast<object>().Select(item => Expression(item, element))) + "}";
            }
            var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Cast<MemberInfo>()
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance)).ToArray();
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault(c =>
                c.GetParameters().All(p => members.Any(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase))));
            if (constructor == null) throw new InvalidOperationException("FORMAL_EXPORT_CONSTRUCTOR: " + type.FullName);
            return "new " + Name(type) + "(" + string.Join(",", constructor.GetParameters().Select(p =>
            {
                var member = members.Single(m => string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                return p.Name + ":" + Expression(member is PropertyInfo property ? property.GetValue(value) : ((FieldInfo)member).GetValue(value), p.ParameterType);
            })) + ")";
        }
        static string Name(Type type) => type.IsGenericType
            ? "global::" + type.GetGenericTypeDefinition().FullName.Split('`')[0].Replace('+', '.') + "<" + string.Join(",", type.GetGenericArguments().Select(Name)) + ">"
            : "global::" + type.FullName.Replace('+', '.');
    }
}
