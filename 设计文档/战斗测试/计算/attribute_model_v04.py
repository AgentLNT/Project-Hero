"""Candidate attribute arithmetic, not a Unity battle or balance simulation.

Inputs are extra ranks above rank 1. This isolates equal-budget builds, control
thresholds, and attack starts within a 180-Tick window. No production data changes.
"""

from fractions import Fraction as F
from pathlib import Path
import json


ATTACKS = {
    "quick": {"damage": F(4), "impact": F(3), "primary": "agility"},
    "heavy": {"damage": F(6), "impact": F(10), "primary": "strength"},
    "sweep": {"damage": F(3), "impact": F(2), "primary": "agility"},
}


def ceil_fraction(value):
    return -(-value.numerator // value.denominator)


def round_half_up(value):
    return (2 * value.numerator + value.denominator) // (2 * value.denominator)


def derive(strength, agility, constitution, can_balance=False):
    stats = {"strength": strength, "agility": agility, "constitution": constitution}
    return {
        "extra_ranks": stats,
        "health": F(24 + 3 * constitution),
        "stability": 10 + F(constitution, 2) + (min(F(agility, 4), F(1)) if can_balance else 0),
        "move_speed_factor": 1 + F(agility, 32),
        "attacks": {
            key: {
                "damage": a["damage"] * (1 + F(stats[a["primary"]], 8)),
                "impact": a["impact"] * (1 + F(strength, 16)),
            } for key, a in ATTACKS.items()
        },
    }


def window_starts(action_speed):
    windup = max(1, round_half_up(F(30 * 20, action_speed)))
    starts = []
    tick = 1
    while tick < 180:
        starts.append(tick)
        tick += windup + 30
    return {"action_speed": action_speed, "windup_ticks": windup,
            "start_ticks": starts, "impact_ticks": [t + windup for t in starts]}


def number_json(value):
    if isinstance(value, F):
        return int(value) if value.denominator == 1 else float(value)
    raise TypeError(type(value).__name__)


def main():
    builds = []
    for s in range(5):
        for a in range(5):
            c = 4 - s - a
            if 0 <= c <= 4:
                b = derive(s, a, c)
                b["stability_when_balanced"] = derive(s, a, c, True)["stability"]
                b["hits_against_24_hp"] = {key: ceil_fraction(F(24) / v["damage"])
                                          for key, v in b["attacks"].items()}
                builds.append(b)
    control_edges = []
    for impact in [F(10), F(11), F(12)]:
        for a, c, ready in [(0, 0, False), (0, 1, False), (1, 0, True), (4, 0, True), (0, 4, False)]:
            resistance = derive(0, a, c, ready)["stability"]
            control_edges.append({"incoming_impact": impact, "defender_agility": a,
                                  "defender_constitution": c, "can_balance": ready,
                                  "stability": resistance, "stagger": impact >= resistance,
                                  "knockdown": impact >= resistance * F(3, 2)})
    equal_growth = []
    for x in range(5):
        b = derive(x, x, x)
        balanced = derive(x, x, x, True)
        equal_growth.append({"all_extra_ranks": x, "health": b["health"],
                             "quick_damage": b["attacks"]["quick"]["damage"],
                             "quick_hits_same_growth_target": ceil_fraction(b["health"] / b["attacks"]["quick"]["damage"]),
                             "received_resource_per_quick_hit": b["attacks"]["quick"]["damage"] / 8,
                             "heavy_impact_ratio": b["attacks"]["heavy"]["impact"] / b["stability"],
                             "heavy_impact_ratio_when_balanced": b["attacks"]["heavy"]["impact"] / balanced["stability"]})
    result = {
        "status": "proposal-arithmetic-only-not-engine-validation",
        "budget": 4, "builds": builds, "control_edges": control_edges,
        "speed_window_boundary": [window_starts(20), window_starts(21)],
        "equal_growth": equal_growth,
    }
    output = Path(__file__).with_name("attribute_model_v04_results.json")
    output.write_text(json.dumps(result, default=number_json, ensure_ascii=False, indent=2), encoding="utf-8")
    summary = {
        "equal_budget_builds": len(builds),
        "specialists": [b for b in builds if 4 in b["extra_ranks"].values()],
        "speed_window_boundary": result["speed_window_boundary"],
        "control_10_edges": [x for x in control_edges if x["incoming_impact"] == 10],
        "equal_growth_endpoints": [equal_growth[0], equal_growth[-1]],
    }
    print(json.dumps(summary, default=number_json, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
