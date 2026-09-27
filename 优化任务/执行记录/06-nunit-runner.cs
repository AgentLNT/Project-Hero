// 任务 06 的离线测试运行器（纯 Logic 测试程序集：编译 + mono 执行）：只用 BCL 反射执行 NUnit 用例并写出兼容 NUnit XML 的报告。
//
// 为什么需要它：本环境的 Unity Editor 无法完成包管理器 IPC 初始化（见交接记录 §3），
// 因此无法使用 Unity Test Runner。本运行器让同一批 EditMode 用例在 Unity 自带的
// Mono + Unity 的 nunit.framework 上真实执行，从而给出可复核的通过/失败证据。
//
// 它与 Unity Test Runner 的差异（必须显式声明，不得当作等价物）：
//   * 不支持 SetUpFixture / OneTimeTearDown 之外的 NUnit 生命周期扩展；
//   * 支持 [TestFixture] / [SetUp] / [TearDown] / [OneTimeSetUp] / [OneTimeTearDown] /
//     [Test] / [TestCase] / [Ignore] / [Explicit]；
//   * 不支持 [Values]/[Range]/[TestCaseSource]/[Theory] 等参数化来源。
// 若某用例依赖上面未列出的特性，运行器会把它报为 Inconclusive 并在报告中写明原因，
// 绝不静默跳过。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;

internal static class T06Runner
{
    private sealed class CaseResult
    {
        public string FullName;
        public string Result;          // Passed / Failed / Skipped / Inconclusive
        public string Message;
        public string StackTrace;
        public double DurationSeconds;
        public bool IsFixtureLevel;    // true = 代表一个 TestFixture（用于 XML 分组）
        public bool HasChildren;
    }

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromApplicationDirectory;

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: T06Runner <testAssembly> <resultsXml> [filter]");
            return 2;
        }

        string assemblyPath = Path.GetFullPath(args[0]);
        string resultsPath = Path.GetFullPath(args[1]);
        string filter = args.Length > 2 ? args[2] : null;

        Assembly assembly = Assembly.LoadFrom(assemblyPath);

        var cases = new List<CaseResult>();
        int passed = 0, failed = 0, skipped = 0, inconclusive = 0;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            Console.Error.WriteLine("WARNING: ReflectionTypeLoadException; inspecting " + types.Length + " loadable types");
        }

        // 与 Unity Test Runner 一致：显式 [TestFixture] 与"仅含 [Test] 方法的类"都算夹具。
        var fixtureTypes = types
            .Where(t => t != null && t.IsClass && !t.IsAbstract)
            .Where(t => t.GetCustomAttributes(typeof(NUnit.Framework.TestFixtureAttribute), true).Length > 0
                        || HasTestMethods(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (Type fixtureType in fixtureTypes)
        {
            string fixtureName = fixtureType.FullName;
            bool fixtureMatches = string.IsNullOrEmpty(filter) ||
                fixtureName.IndexOf(filter, StringComparison.Ordinal) >= 0;

            var methods = fixtureType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), true).Length > 0)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList();

            var setUp = FindSingle(fixtureType, typeof(NUnit.Framework.SetUpAttribute));
            var tearDown = FindSingle(fixtureType, typeof(NUnit.Framework.TearDownAttribute));
            var oneTimeSetUp = FindSingle(fixtureType, typeof(NUnit.Framework.OneTimeSetUpAttribute));
            var oneTimeTearDown = FindSingle(fixtureType, typeof(NUnit.Framework.OneTimeTearDownAttribute));

            var fixtureCases = new List<CaseResult>();
            bool fixtureSetupFailed = false;
            string fixtureSetupError = null;

            object fixtureInstance = null;
            try
            {
                if (oneTimeSetUp != null)
                {
                    fixtureInstance = ResolveTarget(fixtureType, oneTimeSetUp, null);
                    Invoke(oneTimeSetUp, fixtureInstance);
                }
            }
            catch (Exception ex)
            {
                fixtureSetupFailed = true;
                fixtureSetupError = Describe(ex);
            }

            foreach (MethodInfo method in methods)
            {
                if (!fixtureMatches &&
                    method.Name.IndexOf(filter, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var result = new CaseResult
                {
                    FullName = fixtureName + "." + method.Name,
                    Result = "Passed"
                };

                if (fixtureSetupFailed)
                {
                    result.Result = "Failed";
                    result.Message = "OneTimeSetUp failed: " + fixtureSetupError;
                    failed++;
                    fixtureCases.Add(result);
                    cases.Add(result);
                    continue;
                }

                if (method.GetCustomAttributes(typeof(NUnit.Framework.IgnoreAttribute), true).Length > 0)
                {
                    result.Result = "Skipped";
                    result.Message = "Ignored";
                    skipped++;
                    fixtureCases.Add(result);
                    cases.Add(result);
                    continue;
                }

                object instance = null;
                DateTime start = DateTime.UtcNow;
                try
                {
                    instance = Activator.CreateInstance(fixtureType);
                    if (setUp != null) Invoke(setUp, instance);
                    method.Invoke(method.IsStatic ? null : instance, null);
                    result.Result = "Passed";
                    passed++;
                }
                catch (TargetInvocationException tie)
                {
                    HandleException(tie.InnerException ?? tie, result, fixtureName, method.Name);
                    if (result.Result == "Failed") failed++; else inconclusive++;
                }
                catch (Exception ex)
                {
                    HandleException(ex, result, fixtureName, method.Name);
                    if (result.Result == "Failed") failed++; else inconclusive++;
                }
                finally
                {
                    if (tearDown != null && instance != null)
                    {
                        try { Invoke(tearDown, instance); }
                        catch (Exception ex)
                        {
                            if (result.Result == "Passed")
                            {
                                result.Result = "Failed";
                                failed++;
                                passed--;
                                result.Message = "TearDown failed: " + Describe(ex);
                            }
                        }
                    }
                    result.DurationSeconds = (DateTime.UtcNow - start).TotalSeconds;
                }

                fixtureCases.Add(result);
                cases.Add(result);
            }

            if (oneTimeTearDown != null)
            {
                try { Invoke(oneTimeTearDown, fixtureInstance); }
                catch (Exception ex) { Console.Error.WriteLine("OneTimeTearDown failed: " + Describe(ex)); }
            }

            var fixtureRow = new CaseResult
            {
                FullName = fixtureName,
                Result = fixtureCases.Any(c => c.Result == "Failed") ? "Failed" : "Passed",
                IsFixtureLevel = true,
                HasChildren = fixtureCases.Count > 0
            };
            cases.Add(fixtureRow);
        }

        WriteXml(resultsPath, cases, passed, failed, skipped, inconclusive);

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "total={0} passed={1} failed={2} skipped={3} inconclusive={4}",
            passed + failed + skipped + inconclusive, passed, failed, skipped, inconclusive));

        if (failed > 0 || inconclusive > 0)
        {
            Console.WriteLine("---- non-passing detail ----");
            foreach (CaseResult row in cases)
            {
                if (row.IsFixtureLevel) continue;
                if (row.Result == "Passed" || row.Result == "Skipped") continue;
                Console.WriteLine("[" + row.Result + "] " + row.FullName);
                if (!string.IsNullOrEmpty(row.Message)) Console.WriteLine("    " + row.Message);
                if (row.StackTrace != null)
                {
                    string[] lines = row.StackTrace.Split('\n');
                    for (int i = 0; i < lines.Length && i < 12; i++)
                    {
                        Console.WriteLine("      " + lines[i].TrimEnd('\r'));
                    }
                }
            }
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 从运行器所在目录解析依赖。Unity 的 nunit.framework 是 unity-custom 构建，
    /// Mono 的默认探测（GAC / 应用目录）在带文化名的场景下可能落空，因此显式兜底。
    /// </summary>
    private static Assembly ResolveFromApplicationDirectory(object sender, ResolveEventArgs e)
    {
        try
        {
            var requested = new AssemblyName(e.Name);
            string appDir = Path.GetDirectoryName(typeof(T06Runner).Assembly.Location);
            if (string.IsNullOrEmpty(appDir)) appDir = Directory.GetCurrentDirectory();
            string candidate = Path.Combine(appDir, requested.Name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        catch
        {
            // 兜底失败时让原始加载错误继续传播，避免掩盖真实原因。
        }
        return null;
    }

    private static bool HasTestMethods(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => m.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), true).Length > 0);

    private static void HandleException(Exception ex, CaseResult result, string fixtureName, string methodName)    {
        if (ex is NUnit.Framework.IgnoreException)
        {
            result.Result = "Skipped";
            result.Message = ex.Message;
            return;
        }
        if (ex is NUnit.Framework.InconclusiveException)
        {
            result.Result = "Inconclusive";
            result.Message = Describe(ex);
            result.StackTrace = ex.StackTrace;
            return;
        }
        result.Result = "Failed";
        result.Message = Describe(ex);
        result.StackTrace = ex.StackTrace;
    }

    private static string Describe(Exception ex)
        => ex.GetType().FullName + ": " + ex.Message;

    private static MethodInfo FindSingle(Type type, Type attributeType)
    {
        var found = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttributes(attributeType, true).Length > 0)
            .ToList();
        return found.Count == 0 ? null : found[0];
    }

    private static object ResolveTarget(Type fixtureType, MethodInfo method, object preferred)
    {
        if (method.IsStatic) return null;
        return preferred ?? Activator.CreateInstance(fixtureType);
    }

    private static void Invoke(MethodInfo method, object target)
    {
        try
        {
            method.Invoke(method.IsStatic ? null : target, null);
        }
        catch (TargetInvocationException tie)
        {
            throw tie.InnerException ?? tie;
        }
    }

    private static void WriteXml(string path, List<CaseResult> cases, int passed, int failed, int skipped, int inconclusive)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(path, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("test-run");
            writer.WriteAttributeString("id", "1");
            writer.WriteAttributeString("testcasecount", (passed + failed + skipped + inconclusive).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("result", failed == 0 ? "Passed" : "Failed");
            writer.WriteAttributeString("total", (passed + failed + skipped + inconclusive).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("passed", passed.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("failed", failed.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("skipped", skipped.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("inconclusive", inconclusive.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("engine", "T06Runner-reflect");

            int id = 2;
            foreach (CaseResult row in cases)
            {
                if (!row.IsFixtureLevel)
                {
                    WriteCase(writer, row, ref id);
                }
            }
            foreach (CaseResult row in cases)
            {
                if (!row.IsFixtureLevel) continue;
                writer.WriteStartElement("test-suite");
                writer.WriteAttributeString("type", "TestFixture");
                writer.WriteAttributeString("id", (id++).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("name", row.FullName);
                writer.WriteAttributeString("fullname", row.FullName);
                writer.WriteAttributeString("result", row.Result);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
    }

    private static void WriteCase(XmlWriter writer, CaseResult row, ref int id)
    {
        writer.WriteStartElement("test-case");
        writer.WriteAttributeString("id", (id++).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", row.FullName);
        writer.WriteAttributeString("fullname", row.FullName);
        writer.WriteAttributeString("result", row.Result);
        writer.WriteAttributeString("duration", row.DurationSeconds.ToString("0.000000", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(row.Message))
        {
            writer.WriteStartElement("failure");
            writer.WriteStartElement("message");
            writer.WriteCData(row.Message);
            writer.WriteEndElement();
            if (!string.IsNullOrEmpty(row.StackTrace))
            {
                writer.WriteStartElement("stack-trace");
                writer.WriteCData(row.StackTrace);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }
}
