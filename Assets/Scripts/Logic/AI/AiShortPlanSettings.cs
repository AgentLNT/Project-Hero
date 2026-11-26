using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.AI
{
    /// <summary>Bounded move-then-attack proposals. Only the first command is committed; the next
    /// decision replans from public facts. Values are immutable replay/configuration inputs.</summary>
    public sealed record AiShortPlanSettings(int MaxMoveEdges=4,int GoalPositionsPerAttack=2,
        int MaxGoalPathProbes=24,int PressureValueQ10=65536,int ApproachValue=16384,bool ConsiderOrdinaryEvasion=false)
    {
        public const string Version="short-plan-public-kernel-v1";
        public string Validate()=>MaxMoveEdges<1 || MaxMoveEdges>12 || GoalPositionsPerAttack<1 || GoalPositionsPerAttack>4
            || MaxGoalPathProbes<GoalPositionsPerAttack || MaxGoalPathProbes>64 || PressureValueQ10<0 || PressureValueQ10>1048576
            || ApproachValue<0 || ApproachValue>1048576 ? "AI_SHORT_PLAN_SETTINGS_INVALID" : null;
        public void WriteHashComponents(CanonicalHashWriter w)
        {
            w.Write("ai.short_plan.version",Version);w.Write("ai.short_plan.move_edges",MaxMoveEdges);
            w.Write("ai.short_plan.goal_positions",GoalPositionsPerAttack);w.Write("ai.short_plan.path_probes",MaxGoalPathProbes);
            w.Write("ai.short_plan.pressure_q10",PressureValueQ10);w.Write("ai.short_plan.approach_value",ApproachValue);
            w.Write("ai.short_plan.grouping","owner-template-attack-mean-exp-v1");
            if(ConsiderOrdinaryEvasion)w.Write("ai.short_plan.ordinary_evasion","two-reachable-escapes-public-window-v1");
        }
    }
}
