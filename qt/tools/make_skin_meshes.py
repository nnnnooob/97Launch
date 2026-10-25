"""Bake six texture-mapped Minecraft cuboids with Qt's Balsam asset tool."""
from pathlib import Path
import argparse
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("balsam", type=Path)
args = parser.parse_args()
source_dir = root / "build-basic" / "skin-source"
output_dir = root / "assets" / "skin"
source_dir.mkdir(parents=True, exist_ok=True)
output_dir.mkdir(parents=True, exist_ok=True)

# UV rectangles are in pixels on the existing 64 x 64 Steve texture.
parts = {
    "head": ((8, 8, 8), [(8,8,8,8),(24,8,8,8),(0,8,8,8),(16,8,8,8),(8,0,8,8),(16,0,8,8)]),
    "torso": ((8,12,4), [(20,20,8,12),(32,20,8,12),(16,20,4,12),(28,20,4,12),(20,16,8,4),(28,16,8,4)]),
    "right_arm": ((4,12,4), [(44,20,4,12),(52,20,4,12),(40,20,4,12),(48,20,4,12),(44,16,4,4),(48,16,4,4)]),
    "left_arm": ((4,12,4), [(36,52,4,12),(44,52,4,12),(32,52,4,12),(40,52,4,12),(36,48,4,4),(40,48,4,4)]),
    "right_leg": ((4,12,4), [(4,20,4,12),(12,20,4,12),(0,20,4,12),(8,20,4,12),(4,16,4,4),(8,16,4,4)]),
    "left_leg": ((4,12,4), [(20,52,4,12),(28,52,4,12),(16,52,4,12),(24,52,4,12),(20,48,4,4),(24,48,4,4)]),
}

for name, (size, rectangles) in parts.items():
    x, y, z = (v / 2 for v in size)
    faces = [
        [(-x,-y,z),(x,-y,z),(x,y,z),(-x,y,z)],
        [(x,-y,-z),(-x,-y,-z),(-x,y,-z),(x,y,-z)],
        [(-x,-y,-z),(-x,-y,z),(-x,y,z),(-x,y,-z)],
        [(x,-y,z),(x,-y,-z),(x,y,-z),(x,y,z)],
        [(-x,y,z),(x,y,z),(x,y,-z),(-x,y,-z)],
        [(-x,-y,-z),(x,-y,-z),(x,-y,z),(-x,-y,z)],
    ]
    lines = [f"o {name}"]
    for vertices in faces:
        lines.extend("v " + " ".join(str(v) for v in p) for p in vertices)
    for u, v, w, h in rectangles:
        for a, b in [(u,v+h),(u+w,v+h),(u+w,v),(u,v)]:
            lines.append(f"vt {a/64} {1-b/64}")
    for face in range(6):
        a, b, c, d = range(face * 4 + 1, face * 4 + 5)
        lines.extend([f"f {a}/{a} {b}/{b} {c}/{c}", f"f {a}/{a} {c}/{c} {d}/{d}"])
    obj = source_dir / f"{name}.obj"
    obj.write_text("\n".join(lines) + "\n", encoding="utf-8")
    subprocess.run([str(args.balsam.resolve()), "--generateNormals", "-o", str(output_dir), str(obj)], check=True)
