from pathlib import Path
import hashlib,json,re,sys,zipfile
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
def digest(p):
    h=hashlib.sha256()
    with p.open('rb') as f:
        for b in iter(lambda:f.read(1024*1024),b''):h.update(b)
    return h.hexdigest()
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def save(n,v):(ROOT/n).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def finish():
    if not (ROOT/'restoration.json').exists():
        restored=[]
        with zipfile.ZipFile(ROOT/'prior-test-artifacts.zip') as old,zipfile.ZipFile(ROOT/'test-regenerated.zip','w',zipfile.ZIP_DEFLATED) as new:
            for p in old.namelist():
                path=REPO/p;before=old.read(p)
                if path.read_bytes()==before:continue
                new.write(path,p);path.write_bytes(before);restored.append(p)
            with zipfile.ZipFile(ROOT/'before-source.zip') as source:
                p='ProjectSettings/ProjectSettings.asset';path=REPO/p
                after=path.read_bytes();before=source.read(p)
                if after!=before:
                    assert after.replace(b';SENTIS_ANALYTICS_ENABLED',b'')==before,'Unexpected project settings change'
                    new.write(path,p);path.write_bytes(before);restored.append(p)
        save('restoration.json',{'restored':restored,'originalBytesRestored':True})
    with zipfile.ZipFile(ROOT/'prior-test-artifacts.zip') as old:
        assert all((REPO/p).read_bytes()==old.read(p) for p in old.namelist())
    frozen=load(ROOT/'frozen-final-source.json');assert all(digest(REPO/p)==h for p,h in frozen['files'].items())
    content=load(ROOT.parent/'偏好互打长局-2026-11-11/before-content.json')
    assert all(digest(REPO/p)==h for p,h in content.items())
    prior={}
    for name in ('温度采样与磨血防御-2026-11-13','短组合规划-2026-11-13','连续施压规划-2026-11-14'):
        base=ROOT.parent/name;seal=load(base/'sealed-manifest.json')
        assert all(digest(base/p)==h for p,h in seal.items()),name
        prior[name]=len(seal)
    save('preservation.json',{'formalContentFiles':len(content),'priorSeals':prior,'allEqual':True})
    redact()
def redact():
    old=ROOT/'log-redactions.json';changed=set(load(old)['files']) if old.exists() else set()
    patterns=[r'''(?i)(-accessToken\s+|["']?(?:access_token|refresh_token|client_secret)["']?\s*[:=]\s*["']?)([^\s"',}]+)''',
        r'(?i)(-hubSessionId\s+)([^\s\r\n]+)',r'(?i)(-licensingIpc\s+)([^\s\r\n]+)']
    for path in ROOT.glob('*.log'):
        value=path.read_text(encoding='utf-8-sig',errors='replace');before=value
        for pattern in patterns:value=re.sub(pattern,r'\1[REDACTED]',value)
        if value!=before:path.write_text(value,encoding='utf-8');changed.add(path.name)
    save('log-redactions.json',{'files':sorted(changed)})
def seal():
    finish()
    for n,want in [('editmode.xml',1228),('production-shadow-playmode.xml',22)]:
        a=ET.parse(ROOT/n).getroot().attrib;assert a['result']=='Passed' and int(a['passed'])==want,(n,a)
    frozen=load(ROOT/'frozen-final-source.json')
    for batch,n in [('formal',162),('unity-reference',12)]:
        inputs=list((ROOT/batch/'cases').glob('*.input.json'));assert len(inputs)==n
        assert all(load(p)['sourceFingerprint']==frozen['fingerprint'] for p in inputs)
    for file,n in [('formal/disk-audit.txt',162),('formal/unity-reference-audit.txt',12),('legacy-audit.txt',162)]:
        assert (ROOT/file).read_text(encoding='utf-8-sig').startswith(f'cases={n};')
    report=load(ROOT/'comparison.json');assert all(report['totals'][s]['cases']==54 for s in ('P','Z','M','B'))
    assert load(ROOT/'continuation-realization.json')['totals']['chosen']==252
    files={p.relative_to(ROOT).as_posix():digest(p) for p in sorted(ROOT.rglob('*')) if p.is_file() and p.name not in ('sealed-manifest.json','seal-result.json')}
    save('sealed-manifest.json',files)
    assert all(digest(ROOT/p)==h for p,h in files.items())
    save('seal-result.json',{'sealed':True,'files':len(files),'sourceFingerprint':frozen['fingerprint'],'formalCases':162,'nativeReferenceCases':12,'legacyCases':162})
    print('Verified and sealed',len(files),'files')
{'finish':finish,'seal':seal}[sys.argv[1]]()
