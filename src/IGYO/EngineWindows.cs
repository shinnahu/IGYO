using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // Windows the engine opens by itself. IGYO shows every page in its own window, and the engine has no window
    // to show a page in. So when something inside the engine wants a page opened and it is not a tab that asks
    // (an extension can name a page to be opened when it is removed, and the engine opens it), the engine makes
    // a browser window of its own. That window is not IGYO's: it stands apart from it, nothing in IGYO knows
    // of it, and while it is open the engine's processes keep running after IGYO has been closed.
    // Such a window is closed where it is known to appear (while an extension is taken out), and any that is
    // there when IGYO starts or ends.
    static class EngineWindows
    {
        static uint engine;   // the engine's main process, once known

        // (Asked of the engine on the window's own thread; what was learned last is kept for the other callers.)
        static uint ProcessId()
        {
            try
            {
                var infos = BrowserEngine.Current?.GetProcessInfos();
                if (infos != null)
                    foreach (var p in infos)
                        if (p.Kind == CoreWebView2ProcessKind.Browser) { engine = (uint)p.ProcessId; break; }
            }
            catch { }
            return engine;
        }

        // The engine's main process (0 while it is not known).
        public static uint EngineProcessId() => ProcessId();

        // The engine's own browser windows: windows of its main process that stand by themselves, with a frame of
        // their own. Not the developer tools' window (docked into IGYO, or popped out of it), not one that floats
        // above the others (picture in picture), not a menu or a hint.
        static List<IntPtr> Find(uint process)
        {
            var found = new List<IntPtr>();
            if (process == 0) return found;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint p);
                if (p != process || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                var name = new StringBuilder(64);
                GetClassName(h, name, name.Capacity);
                if (name.ToString() != "Chrome_WidgetWin_1") return true;
                if (!IsWindowVisible(h)) return true;   // (with extensions in use the engine keeps one of its own, never shown: left alone)
                long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64(), ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((style & WS_CHILD) != 0 || (style & Framed) != Framed || (ex & (WS_EX_TOPMOST | WS_EX_TOOLWINDOW)) != 0) return true;
                var title = new StringBuilder(64);
                GetWindowText(h, title, title.Capacity);
                if (title.ToString().StartsWith("DevTools", StringComparison.Ordinal)) return true;
                found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static void Close(IntPtr window, string when)
        {
#if DEBUG
            Note(when, window);
#endif
            ShowWindowAsync(window, SW_HIDE);                        // out of sight at once…
            PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);  // …and closed the way its own X closes it
        }

        // Closes every window of that kind that is there now. How many there were.
        public static int CloseAll(string when)
        {
#if DEBUG
            if (Keep == "all" && when == "end") return 0;
#endif
            try
            {
                var all = Find(ProcessId());
                foreach (var h in all) Close(h, when);
                return all.Count;
            }
            catch { return 0; }
        }

#if DEBUG
        // Test builds: "removal" leaves the window an extension's removal brings up where it is, so that the
        // checks can see what the end of IGYO does about a window that is there; "all" leaves it there at the
        // end as well (as builds before this one did), so that they can see what the next start does about it.
        static readonly string Keep = Environment.GetEnvironmentVariable("NSB_TEST_KEEP_ENGINE_WINDOWS") ?? "";

        // Test builds: what was closed, and when, written down for the checks.
        static void Note(string when, IntPtr window)
        {
            try
            {
                var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
                var title = new StringBuilder(128); GetWindowText(window, title, title.Capacity);
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-engine-windows.txt"),
                    $"{when}: {name} style {GetWindowLongPtr(window, GWL_STYLE).ToInt64() & 0xFFFFFFFFL:X8} ex {GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL:X8} shown {IsWindowVisible(window)} \"{title}\"\n");
            }
            catch { }
        }
#endif

        // Does something that can make the engine open a window of its own (taking an extension out of a profile)
        // and closes any such window that appears meanwhile, or in the few seconds after: looked for every few
        // hundredths of a second, so that it is gone before it is seen.
        public static async Task Quietly(Func<Task> action)
        {
#if DEBUG
            if (Keep == "removal" || Keep == "all") { await action(); return; }
#endif
            uint process = ProcessId();
            var before = new HashSet<IntPtr>(Find(process));
            var closed = new HashSet<IntPtr>();
            int busy = 0;
            var timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref busy, 1) != 0) return;
                try
                {
                    foreach (var h in Find(process))
                        if (!before.Contains(h) && closed.Add(h)) Close(h, "an extension taken out");
                }
                catch { }
                finally { Volatile.Write(ref busy, 0); }
            }, null, 0, 30);
            try { await action(); }
            finally { _ = Task.Delay(5000).ContinueWith(t => timer.Dispose()); }
        }

        const int GWL_STYLE = -16, GWL_EXSTYLE = -20, SW_HIDE = 0;
        const uint GW_OWNER = 4, WM_CLOSE = 0x0010;
        const long WS_CHILD = 0x40000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L, Framed = WS_CAPTION | WS_THICKFRAME;
        const long WS_EX_TOPMOST = 0x8L, WS_EX_TOOLWINDOW = 0x80L;

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
