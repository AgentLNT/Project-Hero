"""Check full paired matrices, constant direct damage and per-unit damage deadlines."""
import itertools
import json
from pathlib import Path
from statistics import median

ROOT = Path(__file__).resolve().parent
POLICIES = ('SustainedOffense', 'SeekInterrupt', 'CautiousDefense')
SPEEDS = ((20, 30), (24, 20))
PERCENTS = (100, 90, 80)
MOVES = ('quick_slash', 'spear_thrust', 'wide_cleave', 'heavy_smash')
ONE = 1024


def read(name):
    return [json.loads(line) for line in (ROOT / name).read_text(encoding='utf-8-sig').splitlines()]


def half_up(n, d):
    return (n * 2 + d) // (2 * d)


def fixed_key(r):
    return r['Profile']['Id'], r['Q'], r['Left'], r['Right'], r['Gap'], r['Guard']


def fixed():
    result = {}
    previous = ROOT.parent / '三类攻击同强度对照-2026-11-18'
    for label in ('fixed', 'clash'):
        indexes = {}
        recipes = ('Pierce', 'Slash', 'Blunt')
        profiles = ('Native', 'A20M30-R20', 'A24M20-R20', 'A20M30-R25', 'A24M20-R25')
        strengths = (1, 2, 3, 4, 5, 6, 8)
        expected = set(itertools.product(profiles, strengths, recipes, recipes, (-1, 0), (False,))) if label == 'clash' else {
            (p, q, a, None, g, guard)
            for p, q, a, g, guard in itertools.product(profiles, strengths, recipes, (-1, 0, 1), (False, True))}
        for percent in PERCENTS:
            rows = read(f'{label}-M{percent}.jsonl')
            index = {fixed_key(r): r for r in rows}
            assert set(index) == expected and len(rows) == len(expected)
            assert all(r['Percent'] == percent and r['ReplayVerified'] for r in rows)
            indexes[percent] = index
            for r in rows:
                for effect in (r['OnLeft'], r['OnRight']) if label == 'clash' else (r['OnRight'],):
                    assert 0 <= effect['WallApplied'] <= effect['WallRaw']
                    assert max(0, r['Profile']['Hp'] * ONE - effect['Direct'] - effect['WallApplied']) == effect['Hp']
        archived = {fixed_key(r): r for r in read_previous(previous / f'{label}.jsonl')}
        assert set(archived) == expected
        for k, r in indexes[100].items():
            assert r['OnLeft'] == archived[k]['OnLeft'] and r['OnRight'] == archived[k]['OnRight'], (label, k)
        if label == 'fixed':
            coeffs = {'Pierce': 1, 'Slash': 3, 'Blunt': 5}
            for percent in PERCENTS:
                for k, r in indexes[percent].items():
                    original = indexes[100][k]['OnRight']
                    assert r['OnRight']['Direct'] == original['Direct']
                    momentum = half_up(r['Q'] * ONE * coeffs[r['Left']] * percent, 100)
                    assert r['OnRight']['Momentum'] == (half_up(momentum, 2) if r['Guard'] else momentum)
        else:
            residual_errors = {p: max(abs(indexes[p][k][side]['Direct'] - indexes[100][k][side]['Direct'])
                                      for k in expected for side in ('OnLeft', 'OnRight')) for p in PERCENTS}
            # Only final quantization may perturb the residual damage ratio.
            assert max(residual_errors.values()) <= 2
            result['residual_max_delta_q10'] = residual_errors
        result[label] = dict(cases_per_percent=len(expected), total_cases=3 * len(expected),
                             archived_100_percent_effects_identical=True)
    return result


def read_previous(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines()]


def key(r):
    return (r['A'], r['Move']), r['Layout'], r['Hero'], r['Enemy'], r['Mirror']


def ai(strength, scene, percent):
    rows = read(f'ai-S{strength}-M{percent}-{scene}.jsonl')
    index = {key(r): r for r in rows}
    expected = set(itertools.product(SPEEDS, ('Near',) if scene == 'wall' else ('Near', 'Main', 'Far'),
                                     POLICIES, POLICIES, (False, True)))
    assert set(index) == expected and len(rows) == len(expected), (strength, scene, percent, len(rows))
    assert sum(r['ReplayVerified'] for r in rows) == 6
    for r in rows:
        assert (r['Strength'], r['Percent'], r['Scene']) == (strength, percent, scene)
        assert r['Pass'] == (r['Ended'] and any(a['DamageQ10'] == 50 * ONE for a in r['Actors']))
        assert r['EarlyDeaths'] == sum(a['Windows'] == 0 and a['DeathTick'] is not None for a in r['Actors'])
        assert 0 <= r['WallAppliedQ10'] <= r['WallRawQ10']
        assert sum(r['DamageByAttackQ10'].values()) == sum(a['TotalQ10'] for a in r['Actors'])
        assert sum(a['RemainingQ10'] + a['TotalQ10'] for a in r['Actors']) == 100 * ONE
    return index


def stats(rows):
    ended = [r['Tick'] for r in rows if r['Ended']]
    def sum_counter(field):
        return {k: sum(r[field][k] for r in rows) for k in MOVES}
    return dict(cases=len(rows), ended=len(ended), within_two_windows=sum(r['Pass'] for r in rows),
                ended_late=sum(r['Ended'] and not r['Pass'] for r in rows),
                before_first_window_deaths=sum(r['EarlyDeaths'] for r in rows),
                both_opened_and_two_windows=sum(r['Pass'] and r['EarlyDeaths'] == 0 for r in rows),
                median_end_tick=median(ended) if ended else None,
                wall_cases=sum(r['WallCount'] > 0 for r in rows), wall_events=sum(r['WallCount'] for r in rows),
                wall_raw_hp=sum(r['WallRawQ10'] for r in rows) / ONE,
                wall_applied_hp=sum(r['WallAppliedQ10'] for r in rows) / ONE,
                staggers=sum(r['Staggers'] for r in rows), knockdowns=sum(r['Knockdowns'] for r in rows),
                displacements=sum(r['Displacements'] for r in rows), clash_participants=sum(r['ClashParticipants'] for r in rows),
                block_success=sum(r['BlockSuccess'] for r in rows), dodge_success=sum(r['DodgeSuccess'] for r in rows),
                planned=sum_counter('Planned'), impacts=sum_counter('Impacts'),
                damage_by_attack_hp={k: v / ONE for k, v in sum_counter('DamageByAttackQ10').items()},
                audited_replays=sum(r['ReplayVerified'] for r in rows))


def paired(control, candidate):
    assert control.keys() == candidate.keys()
    return dict(gained=[r['Id'] for k, r in candidate.items() if r['Pass'] and not control[k]['Pass']],
                lost=[r['Id'] for k, r in candidate.items() if not r['Pass'] and control[k]['Pass']],
                fewer_early_deaths=[r['Id'] for k, r in candidate.items() if r['EarlyDeaths'] < control[k]['EarlyDeaths']],
                more_early_deaths=[r['Id'] for k, r in candidate.items() if r['EarlyDeaths'] > control[k]['EarlyDeaths']])


def parameters(strength):
    reports = {p: json.loads((ROOT / f'parameters-S{strength}-M{p}.json').read_text(encoding='utf-8')) for p in PERCENTS}
    baseline = {r['Spec']: r for r in reports[100]['Parameters']}
    assert len(baseline) == 4
    for p, report in reports.items():
        for r in report['Parameters']:
            b = baseline[r['Spec']]
            assert all(r[field] == b[field] for field in ('Recipe', 'Q10', 'DamageQ10', 'Pattern', 'TargetPolicy'))
            assert r['MomentumUnits'] == half_up(b['MomentumUnits'] * p, 100)
    return reports


def main():
    audit = json.loads((ROOT / 'parameter-audit.json').read_text(encoding='utf-8-sig'))
    assert len(audit) == 6
    for strength in (3, 4):
        rows = [r for r in audit if r['Strength'] == strength]
        assert {r['Percent'] for r in rows} == set(PERCENTS) and len({r['Hash'] for r in rows}) == 3
        reference = {r['Spec']: r for r in rows[0]['Parameters']}
        for group in rows:
            for r in group['Parameters']:
                assert r['ShapeAndTimingIdenticalToSource']
                assert all(r[f] == reference[r['Spec']][f] for f in (
                    'Recipe', 'Q10', 'DamageQ10', 'BaseWindupTicks', 'RecoveryTicks', 'Pattern', 'TargetPolicy', 'Tags'))
    result = dict(mechanisms=fixed(), ai={}, parameters={}, geometry_and_timing_audit=audit)
    for strength in (3, 4):
        result['parameters'][strength] = parameters(strength)
        result['ai'][strength] = {}
        for scene in ('open', 'wall'):
            indexes = {p: ai(strength, scene, p) for p in PERCENTS}
            assert all(len({indexes[p][k]['Hash'] for p in PERCENTS}) == 3 for k in indexes[100])
            result['ai'][strength][scene] = {p: dict(overall=stats(list(index.values())),
                speed={f'A{a}M{m}': stats([r for r in index.values() if (r['A'], r['Move']) == (a, m)]) for a, m in SPEEDS},
                layout={layout: stats([r for r in index.values() if r['Layout'] == layout])
                        for layout in (('Near',) if scene == 'wall' else ('Near', 'Main', 'Far'))},
                vs_100=paired(indexes[100], index)) for p, index in indexes.items()}
    (ROOT / 'summary.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(result['mechanisms'])
    for s, scenes in result['ai'].items():
        for scene, variants in scenes.items():
            for p, record in variants.items():
                r = record['overall']
                print(s, scene, p, {k: r[k] for k in ('cases', 'within_two_windows', 'before_first_window_deaths',
                    'both_opened_and_two_windows', 'wall_applied_hp', 'staggers', 'knockdowns', 'displacements', 'clash_participants')},
                    'paired +', len(record['vs_100']['gained']), '-', len(record['vs_100']['lost']))


if __name__ == '__main__':
    main()
