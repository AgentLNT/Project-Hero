using ProjectHero.Logic.Combat;

namespace ProjectHero.Logic.Definitions
{
    /// <summary>
    /// 命令来源类型（主方案 2.2 / 00 号规则 18）。
    /// 它由已注册入口绑定，命令生产者不得自行声明；后续命令网关按它派生优先级。
    ///
    /// <see cref="System"/> 代表外部引擎/工具来源。它必须被 <c>ControllerBinding</c> 拒绝：
    /// 任何可被外部伪造的来源都不能出现在战斗定义里，否则调用方就能伪装成系统来源
    /// 绕过优先级与校验。
    /// </summary>
    public enum CommandSourceKind
    {
        Player = 0,
        Ai = 1,
        System = 2
    }

    /// <summary>
    /// 控制者绑定：一个稳定 <see cref="ControllerId"/> 控制一组固定出场槽位。
    /// 只定义"控制权"，不定义阵营、不定义胜负、不定义目标资格——
    /// FactionId 与 Controller 正交，ControllerBinding 与阵营分组<strong>不要求同构</strong>。
    /// </summary>
    public sealed record ControllerBinding(
        Ids.ControllerId ControllerId,
        CommandSourceKind SourceKind,
        System.Collections.Generic.IReadOnlyList<Ids.EncounterSlotId> ControlledSlots,
        AI.AiPolicyKind AiPolicy = AI.AiPolicyKind.Legacy,
        AI.AiExecutionKind AiExecution = AI.AiExecutionKind.SingleCommand,
        AI.AiForecastSettings AiForecast = null)
    {
        public void WriteHashComponents(CanonicalHashWriter writer)
        {
            writer.Write("controller_binding.controller_id", ControllerId.Value ?? string.Empty);
            writer.Write("controller_binding.source_kind", SourceKind.ToString() + ":" + (int)SourceKind);
            // Legacy definitions retain their exact encoding. Opt-in policies are immutable replay inputs.
            if (AiPolicy != AI.AiPolicyKind.Legacy)
            {
                writer.Write("controller_binding.ai_policy_version", AI.AiTacticalPolicy.Version);
                writer.Write("controller_binding.ai_policy", (int)AiPolicy);
            }
            if (AiExecution != AI.AiExecutionKind.SingleCommand)
            {
                writer.Write("controller_binding.ai_execution_version", AI.AiBehaviorTree.Version);
                writer.Write("controller_binding.ai_execution", (int)AiExecution);
            }
            AiForecast?.WriteHashComponents(writer);
            if (ControlledSlots == null) return;
            foreach (var slot in ControlledSlots)
            {
                writer.Write("controller_binding.slot", slot.Value ?? string.Empty);
            }
        }
    }
}
