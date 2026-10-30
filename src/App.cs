using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Tessera
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: real pixel coordinates on every monitor
            bool first;
            using (new Mutex(true, "Tessera.SingleInstance", out first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.Run(new App());
            }
        }
    }

    // Hidden window that runs shortcut actions after the keyboard hook returns, and hears monitor / taskbar changes.
    class MessageWindow : NativeWindow, IDisposable
    {
        const int WM_APP = 0x8000, WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A, WM_QUERYENDSESSION = 0x0011;
        readonly Queue<Action> pending = new Queue<Action>();
        public Action ScreenChanged, SessionEnding;

        public MessageWindow() { CreateHandle(new CreateParams()); }

        public void Post(Action action)
        {
            pending.Enqueue(action);
            Native.PostMessage(Handle, WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APP) pending.Dequeue()();
            else if ((m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE) && ScreenChanged != null) ScreenChanged();
            else if (m.Msg == WM_QUERYENDSESSION && SessionEnding != null) SessionEnding();
            base.WndProc(ref m);
        }

        public void Dispose() { DestroyHandle(); }
    }

    class App : ApplicationContext
    {
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        // foreground, move/resize start/end, minimize start/end, destroy/show/hide, title change, cloaked/uncloaked
        static readonly uint[] EventRanges = { 0x0003, 0x0003, 0x000A, 0x000B, 0x0016, 0x0017, 0x8001, 0x8003, 0x800C, 0x800C, 0x8017, 0x8018 };

        readonly WM wm = new WM();
        readonly DragController drag;
        readonly MessageWindow msgs = new MessageWindow();
        readonly System.Windows.Forms.Timer throttle = new System.Windows.Forms.Timer { Interval = 60 };
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 3000 }; // layout.json, only when changed
        readonly Native.WinEventProc onEvent; // kept in a field: a garbage-collected callback would crash the hook
        readonly List<IntPtr> hooks = new List<IntPtr>();
        readonly Native.LowLevelKeyboardProc onKey; // same reason as onEvent
        readonly IntPtr keyHook;
        readonly List<KeyAction> actions = new List<KeyAction>();
        Dictionary<int, KeyAction> bound = new Dictionary<int, KeyAction>(); // KeyBindings.Parse code -> action
        readonly List<KeyValuePair<ToolStripMenuItem, string>> menuKeys = new List<KeyValuePair<ToolStripMenuItem, string>>(); // item -> action id
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem pauseItem;
        readonly Inspector inspector;
        readonly SettingsServer settings;

        public App()
        {
            drag = new DragController(wm);
            throttle.Tick += delegate { throttle.Stop(); wm.Sync(); };
            onEvent = (hook, ev, h, idObject, idChild, thread, time) =>
            {
                if (idObject != 0) return; // whole windows only, not carets/scrollbars
                if (ev == EVENT_SYSTEM_FOREGROUND) wm.NoteFocus(h);
                else if (ev == EVENT_SYSTEM_MOVESIZESTART) drag.Start(h);
                else if (ev == EVENT_SYSTEM_MOVESIZEEND) drag.End(h);
                if (!throttle.Enabled) throttle.Start();
            };
            for (int i = 0; i < EventRanges.Length; i += 2)
                hooks.Add(Native.SetWinEventHook(EventRanges[i], EventRanges[i + 1], IntPtr.Zero, onEvent, 0, 0, 2 /* out of context, skip own process */));

            inspector = new Inspector(wm);
            settings = new SettingsServer(actions, Rebind);
            DefineActions();
            KeyBindings.Load(actions);
            onKey = OnKey;
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, onKey, Native.GetModuleHandle(null), 0);
            msgs.ScreenChanged = () => { if (!throttle.Enabled) throttle.Start(); };
            msgs.SessionEnding = wm.SessionEnding;
            saveTimer.Tick += delegate { wm.SaveLayout(); };
            saveTimer.Start();

            var menu = new ContextMenuStrip();
            menu.Items.Add(MenuItem("Settings && help", "settings", settings.Open));
            menu.Items.Add(MenuItem("Show windows", "inspector", inspector.Toggle));
            menu.Items.Add(pauseItem = MenuItem("Pause tiling", "pause", TogglePause));
            menu.Items.Add(MenuItem("Re-tile all", "retile", wm.Reload));
            menu.Items.Add("Exit", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Tessera", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { settings.Open(); };
            Rebind();
            if (keyHook == IntPtr.Zero)
                tray.ShowBalloonTip(8000, "Tessera", "Keyboard shortcuts could not be enabled.", ToolTipIcon.Warning);

            Ignore.Load();
            wm.StartRestore();
            wm.Sync();
        }

        void Add(string section, string id, string label, string description, string keys, Action run, bool whilePaused = false)
        {
            actions.Add(new KeyAction { Section = section, Id = id, Label = label, Description = description, Default = keys, Run = run, WhilePaused = whilePaused });
        }

        // Every command the user can bind, in the order the settings page shows them.
        void DefineActions()
        {
            string[] dir = { "Left", "Up", "Right", "Down" }, ids = { "left", "up", "right", "down" };
            string[] where = { "to the left", "above", "to the right", "below" }, monitor = { "left", "above", "right", "below" };
            for (int d = 0; d < 4; d++)
            {
                int i = d; // C# 5 'for' loops share one variable across iterations
                Add("Moving around", "focus-" + ids[i], "Focus " + ids[i], "Focus the window " + where[i] + " (also across monitors).", "Win+Shift+" + dir[i], () => wm.FocusDir(i));
            }
            for (int d = 0; d < 4; d++)
            {
                int i = d;
                Add("Moving around", "swap-" + ids[i], "Swap " + ids[i], "Swap the focused window with the one " + where[i] + " (same monitor).", "Win+" + dir[i], () => wm.MoveDir(i));
            }
            for (int d = 0; d < 4; d++)
            {
                int i = d;
                Add("Moving around", "monitor-" + ids[i], "Send to monitor " + monitor[i], "Move the focused window to the monitor " + (i % 2 == 0 ? "on the " : "") + monitor[i] + ".", "Win+Alt+Shift+" + dir[i], () => wm.MoveToMonitor(i));
            }
            string[] size = { "narrower", "shorter", "wider", "taller" };
            for (int d = 0; d < 4; d++)
            {
                int i = d;
                Add("Sizes", size[i], char.ToUpper(size[i][0]) + size[i].Substring(1), "Make the focused window " + size[i] + ".", "Win+Alt+" + dir[i], () => wm.ResizeFocused(i));
            }
            Add("Sizes", "equalize", "Equal sizes", "Make all tiles equal size again.", "Win+Alt+E", wm.Equalize);
            Add("Layout", "split-beside", "Open next beside", "The next window you open goes beside the focused one.", "Win+H", () => wm.SplitFocused(false));
            Add("Layout", "split-below", "Open next below", "The next window you open goes below the focused one.", "Win+V", () => wm.SplitFocused(true));
            Add("Layout", "flip", "Flip direction", "Flip the focused group between side by side and stacked.", "Win+D", wm.FlipDirection);
            Add("Layout", "float", "Float / tile", "Float the focused window (centered, on top) or put it back in the tiles.", "Win+F", wm.ToggleFloat);
            Add("Tabs", "tabs", "Tab group", "Turn the focused window into a tab group, or lay a tab group out side by side again.", "Win+S", wm.ToggleStack);
            Add("Tabs", "tab-next", "Next tab", "Show the next tab of the group.", "Win+PageDown", () => wm.NextTab(1));
            Add("Tabs", "tab-prev", "Previous tab", "Show the previous tab of the group.", "Win+PageUp", () => wm.NextTab(-1));
            Add("Tools", "inspector", "Window inspector", "See every managed window and its program; float, ignore, close or kill it.", "Win+Shift+I", inspector.Toggle, true);
            Add("Tools", "pause", "Pause / resume", "Pause tiling and give Windows its own shortcuts back.", "Win+P", TogglePause, true);
            Add("Tools", "retile", "Re-tile everything", "Start over from scratch (resets sizes and tab groups, reloads the ignore list).", "Win+Y", wm.Reload);
            Add("Tools", "settings", "Settings & help", "Open this window.", "Win+F1", settings.Open, true);
        }

        ToolStripMenuItem MenuItem(string text, string actionId, Action run)
        {
            var item = new ToolStripMenuItem(text, null, delegate { run(); });
            menuKeys.Add(new KeyValuePair<ToolStripMenuItem, string>(item, text));
            item.Tag = actionId;
            return item;
        }

        // Keys changed (startup or the settings page): rebuild the lookup and the tray menu hints.
        void Rebind()
        {
            var next = new Dictionary<int, KeyAction>();
            foreach (var a in actions)
            {
                int combo = KeyBindings.Parse(a.Keys);
                if (combo > 0) next[combo] = a;
            }
            bound = next;
            foreach (var m in menuKeys)
            {
                var a = actions.Find(x => x.Id == (string)m.Key.Tag);
                m.Key.Text = a != null && a.Keys.Length > 0 ? m.Value + " (" + a.Keys + ")" : m.Value;
            }
        }

        // Sees keys before Windows' own shortcuts, so bound combos win; anything unbound passes through.
        // While paused, only actions marked WhilePaused (pause, inspector, settings) still fire.
        IntPtr OnKey(int code, IntPtr msg, ref Native.KBDLLHOOKSTRUCT k)
        {
            int m = msg.ToInt32();
            if (code >= 0 && (m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN) && (k.flags & Native.LLKHF_INJECTED) == 0)
            {
                bool win = Native.KeyDown(0x5B) || Native.KeyDown(0x5C), alt = Native.KeyDown(0x12);
                int combo = (int)k.vkCode | (win ? KeyBindings.Win : 0) | (Native.KeyDown(0x11) ? KeyBindings.Ctrl : 0)
                    | (alt ? KeyBindings.Alt : 0) | (Native.KeyDown(0x10) ? KeyBindings.Shift : 0);
                KeyAction a;
                if (bound.TryGetValue(combo, out a) && (!wm.Paused || a.WhilePaused))
                {
                    if (win || alt) Native.TapMaskKey(); // so releasing Win/Alt doesn't open Start or an app's menu bar
                    wm.StopRestoring(); // the user is arranging things now; don't snap back to the saved layout
                    msgs.Post(a.Run); // run after returning: Windows drops hooks that take too long
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, msg, ref k);
        }

        void TogglePause()
        {
            wm.Paused = !wm.Paused;
            pauseItem.Checked = wm.Paused;
            if (!wm.Paused) wm.Sync();
        }

        protected override void ExitThreadCore()
        {
            wm.SaveLayout();
            wm.ClearBorder();
            foreach (var h in hooks) Native.UnhookWinEvent(h);
            Native.UnhookWindowsHookEx(keyHook);
            msgs.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
