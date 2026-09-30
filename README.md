# WacOS — macOS Spaces, Mission Control and Stage Manager for Windows 11

[中文文档 / Chinese documentation](README.zh-CN.md)

WacOS brings the two macOS window‑management features to Windows 11 with the same behaviour, gestures,
shortcuts and settings:

* **Spaces / Mission Control** — multiple desktops with the top Spaces bar, drag‑and‑drop between spaces,
  Ctrl+←/→ switching, three/four‑finger swipes, hot corners, App Exposé, Show Desktop, per‑space wallpapers,
  "assign app to desktop", auto‑rearrange, full‑screen apps as their own space.
* **Stage Manager** — the active app on the stage, recent apps as live thumbnails in a left strip, groups
  (Shift‑click / drag), click‑the‑wallpaper to reveal the desktop, auto‑hiding strip, "All at Once / One at a Time",
  desktop items visibility, per‑space and per‑display state.

The complete macOS feature inventory and how each item maps to Windows is in
[`docs/macOS-Feature-Inventory.md`](docs/macOS-Feature-Inventory.md).

## Requirements

* Windows 11 24H2 or 25H2 (build 26100+). The undocumented virtual‑desktop COM interfaces are build‑specific.
* .NET 8 SDK to build (`dotnet --list-sdks`).
* A Precision Touchpad for gestures (any modern laptop touchpad).

## Build & run

```powershell
cd src\WacOS
dotnet build -c Release
.\bin\Release\net8.0-windows10.0.19041.0\WacOS.exe
```

Command-line switches:

| Switch | Effect |
|---|---|
| `--selftest` | Prints the detected spaces, monitors and windows (with capture results) and exits. Use it to check a new Windows build. |
| `--verbose` | Debug logging to `%AppData%\WacOS\wacos.log`. |
| `--stage` | Start with Stage Manager on (ignores the saved setting). |
| `--quit` | Asks the running instance to quit gracefully (restores windows, hooks and desktop icons). |

WacOS runs in the system tray. Left‑click the icon = Mission Control, right‑click = menu (Stage Manager toggle,
Spaces list, "Assign app to…", Settings). The Settings window opens on first run.

## Default shortcuts and gestures (identical to macOS)

| Action | Keyboard | Trackpad |
|---|---|---|
| Mission Control | Ctrl+↑ or F3 | 3/4‑finger swipe up |
| Application windows (App Exposé) | Ctrl+↓ | 3/4‑finger swipe down |
| Show Desktop | F11 | Spread thumb + three fingers |
| Move left / right a space | Ctrl+← / Ctrl+→ | 3/4‑finger swipe left/right |
| Switch to Desktop N | Ctrl+1 … (off by default) | – |
| Stage Manager on/off | none (configurable) | – |
| Cycle windows of the app | Alt+` (⌘` on macOS) | – |
| Inside Mission Control | Esc exit · Space = Quick Look · Alt = show all ✕ · Alt‑click space = switch and stay | – |

Windows tip: set the three‑ and four‑finger swipes to **Nothing** in *Settings → Bluetooth & devices → Touchpad*
so the OS does not also react to them. WacOS reads the raw touchpad reports, so its gestures work regardless.
Ctrl+←/→ is word navigation in Windows editors; the Shortcuts page has a switch to use Win+Ctrl+←/→ instead.

## Project layout

```
src/WacOS
  Native/            Win32, DWM, HID P/Invoke, WinEvent + low-level hooks
  VirtualDesktops/   Windows 11 virtual desktop COM interop + VirtualDesktopService (Spaces)
  Desktop/           window enumeration, capture (PrintWindow), icons, monitors, WindowTracker
  Input/             HotkeyService, TouchpadGestureService (raw HID), MouseService (hot corners, edges, wallpaper click)
  Features/
    Spaces/          shortcuts, gestures, drag-to-edge, app assignments, auto-rearrange, fullscreen spaces
    MissionControl/  Mission Control + App Exposé overlay (spaces bar, drag & drop, Quick Look)
    StageManager/    StageManagerService, StripWindow
    Anim / ThumbAnimator / OverlayWindow   animation engine (spring easing, compositor-paced thumbnail motion)
    ShowDesktopService, HotCornerService
  Settings/          settings model (JSON in %AppData%\WacOS) + Settings window
  Tray/              tray icon and menu
docs/macOS-Feature-Inventory.md
```

## How the animations work

Every transition moves *live* window pictures, not screenshots: WacOS registers DWM thumbnails of the real windows
and lets the desktop compositor draw them on the GPU. A dedicated thread paced by `DwmFlush` updates their position
once per composed frame with a critically damped spring curve, so motion runs at the display refresh rate and is not
affected by UI-thread work. Mission Control's own window is opaque, pre-rendered and kept DWM-cloaked, which makes
opening it instant; its first frame is an exact picture of the desktop, so windows appear to fly straight out of place.
Run with `--verbose` to get per-animation frame statistics in the log.

Windows that leave the screen (Stage Manager strip, Show Desktop) are not minimized. The shell cloaks them, the same
mechanism Windows uses for windows on other virtual desktops: they keep rendering, their thumbnails stay live, and
they reappear without the app having to repaint, so there is no flicker at the end of an animation. Every hidden
window is journaled to `%AppData%\WacOS\hidden-windows.txt` and revealed again on exit or on the next start.

Motion curves: Mission Control uses cubic Bézier curves (a long expo-out glide on the way in, a quick settle on the
way out) with a short distance-based cascade; Stage Manager uses a real damped spring with a small overshoot and
staggered windows; Show Desktop accelerates windows off screen and springs them back.

## Stage Manager drag gestures

* Drag a stage window onto the strip to stow it. The drag is recognised from window-location events plus the pointer
  position, so it works for every app: custom title bars and elevated windows (Task Manager, apps run as administrator)
  included, which low-level mouse hooks never see.
* Companion windows travel together: windows of the same app that moved along with the dragged one (or are tied to it
  by ownership) are stowed as one unit and come back together with their relative positions intact.
* Drag a thumbnail out of the strip: its live windows follow the pointer, grow as they leave the strip, and land where
  they are dropped, joining the current group.
* While Stage Manager is on, Windows' "drag to a screen edge to snap" is switched off (the left edge belongs to the
  strip) and restored when Stage Manager is turned off or WacOS exits. There is a setting to keep snapping enabled.

## Known differences from macOS

* Windows of **elevated (administrator) apps** can be stowed and shown without elevation, but a normal process cannot
  move or resize them. Turn on *Settings → General → Run as administrator* (or start with `--elevate`) for full control;
  sign-in start-up then uses a highest-privilege scheduled task so there is no consent prompt at logon.
* Windows virtual desktops span all displays; "Displays have separate Spaces" therefore only affects per‑display
  Mission Control / Stage Manager, not independent desktops per display.
* "Hide app" (⌘H) has no Windows equivalent; Split View from Mission Control is out of scope.
* Full‑screen‑as‑a‑space is emulated by moving the full‑screen window to a new desktop.
