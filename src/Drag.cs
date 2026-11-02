using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tessera
{
    // While a tiled window is dragged by its title bar, shows drop zones on the other tiles;
    // releasing over one swaps (center) or splits (edge). Esc or dropping elsewhere snaps it back.
    class DragController
    {
        readonly WM wm;
        readonly Timer timer = new Timer { Interval = 30 };
        readonly List<DropOverlay> overlays = new List<DropOverlay>();
        IntPtr dragged;
        Rectangle startRect;
        List<KeyValuePair<IntPtr, Rectangle>> targets;
        List<Display> displays;
        public Rectangle StartWork; // work area of the monitor the drag started on
        public IntPtr HoverTarget;
        public Rectangle HoverRect;
        public int HoverZone = -1; // Tree.ZoneAt zones, or MoveZone
        public const int MoveZone = 6; // another monitor, not over a tile: the window joins that monitor
        string hoverMonitor;

        public DragController(WM wm)
        {
            this.wm = wm;
            timer.Tick += delegate { Update(); };
        }

        public List<KeyValuePair<IntPtr, Rectangle>> Targets { get { return targets; } }

        public void Start(IntPtr h)
        {
            if (wm.Paused || !wm.IsTiled(h) || Native.IsZoomed(h)) return;
            wm.StopRestoring();
            dragged = h;
            wm.Dragging = h;
            startRect = Native.VisibleRect(h);
            targets = wm.Targets(h);
            displays = Native.Displays();
            StartWork = Native.DisplayOf(h).Work;
            HoverZone = -1;
            timer.Start();
        }

        public void End(IntPtr h)
        {
            if (h != dragged || dragged == IntPtr.Zero) return;
            Update();
            timer.Stop();
            foreach (var o in overlays) o.Dispose();
            overlays.Clear();
            var now = Native.VisibleRect(h);
            bool moved = now != startRect; // Esc restores the start position
            dragged = IntPtr.Zero;
            wm.Dragging = IntPtr.Zero;
            if (now.Size != startRect.Size) wm.Resized(h, startRect, now); // border drag: resize the tiles
            else if (moved && HoverZone == MoveZone) wm.MoveTo(h, hoverMonitor);
            else if (moved && HoverZone >= 0) wm.Drop(h, HoverTarget, HoverZone);
            else wm.Sync(); // snap back into its tile
        }

        void Update()
        {
            var r = Native.VisibleRect(dragged);
            // same size but new position = a move; a size change means the user is resizing from a border
            if (r.Size != startRect.Size || r.Location == startRect.Location) return;
            if (overlays.Count == 0)
                foreach (var d in displays)
                {
                    var o = new DropOverlay(this, d.Work);
                    overlays.Add(o);
                    o.Show();
                }

            var p = Cursor.Position;
            int zone = -1;
            IntPtr target = IntPtr.Zero;
            Rectangle rect = Rectangle.Empty;
            foreach (var t in targets)
                if (t.Value.Contains(p)) { target = t.Key; rect = t.Value; zone = Tree.ZoneAt(t.Value, p); }
            if (zone < 0)
                foreach (var d in displays)
                    if (d.Work.Contains(p) && d.Work != StartWork) { zone = MoveZone; rect = d.Work; hoverMonitor = d.Name; }
            if (zone == HoverZone && target == HoverTarget && rect == HoverRect) return;
            HoverZone = zone;
            HoverTarget = target;
            HoverRect = rect;
            foreach (var o in overlays) o.Invalidate();
        }
    }

    // One per monitor: transparent, click-through, always on top.
    class DropOverlay : Form
    {
        static readonly Color Accent = Color.FromArgb(0, 120, 215);
        readonly DragController drag;

        public DropOverlay(DragController drag, Rectangle area)
        {
            this.drag = drag;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = area;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta; // magenta pixels are see-through
            Opacity = 0.85;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // tool window, no activate, click-through
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TranslateTransform(-Left, -Top);
            float s = DeviceDpi / 96f;
            using (var border = new Pen(Accent, 5 * s))
            using (var thick = new Pen(Accent, 6 * s))
            using (var dashed = new Pen(Accent, 3 * s) { DashStyle = DashStyle.Dash })
            using (var fill = new SolidBrush(Accent))
            using (var font = new Font("Segoe UI", 18f * s, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                // where the window would land, drawn first so the labels stay readable on top of it
                if (drag.HoverZone >= 0 && drag.HoverZone != DragController.MoveZone && Bounds.IntersectsWith(drag.HoverRect))
                    g.FillRectangle(fill, Rectangle.Inflate(Tree.ZoneRect(drag.HoverRect, drag.HoverZone), -6, -6));
                foreach (var t in drag.Targets)
                {
                    if (!Bounds.IntersectsWith(t.Value)) continue;
                    var r = t.Value;
                    int b = (int)(3 * s);
                    g.DrawRectangle(border, r.X + b, r.Y + b, r.Width - 2 * b, r.Height - 2 * b);
                    var swap = Rectangle.Inflate(r, -r.Width * 3 / 10, -r.Height * 3 / 10);
                    var tab = Tree.ZoneRect(r, 5);
                    g.DrawRectangle(dashed, swap);
                    g.DrawRectangle(dashed, tab);
                    Label(g, "Swap", font, Center(swap), s);
                    Label(g, "Tab", font, Center(tab), s);
                    // the edge zones: the window goes on that side of this tile
                    int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                    Label(g, "← Left", font, new Point(r.X + r.Width * 15 / 100, cy), s);
                    Label(g, "Right →", font, new Point(r.X + r.Width * 85 / 100, cy), s);
                    Label(g, "↑ Above", font, new Point(cx, r.Y + r.Height * 25 / 100), s);
                    Label(g, "↓ Below", font, new Point(cx, r.Y + r.Height * 85 / 100), s);
                }
                // another monitor without tiles: say it can take the window
                var box = Rectangle.Inflate(Bounds, -Bounds.Width * 3 / 10, -Bounds.Height * 3 / 10);
                bool hovered = drag.HoverZone == DragController.MoveZone && drag.HoverRect == Bounds;
                if (hovered)
                {
                    g.DrawRectangle(thick, Left + 3, Top + 3, Width - 6, Height - 6);
                    g.FillRectangle(fill, box);
                    TextRenderer.DrawText(g, "Move here", font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else if (Bounds != drag.StartWork && !drag.Targets.Exists(t => Bounds.IntersectsWith(t.Value)))
                {
                    g.DrawRectangle(dashed, box);
                    Label(g, "Move here", font, Center(box), s);
                }
            }
        }

        static Point Center(Rectangle r) { return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

        // White text on a solid blue badge with a white rim, so it reads over any window and over the blue drop preview.
        // No anti-aliasing: soft edges would blend with the magenta transparency key.
        static void Label(Graphics g, string text, Font font, Point center, float s)
        {
            var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            int px = (int)(12 * s), py = (int)(6 * s), rim = Math.Max(2, (int)(2 * s));
            var box = new Rectangle(center.X - size.Width / 2 - px, center.Y - size.Height / 2 - py, size.Width + 2 * px, size.Height + 2 * py);
            g.FillRectangle(Brushes.White, Rectangle.Inflate(box, rim, rim));
            using (var fill = new SolidBrush(Accent)) g.FillRectangle(fill, box);
            TextRenderer.DrawText(g, text, font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
