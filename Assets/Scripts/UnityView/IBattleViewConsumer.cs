using ProjectHero.Logic.Events;
using ProjectHero.Logic.Initialization;
using ProjectHero.Logic.Snapshots;

namespace ProjectHero.Core.Compatibility.Runtime
{
    public interface IBattleViewConsumer
    {
        void Bind(BattleInitializationResult initialization, LogicSnapshot snapshot);
        void Consume(EventBatch events, LogicSnapshot snapshot);
    }
}
