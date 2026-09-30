# WheelGuard

Windows tray utility for a mouse whose wheel (middle) button sticks.

When the middle button gets stuck, Windows keeps receiving phantom clicks and
drags that break whatever you are doing. WheelGuard watches the physical button
and cuts the wheel click off when it misbehaves.

## How it works

- Toggle the wheel click on/off with a hotkey (default `Ctrl+Alt+W`), the tray
  menu or the app window.
- While enabled, it only polls the middle-button state: if Windows sees the
  button held longer than `holdSeconds` (5 s by default), the stuck state is
  force-released in Windows — the injected release repeats until Windows stops
  seeing the button held. With `autoDisable` the wheel click is also switched
  off. Apps where the wheel is held on purpose (e.g. Blender) get a longer
  limit (`longHoldSeconds` / `longHoldApps`).
- If Windows reports the wheel held while the physical button is already
  released (leftover stuck state), it is force-released within 0.5 s in any
  mode, even with `autoDisable` off.
- No balloon/toast notifications: state changes show up in the tray icon,
  tooltip and log only.
- Optional remap (armoury-crate style): with the click enabled, the wheel press
  can be sent as another key/combo (`F6`, `Ctrl+C`, …) or mouse button
  (`X1`, `X2`, `Left`, `Right`) instead — press-and-hold works, and if the
  wheel sticks the remapped key is released automatically. Set it in the
  window (click the field and press the keys) or via `remap=` in the ini.
  Note: apps reading raw input directly may still see the wheel itself.
- While disabled, a low-level mouse hook swallows physical middle-button
  events. The hook exists only in that mode (or while a remap is set), so
  normal use adds no input latency.
- The physical button state (window, optional tray indicator) comes from raw
  input on a separate thread, registered only while the window is shown —
  raw input never delays other apps.

## Settings

`WheelGuard.ini` next to the exe; easier to change them in the WheelGuard
window (left-click the tray icon):

```ini
enabled=1
autoDisable=1
# seconds Windows may see the wheel held before the wheel click is switched off
holdSeconds=5
# longer limit for apps where the wheel is held on purpose (comma-separated process names)
longHoldSeconds=60
longHoldApps=blender
hotkey=Ctrl+Alt+W
# remap the wheel press to X1, X2, Left, Right or a key/combo; empty = off
remap=
trayIndicator=1
```

## Build

Single C# file, no project needed:

```
csc /target:winexe /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll WheelGuard.cs
```
