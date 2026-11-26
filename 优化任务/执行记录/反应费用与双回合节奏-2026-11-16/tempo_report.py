import json
import sys
from collections import Counter, defaultdict
from pathlib import Path


ROOT = Path(__file__).resolve().parent
FOLDERS = {"tempo-cases": 80, "tempo-confirm": 54, "tempo-expanded": 116}
LAYOUTS = {0: "Near", 1: "Main", 2: "Far"}


def run(partial=False):
    output = {}
    for folder, expected in FOLDERS.items():
        files = sorted((ROOT / folder).glob("*.json"))
        if not partial:
            assert len(files) == expected, (folder, len(files))
        models = defaultdict(Counter)
        layouts = defaultdict(Counter)
        show_errors = []
        for path in files:
            row = json.loads(path.read_text(encoding="utf-8-sig"))
            scenario = row["Scenario"]
            hp, speed, move = (scenario["Model"][k] for k in ("Health", "ActionSpeed", "MoveSpeed"))
            sizes = scenario["TeamSize"]
            key = f"{hp}HP/{speed}A/{move}M/{sizes}v{sizes}"
            totals = Counter(cases=1, failed=row.get("Failed", False))
            if row.get("Failed"):
                show_errors.append(dict(case=path.stem, message=row["Error"].splitlines()[0]))
            else:
                actors = row["Actors"]
                assert len(actors) == 2 * sizes
                totals.update(valid=1, battleEnded=row["Ended"], anyDeath=row["FirstDeath"] is not None,
                    anySingleActorLethal=any(x["Lethal"] for x in actors),
                    actorsLethal=sum(x["Lethal"] for x in actors), actors=len(actors),
                    survivorsWithTwoWindows=sum(x["Death"] is None and x["Deadline"] is not None
                        and x["Deadline"] <= row["Ticks"] for x in actors),
                    deathsWithoutOwnWindow=row["DeathsBeforeAnyWindow"], rejections=row["Rejections"])
            models[key].update(totals)
            layouts[(key, LAYOUTS[scenario["Layout"]])].update(totals)
        output[folder] = dict(cases=len(files), groups=models,
            byLayout={f"{k}/{l}": v for (k, l), v in layouts.items()}, errors=show_errors)
    if not partial:
        (ROOT / "tempo-comparison.json").write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for folder, report in output.items():
        print(folder, report["cases"], json.dumps(report["groups"], ensure_ascii=False))
        print("Failures", len(report["errors"]))


if __name__ == "__main__":
    run(partial="--partial" in sys.argv)
