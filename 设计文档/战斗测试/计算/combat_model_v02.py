"""Exact arithmetic for a design proposal; this is not the Unity battle simulation.

Run with Python 3. Outputs JSON beside this script. No production inputs are changed.
The window probe isolates resource feedback: fixed contacts, idle defender, no
movement, control, clash, armor, healing, outgoing defender damage or failed blocks.
"""

from fractions import Fraction as F
from pathlib import Path
import heapq
import json


ATTACKS = {
    "quick": {"label": "快斩", "base": F(4), "windup": 30, "recovery": 30},
    "heavy": {"label": "重砸", "base": F(8), "windup": 90, "recovery": 30},
    "sweep": {"label": "横扫", "base": F(3), "windup": 60, "recovery": 30},
}


def ceil_f(value):
    return -(-value.numerator // value.denominator)


def damage(key, power=0, resistance=F(0)):
    return ATTACKS[key]["base"] * (1 + F(power, 8)) * (1 - resistance)


def health(vitality=0):
    return F(24 + 3 * vitality)


def required_power(key, hit_count, hp=F(24), resistance=F(0)):
    return max(0, ceil_f(8 * (hp / (hit_count * ATTACKS[key]["base"] * (1-resistance)) - 1)))


def steady(d, rate=F(1), beta=F(1, 8), cost=F(1), reward=F(0)):
    """Infinite-duration approximation: received damage is the only resource source."""
    net = cost - reward
    assert d > 0 and rate > 0 and beta >= 0 and net > 0
    p = beta * d / (net + beta * d)
    actual = rate * d * (1-p)
    return {"damage": float(d), "attacks_per_second": float(rate),
            "blocked_fraction": float(p), "actual_damage_per_second": float(actual),
            "resource_income_per_second": float(beta * actual)}


def window_probe(key, power=0, vitality=0, beta=F(1, 8), cap=F(2), enabled=True):
    """1v1 alternating 180-Tick windows, with an immediately reacting idle defender.

    First attack starts at Tick 1. Ordinary attacks remain serial and may extend
    beyond their window. A published attack can be answered at start+1, with its
    reaction reserved then and executed at impact. Reservations survive resets.
    At most one attack is pending because this probe uses serial single hits.
    This is a conditional arithmetic probe, not a complete game or AI test.
    """
    hp = health(vitality)
    d = damage(key, power)
    timing = ATTACKS[key]
    queue, seq = [], 0

    def push(tick, phase, kind, data=None):
        nonlocal seq
        seq += 1
        heapq.heappush(queue, (tick, phase, seq, kind, data))

    for tick in range(180, 18000, 360):
        push(tick, 0, "reset")
    free_tick = 1
    for ident in range(120):
        start = free_tick
        phase = start % 360
        if phase >= 180:
            start += 361 - phase
        elif phase == 0:
            start += 1
        assert 1 <= start % 360 < 180
        impact = start + timing["windup"]
        push(start + 1, 1, "accept", ident)
        push(impact, 2, "impact", ident)
        free_tick = impact + timing["recovery"]

    available, reserved = F(0), {}
    gain, reset_loss, cap_loss = F(0), F(0), F(0)
    attempts, blocks, hits = 0, 0, 0
    history = []
    while queue:
        tick, _, _, kind, ident = heapq.heappop(queue)
        if kind == "reset":
            reset_loss += available
            available = F(0)
        elif kind == "accept":
            if enabled and available >= 1:
                available -= 1
                reserved[ident] = F(1)
        else:
            attempts += 1
            if ident in reserved:
                del reserved[ident]
                blocks += 1
                outcome = "block"
            else:
                loss = min(hp, d)
                hp -= loss
                hits += 1
                outcome = "hit"
                if hp > 0:
                    earned = beta * loss if enabled else F(0)
                    gain += earned
                    room = max(F(0), cap - sum(reserved.values(), F(0)) - available)
                    cap_loss += max(F(0), earned - room)
                    available += min(earned, room)
            history.append({"tick": tick, "outcome": outcome, "hp": float(hp),
                            "available": float(available), "reserved": float(sum(reserved.values(), F(0)))})
            if hp <= 0:
                assert hits == ceil_f(health(vitality) / d)
                return {"attack": key, "power": power, "vitality": vitality,
                        "reactions": enabled, "damage": float(d), "kill_tick": tick,
                        "seconds": tick/60, "attempts": attempts, "hits": hits, "blocks": blocks,
                        "usable_received_gain": float(gain), "reset_loss": float(reset_loss),
                        "cap_loss": float(cap_loss), "history": history}
        assert available >= 0
        assert available + sum(reserved.values(), F(0)) <= cap
    raise AssertionError("probe did not finish")


def packet_probe(d, beta, cycle_attacks=None, hp=F(24), cap=F(2)):
    """A separate attack-event sensitivity probe; no game timing is implied."""
    available = F(0)
    count = blocks = 0
    while hp > 0:
        if cycle_attacks and count % cycle_attacks == 0:
            available = F(0)
        count += 1
        if available >= 1:
            available -= 1
            blocks += 1
        else:
            loss = min(hp, d)
            hp -= loss
            if hp > 0:
                available = min(cap, available + beta * loss)
        assert count < 1000
    return {"damage": float(d), "beta": str(beta), "attacks_per_cycle": cycle_attacks,
            "attempts": count, "blocks": blocks}


def main():
    growth = [{"power": p, **{key: {"damage": float(damage(key, p)),
              "hits": ceil_f(F(24)/damage(key, p))} for key in ATTACKS}} for p in range(9)]
    thresholds = [{"attack": key, "target_hits": n, "minimum_power": required_power(key, n)}
                  for key in ATTACKS for n in range(ceil_f(F(24)/ATTACKS[key]["base"])-1, 1, -1)]
    probes = [window_probe(key, power=p, enabled=enabled) for key in ATTACKS
              for enabled in [False, True] for p in range(9)]
    regressions = []
    for key in ATTACKS:
        rows = [r for r in probes if r["attack"] == key and r["reactions"]]
        for previous, current in zip(rows, rows[1:]):
            if current["kill_tick"] > previous["kill_tick"]:
                regressions.append({"attack": key, "from_power": previous["power"],
                                    "to_power": current["power"], "from_tick": previous["kill_tick"],
                                    "to_tick": current["kill_tick"]})
    grid_cases = 0
    for v in range(5):
        for p in range(9):
            for key in ATTACKS:
                for resistance in [F(0), F(1, 4), F(1, 2)]:
                    hp, d = health(v), damage(key, p, resistance)
                    hits = ceil_f(hp / d)
                    assert (hits - 1) * d < hp <= hits * d
                    grid_cases += 1
    assert [growth[0][k]["hits"] for k in ATTACKS] == [6, 3, 8]
    assert [required_power(k, ceil_f(F(24)/ATTACKS[k]["base"])-1) for k in ATTACKS] == [2, 4, 2]
    assert sum([F(1, 10)]*10) == 1
    data = {"status": "design-calculation-only-not-engine-validation", "growth": growth,
            "thresholds": thresholds, "steady_fixed_rate": [steady(F(d)) for d in [4, 8, 12]],
            "steady_equal_raw_dps": [steady(F(4), F(1)), steady(F(8), F(1, 2))],
            "window_probes": probes, "window_probe_growth_regressions": regressions,
            "high_gain_counterexample": [packet_probe(F(d), F(1,4), 8, cap=F(3)) for d in [6,7]],
            "arithmetic_grid_cases": grid_cases}
    output = Path(__file__).with_name("combat_model_v02_results.json")
    output.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"growth": growth, "steady": data["steady_fixed_rate"],
        "baseline_window_probes": [{k: v for k, v in r.items() if k != "history"}
                                  for r in probes if r["power"] == 0],
        "growth_regressions": regressions, "counterexample": data["high_gain_counterexample"],
        "arithmetic_grid_cases": grid_cases, "output": str(output)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
