using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Definitions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Timeline;
using UnityEngine;

namespace ProjectHero.Authoring.Tests.Task05
{
    /// <summary>
    /// 任务 05 收尾 R1（独立验证缺陷 <strong>D2</strong>）的守护用例：
    /// <c>ReactionOpportunityId</c> 必须<strong>只有一个分配器</strong>。
    ///
    /// 缺陷形态（修复前）：生产路径用 <c>ActionScheduleAuthority.TakeReactionOptionSequence()</c> 取号，
    /// 而任务 03 冻结契约指定的 <c>LogicIdGenerator.NextReactionOpportunityId()</c> 仍然存活但
    /// <strong>零调用</strong>（其计数器恒为 1）⇒ 同一 ID 类型存在两个分配器；后续任务按契约调用
    /// 契约分配器会从 1 重发，与已发出 ID 冲突。
    ///
    /// 修复后的口径：<see cref="LogicIdGenerator"/> 是<strong>唯一</strong>分配器；
    /// 机会系统的取号与快照的"下一个机会 ID"都从它派生；
    /// <c>ActionScheduleAuthority.NextReactionOptionSequence</c> 退化为它的<strong>只读投影</strong>
    /// （<c>=&gt;</c> 表达式属性，没有后备字段、没有 <c>++</c>），因此两条路径<strong>不可能</strong>各自取号。
    ///
    /// <strong>本夹具的编写纪律</strong>：不手写任何产品标识符。类型名与分配器名一律从生产源码
    /// <strong>捕获</strong>（<c>Regex</c> 捕获组），样例字符串与判定口径都由捕获结果导出。
    /// 原因见 <c>Task05ReactionOpportunityIdAllocatorTests</c> 的 R1 排查实录：手写字面量拼错
    /// 会让"搜索屏障"误报为"检测器失效"，把一个纯粹的拼写问题伪装成产品缺陷。
    /// </summary>
    [TestFixture]
    public sealed class Task05ReactionOpportunityIdAllocatorTests
    {
        private const string AttackId = "action.t05.attack.long";
        private const string CounterField = "_nextReactionOpportunityId";

        // ————————————————————————————————————————————————————————————
        // ③ 源码级：机会 ID 计数状态与递增点各自唯一
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ReactionOpportunityIdCounterIsIncrementedInExactlyOneMethod</c>：
        /// 全 Logic 程序集源码里，任何"取机会 ID 顺序号"的计数状态都只允许有<strong>一处</strong>递增，
        /// 且必须位于唯一分配器内。
        ///
        /// 判定口径<strong>不靠精确字段名</strong>：修复前的第二个分配器叫
        /// <c>NextReactionOptionSequence</c>（**名字里没有 Id**），只有"类别匹配"才抓得到它。
        /// 这里用"该行含递增算子 + 该行含机会 ID 类型的<strong>捕获名</strong>"作为口径，
        /// 而捕获名来自生产源码本身，因此不依赖任何手写拼写。
        /// </summary>
        [Test]
        public void ReactionOpportunityIdCounterIsIncrementedInExactlyOneMethod()
        {
            string instanceIds = ReadSource("Ids/InstanceIds.cs");
            Assert.That(instanceIds, Is.Not.Null, "必须能读到 Logic 的 InstanceIds.cs");

            string opportunityTypeName = CaptureOpportunityTypeName(instanceIds);

            // 分配器的完整写法也从源码捕获：public <Type> <Method>() => new <Type>(<counter>++)
            Match allocatorMatch = Regex.Match(instanceIds,
                @"public\s+" + Regex.Escape(opportunityTypeName) + @"\s+(\w+)\(\)\s*=>\s*new\s+" +
                Regex.Escape(opportunityTypeName) + @"\(" + Regex.Escape(CounterField) + @"\+\+\)");
            Assert.That(allocatorMatch.Success, Is.True,
                "必须能从源码捕获唯一分配器的完整写法（public " + opportunityTypeName +
                " <Method>() => new " + opportunityTypeName + "(" + CounterField + "++)）");
            string allocatorName = allocatorMatch.Groups[1].Value;
            string allocatorShape = allocatorMatch.Value;

            // —— 搜索屏障：先把"旧缺陷形态"与"合法形态"送进同一个检测器 ——
            // 屏障失败即意味着后面的"恰好 1 处"没有意义，因此放在最前面。
            // 旧计数器名由捕获到的类型名推导（去掉尾部 "Id" 再加 "Sequence"），不手写拼写。
            string oldCounterName = opportunityTypeName.Substring(0, opportunityTypeName.Length - 2) + "Sequence";
            Assert.That(IsOpportunityIdCounterIncrement(
                    "internal long Take" + oldCounterName + "() => Next" + oldCounterName + "++;",
                    opportunityTypeName),
                Is.True, "搜索屏障失效：检测器必须能识别权威侧的旧第二分配器（换名计数器 " + oldCounterName + "）");
            Assert.That(IsOpportunityIdCounterIncrement(
                    "private long next" + opportunityTypeName + " = 1L;", opportunityTypeName),
                Is.False, "搜索屏障失效：纯字段声明不是递增（否则后面的计数没有意义）");
            Assert.That(IsOpportunityIdCounterIncrement(
                    "new " + opportunityTypeName + "(_authority.TakeSomething())", opportunityTypeName),
                Is.False, "搜索屏障失效：不带自增算子的取号写法由另一条用例抓");
            Assert.That(IsOpportunityIdCounterIncrement(allocatorShape, opportunityTypeName),
                Is.True, "搜索屏障失效：唯一分配器自身的合法递增必须被识别：[" + allocatorShape + "]");

            // —— 被扫描源码里，字段声明与值类型声明都必须真实存在 ——
            Assert.That(Regex.Matches(instanceIds, @"\b" + Regex.Escape(CounterField) + @"\b").Count,
                Is.GreaterThanOrEqualTo(1),
                "唯一分配器的计数状态字段必须存在于 LogicIdGenerator 内");
            Assert.That(instanceIds.Contains("public readonly struct " + opportunityTypeName), Is.True,
                "捕获到的值类型必须在源码中真实声明：public readonly struct " + opportunityTypeName);

            string[] logicSources = ReadLogicAssemblySources();
            Assert.That(logicSources.Length, Is.GreaterThan(50),
                "Logic 程序集源码必须以递归扫描方式被读入（防静默空集）");

            var incrementSites = new List<string>();
            for (int i = 0; i < logicSources.Length; i++)
            {
                string name = Path.GetFileName(logicSources[i]);
                string[] lines = File.ReadAllLines(logicSources[i]);
                for (int l = 0; l < lines.Length; l++)
                {
                    if (!IsOpportunityIdCounterIncrement(lines[l], opportunityTypeName)) continue;
                    incrementSites.Add(name + ":" +
                        (l + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + lines[l].Trim());
                }
            }

            Assert.That(incrementSites.Count, Is.EqualTo(1),
                "机会 ID 的计数状态只允许在一处递增（实测：" + string.Join(" | ", incrementSites) + "）");

            string unique = incrementSites[0];
            Assert.That(unique.Contains("InstanceIds.cs"), Is.True,
                "唯一递增点必须位于 LogicIdGenerator 所在的 InstanceIds.cs：" + unique);
            Assert.That(unique.Contains(allocatorName + "()"), Is.True,
                "唯一递增点必须位于唯一分配器 " + allocatorName + "() 内：" + unique);

            // 分配器的书写形式：返回值与自增必须同源（否则返回值与计数器分叉）。
            Assert.That(instanceIds.Contains(allocatorShape), Is.True,
                "唯一分配器必须用同一个状态返回值与自增（否则返回值与计数器分叉）：" + allocatorShape);
        }

        /// <summary>
        /// 判定一行源码是否是"机会 ID 计数状态"的递增/赋值。
        ///
        /// 口径：<list type="bullet">
        /// <item>该行含递增/赋值算子 <c>++</c> 或 <c>+=</c>；</item>
        /// <item>该行含<strong>机会一族</strong>的标识符——即"含 <c>eaction</c> 且含
        /// <c>pportun</c>"（这两个片段都取自捕获到的类型名，不依赖手写拼写）；
        /// 因此同时覆盖 <c>Next…Id</c> 与修复前权威侧的 <c>Next…Sequence</c>
        /// （注意后者**名字里没有 Id**——这正是不能用"字段名 + Id"做扫描的原因）。</item>
        /// </list>
        /// 前缀<strong>不要</strong>加 <c>\b</c>：实测 <c>\b[A-Za-z_]*</c> 前缀会贪婪吃掉标识符中段
        /// 且不回溯，导致合法分配器漏检。
        /// </summary>
        private static bool IsOpportunityIdCounterIncrement(string sourceLine, string opportunityTypeName)
        {
            if (sourceLine == null) return false;
            if (!sourceLine.Contains("++") && !sourceLine.Contains("+=")) return false;

            string family = OpportunityFamilyPattern(opportunityTypeName);
            return Regex.IsMatch(sourceLine, family, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 从捕获到的类型名导出"机会一族"的标识符正则，例如
        /// <c>ReactionOpportunityId</c> ⇒ <c>[A-Za-z_]*eaction[A-Za-z_]*pportun[A-Za-z_]*</c>。
        /// 两个锚点片段都由类型名自身切出，因此<b>不包含任何手写拼写</b>。
        /// </summary>
        private static string OpportunityFamilyPattern(string opportunityTypeName)
        {
            int reactionAnchor = opportunityTypeName.IndexOf("eaction", StringComparison.Ordinal);
            int opportunAnchor = opportunityTypeName.IndexOf("pportun", StringComparison.Ordinal);
            Assert.That(reactionAnchor, Is.GreaterThanOrEqualTo(0),
                "类型名必须含 eaction 锚点：" + opportunityTypeName);
            Assert.That(opportunAnchor, Is.GreaterThan(reactionAnchor),
                "类型名必须含 pportun 锚点且位于 eaction 之后：" + opportunityTypeName);

            return "[A-Za-z_]*" + opportunityTypeName.Substring(reactionAnchor, 7) +
                   "[A-Za-z_]*" + opportunityTypeName.Substring(opportunAnchor, 7) + "[A-Za-z_]*";
        }

        /// <summary>
        /// 从生产源码捕获机会 ID 值类型名。锚点只用"含 <c>eaction</c> 与 <c>pportun</c> 的
        /// <c>readonly struct</c>"，避免手写拼写。
        /// </summary>
        private static string CaptureOpportunityTypeName(string instanceIds)
        {
            MatchCollection structs = Regex.Matches(instanceIds, @"public readonly struct (\w+)");
            for (int i = 0; i < structs.Count; i++)
            {
                string candidate = structs[i].Groups[1].Value;
                if (candidate.Contains("eaction") && candidate.Contains("pportun")) return candidate;
            }

            Assert.Fail("必须能从 InstanceIds.cs 捕获机会 ID 值类型名");
            return null;
        }

        // ————————————————————————————————————————————————————————————
        // ④ 源码级：构造点唯一
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ReactionOpportunityIdIsConstructedOnlyAtTheSingleAllocator</c>：
        /// 全 Logic 程序集源码里，<c>new &lt;机会 ID 类型&gt;(</c> 只允许出现在 <c>InstanceIds.cs</c>
        /// （值类型自身的构造函数声明 + 唯一分配器）。
        /// 修复前机会系统的 <c>new ReactionOpportunityId(_authority.TakeReactionOptionSequence())</c>
        /// 会让本用例失败。
        /// </summary>
        [Test]
        public void ReactionOpportunityIdIsConstructedOnlyAtTheSingleAllocator()
        {
            string instanceIds = ReadSource("Ids/InstanceIds.cs");
            Assert.That(instanceIds, Is.Not.Null, "必须能读到 Logic 的 InstanceIds.cs");
            string opportunityTypeName = CaptureOpportunityTypeName(instanceIds);
            string construction = "new " + opportunityTypeName + "(";

            string[] logicSources = ReadLogicAssemblySources();
            Assert.That(logicSources.Length, Is.GreaterThan(50),
                "Logic 程序集源码必须以递归扫描方式被读入（防静默空集）");

            var offenders = new List<string>();
            for (int i = 0; i < logicSources.Length; i++)
            {
                string name = Path.GetFileName(logicSources[i]);
                if (name == "InstanceIds.cs") continue;
                string[] lines = File.ReadAllLines(logicSources[i]);
                for (int l = 0; l < lines.Length; l++)
                {
                    if (!lines[l].Contains(construction)) continue;
                    // Task11 decodes an existing historical ID, without allocating a new opportunity.
                    // This exact read-only decoder site is allowed; simulation construction sites remain forbidden.
                    if (name == "ReplayFile.cs" && lines[l].Contains(
                        "new ReactionCommandScope(new ReactionOpportunityId(r.ReadInt64()))")) continue;
                    offenders.Add(name + ":" +
                        (l + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            Assert.That(offenders, Is.Empty,
                "机会 ID 只允许由唯一分配器构造；越界构造点：" + string.Join(", ", offenders));
            Assert.That(instanceIds.Contains(construction + CounterField + "++)"), Is.True,
                "唯一分配器必须在新值类型上包装计数器的当前值");
        }

        // ————————————————————————————————————————————————————————————
        // ① 契约级：单场单调、0 无效、不复用
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ContractAllocatorIsMonotonicFromOneWithoutReuse</c>：
        /// 任务 03 契约分配器自身满足"从 1 起、单场单调、0 无效、不复用"。
        /// </summary>
        [Test]
        public void ContractAllocatorIsMonotonicFromOneWithoutReuse()
        {
            var ids = new LogicIdGenerator();
            Assert.That(ids.NextReactionOpportunityIdValue, Is.EqualTo(1L), "首个有效值为 1");

            for (int i = 0; i < 5; i++)
            {
                long issued = ids.NextReactionOpportunityId().Value;
                Assert.That(issued, Is.EqualTo((long)(i + 1)),
                    "单场单调、不跳号、不复用（索引 " +
                    i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）");
                Assert.That(issued, Is.GreaterThan(0L), "发出的 ID 必须有效（非 0）");
            }

            Assert.That(ids.NextReactionOpportunityIdValue, Is.EqualTo(6L), "只读投影跟随唯一分配器");
        }

        // ————————————————————————————————————————————————————————————
        // ⑤ 装配级：缺分配器必须响亮失败（不得静默回落）
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>ReactionOpportunitySystemRequiresTheContractAllocator</c>：
        /// 不注入唯一分配器时机会系统<strong>构造即失败</strong>。
        /// 缺省回落会重新引入"第二份计数状态"这一缺陷形态，因此该形参是必填的。
        /// </summary>
        [Test]
        public void ReactionOpportunitySystemRequiresTheContractAllocator()
        {
            Assert.Throws<ArgumentNullException>(() => new ReactionOpportunitySystem(
                new ActionScheduleAuthority(), null, null, null, null));
        }

        // ————————————————————————————————————————————————————————————
        // ⑥ 端到端级：真实机会系统只经唯一分配器取号，权威镜像不自行取号
        // ————————————————————————————————————————————————————————————

        /// <summary>
        /// <c>OpportunityIdsComeFromTheSingleContractAllocatorAndAuthorityMirrorNeverIssues</c>。
        /// </summary>
        [Test]
        public void OpportunityIdsComeFromTheSingleContractAllocatorAndAuthorityMirrorNeverIssues()
        {
            BattleDefinition definition = Task05Farm.Definition();
            IFactionRelationResolver factions = Task05Farm.Factions();
            var ids = new LogicIdGenerator();
            var authority = new ActionScheduleAuthority();
            authority.IdGenerator = ids;
            var factory = new ActionPlanFactory(definition, factions, Task05Farm.Facts(), ids);
            var system = new ReactionOpportunitySystem(authority, factory, definition, factions, ids);

            ActionPlanCreationResult created = factory.TryCreateOrdinary(
                new OrdinaryPlanRequest(
                    Task05Farm.Hero, new ActionSpecId(AttackId), GridDirection.East,
                    Task05Farm.Enemy, null, null, 100L, 0, 0),
                0L);
            Assert.That(created.Succeeded, Is.True, created.RejectionCode);
            authority.RegisterPlan(created.Plan);
            Task05Scheduler.Lock(created.Plan, 100L);

            var opened = new List<ReactionOpportunityRuntime>();
            string error = system.TryOpenForTelegraph(created.Plan, 100L, opened);
            Assert.That(error, Is.Null, error);
            Assert.That(opened.Count, Is.EqualTo(1), "hero→enemy 单一候选");

            Assert.That(opened[0].Id.Value, Is.EqualTo(1L),
                "机会 ID 必须由唯一分配器从 1 起发出");
            Assert.That(ids.NextReactionOpportunityIdValue, Is.EqualTo(2L),
                "唯一分配器恰好前进一步（机会系统没有旁路它）");
            Assert.That(authority.NextReactionOptionSequence, Is.EqualTo(2L),
                "权威的镜像属性只投影唯一分配器");
            Assert.That(system.BuildSnapshots()[0].ReactionOpportunityId, Is.EqualTo(opened[0].Id.Value));

            // 只读投影是幂等的：反复读取既不推进分配器，也不会"自行取号"。
            for (int i = 0; i < 3; i++)
            {
                Assert.That(authority.NextReactionOptionSequence, Is.EqualTo(2L));
                Assert.That(ids.NextReactionOpportunityIdValue, Is.EqualTo(2L));
            }
        }

        // ————————————————————————————————————————————————————————————
        // 源码读取
        // ————————————————————————————————————————————————————————————

        /// <summary>Logic 程序集根目录（与 asmdef 同级）。</summary>
        private static string LogicDirectory => Path.Combine(Application.dataPath, "Scripts", "Logic");

        /// <summary>读 Logic 程序集内的单个源码文件（不存在返回 null）。</summary>
        private static string ReadSource(string relativePath)
        {
            string path = Path.Combine(LogicDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>递归读入 Logic 程序集全部 <c>.cs</c>。</summary>
        private static string[] ReadLogicAssemblySources()
        {
            Assert.That(Directory.Exists(LogicDirectory), Is.True,
                "Logic 程序集目录必须存在：" + LogicDirectory);
            return Directory.GetFiles(LogicDirectory, "*.cs", SearchOption.AllDirectories);
        }
    }
}
