using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Tessera
{
    // Keeps one TabStrip window per tab group in sync with the layout.
    class TabStrips
    {
        readonly Dictionary<Node, TabStrip> strips = new Dictionary<Node, TabStrip>();

        public void Update(List<KeyValuePair<Node, Rectangle>> groups, Func<Node, IntPtr> activeOf)
        {
            var alive = new HashSet<Node>();
            foreach (var g in groups)
            {
                alive.Add(g.Key);
                TabStrip strip;
                if (!strips.TryGetValue(g.Key, out strip)) strips[g.Key] = strip = new TabStrip();
                strip.Set(g.Value, g.Key.Children.Select(c => c.Hwnd).ToList(), activeOf(g.Key));
            }
            foreach (var n in strips.Keys.Where(n => !alive.Contains(n)).ToList())
            {
                strips[n].Dispose();
                strips.Remove(n);
            }
        }
    }

    // The bar above a tab group. Click a tab to show that window, middle-click to close it.
    class TabStrip : Form
    {
        static readonly Color Back = Color.FromArgb(32, 32, 32), Active = Color.FromArgb(0, 120, 215), Fore = Color.FromArgb(235, 235, 235);
        List<IntPtr> tabs = new List<IntPtr>();
        List<string> titles = new List<string>();
        IntPtr active;
        Font font;

        public TabStrip()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Back;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000; // tool window, no activate
                return cp;
            }
        }

        public void Set(Rectangle bounds, List<IntPtr> hwnds, IntPtr activeTab)
        {
            var newTitles = hwnds.Select(h => Native.Title(h)).ToList();
            if (Visible && bounds == Bounds && activeTab == active && hwnds.SequenceEqual(tabs) && newTitles.SequenceEqual(titles)) return;
            tabs = hwnds;
            titles = newTitles;
            active = activeTab;
            if (font == null || bounds.Height != Height)
            {
                if (font != null) font.Dispose();
                font = new Font("Segoe UI", bounds.Height * 0.42f, GraphicsUnit.Pixel);
            }
            Bounds = bounds;
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (tabs.Count == 0) return;
            using (var activeBrush = new SolidBrush(Active))
            using (var line = new Pen(Color.FromArgb(70, 70, 70)))
                for (int i = 0; i < tabs.Count; i++)
                {
                    var r = Rectangle.FromLTRB(Width * i / tabs.Count, 0, Width * (i + 1) / tabs.Count, Height);
                    if (tabs[i] == active) e.Graphics.FillRectangle(activeBrush, r);
                    else if (i > 0) e.Graphics.DrawLine(line, r.Left, 4, r.Left, Height - 4);
                    TextRenderer.DrawText(e.Graphics, titles[i], font, Rectangle.Inflate(r, -8, 0), Fore,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (tabs.Count == 0) return;
            var h = tabs[Math.Min(tabs.Count - 1, e.X * tabs.Count / Math.Max(1, Width))];
            if (e.Button == MouseButtons.Left) Native.Focus(h);
            else if (e.Button == MouseButtons.Middle) Native.PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && font != null) font.Dispose();
            base.Dispose(disposing);
        }
    }
}
