using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // One browser window. The window has no Windows title bar: its whole interface is the page in UiHost,
    // which lies over the window; the web pages (one view per tab) sit in ContentHost, in the hole the
    // interface leaves for them. This class owns the tabs and does what the interface asks for.
    sealed partial class MainForm : Form
    {
        readonly BrowserApp app;
        readonly List<BrowserTab> tabs = new List<BrowserTab>();
        readonly Stack<string> closedTabs = new Stack<string>();
        readonly BookmarkStore bookmarks;
        readonly string[] startUrls;
        Session session;
        BrowserTab current;
        Theme theme;
        BrowserTab adoptTab;           // a window made for a dragged-out tab: the tab it takes in when it opens
        readonly Rectangle? startPlace;   // and where it opens
        readonly bool madeForTab;      // such a window: it never opens tabs of its own
        bool torn;                     // and while that tab is still being dragged (the window follows the mouse: see Tear)

        readonly UiHost ui;
        readonly DevToolsDrawer devtools;
        readonly ToolTip tips = new ToolTip();
        readonly Timer housekeepingTimer = new Timer { Interval = 5000 };
        readonly Timer suggestTimer = new Timer { Interval = 120 };   // waits for a pause in typing
        readonly Timer blockedTimer = new Timer { Interval = 250 };   // the ad block counter changes many times a second
        readonly TaskCompletionSource<bool> uiReady = new TaskCompletionSource<bool>();
        System.Threading.CancellationTokenSource suggestCancel;
        string suggestText = "";
        bool fullScreen, windowFullScreen, addressFocused, focusAddressWhenReady;
        FormWindowState stateBeforeFullScreen;
        Rectangle boundsBeforeFullScreen;
        DateTime minimizedAt = DateTime.MaxValue;
        bool sleptWhileMinimized;
        string newTabLook;   // what open New Tab pages were drawn with

        // Where the interface says the page goes, as distances from the window's edges (pixels), and what floats over the page.
        Padding insets;
        bool insetsKnown, uiModal;
        readonly List<Rectangle> uiOverlays = new List<Rectangle>();
        bool tabsDirty, navDirty, bookmarksDirty, flushQueued;
        readonly bool tabsComeBack;   // last time's tabs are opened even when tabs are not kept between starts

        // restore: the tabs and place this window had when IGYO was last left (null for a new window).
        // adopt and place: a window made for a tab dragged out of another window; torn: the tab is still being dragged
        // (see Tear).
        public MainForm(BrowserApp app, Profile profile, bool isPrivate, string[] urls, Session restore, BrowserTab adopt = null, Rectangle? place = null, bool torn = false)
        {
            this.app = app;
            Profile = profile;
            IsPrivate = isPrivate;
            startUrls = urls ?? new string[0];
            adoptTab = adopt;
            startPlace = place;
            madeForTab = adopt != null;
            this.torn = torn;
            bookmarks = app.BookmarksFor(profile);
            session = restore;
            tabsComeBack = BrowserApp.RestoreOnce;   // this copy takes the place of one that restarted itself

            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Segoe UI", 9f);
            MinimumSize = new Size(560, 400);
            Size = new Size(1280, 820);
            StartPosition = session?.Bounds != null || place != null ? FormStartPosition.Manual : FormStartPosition.WindowsDefaultLocation;
            try { Icon = app.WindowIcon ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            ui = new UiHost(this);
            ui.Message += OnUiMessage;
            ui.Failed += reason => MessageBox.Show(this, L.T("{0}'s interface couldn't start: {1}", InstallInfo.AppName, reason) + "\n\n" + L.T("Details are in {0}", ErrorLog.FilePath),
                InstallInfo.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            devtools = new DevToolsDrawer(this, tips);
            ContentHost.Controls.Add(devtools.Splitter);
            ContentHost.Controls.Add(devtools);
            Controls.Add(ContentHost);
            Controls.Add(ui);
            ui.BringToFront();

            tearWatch.Tick += (s, e) => WatchTear();
            heldWatch.Tick += (s, e) => WatchHeld();
            suggestTimer.Tick += async (s, e) => await FetchSuggestionsAsync();
            blockedTimer.Tick += (s, e) => { blockedTimer.Stop(); MarkDirty(nav: true); };
            housekeepingTimer.Tick += (s, e) => Housekeeping();
            fullScreenTop.Tick += (s, e) => WatchFullScreenTop();

            app.ThemeChanged += OnThemeChanged;
            app.SettingsChanged += OnSettingsChanged;
            app.Bridge.Changed += OnBridgeChanged;
            bookmarks.Changed += OnBookmarksChanged;
            Favicons.Changed += OnFaviconChanged;
            theme = app.ThemeFor(Profile);
            BackColor = theme.Frame;
            UpdateTitle();
        }

        public Profile Profile { get; }
        public bool IsPrivate { get; }
        public Panel ContentHost { get; } = new Panel();
        public CoreWebView2 ActiveCore => current?.Core;
        public BrowserTab CurrentTab => current;
        public IReadOnlyList<BrowserTab> Tabs => tabs;
        public void ExpectDevTools(BrowserTab tab) => devtools.Expect(tab);
        public bool IsShowing(BrowserTab tab) => tab == current && WindowState != FormWindowState.Minimized;
        public void OpenUrl(string url) => NewTab(url, true, false);

        // Links handed over from outside (another program opened them; see OneCopy): each opens as a new tab at the end.
        public void OpenFromOutside(IEnumerable<string> links)
        {
            foreach (var link in links)
            {
                var url = AddressInput.ToUrl(link, Settings.SearchUrl);
                if (url != null) NewTab(url, true, true);
            }
        }

        // Brought to the front for something handed over from outside. (Never a window started for checks: see QuietStart.)
        public void ComeForward()
        {
            if (QuietStart || IsDisposed || !IsHandleCreated) return;
            if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9 /* SW_RESTORE: back as it was, maximized or not */);
            Activate();
        }
        // This window's look: its profile's (each profile has its own, see BrowserApp).
        public Theme CurrentTheme => theme ?? app.ThemeFor(Profile);
        public ThemeDef ThemeDef => app.ThemeDefFor(Profile);
        public string AccentHex => app.AccentHexFor(Profile);
        public BrowserApp App => app;
        public BookmarkStore Bookmarks => bookmarks;
        public AdBlocker Blocker => app.Blocker;
        public bool AdBlockOn => app.Settings.AdBlock;
        public bool ForceDark => app.Settings.ForceDark;   // websites are shown dark (Tools > Dark mode for websites)
        // What videos are asked to be sharpened with (Tools > Enhance videos); "" while that is off.
        public string EnhanceKind => app.Settings.EnhanceVideos ? Tools.EnhanceKind(app.Settings.EnhanceLevel) : "";
        public bool IdentifyAsEdge => app.Settings.IdentifyAsEdge;
        public Task BlockerReady => (Task)app.BlockerTask ?? Task.CompletedTask;
        Settings Settings => app.Settings;
        public Settings AppSettings => app.Settings;
        string HomeUrl => Settings.NewTabPage ? NewTabPage.Url : null;   // what a new tab shows
        int S(int value) => LogicalToDeviceUnits(value);

        // ----- window lifetime -----

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var b = session?.Bounds;
            if (b != null && b.Length == 4)
            {
                var r = new Rectangle(b[0], b[1], b[2], b[3]);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) Bounds = r;
                if (session.Maximized) WindowState = FormWindowState.Maximized;
            }
            // A window for a dragged-out tab: under the mouse, wherever it is while the tab is still being dragged (it
            // follows the mouse from there), else kept on the screen. (Set here, after the window has been scaled for
            // the screen's DPI: the size given is already in the screen's pixels.)
            if (startPlace is Rectangle q && torn) Bounds = q;
            else if (startPlace is Rectangle p)
            {
                var area = Screen.FromPoint(new Point(p.X + S(110), p.Y + S(18))).WorkingArea;
                int w = Math.Min(p.Width, area.Width), h = Math.Min(p.Height, area.Height);
                Bounds = new Rectangle(Math.Max(area.Left, Math.Min(p.X, area.Right - w)), Math.Max(area.Top, Math.Min(p.Y, area.Bottom - h)), w, h);
            }
            OnThemeChanged(null);
            LayoutViews();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindow(this, CurrentTheme);
            // The title bar is gone from the first moment (see WndProc).
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
#if DEBUG
            // Test builds only: NSB_TEST_ERROR makes the window fail on purpose, to check that errors are logged, not fatal.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_ERROR")))
                BeginInvoke(new Action(() => throw new InvalidOperationException("Test error (NSB_TEST_ERROR)")));
#endif
            // Behind the other windows (not one made for a dragged tab: that one is over the window it came from).
            if (QuietStart && !madeForTab) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            StartupTrace.Mark("window shown");
            _ = ui.InitAsync();
            OpenStartTabs();
            EarlyViews.DropUnused();   // by now the first tab and the interface have taken theirs, if they could
            session = null;
            housekeepingTimer.Start();
            UpdateSign();   // a question asked before the window was on screen
            if (!Settings.FirstRunDone && !IsPrivate)
            {
                Settings.FirstRunDone = true;
                Settings.Save();
                OfferChromeImport();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            devtools.CloseAll();
            if (IsPrivate) return;
            // Its tabs and place go to the app, which keeps them with the profile's other windows (see Session.Together).
            var open = tabs.Where(t => !t.IsBlank).ToList();
            var saved = new Session
            {
                Tabs = open.Select(t => t.Url).ToList(),
                Titles = open.Select(t => t.Title).ToList(),
                Active = Math.Max(0, open.IndexOf(current)),
                Maximized = fullScreen ? stateBeforeFullScreen == FormWindowState.Maximized : WindowState == FormWindowState.Maximized,
            };
            var r = fullScreen ? boundsBeforeFullScreen : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            saved.Bounds = new[] { r.X, r.Y, r.Width, r.Height };
            app.WindowClosing(this, saved);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            app.ThemeChanged -= OnThemeChanged;
            app.SettingsChanged -= OnSettingsChanged;
            app.Bridge.Changed -= OnBridgeChanged;
            CloseBridgeParts();
            bookmarks.Changed -= OnBookmarksChanged;
            Favicons.Changed -= OnFaviconChanged;
            housekeepingTimer.Dispose();
            fullScreenTop.Dispose();
            tearWatch.Dispose();
            heldWatch.Dispose();
            suggestTimer.Dispose();
            blockedTimer.Dispose();
            suggestCancel?.Cancel();
            foreach (var waiting in dialogs.Values.ToList()) waiting.TrySetResult(null);
            dialogs.Clear();
            foreach (var tab in tabs) tab.Close();
            base.OnFormClosed(e);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            insetsKnown = false;   // the interface reports its sizes again, in the new pixels
            LayoutViews();
            UpdateSign();          // drawn again at the new size
        }

        // Claude's sign in the page's corner goes with the window (MainForm.Bridge.cs).
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            PlaceSign();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateSign();   // a hidden window's owned windows stay on screen unless hidden too
        }

        bool isActive;   // this window is the one in front (the interface dims its frame when it isn't)

        // Someone is using this window right now: it is the one in front and not minimized.
        public bool IsInUse => isActive && WindowState != FormWindowState.Minimized;

        // The engine's name for the interface's own page, so that its process is told apart from the tabs'.
        public uint InterfaceFrameId { get { try { return ui.Core?.FrameId ?? 0; } catch { return 0; } } }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            isActive = true;
            LastActive = DateTime.UtcNow;
#if DEBUG
            // Test builds: every time a window becomes the active one, for the checks that Claude's acting never does that.
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-activations.txt"), DateTime.UtcNow.ToString("HH:mm:ss.fff") + " window " + Number + " " + Profile.Id + "\r\n"); } catch { }
#endif
            MiddleClick.Watch(!Settings.SwitchToNewTabs);   // (see MiddleClick: only while a window of IGYO's is the active one)
            PostWindow();
            // A Settings page on show reads its facts again, so that what was changed outside IGYO meanwhile (the
            // default browser, chosen in Windows' settings) shows at once.
            if (current != null && SettingsPage.IsUrl(current.Url) && current.Core != null && !current.Core.IsSuspended)
                try { _ = current.Core.ExecuteScriptAsync("window.IGYOSettings && window.IGYOSettings.refresh()"); } catch { }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            isActive = false;
            MiddleClick.Watch(false);
            CloseExtensionPopup();   // an extension's popup goes when the window is left, as in a browser
            current?.TakePreview();  // for the card over its tab later
            PostWindow();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            CloseExtensionPopup();   // its button is somewhere else now
            if (WindowState == FormWindowState.Minimized)
            {
                if (minimizedAt == DateTime.MaxValue)
                {
                    minimizedAt = DateTime.Now;
                    current?.Hide();   // nothing on screen to draw while minimized
                    UpdateSign();      // (Claude's sign goes with the window)
                }
                return;
            }
            bool back = minimizedAt != DateTime.MaxValue;
            if (back)
            {
                minimizedAt = DateTime.MaxValue;
                sleptWhileMinimized = false;
                current?.Show();       // wakes the tab if it was asleep
                MarkDirty(tabs: true);
            }
            LayoutViews();
            if (back) UpdateSign();    // Claude's sign comes back with the window
            PostWindow();
        }

        // ----- the window without a title bar -----

        const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_WINDOWPOSCHANGED = 0x0047;
        const int HTCLIENT = 1, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        bool wasNormal = true, restoring;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public IntPtr pos; }
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);

        // For checks, like NSB_DATA_DIR: with NSB_NO_ACTIVATE the window opens behind the others and without taking
        // the keyboard, so an automated check (of the installed copy too) doesn't interrupt whoever is at the PC.
        static readonly bool QuietStart = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_NO_ACTIVATE"));
        // (A window made for a tab that is still being dragged does not take the front from the window the mouse is
        // dragging in either, until the tab is let go: see Tear.)
        protected override bool ShowWithoutActivation => QuietStart || torn;
        static bool QuietStartActive => QuietStart;   // such a window is never in front, but is pictured as if it were

        // Such a window also never pulls the keyboard to itself: when a view is given the keyboard the engine brings
        // the whole window to the front. Once someone has put it in front themselves, it behaves like any other.
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        internal bool MayTakeKeyboard => !QuietStart || GetForegroundWindow() == Handle;
        void FocusTab(BrowserTab tab) { if (MayTakeKeyboard) tab?.View?.Focus(); }

        bool Frameless => FormBorderStyle == FormBorderStyle.Sizable;

        // The strip at the very top where the mouse resizes the window (the interface starts just below it).
        int TopBand => Frameless && WindowState == FormWindowState.Normal ? S(4) : 0;

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE when m.WParam != IntPtr.Zero && Frameless:
                {
                    // Keep Windows' frame at the sides and bottom (resizing, shadow, snapping); give the title bar's room to the interface.
                    var before = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                    base.WndProc(ref m);
                    var after = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                    after.r0.Top = before.r0.Top;
                    // A maximized window hangs over the screen's edges by its frame; without this its first rows would be off-screen.
                    if (IsZoomed(Handle)) after.r0.Top += GetSystemMetricsForDpi(33, (uint)DeviceDpi) + GetSystemMetricsForDpi(92, (uint)DeviceDpi);
                    Marshal.StructureToPtr(after, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                case WM_NCHITTEST when TopBand > 0:
                {
                    base.WndProc(ref m);
                    if ((int)m.Result != HTCLIENT) return;
                    long l = m.LParam.ToInt64();
                    var p = PointToClient(new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)));
                    if (p.Y < TopBand) m.Result = (IntPtr)(p.X < S(14) ? HTTOPLEFT : p.X >= ClientSize.Width - S(14) ? HTTOPRIGHT : HTTOP);
                    return;
                }
                case 0x0216 /* WM_MOVING */ when movingByTab:
                    base.WndProc(ref m);
                    CheckRowWhileMoving();
                    return;
                case WM_WINDOWPOSCHANGED:
                {
                    // Coming back from minimized or maximized, Windows has already put the window where it was. WinForms then sets
                    // the size again from a client size it worked out with a title bar, which would grow the window each time.
                    bool nowNormal = IsHandleCreated && !IsIconic(Handle) && !IsZoomed(Handle);
                    restoring = !wasNormal && nowNormal;
                    try { base.WndProc(ref m); }
                    finally { restoring = false; }
                    wasNormal = nowNormal;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (restoring && IsHandleCreated && GetWindowRect(Handle, out var r))
            {
                x = r.Left; y = r.Top; width = r.Right - r.Left; height = r.Bottom - r.Top;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }

        // The client area is larger than WinForms assumes (it has the title bar's room), so sizes are worked out from the real window.
        protected override void SetClientSizeCore(int x, int y)
        {
            if (IsHandleCreated && Frameless && GetWindowRect(Handle, out var w) && GetClientRect(Handle, out var c))
            {
                Size = new Size(x + (w.Right - w.Left) - (c.Right - c.Left), y + (w.Bottom - w.Top) - (c.Bottom - c.Top));
                return;
            }
            base.SetClientSizeCore(x, y);
        }

        void LayoutViews()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
            var client = ClientSize;
            if (client.Width <= 0 || client.Height <= 0) return;
            int band = TopBand;
            if (!insetsKnown)
                insets = fullScreen ? Padding.Empty : new Padding(0, S(Settings.ShowBookmarksBar ? 118 : 86), 0, 0);   // until the interface reports
            ui.Bounds = new Rectangle(0, band, client.Width, client.Height - band);
            ContentHost.Bounds = new Rectangle(insets.Left, band + insets.Top,
                Math.Max(0, client.Width - insets.Horizontal), Math.Max(0, client.Height - band - insets.Vertical));
            ApplyShape();
            PlaceSign();
        }

        void ApplyShape()
        {
            var size = ui.ClientSize;
            if (uiModal)
            {
                ui.SetShape(new[] { new Rectangle(Point.Empty, size) });
                return;
            }
            var rects = new List<Rectangle>(uiOverlays);
            if (insets.Top > 0) rects.Add(new Rectangle(0, 0, size.Width, insets.Top));
            if (insets.Bottom > 0) rects.Add(new Rectangle(0, size.Height - insets.Bottom, size.Width, insets.Bottom));
            if (insets.Left > 0) rects.Add(new Rectangle(0, 0, insets.Left, size.Height));
            if (insets.Right > 0) rects.Add(new Rectangle(size.Width - insets.Right, 0, insets.Right, size.Height));
            ui.SetShape(rects);
        }

        // The interface reports where the page's hole is and what it has floating over the page.
        void OnUiLayout(Dictionary<string, object> m)
        {
            var view = Numbers(Json.Get(m, "view"));
            var size = Numbers(Json.Get(m, "size"));
            if (view.Length != 4 || size.Length != 2 || size[0] <= 0 || size[1] <= 0) return;
            insets = new Padding(Clamp(view[0]), Clamp(view[1]), Clamp(size[0] - view[0] - view[2]), Clamp(size[1] - view[1] - view[3]));
            insetsKnown = true;
            uiModal = Json.Get(m, "modal") is bool modal && modal;
            uiOverlays.Clear();
            foreach (var item in Json.Arr(Json.Get(m, "hit")))
            {
                var r = Numbers(item);
                if (r.Length == 4) uiOverlays.Add(new Rectangle(r[0], r[1], r[2], r[3]));
            }
            var strip = Numbers(Json.Get(m, "strip"));
            stripArea = strip.Length == 4 ? new Rectangle(strip[0], strip[1], strip[2], strip[3]) : Rectangle.Empty;
            var row = Numbers(Json.Get(m, "row"));
            rowArea = row.Length == 4 ? new Rectangle(row[0], row[1], row[2], row[3]) : Rectangle.Empty;
            LayoutViews();
        }

        Rectangle stripArea;   // where the tab strip is in the interface's view (as it reports it)
        Rectangle rowArea;     // and the whole row it is in (the new-tab button and the empty room beside the tabs too)

        // Whether a point of the screen is over this window's tab row, or within margin of it: a tab dragged from another
        // window goes into this one's strip there (see Tear, DragWindowNow). The whole row counts, from one side of the
        // window to the other, not only the tabs themselves.
        public bool TabStripContains(Point screen, int margin = 16)
        {
            if (!IsHandleCreated || IsDisposed || !Visible || hiddenForDrag || WindowState == FormWindowState.Minimized || uiModal) return false;
            var band = rowArea.IsEmpty ? stripArea : rowArea;
            if (band.IsEmpty) return false;
            var at = ui.PointToClient(screen);
            int m = S(margin);
            return at.X >= -m && at.X < ui.ClientSize.Width + m && at.Y >= band.Top - m && at.Y < band.Bottom + m;
        }

        // The visible part of this window on the screen (WindowToJoin looks there for what lies on top of it).
        public Rectangle ScreenBounds => IsHandleCreated ? RectangleToScreen(ClientRectangle) : Rectangle.Empty;

#if DEBUG
        // Test builds: what TabStripContains looked at (see BrowserApp.WindowToJoin).
        public string StripDebug(Point screen)
        {
            if (!IsHandleCreated || IsDisposed) return "gone";
            var inUi = ui.PointToClient(screen);
            return $"[visible {Visible} hidden {hiddenForDrag} modal {uiModal} strip {stripArea} row {rowArea} at-ui {inUi.X},{inUi.Y}]";
        }
#endif

        static int Clamp(int v) => Math.Max(0, Math.Min(4000, v));

        static int[] Numbers(object value) => Json.Arr(value).Select(n => { try { return (int)Math.Round(Convert.ToDouble(n)); } catch { return 0; } }).ToArray();

        static int Int(Dictionary<string, object> m, string key)
        {
            try { return (int)Math.Round(Convert.ToDouble(Json.Get(m, key) ?? 0)); } catch { return 0; }
        }

        static bool Flag(Dictionary<string, object> m, string key) => Json.Get(m, key) is bool b && b;

        // ----- tabs -----

        BrowserTab CreateTab(string url, string title = null)
        {
            var tab = new BrowserTab(this, url, title);
            Watch(tab);
            return tab;
        }

        void Watch(BrowserTab tab)
        {
            tab.Changed += OnTabChanged;
            tab.ZoomChanged += OnTabZoomChanged;
            tab.BlockedChanged += OnTabBlockedChanged;
        }

        void Unwatch(BrowserTab tab)
        {
            tab.Changed -= OnTabChanged;
            tab.ZoomChanged -= OnTabZoomChanged;
            tab.BlockedChanged -= OnTabBlockedChanged;
        }

        void OnTabBlockedChanged(BrowserTab tab)
        {
            if (tab == current && !blockedTimer.Enabled) blockedTimer.Start();
        }

        // ----- a tab dragged off the strip, as in Chrome -----
        // Once the mouse takes a tab well off the strip, the tab gets a window of its own at once, the size of this one,
        // and that window follows the mouse with the tab under it, over this window and off it. Over a tab strip (this
        // window's or another window's of the same profile) the tab goes into that strip and moves along it with the
        // mouse while its window waits out of sight; pulled off the strip again, it is back in its window under the
        // mouse. Let go, it stays where it is: in a strip, or in its window there. Esc puts it back where it was. The
        // page moves as it is (BrowserTab.MoveTo): nothing is loaded again.
        // This window's interface keeps the mouse until the button is let go, so it goes on saying where the mouse is
        // (tearMove) and where it was let go (tearEnd), and this window does the moving. A window's only tab is not
        // torn off: dragging it moves the window (DragWindow).
        sealed class Tear
        {
            public BrowserTab Tab;
            public MainForm Window;    // the window made for it
            public MainForm Strip;     // the window whose strip it is in meanwhile, or null
            public Point Grab;         // the mouse from the top left of its window's interface (the tab first in the strip)
            public int HoldX, HoldY;   // the mouse from the tab's own top left
            public int From;           // its place in this window, for Esc
            public DateTime Heard;     // when the interface last said where the mouse is
        }

        Tear tear;
        readonly Timer tearWatch = new Timer { Interval = 250 };
        bool hiddenForDrag;            // a window made for a dragged tab, out of sight while the tab is in a strip

        // at: the mouse on the screen; grab: the mouse from the top left of the interface, where it would hold the tab
        // as the only one in a strip; hold: the mouse from the tab's top left (all in the interface's pixels).
        void TearOff(BrowserTab tab, Point at, Point grab, Point hold)
        {
            if (tear != null || tabs.Count < 2 || !tabs.Contains(tab)) return;
            int from = tabs.IndexOf(tab);
            var size = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            Release(tab);
            var window = app.OpenWindow(Profile, IsPrivate, adopt: tab, place: new Rectangle(at.X - grab.X, at.Y - grab.Y, size.Width, size.Height), torn: true);
            tear = new Tear { Tab = tab, Window = window, Grab = grab, HoldX = hold.X, HoldY = hold.Y, From = from, Heard = DateTime.UtcNow };
            window.FollowMouse(at, grab);
            tearWatch.Start();
        }

        void TearMove(Point at)
        {
            var t = tear;
            if (t == null) return;
            t.Heard = DateTime.UtcNow;
            if (t.Window.IsDisposed) { tear = null; return; }
            if (t.Strip != null)
            {
                if (!t.Strip.IsDisposed && t.Strip.tabs.Contains(t.Tab) && t.Strip.TabStripContains(at, 40))
                {
                    t.Strip.HoldTab(t.Tab, at, t.HoldX, t.HoldY, true);   // along the strip with the mouse
                    return;
                }
                // Pulled off that strip: back in its own window, under the mouse.
                if (!t.Strip.IsDisposed && t.Strip.tabs.Contains(t.Tab))
                {
                    t.Strip.HoldTab(t.Tab, at, t.HoldX, t.HoldY, false);
                    t.Strip.Release(t.Tab);
                }
                t.Strip = null;
                t.Window.Adopt(t.Tab);
                t.Window.FollowMouse(at, t.Grab);
                t.Window.HideForDrag(false);
                return;
            }
            // Over a strip (until its own window has taken the tab in, it stays there).
            var strip = t.Window.tabs.Contains(t.Tab) ? app.WindowToJoin(t.Window, at) : null;
            if (strip != null)
            {
                t.Window.Release(t.Tab);
                t.Window.HideForDrag(true);
                strip.Adopt(t.Tab);
                strip.RaiseForDrag();
                strip.HoldTab(t.Tab, at, t.HoldX, t.HoldY, true);
                t.Strip = strip;
                return;
            }
            t.Window.FollowMouse(at, t.Grab);
        }

        // Let go at that point of the screen; putBack: Esc.
        void TearEnd(Point at, bool putBack)
        {
            var t = tear;
            tear = null;
            tearWatch.Stop();
            if (t == null) return;
            var window = t.Window;
            bool inStrip = t.Strip != null && !t.Strip.IsDisposed && t.Strip.tabs.Contains(t.Tab);
            if (inStrip) t.Strip.HoldTab(t.Tab, at, t.HoldX, t.HoldY, false);   // put down where it is
            if (putBack)
            {
                if (inStrip) { if (t.Strip != this) { t.Strip.Release(t.Tab); Adopt(t.Tab); } }
                else if (!window.IsDisposed && window.tabs.Contains(t.Tab)) { window.Release(t.Tab); Adopt(t.Tab); }
                else if (!window.IsDisposed && window.adoptTab == t.Tab) { window.adoptTab = null; Adopt(t.Tab); }   // (not opened yet)
                MoveTab(t.Tab, t.From);
                window.CloseForDrag();
                return;
            }
            if (inStrip)
            {
                t.Strip.ComeForward();
                window.CloseForDrag();
                return;
            }
            if (window.IsDisposed) return;
            window.FollowMouse(at, t.Grab);
            window.EndFollowing(at);
        }

        // Should the interface's word about the button being let go never come, the tab is let go where the mouse is
        // once the button is up. (Test builds: not with NSB_TEST_TEAR, as checks give the mouse in steps.)
        void WatchTear()
        {
            var t = tear;
            if (t == null) { tearWatch.Stop(); return; }
#if DEBUG
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_TEAR"))) return;
#endif
            if ((DateTime.UtcNow - t.Heard).TotalMilliseconds > 1000 && !ButtonNow()) TearEnd(PointerNow(), false);
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        static bool LeftButtonDown() => (GetAsyncKeyState(GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0 ? 0x02 : 0x01) & 0x8000) != 0;

        // A window made for a dragged tab, put so that the mouse holds the tab where it took it (grab: see Tear).
        void FollowMouse(Point at, Point grab)
        {
            if (IsDisposed || !IsHandleCreated) return;
            var origin = ui.PointToScreen(Point.Empty);
            int x = at.X - grab.X - (origin.X - Left), y = at.Y - grab.Y - (origin.Y - Top);
            if (x != Left || y != Top) SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // Its tab was let go there: the window stays (its top kept on the screen) and comes forward.
        void EndFollowing(Point at)
        {
            torn = false;
            var area = Screen.FromPoint(at).WorkingArea;
            if (Top < area.Top) Top = area.Top;
            ComeForward();
        }

        // Out of sight while its tab is in another strip; back, in front, when the tab comes out again.
        void HideForDrag(bool hide)
        {
            if (IsDisposed || !IsHandleCreated || hiddenForDrag == hide) return;
            hiddenForDrag = hide;
            ShowWindow(Handle, hide ? 0 /* SW_HIDE */ : 4 /* SW_SHOWNOACTIVATE */);
            if (!hide) RaiseForDrag();
        }

        // Above the other windows while a tab is dragged in it, without taking the keyboard from the one dragging.
        void RaiseForDrag()
        {
            if (!IsDisposed && IsHandleCreated) SetWindowPos(Handle, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // The tab it was made for went elsewhere: it goes (once it has opened, if it has not yet).
        void CloseForDrag()
        {
            if (IsDisposed) return;
            torn = false;
            adoptTab = null;
            if (tabs.Count == 0 && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed && tabs.Count == 0) Close(); }));
        }

        // A tab dragged in from another window (or back into this one) while the mouse still holds it: the interface
        // moves it along the strip with the mouse (holding), or puts it down where it is.
        void HoldTab(BrowserTab tab, Point screen, int holdX, int holdY, bool holding)
        {
            var at = ui.PointToClient(screen);
            var message = new Dictionary<string, object> { ["id"] = tab.Id, ["x"] = at.X, ["y"] = at.Y, ["gx"] = holdX, ["gy"] = holdY, ["drag"] = holding };
            BeginInvoke(new Action(() => { if (!IsDisposed) Post("placeTab", message); }));   // after the tabs, when it has just come in
        }

        // ----- the card over a tab the mouse rests on (the interface's, as in Chrome) -----
        // What the interface cannot know: a picture of the tab's page when it is not the one on show (taken the last
        // time it was: see BrowserTab.TakePreview), and the memory of the process that runs its page (pages of one site
        // may share a process; a tab unloaded has none): in use, as Task Manager counts it, and reserved. Both are
        // given because IGYO lets go of idle memory: "in use" then drops near nothing while the memory stays reserved.
        async void SendTabCard(BrowserTab tab)
        {
            (long inUse, long reserved)? memory = null;
            try { memory = await TabMemoryAsync(tab); } catch { }
            if (IsDisposed || !tabs.Contains(tab)) return;
            var picture = tab != current && tab.Preview != null ? "data:image/jpeg;base64," + Convert.ToBase64String(tab.Preview) : null;
            int Mb(long bytes) => (int)Math.Round(bytes / 1048576.0);
            Post("tabCard", new Dictionary<string, object>
            {
                ["id"] = tab.Id,
                ["picture"] = picture,
                ["memory"] = memory.HasValue ? (object)Mb(memory.Value.inUse) : null,
                ["reserved"] = memory.HasValue ? (object)Mb(memory.Value.reserved) : null,
                ["state"] = tab.State == TabState.Sleeping ? "asleep" : tab.State == TabState.Unloaded ? "unloaded" : "live",
            });
        }

        static async Task<(long inUse, long reserved)?> TabMemoryAsync(BrowserTab tab)
        {
            var environment = BrowserEngine.Current;
            var core = tab.Core;
            if (environment == null || core == null) return null;
            uint frame;
            try { frame = core.FrameId; } catch { return null; }
            int pid = 0;
            foreach (var info in await environment.GetProcessExtendedInfosAsync())
            {
                if (info.ProcessInfo.Kind != CoreWebView2ProcessKind.Renderer) continue;
                if (info.AssociatedFrameInfos.Any(f => f.FrameId == frame)) { pid = info.ProcessInfo.ProcessId; break; }   // its page itself
                if (pid == 0 && info.AssociatedFrameInfos.Any(f => MemoryCare.Root(f) == frame)) pid = info.ProcessInfo.ProcessId;
            }
            if (pid == 0) return null;
            var usage = ProcessMemory.Usage(pid);
            return usage.reserved > 0 ? usage : ((long, long)?)null;
        }

        // A window's only tab let go over this window's strip (the window was dragged): it joins this one, where it was let go.
        public void TakeTab(BrowserTab tab, Point screen)
        {
            Adopt(tab);
            var at = ui.PointToClient(screen);
            // After the tabs are sent (Adopt asked for that first), the interface puts it in its place among them.
            BeginInvoke(new Action(() => Post("placeTab", new Dictionary<string, object> { ["id"] = tab.Id, ["x"] = at.X, ["y"] = at.Y })));
            ComeForward();
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        // ----- a window moved by its only tab, as in Chrome -----
        // Dragging a window's only tab hands the move to Windows, as when a title bar is dragged (a maximized window
        // comes down to its size, and the screen's edges snap it). The moment the mouse reaches another window's tab
        // row (same profile), the move ends and the tab goes into that row, moving along it with the mouse while the
        // button is down; pulled off it, this window is back under the mouse and Windows moves it again. Let go in a
        // row, the tab stays there and this window closes. Only while the left button is down: started without it,
        // Windows would keep the window on the mouse. (Run after the interface's message is handled: Windows' moving
        // goes on until the button is let go or the move is ended.)
        Point moveGrab;        // the mouse from the top left of the interface, as the move began
        Point moveHold;        // the mouse from the tab's own top left
        bool movingByTab;      // Windows is moving this window by its only tab
        MainForm moveJoin;     // the window whose row the mouse reached meanwhile
        sealed class HeldTab { public BrowserTab Tab; public MainForm Row; public Point Last; }
        HeldTab heldTab;       // this window's tab, in another window's row while the button is still down
        readonly Timer heldWatch = new Timer { Interval = 15 };

        // Where the mouse is and whether its button is down, as Windows says. (Test builds: as a check says, through
        // the interface's devPointer: the real button can't be held down for a check.)
        static Point? scriptedPointer;
        static bool scriptedDown;
        static Point PointerNow() => scriptedPointer ?? Cursor.Position;
        static bool ButtonNow() => scriptedPointer != null ? scriptedDown : LeftButtonDown();
#if DEBUG
        public static void Script(Point? at, bool down) { scriptedPointer = at; scriptedDown = down; }
#endif

        void DragWindow(Point grab, Point hold)
        {
            moveGrab = grab;
            moveHold = hold;
            BeginInvoke(new Action(DragWindowNow));
        }

        void DragWindowNow()
        {
            if (IsDisposed || tabs.Count != 1 || heldTab != null) return;
            bool down = ButtonNow();
#if DEBUG
            // Test builds: that the interface asked, for the checks (a check cannot hold the real mouse button down),
            // and, with NSB_TEST_DROP=x,y, the window let go at that point of the screen without being moved.
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-drag-window.txt"), "asked, left button down: " + down + "\r\n"); } catch { }
            var test = (Environment.GetEnvironmentVariable("NSB_TEST_DROP") ?? "").Split(',');
            if (test.Length == 2 && int.TryParse(test[0], out int tx) && int.TryParse(test[1], out int ty))
            {
                JoinAfterMove(new Point(tx, ty));
                return;
            }
#endif
            if (!down || fullScreen) return;
#if DEBUG
            if (scriptedPointer != null) { FollowScripted(); return; }   // a check: Windows' move stood in for
#endif
            movingByTab = true;
            moveJoin = null;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);   // until let go or ended (WndProc)
            movingByTab = false;
            if (IsDisposed) return;
            var join = moveJoin;
            moveJoin = null;
            if (join != null && !join.IsDisposed && tabs.Count == 1) HoldInRow(join, PointerNow());
            else JoinAfterMove(PointerNow());
        }

        // While Windows moves this window (WM_MOVING): over another window's tab row, the move ends there.
        void CheckRowWhileMoving()
        {
            if (moveJoin != null || tabs.Count != 1) return;
            var join = app.WindowToJoin(this, PointerNow());
            if (join == null) return;
            moveJoin = join;
            SendMessage(Handle, 0x001F /* WM_CANCELMODE: ends Windows' move, as Chromium does */, IntPtr.Zero, IntPtr.Zero);
        }

#if DEBUG
        // A check: the window follows the check's mouse as Windows would move it, with the same look for a row.
        void FollowScripted()
        {
            var follow = new Timer { Interval = 15 };
            follow.Tick += (s, e) =>
            {
                if (IsDisposed || tabs.Count != 1 || scriptedPointer == null) { follow.Dispose(); return; }
                var at = PointerNow();
                if (!ButtonNow()) { follow.Dispose(); JoinAfterMove(at); return; }
                FollowMouse(at, moveGrab);
                var join = app.WindowToJoin(this, at);
                if (join != null) { follow.Dispose(); HoldInRow(join, at); }
            };
            follow.Start();
        }
#endif

        // This window's only tab goes into another window's row and moves along it with the mouse; this window waits
        // out of sight until the button is let go (it then closes) or the tab is pulled off the row again.
        void HoldInRow(MainForm row, Point at)
        {
            var tab = tabs[0];
            Release(tab);
            HideForDrag(true);
            row.Adopt(tab);
            row.RaiseForDrag();
            row.HoldTab(tab, at, moveHold.X, moveHold.Y, true);
            heldTab = new HeldTab { Tab = tab, Row = row, Last = at };
            heldWatch.Start();
        }

        void WatchHeld()
        {
            var h = heldTab;
            if (h == null) { heldWatch.Stop(); return; }
            var at = PointerNow();
            if (h.Row.IsDisposed || !h.Row.tabs.Contains(h.Tab))
            {
                heldWatch.Stop();
                heldTab = null;
                CloseForDrag();
                return;
            }
            if (!ButtonNow())
            {
                // Let go in the row: the tab stays there.
                heldWatch.Stop();
                heldTab = null;
                h.Row.HoldTab(h.Tab, at, moveHold.X, moveHold.Y, false);
                h.Row.ComeForward();
                CloseForDrag();
                return;
            }
            if (at == h.Last) return;
            h.Last = at;
            if (h.Row.TabStripContains(at, 40)) { h.Row.HoldTab(h.Tab, at, moveHold.X, moveHold.Y, true); return; }
            // Pulled off the row: back in this window, under the mouse, and Windows moves it again.
            heldWatch.Stop();
            heldTab = null;
            h.Row.HoldTab(h.Tab, at, moveHold.X, moveHold.Y, false);
            h.Row.Release(h.Tab);
            Adopt(h.Tab);
            FollowMouse(at, moveGrab);
            HideForDrag(false);
            BeginInvoke(new Action(DragWindowNow));
        }

        // The window was let go with its only tab over another window's row: the tab joins that window.
        void JoinAfterMove(Point at)
        {
            if (tabs.Count != 1) return;
            var join = app.WindowToJoin(this, at);
            if (join == null) return;
            var tab = tabs[0];
            Release(tab);
            join.TakeTab(tab, at);
            BeginInvoke(new Action(Close));   // nothing is left in this one
        }

        // Lets go of a tab without closing it: it goes on in another window.
        void Release(BrowserTab tab)
        {
            int index = tabs.IndexOf(tab);
            if (index < 0) return;
            if (tab == current) CloseExtensionPopup();
            tabs.RemoveAt(index);
            openedBehind.Remove(tab);
            devtools.CloseFor(tab);
            Unwatch(tab);
            tab.Detach();
            if (tab == current)
            {
                current = null;
                if (tabs.Count > 0) Select(tabs[Math.Min(index, tabs.Count - 1)]);   // (none left: the window closes, see JoinAfterMove)
            }
            MarkDirty(tabs: true);
        }

        // Takes in a tab another window let go of, and shows it.
        void Adopt(BrowserTab tab)
        {
            tab.MoveTo(this);
            Watch(tab);
            tabs.Add(tab);
            Select(tab);
        }

        BrowserTab NewTab(string url, bool activate, bool atEnd)
        {
            var tab = CreateTab(url);
            int index = atEnd || current == null ? tabs.Count : tabs.IndexOf(current) + 1;
            tabs.Insert(index, tab);
            if (activate) Select(tab);
            else
            {
                MarkDirty(tabs: true);
                _ = tab.EnsureLiveAsync();
            }
            return tab;
        }

        BrowserTab TabById(int id) => tabs.FirstOrDefault(t => t.Id == id);

        // quiet: for Claude (see MainForm.Bridge): the keyboard stays where it is, so a window Claude works in is
        // never brought forward or given the keyboard by it.
        async void Select(BrowserTab tab, bool quiet = false)
        {
            if (tab == null || !tabs.Contains(tab)) return;
            HideSuggestions();
            if (current != tab) CloseExtensionPopup();   // it was opened over the other tab
            if (current != tab)
            {
                current?.Hide();
                current = tab;
                openedBehind.Clear();
            }
            TranslationFollows(null);   // a translation shown over the page that was on show goes with it
            UpdateChrome();
            try
            {
                await tab.EnsureLiveAsync();
                if (current != tab || WindowState == FormWindowState.Minimized) return;
                tab.Show();
                devtools.ShowFor(tab);
                UpdateChrome();
                if (quiet) { }
                else if (tab.IsBlank) FocusAddress();
                else if (ContainsFocus && !addressFocused && !uiModal) FocusTab(tab);
            }
            catch (Exception ex)
            {
                ShowNotice(L.T("This tab couldn't start: ") + ex.Message);
            }
        }

        public void CloseTab(BrowserTab tab) => CloseTab(tab, false);

        void CloseTab(BrowserTab tab, bool quiet)
        {
            int index = tabs.IndexOf(tab);
            if (index < 0) return;
            if (!tab.IsBlank && !IsPrivate) closedTabs.Push(tab.Url);
            tabs.RemoveAt(index);
            openedBehind.Remove(tab);
            devtools.CloseFor(tab);
            app.Bridge.TabClosed(tab);
            tab.Close();
            if (tabs.Count == 0)
            {
                Close();
                return;
            }
            if (tab == current)
            {
                current = null;
                Select(tabs[Math.Min(index, tabs.Count - 1)], quiet);
            }
            MarkDirty(tabs: true);
            app.EndPreviewIfEditorGone();
        }

        void CloseTabs(IEnumerable<BrowserTab> which)
        {
            foreach (var tab in which.ToList()) CloseTab(tab);
        }

        void MoveTab(BrowserTab tab, int index)
        {
            int from = tabs.IndexOf(tab);
            if (from < 0) return;
            index = Math.Max(0, Math.Min(tabs.Count - 1, index));
            if (index == from) return;
            tabs.RemoveAt(from);
            tabs.Insert(index, tab);
            MarkDirty(tabs: true);
        }

        void ReopenClosedTab()
        {
            if (closedTabs.Count > 0) NewTab(closedTabs.Pop(), true, false);
        }

        void SwitchTab(int step)
        {
            if (tabs.Count < 2 || current == null) return;
            int i = (tabs.IndexOf(current) + step + tabs.Count) % tabs.Count;
            Select(tabs[i]);
        }

        void OpenStartTabs()
        {
            if (adoptTab != null)   // a window made for a tab dragged out of another one: that tab is all it has
            {
                Adopt(adoptTab);
                adoptTab = null;
                return;
            }
            if (madeForTab) return;   // its tab went back before it opened (Esc): it closes (see CloseForDrag)
            var urls = startUrls.Select(u => AddressInput.ToUrl(u, Settings.SearchUrl)).Where(u => u != null).ToList();
            // Last time's tabs come back even when IGYO is started by a link from another program: the link opens
            // as one more tab after them. (Before, starting from a link dropped the saved tabs for good.)
            if (session != null && (Settings.RestoreTabs || tabsComeBack) && session.Tabs != null && session.Tabs.Count > 0)
            {
                for (int i = 0; i < session.Tabs.Count; i++)
                {
                    var title = session.Titles != null && i < session.Titles.Count ? session.Titles[i] : null;
                    tabs.Add(CreateTab(session.Tabs[i], title));   // restored tabs load only when you open them
                }
                if (urls.Count == 0)
                {
                    Select(tabs[Math.Max(0, Math.Min(session.Active, tabs.Count - 1))]);
                    return;
                }
            }
            if (urls.Count == 0) urls.Add(HomeUrl);
            foreach (var url in urls) NewTab(url, true, true);
        }

        void OnTabChanged(BrowserTab tab)
        {
            if (tab == current) UpdateChrome();
            else MarkDirty(tabs: true);
            TranslationFollows(tab);    // a translation belongs to the page it was asked on
            app.Bridge.TabMoved(tab);   // what Claude was allowed "this time" ends when the tab leaves that site
            if (app.Previewing) app.EndPreviewIfEditorGone();   // the Settings tab may just have gone somewhere else
        }

        public void OnTabReady(BrowserTab tab)
        {
            ApplyProfileSettings(tab.Core);
            LoadExtensions(false);   // once for the profile: what stands in the extensions folder
            if (!IsPrivate && Profile.EnginePath != tab.Core.Profile.ProfilePath)
            {
                Profile.EnginePath = tab.Core.Profile.ProfilePath;
                app.Profiles.Save();
            }
            CheckGoogleSession();
        }

        // A page finished loading. If it is one of Google's, signing in or out may just have happened.
        public void OnTabLoaded(BrowserTab tab)
        {
            app.Memory.PageLoaded();
            if (GoogleSession.IsGoogleSite(tab.Url)) CheckGoogleSession();
        }

        // Whether this window's profile is signed in on Google's sites, as its own cookies say (see GoogleSession).
        // The profile menu and Settings show it; a private window has its own answer.
        public bool GoogleSignedIn { get; private set; }

        public async void CheckGoogleSession() => await RefreshGoogleSessionAsync();

        async Task RefreshGoogleSessionAsync()
        {
            try
            {
                var core = tabs.Select(t => t.Core).FirstOrDefault(c => c != null);   // any view of this profile can answer
                if (core == null || IsDisposed) return;
                var reading = GoogleSession.ReadAsync(core);
                if (await Task.WhenAny(reading, Task.Delay(400)) != reading) return;   // never keeps a menu waiting
                var answer = await reading;
                if (answer == null || IsDisposed || answer.Value == GoogleSignedIn) return;
                GoogleSignedIn = answer.Value;
                RefreshSettingsPages();
            }
            catch { }
        }

        void ApplyProfileSettings(CoreWebView2 core)
        {
            if (core == null) return;
            try
            {
                var p = core.Profile;
                // Websites are told which look is wanted: dark when websites are to be shown dark (those that have a
                // dark look of their own then use it), otherwise the theme's.
                p.PreferredColorScheme = Settings.ForceDark ? CoreWebView2PreferredColorScheme.Dark
                    : ThemeStore.Normalize(app.ThemeOf(Profile)) == ThemeStore.Auto ? CoreWebView2PreferredColorScheme.Auto
                    : CurrentTheme.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                p.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
                p.IsPasswordAutosaveEnabled = Settings.SavePasswords && !IsPrivate;
                if (!string.IsNullOrWhiteSpace(Settings.DownloadFolder)) p.DefaultDownloadFolderPath = Settings.DownloadFolder;
            }
            catch { }
        }

        // A tab that is opened without going to it: it stands right of the tab on show, after the ones opened from
        // that tab the same way before it (so several links opened in a row stand in the order they were opened).
        BrowserTab NewTabBehind(string url)
        {
            var tab = CreateTab(url);
            int index = current == null || !tabs.Contains(current) ? tabs.Count : tabs.IndexOf(current) + 1;
            while (index < tabs.Count && openedBehind.Contains(tabs[index])) index++;
            tabs.Insert(index, tab);
            openedBehind.Add(tab);
            MarkDirty(tabs: true);
            _ = tab.EnsureLiveAsync();
            return tab;
        }

        // The tabs opened behind the one on show since it came on show. Forgotten when another tab is chosen: the
        // next one opened behind then stands right of the tab on show again, as in other browsers.
        readonly HashSet<BrowserTab> openedBehind = new HashSet<BrowserTab>();

        // Whether the tab a page is asking for right now is to be opened behind the one on show: the link was
        // clicked with the middle button, or with Ctrl held, as in other browsers (with Shift held as well it is
        // gone to, as there). Settings can have IGYO go to such a tab every time.
        bool OpensBehind()
        {
            if (Settings.SwitchToNewTabs) return false;
            bool middle = MiddleClick.Take();
            return (middle || MiddleClick.CtrlDown) && !MiddleClick.ShiftDown;
        }

        public async void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                // The engine's own pages send to its settings page (the history page's "Delete browsing data" does),
                // which the engine does not have in an app like this one: no tab is opened for it, and when it
                // was one of the engine's pages that asked, IGYO's own Settings answer, at the part that clears
                // browsing data. (A website asking for that address gets nothing.)
                string wanted = "";
                try { wanted = e.Uri ?? ""; } catch { }
                if (IsEngineSettingsUrl(wanted))
                {
                    e.Handled = true;
                    var asking = tabs.FirstOrDefault(t => t.Core != null && ReferenceEquals(t.Core, sender));
                    if (asking != null && (asking.Url ?? "").StartsWith("edge://", StringComparison.OrdinalIgnoreCase)) OpenSettings("privacy");
                    return;
                }
                bool popup = false;
                try { popup = e.WindowFeatures != null && (e.WindowFeatures.HasSize || e.WindowFeatures.HasPosition); } catch { }
                bool behind = e.IsUserInitiated && !popup && OpensBehind();
#if DEBUG
                // Test builds only: what was asked for and what was decided, for the checks to read.
                try
                {
                    File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-newwindow.txt"),
                        $"asked by the user: {e.IsUserInitiated}, a popup: {popup}, opened behind: {behind}, the mouse: {MiddleClick.State}\n");
                }
                catch { }
#endif
                var tab = behind ? NewTabBehind(null) : NewTab(null, true, false);
                await tab.EnsureLiveAsync();
                if (tab.Core == null) return;
                e.NewWindow = tab.Core;   // the page's window.open() lands in this tab
                e.Handled = true;
            }
            catch
            {
                e.Handled = false;
            }
            finally
            {
                deferral.Complete();
            }
        }

        // A tab's icon as the site sent it (for the interface's /icon/tab/<id>).
        public byte[] TabIcon(string id) => int.TryParse(id, out int n) ? TabById(n)?.FaviconPng : null;

        public byte[] AvatarPng(string profileId)
        {
            if (string.Equals(profileId, Profile.Id, StringComparison.OrdinalIgnoreCase)) return InternalAssets.AvatarPng(Profile, IsPrivate);
            var other = app.Profiles.Find(profileId);
            return other == null ? null : InternalAssets.AvatarPng(other, false);
        }

        // ----- full screen (zoom: MainForm.Zoom.cs) -----

        void ToggleWindowFullScreen()
        {
            windowFullScreen = !windowFullScreen;
            SetFullScreen(windowFullScreen);
            if (windowFullScreen) ShowNotice(L.T("Full screen. Press F11 to exit."));
        }

        public void OnFullScreenChanged(BrowserTab tab, bool on)
        {
            if (tab != current || windowFullScreen) return;
            SetFullScreen(on);
        }

        // In full screen the pointer at the very top of the screen brings down a round ✕ that leaves full screen, as in
        // Chrome. The interface draws it; the page under the pointer has the mouse there, so the window watches where
        // the pointer is and tells the interface. The ✕ goes again once the pointer is well below it.
        readonly Timer fullScreenTop = new Timer { Interval = 100 };
        bool exitShown;

        void WatchFullScreenTop()   // (WindowFromPoint and GetAncestor: MainForm.Zoom.cs)
        {
            if (!fullScreen || IsDisposed || WindowState == FormWindowState.Minimized) { ShowExitButton(false); fullScreenTop.Stop(); return; }
            var at = PointerNow();
            var screen = RectangleToScreen(ClientRectangle);
            bool over = at.X >= screen.Left && at.X < screen.Right && at.Y >= screen.Top && at.Y < screen.Bottom;
            // (Only over this window: not when another window lies on top there.)
            if (over && scriptedPointer == null) over = GetAncestor(WindowFromPoint(at), 2 /* GA_ROOT */) == Handle;
            if (!over) { ShowExitButton(false); return; }
            int y = at.Y - screen.Top;
            if (!exitShown && y <= S(2)) ShowExitButton(true);
            else if (exitShown && y > S(110)) ShowExitButton(false);
        }

        void ShowExitButton(bool on)
        {
            if (exitShown == on) return;
            exitShown = on;
            if (ui.Ready) Post("fsExit", new Dictionary<string, object> { ["show"] = on });
        }

        // The ✕: the window's full screen ends (F11's), or a page's own full screen (a video's), as Esc would end it.
        void LeaveFullScreen()
        {
            ShowExitButton(false);
            if (windowFullScreen) { ToggleWindowFullScreen(); return; }
            if (!fullScreen) return;
            try { _ = current?.Core?.ExecuteScriptAsync("document.fullscreenElement && document.exitFullscreen()"); } catch { }
        }

        void SetFullScreen(bool on)
        {
            if (on == fullScreen) return;
            SuspendLayout();
            fullScreen = on;
            insetsKnown = false;
            if (on) fullScreenTop.Start();
            else { fullScreenTop.Stop(); ShowExitButton(false); }
            if (on)
            {
                stateBeforeFullScreen = WindowState;
                boundsBeforeFullScreen = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                FormBorderStyle = FormBorderStyle.None;
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = stateBeforeFullScreen;
                if (stateBeforeFullScreen == FormWindowState.Normal && boundsBeforeFullScreen.Width > 0) Bounds = boundsBeforeFullScreen;
                Theme.ApplyWindow(this, CurrentTheme);
            }
            ResumeLayout();
            PostWindow();
            LayoutViews();
        }

        public void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                // (Started again the way IGYO restarts itself, with this time's tabs, not with the words it was first
                // started with: those may hold a link handed over by Windows, which must stay one link; see CommandLine.)
                ShowNotice(L.T("The browser engine stopped."), L.T("Restart the browser"), () => app.Restart(this));
        }

        bool housekeeping;   // a round still asking pages when the next one is due: that one is left out

        async void Housekeeping()
        {
            if (housekeeping) return;
            housekeeping = true;
            try
            {
                var now = DateTime.Now;
                app.Memory.Tick();
                // The tab on show sleeps once the window has been minimized a while, but not while it plays, floats
                // something, is in a call or is on a site kept awake: then it is asked again next round.
                var shown = current;
                if (WindowState == FormWindowState.Minimized && Settings.SleepWhenMinimized && !sleptWhileMinimized && shown != null
                    && (now - minimizedAt).TotalSeconds >= Settings.MinimizedSleepSeconds
                    && !shown.IsPlayingAudio && !shown.OnAwakeList && !await shown.CheckInUseAsync()
                    && WindowState == FormWindowState.Minimized && !sleptWhileMinimized && shown == current)
                {
                    sleptWhileMinimized = true;
                    await shown.SleepAsync();
                    MemoryTrim.Trim();
                }
                foreach (var tab in tabs.ToList())
                {
                    if (tab == current || tab.State == TabState.Unloaded) continue;
                    // Floating a video or in a call: asked again every round. Once that ends, its time in the
                    // background starts over (Hide), as if it had just been left.
                    if (tab.InPictureInPicture || tab.InCall)
                    {
                        if (!await tab.CheckInUseAsync() && tab != current && tabs.Contains(tab)) tab.Hide();
                        continue;
                    }
                    if (tab.IsPlayingAudio || tab.OnAwakeList) continue;
                    var idle = (now - tab.HiddenSince).TotalMinutes;
                    bool unload = Settings.UnloadAfterMinutes > 0 && idle >= Settings.UnloadAfterMinutes;
                    bool sleep = !unload && Settings.SleepAfterMinutes > 0 && tab.State == TabState.Live && idle >= Settings.SleepAfterMinutes
                        && (DateTime.Now - tab.LastSleepTry).TotalSeconds >= 60;   // (the engine may decline: tried again a minute later)
                    if (!unload && !sleep) continue;
                    // A call or a floating window that began after the tab was left is found here, before anything is
                    // done to it. (A sleeping page can't have begun one.)
                    if (tab.State == TabState.Live && await tab.CheckInUseAsync()) continue;
                    if (tab == current || !tabs.Contains(tab)) continue;   // chosen or closed meanwhile
                    if (unload) tab.Unload();
                    else await tab.SleepAsync();
                }
#if DEBUG
                // Test builds only: lets the automated test see each tab's state.
                try
                {
                    File.WriteAllText(Path.Combine(AppPaths.DataDir, $"debug-tabs-{Profile.Id}{(IsPrivate ? "-private" : "")}.json"), Json.Write(tabs.Select(t => new Dictionary<string, object>
                    {
                        ["title"] = t.Title, ["url"] = t.Url, ["state"] = t.State.ToString(), ["current"] = t == current,
                        ["visible"] = t.View?.Visible ?? false, ["suspended"] = t.Core?.IsSuspended ?? false, ["sleep"] = t.LastSleepResult,
                        ["call"] = t.InCall, ["pip"] = t.InPictureInPicture, ["audio"] = t.IsPlayingAudio, ["listed"] = t.OnAwakeList,
                    }).ToArray()));
                }
                catch { }
#endif
            }
            finally { housekeeping = false; }
        }

        // ----- address bar -----

        void NavigateFromAddress(string text, bool inNewTab)
        {
            HideSuggestions();
            var url = AddressInput.ToUrl(text, Settings.SearchUrl);
            if (url == null) return;
            if (IsEngineSettingsUrl(url)) { OpenSettings(); return; }   // (typed out of habit: the settings there are to see are IGYO's)
            var tab = inNewTab || current == null ? NewTab(null, true, true) : current;
            tab.NavigateTo(url);
            addressFocused = false;
            FocusTab(tab);
        }

        void FocusAddress()
        {
            if (!ui.Ready) { focusAddressWhenReady = true; return; }
            ui.FocusView();
            Post("focusAddress");
        }

        void FocusPage()
        {
            addressFocused = false;
            if (current != null && (!current.IsBlank || current.IsNewTabPage)) FocusTab(current);
        }

        // ----- Google's suggestions (only when turned on in Settings; never in private windows) -----

        bool SuggestionsWanted => Settings.SearchSuggestions && !IsPrivate;

        void ScheduleSuggestions(string text)
        {
            suggestTimer.Stop();
            suggestText = text ?? "";
            if (!SuggestionsWanted || !GoogleSuggest.ShouldAsk(suggestText))
            {
                HideSuggestions();
                return;
            }
            suggestTimer.Start();
        }

        async Task FetchSuggestionsAsync()
        {
            suggestTimer.Stop();
            var typed = suggestText;
            if (!SuggestionsWanted || !addressFocused || !GoogleSuggest.ShouldAsk(typed)) { HideSuggestions(); return; }
            suggestCancel?.Cancel();
            var cancel = suggestCancel = new System.Threading.CancellationTokenSource();
            List<Suggestion> found;
            try { found = await GoogleSuggest.FetchAsync(typed, cancel.Token); }
            catch { return; }   // offline, slow or refused: just no list
            if (cancel.IsCancellationRequested || IsDisposed || !addressFocused || suggestText != typed) return;
            var engine = Settings.SearchEngine == "Custom" || !Settings.SearchEngines.ContainsKey(Settings.SearchEngine ?? "") ? L.T("the web") : Settings.SearchEngine;
            Post("suggestions", new Dictionary<string, object>
            {
                ["typed"] = typed,
                ["engine"] = engine,
                ["typedIsAddress"] = AddressInput.ToUrl(typed, q => null) != null,
                ["items"] = found.Select(s => new Dictionary<string, object>
                {
                    ["text"] = s.Text, ["site"] = s.IsSite, ["title"] = s.Title ?? "",
                    ["display"] = s.IsSite ? GoogleSuggest.DisplayUrl(s.Text) : s.Text,
                    ["icon"] = s.IsSite ? SiteIcon(s.Text) : "",
                }).ToList(),
            });
        }

        void HideSuggestions()
        {
            suggestTimer.Stop();
            suggestCancel?.Cancel();
            Post("suggestions", new Dictionary<string, object> { ["typed"] = "", ["items"] = new object[0] });
        }

        // ----- what the interface shows -----

        void UpdateChrome()
        {
            UpdateTitle();
            MarkDirty(tabs: true, nav: true);
        }

        void UpdateTitle()
        {
            var page = current == null || current.IsBlank ? null : current.Title;
            var suffix = IsPrivate ? L.T(" (Private)") : app.Profiles.Profiles.Count > 1 ? $" ({Profile.Name})" : "";
            Text = (string.IsNullOrEmpty(page) ? "" : page + " - ") + InstallInfo.AppName + suffix;
        }

        bool IsCurrentBookmarked => current != null && !current.IsBlank && bookmarks.FindByUrl(current.Url) != null;

        void MarkDirty(bool tabs = false, bool nav = false, bool bookmarks = false)
        {
            tabsDirty |= tabs;
            navDirty |= nav;
            bookmarksDirty |= bookmarks;
            if (flushQueued || !IsHandleCreated || IsDisposed) return;
            flushQueued = true;
            BeginInvoke(new Action(Flush));
        }

        void Flush()
        {
            flushQueued = false;
            if (IsDisposed || !ui.Ready) return;
            if (tabsDirty) { tabsDirty = false; Post("tabs", TabsState()); }
            if (navDirty) { navDirty = false; Post("nav", NavState()); }
            if (zoomPopupQueued) { zoomPopupQueued = false; Post("zoomShow"); }   // after the new zoom (MainForm.Zoom.cs)
            if (bookmarksDirty) { bookmarksDirty = false; Post("bookmarks", BookmarksState()); }
        }

        void Post(string type, Dictionary<string, object> data = null)
        {
            var message = data ?? new Dictionary<string, object>();
            message["t"] = type;
            ui.Post(message);
        }

        static string SiteIcon(string url)
        {
            var host = Favicons.HostOf(url);
            return host != null && Favicons.Has(host) ? "/icon/site/" + Uri.EscapeDataString(host) : "";
        }

        Dictionary<string, object> TabsState() => new Dictionary<string, object>
        {
            ["active"] = current?.Id ?? 0,
            ["list"] = tabs.Select(t => new Dictionary<string, object>
            {
                ["id"] = t.Id,
                ["title"] = t.Title ?? "",
                ["url"] = t.IsBlank ? "" : t.Url,
                ["icon"] = t.IsNewTabPage || string.IsNullOrEmpty(t.Url) ? "home"
                    : InternalPages.IsInternal(t.Url) ? "settings"
                    : IsHistoryUrl(t.Url) ? "history"
                    : t.FaviconPng != null ? $"/icon/tab/{t.Id}?v={t.IconVersion}" : SiteIcon(t.Url),
                ["loading"] = t.IsLoading,
                ["audio"] = t.IsPlayingAudio,
                ["state"] = t == current || t.State == TabState.Live ? "live" : t.State == TabState.Sleeping ? "asleep" : "unloaded",
            }).ToList(),
        };

        Dictionary<string, object> NavState()
        {
            var tab = current;
            var core = tab?.Core;
            var url = tab == null || tab.IsBlank ? "" : tab.Url;
            bool settings = SettingsPage.IsUrl(tab?.Url);
            return new Dictionary<string, object>
            {
                ["tab"] = tab?.Id ?? 0,
                ["url"] = settings ? "" : url,
                ["title"] = tab == null || tab.IsBlank ? "" : tab.Title ?? "",
                ["kind"] = settings ? "settings" : url.Length == 0 ? "blank" : url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "secure" : "open",
                ["canBack"] = core != null && core.CanGoBack,
                ["canForward"] = core != null && core.CanGoForward,
                ["loading"] = tab != null && tab.IsLoading,
                ["bookmarked"] = IsCurrentBookmarked,
                ["canBookmark"] = tab != null && !tab.IsBlank && !settings,
                ["blocked"] = tab?.Blocked ?? 0,
                ["volume"] = (int)Math.Round((tab?.Volume ?? 1) * 100),   // this tab's volume in percent (its button, or the tools button, says when it is not 100)
                ["website"] = core != null && !tab.IsBlank && !InternalPages.IsInternal(tab.Url),   // a page the tools can act on
                ["translated"] = PageIsTranslated,   // shown translated by Google Translate's website: the translate tool then shows the original
                ["zoom"] = (int)Math.Round((tab?.ZoomFactor ?? 1) * 100),   // the page's zoom in percent: the address bar shows a button for it when it is not 100
            };
        }

        Dictionary<string, object> BookmarksState() => new Dictionary<string, object>
        {
            ["bar"] = bookmarks.Bar.Children.Select(BookmarkJson).ToList(),
            ["other"] = bookmarks.Other.Children.Select(BookmarkJson).ToList(),
            ["otherId"] = bookmarks.Other.Id,
        };

        static Dictionary<string, object> BookmarkJson(Bookmark b)
        {
            var d = new Dictionary<string, object> { ["id"] = b.Id, ["title"] = b.Title ?? "" };
            if (b.IsFolder) d["children"] = b.Children.Select(BookmarkJson).ToList();
            else
            {
                d["url"] = b.Url ?? "";
                d["icon"] = SiteIcon(b.Url);
            }
            return d;
        }

        Bookmark BookmarkById(int id) => bookmarks.Roots.SelectMany(BookmarkStore.Flatten).FirstOrDefault(b => b.Id == id);

        Dictionary<string, object> SettingsState() => new Dictionary<string, object>
        {
            ["compact"] = Settings.Compact,
            ["animations"] = Settings.Animations,
            ["bookmarksBar"] = Settings.ShowBookmarksBar,
            ["toolsButton"] = Settings.ShowToolsButton,
            ["tools"] = Settings.ToolbarTools.ToList(),   // the tools pinned to the toolbar, left to right (an extension's as "ext:" and its name)
            ["extensions"] = ExtensionsState(),           // this profile's extensions: what the toolbar draws their buttons from
            ["downloadsButton"] = Settings.ShowDownloadsButton,
            ["adBlock"] = Settings.AdBlock,
            ["dark"] = Settings.ForceDark,
            ["enhance"] = Settings.EnhanceVideos,
            ["engine"] = Settings.SearchEngine == "Custom" || !Settings.SearchEngines.ContainsKey(Settings.SearchEngine ?? "") ? L.T("the web") : Settings.SearchEngine,
        };

        Dictionary<string, object> ProfileState() => new Dictionary<string, object>
        {
            ["id"] = Profile.Id,
            ["name"] = Profile.Name,
            ["avatar"] = "/avatar/" + Uri.EscapeDataString(Profile.Id) + "?v=" + DateTime.UtcNow.Ticks,
            ["several"] = app.Profiles.Profiles.Count > 1,
        };

        Dictionary<string, object> WindowState_() => new Dictionary<string, object>
        {
            ["active"] = isActive || QuietStartActive,
            ["maximized"] = WindowState == FormWindowState.Maximized,
            ["fullscreen"] = fullScreen,
        };

        Dictionary<string, object> ThemeState()
        {
            var meta = ThemeStore.Meta(ThemeDef);
            meta["v"] = app.ThemeStamp;
            return meta;
        }

        // Told only when something in it changed: every step of a resize used to send it, and the interface then drew
        // its window buttons again and laid itself out once more for nothing.
        string windowStateSent;
        void PostWindow()
        {
            if (ui == null || !ui.Ready) return;
            var state = WindowState_();
            var key = string.Join("|", state.Values);
            if (key == windowStateSent) return;
            windowStateSent = key;
            Post("window", state);
        }

        void SendEverything()
        {
            Post("init", new Dictionary<string, object>
            {
                ["app"] = InstallInfo.AppName,
                ["isPrivate"] = IsPrivate,
                ["profile"] = ProfileState(),
                ["settings"] = SettingsState(),
                ["theme"] = ThemeState(),
                ["window"] = WindowState_(),
                ["bridge"] = BridgeState(),
            });
            windowStateSent = string.Join("|", WindowState_().Values);   // (what the interface was just given)
            tabsDirty = navDirty = bookmarksDirty = true;
            Flush();
            if (focusAddressWhenReady || (current != null && current.IsBlank && ContainsFocus))
            {
                focusAddressWhenReady = false;
                FocusAddress();
            }
        }

        // ----- look -----

        // A look changed: this window takes its profile's again (another profile's change leaves it as it is).
        void OnThemeChanged(string profileId)
        {
            if (profileId != null && !string.Equals(profileId, Profile.Id, StringComparison.OrdinalIgnoreCase)) return;
            theme = app.ThemeFor(Profile);
            var t = theme;
            BackColor = t.Frame;
            devtools.ApplyTheme(t);
            ContentHost.BackColor = t.Page;
            Theme.ApplyWindow(this, t);
            foreach (var tab in tabs)
            {
                tab.ApplyAppearance();
                ApplyProfileSettings(tab.Core);
                // IGYO's own pages take the new look without reloading (a theme being edited keeps its editor open).
                if (InternalPages.IsInternal(tab.Url) && tab.Core != null && !tab.Core.IsSuspended)
                    try { _ = tab.Core.ExecuteScriptAsync("window.IGYOTheme && window.IGYOTheme.reload()"); } catch { }
            }
            if (ui.Ready) Post("theme", new Dictionary<string, object> { ["theme"] = ThemeState() });
            Invalidate();
            if (sign != null && sign.IsShown) UpdateSign();   // Claude's sign takes the new colors too
        }

        // The interface, New Tab pages and Settings are loaded again: they take their words when they load.
        public void ReloadOwnPages()
        {
            foreach (var tab in tabs) tab.Rename();
            UpdateTitle();
            ui.Reload();
            if (sign != null && sign.IsShown) UpdateSign();   // (and Claude's sign its words)
            foreach (var tab in tabs)
                if (InternalPages.IsInternal(tab.Url) && tab.Core != null)
                    try { if (tab.Core.IsSuspended) tab.Core.Resume(); tab.Core.Reload(); } catch { }
        }

        // Open New Tab pages pick up a new search engine or animation setting.
        void RefreshNewTabPages()
        {
            foreach (var tab in tabs)
                if (tab.IsNewTabPage && tab.Core != null && !tab.Core.IsSuspended)
                    try { tab.Core.Reload(); } catch { }
        }

        void OnSettingsChanged()
        {
            if (isActive) MiddleClick.Watch(!Settings.SwitchToNewTabs);
            foreach (var tab in tabs)
            {
                tab.SetAdBlock(Settings.AdBlock);
                ApplyProfileSettings(tab.Core);
                tab.ApplyDarkMode();     // websites shown dark, or not any more
                tab.ApplyEnhance();      // videos sharpened, or not any more
                tab.ApplyAppearance();
            }
            RefreshSettingsPages();
            var look = Settings.SearchTemplate() + "|" + Settings.SearchEngine + "|" + Settings.Animations;
            if (newTabLook != null && newTabLook != look) RefreshNewTabPages();
            newTabLook = look;
            if (ui.Ready)
            {
                Post("settings", new Dictionary<string, object> { ["settings"] = SettingsState(), ["profile"] = ProfileState() });
                MarkDirty(nav: true);
            }
            UpdateTitle();
        }

        // A site's icon is new or changed: show it on the bookmarks bar if that site is bookmarked.
        void OnFaviconChanged(string host)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (bookmarks.Roots.SelectMany(BookmarkStore.Flatten).Any(b => !b.IsFolder && Favicons.HostOf(b.Url) == host))
                BeginInvoke(new Action(() => MarkDirty(bookmarks: true)));
        }

        void OnBookmarksChanged()
        {
            MarkDirty(nav: true, bookmarks: true);
            RefreshSettingsPages();   // the bookmark manager shows the change too
        }

        // Settings pages that are open read their facts again (they keep what is being typed and any open editor).
        internal void RefreshSettingsPages()
        {
            foreach (var tab in tabs)
                if (SettingsPage.IsUrl(tab.Url) && tab.Core != null && !tab.Core.IsSuspended)
                    try { _ = tab.Core.ExecuteScriptAsync("window.IGYOSettings && window.IGYOSettings.refresh()"); } catch { }
        }

        // ----- bookmarks -----

        // "behind": asked for with the middle button or with Ctrl held, so the new tab is not gone to (unless
        // Settings says to go to such tabs).
        void OpenBookmark(Bookmark b, bool inNewTab, bool behind = false)
        {
            if (b == null || b.IsFolder) return;
            if (inNewTab && behind && current != null && !Settings.SwitchToNewTabs) NewTabBehind(b.Url);
            else if (inNewTab || current == null) NewTab(b.Url, true, false);
            else
            {
                current.NavigateTo(b.Url);
                FocusTab(current);
            }
        }

        void EditBookmarkForCurrentPage()
        {
            var tab = current;
            if (tab == null || tab.IsBlank || SettingsPage.IsUrl(tab.Url)) return;
            var existing = bookmarks.FindByUrl(tab.Url);
            if (existing != null)
            {
                EditBookmark(existing, L.T("Edit bookmark"));
                return;
            }
            existing = Bookmark.Link(tab.Title, tab.Url);
            bookmarks.Bar.Children.Add(existing);
            bookmarks.NotifyChanged();
            EditBookmark(existing, L.T("Bookmark added"));
        }

        async void EditBookmark(Bookmark b, string heading)
        {
            if (b == null || bookmarks.Roots.Contains(b)) return;
            var inside = new HashSet<Bookmark>(BookmarkStore.Flatten(b));
            var folders = bookmarks.Folders().Where(f => !inside.Contains(f.folder)).ToList();
            var answer = await AskAsync(new Dictionary<string, object>
            {
                ["kind"] = "bookmark",
                ["title"] = heading,
                ["name"] = b.Title,
                ["url"] = b.Url ?? "",
                ["isFolder"] = b.IsFolder,
                ["folder"] = bookmarks.ParentOf(b)?.Id ?? 0,
                ["folders"] = folders.Select(f => new Dictionary<string, object> { ["id"] = f.folder.Id, ["label"] = bookmarks.TitleOf(f.folder), ["depth"] = f.depth }).ToList(),
            });
            if (answer == null || bookmarks.ParentOf(b) == null) return;   // closed, or the bookmark went away meanwhile
            switch (Json.Str(answer, "action"))
            {
                case "done":
                    var name = (Json.Str(answer, "name") ?? "").Trim();
                    var url = (Json.Str(answer, "url") ?? "").Trim();
                    b.Title = name.Length == 0 ? b.Url ?? L.T("Folder") : name;
                    if (!b.IsFolder && url.Length > 0) b.Url = url;
                    var target = folders.Select(f => f.folder).FirstOrDefault(f => f.Id == Int(answer, "folder"));
                    if (target != null && target != bookmarks.ParentOf(b)) bookmarks.Move(b, target);
                    bookmarks.NotifyChanged();
                    break;
                case "remove":
                    bookmarks.Remove(b);
                    bookmarks.NotifyChanged();
                    break;
            }
        }

        void ToggleBookmarksBar()
        {
            Settings.ShowBookmarksBar = !Settings.ShowBookmarksBar;
            app.SaveSettings();
        }

        async void OfferChromeImport()
        {
            if (Profile.Id != "Default" || !bookmarks.IsEmpty || ChromeImport.Profiles().Count == 0) return;
            await uiReady.Task;
            if (IsDisposed) return;
            if (await ConfirmAsync(L.T("Import your bookmarks from Google Chrome?"), L.T("You can do this later from Menu > Bookmarks."), L.T("Import"), L.T("Not now")))
                ImportFromChrome();
        }

        public async void ImportFromChrome()
        {
            var profiles = ChromeImport.Profiles();
            if (profiles.Count == 0)
            {
                ShowNotice(L.T("No Google Chrome bookmarks were found on this PC."));
                return;
            }
            var chosen = profiles[0];
            if (profiles.Count > 1)
            {
                var answer = await AskAsync(new Dictionary<string, object>
                {
                    ["kind"] = "choice", ["title"] = L.T("Import from Google Chrome"), ["message"] = L.T("Import bookmarks from this Chrome profile:"),
                    ["options"] = profiles.Select(p => p.Name).ToList(), ["ok"] = L.T("Import"),
                });
                if (answer == null || Json.Str(answer, "action") != "ok") return;
                int index = Int(answer, "index");
                if (index < 0 || index >= profiles.Count) return;
                chosen = profiles[index];
            }
            try
            {
                ChromeImport.Read(chosen.Dir, out var bar, out var other);
                int n = bookmarks.Import(bar, other, "Google Chrome");
                ShowNotice(n == 0 ? L.T("That Chrome profile has no bookmarks.") : L.T("Imported {0} bookmarks from Chrome ({1}).", n, chosen.Name));
            }
            catch (Exception ex)
            {
                ShowNotice(L.T("Couldn't import from Chrome: ") + ex.Message);
            }
        }

        public void ImportFromFile()
        {
            using (var dialog = new OpenFileDialog { Title = L.T("Import bookmarks"), Filter = L.T("Bookmarks file") + " (*.html;*.htm)|*.html;*.htm|" + L.T("All files") + "|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    BookmarkHtml.Parse(File.ReadAllText(dialog.FileName), out var bar, out var other);
                    int n = bookmarks.Import(bar, other, Path.GetFileNameWithoutExtension(dialog.FileName));
                    ShowNotice(n == 0 ? L.T("No bookmarks were found in that file.") : L.T("Imported {0} bookmarks.", n));
                }
                catch (Exception ex)
                {
                    ShowNotice(L.T("Couldn't import that file: ") + ex.Message);
                }
            }
        }

        public void ExportToFile()
        {
            using (var dialog = new SaveFileDialog
            {
                Title = L.T("Export bookmarks"),
                Filter = L.T("Bookmarks file") + " (*.html)|*.html",
                FileName = $"bookmarks_{DateTime.Now:yyyy_MM_dd}.html",
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, BookmarkHtml.Write(bookmarks.Bar, bookmarks.Other), new UTF8Encoding(false));
                    ShowNotice(L.T("Bookmarks exported."), L.T("Show in folder"), () => Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\""));
                }
                catch (Exception ex)
                {
                    ShowNotice(L.T("Couldn't export: ") + ex.Message);
                }
            }
        }

        // ----- tools -----

        void ReloadOrStop()
        {
            var core = current?.Core;
            if (core == null) return;
            if (current.IsLoading) core.Stop();
            else core.Reload();
        }

        void ToggleDownloads()
        {
            var core = current?.Core;
            if (core == null) return;
            if (core.IsDefaultDownloadDialogOpen) core.CloseDefaultDownloadDialog();
            else core.OpenDefaultDownloadDialog();
        }

        void OpenDevTools() => devtools.Toggle(current);   // docked in a drawer under the page

        async void TogglePictureInPicture()
        {
            var core = current?.Core;
            if (core == null) return;
            try
            {
                var result = await PageTools.TogglePictureInPictureAsync(core);
                if (result == "no-video") ShowNotice(L.T("There's no playable video on this page."));
                else if (result == "unsupported") ShowNotice(L.T("This page doesn't allow picture in picture."));
                else if (result.StartsWith("error")) ShowNotice(L.T("Picture in picture didn't start: ") + result.Substring(5).TrimStart(':', ' '));
            }
            catch (Exception ex)
            {
                ShowNotice(L.T("Picture in picture didn't start: ") + ex.Message);
            }
        }

        async void TakeScreenshot()
        {
            var tab = current;
            if (tab?.Core == null || tab.IsBlank) return;
            try
            {
                var file = await PageTools.CaptureFullPageAsync(tab.Core, Settings.EffectiveScreenshotFolder, tab.Title);
                ShowNotice(L.T("Full-page screenshot saved: ") + Path.GetFileName(file), L.T("Show in folder"),
                    () => Process.Start("explorer.exe", $"/select,\"{file}\""));
            }
            catch (Exception ex)
            {
                ShowNotice(L.T("Screenshot failed: ") + ex.Message);
            }
        }

        void SetAdBlock(bool on)
        {
            Settings.AdBlock = on;
            app.SaveSettings();
            current?.Core?.Reload();
        }

        async void UpdateAdList()
        {
            ShowNotice(L.T("Downloading the latest EasyList…"));
            try
            {
                var task = AdBlocker.DownloadLatestAsync();
                var blocker = await task;
                app.BlockerTask = task;
                ShowNotice(L.T("Ad block list updated: EasyList {0}, {1} ad domains.", blocker.Version, blocker.RuleCount.ToString("N0")));
            }
            catch (Exception ex)
            {
                ShowNotice(L.T("Couldn't update the list: ") + ex.Message);
            }
        }

        // What's using memory right now, for Settings > Memory saver.
        public Dictionary<string, object> MemoryState()
        {
            long engine = 0, own;
            int processes = 0;
            var environment = BrowserEngine.Current;
            if (environment != null)
            {
                foreach (var info in environment.GetProcessInfos())
                {
                    try
                    {
                        // Memory in use as Task Manager shows it (the private working set), not the whole working set,
                        // which counts what the engine's processes share with each other once for each of them.
                        long inUse = ProcessMemory.InUse(info.ProcessId);
                        if (inUse > 0)
                        {
                            engine += inUse;
                            processes++;
                        }
                    }
                    catch { }
                }
            }
            own = ProcessMemory.InUseByThisProcess();
            return new Dictionary<string, object>
            {
                ["total"] = (engine + own) / 1048576,
                ["engine"] = engine / 1048576,
                ["processes"] = processes,
                ["own"] = own / 1048576,
                ["tabs"] = app.Windows.SelectMany(w => w.tabs.Select(tab => new Dictionary<string, object>
                {
                    ["title"] = Ui.Trim(tab.Title, 90),
                    ["state"] = MemoryStateOf(w, tab),
                })).ToList(),
            };
        }

        // Names of states: the Settings page puts them into words. A tab in the background that is kept awake says why.
        static string MemoryStateOf(MainForm w, BrowserTab tab) =>
            tab == w.current ? "Active"
            : tab.State == TabState.Sleeping ? "Sleeping"
            : tab.State == TabState.Unloaded ? "Unloaded"
            : tab.InCall ? "Call"
            : tab.InPictureInPicture ? "Floating"
            : tab.IsPlayingAudio ? "Sound"
            : tab.OnAwakeList ? "Kept"
            : "Open";

        // Its name, and the color of its circle: the circle before the name shows the colors it can have.
        async void AddProfile()
        {
            var answer = await AskAsync(new Dictionary<string, object>
            {
                ["kind"] = "profile", ["title"] = L.T("Add profile"), ["message"] = L.T("Name for the new profile:"), ["value"] = "", ["ok"] = L.T("Add"),
                ["colors"] = ProfileStore.Colors, ["color"] = app.Profiles.NextColor(),
            });
            if (answer == null || Json.Str(answer, "action") != "ok") return;
            var name = (Json.Str(answer, "value") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            var profile = app.Profiles.Add(name.Length > 40 ? name.Substring(0, 40) : name, Json.Str(answer, "color"));
            app.SaveSettings();   // open windows now show their profile's name in the title
            app.OpenWindow(profile, false);
        }

        // Settings live in a tab (https://settings.igyo). One per window: asking again goes to it.
        public void OpenSettings(string section = null)
        {
            var url = SettingsPage.Url + (string.IsNullOrEmpty(section) ? "" : "#" + section);
            var existing = tabs.FirstOrDefault(t => SettingsPage.IsUrl(t.Url));
            if (existing == null)
            {
                NewTab(url, true, false);
                return;
            }
            Select(existing);
            if (!string.IsNullOrEmpty(section) && existing.Core != null)
                try { _ = existing.Core.ExecuteScriptAsync("location.hash = " + Json.Write(section)); } catch { }
        }

        // The pages visited. The engine keeps that record itself, for each profile, and has a page of its own that
        // shows it (with a search box, dates, and a way to delete entries): History opens that page in a tab, and
        // IGYO keeps no record of its own beside it. One per window: asking again goes to it.
        public const string HistoryUrl = "edge://history/all";

        public static bool IsHistoryUrl(string url) => (url ?? "").StartsWith("edge://history", StringComparison.OrdinalIgnoreCase);

        // The engine's settings page, by either of its names. It has none to show in an app like this one.
        public static bool IsEngineSettingsUrl(string url) =>
            (url ?? "").StartsWith("edge://settings", StringComparison.OrdinalIgnoreCase) || (url ?? "").StartsWith("chrome://settings", StringComparison.OrdinalIgnoreCase);

        public void OpenHistory()
        {
            if (IsPrivate)
            {
                // A private window leaves no record, and the engine shows it no history page (the tab would stay
                // empty): the profile's own window shows it, as in other browsers. That window is opened if there
                // is none, the way a start with an address does it (last time's tabs first, where they are kept).
                var own = app.Windows.FirstOrDefault(w => !w.IsPrivate && w.Profile.Id == Profile.Id);
                if (own == null) { app.OpenWindow(Profile, false, new[] { HistoryUrl }, true); return; }
                if (own.WindowState == FormWindowState.Minimized) own.WindowState = FormWindowState.Normal;
                own.Activate();
                own.OpenHistory();
                return;
            }
            var existing = tabs.FirstOrDefault(t => IsHistoryUrl(t.Url));
            if (existing != null) Select(existing);
            else NewTab(HistoryUrl, true, false);
        }

        // ----- notices -----

        readonly Dictionary<int, Action> noticeActions = new Dictionary<int, Action>();
        int noticeSeq;
        Dictionary<string, object> noticeWaiting;   // a notice the interface was not ready for yet

        public void ShowNotice(string text, string linkText = null, Action action = null, int ms = 0)
        {
            int id = ++noticeSeq;
            if (action != null)
            {
                noticeActions[id] = action;
                foreach (var old in noticeActions.Keys.Where(k => k < id - 8).ToList()) noticeActions.Remove(old);
            }
            var notice = new Dictionary<string, object> { ["id"] = id, ["text"] = text, ["action"] = action == null ? "" : linkText ?? "", ["ms"] = ms };
            // The interface takes nothing in before it is ready (right after a start, or while it loads again for
            // another language), and what a start has to say can be there a moment earlier: such a notice waits,
            // and is shown when the interface says it is ready (ShowWaitingNotice). The latest one wins, as on screen.
            if (ui == null || !ui.Ready) { noticeWaiting = notice; return; }
            noticeWaiting = null;
            Post("notice", notice);
        }

        void ShowWaitingNotice()
        {
            var notice = noticeWaiting;
            noticeWaiting = null;
            if (notice != null) Post("notice", notice);
        }

        // Something unexpected went wrong; it was written to errors.log and the browser kept going.
        public void ShowProblem() =>
            ShowNotice(L.T("Something went wrong, and IGYO kept going."), L.T("Details"),
                () => { try { Process.Start("explorer.exe", $"/select,\"{ErrorLog.FilePath}\""); } catch { } }, 10000);

        // ----- keyboard -----

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

        // Called from the window, from the interface and from the page (the engine reports shortcut keys pressed inside a view).
        public bool HandleShortcut(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.T: NewTab(HomeUrl, true, true); return true;
                case Keys.Control | Keys.N: app.OpenWindow(Profile, false); return true;
                case Keys.Control | Keys.Shift | Keys.N: app.OpenWindow(Profile, true); return true;
                case Keys.Control | Keys.W:
                case Keys.Control | Keys.F4: if (current != null) CloseTab(current); return true;
                case Keys.Control | Keys.Shift | Keys.T: ReopenClosedTab(); return true;
                case Keys.Control | Keys.L:
                case Keys.Alt | Keys.D:
                case Keys.F6: FocusAddress(); return true;
                case Keys.Control | Keys.Tab: SwitchTab(1); return true;
                case Keys.Control | Keys.Shift | Keys.Tab: SwitchTab(-1); return true;
                case Keys.Control | Keys.D: EditBookmarkForCurrentPage(); return true;
                case Keys.Control | Keys.Shift | Keys.B: ToggleBookmarksBar(); return true;
                case Keys.Control | Keys.Shift | Keys.O: OpenSettings("bookmarks"); return true;
                case Keys.Control | Keys.J: ToggleDownloads(); return true;
                case Keys.Control | Keys.H: OpenHistory(); return true;
                case Keys.Control | Keys.Shift | Keys.S: TakeScreenshot(); return true;
                case Keys.Control | Keys.Oemcomma: OpenSettings(); return true;
                case Keys.F5:
                case Keys.Control | Keys.R: current?.Core?.Reload(); return true;
                case Keys.Alt | Keys.Left: current?.Core?.GoBack(); return true;
                case Keys.Alt | Keys.Right: current?.Core?.GoForward(); return true;
                case Keys.F12:
                case Keys.Control | Keys.Shift | Keys.I: OpenDevTools(); return true;
                case Keys.Control | Keys.Shift | Keys.J:
                case Keys.Control | Keys.Shift | Keys.C: devtools.Expect(current); return false;   // the engine opens them; the drawer docks them
                case Keys.F11: ToggleWindowFullScreen(); return true;
                case Keys.Escape when windowFullScreen && !uiModal: ToggleWindowFullScreen(); return true;
                case Keys.Control | Keys.P: current?.Core?.ShowPrintUI(); return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Shift | Keys.Oemplus:
                case Keys.Control | Keys.Add: Zoom(1); return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract: Zoom(-1); return true;
                case Keys.Control | Keys.D0:
                case Keys.Control | Keys.NumPad0: Zoom(0); return true;
                case Keys.Alt | Keys.F:
                case Keys.Alt | Keys.E: ui.FocusView(); Post("openMenu", new Dictionary<string, object> { ["name"] = "main" }); return true;
            }
            var key = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.Control && key >= Keys.D1 && key <= Keys.D9 && tabs.Count > 0)
            {
                int n = key - Keys.D1;
                Select(n == 8 ? tabs[tabs.Count - 1] : tabs[Math.Min(n, tabs.Count - 1)]);
                return true;
            }
            return false;
        }
    }

    // Which window is on top at a point of the screen, of those that can be seen there (other programs' too).
    static class ScreenWindows
    {
        delegate bool EnumProc(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Box box);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Box value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [StructLayout(LayoutKind.Sequential)] struct Box { public int Left, Top, Right, Bottom; }

        // skip: a window not to count (the one being dragged there). Windows go from the top down; one that lets the
        // mouse through (WS_EX_TRANSPARENT), is hidden on another desktop of Windows' (cloaked) or minimized is not seen.
        public static IntPtr TopAt(Point screen, IntPtr skip)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, data) =>
            {
                if (hwnd == skip || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                int ex = GetWindowLong(hwnd, -20 /* GWL_EXSTYLE */), style = GetWindowLong(hwnd, -16 /* GWL_STYLE */);
                if ((ex & 0x20 /* WS_EX_TRANSPARENT */) != 0) return true;
                // Tooltips, menus and other small popups (a tooltip under the mouse must not hide the strip under it).
                if ((ex & 0x80 /* WS_EX_TOOLWINDOW */) != 0 && (style & unchecked((int)0x80000000) /* WS_POPUP */) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                // Its visible edges (the frame's invisible border, where the mouse resizes, is not counted).
                if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out Box box, Marshal.SizeOf(typeof(Box))) != 0
                    && !GetWindowRect(hwnd, out box)) return true;
                if (screen.X < box.Left || screen.X >= box.Right || screen.Y < box.Top || screen.Y >= box.Bottom) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

#if DEBUG
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        public static string Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "none";
            var name = new System.Text.StringBuilder(128);
            GetClassName(hwnd, name, 128);
            GetWindowThreadProcessId(hwnd, out int pid);
            GetWindowRect(hwnd, out Box box);
            DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4);
            return $"{hwnd} {name} pid {pid} ({box.Left},{box.Top})-({box.Right},{box.Bottom}) cloaked {cloaked}";
        }
#endif
    }
}
