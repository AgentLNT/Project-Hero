from pathlib import Path
import collections,csv,gzip,json
ROOT=Path(__file__).resolve().parent
out=collections.defaultdict(collections.Counter)
for path in (ROOT/'formal/cases').glob('*.candidates.csv.gz'):
    stage=path.name[0]
    with gzip.open(path,'rt',encoding='utf-8-sig',newline='') as f:
        for e in csv.DictReader(f):
            if e['chosen']!='True' or e['type']!='Attack':continue
            c=out[stage];c['chosenAttacks']+=1
            zero=int(e['damageUtility'])==0
            c['worstBranchDamageUtilityZero']+=zero
            c['positivePressureOnZeroDamageUtility']+=zero and int(e['pressureUtility'])>0
            if e['planKind']=='AttackAttack':
                c['selectedAttackAttack']+=1;c['attackAttackZeroDamageUtility']+=zero
                c['attackAttackBaseNonpositiveBeforePressure']+=int(e['score'])-int(e['pressureUtility'])<=0
result={k:dict(v) for k,v in out.items()}
(ROOT/'pressure-contribution.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(result)
