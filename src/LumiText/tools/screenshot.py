# S1 spike screenshot helper (ctypes only, no third-party deps).
# Usage: python screenshot.py <pid> <out.bmp> [--unfocus]
import ctypes
import ctypes.wintypes as wt
import struct
import sys

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
kernel32 = ctypes.windll.kernel32

# Per-monitor DPI aware v2 so coordinates match physical pixels.
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    pass


def find_main_window(pid):
    result = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def enum_cb(hwnd, lparam):
        proc = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(proc))
        if proc.value == pid and user32.IsWindowVisible(hwnd):
            length = user32.GetWindowTextLengthW(hwnd)
            if length > 0:
                buf = ctypes.create_unicode_buffer(length + 1)
                user32.GetWindowTextW(hwnd, buf, length + 1)
                result.append((hwnd, buf.value))
        return True

    user32.EnumWindows(enum_cb, 0)
    return result


def capture(hwnd, out_path):
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w = rect.right - rect.left
    h = rect.bottom - rect.top

    hdc_screen = user32.GetDC(None)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    gdi32.SelectObject(hdc_mem, hbmp)
    SRCCOPY = 0x00CC0020
    gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, rect.left, rect.top, SRCCOPY)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [
            ("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
            ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
            ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
            ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
        ]

    bih = BITMAPINFOHEADER()
    bih.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bih.biWidth = w
    bih.biHeight = -h  # top-down
    bih.biPlanes = 1
    bih.biBitCount = 24
    bih.biCompression = 0  # BI_RGB
    row_size = (w * 3 + 3) & ~3
    img_size = row_size * h
    buf = ctypes.create_string_buffer(img_size)
    gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bih), 0)

    with open(out_path, "wb") as f:
        f.write(b"BM")
        f.write(struct.pack("<IHHI", 14 + 40 + img_size, 0, 0, 54))
        # 负高度 = top-down 声明，与 GetDIBits(biHeight=-h) 拿到的数据顺序一致，
        # 否则查看器按 bottom-up 解读会把图像翻转。
        f.write(struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 24, 0, img_size, 2835, 2835, 0, 0))
        f.write(buf.raw)

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(None, hdc_screen)
    print(f"saved={out_path} {w}x{h}")


def unfocus_others(own_hwnd):
    # Move foreground to the desktop shell window so our window loses focus.
    progman = user32.FindWindowW("Progman", None)
    if progman:
        user32.SetForegroundWindow(progman)


def main():
    pid = int(sys.argv[1])
    out = sys.argv[2]
    wins = find_main_window(pid)
    if not wins:
        print("WINDOW_NOT_FOUND")
        sys.exit(1)
    hwnd, title = wins[0]
    print(f"hwnd={hwnd} title={title}")
    if "--unfocus" in sys.argv:
        unfocus_others(hwnd)
        import time
        time.sleep(0.8)
    capture(hwnd, out)


if __name__ == "__main__":
    main()
