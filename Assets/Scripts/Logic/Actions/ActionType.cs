namespace ProjectHero.Logic.Actions
{
    /// <summary>
    /// 动作族。ActionType、Timing 与 Payload 必须使用闭合匹配矩阵
    /// （Attack↔AttackTimingSpec+AttackPayloadSpec 等），不能把攻击时序隐式套用到其他动作。
    /// 注：旧 ActionType 没有 Guard（旧系统只有 Block/Dodge 反应与 Move），
    /// Guard 是新模型的窗口型普通防御，插入在 Move 之后；数值在 02B 前冻结。
    /// </summary>
    public enum ActionType
    {
        None = 0,
        Attack = 1,
        Block = 2,
        Dodge = 3,
        Move = 4,
        Guard = 5,
        Cast = 6,
        Item = 7
    }
}
