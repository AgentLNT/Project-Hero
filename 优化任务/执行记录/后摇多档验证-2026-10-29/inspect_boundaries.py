"""Paired integer-transition evidence from exported native-engine facts."""
import csv
import json
import re
from collections import Counter, defaultdict

from analyze_recovery import ROOT, DISCRETE, group, num, read

BEHAVIOR = {
    'ActionPlanLockedEvent', 'ActionPlanTerminatedEvent', 'ActionPlanCompletedEvent',
    'ClashParticipantResolvedEvent', 'ClashResidualImpactResolvedEvent',
    'DamageChannelResolvedEvent', 'ReactionTriggeredEvent', 'DodgeResolvedEvent',
    'GuardPartiallyResistedEvent', 'BlockResolvedEvent', 'UnitDiedEvent',
}


def base(row):
    return ROOT / row['_folder'] / row['scenario']


def events(row, behavior=False):
    ticks = defaultdict(list)
    with base(row).with_suffix('.events.jsonl').open(encoding='utf-8-sig') as f:
        for line in f:
            value = json.loads(line)
            event = value['event']
            name = event.split('{', 1)[0].rsplit('.', 1)[-1]
            if behavior and (value['tick'] <= num(row,'opening_impact') or name not in BEHAVIOR):
                continue
            ticks[value['tick']].append(re.sub(r'Sequence=\d+;', '', event))
    return ticks


def first_difference(a,b,behavior=False):
    left,right = events(a,behavior),events(b,behavior)
    for tick in sorted(set(left)|set(right)):
        if left[tick] != right[tick]:
            return dict(tick=tick,left=left[tick],right=right[tick])
    return None


def actions(row):
    with base(row).with_suffix('.actions.csv').open(encoding='utf-8-sig',newline='') as f:
        return list(csv.DictReader(f))


def main():
    _,coarse = read('coarse')
    _,fine = read('fine')
    groups = group(coarse+fine)
    assert sorted(groups) == list(range(30,61))
    transitions = []
    witnesses = []
    selected = set()

    def witness(lo,hi,key,why):
        identity = (lo,hi,key)
        if identity in selected:
            return
        selected.add(identity)
        a,b = groups[lo][key],groups[hi][key]
        fields = [k for k in a if not k.startswith('_') and a[k]!=b[k]]
        witnesses.append(dict(lower=lo,upper=hi,case=key,why=why,
            changes={k:[a[k],b[k]] for k in fields if k not in ['scenario','definition_hash','recovery_ticks','delta']},
            first_event_difference=first_difference(a,b),
            first_post_clash_behavior_difference=first_difference(a,b,True),
            lower_actions=actions(a),upper_actions=actions(b),
            lower_prefix=str(base(a).relative_to(ROOT)),upper_prefix=str(base(b).relative_to(ROOT))))

    for lo in range(30,60):
        counts = Counter()
        for key,a in groups[lo].items():
            b = groups[lo+1][key]
            fields = [k for k in DISCRETE if a[k] != b[k]]
            counts.update(fields)
            if lo==30 and key=='ProbeSlash_Repeat_b2_s0_t329_p0_h24_r10240_short0_wall0':
                witness(lo,lo+1,key,'Last legal source-window start is crossed by +1 Tick.')
            if lo==58 and key=='ProbeSlash_Repeat_b1_s0_t213_p0_h24_r10240_short0_wall0':
                witness(lo,lo+1,key,'Early-window repeated attack count boundary.')
            if lo==42 and key=='ProbeSlash_Repeat_b1_s0_t213_p0_h24_r10240_short0_wall0':
                witness(lo,lo+1,key,'A longer recovery loses an additional direct hit by the observation horizon.')
            if lo==31 and key=='HeavyQuick_Repeat_b1_s0_t273_p0_h24_r10240_short0_wall0':
                witness(lo,lo+1,key,'Actual heavy/quick exchange changes even between the two mandatory fine-scan values.')
            if lo==39 and key.startswith(('HeavyQuick_Repeat_b1','HeavySweep_Repeat_b1')) and any(
                a[field]!=b[field] for field in ['blunt_dealt_q10','opponent_dealt_q10']) and not any(w['why'].startswith('Moderate') for w in witnesses):
                witness(lo,lo+1,key,'Moderate recovery enters a different actual-action exchange regime.')
            if len([w for w in witnesses if w['why'].startswith('Paid')])<4 and 'blunt_block_uses' in fields:
                witness(lo,lo+1,key,'Paid reaction changes with timing, using naturally earned resources.')
            if len([w for w in witnesses if w['why'].startswith('Control')])<3 and any('cancelled_windups' in k for k in fields):
                witness(lo,lo+1,key,'Control cancellation changes an accepted follow-up windup.')
        transitions.append(dict(lower=lo,upper=lo+1,changed_fields=dict(counts)))
    result = dict(transitions=transitions,witnesses=witnesses)
    (ROOT/'integer-boundaries.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(adjacent_comparisons=len(transitions),witnesses=len(witnesses),
        discrete_boundaries=[t['upper'] for t in transitions if t['changed_fields']]),ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
