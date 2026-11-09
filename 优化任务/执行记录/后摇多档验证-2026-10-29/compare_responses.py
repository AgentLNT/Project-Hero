"""Compare fixed response scripts only where an otherwise identical Repeat cell exists."""
import csv
import json
from collections import Counter

from analyze_recovery import ROOT, group, num, read, summary

IDENTITY=['pair','recovery_ticks','blunt_owner','swap_spawns','opening_impact','growth','health',
    'stability_q10','short_range','wall','max_ticks','seed']
METRICS=['clashes','blunt_dealt_q10','opponent_dealt_q10','blunt_attack_starts','opponent_attack_starts',
    'blunt_control_ticks','opponent_control_ticks','blunt_resource_consumed','opponent_resource_consumed',
    'blunt_cycle_loss','opponent_cycle_loss','blunt_guard_starts','blunt_move_starts',
    'blunt_block_uses','blunt_dodge_uses','guard_contacts','dodge_avoids','block_contacts',
    'blunt_no_resource','blunt_no_opportunity','blunt_no_dodge','blunt_no_reaction_timing','blunt_no_permission']


def main():
    _,coarse=read('coarse')
    _,fine=read('fine')
    rows=coarse+fine
    repeated={tuple(r[k] for k in IDENTITY):r for r in rows if r['response']=='Repeat'}
    compared=[]
    for r in rows:
        if r['response']=='Repeat':
            continue
        baseline=repeated.get(tuple(r[k] for k in IDENTITY))
        item={k:r[k] for k in ['scenario','response']+IDENTITY}
        item.update(paired_repeat=baseline['scenario'] if baseline else '',
            status='paired' if baseline else 'standalone-no-identical-repeat-cell',outcome=r['outcome'])
        for field in METRICS:
            item[field]=r[field]
            item['difference_'+field]=num(r,field)-num(baseline,field) if baseline else ''
        compared.append(item)
    with (ROOT/'response-comparisons.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=list(compared[0]))
        writer.writeheader();writer.writerows(compared)
    lines=['# 30～60 Tick 逐档对照','',
        '每档 212 个固定输入；角色与窗口不对称，以下计数不表示胜率。直接读取真实内核导出结果。',
        '探针为 T=213、钝击单位 1、未交换出生点；击杀为无防御重砸、P0/H24 的窗口制样本。',
        '“取消前摇”为本档所有场景双方已锁定但在命中前终止的攻击；防御计数也是全场景汇总。','',
        '| 后摇 | Δ | 下一击 T+ | 连续重复拼刀 | 等待重对齐拼刀 | 来源窗2钝击出手 | 重砸击杀 Tick | 取消前摇 | 付费 Block | 成功闪避 |',
        '|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|']
    for s in summary(rows):
        lines.append('| '+' | '.join(str(v) for v in [s['recovery'],s['recovery']-30,s['blunt_next_hit'],s['probe_clashes'],
            s['wait_clashes'],s['probe_blunt_window2_starts'],s['heavy_htk24_end'],s['cancelled_windups'],
            s['paid_block_uses'],s['dodge_avoids']])+' |')
    lines.extend(['','逐场实际伤害、资源、控制及位置见 `combined-summary.csv`；策略分支对照见 `response-comparisons.csv`。',
        '短距高生命的 Dodge/Move 探针没有相同输入的 Repeat 格，明确标记 standalone，不把跨场景差异当作分支收益。'])
    (ROOT/'逐档对照.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    result=dict(response_cells=len(compared),status=dict(Counter(r['status'] for r in compared)),
        per_response=dict(Counter(r['response'] for r in compared)),
        note='Standalone probes measure feasibility; their differences are intentionally left empty.')
    (ROOT/'response-audit.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False,indent=2))


if __name__=='__main__':
    main()
