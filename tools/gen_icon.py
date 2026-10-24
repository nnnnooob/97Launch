#!/usr/bin/env python3
"""Package the approved AppIcon.png artwork as a multi-resolution Windows icon."""

from pathlib import Path

from PIL import Image

ASSETS = Path(__file__).resolve().parent.parent / "src" / "Launcher.App" / "Assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def main() -> None:
    with Image.open(ASSETS / "AppIcon.png") as source:
        source.convert("RGBA").save(
            ASSETS / "AppIcon.ico", format="ICO", sizes=[(size, size) for size in SIZES]
        )
    print(f"Packaged {len(SIZES)} icon sizes into {ASSETS / 'AppIcon.ico'}")


if __name__ == "__main__":
    main()
