using System;
using System.Globalization;
using System.IO;
using System.Linq;
using ProjectHero.Logic.Combat;
using ProjectHero.Logic.Experiments;
using ProjectHero.Logic.Replay;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProjectHero.Editor
{
    /// <summary>Read-only developer replay viewer; always rebuilds from Tick 0.</summary>
    public sealed class CombatExperimentWindow : EditorWindow
    {
        private ReplayPlayer _player;
        private BattleReplay _replay;
        private Label _status;
        private TextField _facts;
        private double _lastTime;
        private double _accumulator;
        [MenuItem("Project Hero/Combat Experiments/Open Replay Viewer")]
        public static void ShowWindow()=>GetWindow<CombatExperimentWindow>("Combat experiments");
        public void CreateGUI()
        {
            var row=new VisualElement(); row.style.flexDirection=FlexDirection.Row;
            row.Add(new Button(Open){text="Open replay"});
            row.Add(new Button(()=>{if(_player!=null){if(_player.IsPaused)_player.Play();else _player.Pause();}}){text="Play / Pause"});
            row.Add(new Button(()=>{_player?.Restart();Render();}){text="Restart"});
            row.Add(new Button(()=>{if(_player!=null){_player.Play();_player.AdvanceOneTick();_player.Pause();Render();}}){text="Step"});
            rootVisualElement.Add(row);
            _status=new Label("Choose a .heroReplay with its .input.json beside it.");_status.style.whiteSpace=WhiteSpace.Normal;
            rootVisualElement.Add(_status);
            _facts=new TextField{multiline=true,isReadOnly=true};_facts.style.flexGrow=1;
            rootVisualElement.Add(_facts);
        }
        private void OnEnable(){EditorApplication.update+=Advance;_lastTime=EditorApplication.timeSinceStartup;}
        private void OnDisable(){EditorApplication.update-=Advance;_player?.Dispose();_player=null;}
        private void Open()
        {
            string path=EditorUtility.OpenFilePanel("Open combat experiment replay",CombatExperimentTools.EvidenceRoot,"heroReplay");
            if(string.IsNullOrEmpty(path))return;
            try
            {
                string inputText=File.ReadAllText(Path.ChangeExtension(path,".input.json"));
                var recovery=JsonUtility.FromJson<RecoveryExperimentInput>(inputText);
                ProjectHero.Logic.Definitions.BattleDefinition definition;
                if (recovery.kind == "recovery-scan" && recovery.scenarioVersion == RecoveryExperimentScenario.Version)
                    definition=recovery.Definition();
                else
                {
                var input=JsonUtility.FromJson<CombatExperimentInput>(inputText);
                if(input.numericalVersion!=CombatValuesV05.Version || input.scenarioVersion!=CombatExperimentScenario.ScenarioVersion
                    || input.scriptVersion!=CombatExperimentScenario.ScriptVersion)throw new InvalidOperationException("Experiment input version mismatch.");
                definition=CombatExperimentDefinition.Build(input.Scenario());
                if(definition.BattleDefinitionHashValue!=input.definitionHash)throw new InvalidOperationException("Experiment definition hash mismatch.");
                }
                BattleReplay replay;
                using(var stream=File.OpenRead(path))replay=ReplayFile.Load(stream,definition);
                var player=new ReplayPlayer(definition);
                try{player.Load(replay);}catch{player.Dispose();throw;}
                _player?.Dispose();_player=player;_replay=replay;_accumulator=0;Render();
            }
            catch(Exception error){EditorUtility.DisplayDialog("Cannot open replay",error.Message,"OK");}
        }
        private void Advance()
        {
            double now=EditorApplication.timeSinceStartup,elapsed=now-_lastTime;_lastTime=now;
            if(_player==null||_player.IsPaused||_player.IsComplete||_player.Deviation!=null)return;
            _accumulator+=Math.Min(elapsed,0.25)*60;
            while(_accumulator>=1&&!_player.IsComplete&&_player.Deviation==null){_player.AdvanceOneTick();_accumulator--;}
            Render();
        }
        private void Render()
        {
            if(_player==null||_status==null)return;
            var snapshot=_player.CurrentSnapshot;
            _status.text="Tick "+(snapshot?.Tick??-1)+" / "+(_replay.Records.Count-1)+"   "+(_player.IsComplete?"Complete":_player.IsPaused?"Paused":"Playing")
                +(_player.Deviation==null?"":"   DEVIATION: "+_player.Deviation);
            if(snapshot==null)return;
            string units=string.Join("\n",snapshot.Units.Select(u=>"Unit "+u.UnitId+"  HP "+(u.HealthQ10/1024d).ToString("0.###",CultureInfo.InvariantCulture)
                +"  Cell ("+u.X+","+u.Y+")  State "+u.StateValue+"  Adrenaline "+(u.AvailableAdrenaline/(double)CombatValuesV05.ResourceUnitsPerPoint).ToString("0.####",CultureInfo.InvariantCulture)));
            units+="\n"+string.Join("\n",snapshot.Resources.AdrenalineLedgers.Select(l=>"Unit "+l.UnitId+" reserved "
                +(l.ReservedTotal/(double)CombatValuesV05.ResourceUnitsPerPoint).ToString("0.####",CultureInfo.InvariantCulture)+"  Cycle "+l.CycleId));
            string facts=snapshot.Tick<0?"":string.Join("\n",_replay.Records[(int)snapshot.Tick].Events.Select(ReplayEventComparison.Canonical));
            _facts.SetValueWithoutNotify(units+"\n\nEvents at this Tick\n"+facts);
        }
    }
}
