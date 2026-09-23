namespace ProjectHero.Core.Physics
{
    /// <summary>
    /// 旧伤害/冲击类型枚举。
    ///
    /// 任务 02B 前置拆分（任务 01 记录 §5.2 阻塞点 2）：本枚举原先与运行时 <c>PhysicsEngine</c>
    /// 同文件，而 PhysicsEngine 直接引用 <c>CombatUnit</c>/<c>BattleTimeline</c>/<c>GameFeelManager</c>
    /// 等大量运行时类型，无法随 Authoring 闭包搬迁。因此把枚举拆到独立文件并放入
    /// <c>ProjectHero.Authoring</c>，<c>PhysicsEngine.cs</c> 本身留在 Assembly-CSharp 且保留其
    /// MonoScript GUID（940d2ce0…）。
    ///
    /// 拆分安全性：旧资产中 <c>Action.ImpactType</c> 只以 int 值序列化（YAML <c>ImpactType: 0/1/2</c>），
    /// 没有任何资产或场景保存过 ImpactType 的 MonoScript 引用，因此本枚举换文件、换程序集
    /// 不会破坏任何既有序列化数据。
    ///
    /// 首版语义（characterization，任务 01 §6.2）：它同时充当"名义伤害类型"与"动量传递系数来源"，
    /// 伤害侧没有任何分支。迁移时按拍板 B1 与任务 01 §6.3 显式拆为
    /// <c>DamageChannelId</c>（伤害通道）× <c>ImpactProfileId</c>（冲击传递 Profile）两个正交字段；
    /// 数值 0/1/2 与名称不得在迁移中被重新合并或静默改写。
    /// </summary>
    public enum ImpactType
    {
        Blunt = 0,
        Slash = 1,
        Pierce = 2
    }
}
