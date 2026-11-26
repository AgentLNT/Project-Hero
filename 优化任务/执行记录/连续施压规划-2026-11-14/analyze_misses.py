from pathlib import Path
import collections,csv,gzip,json,re
ROOT=Path(__file__).resolve().parent
OLD=ROOT.parent/'短组合规划-2026-11-13'
def ident(e,name):return int(re.search(r'\b'+name+r'=[^{]+\{[^}]*Value=(\d+);',e).group(1))
data=json.loads((ROOT/'miss-diagnosis.json').read_text(encoding='utf-8'))
bycase=collections.defaultdict(list)
for row in data['rows']:bycase[row['case']].append(row)
for case,rows in bycase.items():
    with gzip.open(OLD/'formal/cases'/f'{case}.events.csv.gz','rt',encoding='utf-8-sig',newline='') as f:
        events=list(csv.DictReader(f))
    bytick=collections.defaultdict(list)
    for e in events:bytick[int(e['tick'])].append(e['event'])
    for row in rows:
        hit=bytick[row['impact']]
        row['blockEvents']=[e for e in hit if '.BlockResolvedEvent{' in e and ident(e,'AttackPlanId')==row['plan']]
        for motion in row['motion']:
            es=bytick[motion['tick']]
            if any('.DodgeResolvedEvent{' in e and ident(e,'DefenderUnitId')==motion['unit'] for e in es):motion['cause']='dodge'
            elif any('.ForcedDisplacementResolvedEvent{' in e and ident(e,'UnitId')==motion['unit'] for e in es):motion['cause']='forced-displacement'
        if row['blockEvents']:
            assert len(row['blockEvents'])==1
            e=row['blockEvents'][0]
            assert 'AfterBlockDamageQ10=0;' in e and 'RewardsBlock=True;' in e
            row['category']='block-fully-resisted' if re.search(r'Outcome=[^;]*:0;',e) else 'block-contact-other'
        else:
            assert not row['contacts'] and row['targetMoved'] and row['intentPresent']
            row['category']='no-contact-after-target-movement'
summary={'cases':data['cases'],'ticks':data['ticks'],'attacks':data['misses'],
    'categories':dict(collections.Counter(r['category'] for r in data['rows'])),
    'targetMotion':dict(collections.Counter('+'.join(sorted({m['cause'] for m in r['motion'] if m['unit']==r['targetAtImpact']['UnitId']})) or 'none' for r in data['rows'])),
    'noContactOriginalCoverage':dict(collections.Counter(str(r['originalCoverage']) for r in data['rows'] if not r['contacts']))}
(ROOT/'miss-diagnosis-confirmed.json').write_text(json.dumps({'summary':summary,'rows':data['rows']},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False,indent=2))
