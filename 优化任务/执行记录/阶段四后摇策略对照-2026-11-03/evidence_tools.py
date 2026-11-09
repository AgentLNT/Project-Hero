"""Version and compare native evidence; never simulates combat."""
from pathlib import Path
import argparse
import csv
import gzip
import hashlib
import json
import zipfile
import xml.etree.ElementTree as ET
from collections import Counter

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]
V2 = ROOT / "corrected-preview-v2"

def sha(data):
    return hashlib.sha256(data).hexdigest()

def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))

def rows(path):
    with path.open(encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))

def fingerprint(files):
    return sha("".join(f"{p}:{h}\n" for p, h in files.items()).encode())

def freeze():
    verdict = ET.parse(V2 / "full-editmode.xml").getroot()
    assert verdict.attrib["result"] == "Passed" and int(verdict.attrib["passed"]) == 1099
    old = read(ROOT / "frozen-existing-logic.json")
    changed = [p for p, h in old.items() if sha((REPO / p).read_bytes()) != h]
    assert set(changed) == {"Assets/Scripts/Logic/AI/AiTacticalPolicy.cs", "Assets/Scripts/Logic/Snapshots/AiForecastWorld.cs"}, changed
    logic = {p.relative_to(REPO).as_posix(): sha(p.read_bytes()) for p in sorted((REPO / "Assets/Scripts/Logic").rglob("*.cs"))}
    write(V2 / "frozen-existing-logic.json", logic)
    sources = sorted([*(REPO / "Assets/Scripts").rglob("*.cs"), *(REPO / "Assets/Tests").rglob("*.cs")], key=lambda p: p.relative_to(REPO).as_posix())
    hashes = {p.relative_to(REPO).as_posix(): sha(p.read_bytes()) for p in sources}
    archive = V2 / "source-archive.zip"
    assert not archive.exists(), "Never overwrite a sealed source archive"
    files = set(sources)
    files.update(p.with_name(p.name + ".meta") for p in sources)
    for folder in ("Assets/Scripts", "Assets/Tests"):
        for p in (REPO / folder).rglob("*.asmdef"):
            files.update((p, p.with_name(p.name + ".meta")))
    files.update(REPO / p for p in ("Packages/manifest.json", "Packages/packages-lock.json", "ProjectSettings/ProjectVersion.txt"))
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as z:
        for p in sorted(files):
            assert p.is_file(), p
            z.write(p, p.relative_to(REPO).as_posix())
    provenance = dict(policyVersion="tactical-public-kernel-v2", cases=96, coreTicks=[30,31,32,39,40,41],
                      sourceFingerprint=fingerprint(hashes), frozenLogicFingerprint=fingerprint(logic),
                      sourceFiles=len(hashes), logicFiles=len(logic), archiveFiles=len(files),
                      modifiedExistingRuntimePaths=changed, fullEditModePassed=1099,
                      scope="Preview ingress correction only; actual combat rules and policy weights unchanged")
    write(V2 / "provenance.json", provenance)
    print(json.dumps(provenance, ensure_ascii=False, indent=2))

def unavailable(root, case):
    return sum(p["terminalReason"] == "ACTION_TERMINATION_ACTOR_UNAVAILABLE_AT_START" and p["type"] == "1"
               for p in rows(root / "cases" / (case + ".plans.csv")))

def compare():
    old = {r["case"]: r for r in rows(ROOT / "summary.csv")}
    new = rows(V2 / "summary.csv")
    assert len(new) == 96
    comparison = []
    ignored = {"sourceFingerprint", "frozenLogicFingerprint", "definitionHash"}
    for b in new:
        a = old[b["case"]]
        for key in ("block", "layout", "recovery", "bluntPolicy", "opponentPolicy", "opponentFirst", "swapSpawns", "seed", "windowTicks", "maxTicks"):
            assert a[key] == b[key], (b["case"], key)
        changed = [key for key in a if key not in ignored and a[key] != b[key]]
        comparison.append(dict(case=b["case"], layout=b["layout"], recovery=b["recovery"],
            outcomeV1=a["outcome"], outcomeV2=b["outcome"], endTickV1=a["endTick"], endTickV2=b["endTick"],
            aHealthV1=a["a_HealthQ10"], aHealthV2=b["a_HealthQ10"], bHealthV1=a["b_HealthQ10"], bHealthV2=b["b_HealthQ10"],
            unavailableV1=unavailable(ROOT, b["case"]), unavailableV2=unavailable(V2, b["case"]), changedMetrics=";".join(changed)))
    with (V2 / "v1-vs-v2.csv").open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(comparison[0])); writer.writeheader(); writer.writerows(comparison)
    total_old = sum(unavailable(ROOT, case) for case in old)
    stats = dict(cases=96, ticks=sum(int(b["endTick"])+1 for b in new),
        outcomes=dict(Counter(b["outcome"] for b in new)), outcomeChanges=sum(c["outcomeV1"] != c["outcomeV2"] for c in comparison),
        healthChanges=sum(c["aHealthV1"] != c["aHealthV2"] or c["bHealthV1"] != c["bHealthV2"] for c in comparison),
        endTickChanges=sum(c["endTickV1"] != c["endTickV2"] for c in comparison),
        commandRejections=sum(int(b["commandRejections"]) for b in new),
        actorUnavailableBefore=total_old, actorUnavailableCovered=sum(c["unavailableV1"] for c in comparison),
        actorUnavailableAfter=sum(c["unavailableV2"] for c in comparison),
        changedMetricCases=sum(bool(c["changedMetrics"]) for c in comparison))
    stats["actualAttackStartsDelta"] = sum(int(b[f"{side}_AttackStarts"]) - int(old[b["case"]][f"{side}_AttackStarts"]) for b in new for side in ("a", "b"))
    stats["actualAttackImpactsDelta"] = sum(int(b[f"{side}_AttackImpacts"]) - int(old[b["case"]][f"{side}_AttackImpacts"]) for b in new for side in ("a", "b"))
    stats["postClashRecoveryHits"] = sum(int(b[f"{side}_PostClashRecoveryHits"]) for b in new for side in ("a", "b"))
    stats["postClashRecoveryLossQ10"] = sum(int(b[f"{side}_PostClashRecoveryLossQ10"]) for b in new for side in ("a", "b"))
    assert stats["actorUnavailableCovered"] == total_old, "Regression matrix must cover every observed start-gate failure"
    case = "HeavyQuickWall-CautiousDefense-SustainedOffense-firstA-swap0-seed31-w180-r31"
    proof = dict(case=case, tick=222)
    for label, folder in (("v1", ROOT), ("v2", V2)):
        dto = read(folder / "cases" / (case + ".input.json"))
        with gzip.open(folder / "cases" / (case + ".candidates.csv.gz"), "rt", encoding="utf-8-sig", newline="") as f:
            candidate, = [r for r in csv.DictReader(f) if r["tick"] == "222" and "/action.experiment.quick/" in r["key"]]
        proof[label] = dict(policyVersion=dto["policyVersion"], sourceFingerprint=dto["sourceFingerprint"], candidate=candidate)
    assert proof["v1"]["candidate"]["damageDeltaQ10"] == "4096" and proof["v1"]["candidate"]["chosen"] == "True"
    assert proof["v2"]["candidate"]["damageDeltaQ10"] == "0" and proof["v2"]["candidate"]["chosen"] == "False"
    write(V2 / "counterexample-after-fix.json", proof)
    write(V2 / "comparison.json", stats)
    print(json.dumps(stats, ensure_ascii=False, indent=2))

def restore():
    baseline = read(ROOT / "pre-validation-baseline.json")
    historical = {REPO / p for p in baseline}
    for folder in ("优化任务/执行记录/M1-regression", "优化任务/执行记录/拼刀后摇纠偏-2026-10-29", "优化任务/执行记录/规则AI阶段三-2026-11-03"):
        historical.update(p for p in (REPO / folder).rglob("*") if p.is_file())
    changed = [p for p in historical if p.is_file() and sha(p.read_bytes()) != baseline.get(p.relative_to(REPO).as_posix())]
    archive = ROOT / "validation-regenerated-files.zip"
    assert not archive.exists(), "Never overwrite the saved validation output"
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as z:
        for p in sorted(changed): z.write(p, p.relative_to(REPO).as_posix())
    with zipfile.ZipFile(ROOT / "pre-validation-baseline.zip") as z:
        for name, expected in baseline.items():
            target = (REPO / name).resolve()
            assert target.is_relative_to(REPO.resolve())
            data = z.read(name); assert sha(data) == expected
            target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
    assert all(sha((REPO / p).read_bytes()) == h for p,h in baseline.items())
    write(ROOT / "baseline-restoration.json", dict(verifiedOriginalFiles=len(baseline), archivedRegeneratedFiles=len(changed),
        newHistoricalFilesPreserved=[p.relative_to(REPO).as_posix() for p in changed if p.relative_to(REPO).as_posix() not in baseline],
        originalBytesRestored=True))
    print(f"Restored and verified {len(baseline)} original files; archived {len(changed)} regenerated files")

def seal():
    for root, count in ((ROOT, 304), (V2, 96)):
        summary = rows(root / "summary.csv")
        assert len(summary) == count
        audit = (root / "native-disk-replay-audit.txt").read_text(encoding="utf-8-sig")
        ticks = sum(int(r["endTick"])+1 for r in summary)
        assert f"cases={count}; ticks={ticks};" in audit
        provenance = read(root / "analysis.json")
        assert provenance["recordedTicks"] == ticks
        test = ET.parse(root / "full-editmode.xml").getroot()
        assert test.attrib["result"] == "Passed" and int(test.attrib["passed"]) == (1096 if root == ROOT else 1099)
        assert not list((root / "cases").glob("*.failure.txt"))
        with zipfile.ZipFile(root / "source-archive.zip") as z:
            source_files = sorted(p for p in z.namelist() if p.endswith(".cs") and p.startswith(("Assets/Scripts/", "Assets/Tests/")))
            assert fingerprint({p:sha(z.read(p)) for p in source_files}) == provenance["sourceFingerprint"]
            for p,h in read(root / "frozen-existing-logic.json").items(): assert sha(z.read(p)) == h
        files = [p for p in (root / "cases").rglob("*") if p.is_file()]
        files += [root / name for name in ("source-archive.zip", "matrix.csv", "frozen-existing-logic.json", "native-disk-replay-audit.txt", "full-editmode.xml")]
        if root == V2:
            play = ET.parse(root / "production-shadow-playmode.xml").getroot()
            assert play.attrib["result"] == "Passed" and int(play.attrib["passed"]) == 14
            files += [root / name for name in ("production-shadow-playmode.xml", "before-fix.xml", "after-fix.xml", "counterexample-after-fix.json", "comparison.json")]
        manifest = {p.relative_to(root).as_posix():sha(p.read_bytes()) for p in sorted(files)}
        write(root / "sealed-evidence-manifest.json", dict(cases=count, ticks=ticks, files=manifest))
        print(f"Sealed {count} cases / {ticks} ticks / {len(manifest)} files: {root.name}")
    baseline = read(ROOT / "pre-validation-baseline.json")
    assert all(sha((REPO / p).read_bytes()) == h for p,h in baseline.items())
    live = {p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in sorted(
        [*(REPO / "Assets/Scripts").rglob("*.cs"), *(REPO / "Assets/Tests").rglob("*.cs")], key=lambda p:p.relative_to(REPO).as_posix())}
    assert fingerprint(live) == read(V2 / "provenance.json")["sourceFingerprint"]
    print(f"Current V2 source and all {len(baseline)} preserved original files verified")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("action", choices=("freeze", "compare", "restore", "seal"))
    globals()[parser.parse_args().action]()
