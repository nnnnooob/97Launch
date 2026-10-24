#!/usr/bin/env python3
"""Screenshot a window by handle, without depending on foreground focus.

Screen-grabbing the window rect only works when the target is actually on top;
against a browser that keeps stealing the foreground it silently captures the
wrong pixels. PrintWindow asks the window to render itself into a bitmap, which
works regardless of stacking order, and PW_RENDERFULLCONTENT (0x2) makes it
include DirectComposition content such as the Mica backdrop and WinUI's own
swap chain.
"""

import ctypes
import sys
import time
from ctypes import wintypes

from PIL import Image

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()

PW_RENDERFULLCONTENT = 0x00000002
SW_RESTORE = 9
SW_SHOW = 5
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def capture(hwnd: int) -> Image.Image:
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w = rect.right - rect.left
    h = rect.bottom - rect.top
    if w <= 0 or h <= 0:
        raise OSError(f"window has no area: {w}x{h}")

    hdc_screen = user32.GetDC(0)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbm = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, hbm)

    ok = user32.PrintWindow(hwnd, hdc_mem, PW_RENDERFULLCONTENT)
    if not ok:
        # Older builds reject PW_RENDERFULLCONTENT; plain PrintWindow still works.
        ok = user32.PrintWindow(hwnd, hdc_mem, 0)

    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h  # negative => top-down rows
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdc_mem, hbm, 0, h, buf, ctypes.byref(bmi), DIB_RGB_COLORS)

    img = Image.frombuffer("RGBA", (w, h), buf.raw, "raw", "BGRA", 0, 1).convert("RGB")

    gdi32.DeleteObject(hbm)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(0, hdc_screen)
    return img


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: shot.py <hwnd> <output.png>")
        return 2

    hwnd = int(sys.argv[1], 0)
    out = sys.argv[2]

    if not user32.IsWindow(hwnd):
        print(f"hwnd={hwnd} is not a window")
        return 1

    if user32.IsIconic(hwnd):
        user32.ShowWindow(hwnd, SW_RESTORE)
    user32.ShowWindow(hwnd, SW_SHOW)
    time.sleep(0.5)

    img = capture(hwnd)
    img.save(out)
    print(f"saved {out} ({len(open(out, 'rb').read())} bytes) size={img.size}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
