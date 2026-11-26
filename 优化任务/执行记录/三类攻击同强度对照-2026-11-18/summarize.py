"""Validate full matrices and summarize wall/no-wall pairs, without pooling units' deadlines."""
import itertools
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parent
RECIPES = ('Pierce', 'Slash', 'Blunt')
PROFILES = ('Native', 'A20M30-R20', 'A24M20-R20', 'A20M30-R25', 'A24M20-R25')
AI_PROFILES = PROFILES[1:3]
POLICIES = ('SustainedOffense', 'SeekInterrupt', 'CautiousDefense')
QS = (1, 2, 3, 4, 5, 6, 8)
ONE = 1024


def read(file):
    return [json.loads(line) for line in (ROOT / file).read_text(encoding='utf-8').splitlines()]


def validate_fixed(rows, clash):
    if clash:
        expected = set(itertools.product(PROFILES, QS, RECIPES, RECIPES, (-1, 0), (False,)))
    else:
        expected = {(p, q, a, None, g, guard)
                    for p, q, a, g, guard in itertools.product(PROFILES, QS, RECIPES, (-1, 0, 1), (False, True))}
    actual = {(r['Profile']['Id'], r['Q'], r['Left'], r['Right'], r['Gap'], r['Guard']) for r in rows}
    assert actual == expected and len(rows) == len(expected)
    for r in rows:
        assert r['ReplayVerified']
        for target in (r['OnLeft'], r['OnRight']) if clash else (r['OnRight'],):
            assert 0 <= target['WallApplied'] <= target['WallRaw']
            assert max(0, r['Profile']['Hp'] * ONE - target['Direct'] - target['WallApplied']) == target['Hp']
    return dict(cases=len(rows), audited_replays=sum(r['ReplayVerified'] for r in rows),
                walls=sum((r['OnLeft'] or {}).get('WallRaw', 0) > 0 or r['OnRight']['WallRaw'] > 0 for r in rows))


def key(r):
    return r['Profile']['Id'], r['Left'], r['Right'], r['Policy'], r['Mirror']


def load_ai(q, scene):
    rows = read(f'ai-Q{q}-{scene}.jsonl')
    index = {key(r): r for r in rows}
    expected = set(itertools.product(AI_PROFILES, RECIPES, RECIPES, POLICIES, (False, True)))
    assert set(index) == expected and len(index) == len(rows), (q, scene, len(rows))
    assert sum(r['ReplayVerified'] for r in rows) == 8
    for r in rows:
        assert r['Q'] == q and r['Scene'] == scene
        actors = r['Actors']
        assert [a['Unit'] for a in actors] == [1, 2]
        assert r['EarlyDeaths'] == sum(a['DeathTick'] is not None and a['Windows'] == 0 for a in actors)
        assert r['Pass'] == (r['Ended'] and any(a['DamageByDeadlineQ10'] == 50 * ONE for a in actors))
        for a, b in (actors, actors[::-1]):
            assert 0 <= a['WallQ10'] <= a['TotalQ10'] <= 50 * ONE
            assert a['TotalQ10'] + b['RemainingQ10'] == 50 * ONE
    return index


def stats(rows):
    return dict(cases=len(rows), ended=sum(r['Ended'] for r in rows),
                pass_two_windows=sum(r['Pass'] for r in rows),
                pass_and_both_had_first_window=sum(r['Pass'] and not r['EarlyDeaths'] for r in rows),
                before_first_window_deaths=sum(r['EarlyDeaths'] for r in rows),
                walls=sum(r['Walls'] for r in rows), clashes=sum(r['ClashParticipants'] for r in rows) // 2,
                block_success=sum(r['Blocks'] for r in rows), dodge_success=sum(r['Dodges'] for r in rows),
                audited_replays=sum(r['ReplayVerified'] for r in rows))


def per_recipe(rows):
    result = {}
    for recipe in RECIPES:
        actors = [actor for row in rows
                  for kind, actor in zip((row['Left'], row['Right']), row['Actors']) if recipe == kind]
        result[recipe] = dict(exposures=len(actors),
                              lethal=sum(a['TotalQ10'] == 50 * ONE for a in actors),
                              lethal_by_deadline=sum(a['DamageByDeadlineQ10'] == 50 * ONE for a in actors),
                              wall_hp=sum(a['WallQ10'] for a in actors) / ONE,
                              mean_hp_dealt=sum(a['TotalQ10'] for a in actors) / ONE / len(actors))
    return result


def paired(open_rows, wall_rows):
    assert set(open_rows) == set(wall_rows)
    return dict(gained=[r['Id'] for k, r in wall_rows.items() if r['Pass'] and not open_rows[k]['Pass']],
                lost=[r['Id'] for k, r in wall_rows.items() if not r['Pass'] and open_rows[k]['Pass']],
                added_first_window_death=[r['Id'] for k, r in wall_rows.items() if r['EarlyDeaths'] > open_rows[k]['EarlyDeaths']])


def main():
    fixed = read('fixed.jsonl')
    clash = read('clash.jsonl')
    result = dict(fixed=validate_fixed(fixed, False), clash=validate_fixed(clash, True), ai={})
    for q in (4, 6):
        open_rows, wall_rows = (load_ai(q, s) for s in ('open', 'wall'))
        result['ai'][q] = {}
        for label, indexed in (('open', open_rows), ('wall', wall_rows)):
            rows = list(indexed.values())
            result['ai'][q][label] = dict(overall=stats(rows), per_recipe=per_recipe(rows),
                per_speed={p: stats([r for r in rows if r['Profile']['Id'] == p]) for p in AI_PROFILES},
                per_policy={p: stats([r for r in rows if r['Policy'] == p]) for p in POLICIES})
        result['ai'][q]['walls_vs_open'] = paired(open_rows, wall_rows)
    (ROOT / 'summary.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('fixed', result['fixed'], 'clash', result['clash'])
    for q, value in result['ai'].items():
        for scene in ('open', 'wall'):
            print(q, scene, value[scene]['overall'], value[scene]['per_recipe'])
        print(q, 'paired +', len(value['walls_vs_open']['gained']), '-', len(value['walls_vs_open']['lost']))


if __name__ == '__main__':
    main()
