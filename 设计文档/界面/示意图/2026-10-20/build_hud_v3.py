"""Deterministic vector HUD studies. Run once to build both 1920 x 1080 states."""
from pathlib import Path
from html import escape
import math
import re

OUT = Path(__file__).resolve().parent
OLD = OUT.parent / '2026-10-19' / 'hero-hud-normal-vector-v2.svg'
# Reuse only the previous study's hand-authored vector environment and icon definitions.
# Everything after its tactical-feedback layer, including all HUD layout, is replaced.
old = OLD.read_text(encoding='utf-8')
DEFS = re.search(r'<defs>(.*?)</defs>', old, re.S).group(1)
scenery = old.split('</defs>', 1)[1].split('<polygon points="676.0,512.0')[0]
# The original environment ends just before the first tactical tile. Use the exact
# first tactical element as a delimiter, falling back to the background's known count.
first_tile = '<polygon points="676.0,512.0 738.0,543.0 676.0,574.0 614.0,543.0"'
if first_tile not in old:
    # Extract the original deterministic environment from the first unit group boundary;
    # remove the tactical overlay using its first ground-highlight polygon.
    marker = '<polygon points="676.0,512.0'
    if marker not in old:
        raise RuntimeError('Expected vector environment delimiter not found')
scenery = old.split('</defs>', 1)[1].split('<polygon points="676.0,512.0', 1)[0]

C = dict(ink='#172528', panel='#1b2b2e', light='#f0e5cc', muted='#b7c2b5', gold='#d9bd82',
         sage='#95b5a6', red='#cf927e', dim='#627a72', line='#53665d', blue='#b1c5c0')
s=[]
def add(v): s.append(v)
def rect(x,y,w,h,fill,stroke='none',r=0,extra=''):
    add(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" fill="{fill}" stroke="{stroke}" {extra}/>')
def line(x,y,xx,yy,c=C['line'],sw=1,extra=''):
    add(f'<line x1="{x}" y1="{y}" x2="{xx}" y2="{yy}" stroke="{c}" stroke-width="{sw}" {extra}/>')
def path(d,fill='none',stroke='none',sw=1,extra=''):
    add(f'<path d="{d}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" {extra}/>')
def txt(x,y,t,z=20,c=C['light'],anchor='start',serif=False,extra=''):
    font='Georgia,SimSun,serif' if serif else 'Microsoft YaHei,Segoe UI,sans-serif'
    add(f'<text x="{x}" y="{y}" font-family="{font}" font-size="{z}" fill="{c}" text-anchor="{anchor}" {extra}>{escape(str(t))}</text>')
def circ(x,y,r,fill,stroke='none',sw=1,extra=''):
    add(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" {extra}/>')
def ell(x,y,rx,ry,fill,stroke='none',sw=1,extra=''):
    add(f'<ellipse cx="{x}" cy="{y}" rx="{rx}" ry="{ry}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}" {extra}/>')
def diamond(x,y,r=5,c=C['gold'],fill=None):
    path(f'M{x} {y-r}L{x+r} {y}L{x} {y+r}L{x-r} {y}Z',c if fill is None else fill,c,1.3)
def icon(name,x,y,size=30,c=C['light']):
    add(f'<use href="#{name}" x="{x}" y="{y}" width="{size}" height="{size}" color="{c}"/>')
def button(x,y,w,label,kind='quiet',h=38,z=18):
    fill,stroke,col = (C['gold'],C['gold'],C['ink']) if kind=='primary' else (('#344a46',C['gold'],C['light']) if kind=='selected' else ('#233639',C['line'],C['muted']))
    rect(x,y,w,h,fill,stroke,2)
    txt(x+w/2,y+h/2+z*.35,label,z,col,'middle')
def panel(x,y,w,h):
    path(f'M{x+12} {y}H{x+w-12}L{x+w} {y+12}V{y+h}H{x}V{y+12}Z','url(#surface)',C['line'],1)
    line(x+12,y,x+w-12,y,C['gold'],1.4)
def portrait(x,y,size=70,color='#66858b'):
    add(f'<g transform="translate({x} {y}) scale({size/70})">')
    path('M0 8L35 -5L70 8V60L35 74L0 60Z','#253e40',C['gold'])
    path('M5 60L10 43L26 37H46L62 45L66 60L35 70Z',color)
    path('M22 15Q34 3 49 16L47 38L36 47L25 38Z','#d8b793')
    path('M20 25L19 13L27 5L40 3L52 13L51 27L43 16L32 21L27 17L24 28Z','#413f35')
    path('M18 43L30 49L38 46L53 40L57 49L37 58L22 53Z',color)
    add('</g>')
def unit(x,y,coat,ghost=False,label=None):
    add(f'<g transform="translate({x} {y})" opacity="{.55 if ghost else 1}">')
    ell(4,4,32,13,'#263c35',extra='opacity=".4"')
    path('M-18 -66L-29 -17L-7 -4L23 -15L17 -68Z',coat)
    path('M-15 -32L-13 -5L-3 0L1 -34M9 -33L11 -5L23 -2L21 -40','#343d38','#263732',1.5)
    path('M-20 -70L4 -79L23 -64L17 -36L-9 -32L-23 -47Z','#8fa49a','#324b47',2)
    path('M-19 -62L14 -68L12 -44L-11 -42Z',coat)
    path('M-21 -62L-32 -44L-27 -23L-19 -24L-19 -46M20 -61L30 -44L20 -27L14 -32L20 -48','url(#steel)','#39504a')
    path('M-15 -86Q-13 -109 5 -105Q24 -100 17 -80L5 -68L-7 -73Z','url(#skin)','#555b49')
    path('M-17 -88L-19 -101L-9 -113L7 -112L20 -102L20 -85L10 -98L0 -93L-8 -98L-7 -84Z','#473f32')
    path('M-17 -73L-2 -66L19 -73L17 -61L0 -57L-20 -64Z',coat)
    path('M18 -31L48 -76L52 -77L49 -68L23 -26Z','#e6dfc4','#58736b')
    line(14,-34,29,-22,'#bbac7a',3)
    add('</g>')
    if label:
        txt(x,y-137,label,19,C['light'],'middle',extra='paint-order="stroke" stroke="#394b42" stroke-width="5" stroke-linejoin="round"')
def mini_time(x,y,w,start,end,boundary=6):
    maximum=max(end,7)
    f=lambda v:x+w*v/maximum
    rect(x,y,w,10,'#3e534d',r=2)
    rect(f(start),y,f(min(end,boundary))-f(start),10,C['gold'])
    if end>boundary: rect(f(boundary),y,f(end)-f(boundary),10,'url(#hatch)')
    line(f(boundary),y-7,f(boundary),y+17,C['light'],1.5)
    txt(x,y+34,'0',15,C['muted'])
    txt(f(start),y+34,f'{start:g}',15,C['light'],'middle')
    txt(f(boundary),y-12,'窗口末尾 6.0',15,C['muted'],'middle')
    txt(f(end),y+34,f'{end:g}',15,C['gold'],'end')
def relic(active):
    x,y=1142,1015
    if active: add('<g transform="translate(1142 1023) scale(.66) translate(-1142 -1015)">')
    path(f'M{x-44} {y-33}L{x} {y-48}L{x+44} {y-33}L{x+52} {y}L{x+44} {y+33}L{x} {y+48}L{x-44} {y+33}L{x-52} {y}Z','#20383a',C['gold'],1)
    circ(x,y,38,'#34504e' if active else '#172d30',C['gold'],2.5)
    circ(x,y,30,'none','#91b2ac',1)
    for a in range(0,360,45):
        a=math.radians(a);line(x+33*math.cos(a),y+33*math.sin(a),x+37*math.cos(a),y+37*math.sin(a),C['gold'],1.5)
    icon('hourglass',x-20,y-22,40,C['light'])
    if active: add('</g>')
    txt(1211,1011,'介入中' if active else '介入',23,C['gold'],serif=True)
    txt(1211,1037,'返回常规' if active else '克罗诺斯',15,C['muted'])

def scene(active):
    rect(0,0,1920,1080,'#788776')
    add(f'<g transform="translate(0 {-160 if active else 0}) scale(1.2)">{scenery}</g>')
    # Reframe the battlefield above the expanded dock; units stay large and readable.
    dy=-170 if active else 0
    ally=(506,480+dy); hero=(718,670+dy); end=(928,615+dy); a=(1010,490+dy); b=(1130,650+dy)
    path(f'M{hero[0]} {hero[1]}L778 {656+dy}L840 {625+dy}L{end[0]} {end[1]}','none','#f1e5c8',3,'stroke-dasharray="4 10" stroke-linecap="round"')
    ell(*hero,42,18,'none','#d3e4cc',2)
    ell(*end,38,16,'none',C['gold'],2,extra='stroke-dasharray="6 4"')
    if active:
        path(f'M1010 {490+dy}L865 {616+dy}Q966 {664+dy}1076 {585+dy}Z','#ba725c','#db9a7d',1,extra='fill-opacity=".15" stroke-dasharray="8 5"')
        path(f'M1130 {650+dy}L902 {567+dy}Q874 {652+dy}977 {701+dy}Z','#ba725c','#dfad8b',1,extra='fill-opacity=".12" stroke-dasharray="8 5"')
    unit(*ally,'#64765d',label='莉亚')
    unit(1260,400+dy,'#8c8671',label='旅人 · 未知' if active else None)
    unit(*hero,'#527d85')
    unit(*end,'#527d85',True)
    unit(*a,'#885c56',label='掠袭者')
    unit(*b,'#7e5c56',label='重剑兵')
    if active:
        unit(985,515+dy,'#885c56',True)
        diamond(928,615+dy,8)
        path(f'M942 {615+dy}L998 {574+dy}H1268','none',C['gold'],1.4)
        rect(1000,539+dy,295,40,C['ink'],C['gold'],2,extra='fill-opacity=".94"')
        txt(1018,566+dy,'◆ 4.6 s  格挡被轻斩先消耗',18,C['gold'])
        txt(644,586,'游标 4.6 s · 显示已公开计划与攻击范围',18,C['light'],extra='paint-order="stroke" stroke="#344a42" stroke-width="5"')
    rect(0,0,1920,145,'url(#headerFade)')
    txt(40,48,'时漏城 / 钟楼街口',24,C['light'],serif=True,extra='letter-spacing="2"')
    txt(40,82,'守住钟楼入口',18,C['muted'])
    rect(664,23,690,48,C['ink'],C['line'],2,extra='fill-opacity=".92"')
    circ(688,47,4,C['gold'])
    txt(705,54,'战术暂停',19)
    line(803,34,803,60)
    txt(826,54,'艾伦的窗口  0.0—6.0 s',19)
    line(1081,34,1081,60)
    txt(1103,54,'已介入 · 可规划' if active else '常规 · 可规划',19,C['gold'] if active else C['muted'])
    txt(1812,47,'帮助',18,C['light'],'end');txt(1880,47,'菜单',18,C['light'],'end')
    txt(1880,79,'HUD V3 · 设计示意',14,C['muted'],'end')
    for i,(name,color) in enumerate([('艾伦','#66858b'),('莉亚','#738369')]):
        portrait(38,160+i*106,56,color)
        rect(39,226+i*106,54,4,'#334b43');rect(39,226+i*106,43,4,C['sage'])
        if i==0: path(f'M30 {169+i*106}V{216+i*106}','none',C['gold'],3)

def normal():
    scene(False)
    # Short queue occupies the exact region reclaimed by the intervention dock.
    panel(388,842,1052,62)
    txt(410,879,'行动队列',18,C['muted'])
    rect(530,854,330,38,'#2c4441',C['line'],2)
    icon('move',542,861,24,C['sage']);txt(578,880,'① 移动',19)
    txt(840,880,'0.0—3.8 s',18,C['muted'],'end')
    txt(900,880,'编辑',17,C['muted']);txt(965,880,'移除',17,C['muted'])
    txt(1417,880,'1 项待确认',18,C['gold'],'end')
    panel(24,923,1416,133)
    portrait(51,949,82)
    txt(166,965,'艾伦',29,C['light'],serif=True)
    txt(166,996,'78 / 100',19,C['light'])
    rect(166,1009,177,5,'#40554e');rect(166,1009,138,5,C['sage'])
    txt(166,1040,'肾上腺素 3',17,C['muted'])
    for i,(sym,name) in enumerate([('move','移动'),('sword','攻击'),('shield','防守'),('potion','物品')]):
        x=405+i*133
        rect(x,946,108,83,'#dfd7bb' if i==1 else '#263b3b',C['gold'] if i==1 else C['line'],2)
        icon(sym,x+37,955,34,C['ink'] if i==1 else C['light'])
        txt(x+54,1014,name,20,C['ink'] if i==1 else C['light'],'middle')
        txt(x+10,963,str(i+1),12,C['ink'] if i==1 else C['muted'])
    line(982,946,982,1037)
    txt(1015,971,'核心能力',16,C['muted'])
    relic(False)
    button(1310,976,108,'确认计划','primary',54,18)
    # Candidate preview is small enough to remain local, not a permanent full-height sidebar.
    panel(1464,610,432,446)
    icon('sword',1488,633,32,C['gold']);txt(1537,660,'普通攻击',28,C['light'],serif=True)
    txt(1488,695,'目标  重剑兵',18,C['muted'])
    txt(1488,760,'24',60,C['light'],serif=True)
    txt(1587,730,'预计伤害',17,C['muted'])
    txt(1587,761,'生命 42 → 18',22)
    txt(1488,800,'结果：命中 · 原地后摇',19,C['muted'])
    line(1488,820,1872,820)
    txt(1488,850,'加入后占用',18,C['muted']);txt(1872,850,'3.8—6.8 s',21,C['light'],'end')
    mini_time(1488,903,384,3.8,6.8)
    txt(1488,976,'△ 超出窗口 0.8 s · 仍可加入',19,C['gold'])
    button(1488,996,384,'加入队列','primary',40,20)

def track_header(y,name,group,resource,primary=False):
    txt(46,y+22,name,20 if primary else 18)
    txt(134,y+22,group,15,C['muted'])
    if primary:
        txt(46,y+47,'HP 78/100 · 肾上腺素 3',16,C['muted'])
        txt(46,y+67,'候选：格挡',15,C['gold'])
    else: txt(265,y+22,resource,15,C['muted'],'end')
def active():
    scene(True)
    # Bottom dock: fixed window/ruler, 1 primary + 4 compact tracks, then command footer.
    panel(24,642,1416,414)
    txt(46,677,'时序规划',25,C['light'],serif=True)
    txt(190,677,'草稿 1 项 · 尚未提交',18,C['muted'])
    button(770,653,120,'回到当前',h=34,z=17)
    button(904,653,49,'−',h=34,z=21);txt(1004,677,'缩放 100%',17,C['muted'],'middle')
    button(1057,653,49,'＋',h=34,z=20)
    button(1120,653,85,'‹  0.1 s',h=34,z=16);button(1217,653,85,'0.1 s  ›',h=34,z=16)
    button(1316,653,106,'更多 +2',h=34,z=16)
    X=300; W=1104; f=lambda t:X+W*t/8
    txt(46,718,'窗口 / 起点',16,C['muted'])
    rect(X,700,f(6)-X,25,'#3c5148');txt((X+f(6))/2,718,'艾伦 · 当前窗口 · 普通动作起点可放区',16,C['light'],'middle')
    rect(f(6),700,f(7)-f(6),25,'#65583f');txt((f(6)+f(7))/2,718,'窗口外',15,C['gold'],'middle')
    rect(f(7),700,f(8)-f(7),25,'#58433e');txt((f(7)+f(8))/2,718,'敌方窗口（预计）',13,C['red'],'middle')
    txt(46,750,'当前 0.0 s',16,C['muted'])
    for t in range(9):
        xx=f(t)
        line(xx,756,xx,986,'#455d53',.8)
        txt(xx,749,f'{t}.0',15,C['muted'],'middle')
    rect(f(7),756,f(8)-f(7),230,'url(#unknown)',extra='opacity=".75"')
    # The body has one expanded row and four compact rows, with stable group ordering.
    rows=[(764,70),(838,34),(876,34),(914,34),(952,34)]
    for i,(y,h) in enumerate(rows):
        rect(36,y,1378,h,'#2c413e' if i==0 else ('#213335' if i%2 else '#243638'),extra='fill-opacity=".72"')
        line(285,y,285,y+h)
    track_header(764,'艾伦','主选','',True)
    track_header(838,'莉亚','同伴','肾上腺素 2')
    track_header(876,'旅人','中立','肾上腺素 0')
    track_header(914,'掠袭者','敌方','肾上腺素 1')
    track_header(952,'重剑兵','敌方','肾上腺素 2')
    # A single pending movement is shared with normal mode. Preview is not queued yet.
    rect(f(0),774,f(3.8)-f(0),26,'#638279',C['sage'],2,extra='stroke-dasharray="5 3"')
    txt(f(1.8),793,'① 移动 · 草稿',17,C['light'],'middle')
    for t in [.8,1.5,2.3,3.0]: line(f(t),778,f(t),796,'#aac5b4',1)
    # Block candidate: nominal hard/soft segments; early consumption makes tail dashed.
    rect(f(4.2),807,f(6.2)-f(4.2),20,'none',C['gold'],1,extra='stroke-dasharray="5 3"')
    rect(f(4.2),807,f(4.6)-f(4.2),20,'#bd965e')
    rect(f(4.6),807,f(5.4)-f(4.6),20,'#bc9561','#bc9561',extra='fill-opacity=".18" stroke-dasharray="4 3"')
    rect(f(5.4),807,f(6.2)-f(5.4),20,'url(#softOccupancy)',extra='opacity=".45"')
    rect(f(6),807,f(6.2)-f(6),20,'url(#hatch)',extra='opacity=".7"')
    # Ally's guard capacity, neutral uncertainty, two public opponent attacks.
    rect(f(1),844,f(5.8)-f(1),22,'#4c6d63',C['sage'],2)
    txt(f(1)+12,861,'防守 · 剩余额度 2/3',16)
    rect(f(5.8)-45,850,10,10,C['sage']);rect(f(5.8)-28,850,10,10,C['sage']);rect(f(5.8)-11,850,7,10,'none',C['sage'])
    txt(f(.2),898,'未公开计划',16,C['muted'])
    for y,start,hit,end,name in [(920,3.9,4.6,5.2,'轻斩'),(958,3.4,5.4,6.4,'重击')]:
        rect(f(start),y,f(hit)-f(start),22,'#855c51',C['red'],2)
        txt(f(start)+10,y+17,name if hit-start<1 else name+' · 前摇',15)
        rect(f(hit),y,f(end)-f(hit),22,'#4a3d38',C['red'],1)
        txt(f(hit)+9,y+17,'后摇',14,C['red'])
        diamond(f(hit),y+11,5,C['red'])
    # Contact links and preview cursor share time, but contacts have explicit outcomes.
    line(f(4.6),756,f(4.6),986,C['gold'],1.6,extra='stroke-dasharray="4 4"')
    path(f'M{f(4.6)} 931V817','none',C['gold'],2)
    diamond(f(4.6),817,6,C['gold'])
    rect(f(4.6)+10,884,82,24,C['ink'],C['gold'],2);txt(f(4.6)+51,902,'先格挡',15,C['gold'],'middle')
    path(f'M{f(5.4)} 969V790','none',C['red'],1.8)
    diamond(f(5.4),790,6,C['red'])
    rect(f(5.4)+9,884,93,24,C['ink'],C['red'],2);txt(f(5.4)+55,902,'仍命中 24',14,C['red'],'middle')
    # Distinguish the active owner's window end from the public knowledge horizon.
    line(f(6),726,f(6),987,C['gold'],1.4)
    line(f(7),726,f(7),987,C['blue'],1.8,extra='stroke-dasharray="6 4"')
    txt(f(6)-9,783,'窗口末尾',14,C['gold'],'end')
    txt(f(7)+9,783,'已知视界',14,C['blue'])
    txt(f(7)+9,807,'之后未知',14,C['muted'])
    rect(1424,765,4,221,'#344a43',r=2);rect(1424,765,4,159,'#8a9d8c',r=2)
    # Reflowed command footer: same relic anchor; no duplicated full queue or big portrait.
    line(36,991,1424,991)
    for i,(sym,name) in enumerate([('move','移动'),('sword','攻击'),('shield','防守'),('potion','物品')]):
        x=44+i*94;icon(sym,x,1007,27,C['muted']);txt(x+33,1028,name,16,C['muted'])
    line(424,1005,424,1041)
    txt(444,1028,'编辑',17);txt(512,1028,'移除',17);txt(580,1028,'撤销',17)
    txt(692,1017,'虚线：草稿 / 预计终止',14,C['muted'])
    txt(692,1040,'实心：硬占用   斜纹：软占用',14,C['muted'])
    relic(True)
    button(1310,1000,108,'确认计划','primary',40,17)
    # A shared right-hand detail region grows upward; compact threats never hide deadlines.
    panel(1464,186,432,870)
    txt(1488,225,'反应机会',26,C['light'],serif=True);txt(1872,225,'2 项',18,C['muted'],'end')
    rect(1488,244,384,76,'#283b3b',C['line'],2)
    txt(1504,271,'轻斩 → 艾伦',20);txt(1857,270,'掠袭者',16,C['muted'],'end')
    txt(1504,301,'命中 4.6 s',17,C['muted']);txt(1857,301,'输入截止 4.3 s',18,C['gold'],'end')
    rect(1488,330,384,76,'#3b4136',C['gold'],2)
    txt(1504,357,'重击 → 艾伦',20);txt(1857,356,'重剑兵',16,C['muted'],'end')
    txt(1504,387,'命中 5.4 s',17,C['muted']);txt(1857,387,'输入截止 5.1 s',18,C['gold'],'end')
    line(1488,426,1872,426)
    txt(1488,458,'回应重击',24,C['light'],serif=True)
    txt(1872,456,'可用肾上腺素 3',17,C['muted'],'end')
    button(1488,478,182,'格挡  /  消耗 2','selected',48,20)
    button(1690,478,182,'闪避  /  消耗 3',h=48,z=20)
    txt(1488,558,'持续 2.0 s',18);txt(1872,558,'不响应',17,C['muted'],'end')
    txt(1488,596,'可放起点 3.8—5.4 s',18,C['muted'])
    txt(1488,632,'起点',19);button(1553,608,44,'−',h=34,z=21);button(1607,608,165,'4.2 s','selected',34,21);button(1782,608,44,'＋',h=34,z=21)
    txt(1488,674,'时间占用 4.2—6.2 s',18)
    mini_time(1488,712,384,4.2,6.2)
    txt(1488,780,'△ 将被 4.6 s 的轻斩先消耗',19,C['gold'])
    txt(1488,811,'来源重击未被挡住 · 预计受伤 24',19,C['red'])
    txt(1488,842,'艾伦生命 78 → 54',19,C['light'])
    line(1488,860,1872,860)
    txt(1488,889,'△ 尾段越窗 0.2 s · 仍可加入',18,C['gold'])
    txt(1488,920,'起点合法 · 可调整到轻斩之后',18,C['muted'])
    txt(1488,951,'下次自身窗口开启时肾上腺素清零',16,C['muted'])
    button(1488,976,384,'加入反应草稿','primary',42,20)
    txt(1680,1040,'候选尚未加入 · 与底部计划统一确认',15,C['muted'],'middle')

for state,fn in [('normal',normal),('intervention',active)]:
    s=[]
    add(f'<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080" role="img" aria-labelledby="title desc"><title id="title">Project Hero HUD V3 / {"介入关闭" if state=="normal" else "介入开启"}</title><desc id="desc">设计示意。数值为布局样例。常规态保留短队列和加入前时间位置预览；介入态展开五条同轴轨道和反应详情。两态共享底部介入入口。非游戏运行截图。</desc><defs>{DEFS}<linearGradient id="surface" x2="0" y2="1"><stop stop-color="#243638"/><stop offset="1" stop-color="#17272a"/></linearGradient><linearGradient id="headerFade" x2="0" y2="1"><stop stop-color="#152629" stop-opacity=".9"/><stop offset="1" stop-color="#152629" stop-opacity="0"/></linearGradient><pattern id="softOccupancy" width="8" height="8" patternUnits="userSpaceOnUse"><rect width="8" height="8" fill="#6d795f"/><path d="M-2 8L8-2M6 10L10 6" stroke="#d1c295" stroke-width="2"/></pattern><pattern id="unknown" width="12" height="12" patternUnits="userSpaceOnUse"><rect width="12" height="12" fill="#213034"/><path d="M0 12L12 0" stroke="#829c94" stroke-opacity=".18"/></pattern></defs>')
    fn()
    add('</svg>')
    target=OUT/f'hero-hud-{state}-vector-v3.svg'
    target.write_text('\n'.join(s),encoding='utf-8')
    print(target)
