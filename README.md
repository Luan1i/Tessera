# Tessera

A small tiling window manager for Windows 11. Open an app and it gets a tile; close it and the others grow to fill
the space. Invisible and hidden "ghost" windows are skipped, so you never get an empty tile.

## Build and run

Run `build.cmd`. It uses the C# compiler that ships with Windows (.NET Framework 4.8), so there is nothing to install.
Then start `bin\Tessera.exe`. It lives in the tray; double-click the tray icon for **Settings & help**.

## What it does

- **Auto-tiling** per monitor, with gaps, equal or custom sizes.
- **Keyboard control** for focus, swapping, resizing, moving between monitors, floating and tab groups.
  Every shortcut can be changed in Settings & help (stored in `bin\keys.json`).
- **Drag a title bar**: drop zones appear on the other tiles — *Swap* in the middle, *Tab* near the top, or an edge to
  put the window on that side. **Drag a border** to resize tiles.
- **Tab groups**: several windows share one tile under a tab strip (click to switch, middle-click to close).
- **Focus border**: the focused window gets a blue border.
- **Layout memory**: sizes, tab groups and which app is where are saved to `bin\layout.json` and restored after a
  restart as your apps open.
- **Window inspector**: shows every managed window with its program, PID and path; float, ignore forever
  (`bin\ignore.txt`), close or kill it.
- **Starts with Windows** (switch in Settings & help).

## Default shortcuts

| Shortcut | Action |
|---|---|
| Win+Shift+Arrow | Focus the window in that direction (any monitor) |
| Win+Arrow | Swap with the window in that direction (same monitor) |
| Win+Alt+Shift+Arrow | Send the focused window to the monitor in that direction |
| Win+Alt+Right / Left / Down / Up | Wider / narrower / taller / shorter |
| Win+Alt+E | Equal sizes |
| Win+H / Win+V | Next window opens beside / below the focused one |
| Win+D | Flip a group between side by side and stacked |
| Win+F | Float / tile the focused window |
| Win+S | Make a tab group, or lay one out side by side again |
| Win+PageDown / PageUp | Next / previous tab |
| Win+Shift+I | Window inspector |
| Win+P | Pause / resume (gives Windows its own shortcuts back) |
| Win+Y | Re-tile everything from scratch |
| Win+F1 | Settings & help |

The defaults replace some Windows shortcuts (Win+Arrow snap, Win+D, Win+V, Win+H, Win+F, Win+S, Win+P, Win+F1)
while Tessera runs; change them in Settings & help if you'd rather keep those.

## How it's built

| File | What |
|---|---|
| `src/App.cs` | Tray app, keyboard hook, actions and their default keys |
| `src/WM.cs` | Window filter, layout sync, all window commands, saved layout |
| `src/Tree.cs` | The tiling tree (splits, tabs, sizes) + self-test |
| `src/Drag.cs`, `src/Tabs.cs` | Drag-and-drop zones, tab strips |
| `src/Keys.cs` | Key binding parsing and `keys.json` |
| `src/Settings.cs`, `src/settings.html` | Settings & help page (served on 127.0.0.1, shown in an Edge app window), start-with-Windows |
| `src/Inspector.cs`, `src/Layout.cs`, `src/Native.cs` | Inspector, layout save/restore, Win32 calls |
