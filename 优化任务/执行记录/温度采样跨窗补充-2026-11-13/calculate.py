"""Read-only analysis of Task 22 sealed canonical events; no simulation or scoring changes."""
from pathlib import Path
import collections,csv,gzip,hashlib,json,re

ROOT=Path(__file__).resolve().parent
SOURCE=ROOT.parent/'温度采样与磨血防御-2026-11-13'
BATCHES=['baseline','extra-seed','greedy','low-temperature','high-temperature','top-p-95','top-p-90']

def rows(path):
    with (gzip.open if str(path).endswith('.gz') else open)(path,'rt',encoding='utf-8-sig',newline='') as f:
        yield from csv.DictReader(f)

def number(e,name):
    return int(re.search(r'\b'+name+r'=(-?\d+);',e).group(1))

def identifier(e,name):
    return int(re.search(r'\b'+name+r'=[^{]+\{[^}]*Value=(\d+);',e).group(1))

def text(e,name):
    return re.search(r'\b'+name+r'=\d+:([^;]*);',e).group(1)

def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()

def run():
    seal=json.loads((SOURCE/'sealed-manifest.json').read_text(encoding='utf-8'))
    export=SOURCE/'harness/FormalAiSourceExport.cs'
    assert sha(export)==seal['harness/FormalAiSourceExport.cs']
    recovery={key:int(value) for key,_,value in re.findall(
        r'ActionSpec\(ActionSpecId:new global::ProjectHero\.Logic\.Ids\.ActionSpecId\(value:"([^"]+)"\),Type:[^,]+,Timing:new global::ProjectHero\.Logic\.Actions\.AttackTimingSpec\(BaseWindupTicks:(\d+),RecoveryTicks:(\d+)\)',
        export.read_text(encoding='utf-8-sig'))}
    assert len(recovery)==10
    groups=collections.defaultdict(collections.Counter)
    details=[]; case_stats=[]; checked=0
    for batch in BATCHES:
        for path in sorted((SOURCE/batch/'cases').glob('*.input.json')):
            stem=str(path).removesuffix('.input.json')
            event_path=Path(stem+'.events.csv.gz')
            for p in (path,event_path,Path(stem+'.summary.csv'),Path(stem+'.owners.csv'),Path(stem+'.actions.csv')):
                assert sha(p)==seal[p.relative_to(SOURCE).as_posix()]
            data=json.loads(path.read_text(encoding='utf-8-sig')); summary=next(rows(stem+'.summary.csv'))
            owners=next(rows(stem+'.owners.csv')); hero=int(owners['heroUnit'])
            plans={}; current=None; windows={}; reactions=0; deferred=0
            for row in rows(event_path):
                tick=int(row['tick']); e=row['event']; kind=e.split('{',1)[0].rsplit('.',1)[-1]
                if kind=='TurnWindowOpenedEvent':
                    wid=identifier(e,'WindowId')
                    current=dict(id=wid,owner=identifier(e,'OwnerUnitId'),opened=tick,end=tick+number(e,'DurationTicks'))
                    assert current['end']-tick==180
                    windows[wid]=current
                elif kind=='TurnWindowClosedEvent':
                    assert current and current['id']==identifier(e,'WindowId')
                    current=None
                elif kind=='ActionPlanCreatedEvent':
                    pid=number(e,'ActionPlanId'); assert pid not in plans
                    owner=number(e,'OwnerUnitId'); origin=number(e,'Origin')
                    if origin==0: assert current and current['owner']==owner
                    else: reactions+=1
                    plans[pid]=dict(id=pid,owner=owner,origin=origin,type=number(e,'ActionType'),spec=text(e,'ActionSpecId'),
                        start=number(e,'StartTick'),end=number(e,'EndTick'),created=tick,
                        window=dict(current) if origin==0 else None,started=None,terminal=None,terminalKind=None)
                elif kind=='ActionPlanAutoDeferredEvent':
                    deferred+=1; p=plans[number(e,'ActionPlanId')]
                    ripple=re.search(r'RipplePlanIds=\[([^]]*)\]',e).group(1)
                    assert not ripple.strip(), 'Nonempty ripple requires a richer schedule reconstruction'
                    p['start']=number(e,'NewStartTick');p['end']=number(e,'NewEndTick')
                elif kind=='ActionPlanLockedEvent':
                    p=plans[number(e,'ActionPlanId')]; assert p['origin']==0 and p['started'] is None
                    assert p['start']==number(e,'StartTick')==tick
                    p['started']=tick
                elif kind in ('ActionPlanCompletedEvent','ActionPlanTerminatedEvent'):
                    p=plans[number(e,'ActionPlanId')]; assert p['terminal'] is None
                    p['terminal']=number(e,'TerminalTick');p['terminalKind']=kind
                    if kind=='ActionPlanTerminatedEvent':p['reason']=text(e,'ReasonCode')
            started=[p for p in plans.values() if p['origin']==0 and p['started'] is not None]
            assert len(started)==sum(int(r['starts']) for r in rows(stem+'.actions.csv'))
            assert sum(p['type']==1 for p in started)==int(summary['attackStarts'])
            assert sum(p['type']==4 for p in started)==int(summary['movesStarted'])
            counters=collections.Counter(cases=1,ordinaryStarted=len(started),reactionPlansExcluded=reactions,automaticDeferrals=deferred)
            cutoff=int(summary['ticks'])
            for p in started:
                w=p['window']; assert w['opened']<=p['started']<w['end']
                # Independent boundary check: this two-unit matrix has fixed alternating 180-Tick windows.
                assert w['end']==(p['started']//180+1)*180
                cross=p['end']>w['end']; equal=p['end']==w['end']
                observed_end=p['terminal'] if p['terminal'] is not None else cutoff
                observed_cross=cross and observed_end>w['end']
                prevented=cross and p['terminal'] is not None and p['terminal']<=w['end']
                censored=cross and p['terminal'] is None and cutoff<=w['end']
                assert not cross or observed_cross+prevented+censored==1
                impact=p['end']-recovery[p['spec']] if p['type']==1 else None
                impact_cross=impact is not None and impact>=w['end']
                record=dict(batch=batch,stage=data['stage'],seed=data['seed'],case=summary['case'],plan=p['id'],owner=p['owner'],
                    role='hero' if p['owner']==hero else 'enemy',type={1:'Attack',4:'Move',5:'Guard'}[p['type']],spec=p['spec'],
                    windowId=w['id'],windowStart=w['opened'],windowEnd=w['end'],start=p['started'],end=p['end'],impact=impact,
                    crosses=cross,equalBoundary=equal,excessTicks=max(0,p['end']-w['end']),observedCross=observed_cross,
                    endedBeforeCrossing=prevented,unresolvedAtCutoff=censored,terminal=p['terminal'],terminalKind=p['terminalKind'],
                    impactAtOrAfterWindow=impact_cross)
                details.append(record)
                for prefix in ('',record['type']+':',record['role']+':'):
                    counters[prefix+'started']+=1;counters[prefix+'crosses']+=cross;counters[prefix+'equalBoundary']+=equal
                    counters[prefix+'observedCross']+=observed_cross;counters[prefix+'endedBeforeCrossing']+=prevented
                    counters[prefix+'unresolvedAtCutoff']+=censored;counters[prefix+'impactAtOrAfterWindow']+=impact_cross
                    if cross:counters[prefix+'totalExcessTicks']+=record['excessTicks']
            counters['casesWithCrossing']=int(any(p['end']>p['window']['end'] for p in started))
            groups[(batch,data['stage'])].update(counters)
            case_stats.append(dict(batch=batch,stage=data['stage'],case=summary['case'],**counters))
            checked+=1
    assert checked==315
    summary=[]
    for (batch,stage),counter in groups.items():
        value=dict(batch=batch,stage=stage,**counter)
        value['crossingRate']=counter['crosses']/counter['ordinaryStarted']
        value['attackCrossingRate']=counter['Attack:crosses']/counter['Attack:started']
        summary.append(value)
    result=dict(definition='Actual started ordinary plans; planned EndTick > scheduled source WindowEndTick. Equality is separate. Actual observed crossing requires actual terminal or observation end > WindowEndTick.',
        source=str(SOURCE),sealedSourceVerified=True,verifiedSourceFiles=checked*5+1,
        independentFixedWindowBoundaryCheck=True,cases=checked,summary=summary)
    (ROOT/'summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (ROOT/'actions.json').write_text(json.dumps(details,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (ROOT/'cases.json').write_text(json.dumps(case_stats,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False))

if __name__=='__main__':run()
