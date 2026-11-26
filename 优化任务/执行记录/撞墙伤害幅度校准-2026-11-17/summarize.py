"""Check completeness and reduce the independently recorded battle results."""
import itertools
import json
from pathlib import Path
from statistics import median

ROOT = Path(__file__).resolve().parent
SPEEDS = ((20, 30), (24, 20))
POLICIES = ('SustainedOffense', 'SeekInterrupt', 'CautiousDefense')
MODELS = {
    'open': ('R1000-ratio', 'R6-ratio', 'R9-soft-heavy', 'R12.5-ratio', 'R20-ratio', 'R25-ratio'),
    'walls': ('R6-ratio', 'R9-soft-heavy', 'R12.5-ratio', 'R20-ratio', 'R25-ratio', 'R20-long'),
}


def hp(q10):
    return q10 / 1024


def stats(rows):
    return dict(matches=len(rows), ended=sum(r['Ended'] for r in rows),
                within_two_windows=sum(r['Pass'] for r in rows),
                within_two_windows_after_both_opened=sum(r['Pass'] and r['DeathsBeforeFirstWindow'] == 0 for r in rows),
                before_first_window_deaths=sum(r['DeathsBeforeFirstWindow'] for r in rows),
                wall_cases=sum(r['WallCount'] > 0 for r in rows),
                wall_events=sum(r['WallCount'] for r in rows),
                raw_wall_hp=hp(sum(r['WallRawQ10'] for r in rows)),
                applied_wall_hp=hp(sum(r['WallAppliedQ10'] for r in rows)),
                displacements=sum(r['Displacements'] for r in rows),
                clash_participants=sum(r['ClashParticipants'] for r in rows),
                median_end_tick=median([r['Ticks'] - 1 for r in rows if r['Ended']]),
                rejected_commands=sum(r['Rejections'] for r in rows),
                audited_replays=sum(r['ReplayVerified'] for r in rows))


def key(r):
    return ((r['A'], r['Move']), r['Layout'], r['Hero'], r['Enemy'], r['Mirror'])


def read_ai(model, scene):
    rows = [json.loads(line) for line in (ROOT / f'ai-{model}-{scene}.jsonl').read_text(encoding='utf-8').splitlines()]
    errors = [r for r in rows if 'Error' in r]
    assert not errors, errors[:2]
    index = {key(r): r for r in rows}
    layouts = ('Near',) if scene == 'walls' else ('Near', 'Main', 'Far')
    expected = set(itertools.product(SPEEDS, layouts, POLICIES, POLICIES, (False, True)))
    assert set(index) == expected and len(rows) == len(expected), (model, scene, len(rows), len(expected))
    assert all(r['Model']['Id'] == model and r['Walls'] == (scene == 'walls') for r in rows)
    assert all(r['Rejections'] == 0 and 0 <= r['WallAppliedQ10'] <= r['WallRawQ10'] for r in rows)
    assert all(r['DeathsBeforeFirstWindow'] in (0, 1) and len(r['Actors']) == 2 for r in rows)
    assert all(r['Pass'] == (r['Ended'] and any(a['DamageQ10'] == 50 * 1024 for a in r['Actors'])) for r in rows)
    assert sum(r['ReplayVerified'] for r in rows) == 6
    return index


def compare(baseline, candidate):
    assert baseline.keys() == candidate.keys()
    return dict(new_passes=[r['Id'] for k, r in candidate.items() if r['Pass'] and not baseline[k]['Pass']],
                lost_passes=[r['Id'] for k, r in candidate.items() if not r['Pass'] and baseline[k]['Pass']],
                cases_with_more_early_deaths=sum(r['DeathsBeforeFirstWindow'] > baseline[k]['DeathsBeforeFirstWindow']
                                                for k, r in candidate.items()),
                cases_with_fewer_early_deaths=sum(r['DeathsBeforeFirstWindow'] < baseline[k]['DeathsBeforeFirstWindow']
                                                 for k, r in candidate.items()))


def fixed():
    rows = json.loads((ROOT / 'fixed.json').read_text(encoding='utf-8'))
    assert len(rows) == len({r['Id'] for r in rows}) == 1040
    assert all(r['ReplayVerified'] for r in rows)
    assert all(0 <= r['WallAppliedQ10'] <= r['WallRawQ10'] for r in rows)
    assert all(hp(r['DirectQ10'] + r['WallAppliedQ10'] + r['RemainingHpQ10']) == 50 for r in rows)
    return dict(cases=len(rows), verified_replays=len(rows), wall_cases=sum(r['WallRawQ10'] > 0 for r in rows),
                examples=[dict(model=r['Model']['Id'], attack=r['Attack'], wall_gap=r['Gap'], guard=r['Guard'],
                               attack_damage_hp=hp(r['DirectQ10']), momentum=r['MomentumUnits'] / 100,
                               wall_damage_raw_hp=hp(r['WallRawQ10']), wall_damage_applied_hp=hp(r['WallAppliedQ10']),
                               remaining_hp=hp(r['RemainingHpQ10']), steps=r['Steps'],
                               contact_after_ticks=None if r['WallTick'] is None else r['WallTick'] - r['ImpactTick'])
                          for r in rows if r['A'] == 20 and r['Attack'] in ('heavy_smash', 'whirlwind')])


def clash():
    rows = json.loads((ROOT / 'clash.json').read_text(encoding='utf-8'))
    assert len(rows) == len({(r['Model']['Id'], r['Left'], r['Right']) for r in rows}) == 325
    return dict(cases=len(rows),
                heavy_vs_thrust={r['Model']['Id']: r['Residuals'] for r in rows
                                 if r['Left'] == 'heavy_smash' and r['Right'] == 'spear_thrust'})


def main():
    ai = {}
    indexes = {}
    for scene, models in MODELS.items():
        indexed = {model: read_ai(model, scene) for model in models}
        indexes[scene] = indexed
        ai[scene] = {model: dict(overall=stats(list(rows.values())),
                                 speed={f'A{a}M{m}': stats([r for r in rows.values() if (r['A'], r['Move']) == (a, m)])
                                        for a, m in SPEEDS},
                                 vs_control=compare(indexed['R1000-ratio' if scene == 'open' else 'R20-ratio'], rows))
                     for model, rows in indexed.items()}
    near = {}
    for model in MODELS['walls']:
        if model in indexes['open']:
            open_rows = {k: r for k, r in indexes['open'][model].items() if r['Layout'] == 'Near'}
            wall_rows = indexes['walls'][model]
            near[model] = dict(open=stats(list(open_rows.values())), walls=stats(list(wall_rows.values())),
                               walls_vs_open=compare(open_rows, wall_rows))
    summary = dict(fixed=fixed(), clash=clash(), ai=ai, paired_near_layout=near)
    (ROOT / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    for scene, models in ai.items():
        for model, result in models.items():
            print(scene, model, result['overall'])


if __name__ == '__main__':
    main()
