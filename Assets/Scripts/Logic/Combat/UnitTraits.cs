using System;
using ProjectHero.Logic.Definitions;

namespace ProjectHero.Logic.Combat
{
    public enum UnitKind { Combatant, Object, Wall }
    public enum UnitControl { PlayerOrAi, PlayerOnly, AiOnly, Passive }

    /// <summary>Definition-level traits. The encounter assigns the actual controller and faction.</summary>
    public readonly struct UnitTraits : IEquatable<UnitTraits>
    {
        public UnitTraits(UnitKind kind, UnitControl control)
        {
            Kind = kind;
            Control = control;
        }

        public UnitKind Kind { get; }
        public UnitControl Control { get; }

        public bool IsCombatant => Kind == UnitKind.Combatant;

        public bool AllowsController(CommandSourceKind kind) => Control switch
        {
            UnitControl.PlayerOrAi => kind is CommandSourceKind.Player or CommandSourceKind.Ai,
            UnitControl.PlayerOnly => kind == CommandSourceKind.Player,
            UnitControl.AiOnly => kind == CommandSourceKind.Ai,
            _ => false
        };

        internal bool IsValid => Enum.IsDefined(typeof(UnitKind), Kind)
            && Enum.IsDefined(typeof(UnitControl), Control)
            && (IsCombatant || Control == UnitControl.Passive);

        public bool Equals(UnitTraits other)
            => Kind == other.Kind && Control == other.Control;

        public override bool Equals(object obj) => obj is UnitTraits other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Kind, Control);
        public static bool operator ==(UnitTraits left, UnitTraits right) => left.Equals(right);
        public static bool operator !=(UnitTraits left, UnitTraits right) => !left.Equals(right);
    }
}
