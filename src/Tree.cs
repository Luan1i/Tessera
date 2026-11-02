using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Tessera
{
    // Leaf = one window. Container = a row (Vertical=false) or column (Vertical=true) of children,
    // or a tab group (Stacked=true): its windows share one spot under a tab strip. The root is never stacked.
    class Node
    {
        public IntPtr Hwnd;
        public bool Vertical;
        public bool Stacked;
        public IntPtr ActiveTab; // stacked only: the window shown on top
        public double Weight = 1; // share of the parent's space, relative to its siblings
        public Node Parent;
        public readonly List<Node> Children = new List<Node>();
        public bool IsLeaf { get { return Hwnd != IntPtr.Zero; } }
    }

    static class Tree
    {
        public static Node Find(Node n, IntPtr h)
        {
            if (n.IsLeaf) return n.Hwnd == h ? n : null;
            foreach (var c in n.Children)
            {
                var found = Find(c, h);
                if (found != null) return found;
            }
            return null;
        }

        public static void Leaves(Node n, List<Node> result)
        {
            if (n.IsLeaf) result.Add(n);
            foreach (var c in n.Children) Leaves(c, result);
        }

        // Adds window h right after leaf 'after', or at the end of root when after is null.
        public static void Insert(Node root, IntPtr h, Node after)
        {
            var parent = after != null ? after.Parent : root;
            int i = after != null ? parent.Children.IndexOf(after) + 1 : parent.Children.Count;
            parent.Children.Insert(i, new Node { Hwnd = h, Parent = parent });
        }

        public static void Remove(Node leaf)
        {
            var p = leaf.Parent;
            p.Children.Remove(leaf);
            leaf.Parent = null;
            // collapse containers left with 0 or 1 child; the root always stays
            while (p.Parent != null && p.Children.Count <= 1)
            {
                var gp = p.Parent;
                int i = gp.Children.IndexOf(p);
                gp.Children.RemoveAt(i);
                if (p.Children.Count == 1)
                {
                    p.Children[0].Parent = gp;
                    p.Children[0].Weight = p.Weight; // takes over the collapsed container's size
                    gp.Children.Insert(i, p.Children[0]);
                }
                p = gp;
            }
        }

        // The next window opened after 'leaf' goes below it (vertical) or beside it (horizontal).
        public static void Split(Node leaf, bool vertical)
        {
            var p = leaf.Parent;
            if (p.Children.Count == 1) { p.Vertical = vertical; return; }
            if (p.Vertical == vertical) return;
            Wrap(leaf).Vertical = vertical;
        }

        // Replaces n with a new container holding only n; returns the container.
        public static Node Wrap(Node n)
        {
            var p = n.Parent;
            var c = new Node { Parent = p, Weight = n.Weight };
            p.Children[p.Children.IndexOf(n)] = c;
            n.Parent = c;
            n.Weight = 1;
            c.Children.Add(n);
            return c;
        }

        // Makes h a tab next to target, turning target into a tab group if it isn't in one.
        public static void AddTab(Node target, IntPtr h)
        {
            var p = target.Parent;
            if (!p.Stacked)
            {
                if (p.Parent != null && p.Children.Count == 1) p.Stacked = true;
                else { p = Wrap(target); p.Stacked = true; }
            }
            p.Children.Insert(p.Children.IndexOf(target) + 1, new Node { Hwnd = h, Parent = p });
        }

        // Puts h after 'target' in a new split of target's tile (side by side or stacked), so it takes half that tile.
        public static void SplitInsert(Node target, IntPtr h, bool vertical)
        {
            var p = target.Parent.Children.Count == 1 ? target.Parent : Wrap(target);
            p.Vertical = vertical;
            p.Children.Insert(p.Children.IndexOf(target) + 1, new Node { Hwnd = h, Parent = p });
        }

        // Automatic layouts. "manual" means new windows split the focused tile and nothing is rearranged.
        public static readonly string[] Layouts = { "grid", "columns", "rows", "main-left", "main-right", "spiral", "tabs", "manual" };
        public static readonly string[] LayoutNames = { "Grid", "Columns", "Rows", "Main + stack", "Stack + main", "Spiral", "Tabs", "Manual" };

        // A fresh tree with root's windows in that layout, all shares equal, window order kept.
        // Tab groups stay one tile unless 'flatten' (picking a layout starts from scratch).
        public static Node Arrange(Node root, string layout, bool flatten)
        {
            var leaves = new List<Node>();
            Leaves(root, leaves);
            var units = new List<Node>();
            foreach (var leaf in leaves)
            {
                var u = leaf.Parent.Stacked && !flatten ? leaf.Parent : new Node { Hwnd = leaf.Hwnd };
                if (units.Count == 0 || units[units.Count - 1] != u) units.Add(u);
            }
            int n = units.Count;
            Node built = null;
            if (n == 0) return new Node();
            if (layout == "columns" || layout == "rows") built = Box(layout == "rows", units);
            else if ((layout == "main-left" || layout == "main-right") && n > 1)
            {
                var stack = Box(true, units.GetRange(1, n - 1));
                built = Box(false, layout == "main-left" ? new List<Node> { units[0], stack } : new List<Node> { stack, units[0] });
            }
            else if (layout == "spiral")
            {
                built = units[n - 1];
                for (int i = n - 2; i >= 0; i--) built = Box(i % 2 == 1, new List<Node> { units[i], built }); // beside, below, beside...
            }
            else if (layout == "tabs" && leaves.Count > 1)
            {
                built = new Node { Stacked = true };
                foreach (var leaf in leaves)
                {
                    built.Children.Add(new Node { Hwnd = leaf.Hwnd, Parent = built });
                    if (leaf.Parent.Stacked && built.ActiveTab == IntPtr.Zero) built.ActiveTab = leaf.Parent.ActiveTab;
                }
            }
            else if (n == 1) built = units[0];
            else // grid: columns filled top to bottom; with an odd count the first columns hold one window less (3 = 1 tall + 2)
            {
                int cols = (int)Math.Ceiling(Math.Sqrt(n)), start = 0;
                var columns = new List<Node>();
                for (int i = 0; i < cols; i++)
                {
                    int count = n / cols + (i >= cols - n % cols ? 1 : 0);
                    columns.Add(Box(true, units.GetRange(start, count)));
                    start += count;
                }
                built = Box(false, columns);
            }
            if (built.IsLeaf || built.Stacked) built = Box(false, new List<Node> { built }, true); // the root is a plain container
            built.Parent = null;
            built.Weight = 1;
            return built;
        }

        // A row (or column) of kids with equal shares; a single kid is returned as it is.
        static Node Box(bool vertical, List<Node> kids, bool always = false)
        {
            if (kids.Count == 1 && !always) return kids[0];
            var n = new Node { Vertical = vertical };
            foreach (var k in kids) { k.Parent = n; k.Weight = 1; n.Children.Add(k); }
            return n;
        }

        public static void Equalize(Node n)
        {
            n.Weight = 1;
            foreach (var c in n.Children) Equalize(c);
        }

        // Puts h on one side of 'target': zone 0 left, 1 top, 2 right, 3 bottom (same numbering as arrow directions).
        public static void InsertBeside(Node target, IntPtr h, int zone)
        {
            Split(target, zone % 2 == 1);
            var p = target.Parent;
            p.Children.Insert(p.Children.IndexOf(target) + (zone < 2 ? 0 : 1), new Node { Hwnd = h, Parent = p });
        }

        // Drop zone of tile r under point p: 0 left, 1 top, 2 right, 3 bottom, 4 center (swap), 5 tab box.
        public static int ZoneAt(Rectangle r, Point p)
        {
            double fx = (p.X - r.X) / (double)r.Width, fy = (p.Y - r.Y) / (double)r.Height;
            if (fx > 0.35 && fx < 0.65 && fy > 0.05 && fy < 0.2) return 5;
            if (fx > 0.3 && fx < 0.7 && fy > 0.3 && fy < 0.7) return 4;
            double[] edge = { fx, fy, 1 - fx, 1 - fy };
            int best = 0;
            for (int i = 1; i < 4; i++) if (edge[i] < edge[best]) best = i;
            return best;
        }

        // How far to move window w so it stays inside area (an app's minimum size can make it bigger than its tile).
        // Too big for the area: it lines up with the area's left / top edge.
        public static Point Inside(Rectangle w, Rectangle area)
        {
            int dx = w.Right > area.Right ? area.Right - w.Right : 0, dy = w.Bottom > area.Bottom ? area.Bottom - w.Bottom : 0;
            if (w.Left + dx < area.Left) dx = area.Left - w.Left;
            if (w.Top + dy < area.Top) dy = area.Top - w.Top;
            return new Point(dx, dy);
        }

        // Roughly where the dropped window lands inside tile r, for the preview.
        public static Rectangle ZoneRect(Rectangle r, int zone)
        {
            switch (zone)
            {
                case 0: return new Rectangle(r.X, r.Y, r.Width / 2, r.Height);
                case 1: return new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
                case 2: return new Rectangle(r.X + r.Width / 2, r.Y, r.Width - r.Width / 2, r.Height);
                case 3: return new Rectangle(r.X, r.Y + r.Height / 2, r.Width, r.Height - r.Height / 2);
                case 5: return new Rectangle(r.X + r.Width * 35 / 100, r.Y + r.Height * 5 / 100, r.Width * 30 / 100, r.Height * 15 / 100);
                default: return r;
            }
        }

        // bars (optional) receives each tab group and where its tab strip goes.
        // min (optional) gives a window's smallest size (Size.Empty or 0 = unknown); tiles get at least that when there's room.
        public static void Layout(Node n, Rectangle r, int gap, Dictionary<IntPtr, Rectangle> result, int tabHeight = 0,
            List<KeyValuePair<Node, Rectangle>> bars = null, Func<IntPtr, Size> min = null)
        {
            if (n.IsLeaf) { result[n.Hwnd] = Rectangle.Inflate(r, -gap / 2, -gap / 2); return; }
            if (n.Stacked)
            {
                if (bars != null && n.Children.Count > 0)
                    bars.Add(new KeyValuePair<Node, Rectangle>(n, Rectangle.FromLTRB(r.Left + gap / 2, r.Top + gap / 2, r.Right - gap / 2, r.Top + gap / 2 + tabHeight)));
                var below = Rectangle.FromLTRB(r.Left, r.Top + tabHeight, r.Right, r.Bottom);
                foreach (var c in n.Children) Layout(c, below, gap, result, tabHeight, bars, min);
                return;
            }
            var sizes = Shares(n.Children.Select(c => c.Weight).ToArray(),
                n.Children.Select(c => min == null ? 0 : n.Vertical ? MinSize(c, gap, tabHeight, min).Height : MinSize(c, gap, tabHeight, min).Width).ToArray(),
                n.Vertical ? r.Height : r.Width);
            double acc = 0;
            for (int i = 0; i < sizes.Length; i++)
            {
                int a = (int)Math.Round(acc), b = (int)Math.Round(acc += sizes[i]);
                var part = n.Vertical ? Rectangle.FromLTRB(r.Left, r.Top + a, r.Right, r.Top + b) : Rectangle.FromLTRB(r.Left + a, r.Top, r.Left + b, r.Bottom);
                Layout(n.Children[i], part, gap, result, tabHeight, bars, min);
            }
        }

        // Splits length by weight, but nobody gets less than their minimum (when all the minimums fit).
        public static double[] Shares(double[] weight, int[] min, int length)
        {
            int n = weight.Length;
            var size = new double[n];
            var held = new bool[n]; // given exactly its minimum
            Func<double> left = () => length - Enumerable.Range(0, n).Where(i => held[i]).Sum(i => (double)min[i]);
            Func<double> rest = () => Enumerable.Range(0, n).Where(i => !held[i]).Sum(i => weight[i]);
            if (min.Sum() <= length) // otherwise they can't all be honoured: plain weights
                for (bool again = true; again && rest() > 0; )
                {
                    again = false;
                    double free = left(), w = rest();
                    for (int i = 0; i < n; i++)
                        if (!held[i] && free * weight[i] / w < min[i]) { held[i] = true; again = true; }
                }
            double l = left(), r = rest(), all = weight.Sum();
            for (int i = 0; i < n; i++)
                size[i] = !held[i] ? l * weight[i] / r : r > 0 ? min[i] : min[i] + l * weight[i] / all; // all held: spare room by weight
            return size;
        }

        // Smallest room n needs: its windows' minimums plus gaps and tab strips (unknown minimums count as 0).
        public static Size MinSize(Node n, int gap, int tabHeight, Func<IntPtr, Size> min)
        {
            if (n.IsLeaf)
            {
                var m = min(n.Hwnd);
                return new Size(m.Width > 0 ? m.Width + gap : 0, m.Height > 0 ? m.Height + gap : 0);
            }
            int w = 0, h = 0;
            foreach (var c in n.Children)
            {
                var s = MinSize(c, gap, tabHeight, min);
                if (n.Stacked) { w = Math.Max(w, s.Width); h = Math.Max(h, s.Height); }
                else if (n.Vertical) { w = Math.Max(w, s.Width); h += s.Height; }
                else { w += s.Width; h = Math.Max(h, s.Height); }
            }
            return n.Stacked && h > 0 ? new Size(w, h + tabHeight) : new Size(w, h);
        }

        // A copy of the tree (same windows, new nodes), to try a change on without touching the real one.
        public static Node Clone(Node n)
        {
            var c = new Node { Hwnd = n.Hwnd, Vertical = n.Vertical, Stacked = n.Stacked, ActiveTab = n.ActiveTab, Weight = n.Weight };
            foreach (var k in n.Children) { var kc = Clone(k); kc.Parent = c; c.Children.Add(kc); }
            return c;
        }

        // Takes window h out of its tab group and puts it right after the group, as a tile of its own.
        public static void Untab(Node root, IntPtr h)
        {
            var leaf = Find(root, h);
            if (leaf == null || !leaf.Parent.Stacked) return;
            var group = leaf.Parent;
            if (group.Children.Count == 1) { group.Stacked = false; return; }
            var other = group.Children.First(c => c != leaf);
            Remove(leaf);
            var unit = other.Parent.Stacked ? other.Parent : other; // the group, or the window left when it collapsed
            var p = unit.Parent;
            p.Children.Insert(p.Children.IndexOf(unit) + 1, new Node { Hwnd = h, Parent = p });
        }

        // While root's windows can't fit in area even at their smallest, the last window becomes a tab of the one
        // before it, so windows never have to overlap. Returns true if anything changed; tabbed (optional) gets those windows.
        public static bool TabWhatDoesntFit(Node root, Size area, int gap, int tabHeight, Func<IntPtr, Size> min, HashSet<IntPtr> tabbed = null)
        {
            for (bool changed = false; ; changed = true)
            {
                var need = MinSize(root, gap, tabHeight, min);
                if (need.Width <= area.Width && need.Height <= area.Height) return changed;
                var leaves = new List<Node>();
                Leaves(root, leaves);
                int i = leaves.Count - 1;
                while (i > 0 && leaves[i].Parent.Stacked && leaves[i].Parent == leaves[i - 1].Parent) i--; // skip whole tab groups
                if (i <= 0) return changed; // already one tile: nothing more to do
                var h = leaves[i].Hwnd;
                Remove(leaves[i]);
                AddTab(leaves[i - 1], h);
                if (tabbed != null) tabbed.Add(h);
            }
        }

        // Moves the edge on 'side' (0 left, 1 top, 2 right, 3 bottom) of n's tile by delta pixels (+ = right/down),
        // resizing the neighbor on that side. length(p) is container p's size in pixels along its axis.
        // Returns false when that edge is the screen edge.
        public static bool MoveEdge(Node n, int side, int delta, Func<Node, int> length)
        {
            bool vertical = side % 2 == 1, after = side >= 2;
            for (; n.Parent != null; n = n.Parent)
            {
                var p = n.Parent;
                int i = p.Children.IndexOf(n), j = after ? i + 1 : i - 1;
                if (p.Stacked || p.Vertical != vertical || j < 0 || j >= p.Children.Count) continue; // tabs share one size
                int len = length(p);
                if (len <= 0) return false;
                double total = 0;
                foreach (var c in p.Children) total += c.Weight;
                Node first = p.Children[Math.Min(i, j)], second = p.Children[Math.Max(i, j)];
                double min = total * 0.1; // no tile smaller than 10% of its row/column
                double dw = Math.Max(min - first.Weight, Math.Min(second.Weight - min, delta * total / len));
                first.Weight += dw;
                second.Weight -= dw;
                return true;
            }
            return false;
        }
    }

#if SELFTEST
    static class TreeTest
    {
        static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }

        static Dictionary<IntPtr, Rectangle> Lay(Node root)
        {
            var r = new Dictionary<IntPtr, Rectangle>();
            Tree.Layout(root, new Rectangle(0, 0, 300, 100), 0, r);
            return r;
        }

        static int Main()
        {
            IntPtr a = (IntPtr)1, b = (IntPtr)2, c = (IntPtr)3, d = (IntPtr)4;
            var root = new Node();
            Tree.Insert(root, a, null);
            Tree.Insert(root, b, Tree.Find(root, a));
            Tree.Insert(root, c, Tree.Find(root, b));
            Check(Lay(root)[b] == new Rectangle(100, 0, 100, 100), "three equal columns");

            Tree.Split(Tree.Find(root, b), true);
            Tree.Insert(root, d, Tree.Find(root, b));
            var r = Lay(root);
            Check(r[b] == new Rectangle(100, 0, 100, 50) && r[d] == new Rectangle(100, 50, 100, 50), "b and d stacked");

            Tree.Remove(Tree.Find(root, b));
            Check(root.Children.Count == 3 && root.Children[1].Hwnd == d, "empty container collapsed");

            foreach (var h in new[] { a, c, d }) Tree.Remove(Tree.Find(root, h));
            Check(root.Children.Count == 0, "all removed");

            var g = new Dictionary<IntPtr, Rectangle>();
            Tree.Insert(root, a, null);
            Tree.Layout(root, new Rectangle(0, 0, 100, 100), 8, g);
            Check(g[a] == new Rectangle(4, 4, 92, 92), "gap applied");

            // drag-and-drop: a dropped on the bottom of b, then on the left of c
            root = new Node();
            Tree.Insert(root, a, null);
            Tree.Insert(root, b, Tree.Find(root, a));
            Tree.Insert(root, c, Tree.Find(root, b));
            Tree.Remove(Tree.Find(root, a));
            Tree.InsertBeside(Tree.Find(root, b), a, 3);
            r = Lay(root);
            Check(r[b] == new Rectangle(0, 0, 150, 50) && r[a] == new Rectangle(0, 50, 150, 50) && r[c] == new Rectangle(150, 0, 150, 100), "a below b");
            Tree.Remove(Tree.Find(root, a));
            Tree.InsertBeside(Tree.Find(root, c), a, 0);
            Check(root.Children.Count == 3 && root.Children[0].Hwnd == b && root.Children[1].Hwnd == a, "a left of c");

            var tile = new Rectangle(0, 0, 100, 100);
            Check(Tree.ZoneAt(tile, new Point(50, 50)) == 4 && Tree.ZoneAt(tile, new Point(5, 50)) == 0 && Tree.ZoneAt(tile, new Point(50, 95)) == 3, "zones");

            // resizing: root is [b, a, c], 300 px wide
            Func<Node, int> width = p => 300;
            Check(Tree.MoveEdge(Tree.Find(root, b), 2, 50, width), "b has a right neighbor");
            r = Lay(root);
            Check(r[b] == new Rectangle(0, 0, 150, 100) && r[a] == new Rectangle(150, 0, 50, 100) && r[c].Width == 100, "b wider, a narrower");
            Check(!Tree.MoveEdge(Tree.Find(root, b), 0, 50, width), "b's left edge is the screen edge");
            Tree.MoveEdge(Tree.Find(root, b), 2, 1000, width);
            Check(Lay(root)[a].Width == 30, "a stops at 10%");
            Tree.Remove(Tree.Find(root, b));
            Check(Lay(root)[a].Width + Lay(root)[c].Width == 300, "remaining tiles fill the space");

            // tabs: root [a, c]; c gets a tab d -> c and d share the right half under a 20 px strip
            Tree.AddTab(Tree.Find(root, c), d);
            var tabs = new List<KeyValuePair<Node, Rectangle>>();
            r = new Dictionary<IntPtr, Rectangle>();
            Tree.Layout(root, new Rectangle(0, 0, 300, 100), 0, r, 20, tabs);
            Check(tabs.Count == 1 && r[c] == r[d] && r[d].Top == 20 && r[d].Left == r[a].Right && tabs[0].Value.Height == 20, "c and d are tabs");
            Check(!Tree.MoveEdge(Tree.Find(root, d), 3, 10, width), "tabs don't resize against each other");
            Check(Tree.ZoneAt(tile, new Point(50, 10)) == 5, "tab zone");
            Tree.Remove(Tree.Find(root, d));
            Check(root.Children.Count == 2 && root.Children[1].Hwnd == c, "one-tab group collapsed");

            // automatic layouts, 300x100 screen
            IntPtr e = (IntPtr)5;
            root = new Node();
            foreach (var h in new[] { a, b, c, d }) Tree.Insert(root, h, null);
            r = Lay(Tree.Arrange(root, "grid", false));
            Check(r[a] == new Rectangle(0, 0, 150, 50) && r[b] == new Rectangle(0, 50, 150, 50) && r[c] == new Rectangle(150, 0, 150, 50) && r[d] == new Rectangle(150, 50, 150, 50), "grid 2x2, filled by columns");
            r = Lay(Tree.Arrange(root, "columns", false));
            Check(r[a].Width == 75 && r[d] == new Rectangle(225, 0, 75, 100), "4 equal columns");
            r = Lay(Tree.Arrange(root, "rows", false));
            Check(r[c] == new Rectangle(0, 50, 300, 25), "4 equal rows");
            Tree.Remove(Tree.Find(root, d));
            r = Lay(Tree.Arrange(root, "main-left", false));
            Check(r[a] == new Rectangle(0, 0, 150, 100) && r[b] == new Rectangle(150, 0, 150, 50) && r[c] == new Rectangle(150, 50, 150, 50), "50 + 25 + 25");
            r = Lay(Tree.Arrange(root, "main-right", false));
            Check(r[a] == new Rectangle(150, 0, 150, 100) && r[b] == new Rectangle(0, 0, 150, 50), "25 + 25 + 50");
            r = Lay(Tree.Arrange(root, "grid", false));
            Check(r[a] == new Rectangle(0, 0, 150, 100) && r[b] == new Rectangle(150, 0, 150, 50) && r[c] == new Rectangle(150, 50, 150, 50), "grid of 3: one tall on the left, two stacked");
            var five = new Node();
            foreach (var h in new[] { a, b, c, d, e }) Tree.Insert(five, h, null);
            r = Lay(Tree.Arrange(five, "grid", false));
            Check(r[a] == new Rectangle(0, 0, 100, 100) && r[c] == new Rectangle(100, 50, 100, 50) && r[e] == new Rectangle(200, 50, 100, 50), "grid of 5: 1 + 2 + 2");
            Tree.Insert(root, d, null);
            r = Lay(Tree.Arrange(root, "spiral", false));
            Check(r[a] == new Rectangle(0, 0, 150, 100) && r[b] == new Rectangle(150, 0, 150, 50) && r[c] == new Rectangle(150, 50, 75, 50) && r[d] == new Rectangle(225, 50, 75, 50), "spiral");
            var tabbed = Tree.Arrange(root, "tabs", false);
            r = Lay(tabbed);
            Check(r[a] == r[d] && r[a].Width == 300 && !tabbed.Stacked && tabbed.Children[0].Stacked, "everything in one tab group");
            Check(Lay(Tree.Arrange(tabbed, "columns", true))[b].Width == 75, "picking a layout breaks the group up");
            Tree.AddTab(Tree.Find(root, a), e);
            r = Lay(Tree.Arrange(root, "columns", false));
            Check(r[a] == r[e] && r[a].Width == 75, "a user's tab group stays one tile");
            root = new Node();
            Tree.Insert(root, a, null);
            Tree.Insert(root, b, Tree.Find(root, a));
            Tree.Insert(root, c, Tree.Find(root, b));
            Tree.SplitInsert(Tree.Find(root, c), d, true);
            r = Lay(root);
            Check(r[c] == new Rectangle(200, 0, 100, 50) && r[d] == new Rectangle(200, 50, 100, 50) && r[a].Width == 100, "next window halves the focused tile");

            // restore: saved [chrome "A" (wide) | tabs(code "x", code "y" active, notepad)]; notepad isn't open any more
            var w1 = new SavedNode { Kind = "window", Process = "chrome", Class = "C", Title = "A", Weight = 2 };
            var t1 = new SavedNode { Kind = "window", Process = "code", Class = "V", Title = "x" };
            var t2 = new SavedNode { Kind = "window", Process = "code", Class = "V", Title = "y", Active = true };
            var gone = new SavedNode { Kind = "window", Process = "notepad", Class = "N", Title = "n" };
            var saved = new SavedNode { Kind = "split", Children = new List<SavedNode> { w1, new SavedNode { Kind = "tabs", Children = new List<SavedNode> { t1, t2, gone } } } };
            var live = new Dictionary<IntPtr, string[]>
            {
                { (IntPtr)11, new[] { "code", "V", "y - title changed" } },
                { (IntPtr)12, new[] { "code", "V", "x" } },
                { (IntPtr)10, new[] { "chrome", "C", "A" } },
            };
            var m = SavedLayout.Match(new[] { saved }, live);
            var built = SavedLayout.Build(saved, m, true);
            Check(m.Count == 3 && built.Children[0].Hwnd == (IntPtr)10 && built.Children[0].Weight == 2, "chrome back with its size");
            var group = built.Children[1];
            Check(group.Stacked && group.Children.Count == 2 && group.Children[0].Hwnd == (IntPtr)12 && group.ActiveTab == (IntPtr)11, "tab group back, closed app skipped");

            // key bindings
            Check(KeyBindings.Parse("Win+Alt+E") == (KeyBindings.Win | KeyBindings.Alt | 'E'), "parse Win+Alt+E");
            Check(KeyBindings.Parse("Ctrl+Shift+[") == (KeyBindings.Ctrl | KeyBindings.Shift | 0xDB), "parse punctuation");
            Check(KeyBindings.Parse("") == 0 && KeyBindings.Parse("E") < 0 && KeyBindings.Parse("Win+L") < 0 && KeyBindings.Parse("Win+Nope") < 0, "reject bad keys");
            var acts = new List<KeyAction> { new KeyAction { Id = "x", Label = "X", Keys = "Win+X" }, new KeyAction { Id = "y", Label = "Y", Keys = "Win+Y" } };
            Check(KeyBindings.Apply(acts, new Dictionary<string, string> { { "y", "Win+X" } }) != null && acts[1].Keys == "Win+Y", "duplicate rejected, nothing changed");
            Check(KeyBindings.Apply(acts, new Dictionary<string, string> { { "x", "" }, { "y", "Win+X" } }) == null && acts[0].Keys == "" && acts[1].Keys == "Win+X", "move a binding");

            // a 750 px wide Brave in the last 480 px column of a 1920 px screen moves left instead of onto the next monitor
            var screen = new Rectangle(0, 0, 1920, 1080);
            Check(Tree.Inside(new Rectangle(1445, 0, 750, 1080), screen) == new Point(-275, 0), "kept on its monitor");
            Check(Tree.Inside(new Rectangle(10, 10, 100, 100), screen) == Point.Empty, "fits: not moved");
            Check(Tree.Inside(new Rectangle(500, -20, 2400, 100), screen) == new Point(-500, 20), "too wide: left edge wins");

            // minimum sizes: on 2560 px, Brave needs 700, Kontabo and GitHub Desktop 1024 each (2748 > 2560)
            IntPtr brave = (IntPtr)20, kontabo = (IntPtr)21, github = (IntPtr)22;
            var mins = new Dictionary<IntPtr, Size> { { brave, new Size(700, 0) }, { kontabo, new Size(1024, 0) }, { github, new Size(1024, 0) } };
            Func<IntPtr, Size> minOf = h => mins.ContainsKey(h) ? mins[h] : Size.Empty;
            var sh = Tree.Shares(new double[] { 1, 1, 1 }, new[] { 0, 1024, 0 }, 2560);
            Check(sh[1] == 1024 && Math.Abs(sh[0] - 768) < 0.01 && Math.Abs(sh[2] - 768) < 0.01, "a window gets its minimum, the others share the rest");
            sh = Tree.Shares(new double[] { 1, 1 }, new[] { 100, 100 }, 300);
            Check(sh[0] == 150 && sh[1] == 150, "minimums that fit change nothing");
            var row = new Node();
            foreach (var h in new[] { brave, kontabo }) Tree.Insert(row, h, null);
            r = new Dictionary<IntPtr, Rectangle>();
            Tree.Layout(row, new Rectangle(0, 0, 2560, 1400), 0, r, 30, null, minOf);
            Check(r[kontabo].Width == 1280 && r[brave].Width == 1280, "two fit: even halves");
            Tree.Insert(row, github, null);
            var made = new HashSet<IntPtr>();
            Check(Tree.TabWhatDoesntFit(row, new Size(2560, 1400), 0, 30, minOf, made) && made.SetEquals(new[] { github }), "three don't fit side by side");
            Check(row.Children.Count == 2 && row.Children[1].Stacked && row.Children[1].Children[1].Hwnd == github, "the last one became a tab of the one before");
            r = new Dictionary<IntPtr, Rectangle>();
            Tree.Layout(row, new Rectangle(0, 0, 2560, 1400), 0, r, 30, null, minOf);
            Check(r[brave].Right <= r[kontabo].Left && r[kontabo] == r[github] && r[kontabo].Width >= 1024, "no overlap any more");
            Check(!Tree.TabWhatDoesntFit(row, new Size(2560, 1400), 0, 30, minOf), "fits now: nothing changes");
            var copy = Tree.Clone(row);
            Tree.Untab(copy, github);
            Check(copy.Children.Count == 3 && copy.Children[2].Hwnd == github && row.Children.Count == 2, "untab: its own tile again, original untouched");
            Check(Tree.TabWhatDoesntFit(copy, new Size(2560, 1400), 0, 30, minOf), "still no room: tabbed again");
            copy = Tree.Clone(row);
            Tree.Untab(copy, github);
            mins[github] = new Size(500, 0); // 700 + 1024 + 500 fits in 2560
            Check(!Tree.TabWhatDoesntFit(copy, new Size(2560, 1400), 0, 30, minOf), "room again: stays its own tile");

            // app rules
            var rs = Rules.Clean(new[] { new Rule { App = " Spotify.exe ", Action = "float" }, new Rule { App = "spotify", Action = "float" },
                new Rule { App = "x", Action = "nope" }, new Rule { App = "d", Action = "monitor" }, new Rule { App = "d", Action = "monitor", Monitor = "M2" }, null });
            Check(rs.Count == 2 && rs[0].App == "Spotify" && rs[1].Monitor == "M2", "rules cleaned: .exe dropped, repeats, bad actions and monitorless rules skipped");

            Console.WriteLine("selftest ok");
            return 0;
        }
    }
#endif
}
