import csv
import gzip
import json
import re
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path


ROOT = Path(__file__).resolve().parent
COSTS = {"Z": {2: 2, 3: 1}, "G": {2: 3, 3: 1}, "D": {2: 2, 3: 2}, "J": {2: 3, 3: 2}}
MATCH = ("heroPolicy", "enemyPolicy", "layout", "mirror", "seed", "metaResource", "maxTicks",
         "sourceHash", "sourceFingerprint", "contentFingerprint", "temperature", "topP")


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def rows(path):
    opener = gzip.open if str(path).endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8-sig", newline="") as stream:
        yield from csv.DictReader(stream)


def number(event, key):
    return int(re.search(r"\b" + key + r"=(-?\d+);", event)[1])


def nested_id(event, key):
    return int(re.search(r"\b" + key + r"=[^{]+\{[^}]*Value=(\d+);", event)[1])


def inspect(path, stage):
    stem = str(path).removesuffix(".input.json")
    summary = next(rows(stem + ".summary.csv"))
    ended = summary["ended"] == "True"
    ticks = int(summary["ticks"])
    hp = [max(0, int(summary[k])) for k in ("heroHealthQ10", "enemyHealthQ10")]
    counts = Counter(cases=1, ended=ended, timeouts=not ended, ticks=ticks, hits=int(summary["hits"]),
        effectiveLossQ10=409600-sum(hp), attackStarts=int(summary["attackStarts"]), moveStarts=int(summary["movesStarted"]),
        rejected=int(summary["rejections"]), endedBothDealtDamage=ended and max(hp)<204800)
    windows, damage, total_by_pair = defaultdict(list), [], Counter()
    deaths, created, reservations, defenses = [], {}, {}, {}
    fallback_stopped = set()
    raw_total, positives = 0, 0
    for row in rows(stem + ".events.csv.gz"):
        event, tick = row["event"], int(row["tick"])
        kind = event.split("{", 1)[0].rsplit(".", 1)[-1]
        if kind == "TurnWindowOpenedEvent":
            actor = nested_id(event, "OwnerUnitId")
            duration = number(event, "DurationTicks")
            assert duration == 180
            windows[actor].append((tick, tick + duration))
        elif kind == "DamageChannelResolvedEvent":
            amount = number(event, "AfterBlockDamageQ10")
            attacker, target = nested_id(event, "AttackerUnitId"), nested_id(event, "TargetUnitId")
            raw_total += amount
            if amount > 0:
                positives += 1
                damage.append((tick, attacker, target, amount))
                total_by_pair[attacker, target] += amount
        elif kind == "UnitDiedEvent":
            deaths.append((tick, nested_id(event, "UnitId")))
        elif kind == "ActionPlanCreatedEvent":
            plan = number(event, "ActionPlanId")
            if number(event, "Origin") == 1:
                defenses[plan] = number(event, "ActionType")
            else:
                created[tick, number(event, "OwnerUnitId")] = plan
        elif kind == "ActionPlanTerminatedEvent" and "ACTION_TERMINATION_ACTOR_UNAVAILABLE_AT_START" in event:
            fallback_stopped.add(number(event, "ActionPlanId"))
        elif kind == "AdrenalineLedgerChangedEvent":
            kind_id = int(re.search(r"\bChangeKind=[^;]+:(\d+);", event)[1])
            if kind_id == 1:
                p = number(event, "ReservationPlanId")
                amount, before = number(event, "ReservationAmount"), number(event, "AvailableBefore")
                assert before >= amount and before - amount == number(event, "AvailableAfter")
                reservations[p] = amount
            elif kind_id in (2, 3):
                p = number(event, "ReservationPlanId")
                assert number(event, "ReservationAmount") == reservations[p]
                counts["reactionConsumed" if kind_id == 2 else "reactionRefunded"] += 1
        elif kind == "ReactionTriggeredEvent":
            counts["triggered:" + str(number(event, "ReactionType"))] += 1
    assert positives == counts["hits"]
    assert raw_total == int(summary["healthLossQ10"])
    assert ended == bool(deaths)
    assert len(defenses) == len(reservations)
    for plan, action in defenses.items():
        assert reservations[plan] == COSTS[stage][action]
        counts[f"reserved:{action}"] += 1
    for row in rows(stem + ".candidates.csv.gz"):
        if row["chosen"] == "True" and row["reason"] == "SELECTED_LEGAL_FALLBACK":
            key = int(row["tick"]) + 1, int(row["key"].split("/")[1])
            counts["fallbackUnavailableAtStart"] += created.get(key) in fallback_stopped

    actors = set(windows) | {u for _, u in deaths}
    assert len(actors) == 2
    two_windows = {}
    for actor in actors:
        deadline = windows[actor][1][1] if len(windows[actor]) >= 2 else None
        observed_until = deadline if deadline is not None else ticks
        per_target = Counter()
        for tick, attacker, target, amount in damage:
            if attacker == actor and tick < observed_until:
                per_target[target] += amount
        max_damage = max((min(204800, q) for q in per_target.values()), default=0)
        two_windows[actor] = dict(deadline=deadline, damageToOneTargetQ10=max_damage,
            lethal=max_damage >= 204800, secondWindowClosed=deadline is not None and deadline <= ticks)
    counts["unitsLethalByOwnDeadline"] = sum(x["lethal"] for x in two_windows.values())
    counts["casesWithLethalByOwnDeadline"] = bool(counts["unitsLethalByOwnDeadline"])
    counts["firstDeathBeforeBothDeadlines"] = bool(deaths and deaths[0][0] < 720)
    return dict(counts=counts, twoWindows=two_windows, firstDeath=deaths[0][0] if deaths else None,
        layout=summary["layout"], pair=summary["pair"])


def run(partial=False):
    files = sorted((ROOT / "formal/cases").glob("*.input.json"))
    by_pair, stages, by_layout = defaultdict(dict), defaultdict(Counter), defaultdict(Counter)
    for path in files:
        source = load(path)
        stage, key = source["stage"], path.name[1:].removesuffix(".input.json")
        case = inspect(path, stage)
        case["input"] = source
        by_pair[key][stage] = case
    if not partial:
        assert len(by_pair) == 54
        assert all(set(group) == set(COSTS) for group in by_pair.values())
    for group in by_pair.values():
        base = group["Z"]["input"]
        hashes = set()
        for stage, case in group.items():
            source = case["input"]
            assert all(source[k] == base[k] for k in MATCH)
            hashes.add(source["definitionHash"])
            stages[stage].update(case["counts"])
            by_layout[stage + "/" + case["layout"]].update(case["counts"])
        assert len(hashes) == len(group)
    paired = defaultdict(Counter)
    for group in by_pair.values():
        a = group["Z"]["counts"]
        for stage, info in group.items():
            if stage == "Z":
                continue
            b = info["counts"]
            delta = b["effectiveLossQ10"] - a["effectiveLossQ10"]
            paired[stage]["moreLoss" if delta > 0 else "lessLoss" if delta < 0 else "equalLoss"] += 1
            paired[stage]["onlyRevisedEnded"] += b["ended"] and not a["ended"]
            paired[stage]["onlyControlEnded"] += a["ended"] and not b["ended"]
    tempo = {}
    for stage in stages:
        units = [u["damageToOneTargetQ10"] / 1024 for group in by_pair.values()
                 if stage in group for u in group[stage]["twoWindows"].values()]
        tempo[stage] = dict(units=len(units), medianDamageHp=statistics.median(units), maxDamageHp=max(units),
            noDamage=sum(v == 0 for v in units))
    result = dict(stages=stages, paired=paired, tempo=tempo, byLayout=by_layout, cases=by_pair)
    if not partial:
        (ROOT / "comparison.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("stages", "paired", "tempo")}, indent=2))


if __name__ == "__main__":
    run(partial="--partial" in sys.argv)
