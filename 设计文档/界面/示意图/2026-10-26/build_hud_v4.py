"""HUD v4 vector studies (2026-10-26). Builds four 1920 x 1080 states corrected against the
revised layered HUD plan. Standard library only; reuses the v2 vector environment and icons."""
from pathlib import Path
from html import escape
import math
import re

OUT = Path(__file__).resolve().parent
OLD = OUT.parent / '2026-10-19' / 'hero-hud-normal-vector-v2.svg'
old = OLD.read_text(encoding='utf-8')
DEFS = re.search(r'<defs>(.*?)</defs>', old, re.S).group(1)
# The v2 environment ends just before its first tactical ground highlight.
MARKER = '<polygon points="676.0,512.0'
if MARKER not in old:
    raise RuntimeError('Expected vector environment delimiter not found')
scenery = old.split('</defs>', 1)[1].split(MARKER, 1)[0]

C = dict(ink='#172528', panel='#1b2b2e', light='#f0e5cc', muted='#b7c2b5', gold='#d9bd82',
         sage='#95b5a6', red='#cf927e', dim='#627a72', line='#53665d', blue='#b1c5c0', off='#6f7f78')
s = []
def add(v): s.append(v)
def rect(x, y, w, h, fill, stroke='none', r=0, extra=''):
    add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{max(w, 0):.1f}" height="{h:.1f}" rx="{r}" fill="{fill}" stroke="{stroke}" {extra}/>')
def line(x, y, xx, yy, c=C['line'], sw=1, extra=''):
    add(f'<line x1="{x:.1f}" y1="{y:.1f}" x2="{xx:.1f}" y2="{yy:.1f}" stroke="{c}" stroke-width="{sw}" {extra}/>')
def path(d, fill='none', stroke='none', sw=1, extra=''):
    add(f'<path d="{d}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" {extra}/>')
def txt(x, y, t, z=20, c=C['light'], anchor='start', serif=False, extra=''):
    font = 'Georgia,SimSun,serif' if serif else 'Microsoft YaHei,Segoe UI,sans-serif'
    add(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{font}" font-size="{z}" fill="{c}" text-anchor="{anchor}" {extra}>{escape(str(t))}</text>')
def circ(x, y, r, fill, stroke='none', sw=1, extra=''):
    add(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" {extra}/>')
def ell(x, y, rx, ry, fill, stroke='none', sw=1, extra=''):
    add(f'<ellipse cx="{x:.1f}" cy="{y:.1f}" rx="{rx}" ry="{ry}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" {extra}/>')
def diamond(x, y, r=5, c=C['gold']):
    path(f'M{x:.1f} {y - r:.1f}L{x + r:.1f} {y:.1f}L{x:.1f} {y + r:.1f}L{x - r:.1f} {y:.1f}Z', c, c, 1.3)
def tri(x, y, r=6, c=C['red']):
    """Upward marker for a public incoming hit."""
    path(f'M{x:.1f} {y - r:.1f}L{x + r:.1f} {y + r * .8:.1f}L{x - r:.1f} {y + r * .8:.1f}Z', c, c, 1)
def icon(name, x, y, size=30, c=C['light']):
    add(f'<use href="#{name}" x="{x}" y="{y}" width="{size}" height="{size}" color="{c}"/>')
def tw(t, z):
    """Rough rendered width; CJK and full-width punctuation take one em."""
    return sum(z if ord(ch) >= 0x2E80 or ch in '—' else z * .56 for ch in str(t))
STYLES = {'primary': (C['gold'], C['gold'], C['ink']), 'selected': ('#344a46', C['gold'], C['light']),
          'quiet': ('#233639', C['line'], C['muted']), 'disabled': ('#1d2b2d', '#34443f', C['off']),
          'accent': ('#2c3a33', C['gold'], C['gold'])}
def button(x, y, w, label, kind='quiet', h=38, z=18):
    fill, stroke, col = STYLES[kind]
    rect(x, y, w, h, fill, stroke, 2, extra='stroke-dasharray="4 3"' if kind == 'disabled' else '')
    txt(x + w / 2, y + h / 2 + z * .35, label, z, col, 'middle')
def panel(x, y, w, h):
    path(f'M{x + 12} {y}H{x + w - 12}L{x + w} {y + 12}V{y + h}H{x}V{y + 12}Z', 'url(#surface)', C['line'], 1)
    line(x + 12, y, x + w - 12, y, C['gold'], 1.4)
def portrait(x, y, size=70, color='#66858b'):
    add(f'<g transform="translate({x} {y}) scale({size / 70})">')
    path('M0 8L35 -5L70 8V60L35 74L0 60Z', '#253e40', C['gold'])
    path('M5 60L10 43L26 37H46L62 45L66 60L35 70Z', color)
    path('M22 15Q34 3 49 16L47 38L36 47L25 38Z', '#d8b793')
    path('M20 25L19 13L27 5L40 3L52 13L51 27L43 16L32 21L27 17L24 28Z', '#413f35')
    path('M18 43L30 49L38 46L53 40L57 49L37 58L22 53Z', color)
    add('</g>')
def unit(x, y, coat, ghost=False, label=None):
    add(f'<g transform="translate({x} {y})" opacity="{.55 if ghost else 1}">')
    ell(4, 4, 32, 13, '#263c35', extra='opacity=".4"')
    path('M-18 -66L-29 -17L-7 -4L23 -15L17 -68Z', coat)
    path('M-15 -32L-13 -5L-3 0L1 -34M9 -33L11 -5L23 -2L21 -40', '#343d38', '#263732', 1.5)
    path('M-20 -70L4 -79L23 -64L17 -36L-9 -32L-23 -47Z', '#8fa49a', '#324b47', 2)
    path('M-19 -62L14 -68L12 -44L-11 -42Z', coat)
    path('M-21 -62L-32 -44L-27 -23L-19 -24L-19 -46M20 -61L30 -44L20 -27L14 -32L20 -48', 'url(#steel)', '#39504a')
    path('M-15 -86Q-13 -109 5 -105Q24 -100 17 -80L5 -68L-7 -73Z', 'url(#skin)', '#555b49')
    path('M-17 -88L-19 -101L-9 -113L7 -112L20 -102L20 -85L10 -98L0 -93L-8 -98L-7 -84Z', '#473f32')
    path('M-17 -73L-2 -66L19 -73L17 -61L0 -57L-20 -64Z', coat)
    path('M18 -31L48 -76L52 -77L49 -68L23 -26Z', '#e6dfc4', '#58736b')
    line(14, -34, 29, -22, '#bbac7a', 3)
    add('</g>')
    if label:
        txt(x, y - 137, label, 19, C['light'], 'middle', extra='paint-order="stroke" stroke="#394b42" stroke-width="5" stroke-linejoin="round"')
def chip(x, y, label, c, z=17, h=32):
    w = tw(label, z) + 26
    rect(x, y, w, h, C['ink'], c, 2, extra='fill-opacity=".94"')
    txt(x + 13, y + h / 2 + z * .36, label, z, c)
    return w

RELIC_X = 1090  # The intervention entry keeps the same anchor in both layers.
def relic(active):
    x, y = RELIC_X, 1015
    if active:
        add(f'<g transform="translate({x} 1023) scale(.66) translate({-x} -1015)">')
    path(f'M{x - 44} {y - 33}L{x} {y - 48}L{x + 44} {y - 33}L{x + 52} {y}L{x + 44} {y + 33}L{x} {y + 48}L{x - 44} {y + 33}L{x - 52} {y}Z', '#20383a', C['gold'], 1)
    circ(x, y, 38, '#34504e' if active else '#172d30', C['gold'], 2.5)
    circ(x, y, 30, 'none', '#91b2ac', 1)
    for a in range(0, 360, 45):
        a = math.radians(a)
        line(x + 33 * math.cos(a), y + 33 * math.sin(a), x + 37 * math.cos(a), y + 37 * math.sin(a), C['gold'], 1.5)
    icon('hourglass', x - 20, y - 22, 40, C['light'])
    if active:
        add('</g>')
    txt(x + 69, 1011, '介入中' if active else '介入', 23, C['gold'], serif=True)
    txt(x + 69, 1037, '返回常规' if active else '时间视图', 15, C['muted'])

def topbar(segs):
    pad, lead = 22, 18
    widths = [tw(t, 19) + 2 * pad for t, _ in segs]
    total = sum(widths) + lead
    x0 = 960 - total / 2
    rect(x0, 23, total, 48, C['ink'], C['line'], 2, extra='fill-opacity=".92"')
    circ(x0 + 24, 47, 4, C['gold'])
    x = x0 + lead
    for i, ((t, c), w) in enumerate(zip(segs, widths)):
        if i:
            line(x, 34, x, 60)
        txt(x + pad, 54, t, 19, c)
        x += w

def scene(mode, segs, hero_hp=78):
    active = mode in ('own', 'other')
    rect(0, 0, 1920, 1080, '#788776')
    add(f'<g transform="translate(0 {-160 if active else 0}) scale(1.2)">{scenery}</g>')
    dy = -170 if active else 0
    ally = (506, 480 + dy); start = (718, 670 + dy); end = (928, 615 + dy)
    a = (1010, 490 + dy); b = (1130, 650 + dy)
    moved = mode == 'other'  # Later moment: the movement has completed.
    hero = end if moved else start
    if not moved:
        path(f'M{start[0]} {start[1]}L778 {656 + dy}L840 {625 + dy}L{end[0]} {end[1]}', 'none', '#f1e5c8', 3, 'stroke-dasharray="4 10" stroke-linecap="round"')
        ell(*end, 38, 16, 'none', C['gold'], 2, extra='stroke-dasharray="6 4"')
    ell(*hero, 42, 18, 'none', '#d3e4cc', 2)
    if mode in ('reaction', 'own'):
        # Public attack ranges of the two locked opponent attacks.
        path(f'M1010 {490 + dy}L865 {616 + dy}Q966 {664 + dy}1076 {585 + dy}Z', '#ba725c', '#db9a7d', 1.6, extra='fill-opacity=".24" stroke-dasharray="8 5"')
        path(f'M1130 {650 + dy}L902 {567 + dy}Q874 {652 + dy}977 {701 + dy}Z', '#ba725c', '#dfad8b', 1.6, extra='fill-opacity=".22" stroke-dasharray="8 5"')
    if mode == 'other':
        path(f'M1130 {650 + dy}L880 {575 + dy}Q872 {660 + dy}1000 {700 + dy}Z', '#ba725c', '#dfad8b', 1.6, extra='fill-opacity=".26" stroke-dasharray="8 5"')
    if mode == 'timing':
        path(f'M1130 {650 + dy}L902 {567 + dy}Q874 {652 + dy}977 {701 + dy}Z', '#ba725c', '#dfad8b', 1.6, extra='fill-opacity=".24" stroke-dasharray="8 5"')
    if mode in ('plan', 'timing'):
        ell(b[0], b[1] + 4, 40, 16, 'none', C['gold'], 2, extra='stroke-dasharray="6 4"')
        path(f'M{end[0] + 22} {end[1] - 44}L{b[0] - 26} {b[1] - 48}', 'none', C['gold'], 2, 'stroke-dasharray="2 7" stroke-linecap="round"')
    unit(*ally, '#64765d', label='莉亚')
    unit(1260, 400 + dy, '#8c8671', label='旅人 · 未知' if active else None)
    unit(*hero, '#527d85')
    if not moved:
        unit(*end, '#527d85', True)
    unit(*a, '#885c56', label='掠袭者')
    unit(*b, '#7e5c56', label='重剑兵')
    if mode == 'reaction':
        w = chip(600, 503, '⚠ 轻斩 4.6 s · 重击 5.4 s', C['red'])
        path(f'M{600 + w:.1f} 519L896 532', 'none', C['red'], 1.4, 'stroke-dasharray="3 3"')
    if mode == 'own':
        unit(985, 515 + dy, '#885c56', True)
        diamond(928, 615 + dy, 8)
        path(f'M942 {615 + dy}L998 {574 + dy}H1268', 'none', C['gold'], 1.4)
        chip(1000, 539 + dy, '◆ 4.6 s  格挡被轻斩先消耗', C['gold'], 18, 40)
        txt(644, 586, '游标 4.6 s · 显示已公开计划与攻击范围', 18, C['light'], extra='paint-order="stroke" stroke="#344a42" stroke-width="5"')
    if mode == 'other':
        chip(1190, 500, '横扫 → 艾伦 · 命中 7.2 s', C['red'], 18, 36)
    if mode == 'timing':
        # The opponent's committed swing is the reference the timing choice is made against.
        chip(1080, 448, '重砸 · 前摇中 · 2.4 s 命中', C['red'], 17)
        diamond(1029, 634, 9)
        path('M1029 643V682', 'none', C['gold'], 1.4)
        chip(985, 682, '◆ 2.4 s 拼刀 · 双方终止', C['gold'], 17)
    rect(0, 0, 1920, 145, 'url(#headerFade)')
    txt(40, 48, '时漏城 / 钟楼街口', 24, C['light'], serif=True, extra='letter-spacing="2"')
    txt(40, 82, '守住钟楼入口', 18, C['muted'])
    topbar(segs)
    txt(1812, 47, '帮助', 18, C['light'], 'end'); txt(1880, 47, '菜单', 18, C['light'], 'end')
    txt(1880, 79, 'HUD V4 · 设计示意', 14, C['muted'], 'end')
    for i, (name, color) in enumerate([('艾伦', '#66858b'), ('莉亚', '#738369')]):
        portrait(38, 160 + i * 106, 56, color)
        rect(39, 226 + i * 106, 54, 4, '#334b43')
        rect(39, 226 + i * 106, 43 * (hero_hp / 78 if i == 0 else 1), 4, C['sage'])
        if i == 0:
            path(f'M30 {169 + i * 106}V{216 + i * 106}', 'none', C['gold'], 3)

# ---------- Normal layer ----------
ACTIONS = [('move', '移动'), ('sword', '攻击'), ('shield', '防守'), ('potion', '物品')]
def normal_dock(selected, n, hp='78 / 100', hp_ratio=.78):
    panel(24, 923, 1416, 133)
    portrait(51, 949, 82)
    txt(166, 965, '艾伦', 29, C['light'], serif=True)
    txt(166, 996, hp, 19, C['light'])
    rect(166, 1009, 177, 5, '#40554e'); rect(166, 1009, 177 * hp_ratio, 5, C['sage'])
    txt(166, 1040, '肾上腺素 3', 17, C['muted'])
    for i, (sym, name) in enumerate(ACTIONS):
        x = 405 + i * 128; on = i == selected
        rect(x, 946, 104, 83, '#dfd7bb' if on else '#263b3b', C['gold'] if on else C['line'], 2)
        icon(sym, x + 35, 955, 34, C['ink'] if on else C['light'])
        txt(x + 52, 1014, name, 20, C['ink'] if on else C['light'], 'middle')
        txt(x + 10, 963, str(i + 1), 12, C['ink'] if on else C['muted'])
    line(930, 946, 930, 1037)
    txt(948, 971, '核心能力', 16, C['muted'])
    relic(False)
    button(1262, 944, 156, f'确认行动 ({n})', 'primary', 44, 18)
    button(1262, 996, 156, '▶ 继续', 'quiet', 42, 18)

def queue_strip(items, n):
    panel(388, 842, 1052, 62)
    txt(410, 879, '行动队列', 18, C['muted'])
    x = 530
    for sym, label, span, tags in items:
        w = 300 if not tags else 420
        rect(x, 854, w, 38, '#2c4441', C['line'], 2)
        icon(sym, x + 12, 861, 24, C['sage'])
        txt(x + 46, 880, label, 19)
        right = x + w - 12
        for tag in reversed(tags):
            tag_w = tw(tag, 15) + 16
            right -= tag_w
            rect(right, 861, tag_w, 24, '#3a2f2b' if tag == '受击' else '#3b3a2c', C['red'] if tag == '受击' else C['gold'], 1.5)
            txt(right + tag_w / 2, 878, tag, 15, C['red'] if tag == '受击' else C['gold'], 'middle')
            right -= 8
        txt(right - 4, 880, span, 18, C['muted'], 'end')
        x += w + 12
    if items[-1][3]:
        txt(x + 8, 880, '移除', 17, C['muted'])
    else:
        txt(x + 8, 880, '编辑', 17, C['muted']); txt(x + 62, 880, '移除', 17, C['muted'])
    txt(1417, 880, f'{n} 项待确认', 18, C['gold'], 'end')

def plan_bar(x, y, w):
    """Read-only position bar: queued part, candidate, window end, public hits, known horizon."""
    T1 = 7.6
    f = lambda t: x + w * t / T1
    rect(x, y, w, 10, '#3e534d', r=2)
    rect(f(0), y, f(3.8) - f(0), 10, '#6f8a80')
    rect(f(3.8), y, f(6.0) - f(3.8), 10, C['gold'])
    rect(f(6.0), y, f(6.8) - f(6.0), 10, 'url(#hatch)')
    rect(f(6.8) - 1.5, y - 3, 3, 16, C['gold'])
    rect(f(7.0), y, f(T1) - f(7.0), 10, 'url(#unknown)')
    line(f(6.0), y - 8, f(6.0), y + 18, C['light'], 1.5)
    line(f(7.0), y - 6, f(7.0), y + 16, C['blue'], 1.5, 'stroke-dasharray="3 2"')
    txt(f(6.0), y - 13, '窗口末尾 6.0', 15, C['muted'], 'middle')
    for t in (4.6, 5.4):
        tri(f(t), y + 20, 6, C['red'])
    txt(x, y + 44, '0', 15, C['muted'])
    txt(f(3.8), y + 44, '3.8', 15, C['light'], 'middle')
    txt(f(4.6), y + 44, '4.6', 15, C['red'], 'middle')
    txt(f(5.4), y + 44, '5.4', 15, C['red'], 'middle')
    txt(f(6.8), y + 44, '6.8', 15, C['gold'], 'middle')
    txt(x, y + 66, '▲ 公开受击     ┆ 7.0 s 已知视界，之后未知', 14, C['muted'])

def plan_card():
    panel(1464, 520, 432, 536)
    # Pending threats stay visible while the player previews an ordinary action.
    rect(1488, 538, 384, 40, '#3a2f2b', C['red'], 2)
    txt(1504, 565, '⚠ 2 个反应机会 · 最早截止 4.3 s', 17, C['red'])
    txt(1858, 565, '查看 ›', 17, C['light'], 'end')
    icon('sword', 1488, 598, 30, C['gold']); txt(1532, 623, '普通攻击', 27, C['light'], serif=True)
    txt(1488, 655, '目标  重剑兵', 18, C['muted'])
    txt(1488, 716, '24', 58, C['light'], serif=True)
    txt(1584, 688, '预计伤害', 17, C['muted']); txt(1584, 718, '生命 42 → 18', 22)
    txt(1488, 752, '结果：命中 6.3 s · 原地后摇', 18, C['muted'])
    line(1488, 770, 1872, 770)
    txt(1488, 800, '加入后占用', 18, C['muted']); txt(1872, 800, '3.8—6.8 s', 21, C['light'], 'end')
    plan_bar(1488, 832, 384)
    # Fixed hint slot: lines are replaced in place; adding stays available.
    rect(1488, 912, 384, 82, '#1f2e30', C['line'], r=2)
    txt(1502, 937, '△ 超出窗口 0.8 s · 仍可加入', 17, C['gold'])
    txt(1502, 961, '▲ 期间受击：4.6 s 轻斩、5.4 s 重击', 17, C['red'])
    txt(1502, 985, '届时无法格挡/闪避（被本攻击占用）', 16, C['red'])
    button(1488, 1006, 384, '加入队列', 'primary', 40, 20)

def reaction_card():
    panel(1464, 470, 432, 586)
    txt(1488, 506, '反应机会', 26, C['light'], serif=True); txt(1872, 504, '2 项 · 按截止排序', 16, C['muted'], 'end')
    for y, name, who, hit, deadline, on in [(522, '轻斩 → 艾伦', '掠袭者', '命中 4.6 s', '截止 4.3 s', False),
                                            (600, '重击 → 艾伦', '重剑兵', '命中 5.4 s · 预计 24', '截止 5.1 s', True)]:
        rect(1488, y, 384, 68, '#3b4136' if on else '#283b3b', C['gold'] if on else C['line'], 2)
        txt(1504, y + 27, name, 20); txt(1857, y + 26, who, 16, C['muted'], 'end')
        txt(1504, y + 55, hit, 17, C['muted']); txt(1857, y + 55, deadline, 18, C['gold'], 'end')
    line(1488, 684, 1872, 684)
    txt(1488, 716, '回应重击', 24, C['light'], serif=True); txt(1872, 714, '可用肾上腺素 3', 17, C['muted'], 'end')
    button(1488, 732, 122, '格挡 · 消耗 2', 'disabled', 42, 16)
    button(1619, 732, 122, '闪避 · 消耗 3', 'disabled', 42, 16)
    button(1750, 732, 122, '不响应', 'quiet', 42, 16)
    txt(1488, 802, '起点：命中时刻 5.4 s（默认）', 18, C['muted'])
    rect(1488, 818, 384, 74, '#3a2f2b', C['red'], r=2)
    txt(1502, 845, '✕ 格挡、闪避均不可用', 19, C['red'])
    txt(1502, 876, '5.4 s 时艾伦被 ② 攻击（3.8—6.8 s）占用', 16, C['light'])
    button(1488, 906, 384, '移除 ② 并格挡 · 消耗 2', 'primary', 42, 19)
    button(1488, 958, 384, '精确调整 → 介入', 'quiet', 38, 17)
    txt(1680, 1034, '选定的反应与行动队列统一确认', 15, C['muted'], 'middle')

def normal_plan():
    scene('plan', [('战术暂停', C['light']), ('艾伦的窗口  0.0—6.0 s', C['light']), ('我的窗口 · 可下令', C['muted'])])
    queue_strip([('move', '① 移动', '0.0—3.8 s', [])], 1)
    normal_dock(1, 1)
    plan_card()

def normal_reaction():
    scene('reaction', [('战术暂停', C['light']), ('艾伦的窗口  0.0—6.0 s', C['light']), ('我的窗口 · 可下令', C['muted'])])
    queue_strip([('move', '① 移动', '0.0—3.8 s', []), ('sword', '② 攻击 → 重剑兵', '3.8—6.8 s', ['越窗', '受击'])], 2)
    normal_dock(None, 2)
    reaction_card()

def timing_bar(x, y, w):
    """Selected timing (clash): queued move, explicit wait gap, windup to the shared contact."""
    T1 = 3.6
    f = lambda t: x + w * t / T1
    rect(x, y, w, 10, '#3e534d', r=2)
    rect(f(0), y, f(1.0) - f(0), 10, '#6f8a80')
    rect(f(1.0), y + 1, f(1.9) - f(1.0), 8, 'none', C['muted'], extra='stroke-dasharray="2 3"')
    rect(f(1.9), y, f(2.4) - f(1.9), 10, C['gold'])
    rect(f(2.4), y, f(2.9) - f(2.4), 10, 'none', C['gold'], extra='stroke-dasharray="4 3"')
    rect(f(3.0), y, f(T1) - f(3.0), 10, 'url(#unknown)')
    line(f(3.0), y - 8, f(3.0), y + 18, C['light'], 1.5)
    diamond(f(2.4), y + 5, 7, C['gold'])
    tri(f(2.4), y + 22, 6, C['red'])
    txt(f(1.45), y - 13, '等待 0.9 s', 14, C['muted'], 'middle')
    txt(f(3.0), y - 13, '窗口末尾 3.0', 15, C['muted'], 'middle')
    txt(x, y + 46, '0', 15, C['muted'])
    txt(f(1.0), y + 46, '1.0', 15, C['muted'], 'middle')
    txt(f(1.9), y + 46, '1.9', 15, C['light'], 'middle')
    txt(f(2.4), y + 46, '2.4', 15, C['gold'], 'middle')
    txt(x, y + 68, '◆ 本次命中   ▲ 公开受击   虚框：拼刀后不发生   3.0 s 后未知', 14, C['muted'])

TIMINGS = [
    ('尽早', '命中 1.5 s', ['造成 4 · 冲击 3 不足以打断', '2.4 s 仍受重砸 6+硬直 · 届时可格挡'], 'quiet'),
    ('拼刀', '命中 2.4 s', ['双方攻击终止 · 肾上腺素 +1', '残余冲击预计不致硬直 · 先等 0.9 s'], 'selected'),
    ('抓后摇', '命中 2.42 s', ['✕ 2.4 s 先受重砸 6+硬直', '快斩被打断：届时在前摇，无法格挡'], 'bad'),
]
def timing_card():
    panel(1464, 360, 432, 696)
    rect(1488, 378, 384, 40, '#3a2f2b', C['red'], 2)
    txt(1504, 405, '⚠ 1 个反应机会 · 截止 2.1 s', 17, C['red'])
    txt(1858, 405, '查看 ›', 17, C['light'], 'end')
    icon('sword', 1488, 432, 30, C['gold']); txt(1532, 457, '快斩 → 重剑兵', 27, C['light'], serif=True)
    txt(1488, 487, '伤害 4 · 冲击 3 · 前摇 0.5 s + 后摇 0.5 s', 16, C['muted'])
    line(1488, 505, 1872, 505)
    txt(1488, 535, '时机', 22, C['light'], serif=True)
    txt(1872, 533, '相对：重砸 · 命中 2.4 s', 16, C['muted'], 'end')
    for i, (name, hit, (l1, l2), kind) in enumerate(TIMINGS):
        y = 550 + i * 66
        fill, stroke = {'selected': ('#3b4136', C['gold']), 'bad': ('#2f2a28', '#7d5a50'), 'quiet': ('#283b3b', C['line'])}[kind]
        rect(1488, y, 384, 60, fill, stroke, 2)
        txt(1502, y + 26, name, 20, C['gold'] if kind == 'selected' else C['light'])
        txt(1502, y + 49, hit, 14, C['muted'])
        txt(1588, y + 26, l1, 16, C['red'] if kind == 'bad' else C['light'])
        txt(1588, y + 49, l2, 15, C['red'] if kind == 'bad' else C['muted'])
    txt(1488, 766, '加入后占用', 18, C['muted']); txt(1872, 766, '1.9—2.4 s（拼刀终止）', 19, C['light'], 'end')
    timing_bar(1488, 814, 384)
    rect(1488, 892, 384, 84, '#1f2e30', C['line'], r=2)
    txt(1502, 917, '◆ 2.4 s 与重砸同 Tick 命中 → 拼刀', 17, C['gold'])
    txt(1502, 941, '拼刀后 2.4—3.0 s 轨道空出，可再加一项', 16, C['light'])
    txt(1502, 965, '其他起点 → 介入精确调整', 15, C['muted'])
    button(1488, 1006, 384, '加入队列 · 拼刀', 'primary', 40, 20)

def normal_timing():
    scene('timing', [('战术暂停', C['light']), ('艾伦的窗口  0.0—3.0 s', C['light']), ('我的窗口 · 可下令', C['muted'])], hero_hp=65)
    queue_strip([('move', '① 移动', '0.0—1.0 s', [])], 1)
    normal_dock(1, 1, '20 / 24', 20 / 24)
    timing_card()

# ---------- Intervention layer ----------
X, W = 300, 1104
ROWS = [(764, 70), (838, 34), (876, 34), (914, 34), (952, 34)]
def dock(T0, T1, status, windows, now, headers, grant_button=False):
    f = lambda t: X + W * (t - T0) / (T1 - T0)
    panel(24, 642, 1416, 414)
    txt(46, 677, '时序规划', 25, C['light'], serif=True)
    txt(190, 677, status, 18, C['muted'])
    if grant_button:
        button(452, 653, 300, '申请并发授权 · 费用 1 · 余额 2', 'accent', 34, 16)
    button(770, 653, 120, '回到当前', h=34, z=17)
    button(904, 653, 49, '−', h=34, z=21); txt(1004, 677, '缩放 100%', 17, C['muted'], 'middle')
    button(1057, 653, 49, '＋', h=34, z=20)
    button(1120, 653, 85, '‹  0.1 s', h=34, z=16); button(1217, 653, 85, '0.1 s  ›', h=34, z=16)
    button(1316, 653, 106, '更多 +2', h=34, z=16)
    txt(46, 718, '窗口 / 起点', 16, C['muted'])
    for a, b, label, fill, col, z in windows:
        rect(f(a), 700, f(b) - f(a), 25, fill)
        txt((f(a) + f(b)) / 2, 718, label, z, col, 'middle')
    txt(46, 750, f'当前 {now:.1f} s', 16, C['muted'])
    for t in range(int(T0), int(T1) + 1):
        line(f(t), 756, f(t), 986, '#455d53', .8)
        txt(f(t), 749, f'{t}.0', 15, C['muted'], 'middle')
    for i, (y, h) in enumerate(ROWS):
        rect(36, y, 1378, h, '#2c413e' if i == 0 else ('#213335' if i % 2 else '#243638'), extra='fill-opacity=".72"')
        line(285, y, 285, y + h)
    for (y, _), (name, group, resource, sub) in zip(ROWS, headers):
        primary = y == ROWS[0][0]
        txt(46, y + 22, name, 20 if primary else 18)
        txt(134, y + 22, group, 15, C['muted'])
        if primary:
            txt(46, y + 47, resource, 16, C['muted'])
            txt(46, y + 67, sub, 15, C['gold'])
        else:
            txt(265, y + 22, resource, 15, C['muted'], 'end')
    rect(1424, 765, 4, 221, '#344a43', r=2); rect(1424, 765, 4, 159, '#8a9d8c', r=2)
    return f

def attack_block(f, y, start, hit, end, name, dim=False, clip=None):
    s0 = max(start, clip) if clip is not None else start
    op = 'opacity=".45"' if dim else ''
    if hit > s0:
        rect(f(s0), y, f(hit) - f(s0), 22, '#855c51', C['red'], 2, extra=op)
        txt(f(s0) + 10, y + 17, name if hit - s0 < 1 else name + ' · 前摇', 15, extra=op)
    rect(f(hit), y, f(end) - f(hit), 22, '#4a3d38', C['red'], 1, extra=op)
    txt(f(hit) + 9, y + 17, '后摇', 14, C['red'], extra=op)
    diamond(f(hit), y + 11, 5, C['red'])

def footer(actions_enabled, legend, n, can_confirm):
    line(36, 991, 1424, 991)
    col = C['muted'] if actions_enabled else C['off']
    for i, (sym, name) in enumerate(ACTIONS):
        x = 44 + i * 94
        icon(sym, x, 1007, 27, col); txt(x + 33, 1028, name, 16, col)
    line(424, 1005, 424, 1041)
    edit = C['light'] if actions_enabled else C['off']
    txt(444, 1028, '编辑', 17, edit); txt(512, 1028, '移除', 17, edit); txt(580, 1028, '撤销', 17, edit)
    txt(692, 1017, legend[0], 14, C['muted']); txt(692, 1040, legend[1], 14, C['muted'])
    relic(True)
    button(1240, 1000, 100, f'确认行动 ({n})', 'primary' if can_confirm else 'disabled', 40, 16)
    button(1350, 1000, 68, '▶ 继续', 'quiet', 40, 16)

def react_bar(x, y, w, T0, T1, start, hard_end, end, markers, consumed=None, start_label=None):
    """Reaction occupancy only; ordinary window boundaries do not apply to reactions."""
    f = lambda t: x + w * (t - T0) / (T1 - T0)
    rect(x, y, w, 10, '#3e534d', r=2)
    rect(f(start), y, f(end) - f(start), 10, 'none', C['gold'], 1, extra='stroke-dasharray="4 3"')
    if consumed is not None:
        rect(f(start), y, max(f(consumed) - f(start), 3), 10, C['gold'])
        rect(f(consumed), y, f(hard_end) - f(consumed), 10, C['gold'], extra='fill-opacity=".22"')
        rect(f(hard_end), y, f(end) - f(hard_end), 10, 'url(#softOccupancy)', extra='opacity=".4"')
    else:
        rect(f(start), y, max(f(hard_end) - f(start), 3), 10, C['gold'])
        rect(f(hard_end) + 3, y, f(end) - f(hard_end) - 3, 10, 'url(#softOccupancy)', extra='opacity=".8"')
    txt(f(start), y - 10, start_label or f'起点 {start:g}', 15, C['light'], 'middle')
    for t, label in markers:
        tri(f(t), y + 20, 6, C['red'])
        txt(f(t), y + 44, label, 15, C['red'], 'middle')
    txt(x, y + 44, f'{T0:.1f}', 15, C['muted']); txt(x + w, y + 44, f'{T1:.1f}', 15, C['muted'], 'end')

def intervention_own():
    scene('own', [('战术暂停', C['light']), ('艾伦的窗口  0.0—6.0 s', C['light']), ('我的窗口 · 可下令', C['muted']), ('介入中', C['gold'])])
    f = dock(0, 8, '草稿 1 项 · 反应暂存 1 项 · 尚未提交',
             [(0, 6, '艾伦 · 当前窗口 · 普通动作起点可放区', '#3c5148', C['light'], 16),
              (6, 7, '莉亚（预计）', '#4d5a46', C['muted'], 14),
              (7, 8, '敌方窗口（预计）', '#58433e', C['red'], 13)], 0.0,
             [('艾伦', '主选', 'HP 78/100 · 肾上腺素 3', '候选：格挡 · 起点 4.5 s'),
              ('莉亚', '同伴', '肾上腺素 2', ''), ('旅人', '中立', '肾上腺素 0', ''),
              ('掠袭者', '敌方', '肾上腺素 1', ''), ('重剑兵', '敌方', '肾上腺素 2', '')])
    rect(f(7), 756, f(8) - f(7), 230, 'url(#unknown)', extra='opacity=".75"')
    # Shared plan: the queued movement from the normal layer.
    rect(f(0), 774, f(3.8) - f(0), 26, '#638279', C['sage'], 2, extra='stroke-dasharray="5 3"')
    txt(f(1.8), 793, '① 移动 · 草稿', 17, C['light'], 'middle')
    for t in [.8, 1.5, 2.3, 3.0]:
        line(f(t), 778, f(t), 796, '#aac5b4', 1)
    # Block 1.0 s from 4.5: hard to the source hit (5.4), soft after; consumed early at 4.6.
    rect(f(4.5), 807, f(5.5) - f(4.5), 20, 'none', C['gold'], 1, extra='stroke-dasharray="5 3"')
    rect(f(4.5), 807, f(4.6) - f(4.5), 20, '#bd965e')
    rect(f(4.6), 807, f(5.4) - f(4.6), 20, '#bc9561', '#bc9561', extra='fill-opacity=".18" stroke-dasharray="4 3"')
    rect(f(5.4), 807, f(5.5) - f(5.4), 20, 'url(#softOccupancy)', extra='opacity=".45"')
    txt(f(4.68), 822, '格挡', 14, C['gold'])
    rect(f(1), 844, f(5.8) - f(1), 22, '#4c6d63', C['sage'], 2)
    txt(f(1) + 12, 861, '防守 · 剩余额度 2/3', 16)
    rect(f(5.8) - 45, 850, 10, 10, C['sage']); rect(f(5.8) - 28, 850, 10, 10, C['sage']); rect(f(5.8) - 11, 850, 7, 10, 'none', C['sage'])
    txt(f(.2), 898, '未公开计划', 16, C['muted'])
    attack_block(f, 920, 3.9, 4.6, 5.1, '轻斩')
    attack_block(f, 958, 3.4, 5.4, 5.9, '重击')
    # Contacts: outcome chips sit on the struck unit's own track.
    line(f(4.6), 756, f(4.6), 986, C['gold'], 1.6, extra='stroke-dasharray="4 4"')
    path(f'M{f(4.6):.1f} 931V817', 'none', C['gold'], 2)
    diamond(f(4.6), 817, 6, C['gold'])
    # Chips sit left of their contact lines so none crosses the window-end line.
    chip(f(4.6) - 10 - (tw('先格挡', 15) + 26), 775, '先格挡', C['gold'], 15, 24)
    path(f'M{f(5.4):.1f} 969V790', 'none', C['red'], 1.8)
    diamond(f(5.4), 790, 6, C['red'])
    chip(f(5.4) - 10 - (tw('仍命中 24', 15) + 26), 775, '仍命中 24', C['red'], 15, 24)
    line(f(6), 726, f(6), 987, C['gold'], 1.4)
    line(f(7), 726, f(7), 987, C['blue'], 1.8, extra='stroke-dasharray="6 4"')
    txt(f(6) - 9, 783, '窗口末尾', 14, C['gold'], 'end')
    txt(f(7) + 9, 783, '已知视界', 14, C['blue'])
    txt(f(7) + 9, 807, '之后未知', 14, C['muted'])
    footer(True, ('虚线：草稿 / 预计终止', '实心：硬占用   斜纹：软占用'), 2, True)
    panel(1464, 186, 432, 870)
    txt(1488, 225, '反应机会', 26, C['light'], serif=True); txt(1872, 225, '2 项', 18, C['muted'], 'end')
    for y, name, who, hit, deadline, on in [(244, '轻斩 → 艾伦', '掠袭者', '命中 4.6 s', '输入截止 4.3 s', False),
                                            (330, '重击 → 艾伦', '重剑兵', '命中 5.4 s', '输入截止 5.1 s', True)]:
        rect(1488, y, 384, 76, '#3b4136' if on else '#283b3b', C['gold'] if on else C['line'], 2)
        txt(1504, y + 27, name, 20); txt(1857, y + 26, who, 16, C['muted'], 'end')
        txt(1504, y + 57, hit, 17, C['muted']); txt(1857, y + 57, deadline, 18, C['gold'], 'end')
    line(1488, 426, 1872, 426)
    txt(1488, 458, '回应重击', 24, C['light'], serif=True); txt(1872, 456, '可用肾上腺素 3', 17, C['muted'], 'end')
    button(1488, 478, 182, '格挡  /  消耗 2', 'selected', 48, 20)
    button(1690, 478, 182, '闪避  /  消耗 3', h=48, z=20)
    txt(1488, 558, '持续 1.0 s', 18); txt(1872, 558, '不响应', 17, C['muted'], 'end')
    txt(1488, 592, '可放起点 4.42—5.4 s · 默认 5.4 s', 17, C['muted'])
    txt(1488, 632, '起点', 19)
    button(1553, 608, 44, '−', h=34, z=21); button(1607, 608, 165, '4.5 s', 'selected', 34, 21); button(1782, 608, 44, '＋', h=34, z=21)
    txt(1488, 674, '占用 4.5—5.5 s · 硬段至 5.4 · 软段 5.4—5.5', 16, C['light'])
    react_bar(1488, 712, 384, 4.0, 6.0, 4.5, 5.4, 5.5, [(4.6, '轻斩 4.6'), (5.4, '重击 5.4')], consumed=4.6)
    txt(1488, 796, '△ 将被 4.6 s 的轻斩先消耗', 19, C['gold'])
    txt(1488, 827, '来源重击未被挡住 · 预计受伤 24', 19, C['red'])
    txt(1488, 858, '艾伦生命 78 → 54', 19, C['light'])
    line(1488, 876, 1872, 876)
    txt(1488, 905, '起点调到 4.6 s 之后，可只挡重击', 17, C['muted'])
    button(1488, 920, 200, '恢复默认 5.4 s', 'quiet', 36, 17)
    button(1488, 972, 384, '更新反应草稿', 'primary', 42, 20)
    txt(1680, 1038, '与底部计划统一确认', 15, C['muted'], 'middle')

def intervention_other():
    scene('other', [('战术暂停 · 新威胁', C['light']), ('重剑兵的窗口  6.0—9.0 s', C['light']),
                    ('他人窗口 · 可申请授权', C['gold']), ('介入中', C['gold'])], hero_hp=54)
    f = dock(4, 12, '普通计划只读 · 反应可编辑',
             [(4, 6, '艾伦的窗口（已结束）', '#33413c', C['off'], 14),
              (6, 9, '重剑兵 · 当前窗口 · 艾伦放置普通动作起点需授权', '#58433e', C['light'], 15),
              (9, 12, '莉亚的窗口（预计）', '#4d5a46', C['muted'], 14)], 6.2,
             [('艾伦', '主选', 'HP 54/100 · 肾上腺素 2', '候选：格挡 · 默认命中时刻'),
              ('莉亚', '同伴', '肾上腺素 2', ''), ('旅人', '中立', '肾上腺素 0', ''),
              ('掠袭者', '敌方', '肾上腺素 1', ''), ('重剑兵', '敌方', '肾上腺素 2', '')], grant_button=True)
    # Completed history before the current tick.
    attack_block(f, 920, 3.9, 4.6, 5.1, '轻斩', dim=True, clip=4)
    attack_block(f, 958, 3.4, 5.4, 5.9, '重击', dim=True, clip=4)
    rect(f(4.5), 774, f(4.6) - f(4.5), 26, '#bd965e', extra='opacity=".45"')
    txt(f(4.7), 793, '格挡 · 4.6 s 已生效', 14, C['off'])
    rect(f(4), 756, f(6.2) - f(4), 230, '#101b1d', extra='fill-opacity=".35"')
    # Hero: ordinary starts in this window need a grant; reactions do not.
    rect(f(6.2), 774, f(9) - f(6.2), 26, 'url(#needAuth)', '#7d8c85', 1, extra='stroke-dasharray="4 3"')
    txt((f(6.2) + f(9)) / 2 + 60, 793, '普通动作起点需授权', 15, C['muted'], 'middle')
    rect(f(7.2), 807, f(8.2) - f(7.2), 20, 'none', C['gold'], 1, extra='stroke-dasharray="5 3"')
    rect(f(7.2), 807, 3, 20, C['gold'])
    rect(f(7.2) + 3, 807, f(8.2) - f(7.2) - 3, 20, 'url(#softOccupancy)', extra='opacity=".7"')
    chip(f(8.2) + 8, 805, '预计挡住', C['sage'], 14, 24)
    txt(f(6.4), 861, '无计划', 16, C['muted'])
    txt(f(6.4), 898, '未公开计划', 16, C['muted'])
    rect(f(6.2), 914, f(12) - f(6.2), 34, 'url(#unknown)', extra='opacity=".8"')
    txt(f(6.4), 936, '本窗口内尚未启动的计划不公开', 15, C['muted'])
    attack_block(f, 958, 6.2, 7.2, 7.7, '横扫')
    rect(f(7.7), 952, f(12) - f(7.7), 34, 'url(#unknown)', extra='opacity=".8"')
    txt(f(7.9), 974, '可能追加（未公开）', 15, C['muted'])
    line(f(6.2), 726, f(6.2), 987, C['light'], 1.6)
    path(f'M{f(7.2):.1f} 969V817', 'none', C['gold'], 2)
    diamond(f(7.2), 817, 6, C['gold'])
    line(f(9), 726, f(9), 987, C['gold'], 1.4)
    txt(f(9) + 9, 845, '窗口末尾', 14, C['gold'])
    footer(False, ('灰色：普通动作与编辑需授权', '灰斜线：需授权   暗斜纹：未公开'), 0, False)
    panel(1464, 186, 432, 870)
    txt(1488, 225, '反应机会', 26, C['light'], serif=True); txt(1872, 225, '1 项', 18, C['muted'], 'end')
    rect(1488, 244, 384, 76, '#3b4136', C['gold'], 2)
    txt(1504, 271, '横扫 → 艾伦', 20); txt(1857, 270, '重剑兵', 16, C['muted'], 'end')
    txt(1504, 301, '命中 7.2 s · 预计 18', 17, C['muted']); txt(1857, 301, '输入截止 6.9 s', 18, C['gold'], 'end')
    line(1488, 340, 1872, 340)
    txt(1488, 372, '回应横扫', 24, C['light'], serif=True); txt(1872, 370, '可用肾上腺素 2', 17, C['muted'], 'end')
    button(1488, 392, 182, '格挡  /  消耗 2', 'selected', 48, 20)
    button(1690, 392, 182, '闪避  /  消耗 3', 'disabled', 48, 20)
    txt(1488, 470, '闪避不可用：肾上腺素不足（需 3，余 2）', 16, C['red'])
    txt(1488, 504, '持续 1.0 s', 18); txt(1872, 504, '不响应', 17, C['muted'], 'end')
    txt(1488, 536, '可放起点 6.22—7.2 s · 默认 7.2 s', 17, C['muted'])
    txt(1488, 576, '起点', 19)
    button(1553, 552, 44, '−', h=34, z=21); button(1607, 552, 165, '7.2 s', 'selected', 34, 21); button(1782, 552, 44, '＋', h=34, z=21)
    txt(1488, 618, '占用 7.2—8.2 s · 硬段仅命中 Tick', 16, C['light'])
    react_bar(1488, 656, 384, 6.0, 8.5, 7.2, 7.2, 8.2, [(7.2, '横扫 7.2')], start_label='起点 = 命中时刻')
    txt(1488, 740, '✓ 预计挡住横扫 · 艾伦不受伤', 19, C['sage'])
    txt(1488, 770, '反应不受窗口限制，无需授权', 16, C['muted'])
    line(1488, 790, 1872, 790)
    txt(1488, 822, '并发授权', 22, C['light'], serif=True); txt(1872, 820, '未申请', 17, C['muted'], 'end')
    txt(1488, 852, '授权后可在 6.2—9.0 s 内放置普通动作起点', 16, C['muted'])
    txt(1488, 878, '关闭介入不撤销授权，也不退款', 16, C['muted'])
    txt(1488, 904, '拒绝、余额不足或窗口过期时在此说明原因', 16, C['muted'])
    button(1488, 966, 384, '加入反应草稿', 'primary', 42, 20)
    txt(1680, 1034, '与底部计划统一确认', 15, C['muted'], 'middle')

EXTRA_DEFS = ('<linearGradient id="surface" x2="0" y2="1"><stop stop-color="#243638"/><stop offset="1" stop-color="#17272a"/></linearGradient>'
              '<linearGradient id="headerFade" x2="0" y2="1"><stop stop-color="#152629" stop-opacity=".9"/><stop offset="1" stop-color="#152629" stop-opacity="0"/></linearGradient>'
              '<pattern id="softOccupancy" width="8" height="8" patternUnits="userSpaceOnUse"><rect width="8" height="8" fill="#6d795f"/><path d="M-2 8L8-2M6 10L10 6" stroke="#d1c295" stroke-width="2"/></pattern>'
              '<pattern id="unknown" width="12" height="12" patternUnits="userSpaceOnUse"><rect width="12" height="12" fill="#213034"/><path d="M0 12L12 0" stroke="#829c94" stroke-opacity=".18"/></pattern>'
              '<pattern id="needAuth" width="10" height="10" patternUnits="userSpaceOnUse"><rect width="10" height="10" fill="#2a3532"/><path d="M-2 10L10-2M8 12L12 8" stroke="#7d8c85" stroke-opacity=".45" stroke-width="2"/></pattern>')
STATES = [
    ('1-normal-plan', '常规 · 选择动作', '常规层选择普通攻击：只读位置条含公开受击标记与已知视界，固定提示槽同时显示越窗与届时无法反应；反应机会提示常驻；底部有确认与继续。', normal_plan),
    ('2-normal-reaction', '常规 · 反应卡', '常规层反应卡：格挡默认放在命中时刻；与队列中的攻击冲突时说明原因，并提供移除冲突项后格挡。', normal_reaction),
    ('3-intervention-own', '介入 · 我的窗口', '自身窗口打开介入：多单位轨道，格挡 1.0 s、起点 4.5 s 被轻斩先消耗；结果标签位于艾伦轨道；反应详情不显示越窗。', intervention_own),
    ('4-intervention-other', '介入 · 他人窗口', '他人窗口打开介入：普通计划只读，需申请并发授权；反应照常可编辑，闪避因肾上腺素不足不可用。', intervention_other),
    ('5-normal-timing', '常规 · 相对时机（试行）', '常规层选择快斩的时机：尽早、拼刀、抓后摇三项各自给出预计结果；选中拼刀，位置条显示等待段与同 Tick 接触。', normal_timing),
]
def build(target, title, desc, fn, extra_defs=''):
    """Render one state into target; other studies import this module and reuse it."""
    global s
    s = []
    add(f'<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080" role="img" aria-labelledby="title desc">'
        f'<title id="title">Project Hero HUD V4 / {title}</title><desc id="desc">设计示意，数值为布局样例，非游戏运行截图。{desc}</desc>'
        f'<defs>{DEFS}{EXTRA_DEFS}{extra_defs}</defs>')
    fn()
    add('</svg>')
    target.write_text('\n'.join(s), encoding='utf-8')
    print(target.name)

if __name__ == '__main__':
    for stem, title, desc, fn in STATES:
        build(OUT / f'hero-hud-v4-{stem}.svg', title, desc, fn)
