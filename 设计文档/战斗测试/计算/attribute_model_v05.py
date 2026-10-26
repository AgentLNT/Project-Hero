"""Exact arithmetic for the v0.5 design; not a Unity combat simulation.

All ranks below are extra ranks above rank 1. Ratios are enforced at attack
generation, before clash, damage mitigation, control aggregation or rounding.
"""

from fractions import Fraction as F
from pathlib import Path
import json


PROFILES = {
    "pierce": {"damage": F(5), "impact": F(1)},
    "slash": {"damage": F(4), "impact": F(3)},
    "blunt": {"damage": F(3), "impact": F(5)},
}
ATTACKS = {
    "quick": {"profile": "slash", "power": F(1), "primary": "agility"},
    "heavy": {"profile": "blunt", "power": F(2), "primary": "strength"},
    "sweep": {"profile": "slash", "power": F(3, 4), "primary": "agility"},
    "thrust_example": {"profile": "pierce", "power": F(1), "primary": "agility"},
}


def ceil_fraction(value):
    return -(-value.numerator // value.denominator)


def round_half_up(value):
    return (2 * value.numerator + value.denominator) // (2 * value.denominator)


def attack_values(key, primary_extra):
    spec = ATTACKS[key]
    profile = PROFILES[spec["profile"]]
    power = spec["power"] * (1 + F(primary_extra, 8))
    return {
        "profile": spec["profile"],
        "power": power,
        "damage": power * profile["damage"],
        "impact": power * profile["impact"],
    }


def derive(strength, agility, constitution, gear_health=0, balance=False):
    ranks = {"strength": strength, "agility": agility, "constitution": constitution}
    return {
        "extra_ranks": ranks,
        "gear_health": gear_health,
        "health": F(24 + 3 * constitution + gear_health),
        "base_stability": 10 + F(constitution, 2),
        "effective_stability": 10 + F(constitution, 2) + (F(agility, 4) if balance else 0),
        "move_speed_factor": 1 + F(agility, 32),
        "attacks": {key: attack_values(key, ranks[spec["primary"]])
                    for key, spec in ATTACKS.items()},
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
    growth = []
    checks = {"fixed_ratio_cases": 0, "hit_threshold_cases": 0,
              "exact_health_q10_cases": 0, "gear_isolation_cases": 0}
    for p in range(5):
        row = {"primary_extra": p, "attacks": {}}
        for key, spec in ATTACKS.items():
            values = attack_values(key, p)
            profile = PROFILES[spec["profile"]]
            assert values["damage"] * profile["impact"] == values["impact"] * profile["damage"]
            checks["fixed_ratio_cases"] += 1
            assert (values["damage"] * 1024).denominator == 1
            checks["exact_health_q10_cases"] += 1
            for health in [24, 27, 30, 36, 42]:
                hits = ceil_fraction(F(health) / values["damage"])
                assert (hits - 1) * values["damage"] < health <= hits * values["damage"]
                checks["hit_threshold_cases"] += 1
            row["attacks"][key] = {**values,
                                   "hits_against_24_hp": ceil_fraction(F(24) / values["damage"]),
                                   "damage_income": values["damage"] / 12,
                                   "received_income": values["damage"] / 8}
        growth.append(row)

    builds = []
    for s in range(5):
        for a in range(5):
            c = 4 - s - a
            if not 0 <= c <= 4:
                continue
            base = derive(s, a, c)
            for gear in [3, 6]:
                equipped = derive(s, a, c, gear)
                assert equipped["health"] - base["health"] == gear
                for field in ["attacks", "base_stability", "effective_stability", "move_speed_factor"]:
                    assert equipped[field] == base[field]
                checks["gear_isolation_cases"] += 1
            base["conditional_stability_if_enabled_and_eligible"] = derive(s, a, c, balance=True)["effective_stability"]
            builds.append(base)

    equipment_hits = [
        {"gear_health": gear, "health": 24 + gear,
         "hits": {key: ceil_fraction(F(24 + gear) / attack_values(key, 0)["damage"])
                  for key in ATTACKS}}
        for gear in [0, 3, 6]
    ]
    control = []
    for p in range(5):
        impact = attack_values("heavy", p)["impact"]
        for stability in [F(10), F(21, 2), F(11), F(12), F(13), F(15)]:
            control.append({"strength_extra": p, "heavy_impact": impact,
                            "stability": stability, "stagger_threshold_met": impact >= stability,
                            "knockdown_threshold_met": impact >= F(3, 2) * stability})

    equal_growth = []
    for p in range(5):
        b = derive(p, p, p)
        equal_growth.append({
            "all_extra_ranks": p, "health": b["health"],
            "stability": b["base_stability"],
            "hits_same_growth_target": {
                key: ceil_fraction(b["health"] / values["damage"])
                for key, values in b["attacks"].items()},
            "heavy_impact_ratio": b["attacks"]["heavy"]["impact"] / b["base_stability"],
            "received_resource_per_quick_hit": b["attacks"]["quick"]["damage"] / 8,
        })

    first_improvement = {}
    for key in ATTACKS:
        start = growth[0]["attacks"][key]["hits_against_24_hp"]
        first_improvement[key] = next(
            row["primary_extra"] for row in growth[1:]
            if row["attacks"][key]["hits_against_24_hp"] < start)

    result = {
        "status": "design-candidate-exact-arithmetic-not-unity-or-balance-validation",
        "scope": "Ordinary size; no active defense, clash, control scheduling, healing or AI.",
        "profiles": PROFILES, "attack_definitions": ATTACKS,
        "growth": growth, "first_hit_count_improvement": first_improvement,
        "equal_budget_builds": builds, "equipment_hit_counts": equipment_hits,
        "control_edges": control, "equal_growth": equal_growth,
        "speed_window_boundary": [window_starts(20), window_starts(21)],
        "validation_counts": checks,
    }
    output = Path(__file__).with_name("attribute_model_v05_results.json")
    output.write_text(json.dumps(result, default=number_json, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "validation_counts": checks,
        "first_hit_count_improvement": first_improvement,
        "baseline": growth[0], "highest_growth": growth[-1],
        "equipment_hit_counts": equipment_hits,
        "specialists": [b for b in builds if 4 in b["extra_ranks"].values()],
        "equal_growth_endpoints": [equal_growth[0], equal_growth[-1]],
    }, default=number_json, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
