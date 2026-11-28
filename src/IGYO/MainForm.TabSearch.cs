using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace IGYO
{
    // The tab search, as Chrome's (his words, 27 Nov 2026, with a picture of Chrome's: "Also it does something? Chrome
    // does this"): a box to type in over the open tabs and the tabs closed lately. It opens from 異 at the start of the
    // tab strip (the Ura kit shows it) and with Ctrl+Shift+A in any look; the interface draws it (overlays.js).
    // Open tabs: those of every window of this profile (a private window: only its own), the one used last first.
    // Recently closed: this window's, the one closed last first, from the same list Ctrl+Shift+T takes them back from.
    sealed partial class MainForm
    {
        // A tab that was closed: where it was, its title and when.
        sealed class ClosedTab
        {
            static int nextId;
            public readonly int Id = ++nextId;   // how the interface names it
            public readonly string Url, Title;
            public readonly DateTime At = DateTime.UtcNow;

            public ClosedTab(string url, string title)
            {
                Url = url;
                Title = title ?? "";
            }
        }

        // The windows whose tabs this window's search lists.
        IEnumerable<MainForm> SearchWindows() =>
            IsPrivate ? new[] { this }
            : app.Windows.Where(w => w == this || (!w.IsDisposed && !w.IsPrivate && string.Equals(w.Profile?.Id, Profile?.Id, StringComparison.OrdinalIgnoreCase)));

        BrowserTab ListedTab(int id, out MainForm window)
        {
            foreach (var w in SearchWindows())
            {
                var tab = w.TabById(id);
                if (tab != null) { window = w; return tab; }
            }
            window = null;
            return null;
        }

        static long JsTime(DateTime utc) => (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        // Opens the search (the interface only brings the box to the front when it is open already). again: after a
        // change made from the search itself, which shows the lists again only while it is still open.
        void OpenTabSearch(bool again = false)
        {
            var now = DateTime.UtcNow;
            var open = new List<(DateTime used, Dictionary<string, object> line)>();
            foreach (var w in SearchWindows())
                foreach (var t in w.tabs)
                {
                    // In use now: the tab on show in this window, then the one on show in each other window; the rest
                    // when they were last left.
                    var used = t == w.current ? (w == this ? now : now.AddMilliseconds(-1)) : t.HiddenSince.ToUniversalTime();
                    open.Add((used, new Dictionary<string, object>
                    {
                        ["id"] = t.Id,
                        ["title"] = string.IsNullOrEmpty(t.Title) ? (t.IsBlank ? "" : t.Url) : t.Title,
                        ["url"] = t.IsBlank ? "" : t.Url,
                        ["icon"] = IconOf(t),
                        ["at"] = JsTime(used),
                        ["here"] = w == this,
                    }));
                }
            Post("tabSearch", new Dictionary<string, object>
            {
                ["again"] = again,
                ["open"] = open.OrderByDescending(o => o.used).Select(o => o.line).ToList(),
                ["closed"] = Enumerable.Reverse(closedTabs).Select(c => new Dictionary<string, object>
                {
                    ["key"] = c.Id,
                    ["title"] = string.IsNullOrEmpty(c.Title) ? c.Url : c.Title,
                    ["url"] = c.Url,
                    ["icon"] = SiteIcon(c.Url),
                    ["at"] = JsTime(c.At),
                }).ToList(),
            });
        }

        // A line of the search was chosen: that tab shows, in its own window (brought forward when that is another).
        void ShowFromSearch(int id)
        {
            var tab = ListedTab(id, out var window);
            if (tab == null) return;
            if (window != this)
            {
                if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
                window.Activate();
            }
            window.Select(tab);
        }

        // Its ✕: the tab closes, and the search shows the lists again (a tab closed in this window is now among the
        // closed ones).
        void CloseFromSearch(int id)
        {
            var tab = ListedTab(id, out var window);
            if (tab == null) return;
            bool last = window == this && tabs.Count == 1;
            window.CloseTab(tab);
            if (!last && !IsDisposed) OpenTabSearch(true);
        }

        void ReopenFromSearch(int key)
        {
            var closed = closedTabs.FirstOrDefault(c => c.Id == key);
            if (closed != null) ReopenClosed(closed);
        }

        void ReopenClosed(ClosedTab closed)
        {
            closedTabs.Remove(closed);
            NewTab(closed.Url, true, false);
        }
    }
}
