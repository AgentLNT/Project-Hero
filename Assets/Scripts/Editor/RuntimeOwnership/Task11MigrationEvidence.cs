using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using ProjectHero.Core.Compatibility.Runtime;
using UnityEngine;

namespace ProjectHero.Editor.RuntimeOwnership
{
    public static class Task11MigrationEvidence
    {
        [Serializable] private sealed class Manifest
        {
            public string decision, caseId, rulesVersion, definitionHash, editResults, playResults, editSha256, playSha256;
            public SourceHash[] sourceHashes; public Entry[] entries;
        }
        [Serializable] private sealed class SourceHash { public string path, sha256; }
        [Serializable] private sealed class Entry { public string id, category, reason; public string[] executedCases; }
        public static IReadOnlyList<ShadowMigrationObligation> Load(string caseId, string rules, string definitionHash)
        {
            const string root = "优化任务/执行记录/";
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(root + "11-shadow-migration-evidence.json"));
            if (manifest.decision != "accepted-2026-10-10-rule-obligations-performance-deferred" || manifest.caseId != caseId
                || manifest.rulesVersion != rules || manifest.definitionHash != definitionHash || manifest.entries?.Length != 60)
                throw new InvalidOperationException("MIGRATION_EVIDENCE_SCOPE_INVALID");
            var files = Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("Assets/Scripts", "*.asmdef", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("Assets/Scripts", "*.xml", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("Assets/Tests", "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("Assets/Tests", "*.asmdef", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("Assets/Tests", "*.xml", SearchOption.AllDirectories))
                .Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (manifest.sourceHashes == null || manifest.sourceHashes.Length != files.Length)
                throw new InvalidOperationException("MIGRATION_EVIDENCE_SOURCE_SET_CHANGED");
            var hashes = manifest.sourceHashes.ToDictionary(h => h.path, h => h.sha256, StringComparer.Ordinal);
            foreach (var file in files)
                if (!hashes.TryGetValue(file, out string expected) || Hash(file) != expected)
                    throw new InvalidOperationException("MIGRATION_EVIDENCE_SOURCE_CHANGED|" + file);
            var passed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in new[] { (manifest.editResults, manifest.editSha256), (manifest.playResults, manifest.playSha256) })
            {
                if (Path.GetFileName(pair.Item1) != pair.Item1 || Hash(root + pair.Item1) != pair.Item2)
                    throw new InvalidOperationException("MIGRATION_EVIDENCE_RESULTS_CHANGED");
                var doc = new XmlDocument(); doc.Load(root + pair.Item1);
                foreach (XmlElement test in doc.SelectNodes("//test-case"))
                {
                    if (test.GetAttribute("result") != "Passed") throw new InvalidOperationException("MIGRATION_EVIDENCE_NONPASSING_CASE");
                    passed.Add(test.GetAttribute("fullname"));
                }
            }
            var result = new List<ShadowMigrationObligation>();
            foreach (var entry in manifest.entries)
            {
                if (entry.executedCases == null || entry.executedCases.Length == 0 || entry.executedCases.Any(c => !passed.Contains(c)))
                    throw new InvalidOperationException("MIGRATION_EVIDENCE_CASE_NOT_PASSED|" + entry.id);
                result.Add(new ShadowMigrationObligation(entry.id, entry.category, caseId, rules, definitionHash, entry.reason, entry.executedCases));
            }
            var policy = ShadowCasePolicy.ResolveMigrationObligations(caseId, rules, definitionHash, result);
            if (policy.Rejections.Count != 0 || policy.TemporarilyUncomparable.Count != 0)
                throw new InvalidOperationException("MIGRATION_EVIDENCE_INCOMPLETE");
            return result.AsReadOnly();
        }
        private static string Hash(string path)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    }
}
