using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace IGYO
{
    // An extension's popup: the page of its own that a click on its toolbar button shows. The engine has no such
    // button and no such popup in an app like this one, so IGYO shows the page itself, in a small view of its
    // own under the button, sized to what the page holds (as browsers do, between 25 by 25 and 800 by 600), and
    // closes it when something else is clicked.
    //
    // To the engine that view is one more window with one more tab, so an extension that asks for "the tab on
    // show in this window" would be handed its own popup. The page is therefore given a few lines of script
    // before its own run (Shim): what it asks about the current window is answered for the window of the tab the
    // popup was opened over, and a recording it asks for is one of that tab.
    sealed class ExtensionPopup
    {
        const int MinSide = 25, MaxWidth = 800, MaxHeight = 600;

        readonly MainForm owner;
        readonly Panel frame = new Panel { Padding = new Padding(1), BackColor = Color.FromArgb(96, 96, 104) };
        readonly WebView view = new WebView { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
        readonly Timer watch = new Timer { Interval = 100 };
        Rectangle anchor;      // the button it hangs under, in the window's pixels
        double scale = 1;      // the window's pixels for one of the page's
        Size content;          // what the page was last measured at, in its own pixels
        bool closed, measuring, toldRefused, placed, focused;
        int ticks;

        public ExtensionInfo Extension { get; }
        public BrowserTab Tab { get; }      // the tab it was opened over
        public bool IsOpen => !closed;
        public bool IsShown => !closed && placed;
        public DateTime ShownAt { get; private set; } = DateTime.MaxValue;
        public Rectangle Bounds => frame.Bounds;
        public CoreWebView2 Core => closed ? null : view.CoreWebView2;
        public event Action Closed;

        ExtensionPopup(MainForm owner, ExtensionInfo extension, BrowserTab tab, Rectangle anchor, double scale)
        {
            this.owner = owner;
            Extension = extension;
            Tab = tab;
            this.anchor = anchor;
            this.scale = scale > 0.2 && scale < 8 ? scale : 1;
            frame.Controls.Add(view);
        }

        public static ExtensionPopup Open(MainForm owner, ExtensionInfo extension, BrowserTab tab, Rectangle anchor, double scale)
        {
            var popup = new ExtensionPopup(owner, extension, tab, anchor, scale);
            popup.Start();
            return popup;
        }

        async void Start()
        {
            try
            {
                // Laid out at the largest size a popup may have, beyond the window's edge where it is not seen, until
                // the page says how large it is.
                frame.Bounds = new Rectangle(-8000, -8000, (int)(MaxWidth * scale) + 2, (int)(MaxHeight * scale) + 2);
                owner.Controls.Add(frame);
                frame.BringToFront();
                _ = view.Handle;
                var environment = await BrowserEngine.GetAsync();
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = owner.Profile.Id;
                await view.EnsureCoreWebView2Async(environment, options);
                if (closed) return;
                var core = view.CoreWebView2;
                var s = core.Settings;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.IsSwipeNavigationEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                s.IsWebMessageEnabled = false;
#if DEBUG
                s.AreDevToolsEnabled = true;
#else
                s.AreDevToolsEnabled = false;
                s.AreDefaultContextMenusEnabled = false;
#endif
                string home = "chrome-extension://" + Extension.Id + "/";
                // A link in the popup opens as a tab; the popup itself only ever shows the extension's own pages.
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    string uri = e.Uri;
                    owner.BeginInvoke(new Action(() => { owner.OpenFromExtension(uri); Close(); }));
                };
                core.NavigationStarting += (_, e) =>
                {
                    if ((e.Uri ?? "").StartsWith(home, StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank") return;
                    e.Cancel = true;
                    string uri = e.Uri;
                    owner.BeginInvoke(new Action(() => { owner.OpenFromExtension(uri); Close(); }));
                };
                core.WindowCloseRequested += (_, e) => owner.BeginInvoke(new Action(Close));
                core.ProcessFailed += (_, e) => { try { owner.BeginInvoke(new Action(Close)); } catch { } };
                core.NavigationCompleted += (_, e) => { ticks = 0; watch.Start(); };
                view.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; e.SuppressKeyPress = true; Close(); } };
                view.GotFocus += (_, e) => focused = true;
                view.LostFocus += (_, e) => { focused = false; LeftAlone(); };

                int window = Tab?.Core == null ? 0 : await WindowOf(Tab.Core);
                if (closed) return;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim(window));
                if (closed) return;
                watch.Tick += (_, e) => Measure();
                core.Navigate(home + Extension.Popup);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                Close();
            }
        }

        // The number the engine knows a view's window by: the same one it gives extensions for that window.
        public static async Task<int> WindowOf(CoreWebView2 core)
        {
            try
            {
                var answer = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Browser.getWindowForTarget", "{}")));
                return Convert.ToInt32(Json.Get(answer, "windowId") ?? 0);
            }
            catch { return 0; }
        }

        // The size the page has by itself, and what it left for the app to act on.
        const string MeasureScript = @"(() => {
  const d = document.documentElement, b = document.body;
  if (!b) return null;
  const state = window.__igyoPopup || {};
  const cs = getComputedStyle(b), num = (v) => parseFloat(v) || 0;
  const mx = num(cs.marginLeft) + num(cs.marginRight), my = num(cs.marginTop) + num(cs.marginBottom);
  // (Whether the page gave itself a size: asked of the style as it was written, where no size reads 'auto'.)
  const given = (node, side) => { try { const v = node.computedStyleMap().get(side); return !!v && v.unit === 'px'; } catch (e) { return false; } };
  let w, h;
  if (given(d, 'width')) w = d.getBoundingClientRect().width;
  else if (given(b, 'width')) w = b.getBoundingClientRect().width + mx;
  else {
    // as wide as the view lets it be: what it would take by itself
    const was = b.style.width;
    b.style.width = 'max-content';
    w = b.getBoundingClientRect().width + mx;
    b.style.width = was;
    if (b.getAttribute('style') === '') b.removeAttribute('style');
  }
  if (given(d, 'height')) h = d.getBoundingClientRect().height;
  else h = b.getBoundingClientRect().height + my;
  // Something inside that is larger still shows as the page being scrollable. It is remembered for as long as the
  // page's own size stays what it is (once the view is that large, nothing scrolls any more to tell it by).
  const over = state.over || (state.over = { w: 0, h: 0, ow: 0, oh: 0 });
  if (over.w !== w) { over.w = w; over.ow = 0; }
  if (over.h !== h) { over.h = h; over.oh = 0; }
  if (d.scrollWidth > window.innerWidth) over.ow = d.scrollWidth;
  if (d.scrollHeight > window.innerHeight) over.oh = d.scrollHeight;
  w = Math.max(w, over.ow);
  h = Math.max(h, over.oh);
  const out = [Math.ceil(w), Math.ceil(h), state.close ? 1 : 0, state.refused ? 1 : 0, document.readyState === 'complete' ? 1 : 0];
  state.refused = false;
  return out;
})()";

        async void Measure()
        {
            if (closed || measuring) return;
            measuring = true;
            try
            {
                ticks++;
                if (ticks == 40) watch.Interval = 350;   // settled by now: looked at less often
                var core = view.CoreWebView2;
                var got = Json.Arr(Json.Parse(await core.ExecuteScriptAsync(MeasureScript)));
                if (closed || got.Length < 5) return;
                int[] n = got.Select(v => { try { return (int)Math.Round(Convert.ToDouble(v)); } catch { return 0; } }).ToArray();
                if (n[2] == 1) { Close(); return; }
                if (n[3] == 1 && !toldRefused) { toldRefused = true; owner.OnRecordingRefused(Extension); }
                var size = new Size(Math.Max(MinSide, Math.Min(MaxWidth, n[0])), Math.Max(MinSide, Math.Min(MaxHeight, n[1])));
                bool first = !placed;
                if (first && n[4] == 0 && ticks < 30) return;   // still loading: shown when it is done (or after three seconds)
                if (size != content || first)
                {
                    content = size;
                    placed = true;
                    Place();
                }
                if (first)
                {
                    ShownAt = DateTime.UtcNow;
                    frame.BringToFront();
                    if (owner.MayTakeKeyboard) view.Focus();
                    owner.OnExtensionPopupShown(this);
                }
            }
            catch { }
            finally { measuring = false; }
        }

        // Under its button, its right edge on the button's right edge, and inside the window.
        void Place()
        {
            var client = owner.ClientSize;
            int width = (int)Math.Round(content.Width * scale) + 2, height = (int)Math.Round(content.Height * scale) + 2;
            int gap = (int)Math.Round(4 * scale);
            int top = Math.Min(anchor.Bottom + gap, Math.Max(0, client.Height - 2 * MinSide));
            height = Math.Max(MinSide, Math.Min(height, client.Height - top - gap));
            width = Math.Max(MinSide, Math.Min(width, client.Width - 2 * gap));
            int left = Math.Max(gap, Math.Min(anchor.Right - width, client.Width - width - gap));
            frame.Bounds = new Rectangle(left, top, width, height);
        }

        // Focus went somewhere else: the popup goes, as it does in a browser. (Looked at a moment later: something
        // the page itself opens can take the focus and give it straight back.)
        async void LeftAlone()
        {
            if (closed || !placed) return;
            await Task.Delay(180);
            if (closed || focused || owner.IsDisposed) return;
            if ((DateTime.UtcNow - ShownAt).TotalMilliseconds < 400) return;   // it has only just come up
            Close();
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            watch.Stop();
            watch.Dispose();
            try { frame.Visible = false; } catch { }
            try { owner.Controls.Remove(frame); } catch { }
            // (Let go a moment later: it may be closing from inside one of its own events.)
            try { owner.BeginInvoke(new Action(() => { try { view.Dispose(); frame.Dispose(); } catch { } })); }
            catch { try { view.Dispose(); frame.Dispose(); } catch { } }
            try { Closed?.Invoke(); } catch (Exception ex) { ErrorLog.Write(ex); }
        }

        // What the page is given before its own scripts run. WINDOW is the window of the tab under the popup.
        public static string Shim(int window) => @"(() => {
  'use strict';
  const WINDOW = " + window + @";
  const c = window.chrome;
  if (!c || !c.runtime || !c.runtime.id || window.top !== window) return;
  const state = { close: false, refused: false };
  try { Object.defineProperty(window, '__igyoPopup', { value: state, enumerable: false }); } catch (e) { return; }
  // (An extension written the older way hands over a function to be called; a newer one waits for a promise.)
  const answer = (promise, cb, empty) => { if (typeof cb === 'function') { promise.then((v) => cb(v), () => cb(empty)); return undefined; } return promise; };
  try { window.close = () => { state.close = true; }; } catch (e) {}
  // The extension's own way to its options page (a 'Settings' button in its popup). Asked of the engine, the page
  // comes up in a browser window of the engine's own, apart from IGYO; opened from here it becomes a tab.
  try {
    const m = c.runtime.getManifest ? c.runtime.getManifest() : null;
    const options = m && ((m.options_ui && m.options_ui.page) || m.options_page);
    if (options && c.runtime.openOptionsPage) c.runtime.openOptionsPage = (cb) => { window.open(c.runtime.getURL(options)); return answer(Promise.resolve(), cb, undefined); };
  } catch (e) {}
  if (!WINDOW) return;
  try {
    if (c.tabs && c.tabs.query) {
      const query = c.tabs.query.bind(c.tabs);
      const here = (q) => {
        const o = Object.assign({}, q || {});
        const unnamed = o.windowId == null && o.currentWindow == null && o.lastFocusedWindow == null;
        if (o.currentWindow === true || o.lastFocusedWindow === true || o.windowId === -2 || (unnamed && o.active === true)) { delete o.currentWindow; delete o.lastFocusedWindow; o.windowId = WINDOW; }
        return o;
      };
      const target = () => query({ windowId: WINDOW }).then((list) => list[0]);
      c.tabs.query = (q, cb) => answer(query(here(q)), cb, []);
      c.tabs.getCurrent = (cb) => answer(Promise.resolve(undefined), cb, undefined);
      if (c.tabs.captureVisibleTab) {
        const shot = c.tabs.captureVisibleTab.bind(c.tabs);
        c.tabs.captureVisibleTab = (...a) => {
          const cb = typeof a[a.length - 1] === 'function' ? a.pop() : null;
          const w = typeof a[0] === 'number' ? a.shift() : (a.length > 1 || a[0] == null ? a.shift() : null);
          return answer(shot(w == null || w === -2 ? WINDOW : w, a[0] || {}), cb, undefined);
        };
      }
      if (c.tabCapture && c.tabCapture.getMediaStreamId) {
        const streamId = c.tabCapture.getMediaStreamId.bind(c.tabCapture);
        const idFor = (options) => (options && options.targetTabId != null ? Promise.resolve(options) : target().then((t) => Object.assign({}, options || {}, { targetTabId: t.id })))
          .then((o) => streamId(o)).catch((e) => { state.refused = true; throw e; });
        c.tabCapture.getMediaStreamId = (options, cb) => { if (typeof options === 'function') { cb = options; options = undefined; } return answer(idFor(options), cb, undefined); };
        c.tabCapture.capture = (options, cb) => {
          const from = (id, more) => ({ mandatory: Object.assign({ chromeMediaSource: 'tab', chromeMediaSourceId: id }, (more && more.mandatory) || {}) });
          const o = options || {};
          return answer(idFor(undefined).then((id) => navigator.mediaDevices.getUserMedia({ audio: o.audio ? from(id, o.audioConstraints) : false, video: o.video ? from(id, o.videoConstraints) : false })), cb, null);
        };
      }
    }
    if (c.windows && c.windows.get) {
      const get = c.windows.get.bind(c.windows);
      const mine = (a) => { const cb = typeof a[a.length - 1] === 'function' ? a.pop() : null; return answer(get(WINDOW, a[0] || {}), cb, undefined); };
      c.windows.getCurrent = (...a) => mine(a);
      c.windows.getLastFocused = (...a) => mine(a);
    }
  } catch (e) {}
})();";
    }
}
