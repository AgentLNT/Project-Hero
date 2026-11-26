using System;
using ProjectHero.Editor;

static class Program
{
    static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("HERO25_BATCH", Environment.GetEnvironmentVariable("HERO25_BATCH") ?? "formal");
        Environment.SetEnvironmentVariable("HERO25_STAGES", Environment.GetEnvironmentVariable("HERO25_STAGES") ?? "Z,G,D,J");
        switch (args[0])
        {
            case "run": ContinuationMatchupTools.Run(); return 0;
            case "audit": ContinuationMatchupTools.Audit(); return 0;
            default: throw new ArgumentException("Expected run or audit");
        }
    }
}