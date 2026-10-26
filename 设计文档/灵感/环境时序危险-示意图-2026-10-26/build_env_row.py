"""Shelved study (2026-10-26): intervention view with a dedicated environment row.
Reuses the HUD v4 drawing helpers; writes one 1920 x 1080 SVG next to this script."""
from pathlib import Path
import sys

OUT = Path(__file__).resolve().parent
sys.dont_write_bytecode = True  # Keep the imported v4 folder free of __pycache__.
sys.path.insert(0, str(OUT.parents[1] / '界面' / '示意图' / '2026-10-26'))
import build_hud_v4 as h  # noqa: E402

C = h.C
ENV = '#b9a3d6'  # Environment hue: distinct from enemy red, applies to every faction.
ENV_DEFS = ('<pattern id="envWarn" width="8" height="8" patternUnits="userSpaceOnUse"><rect width="8" height="8" fill="#3b3548"/>'
            '<path d="M-2 8L8-2M6 10L10 6" stroke="#b9a3d6" stroke-opacity=".6" stroke-width="2"/></pattern>')
X, W, T0, T1 = 300, 1104, 0, 8
f = lambda t: X + W * (t - T0) / (T1 - T0)
ENV_Y, LANE_H = 730, 12
SWING = dict(times=[.3 + i for i in range(8)], warn=.2, cool=.1, y=ENV_Y + 3)
LAVA = dict(times=[.8, 3.9, 7.0], warn=.8, cool=.4, y=ENV_Y + 17)

def env_diamond(x, y, r=5, hollow=False):
    h.path(f'M{x:.1f} {y - r:.1f}L{x + r:.1f} {y:.1f}L{x:.1f} {y + r:.1f}L{x - r:.1f} {y:.1f}Z',
           '#1b2b2e' if hollow else ENV, ENV, 1.6)
def tile(cx, cy, hw, hh, fill, extra='', stroke=ENV):
    h.path(f'M{cx} {cy - hh}L{cx + hw} {cy}L{cx} {cy + hh}L{cx - hw} {cy}Z', fill, stroke, 2, extra)

def scene():
    dy = -170
    start, end = (718, 670 + dy), (928, 615 + dy)
    h.rect(0, 0, 1920, 1080, '#788776')
    h.add(f'<g transform="translate(0 -160) scale(1.2)">{h.scenery}</g>')
    # Lava vent under the destination; the pendulum sweeps across the path.
    tile(928, 449, 74, 37, '#c86a3c', 'fill-opacity=".42" stroke-dasharray="7 4"')
    tile(928, 449, 40, 20, '#e8a05e', 'fill-opacity=".45"', 'none')
    tile(822, 470, 60, 30, '#3b3548', 'fill-opacity=".35" stroke-dasharray="7 4"')
    h.path('M744 438Q822 500 900 438', 'none', ENV, 2, 'stroke-dasharray="3 6" stroke-linecap="round"')
    h.line(840, 290, 812, 432, '#4a5450', 5)
    h.circ(812, 438, 13, '#6a6f68', '#3b423f', 2)
    h.path(f'M{start[0]} {start[1]}L778 486L840 455L{end[0]} {end[1]}', 'none', '#f1e5c8', 3, 'stroke-dasharray="4 10" stroke-linecap="round"')
    h.ell(*start, 42, 18, 'none', '#d3e4cc', 2)
    h.ell(*end, 38, 16, 'none', C['gold'], 2, extra='stroke-dasharray="6 4"')
    h.unit(506, 310, '#64765d', label='莉亚')
    h.unit(1260, 230, '#8c8671', label='旅人 · 未知')
    h.unit(*start, '#527d85')
    h.unit(*end, '#527d85', True)
    h.unit(1010, 320, '#885c56', label='掠袭者')
    h.unit(1130, 480, '#7e5c56', label='重剑兵')
    w = h.chip(430, 520, '摆锤 · 每 1.0 s 扫过', ENV, 17)
    h.chip(430, 556, '经过 0.5—0.7 s → 两次扫过之间通过', C['light'], 17)
    h.path(f'M{430 + w:.1f} 536L766 480', 'none', ENV, 1.4, 'stroke-dasharray="3 3"')
    h.chip(980, 520, '岩浆口 A · 0.8 / 3.9 s 喷发', ENV, 17)
    h.chip(980, 556, '1.2 s 进入 · 下次喷发在窗口外', C['gold'], 17)
    h.path('M1000 520L960 478', 'none', ENV, 1.4, 'stroke-dasharray="3 3"')
    h.rect(0, 0, 1920, 145, 'url(#headerFade)')
    h.txt(40, 48, '时漏城 / 钟楼街口', 24, C['light'], serif=True, extra='letter-spacing="2"')
    h.txt(40, 82, '守住钟楼入口', 18, C['muted'])
    h.topbar([('战术暂停', C['light']), ('艾伦的窗口  0.0—3.0 s', C['light']),
              ('我的窗口 · 可下令', C['muted']), ('介入中', C['gold'])])
    h.txt(1812, 47, '帮助', 18, C['light'], 'end'); h.txt(1880, 47, '菜单', 18, C['light'], 'end')
    h.txt(1880, 79, '环境行 · 搁置的示意', 14, C['muted'], 'end')
    for i, color in enumerate(['#66858b', '#738369']):
        h.portrait(38, 160 + i * 106, 56, color)
        h.rect(39, 226 + i * 106, 54, 4, '#334b43'); h.rect(39, 226 + i * 106, 43 * (.83 if i == 0 else 1), 4, C['sage'])
        if i == 0:
            h.path('M30 169V216', 'none', C['gold'], 3)

def pulses(spec):
    """Warning (hatched) → effect tick (◆) → cooldown, all on the shared time scale."""
    y = spec['y']
    for t in spec['times']:
        a = max(t - spec['warn'], T0)
        if a < T1:
            h.rect(f(a), y, f(min(t, T1)) - f(a), LANE_H, 'url(#envWarn)')
        if t <= T1:
            h.rect(f(t), y, min(f(t + spec['cool']), f(T1)) - f(t), LANE_H, ENV, extra='fill-opacity=".35"')
            env_diamond(f(t), y + LANE_H / 2, 5)

def dock():
    h.panel(24, 608, 1416, 448)
    h.txt(46, 643, '时序规划', 25, C['light'], serif=True)
    h.txt(190, 643, '草稿 2 项 · 反应暂存 1 项 · 尚未提交', 18, C['muted'])
    h.button(770, 619, 120, '回到当前', h=34, z=17)
    h.button(904, 619, 49, '−', h=34, z=21); h.txt(1004, 643, '缩放 100%', 17, C['muted'], 'middle')
    h.button(1057, 619, 49, '＋', h=34, z=20)
    h.button(1120, 619, 85, '‹  0.1 s', h=34, z=16); h.button(1217, 619, 85, '0.1 s  ›', h=34, z=16)
    h.button(1316, 619, 106, '更多 +2', h=34, z=16)
    h.txt(46, 684, '窗口 / 起点', 16, C['muted'])
    for a, b, label, fill, col, z in [(0, 3, '艾伦 · 当前窗口 · 普通动作起点可放区', '#3c5148', C['light'], 16),
                                      (3, 6, '重剑兵的窗口（预计）', '#58433e', C['red'], 14),
                                      (6, 8, '莉亚的窗口（预计）', '#4d5a46', C['muted'], 14)]:
        h.rect(f(a), 666, f(b) - f(a), 25, fill)
        h.txt((f(a) + f(b)) / 2, 684, label, z, col, 'middle')
    h.txt(46, 715, '当前 0.0 s', 16, C['muted'])
    for t in range(T0, T1 + 1):
        h.line(f(t), 722, f(t), 986, '#455d53', .8)
        h.txt(f(t), 715, f'{t}.0', 15, C['muted'], 'middle')
    # Environment row: fixed above unit tracks, outside faction grouping and the 1+4 budget.
    h.rect(36, ENV_Y, 1378, 32, '#2b2a36', extra='fill-opacity=".85"')
    h.line(285, ENV_Y, 285, ENV_Y + 32)
    h.txt(46, ENV_Y + 22, '环境', 18, ENV)
    h.txt(100, ENV_Y + 21, '+1 项', 13, C['muted'])
    h.txt(265, ENV_Y + 13, '摆锤', 13, ENV, 'end')
    h.txt(265, ENV_Y + 28, '岩浆口 A', 13, ENV, 'end')
    pulses(SWING); pulses(LAVA)
    rows = h.ROWS
    for i, (y, hh) in enumerate(rows):
        h.rect(36, y, 1378, hh, '#2c413e' if i == 0 else ('#213335' if i % 2 else '#243638'), extra='fill-opacity=".72"')
        h.line(285, y, 285, y + hh)
    headers = [('艾伦', '主选', 'HP 20/24 · 肾上腺素 3', '候选：格挡喷发 · 起点 3.9 s'),
               ('莉亚', '同伴', '肾上腺素 2', ''), ('旅人', '中立', '肾上腺素 0', ''),
               ('掠袭者', '敌方', '肾上腺素 1', ''), ('重剑兵', '敌方', '肾上腺素 2', '')]
    for (y, _), (name, group, resource, sub) in zip(rows, headers):
        primary = y == rows[0][0]
        h.txt(46, y + 22, name, 20 if primary else 18)
        h.txt(134, y + 22, group, 15, C['muted'])
        if primary:
            h.txt(46, y + 47, resource, 16, C['muted']); h.txt(46, y + 67, sub, 15, C['gold'])
        else:
            h.txt(265, y + 22, resource, 15, C['muted'], 'end')
    # Beyond the known horizon other units are unknown; the environment row continues.
    for y, hh in rows[2:]:
        h.rect(f(3), y, f(T1) - f(3), hh, 'url(#unknown)', extra='opacity=".8"')
    h.txt(f(3.2), 974, '重剑兵的窗口 · 计划未公开', 15, C['muted'])
    # Hero: queued move then quick slash; the pass over the pendulum tile sits between sweeps.
    h.rect(f(0), 774, f(1.2) - f(0), 26, '#638279', C['sage'], 2, extra='stroke-dasharray="5 3"')
    h.txt(f(0) + 8, 793, '① 移动', 16)
    h.rect(f(.5), 770, f(.7) - f(.5), 4, ENV)
    for t in (.3, 1.3):
        h.line(f(t), ENV_Y + 15, f(t), 774, ENV, 1.2, 'stroke-dasharray="2 3"')
    h.rect(f(1.2), 774, f(1.7) - f(1.2), 26, '#7a6a4c', C['gold'], 2, extra='stroke-dasharray="5 3"')
    h.txt(f(1.2) + 8, 793, '② 快斩', 16)
    h.rect(f(1.7), 774, f(2.2) - f(1.7), 26, '#4a4436', C['gold'], 1, extra='stroke-dasharray="5 3"')
    h.txt(f(1.7) + 8, 792, '后摇', 14, C['gold'])
    h.path(f'M{f(1.7):.1f} 800V963', 'none', C['gold'], 1.6)
    h.diamond(f(1.7), 787, 5, C['gold']); h.diamond(f(1.7), 963, 6, C['gold'])
    h.chip(f(1.7) + 10, 953, '命中 4', C['gold'], 14, 22)
    h.txt(f(.15), 974, '空闲', 15, C['muted'])
    # Staged block for the 3.9 s eruption, placed during the hero's own window under option (b).
    h.txt(f(3.9) - 8, 822, '格挡 · 暂存', 14, C['light'], 'end')
    h.rect(f(3.9), 807, f(4.9) - f(3.9), 20, 'none', C['gold'], 1, extra='stroke-dasharray="5 3"')
    h.rect(f(3.9), 807, 3, 20, C['gold'])
    h.rect(f(3.9) + 3, 807, f(4.9) - f(3.9) - 3, 20, 'url(#softOccupancy)', extra='opacity=".7"')
    h.chip(f(4.9) + 8, 805, '预计挡住', C['sage'], 14, 24)
    h.line(f(3.9), LAVA['y'] + LANE_H, f(3.9), 810, ENV, 1.6)
    env_diamond(f(3.9), 817, 6, hollow=True)
    # Others inside the horizon.
    h.rect(f(.4), 844, f(2.4) - f(.4), 22, '#4c6d63', C['sage'], 2)
    h.txt(f(.4) + 12, 861, '防守 · 额度 3/3', 16)
    h.txt(f(.2), 898, '未公开计划', 16, C['muted'])
    h.rect(f(.6), 920, f(1.8) - f(.6), 22, '#5a4a46', C['red'], 1.5)
    h.txt(f(.6) + 10, 937, '移动 · 公开', 15)
    # Own window ends where the next opponent window opens, so the horizon coincides.
    h.line(f(3), 692, f(3), 987, C['gold'], 1.4)
    h.line(f(3) + 3, 722, f(3) + 3, 987, C['blue'], 1.6, extra='stroke-dasharray="6 4"')
    h.txt(f(3) + 12, 790, '窗口末尾 = 已知视界', 14, C['blue'])
    h.rect(1424, 765, 4, 221, '#344a43', r=2); h.rect(1424, 765, 4, 159, '#8a9d8c', r=2)
    h.footer(True, ('淡紫：环境 · 预警 → ◆ 生效 → 冷却', '空心 ◆：视界外，站位可能改变'), 2, True)

def detail():
    h.panel(1464, 186, 432, 870)
    h.txt(1488, 225, '反应机会', 26, C['light'], serif=True); h.txt(1872, 225, '1 项', 18, C['muted'], 'end')
    h.rect(1488, 244, 384, 100, '#2f2d3a', ENV, 2)
    h.txt(1504, 271, '岩浆口 A → 艾伦', 20); h.txt(1857, 270, '环境', 16, ENV, 'end')
    h.txt(1504, 301, '生效 3.9 s · 预计伤害 6', 17, C['muted']); h.txt(1857, 301, '输入截止 3.6 s', 18, C['gold'], 'end')
    h.txt(1504, 329, '按预测站位 · 前瞻至艾伦下次自身窗口', 14, C['muted'])
    h.line(1488, 362, 1872, 362)
    h.txt(1488, 394, '回应喷发', 24, C['light'], serif=True); h.txt(1872, 392, '可用肾上腺素 3', 17, C['muted'], 'end')
    h.button(1488, 412, 182, '格挡  /  消耗 2', 'selected', 48, 20)
    h.button(1690, 412, 182, '闪避  /  消耗 3', h=48, z=20)
    h.txt(1488, 492, '持续 1.0 s', 18); h.txt(1872, 492, '不响应：受伤 6', 17, C['muted'], 'end')
    h.txt(1488, 524, '可放起点 2.92—3.9 s · 默认 3.9 s', 17, C['muted'])
    h.txt(1488, 564, '起点', 19)
    h.button(1553, 540, 44, '−', h=34, z=21); h.button(1607, 540, 165, '3.9 s', 'selected', 34, 21); h.button(1782, 540, 44, '＋', h=34, z=21)
    h.txt(1488, 606, '占用 3.9—4.9 s · 硬段仅生效 Tick', 16, C['light'])
    h.react_bar(1488, 644, 384, 2.5, 5.0, 3.9, 3.9, 4.9, [(3.9, '喷发 3.9')], start_label='起点 = 生效时刻')
    h.txt(1488, 728, '✓ 预计挡住喷发 · 艾伦不受伤', 19, C['sage'])
    h.txt(1488, 758, '在自己的窗口内预先安排，不触发威胁暂停', 16, C['muted'])
    h.line(1488, 778, 1872, 778)
    h.txt(1488, 808, '视界外风险', 20, C['light'], serif=True)
    h.txt(1488, 838, '重剑兵若把艾伦推离此格，格挡落空', 16, C['red'])
    h.txt(1488, 862, '落空是否退款：待定', 15, C['muted'])
    h.line(1488, 880, 1872, 880)
    h.txt(1488, 908, '另一选择：快斩后 2.2—3.0 s 移出此格', 16, C['light'])
    h.txt(1488, 932, '移出后，下一轮须重新接近重剑兵', 15, C['muted'])
    h.button(1488, 958, 384, '更新反应草稿', 'primary', 42, 20)
    h.txt(1680, 1034, '与底部计划统一确认', 15, C['muted'], 'middle')

def study():
    scene(); dock(); detail()

if __name__ == '__main__':
    h.build(OUT / 'env-row-intervention.svg', '环境行（搁置的示意）',
            '介入时间轴中环境单独成行：摆锤与岩浆口的预警、生效与冷却；艾伦在两次扫过之间通过，3.9 s 的喷发按选项 (b) 在自身窗口内预先安排格挡。',
            study, ENV_DEFS)
