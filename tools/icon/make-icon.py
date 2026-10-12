"""Writes modicon.svg: an open quest book with a small quest tree across its pages.
Render to the 480x480 modicon.png with: convert -background none modicon.svg modicon.png"""
import math, sys

S = 480

def gear(cx, cy, r_out, r_in, teeth, hole):
    pts = []
    for i in range(teeth * 4):
        a = 2 * math.pi * i / (teeth * 4) - math.pi / 2
        r = r_out if i % 4 in (1, 2) else r_in
        pts.append(f"{cx + r * math.cos(a):.1f},{cy + r * math.sin(a):.1f}")
    ring = (f"M{cx + hole},{cy} a{hole},{hole} 0 1,0 {-2 * hole},0 a{hole},{hole} 0 1,0 {2 * hole},0 Z")
    return "M" + " L".join(pts) + " Z"

def check(cx, cy, s):
    return f"M{cx - s:.1f},{cy:.1f} L{cx - s * 0.3:.1f},{cy + s * 0.7:.1f} L{cx + s:.1f},{cy - s * 0.65:.1f}"

# The tree: two quests done, one ready, the milestone gear at the end.
A = (138, 250); B = (238, 186); C = (238, 314); G = (352, 246)
node = 30

svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{S}" height="{S}" viewBox="0 0 {S} {S}">
<defs>
  <radialGradient id="bg" cx="50%" cy="40%" r="70%">
    <stop offset="0" stop-color="#4a3626"/><stop offset="1" stop-color="#1e140d"/>
  </radialGradient>
  <linearGradient id="pageL" x1="0" x2="1"><stop offset="0" stop-color="#e8d9b0"/><stop offset="0.85" stop-color="#f3e8c8"/><stop offset="1" stop-color="#cdb98c"/></linearGradient>
  <linearGradient id="pageR" x1="0" x2="1"><stop offset="0" stop-color="#cdb98c"/><stop offset="0.15" stop-color="#f3e8c8"/><stop offset="1" stop-color="#e8d9b0"/></linearGradient>
  <linearGradient id="gold" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f2c75c"/><stop offset="1" stop-color="#c8902c"/></linearGradient>
  <linearGradient id="done" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#7fae5a"/><stop offset="1" stop-color="#4f7d35"/></linearGradient>
</defs>
<rect x="0" y="0" width="{S}" height="{S}" rx="64" fill="#2a1c12"/>
<circle cx="240" cy="210" r="230" fill="#33241a"/>
<circle cx="240" cy="210" r="170" fill="#3b2a1e"/>
<!-- cover -->
<path d="M40,118 Q140,92 240,112 Q340,92 440,118 L440,388 Q340,364 240,384 Q140,364 40,388 Z" fill="#6e3f22" stroke="#3a2010" stroke-width="6"/>
<!-- pages, a little stack under each -->
<path d="M58,112 Q150,86 240,108 L240,370 Q150,350 58,374 Z" fill="#b9a275"/>
<path d="M422,112 Q330,86 240,108 L240,370 Q330,350 422,374 Z" fill="#b9a275"/>
<path d="M64,104 Q152,78 240,100 L240,362 Q152,342 64,366 Z" fill="#f1e5c3" stroke="#8c7550" stroke-width="2"/>
<path d="M416,104 Q328,78 240,100 L240,362 Q328,342 416,366 Z" fill="#f1e5c3" stroke="#8c7550" stroke-width="2"/>
<path d="M222,100 L240,100 L240,362 L222,358 Z" fill="#ddcca0"/>
<path d="M258,100 L240,100 L240,362 L258,358 Z" fill="#ddcca0"/>
<line x1="240" y1="100" x2="240" y2="362" stroke="#8c7550" stroke-width="3"/>
<!-- links -->
<g stroke="#5a3e24" stroke-width="9" stroke-linecap="round" fill="none">
  <path d="M{A[0]},{A[1]} L{B[0]},{B[1]}"/>
  <path d="M{A[0]},{A[1]} L{C[0]},{C[1]}"/>
  <path d="M{B[0]},{B[1]} L{G[0]},{G[1]}"/>
  <path d="M{C[0]},{C[1]} L{G[0]},{G[1]}"/>
</g>
<!-- quests -->
<g stroke="#3a2a18" stroke-width="5">
  <rect x="{A[0]-node}" y="{A[1]-node}" width="{2*node}" height="{2*node}" rx="9" fill="#5f9440"/>
  <rect x="{B[0]-node}" y="{B[1]-node}" width="{2*node}" height="{2*node}" rx="9" fill="#5f9440"/>
  <rect x="{C[0]-node}" y="{C[1]-node}" width="{2*node}" height="{2*node}" rx="9" fill="#efe3c0"/>
</g>
<g stroke="#f6f0dc" stroke-width="9" stroke-linecap="round" stroke-linejoin="round" fill="none">
  <path d="{check(A[0], A[1], 15)}"/>
  <path d="{check(B[0], B[1], 15)}"/>
</g>
<text x="{C[0]}" y="{C[1]+15}" text-anchor="middle" font-family="DejaVu Sans, sans-serif" font-weight="bold" font-size="42" fill="#8a6a3c">!</text>
<path d="{gear(G[0], G[1], 54, 42, 10, 15)}" fill="#e0a83a" fill-rule="evenodd" stroke="#6b4a14" stroke-width="5"/>
<circle cx="{G[0]}" cy="{G[1]}" r="16" fill="#f1e5c3" stroke="#6b4a14" stroke-width="5"/>
</svg>
'''
open(sys.argv[1] if len(sys.argv) > 1 else "modicon.svg", "w").write(svg)
