using System;
using System.Collections.Generic;

namespace Tessera
{
    // One node of the saved layout (layout.json). Window handles change after a restart,
    // so windows are found again by program, window class and title.
    class SavedNode
    {
        public string Kind; // "split", "tabs" or "window"
        public bool Vertical, Active; // Active: the visible tab of its group
        public double Weight = 1;
        public long Hwnd;
        public string Process, Class, Title;
        public List<SavedNode> Children;
    }

    static class SavedLayout
    {
        public static void Windows(SavedNode s, List<SavedNode> result)
        {
            if (s.Kind == "window") result.Add(s);
            else if (s.Children != null) foreach (var c in s.Children) Windows(c, result);
        }

        // live: window -> { process, class, title }. Exact matches first (same handle or same title),
        // then any remaining window of the same program and class.
        public static Dictionary<SavedNode, IntPtr> Match(IEnumerable<SavedNode> roots, Dictionary<IntPtr, string[]> live)
        {
            var saved = new List<SavedNode>();
            foreach (var r in roots) Windows(r, saved);
            var match = new Dictionary<SavedNode, IntPtr>();
            var used = new HashSet<IntPtr>();
            for (int pass = 0; pass < 2; pass++)
                foreach (var s in saved)
                {
                    if (match.ContainsKey(s)) continue;
                    foreach (var kv in live)
                    {
                        if (used.Contains(kv.Key) || kv.Value[0] != s.Process || kv.Value[1] != s.Class) continue;
                        if (pass == 0 && kv.Key.ToInt64() != s.Hwnd && kv.Value[2] != s.Title) continue;
                        match[s] = kv.Key;
                        used.Add(kv.Key);
                        break;
                    }
                }
            return match;
        }

        // The saved tree with only the windows that were found; empty groups vanish, one-child groups collapse.
        public static Node Build(SavedNode s, Dictionary<SavedNode, IntPtr> match, bool isRoot)
        {
            if (s.Kind == "window")
            {
                IntPtr h;
                return match.TryGetValue(s, out h) ? new Node { Hwnd = h, Weight = s.Weight } : null;
            }
            var n = new Node { Vertical = s.Vertical, Stacked = s.Kind == "tabs" && !isRoot, Weight = s.Weight };
            foreach (var cs in s.Children ?? new List<SavedNode>())
            {
                var c = Build(cs, match, false);
                if (c == null) continue;
                c.Parent = n;
                n.Children.Add(c);
                if (cs.Active) n.ActiveTab = c.Hwnd;
            }
            if (isRoot) return n;
            if (n.Children.Count == 0) return null;
            if (n.Children.Count == 1) { n.Children[0].Weight = n.Weight; return n.Children[0]; } // caller re-parents it
            return n;
        }
    }
}
