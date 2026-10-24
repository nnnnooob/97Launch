#!/usr/bin/env python3
"""Convert Lucide SVG icons into WinUI PathIcon geometry for Icons.xaml.

Lucide icons are 24x24 stroke-based icons. Their <path d="..."> content uses
standard SVG path syntax, which is a superset of the mini-language accepted by
Windows.UI.Xaml.Media.Geometry (FillRule / StreamGeometry).

We extract the `d` attributes and normalise the formatting so the generated
XAML stays readable.
"""

import re
import sys
from pathlib import Path

ICON_DIR = Path(__file__).resolve().parent.parent / "src" / "Launcher.App" / "Assets" / "Icons"
OUT_FILE = ICON_DIR.parent.parent / "Themes" / "Icons.xaml"

# WinUI PathIcon geometry strings get long. Wrap them so the XAML stays diffable.
MAX_LINE = 96


def read_svg(name: str) -> str:
    p = ICON_DIR / f"{name}.svg"
    if not p.exists():
        raise FileNotFoundError(f"missing icon: {p}")
    return p.read_text(encoding="utf-8")


def extract_paths(svg: str) -> list[str]:
    """Pull every d="..." attribute out of <path> elements, plus circle/rect/line."""
    found: list[str] = []

    # Lucide emits a first <path stroke="none" d="M0 0h24v24H0z"> which is just
    # a fill-prelude for the stroke colour. It is meaningless for PathIcon, skip it.
    for m in re.finditer(r"<path\b([^>]*)>", svg):
        attrs = m.group(1)
        if 'stroke="none"' in attrs:
            continue
        dm = re.search(r'\bd="([^"]*)"', attrs)
        if dm:
            d = dm.group(1).strip()
            if d:
                found.append(d)

    for tag in ("polyline", "polygon"):
        for m in re.finditer(rf"<{tag}\b([^>]*)/?>", svg):
            attrs = m.group(1)
            pm = re.search(r'\bpoints="([^"]*)"', attrs)
            if pm:
                nums = pm.group(1).replace(",", " ").split()
                if len(nums) >= 2:
                    d = "M " + " ".join(nums)
                    if tag == "polygon":
                        d += " Z"
                    found.append(d)

    for m in re.finditer(r"<circle\b([^>]*)/?>", svg):
        attrs = m.group(1)
        cx = re.search(r'\bcx="([^"]*)"', attrs)
        cy = re.search(r'\bcy="([^"]*)"', attrs)
        r = re.search(r'\br="([^"]*)"', attrs)
        if cx and cy and r:
            cxv, cyv, rv = (float(cx.group(1)), float(cy.group(1)), float(r.group(1)))
            found.append(
                f"M {cxv - rv:.4g} {cyv:.4g} a {rv:.4g} {rv:.4g} 0 1 0 {rv*2:.4g} 0 "
                f"a {rv:.4g} {rv:.4g} 0 1 0 -{rv*2:.4g} 0"
            )

    for m in re.finditer(r"<rect\b([^>]*)/?>", svg):
        attrs = m.group(1)
        def _f(n):
            g = re.search(rf'\b{n}="([^"]*)"', attrs)
            return float(g.group(1)) if g else 0.0
        x, y, w, h = _f("x"), _f("y"), _f("width"), _f("height")
        if w > 0 and h > 0:
            found.append(f"M {x:.4g} {y:.4g} h {w:.4g} v {h:.4g} h -{w:.4g} Z")

    for m in re.finditer(r"<line\b([^>]*)/?>", svg):
        attrs = m.group(1)
        def _l(n):
            g = re.search(rf'\b{n}="([^"]*)"', attrs)
            return float(g.group(1)) if g else 0.0
        x1, y1, x2, y2 = _l("x1"), _l("y1"), _l("x2"), _l("y2")
        found.append(f"M {x1:.4g} {y1:.4g} L {x2:.4g} {y2:.4g}")

    return found


def normalise(d: str) -> str:
    d = re.sub(r"\s+", " ", d).strip()
    # Insert a break after each command letter run boundary for wrap-ability
    return d


def wrap(d: str, limit: int = MAX_LINE) -> list[str]:
    """Split a path string on spaces, never breaking a single token."""
    tokens = d.split(" ")
    lines: list[str] = []
    cur = ""
    for t in tokens:
        if not t:
            continue
        cand = f"{cur} {t}" if cur else t
        if len(cand) > limit and cur:
            lines.append(cur)
            cur = t
        else:
            cur = cand
    if cur:
        lines.append(cur)
    return lines or [""]


def build_entry(name: str, key: str, paths: list[str]) -> str:
    """Emit an x:String holding the path data.

    Geometry cannot be declared as a resource dictionary entry at all:

    * <Geometry x:Key="...">data</Geometry> compiles, but the element is
      abstract, so element content never reaches the Geometry TypeConverter and
      the app dies at runtime with
      "Failed to assign to property 'PathIcon.Data'".
    * <PathGeometry x:Key="..." Figures="data" /> kills MarkupCompilePass1,
      which exits 1 with no diagnostic.

    So the dictionary stores the plain path string. Consumers hand it to
    PathIcon.Data, whose Geometry TypeConverter parses it at load time, which is
    the one place the conversion is known to work.
    """
    combined: list[str] = []
    for d in paths:
        if combined and not d.lstrip().upper().startswith("M"):
            # Each figure has to begin with a move command.
            d = "M " + d
        combined.append(normalise(d))

    figures = " ".join(combined)
    # Escape for XML attribute content.
    figures = figures.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;")
    return f'    <x:String x:Key="{key}">{figures}</x:String>'


HEADER = '''<!--
  Auto-generated by tools/gen_icons.py from Lucide 0.469.0 (ISC License).

  Each icon is a shareable PathGeometry. Consumers wrap it in their own
  PathIcon, which is why the stroke attributes (thickness, cap, join) are set at
  the use site rather than here: a Geometry carries shape only, and one instance
  is safely referenced by many elements.
-->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
      <SolidColorBrush x:Key="IconForegroundBrush" Color="#1A1A1A" />
      <SolidColorBrush x:Key="IconForegroundMutedBrush" Color="#6B6B6B" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
      <SolidColorBrush x:Key="IconForegroundBrush" Color="#FFFFFF" />
      <SolidColorBrush x:Key="IconForegroundMutedBrush" Color="#9E9E9E" />
    </ResourceDictionary>
    <ResourceDictionary x:Key="HighContrast">
      <SolidColorBrush x:Key="IconForegroundBrush" Color="{ThemeResource SystemColorWindowTextColor}" />
      <SolidColorBrush x:Key="IconForegroundMutedBrush" Color="{ThemeResource SystemColorGrayTextColor}" />
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>

'''

FOOTER = "\n</ResourceDictionary>\n"


def main() -> int:
    specs = [
        ("rocket", "IconRocket"),
        ("zap", "IconLightning"),
        ("box", "IconBox"),
        ("download", "IconDownload"),
        ("puzzle", "IconPuzzle"),
        ("settings", "IconSettings"),
        ("user-round", "IconUser"),
        ("user-round-plus", "IconUserPlus"),
        ("folder-open", "IconFolder"),
        ("refresh", "IconRefresh"),
        ("plus", "IconPlus"),
        ("log-out", "IconLogout"),
        ("shield-check", "IconShield"),
    ]

    chunks = [HEADER]
    for name, key in specs:
        svg = read_svg(name)
        paths = extract_paths(svg)
        if not paths:
            print(f"WARN: no paths extracted for {name}", file=sys.stderr)
            continue
        chunks.append(build_entry(name, key, paths) + "\n")

    chunks.append(FOOTER)
    OUT_FILE.parent.mkdir(parents=True, exist_ok=True)
    OUT_FILE.write_text("".join(chunks), encoding="utf-8")

    total_paths = sum(len(extract_paths(read_svg(n))) for n, _ in specs)
    print(f"wrote {OUT_FILE} ({len(specs)} icons, {total_paths} paths)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
