using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Logic.AI
{
    /// <summary>AI 普通动作候选的种类（与产出的 <c>ScheduleEditCommand</c> 载荷一一对应）。</summary>
    public enum AiCandidateKind
    {
        /// <summary>新增普通计划（Add）。</summary>
        Add = 0,

        /// <summary>重排自己仍为 Editable 的普通计划（Move）。</summary>
        Move = 1,

        /// <summary>删除自己仍为 Editable 的普通计划（Remove）。</summary>
        Remove = 2
    }

    /// <summary>
    /// 候选被排除／放行的稳定原因（诊断；<strong>不</strong>替代命令层的拒绝码）。
    ///
    /// 它是"AI 侧预筛"的结果，永远不是授权：即使这里给出 <see cref="Eligible"/>，
    /// 命令层仍会重新校验控制权、修订号、窗口、机会截止与目的格。
    /// </summary>
    public enum AiCandidateEligibility
    {
        Eligible = 0,

        /// <summary>该 ActionSpec 的 <c>AllowedTargetRelations</c> 不放行该候选
        /// （命令层的稳定码 = <c>TARGET_RELATION_NOT_ALLOWED</c>，两者同源）。</summary>
        TargetRelationNotAllowed = 1,

        /// <summary>该 ActionSpec 的关系掩码本身非法，或它不是携带掩码的攻击载荷。</summary>
        SpecHasNoTargetRelationMask = 2,

        /// <summary>本 Tick 该控制者在目标 Tick 上没有仍接收提交的窗口。</summary>
        NoSubmissionWindow = 3,

        /// <summary>没有权威路径重算端口（<c>Move</c> 候选 fail-closed，绝不用距离/速度估算路线）。</summary>
        PathCalculatorUnavailable = 4,

        /// <summary>权威路径重算明确失败（无路/越界/上限/溢出，或该计划不参与重算）。</summary>
        PathRejected = 5
    }

    /// <summary>
    /// 反应选项上的<strong>目标格资格</strong>（任务 09 产出 9/10：Dodge 目的格只能来自 Logic 公布面）。
    /// 它<strong>不</strong>是"命令层已经校验过"的证明——真正的目的格校验仍由
    /// <c>ReactionPlanner</c> → 机会系统 → 任务 06 目的格预留重新执行一次。
    /// </summary>
    public enum AiReactionDestinationEligibility
    {
        /// <summary>该选项不使用目的格（Block）。</summary>
        NotApplicable = 0,

        /// <summary>已在 Logic 公布的目的格集合中。</summary>
        Eligible = 1,

        /// <summary>不在公布集合中（AI 不得据此创建目的格）。</summary>
        Contradicted = 2
    }
}
