using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // Translating what is selected on a page: "Translate selection" in the page's right-click menu, and a small
    // panel over the page with the translation. The panel names the two languages, and either can be changed
    // there from a short list: the languages picked before come first, so the long list of all of them is only
    // needed the first time. Text that is already in the wanted language goes into the second language, if one
    // was chosen in Settings, instead of being "translated" into itself.
    sealed partial class MainForm
    {
        sealed class TranslationJob
        {
            public int Id;
            public BrowserTab Tab;
            public string Url, Text, From = "", To = "", Result = "";
            public string FoundFrom = "", WentTo = "";   // what the last translation turned out to be from, and into
            public Point At;
            public CancellationTokenSource Cancel;
        }

        TranslationJob translation;
        int translationSeq;

        // The language text is translated into: the one chosen, else the one IGYO itself is shown in.
        string TranslateTarget => !string.IsNullOrEmpty(Settings.TranslateTo) ? Settings.TranslateTo
            : TranslateLang.All.Contains(L.Id) ? L.Id : L.English;

        // The languages offered first wherever one is picked: the two from Settings, the ones picked before, the
        // interface's own and English.
        List<string> MyLanguages() =>
            new[] { TranslateTarget, Settings.TranslateSecond }.Concat(Settings.TranslateRecent).Concat(new[] { L.Id, L.English })
                .Where(id => !string.IsNullOrEmpty(id) && TranslateLang.All.Contains(id)).Distinct().Take(8).ToList();

        // A picture's address that a tab can be opened at: one on the web or in a file on this PC, the picture
        // itself written out as an address ("data:image/…"), or one the page made ("blob:…").
        public static bool OpensAsPicture(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            foreach (var start in new[] { "http://", "https://", "file:///", "data:image/", "blob:http://", "blob:https://" })
                if (address.StartsWith(start, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // On a picture, the page's own right-click menu gets "Open image in new tab", which the engine's menu
        // lacks in an app like this one. The tab opens behind the one on show, as a link opened with the middle
        // button does, unless Settings says to go to such tabs.
        void AddOpenImage(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            try
            {
                var target = e.ContextMenuTarget;
                if (target.Kind != CoreWebView2ContextMenuTargetKind.Image || InternalPages.IsInternal(tab.Url)) return;
                // (The engine hands over the picture's address while saying, in the same breath, that there is
                //  none: its "has a source" answer was false for every picture tried. So the address decides.)
                string address = "";
                try { address = target.SourceUri ?? ""; } catch { }
                if (!OpensAsPicture(address)) return;
                var item = core.Environment.CreateContextMenuItem(L.T("Open image in new tab"), null, CoreWebView2ContextMenuItemKind.Command);
                Action open = () =>
                {
                    if (IsDisposed) return;
                    if (Settings.SwitchToNewTabs) NewTab(address, true, false); else NewTabBehind(address);
                };
                item.CustomItemSelected += (s, a) => BeginInvoke(open);
#if DEBUG
                // Test builds, when asked: the line is taken as pressed (a check could not press a line of the engine's menu).
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_OPEN_IMAGE"))) BeginInvoke(open);
#endif
                // Above the engine's own lines about the picture, where it has any.
                int index = 0;
                for (int i = 0; i < e.MenuItems.Count; i++)
                    if ((e.MenuItems[i].Name ?? "").IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0) { index = i; break; }
                e.MenuItems.Insert(index, item);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

#if DEBUG
        static string SafeSource(CoreWebView2ContextMenuTarget target)
        {
            try { return target.SourceUri ?? "(null)"; } catch (Exception ex) { return "(threw " + ex.GetType().Name + ")"; }
        }
#endif

        // The page's own right-click menu gets one more line while something is selected.
        public void OnPageContextMenu(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            AddOpenImage(tab, core, e);
            try
            {
                var target = e.ContextMenuTarget;
#if DEBUG
                // Test builds, when asked: what the engine's menu offers on a page with nothing selected is written down too.
                if (!target.HasSelection && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_CONTEXT_MENU")))
                {
                    try
                    {
                        System.IO.File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-contextmenu-page.txt"),
                            "target: " + target.Kind + (target.HasLinkUri ? ", link" : "") + (target.HasSourceUri ? ", has a source" : ", says it has no source") + ", source given: \"" + Ui.Trim(SafeSource(target), 60) + "\"" +
                            ", at " + e.Location.X + "," + e.Location.Y + "\n" +
                            string.Join("\n", e.MenuItems.Select(m => m.Kind + " " + m.Name + " | " + m.Label)));
                    }
                    catch { }
                    e.Handled = true;
                }
#endif
                if (!target.HasSelection || InternalPages.IsInternal(tab.Url)) return;
                string text = (target.SelectionText ?? "").Trim();
                if (text.Length == 0) return;
                var item = core.Environment.CreateContextMenuItem(L.T("Translate selection"), null, CoreWebView2ContextMenuItemKind.Command);
                var at = e.Location;
                item.CustomItemSelected += (s, a) => BeginInvoke(new Action(() => StartTranslation(tab, text, at)));
                // Right under "Copy", where the engine's menu has that line.
                int index = 0;
                for (int i = 0; i < e.MenuItems.Count; i++)
                    if (e.MenuItems[i].Name == "copy") { index = i + 1; break; }
                e.MenuItems.Insert(index, item);
#if DEBUG
                // Test builds, when asked: the engine's menu is not shown (a check could not press a line of it).
                // Its lines are written down, and "Translate selection" is taken as pressed.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_CONTEXT_MENU")))
                {
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-contextmenu.txt"), string.Join("\n", e.MenuItems.Select(m => m.Kind + " " + m.Name + " | " + m.Label))); } catch { }
                    e.Handled = true;
                    BeginInvoke(new Action(() => StartTranslation(tab, text, at)));
                }
#endif
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        void StartTranslation(BrowserTab tab, string text, Point at)
        {
            if (IsDisposed || tab != current) return;
            CancelTranslation();
            translation = new TranslationJob { Id = ++translationSeq, Tab = tab, Url = tab.Url, Text = text, At = at, To = TranslateTarget };
            RunTranslation();
        }

#if DEBUG
        // Test builds: the checks cannot press a line of the engine's own right-click menu, so they start a
        // translation of the current page's selection directly.
        async void TranslateSelectionForChecks()
        {
            var tab = current;
            if (tab?.Core == null) return;
            var answer = await tab.Core.ExecuteScriptAsync("(() => { const s = getSelection(); const r = s.rangeCount ? s.getRangeAt(0).getBoundingClientRect() : { left: 0, bottom: 0 }; return [String(s), Math.round(r.left * devicePixelRatio), Math.round(r.bottom * devicePixelRatio)]; })()");
            var parts = Json.Arr(Json.Parse(answer));
            if (parts.Length == 3 && (parts[0] as string ?? "").Trim().Length > 0)
                StartTranslation(tab, ((string)parts[0]).Trim(), new Point(Convert.ToInt32(parts[1]), Convert.ToInt32(parts[2])));
        }
#endif

        void CancelTranslation()
        {
            try { translation?.Cancel?.Cancel(); } catch { }
        }

        Dictionary<string, object> TranslationMessage(TranslationJob job, string state) => new Dictionary<string, object>
        {
            ["id"] = job.Id, ["state"] = state,
            ["x"] = job.At.X, ["y"] = job.At.Y,   // where the selection was right-clicked, in the page's own pixels
            ["from"] = job.From, ["to"] = job.To, ["text"] = job.Result,
            ["mine"] = MyLanguages(), ["all"] = TranslateLang.All, ["service"] = Translator.ServiceName,
        };

        async void RunTranslation()
        {
            var job = translation;
            if (job == null) return;
            CancelTranslation();
            var cancel = job.Cancel = new CancellationTokenSource();
            string asked = job.From;   // "" when the language is to be found out
            var working = TranslationMessage(job, "working");
            working["auto"] = asked.Length == 0;
            Post("translate", working);
            Translation result;
            try
            {
                bool japaneseReader = MyLanguages().Contains("ja") && !MyLanguages().Any(id => id.StartsWith("zh"));
                result = await Translator.TranslateAsync(job.Text, asked, job.To, Settings.TranslateSecond, japaneseReader, cancel.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { ErrorLog.Write(ex); result = new Translation { Error = "service", To = job.To }; }
            if (IsDisposed || translation != job || cancel.IsCancellationRequested) return;
            job.Result = result.Text ?? "";
            job.FoundFrom = result.From ?? "";
            job.WentTo = string.IsNullOrEmpty(result.To) ? job.To : result.To;
            var message = TranslationMessage(job, result.Error != null ? "error" : result.Same ? "same" : "done");
            message["from"] = job.FoundFrom;
            message["to"] = job.WentTo;
            message["auto"] = asked.Length == 0;
            if (result.Partial) message["note"] = L.T("The selection is long: only its beginning was translated.");
            if (result.Error != null)
                message["error"] = result.Error == "quota" ? L.T("{0} has given its free translations for today.", Translator.ServiceName)
                    : result.Error == "network" ? L.T("The translation service could not be reached.")
                    : L.T("The translation service did not give a translation.");
            Post("translate", message);
        }

        // A language was picked in the panel. The one to translate into is kept for next time; the one to translate
        // from is only for this text ("" goes back to finding it out). Both join the short list.
        void TranslateAgain(string from, string to)
        {
            var job = translation;
            if (job == null) return;
            from = from ?? job.From;
            to = string.IsNullOrEmpty(to) ? job.To : to;
            if (!TranslateLang.IsKnown(from) || !TranslateLang.All.Contains(to)) return;
            bool changed = false;
            if (to != TranslateTarget) { Settings.TranslateTo = to; changed = true; }
            foreach (var id in new[] { from, to })
            {
                if (string.IsNullOrEmpty(id) || (Settings.TranslateRecent.Count > 0 && Settings.TranslateRecent[0] == id)) continue;
                Settings.TranslateRecent.Remove(id);
                Settings.TranslateRecent.Insert(0, id);
                changed = true;
            }
            while (Settings.TranslateRecent.Count > 6) Settings.TranslateRecent.RemoveAt(Settings.TranslateRecent.Count - 1);
            job.From = from;
            job.To = to;
            if (changed) app.SaveSettings();
            RunTranslation();
        }

        void CopyTranslation()
        {
            if (translation == null || translation.Result.Length == 0) return;
            CopyText(translation.Result);
            ShowNotice(L.T("Translation copied."));
        }

        void OpenTranslationSite()
        {
            var job = translation;
            if (job == null) return;
            // The website is told the languages as they turned out here (it can still be changed there).
            NewTab(Translator.SiteUrl(job.Text, job.From.Length > 0 ? job.From : job.FoundFrom, job.WentTo.Length > 0 ? job.WentTo : job.To), true, false);
            CloseTranslation(true);
        }

        // The panel goes away: closed by its own button (tell: false), or because its page is no longer the one
        // on show (tell: true, so the interface takes it down).
        void CloseTranslation(bool tell)
        {
            if (translation == null) return;
            CancelTranslation();
            int id = translation.Id;
            translation = null;
            if (tell && ui != null && ui.Ready) Post("translateClose", new Dictionary<string, object> { ["id"] = id });
        }

        // ----- the whole page -----

        // Whether the page on show is one that Google Translate's website is showing translated.
        bool PageIsTranslated => current != null && PageTranslation.IsTranslated(current.Url);

        // "Translate this page" and, on a page shown translated, "Show the original page": one tool for both.
        void TranslatePage()
        {
            var tab = current;
            if (tab?.Core == null) return;
            string url = tab.Url ?? "";
            string original = PageTranslation.Original(url);
            if (original != null)
            {
                tab.NavigateTo(original);
                return;
            }
            if (tab.IsBlank || InternalPages.IsInternal(url) || !PageTranslation.CanTranslate(url))
            {
                ShowNotice(L.T("Only pages on the web can be translated: Google Translate has to be able to open the page itself."));
                return;
            }
            tab.NavigateTo(PageTranslation.Address(url, TranslateTarget, L.Id));
        }

        // Called when a tab changed: a translation belongs to the page it was asked on.
        void TranslationFollows(BrowserTab tab)
        {
            var job = translation;
            if (job == null) return;
            if (job.Tab != current || (tab == job.Tab && tab.Url != job.Url)) CloseTranslation(true);
        }
    }
}
