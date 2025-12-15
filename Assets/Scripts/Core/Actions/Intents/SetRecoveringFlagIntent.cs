using ProjectHero.Core.Entities;
using ProjectHero.Core.Interactions;

namespace ProjectHero.Core.Actions.Intents
{
    /// <summary>
    /// Sets CombatUnit.IsRecoveringAction on the timeline.
    /// Kept as ActionType.None so it does not participate in arbiter interactions.
    /// </summary>
    public sealed class SetRecoveringFlagIntent : CombatIntent
    {
        private readonly bool _value;

        public SetRecoveringFlagIntent(CombatUnit owner, bool value) : base(owner, ActionType.None)
        {
            _value = value;
        }

        public override void ExecuteSuccess()
        {
            if (Owner == null) return;
            Owner.IsRecoveringAction = _value;
        }
    }
}
