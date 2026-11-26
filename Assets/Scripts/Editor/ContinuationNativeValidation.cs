using System;
using System.IO;
using ProjectHero.Logic.AI;
using ProjectHero.Logic.Experiments;

namespace ProjectHero.Editor
{
    public static class ContinuationNativeValidation
    {
        public static void RunPressureAblation()
        {
            const string prefix="优化任务/执行记录/施压奖励与移动避让-2026-11-16/";
            if(Directory.Exists(prefix+"unity-reference"))throw new InvalidOperationException("Use a fresh record directory for native validation");
            string previous=Environment.GetEnvironmentVariable("HERO25_BATCH");
            try{Environment.SetEnvironmentVariable("HERO25_BATCH","unity-reference");ContinuationMatchupTools.Pilot();}
            finally{Environment.SetEnvironmentVariable("HERO25_BATCH",previous);}
        }

        public static void Run()
        {
            const string root="优化任务/执行记录/连续施压规划-2026-11-14/native-mechanisms";Directory.CreateDirectory(root);
            foreach(bool plans in new[]{false,true})
                foreach(AiPolicySituation situation in Enum.GetValues(typeof(AiPolicySituation)))
                    foreach(var policy in new[]{AiPolicyKind.SustainedOffense,AiPolicyKind.SeekInterrupt,AiPolicyKind.CautiousDefense})
                        foreach(int distance in situation==AiPolicySituation.Approach?new[]{14,18}:new[]{14})
                        {
                            var input=new AiPolicyExperimentInput(policy,situation,MaxTicks:480,ApproachDistance:distance,
                                Sampling:new AiSamplingSettings(SignedDamage:true,HealthDefense:true),ShortPlans:new AiShortPlanSettings(),Continuations:new AiContinuationSettings(plans));
                            var result=AiPolicyExperiment.Run(input);AiPolicyExperiment.VerifyReplay(result);
                            File.WriteAllLines(root+"/"+(plans?"C":"V")+"-"+situation+"-"+policy+"-d"+distance+".canonical.txt",AiPressureEvidence.Canonical(result));
                        }
            UnityEngine.Debug.Log("CONTINUATION_NATIVE_MECHANISMS_PASS cases=54");
            string prior=Environment.GetEnvironmentVariable("HERO24_BATCH");
            try{Environment.SetEnvironmentVariable("HERO24_BATCH","unity-reference");ContinuationMatchupTools.Pilot();}
            finally{Environment.SetEnvironmentVariable("HERO24_BATCH",prior);}
        }
    }
}
