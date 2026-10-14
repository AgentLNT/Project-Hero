using System.Collections.Generic;
using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Movement
{
    /// <summary>
    /// 闪避条件预检（只读）：若该闪避实际换位，依赖旧起点的哪些未来 Editable Move 会失效。
    /// 失效只终止计划并清理路径与空间预留；普通动作没有时间费用（R1），因此不存在退款。
    /// </summary>
    public sealed record DodgeCancellationPreview(string RejectionCode, IReadOnlyList<ActionPlanId> AffectedPlanIds);
}
