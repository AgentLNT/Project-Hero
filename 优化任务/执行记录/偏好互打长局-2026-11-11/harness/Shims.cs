// Standalone host adapters only. All combat, AI, scheduling and replay code comes from Assets/Scripts/Logic.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;

namespace UnityEngine
{
    public static class Debug { public static void Log(object value) => Console.WriteLine(value); }
    public static class JsonUtility
    {
        static JsonSerializerOptions Options(bool pretty)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info => { if (info.Kind == JsonTypeInfoKind.Object && info.CreateObject == null)
                info.CreateObject = () => Activator.CreateInstance(info.Type, true); });
            return new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true,
                WriteIndented = pretty, TypeInfoResolver = resolver };
        }
        public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Options(false));
        public static string ToJson(object value, bool pretty = false) => JsonSerializer.Serialize(value, value.GetType(), Options(pretty));
    }
}
namespace ProjectHero.Core.Compatibility.Authoring { internal static class StandaloneAuthoringMarker { } }
namespace ProjectHero.Core.Compatibility.Runtime
{
    public sealed record BattleSimulationSeed(BattleDefinition Definition, EncounterDefinitionId EncounterId, BattleRuntimeInputs RuntimeInputs)
    { public string BattleDefinitionHash => Definition.BattleDefinitionHashValue; }
}
namespace ProjectHero.Authoring.Legacy
{
    public static class LegacyIdMigrationManifest
    {
        public static readonly System.Collections.Generic.IReadOnlyList<object> LibraryVolumes
            = Array.AsReadOnly(Enumerable.Range(0, ProjectHero.Editor.FormalAiSourceExport.LibraryVolumeBindingCount).Select(_ => new object()).ToArray());
    }
}
namespace ProjectHero.Editor
{
    public static class FormalAiBaselineTools
    {
        // Same content-fingerprint protocol as the Unity tool, including uppercase per-file digests.
        public static string ContentFingerprint()
        {
            var files = Directory.GetFiles("Assets/Resources", "*", SearchOption.AllDirectories).Append("Assets/Scenes/CombatSampleScene.unity")
                .OrderBy(p => p, StringComparer.Ordinal);
            var text = new StringBuilder(); using var sha = SHA256.Create();
            foreach (var path in files) text.Append(path.Replace('\\', '/')).Append(':')
                .Append(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "")).Append('\n');
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
