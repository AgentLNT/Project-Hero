"""Check source identity and preserve/restore test-generated historical artifacts."""
import argparse
import hashlib
import json
import zipfile
from pathlib import Path
from xml.etree import ElementTree

ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def source():
    manifest=[]
    chunks=[]
    for path in sorted((REPO/'Assets/Scripts').rglob('*.cs'),key=lambda p:str(p)):
        data=path.read_bytes()
        relative='/'+path.relative_to(REPO).as_posix()
        # Match File.ReadAllText(Encoding.UTF8), including its replacement fallback.
        chunks.append(f'{len(relative)}:{relative}\n'+data.decode('utf-8-sig',errors='replace')+'\n')
        manifest.append(dict(path=relative[1:],sha256=sha(data),bytes=len(data)))
    fingerprint=sha(''.join(chunks).encode('utf-8'))
    test=ElementTree.parse(ROOT/'unity-editmode-final.xml').getroot()
    result=dict(source_fingerprint=fingerprint,files=manifest,
        editmode={k:test.attrib[k] for k in ['result','total','passed','failed','skipped','duration']})
    (ROOT/'source-manifest.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(source_fingerprint=fingerprint,source_files=len(manifest),editmode=result['editmode'])))
    return fingerprint


def restore():
    manifest=json.loads((ROOT/'pretest-baseline-manifest.json').read_text(encoding='utf-8-sig'))
    originals={}
    changed=[]
    new=[]
    with zipfile.ZipFile(ROOT/'pretest-baseline.zip') as archive:
        for item in manifest:
            name=item['path']
            data=archive.read(name)
            assert sha(data).lower()==item['sha256'].lower(),name
            target=REPO/'优化任务/执行记录'/name[8:] if name.startswith('records/') else REPO/name[7:]
            target=target.resolve()
            assert target.is_relative_to(REPO) and not target.is_relative_to(ROOT),target
            originals[target]=data
            if not target.exists() or target.read_bytes()!=data:
                changed.append(dict(path=target.relative_to(REPO).as_posix(),baseline_sha256=sha(data),
                    generated_sha256=sha(target.read_bytes()) if target.exists() else None))
    for target in (REPO/'优化任务/执行记录').rglob('*'):
        if target.is_file() and not target.is_relative_to(ROOT) and target.resolve() not in originals:
            new.append(target.resolve())
    output=ROOT/'regression-generated-artifacts.zip'
    assert not output.exists(),'Restore already performed; do not overwrite the saved generated artifacts.'
    with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as archive:
        for item in changed:
            target=REPO/item['path']
            if target.exists():
                archive.write(target,'changed/'+item['path'])
        for target in new:
            archive.write(target,'new/'+target.relative_to(REPO).as_posix())
    with zipfile.ZipFile(output) as archive:
        assert archive.testzip() is None
        for item in changed:
            if item['generated_sha256']:
                assert sha(archive.read('changed/'+item['path']))==item['generated_sha256']
        for target in new:
            assert archive.read('new/'+target.relative_to(REPO).as_posix())==target.read_bytes()
    for target,data in originals.items():
        if not target.exists() or target.read_bytes()!=data:
            target.parent.mkdir(parents=True,exist_ok=True)
            target.write_bytes(data)
    for target in new:
        assert target.is_relative_to(REPO/'优化任务/执行记录') and not target.is_relative_to(ROOT)
        target.unlink()
    assert all(target.read_bytes()==data for target,data in originals.items())
    report=dict(restored=len(changed),baseline_files_verified=len(originals),generated_archive=output.name,
        archive_sha256=sha(output.read_bytes()),changes=changed,
        generated_new_files=[p.relative_to(REPO).as_posix() for p in new],
        note='Verified before subsequent intentional documentation status updates; baseline includes pre-existing dirty files.')
    (ROOT/'baseline-restoration.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ['changes','generated_new_files']},ensure_ascii=False))


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--restore',action='store_true')
    args=parser.parse_args()
    source()
    if args.restore:
        restore()
