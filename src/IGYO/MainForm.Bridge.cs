using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IGYO
{
    // The window's part in the Claude bridge: the questions it puts to the person, the sign in the page's corner
    // (ClaudeSign), the mark on the toolbar and that mark's menu. What may be read is decided in Bridge; this only
    // shows and asks.
    sealed partial class MainForm
    {
        sealed class AskBar
        {
            public int Id;
            public string Text, Tip;
            public (string id, string label)[] Buttons;
            public HashSet<string> Choices;
            public object Tag;
            public Timer Life;
            public readonly TaskCompletionSource<string> Answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        readonly List<AskBar> askBars = new List<AskBar>();   // the first one is on screen, the others wait their turn
        int askSeq;

        static int windowSeq;
        public int Number { get; } = ++windowSeq;   // how Claude names this window ("<process>:w<number>")

        // When this window was last the one in front: "the tab in front" means the front tab of the latest one.
        public DateTime LastActive { get; private set; } = DateTime.UtcNow;

        // Puts a question in the page's corner and waits for the answer: the id of the button pressed, or null when it
        // was closed, taken back, or left unanswered for too long. It is drawn in the sign (ClaudeSign), a window of
        // IGYO's own that no web page can reach, so a page can neither fake the question nor press its buttons. The
        // page stays usable.
        public Task<string> AskAsync(string text, string tip, (string id, string label)[] buttons, object tag, int seconds)
        {
            var bar = new AskBar { Id = ++askSeq, Text = text ?? "", Tip = tip ?? "", Buttons = buttons, Tag = tag, Choices = new HashSet<string>(buttons.Select(b => b.id)) };
            bar.Life = new Timer { Interval = Math.Max(5, seconds) * 1000 };
            bar.Life.Tick += (s, e) => FinishAsk(bar, null);
            askBars.Add(bar);
            if (askBars[0] == bar) PostAsk();
            return bar.Answer.Task;
        }

        // Shows the question whose turn it is.
        void PostAsk()
        {
            if (askBars.Count == 0 || IsDisposed) return;
            var bar = askBars[0];
            if (!bar.Life.Enabled) bar.Life.Start();   // its time runs from when it is first shown
            UpdateSign();
        }

        void FinishAsk(AskBar bar, string choice)
        {
            int index = askBars.IndexOf(bar);
            if (index < 0) return;
            askBars.RemoveAt(index);
            bar.Life.Stop();
            bar.Life.Dispose();
            if (index == 0 && !IsDisposed)
            {
                if (askBars.Count > 0) PostAsk();
                else UpdateSign();
            }
            bar.Answer.TrySetResult(choice);
        }

        void OnAskAnswer(int id, string choice)
        {
            var bar = askBars.FirstOrDefault(b => b.Id == id);
            if (bar != null) FinishAsk(bar, choice != null && bar.Choices.Contains(choice) ? choice : null);
        }

        // Takes back the questions that were asked for the given reason.
        public void WithdrawAsks(Func<object, bool> which)
        {
            foreach (var bar in askBars.Where(b => which(b.Tag)).ToList()) FinishAsk(bar, null);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags, uCount, dwTimeout;
        }

        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);

        // A question is waiting in this window while another program is in front (as a rule the Claude app that
        // caused it): its taskbar button flashes. The window is not brought forward and the keyboard stays put.
        public void FlashForAttention()
        {
            if (QuietStart || IsDisposed || !IsHandleCreated || GetForegroundWindow() == Handle) return;
            var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = Handle, dwFlags = 2 | 12, uCount = uint.MaxValue };   // the taskbar button, until the window is in front
            try { FlashWindowEx(ref info); } catch { }
        }

        // What the interface shows of the bridge: the toolbar mark (only for a profile with the switch on, never in
        // a private window), whether a Claude app is connected, and which tabs are being read right now.
        Dictionary<string, object> BridgeState()
        {
            var bridge = app.Bridge;
            bool on = !IsPrivate && Profile.ClaudeBridge;
            return new Dictionary<string, object>
            {
                ["on"] = on,
                ["paused"] = bridge.Paused,
                ["connected"] = on ? bridge.ConnectedNames() : new List<string>(),
                ["reading"] = on ? tabs.Where(t => bridge.IsReading(t.Id)).Select(t => t.Id).ToList() : new List<int>(),
                ["own"] = on ? tabs.Where(t => bridge.IsClaudesTab(t)).Select(t => t.Id).ToList() : new List<int>(),   // the tabs Claude opened
            };
        }

        void OnBridgeChanged(bool lists)
        {
            if (IsDisposed) return;
            if (ui.Ready) Post("bridge", BridgeState());
            UpdateSign();   // Claude started or stopped working in this window
            if (lists) RefreshSettingsPages();
        }

        void CloseBridgeParts()
        {
            foreach (var bar in askBars.ToList()) FinishAsk(bar, null);
            foreach (var tab in tabs) app.Bridge.TabClosed(tab);
            CloseClaudeNotice();
            sign?.Dispose();
            sign = null;
        }

        // ----- the sign in the page's bottom left corner (ClaudeSign) -----
        // From the top: a notice about Claude, the question waiting for an answer, and, in the corner itself, that
        // Claude is working in this window. Screen recordings and screenshots leave it out (see ClaudeSign).

        ClaudeSign sign;

        sealed class SignNotice
        {
            public int Id;
            public string Text, Action;
            public Action Run;
            public Timer Life;
        }
        SignNotice claudeNotice;
        int claudeNoticeSeq;

        // A notice about Claude (a recording it saved, a change to its switches that needs a restart): in the corner
        // with Claude's other signs, for a while (longer while the pointer is on it).
        public void ShowClaudeNotice(string text, string linkText = null, Action action = null, int ms = 0)
        {
            if (IsDisposed) return;
            CloseClaudeNotice(false);
            var n = new SignNotice { Id = ++claudeNoticeSeq, Text = text ?? "", Action = action == null ? null : linkText, Run = action };
            n.Life = new Timer { Interval = ms > 0 ? ms : action != null ? 7000 : 4500 };
            n.Life.Tick += (s, e) =>
            {
                if (sign != null && sign.IsHovered("notice:" + n.Id)) { n.Life.Interval = 2500; return; }
                if (claudeNotice == n) CloseClaudeNotice();
            };
            claudeNotice = n;
            n.Life.Start();
            UpdateSign();
        }

        void CloseClaudeNotice(bool update = true)
        {
            var n = claudeNotice;
            if (n == null) return;
            claudeNotice = null;
            n.Life.Stop();
            n.Life.Dispose();
            if (update) UpdateSign();
        }

        // What the sign shows now; nothing while the window is minimized or hidden (it comes back with the window).
        void UpdateSign()
        {
            if (IsDisposed || !IsHandleCreated) return;
            var cards = new List<ClaudeSign.Card>();
            if (claudeNotice != null)
                cards.Add(new ClaudeSign.Card
                {
                    Key = "notice:" + claudeNotice.Id,
                    Text = claudeNotice.Text,
                    Closable = true,
                    Buttons = claudeNotice.Action == null ? new (string, string)[0] : new[] { ("go", claudeNotice.Action) },
                });
            if (askBars.Count > 0)
            {
                var bar = askBars[0];
                cards.Add(new ClaudeSign.Card { Key = "ask:" + bar.Id, Text = bar.Text, Detail = bar.Tip, Strong = true, Closable = true, Armed = true, Buttons = bar.Buttons });
            }
            bool on = !IsPrivate && Profile.ClaudeBridge;
            if (on && app.Bridge.ActingIn(this) != null)
                cards.Add(new ClaudeSign.Card { Key = "acting", Text = L.T("Claude is working in this window."), Busy = true, Buttons = new[] { ("stop", L.T("Stop Claude")) } });
            if (cards.Count == 0 || !Visible || WindowState == FormWindowState.Minimized)
            {
                sign?.Hide();
                return;
            }
            if (sign == null)
            {
                sign = new ClaudeSign(this);
                sign.Pressed += OnSignPressed;
            }
            sign.Show(cards, PageOnScreen(), CurrentTheme, DeviceDpi);
        }

        // The page's area on the screen: the sign stands in its bottom left corner.
        Rectangle PageOnScreen()
        {
            try
            {
                var r = ContentHost.IsHandleCreated ? ContentHost.RectangleToScreen(ContentHost.ClientRectangle) : Rectangle.Empty;
                return r.Width > 0 && r.Height > 0 ? r : RectangleToScreen(ClientRectangle);
            }
            catch { return Bounds; }
        }

        // The window moved, changed size or laid itself out anew: the sign goes with the page.
        void PlaceSign()
        {
            if (sign != null && sign.IsShown && !IsDisposed) sign.Place(PageOnScreen(), DeviceDpi);
        }

        void OnSignPressed(string card, string button)
        {
            if (card == "acting")
            {
                if (button == "stop") app.Bridge.StopActing();
                return;
            }
            if (card.StartsWith("ask:", StringComparison.Ordinal))
            {
                if (int.TryParse(card.Substring(4), out int id)) OnAskAnswer(id, button);   // "" (the ✕): not now
                return;
            }
            if (card.StartsWith("notice:", StringComparison.Ordinal) && claudeNotice != null && card == "notice:" + claudeNotice.Id)
            {
                var run = button == "go" ? claudeNotice.Run : null;
                CloseClaudeNotice();
                run?.Invoke();
            }
        }

        // ----- what the bridge does to the window when Claude acts (Bridge.Act.cs) -----

        // A tab of Claude's own, at the end, put on show in this window. The window is not brought forward and the
        // keyboard stays where it is: the person may be working in another window.
        public BrowserTab OpenTabForClaude(string url)
        {
            var tab = CreateTab(url);
            tabs.Add(tab);
            Select(tab, true);
            MarkDirty(tabs: true);
            return tab;
        }

        // The tab Claude works in is the one on show in its window (the engine only draws that one), quietly.
        public void ShowForClaude(BrowserTab tab)
        {
            if (tab != null && tabs.Contains(tab) && current != tab) Select(tab, true);
        }

        // Claude's tab goes; the window never closes because of it (an empty tab takes its place when it was the last).
        public void CloseTabForClaude(BrowserTab tab)
        {
            if (!tabs.Contains(tab)) return;
            if (tabs.Count == 1)
            {
                var other = CreateTab(HomeUrl);
                tabs.Add(other);
                Select(other, true);
            }
            CloseTab(tab, true);
        }

        const uint SWP_NOMOVE_ = 0x0002, SWP_NOZORDER_ = 0x0004, SWP_NOACTIVATE_ = 0x0010;

        // Claude's resize_window: a window that is maximized is first put back to its normal size, without being
        // activated; the size is kept inside the screen. Returns the size, or empty when nothing was done.
        public Size ResizeForClaude(int width, int height)
        {
            if (WindowState == FormWindowState.Minimized || fullScreen) return Size.Empty;
            if (WindowState == FormWindowState.Maximized) ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
            var area = Screen.FromHandle(Handle).WorkingArea;
            int w = Math.Max(MinimumSize.Width, Math.Min(area.Width, width));
            int h = Math.Max(MinimumSize.Height, Math.Min(area.Height, height));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE_ | SWP_NOZORDER_ | SWP_NOACTIVATE_);
            return new Size(w, h);
        }

        // A recording Claude saved: said in the window's corner, with a way to see it.
        public void ShowSavedRecording(string file)
        {
            if (IsDisposed) return;
            ShowClaudeNotice(L.T("Claude saved a recording: {0}", System.IO.Path.GetFileName(file)), L.T("Show in folder"), () => ShowInExplorer(file), 12000);
        }

        IEnumerable<MenuEntry> ClaudeItems()
        {
            var bridge = app.Bridge;
            var names = bridge.ConnectedNames();
            yield return MenuEntry.Head(L.T("Claude"));
            yield return MenuEntry.Note(bridge.Paused ? L.T("Paused: nothing can connect") : names.Count > 0 ? L.T("Connected: {0}", string.Join(", ", names)) : L.T("Nothing is connected"));

            // The site in this tab: what Claude may do with it, to see and to change.
            var tab = current;
            string origin = tab == null || tab.IsBlank ? null : BridgeSites.OriginOf(tab.Url);
            if (origin != null)
            {
                string mode = bridge.ModeFor(Profile, origin);
                bool once = bridge.AllowedThisTime(tab, origin);
                yield return MenuEntry.Line();
                yield return MenuEntry.Head(Ui.Trim(BridgeSites.Name(origin), 44));
                yield return Item(L.T("Ask before reading"), null, () =>
                {
                    bridge.StopThisTime(tab);
                    if (mode != BridgeSites.Ask) bridge.SetSite(Profile, origin, BridgeSites.Ask);
                }, true, mode == BridgeSites.Ask && !once);
                if (once) yield return Item(L.T("Allowed in this tab for now"), null, () => bridge.StopThisTime(tab), true, true);
                yield return Item(L.T("Always allow reading"), null, () => bridge.SetSite(Profile, origin, BridgeSites.Read), true, mode == BridgeSites.Read);
                if (Profile.ClaudeAct)
                {
                    if (bridge.ActAllowedThisTime(tab, origin)) yield return Item(L.T("Acting allowed in this tab for now"), null, () => bridge.StopThisTime(tab), true, true);
                    yield return Item(L.T("Always allow reading and acting"), null, () => bridge.SetSite(Profile, origin, BridgeSites.Act), true, mode == BridgeSites.Act);
                }
                yield return Item(L.T("Never allow"), null, () => bridge.SetSite(Profile, origin, BridgeSites.Never), true, mode == BridgeSites.Never);
            }
            yield return MenuEntry.Line();
            yield return Item(bridge.Paused ? L.T("Paused (click to resume)") : L.T("Pause: disconnect now"), null, () => bridge.SetPaused(!bridge.Paused), true, bridge.Paused);
            yield return Item(L.T("Sites, programs and activity…"), null, () => OpenSettings("privacy/claude"), icon: "settings");
        }
    }
}
