"""Independent calculations from sealed/public events and CSVs; no combat simulation here."""
from pathlib import Path
import collections,csv,gzip,hashlib,importlib.util,json,re,sys
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
OLD=ROOT.parent/'短组合规划-2026-11-13'
sys.dont_write_bytecode=True
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def save(n,v):(ROOT/n).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def rows(p):
    with (gzip.open if str(p).endswith('.gz') else open)(p,'rt',encoding='utf-8-sig',newline='') as f:yield from csv.DictReader(f)
def module(folder,name):
    seal=json.loads((folder/'sealed-manifest.json').read_text(encoding='utf-8'))
    assert sha(folder/'analyze.py')==seal['analyze.py']
    spec=importlib.util.spec_from_file_location(name,folder/'analyze.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
    m.ROOT=ROOT;return m
def analyze():
    m22=module(ROOT.parent/'温度采样与磨血防御-2026-11-13','sealed_task22');m22.BATCHES=['formal'];m22.analyze()
    m23=module(OLD,'sealed_task23')
    export=(ROOT/'harness/FormalAiSourceExport.cs').read_text(encoding='utf-8-sig')
    recovery={k:int(r) for k,_,r in re.findall(r'ActionSpec\(ActionSpecId:new global::ProjectHero\.Logic\.Ids\.ActionSpecId\(value:"([^"]+)"\),Type:[^,]+,Timing:new global::ProjectHero\.Logic\.Actions\.AttackTimingSpec\(BaseWindupTicks:(\d+),RecoveryTicks:(\d+)\)',export)}
    assert len(recovery)==10
    totals=collections.defaultdict(collections.Counter);actions=[];continuations=[];cases=[];extra=[];outcomes=[];pairs=[]
    for path in sorted((ROOT/'formal/cases').glob('*.input.json')):
        source=json.loads(path.read_text(encoding='utf-8-sig'));stage=source['stage'];stem=str(path).removesuffix('.input.json')
        summary=next(rows(stem+'.summary.csv'))
        case,aa,pp=m23.reconstruct(path,recovery);actions.extend(aa);continuations.extend(pp);cases.append(dict(stage=stage,**case))
        totals[stage].update({k:v for k,v in case.items() if isinstance(v,int) and k not in ('heroPolicy','enemyPolicy')})
        created={(a['created'],a['owner']):a for a in aa if a['origin']==0}
        for e in rows(stem+'.candidates.csv.gz'):
            if e['planKind']!='AttackAttack':continue
            t=totals[stage];t['attackAttackEvaluated']+=1;t['attackAttackCovered']+=e['coverage']=='Complete'
            t['attackAttackWithTwoReplyBranches']+=int(e['twoReplyBranches'])>0
            t['attackAttackChosen']+=e['chosen']=='True'
            if e['chosen']!='True':continue
            owner=int(e['key'].split('/')[1]);a=created[(int(e['tick'])+1,owner)]
            t['attackAttackFirstStarted']+=a['started'] is not None;t['attackAttackFirstCompleted']+=a['terminalKind']=='ActionPlanCompletedEvent'
            t['attackAttackFirstDamaged']+=a['damageQ10']>0
            after=sorted((p for p in aa if p['owner']==owner and p['created']>a['created'] and a['terminal'] is not None
                and p['started']>=a['terminal']),key=lambda p:p['started'])
            attack=next((p for p in after if p['type']==1),None)
            if not after:t['attackAttackNoObservedNextAction']+=1
            if attack:
                index=after.index(attack);t['attackAttackNextAttackObserved']+=1
                t['attackAttackImmediateAttack']+=index==0
                t['attackAttackOneMoveThenAttack']+=index==1 and after[0]['type']==4
                t['attackAttackSameProposedSkill']+=attack['spec']==e['followUpSpec']
                t['attackAttackLaterAttackDamaged']+=attack['damageQ10']>0
            extra.append(dict(case=summary['case'],decisionTick=int(e['tick']),first=a,proposedB=e['followUpSpec'],
                predictedBRequest=int(e['followUpRequest']),predictedBStart=int(e['followUpStart']),nextActions=after[:3],firstSubsequentAttack=attack,
                interpretation='Only A was committed. Every later command was independently replanned; continuation rows may overlap.'))
        damage=collections.Counter();blocks=set();dodged=set();clash=set()
        for er in rows(stem+'.events.csv.gz'):
            e=er['event']
            if '.BlockResolvedEvent{' in e and 'AfterBlockDamageQ10=0;' in e and 'RewardsBlock=True;' in e:blocks.add(m23.identifier(e,'AttackPlanId'))
            if '.DodgeResolvedEvent{' in e:
                dodged.update(map(int,re.findall(r'Value=(\d+);',re.search(r'InvalidatedAttackPlanIds=\[([^]]*)\]',e).group(1))))
            if '.ClashParticipantResolvedEvent{' in e:clash.add(m23.identifier(e,'ActionPlanId'))
        for a in aa:
            if a['type']!=1:continue
            if a['damageQ10']>0:kind='positive-damage'
            elif a['impact']>=int(summary['ticks']) and a['terminal'] is None:kind='impact-not-observed-at-cutoff'
            elif a['terminal'] is not None and a['terminal']<a['impact']:kind='terminated-before-impact'
            elif a['id'] in blocks:kind='block-fully-resisted'
            elif a['id'] in dodged:kind='dodge-invalidated-contact'
            elif a['id'] in clash:kind='clash-without-positive-damage'
            else:kind='no-positive-damage-other-contact-or-miss'
            totals[stage]['attackOutcome:'+kind]+=1;outcomes.append(dict(stage=stage,category=kind,**a))
        if stage=='C':
            vpath=Path(str(path).replace('/C-T','/V-T').replace('\\C-T','\\V-T'))
            vin=json.loads(vpath.read_text(encoding='utf-8-sig'));vs=next(rows(str(vpath).removesuffix('.input.json')+'.summary.csv'))
            ppath=OLD/'formal/cases'/('P'+path.name[1:]);pin=json.loads(ppath.read_text(encoding='utf-8-sig'));ps=next(rows(str(ppath).removesuffix('.input.json')+'.summary.csv'))
            assert all(source[k]==vin[k]==pin[k] for k in ('heroPolicy','enemyPolicy','layout','mirror','seed','metaResource','sourceHash','contentFingerprint'))
            def metrics(s):return dict(ended=s['ended']=='True',hits=int(s['hits']),ticks=int(s['ticks']),moves=int(s['movesStarted']),attacks=int(s['attackStarts']),
                effectiveHealthLossQ10=409600-sum(max(0,int(s[k])) for k in ('heroHealthQ10','enemyHealthQ10')))
            pairs.append(dict(case=summary['case'],layout=summary['layout'],P=metrics(ps),V=metrics(vs),C=metrics(summary)))
    assert len(cases)==108 and len(pairs)==54
    report=json.loads((ROOT/'analysis.json').read_text(encoding='utf-8'))['formal']
    def compare(a,b):return dict(cases=len(pairs),moreEffectiveDamage=sum(p[b]['effectiveHealthLossQ10']>p[a]['effectiveHealthLossQ10'] for p in pairs),
        lessEffectiveDamage=sum(p[b]['effectiveHealthLossQ10']<p[a]['effectiveHealthLossQ10'] for p in pairs),equalEffectiveDamage=sum(p[b]['effectiveHealthLossQ10']==p[a]['effectiveHealthLossQ10'] for p in pairs),
        moreHits=sum(p[b]['hits']>p[a]['hits'] for p in pairs),lessHits=sum(p[b]['hits']<p[a]['hits'] for p in pairs),
        endedOnlyAfter=sum(not p[a]['ended'] and p[b]['ended'] for p in pairs),endedOnlyBefore=sum(p[a]['ended'] and not p[b]['ended'] for p in pairs))
    save('paired-comparison.json',pairs)
    save('comparison.json',dict(P=json.loads((OLD/'analysis.json').read_text(encoding='utf-8'))['formal']['P'],V=report['V'],C=report['C'],
        PtoV=compare('P','V'),VtoC=compare('V','C'),PtoC=compare('P','C')))
    save('plan-analysis.json',dict(totals={k:dict(v) for k,v in totals.items()},cases=cases))
    save('actions.json',actions);save('move-continuations.json',continuations);save('attack-continuations.json',extra);save('actual-attack-outcomes.json',outcomes)
    print(json.dumps({'planTotals':{k:dict(v) for k,v in totals.items()},'VtoC':compare('V','C')},ensure_ascii=False))
def cross_runtime():
    files=list((ROOT/'mechanisms').glob('*.canonical.txt'));assert len(files)==54
    assert all(sha(p)==sha(ROOT/'native-mechanisms'/p.name) for p in files)
    save('cross-runtime-check.json',dict(mechanisms=54,canonicalBytesEqual=True))
    print('native/standalone mechanisms: 54 equal')
if __name__=='__main__':{'analyze':analyze,'cross-runtime':cross_runtime}[sys.argv[1] if len(sys.argv)>1 else 'analyze']()
