using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace Tessera
{
    // ignore.txt next to Tessera.exe: one rule per line, "process:name" or "class:WindowClass". Never tiled.
    static class Ignore
    {
        public static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ignore.txt");
        static HashSet<string> rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0];
            rules = new HashSet<string>(lines.Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
        }

        public static void Add(string rule)
        {
            File.AppendAllText(FilePath, rule + Environment.NewLine);
            Load();
        }

        public static bool Matches(IntPtr h)
        {
            if (rules.Count == 0) return false;
            return rules.Contains("class:" + Native.ClassName(h)) || rules.Contains("process:" + Native.ProcessName(Native.Pid(h)));
        }
    }

    class WM
    {
        const int Gap = 8;
        static readonly HashSet<string> SkipClasses = new HashSet<string> { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };
        static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;

        readonly Dictionary<string, Node> roots = new Dictionary<string, Node>(); // one tree per monitor
        readonly HashSet<IntPtr> floating = new HashSet<IntPtr>();
        Dictionary<IntPtr, Rectangle> layout = new Dictionary<IntPtr, Rectangle>();
        IntPtr lastFocus;
        public bool Paused;
        public IntPtr Dragging; // left alone by Apply while the user drags it
        readonly TabStrips strips = new TabStrips();
        IntPtr bordered;

        static readonly string OptionsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "options.json");
        public string Mode = LoadMode(); // one of Tree.Layouts
        readonly HashSet<string> handMade = new HashSet<string>(); // monitors arranged by hand: new windows don't re-run the layout
        bool? splitNext; // Win+H / Win+V: the next new window goes beside (false) / below (true) the focused one

        static readonly string LayoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.json");
        Dictionary<string, SavedNode> template; // the layout saved before the last exit/restart
        int savedCount, lastMatchCount = -1;
        DateTime restoreUntil;
        string lastSaved;
        bool sessionEnding;
        bool Restoring { get { return template != null && DateTime.Now < restoreUntil; } }

        // Only real, visible app windows. Invisible / cloaked / transparent / zero-size windows are the
        // "ghost apps" that would otherwise take a tile and show up as an empty area.
        public static bool IsTileable(IntPtr h)
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
            long style = Native.Style(h), ex = Native.ExStyle(h);
            if ((style & Native.WS_CHILD) != 0 || (style & Native.WS_THICKFRAME) == 0) return false;
            if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT)) != 0) return false;
            if ((ex & Native.WS_EX_LAYERED) != 0)
            {
                uint key, flags; byte alpha;
                if (Native.GetLayeredWindowAttributes(h, out key, out alpha, out flags) && (flags & Native.LWA_ALPHA) != 0 && alpha == 0) return false;
            }
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r) || r.Right <= r.Left || r.Bottom <= r.Top) return false;
            if (Native.MonitorFromWindow(h, 0 /* null if off-screen */) == IntPtr.Zero) return false;
            if (Native.Title(h).Length == 0 || SkipClasses.Contains(Native.ClassName(h))) return false;
            return Native.Pid(h) != MyPid && !Ignore.Matches(h);
        }

        Node FindLeaf(IntPtr h)
        {
            foreach (var root in roots.Values)
            {
                var n = Tree.Find(root, h);
                if (n != null) return n;
            }
            return null;
        }

        public void NoteFocus(IntPtr h)
        {
            var n = FindLeaf(h);
            if (n == null) return;
            lastFocus = h;
            if (n.Parent.Stacked) n.Parent.ActiveTab = h;
        }

        IntPtr ActiveOf(Node group)
        {
            foreach (var c in group.Children)
                if (c.Hwnd == group.ActiveTab) return c.Hwnd;
            return group.Children[0].Hwnd;
        }

        // False for tabs hidden behind another tab of their group.
        bool Shown(IntPtr h)
        {
            var n = FindLeaf(h);
            return n != null && (!n.Parent.Stacked || ActiveOf(n.Parent) == h);
        }

        // Diff the tree against the windows that exist right now, then re-place everything.
        public void Sync()
        {
            if (Paused) return;
            var displays = Native.Displays();
            foreach (var name in roots.Keys.ToList())
                if (!displays.Any(d => d.Name == name)) roots.Remove(name); // its windows get re-added below
            foreach (var d in displays)
                if (!roots.ContainsKey(d.Name)) roots[d.Name] = new Node();

            var live = new List<IntPtr>();
            Native.EnumWindows((h, l) => { if (IsTileable(h)) live.Add(h); return true; }, IntPtr.Zero);
            var liveSet = new HashSet<IntPtr>(live);
            floating.RemoveWhere(h => !Native.IsWindow(h));

            var changed = new HashSet<string>(); // monitors that gained or lost a window
            foreach (var kv in roots)
            {
                var leaves = new List<Node>();
                Tree.Leaves(kv.Value, leaves);
                foreach (var leaf in leaves)
                    if (!liveSet.Contains(leaf.Hwnd) || floating.Contains(leaf.Hwnd)) { Tree.Remove(leaf); changed.Add(kv.Key); }
            }

            if (Restoring)
            {
                var info = live.Where(h => !floating.Contains(h))
                    .ToDictionary(h => h, h => new[] { Native.ProcessName(Native.Pid(h)), Native.ClassName(h), Native.Title(h) });
                var match = SavedLayout.Match(template.Values, info);
                if (match.Count != lastMatchCount) // another saved app showed up: lay everything out as saved
                {
                    lastMatchCount = match.Count;
                    foreach (var d in displays)
                    {
                        SavedNode s;
                        roots[d.Name] = template.TryGetValue(d.Name, out s) ? SavedLayout.Build(s, match, true) : new Node();
                        if (s != null && s.Manual) handMade.Add(d.Name);
                    }
                }
                if (match.Count == savedCount) StopRestoring(); // everything is back
            }

            NoteFocus(Native.GetForegroundWindow());
            foreach (var h in live)
            {
                if (floating.Contains(h) || FindLeaf(h) != null) continue;
                string name = Native.DisplayOf(h).Name;
                Node root;
                if (!roots.TryGetValue(name, out root)) continue;
                var at = Tree.Find(root, lastFocus); // the focused window, if it's on this monitor
                if (at == null || at.Parent.Stacked) Tree.Insert(root, h, at); // opened from a tab group: becomes a tab
                else if (splitNext.HasValue || !Auto(name))
                {
                    Rectangle r;
                    bool below = splitNext ?? (layout.TryGetValue(at.Hwnd, out r) && r.Height > r.Width); // split the long side
                    Tree.SplitInsert(at, h, below);
                    if (splitNext.HasValue) handMade.Add(name);
                    splitNext = null;
                }
                else Tree.Insert(root, h, at); // right after the focused window; the layout below decides where that is
                changed.Add(name);
            }
            foreach (var name in changed)
                if (Auto(name)) roots[name] = Tree.Arrange(roots[name], Mode, false);
            foreach (var kv in roots)
            {
                var leaves = new List<Node>();
                Tree.Leaves(kv.Value, leaves);
                if (leaves.Count <= 1) handMade.Remove(kv.Key); // nothing left to keep: back to the automatic layout
            }
            NoteFocus(Native.GetForegroundWindow()); // a new window opened in a tab group becomes its active tab
            Apply();
        }

        bool Auto(string monitor) { return Mode != "manual" && !handMade.Contains(monitor); }

        string MonitorOf(IntPtr h) { return roots.First(kv => Tree.Find(kv.Value, h) != null).Key; }

        // The user arranged this window's monitor by hand: keep it as it is from now on.
        void Arranged(IntPtr h) { if (FindLeaf(h) != null) handMade.Add(MonitorOf(h)); }

        static string LoadMode()
        {
            try
            {
                var o = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(OptionsPath));
                string m;
                if (o != null && o.TryGetValue("layout", out m) && Tree.Layouts.Contains(m)) return m;
            }
            catch (Exception) { } // missing or unreadable: the default
            return "grid";
        }

        // Picks the layout for new windows and lays every monitor out in it now (manual keeps what's there).
        public bool SetLayout(string mode)
        {
            if (!Tree.Layouts.Contains(mode)) return false;
            Mode = mode;
            try { File.WriteAllText(OptionsPath, new JavaScriptSerializer().Serialize(new Dictionary<string, string> { { "layout", mode } })); }
            catch (IOException) { } // still applies until Tessera restarts
            handMade.Clear();
            splitNext = null;
            StopRestoring();
            if (mode != "manual")
                foreach (var name in roots.Keys.ToList()) roots[name] = Tree.Arrange(roots[name], mode, true);
            NoteFocus(Native.GetForegroundWindow());
            Apply();
            return true;
        }

        public void NextLayout()
        {
            SetLayout(Tree.Layouts[(Array.IndexOf(Tree.Layouts, Mode) + 1) % Tree.Layouts.Length]);
        }

        void Apply()
        {
            var next = new Dictionary<IntPtr, Rectangle>();
            var groups = new List<KeyValuePair<Node, Rectangle>>();
            foreach (var d in Native.Displays())
            {
                Node root;
                if (roots.TryGetValue(d.Name, out root))
                    Tree.Layout(root, Rectangle.Inflate(d.Work, -Gap / 2, -Gap / 2), Gap, next, 30 * d.Dpi / 96, groups);
            }
            layout = next;
            foreach (var kv in layout)
                if (kv.Key != Dragging) Place(kv.Key, kv.Value);
            // tabs share one spot: tuck the inactive ones right under the active one (never raises anything)
            foreach (var g in groups)
            {
                var top = ActiveOf(g.Key);
                foreach (var c in g.Key.Children)
                    if (c.Hwnd != top && c.Hwnd != Dragging)
                        Native.SetWindowPos(c.Hwnd, top, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            strips.Update(groups, ActiveOf);
            Border(Native.GetForegroundWindow());
        }

        // Windows 11 window-border color on the focused managed window; the previous one goes back to normal.
        void Border(IntPtr fg)
        {
            if (fg != IntPtr.Zero && FindLeaf(fg) == null && !floating.Contains(fg)) fg = IntPtr.Zero;
            if (fg == bordered) return;
            if (bordered != IntPtr.Zero) Native.SetBorderColor(bordered, 0xFFFFFFFF /* system default */);
            if (fg != IntPtr.Zero) Native.SetBorderColor(fg, 0x00D77800 /* blue, 0x00BBGGRR */);
            bordered = fg;
        }

        public void ClearBorder() { Border(IntPtr.Zero); }

        // Read layout.json; for the next 2 minutes, windows that were saved go back to their saved spot as they open.
        public void StartRestore()
        {
            try
            {
                if (File.Exists(LayoutPath))
                    template = new JavaScriptSerializer().Deserialize<Dictionary<string, SavedNode>>(File.ReadAllText(LayoutPath));
            }
            catch (Exception) { template = null; } // unreadable file: just start fresh
            if (template == null) return;
            var all = new List<SavedNode>();
            foreach (var s in template.Values) SavedLayout.Windows(s, all);
            savedCount = all.Count;
            restoreUntil = DateTime.Now.AddMinutes(2);
        }

        // The user changed the layout (or everything is restored): from now on the current layout is what gets saved.
        public void StopRestoring() { restoreUntil = DateTime.MinValue; }

        // Windows is shutting down: save now, then stop saving so apps closing don't overwrite the layout.
        public void SessionEnding()
        {
            SaveLayout();
            sessionEnding = true;
        }

        public void SaveLayout()
        {
            if (Restoring || sessionEnding || layout.Count == 0) return; // never overwrite the saved layout with an empty one
            var data = roots.ToDictionary(kv => kv.Key, kv => { var s = Save(kv.Value); s.Manual = handMade.Contains(kv.Key); return s; });
            var json = new JavaScriptSerializer().Serialize(data);
            if (json == lastSaved) return;
            try
            {
                File.WriteAllText(LayoutPath, json);
                lastSaved = json;
            }
            catch (IOException) { } // try again next time
        }

        static SavedNode Save(Node n)
        {
            var s = new SavedNode { Weight = n.Weight, Vertical = n.Vertical };
            if (!n.IsLeaf)
            {
                s.Kind = n.Stacked ? "tabs" : "split";
                s.Children = n.Children.Select(Save).ToList();
                return s;
            }
            s.Kind = "window";
            s.Hwnd = n.Hwnd.ToInt64();
            s.Process = Native.ProcessName(Native.Pid(n.Hwnd));
            s.Class = Native.ClassName(n.Hwnd);
            s.Title = Native.Title(n.Hwnd);
            s.Active = n.Parent != null && n.Parent.Stacked && n.Parent.ActiveTab == n.Hwnd;
            return s;
        }

        // Win+Alt+E: every tile back to an equal share.
        public void Equalize()
        {
            foreach (var root in roots.Values) Tree.Equalize(root);
            Apply();
        }

        // Win+Alt+Shift+Arrow: send the focused window to the monitor in that direction.
        public void MoveToMonitor(int dir)
        {
            var cur = Focused();
            if (cur == IntPtr.Zero || Paused) return;
            var here = roots.First(kv => Tree.Find(kv.Value, cur) != null).Key;
            var others = Native.Displays().Where(d => d.Name != here).ToList();
            int i = Nearest(layout[cur], dir, others.Select(d => d.Work).ToList());
            if (i >= 0) MoveTo(cur, others[i].Name);
        }

        // Moves tiled window h to the end of another monitor's tiles (also: dropped there with the mouse).
        public void MoveTo(IntPtr h, string monitor)
        {
            var n = FindLeaf(h);
            Node root;
            if (n != null && roots.TryGetValue(monitor, out root))
            {
                string here = MonitorOf(h);
                Tree.Remove(n);
                Tree.Insert(root, h, null);
                foreach (var name in new[] { here, monitor }.Distinct())
                    if (Auto(name)) roots[name] = Tree.Arrange(roots[name], Mode, false);
            }
            Apply();
        }

        public bool IsTiled(IntPtr h) { return FindLeaf(h) != null; }

        // Container p's current size in pixels along its split axis.
        int ContainerLength(Node p)
        {
            var leaves = new List<Node>();
            Tree.Leaves(p, leaves);
            var b = Rectangle.Empty;
            foreach (var leaf in leaves)
            {
                Rectangle r;
                if (layout.TryGetValue(leaf.Hwnd, out r)) b = b.IsEmpty ? r : Rectangle.Union(b, r);
            }
            return p.Vertical ? b.Height : b.Width;
        }

        // dir: 0 narrower, 1 shorter, 2 wider, 3 taller
        public void ResizeFocused(int dir)
        {
            var n = FindLeaf(Focused());
            if (n == null || Paused) return;
            bool vertical = dir % 2 == 1, grow = dir >= 2;
            var work = Native.DisplayOf(n.Hwnd).Work;
            int step = (vertical ? work.Height : work.Width) / 20;
            // move the right/bottom edge; at the screen edge use the left/top edge instead
            if (!Tree.MoveEdge(n, vertical ? 3 : 2, grow ? step : -step, ContainerLength))
                Tree.MoveEdge(n, vertical ? 1 : 0, grow ? -step : step, ContainerLength);
            Apply();
        }

        // The user resized a tiled window with the mouse: move the tile edges to match.
        // 'was' is where the window really was (an app's minimum size can make it differ from its tile).
        public void Resized(IntPtr h, Rectangle was, Rectangle now)
        {
            var n = FindLeaf(h);
            if (n != null)
            {
                int[] delta = { now.Left - was.Left, now.Top - was.Top, now.Right - was.Right, now.Bottom - was.Bottom };
                for (int side = 0; side < 4; side++)
                    if (Math.Abs(delta[side]) > 2) Tree.MoveEdge(n, side, delta[side], ContainerLength);
            }
            Apply();
        }

        // Tiles the dragged window can be dropped on.
        public List<KeyValuePair<IntPtr, Rectangle>> Targets(IntPtr dragged) { return layout.Where(kv => kv.Key != dragged && Shown(kv.Key)).ToList(); }

        // zone: 0 left, 1 top, 2 right, 3 bottom of the target tile, 4 center = swap, 5 = add as a tab
        public void Drop(IntPtr dragged, IntPtr target, int zone)
        {
            var a = FindLeaf(dragged);
            var b = FindLeaf(target);
            if (a != null && b != null && a != b)
            {
                if (zone == 4) Swap(a, b);
                else
                {
                    string from = MonitorOf(dragged), to = MonitorOf(target);
                    if (zone != 5) Arranged(target); // a tab group is one tile in any layout; an edge drop is arranging by hand
                    Tree.Remove(a);
                    if (zone == 5) { Tree.AddTab(b, dragged); b.Parent.ActiveTab = dragged; }
                    else Tree.InsertBeside(b.Parent.Stacked ? b.Parent : b, dragged, zone); // beside the whole tab group
                    foreach (var name in new[] { from, to }.Distinct())
                        if (Auto(name)) roots[name] = Tree.Arrange(roots[name], Mode, false);
                }
            }
            Apply();
        }

        static void Place(IntPtr h, Rectangle r)
        {
            if (Native.IsZoomed(h)) return; // maximized on purpose: leave it; restoring puts it back in its tile
            Native.RECT wr, fr;
            if (!Native.GetWindowRect(h, out wr)) return;
            // Windows 10/11 add an invisible resize border; grow the target so the visible frame fills the tile
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out fr, Marshal.SizeOf(typeof(Native.RECT))) == 0)
                r = Rectangle.FromLTRB(r.Left - (fr.Left - wr.Left), r.Top - (fr.Top - wr.Top), r.Right + (wr.Right - fr.Right), r.Bottom + (wr.Bottom - fr.Bottom));
            if (wr.Left == r.Left && wr.Top == r.Top && wr.Right == r.Right && wr.Bottom == r.Bottom) return;
            Native.SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        IntPtr Focused()
        {
            var fg = Native.GetForegroundWindow();
            if (layout.ContainsKey(fg)) return fg;
            return layout.ContainsKey(lastFocus) ? lastFocus : IntPtr.Zero;
        }

        // Index of the rect nearest to 'from' in direction dir (0 left, 1 up, 2 right, 3 down), or -1.
        static int Nearest(Rectangle from, int dir, List<Rectangle> rects)
        {
            int best = -1, bestScore = int.MaxValue;
            int cx = from.X + from.Width / 2, cy = from.Y + from.Height / 2;
            for (int i = 0; i < rects.Count; i++)
            {
                int dx = rects[i].X + rects[i].Width / 2 - cx, dy = rects[i].Y + rects[i].Height / 2 - cy;
                int along = dir == 0 ? -dx : dir == 2 ? dx : dir == 1 ? -dy : dy;
                int across = dir % 2 == 0 ? Math.Abs(dy) : Math.Abs(dx);
                if (along <= 0) continue;
                int score = along + 2 * across;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        // Nearest tiled window in direction dir; with sameMonitor, only windows tiled on cur's monitor.
        IntPtr Neighbor(IntPtr cur, int dir, bool sameMonitor)
        {
            var leaves = new List<Node>();
            foreach (var root in roots.Values)
                if (!sameMonitor || Tree.Find(root, cur) != null) Tree.Leaves(root, leaves);
            var others = leaves.Select(n => n.Hwnd).Where(h => h != cur && layout.ContainsKey(h) && Shown(h)).ToList();
            int i = Nearest(layout[cur], dir, others.Select(h => layout[h]).ToList());
            return i < 0 ? IntPtr.Zero : others[i];
        }

        public void FocusDir(int dir)
        {
            var cur = Focused();
            if (cur == IntPtr.Zero) return;
            var next = Neighbor(cur, dir, false);
            if (next != IntPtr.Zero) Native.Focus(next);
        }

        public void MoveDir(int dir)
        {
            var cur = Focused();
            if (cur == IntPtr.Zero || Paused) return;
            var next = Neighbor(cur, dir, true);
            if (next == IntPtr.Zero) return;
            Swap(FindLeaf(cur), FindLeaf(next));
            Apply();
        }

        // Windows trade places; a window swapped into a tab group stays its visible tab.
        static void Swap(Node a, Node b)
        {
            var h = a.Hwnd;
            a.Hwnd = b.Hwnd;
            b.Hwnd = h;
            if (a.Parent.Stacked) a.Parent.ActiveTab = a.Hwnd;
            if (b.Parent.Stacked) b.Parent.ActiveTab = b.Hwnd;
        }

        // Win+S: turn the focused window into a tab group (new windows opened from it join as tabs),
        // or, if it's already in one, lay that group's windows out side by side again.
        public void ToggleStack()
        {
            var n = FindLeaf(Focused());
            if (n == null || Paused) return;
            var p = n.Parent;
            if (p.Stacked) p.Stacked = false;
            else if (p.Parent != null && p.Children.Count == 1) p.Stacked = true;
            else Tree.Wrap(n).Stacked = true;
            n.Parent.ActiveTab = n.Hwnd;
            string m = MonitorOf(n.Hwnd);
            if (Auto(m)) roots[m] = Tree.Arrange(roots[m], Mode, false); // a group is one tile; split up, each window gets its own
            Apply();
        }

        // Show the next (+1) or previous (-1) tab of the focused window's group.
        public void NextTab(int step)
        {
            var n = FindLeaf(Focused());
            if (n == null || !n.Parent.Stacked) return;
            var tabs = n.Parent.Children;
            Native.Focus(tabs[(tabs.IndexOf(n) + step + tabs.Count) % tabs.Count].Hwnd);
        }

        public void ToggleFloat() { ToggleFloat(Native.GetForegroundWindow()); }

        public void ToggleFloat(IntPtr h)
        {
            if (floating.Remove(h)) { Sync(); return; }
            if (FindLeaf(h) == null) return;
            floating.Add(h);
            Sync();
            // centered and on top, so it's obvious the window left the tiling
            var w = Native.DisplayOf(h).Work;
            Native.SetWindowPos(h, IntPtr.Zero, w.X + w.Width / 6, w.Y + w.Height / 6, w.Width * 2 / 3, w.Height * 2 / 3, Native.SWP_NOACTIVATE);
        }

        // Win+H / Win+V: only the next window that opens goes beside / below the focused one (inside tabs it becomes a tab).
        public void SplitFocused(bool vertical) { splitNext = vertical; }

        public void FlipDirection()
        {
            var n = FindLeaf(Focused());
            if (n == null || n.Parent.Stacked) return;
            Arranged(n.Hwnd);
            n.Parent.Vertical = !n.Parent.Vertical;
            Apply();
        }

        // Every window Tessera manages, with its state, for the inspector.
        public List<KeyValuePair<IntPtr, string>> Tracked()
        {
            var result = new List<KeyValuePair<IntPtr, string>>();
            foreach (var root in roots.Values)
            {
                var leaves = new List<Node>();
                Tree.Leaves(root, leaves);
                foreach (var leaf in leaves) result.Add(new KeyValuePair<IntPtr, string>(leaf.Hwnd, Native.IsZoomed(leaf.Hwnd) ? "Maximized" : leaf.Parent.Stacked ? "Tab" : "Tiled"));
            }
            foreach (var h in floating) result.Add(new KeyValuePair<IntPtr, string>(h, "Floating"));
            return result;
        }

        public void Reload()
        {
            StopRestoring();
            Ignore.Load();
            roots.Clear();
            handMade.Clear();
            splitNext = null;
            floating.Clear();
            layout.Clear();
            Sync();
        }
    }
}
