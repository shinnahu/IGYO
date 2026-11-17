using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IGYO
{
    // Extensions in the window: their buttons on the toolbar, their lines in the tools menu, their popups and
    // their right-click menu. (What an extension is, and how it gets into a profile: Extensions.cs. The popup's
    // own view: ExtensionPopup.cs.)
    sealed partial class MainForm
    {
        ExtensionPopup extensionPopup;
        string closedPopupOf;          // the extension whose popup closed last, and when: the click that closed it
        DateTime closedPopupAt;        //   by taking the focus away must not open it again

        ExtensionInfo ExtensionByKey(string key) => string.IsNullOrEmpty(key) ? null : ExtensionSet?.List.FirstOrDefault(x => x.Key == key);

        // The extension's own picture, for IGYO's pages (they ask for /icon/ext/<its name in the folder>).
        public string ExtensionIconFile(string key) => ExtensionByKey(key)?.IconFile;

        static string ExtensionIconAddress(ExtensionInfo x)
        {
            if (x.IconFile == null) return "";
            long stamp = 0;
            try { stamp = File.GetLastWriteTimeUtc(x.IconFile).Ticks / 10000000; } catch { }
            return "/icon/ext/" + Uri.EscapeDataString(x.Key) + "?v=" + stamp;
        }

        // What the interface knows of each extension: enough to draw its button and say what a click does.
        List<object> ExtensionsState()
        {
            var list = new List<object>();
            foreach (var x in ExtensionSet?.List ?? new List<ExtensionInfo>())
                list.Add(new Dictionary<string, object>
                {
                    ["key"] = x.Key,
                    ["name"] = Ui.Trim(x.Name, 60),
                    ["title"] = Ui.Trim(string.IsNullOrEmpty(x.Title) ? x.Name : x.Title, 80),
                    ["icon"] = ExtensionIconAddress(x),
                    ["on"] = x.Error == null && x.Handle != null && x.Enabled,
                    ["popup"] = x.Popup != null,
                });
            return list;
        }

        // After the extensions folder was looked at: a new extension gets its button, what belonged to one that is
        // gone is dropped, and the engine's next start is told which one may record tabs. The interface is sent
        // the list as it now is.
        void ExtensionsLookedAt()
        {
            var set = ExtensionSet;
            if (set == null) return;
            bool changed = Settings.FollowExtensions(set.List.Select(x => x.Key));
            if (Settings.RecordingExtension.Length == 0)
            {
                // The engine's own note of the extension that may record (written when the person chose it) is
                // believed when the settings do not name one: the choice then holds even if the settings could
                // not be saved at the time.
                string noted = BrowserEngine.WantedRecording();
                var its = noted.Length == 0 ? null : set.List.FirstOrDefault(x => x.WantsRecording && EngineIdOf(x) == noted);
                if (its != null) { Settings.RecordingExtension = its.Key; changed = true; }
            }
            var recording = ExtensionByKey(Settings.RecordingExtension);
            BrowserEngine.SetWantedRecording(recording == null ? "" : EngineIdOf(recording));
            string now = Json.Write(ExtensionsState());
            bool same = now == extensionsSent;
            extensionsSent = now;
            if (changed) app.SaveSettings();   // (every window then sends its interface the settings anew)
            else if (!same && ui.Ready) Post("settings", new Dictionary<string, object> { ["settings"] = SettingsState(), ["profile"] = ProfileState() });
        }

        string extensionsSent;   // the list as the interface last had it

        // The id the engine knows the extension by (or will, once it is loaded).
        static string EngineIdOf(ExtensionInfo x) => x.Id ?? x.KeyId ?? ExtensionStore.IdOfPath(x.Folder) ?? "";

        // ----- a click on an extension's button, or on its line in the tools menu -----

        // (A line in a menu has no place of its own once the menu is gone: the interface is asked where the popup
        // should hang, and answers with the same message a click on the button sends.)
        void AskWhereForPopup(ExtensionInfo x) => Post("extensionAnchor", new Dictionary<string, object> { ["key"] = x.Key });

        void OnExtensionClick(Dictionary<string, object> m)
        {
            var x = ExtensionByKey(Json.Str(m, "key"));
            if (x == null) return;
            string name = Ui.Trim(x.Name, 44);
            bool wasOpen = extensionPopup != null && extensionPopup.IsOpen && extensionPopup.Extension == x;
            bool justClosed = closedPopupOf == x.Key && (DateTime.UtcNow - closedPopupAt).TotalMilliseconds < 450;
            CloseExtensionPopup();
            if (wasOpen || justClosed) return;   // a click on the button of the popup that is open closes it
            if (x.Error != null) { ShowNotice(name + ": " + x.Error, null, null, 12000); return; }
            if (x.Handle == null)
            {
                ShowNotice(L.T("\"{0}\" is used from the next start of IGYO on: close it and start it again.", name), null, null, 9000);
                return;
            }
            if (!x.Enabled)
            {
                ShowNotice(L.T("\"{0}\" is switched off.", name), L.T("Switch it on"), () => SetExtension(x, true), 9000);
                return;
            }
            if (x.Popup == null)
            {
                // In a browser the click itself is handed to the extension, which then does what it likes with it.
                // The engine has no way to hand it over in an app like this one.
                if (x.Options != null) OpenExtensionPage(x, x.Options);
                else ShowNotice(L.T("\"{0}\" has no popup of its own, and what a click on its button does in a browser cannot be passed on to it in IGYO.", name), null, null, 10000);
                return;
            }
            var tab = current;
            if (tab?.Core == null) return;
            var r = Numbers(Json.Get(m, "rect"));
            double scale = 1;
            try { scale = Convert.ToDouble(Json.Get(m, "dpr") ?? 1.0, System.Globalization.CultureInfo.InvariantCulture); } catch { }
            var anchor = r.Length == 4 ? new Rectangle(ui.Left + r[0], ui.Top + r[1], r[2], r[3]) : new Rectangle(ClientSize.Width - S(60), ui.Top + S(40), S(32), S(32));
            var popup = ExtensionPopup.Open(this, x, tab, anchor, scale);
            extensionPopup = popup;
            popup.Closed += () =>
            {
                closedPopupOf = popup.Extension.Key;
                closedPopupAt = DateTime.UtcNow;
                if (extensionPopup == popup) extensionPopup = null;
                if (!IsDisposed && ui.Ready) Post("extensionPopup", new Dictionary<string, object> { ["key"] = "" });
            };
        }

        public void OnExtensionPopupShown(ExtensionPopup popup)
        {
            if (popup != extensionPopup || !ui.Ready) return;
            Post("extensionPopup", new Dictionary<string, object> { ["key"] = popup.Extension.Key });
        }

        // Called whenever the popup should go: another tab, a click somewhere in the interface, the window resized.
        public void CloseExtensionPopup()
        {
            var popup = extensionPopup;
            extensionPopup = null;
            popup?.Close();
        }

        // The page took the keyboard: it was clicked, so a popup that is on show over it goes. (One that is still
        // being opened, or has only just come up, stays: the focus may be on its way back to the page from the
        // menu the popup was asked for in.)
        public void PageTookFocus()
        {
            var popup = extensionPopup;
            if (popup == null || !popup.IsShown || (DateTime.UtcNow - popup.ShownAt).TotalMilliseconds < 400) return;
            CloseExtensionPopup();
        }

        // A link in a popup, or a page the popup wants to go to: opened as a tab.
        public void OpenFromExtension(string uri)
        {
            if (IsDisposed || string.IsNullOrEmpty(uri)) return;
            if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !uri.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) return;
            NewTab(uri, true, false);
        }

        // An extension's popup asked the engine to record the tab and was refused (see BrowserEngine.Recording).
        // The popup cannot hear anything then, and most go on as if they were listening, so it is closed and the
        // person is asked whether this extension is to be the one that may record. That holds from the engine's
        // next start on, so a yes closes IGYO and starts it again, with the tabs that were open.
        public async void OnRecordingRefused(ExtensionInfo x)
        {
            if (IsDisposed || askingAboutRecording) return;
            string name = Ui.Trim(x.Name, 44);
            if (BrowserEngine.Recording.Length > 0 && BrowserEngine.Recording == EngineIdOf(x))
            {
                // It is the one that may record, and the engine knows: this tab is one that cannot be recorded.
                ShowNotice(L.T("\"{0}\" could not record this tab.", name));
                return;
            }
            askingAboutRecording = true;
            try
            {
                CloseExtensionPopup();
                bool chosen = Settings.RecordingExtension == x.Key;
                var other = chosen ? null : ExtensionByKey(Settings.RecordingExtension);
                string message = chosen
                    ? L.T("It is the extension allowed to record tabs, but the engine only learns that when IGYO starts.")
                    : L.T("It asked to record the sound of this tab and the engine refused. One extension can be allowed to record: it may then record any tab at any time. That holds from the next start of IGYO on.");
                if (other != null) message += " " + L.T("\"{0}\" is the one allowed now, and would no longer be.", Ui.Trim(other.Name, 44));
                message += " " + L.T("IGYO closes and starts again; your tabs come back.");
                if (app.Windows.Any(w => w.IsPrivate)) message += " " + L.T("Private windows are closed and do not come back.");
                bool yes = await ConfirmAsync(chosen ? L.T("Restart IGYO so that \"{0}\" can record?", name) : L.T("Let \"{0}\" record tabs?", name),
                    message, chosen ? L.T("Restart now") : L.T("Allow and restart"), L.T("Not now"));
                if (!yes || IsDisposed) return;
                if (!chosen) SetRecordingExtension(x.Key, true);
                app.Restart(this);
            }
            finally { askingAboutRecording = false; }
        }

        bool askingAboutRecording;

        // ----- its right-click menu -----

        IEnumerable<MenuEntry> ExtensionContextItems(string key)
        {
            var x = ExtensionByKey(key);
            if (x == null) yield break;
            yield return MenuEntry.Head(Ui.Trim(x.Name, 44));
            if (x.Error != null)
                yield return Item(L.T("Why it is not used…"), null, () => ShowNotice(Ui.Trim(x.Name, 40) + ": " + x.Error, null, null, 12000), icon: "info");
            else if (x.Handle == null)
                yield return MenuEntry.Note(L.T("Loaded the next time IGYO starts"));
            else
            {
                yield return new MenuEntry { Label = L.T("On"), Icon = "plug", Switch = x.Enabled, Run = () => SetExtension(x, !x.Enabled) };
                if (x.Options != null) yield return Item(L.T("Options"), null, () => OpenExtensionPage(x, x.Options), icon: "settings");
            }
            yield return MenuEntry.Note(SiteAccessText(x));
            yield return MenuEntry.Line();
            yield return Item(L.T("Manage extension"), null, () => OpenSettings("extensions"), icon: "info");
            string pin = Tools.ForExtension(x.Key);
            bool pinned = Settings.IsPinned(pin);
            yield return Item(pinned ? L.T("Unpin from the toolbar") : L.T("Pin to the toolbar"), null, () => SetPinned(pin, !pinned), icon: pinned ? "pin-on" : "pin");
            yield return Item(L.T("Show in folder"), null, () => ShowInExplorer(x.Entry), icon: "folder");
            yield return Item(L.T("Remove from IGYO…"), null, () => RemoveExtension(x), icon: "trash");
        }

        // In plain words, where the extension's manifest lets it read and change pages.
        public static string SiteAccessText(ExtensionInfo x)
        {
            if (x.Sites.Any(s => s == "<all_urls>" || s.Contains("://*/"))) return L.T("Can read and change pages on all websites");
            var hosts = x.Sites.Select(SiteOf).Where(h => h != null).Distinct().ToList();
            if (hosts.Count == 0) return L.T("Asks for no website by itself");
            string list = string.Join(", ", hosts.Take(4));
            return hosts.Count > 4 ? L.T("Can read and change pages on: {0} and {1} more", list, hosts.Count - 4) : L.T("Can read and change pages on: {0}", list);
        }

        // "*://*.example.com/*" -> "example.com"
        static string SiteOf(string pattern)
        {
            int at = pattern.IndexOf("://", StringComparison.Ordinal);
            string rest = at < 0 ? pattern : pattern.Substring(at + 3);
            int slash = rest.IndexOf('/');
            string host = (slash < 0 ? rest : rest.Substring(0, slash)).Trim();
            if (host.StartsWith("*.")) host = host.Substring(2);
            return host.Length == 0 || host == "*" ? null : Ui.Trim(host, 40);
        }

        // ----- switching, removing, recording -----

        public async void RemoveExtension(ExtensionInfo x) => await RemoveExtensionAsync(x);

        // Asks first. Says what came of it: "removed", "cancelled" or "failed".
        async Task<string> RemoveExtensionAsync(ExtensionInfo x)
        {
            var set = ExtensionSet;
            if (set == null || x == null) return "failed";
            string name = Ui.Trim(x.Name, 60);
            bool yes = await ConfirmAsync(L.T("Remove \"{0}\"?", name),
                L.T("It is taken out of IGYO and its files go from the extensions folder to the Recycle Bin. What it stored in IGYO is deleted."), L.T("Remove"), null, true);
            if (!yes || IsDisposed) return "cancelled";
            if (!set.List.Contains(x)) return "failed";   // gone meanwhile (removed another way while the question stood)
            CloseExtensionPopup();
            ProfileExtensions.Removal done;
            try { done = await set.RemoveAsync(x, LiveEngineProfile()); }
            catch (Exception ex) { ErrorLog.Write(ex); return "failed"; }
            // Its files served every profile: the others take it out of the engine too, now where a window of theirs
            // is open, else the next time one opens.
            ProfileExtensions.LookAgainExcept(Profile.Id);
            foreach (var w in app.Windows.Where(w => w != this && !w.IsPrivate && w.Profile.Id != Profile.Id).ToList()) w.ExtensionsChangedElsewhere();
            if (IsDisposed) return "removed";
            ExtensionsLookedAt();
            RefreshSettingsPages();
            if (done.Untouched || done.SwitchedOff)
                ShowNotice(done.Untouched
                    ? L.T("The engine would not let go of \"{0}\" while IGYO runs. It is taken out, files and all, the next time IGYO starts.", name)
                    : L.T("\"{0}\" is switched off. The engine would not let go of it while IGYO runs, so it is taken out completely the next time IGYO starts.", name),
                    L.T("Restart now"), () => app.Restart(this), 16000);
            else if (done.Left != null) ShowNotice(L.T("\"{0}\" was taken out of IGYO, but this could not be moved to the Recycle Bin: {1}", name, done.Left), L.T("Show in folder"), () => ShowInExplorer(done.Left), 14000);
            else ShowNotice(L.T("Removed \"{0}\".", name));
            return "removed";
        }

        // The store's own "Remove" button was pressed in a tab (see ExtensionStore.PageScript): the same thing as
        // "Remove from IGYO", for the extension the store's page named by its id. The page is told "removed",
        // "cancelled" or "failed"; or "unknown" when it is not one of this profile's extensions, and the engine's
        // own function is then called as before.
        public async Task<string> RemoveFromStoreButtonAsync(BrowserTab tab, string host, string id)
        {
            if (IsDisposed || IsPrivate || tab == null || !tabs.Contains(tab)) return "failed";
            // Only a page that really is at a store's address is listened to, and it is the tab on show.
            if (!Uri.TryCreate(tab.Url ?? "", UriKind.Absolute, out var at) || at.Scheme != "https" || !string.Equals(at.Host, host, StringComparison.OrdinalIgnoreCase)) return "failed";
            var x = ExtensionStore.IsId(id) ? ExtensionSet?.List.FirstOrDefault(e => EngineIdOf(e) == id) : null;
            if (x == null) return "unknown";
            if (tab != current) Select(tab);
            return await RemoveExtensionAsync(x);
        }

        // The one extension that may record tabs without a click on its button ("" for none). Said to the engine
        // when it starts, so it holds from the next start of IGYO on.
        public void SetRecordingExtension(string key, bool on)
        {
            var x = ExtensionByKey(key);
            if (x == null || !x.WantsRecording) return;
            if (on) Settings.RecordingExtension = x.Key;
            else if (Settings.RecordingExtension == x.Key) Settings.RecordingExtension = "";
            var chosen = ExtensionByKey(Settings.RecordingExtension);
            BrowserEngine.SetWantedRecording(chosen == null ? "" : EngineIdOf(chosen));
            app.SaveSettings();
        }

        // What Settings shows of the extensions (see SettingsPage).
        public List<object> ExtensionsForSettings()
        {
            var list = new List<object>();
            foreach (var x in ExtensionSet?.List ?? new List<ExtensionInfo>())
            {
                long bytes = 0;
                try { if (x.Folder != null) bytes = new DirectoryInfo(x.Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
                string id = EngineIdOf(x);
                bool may = Settings.RecordingExtension == x.Key;
                list.Add(new Dictionary<string, object>
                {
                    ["key"] = x.Key,
                    ["name"] = Ui.Trim(x.Name, 80),
                    ["icon"] = ExtensionIconAddress(x),
                    ["version"] = Ui.Trim(x.Version, 40),
                    ["description"] = Ui.Trim(x.Description, 400),
                    ["error"] = x.Error ?? "",
                    ["loaded"] = x.Handle != null,
                    ["on"] = x.Handle != null && x.Enabled,
                    ["pinned"] = Settings.IsPinned(Tools.ForExtension(x.Key)),
                    ["options"] = x.Options != null && x.Handle != null,
                    ["permissions"] = x.Permissions.Take(40).ToList(),
                    ["sites"] = SiteAccessText(x),
                    ["size"] = bytes,
                    ["store"] = x.Store ?? "",
                    ["storeId"] = x.StoreId ?? (x.Store != null && ExtensionStore.IsId(x.Key) ? x.Key : ""),
                    ["wantsRecording"] = x.WantsRecording,
                    ["mayRecord"] = may,
                    ["recordsNow"] = may && BrowserEngine.Recording.Length > 0 && BrowserEngine.Recording == id,
                });
            }
            return list;
        }

        public void LookForExtensions() => LoadExtensions(true);

        // What a button in Settings asks for, for one extension.
        public void ExtensionFromSettings(string key, string what, bool on)
        {
            var x = ExtensionByKey(key);
            if (x == null) return;
            switch (what)
            {
                case "on": SetExtension(x, on); break;
                case "pin": SetPinned(Tools.ForExtension(x.Key), on); break;
                case "record":
                    SetRecordingExtension(x.Key, on);
                    // (the engine is told when it starts: a restart is offered, with the tabs coming back)
                    if (on && Settings.RecordingExtension == x.Key && BrowserEngine.Recording != EngineIdOf(x))
                        ShowNotice(L.T("\"{0}\" may record tabs from the next start of IGYO on: close it and start it again.", Ui.Trim(x.Name, 44)), L.T("Restart now"), () => app.Restart(this), 16000);
                    break;
                case "options": if (x.Options != null) OpenExtensionPage(x, x.Options); break;
                case "folder": ShowInExplorer(x.Entry); break;
                case "store":
                    string id = x.StoreId ?? x.Key;
                    if (ExtensionStore.IsId(id))
                        NewTab(x.Store == "Microsoft Edge Add-ons" ? "https://microsoftedge.microsoft.com/addons/detail/" + id : "https://chromewebstore.google.com/detail/" + id, true, false);
                    break;
                case "remove": RemoveExtension(x); break;
            }
        }
    }
}
