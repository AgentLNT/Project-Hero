using System;
using ProjectHero.Authoring.Legacy;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Factions;
using ProjectHero.Logic.Grid;
using ProjectHero.Logic.Ids;
using UnityEngine;

namespace ProjectHero.Authoring
{
    [CreateAssetMenu(menuName = "Project Hero/Battle Unit")]
    public sealed class BattleUnitProfile : ScriptableObject
    {
        public string DefinitionId = "unit.wall.stone";
        public UnitKind Kind = UnitKind.Wall;
        public UnitControl Control = UnitControl.Passive;
        public string VolumeId = "unit_volume.hex.radius_1";
        public string ActionSetId = LegacyIdMigrationManifest.ActionSetRadius1;
        [Min(0.01f)] public float HitPoints = 5000f;
        [Min(0.01f)] public float ImpactResistance = 5000f;
        [Min(0)] public float ActionSpeed = 20f;
        [Min(0)] public float MoveSpeed = 20f;

        public UnitDefinition BuildDefinition()
        {
            return new UnitDefinition(new UnitDefinitionId(DefinitionId), 1f, 1f, ActionSpeed, MoveSpeed,
                null, HitPoints,
                Control == UnitControl.Passive ? default : new ActionSetId(ActionSetId), new VolumeSpecId(VolumeId))
            {
                Traits = new UnitTraits(Kind, Control),
                ScalarControlResistanceUnits = MomentumQuantizer.QuantizeLegacyMomentumUnits(ImpactResistance)
            };
        }
    }

    public sealed class BattleUnitPlacement
    {
        public BattleUnitPlacement(EncounterSlotId slotId, UnitDefinition unit, GridPoint position,
            GridDirection facing, FactionId factionId = default, ControllerId controllerId = default)
        {
            SlotId = slotId;
            Unit = unit;
            Position = position;
            Facing = facing;
            FactionId = factionId;
            ControllerId = controllerId;
        }

        public EncounterSlotId SlotId { get; }
        public UnitDefinition Unit { get; }
        public GridPoint Position { get; }
        public GridDirection Facing { get; }
        public FactionId FactionId { get; }
        public ControllerId ControllerId { get; }
    }

    public enum BattleUnitController { None, Player, EnemyAi }
    public enum BattleUnitFaction { Hero, Monster }

    [Serializable]
    public sealed class BattleUnitScenePlacement
    {
        public string SlotId;
        public BattleUnitProfile Profile;
        public Vector2Int Position;
        public GridDirection Facing = GridDirection.East;
        public BattleUnitFaction Faction;
        public BattleUnitController Controller;

        public BattleUnitPlacement Read()
            => new(new EncounterSlotId(SlotId), Profile == null ? null : Profile.BuildDefinition(),
                new GridPoint(Position.x, Position.y), Facing,
                Faction == BattleUnitFaction.Monster ? FactionIds.Monster : FactionIds.Hero,
                Controller switch
                {
                    BattleUnitController.Player => new ControllerId(LegacyIdMigrationManifest.ControllerPlayer),
                    BattleUnitController.EnemyAi => new ControllerId(LegacyIdMigrationManifest.ControllerEnemyAi),
                    _ => default
                });
    }
}
