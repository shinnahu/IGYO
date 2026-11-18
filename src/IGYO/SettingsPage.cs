using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // Settings, the theme gallery and editor, and the bookmark manager are one page, https://settings.igyo,
    // shown in a tab and served from Assets\Settings. The page asks for things with requests to /api/*, which
    // are answered here and only for a tab that is showing this page: no other site can reach them.
    static class SettingsPage
    {
        public const string Origin = "https://settings.igyo";
        public const string Url = Origin + "/";
        public const string Filter = Origin + "/*";

        const string PageHeaders = InternalAssets.Common +
            "\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
            "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'" +
            "\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer";

        public static bool IsUrl(string url) =>
            !string.IsNullOrEmpty(url) &&
            (url.Equals(Origin, StringComparison.OrdinalIgnoreCase) || url.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase));

        public static bool TryServe(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string topLevelRequest)
        {
            var uri = e.Request.Uri;
            if (!IsUrl(uri) || core == null) return false;
            try
            {
                var path = new Uri(uri).AbsolutePath;
                if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    // Only as the page of a tab: never inside another site's frame, never as the target of a form.
                    if ((path == "/" || path == "/index.html") && e.Request.Method == "GET" && InternalAssets.SameAddress(uri, topLevelRequest))
                        InternalAssets.Respond(core, e, File.ReadAllBytes(Path.Combine(InternalAssets.AssetsDir, "Settings", "index.html")), "text/html; charset=utf-8", PageHeaders);
                    else InternalAssets.Refuse(core, e);
                    return true;
                }
                if (!IsUrl(core.Source)) { InternalAssets.Refuse(core, e, 403); return true; }
                if (e.Request.Method == "GET") Get(owner, core, e, path, InternalAssets.Query(uri));
                else if (e.Request.Method == "POST" && path.StartsWith("/api/")) Post(owner, core, e, path.Substring("/api/".Length));
                else InternalAssets.Refuse(core, e, 405);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                try { InternalAssets.Refuse(core, e, 500); } catch { }
            }
            return true;
        }

        // ----- reading -----

        static void Get(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string path, Dictionary<string, string> query)
        {
            if (InternalAssets.TryServeCommon(owner, core, e, path)) return;
            if (path.StartsWith("/avatar/"))
            {
                var png = owner.AvatarPng(Uri.UnescapeDataString(path.Substring("/avatar/".Length)));
                if (png != null) InternalAssets.Respond(core, e, png, "image/png");
                else InternalAssets.Refuse(core, e);
                return;
            }
            switch (path)
            {
                case "/api/state":
                    InternalAssets.RespondJson(core, e, State(owner));
                    owner.CheckGoogleSession();   // read again in the background; the page is told if the answer changed
                    return;
                case "/api/memory": InternalAssets.RespondJson(core, e, owner.MemoryState()); return;
                case "/api/bookmarks": InternalAssets.RespondJson(core, e, BookmarkTree(owner)); return;
                case "/api/theme":
                {
                    var def = ThemeStore.Find(query.TryGetValue("id", out var id) ? id : "");
                    if (def == null) InternalAssets.Refuse(core, e);
                    else InternalAssets.RespondText(core, e, ThemeStore.ToJson(def), "application/json; charset=utf-8");
                    return;
                }
                case "/api/kits":
                {
                    var file = Path.Combine(ThemeStore.BuiltInDir, "kits", "kits.json");
                    if (File.Exists(file)) InternalAssets.Respond(core, e, File.ReadAllBytes(file), "application/json; charset=utf-8");
                    else InternalAssets.RespondText(core, e, "{}", "application/json; charset=utf-8");
                    return;
                }
            }
            if (!InternalAssets.TryServeFile(core, e, "Settings", path.TrimStart('/'))) InternalAssets.Refuse(core, e);
        }

        static readonly (string label, double minutes)[] SleepOptions = { (L.Key("Never"), 0), (L.Key("1 minute"), 1), (L.Key("5 minutes"), 5), (L.Key("15 minutes"), 15), (L.Key("1 hour"), 60) };
        static readonly (string label, double minutes)[] UnloadOptions = { (L.Key("Never"), 0), (L.Key("15 minutes"), 15), (L.Key("30 minutes"), 30), (L.Key("1 hour"), 60), (L.Key("4 hours"), 240) };

        static List<Dictionary<string, object>> Options((string label, double minutes)[] options) =>
            options.Select(o => new Dictionary<string, object> { ["label"] = L.T(o.label), ["value"] = o.minutes }).ToList();

        static Dictionary<string, object> State(MainForm owner)
        {
            var app = owner.App;
            var s = app.Settings;
            var blocker = app.Blocker;
            var runtime = AppPaths.RuntimeFolder();
            return new Dictionary<string, object>
            {
                ["app"] = new Dictionary<string, object>
                {
                    ["name"] = InstallInfo.AppName,
                    ["tagline"] = L.T("Web browser"),
                    ["version"] = Application.ProductVersion,
                    ["engine"] = BrowserEngine.Current?.BrowserVersionString ?? "",
                    ["runtime"] = runtime ?? "",
                    ["dataDir"] = AppPaths.DataDir,
                    ["adList"] = blocker?.Version ?? "",
                    ["adRules"] = blocker?.RuleCount ?? 0,
                },
                ["settings"] = new Dictionary<string, object>
                {
                    ["Compact"] = s.Compact, ["Animations"] = s.Animations, ["NewTabPage"] = s.NewTabPage, ["SearchSuggestions"] = s.SearchSuggestions,
                    ["ShowBookmarksBar"] = s.ShowBookmarksBar, ["ShowDownloadsButton"] = s.ShowDownloadsButton,
                    ["ShowToolsButton"] = s.ShowToolsButton, ["ForceDark"] = s.ForceDark, ["EnhanceVideos"] = s.EnhanceVideos, ["EnhanceLevel"] = s.EnhanceLevel,
                    // "Pin.<tool>": whether that tool has a button of its own on the toolbar
                    ["Pin.adblock"] = s.IsPinned(Tools.AdBlock), ["Pin.dark"] = s.IsPinned(Tools.Dark), ["Pin.enhance"] = s.IsPinned(Tools.Enhance),
                    ["Pin.picker"] = s.IsPinned(Tools.Picker), ["Pin.translate"] = s.IsPinned(Tools.Translate), ["Pin.volume"] = s.IsPinned(Tools.Volume),
                    ["Pin.pip"] = s.IsPinned(Tools.Pip), ["Pin.shot"] = s.IsPinned(Tools.Shot), ["Pin.extensions"] = s.IsPinned(Tools.Extensions),
                    ["TranslateTo"] = s.TranslateTo ?? "", ["TranslateSecond"] = s.TranslateSecond ?? "",
                    ["SearchEngine"] = s.SearchEngine, ["CustomSearchUrl"] = s.CustomSearchUrl, ["RestoreTabs"] = s.RestoreTabs, ["SwitchToNewTabs"] = s.SwitchToNewTabs,
                    ["AskProfileAtStart"] = app.Profiles.AskAtStart,   // (kept with the profiles, in profiles.json)
                    ["AdBlock"] = s.AdBlock, ["SavePasswords"] = s.SavePasswords, ["IdentifyAsEdge"] = s.IdentifyAsEdge,
                    ["SleepAfterMinutes"] = s.SleepAfterMinutes, ["UnloadAfterMinutes"] = s.UnloadAfterMinutes, ["SleepWhenMinimized"] = s.SleepWhenMinimized, ["ReleaseMemory"] = s.ReleaseMemory,
                    ["MinimizedSleepSeconds"] = s.MinimizedSleepSeconds,
                    ["DownloadFolder"] = s.DownloadFolder ?? "", ["ScreenshotFolder"] = s.ScreenshotFolder ?? "",
                    ["EffectiveScreenshotFolder"] = s.EffectiveScreenshotFolder,
                },
                ["language"] = L.Chosen,     // "auto" or a language's id; "languages" are the ones that can be chosen
                ["languages"] = new[] { new Dictionary<string, object> { ["id"] = L.Auto, ["name"] = L.T("Same as Windows ({0})", L.Available().First(a => a.Key == L.FromWindows()).Value) } }
                    .Concat(L.Available().Select(a => new Dictionary<string, object> { ["id"] = a.Key, ["name"] = a.Value })).ToList(),
                ["searchEngines"] = Settings.SearchEngines.Keys.ToList(),
                ["translateLanguages"] = TranslateLang.All,          // what a selection can be translated between
                ["translateService"] = Translator.ServiceName,
                ["extensionsFolder"] = ExtensionFolder.Dir,     // where an extension is put for IGYO to use it
                ["extensionsOn"] = BrowserEngine.ExtensionsOn,  // whether the engine was started with its extension support
                ["extensions"] = owner.ExtensionsForSettings(), // this window's profile's extensions, one card each
                ["sleepOptions"] = Options(SleepOptions),
                ["unloadOptions"] = Options(UnloadOptions),
                // This window's profile's look: each profile has its own (see BrowserApp)
                ["theme"] = ThemeStore.Normalize(app.ThemeOf(owner.Profile)),
                ["activeTheme"] = owner.ThemeDef.id,
                ["previewing"] = app.PreviewingFor(owner.Profile),
                ["themeProfile"] = owner.Profile.Name,
                ["themes"] = ThemeStore.All().Select(ThemeStore.Summary).ToList(),
                ["accent"] = ThemeStore.AccentHex(app.AccentOf(owner.Profile)),
                ["accents"] = ThemeStore.LegacyAccents.Where(a => a.Value.Length > 0).Select(a => new Dictionary<string, object> { ["name"] = a.Key, ["hex"] = a.Value }).ToList(),
                ["profiles"] = app.Profiles.Profiles.Select(p => new Dictionary<string, object>
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["avatar"] = "/avatar/" + Uri.EscapeDataString(p.Id) + "?v=" + DateTime.UtcNow.Ticks,
                    ["here"] = p.Id == owner.Profile.Id,
                    ["open"] = app.Windows.Any(w => w.Profile.Id == p.Id && !w.IsPrivate),
                    ["hasPicture"] = File.Exists(ProfileStore.PicturePath(p.Id)),
                    ["isDefault"] = p.Id == "Default",
                    ["color"] = ProfileStore.ColorOf(p),
                    ["isStartDefault"] = string.Equals(app.Profiles.StartWith, p.Id, StringComparison.OrdinalIgnoreCase),
                }).ToList(),
                ["profileColors"] = ProfileStore.Colors,   // what a profile's circle can be colored with, besides a color of one's own
                ["profileNextColor"] = app.Profiles.NextColor(),   // a new profile's circle unless another is picked
                ["window"] = new Dictionary<string, object> { ["isPrivate"] = owner.IsPrivate, ["profile"] = owner.Profile.Name, ["googleSignedIn"] = owner.GoogleSignedIn },
                ["bridge"] = app.Bridge.SettingsState(owner),   // the Claude bridge, for this window's profile
                ["defaultBrowser"] = DefaultBrowser.State(),     // whether Windows opens links from other programs here
            };
        }

        static Dictionary<string, object> BookmarkTree(MainForm owner) => new Dictionary<string, object>
        {
            ["roots"] = owner.Bookmarks.Roots.Select(root => { var node = Node(root); node["title"] = owner.Bookmarks.TitleOf(root); return node; }).ToList(),
            ["chrome"] = ChromeImport.Profiles().Count > 0,
        };

        static Dictionary<string, object> Node(Bookmark b)
        {
            var d = new Dictionary<string, object> { ["id"] = b.Id, ["title"] = b.Title ?? "" };
            if (b.IsFolder) d["children"] = b.Children.Select(Node).ToList();
            else
            {
                d["url"] = b.Url ?? "";
                var host = Favicons.HostOf(b.Url);
                d["icon"] = host != null && Favicons.Has(host) ? "/icon/site/" + Uri.EscapeDataString(host) : "";
            }
            return d;
        }

        // ----- doing -----

        static readonly Dictionary<string, object> Ok = new Dictionary<string, object> { ["ok"] = true };

        static Dictionary<string, object> Error(string message) => new Dictionary<string, object> { ["error"] = message };

        // For work that waits (a file picker, a download): the page's request is answered when it is done.
        static void Later(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, Func<Task<object>> work)
        {
            var deferral = e.GetDeferral();
            owner.BeginInvoke(new Action(async () =>
            {
                try { InternalAssets.RespondJson(core, e, await work() ?? Ok); }
                catch (Exception ex) { try { InternalAssets.RespondJson(core, e, Error(ex.Message)); } catch { } }
                finally { deferral.Complete(); }
            }));
        }

        static void Post(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string command)
        {
            var app = owner.App;
            var body = InternalAssets.Body(e);
            var args = (string.IsNullOrWhiteSpace(body) ? null : Json.Obj(Json.Parse(body))) ?? new Dictionary<string, object>();
            string Str(string key) => Json.Str(args, key) ?? "";
            int Num(string key) { try { return (int)Math.Round(Convert.ToDouble(Json.Get(args, key) ?? 0)); } catch { return 0; } }
            void Answer(object value) => InternalAssets.RespondJson(core, e, value);

            switch (command)
            {
                case "set":
                    Answer(Set(app, owner.Profile, Str("key"), Json.Get(args, "value")) ? Ok : Error(L.T("That setting can't be changed here.")));
                    return;

                // ----- themes -----
                case "theme/apply":
                {
                    var id = Str("id");
                    if (id != ThemeStore.Auto && ThemeStore.Find(id) == null) { Answer(Error(L.T("That theme no longer exists."))); return; }
                    app.EndPreviewQuietly(owner.Profile);
                    app.ChooseTheme(owner.Profile, id);   // this window's profile only, its look sent once
                    Answer(Ok);
                    return;
                }
                case "theme/preview":
                {
                    var def = ThemeStore.Parse(Json.Write(Json.Get(args, "theme")));
                    if (def == null) { Answer(Error(L.T("That theme can't be shown."))); return; }
                    if (def.id.Length == 0) def.id = "preview";
                    app.PreviewTheme(def, owner.Profile);   // in this profile's windows
                    Answer(Ok);
                    return;
                }
                case "theme/preview-end":
                    if (app.PreviewingFor(owner.Profile)) app.PreviewTheme(null, owner.Profile);
                    Answer(Ok);
                    return;
                case "theme/save":
                {
                    var def = ThemeStore.Parse(Json.Write(Json.Get(args, "theme")));
                    if (def == null) { Answer(Error(L.T("That theme can't be saved."))); return; }
                    var saved = ThemeStore.Save(def);
                    app.EndPreviewQuietly(owner.Profile);
                    if (Json.Get(args, "apply") is bool apply && apply) app.ChooseTheme(owner.Profile, saved.id, apply: false);
                    app.ApplyTheme();   // once, for every profile (a theme other profiles have may have changed)
                    Answer(new Dictionary<string, object> { ["ok"] = true, ["id"] = saved.id });
                    return;
                }
                case "theme/duplicate":
                {
                    var copy = ThemeStore.Duplicate(Str("id"));
                    Answer(copy == null ? Error(L.T("That theme no longer exists.")) : new Dictionary<string, object> { ["ok"] = true, ["id"] = copy.id });
                    return;
                }
                case "theme/delete":
                {
                    var id = Str("id");
                    if (!ThemeStore.Delete(id)) { Answer(Error(L.T("Only your own themes can be deleted."))); return; }
                    // Every profile that had it (and the shared one in settings) goes back to Automatic.
                    if (ThemeStore.Forget(id, app.Profiles.Profiles, app.Settings)) { app.Profiles.Save(); app.SaveSettings(); }
                    app.EndPreviewQuietly(owner.Profile);
                    app.ApplyTheme();   // once, for every profile
                    Answer(Ok);
                    return;
                }
                case "theme/import":
                    Later(owner, core, e, () =>
                    {
                        using (var dialog = new OpenFileDialog { Title = L.T("Import a theme"), Filter = L.T("IGYO theme") + " (*.json;*.igyo-theme)|*.json;*.igyo-theme;*.tacit-theme|" + L.T("All files") + "|*.*", Multiselect = true })
                        {
                            if (dialog.ShowDialog(owner) != DialogResult.OK) return Task.FromResult<object>(new Dictionary<string, object> { ["cancelled"] = true });
                            var ids = new List<string>();
                            string problem = null;
                            foreach (var file in dialog.FileNames)
                            {
                                try { ids.Add(ThemeStore.Import(file).id); }
                                catch (Exception ex) { problem = Path.GetFileName(file) + ": " + ex.Message; }
                            }
                            var result = new Dictionary<string, object> { ["ok"] = ids.Count > 0, ["ids"] = ids };
                            if (problem != null) result["error"] = problem;
                            return Task.FromResult<object>(result);
                        }
                    });
                    return;
                case "theme/export":
                {
                    var def = ThemeStore.Find(Str("id"));
                    if (def == null) { Answer(Error(L.T("That theme no longer exists."))); return; }
                    Later(owner, core, e, () =>
                    {
                        using (var dialog = new SaveFileDialog { Title = L.T("Export theme"), Filter = L.T("IGYO theme") + " (*.json)|*.json", FileName = def.id + ".igyo-theme.json" })
                        {
                            if (dialog.ShowDialog(owner) != DialogResult.OK) return Task.FromResult<object>(new Dictionary<string, object> { ["cancelled"] = true });
                            ThemeStore.Export(def.id, dialog.FileName);
                            return Task.FromResult<object>(new Dictionary<string, object> { ["ok"] = true, ["file"] = dialog.FileName });
                        }
                    });
                    return;
                }
                case "theme/picture":
                    // A picture for a theme's New Tab background, handed to the editor as text so it travels inside the theme file.
                    Later(owner, core, e, () =>
                    {
                        using (var dialog = new OpenFileDialog { Title = L.T("Choose a background picture"), Filter = L.T("Pictures") + "|*.jpg;*.jpeg;*.png;*.gif;*.bmp|" + L.T("All files") + "|*.*" })
                        {
                            if (dialog.ShowDialog(owner) != DialogResult.OK) return Task.FromResult<object>(new Dictionary<string, object> { ["cancelled"] = true });
                            return Task.FromResult<object>(new Dictionary<string, object> { ["ok"] = true, ["data"] = PictureAsText(dialog.FileName) });
                        }
                    });
                    return;

                // ----- folders, ad block, privacy -----
                case "folder":
                {
                    bool downloads = Str("which") == "downloads";
                    if (Json.Get(args, "reset") is bool reset && reset)
                    {
                        if (downloads) app.Settings.DownloadFolder = ""; else app.Settings.ScreenshotFolder = "";
                        app.SaveSettings();
                        Answer(Ok);
                        return;
                    }
                    Later(owner, core, e, () =>
                    {
                        using (var dialog = new FolderBrowserDialog { SelectedPath = downloads ? app.Settings.DownloadFolder : app.Settings.ScreenshotFolder, Description = downloads ? L.T("Downloads") : L.T("Full-page screenshots") })
                        {
                            if (dialog.ShowDialog(owner) != DialogResult.OK) return Task.FromResult<object>(new Dictionary<string, object> { ["cancelled"] = true });
                            if (downloads) app.Settings.DownloadFolder = dialog.SelectedPath; else app.Settings.ScreenshotFolder = dialog.SelectedPath;
                            app.SaveSettings();
                            return Task.FromResult<object>(Ok);
                        }
                    });
                    return;
                }
                case "adblock/update":
                    Later(owner, core, e, async () =>
                    {
                        var task = AdBlocker.DownloadLatestAsync();
                        var blocker = await task;
                        app.BlockerTask = task;
                        return new Dictionary<string, object> { ["ok"] = true, ["version"] = blocker.Version, ["rules"] = blocker.RuleCount };
                    });
                    return;
                case "clear":
                {
                    CoreWebView2BrowsingDataKinds kinds = 0;
                    foreach (var kind in Json.Arr(Json.Get(args, "kinds")).OfType<string>())
                    {
                        switch (kind)
                        {
                            case "history": kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory; break;
                            case "cookies": kinds |= CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage; break;
                            case "cache": kinds |= CoreWebView2BrowsingDataKinds.DiskCache; break;
                            case "downloads": kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory; break;
                            case "passwords": kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave; break;
                            case "autofill": kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill; break;
                        }
                    }
                    if (kinds == 0) { Answer(Error(L.T("Nothing was selected."))); return; }
                    Later(owner, core, e, async () =>
                    {
                        await core.Profile.ClearBrowsingDataAsync(kinds);   // this tab's profile is the window's profile
                        return Ok;
                    });
                    return;
                }
                // ----- extensions (of the profile of the window this page is in) -----
                case "extension":
                    owner.ExtensionFromSettings(Str("key"), Str("what"), Json.Get(args, "on") is bool wanted && wanted);
                    Answer(Ok);
                    return;
                case "extensions/look":
                    owner.LookForExtensions();
                    Answer(Ok);
                    return;

                case "open":
                    switch (Str("what"))
                    {
                        case "data-folder": try { Process.Start("explorer.exe", $"\"{AppPaths.DataDir}\""); } catch { } break;
                        case "themes-folder":
                            try { Directory.CreateDirectory(ThemeStore.UserDir); Process.Start("explorer.exe", $"\"{ThemeStore.UserDir}\""); } catch { }
                            break;
                        case "extensions-folder":   // where an extension is put for IGYO to use it
                            try { Directory.CreateDirectory(ExtensionFolder.Dir); Process.Start("explorer.exe", $"\"{ExtensionFolder.Dir}\""); } catch { }
                            break;
                        case "fonts-folder":   // the fonts that come with IGYO, each with its license text
                            try { var fonts = Fonts.Dir; if (Directory.Exists(fonts)) Process.Start("explorer.exe", $"\"{fonts}\""); } catch { }
                            break;
                        case "google": owner.OpenUrl(owner.GoogleSignedIn ? GoogleSession.AccountUrl : GoogleSession.SignInUrl); break;
                    }
                    Answer(Ok);
                    return;

                // Windows' own page for choosing IGYO as the default browser.
                case "default-browser":
                {
                    var problem = DefaultBrowser.Choose();
                    Answer(problem == null ? Ok : Error(problem));
                    return;
                }

                // ----- the Claude bridge (for the profile of the window this page is in) -----
                case "bridge/enable":
                    app.Bridge.SetProfile(owner.Profile, Json.Get(args, "on") is bool on && on);
                    Answer(Ok);
                    return;
                case "bridge/pause":
                    app.Bridge.SetPaused(Json.Get(args, "on") is bool paused && paused);
                    Answer(Ok);
                    return;
                case "bridge/act":
                    if (!owner.Profile.ClaudeBridge) { Answer(Error(L.T("Switch on \"Let Claude use this profile\" first."))); return; }
                    app.Bridge.SetActing(owner.Profile, Json.Get(args, "on") is bool act && act);
                    // A window covered by others is only drawn (and so can only be pictured) when the engine was started that way.
                    if (owner.Profile.ClaudeAct && !BrowserEngine.DrawCovered)
                        owner.ShowClaudeNotice(L.T("Claude can take pictures of a window that other windows cover from IGYO's next start on."), L.T("Restart now"), () => app.Restart(owner), 16000);
                    Answer(Ok);
                    return;
                case "bridge/site":
                {
                    var site = Str("site");
                    var mode = Str("mode");
                    if (BridgeSites.OriginOf(site) != site || (mode != BridgeSites.Ask && mode != BridgeSites.Read && mode != BridgeSites.Act && mode != BridgeSites.Never)) { Answer(Error(L.T("That site can't be set."))); return; }
                    app.Bridge.SetSite(owner.Profile, site, mode);
                    Answer(Ok);
                    return;
                }
                case "bridge/program-remove":
                    app.Bridge.RemoveProgram(Str("path"));
                    Answer(Ok);
                    return;
                case "bridge/activity-clear":
                    app.Bridge.ClearActivity();
                    Answer(Ok);
                    return;

                // ----- profiles -----
                case "profile/add":
                {
                    var name = Str("name").Trim();
                    if (name.Length == 0) { Answer(Error(L.T("Give the profile a name."))); return; }
                    app.Profiles.Add(name.Length > 40 ? name.Substring(0, 40) : name, Str("color").Trim());   // the circle's color, as picked (else the next one)
                    app.SaveSettings();
                    Answer(Ok);
                    return;
                }
                case "profile/rename":
                {
                    var p = app.Profiles.Find(Str("id"));
                    var name = Str("name").Trim();
                    if (p == null || name.Length == 0) { Answer(Error(L.T("Give the profile a name."))); return; }
                    p.Name = name.Length > 40 ? name.Substring(0, 40) : name;
                    app.Profiles.Save();
                    app.SaveSettings();
                    Answer(Ok);
                    return;
                }
                case "profile/delete":
                {
                    var p = app.Profiles.Find(Str("id"));
                    if (p == null || p.Id == "Default") { Answer(Error(L.T("That profile can't be deleted."))); return; }
                    if (app.Windows.Any(w => w.Profile.Id == p.Id)) { Answer(Error(L.T("Close the windows of \"{0}\" first.", p.Name))); return; }
                    app.Profiles.Delete(p);
                    app.SaveSettings();
                    Answer(Ok);
                    return;
                }
                case "profile/open":
                {
                    var p = app.Profiles.Find(Str("id"));
                    if (p != null) owner.BeginInvoke(new Action(() => app.OpenOrFocus(p)));
                    Answer(Ok);
                    return;
                }
                case "profile/picture":
                {
                    var p = app.Profiles.Find(Str("id"));
                    if (p == null) { Answer(Error(L.T("That profile no longer exists."))); return; }
                    Later(owner, core, e, () =>
                    {
                        using (var dialog = new OpenFileDialog { Title = L.T("Choose a picture for \"{0}\"", p.Name), Filter = L.T("Pictures") + "|*.jpg;*.jpeg;*.png;*.gif;*.bmp|" + L.T("All files") + "|*.*" })
                        {
                            if (dialog.ShowDialog(owner) != DialogResult.OK) return Task.FromResult<object>(new Dictionary<string, object> { ["cancelled"] = true });
                            if (!ProfileStore.SetPicture(p, dialog.FileName)) return Task.FromResult<object>(Error(L.T("That file couldn't be used as a picture. Try a JPG or PNG.")));
                            app.SaveSettings();   // every window redraws its profile button
                            return Task.FromResult<object>(Ok);
                        }
                    });
                    return;
                }
                case "profile/remove-picture":
                {
                    var p = app.Profiles.Find(Str("id"));
                    if (p != null) { ProfileStore.RemovePicture(p); app.SaveSettings(); }
                    Answer(Ok);
                    return;
                }
                case "profile/default":
                {
                    // The profile opened at start without asking, when "Ask which profile to open" is off.
                    var p = app.Profiles.Find(Str("id"));
                    if (p == null) { Answer(Error(L.T("That profile no longer exists."))); return; }
                    bool makeDefault = Json.Get(args, "on") is bool askedFor && askedFor;
                    if (makeDefault) app.Profiles.StartWith = p.Id;
                    else if (string.Equals(app.Profiles.StartWith, p.Id, StringComparison.OrdinalIgnoreCase)) app.Profiles.StartWith = "";
                    app.Profiles.Save();
                    app.SaveSettings();   // Settings pages show it at once
                    Answer(Ok);
                    return;
                }
                case "profile/color":
                {
                    // The color of the circle with the profile's first letter (what stands for a profile without a picture).
                    var p = app.Profiles.Find(Str("id"));
                    var color = Str("color").Trim();
                    if (p == null) { Answer(Error(L.T("That profile no longer exists."))); return; }
                    if (!ProfileStore.IsColor(color)) { Answer(Error(L.T("That is not a color."))); return; }
                    p.Color = color.ToUpperInvariant();
                    app.Profiles.Save();
                    app.SaveSettings();   // every window redraws its profile button
                    Answer(Ok);
                    return;
                }

                // ----- bookmarks -----
                case "bm/open":
                {
                    var b = Find(owner, Num("id"));
                    if (b != null && !b.IsFolder) owner.BeginInvoke(new Action(() => owner.OpenUrl(b.Url)));
                    Answer(Ok);
                    return;
                }
                case "bm/add-folder":
                {
                    var store = owner.Bookmarks;
                    var parent = Find(owner, Num("parent"));
                    if (parent == null || !parent.IsFolder) parent = store.Bar;
                    var folder = Bookmark.Folder(Str("title").Trim().Length == 0 ? L.T("New folder") : Str("title").Trim());
                    parent.Children.Add(folder);
                    store.NotifyChanged();
                    Answer(new Dictionary<string, object> { ["ok"] = true, ["id"] = folder.Id });
                    return;
                }
                case "bm/add":
                {
                    var store = owner.Bookmarks;
                    var parent = Find(owner, Num("parent"));
                    if (parent == null || !parent.IsFolder) parent = store.Bar;
                    var url = AddressInput.ToUrl(Str("url"), q => null);
                    if (url == null) { Answer(Error(L.T("Type a web address, like example.com"))); return; }
                    var link = Bookmark.Link(Str("title"), url);
                    parent.Children.Add(link);
                    store.NotifyChanged();
                    Answer(new Dictionary<string, object> { ["ok"] = true, ["id"] = link.Id });
                    return;
                }
                case "bm/edit":
                {
                    var store = owner.Bookmarks;
                    var b = Find(owner, Num("id"));
                    if (b == null || store.Roots.Contains(b)) { Answer(Error(L.T("That bookmark no longer exists."))); return; }
                    var title = Str("title").Trim();
                    var url = Str("url").Trim();
                    b.Title = title.Length == 0 ? b.Url ?? L.T("Folder") : title;
                    if (!b.IsFolder && url.Length > 0) b.Url = url;
                    store.NotifyChanged();
                    Answer(Ok);
                    return;
                }
                case "bm/delete":
                {
                    var store = owner.Bookmarks;
                    var b = Find(owner, Num("id"));
                    if (b == null || store.Roots.Contains(b)) { Answer(Error(L.T("That bookmark no longer exists."))); return; }
                    store.Remove(b);
                    store.NotifyChanged();
                    Answer(Ok);
                    return;
                }
                case "bm/move":
                {
                    var store = owner.Bookmarks;
                    var b = Find(owner, Num("id"));
                    var folder = Find(owner, Num("folder"));
                    int index = Json.Get(args, "index") == null ? -1 : Num("index");
                    Answer(b != null && folder != null && store.Move(b, folder, index) ? Ok : Error(L.T("That can't be moved there.")));
                    if (b != null) store.NotifyChanged();
                    return;
                }
                case "bm/import-chrome": owner.BeginInvoke(new Action(owner.ImportFromChrome)); Answer(Ok); return;
                case "bm/import-file": owner.BeginInvoke(new Action(owner.ImportFromFile)); Answer(Ok); return;
                case "bm/export": owner.BeginInvoke(new Action(owner.ExportToFile)); Answer(Ok); return;
            }
            InternalAssets.Refuse(core, e);
        }

        static Bookmark Find(MainForm owner, int id) =>
            id == 0 ? null : owner.Bookmarks.Roots.SelectMany(BookmarkStore.Flatten).FirstOrDefault(b => b.Id == id);

        // Only the settings on this list can be changed by the page, each with the kind of value it takes.
        static bool Set(BrowserApp app, Profile profile, string key, object value)
        {
            var s = app.Settings;
            bool? flag = value as bool?;
            string text = value as string;
            double? number = null;
            try { if (value != null && !(value is bool) && !(value is string)) number = Convert.ToDouble(value); } catch { }
            if (key == "Language")
            {
                if (text == null || !L.IsChoice(text)) return false;
                app.SetLanguage(text);   // IGYO's own pages load again in that language (this one too)
                return true;
            }
            if (key.StartsWith("Pin."))
            {
                string tool = key.Substring(4);
                if (!flag.HasValue || !Tools.IsTool(tool)) return false;
                s.Pin(tool, flag.Value);
                app.SaveSettings();
                return true;
            }
            switch (key)
            {
                case "Compact" when flag.HasValue: s.Compact = flag.Value; break;
                case "Animations" when flag.HasValue: s.Animations = flag.Value; break;
                case "NewTabPage" when flag.HasValue: s.NewTabPage = flag.Value; break;
                case "SearchSuggestions" when flag.HasValue: s.SearchSuggestions = flag.Value; break;
                case "ShowBookmarksBar" when flag.HasValue: s.ShowBookmarksBar = flag.Value; break;
                case "ShowDownloadsButton" when flag.HasValue: s.ShowDownloadsButton = flag.Value; break;
                case "ShowToolsButton" when flag.HasValue: s.ShowToolsButton = flag.Value; break;
                case "ForceDark" when flag.HasValue: s.ForceDark = flag.Value; break;
                case "EnhanceVideos" when flag.HasValue: s.EnhanceVideos = flag.Value; break;
                case "EnhanceLevel" when text != null && Tools.IsLevel(text): s.EnhanceLevel = text; break;
                case "TranslateTo" when text != null && TranslateLang.IsKnown(text): s.TranslateTo = text; break;
                case "TranslateSecond" when text != null && TranslateLang.IsKnown(text): s.TranslateSecond = text; break;
                case "RestoreTabs" when flag.HasValue: s.RestoreTabs = flag.Value; break;
                case "AskProfileAtStart" when flag.HasValue: app.Profiles.AskAtStart = flag.Value; app.Profiles.Save(); break;
                case "SwitchToNewTabs" when flag.HasValue: s.SwitchToNewTabs = flag.Value; break;
                case "AdBlock" when flag.HasValue: s.AdBlock = flag.Value; break;
                case "SavePasswords" when flag.HasValue: s.SavePasswords = flag.Value; break;
                case "IdentifyAsEdge" when flag.HasValue: s.IdentifyAsEdge = flag.Value; break;
                case "SleepWhenMinimized" when flag.HasValue: s.SleepWhenMinimized = flag.Value; break;
                case "ReleaseMemory" when flag.HasValue: s.ReleaseMemory = flag.Value; break;
                case "SearchEngine" when text != null && Settings.SearchEngines.ContainsKey(text): s.SearchEngine = text; break;
                case "CustomSearchUrl" when text != null && text.Length <= 2048: s.CustomSearchUrl = text.Trim(); break;
                case "SleepAfterMinutes" when number.HasValue && SleepOptions.Any(o => Math.Abs(o.minutes - number.Value) < 0.001): s.SleepAfterMinutes = number.Value; break;
                case "UnloadAfterMinutes" when number.HasValue && UnloadOptions.Any(o => Math.Abs(o.minutes - number.Value) < 0.001): s.UnloadAfterMinutes = number.Value; break;
                case "Accent" when text != null && (text.Length == 0 || ThemeStore.TryParseColor(text, out _)): app.ChooseTheme(profile, accent: text); break;   // this profile's
                default: return false;
            }
            app.SaveSettings();
            return true;
        }

        // A picture as a data: address, scaled to at most 1920 pixels and kept as JPEG so a theme file stays small.
        static string PictureAsText(string file)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > 40L * 1024 * 1024) throw new InvalidDataException(L.T("That picture is too large."));
            using (var stream = new MemoryStream(File.ReadAllBytes(file)))
            using (var image = Image.FromStream(stream, true, true))
            {
                int orientation = Pictures.Orientation(image);
                if (orientation > 1) image.RotateFlip(Pictures.FlipFor(orientation));
                using (var scaled = Pictures.Scale(image, 1920))
                using (var output = new MemoryStream())
                {
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 84L);
                        scaled.Save(output, codec, parameters);
                    }
                    return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
                }
            }
        }
    }
}
