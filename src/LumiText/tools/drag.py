# Simulate a mouse drag inside a window (ctypes only).
# Usage: python drag.py <pid> <x1> <y1> <x2> <y2> [steps]
# Coordinates are client-area pixels relative to the window's client origin.
import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.windll.user32


def find_main_window(pid):
    result = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def enum_cb(hwnd, lparam):
        proc = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(proc))
        if proc.value == pid and user32.IsWindowVisible(hwnd):
            if user32.GetWindowTextLengthW(hwnd) > 0:
                result.append(hwnd)
        return True

    user32.EnumWindows(enum_cb, 0)
    return result[0] if result else None


def main():
    pid, x1, y1, x2, y2 = (int(v) for v in sys.argv[1:6])
    steps = int(sys.argv[6]) if len(sys.argv) > 6 else 25
    hwnd = find_main_window(pid)
    if not hwnd:
        print("WINDOW_NOT_FOUND")
        sys.exit(1)

    pt = wt.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    sx, sy = pt.x, pt.y
    print(f"client_origin=({sx},{sy})")

    user32.SetForegroundWindow(hwnd)
    time.sleep(0.3)

    user32.SetCursorPos(sx + x1, sy + y1)
    time.sleep(0.2)
    user32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
    time.sleep(0.15)
    for i in range(1, steps + 1):
        nx = sx + x1 + (x2 - x1) * i // steps
        ny = sy + y1 + (y2 - y1) * i // steps
        user32.SetCursorPos(nx, ny)
        user32.mouse_event(0x0001, 0, 0, 0, 0)  # MOVE
        time.sleep(0.02)
    time.sleep(0.15)
    user32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
    print("drag_done")


if __name__ == "__main__":
    main()
