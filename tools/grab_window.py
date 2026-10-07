"""Capture a top-level window to a PNG.

IMPORTANT — do not use PrintWindow on WinUI 3 / Windows App SDK windows.
Measured during this project: `PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)` on a
WinUI 3 window reliably kills the process with

    Fatal error. System.AccessViolationException

raised at the next framework dispatch (typically `Frame.Navigate`), often many
seconds after the capture, which makes it look like an unrelated page bug. The
WinUI compositor does not support being asked to render into a foreign DC this
way. It was misdiagnosed as a page defect at least twice before being isolated.

This tool therefore raises the window and grabs it off the screen with BitBlt,
which is safe while the desktop is unlocked and no other window covers it. Pass
--printwindow only for classic GDI/HWND apps (Win32, WinForms), never for WinUI.

Usage:
    python grab_window.py <title-substring> <output.png> [--printwindow]
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

from PIL import Image, ImageGrab

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
user32.SetProcessDPIAware()

SW_RESTORE = 9
PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def find_window(substr: str):
    matches = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def enum_proc(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length == 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buf, length + 1)
        if substr.lower() in buf.value.lower():
            matches.append((hwnd, buf.value))
        return True

    user32.EnumWindows(enum_proc, 0)
    return matches


def window_rect(hwnd):
    rect = wt.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        return None
    if rect.right <= rect.left or rect.bottom <= rect.top:
        return None
    return (rect.left, rect.top, rect.right, rect.bottom)


def capture_with_printwindow(hwnd, box, out_path):
    """GDI capture. Safe for Win32/WinForms, UNSAFE for WinUI 3 (see module docstring)."""
    left, top, right, bottom = box
    width, height = right - left, bottom - top

    window_dc = user32.GetWindowDC(hwnd)
    mem_dc = gdi32.CreateCompatibleDC(window_dc)
    bitmap = gdi32.CreateCompatibleBitmap(window_dc, width, height)
    gdi32.SelectObject(mem_dc, bitmap)

    user32.PrintWindow(hwnd, mem_dc, PW_RENDERFULLCONTENT)

    info = BITMAPINFO()
    info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    info.bmiHeader.biWidth = width
    info.bmiHeader.biHeight = -height
    info.bmiHeader.biPlanes = 1
    info.bmiHeader.biBitCount = 32
    info.bmiHeader.biCompression = 0

    buffer = ctypes.create_string_buffer(width * height * 4)
    gdi32.GetDIBits(mem_dc, bitmap, 0, height, buffer, ctypes.byref(info), DIB_RGB_COLORS)
    Image.frombuffer("RGBA", (width, height), buffer, "raw", "BGRA", 0, 1).convert("RGB").save(out_path)

    gdi32.DeleteObject(bitmap)
    gdi32.DeleteDC(mem_dc)
    user32.ReleaseDC(hwnd, window_dc)


def capture_from_screen(hwnd, box, out_path):
    """Raise the window, then grab its screen region. Safe for WinUI 3."""
    user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetForegroundWindow(hwnd)
    time.sleep(1.2)

    # Re-read the rect after restoring: a minimised window's rect is bogus.
    box = window_rect(hwnd) or box
    ImageGrab.grab(bbox=box, all_screens=True).save(out_path)
    return box


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    use_printwindow = "--printwindow" in sys.argv

    if len(args) < 2:
        print(__doc__)
        return 2

    target, out_path = args[0], args[1]
    matches = find_window(target)
    if not matches:
        print(f"no visible window matching {target!r}")
        return 1

    hwnd, title = matches[0]
    box = window_rect(hwnd)
    if box is None:
        print("could not read the window rect")
        return 1

    print(f"window {title!r} hwnd={hwnd} rect={box}")

    if use_printwindow:
        print("WARNING: PrintWindow can crash WinUI 3 / Windows App SDK windows.")
        capture_with_printwindow(hwnd, box, out_path)
        width, height = box[2] - box[0], box[3] - box[1]
    else:
        got = capture_from_screen(hwnd, box, out_path)
        width, height = got[2] - got[0], got[3] - got[1]

    print(f"saved {out_path} ({width}x{height})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
