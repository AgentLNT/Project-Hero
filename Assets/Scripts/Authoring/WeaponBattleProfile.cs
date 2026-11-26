using System;
using ProjectHero.Logic.Actions;
using ProjectHero.Logic.Combat;
using UnityEngine;

namespace ProjectHero.Authoring
{
    [CreateAssetMenu(menuName = "Project Hero/Battle Weapon")]
    public sealed class WeaponBattleProfile : ScriptableObject
    {
        [Serializable]
        public sealed class Technique
        {
            public string ActionId;
            public PhysicalRecipe Recipe;
            [Min(0)] public int BaseStrengthQ10;
            public AttackControlEffects Controls;
            [Range(-11, 11)] public int DisplacementDirectionOffset;
        }

        public AttackGrowthAttribute GrowthAttribute;
        public Technique[] Techniques = Array.Empty<Technique>();
    }
}
