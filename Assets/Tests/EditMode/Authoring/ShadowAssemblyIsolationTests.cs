using System.Linq;
using NUnit.Framework;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEditor;
using UnityEditor.Compilation;

namespace ProjectHero.Authoring.Tests
{
    public sealed class ShadowAssemblyIsolationTests
    {
        [Test]
        public void RuntimeHasNoDiagnosticTypesOrAssemblyReferences()
        {
            var runtime = typeof(BattleRuntimeBootstrap).Assembly;
            Assert.That(runtime.GetReferencedAssemblies().Any(a => a.Name.Contains("Diagnostics")), Is.False);
            Assert.That(runtime.GetTypes().Any(t => t.Name.StartsWith("Shadow") || t.Name == "RuntimeCallLedger"
                || t.Name == "LegacyWriterRegistry" || t.Name == "RuntimeCallbackRegistry" || t.Name == "FrameAdapterSet"), Is.False);
            Assert.That(typeof(ShadowBattleRunner).Assembly.GetName().Name, Is.EqualTo("ProjectHero.Migration.Diagnostics"));
            Assert.That(typeof(RuntimeCallLedger).Assembly, Is.EqualTo(typeof(ShadowBattleRunner).Assembly));
            Assert.That(typeof(LegacyWriterRegistry).Assembly, Is.EqualTo(typeof(ShadowBattleRunner).Assembly));
        }

        [Test]
        public void PlayerCompilationGraphExcludesMigrationAndDiagnosticScenes()
        {
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            Assert.That(assemblies.Any(a => a.name == "ProjectHero.UnityView"), Is.True);
            Assert.That(assemblies.Where(a => a.name.StartsWith("ProjectHero.")).Any(a => a.name.Contains("Diagnostics") || a.name.Contains("Tests")), Is.False);
            Assert.That(assemblies.SelectMany(a => a.sourceFiles).Any(p => p.Replace('\\', '/').StartsWith("Assets/Diagnostics/")), Is.False);
            Assert.That(EditorBuildSettings.scenes.Where(s => s.enabled).Any(s => s.path.StartsWith("Assets/Diagnostics/")), Is.False);
        }

        [Test]
        public void MainSceneAssetDependenciesExcludeMigrationHarness()
        {
            var paths = AssetDatabase.GetDependencies("Assets/Scenes/CombatSampleScene.unity", true);
            Assert.That(paths.Any(p => p.StartsWith("Assets/Diagnostics/")), Is.False, string.Join("\n", paths.Where(p => p.Contains("Diagnostics"))));
        }
    }
}
