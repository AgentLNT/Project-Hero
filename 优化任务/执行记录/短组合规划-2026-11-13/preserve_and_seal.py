from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import hashlib,json,re,shutil,subprocess,sys,tempfile,xml.etree.ElementTree as ET,zipfile

ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]

def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def save(name,value):(ROOT/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
INTENTIONAL={'Assets/Tests/PlayMode/FormalAiSceneTests.cs','优化任务/执行记录/README.md'}

def preserve():
    before=json.loads((ROOT/'before-protected.json').read_text(encoding='utf-8'))
    expected={p:h for p,h in before.items() if p not in INTENTIONAL}
    def check(p):return p if not (REPO/p).exists() or digest(REPO/p)!=expected[p] else None
    with ThreadPoolExecutor(max_workers=8) as pool:changed=[p for p in pool.map(check,expected) if p]
    old=ROOT/'restoration.json';restored=json.loads(old.read_text(encoding='utf-8-sig'))['restored'] if old.exists() else []
    with zipfile.ZipFile(ROOT/'before-source.zip') as original,zipfile.ZipFile(ROOT/'validation-regenerated-files.zip','a',zipfile.ZIP_DEFLATED) as archive:
        for relative in changed:
            target=(REPO/relative).resolve();assert target.is_relative_to(REPO.resolve())
            data=original.read(relative) if relative in original.namelist() else subprocess.check_output(['git','show','HEAD:'+relative],cwd=REPO)
            if hashlib.sha256(data).hexdigest()!=expected[relative]:
                crlf=data.replace(b'\r\n',b'\n').replace(b'\n',b'\r\n')
                if hashlib.sha256(crlf).hexdigest()==expected[relative]:data=crlf
            assert hashlib.sha256(data).hexdigest()==expected[relative], 'Pre-task bytes unavailable: '+relative
            if target.exists():archive.write(target,relative)
            target.write_bytes(data);restored.append(relative)
    assert all(digest(REPO/p)==h for p,h in expected.items())
    save('restoration.json',dict(protectedFiles=len(expected),restored=restored,allProtectedByteHashesEqual=True,
        intentionallyExcluded=sorted(INTENTIONAL),method='Pre-task zip or clean HEAD with checked line endings; every restored byte hash matches the starting manifest.'))

def redact():
    changed=[]
    patterns=[r'''(?i)(-accessToken\s+|["']?(?:access_token|refresh_token|client_secret)["']?\s*[:=]\s*["']?)([^\s"',}]+)''',
        r'(?i)(-hubSessionId\s+)([^\s\r\n]+)',r'(?i)(-licensingIpc\s+)([^\s\r\n]+)']
    for path in ROOT.glob('*.log'):
        value=path.read_text(encoding='utf-8-sig',errors='replace');before=value
        for pattern in patterns:value=re.sub(pattern,r'\1[REDACTED]',value)
        if value!=before:path.write_text(value,encoding='utf-8');changed.append(path.name)
    save('log-redactions.json',dict(files=changed))

def seal():
    for name,count in [('editmode.xml',1217),('production-shadow-playmode.xml',22)]:
        result=ET.parse(ROOT/name).getroot().attrib
        assert result['result']=='Passed' and result['failed']=='0' and int(result['passed'])==count,(name,result)
    source={p.relative_to(REPO).as_posix():digest(p) for folder in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/folder).rglob('*.cs')}
    fingerprint=hashlib.sha256(''.join(p+':'+source[p]+'\n' for p in sorted(source)).encode()).hexdigest()
    matrix=json.loads((ROOT/'matrix-validation-source.json').read_text(encoding='utf-8-sig'))
    with zipfile.ZipFile(ROOT/'matrix-validation-source.zip') as archived:
        assert all(hashlib.sha256(archived.read(p)).hexdigest()==h for p,h in matrix['files'].items())
    differences=[p for p,h in source.items() if matrix['files'].get(p)!=h]
    assert differences==['Assets/Tests/PlayMode/FormalAiSceneTests.cs'],differences
    save('validation-source-versions.json',dict(matrixSource=matrix['fingerprint'],finalSource=fingerprint,
        differences=differences,productionKernelAndAiSourceUnchanged=True,
        explanation='Only the new PlayMode fixture is corrected to inspect canonical stored replay events and actual snapshot health loss, at the original 360 frames. Runtime, all EditMode tests and all matrix/native tool sources are identical.'))
    # The exploratory pilot predates the scene-test addition and is explicitly excluded from final-source claims.
    for batch,count in [('formal',54),('unity-reference',4)]:
        inputs=list((ROOT/batch/'cases').glob('*.input.json'));assert len(inputs)==count
        assert all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint']==matrix['fingerprint'] for p in inputs),batch
    assert 'cases=54;' in (ROOT/'formal/disk-audit.txt').read_text(encoding='utf-8-sig')
    assert 'cases=4;' in (ROOT/'formal/unity-reference-audit.txt').read_text(encoding='utf-8-sig')
    assert 'cases=54;' in (ROOT/'legacy-d-audit.txt').read_text(encoding='utf-8-sig')
    cross=json.loads((ROOT/'cross-runtime-check.json').read_text(encoding='utf-8-sig'));assert cross['cases']==27
    canonical=list((ROOT/'mechanisms').glob('*.canonical.txt'));assert len(canonical)==27
    assert all(digest(p)==digest(ROOT/'native-mechanisms'/p.name) for p in canonical)
    assert 'Reproduction evidence:' in (ROOT/'reproduction.log').read_text(encoding='utf-8-sig')
    content=json.loads((ROOT.parent/'偏好互打长局-2026-11-11/before-content.json').read_text(encoding='utf-8-sig'))
    assert all(digest(REPO/p)==h.lower() for p,h in content.items())
    save('content-preservation.json',dict(files=len(content),unchanged=True))
    before=json.loads((ROOT/'before-protected.json').read_text(encoding='utf-8-sig'))
    expected={p:h for p,h in before.items() if p not in INTENTIONAL}
    assert all(digest(REPO/p)==h for p,h in expected.items()),'Protected pre-task evidence changed'
    old=ROOT.parent/'温度采样与磨血防御-2026-11-13'
    seal22=json.loads((old/'sealed-manifest.json').read_text(encoding='utf-8-sig'))
    assert all(digest(old/p)==h for p,h in seal22.items())
    save('previous-evidence-preservation.json',dict(protectedFiles=len(expected),task22SealedFiles=len(seal22),unchanged=True))
    save('frozen-final-source.json',dict(fingerprint=fingerprint,files=source))
    manifest=json.loads((ROOT/'harness/build-manifest.json').read_text(encoding='utf-8-sig'))
    for row in manifest['sourceHashes']:
        path,h=row.rsplit(':',1);assert digest(REPO/path)==h,path
    dll=Path(tempfile.gettempdir())/('ProjectHero-Matchup-'+manifest['compilerKey'][:16])/'ProjectHero.Logic.dll'
    assert digest(dll)==manifest['assemblySha256']
    assembly=ROOT/'harness/assemblies';assembly.mkdir(exist_ok=True)
    shutil.copyfile(dll,assembly/dll.name);shutil.copyfile(dll.with_suffix('.runtimeconfig.json'),assembly/dll.with_suffix('.runtimeconfig.json').name)
    with zipfile.ZipFile(ROOT/'source-and-content.zip','w',zipfile.ZIP_DEFLATED) as archive:
        included=set(source)|set(content)
        archive.write(REPO/'.gitattributes','.gitattributes')
        for relative in sorted(included):
            archive.write(REPO/relative,relative)
            meta=REPO/(relative+'.meta')
            if meta.exists() and relative+'.meta' not in included:archive.write(meta,relative+'.meta')
        for p in sorted((ROOT/'harness').rglob('*')):
            if p.is_file():archive.write(p,p.relative_to(REPO).as_posix())
    save('seal-result.json',dict(source=fingerprint,matrixSource=matrix['fingerprint'],sourceFiles=len(source),formalCases=54,nativeFormalCases=4,nativeMechanisms=27,
        legacyDReplays=54,formalContentUnchanged=True,protectedFiles=len(expected),task22SealedFiles=len(seal22)))
    manifest={p.relative_to(ROOT).as_posix():digest(p) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name!='sealed-manifest.json'}
    save('sealed-manifest.json',manifest)
    assert all(digest(ROOT/p)==h for p,h in manifest.items())
    print(json.dumps(dict(files=len(manifest),source=fingerprint,verified=True)))

if __name__=='__main__':{'preserve':preserve,'redact':redact,'seal':seal}[sys.argv[1]]()
