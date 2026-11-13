"""Snapshot generated-file inputs, analyze native outputs, restore test side effects, and seal evidence."""
from pathlib import Path
import collections
import csv
import gzip
import hashlib
import json
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write(name, value):
    (ROOT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def generated(path):
    return path.startswith((
        '优化任务/执行记录/M1-regression/',
        '优化任务/执行记录/拼刀后摇纠偏-2026-10-29/',
        '优化任务/执行记录/规则AI阶段三-2026-11-03/',
        'Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/',
        'Assets/Diagnostics/Editor/HiddenRuntimeValidation.unity',
    )) or path == 'ProjectSettings/ProjectSettings.asset' or (
        path.startswith('Assets/Tests/') and path.endswith(('.asset', '.asset.meta')))


def preserve():
    results = {}
    for folder in ('防御应对核对-2026-11-09', '长动作预演覆盖-2026-11-05', '正式动作混合AI-2026-11-05'):
        previous = ROOT.parent / folder
        manifest = json.loads((previous / 'sealed-manifest.json').read_text(encoding='utf-8'))
        assert all(sha((previous / path).read_bytes()) == digest for path, digest in manifest.items()), folder
        results[folder] = {'files': len(manifest), 'unchanged': True}
    write('previous-evidence-preservation.json', results)


def snapshot():
    tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=REPO).decode('utf-8').split('\0')
    paths = [p for p in tracked if generated(p) and (REPO / p).is_file()]
    with zipfile.ZipFile(ROOT / 'generated-file-baseline.zip', 'x', zipfile.ZIP_DEFLATED) as archive:
        for path in paths:
            archive.write(REPO / path, path)
    write('generated-file-baseline.json', {p: sha((REPO / p).read_bytes()) for p in paths})
    content = [p for p in tracked if p.startswith(('Assets/Resources/', 'Packages/')) or p == 'Assets/Scenes/CombatSampleScene.unity']
    write('protected-content.json', {p: sha((REPO / p).read_bytes()) for p in content})
    write('origin.json', {'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=REPO).decode().strip(),
                          'initialWorktree': 'clean before task', 'clientDate': '2026-11-09', 'timezone': 'Asia/Shanghai'})
    preserve()
    print('Baseline:', len(paths), 'generated files;', len(content), 'protected content files')


def restore():
    before = json.loads((ROOT / 'generated-file-baseline.json').read_text(encoding='utf-8'))
    changed = [p for p, digest in before.items() if not (REPO / p).exists() or sha((REPO / p).read_bytes()) != digest]
    with zipfile.ZipFile(ROOT / 'validation-regenerated-files.zip', 'x', zipfile.ZIP_DEFLATED) as archive:
        for path in changed:
            if (REPO / path).exists():
                archive.write(REPO / path, path)
    with zipfile.ZipFile(ROOT / 'generated-file-baseline.zip') as archive:
        for path in changed:
            target = (REPO / path).resolve()
            target.relative_to(REPO.resolve())
            assert generated(path)
            target.write_bytes(archive.read(path))
    assert all(sha((REPO / p).read_bytes()) == digest for p, digest in before.items())
    write('restoration.json', {'restoredFiles': changed, 'verifiedFiles': len(before)})
    preserve()
    print('Restored:', len(changed))


def rows(path):
    opener = gzip.open if path.suffix == '.gz' else open
    with opener(path, 'rt', encoding='utf-8-sig', newline='') as stream:
        return list(csv.DictReader(stream))


def analyze():
    matrix = rows(ROOT / 'summary.csv')
    old = {r['case']: r for r in rows(ROOT / 'baseline-summary.csv')}
    scores = rows(ROOT / 'probe-scores.csv')
    branches = rows(ROOT / 'probe-branches.csv')
    accepted = [r for r in branches if r['reply'] != 'None' and not r['rejection']]
    assert all(r['actualMatches'] == 'True' for r in branches)
    assert len(scores) == 40
    assert all(r['rejections'] == '0' for r in matrix)
    choices = []
    for r in matrix:
        current = rows(ROOT / 'cases' / (r['case'] + '.candidates.csv.gz'))
        previous = rows(ROOT.parent / '防御应对核对-2026-11-09' / 'cases' / (r['case'] + '.candidates.csv.gz'))
        selected = lambda data: [(x['tick'], x['controller'], x['key']) for x in data if x['chosen'] == 'True' and int(x['tick']) < int(r['ticks'])]
        choices.append({'case': r['case'], 'trajectoryChanged': selected(current) != selected(previous),
                        'oldHealthLossQ10': int(old[r['case']]['healthLossQ10']), 'newHealthLossQ10': int(r['healthLossQ10'])})
    tests = {}
    for name in ('full-editmode.xml', 'production-shadow-playmode.xml'):
        root = ET.parse(ROOT / name).getroot()
        tests[name] = {key: root.attrib.get(key) for key in ('total', 'passed', 'failed', 'skipped', 'result')}
        assert root.attrib['result'] == 'Passed', name
    full = ET.parse(ROOT / 'full-editmode.xml').getroot()
    targeted = [node for node in full.iter('test-case') if '.AiDefenseEvaluationTests.' in node.attrib.get('fullname', '')]
    assert len(targeted) == 16 and all(node.attrib['result'] == 'Passed' for node in targeted)
    result = {'tests': tests, 'targetedCasesInFullRun': len(targeted), 'cases': len(matrix), 'ticks': sum(int(r['ticks']) for r in matrix),
              'naturallyEnded': sum(r['ended'] == 'True' for r in matrix),
              'oldHealthLossQ10': sum(int(r['healthLossQ10']) for r in old.values()),
              'newHealthLossQ10': sum(int(r['healthLossQ10']) for r in matrix),
              'pairedCases': choices, 'attackContexts': len(scores), 'branches': len(branches),
              'acceptedDefenseBranches': len(accepted), 'rejectedDefenseBranches': len(branches) - 40 - len(accepted),
              'rejectionCodes': dict(collections.Counter(r['rejection'] for r in branches if r['rejection'])),
              'initialNearChoices': [r for r in scores if r['checkpoint'] == '0' and '-near-' in r['case'] and r['chosen'] == 'True']}
    write('analysis.json', result)
    print(json.dumps(result, ensure_ascii=False, indent=2))


def seal():
    preserve()
    protected = json.loads((ROOT / 'protected-content.json').read_text(encoding='utf-8'))
    assert all(sha((REPO / p).read_bytes()) == digest for p, digest in protected.items()), 'Formal content changed'
    files = sorted((p for folder in ('Assets/Scripts', 'Assets/Tests', 'Assets/Diagnostics') for p in (REPO / folder).rglob('*.cs')),
                   key=lambda p: p.relative_to(REPO).as_posix())
    hashes = {p.relative_to(REPO).as_posix(): sha(p.read_bytes()) for p in files}
    fingerprint = sha(''.join(f'{p}:{digest}\n' for p, digest in hashes.items()).encode('utf-8'))
    assert all(json.loads(p.read_text(encoding='utf-8-sig'))['sourceFingerprint'] == fingerprint for p in (ROOT / 'cases').glob('*.input.json'))
    write('frozen-final-source.json', {'fingerprint': fingerprint, 'files': hashes})
    with zipfile.ZipFile(ROOT / 'source-and-content.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(set(hashes) | set(protected)):
            archive.write(REPO / path, path)
    write('sealed-manifest.json', {p.relative_to(ROOT).as_posix(): sha(p.read_bytes()) for p in sorted(ROOT.rglob('*'))
                                   if p.is_file() and p.name != 'sealed-manifest.json'})
    print('Sealed source:', fingerprint)


if __name__ == '__main__':
    {'snapshot': snapshot, 'restore': restore, 'analyze': analyze, 'seal': seal}[sys.argv[1]]()
