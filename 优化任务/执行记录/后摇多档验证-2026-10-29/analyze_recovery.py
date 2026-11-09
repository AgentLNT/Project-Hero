"""Inspect native-engine exports; never reimplement combat, windows or resource rules."""
import argparse
import csv
import json
from collections import Counter, defaultdict
from pathlib import Path
from statistics import mean

ROOT = Path(__file__).resolve().parent
COARSE = [30, 33, 36, 39, 42, 48, 60]
DISCRETE = [
    'clashes', 'post_first_clashes', 'blunt_attack_starts', 'opponent_attack_starts',
    'blunt_window2_attacks', 'opponent_window2_attacks',
    'blunt_cancelled_windups', 'opponent_cancelled_windups',
    'blunt_block_uses', 'blunt_dodge_uses', 'guard_contacts', 'dodge_avoids', 'outcome',
]

def read(stage):
    name = (ROOT / f'latest-{stage}.txt').read_text(encoding='utf-8-sig').strip()
    folder = ROOT / name
    progress = json.loads((folder / 'progress.json').read_text(encoding='utf-8-sig'))
    assert progress['completed'] == progress['total'] and progress['failed'] == 0, progress
    with (folder / 'summary.csv').open(encoding='utf-8-sig', newline='') as f:
        rows = list(csv.DictReader(f))
    assert len(rows) == progress['total']
    for row in rows:
        row['_folder'] = folder.name
        row['_key'] = row['scenario'].split('_', 1)[1]
    return folder, rows

def num(row, key):
    return int(row[key])

def select(rows, **terms):
    return [r for r in rows if all(r[k].lower() == str(v).lower() for k,v in terms.items())]

def group(rows):
    groups = defaultdict(dict)
    for row in rows:
        ticks = num(row, 'recovery_ticks')
        assert row['_key'] not in groups[ticks], ('duplicate', row['scenario'])
        groups[ticks][row['_key']] = row
    assert len({len(v) for v in groups.values()}) == 1, 'Unequal scenario coverage'
    baseline = set(next(iter(groups.values())))
    assert all(set(v) == baseline for v in groups.values()), 'Unmatched experimental cells'
    return groups

def audit(rows, artifacts=False):
    assert len({r['source_fingerprint'] for r in rows}) == 1, 'Mixed final source versions'
    for r in rows:
        assert r['replay_verified'].lower() == 'true'
        assert num(r, 'command_rejections') == 0
        assert num(r, 'delta') == num(r, 'recovery_ticks') - 30
        assert num(r, 'blunt_received_q10') == num(r, 'opponent_dealt_q10')
        assert num(r, 'opponent_received_q10') == num(r, 'blunt_dealt_q10')
        for side in ['blunt', 'opponent']:
            assert num(r, side + '_health_q10') == num(r, 'health') * 1024 - num(r, side + '_received_q10')
        if not r['pair'].startswith('Unopposed'):
            assert num(r, 'first_clash') == num(r, 'opening_impact')
        if artifacts:
            base = ROOT / r['_folder'] / r['scenario']
            for suffix in ['.input.json','.heroReplay','.events.jsonl','.decisions.jsonl','.actions.csv','.frames.csv','.damage-income.csv']:
                p = base.with_name(base.name + suffix)
                assert p.exists() and p.stat().st_size > 0, p
            inp = json.loads(base.with_name(base.name + '.input.json').read_text(encoding='utf-8-sig'))
            assert inp['recoveryTicks'] == num(r, 'recovery_ticks')
            assert inp['definitionHash'] == r['definition_hash']
            assert inp['sourceFingerprint'] == r['source_fingerprint']
            with base.with_name(base.name + '.actions.csv').open(encoding='utf-8-sig', newline='') as f:
                for action in csv.DictReader(f):
                    if action['action'].rsplit('.',1)[-1] in ['heavy','quick','sweep','probe_blunt','probe_slash','probe_pierce']:
                        expected = num(r,'recovery_ticks') if action['action'].endswith(('heavy','probe_blunt')) else 30
                        assert int(action['impact']) > 0 and int(action['source_window']) > 0, (base,action)
                        assert int(action['end']) - int(action['impact']) == expected, (base,action)
    groups = group(rows)
    baseline = groups[min(groups)]
    for cells in groups.values():
        for key,r in cells.items():
            reference = baseline[key]
            for field in ['blunt_first_residual_q10','opponent_first_residual_q10']:
                assert r[field] == reference[field], ('First clash changed with recovery',r['scenario'],field)
            if r['pair'] in ['UnopposedQuick','UnopposedSweep']:
                for field in ['end_tick','outcome','blunt_attack_starts','blunt_dealt_q10','opponent_health_q10']:
                    assert r[field] == reference[field], ('Unaffected action changed',r['scenario'],field)

def summary(rows):
    groups = group(rows)
    answer = []
    for ticks in sorted(groups):
        rs = list(groups[ticks].values())
        probe = select(rs,pair='ProbeSlash',response='Repeat',opening_impact=213,blunt_owner=1,swap_spawns=False)[0]
        heavy = select(rs,pair='UnopposedHeavy',growth=0,health=24)[0]
        wait = select(rs,pair='ProbeSlash',response='WaitRealign',opening_impact=213,blunt_owner=1)[0]
        actual = [r for r in rs if r['pair'] in ['HeavyQuick','HeavySweep'] and r['response']=='Repeat'
            and r['short_range']=='False' and num(r,'stability_q10')==10240 and num(r,'opening_impact') in [273,303,330]]
        by_role = {str(owner):round(mean((num(r,'blunt_dealt_q10')-num(r,'opponent_dealt_q10'))/1024
            for r in actual if num(r,'blunt_owner')==owner),4) for owner in [1,2]}
        edge = select(rs,pair='ProbeSlash',response='Repeat',opening_impact=329,blunt_owner=2,swap_spawns=False)[0]
        answer.append(dict(recovery=ticks,cases=len(rs),probe_clashes=num(probe,'clashes'),
            blunt_next_hit=num(probe,'blunt_next_attack_impact')-213,opponent_next_hit=num(probe,'opponent_next_attack_impact')-213,
            probe_blunt_window2_starts=num(probe,'blunt_window2_attacks'),wait_clashes=num(wait,'clashes'),
            heavy_htk24_end=num(heavy,'end_tick'),edge_blunt_next_start=num(edge,'blunt_next_attack_start'),
            actual_fixed_horizon_net_damage_by_role=by_role,
            paid_block_uses=sum(num(r,'blunt_block_uses')+num(r,'opponent_block_uses') for r in rs),
            dodge_avoids=sum(num(r,'dodge_avoids') for r in rs),
            cancelled_windups=sum(num(r,'blunt_cancelled_windups')+num(r,'opponent_cancelled_windups') for r in rs),
            outcomes=dict(Counter(r['outcome'] for r in rs))))
    return answer

def propose(rows):
    groups = group(rows)
    assert sorted(groups) == COARSE
    values = {31,32}
    transitions = []
    for lo,hi in zip(COARSE,COARSE[1:]):
        counts = Counter()
        examples = []
        for key,a in groups[lo].items():
            b = groups[hi][key]
            changed = [field for field in DISCRETE if a[field] != b[field]]
            if changed and len(examples)<3:
                examples.append(dict(case=key,fields=changed))
            counts.update(changed)
        if counts:
            values.update(range(lo+1,hi))
        transitions.append(dict(lo=lo,hi=hi,changed_fields=dict(counts),examples=examples))
    values.difference_update(COARSE)
    return dict(values=sorted(values),reason='Fill integer gaps in adjacent coarse intervals with observed discrete behavior changes; 31/32 are mandatory.',
        intervals=transitions)

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--final',action='store_true')
    parser.add_argument('--artifacts',action='store_true')
    args=parser.parse_args()
    coarse_path,coarse=read('coarse')
    audit(coarse,args.artifacts)
    proposal=propose(coarse)
    (ROOT/'fine-request.json').write_text(json.dumps(proposal,ensure_ascii=False,indent=2),encoding='utf-8')
    (ROOT/'coarse-analysis.json').write_text(json.dumps(summary(coarse),ensure_ascii=False,indent=2),encoding='utf-8')
    if not args.final:
        print(json.dumps(dict(coarse_folder=coarse_path.name,cases=len(coarse),per_group=len(coarse)//7,
            fine_values=proposal['values'],discrete_changes=[(t['lo'],t['hi'],t['changed_fields']) for t in proposal['intervals']]),ensure_ascii=False,indent=2))
        return
    fine_path,fine=read('fine')
    assert sorted({num(r,'recovery_ticks') for r in fine})==proposal['values']
    rows=coarse+fine
    audit(rows,args.artifacts)
    result=dict(coarse=coarse_path.name,fine=fine_path.name,cases=len(rows),
        ticks=sorted({num(r,'recovery_ticks') for r in rows}),per_tick=len(group(rows)[30]),
        source_fingerprint=rows[0]['source_fingerprint'],outcomes=dict(Counter(r['outcome'] for r in rows)),summary=summary(rows))
    (ROOT/'final-analysis.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    with (ROOT/'combined-summary.csv').open('w',encoding='utf-8-sig',newline='') as f:
        columns=[k for k in rows[0] if not k.startswith('_')]
        writer=csv.DictWriter(f,fieldnames=columns,extrasaction='ignore')
        writer.writeheader();writer.writerows(rows)
    print(json.dumps({k:v for k,v in result.items() if k!='summary'},ensure_ascii=False,indent=2))

if __name__=='__main__':
    main()
