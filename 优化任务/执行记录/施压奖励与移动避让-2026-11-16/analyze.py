"""Audit logged decisions and committed events without rebuilding combat rules."""
from pathlib import Path
import collections,csv,gzip,hashlib,importlib.util,json,re,statistics,sys
ROOT=Path(__file__).resolve().parent
P=ROOT.parent/'短组合规划-2026-11-13'
C=ROOT.parent/'连续施压规划-2026-11-14'
sys.dont_write_bytecode=True
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def save(p,v):(ROOT/p).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def rows(path):
    with (gzip.open if str(path).endswith('.gz') else open)(path,'rt',encoding='utf-8-sig',newline='') as f:yield from csv.DictReader(f)
def prior(folder):
    path=folder/'analyze.py';assert hashlib.sha256(path.read_bytes()).hexdigest()==load(folder/'sealed-manifest.json')['analyze.py']
    spec=importlib.util.spec_from_file_location('prior_analyzer',path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
    return m
def resource_followthrough():
    previous=prior(P)
    source=P/'actions.json'
    assert hashlib.sha256(source.read_bytes()).hexdigest()==load(P/'sealed-manifest.json')[source.name]
    by_case=collections.defaultdict(list)
    for a in load(source):
        if a['type']==1:by_case[a['case']].append(a)
    totals=collections.Counter()
    for case,attacks in sorted(by_case.items()):
        stem=str(P/'formal/cases'/case);cutoff=int(next(rows(stem+'.summary.csv'))['ticks'])
        by_id={a['id']:a for a in attacks};spend={};triggers=[];resets=collections.defaultdict(list);gains=collections.Counter()
        for r in rows(stem+'.events.csv.gz'):
            e=r['event'];tick=int(r['tick'])
            if '.AdrenalineLedgerChangedEvent{' in e:
                u=previous.identifier(e,'UnitId')
                if '.AdrenalineChangeKind:4;' in e:resets[u].append(tick)
                if '.AdrenalineChangeKind:2;' in e:spend[previous.integer(e,'ReservationPlanId')]=previous.integer(e,'ReservationAmount')
                if '.AdrenalineChangeKind:0;' in e:gains[tick,u]+=previous.integer(e,'AvailableAfter')-previous.integer(e,'AvailableBefore')
            if '.ReactionTriggeredEvent{' in e:
                triggers.append((tick,previous.integer(e,'DefenderUnitId'),previous.integer(e,'ReactionActionPlanId'),previous.integer(e,'SourceAttackPlanId')))
        for tick,unit,reply,source_id in triggers:
            if spend.get(reply,0)<=0 or source_id not in by_id:continue
            stop=next((t for t in resets[unit] if t>tick),cutoff)
            later=[a for a in attacks if a['owner']==by_id[source_id]['owner'] and a['started']>tick and a['impact']<stop
                and (a['terminal'] is None or a['terminal']>=a['impact'])]
            totals['spentResponses']+=1
            totals['sameTickGrossSpendExceedsAllLedgerGains']+=spend[reply]>gains[tick,unit]
            totals['sameAttackerReachedLaterImpactTickBeforeDefenderResetOrCutoff']+=bool(later)
            totals['sameAttackerLandedLaterPositiveDamageBeforeDefenderResetOrCutoff']+=any(a['damageQ10']>0 for a in later)
            totals['noNextResetObservedBeforeCutoff']+=stop==cutoff
    save('resource-followthrough.json',{'stage':'P','totals':dict(totals),'limits':
        'Observed follow-through only. The same later attack can occur after several reactions. Next reset, battle end or cutoff ends observation; no causal gain is attributed to spending. Same-Tick gain includes every accrual source.'})
    print('resource follow-through',dict(totals))
def continuation():
    recorded=load(C/'attack-continuations.json');by_case=collections.defaultdict(list)
    for r in recorded:by_case[r['case']].append(r)
    outcome_file=C/'actual-attack-outcomes.json'
    assert hashlib.sha256(outcome_file.read_bytes()).hexdigest()==load(C/'sealed-manifest.json')[outcome_file.name]
    actual_outcomes={(a['case'],a['id']):a['category'] for a in load(outcome_file) if a['stage']=='C'}
    results=[];t=collections.Counter();matched_outcomes=collections.Counter();distinct_matches=set()
    for case,records in sorted(by_case.items()):
        stem=str(C/'formal/cases'/case);summary=next(rows(stem+'.summary.csv'));stop=int(summary['ticks'])
        by_tick={(int(e['tick']),e['controller'],e['key']):e for e in rows(stem+'.candidates.csv.gz')}
        chosen={(tick,e['key'].split('/')[1]):e for (tick,_,_),e in by_tick.items() if e['chosen']=='True' and e['planKind']=='AttackAttack'}
        for r in records:
            t['chosen']+=1
            tick=r['decisionTick'];e=chosen[tick,str(r['first']['owner'])];actual=r['firstSubsequentAttack']
            a=by_tick[tick,e['controller'],e['key'].split('/then/')[0]]
            marginal=int(e['damageQ10'])-int(a['damageQ10']) if a['damageQ10'] else None
            b_impact=int(e['impact']) if r['predictedBStart']>=0 else None
            can_observe=b_impact is not None and b_impact<stop
            observed=actual is not None and actual['created']<=tick+360
            same=observed and actual['spec']==r['proposedB']
            exact=same and actual['created']==r['predictedBRequest'] and actual['start']==r['predictedBStart']
            hit=observed and actual['impact']<=tick+360 and actual['damageQ10']>0
            t['projectedB']+=b_impact is not None;t['projectedImpactBeforeCutoff']+=can_observe;t['subsequentAttackWithinForecast']+=observed
            t['laterAttackHitWithinForecast']+=hit;t['sameSkillWithinForecast']+=same;t['exactSkillRequestStart']+=exact
            t['exactWithPositiveContact']+=exact and hit;t['marginalPositive']+=marginal is not None and marginal>0
            t['marginalPositiveObservable']+=can_observe and marginal is not None and marginal>0
            t['marginalPositiveObservableExactHit']+=can_observe and marginal is not None and marginal>0 and exact and hit
            category=actual_outcomes[case,actual['id']] if actual else None
            if exact:
                matched_outcomes[category if actual['impact']<=tick+360 else 'impact-outside-proposal-horizon']+=1
                distinct_matches.add((case,actual['id']))
            results.append(dict(case=case,decisionTick=tick,firstPlanId=r['first']['id'],firstSpec=r['first']['spec'],proposedB=r['proposedB'],
                expectedRequest=r['predictedBRequest'],expectedStart=r['predictedBStart'],expectedImpact=b_impact,
                singleControlDamageQ10=int(a['damageQ10']) if a['damageQ10'] else None,sequenceControlDamageQ10=int(e['damageQ10']),
                marginalHorizonDamageQ10=marginal,eligibleToObserve=can_observe,actualFirstSubsequentAttack=actual,
                exactCommandTimingAndSkill=exact,subsequentHitWithinForecast=hit,subsequentOutcomeCategory=category))
    assert len(results)==252
    save('continuation-realization.json',{'totals':dict(t),'exactMatchAttackOutcomes':dict(matched_outcomes),
        'distinctExactMatchedActions':len(distinct_matches),'rows':results,'definition':
        'Marginal = no-response sequence HP delta minus same first-request no-response HP delta over the common horizon. Not a direct B contact measurement. Exact match is owner, skill, submit and start; not equality of opponent world. Censored cases and overlap remain.'})
    print('continuation',dict(t))

def analyze(partial=False):
    files=sorted((ROOT/'formal/cases').glob('*.input.json'))
    if not partial:assert len(files)==162
    resource=prior(ROOT.parent/'温度采样与磨血防御-2026-11-13')
    if not partial:resource.ROOT=ROOT;resource.BATCHES=['formal'];resource.analyze()
    previous=prior(P)
    export=(ROOT/'harness/FormalAiSourceExport.cs').read_text(encoding='utf-8-sig')
    recovery={k:int(r) for k,_,r in re.findall(r'ActionSpec\(ActionSpecId:new global::ProjectHero\.Logic\.Ids\.ActionSpecId\(value:"([^"]+)"\),Type:[^,]+,Timing:new global::ProjectHero\.Logic\.Actions\.AttackTimingSpec\(BaseWindupTicks:(\d+),RecoveryTicks:(\d+)\)',export)}
    assert len(recovery)==10
    stages={s:collections.Counter() for s in ('Z','M','B')};cases=collections.defaultdict(dict);divergences=[];p_decisions={}
    def submissions(stem):
        return {(e['tick'],e['controller']):e['submittedKey'] for e in rows(stem+'.decisions.csv.gz') if e['submittedKey']}
    def metrics(s):
        loss=[204800-max(0,int(s[k])) for k in ('heroHealthQ10','enemyHealthQ10')]
        return {'ended':s['ended']=='True','ticks':int(s['ticks']),'hits':int(s['hits']),
            'effectiveHealthLossQ10':sum(loss),'lowerSideDamageQ10':min(loss),'layout':s['layout'],
            'firstDamageTick':int(s['firstDamageTick']),'lastDamageTick':int(s['lastDamageTick'])}
    for path in files:
        source=load(path);stage=source['stage'];t=stages[stage];stem=str(path).removesuffix('.input.json');pair=path.name[1:].removesuffix('.input.json')
        old=P/'formal/cases'/('P'+path.name[1:]);baseline=load(old)
        assert all(source[k]==baseline[k] for k in ('heroPolicy','enemyPolicy','layout','mirror','seed','metaResource','sourceHash','contentFingerprint','temperature','topP','maxTicks'))
        summary=next(rows(stem+'.summary.csv'));cases[pair]['P']=metrics(next(rows(str(old).removesuffix('.input.json')+'.summary.csv')))
        cases[pair][stage]=metrics(summary)
        last_hit=int(summary['lastDamageTick']);cutoff=int(summary['ticks'])
        t['noPositiveDamageInFinalHalf']+=summary['ended']=='False' and last_hit<cutoff//2
        rec,actions,_=previous.reconstruct(path,recovery)
        t['started']+=rec['started'];t['plannedCrossing']+=rec.get('plannedCrossing',0);t['observedCrossing']+=rec.get('observedCrossing',0)
        t['attacksPlannedCrossing']+=rec.get('Attack:plannedCrossing',0);t['movesImmediatelyFollowedByAttack']+=rec.get('immediateNextActionIsAttack',0)
        t['moveAttackSelected']+=rec.get('sampled:MoveAttack',0);t['approachSelected']+=rec.get('sampled:Approach',0)
        t['attackImpactAtOrAfterWindow']+=rec.get('Attack:impactAtOrAfterWindow',0)
        t['positiveAttackWithImpactAtOrAfterWindow']+=sum(a['type']==1 and a['impact']>=a['window']['end'] and a['damageQ10']>0 for a in actions)
        by_first={(a['created'],a['owner']):a for a in actions}
        old_stem=str(old).removesuffix('.input.json')
        if pair not in p_decisions:p_decisions[pair]=submissions(old_stem)
        before=p_decisions[pair];after=submissions(stem)
        first_change=next((k for k in sorted(set(before)|set(after),key=lambda k:(int(k[0]),k[1])) if before.get(k)!=after.get(k)),None)
        t['casesWithDifferentOrdinaryOrReactionChoice']+=first_change is not None
        trace=None
        if first_change:
            old_key=before.get(first_change);new_key=after.get(first_change)
            trace=dict(stage=stage,case=summary['case'],tick=int(first_change[0]),controller=first_change[1],oldKey=old_key,newKey=new_key)
            if old_key:
                old_candidate=next(e for e in rows(old_stem+'.candidates.csv.gz')
                    if (e['tick'],e['controller'])==first_change and e['key']==old_key)
                trace.update(oldChosenScore=int(old_candidate['score']),oldPressureUtility=int(old_candidate['pressureUtility']),
                    oldDamageQ10=int(old_candidate['damageQ10'] or 0))
            divergences.append(trace)
        blocks=set();dodges=set();interrupted=set()
        for row in rows(stem+'.events.csv.gz'):
            e=row['event']
            if '.BlockResolvedEvent{' in e and 'AfterBlockDamageQ10=0;' in e and 'RewardsBlock=True;' in e:blocks.add(previous.identifier(e,'AttackPlanId'))
            if '.DodgeResolvedEvent{' in e:dodges.update(map(int,re.findall(r'Value=(\d+);',re.search(r'InvalidatedAttackPlanIds=\[([^]]*)\]',e).group(1))))
            if '.ClashParticipantResolvedEvent{' in e:interrupted.add(previous.identifier(e,'ActionPlanId'))
        for a in actions:
            if a['type']!=1:continue
            kind='hit' if a['damageQ10']>0 else 'censored' if a['impact']>=int(summary['ticks']) and a['terminal'] is None else (
                'terminated-early' if a['terminal'] is not None and a['terminal']<a['impact'] else 'block' if a['id'] in blocks else (
                    'dodge' if a['id'] in dodges else 'clash' if a['id'] in interrupted else 'other-no-positive-damage'))
            t['attack:'+kind]+=1
        boundaries=collections.defaultdict(lambda:[False,False,False,False,False])
        for e in rows(stem+'.candidates.csv.gz'):
            n=int(e['evasionReplies']);cap=e['evasionScoreCap'];assert n<=2
            d=boundaries[e['tick'],e['controller']]
            d[0]|=e['key']=='WAIT/ORDINARY' and e['sampled']=='True'
            has_control_hit=(e['type']=='Attack' or e['planKind']=='MoveAttack') and bool(e['score']) and e['intentEligible']=='True' and int(e['damageQ10'] or 0)>0
            d[1]|=has_control_hit
            d[2]|=bool(e['score']) and e['intentEligible']=='True' and not e['key'].startswith('R/') and int(e['score'])>0
            d[3]|=e['key']=='WAIT/ORDINARY'
            d[4]|=has_control_hit and int(e['damageUtility'])<0 and ':True:' in e['targets']
            if stage=='Z':assert n==0 and not cap
            if stage in ('Z','B'):assert int(e['pressureUtility'])==0
            if n:t['evasionEvaluations']+=1;t['evasionReplies']+=n
            if trace is not None and (e['tick'],e['controller'])==first_change:
                if trace['oldKey']==e['key']:
                    trace.update(newScoreOfOldChoice=int(e['score']) if e['score'] else None,evasionReplies=n,evasionScoreCap=int(cap) if cap else None)
                if e['sampled']=='True':trace.update(newSampledKey=e['key'],newSampledScore=int(e['score']))
            if cap:
                t['damageReducingEvasionEvaluations']+=1
                if e['score']:assert int(e['score'])<=int(cap)
            if e['chosen']=='True' and n:
                t['evasionConsideredChosen']+=1;t['damageReducingEvasionChosen']+=bool(cap)
                a=by_first.get((int(e['tick'])+1,int(e['key'].split('/')[1])))
                t['evasionConsideredChosenFirstNotStarted']+=a is None
                if e['planKind']=='DirectAttack':
                    t['evasionConsideredDirectSelected']+=1
                    t['evasionConsideredDirectHit']+=a is not None and a['damageQ10']>0
        t['ordinarySamplingBoundaries']+=sum(ordinary for _,_,_,ordinary,_ in boundaries.values())
        t['sampledOrdinaryWait']+=sum(wait for wait,_,_,_,_ in boundaries.values())
        t['waitWithControlHitAndNoPositiveOrdinaryScore']+=sum(wait and hit and not positive for wait,hit,positive,_,_ in boundaries.values())
        t['waitWithNegativeDamageForLegalReactionAndNoPositiveOrdinaryScore']+=sum(wait and signed and not positive for wait,_,positive,_,signed in boundaries.values())
        t['waitWithoutControlHit']+=sum(wait and not hit for wait,hit,_,_,_ in boundaries.values())
        if trace is not None and trace.get('newScoreOfOldChoice') is not None:
            t['firstDifferentChoiceOldPositiveNewNonpositive']+=trace['oldChosenScore']>0>=trace['newScoreOfOldChoice']
            t['firstDifferentChoiceOldScoreReducedByEvasion']+=bool(stage=='M' and trace.get('evasionScoreCap') is not None
                and trace['newScoreOfOldChoice']<trace['oldChosenScore'])
    print('stages',{s:dict(v) for s,v in stages.items()})
    if partial:return
    assert len(cases)==54 and all(set(r)=={'P','Z','M','B'} for r in cases.values())
    totals={'P':load(P/'analysis.json')['formal']['P'],**load(ROOT/'analysis.json')['formal']}
    base=load(P/'plan-analysis.json')['totals']
    stages['P']=dict(started=base['started'],plannedCrossing=base['plannedCrossing'],observedCrossing=base['observedCrossing'],
        attacksPlannedCrossing=base['Attack:plannedCrossing'],attackImpactAtOrAfterWindow=base['Attack:impactAtOrAfterWindow'],
        positiveAttackWithImpactAtOrAfterWindow=sum(a['type']==1 and a['impact']>=a['window']['end'] and a['damageQ10']>0 for a in load(P/'actions.json')),
        noPositiveDamageInFinalHalf=sum(not a['ended'] and a['lastDamageTick']<a['ticks']//2 for p in cases.values() for a in [p['P']]),
        ordinarySamplingBoundaries=0,sampledOrdinaryWait=0)
    for path in (P/'formal/cases').glob('*.candidates.csv.gz'):
        for e in rows(path):
            if e['key']=='WAIT/ORDINARY':
                stages['P']['ordinarySamplingBoundaries']+=1
                stages['P']['sampledOrdinaryWait']+=e['sampled']=='True'
    def delta(a,b):
        result=collections.Counter()
        for r in cases.values():
            diff=r[b]['effectiveHealthLossQ10']-r[a]['effectiveHealthLossQ10'];result['moreHP' if diff>0 else 'lessHP' if diff<0 else 'equalHP']+=1
            result['onlyAEnded']+=r[a]['ended'] and not r[b]['ended'];result['onlyBEnded']+=r[b]['ended'] and not r[a]['ended']
        return dict(result)
    breakdown=collections.defaultdict(collections.Counter)
    ended_loss=collections.defaultdict(list)
    timeout_metrics=collections.defaultdict(list)
    for pack in cases.values():
        for stage,v in pack.items():
            b=breakdown[stage+'/'+v['layout']];b['cases']+=1;b['ended']+=v['ended'];b['hits']+=v['hits']
            b['effectiveHealthLossQ10']+=v['effectiveHealthLossQ10'];b['singleSidedDamageCases']+=v['lowerSideDamageQ10']==0
            if v['ended']:ended_loss[stage].append(v['lowerSideDamageQ10'])
            else:timeout_metrics[stage].append(v)
    save('comparison.json',{'totals':totals,'paired':{a+'to'+b:delta(a,b) for a,b in [('P','Z'),('P','M'),('Z','B'),('M','B'),('P','B')]},
        'byLayout':{s:dict(v) for s,v in breakdown.items()},
        'naturalEndWinnerHpLoss':{s:{'cases':len(v),'zeroLoss':v.count(0),'medianQ10':statistics.median(v),
            'below25Hp':sum(0<x<25*1024 for x in v)} for s,v in ended_loss.items()},
        'timeoutDetails':{s:{'cases':len(v),'medianLastHitTick':statistics.median(x['lastDamageTick'] for x in v),
            'medianLastHitGap':statistics.median(x['ticks']-x['lastDamageTick'] for x in v),
            'medianLowerRemainingHpQ10':statistics.median(204800-x['effectiveHealthLossQ10']+x['lowerSideDamageQ10'] for x in v)}
            for s,v in timeout_metrics.items()},
        'diagnostics':{s:dict(v) for s,v in stages.items()},'cases':cases})
    save('first-selection-divergence.json',divergences)
    continuation()
    resource_followthrough()
if __name__=='__main__':
    if sys.argv[1:]==['continuation']:continuation()
    elif sys.argv[1:]==['followthrough']:resource_followthrough()
    else:analyze(partial=sys.argv[1:]==['partial'])
