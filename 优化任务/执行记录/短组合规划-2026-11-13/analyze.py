"""Read-only reconstruction from committed events and candidate diagnostics.
No combat rules, paths, scores or runtime state are reimplemented here.
"""
from pathlib import Path
import collections,csv,gzip,hashlib,importlib.util,json,re,sys

ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
OLD=ROOT.parent/'温度采样与磨血防御-2026-11-13'

def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def save(name,value):(ROOT/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def rows(path):
    with (gzip.open if str(path).endswith('.gz') else open)(path,'rt',encoding='utf-8-sig',newline='') as f:yield from csv.DictReader(f)
def integer(e,name):return int(re.search(r'\b'+name+r'=(-?\d+);',e).group(1))
def identifier(e,name):return int(re.search(r'\b'+name+r'=[^{]+\{[^}]*Value=(\d+);',e).group(1))
def string(e,name):return re.search(r'\b'+name+r'=\d+:([^;]*);',e).group(1)

def common_analysis():
    # Reuse the already sealed resource-conservation and reaction-outcome audit.
    # All writes are redirected to THIS record; Task 22 remains read-only.
    sealed=json.loads((OLD/'sealed-manifest.json').read_text(encoding='utf-8'))
    assert digest(OLD/'analyze.py')==sealed['analyze.py']
    spec=importlib.util.spec_from_file_location('task22_readonly_analysis',OLD/'analyze.py')
    module=importlib.util.module_from_spec(spec);sys.dont_write_bytecode=True;spec.loader.exec_module(module)
    module.ROOT=ROOT;module.BATCHES=['formal'];module.analyze()

def reconstruct(path,recovery):
    stem=str(path).removesuffix('.input.json')
    source=json.loads(path.read_text(encoding='utf-8-sig'));summary=next(rows(stem+'.summary.csv'))
    plans={};windows={};current=None;damage_by_plan=collections.Counter();c=collections.Counter(cases=1)
    for row in rows(stem+'.events.csv.gz'):
        e=row['event'];tick=int(row['tick']);kind=e.split('{',1)[0].rsplit('.',1)[-1]
        if kind=='TurnWindowOpenedEvent':
            current=dict(id=identifier(e,'WindowId'),owner=identifier(e,'OwnerUnitId'),start=tick,end=tick+integer(e,'DurationTicks'))
            assert current['end']-tick==180;windows[current['id']]=current
        elif kind=='TurnWindowClosedEvent':
            assert current and current['id']==identifier(e,'WindowId');current=None
        elif kind=='ActionPlanCreatedEvent':
            pid=integer(e,'ActionPlanId');owner=integer(e,'OwnerUnitId');origin=integer(e,'Origin')
            assert pid not in plans
            if origin==0:assert current and current['owner']==owner
            plans[pid]=dict(id=pid,owner=owner,origin=origin,type=integer(e,'ActionType'),spec=string(e,'ActionSpecId'),
                created=tick,start=integer(e,'StartTick'),end=integer(e,'EndTick'),window=dict(current) if origin==0 else None,
                started=None,terminal=None,terminalKind=None,damageQ10=0)
        elif kind=='ActionPlanAutoDeferredEvent':
            assert not re.search(r'RipplePlanIds=\[([^]]*)\]',e).group(1).strip()
            p=plans[integer(e,'ActionPlanId')];p['start']=integer(e,'NewStartTick');p['end']=integer(e,'NewEndTick');c['automaticDeferrals']+=1
        elif kind=='ActionPlanLockedEvent':
            p=plans[integer(e,'ActionPlanId')];assert p['origin']==0 and p['started'] is None
            assert p['start']==integer(e,'StartTick')==tick;p['started']=tick
        elif kind in ('ActionPlanCompletedEvent','ActionPlanTerminatedEvent'):
            p=plans[integer(e,'ActionPlanId')];assert p['terminal'] is None
            p['terminal']=integer(e,'TerminalTick');p['terminalKind']=kind
        elif kind=='DamageChannelResolvedEvent':
            damage_by_plan[identifier(e,'AttackPlanId')]+=integer(e,'AfterBlockDamageQ10')
    for pid,damage in damage_by_plan.items():plans[pid]['damageQ10']=damage
    started=[p for p in plans.values() if p['origin']==0 and p['started'] is not None]
    assert len(started)==sum(int(r['starts']) for r in rows(stem+'.actions.csv'))
    assert sum(p['type']==1 for p in started)==int(summary['attackStarts'])
    assert sum(p['type']==4 for p in started)==int(summary['movesStarted'])
    actions=[];cutoff=int(summary['ticks'])
    for p in started:
        w=p['window'];assert w['start']<=p['started']<w['end']
        assert w['end']==(p['started']//180+1)*180
        cross=p['end']>w['end'];observed=cross and (p['terminal'] if p['terminal'] is not None else cutoff)>w['end']
        prevented=cross and p['terminal'] is not None and p['terminal']<=w['end']
        censored=cross and p['terminal'] is None and cutoff<=w['end']
        assert not cross or observed+prevented+censored==1
        impact=p['end']-recovery[p['spec']] if p['type']==1 else None
        for prefix in ('',{1:'Attack:',4:'Move:',5:'Guard:'}[p['type']]):
            c[prefix+'started']+=1;c[prefix+'plannedCrossing']+=cross;c[prefix+'equalBoundary']+=p['end']==w['end']
            c[prefix+'observedCrossing']+=observed;c[prefix+'endedBeforeCrossing']+=prevented;c[prefix+'censored']+=censored
            c[prefix+'impactAtOrAfterWindow']+=impact is not None and impact>=w['end']
        if p['type']==1:
            c['attacksWithPositiveDamage']+=p['damageQ10']>0;c['attacksWithoutPositiveDamage']+=p['damageQ10']<=0
        actions.append(dict(case=summary['case'],**p,plannedCrossing=cross,observedCrossing=observed,
            endedBeforeCrossing=prevented,censored=censored,impact=impact))
    continuations=[];created={(p['created'],p['owner']):p for p in plans.values() if p['origin']==0}
    for e in rows(stem+'.candidates.csv.gz'):
        if e['intentEligible']=='False':
            c['noTacticalEffectEvaluations']+=1;assert int(e['probabilityAfterQ32'] or 0)==0
        if e['sampled']=='True':
            c['sampled:'+e['planKind']]+=1
            if e['score'] and int(e['score'])<0:c['negativeSampled:'+e['planKind']]+=1
        if e['chosen']!='True':continue
        assert e['coverage']=='Complete' and not e['rejection'] and e['intentEligible']=='True'
        if e['type']=='Attack':
            c['sampledAttacksForecastZeroDamage']+=int(e['noDefenseDamageQ10'] or e['damageQ10'] or 0)<=0
        if e['planKind'] not in ('MoveAttack','Approach'):continue
        owner=int(e['key'].split('/')[1]);p=created[(int(e['tick'])+1,owner)];assert p['type']==4
        c['selectedPlanFirstSteps']+=1
        c['firstStepsStarted']+=p['started'] is not None
        c['firstStepsCompleted']+=p['terminalKind']=='ActionPlanCompletedEvent'
        if p['terminal'] is None:
            c['firstStepsStillActiveAtCutoff']+=1;next_plan=None;first_attack=None
        else:
            following=sorted((a for a in started if a['owner']==owner and a['created']>p['created'] and a['started']>=p['terminal']),key=lambda a:a['started'])
            next_plan=following[0] if following else None
            first_attack=next((a for a in following if a['type']==1),None)
        if next_plan:
            c['firstStepsWithObservedNextOrdinaryAction']+=1;c['immediateNextActionIsAttack']+=next_plan['type']==1
            c['immediateNextAttackMatchesProposedSkill']+=next_plan['type']==1 and next_plan['spec']==e['followUpSpec']
            c['immediateNextAttackDamaged']+=next_plan['type']==1 and next_plan['damageQ10']>0
        else:c['noObservedNextActionBeforeCutoff']+=1
        c['firstStepsEventuallyFollowedByAttack']+=first_attack is not None
        c['firstStepsEventuallyFollowedByDamagingAttack']+=first_attack is not None and first_attack['damageQ10']>0
        continuations.append(dict(case=summary['case'],decisionTick=int(e['tick']),kind=e['planKind'],key=e['key'],firstPlanId=p['id'],
            firstEnd=p['end'],firstTerminal=p['terminal'],firstTerminalKind=p['terminalKind'],proposedSkill=e['followUpSpec'],
            predictedFollowRequest=int(e['followUpRequest']),predictedFollowStart=int(e['followUpStart']),
            nextPlan=next_plan,firstSubsequentAttack=first_attack))
    return dict(case=summary['case'],layout=summary['layout'],heroPolicy=source['heroPolicy'],enemyPolicy=source['enemyPolicy'],**c),actions,continuations

def analyze():
    common_analysis()
    export=(ROOT/'harness/FormalAiSourceExport.cs').read_text(encoding='utf-8-sig')
    recovery={key:int(value) for key,_,value in re.findall(r'ActionSpec\(ActionSpecId:new global::ProjectHero\.Logic\.Ids\.ActionSpecId\(value:"([^"]+)"\),Type:[^,]+,Timing:new global::ProjectHero\.Logic\.Actions\.AttackTimingSpec\(BaseWindupTicks:(\d+),RecoveryTicks:(\d+)\)',export)}
    assert len(recovery)==10
    cases=[];actions=[];continuations=[]
    for path in sorted((ROOT/'formal/cases').glob('*.input.json')):
        case,a,p=reconstruct(path,recovery);cases.append(case);actions.extend(a);continuations.extend(p)
    assert len(cases)==54
    totals=collections.Counter();layouts=collections.defaultdict(collections.Counter)
    for c in cases:
        values={k:v for k,v in c.items() if isinstance(v,int) and k not in ('heroPolicy','enemyPolicy')}
        totals.update(values);layouts[c['layout']].update(values)
    common=json.loads((ROOT/'analysis.json').read_text())['formal']['P']
    old=json.loads((OLD/'analysis.json').read_text())['baseline']['D']
    pairs=[]
    for path in sorted((ROOT/'formal/cases').glob('*.input.json')):
        data=json.loads(path.read_text(encoding='utf-8-sig'));s=str(path).removesuffix('.input.json')
        new=next(rows(s+'.summary.csv'));old_path=OLD/'baseline/cases'/('D'+data['Id'][1:] if 'Id' in data else 'D'+path.name[1:].removesuffix('.input.json'))
        before=next(rows(str(old_path)+'.summary.csv'))
        assert all(data[k]==json.loads(Path(str(old_path)+'.input.json').read_text(encoding='utf-8-sig'))[k] for k in ('heroPolicy','enemyPolicy','layout','mirror','seed','metaResource'))
        pairs.append(dict(case=new['case'],layout=new['layout'],newEnded=new['ended']=='True',oldEnded=before['ended']=='True',
            newHits=int(new['hits']),oldHits=int(before['hits']),newMoves=int(new['movesStarted']),oldMoves=int(before['movesStarted']),
            newEffectiveLossQ10=409600-sum(max(0,int(new[k])) for k in ('heroHealthQ10','enemyHealthQ10')),
            oldEffectiveLossQ10=409600-sum(max(0,int(before[k])) for k in ('heroHealthQ10','enemyHealthQ10')),
            newFirstDamage=int(new['firstDamageTick']),oldFirstDamage=int(before['firstDamageTick']),
            newLastProgress=int(new['lastProgressTick']),oldLastProgress=int(before['lastProgressTick'])))
    save('plan-analysis.json',dict(totals=dict(totals),layouts={k:dict(v) for k,v in layouts.items()},cases=cases,
        definitions=dict(crossing='Actual started ordinary plans; planned EndTick > source window end; equality separate; actual terminal/cutoff determines observed crossing.',
            continuation='Only first Move is submitted. Next ordinary action is independently replanned. Later attack counts overlap across several preceding moves; they are not distinct successful combos.',
            eventual='First subsequent observed ordinary attack by the same owner, possibly after more moves; censored at the case cutoff.')))
    save('actions.json',actions);save('continuations.json',continuations);save('paired-comparison.json',pairs)
    save('comparison.json',dict(oldD=old,newP=common,pairedCases=len(pairs),
        casesWithMoreEffectiveDamage=sum(p['newEffectiveLossQ10']>p['oldEffectiveLossQ10'] for p in pairs),
        casesWithMoreHits=sum(p['newHits']>p['oldHits'] for p in pairs)))
    print(json.dumps(dict(oldTimeouts=old['timeouts'],newTimeouts=common['timeouts'],planTotals=dict(totals)),ensure_ascii=False))

def cross_runtime():
    files=sorted((ROOT/'mechanisms').glob('*.canonical.txt'));assert len(files)==27
    assert all(digest(p)==digest(ROOT/'native-mechanisms'/p.name) for p in files)
    save('cross-runtime-check.json',dict(cases=len(files),eventsSnapshotsDecisionsProbabilitiesMemoryAndPlanDiagnosticsEqual=True))

def attack_outcomes():
    actions=json.loads((ROOT/'actions.json').read_text());cases=collections.defaultdict(list)
    for a in actions:
        if a['type']==1:cases[a['case']].append(a)
    details=[];totals=collections.Counter()
    for case,attacks in cases.items():
        contacts=collections.defaultdict(list);dodged=set();clashes=set()
        for row in rows(ROOT/'formal/cases'/(case+'.events.csv.gz')):
            e=row['event'];kind=e.split('{',1)[0].rsplit('.',1)[-1]
            if kind=='DamageChannelResolvedEvent':contacts[identifier(e,'AttackPlanId')].append(e)
            elif kind=='DodgeResolvedEvent':
                values=re.search(r'InvalidatedAttackPlanIds=\[([^]]*)\]',e).group(1)
                dodged.update(int(p) for p in re.findall(r'Value=(\d+);',values))
            elif kind=='ClashParticipantResolvedEvent':clashes.add(identifier(e,'ActionPlanId'))
        summary=next(rows(ROOT/'formal/cases'/(case+'.summary.csv')));cutoff=int(summary['ticks'])
        for a in attacks:
            if a['damageQ10']>0:kind='positive-damage'
            elif a['impact']>=cutoff and a['terminal'] is None:kind='impact-not-observed-at-cutoff'
            elif a['terminal'] is not None and a['terminal']<a['impact']:kind='terminated-before-impact'
            elif a['id'] in dodged:kind='dodge-invalidated-contact'
            elif contacts[a['id']]:kind='resolved-contact-zero-damage'
            elif a['id'] in clashes:kind='no-positive-damage-with-clash'
            else:kind='no-damage-contact-recorded'
            totals[kind]+=1
            details.append(dict(case=case,plan=a['id'],spec=a['spec'],start=a['start'],impact=a['impact'],terminal=a['terminal'],
                terminalKind=a['terminalKind'],damageQ10=a['damageQ10'],category=kind,dodgeInvalidation=a['id'] in dodged,
                clashRecorded=a['id'] in clashes,contactStopCodes=[string(e,'StopReasonCode') for e in contacts[a['id']]]))
    assert sum(totals.values())==1284 and totals['positive-damage']==455
    save('actual-attack-outcomes.json',dict(totals=dict(totals),actions=details,
        interpretation='Disjoint descriptive categories in the listed priority order. No-contact is not a proof of target movement or poor intent; it needs further trajectory investigation.'))
    print(json.dumps(dict(totals),ensure_ascii=False))

if __name__=='__main__':
    {'analyze':analyze,'cross-runtime':cross_runtime,'outcomes':attack_outcomes}[sys.argv[1] if len(sys.argv)>1 else 'analyze']()
