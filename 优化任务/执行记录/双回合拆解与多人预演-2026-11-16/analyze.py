"""Aggregate matched short battles using each actor's own second-window deadline."""
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path


ROOT = Path(__file__).resolve().parent
FOLDERS = {"scout": 96, "scout-fixed": 96, "multiplayer": 28, "multiplayer-fixed": 28, "confirmed": 432}
LAYOUTS = ("Near", "Main", "Far")


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def summarize(row):
    counts = Counter(cases=1, failed=row.get("Failed", False))
    if row.get("Failed"):
        return counts
    actors = row["Actors"]
    assert len(actors) == 2 * row["Scenario"]["TeamSize"]
    any_lethal = any(a["Lethal"] for a in actors)
    counts.update(completed=1, ended=row["Ended"], lethal=any_lethal, both=row["Ended"] and any_lethal,
        noWindowDeaths=row["DeathsBeforeAnyWindow"], anyDeath=row["FirstDeath"] is not None,
        lethalActors=sum(a["Lethal"] for a in actors), totalActors=len(actors),
        survivorsUnlethalAfterOwnTwo=sum(not a["Lethal"] and a["Deadline"] is not None and a["Deadline"] <= row["Ticks"]
            and (a["Death"] is None or a["Death"] >= a["Deadline"]) for a in actors),
        rejections=row["Rejections"])
    return counts


def run(partial=False):
    results = {}
    for folder, expected in FOLDERS.items():
        files = sorted((ROOT / folder).glob("*.json"))
        if not partial:
            assert len(files) == expected, (folder, len(files))
        models, matchups, by_layout = defaultdict(Counter), defaultdict(dict), defaultdict(Counter)
        errors, failures = {}, defaultdict(list)
        for path in files:
            row = read(path)
            case = row["Scenario"]
            hp, act, move = (case["Model"][field] for field in ("Health", "ActionSpeed", "MoveSpeed"))
            players = case["TeamSize"]
            speed = f"{act}/{move}"
            key = f"{hp}HP/{speed}/{players}v{players}"
            counts = summarize(row)
            models[key].update(counts)
            by_layout[key + "/" + LAYOUTS[case["Layout"]]].update(counts)
            if row.get("Failed"):
                errors[path.stem] = row["Error"].splitlines()[0]
            elif not counts["both"]:
                failures[key].append(path.stem)
            matchup = (hp, players, case["Layout"], case["Mirror"], case["Policy"], case.get("Opponent"))
            assert speed not in matchups[matchup]
            matchups[matchup][speed] = counts
        changes = defaultdict(Counter)
        for matchup, balance in matchups.items():
            if "20/20" not in balance:
                continue
            before = balance["20/20"]
            for speed, after in balance.items():
                if speed == "20/20":
                    continue
                counts = changes[f"{matchup[0]}HP/{speed}"]
                counts["pairs"] += 1
                counts["bothPass"] += before["both"] and after["both"]
                counts["onlyBase"] += before["both"] and not after["both"]
                counts["onlyFaster"] += after["both"] and not before["both"]
        results[folder] = dict(models=models, byLayout=by_layout, errors=errors,
            pairedAgainst20A20M=changes, failedTempoCases=failures)
        print(folder, len(files), "errors", len(errors))
        for key, counts in sorted(models.items()):
            print(key, "pass", counts["both"], "/", counts["cases"], "first lethal", counts["lethal"],
                "before first turn deaths", counts["noWindowDeaths"], "errors", counts["failed"])
    if not partial:
        (ROOT / "comparison.json").write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    run(partial="--partial" in sys.argv)
