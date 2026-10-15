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
    public interface IBattleInputConsumer
    {
        string PlayerControllerId { get; }
        void BindInput(Input.ViewInputPorts ports);
    }
    public interface IBattleViewLifetimeConsumer
    {
        void ReleaseVisuals();
    }
    public interface IBattleDiagnosticFrameConsumer
    {
        void AdvanceDiagnostics(double unscaledSeconds);
    }
}
