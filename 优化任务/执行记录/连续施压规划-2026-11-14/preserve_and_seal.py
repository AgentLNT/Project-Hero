from pathlib import Path
import concurrent.futures,hashlib,json,re,sys,zipfile
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
INTENTIONAL={'优化任务/执行记录/README.md'}
def sha(p):
    h=hashlib.sha256()
    with p.open('rb') as f:
        for b in iter(lambda:f.read(1024*1024),b''):h.update(b)
    return h.hexdigest()
def save(n,v):(ROOT/n).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def restore():
    before=json.loads((ROOT/'before-protected.json').read_text(encoding='utf-8'))
    expected={p:h for p,h in before.items() if p not in INTENTIONAL}
    paths=list(expected)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:actual=dict(zip(paths,pool.map(lambda p:sha(REPO/p),paths)))
    changed=[p for p in paths if actual[p]!=expected[p]]
    if (ROOT/'restoration.json').exists():
        assert not changed,('New historical changes after restoration',changed)
        print('Protected files still match; restoration archive retained')
        return
    restored=[]
    with zipfile.ZipFile(ROOT/'before-tracked.zip') as backup,zipfile.ZipFile(ROOT/'validation-regenerated-files.zip','w',zipfile.ZIP_DEFLATED) as generated:
        for p in changed:
            data=backup.read(p)
            assert hashlib.sha256(data).hexdigest()==expected[p],p
            generated.write(REPO/p,p);(REPO/p).write_bytes(data)
            assert sha(REPO/p)==expected[p]
            restored.append(p)
        # Retain pre-validation bytes of changed files; unchanged files already match their manifest.
        compact=ROOT/'before-tracked-compact.zip'
        with zipfile.ZipFile(compact,'w',zipfile.ZIP_DEFLATED) as z:
            for p in restored:z.writestr(p,backup.read(p))
    compact.replace(ROOT/'before-tracked.zip')
    save('restoration.json',dict(protectedFiles=len(expected),restored=restored,allProtectedByteHashesEqual=True,
        intentionallyExcluded=sorted(INTENTIONAL),beforeTrackedArchiveRetainsRestoredFilesOnly=True))
def redact():
    patterns=[r'''(?i)(-accessToken\s+|["']?(?:access_token|refresh_token|client_secret)["']?\s*[:=]\s*["']?)([^\s"',}]+)''',
        r'(?i)(-hubSessionId\s+)([^\s\r\n]+)',r'(?i)(-licensingIpc\s+)([^\s\r\n]+)']
    prior=ROOT/'log-redactions.json'
    changed=set(json.loads(prior.read_text(encoding='utf-8'))['files']) if prior.exists() else set()
    for p in ROOT.glob('*.log'):
        text=p.read_text(encoding='utf-8-sig',errors='replace');before=text
        for pattern in patterns:text=re.sub(pattern,r'\1[REDACTED]',text)
        if text!=before:p.write_text(text,encoding='utf-8');changed.add(p.name)
    save('log-redactions.json',dict(files=sorted(changed)))
def seal():
    frozen=json.loads((ROOT/'frozen-final-source.json').read_text(encoding='utf-8'))
    actual={p.relative_to(REPO).as_posix():sha(p) for folder in ('Assets/Scripts','Assets/Tests','Assets/Diagnostics') for p in (REPO/folder).rglob('*.cs')}
    assert actual==frozen['files']
    for name,count in [('editmode.xml',1225),('production-shadow-playmode.xml',22)]:
        result=ET.parse(ROOT/name).getroot().attrib
        assert result['result']=='Passed' and result['failed']=='0' and int(result['passed'])==count
    for batch,count in [('formal',108),('unity-reference',8)]:
        files=list((ROOT/batch/'cases').glob('*.input.json'));assert len(files)==count
        assert all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint']==frozen['fingerprint'] for p in files)
    assert 'cases=108;ticks=388800;' in (ROOT/'formal/disk-audit.txt').read_text(encoding='utf-8-sig')
    assert 'cases=8;ticks=4800;' in (ROOT/'formal/unity-reference-audit.txt').read_text(encoding='utf-8-sig')
    assert 'cases=54;ticks=194260;' in (ROOT/'legacy-p-audit.txt').read_text(encoding='utf-8-sig')
    parity=json.loads((ROOT/'cross-runtime-check.json').read_text(encoding='utf-8'));assert parity['mechanisms']==54 and parity['canonicalBytesEqual']
    counts={}
    for name in ('温度采样与磨血防御-2026-11-13','短组合规划-2026-11-13'):
        old=ROOT.parent/name;files=json.loads((old/'sealed-manifest.json').read_text(encoding='utf-8'))
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            assert all(pool.map(lambda item:sha(old/item[0])==item[1],files.items())),name
        counts[name]=len(files)
    save('previous-evidence-preservation.json',dict(sealedFiles=counts,unchanged=True))
    content=json.loads((ROOT.parent/'偏好互打长局-2026-11-11/before-content.json').read_text(encoding='utf-8-sig'))
    assert all(sha(REPO/p)==h for p,h in content.items())
    save('content-preservation.json',dict(formalFiles=len(content),unchanged=True))
    before=json.loads((ROOT/'before-source.json').read_text(encoding='utf-8'))
    save('change-scope.json',dict(modifiedSource=[p for p,h in actual.items() if p in before and before[p]!=h],
        newSource=[p for p in actual if p not in before],sourceFiles=len(actual),sourceFingerprint=frozen['fingerprint']))
    with zipfile.ZipFile(ROOT/'harness-sources.zip','w',zipfile.ZIP_DEFLATED) as z:
        for p in sorted((ROOT/'harness').glob('*')):
            if p.is_file():z.write(p,p.relative_to(ROOT).as_posix())
    redact()
    files={p.relative_to(ROOT).as_posix():sha(p) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name not in ('sealed-manifest.json','seal-result.json')}
    save('sealed-manifest.json',files)
    save('seal-result.json',dict(sealed=True,files=len(files),sourceFiles=len(actual),sourceFingerprint=frozen['fingerprint'],
        editmode=1225,productionShadowPlaymode=22,formalCases=108,formalTicks=388800,nativeMechanisms=54,nativeReferenceCases=8,
        legacyPReplays=54,formalContentUnchanged=True,previousSealedFiles=counts,behaviorAccepted=False))
    assert all(sha(ROOT/p)==h for p,h in files.items())
    print('SEALED',len(files),'source',frozen['fingerprint'])
if __name__=='__main__':{'restore':restore,'seal':seal}[sys.argv[1]]()
