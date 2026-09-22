using ProjectHero.Logic.Ids;

namespace ProjectHero.Logic.Actions
{
    /// <summary>
    /// 纯逻辑不可变动作定义（主方案 3.1.2）。由 Authoring 层从 ScriptableObject 转换而来。
    /// AdrenalineCost：Attack/Guard/Move 必须为 0；Block/Dodge 必须为正（01B 拍板：Block=2、Dodge=1）。
    /// </summary>
    public sealed record ActionSpec(
        ActionSpecId ActionSpecId,
        ActionType Type,
        ActionTimingSpec Timing,
        ActionPayloadSpec Payload,
        int AdrenalineCost);
}
