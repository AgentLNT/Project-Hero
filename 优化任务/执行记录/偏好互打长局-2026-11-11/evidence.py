from pathlib import Path
import csv, gzip, hashlib, json, re, subprocess, sys, zipfile

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]
PREVIOUS = ('进攻防御评价-2026-11-09', '主动接近目标-2026-11-11')

def sha(data): return hashlib.sha256(data).hexdigest()
def write(name, value): (ROOT/name).write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
def generated(p):
    return p.startswith(('优化任务/执行记录/M1-regression/', '优化任务/执行记录/拼刀后摇纠偏-2026-10-29/',
        '优化任务/执行记录/规则AI阶段三-2026-11-03/', 'Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/',
        'Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity')) or p == 'ProjectSettings/ProjectSettings.asset' or (
        p.startswith('Assets/Tests/') and p.endswith(('.asset', '.asset.meta')))

def preserve():
    result=[]
    for name in PREVIOUS:
        folder=ROOT.parent/name; manifest=json.loads((folder/'sealed-manifest.json').read_text(encoding='utf-8'))
        assert all(sha((folder/p).read_bytes())==h for p,h in manifest.items()),name
        result.append(dict(folder=name,files=len(manifest),unchanged=True))
    write('previous-evidence-preservation.json',result)

def snapshot():
    tracked=subprocess.check_output(['git','ls-files','-z'],cwd=REPO).decode('utf-8').split('\0')
    files={p for p in tracked if p and generated(p) and (REPO/p).is_file()}
    files.update(p.relative_to(REPO).as_posix() for d in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/d).rglob('*.cs'))
    with zipfile.ZipFile(ROOT/'before-task.zip','x',zipfile.ZIP_DEFLATED) as z:
        for p in sorted(files): z.write(REPO/p,p)
    write('before-task.json',{p:sha((REPO/p).read_bytes()) for p in sorted(files)})
    content={p for p in tracked if p.startswith(('Assets/Resources/','Packages/')) or p=='Assets/Scenes/CombatSampleScene.unity'}
    content.add('Assets/Scripts/Logic/AI/AiUtilityProfile.cs')
    write('before-content.json',{p:sha((REPO/p).read_bytes()) for p in sorted(content)})
    write('origin.json',dict(commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO).decode().strip(),
        status=subprocess.check_output(['git','-c','core.quotepath=false','status','--short'],cwd=REPO).decode('utf-8'),
        date='2026-11-11',note='Tasks 19 and 20 are already present, uncommitted; baseline is current worktree.'))
    preserve(); print('Snapshotted',len(files),'files and',len(content),'content/weight files')

def restore():
    before=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
    changed=[p for p,h in before.items() if generated(p) and (not (REPO/p).is_file() or sha((REPO/p).read_bytes())!=h)]
    with zipfile.ZipFile(ROOT/'validation-regenerated-files.zip','x',zipfile.ZIP_DEFLATED) as z:
        for p in changed:
            if (REPO/p).is_file(): z.write(REPO/p,p)
    with zipfile.ZipFile(ROOT/'before-task.zip') as z:
        for p in changed:
            target=(REPO/p).resolve(); target.relative_to(REPO.resolve()); target.write_bytes(z.read(p))
    assert all(sha((REPO/p).read_bytes())==h for p,h in before.items() if generated(p))
    write('restoration.json',dict(restoredFiles=changed,verifiedFiles=sum(generated(p) for p in before)))
    preserve(); print('Restored',len(changed))

def archive_unity():
    target=ROOT/'unity-reference'; target.mkdir(exist_ok=True)
    original=(ROOT/'cases').resolve(); destination=(target/'cases').resolve()
    original.relative_to(REPO.resolve()); destination.relative_to(REPO.resolve())
    assert original.is_dir() and not destination.exists()
    source_files=sorted(p for d in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/d).rglob('*.cs'))
    hashes={p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in source_files}
    hashes=dict(sorted(hashes.items()))
    fp=sha(''.join(p+':'+h+'\n' for p,h in hashes.items()).encode())
    completed=list(original.glob('*.input.json'))
    assert len(completed)==4 and all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint']==fp for p in completed)
    content=json.loads((ROOT/'before-content.json').read_text(encoding='utf-8'))
    with zipfile.ZipFile(target/'source-and-content.zip','x',zipfile.ZIP_DEFLATED) as z:
        for p in sorted(set(hashes)|set(content)): z.write(REPO/p,p)
    (target/'frozen-source.json').write_text(json.dumps(dict(fingerprint=fp,files=hashes),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    original.rename(destination); (ROOT/'pilot.log').rename(target/'pilot-interrupted-after-four-complete.log')
    write('unity-reference-status.json',dict(completedCases=4,stoppedAfterCompletedCases=True,partialOtherFilesNotEvidence=True,sourceFingerprint=fp))
    print('Archived 4 completed Unity cases and the exact source/content')

def initial_source():
    folder=ROOT/'pre-scene-compile-fix'; folder.mkdir(exist_ok=True)
    files=sorted(p for d in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/d).rglob('*.cs'))
    hashes=dict(sorted((p.relative_to(REPO).as_posix(),sha(p.read_bytes())) for p in files))
    fp=sha(''.join(p+':'+h+'\n' for p,h in hashes.items()).encode())
    assert all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint']==fp for p in (ROOT/'cases').glob('*.input.json'))
    with zipfile.ZipFile(folder/'source-and-content.zip','x',zipfile.ZIP_DEFLATED) as z:
        content=json.loads((ROOT/'before-content.json').read_text(encoding='utf-8'))
        for p in sorted(set(hashes)|set(content)): z.write(REPO/p,p)
    (folder/'frozen-source.json').write_text(json.dumps(dict(fingerprint=fp,files=hashes),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('Saved pre-scene-compile-fix source',fp)

def initial_results():
    folder=(ROOT/'pre-scene-compile-fix').resolve(); folder.relative_to(REPO.resolve())
    original=(ROOT/'cases').resolve(); original.relative_to(REPO.resolve())
    assert len(list(original.glob('*.input.json')))==54 and not (folder/'cases').exists()
    original.rename(folder/'cases')
    for name in ('summary.csv','standalone-run.log','partial-analysis.json','analysis.json','funded-defense-wait-examples.json'):
        if (ROOT/name).exists(): (ROOT/name).rename(folder/name)
    print('Archived initial 54-case iteration; not final acceptance evidence')

def rows(path):
    opener=gzip.open if str(path).endswith('.gz') else open
    with opener(path,'rt',encoding='utf-8-sig',newline='') as f: return list(csv.DictReader(f))

def redact_logs():
    pattern=re.compile(rb'''(?i)(-accessToken\s+|["']?(?:access_token|refresh_token|client_secret)["']?\s*[:=]\s*["']?)([^\s"',}]+)''')
    report=[]
    for p in sorted(ROOT.rglob('*.log')):
        raw=p.read_bytes(); count=0
        def redact(match):
            nonlocal count
            if match.group(2)==b'[REDACTED]': return match.group(0)
            count+=1; return match.group(1)+b'[REDACTED]'
        masked=pattern.sub(redact,raw)
        if count:
            p.write_bytes(masked)
            report.append(dict(path=p.relative_to(ROOT).as_posix(),maskedCredentialArguments=count))
    if report: write('log-redactions.json',report)
    print('Masked credential arguments in',len(report),'logs')

def analyze(complete=True):
    paths=[p for p in sorted((ROOT/'cases').glob('*.summary.csv')) if p.with_name(p.name.replace('.summary.csv','.input.json')).exists()]
    cases=[rows(p)[0] for p in paths]
    for r in cases:
        # Snapshot HP may be negative. Keep raw CSV loss; distinguish damage below zero in derived metrics.
        r['effectiveHealthLossQ10']=str(400*1024-sum(max(0,int(r[k])) for k in ('heroHealthQ10','enemyHealthQ10')))
        r['overkillQ10']=str(int(r['healthLossQ10'])-int(r['effectiveHealthLossQ10']))
    assert len({r['case'] for r in cases})==len(cases)
    if complete: assert len(cases)==54
    result=dict(cases=len(cases),ticks=sum(int(r['ticks']) for r in cases),ended=sum(r['ended']=='True' for r in cases),
        rejected=sum(int(r['rejections']) for r in cases),uncoveredEvaluations=sum(int(r['uncoveredEvaluations']) for r in cases),
        byLayout={},byPair={},waits={},stagnation=[],mirrorDifferences=[],actionUsage={},
        resources=dict(actualAccrued=0,consumed=0,expiredAtCycleReset=0,refunded=0),
        outcomes=dict(heroWins=0,enemyWins=0,draws=0,timeouts=0),firstHit=dict(singleFirstHits=0,firstHitterWon=0,simultaneousFirstHits=0))
    result['reactionCandidates']=dict(total=0,legal=0,chosen=0,rejections={})
    result['damageTotals']={k:sum(int(r[k]) for r in cases) for k in ('healthLossQ10','effectiveHealthLossQ10','overkillQ10')}
    for field,target in [('layout','byLayout'),('pair','byPair')]:
        for key in sorted({r[field] for r in cases}):
            group=[r for r in cases if r[field]==key]
            result[target][key]=dict(cases=len(group),ended=sum(r['ended']=='True' for r in group),
                lossQ10=sum(int(r['healthLossQ10']) for r in group),hits=sum(int(r['hits']) for r in group),
                effectiveLossQ10=sum(int(r['effectiveHealthLossQ10']) for r in group),overkillQ10=sum(int(r['overkillQ10']) for r in group),
                attacksStarted=sum(int(r['attackStarts']) for r in group),
                minimumFinalDistance=min(int(r['finalDistance']) for r in group),maximumFinalDistance=max(int(r['finalDistance']) for r in group),
                lastDamageTicks=[int(r['lastDamageTick']) for r in group],results=[r['result'] for r in group])
    examples=[]; outcome_rows=[]; prefix_points=0
    for r in cases:
        prefix=ROOT/'cases'/r['case']
        assert r['result'] in ('','victory','defeat','draw')
        result['outcomes'][{'victory':'heroWins','defeat':'enemyWins','draw':'draws','':'timeouts'}[r['result']]]+=1
        for resource in rows(str(prefix)+'.resources.csv'):
            for metric in result['resources']: result['resources'][metric]+=int(resource[metric])
        for action in rows(str(prefix)+'.actions.csv'):
            key=action['unit:action'].split(':',1)[1]
            totals=result['actionUsage'].setdefault(key,dict(starts=0,positiveDamageContacts=0,resolvedDamageQ10=0))
            for metric in totals: totals[metric]+=int(action[metric])
        for w in rows(str(prefix)+'.waits.csv'):
            reason=w['controller:reason'].split(':',1)[1]
            result['waits'][reason]=result['waits'].get(reason,0)+int(w['decisionTicks'])
        if r['ended']=='False' and int(r['lastProgressTick'])<int(r['ticks'])//2:
            result['stagnation'].append(dict(case=r['case'],lastProgressTick=int(r['lastProgressTick']),lastDamageTick=int(r['lastDamageTick']),
                finalDistance=int(r['finalDistance']),healthLossQ10=int(r['healthLossQ10'])))
        if complete:
            owners=rows(str(prefix)+'.owners.csv')[0]; first=set()
            for e in rows(str(prefix)+'.events.csv.gz'):
                if int(e['tick'])>int(r['firstDamageTick']): break
                if int(e['tick'])!=int(r['firstDamageTick']) or not e['event'].startswith('ProjectHero.Logic.Events.DamageChannelResolvedEvent{'): continue
                amount=re.search(r'AfterBlockDamageQ10=(\d+);',e['event'])
                attacker=re.search(r'AttackerUnitId=[^{]+\{[^}]*Value=(\d+);',e['event'])
                if amount and int(amount.group(1))>0 and attacker:
                    first.add('hero' if attacker.group(1)==owners['heroUnit'] else 'enemy')
            winner={'victory':'hero','defeat':'enemy','draw':'draw','':'timeout'}[r['result']]
            if len(first)==1:
                result['firstHit']['singleFirstHits']+=1
                if winner in first: result['firstHit']['firstHitterWon']+=1
            elif len(first)>1: result['firstHit']['simultaneousFirstHits']+=1
            outcome_rows.append(dict(case=r['case'],pair=r['pair'],layout=r['layout'],mirror=r['mirror'],ticks=r['ticks'],
                winner=winner,firstHitter=';'.join(sorted(first)),heroHealthQ10=r['heroHealthQ10'],enemyHealthQ10=r['enemyHealthQ10'],
                rawSnapshotHealthLossQ10=r['healthLossQ10'],effectiveHealthLossQ10=r['effectiveHealthLossQ10'],overkillQ10=r['overkillQ10']))
            old=ROOT/'pre-scene-compile-fix/cases'/(r['case']+'.checkpoints.csv')
            checkpoints=rows(str(prefix)+'.checkpoints.csv')
            for point in rows(old):
                assert point in checkpoints,('1800-Tick prefix checkpoint changed',r['case'],point)
                prefix_points+=1
            decisions=rows(str(prefix)+'.decisions.csv.gz')
            stalled={(d['tick'],d['controller']) for d in decisions if d['selected']=='False' and d['reason']=='WAIT_APPROACH_ATTACK_AVAILABLE'}
            found={}
            for c in rows(str(prefix)+'.candidates.csv.gz'):
                if c['key'].startswith('R/'):
                    reaction=result['reactionCandidates']; reaction['total']+=1
                    reaction['legal']+=not bool(c['rejection']); reaction['chosen']+=c['chosen']=='True'
                    if c['rejection']:
                        reason=c['rejection'].split(':',1)[0]
                        reaction['rejections'][reason]=reaction['rejections'].get(reason,0)+1
                key=c['tick'],c['controller']
                if key not in stalled or c['type']!='Attack' or not c['noDefenseDamageQ10'] or not c['damageQ10']: continue
                if int(c['noDefenseDamageQ10'])>0 and int(c['damageQ10'])<=0:
                    sample=dict(case=r['case'],**{k:c[k] for k in ('tick','controller','policy','key','score','damageQ10','incomingQ10','progress','noDefenseScore','noDefenseDamageQ10','worstDefense','defenseCount','coverage')})
                    if c['controller'] not in found or int(c['tick'])>int(found[c['controller']]['tick']): found[c['controller']]=sample
            examples.extend(found.values())
    for r in cases:
        if r['mirror']!='False': continue
        other=next((s for s in cases if s['pair']==r['pair'] and s['layout']==r['layout'] and s['mirror']=='True'),None)
        if other:
            diff={k:[r[k],other[k]] for k in ('ticks','result','heroHealthQ10','enemyHealthQ10','attackStarts','hits','healthLossQ10','finalDistance') if r[k]!=other[k]}
            if diff: result['mirrorDifferences'].append(dict(pair=r['pair'],layout=r['layout'],differences=diff))
    if complete:
        write('analysis.json',result); write('funded-defense-wait-examples.json',examples)
        write('initial-prefix-checkpoints.json',dict(cases=len(cases),matchedCheckpoints=prefix_points,observedThroughTick=1800))
        write('damage-weight-upper-bounds.json',dict(scope='Fixed captured contexts; the recorded legal zero-damage reply remains an upper bound on worst-reply utility when only Damage weight changes.',
            examples=[dict(case=e['case'],tick=e['tick'],controller=e['controller'],candidate=e['key'],
                legalReply=e['worstDefense'],damageWeightMultipliers=[1,2,4,8,16],
                worstUtilityUpperBounds=[int(e['score'])]*5) for e in examples if int(e['damageQ10'])==0 and int(e['score'])<0]))
        with (ROOT/'outcomes.csv').open('w',encoding='utf-8-sig',newline='') as f:
            writer=csv.DictWriter(f,fieldnames=list(outcome_rows[0])); writer.writeheader(); writer.writerows(outcome_rows)
        pressure=ROOT/'cases/CautiousDefense-vs-CautiousDefense-Near-mirror0'
        write('near-pressure-timeline.json',dict(
            case=pressure.name,owners=rows(str(pressure)+'.owners.csv'),
            events=[e for e in rows(str(pressure)+'.events.csv.gz') if int(e['tick'])<=457
                and any(t in e['event'].split('{')[0] for t in ('AdrenalineLedgerChangedEvent','TurnWindowOpenedEvent','DamageChannelResolvedEvent'))],
            heroDecision=[d for d in rows(str(pressure)+'.decisions.csv.gz') if d['tick']=='180' and d['controller']=='controller.player'],
            heroAttacks=[c for c in rows(str(pressure)+'.candidates.csv.gz') if c['tick']=='180' and c['controller']=='controller.player'
                and c['type']=='Attack' and c['noDefenseDamageQ10'] and int(c['noDefenseDamageQ10'])>0]))
    else: write('partial-analysis.json',result)
    print(json.dumps(result,ensure_ascii=False,indent=2))

def seal():
    import xml.etree.ElementTree as ET
    assert len(list((ROOT/'cases').glob('*.input.json')))==54
    analysis=json.loads((ROOT/'analysis.json').read_text(encoding='utf-8'))
    assert analysis['cases']==54 and analysis['ended']==54 and analysis['rejected']==0
    audit=(ROOT/'disk-audit.txt').read_text(encoding='utf-8-sig')
    assert f"cases=54;ticks={analysis['ticks']};eventsAndSnapshots=True;candidatesAndDecisions=True;metricsRebuilt=True" in audit
    assert json.loads((ROOT/'independent-csv-check.json').read_text())['cases']==54
    for name,count in (('full-editmode.xml',1194),('production-shadow-playmode.xml',21)):
        test=ET.parse(ROOT/name).getroot()
        assert test.attrib['result']=='Passed' and int(test.attrib['passed'])==count and int(test.attrib['failed'])==0
    preserve(); before=json.loads((ROOT/'before-content.json').read_text(encoding='utf-8'))
    write('content-changes.json',[dict(path=p,before=h,after=sha((REPO/p).read_bytes())) for p,h in before.items() if sha((REPO/p).read_bytes())!=h])
    files=sorted((p for d in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/d).rglob('*.cs')),key=lambda p:p.relative_to(REPO).as_posix())
    hashes={p.relative_to(REPO).as_posix():sha(p.read_bytes()) for p in files}
    fp=sha(''.join(p+':'+h+'\n' for p,h in hashes.items()).encode())
    assert all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint']==fp for p in (ROOT/'cases').glob('*.input.json'))
    write('frozen-final-source.json',dict(fingerprint=fp,files=hashes))
    origin=json.loads((ROOT/'before-task.json').read_text(encoding='utf-8'))
    inherited={p:h for p,h in origin.items() if p.endswith('.cs')}
    changed=[p for p,h in inherited.items() if sha((REPO/p).read_bytes())!=h]
    assert all(not p.startswith('Assets/Scripts/Logic/') for p in changed),changed
    write('inherited-source-changes.json',dict(verifiedFiles=len(inherited),changedFiles=changed,logicSourceUnchanged=True))
    with zipfile.ZipFile(ROOT/'source-and-content.zip','w',zipfile.ZIP_DEFLATED) as z:
        for p in sorted(set(hashes)|set(before)): z.write(REPO/p,p)
        for p in hashes:
            if (REPO/(p+'.meta')).is_file(): z.write(REPO/(p+'.meta'),p+'.meta')
        for p in sorted((ROOT/'harness').iterdir()):
            if p.is_file(): z.write(p,p.relative_to(REPO).as_posix())
    write('sealed-manifest.json',{p.relative_to(ROOT).as_posix():sha(p.read_bytes()) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name!='sealed-manifest.json'})
    print('Sealed',fp)

if __name__=='__main__': {'snapshot':snapshot,'restore':restore,'archive-unity':archive_unity,'initial-source':initial_source,'initial-results':initial_results,'redact-logs':redact_logs,'analyze':analyze,'preview':lambda:analyze(False),'seal':seal}[sys.argv[1]]()
