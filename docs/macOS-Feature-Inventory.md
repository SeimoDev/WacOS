# macOS Spaces / Mission Control and Stage Manager — Feature Inventory

Researched from Apple's Mac User Guide (Sequoia/Tahoe), Apple's Desktop & Dock settings reference, and
long-form guides (MacMost "31 Mission Control Tips", MacMost Stage Manager guide, 360-Reader "17 Stage
Manager tips", Setapp). Each row lists the macOS behavior and how WacOS implements it on Windows 11.

Legend: ✅ implemented · 🟡 implemented with a Windows-specific difference · ⛔ not possible on Windows (documented)

## 1. Spaces (multiple desktops)

| # | macOS behavior | WacOS on Windows |
|---|---|---|
| S1 | Up to 16 spaces | ✅ Enforced (create is refused beyond 16) |
| S2 | Spaces are ordered left→right; "Desktop 1..N" naming | ✅ Uses Windows virtual desktops; unnamed desktops shown as "Desktop N" |
| S3 | Switch with Control‑← / Control‑→ (no wrap‑around) | ✅ Global low‑level hook, no wrap‑around |
| S4 | Switch directly with Control‑1 … Control‑9 (opt‑in, off by default) | ✅ Opt‑in toggle, off by default (Ctrl+1..9, and Ctrl+0 = Desktop 10) |
| S5 | Trackpad: swipe left/right with three or four fingers | ✅ Precision‑touchpad raw HID input, finger count configurable (3 or 4) |
| S6 | Magic Mouse: two‑finger horizontal swipe | 🟡 Emulated with horizontal wheel tilt / Shift+wheel (opt‑in) |
| S7 | Slide animation when switching | ✅ Windows 11 own slide animation (SwitchDesktop) |
| S8 | Drag a window to the left/right screen edge and hold ~1 s → moves it to the adjacent space (single display) | ✅ Edge dwell during a window move loop |
| S9 | Press Control‑arrow while dragging a window → window travels with you | ✅ |
| S10 | Drag a window onto a space thumbnail in Mission Control | ✅ |
| S11 | Drag a window onto the "+" button → new space containing that window | ✅ |
| S12 | Assign an app to "All Desktops" / "This Desktop" / "None" (Dock → Options) | ✅ Tray menu + Settings list. All Desktops = pinned app; This Desktop = auto‑moved on open |
| S13 | Every space can have its own wallpaper | ✅ Per‑desktop wallpaper via shell API + Settings |
| S14 | Full‑screen apps become their own space (to the right), named after the app | 🟡 Emulated: a window that goes full‑screen is moved to a new space named after the app; removed when it leaves full‑screen (toggle) |
| S15 | Setting: Automatically rearrange Spaces based on most recent use | ✅ Most recently used space moved to the front |
| S16 | Setting: When switching to an application, switch to a Space with open windows | ✅ Windows already does this for taskbar activation; WacOS enforces it for Alt‑Tab and programmatic activation |
| S17 | Setting: Displays have separate Spaces | 🟡 Windows virtual desktops always span all displays; the option controls whether Mission Control/Stage Manager are per‑display (they are) |
| S18 | Deleting a space moves its windows to another space | ✅ Windows move to the space on the left (or Desktop 1) |
| S19 | Reorder spaces by dragging thumbnails in Mission Control | ✅ |
| S20 | Switching when the app you activate lives on another space | ✅ |

## 2. Mission Control

| # | macOS behavior | WacOS on Windows |
|---|---|---|
| M1 | Enter: Control‑↑, F3 / Mission Control key, three/four‑finger swipe up, hot corner, Dock icon, drag a window to the top of the screen | ✅ all except Dock icon (tray icon instead) |
| M2 | Exit: same shortcut again, Control‑↓, Esc, swipe down, click empty desktop area, click a window, click a Dock app | ✅ (Dock → taskbar click) |
| M3 | Spaces bar along the top shows all spaces as thumbnails; collapsed to names, expands when the pointer moves to the top or when a window is dragged | ✅ |
| M4 | "+" button at the right end of the spaces bar | ✅ |
| M5 | Hover a space → ✕ delete button top‑left; hold Option to show every ✕ | ✅ (Option = Alt) |
| M6 | Windows of the current space laid out as thumbnails preserving relative positions; app icon and title on hover | ✅ |
| M7 | "Group windows by application" setting stacks windows by app with the app icon | ✅ |
| M8 | Hover a thumbnail and press Space → Quick Look enlargement | ✅ |
| M9 | Click a window thumbnail → switches to it and exits | ✅ |
| M10 | Click a space thumbnail → switches and exits; Option‑click → switches but stays in Mission Control | ✅ |
| M11 | App Exposé (Control‑↓ / swipe down): windows of the current app; minimized windows in a smaller row at the bottom | ✅ |
| M12 | Show Desktop (F11 / Command‑Mission Control / spread thumb+three fingers): windows slide off the edges | ✅ Live windows slide off the nearest outer edge and slide back; stacking order is preserved |
| M13 | Hot Corners: Mission Control, Application Windows, Desktop, Notification Center, Launchpad, Quick Note, Lock Screen, Put Display to Sleep, "–" | ✅ Same list (Launchpad → Start, Notification Center/Quick Note → Windows notification center) with optional modifier key |
| M14 | Setting: Drag windows to top of screen to enter Mission Control | ✅ |
| M15 | Live window thumbnails | ✅ Live DWM thumbnails composited on the GPU |
| M17 | Windows fly from their real position into the overview and back (spring animation); the Dock stays visible | ✅ Same motion, display-refresh paced; the taskbar stays visible |
| M16 | Split View by dropping a window onto a full‑screen space thumbnail | ⛔ Not part of Spaces/Stage Manager scope (use Windows Snap) |

## 3. Stage Manager

| # | macOS behavior | WacOS on Windows |
|---|---|---|
| G1 | Toggle from Control Center / System Settings / custom shortcut (no default shortcut) | ✅ Tray, Settings, configurable shortcut (none by default, like macOS) |
| G2 | Active window(s) on the stage; other apps in a strip on the left with live thumbnails, most recent at the top, up to 6 | ✅ Strip thumbnails are snapshots taken as a window leaves the stage; count depends on screen height, max 6 |
| G2b | Strip thumbnails are drawn in perspective, angled inwards towards the stage; they face the user while a window is dragged over them | ✅ Per-thumbnail 3D perspective with spring-animated turn |
| G3 | Click a thumbnail → that set comes to the stage, the current set goes to the strip | ✅ with slide animation |
| G4 | Windows of one app form one set/thumbnail ("All at Once") or each window is separate ("One at a Time") | ✅ Setting |
| G5 | Shift‑click a thumbnail → add it to the current stage set (grouping) | ✅ |
| G6 | Drag a thumbnail from the strip onto the stage → add to the group | ✅ The live windows follow the pointer, grow as they leave the strip and land where dropped |
| G7 | Drag a window from the stage onto the strip → it leaves the stage and is stowed in the strip | ✅ The strip stays visible while dragging, opens a slot under the pointer, and the window flies in on release. Works for every app, including custom title bars and elevated windows; Windows edge snapping is suspended while Stage Manager is on |
| G8 | Opening a new app → it takes the stage, previous set goes to the strip | ✅ |
| G9 | Switching apps with Command‑Tab / Dock → set swap | ✅ Alt‑Tab / taskbar |
| G10 | Minimize (Command‑M / yellow button) → window goes to the strip instead of the Dock | ✅ Minimize is intercepted, animated into the strip |
| G11 | Hide app (Command‑H) → app removed from strip | ⛔ Windows has no "hide app"; closing behaves the same |
| G12 | Click the wallpaper → all windows go to the strip, desktop shown; click a thumbnail to bring windows back | ✅ |
| G13 | Setting: Show Desktop — "Always on wallpaper click" / "Only in Stage Manager" | ✅ |
| G14 | Setting: Show Items — On Desktop / In Stage Manager (desktop icons visibility) | ✅ Desktop icons hidden/shown per setting |
| G15 | Setting: Show recent apps — off → strip hidden; move the pointer to the left edge to reveal | ✅ |
| G16 | Strip auto‑hides when a stage window needs the space; pointer at left edge reveals it | ✅ |
| G17 | Windows are pushed right so they don't overlap the strip when it is visible | ✅ |
| G18 | Drag content over a thumbnail → its windows spring to the stage so you can drop | ✅ Hold a drag over a thumbnail ~0.7 s |
| G19 | Command‑` cycles windows of the current app | ✅ Alt‑` |
| G20 | Each display has its own stage & strip; each space has its own Stage Manager state | ✅ |
| G21 | Full‑screen windows leave Stage Manager (own space) | ✅ via S14 |
| G22 | Works together with Mission Control and Spaces | ✅ |
| G23 | Windows animate (slide/scale) between stage and strip | ✅ Live window ghosts fly between stage and strip on a damped spring with staggered starts; strip windows are hidden by the shell (not minimized), so they come back without repainting or flicker |

## 4. Trackpad gestures (System Settings → Trackpad → More Gestures)

| Gesture | macOS | WacOS |
|---|---|---|
| Swipe between full‑screen apps/spaces | 3 or 4 fingers left/right | ✅ |
| Mission Control | 3 or 4 fingers up | ✅ |
| App Exposé | 3 or 4 fingers down | ✅ |
| Show Desktop | Spread thumb + three fingers | ✅ (4‑contact spread) |
| Launchpad | Pinch thumb + three fingers | 🟡 Opens Start (opt‑in) |

Windows note: Windows Settings → Bluetooth & devices → Touchpad → "Three‑finger gestures"/"Four‑finger gestures"
should be set to "Nothing" for the swipes so the OS does not also react. WacOS reads the raw HID reports
from the Precision Touchpad, so it works regardless.

## 5. Keyboard shortcuts (System Settings → Keyboard → Shortcuts → Mission Control)

| Action | macOS default | WacOS default |
|---|---|---|
| Mission Control | ^↑ (also F3) | Ctrl+↑, F3 |
| Show Notification Center | – | – |
| Turn Do Not Disturb on/off | – | – |
| Application windows | ^↓ | Ctrl+↓ |
| Show Desktop | F11 | F11 |
| Turn Stage Manager on/off | – | – (configurable) |
| Move left a space | ^← | Ctrl+← |
| Move right a space | ^→ | Ctrl+→ |
| Switch to Desktop 1..N | ^1 … (off) | Ctrl+1 … (off) |
| Quick Note | fn Q | – |

All shortcuts are editable in WacOS Settings. Because Ctrl+←/→ is word navigation in Windows editors, there is a
one‑click option to use Win+Ctrl+←/→ instead (Windows' native chord).
