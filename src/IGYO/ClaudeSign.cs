using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IGYO
{
    // What the Claude bridge has to tell the person in a window stands in the bottom left corner of the page: that
    // Claude is working in the window (with a button that stops it), a question waiting for an answer, a notice about
    // Claude (a recording it saved). The cards stack upwards from the corner.
    //
    // It is a small window of its own over the browser window, not part of the interface page, for one reason:
    // Windows can leave a window out of every picture taken of the screen (screen recorders such as OBS, screenshots,
    // screen sharing) while it stays on the screen itself, and it does so for this one. So a recording made while
    // Claude works in IGYO never shows these signs, and nobody watching it sees them; the person at the PC does.
    // A web page can neither draw over it nor press its buttons. It never takes the keyboard and never comes forward
    // by itself: it is owned by its browser window, goes with it when it is minimized, and lies under any window
    // that covers it.
    sealed class ClaudeSign : NativeWindow, IDisposable
    {
        public sealed class Card
        {
            public string Key;            // "acting", "ask:<id>", "notice:<id>": what a press is reported with
            public string Text = "";
            public string Detail;         // a question's explanation: shown under its text while the pointer rests on it
            public bool Busy;             // the beating dot: Claude is working
            public bool Strong;           // the text in semibold (a question)
            public bool Closable;         // ends with an ✕ (reported as the button "")
            public bool Armed;            // a press counts only a moment after the card first appears (see ArmMs)
            public (string Id, string Label)[] Buttons = new (string, string)[0];
        }

        // A press on a card's button: the card's key and the button's id ("" for the ✕).
        public event Action<string, string> Pressed;

        // A click that was on its way to the page when a question appeared doesn't answer it.
        const int ArmMs = 600;

        readonly Control owner;
        readonly Timer beat = new Timer { Interval = 80 };       // the dot's beat (only while Claude works and the sign is shown)
        byte[] still;                                             // the sign as last drawn, without the dot (each beat only draws the dot)
        readonly List<(Point At, Color Ground)> dots = new List<(Point, Color)>();
        readonly Timer detailWait = new Timer { Interval = 350 };
        readonly Dictionary<string, DateTime> firstShown = new Dictionary<string, DateTime>();
        List<Card> cards = new List<Card>();
        Theme theme;
        Rectangle area;        // the page's area on the screen: the cards stand in its bottom left corner
        float scale = 1;
        bool shown, disposed;
        Point origin;          // where the window's top left goes on the screen
        Size size;
        readonly List<Hit> hits = new List<Hit>();
        Hit hover, pressed;
        string detailFor;      // the card whose explanation is shown
        string pendingDetail;  // the card the pointer rests on, before its explanation comes

        sealed class Hit
        {
            public string Card, Button;   // Button: null for the card itself, "" for its ✕
            public Rectangle Box;
            public bool IsButton => Button != null;
        }

        public ClaudeSign(Control owner)
        {
            this.owner = owner;
            beat.Tick += (s, e) => { if (shown && still != null) Upload(Beat(still)); };
            detailWait.Tick += (s, e) =>
            {
                detailWait.Stop();
                if (pendingDetail != null && pendingDetail != detailFor && hover != null && hover.Card == pendingDetail) { detailFor = pendingDetail; Render(); }
            };
        }

        public bool IsShown => shown;

        // Whether Windows leaves the sign out of pictures of the screen (false on a Windows that can't: before
        // Windows 10 2004). Set when the window is made.
        public bool HiddenFromCapture { get; private set; }

        public bool IsHovered(string key) => hover != null && hover.Card == key;

        // Shows these cards (the first on top, the last in the corner) in the corner of the given area of the screen.
        public void Show(IEnumerable<Card> list, Rectangle pageArea, Theme look, int dpi)
        {
            if (disposed) return;
            cards = list.Where(c => c != null).ToList();
            if (cards.Count == 0) { Hide(); return; }
            foreach (var c in cards) if (!firstShown.ContainsKey(c.Key)) firstShown[c.Key] = DateTime.UtcNow;
            foreach (var gone in firstShown.Keys.Where(k => cards.All(c => c.Key != k)).ToList()) firstShown.Remove(gone);
            if (detailFor != null && cards.All(c => c.Key != detailFor)) detailFor = null;
            theme = look;
            area = pageArea;
            scale = Math.Max(1, dpi) / 96f;
            if (Handle == IntPtr.Zero) Make();
            Render();
            if (!shown)
            {
                shown = true;
                // Right above its browser window, not above everything: the browser window may lie behind the program
                // the person is using while Claude works in it, and the sign must not come out over that program.
                var above = GetWindow(owner.Handle, GW_HWNDPREV);
                if (above == Handle) above = IntPtr.Zero;
                SetWindowPos(Handle, above == IntPtr.Zero ? IntPtr.Zero /* HWND_TOP */ : above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            beat.Enabled = cards.Any(c => c.Busy);
        }

        public void Hide()
        {
            beat.Stop();
            detailWait.Stop();
            hover = pressed = null;
            detailFor = pendingDetail = null;
            if (!shown || Handle == IntPtr.Zero) { shown = false; return; }
            shown = false;
            ShowWindow(Handle, SW_HIDE);
#if DEBUG
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-sign.json"), "{\"shown\":false}"); } catch { }
#endif
        }

        // The browser window moved or changed size: the sign goes with it.
        public void Place(Rectangle pageArea, int dpi)
        {
            if (!shown || disposed) return;
            float now = Math.Max(1, dpi) / 96f;
            bool rescale = Math.Abs(now - scale) > 0.001f || pageArea.Width != area.Width;
            area = pageArea;
            scale = now;
            if (rescale) { Render(); return; }
            var at = Origin();
            if (at == origin) return;
            origin = at;
            SetWindowPos(Handle, IntPtr.Zero, at.X, at.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            beat.Dispose();
            detailWait.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        // ----- the window -----

        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int SW_HIDE = 0;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        const uint GW_HWNDPREV = 3;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSELEAVE = 0x02A3;
        const int WM_MOUSEACTIVATE = 0x0021, WM_SETCURSOR = 0x0020, MA_NOACTIVATE = 3;
        const int WM_NCDESTROY = 0x0082;

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

        void Make()
        {
            var p = new CreateParams
            {
                Caption = "IGYO Claude",
                Style = WS_POPUP,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                Parent = owner.Handle,   // for a popup: its owner (it rides on the browser window)
            };
            CreateHandle(p);
            try
            {
                HiddenFromCapture = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE) &&
                    GetWindowDisplayAffinity(Handle, out uint now) && now == WDA_EXCLUDEFROMCAPTURE;
            }
            catch { HiddenFromCapture = false; }
        }

        bool tracking;

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)MA_NOACTIVATE;   // a click never makes it (or the browser window) the active one
                    return;
                case WM_SETCURSOR:
                    SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)(hover != null && hover.IsButton ? 32649 /* IDC_HAND */ : 32512 /* IDC_ARROW */)));
                    m.Result = (IntPtr)1;
                    return;
                case WM_MOUSEMOVE:
                {
                    if (!tracking)
                    {
                        var t = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = 2 /* TME_LEAVE */, hwndTrack = Handle };
                        tracking = TrackMouseEvent(ref t);
                    }
                    PointerAt(At(m.LParam));
                    break;
                }
                case WM_MOUSELEAVE:
                    tracking = false;
                    PointerAt(null);
                    break;
                case WM_LBUTTONDOWN:
                    pressed = HitAt(At(m.LParam));
                    if (pressed != null && pressed.IsButton) SetCapture(Handle);
                    Render();
                    return;
                case WM_LBUTTONUP:
                {
                    var down = pressed;
                    pressed = null;
                    ReleaseCapture();
                    var up = HitAt(At(m.LParam));
                    Render();
                    if (down != null && down.IsButton && up != null && up.Card == down.Card && up.Button == down.Button) Press(down.Card, down.Button);
                    return;
                }
                case WM_NCDESTROY:
                    shown = false;
                    break;
            }
            base.WndProc(ref m);
        }

        static Point At(IntPtr lParam)
        {
            long l = lParam.ToInt64();
            return new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
        }

        Hit HitAt(Point p) =>
            hits.FirstOrDefault(h => h.IsButton && h.Box.Contains(p)) ?? hits.FirstOrDefault(h => !h.IsButton && h.Box.Contains(p));

        void PointerAt(Point? p)
        {
            var now = p.HasValue ? HitAt(p.Value) : null;
            bool same = (now == null && hover == null) || (now != null && hover != null && now.Card == hover.Card && now.Button == hover.Button);
            hover = now;
            // A question's explanation comes when the pointer rests on its card, and goes when it leaves.
            string card = now?.Card;
            var detailed = cards.FirstOrDefault(c => c.Key == card && !string.IsNullOrEmpty(c.Detail));
            if (detailed == null)
            {
                pendingDetail = null;
                detailWait.Stop();
                if (detailFor != null && card != detailFor) { detailFor = null; Render(); return; }
            }
            else if (detailFor != card && pendingDetail != card)
            {
                pendingDetail = card;
                detailWait.Stop();
                detailWait.Start();
            }
            if (!same) Render();
        }

        void Press(string card, string button)
        {
            var c = cards.FirstOrDefault(x => x.Key == card);
            if (c == null) return;
            if (c.Armed && firstShown.TryGetValue(card, out var at) && (DateTime.UtcNow - at).TotalMilliseconds < ArmMs)
            {
#if DEBUG
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-sign-presses.txt"), "  (not armed yet: " + (int)(DateTime.UtcNow - at).TotalMilliseconds + " ms)\r\n"); } catch { }
#endif
                return;
            }
            Pressed?.Invoke(card, button);
        }

#if DEBUG
        // Test builds: a press on a card's button as the person would make it (a check cannot click a window that is
        // never shown; the pointer itself is checked by the picture of the sign and its list of buttons).
        public bool PressForCheck(string card, string button)
        {
            string why = !shown ? "not shown" : cards.All(c => c.Key != card) ? "no such card" : !hits.Any(h => h.Card == card && h.Button == button) ? "no such button" : null;
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-sign-presses.txt"), DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + card + " / " + button + ": " + (why ?? "pressed") + " (cards: " + string.Join(", ", cards.Select(c => c.Key)) + "; buttons: " + string.Join(", ", hits.Where(h => h.IsButton).Select(h => h.Card + "/" + h.Button)) + ")\r\n"); } catch { }
            if (why != null) return false;
            Press(card, button);
            return true;
        }

        // Test builds: the pointer on a card (for its explanation) or off the sign.
        public void PointerForCheck(string card, string button)
        {
            var h = hits.FirstOrDefault(x => x.Card == card && x.Button == button);
            PointerAt(h == null ? (Point?)null : new Point(h.Box.X + h.Box.Width / 2, h.Box.Y + h.Box.Height / 2));
            if (h != null && pendingDetail != null) { detailWait.Stop(); detailFor = pendingDetail; Render(); }
        }
#endif

        // ----- drawing -----

        int P(double v) => (int)Math.Round(v * scale);

        static string FamilyOr(string wanted, string fallback)
        {
            try { using (var f = new FontFamily(wanted)) return f.Name; } catch { return fallback; }
        }

        static readonly string Regular = FamilyOr("Segoe UI Variable Text", "Segoe UI");
        static readonly string Semibold = FamilyOr("Segoe UI Variable Text Semibold", FamilyOr("Segoe UI Semibold", "Segoe UI"));

        const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;

        // One card as laid out: its box, and where each of its parts goes (all in the window's pixels).
        sealed class Laid
        {
            public Card Card;
            public Rectangle Box;
            public int Radius;
            public bool Ring;
            public Rectangle Text, Detail;
            public Point Dot;
            public readonly List<(string Id, string Label, Rectangle Box)> Buttons = new List<(string, string, Rectangle)>();
            public Rectangle Close;
        }

        Point Origin()
        {
            int pad = Shade();
            return new Point(area.Left + P(16) - pad, area.Bottom - P(16) - (size.Height - pad * 2) - pad);
        }

        int Shade() => P(18);   // room around the cards for their shadow

        void Render()
        {
            if (disposed || Handle == IntPtr.Zero || cards.Count == 0 || theme == null) return;
            using (var fText = new Font(Regular, 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fStrong = new Font(Semibold, 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fSmall = new Font(Regular, 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fButton = new Font(Semibold, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                int pad = Shade();
                int maxW = Math.Max(P(220), Math.Min(P(480), area.Width - P(32)));
                var laid = new List<Laid>();
                foreach (var c in cards) laid.Add(Lay(c, maxW, fText, fStrong, fSmall, fButton));
                int w = laid.Max(l => l.Box.Width), gap = P(8);
                int h = laid.Sum(l => l.Box.Height) + gap * (laid.Count - 1);
                // Stacked from the top; every card at the left edge.
                int y = pad;
                foreach (var l in laid) { Shift(l, pad, y); y += l.Box.Height + gap; }
                size = new Size(w + pad * 2, h + pad * 2);
                origin = Origin();

                hits.Clear();
                foreach (var l in laid)
                {
                    hits.Add(new Hit { Card = l.Card.Key, Box = l.Box });
                    foreach (var b in l.Buttons) hits.Add(new Hit { Card = l.Card.Key, Button = b.Id, Box = b.Box });
                    if (l.Card.Closable) hits.Add(new Hit { Card = l.Card.Key, Button = "", Box = l.Close });
                }
                if (hover != null) hover = hits.FirstOrDefault(x => x.Card == hover.Card && x.Button == hover.Button);

                still = Paint(laid, fText, fStrong, fSmall, fButton);
                var frame = dots.Count > 0 ? Beat(still) : still;
                Upload(frame);
#if DEBUG
                Report(laid, frame);
#endif
            }
        }

        static void Shift(Laid l, int dx, int dy)
        {
            Rectangle Move(Rectangle r) => r.IsEmpty ? r : new Rectangle(r.X + dx, r.Y + dy, r.Width, r.Height);
            l.Box = Move(l.Box);
            l.Text = Move(l.Text);
            l.Detail = Move(l.Detail);
            l.Close = Move(l.Close);
            l.Dot = new Point(l.Dot.X + dx, l.Dot.Y + dy);
            for (int i = 0; i < l.Buttons.Count; i++) l.Buttons[i] = (l.Buttons[i].Id, l.Buttons[i].Label, Move(l.Buttons[i].Box));
        }

        // Lays a card out at (0, 0).
        Laid Lay(Card c, int maxW, Font fText, Font fStrong, Font fSmall, Font fButton)
        {
            var l = new Laid { Card = c };
            int bh = P(28), bpad = P(12), bgap = P(6), close = P(28);
            var buttonSizes = c.Buttons.Select(b => new Size(TextRenderer.MeasureText(b.Label ?? "", fButton, new Size(int.MaxValue, int.MaxValue), Line).Width + bpad * 2, bh)).ToList();
            int buttonsW = buttonSizes.Sum(s => s.Width) + bgap * Math.Max(0, buttonSizes.Count - 1);

            if (c.Busy)
            {
                // A pill: the beating dot, one line of text, the button.
                int H = P(40), left = P(14), dotBox = P(14), gapIn = P(10), right = P(6);
                l.Radius = H / 2;
                l.Ring = true;
                int fixedW = left + dotBox + gapIn + gapIn + buttonsW + right + (c.Closable ? close : 0);
                int natural = TextRenderer.MeasureText(c.Text, fText, new Size(int.MaxValue, int.MaxValue), Line).Width + 1;
                int textW = Math.Max(P(40), Math.Min(natural, maxW - fixedW));
                l.Box = new Rectangle(0, 0, fixedW + textW, H);
                l.Dot = new Point(left + dotBox / 2, H / 2);
                l.Text = new Rectangle(left + dotBox + gapIn, P(6), textW, H - P(12));   // (inside the ring: the words' ground is painted)
                int x = l.Text.Right + gapIn;
                for (int i = 0; i < c.Buttons.Length; i++) { l.Buttons.Add((c.Buttons[i].Id, c.Buttons[i].Label, new Rectangle(x, (H - bh) / 2, buttonSizes[i].Width, bh))); x += buttonSizes[i].Width + bgap; }
                if (c.Closable) l.Close = new Rectangle(l.Box.Width - right - close, (H - close) / 2, close, close);
                return l;
            }

            var font = c.Strong ? fStrong : fText;
            int padL = P(16), padR = P(6), padY = P(8), minH = P(42);
            l.Radius = P(12);
            bool question = c.Strong || c.Buttons.Length > 1;
            if (!question)
            {
                // A notice: its text, its button and its ✕ in one row; the text breaks into lines when it is long.
                int fixedW = padL + P(12) + buttonsW + (c.Buttons.Length > 0 ? P(4) : 0) + (c.Closable ? close : 0) + padR;
                int natural = TextRenderer.MeasureText(c.Text, font, new Size(int.MaxValue, int.MaxValue), Line).Width;
                int textW = Math.Max(P(80), Math.Min(natural + 1, maxW - fixedW));
                int textH = Math.Min(TextRenderer.MeasureText(c.Text, font, new Size(textW, int.MaxValue), Wrap).Height, (int)Math.Ceiling(font.GetHeight() * 3));
                int H = Math.Max(minH, textH + padY * 2);
                l.Box = new Rectangle(0, 0, fixedW + textW, H);
                l.Text = new Rectangle(padL, (H - textH) / 2, textW, textH);
                int x = l.Text.Right + P(12);
                for (int i = 0; i < c.Buttons.Length; i++) { l.Buttons.Add((c.Buttons[i].Id, c.Buttons[i].Label, new Rectangle(x, (H - bh) / 2, buttonSizes[i].Width, bh))); x += buttonSizes[i].Width + bgap; }
                if (c.Closable) l.Close = new Rectangle(l.Box.Width - padR - close, (H - close) / 2, close, close);
                return l;
            }

            // A question: its text (and, while the pointer rests on it, its explanation) over a row of buttons; the ✕
            // at the top right.
            {
                int top = P(10), bottom = P(10), right = P(8);
                int natural = TextRenderer.MeasureText(c.Text, font, new Size(int.MaxValue, int.MaxValue), Line).Width;
                int W = Math.Min(maxW, Math.Max(P(300), Math.Max(padL + natural + P(10) + close + right, padL + buttonsW + right)));
                int textW = W - padL - P(10) - close - right;
                int textH = TextRenderer.MeasureText(c.Text, font, new Size(textW, int.MaxValue), Wrap).Height;
                textH = Math.Min(textH, (int)Math.Ceiling(font.GetHeight() * 4));
                int y = top + Math.Max(0, (close - (int)Math.Ceiling(font.GetHeight())) / 2);
                l.Text = new Rectangle(padL, y, textW, textH);
                y = l.Text.Bottom;
                if (detailFor == c.Key && !string.IsNullOrEmpty(c.Detail))
                {
                    int dw = W - padL - right - P(6);
                    int dh = Math.Min(TextRenderer.MeasureText(c.Detail, fSmall, new Size(dw, int.MaxValue), Wrap).Height, (int)Math.Ceiling(fSmall.GetHeight() * 9));
                    l.Detail = new Rectangle(padL, y + P(6), dw, dh);
                    y = l.Detail.Bottom;
                }
                y += P(10);
                // Buttons in rows, as many as fit across.
                int x = padL, rowTop = y;
                for (int i = 0; i < c.Buttons.Length; i++)
                {
                    if (x > padL && x + buttonSizes[i].Width > W - right) { x = padL; rowTop += bh + bgap; }
                    l.Buttons.Add((c.Buttons[i].Id, c.Buttons[i].Label, new Rectangle(x, rowTop, Math.Min(buttonSizes[i].Width, W - padL - right), bh)));
                    x += buttonSizes[i].Width + bgap;
                }
                int H = (c.Buttons.Length > 0 ? rowTop + bh : y - P(10)) + bottom;
                l.Box = new Rectangle(0, 0, W, Math.Max(minH, H));
                if (c.Closable) l.Close = new Rectangle(W - right - close, top - P(2), close, close);
                return l;
            }
        }

        static Color Mix(Color a, Color b, double t) => Ui.Blend(a, b, t);

        static GraphicsPath Round(Rectangle r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // The cards drawn into premultiplied BGRA pixels (with their shadow), top-down rows.
        byte[] Paint(List<Laid> laid, Font fText, Font fStrong, Font fSmall, Font fButton)
        {
            int W = size.Width, H = size.Height;
            var t = theme;
            Color surface = Mix(t.Toolbar, t.Text, t.IsDark ? 0.03 : 0.0);
            Color ink = t.Text, muted = t.Muted, line = t.Border, accent = t.Accent;
            Color soft = Mix(surface, accent, t.IsDark ? 0.16 : 0.12), softHover = Mix(surface, accent, t.IsDark ? 0.27 : 0.2), softDown = Mix(surface, accent, t.IsDark ? 0.36 : 0.28);
            // On a light look a light accent is hard to read on a soft fill: the button's words take the ink then.
            Color softInk = !t.IsDark && Theme.Luminance(accent) > 0.6 ? ink : accent;
            Color plain = Mix(surface, ink, 0.08), plainHover = Mix(surface, ink, 0.14), plainDown = Mix(surface, ink, 0.2);

            var mask = new byte[W * H];
            var rgb = new byte[W * H * 4];
            dots.Clear();
            // The cards' shapes, on nothing: what they cover becomes the window's shape (and its colors at the edges).
            using (var shapes = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(shapes))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    foreach (var l in laid)
                    {
                        using (var outer = Round(l.Box, l.Radius))
                        using (var edge = new SolidBrush(l.Ring ? accent : line))
                            g.FillPath(edge, outer);
                        int inset = l.Ring ? P(2) : Math.Max(1, P(1));
                        var inner = Rectangle.Inflate(l.Box, -inset, -inset);
                        using (var body = Round(inner, Math.Max(0, l.Radius - inset)))
                        using (var fill = new SolidBrush(surface))
                            g.FillPath(fill, body);
                    }
                }
                CopyAlpha(shapes, mask);
                CopyColor(shapes, rgb);
            }
            // Everything on the cards, on an opaque copy of them: buttons, the ✕, then the words, drawn by Windows with
            // the color under them given, so they come out as sharp as the rest of Windows' text.
            using (var face = new Bitmap(W, H, PixelFormat.Format32bppRgb))
            {
                PutColor(face, rgb);
                using (var g = Graphics.FromImage(face))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    foreach (var l in laid)
                    {
                        var c = l.Card;
                        if (c.Busy) dots.Add((l.Dot, surface));   // drawn by Beat, every frame
                        foreach (var b in l.Buttons)
                        {
                            bool on = hover != null && hover.Card == c.Key && hover.Button == b.Id;
                            bool down = on && pressed != null && pressed.Card == c.Key && pressed.Button == b.Id;
                            Color fill = c.Busy ? (down ? plainDown : on ? plainHover : plain) : (down ? softDown : on ? softHover : soft);
                            using (var path = Round(b.Box, c.Busy ? b.Box.Height / 2f : P(8)))
                            using (var brush = new SolidBrush(fill))
                            {
                                g.FillPath(brush, path);
                                if (c.Busy) using (var pen = new Pen(line, Math.Max(1, P(1)))) g.DrawPath(pen, path);
                            }
                        }
                        if (c.Closable)
                        {
                            bool on = hover != null && hover.Card == c.Key && hover.Button == "";
                            if (on) using (var brush = new SolidBrush(plainHover)) g.FillEllipse(brush, l.Close);
                            float cx = l.Close.X + l.Close.Width / 2f, cy = l.Close.Y + l.Close.Height / 2f, r = P(4.5);
                            using (var pen = new Pen(on ? ink : muted, Math.Max(1.2f, 1.5f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            {
                                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                                g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
                            }
                        }
                    }
                }
                using (var g = Graphics.FromImage(face))
                {
                    foreach (var l in laid)
                    {
                        var c = l.Card;
                        var font = c.Busy ? fText : c.Strong ? fStrong : fText;
                        TextRenderer.DrawText(g, c.Text, font, l.Text, ink, surface, c.Busy ? Line : Wrap | TextFormatFlags.EndEllipsis);
                        if (!l.Detail.IsEmpty) TextRenderer.DrawText(g, c.Detail, fSmall, l.Detail, muted, surface, Wrap | TextFormatFlags.EndEllipsis);
                        foreach (var b in l.Buttons)
                        {
                            bool on = hover != null && hover.Card == c.Key && hover.Button == b.Id;
                            bool down = on && pressed != null && pressed.Card == c.Key && pressed.Button == b.Id;
                            Color fill = c.Busy ? (down ? plainDown : on ? plainHover : plain) : (down ? softDown : on ? softHover : soft);
                            var words = TextRenderer.MeasureText(b.Label, fButton, new Size(int.MaxValue, int.MaxValue), Line);
                            var box = new Rectangle(b.Box.X + (b.Box.Width - Math.Min(words.Width, b.Box.Width - P(8))) / 2, b.Box.Y + P(4), Math.Min(words.Width, b.Box.Width - P(8)), b.Box.Height - P(8));
                            TextRenderer.DrawText(g, b.Label, fButton, box, c.Busy ? ink : softInk, fill, Line | TextFormatFlags.HorizontalCenter);
                        }
                    }
                }
                CopyColor(face, rgb);
            }

            // The shadow: the cards' shape, a little lower, softened.
            var shadow = new byte[W * H];
            int drop = P(4);
            for (int y = 0; y < H; y++)
            {
                int from = y - drop;
                if (from < 0) continue;
                Buffer.BlockCopy(mask, from * W, shadow, y * W, W);
            }
            int radius = Math.Max(1, P(5));
            for (int pass = 0; pass < 3; pass++) { BlurRows(shadow, W, H, radius); BlurColumns(shadow, W, H, radius); }
            double strength = theme.IsDark ? 0.42 : 0.2;

            var output = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++)
            {
                int m = mask[i];
                double s = shadow[i] / 255.0 * strength;
                double a = m / 255.0 + s * (1 - m / 255.0);
                int o = i * 4;
                output[o] = (byte)(rgb[o] * m / 255);
                output[o + 1] = (byte)(rgb[o + 1] * m / 255);
                output[o + 2] = (byte)(rgb[o + 2] * m / 255);
                output[o + 3] = (byte)Math.Round(Math.Min(1, a) * 255);
            }
            return output;
        }

        static void CopyAlpha(Bitmap bmp, byte[] mask)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (int x = 0; x < bmp.Width; x++) mask[y * bmp.Width + x] = row[x * 4 + 3];
                }
            }
            finally { bmp.UnlockBits(data); }
        }

        static void CopyColor(Bitmap bmp, byte[] rgb)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
            try
            {
                for (int y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, rgb, y * bmp.Width * 4, bmp.Width * 4);
            }
            finally { bmp.UnlockBits(data); }
        }

        static void PutColor(Bitmap bmp, byte[] rgb)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                for (int y = 0; y < bmp.Height; y++) Marshal.Copy(rgb, y * bmp.Width * 4, data.Scan0 + y * data.Stride, bmp.Width * 4);
            }
            finally { bmp.UnlockBits(data); }
        }

        // The sign with the dot drawn where it is in its beat (slowly out and back, 900 ms each way, as the interface's
        // dot did). Only a small square around each dot is drawn again; it lies inside the card, where nothing shows through.
        byte[] Beat(byte[] frame)
        {
            if (dots.Count == 0 || theme == null) return frame;
            var copy = (byte[])frame.Clone();
            double phase = (Environment.TickCount & int.MaxValue) % 1800 / 900.0;
            double k = phase <= 1 ? phase : 2 - phase;
            k = k * k * (3 - 2 * k);
            int half = P(12), side = half * 2;   // (well inside the pill's ring, whatever the scale)
            foreach (var (at, ground) in dots)
            {
                int x0 = at.X - half, y0 = at.Y - half;
                if (x0 < 0 || y0 < 0 || x0 + side > size.Width || y0 + side > size.Height) continue;
                using (var square = new Bitmap(side, side, PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(square))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(ground);
                        float d = P(9), glow = (float)(d / 2 * (1 + 1.3 * k));   // at most about 10 px out: inside the square
                        using (var halo = new SolidBrush(Color.FromArgb((int)(90 * k), theme.Accent)))
                            g.FillEllipse(halo, half - glow, half - glow, glow * 2, glow * 2);
                        float dot = (float)(d * (1 + 0.15 * k));
                        using (var brush = new SolidBrush(theme.Accent))
                            g.FillEllipse(brush, half - dot / 2, half - dot / 2, dot, dot);
                    }
                    var data = square.LockBits(new Rectangle(0, 0, side, side), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        var row = new byte[side * 4];
                        for (int y = 0; y < side; y++)
                        {
                            Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                            for (int x = 0; x < side; x++)
                            {
                                int o = ((y0 + y) * size.Width + x0 + x) * 4;
                                copy[o] = row[x * 4]; copy[o + 1] = row[x * 4 + 1]; copy[o + 2] = row[x * 4 + 2]; copy[o + 3] = 255;
                            }
                        }
                    }
                    finally { square.UnlockBits(data); }
                }
            }
            return copy;
        }

        static void BlurRows(byte[] a, int w, int h, int r)
        {
            var line = new int[w];
            for (int y = 0; y < h; y++)
            {
                int o = y * w, sum = 0, n = 2 * r + 1;
                for (int x = -r; x <= r; x++) sum += a[o + Math.Min(w - 1, Math.Max(0, x))];
                for (int x = 0; x < w; x++)
                {
                    line[x] = sum / n;
                    sum += a[o + Math.Min(w - 1, x + r + 1)] - a[o + Math.Max(0, x - r)];
                }
                for (int x = 0; x < w; x++) a[o + x] = (byte)line[x];
            }
        }

        static void BlurColumns(byte[] a, int w, int h, int r)
        {
            var col = new int[h];
            for (int x = 0; x < w; x++)
            {
                int sum = 0, n = 2 * r + 1;
                for (int y = -r; y <= r; y++) sum += a[Math.Min(h - 1, Math.Max(0, y)) * w + x];
                for (int y = 0; y < h; y++)
                {
                    col[y] = sum / n;
                    sum += a[Math.Min(h - 1, y + r + 1) * w + x] - a[Math.Max(0, y - r) * w + x];
                }
                for (int y = 0; y < h; y++) a[y * w + x] = (byte)col[y];
            }
        }

        // ----- handing the picture to Windows -----

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        void Upload(byte[] pixels)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = size.Width, biHeight = -size.Height, biPlanes = 1, biBitCount = 32 };
                dib = CreateDIBSection(mem, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return;
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                old = SelectObject(mem, dib);
                var dst = new POINT { X = origin.X, Y = origin.Y };
                var sz = new SIZE { CX = size.Width, CY = size.Height };
                var src = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0 /* AC_SRC_OVER */, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

#if DEBUG
        // Test builds: what the sign shows, where, and its picture, for the checks (written to the data folder).
        void Report(List<Laid> laid, byte[] pixels)
        {
            try
            {
                var facts = new Dictionary<string, object>
                {
                    ["shown"] = true,
                    ["hiddenFromCapture"] = HiddenFromCapture,
                    ["window"] = new[] { origin.X, origin.Y, size.Width, size.Height },
                    ["page"] = new[] { area.X, area.Y, area.Width, area.Height },
                    ["scale"] = scale,
                    ["cards"] = laid.Select(l => new Dictionary<string, object>
                    {
                        ["key"] = l.Card.Key,
                        ["text"] = l.Card.Text,
                        ["detail"] = l.Detail.IsEmpty ? null : l.Card.Detail,
                        ["box"] = new[] { origin.X + l.Box.X, origin.Y + l.Box.Y, l.Box.Width, l.Box.Height },
                        ["buttons"] = l.Buttons.Select(b => b.Id + "=" + b.Label).ToList(),
                        ["close"] = l.Card.Closable,
                    }).ToList(),
                };
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-sign.json"), Json.Write(facts));
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_SIGN_PNG"))) return;
                using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
                {
                    var data = bmp.LockBits(new Rectangle(0, 0, size.Width, size.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    for (int y = 0; y < size.Height; y++) Marshal.Copy(pixels, y * size.Width * 4, data.Scan0 + y * data.Stride, size.Width * 4);
                    bmp.UnlockBits(data);
                    bmp.Save(System.IO.Path.Combine(AppPaths.DataDir, "debug-sign.png"), ImageFormat.Png);
                }
            }
            catch { }
        }
#endif
    }
}
