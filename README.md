<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="brand/logo/tessera-horizontal-reversed.svg">
    <img src="brand/logo/tessera-horizontal.svg" alt="Tessera" height="56">
  </picture>
</h1>

A small tiling window manager for Windows 11. Open an app and it gets a tile; close it and the others grow to fill
the space. Invisible and hidden "ghost" windows are skipped, so you never get an empty tile.

## Download

Get `Tessera-<version>.zip` from [Releases](https://github.com/Luan1i/Tessera/releases), unzip it anywhere and run
`Tessera.exe`. It isn't code-signed, so Windows may say "Windows protected your PC" the first time: click
**More info → Run anyway**. Settings files are kept next to the exe, so put it in a folder you keep.

## Build and run

Run `build.cmd`. It uses the C# compiler that ships with Windows (.NET Framework 4.8), so there is nothing to install.
Then start `bin\Tessera.exe`. It lives in the tray; double-click the tray icon for **Settings & help**.

### Making a release

1. Bump the version in `src/AssemblyInfo.cs` and commit.
2. Tag that commit `v` + the version (e.g. `v1.0.1`) and push the tag.
   In GitHub Desktop: History → right-click the commit → Create Tag…, then Push.

GitHub Actions (`.github/workflows/release.yml`) then builds it and publishes the zip on the Releases page.
`release.cmd` makes the same zip locally in `dist\`.

## What it does

- **Auto-tiling** per monitor, with gaps. Pick a **layout** in Settings & help, the tray menu or with Win+Alt+N:
  Grid, Columns, Rows, Main + stack (50/25/25), Stack + main (25/25/50), Spiral, Tabs or Manual. Space is shared out
  evenly whenever a window opens or closes, so ten windows still look tidy (stored in `bin\options.json`).
- **App rules** (Settings & help → App rules, stored in `bin\rules.json`): pick what happens when an app opens a window —
  float it, put it on a certain monitor, or add it as a tab to that app's other windows. Pick the app from the ones
  open now or from your Start menu; the Window inspector's *Make a rule…* button picks it for you.
- **Keyboard control** for focus, swapping, resizing, moving between monitors, floating and tab groups.
  Every shortcut can be changed in Settings & help (stored in `bin\keys.json`).
- **Drag a title bar**: drop zones appear on the other tiles — *Swap* in the middle, *Tab* near the top, or an edge to
  put the window on that side. Drop it on another monitor (*Move here* on an empty one) to send it there.
  **Drag a border** to resize tiles.
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
| Win+Alt+N | Next layout |
| Win+H / Win+V | Only the next window opens beside / below the focused one |
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
| `src/Rules.cs` | App rules (`rules.json`) |
| `src/Inspector.cs`, `src/Layout.cs`, `src/Native.cs` | Inspector, layout save/restore, Win32 calls |
| `src/AssemblyInfo.cs` | Version number |
| `src/icons/` | App and tray icons (light/dark taskbar), built into the exe |
| `brand/` | Logo files, usage guide (`brand/GUIDELINES.md`) and `build_logo.py`, which generates all of them |
