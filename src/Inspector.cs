using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Tessera
{
    // Lists every window Tessera manages, so a ghost tile can be identified and removed.
    class Inspector : Form
    {
        readonly WM wm;
        readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        readonly FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        readonly Highlight highlight = new Highlight();
        readonly Timer refreshSoon = new Timer { Interval = 300 }; // actions like Close finish asynchronously

        public Inspector(WM wm, Action<string> makeRule)
        {
            this.wm = wm;
            Text = "Tessera - windows";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Segoe UI", 10f);
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;

            foreach (var c in new[] { "Title", "Process", "PID", "State", "Class", "Position / size", "Path" }) list.Columns.Add(c);
            list.SelectedIndexChanged += delegate { ShowHighlight(); };

            AddButton("Float / Tile", (h, item) => wm.ToggleFloat(h));
            AddButton("Make a rule…", (h, item) => { var app = Native.AppName(h); if (app.Length > 0) makeRule(app); });
            AddButton("Ignore this app always", (h, item) =>
            {
                if (item.SubItems[1].Text.Length == 0) return;
                Ignore.Add("process:" + item.SubItems[1].Text);
                wm.Sync();
            });
            AddButton("Ignore this window type always", (h, item) =>
            {
                Ignore.Add("class:" + item.SubItems[4].Text);
                wm.Sync();
            });
            AddButton("Close window", (h, item) => Native.PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero));
            AddButton("Kill process", (h, item) => Kill(item));
            var openList = new Button { Text = "Edit ignore list", AutoSize = true };
            openList.Click += delegate
            {
                File.AppendAllText(Ignore.FilePath, ""); // create it so Notepad doesn't ask
                Process.Start("notepad.exe", "\"" + Ignore.FilePath + "\"");
            };
            buttons.Controls.Add(openList);

            Controls.Add(list);
            Controls.Add(buttons);
            refreshSoon.Tick += delegate { refreshSoon.Stop(); RefreshRows(); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            VisibleChanged += delegate { if (!Visible) highlight.Hide(); };
        }

        void AddButton(string text, Action<IntPtr, ListViewItem> action)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += delegate
            {
                if (list.SelectedItems.Count == 0) return;
                var item = list.SelectedItems[0];
                action((IntPtr)item.Tag, item);
                refreshSoon.Start();
            };
            buttons.Controls.Add(b);
        }

        void Kill(ListViewItem item)
        {
            int pid = int.Parse(item.SubItems[2].Text);
            var msg = "Kill " + item.SubItems[1].Text + " (PID " + pid + ")?\nAll its windows close and unsaved work is lost.";
            if (MessageBox.Show(this, msg, "Tessera", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { Process.GetProcessById(pid).Kill(); }
            catch (Exception e) { MessageBox.Show(this, "Could not kill it: " + e.Message, "Tessera"); }
        }

        public void Toggle()
        {
            if (Visible) { Hide(); return; }
            Ignore.Load(); // picks up edits made in Notepad
            var work = Native.DisplayOf(Native.GetForegroundWindow()).Work;
            Bounds = new Rectangle(work.X + work.Width / 6, work.Y + work.Height / 4, work.Width * 2 / 3, work.Height / 2);
            RefreshRows();
            Show();
            Native.Focus(Handle);
        }

        void RefreshRows()
        {
            var selected = list.SelectedItems.Count > 0 ? (IntPtr)list.SelectedItems[0].Tag : IntPtr.Zero;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var w in wm.Tracked())
            {
                var h = w.Key;
                if (!Native.IsWindow(h)) continue;
                uint pid = Native.Pid(h);
                var path = Native.ProcessPath(pid);
                var r = Native.VisibleRect(h);
                var title = Native.Title(h);
                var item = new ListViewItem(new[]
                {
                    title.Length > 0 ? title : "(no title)",
                    path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : "",
                    pid.ToString(),
                    w.Value,
                    Native.ClassName(h),
                    r.X + ", " + r.Y + "   " + r.Width + " x " + r.Height,
                    path,
                }) { Tag = h };
                list.Items.Add(item);
                if (h == selected) item.Selected = true;
            }
            list.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            if (list.Columns[0].Width > 350) list.Columns[0].Width = 350;
            list.EndUpdate();
            ShowHighlight();
        }

        void ShowHighlight()
        {
            if (list.SelectedItems.Count == 0 || !Native.IsWindow((IntPtr)list.SelectedItems[0].Tag)) { highlight.Hide(); return; }
            highlight.ShowAround(Native.VisibleRect((IntPtr)list.SelectedItems[0].Tag));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } // reuse the same window
            base.OnFormClosing(e);
        }
    }

    // A colored frame drawn on top of a window, without taking focus.
    class Highlight : Form
    {
        const int Thickness = 5;

        public Highlight()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.OrangeRed;
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

        public void ShowAround(Rectangle r)
        {
            var outer = Rectangle.Inflate(r, Thickness, Thickness);
            Bounds = outer;
            var frame = new Region(new Rectangle(0, 0, outer.Width, outer.Height));
            frame.Exclude(new Rectangle(Thickness, Thickness, r.Width, r.Height));
            var old = Region;
            Region = frame;
            if (old != null) old.Dispose();
            if (!Visible) Show();
        }
    }
}
