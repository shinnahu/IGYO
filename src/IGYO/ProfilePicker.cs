using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace IGYO
{
    // At start, when there is more than one profile: a small window with every profile, and the one chosen opens.
    // It is skipped when "Ask which profile to open at start" is off AND a default profile is chosen (Settings >
    // Profiles, a profile's ⋯ menu): that one then opens straight away. A link opened from another program, or a
    // profile named on the command line, never waits for it (see BrowserApp.Start and TakeOver).
    //
    // Its page (Assets\Ui\picker.html) is drawn by the engine with the interface's own storage, like the interface,
    // and reaches nothing outside the app: every request it makes is answered here.
    sealed class ProfilePicker : Form
    {
        public const string Url = UiHost.Origin + "/picker.html";

        const string PageHeaders = InternalAssets.Common +
            "\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self' data:; " +
            "connect-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'" +
            "\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer";

        readonly BrowserApp app;
        readonly WebView view;
        CoreWebView2 core;
        bool ready;

        // A profile was chosen; the window closes right after. (Closed without a choice: Picked is not raised.)
        public event Action<Profile> Picked;

        public ProfilePicker(BrowserApp app)
        {
            this.app = app;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Text = L.T("{0}: choose a profile", InstallInfo.AppName);
            try { Icon = app.WindowIcon ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 500);
            MinimumSize = new Size(460, 360);
            BackColor = app.Theme.Frame;
            KeyPreview = true;
            view = new WebView { Dock = DockStyle.Fill, DefaultBackgroundColor = app.Theme.Frame };
            Controls.Add(view);
        }

        static readonly bool QuietStart = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_NO_ACTIVATE"));
        protected override bool ShowWithoutActivation => QuietStart;   // a window started for checks never takes the front

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindow(this, app.Theme);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var environment = await BrowserEngine.GetAsync();
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = EarlyViews.InterfaceProfile;   // the interface's own storage, apart from every browsing profile
                await view.EnsureCoreWebView2Async(environment, options);
                if (IsDisposed) return;
                core = view.CoreWebView2;
                var s = core.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.AreDefaultScriptDialogsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.IsSwipeNavigationEnabled = false;
                s.IsGeneralAutofillEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                s.AreHostObjectsAllowed = false;
                s.IsWebMessageEnabled = true;
#if DEBUG
                s.AreDevToolsEnabled = true;
#else
                s.AreDevToolsEnabled = false;
#endif
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += Serve;
                core.NavigationStarting += (_, a) => { if (!(a.Uri ?? "").StartsWith(UiHost.Origin + "/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true; };
                core.NewWindowRequested += (_, a) => a.Handled = true;
                core.PermissionRequested += (_, a) => a.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += OnMessage;
                core.Navigate(Url);
                if (!QuietStart) view.Focus();
            }
            catch (Exception ex)
            {
                // Without its page, the profile used last opens as before.
                ErrorLog.Write(ex);
                Choose(app.Profiles.Find(app.Profiles.LastUsed) ?? app.Profiles.Default);
            }
        }

        // What the page is told: every profile (picture, name, whether it is the default), and the switch.
        Dictionary<string, object> State() => new Dictionary<string, object>
        {
            ["t"] = "profiles",
            ["list"] = app.Profiles.Profiles.Select(p => new Dictionary<string, object>
            {
                ["id"] = p.Id,
                ["name"] = p.Name ?? "",
                ["avatar"] = "/avatar/" + Uri.EscapeDataString(p.Id) + "?v=" + (p.Color ?? "") + Stamp(p),
                ["isDefault"] = string.Equals(app.Profiles.StartWith, p.Id, StringComparison.OrdinalIgnoreCase),
                ["lastUsed"] = string.Equals(app.Profiles.LastUsed, p.Id, StringComparison.OrdinalIgnoreCase),
            }).ToList(),
            ["ask"] = app.Profiles.AskAtStart,
            ["hasDefault"] = app.Profiles.Find(app.Profiles.StartWith) != null,
        };

        static string Stamp(Profile p)
        {
            try { var f = ProfileStore.PicturePath(p.Id); return File.Exists(f) ? "-" + File.GetLastWriteTimeUtc(f).Ticks : ""; } catch { return ""; }
        }

        void Post(Dictionary<string, object> message)
        {
            if (!ready || core == null) return;
            try { core.PostWebMessageAsJson(Json.Write(message)); } catch { }
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!(e.Source ?? "").StartsWith(UiHost.Origin + "/", StringComparison.OrdinalIgnoreCase)) return;
            Dictionary<string, object> m;
            try { m = Json.Obj(Json.Parse(e.WebMessageAsJson)); } catch { return; }
            switch (Json.Str(m, "t"))
            {
                case "ready":
                    ready = true;
                    Post(State());
                    break;
                case "open":
                    var p = app.Profiles.Find(Json.Str(m, "id"));
                    if (p != null) Choose(p);
                    break;
                case "ask":
                    // The same switch as in Settings > Profiles.
                    app.Profiles.AskAtStart = Json.Get(m, "on") is bool on && on;
                    app.Profiles.Save();
                    Post(State());
                    break;
                case "close":
                    Close();
                    break;
            }
        }

        bool done;

        void Choose(Profile p)
        {
            if (done || p == null) return;
            done = true;
            Picked?.Invoke(p);
            Close();
        }

        // Another start of IGYO while it is up: it comes to the front (a click on the shortcut, for instance).
        public void ComeForward()
        {
            if (QuietStart || IsDisposed || !IsHandleCreated) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // Closed because something else settled which profile opens (a link handed over meanwhile).
        public void Dismiss()
        {
            done = true;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { view.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        // Every request the page makes ends here; nothing goes out.
        void Serve(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var c = core ?? sender as CoreWebView2;
            if (c == null) return;
            try
            {
                var uri = e.Request.Uri;
                if (!uri.StartsWith(UiHost.Origin + "/", StringComparison.OrdinalIgnoreCase) || e.Request.Method != "GET") { InternalAssets.Refuse(c, e, 403); return; }
                var path = new Uri(uri).AbsolutePath;
                switch (path)
                {
                    case "/theme.css": InternalAssets.RespondText(c, e, ThemeStore.BuildCss(app.ThemeDef, app.AccentHex), "text/css; charset=utf-8"); return;
                    case "/theme.js": InternalAssets.RespondText(c, e, ThemeStore.BuildJs(app.ThemeDef), "text/javascript; charset=utf-8"); return;
                    case "/lang.js": InternalAssets.RespondText(c, e, L.Script(), "text/javascript; charset=utf-8"); return;
                    case "/logo.png":
                        var logo = Path.Combine(InternalAssets.AssetsDir, "logo.png");
                        if (File.Exists(logo)) InternalAssets.Respond(c, e, File.ReadAllBytes(logo), "image/png"); else InternalAssets.Refuse(c, e);
                        return;
                }
                if (path.StartsWith("/shared/"))
                {
                    if (!InternalAssets.TryServeFile(c, e, "Shared", path.Substring("/shared/".Length))) InternalAssets.Refuse(c, e);
                    return;
                }
                if (path.StartsWith("/avatar/"))
                {
                    var p = app.Profiles.Find(Uri.UnescapeDataString(path.Substring("/avatar/".Length)));
                    if (p != null) InternalAssets.Respond(c, e, InternalAssets.AvatarPng(p, false), "image/png"); else InternalAssets.Refuse(c, e);
                    return;
                }
                var name = path.TrimStart('/');
                if (name != "picker.html" && name != "picker.css" && name != "picker.js") { InternalAssets.Refuse(c, e); return; }
                var file = Path.Combine(InternalAssets.AssetsDir, "Ui", name);
                if (!File.Exists(file)) { InternalAssets.Refuse(c, e); return; }
                InternalAssets.Respond(c, e, File.ReadAllBytes(file), InternalAssets.TypeOf(file), name.EndsWith(".html") ? PageHeaders : InternalAssets.Common);
            }
            catch
            {
                try { InternalAssets.Refuse(c, e, 500); } catch { }
            }
        }
    }
}
