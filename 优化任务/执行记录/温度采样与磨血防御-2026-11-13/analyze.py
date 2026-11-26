"""Independent evidence checks. Reads canonical events/CSVs; never implements combat rules."""
from pathlib import Path
import argparse, collections, csv, gzip, hashlib, itertools, json, re, shutil, subprocess, tempfile, zipfile
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]
BATCHES = ['baseline', 'extra-seed', 'greedy', 'low-temperature', 'high-temperature', 'top-p-95', 'top-p-90']

def rows(path):
    with (gzip.open if str(path).endswith('.gz') else open)(path, 'rt', encoding='utf-8-sig', newline='') as f:
        yield from csv.DictReader(f)

def integer(event, name):
    return int(re.search(r'\b' + name + r'=(-?\d+);', event).group(1))

def digest(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1024*1024), b''): h.update(block)
    return h.hexdigest()

def save(name, value):
    (ROOT/name).write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')

def analyze(partial=False):
    report, outcomes, defense_outcomes = {}, [], []
    for batch in BATCHES:
        folder = ROOT/batch/'cases'
        if not folder.exists(): continue
        totals = collections.defaultdict(collections.Counter)
        for path in sorted(folder.glob('*.input.json')):
            source = json.loads(path.read_text(encoding='utf-8-sig'))
            stem = str(path).removesuffix('.input.json')
            summary = next(rows(stem+'.summary.csv')); stage = source['stage']
            t = totals[stage]; t['cases'] += 1; t['ticks'] += int(summary['ticks'])
            ended = summary['ended']=='True'; t['ended'] += ended; t['timeouts'] += not ended
            t[summary['result'] or 'unfinished'] += 1
            raw = int(summary['healthLossQ10'])
            effective = 400*1024 - sum(max(0,int(summary[k])) for k in ('heroHealthQ10','enemyHealthQ10'))
            assert raw == 400*1024-int(summary['heroHealthQ10'])-int(summary['enemyHealthQ10'])
            t['effectiveHealthLossQ10'] += effective; t['overkillQ10'] += raw-effective
            for key in ('commands','rejections','attackStarts','movesStarted','hits','uncoveredEvaluations'): t[key] += int(summary[key])
            if ended:
                assert min(int(summary['heroHealthQ10']),int(summary['enemyHealthQ10'])) <= 0
            ledger = collections.defaultdict(collections.Counter); reservations=collections.defaultdict(dict)
            triggered={}; succeeded=set(); spent_by_plan=collections.Counter()
            damage = 0; hits = 0; first_owner = None
            for e in rows(stem+'.events.csv.gz'):
                event=e['event']
                if event.startswith('ProjectHero.Logic.Events.ActionPlanCreatedEvent{') and integer(event,'Origin')==1:
                    t['acceptedReactionPlans']+=1
                if event.startswith('ProjectHero.Logic.Events.ReactionTriggeredEvent{'):
                    opp=str(integer(event,'ReactionOpportunityId'))
                    assert opp not in triggered
                    triggered[opp]=dict(opportunity=opp,plan=integer(event,'ReactionActionPlanId'),defender=integer(event,'DefenderUnitId'),
                        tick=int(e['tick']),type=integer(event,'ReactionType'))
                if event.startswith('ProjectHero.Logic.Events.ReactionPlanResolvedEvent{') and 'RewardsSuccess=True;' in event:
                    succeeded.add(int(re.search(r'ReactionPlanId=[^{]+\{[^}]*Value=(\d+);',event).group(1)))
                if event.startswith('ProjectHero.Logic.Events.DamageChannelResolvedEvent{'):
                    amount=integer(event,'AfterBlockDamageQ10')
                    if amount>0:
                        damage+=amount; hits+=1
                        if first_owner is None: first_owner=re.search(r'AttackerUnitId=[^{]+\{[^}]*Value=(\d+);',event).group(1)
                if not event.startswith('ProjectHero.Logic.Events.AdrenalineLedgerChangedEvent{'): continue
                unit=re.search(r'UnitId=[^{]+\{[^}]*Value=(\d+);',event).group(1)
                kind=int(re.search(r'ChangeKind=[^;]+:(\d+);',event).group(1)); v=ledger[unit]
                assert v['lastAvailable']==integer(event,'AvailableBefore'), (summary['case'],unit,'ledger continuity')
                change=integer(event,'AvailableAfter')-integer(event,'AvailableBefore')
                plan=integer(event,'ReservationPlanId'); amount=integer(event,'ReservationAmount')
                if kind==0: v['actualAccrued']+=change
                elif kind==1:
                    assert plan not in reservations[unit] and change==-amount
                    reservations[unit][plan]=amount
                elif kind in (2,3,6,7):
                    assert reservations[unit].pop(plan)==amount
                    if kind==2: v['consumed']+=amount; spent_by_plan[plan]+=amount; t['resourceConsumeEvents']+=1
                    elif kind==3: v['refunded']+=change; assert change==amount
                    else: v['discarded']+=amount
                elif kind==4: v['expiredAtCycleReset']+=integer(event,'AvailableBefore'); v['cycles']+=1
                elif kind==5:
                    v['battleEndCleared']+=-change+sum(reservations[unit].values()); reservations[unit].clear()
                v['lastAvailable']=integer(event,'AvailableAfter')
            assert damage==raw and hits==int(summary['hits']), summary['case']
            for resource in rows(stem+'.resources.csv'):
                for key in ('actualAccrued','consumed','refunded','expiredAtCycleReset','cycles'):
                    assert int(resource[key])==ledger[resource['unit']][key], (summary['case'],key)
                    t[key]+=int(resource[key])
            for unit,v in ledger.items():
                retained=v['lastAvailable']+sum(reservations[unit].values())
                assert v['actualAccrued']==v['consumed']+v['expiredAtCycleReset']+v['battleEndCleared']+v['discarded']+retained, (summary['case'],unit,'resource conservation')
                t['battleEndCleared']+=v['battleEndCleared']; t['discardedReservations']+=v['discarded']; t['unspentAtCutoff']+=retained
            actions=list(rows(stem+'.actions.csv'))
            for action in actions:
                key=action['unit:action'].split(':',1)[1]
                t['actionStarts:'+key]+=int(action['starts'])
                t['actionContacts:'+key]+=int(action['positiveDamageContacts'])
            legal=set(); selected=set(); samples=set(); waits=set(); held=set(); attacks=collections.Counter(); zero_damage=0
            health_bands=collections.defaultdict(lambda: [set(),set()])
            for e in rows(stem+'.candidates.csv.gz'):
                is_legal=e['rejection']=='' and e['score']!='' and e['coverage']=='Complete'
                if e['key'].startswith('R/') and is_legal:
                    key=(e['controller'], e['key'].split('/')[1]); legal.add(key)
                    maximum=int(e['actorMaximumHealthQ10'] or 0); hp=int(e['actorHealthQ10'] or 0)
                    band='unknown' if maximum<=0 else 'low' if hp*3<maximum else 'high' if hp*3>maximum*2 else 'middle'
                    health_bands[band][0].add(key)
                    if e['chosen']=='True': selected.add(key); health_bands[band][1].add(key)
                    if int(e['probabilityBeforeQ32'] or 0)>0: samples.add((key,e['tick']))
                if e['sampled']=='True' and e['key']=='WAIT/REACTION': waits.add((e['controller'],e['tick']))
                if e['sampled']=='True' and e['type']=='Attack':
                    attacks[e['group']]+=1
                    zero_damage+=int(e['noDefenseDamageQ10'] or e['damageQ10'] or 0)<=0
            for e in rows(stem+'.decisions.csv.gz'):
                if e['reason']=='WAIT_REACTION_ALREADY_SAMPLED': held.add((e['controller'],e['tick']))
            t['legalReactionOpportunities']+=len(legal); t['selectedReactionOpportunities']+=len(selected)
            assert set(triggered)<=set(k[1] for k in legal), summary['case']
            t['triggeredReactionOpportunities']+=len(triggered); t['successfulReactionPlans']+=len(succeeded)
            for opp,value in triggered.items():
                defense_outcomes.append(dict(batch=batch,stage=stage,case=summary['case'],**value,
                    success=value['plan'] in succeeded,resourceSpent=spent_by_plan[value['plan']]))
            t['reactionSamplingBoundaries']+=len(samples); t['sampledNoResponses']+=len(waits); t['heldNoResponsesTicks']+=len(held)
            t['sampledAttacks']+=sum(attacks.values()); t['sampledZeroDamageAttacks']+=zero_damage
            for band,(opps,selected_opps) in health_bands.items():
                t['legalReactionOpportunities:'+band]+=len(opps); t['selectedReactionOpportunities:'+band]+=len(selected_opps)
            owner=next(rows(stem+'.owners.csv'))
            winner=owner['heroUnit'] if summary['result']=='victory' else owner['enemyUnit'] if summary['result']=='defeat' else None
            if winner is not None and first_owner is not None:
                t['firstDamageWon']+=winner==first_owner; t['endedWithDamage']+=1
            if batch=='baseline' and stage=='A':
                policies={1:'SustainedOffense',2:'SeekInterrupt',3:'CautiousDefense'}
                pair=policies[source['heroPolicy']]+'-vs-'+policies[source['enemyPolicy']]
                old=str(ROOT.parent/'偏好互打长局-2026-11-11'/'cases'/(pair+'-'+['Near','Main','Far'][source['layout']]+'-mirror'+str(int(source['mirror']))))
                old_summary=next(rows(old+'.summary.csv'))
                assert all(summary[k]==v for k,v in old_summary.items() if k!='case'), summary['case']
                assert digest(stem+'.heroReplay')==digest(old+'.heroReplay'), summary['case']
                t['task21ReplayBytesEqual']+=1
            outcomes.append(dict(batch=batch, stage=stage, case=summary['case'], ended=ended, ticks=int(summary['ticks']), result=summary['result'],
                effectiveHealthLossQ10=effective, overkillQ10=raw-effective, legalReactionOpportunities=len(legal), selectedReactionOpportunities=len(selected),
                mirror=source['mirror'],layout=source['layout'],seed=source['seed'],heroPolicy=source['heroPolicy'],enemyPolicy=source['enemyPolicy'],
                firstWindowOwner=owner['firstWindowOwner'],firstDamageOwner=first_owner,winner=winner))
        report[batch]={stage:dict(v) for stage,v in totals.items()}
    save('analysis.json', report); save('outcomes.json', outcomes)
    save('defense-outcomes.json',defense_outcomes)
    paired=collections.defaultdict(dict)
    for row in outcomes:
        paired[(row['batch'],row['stage'],row['seed'],row['heroPolicy'],row['enemyPolicy'],row['layout'])][row['mirror']]=row
    mirrors=[]
    for key,pair in paired.items():
        if set(pair)!={False,True}: continue
        a,b=pair[False],pair[True]
        mirrors.append(dict(batch=key[0],stage=key[1],seed=key[2],heroPolicy=key[3],enemyPolicy=key[4],layout=key[5],
            sameOutcomeAndTicks=all(a[k]==b[k] for k in ('ended','result','ticks')),sameEffectiveLoss=a['effectiveHealthLossQ10']==b['effectiveHealthLossQ10']))
    save('mirror-comparison.json',mirrors)
    print(json.dumps({b:{s:{k:v.get(k,0) for k in ('cases','ended','timeouts','rejections','consumed','legalReactionOpportunities','selectedReactionOpportunities','sampledZeroDamageAttacks','sampledAttacks')} for s,v in stages.items()} for b,stages in report.items()},ensure_ascii=False))

def cross_runtime():
    expected=sorted((ROOT/'mechanisms').glob('*.canonical.txt'))
    assert len(expected)==114, len(expected)
    for path in expected: assert digest(path)==digest(ROOT/'native-mechanisms'/path.name), path.name
    save('cross-runtime-check.json', dict(cases=len(expected), eventsSnapshotsDecisionsProbabilitiesAndMemoryEqual=True))

def mechanisms():
    records=[]
    for path in sorted((ROOT/'mechanisms').glob('blood*.json')):
        probe=json.loads(path.read_text(encoding='utf-8-sig')); data=probe['input']
        decision=next(d for d in probe['decisions'] if any(e['IsReaction'] and e['ProbabilityBeforeQ32']>0 for e in d['Evaluations']))
        block=next(e for e in decision['Evaluations'] if e['Type']==2)
        assert block['ActorHealthQ10']==data['Health']*1024, path.name
        assert block['AvailableResource']==data['FundingDamage']*3*1024, path.name
        assert all(d['SamplingMemory']['DrawCount']==1 for d in probe['decisions'] if d.get('SamplingMemory') and d['SamplingMemory']['OpportunityKey']==decision['SamplingMemory']['OpportunityKey']), path.name
        records.append(dict(case=path.stem,**data,tick=decision['DecisionTick'],score=block['Score'],healthUtility=block['HealthUtility'],resourceUtility=block['ResourceUtility'],
            survivalUtility=block['SurvivalUtility'],probability=block['ProbabilityAfterQ32']/2**32,availableResource=block['AvailableResource'],
            resetTick=block['ResourceResetTick'],drawCount=decision['SamplingMemory']['DrawCount']))
    assert len(records)==18
    for policy in (1,2,3):
        simple=sorted((r for r in records if r['Policy']==policy and r['FundingDamage']==8 and r['ThreatStart']==361),key=lambda r:-r['Health'])
        assert len(simple)==3 and simple[0]['score']<simple[1]['score']<simple[2]['score']
        assert simple[0]['probability']<simple[1]['probability']<simple[2]['probability']
    save('mechanism-summary.json',records)
    print(json.dumps(records,ensure_ascii=False))

def failure_example():
    for path in sorted((ROOT/'baseline'/'cases').glob('D-*.candidates.csv.gz')):
        for key,group in itertools.groupby(rows(path),key=lambda r:(r['tick'],r['controller'])):
            items=list(group)
            attacks=[r for r in items if r['type']=='Attack' and r['rejection']=='' and r['score']!='' and r['coverage']=='Complete']
            misses=[r for r in attacks if r['sampled']=='True' and int(r['noDefenseDamageQ10'] or r['damageQ10'] or 0)==0]
            contact=[r for r in attacks if int(r['noDefenseDamageQ10'] or r['damageQ10'] or 0)>0 and ':True:' in r['targets']]
            if misses and contact and any(int(misses[0]['score'])>int(r['score']) for r in contact):
                save('failure-example.json',dict(case=path.name,tick=key[0],controller=key[1],sampled=misses[0],contactAlternatives=contact,
                    interpretation='The zero-damage sampled attack has higher utility than an available damaging attack against a legally reacting target. This preference reverses useful pressure.'))
                return
    raise AssertionError('No claimed failure example found')

def preserve():
    before=json.loads((ROOT/'before-protected.json').read_text(encoding='utf-8-sig'))
    # Check raw bytes too: Git's normalized diff can hide line-ending-only rewrites.
    own=ROOT/'intentional-changes.json'
    exclusions=set(json.loads(own.read_text(encoding='utf-8'))) if own.exists() else set()
    expected={r['path']:r['sha256'].lower() for r in before if r['path'] not in exclusions}
    def differs(relative):
        path=REPO/relative
        return relative if not path.exists() or digest(path)!=expected[relative] else None
    with ThreadPoolExecutor(max_workers=8) as pool:
        changed=[p for p in pool.map(differs,expected) if p is not None]
    previous=ROOT/'restoration.json'
    restored=json.loads(previous.read_text(encoding='utf-8'))['restored'] if previous.exists() else []
    with zipfile.ZipFile(ROOT/'validation-regenerated-files.zip','a',zipfile.ZIP_DEFLATED) as archive:
        for relative in changed:
            if relative not in expected: continue
            path=REPO/relative
            if path.exists() and digest(path)==expected[relative]: continue
            data=subprocess.check_output(['git','show','HEAD:'+relative],cwd=REPO)
            if hashlib.sha256(data).hexdigest()!=expected[relative]:
                crlf=data.replace(b'\r\n',b'\n').replace(b'\n',b'\r\n')
                if hashlib.sha256(crlf).hexdigest()==expected[relative]: data=crlf
            assert hashlib.sha256(data).hexdigest()==expected[relative], 'Cannot restore a file not equal to the starting HEAD: '+relative
            if path.exists(): archive.write(path,relative)
            path.write_bytes(data); restored.append(relative)
    save('restoration.json', dict(protectedFiles=len(expected),restored=restored,allProtectedByteHashesEqual=True,
        intentionallyExcluded=sorted(exclusions),
        method='starting clean HEAD and verified checkout line endings, every protected byte hash checked against pre-task manifest'))

def redact():
    altered=[]
    patterns=[r'''(?i)(-accessToken\s+|["']?(?:access_token|refresh_token|client_secret)["']?\s*[:=]\s*["']?)([^\s"',}]+)''',
        r'(?i)(-hubSessionId\s+)([^\s\r\n]+)', r'(?i)(-licensingIpc\s+)([^\s\r\n]+)']
    for path in ROOT.glob('*.log'):
        value=path.read_text(encoding='utf-8-sig',errors='replace'); original=value
        for pattern in patterns: value=re.sub(pattern,r'\1[REDACTED]',value)
        if value!=original: path.write_text(value,encoding='utf-8'); altered.append(path.name)
    save('log-redactions.json',dict(files=altered))

def seal():
    expected_counts={'baseline':216,'extra-seed':54,'greedy':9,'low-temperature':9,'high-temperature':9,'top-p-95':9,'top-p-90':9}
    source={p.relative_to(REPO).as_posix():digest(p) for folder in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/folder).rglob('*.cs')}
    fingerprint=hashlib.sha256(''.join(p+':'+source[p]+'\n' for p in sorted(source)).encode()).hexdigest()
    for batch,count in expected_counts.items():
        inputs=list((ROOT/batch/'cases').glob('*.input.json')); assert len(inputs)==count,(batch,len(inputs))
        assert all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint']==fingerprint for p in inputs), batch
        audit=(ROOT/batch/'disk-audit.txt').read_text(encoding='utf-8-sig')
        assert f'cases={count};' in audit and 'metricsRebuilt=True' in audit, batch
    for name,count in [('full-editmode.xml',1206),('production-shadow-playmode.xml',21)]:
        data=ET.parse(ROOT/name).getroot().attrib
        assert data['result']=='Passed' and int(data['passed'])==count and data['failed']=='0',name
    cross_runtime()
    content=json.loads((ROOT.parent/'偏好互打长局-2026-11-11'/'before-content.json').read_text(encoding='utf-8-sig'))
    assert all(digest(REPO/p)==h.lower() for p,h in content.items()), 'Formal content changed'
    save('content-preservation.json',dict(files=len(content),unchanged=True))
    before=json.loads((ROOT/'before-protected.json').read_text(encoding='utf-8-sig'))
    # Navigation intentionally changes after restoration; previous sealed task directories must stay exact.
    protected=[r for r in before if any(x in r['path'] for x in ('偏好互打长局-2026-11-11/','主动接近目标-2026-11-11/','进攻防御评价-2026-11-09/'))]
    assert all(digest(REPO/r['path'])==r['sha256'].lower() for r in protected)
    save('previous-evidence-preservation.json',dict(files=len(protected),unchanged=True))
    save('frozen-final-source.json',dict(fingerprint=fingerprint,files=source))
    assembly_folder=ROOT/'harness'/'assemblies'; assembly_folder.mkdir(exist_ok=True)
    historical_sources=[]
    for manifest in [ROOT/'harness'/'build-manifest.json']+[ROOT/b/'build-manifest.json' for b in BATCHES]:
        value=json.loads(manifest.read_text(encoding='utf-8-sig')); key=value['compilerKey']
        cached=Path(tempfile.gettempdir())/('ProjectHero-Matchup-'+key[:16])/'ProjectHero.Logic.dll'
        assert digest(cached)==value['assemblySha256']
        target=assembly_folder/('ProjectHero.Logic-'+key[:16]+'.dll'); shutil.copyfile(cached,target)
        shutil.copyfile(cached.with_suffix('.runtimeconfig.json'),target.with_suffix('.runtimeconfig.json'))
        for row in value['sourceHashes']:
            relative,expected=row.rsplit(':',1); path=REPO/relative
            if digest(path)==expected: continue
            data=path.read_bytes().replace(b'\r\n',b'\n')
            if path.name=='Task22MatchupTools.cs':
                data=data.replace(b'        public static void Scan() => Generate(Matrix().Where(i => !i.mirror && i.heroPolicy == i.enemyPolicy));\n',b'')
            elif path.name=='Program.cs':
                data=data.replace(b'            else if (mode == "scan") Task22MatchupTools.Scan();\n',b'')
            candidates=[data,data.replace(b'\n',b'\r\n')]
            data=next((b for b in candidates if hashlib.sha256(b).hexdigest()==expected),None)
            assert data is not None, 'Cannot recover recorded tool source: '+relative
            old=ROOT/'harness'/'versions'/key[:16]/path.name; old.parent.mkdir(parents=True,exist_ok=True); old.write_bytes(data)
            historical_sources.append(dict(original=relative,archive=old.relative_to(ROOT).as_posix(),sha256=expected))
    save('historical-tool-sources.json',historical_sources)
    with zipfile.ZipFile(ROOT/'source-and-content.zip','w',zipfile.ZIP_DEFLATED) as archive:
        included=set(source)|set(content)
        for relative in sorted(included):
            archive.write(REPO/relative,relative)
            meta=REPO/(relative+'.meta')
            if meta.exists() and relative+'.meta' not in included: archive.write(meta,relative+'.meta')
        for path in sorted((ROOT/'harness').rglob('*')):
            if path.is_file(): archive.write(path,path.relative_to(REPO).as_posix())
    print(json.dumps(dict(cases=sum(expected_counts.values()),source=fingerprint,formalContentUnchanged=True,oldEvidenceFiles=len(protected))),flush=True)
    # Flush the report before hashing a redirected seal.log, so it is sealed at its final bytes.
    save('sealed-manifest.json',{p.relative_to(ROOT).as_posix():digest(p) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name!='sealed-manifest.json'})

if __name__=='__main__':
    p=argparse.ArgumentParser(); p.add_argument('mode',choices=['analyze','partial','cross-runtime','mechanisms','failure-example','preserve','redact','seal']); a=p.parse_args()
    {'analyze':analyze,'partial':lambda:analyze(True),'cross-runtime':cross_runtime,'mechanisms':mechanisms,'failure-example':failure_example,'preserve':preserve,'redact':redact,'seal':seal}[a.mode]()
