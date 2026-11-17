using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace IGYO
{
    // The browser's own interface (tabs, toolbar, bookmarks bar, menus, dialogs, notices) is a page drawn by the
    // engine, in a see-through view that lies over the whole window. Its window region is only the shapes the
    // interface occupies right now (the frame around the page, an open menu, a notice), so everywhere else the
    // mouse reaches the web page underneath. The page talks to the app through messages, never the other way round
    // for anything but this one page: it is loaded from the app's folder and can't reach the network.
    sealed class UiHost : Panel
    {
        public const string Origin = "https://ui.igyo";
        public const string Url = Origin + "/chrome.html";

        const string PageHeaders = InternalAssets.Common +
            "\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self' data:; " +
            "connect-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'" +
            "\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer";

        readonly MainForm owner;
        WebView view;

        public UiHost(MainForm owner)
        {
            this.owner = owner;
            AccessibleName = L.T("Browser interface");
        }

        public CoreWebView2 Core { get; private set; }
        public bool Ready { get; private set; }   // the page is loaded and listening
        public event Action<Dictionary<string, object>> Message;
        public event Action<string> Failed;

        // Windows composes this panel over the page below it instead of cutting the page out behind it.
        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x20;   // WS_EX_TRANSPARENT
                return p;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public async Task InitAsync()
        {
            try
            {
                // The first window after a start takes the view that was asked for before it existed (EarlyViews).
                var early = EarlyViews.TakeInterface();
                view = early?.View ?? new WebView { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
                view.KeyDown += (s, e) =>
                {
                    if (owner.HandleShortcut(e.KeyData))
                    {
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                };
                Controls.Add(view);
                _ = view.Handle;
                if (early != null) await early.Ready;
                else
                {
                    var environment = await BrowserEngine.GetAsync();
                    var options = environment.CreateCoreWebView2ControllerOptions();
                    options.ProfileName = EarlyViews.InterfaceProfile;   // its own storage, apart from every browsing profile
                    await view.EnsureCoreWebView2Async(environment, options);
                }
                StartupTrace.Mark("interface: view ready");
                if (IsDisposed) return;
                var core = view.CoreWebView2;
                var s = core.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;   // F5, Ctrl+P and the like never act on the interface itself
                s.AreDefaultScriptDialogsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.IsSwipeNavigationEnabled = false;
                s.IsGeneralAutofillEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                s.IsReputationCheckingRequired = false;       // a local page: nothing to look up
                s.AreHostObjectsAllowed = false;
                s.IsWebMessageEnabled = true;                 // how the interface and the app talk
                s.IsNonClientRegionSupportEnabled = true;     // the page marks where the window can be dragged
#if DEBUG
                s.AreDevToolsEnabled = true;
#else
                s.AreDevToolsEnabled = false;
#endif
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += Serve;
                core.NavigationStarting += (_, e) => { if (!IsOwn(e.Uri)) e.Cancel = true; };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += OnMessage;
                core.ProcessFailed += (_, e) =>
                {
                    // If the interface's own process dies, load it again.
                    if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited && e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) return;
                    Ready = false;
                    try { BeginInvoke(new Action(() => { try { core.Navigate(Url); } catch { } })); } catch { }
                };
#if DEBUG
                // Test builds only: NSB_TEST_LATE_INTERFACE (milliseconds) loads the interface that much later, to check
                // what becomes of the things the window has to tell it before it is ready.
                if (int.TryParse(Environment.GetEnvironmentVariable("NSB_TEST_LATE_INTERFACE"), out int late) && late > 0)
                {
                    await System.Threading.Tasks.Task.Delay(late);
                    if (IsDisposed) return;
                }
#endif
                Core = core;
                core.Navigate(Url);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                Failed?.Invoke(ex.Message);
            }
        }

        static bool IsOwn(string uri) => !string.IsNullOrEmpty(uri) && uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase);

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!IsOwn(e.Source)) return;
            Dictionary<string, object> message;
            try { message = Json.Obj(Json.Parse(e.WebMessageAsJson)); } catch { return; }
            if (message == null) return;
            if (Json.Str(message, "t") == "ready") { Ready = true; StartupTrace.Mark("interface: page ready"); }
            try { Message?.Invoke(message); }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        public void Post(Dictionary<string, object> message)
        {
            if (!Ready || Core == null) return;
            try { Core.PostWebMessageAsJson(Json.Write(message)); } catch { }
        }

        // Loads the interface again (after the language changed). It says "ready" once more and is sent everything anew.
        public void Reload()
        {
            if (Core == null) return;
            Ready = false;
            try { Core.Reload(); } catch { }
        }

        public void FocusView()
        {
            if (!owner.MayTakeKeyboard) return;   // a window opened for a quiet check leaves the keyboard where it is
            try { view?.Focus(); } catch { }
        }

        public bool HasFocus => view != null && view.ContainsFocus;

        // The parts of the window the interface occupies, in this panel's pixels. Everything else belongs to the page.
        // The same shape again is not given again: each new shape has Windows redraw the interface, and a resize asks
        // for the shape twice a step (once for the new size, once when the interface reports its new layout).
        Rectangle[] shape;
        public void SetShape(IEnumerable<Rectangle> rects)
        {
            var list = rects.Where(r => r.Width > 0 && r.Height > 0).ToArray();
            if (shape != null && list.SequenceEqual(shape)) return;
            shape = list;
            var region = new Region(Rectangle.Empty);
            foreach (var r in list) region.Union(r);
            var old = Region;
            Region = region;
            old?.Dispose();
        }

        // Every request the interface makes ends here; nothing goes out.
        void Serve(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var core = Core ?? sender as CoreWebView2;
            if (core == null) return;
            try
            {
                var uri = e.Request.Uri;
                if (!IsOwn(uri) || e.Request.Method != "GET") { InternalAssets.Refuse(core, e, 403); return; }
                var path = new Uri(uri).AbsolutePath;
                if (InternalAssets.TryServeCommon(owner, core, e, path)) return;
                if (path.StartsWith("/icon/tab/"))
                {
                    var png = owner.TabIcon(path.Substring("/icon/tab/".Length));
                    if (png != null) InternalAssets.Respond(core, e, png, "image/png");
                    else InternalAssets.Refuse(core, e);
                    return;
                }
                if (path.StartsWith("/avatar/"))
                {
                    var png = owner.AvatarPng(Uri.UnescapeDataString(path.Substring("/avatar/".Length)));
                    if (png != null) InternalAssets.Respond(core, e, png, "image/png");
                    else InternalAssets.Refuse(core, e);
                    return;
                }
                var name = path.TrimStart('/');
                if (name.Length == 0) name = "chrome.html";
                if (!InternalAssets.SafeName(name)) { InternalAssets.Refuse(core, e); return; }
                var file = Path.Combine(InternalAssets.AssetsDir, "Ui", name);
                if (!File.Exists(file)) { InternalAssets.Refuse(core, e); return; }
                InternalAssets.Respond(core, e, File.ReadAllBytes(file), InternalAssets.TypeOf(file),
                    name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? PageHeaders : InternalAssets.Common);
            }
            catch
            {
                try { InternalAssets.Refuse(core, e, 500); } catch { }
            }
        }
    }
}
