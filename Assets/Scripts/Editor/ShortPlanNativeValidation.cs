using System;
using System.IO;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Experiments;

namespace ProjectHero.Editor
{
    public static class ShortPlanNativeValidation
    {
        public static void Run()
        {
            const string root="优化任务/执行记录/短组合规划-2026-11-13/native-mechanisms";Directory.CreateDirectory(root);
            foreach(AiPolicySituation situation in Enum.GetValues(typeof(AiPolicySituation)))
                foreach(var policy in new[]{AiPolicyKind.SustainedOffense,AiPolicyKind.SeekInterrupt,AiPolicyKind.CautiousDefense})
                    foreach(int distance in situation==AiPolicySituation.Approach?new[]{14,18}:new[]{14})
                    {
                        var input=new AiPolicyExperimentInput(policy,situation,MaxTicks:480,ApproachDistance:distance,
                            Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings());
                        var result=AiPolicyExperiment.Run(input);AiPolicyExperiment.VerifyReplay(result);
                        File.WriteAllLines(root+"/"+situation+"-"+policy+"-d"+distance+".canonical.txt",AiPressureEvidence.Canonical(result));
                    }
            UnityEngine.Debug.Log("SHORT_PLAN_NATIVE_MECHANISMS_PASS cases=27");
            string prior=Environment.GetEnvironmentVariable("HERO23_BATCH");
            try{Environment.SetEnvironmentVariable("HERO23_BATCH","unity-reference");ShortPlanMatchupTools.Pilot();}
            finally{Environment.SetEnvironmentVariable("HERO23_BATCH",prior);}
        }
    }
}
