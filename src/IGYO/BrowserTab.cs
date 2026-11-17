using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace IGYO
{
    // Live: running. Sleeping: paused by the engine, keeps its page. Unloaded: engine view released, reloads when opened.
    enum TabState { Unloaded, Live, Sleeping }

    sealed class BrowserTab
    {
        static int nextId;
        MainForm owner;   // the window it is in; a tab dragged out of its window's strip moves to a new one (MoveTo)
        public MainForm Owner => owner;
        Task initTask;
        string pendingNavigation;
        string topLevelRequest;
        string pageHost = "";
        bool adFilterOn;
        bool closed;

        public BrowserTab(MainForm owner, string url, string title = null)
        {
            this.owner = owner;
            if (!string.IsNullOrEmpty(url))
            {
                Url = url;
                pendingNavigation = url;
            }
            Title = !string.IsNullOrWhiteSpace(title) ? title : IsBlank ? L.T("New tab") : SettingsPage.IsUrl(url) ? L.T("Settings") : url;
        }

        public event Action<BrowserTab> Changed;
        public event Action<BrowserTab> BlockedChanged;
        public event Action<BrowserTab> ZoomChanged;   // the engine changed the page's zoom by itself (see MainForm.Zoom.cs)

        public int Id { get; } = ++nextId;   // how the interface names this tab
        public WebView View { get; private set; }
        public CoreWebView2 Core { get; private set; }
        public string Url { get; private set; } = "";
        public string Title { get; private set; }
        public byte[] FaviconPng { get; private set; }   // the site's icon as it sent it
        public int IconVersion { get; private set; }
        public bool IsLoading { get; private set; }
        public bool IsPlayingAudio { get; private set; }
        public int Blocked { get; private set; }
        public TabState State { get; private set; } = TabState.Unloaded;
        public DateTime HiddenSince { get; private set; } = DateTime.Now;
        public bool MemoryReleased { get; set; }   // its memory was handed back to Windows since it was last hidden (MemoryCare)
        public bool IsBlank => string.IsNullOrEmpty(Url) || Url == "about:blank" || IsNewTabPage;
        public bool IsNewTabPage => NewTabPage.IsUrl(Url);
        public string LastSleepResult { get; private set; } = "";
        public double Appear { get; set; } = 1;   // 0..1 while the tab grows in

        // The page's zoom as the engine has it (1 is 100%); a tab whose page is not loaded counts as 100%. (Microsoft's
        // documentation says the engine remembers a zoom for each site. A zoom set from IGYO's own code, the keys,
        // menus and the zoom box, was seen to stay with the tab when it went to another site, 15 Nov 2026.)
        public double ZoomFactor
        {
            get { try { return View?.ZoomFactor ?? 1; } catch { return 1; } }
        }

        // A new document began loading a moment ago. The engine gives a page its site's zoom right after that
        // (Microsoft's documentation of ZoomFactorChanged), so a change of zoom then is the site's, not the person's.
        DateTime newDocumentAt = DateTime.MinValue;
        public bool NewDocumentJustNow => (DateTime.UtcNow - newDocumentAt).TotalMilliseconds < 700;

        public Task EnsureLiveAsync() => initTask ?? (initTask = InitAsync());

        async Task InitAsync()
        {
            // The first tab after a start takes the view that was asked for before the window existed (EarlyViews).
            var early = EarlyViews.TakeTab(owner.Profile.Id, owner.IsPrivate);
            var view = early?.View ?? new WebView { Dock = DockStyle.Fill, Visible = false };
            view.KeyDown += (s, e) =>
            {
                if (owner.HandleShortcut(e.KeyData))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            view.GotFocus += (s, e) => owner.PageTookFocus();   // the page was clicked: an extension's popup over it goes
            // Ctrl and the mouse wheel over the page, or a page that loads and gets the zoom its site was last shown at.
            // (Zoom set from IGYO's own code raises nothing: MainForm.Zoom tells the interface itself.)
            view.ZoomFactorChanged += (s, e) => { if (View == view) ZoomChanged?.Invoke(this); };
            View = view;
            ApplyAppearance();
            owner.ContentHost.Controls.Add(view);
            _ = view.Handle;   // the engine attaches to the control's window
            StartupTrace.Mark(early != null ? "tab: took the early view" : "tab: view asked for");
            if (early != null) await early.Ready;
            else
            {
                var environment = await BrowserEngine.GetAsync();
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = owner.Profile.Id;
                options.IsInPrivateModeEnabled = owner.IsPrivate;
                await view.EnsureCoreWebView2Async(environment, options);
            }
            StartupTrace.Mark("tab: view ready");
            if (closed || View != view) return;

            var core = view.CoreWebView2;
            Configure(core);
            // Presenting as Edge: when what to present is already known, the two instructions are sent and the page
            // is sent on its way right behind them, without waiting for the engine to acknowledge each (it takes
            // them in the order they were sent). Only when it must first be found out (the first start on an
            // engine or Windows version) does the page wait.
            Task identity = Task.CompletedTask;
            if (owner.IdentifyAsEdge)
            {
                bool known = Identity.IsKnown(core);
                identity = Identity.ApplyAsync(core, owner);
                if (!known) await identity;
            }
            StartupTrace.Mark("tab: identity sent");
            // The ad block list is read on another thread from the start; a page only waits for it if it isn't done yet.
            await owner.BlockerReady;
            StartupTrace.Mark("tab: ad block list ready");
            if (closed || View != view) return;
            Core = core;
            State = TabState.Live;
            SetAdBlock(owner.AdBlockOn);
            var target = pendingNavigation;
            pendingNavigation = null;
            ApplyDarkMode(target ?? "");
            ApplyEnhance();
            if (Math.Abs(Volume - 1) > 0.0005) SetVolume(Volume);   // a tab loaded again keeps the volume it was given
            if (!string.IsNullOrEmpty(target)) core.Navigate(target);
            StartupTrace.Mark("tab: told to go to its address");
            owner.OnTabReady(this);
            await identity;
            Changed?.Invoke(this);
        }

        void Configure(CoreWebView2 core)
        {
            var s = core.Settings;
            s.AreDevToolsEnabled = true;                 // right-click > Inspect, F12
            s.AreDefaultContextMenusEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = true;
            s.AreDefaultScriptDialogsEnabled = true;
            s.IsStatusBarEnabled = true;
            s.IsZoomControlEnabled = true;
            s.IsReputationCheckingRequired = true;       // Microsoft SmartScreen
            s.IsGeneralAutofillEnabled = true;
            s.AreHostObjectsAllowed = false;             // pages get no bridge into this app
            s.IsWebMessageEnabled = false;
            core.DefaultDownloadDialogCornerAlignment = CoreWebView2DefaultDownloadDialogCornerAlignment.TopRight;

            core.FrameCreated += (_, e) => TrackFrame(e.Frame);
            core.NavigationStarting += (_, e) =>
            {
                topLevelRequest = e.Uri;
                pageHost = HostOf(e.Uri);
                ApplyDarkMode(e.Uri);   // IGYO's own pages are never darkened; websites are, when that is switched on
                if (!e.IsRedirected)
                {
                    Blocked = 0;
                    BlockedChanged?.Invoke(this);
                }
                IsLoading = true;
                Changed?.Invoke(this);
            };
            core.ContentLoading += (_, e) => newDocumentAt = DateTime.UtcNow;   // (see NewDocumentJustNow)
            core.NavigationCompleted += (_, e) => { StartupTrace.Done("tab: page loaded"); IsLoading = false; Changed?.Invoke(this); owner.OnTabLoaded(this); _ = PreviewSoonAsync(); };
            core.SourceChanged += (_, e) => { Url = core.Source; ApplyAppearance(); Changed?.Invoke(this); };
            core.HistoryChanged += (_, e) => Changed?.Invoke(this);
            core.DocumentTitleChanged += (_, e) =>
            {
                Title = IsBlank ? L.T("New tab") : string.IsNullOrWhiteSpace(core.DocumentTitle) ? Url : core.DocumentTitle;
                Changed?.Invoke(this);
            };
            core.FaviconChanged += async (_, e) => await LoadFaviconAsync(core);
            core.IsDocumentPlayingAudioChanged += (_, e) => { IsPlayingAudio = core.IsDocumentPlayingAudio; Changed?.Invoke(this); };
            core.NewWindowRequested += owner.OnNewWindowRequested;
            core.WindowCloseRequested += (_, e) => owner.BeginInvoke(new Action(() => owner.CloseTab(this)));
            core.ContainsFullScreenElementChanged += (_, e) => owner.OnFullScreenChanged(this, core.ContainsFullScreenElement);
            core.WebResourceRequested += OnWebResourceRequested;
            foreach (var filter in InternalPages.Filters)
                core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            if (!owner.IsPrivate)
            {
                // A web store's own "Add" and "Remove" buttons are answered by IGYO (see ExtensionStore.PageScript).
                // The script does nothing on any other page, and the requests it makes are ones the tab answers itself.
                foreach (var store in ExtensionStore.Hosts)
                    foreach (var path in new[] { ExtensionStore.AddPath, ExtensionStore.RemovePath })
                        core.AddWebResourceRequestedFilter("https://" + store + path + "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                _ = core.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionStore.PageScript);
            }
            core.ContextMenuRequested += (_, e) =>
            {
                owner.ExpectDevTools(this);   // "Inspect" opens DevTools: dock them
                owner.OnPageContextMenu(this, core, e);   // "Translate selection", when something is selected
            };
            core.ProcessFailed += (_, e) => owner.OnProcessFailed(this, e);
        }

        async Task LoadFaviconAsync(CoreWebView2 core)
        {
            try
            {
                if (string.IsNullOrEmpty(core.FaviconUri)) { SetFavicon(null); return; }
                using (var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                {
                    if (stream == null) { SetFavicon(null); return; }
                    // The engine's stream can't report its length, so copy it first.
                    var bytes = new System.IO.MemoryStream();
                    await stream.CopyToAsync(bytes);
                    if (bytes.Length == 0) { SetFavicon(null); return; }
                    bytes.Position = 0;
                    using (var image = Image.FromStream(bytes)) { }   // only a real picture is kept
                    SetFavicon(bytes.ToArray());
                    if (!owner.IsPrivate && !InternalPages.IsInternal(core.Source)) Favicons.Save(core.Source, bytes.ToArray());
                }
            }
            catch { }
        }

        void SetFavicon(byte[] png)
        {
            FaviconPng = png;
            IconVersion++;
            Changed?.Invoke(this);
        }

        void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var core = Core ?? sender as CoreWebView2;
            if (InternalPages.TryServe(owner, core, e, topLevelRequest)) return;
            // A web store's own "Add" or "Remove" button, asking IGYO (see ExtensionStore.PageScript): answered
            // here, when the person has said yes or no, and never sent anywhere.
            if (ExtensionStore.IsButtonRequest(e.Request.Uri, out string storeHost, out string storeButton, out string storeId, out string storeName))
            {
                AnswerStoreButton(core, e, storeHost, storeButton, storeId, storeName);
                return;
            }
            var blocker = owner.Blocker;
            if (blocker == null || core == null) return;
            var uri = e.Request.Uri;
            if (uri == topLevelRequest) return;   // never block the page you asked for
            var host = HostOf(uri);
            if (host.Length == 0 || !blocker.ShouldBlock(host, pageHost)) return;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            LogBlocked(uri + "  (page: " + pageHost + ")");
            Blocked++;
            BlockedChanged?.Invoke(this);
        }

        async void AnswerStoreButton(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string host, string button, string id, string name)
        {
            var deferral = e.GetDeferral();
            string said = "failed";
            try
            {
                // (Only the page itself may ask: a frame inside it, or a page somewhere else, is refused.)
                bool fromThePage = e.Request.Method == "POST" && Uri.TryCreate(Url ?? "", UriKind.Absolute, out var page) && string.Equals(page.Host, host, StringComparison.OrdinalIgnoreCase);
                if (fromThePage) said = button == "remove" ? await owner.RemoveFromStoreButtonAsync(this, host, id) : await owner.AddFromStoreButtonAsync(this, host, id, name);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
            try
            {
                if (core != null)
                    e.Response = core.Environment.CreateWebResourceResponse(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(said)), 200, "OK", "Content-Type: text/plain; charset=utf-8\r\nCache-Control: no-store");
            }
            catch { }
            finally { deferral.Complete(); }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        static void LogBlocked(string text)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-blocked.txt"), text + "\n"); } catch { }
        }

        static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme.StartsWith("http") || u.Scheme.StartsWith("ws")) ? u.Host : "";

        public void SetAdBlock(bool on)
        {
            if (Core == null || on == adFilterOn) return;
            if (on) Core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            else Core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            adFilterOn = on;
        }

        public void ApplyAppearance()
        {
            // Pages expect a white canvas unless they choose a dark one themselves (as in Chrome).
            // IGYO's own pages get the theme's color, so opening a tab never flashes white in a dark theme.
            if (View == null) return;
            bool own = InternalPages.IsInternal(string.IsNullOrEmpty(Url) ? pendingNavigation : Url);
            // (With websites shown dark, the canvas a page starts from is dark too, so loading never flashes white.)
            var color = own ? owner.CurrentTheme.Page : owner.ForceDark ? Color.FromArgb(18, 18, 18) : Color.White;
            if (View.DefaultBackgroundColor != color) View.DefaultBackgroundColor = color;
        }

        public void NavigateTo(string url)
        {
            Url = url;
            if (Core != null) Core.Navigate(url);
            else
            {
                pendingNavigation = url;
                _ = EnsureLiveAsync();
            }
            Changed?.Invoke(this);
        }

        public void Show()
        {
            MemoryReleased = false;
            if (View == null) return;
            View.Visible = true;
            View.BringToFront();
            if (Core != null)
            {
                try
                {
                    if (Core.IsSuspended) Core.Resume();
                    Core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                }
                catch { }
            }
            if (State == TabState.Sleeping) State = TabState.Live;
            ApplyDarkMode();   // a tab that slept through the switching of dark mode for websites takes it now
            ApplyEnhance();    // and the same for video enhancement
            Changed?.Invoke(this);
        }

        // The interface changed language: a tab that shows one of IGYO's own pages gets its name in the new one.
        public void Rename()
        {
            if (IsBlank) Title = L.T("New tab");
            else if (SettingsPage.IsUrl(Url)) Title = L.T("Settings");
            else return;
            Changed?.Invoke(this);
        }

        // Wakes a sleeping tab where it is, without showing it (the Claude bridge reads it in the background).
        // It goes back to sleep by the usual rules, counted from now.
        public void Wake()
        {
            if (Core == null) return;
            try { if (Core.IsSuspended) Core.Resume(); } catch { }
            HiddenSince = DateTime.Now;
            ApplyDarkMode();
            ApplyEnhance();
            if (State != TabState.Sleeping) return;
            State = TabState.Live;
            Changed?.Invoke(this);
        }

        public bool InPictureInPicture { get; private set; }

        // A video floating in picture in picture needs its page to keep running, even when its tab isn't shown.
        public async Task<bool> CheckPictureInPictureAsync()
        {
            var core = Core;
            if (core == null || core.IsSuspended) return InPictureInPicture = false;
            try
            {
                var result = await core.ExecuteScriptAsync(
                    "(() => { const d = [document]; for (const f of document.querySelectorAll('iframe')) { try { if (f.contentDocument) d.push(f.contentDocument); } catch (e) {} } return d.some(x => !!x.pictureInPictureElement); })()");
                InPictureInPicture = result == "true";
            }
            catch { InPictureInPicture = false; }
            return InPictureInPicture;
        }

        public async void Hide()
        {
            HiddenSince = DateTime.Now;
            MemoryReleased = false;
            if (View == null) return;
            if (await CheckPictureInPictureAsync())
            {
                View?.SendToBack();   // stays "visible" behind the shown tab, so the floating video keeps its controls
                return;
            }
            if (View == null || owner.IsShowing(this)) return;
            View.Visible = false;
            if (Core != null && !IsPlayingAudio)
                try { Core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
        }

        // Pauses the page (timers, scripts, rendering) and lets the engine shrink its memory.
        public async Task SleepAsync()
        {
            var core = Core;
            if (core == null || State != TabState.Live || IsPlayingAudio || View == null || View.Visible) return;
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                bool suspended = await core.TrySuspendAsync();
                LastSleepResult = suspended ? "suspended" : "engine declined";
                if (suspended && Core == core && View != null && !View.Visible)
                {
                    State = TabState.Sleeping;
                    Changed?.Invoke(this);
                }
            }
            catch (Exception ex)
            {
                LastSleepResult = "error: " + ex.Message;
            }
        }

        // Releases the page completely; it reloads from its address when the tab is opened again.
        public void Unload()
        {
            if (View == null || IsPlayingAudio || InPictureInPicture) return;
            if (Core != null && !string.IsNullOrEmpty(Core.Source)) Url = Core.Source;
            pendingNavigation = string.IsNullOrEmpty(Url) || Url == "about:blank" ? null : Url;   // the New Tab page comes back too
            DisposeView();
            State = TabState.Unloaded;
            IsLoading = false;
            Changed?.Invoke(this);
        }

        public void Close()
        {
            closed = true;
            DisposeView();
            FaviconPng = null;
        }

        // ----- a picture of the page, for the card over its tab (MainForm.SendTabCard) -----
        // Pages in the background are not drawn, so the picture is taken while the tab is on show: a moment after a
        // page has loaded, when the mouse comes to the tab strip, and when the window is left. The top of the page,
        // 16:9, at most 480 pixels wide (about 20 KB), kept in memory only; at most one every 2 seconds.
        public byte[] Preview { get; private set; }
        DateTime previewAt = DateTime.MinValue;
        bool previewBusy;

        async Task PreviewSoonAsync()
        {
            await Task.Delay(1200);
            if (owner.IsShowing(this)) TakePreview();
        }

        public async void TakePreview()
        {
            var core = Core;
            if (core == null || closed || previewBusy || View == null || !View.Visible || State != TabState.Live) return;
            if ((DateTime.UtcNow - previewAt).TotalSeconds < 2) return;
            try { if (core.IsSuspended) return; } catch { return; }
            previewAt = DateTime.UtcNow;
            previewBusy = true;
            var stream = new System.IO.MemoryStream();
            try
            {
                var capture = core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
                if (await Task.WhenAny(capture, Task.Delay(1500)) != capture)
                {
                    _ = capture.ContinueWith(_ => stream.Dispose());   // not drawn in time: given up (the stream goes once the engine is done with it)
                    return;
                }
                await capture;
                var bytes = stream.ToArray();
                stream.Dispose();
                if (bytes.Length == 0) return;
                var small = await Task.Run(() => Pictures.Thumbnail(bytes, 480));
                if (small != null && !closed) Preview = small;
            }
            catch { }
            finally { previewBusy = false; }
        }

        // ----- moving to another window (a tab dragged out of the strip, see MainForm.Tear) -----
        // The page's own window is not made anew, only given another parent, so the page goes on exactly as it was:
        // nothing is loaded again, a video keeps playing, a form keeps what was typed into it.

        // Taken out of its window's content area (kept alive, out of sight) until another window takes it in.
        public void Detach()
        {
            if (View == null) return;
            View.Visible = false;
            owner.ContentHost.Controls.Remove(View);
        }

        // Taken in by another window of the same profile; that window shows it (Select).
        public void MoveTo(MainForm to)
        {
            owner = to;
            if (View == null || View.Parent == to.ContentHost) return;
            View.Visible = false;
            to.ContentHost.Controls.Add(View);
        }

        // ----- tools: websites shown dark, and this tab's volume -----

        bool darkOn;
        string volumeScript;                 // the engine's name for the volume script given to every new document
        double volumeApplied = 1;
        bool volumeBusy;
        readonly System.Collections.Generic.List<CoreWebView2Frame> frames = new System.Collections.Generic.List<CoreWebView2Frame>();

        public const double MaxVolume = 6;

        // 1 is the page's sound as the page plays it; 0 is silence; up to 6 (600%) makes it louder than the page can.
        public double Volume { get; private set; } = 1;

        // Websites that have no dark look of their own are shown dark by the engine's automatic dark mode, switched
        // on for this tab. IGYO's own pages follow the theme instead and are left alone. (The address is given when
        // the tab is only on its way there.)
        public void ApplyDarkMode(string address = null)
        {
            var core = Core;
            if (core == null) return;
            try { if (core.IsSuspended) return; } catch { return; }   // a sleeping tab takes it when it wakes (Show, Wake)
            bool want = owner.ForceDark && !InternalPages.IsInternal(address ?? Url);
            if (want == darkOn) return;
            darkOn = want;
            try { _ = core.CallDevToolsProtocolMethodAsync("Emulation.setAutoDarkModeOverride", want ? "{\"enabled\":true}" : "{}"); } catch { }
        }

        // Videos are sharpened by the engine as it draws them, when that is switched on (Tools > Enhance videos). It
        // is asked for by a script in the page (see PageTools.EnhanceScript), so switching it reaches the videos
        // that are playing right now. A sleeping tab takes the change when it wakes (Show, Wake).
        string enhanceScript;                // the engine's name for the script given to every new document
        string enhanceApplied = "";          // the kind the open documents were last given ("" for none)
        bool enhanceBusy;

        public async void ApplyEnhance()
        {
            if (enhanceBusy) return;   // the one already at work goes on to the newest wish
            enhanceBusy = true;
            try
            {
                while (Core != null)
                {
                    var core = Core;
                    if (core.IsSuspended) break;
                    string want = owner.EnhanceKind;
                    if (want == enhanceApplied) break;
                    var script = PageTools.EnhanceScript(want);
                    // Documents opened from now on get it as they are created; with enhancement off, nothing at all.
                    if (enhanceScript != null) { core.RemoveScriptToExecuteOnDocumentCreated(enhanceScript); enhanceScript = null; }
                    if (want.Length > 0) enhanceScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                    // What is open right now: the page, and every frame in it.
                    await core.ExecuteScriptAsync(script);
                    foreach (var frame in frames.ToArray())
                    {
                        try { if (frame.IsDestroyed() == 0) await frame.ExecuteScriptAsync(script); } catch { }
                    }
                    enhanceApplied = want;
                }
            }
            catch { }
            finally { enhanceBusy = false; }
        }

        // Frames inside the page (and frames inside those) are remembered, so that a change of volume or of video
        // enhancement reaches the players embedded in a page that is already open.
        void TrackFrame(CoreWebView2Frame frame)
        {
            if (frame == null) return;
            frames.Add(frame);
            try
            {
                frame.Destroyed += (_, e) => frames.Remove(frame);
                frame.FrameCreated += (_, e) => TrackFrame(e.Frame);
            }
            catch { }
        }

        public async void SetVolume(double value)
        {
            Volume = Math.Max(0, Math.Min(MaxVolume, value));
            Changed?.Invoke(this);
            if (volumeBusy) return;   // the one already at work goes on to the newest value
            volumeBusy = true;
            try
            {
                while (Core != null && Math.Abs(volumeApplied - Volume) > 0.0005)
                {
                    double now = Volume;
                    var core = Core;
                    var script = PageTools.VolumeScript(now);
                    // Documents opened from now on get it as they are created; at 100% nothing is given to them at all.
                    if (volumeScript != null) { core.RemoveScriptToExecuteOnDocumentCreated(volumeScript); volumeScript = null; }
                    if (Math.Abs(now - 1) > 0.0005) volumeScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                    // What is open right now: the page, and every frame in it.
                    await core.ExecuteScriptAsync(script);
                    foreach (var frame in frames.ToArray())
                    {
                        try { if (frame.IsDestroyed() == 0) await frame.ExecuteScriptAsync(script); } catch { }
                    }
                    volumeApplied = now;
                }
            }
            catch { }
            finally { volumeBusy = false; }
        }

        void DisposeView()
        {
            var view = View;
            View = null;
            Core = null;
            initTask = null;
            adFilterOn = false;
            darkOn = false;
            volumeScript = null;
            volumeApplied = 1;   // a tab that is loaded again is given its volume again (InitAsync)
            enhanceScript = null;
            enhanceApplied = "";
            frames.Clear();
            if (view == null) return;
            owner.ContentHost.Controls.Remove(view);
            view.Dispose();
        }
    }
}
