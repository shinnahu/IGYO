using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace IGYO
{
    // The bar between the page and the docked DevTools. WinForms' own Splitter only draws a line while it is dragged
    // and moves on release; this one moves the drawer with the mouse, so DevTools and the page resize as you drag.
    sealed class LiveSplitter : Control
    {
        public Control Target;          // what it resizes (the drawer, docked on the same side)
        public int MinSize = 120;       // the drawer's least extent
        public int MinExtra = 120;      // what the page keeps at least
        bool dragging;
        int startAt, startExtent;

        public LiveSplitter()
        {
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        bool Across => Dock == DockStyle.Left || Dock == DockStyle.Right;   // a bar standing up, beside a drawer on the side

        protected override void OnMouseEnter(EventArgs e)
        {
            Cursor = Across ? Cursors.VSplit : Cursors.HSplit;
            base.OnMouseEnter(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Target == null) return;
            Capture = true;
            Begin(PointToScreen(e.Location));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) MoveTo(PointToScreen(e.Location));
        }

        void Begin(Point screen)
        {
            dragging = true;
            startAt = Across ? screen.X : screen.Y;
            startExtent = Across ? Target.Width : Target.Height;
        }

        void MoveTo(Point screen)
        {
            if (Target == null) return;
            int now = Across ? screen.X : screen.Y;
            // The drawer is at the bottom or on the right: the bar dragged up, or to the left, makes it larger.
            int extent = startExtent + (startAt - now);
            int room = Parent == null ? extent : (Across ? Parent.ClientSize.Width - Width : Parent.ClientSize.Height - Height) - MinExtra;
            extent = Math.Max(MinSize, Math.Min(room, extent));
            if ((Across ? Target.Width : Target.Height) == extent) return;
            if (Across) Target.Width = extent; else Target.Height = extent;
            Parent?.Update();   // drawn now, not when the mouse rests
        }

#if DEBUG
        // Checks: dragged up (or left) by each of these distances from where it started, as the mouse would, with what
        // happened after each step written down by `after`.
        internal void Rehearse(int[] distances, Action after)
        {
            var start = PointToScreen(new Point(Width / 2, Height / 2));
            Begin(start);
            foreach (var d in distances)
            {
                MoveTo(Across ? new Point(start.X - d, start.Y) : new Point(start.X, start.Y - d));
                after();
            }
            dragging = false;
        }
#endif

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            dragging = false;
            base.OnMouseCaptureChanged(e);
        }
    }

    // The engine opens DevTools as a window of its own. This drawer takes that window in and docks it
    // under the page (or beside it), one DevTools per tab, and gives it back as a floating window on "Pop out".
    sealed class DevToolsDrawer : Panel
    {
        readonly MainForm owner;
        readonly Panel header = new SmoothPanel();
        readonly Panel host = new Panel();
        readonly Label title = new Label();
        readonly GlyphButton side, popOut, close;
        readonly Timer finder = new Timer { Interval = 60 };
        readonly Timer watch = new Timer { Interval = 700 };
        readonly Dictionary<BrowserTab, Docked> docked = new Dictionary<BrowserTab, Docked>();
        public readonly LiveSplitter Splitter = new LiveSplitter();
        BrowserTab showing, waitingFor;
        HashSet<IntPtr> before = new HashSet<IntPtr>();
        DateTime waitUntil;
        bool onRight;
        Theme theme;

        sealed class Docked
        {
            public IntPtr Window;
            public IntPtr Style, ExStyle;
            public int Caption;   // the engine draws its own title bar inside the window; the drawer keeps it out of view
            public bool Fitted;   // placed once since its frame changed (later fits only move and size it)
        }

        public DevToolsDrawer(MainForm owner, ToolTip tips)
        {
            this.owner = owner;
            Visible = false;
            Dock = DockStyle.Bottom;
            Height = 340;
            AccessibleName = L.T("Developer tools");
            Splitter.Target = this;
            Splitter.Dock = DockStyle.Bottom;
            Splitter.Height = 4;
            Splitter.Visible = false;
            Splitter.MinSize = 120;
            Splitter.MinExtra = 120;

            side = Ui.MakeButton("", L.T("Dock to the right"), tips, L.T("Dock to the right"), 9.5f);
            popOut = Ui.MakeButton("", L.T("Pop out"), tips, L.T("Open DevTools in its own window"), 9.5f);
            close = Ui.MakeButton(Ui.Close, L.T("Close DevTools"), tips, L.T("Close DevTools (F12)"), 8f);
            side.Click += (s, e) => ToggleSide();
            popOut.Click += (s, e) => PopOut();
            close.Click += (s, e) => CloseFor(showing);
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.Text = "DevTools";
            header.Controls.AddRange(new Control[] { title, side, popOut, close });
            header.Dock = DockStyle.Top;
            header.Layout += (s, e) => LayoutHeader();
            host.Dock = DockStyle.Fill;
            host.Resize += (s, e) => FitWindow();
            Controls.Add(host);
            Controls.Add(header);
            finder.Tick += (s, e) => LookForWindow();
            watch.Tick += (s, e) => CheckAlive();
        }

        int S(int v) => LogicalToDeviceUnits(v);

        public void ApplyTheme(Theme t)
        {
            theme = t;
            header.BackColor = t.Toolbar;
            host.BackColor = t.IsDark ? Color.FromArgb(32, 33, 36) : Color.White;
            title.BackColor = t.Toolbar;
            title.ForeColor = t.Muted;
            foreach (var b in new[] { side, popOut, close }) Ui.Style(b, t.Toolbar, t);
            Splitter.BackColor = t.Border;
            BackColor = t.Toolbar;
        }

        void LayoutHeader()
        {
            header.Height = S(30);
            int h = header.ClientSize.Height, b = S(26), y = (h - b) / 2, x = header.ClientSize.Width - S(4);
            foreach (var button in new[] { close, popOut, side })
            {
                x -= b;
                button.SetBounds(x, y, b, b);
                x -= S(2);
            }
            title.SetBounds(S(12), 0, Math.Max(0, x - S(16)), h);
        }

        public bool IsOpenFor(BrowserTab tab) => tab != null && docked.TryGetValue(tab, out var d) && IsWindow(d.Window);

        // F12: open DevTools for the tab in the drawer, or close them if they're already there.
        public void Toggle(BrowserTab tab)
        {
            if (tab?.Core == null) return;
            if (IsOpenFor(tab)) { CloseFor(tab); return; }
            Expect(tab);
            try { tab.Core.OpenDevToolsWindow(); } catch { finder.Stop(); }
        }

        // DevTools are about to open by themselves (Inspect in the page menu, Ctrl+Shift+J or C): dock them when they appear.
        public void Expect(BrowserTab tab)
        {
            if (tab?.Core == null || IsOpenFor(tab)) return;
            waitingFor = tab;
            before = new HashSet<IntPtr>(FindDevTools(BrowserPid(tab)));
            waitUntil = DateTime.UtcNow.AddSeconds(6);
            finder.Start();
        }

        static uint BrowserPid(BrowserTab tab)
        {
            try { return tab?.Core?.BrowserProcessId ?? 0; } catch { return 0; }
        }

        void LookForWindow()
        {
            var tab = waitingFor;
            if (tab?.Core == null || DateTime.UtcNow > waitUntil) { finder.Stop(); waitingFor = null; return; }
            var found = FindDevTools(BrowserPid(tab)).FirstOrDefault(h => !before.Contains(h) && !docked.Values.Any(d => d.Window == h));
            if (found == IntPtr.Zero) return;
            finder.Stop();
            waitingFor = null;
            Embed(tab, found);
        }

        void Embed(BrowserTab tab, IntPtr window)
        {
            var d = new Docked
            {
                Window = window,
                Style = GetWindowLongPtr(window, GWL_STYLE),
                ExStyle = GetWindowLongPtr(window, GWL_EXSTYLE),
                Caption = CaptionHeight(window),
            };
            ShowWindow(window, SW_HIDE);
            long style = (d.Style.ToInt64() & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD;
            long ex = d.ExStyle.ToInt64() & ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE);
            SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style));
            SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(ex));
            SetParent(window, host.Handle);
            docked[tab] = d;
            if (tab == owner.CurrentTab) ShowFor(tab);
        }

        // The selected tab changed: show its DevTools, if it has any docked.
        public void ShowFor(BrowserTab tab)
        {
            if (showing != null && showing != tab && docked.TryGetValue(showing, out var old) && IsWindow(old.Window)) ShowWindow(old.Window, SW_HIDE);
            showing = tab;
            if (tab == null || !docked.TryGetValue(tab, out var d) || !IsWindow(d.Window))
            {
                if (tab != null) docked.Remove(tab);
                SetDrawerVisible(false);
                return;
            }
            SetDrawerVisible(true);
            title.Text = "DevTools  ·  " + (Favicons.HostOf(tab.Url) ?? tab.Title);
            FitWindow();
            ShowWindow(d.Window, SW_SHOWNA);
        }

        void SetDrawerVisible(bool visible)
        {
            if (Visible == visible) return;
            if (visible)
            {
                // Docked first, so the page takes what's left.
                Splitter.SendToBack();
                SendToBack();
            }
            Splitter.Visible = visible;
            Visible = visible;
            if (visible) watch.Start(); else watch.Stop();
        }

        // Follows the drawer at once, also while the window or the splitter is being dragged. (Its frame is worked out
        // again only the first time: on every step it cost a redraw of the whole DevTools window for nothing.)
        void FitWindow()
        {
            if (showing == null || !docked.TryGetValue(showing, out var d)) return;
            var size = host.ClientSize;
            uint flags = SWP_NOZORDER | SWP_NOACTIVATE | (d.Fitted ? 0 : SWP_FRAMECHANGED);
            d.Fitted = true;
            SetWindowPos(d.Window, IntPtr.Zero, 0, -d.Caption, Math.Max(1, size.Width), Math.Max(1, size.Height + d.Caption), flags);
        }

        // Asks the window where its own title bar ends: the first row, from the top, that is page rather than frame.
        static int CaptionHeight(IntPtr window)
        {
            if (!GetWindowRect(window, out var r) || r.Bottom - r.Top < 100) return 0;
            int x = r.Left + Math.Min(80, (r.Right - r.Left) / 4);
            for (int dy = 0; dy < 100; dy++)
            {
                int y = r.Top + dy;
                var point = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
                if (SendMessageTimeout(window, WM_NCHITTEST, IntPtr.Zero, point, SMTO_ABORTIFHUNG, 200, out var hit) == IntPtr.Zero) return 0;
                if (hit.ToInt64() == HTCLIENT) return dy < 80 ? dy : 0;
            }
            return 0;
        }

        void ToggleSide()
        {
            onRight = !onRight;
            var dock = onRight ? DockStyle.Right : DockStyle.Bottom;
            int extent = onRight ? Math.Max(S(360), Parent?.ClientSize.Width * 2 / 5 ?? S(480)) : Math.Max(S(240), Parent?.ClientSize.Height * 2 / 5 ?? S(340));
            Splitter.Dock = dock;
            Dock = dock;
            if (onRight) { Width = extent; Splitter.Width = S(4); } else { Height = extent; Splitter.Height = S(4); }
            side.Glyph = onRight ? "" : "";
            side.Text = side.AccessibleName = onRight ? L.T("Dock to the bottom") : L.T("Dock to the right");
            Splitter.SendToBack();
            SendToBack();
            FitWindow();
        }

#if DEBUG
        // Checks: the splitter dragged in steps; after each, the drawer's height and the docked DevTools window's.
        internal string RehearseSplit(int[] distances)
        {
            var text = new StringBuilder();
            text.Append("open ").Append(Visible).Append(", drawer ").Append(Height).Append(", window ").Append(DockedHeight()).Append('\n');
            Splitter.Rehearse(distances, () => text.Append("drawer ").Append(Height).Append(", window ").Append(DockedHeight()).Append('\n'));
            return text.ToString();
        }

        int DockedHeight() => showing != null && docked.TryGetValue(showing, out var d) && GetWindowRect(d.Window, out var r) ? r.Bottom - r.Top : -1;
#endif

        // Gives the DevTools window back as a normal floating window.
        void PopOut()
        {
            var tab = showing;
            if (tab == null || !docked.TryGetValue(tab, out var d)) return;
            docked.Remove(tab);
            var screen = RectangleToScreen(ClientRectangle);
            SetParent(d.Window, IntPtr.Zero);
            SetWindowLongPtr(d.Window, GWL_STYLE, d.Style);
            SetWindowLongPtr(d.Window, GWL_EXSTYLE, d.ExStyle);
            SetWindowPos(d.Window, IntPtr.Zero, screen.X + S(40), screen.Y - S(40), Math.Max(S(640), screen.Width - S(80)), Math.Max(S(420), screen.Height), SWP_NOZORDER | SWP_FRAMECHANGED);
            ShowWindow(d.Window, SW_SHOW);
            SetDrawerVisible(false);
        }

        public void CloseFor(BrowserTab tab) => CloseFor(tab, false);

        // detach: hand the window back to Windows first, so closing this window never tears it down mid-way.
        void CloseFor(BrowserTab tab, bool detach)
        {
            if (tab == null || !docked.TryGetValue(tab, out var d)) return;
            docked.Remove(tab);
            if (IsWindow(d.Window))
            {
                if (detach)
                {
                    ShowWindow(d.Window, SW_HIDE);
                    SetParent(d.Window, IntPtr.Zero);
                    SetWindowLongPtr(d.Window, GWL_STYLE, d.Style);
                }
                PostMessage(d.Window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            if (tab == showing) SetDrawerVisible(false);
        }

        public void CloseAll()
        {
            finder.Stop();
            foreach (var tab in docked.Keys.ToList()) CloseFor(tab, true);
        }

        // DevTools close themselves when their page goes away; the drawer follows.
        void CheckAlive()
        {
            if (showing != null && docked.TryGetValue(showing, out var d) && !IsWindow(d.Window)) ShowFor(showing);
            foreach (var gone in docked.Where(p => !IsWindow(p.Value.Window)).Select(p => p.Key).ToList()) docked.Remove(gone);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                finder.Dispose();
                watch.Dispose();
            }
            base.Dispose(disposing);
        }

        // ----- Windows -----

        static List<IntPtr> FindDevTools(uint pid)
        {
            var list = new List<IntPtr>();
            if (pid == 0) return list;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint p);
                if (p != pid) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                var text = new StringBuilder(256);
                GetWindowText(h, text, text.Capacity);
                if (text.ToString().StartsWith("DevTools")) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        const int GWL_STYLE = -16, GWL_EXSTYLE = -20, SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNA = 8;
        const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L,
            WS_SYSMENU = 0x00080000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L;
        const long WS_EX_APPWINDOW = 0x40000L, WS_EX_WINDOWEDGE = 0x100L, WS_EX_DLGMODALFRAME = 0x1L, WS_EX_CLIENTEDGE = 0x200L;
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        const uint WM_CLOSE = 0x0010, WM_NCHITTEST = 0x0084, SMTO_ABORTIFHUNG = 0x2;
        const long HTCLIENT = 1;

        struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
