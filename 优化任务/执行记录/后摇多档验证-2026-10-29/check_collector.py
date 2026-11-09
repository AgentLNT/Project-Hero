"""Confirm that repairing diagnostic metadata did not change native combat results."""
import csv
import hashlib
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parent
PILOT=ROOT/'coarse-20261029-100103Z'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    final=ROOT/(ROOT/'latest-coarse.txt').read_text(encoding='utf-8-sig').strip()
    with (PILOT/'summary.csv').open(encoding='utf-8-sig',newline='') as f:
        cases=[r['scenario'] for r in csv.DictReader(f)]
    replay_same=events_same=action_changed=0
    for case in cases:
        assert (final/(case+'.heroReplay')).exists(),case
        replay_same+=digest(PILOT/(case+'.heroReplay'))==digest(final/(case+'.heroReplay'))
        events_same+=digest(PILOT/(case+'.events.jsonl'))==digest(final/(case+'.events.jsonl'))
        action_changed+=digest(PILOT/(case+'.actions.csv'))!=digest(final/(case+'.actions.csv'))
    result=dict(pilot=PILOT.name,final=final.name,matched_inputs=len(cases),
        byte_identical_native_replays=replay_same,byte_identical_event_streams=events_same,
        repaired_action_exports=action_changed,new_boundary_cases=28,
        note='Pilot is excluded from final counts. The repair only fills archived-plan diagnostic timing/window fields.')
    assert replay_same==len(cases) and events_same==len(cases),result
    (ROOT/'collector-recheck.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
