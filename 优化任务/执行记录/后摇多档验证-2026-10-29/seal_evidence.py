"""Seal the final native exports, tests and source identity after analysis succeeds."""
import hashlib
import json
from collections import Counter
from pathlib import Path
from xml.etree import ElementTree

from analyze_recovery import ROOT, audit, group, num, read
from audit_scope import REPO, source

SUFFIXES=['.input.json','.heroReplay','.events.jsonl','.decisions.jsonl','.actions.csv','.frames.csv','.damage-income.csv']


def main():
    coarse_folder,coarse=read('coarse')
    fine_folder,fine=read('fine')
    rows=coarse+fine
    audit(rows,True)
    groups=group(rows)
    assert sorted(groups)==list(range(30,61))
    assert len(rows)==6572 and all(len(v)==212 for v in groups.values())
    fingerprint=source()
    assert fingerprint==rows[0]['source_fingerprint']
    test=ElementTree.parse(ROOT/'unity-editmode-final.xml').getroot()
    cases=[c for c in test.iter('test-case') if 'RecoveryExperimentTests.' in c.attrib.get('fullname','')]
    assert test.attrib['result']=='Passed' and test.attrib['failed']=='0'
    assert len(cases)==41 and all(c.attrib['result']=='Passed' for c in cases)
    count=total_bytes=0
    with (ROOT/'artifact-manifest.jsonl').open('w',encoding='utf-8') as output:
        for row in sorted(rows,key=lambda r:(num(r,'recovery_ticks'),r['scenario'])):
            for suffix in SUFFIXES:
                path=ROOT/row['_folder']/(row['scenario']+suffix)
                data=path.read_bytes()
                output.write(json.dumps(dict(path=path.relative_to(ROOT).as_posix(),bytes=len(data),
                    sha256=hashlib.sha256(data).hexdigest()),ensure_ascii=False)+'\n')
                count+=1;total_bytes+=len(data)
    assert count==46004
    asset_paths=['Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity',
        'Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity.meta']+[
        'Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/'+name+' SDF.asset'
        for name in ['Anton','Bangers','Electronic Highway Sign','Roboto-Bold']]
    import zipfile
    assets=[]
    with zipfile.ZipFile(ROOT/'generated-assets-snapshot.zip','w',zipfile.ZIP_DEFLATED) as archive:
        for relative in asset_paths:
            path=REPO/relative
            archive.write(path,relative)
            assets.append(dict(path=relative,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                last_write_timestamp=path.stat().st_mtime))
    result=dict(status='passed',coarse_folder=coarse_folder.name,fine_folder=fine_folder.name,
        source_fingerprint=fingerprint,source_identity_verified=True,
        cases=len(rows),ticks=list(range(30,61)),per_tick=212,
        coarse_cases=len(coarse),fine_cases=len(fine),native_files=count,native_bytes=total_bytes,
        replay_verified=sum(r['replay_verified'].lower()=='true' for r in rows),
        command_rejections=sum(num(r,'command_rejections') for r in rows),
        outcomes=dict(Counter(r['outcome'] for r in rows)),
        rules_versions=sorted({r['rules_version'] for r in rows}),
        scenario_versions=sorted({r['scenario_version'] for r in rows}),
        script_versions=sorted({r['script_version'] for r in rows}),
        definition_hashes=len({r['definition_hash'] for r in rows}),
        editmode={k:test.attrib[k] for k in ['result','total','passed','failed','skipped','duration']},
        new_recovery_tests=len(cases),
        invariants=['identical paired scenario coverage','actual health loss and dealt/received conservation',
            'first clash residual independent of recovery','unaffected unopposed quick/sweep results',
            'complete accepted attack timing and source-window metadata','on-disk input and every-Tick native replay',
            'frozen C# source fingerprint across coarse and fine'],
        generated_asset_snapshot=assets,
        limitations=['fixed scripts, not tactical AI','deterministic input cells, not random replications',
            'symmetric marginal growth/health inputs, not all character budgets',
            'unit 1 concurrent authority and unit 2 own-window authority must be analyzed separately',
            'no new PlayMode/human playtest in this experiment-only round',
            'generated scene was already dirty before this task; no exact asset baseline is claimed'])
    (ROOT/'acceptance-audit.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ['generated_asset_snapshot','invariants','limitations','ticks']},ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
