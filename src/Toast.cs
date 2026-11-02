using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tessera
{
    // A short note ("Workspace 2") in the middle of a monitor. Never takes focus, can't be clicked, gone after a moment.
    class Toast : Form
    {
        static Toast shown;
        readonly Timer timer = new Timer { Interval = 900 };
        readonly Font font;
        readonly string text;

        public static void Flash(string text, Display d)
        {
            if (shown != null) shown.Close();
            shown = new Toast(text, d);
            shown.Show();
        }

        Toast(string text, Display d)
        {
            this.text = text;
            float s = d.Dpi / 96f;
            font = new Font("Segoe UI", 28f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = TextRenderer.MeasureText(text, font);
            int w = size.Width + (int)(48 * s), h = size.Height + (int)(28 * s);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(0, 120, 215);
            Bounds = new Rectangle(d.Work.X + (d.Work.Width - w) / 2, d.Work.Y + (d.Work.Height - h) / 2, w, h);
            Opacity = 0.92;
            timer.Tick += delegate { Close(); };
            timer.Start();
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
            TextRenderer.DrawText(e.Graphics, text, font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            font.Dispose();
            if (shown == this) shown = null;
            base.OnFormClosed(e);
        }
    }
}
