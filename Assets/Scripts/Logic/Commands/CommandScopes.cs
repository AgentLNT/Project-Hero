using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Commands
{
    /// <summary>
    /// 命令 scope 判别标签（主方案 2.2 / 任务 03「核心契约」）。
    ///
    /// 三类作用域互斥且与载荷判别一一对应：
    /// <list type="bullet">
    /// <item><see cref="ScheduleEdit"/>：普通计划的 Add/Move/Remove；必须携带
    /// <c>ExpectedScheduleRevision</c>（与 Step 冻结的 BatchBase 比较），新增/增加预算时窗口必填。</item>
    /// <item><see cref="Window"/>：关窗/并发行动激活；必须携带 <c>ExpectedWindowId</c>，
    /// 不允许按"当前窗口"猜。</item>
    /// <item><see cref="Reaction"/>：Block/Dodge；必须携带本次反应的 <c>ReactionOpportunityId</c>，
    /// 不允许按来源攻击临时查找。</item>
    /// </list>
    /// scope 与 payload 判别不匹配必须稳定拒绝，不得回退到当前窗口或临时查找机会。
    /// </summary>
    public enum CommandScopeKind
    {
        ScheduleEdit = 0,
        Window = 1,
        Reaction = 2
    }

    /// <summary>
    /// 窗口作用域：关窗与并发行动激活都必须指名目标窗口，不允许隐式"当前窗口"。
    /// 它与任务 02 冻结的 <see cref="ScheduleEditScope"/> / <see cref="ReactionCommandScope"/>
    /// 同属 <see cref="CommandScope"/> 判别联合（不另立第二套 scope 体系）。
    /// </summary>
    public sealed record WindowCommandScope(WindowId ExpectedWindowId) : CommandScope;

    /// <summary>
    /// 判别联合的判别函数与规范描述。scope 值本身不携带 ControllerId、来源种类、优先级、
    /// 生产者序号、CommandSequence、规则费用或反应 TriggerTick——这些字段在类型上就不存在。
    /// </summary>
    public static class CommandScopes
    {
        /// <summary>判别 scope 种类；未知类型以稳定原因码拒绝，绝不猜测。</summary>
        public static CommandScopeKind KindOf(CommandScope scope)
        {
            switch (scope)
            {
                case ScheduleEditScope _: return CommandScopeKind.ScheduleEdit;
                case WindowCommandScope _: return CommandScopeKind.Window;
                case ReactionCommandScope _: return CommandScopeKind.Reaction;
                case null:
                    throw new ProjectHero.Logic.LogicDefinitionException(
                        CommandCodes.COMMAND_SCOPE_NULL, "scope is null");
                default:
                    throw new ProjectHero.Logic.LogicDefinitionException(
                        CommandCodes.SCOPE_PAYLOAD_MISMATCH, "unknown scope type: " + scope.GetType().Name);
            }
        }

        /// <summary>规范描述（诊断/事件文本；不构成玩法数据）。</summary>
        public static string Describe(CommandScope scope)
        {
            switch (scope)
            {
                case ScheduleEditScope schedule:
                    return "ScheduleEdit(rev=" + schedule.ExpectedScheduleRevision.ToString(
                               System.Globalization.CultureInfo.InvariantCulture) + ")";
                case WindowCommandScope window:
                    return "Window(" + window.ExpectedWindowId.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + ")";
                case ReactionCommandScope reaction:
                    return "Reaction(" + reaction.ReactionOpportunityId.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + ")";
                default:
                    return scope == null ? "<null>" : scope.GetType().Name;
            }
        }
    }
}
