#!/usr/bin/env python3
"""Generate the Grably brand assets from one geometry source.

Every SVG, PNG and ICO in assets/brand/ comes from this script, so a change to the mark is made here once and
re-run. Rasterising uses a headless Chromium browser (Microsoft Edge ships with Windows 11) and Pillow assembles
the .ico files.

Usage:
    python tools/brand/build_brand.py                       # writes assets/brand/
    python tools/brand/build_brand.py --preview sheet.png   # also writes a taskbar legibility sheet

Requirements: Python 3.10+, Pillow, Microsoft Edge or Google Chrome.
"""
from __future__ import annotations

import argparse
import base64
import math
import os
import re
import shutil
import subprocess
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parents[2]

# --------------------------------------------------------------------------------------------- palette

NAVY = "#243E8A"       # SBL navy: frame and wordmark on light backgrounds, app tile, banner; 9.9:1 on white
ORANGE = "#F38120"     # SBL orange: the lifted piece; 6.2:1 on #202020, 3.7:1 on navy, 2.6:1 on white
RED = "#E5383B"        # recording state only, never decoration
WHITE = "#FFFFFF"
TASKBAR_LIGHT = "#F3F3F3"
TASKBAR_DARK = "#202020"

# --------------------------------------------------------------------------------------------- geometry helpers


def num(v: float) -> str:
    """Format a coordinate with at most two decimals and no trailing zeros."""
    s = f"{v:.2f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def rotate(x: float, y: float, cx: float, cy: float, deg: float) -> tuple[float, float]:
    a = math.radians(deg)
    dx, dy = x - cx, y - cy
    return cx + dx * math.cos(a) - dy * math.sin(a), cy + dx * math.sin(a) + dy * math.cos(a)


def rounded_rect(x: float, y: float, w: float, h: float, r: float, deg: float = 0.0) -> str:
    """Rounded rectangle as a path, optionally rotated about its centre (rotation is baked into the points)."""
    cx, cy = x + w / 2, y + h / 2
    pts = [(x + r, y), (x + w - r, y), (x + w, y + r), (x + w, y + h - r),
           (x + w - r, y + h), (x + r, y + h), (x, y + h - r), (x, y + r)]
    p = [rotate(px, py, cx, cy, deg) for px, py in pts]
    pt = [f"{num(a)} {num(b)}" for a, b in p]
    arc = f"A{num(r)} {num(r)} 0 0 1"
    return (f"M{pt[0]}L{pt[1]}{arc} {pt[2]}L{pt[3]}{arc} {pt[4]}"
            f"L{pt[5]}{arc} {pt[6]}L{pt[7]}{arc} {pt[0]}Z")


def circle(cx: float, cy: float, r: float) -> str:
    return (f"M{num(cx - r)} {num(cy)}A{num(r)} {num(r)} 0 1 0 {num(cx + r)} {num(cy)}"
            f"A{num(r)} {num(r)} 0 1 0 {num(cx - r)} {num(cy)}Z")


_TOKEN = re.compile(r"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)")
_ARITY = {"M": 2, "L": 2, "H": 1, "V": 1, "A": 7, "Z": 0}


def transform(d: str, s: float = 1.0, dx: float = 0.0, dy: float = 0.0) -> str:
    """Scale then translate an absolute-command path (M L H V A Z), baking the result into the coordinates.

    Only those absolute commands are supported; anything else raises instead of producing a silently wrong shape.
    """
    tokens = _TOKEN.findall(d)
    out: list[str] = []
    i, cmd = 0, ""
    while i < len(tokens):
        if tokens[i].isalpha():
            if tokens[i] not in _ARITY:
                raise ValueError(f"unsupported path command {tokens[i]!r}; use absolute M L H V A Z only")
            cmd = tokens[i]
            out.append(cmd)
            i += 1
            if cmd == "Z":
                continue
        elif cmd in ("", "Z"):
            raise ValueError(f"path number {tokens[i]!r} has no command before it")
        v = [float(t) for t in tokens[i:i + _ARITY[cmd]]]
        if len(v) != _ARITY[cmd]:
            raise ValueError(f"path command {cmd!r} is missing arguments")
        i += _ARITY[cmd]
        if cmd in "ML":
            group = f"{num(v[0] * s + dx)} {num(v[1] * s + dy)}"
        elif cmd == "H":
            group = num(v[0] * s + dx)
        elif cmd == "V":
            group = num(v[0] * s + dy)
        else:  # A rx ry rot large sweep x y
            group = (f"{num(v[0] * s)} {num(v[1] * s)} {num(v[2])} {int(v[3])} {int(v[4])} "
                     f"{num(v[5] * s + dx)} {num(v[6] * s + dy)}")
        # An implicitly repeated command (e.g. "L1 2 3 4") needs a separator between its argument groups.
        out.append(group if out and out[-1].isalpha() else " " + group)
    return "".join(out).replace("Z", "Z ").strip()


def path(d: str, fill: str, evenodd: bool = False) -> str:
    rule = ' fill-rule="evenodd"' if evenodd else ""
    return f'<path fill="{fill}"{rule} d="{d}"/>'


def svg(w: float, h: float, body: str, title: str | None = None, x: float = 0, y: float = 0) -> str:
    head = f'<title id="title">{title}</title>' if title else ""
    role = ' role="img" aria-labelledby="title"' if title else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{num(x)} {num(y)} {num(w)} {num(h)}" '
            f'width="{num(w)}" height="{num(h)}"{role}>{head}{body}</svg>\n')


# --------------------------------------------------------------------------------------------- the mark

# Master (24 px and up), 256 canvas. A rounded screen frame (outer 20..188, stroke 32, r40 / r8) whose bottom-right
# corner is cut away at 108; the cut-out piece (96 square, r26) sits tilted -15 degrees, lifted out of the corner.
FRAME = ("M188 108V60A40 40 0 0 0 148 20H60A40 40 0 0 0 20 60V148A40 40 0 0 0 60 188H108V156H60"
         "A8 8 0 0 1 52 148V60A8 8 0 0 1 60 52H148A8 8 0 0 1 156 60V108Z")
PIECE = rounded_rect(134, 134, 96, 96, 26, -15)
DOT = circle(182, 182, 54)

# Small cut (16 and 20 px), fitted to a 16-unit grid so 16 px renders on whole pixels: 2 px frame, 2 px gaps.
# The piece is not tilted here because a 6 px rotated square only turns to blur.
FRAME_SMALL = ("M176 112V64A48 48 0 0 0 128 16H64A48 48 0 0 0 16 64V128A48 48 0 0 0 64 176H112V144H64"
               "A16 16 0 0 1 48 128V64A16 16 0 0 1 64 48H128A16 16 0 0 1 144 64V112Z")
PIECE_SMALL = rounded_rect(144, 144, 96, 96, 24)
DOT_SMALL = circle(192, 192, 48)

# App icon small cut (16 and 20 px): the mark drawn on the 16-unit grid inside a full-bleed tile, 2 px frame,
# 2 px gap, 4 px piece, so it stays crisp in title bars and Explorer lists.
FRAME_APP_SMALL = ("M160 112V80A32 32 0 0 0 128 48H80A32 32 0 0 0 48 80V128A32 32 0 0 0 80 160H112V128H88"
                   "A8 8 0 0 1 80 120V88A8 8 0 0 1 88 80H120A8 8 0 0 1 128 88V112Z")
PIECE_APP_SMALL = rounded_rect(144, 144, 64, 64, 16)


def mark_parts(small: bool = False, recording: bool = False) -> tuple[str, str]:
    frame = FRAME_SMALL if small else FRAME
    if recording:
        return frame, DOT_SMALL if small else DOT
    return frame, PIECE_SMALL if small else PIECE


def symbol_body(frame_fill: str, piece_fill: str, small: bool = False, recording: bool = False,
                s: float = 1.0, dx: float = 0.0, dy: float = 0.0) -> str:
    frame, piece = mark_parts(small, recording)
    return path(transform(frame, s, dx, dy), frame_fill) + path(transform(piece, s, dx, dy), piece_fill)


# --------------------------------------------------------------------------------------------- the wordmark

@dataclass(frozen=True)
class WordmarkSpec:
    """Parameters of the custom "grably" wordmark. Baseline is y=0; each letter starts at x=0."""
    stem: float = 24       # stroke weight
    xh: float = 120        # x-height
    asc: float = 176       # ascender height (b, l)
    desc: float = 56       # descender depth (g, y hook)
    bowl: float = 112      # bowl width (g, a, b, y)
    radius: float = 56     # outer corner radius of bowls; inner radius is radius - stem
    hook: float = 48       # outer radius of the g / y hook
    hook_end: float = 10   # x where the hook's terminal is cut
    arm: float = 72        # width of r
    space: float = 26      # gap between two straight sides
    space_round: float = 22  # gap after a round side
    space_r: float = 12    # gap after r's open arm


WORD = WordmarkSpec()


def letters(t: WordmarkSpec) -> dict[str, tuple[str, bool]]:
    """Build each letter as one path; True means the path has a counter (needs evenodd)."""
    T, X, A, D, W, R, H, E = t.stem, t.xh, t.asc, t.desc, t.bowl, t.radius, t.hook, t.hook_end
    r, h = R - T, H - T
    n = num
    counter = rounded_rect(T, -X + T, W - 2 * T, X - 2 * T, r)
    hook = (f"V{n(D - H)}A{n(H)} {n(H)} 0 0 1 {n(W - H)} {n(D)}H{n(E)}V{n(D - T)}H{n(W - H)}"
            f"A{n(h)} {n(h)} 0 0 0 {n(W - T)} {n(D - H)}V0")
    left_bowl = f"H{n(R)}A{n(R)} {n(R)} 0 0 1 0 {n(-R)}V{n(-X + R)}A{n(R)} {n(R)} 0 0 1 {n(R)} {n(-X)}Z"
    return {
        "g": (f"M{n(R)} {n(-X)}H{n(W)}{hook}{left_bowl}" + counter, True),
        "r": (f"M0 0V{n(-X + R)}A{n(R)} {n(R)} 0 0 1 {n(R)} {n(-X)}H{n(t.arm)}V{n(-X + T)}H{n(R)}"
              f"A{n(r)} {n(r)} 0 0 0 {n(T)} {n(-X + R)}V0Z", False),
        "a": (f"M{n(R)} {n(-X)}H{n(W)}V0{left_bowl}" + counter, True),
        "b": (f"M0 {n(-A)}H{n(T)}V{n(-X)}H{n(W - R)}A{n(R)} {n(R)} 0 0 1 {n(W)} {n(-X + R)}V{n(-R)}"
              f"A{n(R)} {n(R)} 0 0 1 {n(W - R)} 0H0Z" + counter, True),
        "l": (f"M0 {n(-A)}H{n(T)}V0H0Z", False),
        "y": (f"M0 {n(-X)}H{n(T)}V{n(-R)}A{n(r)} {n(r)} 0 0 0 {n(R)} {n(-T)}H{n(W - T)}V{n(-X)}H{n(W)}{hook}"
              f"H{n(R)}A{n(R)} {n(R)} 0 0 1 0 {n(-R)}Z", False),
    }


def advances(t: WordmarkSpec) -> list[tuple[str, float]]:
    g = 0.0
    r = g + t.bowl + t.space
    a = r + t.arm + t.space_r
    b = a + t.bowl + t.space
    l_ = b + t.bowl + t.space_round
    y = l_ + t.stem + t.space
    return [("g", g), ("r", r), ("a", a), ("b", b), ("l", l_), ("y", y)]


def word_metrics(t: WordmarkSpec) -> tuple[float, float, float]:
    """Width, top and bottom of the wordmark in its own coordinates."""
    return advances(t)[-1][1] + t.bowl, -t.asc, t.desc


WORD_W, WORD_TOP, WORD_BOTTOM = word_metrics(WORD)


def wordmark_body(fill: str, s: float = 1.0, dx: float = 0.0, dy: float = 0.0, t: WordmarkSpec = WORD) -> str:
    shapes = letters(t)
    parts = []
    for ch, ox in advances(t):
        d, evenodd = shapes[ch]
        parts.append(path(transform(d, s, dx + ox * s, dy), fill, evenodd))
    return "".join(parts)


# --------------------------------------------------------------------------------------------- compositions

MARK_BOX = (20, 20, 236, 236)  # visual bounds of the master mark on its 256 canvas
LOCKUP_GAP = 64


def lockup_body(frame_fill: str, piece_fill: str, word_fill: str,
                s: float = 1.0, dx: float = 0.0, dy: float = 0.0) -> tuple[str, float, float]:
    """Horizontal lockup: mark at wordmark height, centred on the x-height. Returns body, width, height at scale s."""
    mark_h = MARK_BOX[3] - MARK_BOX[1]
    x_mid = -60  # middle of the x-height, in wordmark coordinates
    sym_dx, sym_dy = -MARK_BOX[0], x_mid - (MARK_BOX[1] + mark_h / 2)
    top = min(WORD_TOP, sym_dy + MARK_BOX[1])
    bottom = max(WORD_BOTTOM, sym_dy + MARK_BOX[3])
    width = mark_h + LOCKUP_GAP + WORD_W
    body = (symbol_body(frame_fill, piece_fill, s=s, dx=dx + sym_dx * s, dy=dy + (sym_dy - top) * s)
            + wordmark_body(word_fill, s=s, dx=dx + (mark_h + LOCKUP_GAP) * s, dy=dy - top * s))
    return body, width * s, (bottom - top) * s


def lockup(frame_fill: str, piece_fill: str, word_fill: str, title: str | None = None) -> tuple[str, float, float]:
    body, width, height = lockup_body(frame_fill, piece_fill, word_fill)
    return svg(width, height, body, title), width, height


def app_icon(small: bool = False) -> str:
    """App icon: navy tile, white frame, orange piece. Tiles keep the icon visible on light and dark taskbars."""
    if small:
        tile = rounded_rect(0, 0, 256, 256, 48)
        mark = path(FRAME_APP_SMALL, WHITE) + path(PIECE_APP_SMALL, ORANGE)
    else:
        tile = rounded_rect(12, 12, 232, 232, 56)
        s = 0.66
        mark = symbol_body(WHITE, ORANGE, s=s, dx=128 * (1 - s), dy=128 * (1 - s))
    return svg(256, 256, path(tile, NAVY) + mark, "Grably app icon")


def tray(theme: str, recording: bool, small: bool) -> str:
    """Tray glyph: theme-coloured frame, orange piece when idle, red dot when recording."""
    frame_fill = NAVY if theme == "light" else WHITE
    piece_fill = RED if recording else ORANGE
    title = "Grably (recording)" if recording else "Grably"
    return svg(256, 256, symbol_body(frame_fill, piece_fill, small=small, recording=recording), title)


# --------------------------------------------------------------------------------------------- rendering


def find_browser() -> str:
    candidates = [shutil.which("msedge"), shutil.which("chrome"), shutil.which("google-chrome"),
                  r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                  r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                  r"C:\Program Files\Google\Chrome\Application\chrome.exe"]
    for c in candidates:
        if c and Path(c).exists():
            return c
    raise SystemExit("Microsoft Edge or Google Chrome is required to rasterise the SVGs.")


@dataclass
class Job:
    svg: str
    w: int
    h: int
    out: str  # key used to collect the result
    bg: str | None = None


def render_all(jobs: list[Job]) -> dict[str, Image.Image]:
    """Lay every job out on one page, take one headless screenshot, then crop each job back out."""
    pad, x, y, row_h, page_w = 8, 8, 8, 0, 2400
    placed = []
    for j in jobs:
        if x + j.w + pad > page_w:
            x, y, row_h = pad, y + row_h + pad, 0
        placed.append((j, x, y))
        x += j.w + pad
        row_h = max(row_h, j.h)
    page_h = y + row_h + pad
    imgs = []
    for j, px, py in placed:
        data = base64.b64encode(j.svg.encode("utf-8")).decode("ascii")
        bg = f"background:{j.bg};" if j.bg else ""
        imgs.append(f'<img style="position:absolute;left:{px}px;top:{py}px;width:{j.w}px;height:{j.h}px;{bg}" '
                    f'src="data:image/svg+xml;base64,{data}">')
    html = ("<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent;"
            f"width:{page_w}px;height:{page_h}px;overflow:hidden}}img{{display:block}}</style></head>"
            f"<body>{''.join(imgs)}</body></html>")
    tmp = Path(tempfile.mkdtemp(prefix="grably-brand-"))
    try:
        page = tmp / "page.html"
        page.write_text(html, encoding="utf-8")
        shot = tmp / "shot.png"
        cmd = [find_browser(), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
               "--no-default-browser-check", "--disable-extensions", "--force-device-scale-factor=1",
               "--default-background-color=00000000", f"--window-size={page_w},{page_h}",
               f"--screenshot={shot}", f"--user-data-dir={tmp / 'profile'}", page.as_uri()]
        proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline, last = time.time() + 60, -1
        while time.time() < deadline:
            if shot.exists():
                size = shot.stat().st_size
                if size > 0 and size == last:
                    break
                last = size
            time.sleep(0.3)
        if proc.poll() is None:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            else:
                proc.kill()
        if not shot.exists():
            raise SystemExit("The browser produced no screenshot.")
        sheet = Image.open(shot).convert("RGBA")
        sheet.load()
        return {j.out: sheet.crop((px, py, px + j.w, py + j.h)) for j, px, py in placed}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def save_ico(path_: Path, frames: dict[int, Image.Image]) -> None:
    """Write an .ico that holds exactly the given pre-rendered size frames (no resampling)."""
    sizes = sorted(frames)
    largest = frames[sizes[-1]]
    others = [frames[s] for s in sizes[:-1]]
    largest.save(path_, format="ICO", sizes=[(s, s) for s in sizes], append_images=others)


# --------------------------------------------------------------------------------------------- build

ICO_APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
ICO_TRAY_SIZES = [16, 20, 24, 32, 40, 48]
SMALL_MAX = 20  # sizes up to this use the small cut


def build(out: Path) -> dict[str, Image.Image]:
    out.mkdir(parents=True, exist_ok=True)
    (out / "tray").mkdir(exist_ok=True)
    files: dict[str, str] = {}

    files["grably-symbol.svg"] = svg(256, 256, symbol_body(NAVY, ORANGE), "Grably")
    files["grably-symbol-black.svg"] = svg(256, 256, symbol_body("#000000", "#000000"), "Grably")
    files["grably-symbol-white.svg"] = svg(256, 256, symbol_body(WHITE, WHITE), "Grably")
    files["grably-symbol-on-dark.svg"] = svg(256, 256, symbol_body(WHITE, ORANGE), "Grably")
    files["grably-symbol-small.svg"] = svg(256, 256, symbol_body(NAVY, ORANGE, small=True), "Grably")
    files["grably-symbol-recording.svg"] = svg(256, 256, symbol_body(NAVY, RED, recording=True), "Grably recording")
    files["grably-wordmark.svg"] = svg(WORD_W, WORD_BOTTOM - WORD_TOP,
                                       wordmark_body(NAVY, dy=-WORD_TOP), "Grably")
    lockups = {"": (NAVY, ORANGE, NAVY), "-black": ("#000000", "#000000", "#000000"),
               "-white": (WHITE, WHITE, WHITE), "-on-dark": (WHITE, ORANGE, WHITE)}
    lockup_size = (0.0, 0.0)
    for suffix, (f, p, w) in lockups.items():
        text, lw, lh = lockup(f, p, w, "Grably")
        files[f"grably-lockup{suffix}.svg"] = text
        lockup_size = (lw, lh)
    files["grably-app-icon.svg"] = app_icon()
    files["grably-app-icon-small.svg"] = app_icon(small=True)
    for theme in ("light", "dark"):
        for rec in (False, True):
            state = "recording" if rec else "idle"
            files[f"tray/tray-{state}-{theme}.svg"] = tray(theme, rec, small=False)
            files[f"tray/tray-{state}-{theme}-small.svg"] = tray(theme, rec, small=True)
    for name, text in files.items():
        (out / name).write_text(text, encoding="utf-8")

    # Banner for the README: on-dark lockup over SBL navy, tagline underneath.
    lw, lh = lockup_size
    bw, bh = 1280, 400
    scale = 132 / lh
    ly = 108
    lock_body, _, lock_h = lockup_body(WHITE, ORANGE, WHITE, s=scale, dx=(bw - lw * scale) / 2, dy=ly)
    banner = svg(bw, bh, (
        f'<rect width="{bw}" height="{bh}" fill="{NAVY}"/>{lock_body}'
        f'<text x="{bw / 2}" y="{num(ly + lock_h + 64)}" text-anchor="middle" '
        'font-family="Segoe UI Variable Text, Segoe UI, sans-serif" font-size="28" fill="#C9D3F0">'
        "Screenshots, annotations and screen recordings for Windows 11</text>"))

    png_jobs = [Job(files["grably-symbol.svg"], 512, 512, "grably-symbol-512.png"),
                Job(files["grably-symbol-on-dark.svg"], 512, 512, "grably-symbol-on-dark-512.png"),
                Job(files["grably-app-icon.svg"], 512, 512, "grably-app-icon-512.png"),
                Job(files["grably-lockup.svg"], round(lw * 2), round(lh * 2), "grably-lockup.png"),
                Job(files["grably-lockup-on-dark.svg"], round(lw * 2), round(lh * 2), "grably-lockup-on-dark.png"),
                Job(banner, bw, bh, "grably-banner.png")]
    jobs = list(png_jobs)
    for s in ICO_APP_SIZES:
        key = "grably-app-icon-small.svg" if s <= SMALL_MAX else "grably-app-icon.svg"
        jobs.append(Job(files[key], s, s, f"app-{s}"))
    for theme in ("light", "dark"):
        for state in ("idle", "recording"):
            for s in ICO_TRAY_SIZES:
                key = f"tray/tray-{state}-{theme}{'-small' if s <= SMALL_MAX else ''}.svg"
                jobs.append(Job(files[key], s, s, f"tray-{state}-{theme}-{s}"))
    images = render_all(jobs)

    for job in png_jobs:
        images[job.out].save(out / job.out)
    save_ico(out / "grably.ico", {s: images[f"app-{s}"] for s in ICO_APP_SIZES})
    for theme in ("light", "dark"):
        for state in ("idle", "recording"):
            save_ico(out / "tray" / f"tray-{state}-{theme}.ico",
                     {s: images[f"tray-{state}-{theme}-{s}"] for s in ICO_TRAY_SIZES})
    return images


def preview_sheet(images: dict[str, Image.Image], dest: Path, zoom: int = 6) -> None:
    """Real-pixel legibility check: each tray and app icon frame on both taskbar colours, upscaled without smoothing."""
    rows = [("light", TASKBAR_LIGHT), ("dark", TASKBAR_DARK)]
    cols = [(f"tray-{st}", s) for st in ("idle", "recording") for s in (16, 20, 24, 32)] + \
           [("app", s) for s in (16, 24, 32, 48)]
    cell = 48 * zoom // 2 + 16
    sheet = Image.new("RGB", (len(cols) * cell + 16, len(rows) * cell + 16), (255, 255, 255))
    for r, (theme, bg) in enumerate(rows):
        band = Image.new("RGB", (sheet.width, cell), bg)
        sheet.paste(band, (0, 8 + r * cell))
        for c, (kind, s) in enumerate(cols):
            key = f"app-{s}" if kind == "app" else f"{kind}-{theme}-{s}"
            img = images[key]
            z = zoom if s <= 24 else zoom // 2
            big = img.resize((s * z, s * z), Image.NEAREST)
            sheet.paste(big, (8 + c * cell + (cell - big.width) // 2, 8 + r * cell + (cell - big.height) // 2), big)
    sheet.save(dest)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=REPO / "assets" / "brand")
    ap.add_argument("--preview", type=Path, help="also write a taskbar legibility sheet to this PNG")
    args = ap.parse_args()
    images = build(args.out)
    if args.preview:
        preview_sheet(images, args.preview)
    print(f"Brand assets written to {args.out}")


if __name__ == "__main__":
    main()
