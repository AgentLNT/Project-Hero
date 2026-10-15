"""Validate the saved Shadow split evidence without rerunning Unity tests."""
from pathlib import Path
import hashlib
import json

folder = Path(__file__).resolve().parent
root = folder.parents[2]


def read(name):
    return json.loads((folder / name).read_text(encoding="utf-8-sig"))


files = [
    "assembly-isolation.json", "driver.json", "ownership.json", "shadow-mirror.json",
    "lifecycle.json", "M1-hud.json", "M2-hud.json", "dynamic-replay.json",
    "production-scene.json", "diagnostic-failure.json",
]
rows, names = [], set()
for name in files:
    obj = read(name)
    if "data" in obj:
        assert obj["success"], name
        result = obj["data"]["result"]
        summary, results = result["Summary"], result["Results"]
        total, passed, failed = summary["Total"], summary["Passed"], summary["Failed"]
    else:
        assert obj["status"] == "completed", name
        summary, results = obj["summary"], obj["results"]
        total, passed, failed = summary["total"], summary["passed"], summary["failed"]
    assert total == passed == len(results) and total > 0 and failed == 0, name
    for result in results:
        assert result["Status"] == "Passed", (name, result)
        assert result["FullName"] not in names, result["FullName"]
        names.add(result["FullName"])
    rows.append(dict(artifact=name, total=total, passed=passed, failed=failed))

compilation = read("development-player-compilation.json")
assert compilation["success"] and compilation["development"]
assert "ProjectHero.UnityView.dll" in compilation["assemblies"]
assert not any("Migration.Diagnostics" in a for a in compilation["assemblies"])
metadata = read("development-player-metadata.json")
assert len(metadata) == 3
assert all(not row["diagnosticTypes"] for row in metadata)
assert all("ProjectHero.Migration.Diagnostics" not in row["references"] for row in metadata)
assert next(row for row in metadata if row["file"] == "ProjectHero.UnityView.dll")["developmentValidatorPresent"]

scenes = read("scene-audit.json")
assert len(scenes) == 3 and all(s["missingScripts"] == 0 for s in scenes)
for scene in scenes:
    expected = "BattleRuntimeBootstrap" if scene["path"].startswith("Assets/Scenes/") else "MigrationBattleHost"
    assert scene["hosts"] == ["ProjectHero.Core.Compatibility.Runtime." + expected], scene

sources = read("source-manifest.json")
for source in sources:
    actual = hashlib.sha256((root / source["path"]).read_bytes()).hexdigest()
    assert source["sha256"] == actual, source["path"]
identity = read("asset-identity.json")
assert len(identity) == 12 and all(row["guidPreserved"] for row in identity)
for row in identity:
    assert "guid: " + row["guid"] in (root / row["to"]).read_text(encoding="utf-8-sig")

report = dict(
    success=True, targetedTests=sum(r["total"] for r in rows), uniqueTests=len(names), runs=rows,
    developmentPlayerScriptsCompiled=True, playerMetadataNoMigrationDependency=True,
    missingScriptsInAuditedScenes=0, sourceFilesMatchedToTestManifest=len(sources),
    movedScriptGuidsPreserved=len(identity), fullPackagedPlayerExecuted=False,
)
(folder / "verification-summary.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(report, ensure_ascii=False, indent=2))
