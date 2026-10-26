"""HUD v5 vector studies (2026-10-26): two reachable snapshots, each in the normal and the
intervention layer. Built on the HUD v4 drawing helpers; writes four 1920 x 1080 SVGs here."""
from pathlib import Path
import sys

OUT = Path(__file__).resolve().parent
sys.dont_write_bytecode = True  # Keep the imported v4 folder free of __pycache__.
sys.path.insert(0, str(OUT.parent / '2026-10-26'))
import build_hud_v4 as h  # noqa: E402

C = h.C
HERO, HEAVY, RAIDER, ALLY, TRAVELER = (928, 615), (1130, 650), (1010, 490), (506, 480), (1260, 400)
at = lambda p, dy: (p[0], p[1] + dy)

def hollow_diamond(x, y, r=6, c=C['gold']):
    h.path(f'M{x:.1f} {y - r:.1f}L{x + r:.1f} {y:.1f}L{x:.1f} {y + r:.1f}L{x - r:.1f} {y:.1f}Z', '#1b2b2e', c, 1.8)

def main_button(x, y, w, hh, label, sub=None, z=20):
    """The single primary action proposed as D1: 'confirm and continue', or plain continue."""
    h.rect(x, y, w, hh, C['gold'], C['gold'], 2)
    if sub:
        h.txt(x + w / 2, y + hh / 2 - (2 if hh > 60 else 3), label, z, C['ink'], 'middle')
        h.txt(x + w / 2, y + hh / 2 + (22 if hh > 60 else 14), sub, max(z - 6, 11), C['ink'], 'middle')
    else:
        h.txt(x + w / 2, y + hh / 2 + z * .35, label, z, C['ink'], 'middle')

def scene(snapshot, layer, segs, hp_ratio):
    dy = -170 if layer == 'intervention' else 0
    h.rect(0, 0, 1920, 1080, '#788776')
    h.add(f'<g transform="translate(0 {-160 if dy else 0}) scale(1.2)">{h.scenery}</g>')
    hero, heavy, raider = at(HERO, dy), at(HEAVY, dy), at(RAIDER, dy)
    # The heavy smash is the only public threat in both snapshots.
    h.path(f'M{heavy[0]} {heavy[1]}L902 {567 + dy}Q874 {652 + dy}977 {701 + dy}Z', '#ba725c', '#dfad8b', 1.6,
           extra='fill-opacity=".24" stroke-dasharray="8 5"')
    h.ell(*hero, 42, 18, 'none', '#d3e4cc', 2)
    plan_b = snapshot == 'B' and layer == 'intervention'
    if plan_b:
        dest = (925, 300)
        h.path(f'M{hero[0]} {hero[1]}L948 380L{dest[0]} {dest[1]}', 'none', '#f1e5c8', 3, 'stroke-dasharray="4 10" stroke-linecap="round"')
        h.ell(*dest, 38, 16, 'none', C['gold'], 2, extra='stroke-dasharray="6 4"')
        h.ell(raider[0], raider[1] + 4, 40, 16, 'none', C['gold'], 2, extra='stroke-dasharray="6 4"')
    h.unit(*at(ALLY, dy), '#64765d', label='莉亚')
    h.unit(*at(TRAVELER, dy), '#8c8671', label='旅人')
    h.unit(*hero, '#527d85')
    if plan_b:
        h.unit(*dest, '#527d85', True)
    h.unit(*raider, '#885c56', label='掠袭者')
    h.unit(*heavy, '#7e5c56', label='重剑兵')
    hit = '5.7' if snapshot == 'A' else '6.9'
    if layer == 'normal':
        h.chip(1080, 448, f'重砸 · 前摇中 · {hit} s 命中', C['red'], 17)
    else:
        h.chip(1190, 380, f'重砸 · 前摇中 · {hit} s 命中', C['red'], 17)
    if snapshot == 'A' and layer == 'normal':
        w = h.chip(680, 470, '格挡 · 5.7 s（暂存）', C['gold'], 17)
        h.path(f'M{680 + w:.1f} 486L900 540', 'none', C['gold'], 1.4, 'stroke-dasharray="3 3"')
    if snapshot == 'B':
        cy = 634 + dy
        h.diamond(1029, cy, 9)
        h.path(f'M1029 {cy + 9}V{cy + 48}', 'none', C['gold'], 1.4)
        h.chip(985, cy + 48, '◆ 6.9 s 拼刀 · 双方终止', C['gold'], 17)
    if plan_b:
        hollow_diamond(980, 300, 8)
        h.chip(1050, 250, '③ 重砸 · 9.5 s · 视界外', C['gold'], 17)
    h.rect(0, 0, 1920, 145, 'url(#headerFade)')
    h.txt(40, 48, '时漏城 / 钟楼街口', 24, C['light'], serif=True, extra='letter-spacing="2"')
    h.txt(40, 82, '守住钟楼入口', 18, C['muted'])
    h.topbar(segs)
    h.txt(1812, 47, '帮助', 18, C['light'], 'end'); h.txt(1880, 47, '菜单', 18, C['light'], 'end')
    h.txt(1880, 79, f'HUD V5 · 设计示意 · 情境 {snapshot}', 14, C['muted'], 'end')
    for i, color in enumerate(['#66858b', '#738369']):
        h.portrait(38, 160 + i * 106, 56, color)
        h.rect(39, 226 + i * 106, 54, 4, '#334b43')
        h.rect(39, 226 + i * 106, 54 * (hp_ratio if i == 0 else 1), 4, C['sage'])
        if i == 0:
            h.path('M30 169V216', 'none', C['gold'], 3)

# ---------- Normal layer ----------
def queue_strip(text, right, right_col):
    h.panel(388, 842, 1052, 62)
    h.txt(410, 879, '行动队列', 18, C['muted'])
    h.txt(530, 880, text, 17, C['muted'])
    h.txt(1417, 880, right, 18, right_col, 'end')

def normal_dock(hp, hp_ratio, adrenaline, selected, enabled, label, sub=None):
    h.panel(24, 923, 1416, 133)
    h.portrait(51, 949, 82)
    h.txt(166, 965, '艾伦', 29, C['light'], serif=True)
    h.txt(166, 996, hp, 19, C['light'])
    h.rect(166, 1009, 177, 5, '#40554e'); h.rect(166, 1009, 177 * hp_ratio, 5, C['sage'])
    h.txt(166, 1040, adrenaline, 16, C['muted'])
    for i, (sym, name) in enumerate(h.ACTIONS):
        x = 405 + i * 128; on = enabled and i == selected
        fill, stroke = ('#dfd7bb', C['gold']) if on else (('#263b3b', C['line']) if enabled else ('#1d2b2d', '#34443f'))
        col = C['ink'] if on else (C['light'] if enabled else C['off'])
        h.rect(x, 944, 104, 80, fill, stroke, 2)
        h.icon(sym, x + 35, 951, 34, col)
        h.txt(x + 52, 1010, name, 20, col, 'middle')
        h.txt(x + 10, 961, str(i + 1), 12, C['ink'] if on else C['muted'])
    if not enabled:
        h.txt(405, 1047, '他人窗口：普通动作需授权（介入中申请）', 13, C['muted'])
    h.line(930, 946, 930, 1037)
    h.txt(948, 971, '核心能力', 16, C['muted'])
    h.relic(False)
    main_button(1262, 944, 156, 94, label, sub)

def timing_bar(x, y, w):
    """Chosen timing (clash): wait, windup to the shared contact, planned recovery kept."""
    T0, T1 = 6.0, 9.6
    f = lambda t: x + w * (t - T0) / (T1 - T0)
    h.rect(x, y, w, 10, '#3e534d', r=2)
    h.rect(f(6.0), y + 1, f(6.4) - f(6.0), 8, 'none', C['muted'], extra='stroke-dasharray="2 3"')
    h.rect(f(6.4), y, f(6.9) - f(6.4), 10, C['gold'])
    h.rect(f(6.9), y, f(7.4) - f(6.9), 10, 'none', C['gold'], extra='stroke-dasharray="4 3"')
    h.rect(f(7.4) - 1.5, y - 3, 3, 16, C['gold'])
    h.rect(f(9.0), y, f(T1) - f(9.0), 10, 'url(#unknown)')
    h.line(f(9.0), y - 8, f(9.0), y + 18, C['light'], 1.5)
    h.diamond(f(6.9), y + 5, 7, C['gold'])
    h.tri(f(6.9), y + 22, 6, C['red'])
    h.txt(x, y - 13, '等待 0.4 s', 14, C['muted'])
    h.txt(f(9.0), y - 13, '窗口末尾 9.0', 15, C['muted'], 'middle')
    h.txt(x, y + 46, '6.0', 15, C['muted'])
    h.txt(f(6.4), y + 46, '6.4', 15, C['light'], 'middle')
    h.txt(f(6.9), y + 46, '6.9', 15, C['gold'], 'middle')
    h.txt(f(7.4), y + 46, '7.4', 15, C['gold'], 'middle')
    h.txt(x, y + 68, '◆ 命中   ▲ 受击   虚框：计划占用   9.0 s 后未知', 14, C['muted'])

TIMINGS = [
    ('尽早', '命中 6.5 s', ['造成 4 · 冲击 3 不足以打断', '6.9 s 仍受重砸 6+硬直（后摇中）'], 'quiet'),
    ('拼刀', '命中 6.9 s', ['双方攻击终止 · 肾上腺素 +1', '残余冲击预计不致硬直 · 先等 0.4 s'], 'selected'),
    ('抓后摇', '命中 6.92 s', ['✕ 6.9 s 先受重砸 6+硬直', '快斩在前摇中被打断'], 'bad'),
]
def timing_card():
    h.panel(1464, 360, 432, 696)
    h.rect(1488, 378, 384, 40, '#3a2f2b', C['red'], 2)
    h.txt(1502, 404, '⚠ 重砸 6.9 s → 艾伦 · 无可行反应（肾上腺素 0）', 15, C['red'])
    h.icon('sword', 1488, 432, 30, C['gold']); h.txt(1532, 457, '快斩 → 重剑兵', 27, C['light'], serif=True)
    h.txt(1488, 487, '伤害 4 · 冲击 3 · 前摇 0.5 s + 后摇 0.5 s', 16, C['muted'])
    h.line(1488, 505, 1872, 505)
    h.txt(1488, 535, '时机', 22, C['light'], serif=True)
    h.txt(1872, 533, '相对：重砸 · 命中 6.9 s', 16, C['muted'], 'end')
    for i, (name, hit, (l1, l2), kind) in enumerate(TIMINGS):
        y = 550 + i * 66
        fill, stroke = {'selected': ('#3b4136', C['gold']), 'bad': ('#2f2a28', '#7d5a50'), 'quiet': ('#283b3b', C['line'])}[kind]
        h.rect(1488, y, 384, 60, fill, stroke, 2)
        h.txt(1502, y + 26, name, 20, C['gold'] if kind == 'selected' else C['light'])
        h.txt(1502, y + 49, hit, 14, C['muted'])
        h.txt(1588, y + 26, l1, 16, C['red'] if kind == 'bad' else C['light'])
        h.txt(1588, y + 49, l2, 15, C['red'] if kind == 'bad' else C['muted'])
    h.txt(1488, 766, '加入后占用', 18, C['muted']); h.txt(1872, 766, '6.4—7.4 s · 预计 6.9 终止', 19, C['light'], 'end')
    timing_bar(1488, 814, 384)
    h.rect(1488, 892, 384, 84, '#1f2e30', C['line'], r=2)
    h.txt(1502, 917, '◆ 6.9 s 与重砸同 Tick 命中 → 拼刀', 17, C['gold'])
    h.txt(1502, 941, '下一项从 7.4 s 衔接：拼刀不提前释放轨道', 16, C['light'])
    h.txt(1502, 965, '其他起点 → 介入精确调整', 15, C['muted'])
    h.button(1488, 1006, 384, '加入队列 · 拼刀', 'primary', 40, 20)

def reaction_card():
    h.panel(1464, 470, 432, 586)
    h.txt(1488, 506, '反应机会', 26, C['light'], serif=True); h.txt(1872, 504, '1 项', 16, C['muted'], 'end')
    h.rect(1488, 522, 384, 68, '#3b4136', C['gold'], 2)
    h.txt(1504, 549, '重砸 → 艾伦', 20); h.txt(1857, 548, '重剑兵', 16, C['muted'], 'end')
    h.txt(1504, 577, '命中 5.7 s · 预计 6 + 硬直', 17, C['muted']); h.txt(1857, 577, '截止 5.4 s', 18, C['gold'], 'end')
    h.line(1488, 606, 1872, 606)
    h.txt(1488, 638, '回应重砸', 24, C['light'], serif=True); h.txt(1872, 636, '可用肾上腺素 3', 17, C['muted'], 'end')
    h.button(1488, 654, 122, '格挡 · 消耗 2', 'selected', 42, 16)
    h.button(1619, 654, 122, '闪避 · 消耗 3', 'quiet', 42, 16)
    h.button(1750, 654, 122, '不响应', 'quiet', 42, 16)
    h.txt(1488, 724, '起点：命中时刻 5.7 s（默认）', 18, C['muted'])
    h.rect(1488, 740, 384, 92, '#22332f', C['sage'], r=2)
    h.txt(1502, 767, '✓ 格挡已暂存 · 预计挡住重砸', 18, C['sage'])
    h.txt(1502, 793, '5.7 s 生效后即终止，不占用你 6.0 s 的窗口', 15, C['light'])
    h.txt(1502, 819, '剩余 1 将在 6.0 s 你的窗口打开时清零', 15, C['muted'])
    h.button(1488, 848, 384, '精确调整 → 介入', 'quiet', 38, 17)
    h.txt(1488, 916, '反应不受窗口限制，无需授权', 16, C['muted'])
    h.txt(1680, 1034, '暂存的反应由底部“确认并继续”提交', 15, C['muted'], 'middle')

def v5_1():
    scene('B', 'normal', [('战术暂停 · 窗口开启', C['light']), ('艾伦的窗口  6.0—9.0 s', C['light']), ('我的窗口 · 可下令', C['muted'])], 20 / 24)
    queue_strip('空 · 选好动作与时机后加入', '0 项待确认', C['muted'])
    normal_dock('20 / 24', 20 / 24, '肾上腺素 0（开窗清零）', 1, True, '▶ 继续')
    timing_card()

def v5_2():
    scene('A', 'normal', [('战术暂停 · 新威胁', C['light']), ('重剑兵的窗口  3.0—6.0 s', C['light']), ('他人窗口 · 可申请授权', C['gold'])], 1)
    queue_strip('他人窗口：普通动作需授权', '反应 1 项待确认', C['gold'])
    normal_dock('24 / 24', 1, '肾上腺素 3（6.0 s 清零）', None, False, '确认并继续', '反应 1')
    reaction_card()

# ---------- Intervention layer ----------
def footer(enabled, legend, label, sub=None):
    h.line(36, 991, 1424, 991)
    col = C['muted'] if enabled else C['off']
    for i, (sym, name) in enumerate(h.ACTIONS):
        x = 44 + i * 94
        h.icon(sym, x, 1007, 27, col); h.txt(x + 33, 1028, name, 16, col)
    h.line(424, 1005, 424, 1041)
    edit = C['light'] if enabled else C['off']
    h.txt(444, 1028, '编辑', 17, edit); h.txt(512, 1028, '移除', 17, edit); h.txt(580, 1028, '撤销', 17, edit)
    h.txt(692, 1017, legend[0], 14, C['muted']); h.txt(692, 1040, legend[1], 14, C['muted'])
    h.relic(True)
    main_button(1240, 1000, 178, 40, label, sub, 16)

def unknown_after(f, t, t1):
    for y, hh in h.ROWS[1:]:
        h.rect(f(t), y, f(t1) - f(t), hh, 'url(#unknown)', extra='opacity=".8"')

def v5_3():
    scene('B', 'intervention', [('战术暂停 · 窗口开启', C['light']), ('艾伦的窗口  6.0—9.0 s', C['light']),
                                ('我的窗口 · 可下令', C['muted']), ('介入中', C['gold'])], 20 / 24)
    f = h.dock(5, 11, '草稿 3 项 · 尚未提交',
               [(5, 6, '重剑兵（已结束）', '#33413c', C['off'], 14),
                (6, 9, '艾伦 · 当前窗口 · 普通动作起点可放区', '#3c5148', C['light'], 16),
                (9, 11, '掠袭者的窗口（预计）', '#58433e', C['red'], 14)], 6.0,
               [('艾伦', '主选', 'HP 20/24 · 肾上腺素 0', '编辑：③ 重砸 · 精确'),
                ('莉亚', '同伴', '肾上腺素 1', ''), ('旅人', '中立', '肾上腺素 0', ''),
                ('掠袭者', '敌方', '肾上腺素 2', ''), ('重剑兵', '敌方', '肾上腺素 1', '')])
    h.rect(f(5), 756, f(6) - f(5), 230, '#101b1d', extra='fill-opacity=".35"')
    unknown_after(f, 9, 11)
    # Hero: relative timing, auto-chained move, precise heavy smash (sources per state table §3.1).
    h.txt(f(6) + 6, 792, '等待', 13, C['muted'])
    h.rect(f(6.4), 774, f(6.9) - f(6.4), 26, '#7a6a4c', C['gold'], 2)
    h.txt(f(6.4) + 6, 792, '① 快斩 · 拼刀', 14)
    h.rect(f(6.9), 774, f(7.4) - f(6.9), 26, 'none', C['gold'], 1, extra='stroke-dasharray="5 3"')
    h.rect(f(7.4), 774, f(8.0) - f(7.4), 26, '#638279', C['sage'], 2, extra='stroke-dasharray="5 3"')
    h.txt(f(7.4) + 8, 792, '② 移动 · 尽早', 14)
    h.rect(f(8.0), 772, f(9.5) - f(8.0), 30, '#7a6a4c', C['gold'], 2.5)
    h.rect(f(9.0), 772, f(9.5) - f(9.0), 30, 'url(#hatch)', extra='opacity=".55"')
    h.txt(f(8.0) + 8, 792, '③ 重砸 · 精确 · 前摇', 14)
    h.rect(f(9.5), 774, f(10.0) - f(9.5), 26, '#4a4436', C['gold'], 1)
    h.txt(f(9.5) + 8, 792, '后摇', 13, C['gold'])
    # Others: inside the horizon every track is complete; non-owners cannot start plans.
    h.txt(f(6.2), 861, '无计划', 16, C['muted'])
    h.txt(f(6.2), 898, '无计划', 16, C['muted'])
    h.txt(f(6.2), 936, '无计划', 16, C['muted'])
    h.txt(f(9.75), 936, '其窗口 · 计划未公开', 15, C['muted'])
    h.rect(f(5.4), 958, f(6.9) - f(5.4), 22, '#855c51', C['red'], 2)
    h.txt(f(5.4) + 10, 975, '重砸 · 前摇', 15)
    h.rect(f(6.9), 958, f(7.4) - f(6.9), 22, 'none', C['red'], 1, extra='stroke-dasharray="5 3"')
    h.txt(f(6.9) + 9, 975, '预计终止', 13, C['red'])
    h.diamond(f(6.9), 969, 5, C['red'])
    # Contacts: clash inside the horizon; heavy smash on the raider beyond it (hollow).
    h.path(f'M{f(6.9):.1f} 964V792', 'none', C['gold'], 2)
    h.diamond(f(6.9), 787, 6, C['gold'])
    h.chip(f(6.9) + 8, 805, '拼刀 · 双方终止', C['gold'], 14, 22)
    h.line(f(9.5), 792, f(9.5), 926, C['gold'], 1.6, 'stroke-dasharray="4 3"')
    hollow_diamond(f(9.5), 787, 6)
    hollow_diamond(f(9.5), 931, 6)
    h.chip(f(9.5) + 8, 805, '视界外 · 结果取决于掠袭者', C['gold'], 14, 22)
    h.line(f(6), 726, f(6), 987, C['light'], 1.6)
    h.line(f(9), 726, f(9), 987, C['gold'], 1.4)
    h.line(f(9) + 3, 726, f(9) + 3, 987, C['blue'], 1.6, extra='stroke-dasharray="6 4"')
    h.txt(f(9) + 12, 857, '窗口末尾 = 已知视界', 14, C['blue'])
    footer(True, ('实心 ◆：视界内   空心 ◆：视界外', '虚框：计划占用，预计不发生'), '确认并继续', '行动 3')
    h.panel(1464, 186, 432, 870)
    h.txt(1488, 225, '反应机会', 26, C['light'], serif=True); h.txt(1872, 225, '无可行项', 18, C['muted'], 'end')
    h.rect(1488, 244, 384, 90, '#283b3b', C['line'], 2)
    h.txt(1504, 271, '重砸 → 艾伦', 20); h.txt(1857, 270, '重剑兵', 16, C['muted'], 'end')
    h.txt(1504, 298, '命中 6.9 s · 肾上腺素 0（开窗清零），无法格挡', 14, C['muted'])
    h.txt(1504, 322, '已安排 ① 快斩 · 拼刀 应对', 15, C['gold'])
    h.line(1488, 350, 1872, 350)
    h.txt(1488, 384, '③ 重砸 → 掠袭者', 24, C['light'], serif=True); h.txt(1872, 382, '精确', 16, C['muted'], 'end')
    h.txt(1488, 430, '起点', 19)
    h.button(1553, 406, 44, '−', h=34, z=21); h.button(1607, 406, 165, '8.0 s', 'selected', 34, 21); h.button(1782, 406, 44, '＋', h=34, z=21)
    h.txt(1488, 472, '占用 8.0—10.0 s · 命中 9.5 s', 17)
    h.txt(1488, 502, '△ 越窗 1.0 s · 起点合法，仍可加入', 17, C['gold'])
    h.line(1488, 522, 1872, 522)
    h.txt(1488, 554, '命中在已知视界外', 20, C['light'], serif=True); h.txt(1872, 552, '掠袭者 9.0 s 开窗', 15, C['muted'], 'end')
    h.txt(1488, 586, '掠袭者公开肾上腺素 2：够格挡，不够闪避', 16)
    h.txt(1488, 614, '他的应对决定结果：', 15, C['muted'])
    for i, line in enumerate(['· 9.0 s 起手快斩 → 9.5 s 同 Tick 拼刀',
                              '· 前摇更长的攻击 → 被打断（冲击 10 ≥ 稳定 10）',
                              '· 9.0 s 前用肾上腺素 2 预先格挡 → 被挡',
                              '· 移出范围 → 落空']):
        h.txt(1488, 644 + i * 28, line, 15)
    h.line(1488, 760, 1872, 760)
    h.txt(1488, 790, '这是预先承诺：结果取决于对方的应对（AI 读招）', 15, C['muted'])
    h.txt(1488, 816, '9.0 s 开窗后他的肾上腺素清零，只能提前格挡', 15, C['muted'])
    h.button(1488, 966, 384, '更新草稿', 'primary', 42, 20)
    h.txt(1680, 1034, '与底部计划统一确认', 15, C['muted'], 'middle')

def v5_4():
    scene('A', 'intervention', [('战术暂停 · 新威胁', C['light']), ('重剑兵的窗口  3.0—6.0 s', C['light']),
                                ('他人窗口 · 可申请授权', C['gold']), ('介入中', C['gold'])], 1)
    f = h.dock(3, 11, '普通计划只读 · 反应可编辑',
               [(3, 6, '重剑兵 · 当前窗口 · 艾伦放置普通动作起点需授权', '#58433e', C['light'], 15),
                (6, 9, '艾伦的窗口（预计）', '#3c5148', C['muted'], 14),
                (9, 11, '掠袭者的窗口（预计）', '#58433e', C['red'], 14)], 4.2,
               [('艾伦', '主选', 'HP 24/24 · 肾上腺素 3', '候选：格挡 · 默认命中时刻'),
                ('莉亚', '同伴', '肾上腺素 1', ''), ('旅人', '中立', '肾上腺素 0', ''),
                ('掠袭者', '敌方', '肾上腺素 2', ''), ('重剑兵', '敌方', '肾上腺素 1', '')], grant_button=True)
    unknown_after(f, 9, 11)
    # History before the current tick.
    h.rect(f(3), 844, f(3.6) - f(3), 22, '#4c6d63', C['sage'], 2, extra='opacity=".45"')
    h.txt(f(3) + 8, 861, '防守 · 已结束', 14, C['off'])
    h.rect(f(3), 756, f(4.2) - f(3), 230, '#101b1d', extra='fill-opacity=".35"')
    # Hero: ordinary starts in this window need a grant; reactions do not.
    h.rect(f(4.2), 774, f(6) - f(4.2), 26, 'url(#needAuth)', '#7d8c85', 1, extra='stroke-dasharray="4 3"')
    h.txt((f(4.2) + f(6)) / 2, 792, '普通动作起点需授权', 14, C['muted'], 'middle')
    h.txt(f(6.1), 792, '6.0 s 起为你的窗口', 14, C['muted'])
    h.rect(f(5.7), 807, f(6.7) - f(5.7), 20, 'none', C['gold'], 1, extra='stroke-dasharray="5 3"')
    h.rect(f(5.7), 807, 3, 20, C['gold'])
    h.rect(f(5.7) + 3, 807, f(6.7) - f(5.7) - 3, 20, 'url(#softOccupancy)', extra='opacity=".5"')
    h.chip(f(6.7) + 8, 805, '预计挡住 · 生效即终止', C['sage'], 14, 24)
    # Non-owners cannot start ordinary plans in this window; the owner's lane is busy past its end.
    for y in (861, 898, 936):
        h.txt(f(4.4), y, '非窗口拥有者 · 只能反应', 15, C['muted'])
    h.attack_block(f, 958, 4.2, 5.7, 6.2, '重砸')
    h.txt(f(6.3), 975, '轨道占用至 6.2 s，本窗口无法再起手', 14, C['muted'])
    h.path(f'M{f(5.7):.1f} 964V817', 'none', C['gold'], 2)
    h.diamond(f(5.7), 817, 6, C['gold'])
    h.line(f(4.2), 726, f(4.2), 987, C['light'], 1.6)
    h.line(f(6), 726, f(6), 987, C['gold'], 1.4)
    h.line(f(9), 726, f(9), 987, C['blue'], 1.8, extra='stroke-dasharray="6 4"')
    h.txt(f(9) + 9, 857, '已知视界', 14, C['blue'])
    footer(False, ('灰斜线：普通动作起点需授权', '斜纹：未知   虚框：名义占用'), '▶ 继续')
    h.panel(1464, 186, 432, 870)
    h.txt(1488, 225, '反应机会', 26, C['light'], serif=True); h.txt(1872, 225, '1 项', 18, C['muted'], 'end')
    h.rect(1488, 244, 384, 76, '#3b4136', C['gold'], 2)
    h.txt(1504, 271, '重砸 → 艾伦', 20); h.txt(1857, 270, '重剑兵', 16, C['muted'], 'end')
    h.txt(1504, 301, '命中 5.7 s · 预计 6 + 硬直', 17, C['muted']); h.txt(1857, 301, '输入截止 5.4 s', 18, C['gold'], 'end')
    h.line(1488, 340, 1872, 340)
    h.txt(1488, 372, '回应重砸', 24, C['light'], serif=True); h.txt(1872, 370, '可用肾上腺素 3', 17, C['muted'], 'end')
    h.button(1488, 392, 182, '格挡  /  消耗 2', 'selected', 48, 20)
    h.button(1690, 392, 182, '闪避  /  消耗 3', h=48, z=20)
    h.txt(1488, 470, '6.0 s 你的窗口打开时，剩余可用值清零', 16, C['muted'])
    h.txt(1488, 504, '持续 1.0 s', 18); h.txt(1872, 504, '不响应', 17, C['muted'], 'end')
    h.txt(1488, 536, '可放起点 4.72—5.7 s · 默认 5.7 s', 17, C['muted'])
    h.txt(1488, 576, '起点', 19)
    h.button(1553, 552, 44, '−', h=34, z=21); h.button(1607, 552, 165, '5.7 s', 'selected', 34, 21); h.button(1782, 552, 44, '＋', h=34, z=21)
    h.txt(1488, 618, '占用 5.7—6.7 s · 生效即终止', 16)
    h.react_bar(1488, 656, 384, 4.0, 7.0, 5.7, 5.7, 6.7, [(5.7, '重砸 5.7')], start_label='起点 = 命中时刻')
    h.txt(1488, 740, '✓ 预计挡住重砸 · 艾伦不受伤', 19, C['sage'])
    h.txt(1488, 770, '反应不受窗口限制，无需授权', 16, C['muted'])
    h.line(1488, 790, 1872, 790)
    h.txt(1488, 822, '并发授权', 22, C['light'], serif=True); h.txt(1872, 820, '未申请', 17, C['muted'], 'end')
    h.txt(1488, 852, '授权后可在 4.2—6.0 s 内放置普通动作起点', 16, C['muted'])
    h.txt(1488, 878, '单独申请与回执，不随“确认并继续”提交', 16, C['muted'])
    h.txt(1488, 904, '关闭介入不撤销授权，也不退款', 16, C['muted'])
    h.button(1488, 966, 384, '暂存格挡 · 消耗 2', 'primary', 42, 20)
    h.txt(1680, 1034, '暂存后由底部“确认并继续”提交', 15, C['muted'], 'middle')

STATES = [
    ('1-normal-own-timing', '常规 · 自身窗口 · 相对时机', '情境 B：艾伦的窗口刚打开，唯一威胁是上一窗口延续过来的重砸；肾上腺素开窗清零，无法格挡，选择拼刀。', v5_1),
    ('2-normal-other-reaction', '常规 · 他人窗口 · 反应卡', '情境 A：重剑兵在自己的窗口起手重砸；艾伦以默认命中时刻暂存格挡，主操作只计反应。', v5_2),
    ('3-intervention-own', '介入 · 自身窗口', '情境 B 的介入视图：拼刀、尽早移动与精确放置的重砸，后者命中在视界外，结果取决于掠袭者的应对。', v5_3),
    ('4-intervention-other', '介入 · 他人窗口', '情境 A 的介入视图：普通计划需授权，非拥有者无计划，重剑兵轨道占用超过窗口末尾；格挡候选与并发授权分开。', v5_4),
]

def build(stem, title, desc, fn):
    h.s = []
    h.add(f'<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080" role="img" aria-labelledby="title desc">'
          f'<title id="title">Project Hero HUD V5 / {title}</title><desc id="desc">设计示意，数值为布局样例，非游戏运行截图。{desc}</desc>'
          f'<defs>{h.DEFS}{h.EXTRA_DEFS}</defs>')
    fn()
    h.add('</svg>')
    target = OUT / f'hero-hud-v5-{stem}.svg'
    target.write_text('\n'.join(h.s), encoding='utf-8')
    print(target.name)

if __name__ == '__main__':
    for state in STATES:
        build(*state)
