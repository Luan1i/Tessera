using System;
using System.Collections.Generic;
using System.Drawing;

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
        public static void Layout(Node n, Rectangle r, int gap, Dictionary<IntPtr, Rectangle> result, int tabHeight = 0, List<KeyValuePair<Node, Rectangle>> bars = null)
        {
            if (n.IsLeaf) { result[n.Hwnd] = Rectangle.Inflate(r, -gap / 2, -gap / 2); return; }
            if (n.Stacked)
            {
                if (bars != null && n.Children.Count > 0)
                    bars.Add(new KeyValuePair<Node, Rectangle>(n, Rectangle.FromLTRB(r.Left + gap / 2, r.Top + gap / 2, r.Right - gap / 2, r.Top + gap / 2 + tabHeight)));
                var below = Rectangle.FromLTRB(r.Left, r.Top + tabHeight, r.Right, r.Bottom);
                foreach (var c in n.Children) Layout(c, below, gap, result, tabHeight, bars);
                return;
            }
            double total = 0, acc = 0;
            foreach (var c in n.Children) total += c.Weight;
            foreach (var c in n.Children)
            {
                double a = acc / total, b = (acc += c.Weight) / total;
                var part = n.Vertical
                    ? Rectangle.FromLTRB(r.Left, r.Top + (int)Math.Round(r.Height * a), r.Right, r.Top + (int)Math.Round(r.Height * b))
                    : Rectangle.FromLTRB(r.Left + (int)Math.Round(r.Width * a), r.Top, r.Left + (int)Math.Round(r.Width * b), r.Bottom);
                Layout(c, part, gap, result, tabHeight, bars);
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

            Console.WriteLine("selftest ok");
            return 0;
        }
    }
#endif
}
