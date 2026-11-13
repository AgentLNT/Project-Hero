"""Independent CSV/canonical-event cross-check; does not execute or replace combat rules."""
from pathlib import Path
import collections, csv, gzip, hashlib, json, re

ROOT=Path(__file__).resolve().parent
def rows(p):
    with (gzip.open if str(p).endswith('.gz') else open)(p,'rt',encoding='utf-8-sig',newline='') as f:
        return list(csv.DictReader(f))
def integer(event, name):
    return int(re.search(r'\b'+name+r'=(-?\d+);',event).group(1))

inputs=sorted((ROOT/'cases').glob('*.input.json'))
assert len(inputs)==54
actual=[]; total_ticks=0; event_count=0; resource_totals=collections.Counter(); effective_loss=0; overkill=0
for path in inputs:
    stem=path.with_name(path.name.removesuffix('.input.json'))
    source=json.loads(path.read_text(encoding='utf-8-sig'))
    summary=rows(str(stem)+'.summary.csv')[0]
    assert source['maxTicks']==3600 and summary['ended']=='True'
    assert 1800<int(summary['ticks'])<=3600
    hp=int(summary['heroHealthQ10'])+int(summary['enemyHealthQ10'])
    assert 400*1024-hp==int(summary['healthLossQ10'])  # This matrix has no healing/spawns.
    effective=400*1024-sum(max(0,int(summary[k])) for k in ('heroHealthQ10','enemyHealthQ10'))
    effective_loss+=effective; overkill+=int(summary['healthLossQ10'])-effective
    if summary['result']=='victory': assert int(summary['enemyHealthQ10'])<=0 and int(summary['heroHealthQ10'])>0
    elif summary['result']=='defeat': assert int(summary['heroHealthQ10'])<=0 and int(summary['enemyHealthQ10'])>0
    else: raise AssertionError(summary['result'])
    events=rows(str(stem)+'.events.csv.gz'); event_count+=len(events)
    damages=[e for e in events if e['event'].startswith('ProjectHero.Logic.Events.DamageChannelResolvedEvent{')
        and integer(e['event'],'AfterBlockDamageQ10')>0]
    assert len(damages)==int(summary['hits'])
    # The kernel permits negative HP: event damage equals raw snapshot decrease, including overkill.
    assert sum(integer(e['event'],'AfterBlockDamageQ10') for e in damages)==int(summary['healthLossQ10'])
    assert int(damages[0]['tick'])==int(summary['firstDamageTick'])
    assert int(damages[-1]['tick'])==int(summary['lastDamageTick'])
    assert int(summary['lastDamageTick'])+1==int(summary['ticks'])
    ledger=collections.defaultdict(collections.Counter)
    for e in events:
        event=e['event']
        if not event.startswith('ProjectHero.Logic.Events.AdrenalineLedgerChangedEvent{'): continue
        unit=re.search(r'UnitId=[^{]+\{[^}]*Value=(\d+);',event).group(1)
        kind=int(re.search(r'ChangeKind=[^;]+:(\d+);',event).group(1))
        if kind==0: ledger[unit]['actualAccrued']+=integer(event,'AvailableAfter')-integer(event,'AvailableBefore')
        elif kind==2: ledger[unit]['consumed']+=integer(event,'ReservationAmount')
        elif kind==3: ledger[unit]['refunded']+=integer(event,'AvailableAfter')-integer(event,'AvailableBefore')
        elif kind==4: ledger[unit]['expiredAtCycleReset']+=integer(event,'AvailableBefore'); ledger[unit]['cycles']+=1
        elif kind==5: resource_totals['battleEndCleared']+=integer(event,'AvailableBefore')-integer(event,'AvailableAfter')
    for row in rows(str(stem)+'.resources.csv'):
        for key in ('actualAccrued','consumed','refunded','expiredAtCycleReset','cycles'):
            assert int(row[key])==ledger[row['unit']][key],(stem.name,key)
            resource_totals[key]+=int(row[key])
    initial=ROOT/'pre-player-port-fixture-fix/cases'/(stem.name+'.summary.csv')
    assert rows(initial)[0]==summary  # Test-only fixture fix must not alter a battle result.
    for suffix in ('.candidates.csv.gz','.decisions.csv.gz','.events.csv.gz'):
        old=ROOT/'pre-player-port-fixture-fix/cases'/(stem.name+suffix)
        with gzip.open(str(stem)+suffix,'rb') as f: new_hash=hashlib.sha256(f.read()).hexdigest()
        with gzip.open(old,'rb') as f: old_hash=hashlib.sha256(f.read()).hexdigest()
        assert new_hash==old_hash,(stem.name,suffix)
    total_ticks+=int(summary['ticks']); actual.append(summary['case'])
assert resource_totals['actualAccrued']+resource_totals['refunded']==sum(resource_totals[k] for k in ('consumed','expiredAtCycleReset','battleEndCleared'))
report=dict(cases=len(actual),ticks=total_ticks,canonicalEvents=event_count,terminalHealthVerified=True,
    effectiveHealthLossQ10=effective_loss,overkillQ10=overkill,
    damageAndLedgerCsvRebuiltIndependently=True,testOnlyFixBattleAndDiagnosticBytesUnchanged=True,
    resources=dict(resource_totals))
(ROOT/'independent-csv-check.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
