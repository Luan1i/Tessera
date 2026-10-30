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
        public IntPtr HoverTarget;
        public Rectangle HoverRect;
        public int HoverZone = -1;

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
            else if (moved && HoverZone >= 0) wm.Drop(h, HoverTarget, HoverZone);
            else wm.Sync(); // snap back into its tile
        }

        void Update()
        {
            var r = Native.VisibleRect(dragged);
            // same size but new position = a move; a size change means the user is resizing from a border
            if (r.Size != startRect.Size || r.Location == startRect.Location) return;
            if (overlays.Count == 0)
                foreach (var d in Native.Displays())
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
            if (zone == HoverZone && target == HoverTarget) return;
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
            Opacity = 0.6;
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
            using (var border = new Pen(Accent, 3))
            using (var dashed = new Pen(Accent, 2) { DashStyle = DashStyle.Dash })
            using (var fill = new SolidBrush(Accent))
            using (var font = new Font("Segoe UI", 18f * DeviceDpi / 96, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                foreach (var t in drag.Targets)
                {
                    if (!Bounds.IntersectsWith(t.Value)) continue;
                    var r = t.Value;
                    g.DrawRectangle(border, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                    var swap = Rectangle.Inflate(r, -r.Width * 3 / 10, -r.Height * 3 / 10);
                    var tab = Tree.ZoneRect(r, 5);
                    g.DrawRectangle(dashed, swap);
                    g.DrawRectangle(dashed, tab);
                    TextRenderer.DrawText(g, "Swap", font, swap, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, "Tab", font, tab, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (drag.HoverZone >= 0 && Bounds.IntersectsWith(drag.HoverRect))
                    g.FillRectangle(fill, Rectangle.Inflate(Tree.ZoneRect(drag.HoverRect, drag.HoverZone), -6, -6));
            }
        }
    }
}
