using System;
using ProjectHero.Authoring;
using ProjectHero.Authoring.Compatibility;
using ProjectHero.Core.Compatibility.Runtime;
using ProjectHero.Logic;
using ProjectHero.Logic.Ids;
using ProjectHero.Logic.Initialization;
using UnityEngine;

namespace ProjectHero.Core.Compatibility.Authoring
{
    /// <summary>Neutral authoring host. Uses authored values and shared configuration assets, never Legacy units or clocks.</summary>
    public sealed class NewBattleSimulationSource : MonoBehaviour, IBattleSimulationSource
    {
        [Serializable] private sealed class UnitStats
        {
            public float strength, dexterity, constitution, armorWeight, armorDefense, magicResistance;
            public bool isExhausted;
            public UnitStats(LegacyUnitStatInputs value)
            {
                strength = value.Strength; dexterity = value.Dexterity; constitution = value.Constitution;
                armorWeight = value.ArmorWeight; armorDefense = value.ArmorDefense;
                magicResistance = value.MagicResistance; isExhausted = value.IsExhausted;
            }
            public LegacyUnitStatInputs Read() => new LegacyUnitStatInputs(strength, dexterity, constitution,
                armorWeight, armorDefense, magicResistance, isExhausted);
        }
        [SerializeField] private UnitStats _heroStats;
        [SerializeField] private UnitStats _enemyStats;
        [SerializeField] private long _initialRngSeed;
        [SerializeField] private int _initialMetaResource;
        private BattleSimulationSeed _seed;
        private string _error;
        private bool _built;
        public string SourceName => nameof(NewBattleSimulationSource);
        public string LastConfigurationError { get { Build(); return _error; } }

        public void Configure(LegacyUnitStatInputs hero, LegacyUnitStatInputs enemy, ulong rngSeed, int metaResource)
        {
            if (_built) throw new InvalidOperationException("NEW_SOURCE_ALREADY_BUILT");
            _heroStats = new UnitStats(hero); _enemyStats = new UnitStats(enemy);
            _initialRngSeed = unchecked((long)rngSeed); _initialMetaResource = metaResource;
        }
        public BattleSimulationSeed BuildSeed() { Build(); return _seed; }
        private void Build()
        {
            if (_built) return;
            _built = true;
            if (_heroStats == null || _enemyStats == null) { _error = "NEW_SOURCE_AUTHORED_STATS_MISSING"; return; }
            var inputs = new BattleRuntimeInputs(unchecked((ulong)_initialRngSeed), _initialMetaResource);
            _error = inputs.Validate(); if (_error != null) return;
            try
            {
                var result = BattleDefinitionBuilder.BuildMainBattleDefinition(new LegacyBattleUnitSource(
                    _heroStats.Read(), _enemyStats.Read(), "Explicit authored unit values; no Legacy runtime reads"));
                if (!result.Succeeded) { _error = "NEW_SOURCE_DEFINITION_INVALID|" + string.Join(",", result.ErrorCodes()); return; }
                _seed = new BattleSimulationSeed(result.Definition,
                    new EncounterDefinitionId(ProjectHero.Authoring.Legacy.LegacyIdMigrationManifest.MainEncounterId),
                    inputs, "Independent New authoring; explicit hero/enemy stat values");
                _error = _seed.Validate();
            }
            catch (Exception error) { _error = "NEW_SOURCE_BUILD_FAILED|" + error.GetType().Name + "|" + error.Message; }
        }
    }
}
