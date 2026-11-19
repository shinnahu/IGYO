using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IGYO
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // A link Windows handed over ("--single-argument <link>") is read as one link, and no word of it as a
            // switch: from here on only the switches before it count (see CommandLine).
            CommandLine.Read(Environment.CommandLine, args);
            if (CommandLine.HandedOver != null) args = CommandLine.Switches;

            // "--mcp": the relay a Claude app starts to reach the running browser (see McpRelay). No window, and it
            // must not go near the data folder, so it leaves before anything else here runs.
            if (args.Contains("--mcp")) return McpRelay.Run();
            StartupTrace.Mark("main");
#if !DEBUG
            // Release builds never take engine switches from the environment, such as a remote
            // debugging port another program could use to control the browser or read its cookies.
            foreach (var name in new[] { "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",
                                         "WEBVIEW2_USER_DATA_FOLDER", "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE" })
                Environment.SetEnvironmentVariable(name, null);
#endif
            if (args.Contains("--selftest"))
            {
                // The self-test never touches your real data: it works in a throwaway folder (and never moves old data).
                var scratch = Path.Combine(Path.GetTempPath(), "igyo-selftest-" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("NSB_DATA_DIR", scratch);
                try { return SelfTest.Run(args); }
                finally { try { Directory.Delete(scratch, true); } catch { } }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--uninstall")) return Uninstaller.Run(args);
            return RunBrowser(args);
        }

        // The browser itself. Kept out of Main on purpose: this is the first code that names the data folder, and
        // the modes above (the relay, the self-test) must have gone their own way before the data folder is as much
        // as looked for. (Code in Main itself can be prepared by .NET before Main's first line runs.)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int RunBrowser(string[] args)
        {
            // Unexpected errors are written to errors.log and the browser keeps going.
            BrowserApp app = null;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ErrorLog.Report(e.Exception, app);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ErrorLog.Write(e.ExceptionObject as Exception);

            // A copy started to take the place of one that is closing (BrowserApp.Restart) waits until that copy and
            // its engine have ended: how the engine is asked for cannot be changed while it runs, so the engine
            // has to start anew. If the other copy does not end, this one gives up and leaves things as they are.
            var waitFor = args.FirstOrDefault(a => a.StartsWith("--wait-for="))?.Substring("--wait-for=".Length);
            if (waitFor != null && !WaitForEnd(waitFor)) return 0;

            // When IGYO is open already, on the same data, what this copy was started with goes to that one, and
            // this one ends (see OneCopy).
            var profile = args.FirstOrDefault(a => a.StartsWith("--profile="))?.Substring("--profile=".Length);
            var also = args.FirstOrDefault(a => a.StartsWith("--also-profiles="))?.Substring("--also-profiles=".Length);
            var alsoProfiles = (also ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (!OneCopy.Claim(new OneCopy.Request { Links = CommandLine.Links.ToList(), Profile = profile ?? "", Restore = args.Contains("--restore-tabs"), AlsoProfiles = alsoProfiles.ToList() }))
                return 0;

            // What this start compiles is noted, so that the next start compiles it ahead on the PC's other processors.
            try
            {
                System.Runtime.ProfileOptimization.SetProfileRoot(AppPaths.DataDir);
                System.Runtime.ProfileOptimization.StartProfile("startup.profile");
            }
            catch { }

            BrowserApp.RestoreOnce = args.Contains("--restore-tabs");

            // A copy that had to start itself again says how the engine that is already running was asked for
            // (see BrowserEngine.StartAgainTheOtherWay).
            var given = args.FirstOrDefault(a => a.StartsWith("--engine-extensions="))?.Substring("--engine-extensions=".Length);
            if (given == "on" || given == "off") BrowserEngine.ExtensionsGiven = given == "on";
            given = args.FirstOrDefault(a => a.StartsWith("--engine-recording="))?.Substring("--engine-recording=".Length);
            if (given != null) BrowserEngine.RecordingGiven = ExtensionStore.IsId(given) ? given : "";
            given = args.FirstOrDefault(a => a.StartsWith("--engine-covered="))?.Substring("--engine-covered=".Length);
            if (given == "on" || given == "off") BrowserEngine.DrawCoveredGiven = given == "on";

            // The engine is asked for its first views now, and works on them while the rest gets ready (see EarlyViews).
            EarlyViews.Start(() => ProfileStore.StartId(profile));

            app = new BrowserApp(CommandLine.Links, profile, alsoProfiles);
            Application.Run(app);
#if DEBUG
            // Test builds: stays a while after its last window has closed, as a copy that is slow to end would, so
            // that the checks can start another copy while this one is closing (see OneCopy).
            if (int.TryParse(Environment.GetEnvironmentVariable("NSB_TEST_SLOW_EXIT"), out int slowExit) && slowExit > 0) System.Threading.Thread.Sleep(slowExit);
#endif
            return 0;
        }

        // "<process id of the other copy>,<process id of its engine>": true once the other copy has ended (its
        // engine is then given a little longer), false when the other copy is still there after twenty seconds.
        static bool WaitForEnd(string ids)
        {
            var parts = ids.Split(',');
            for (int i = 0; i < parts.Length && i < 2; i++)
            {
                if (!int.TryParse(parts[i], out int id) || id <= 4) continue;
                try
                {
                    using (var other = System.Diagnostics.Process.GetProcessById(id))
                    {
                        if (!other.WaitForExit(i == 0 ? 20000 : 10000) && i == 0) return false;
                    }
                }
                catch (ArgumentException) { }   // ended already
                catch { if (i == 0) return false; }
            }
            return true;
        }
    }

    // Owns the windows (one profile each), the shared settings, theme and ad block list.
    sealed class BrowserApp : ApplicationContext
    {
        readonly List<MainForm> windows = new List<MainForm>();
        readonly Dictionary<string, BookmarkStore> bookmarkStores = new Dictionary<string, BookmarkStore>(StringComparer.OrdinalIgnoreCase);

        public Settings Settings { get; private set; }
        public ProfileStore Profiles { get; private set; }
        public System.Drawing.Icon WindowIcon { get; private set; }   // the icon IGYO wears (Settings > Appearance > App icon), for every window
        // Each profile has a look of its own (Profile.Theme/Accent; one that never chose has the one in settings.json),
        // and every window shows its profile's. Theme and ThemeDef are the look of the profile used last: the profile
        // picker's, which belongs to no profile. Its accent is IGYO's own (settings.json: the color picked in setup)
        // when there is one, else that profile's.
        public Theme Theme => ThemeFor(LastProfile);              // the few colors Windows itself draws with
        public ThemeDef ThemeDef => ThemeDefFor(LastProfile);     // the whole theme, as pages load it
        public string AccentHex => ThemeStore.AccentHex(Settings?.Accent) is string own && own.Length > 0 ? own : AccentHexFor(LastProfile);
        public int ThemeStamp { get; private set; }      // changes whenever a look does, so pages fetch it again
        ThemeDef previewing;                             // a theme being edited, shown before it is saved,
        string previewingFor;                            //   in the windows of this profile only
        public bool Previewing => previewing != null;
        readonly Dictionary<string, Theme> looks = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase);

        Profile LastProfile => Profiles?.Find(Profiles.LastUsed) ?? Profiles?.Profiles.FirstOrDefault();
        public string ThemeOf(Profile profile) => ThemeStore.ChosenFor(profile, Settings);
        public string AccentOf(Profile profile) => profile?.Accent ?? Settings.Accent;
        public bool PreviewingFor(Profile profile) => previewing != null && profile != null && string.Equals(profile.Id, previewingFor, StringComparison.OrdinalIgnoreCase);
        public ThemeDef ThemeDefFor(Profile profile) => PreviewingFor(profile) ? previewing : ThemeStore.Resolve(ThemeOf(profile));
        public string AccentHexFor(Profile profile) => PreviewingFor(profile) ? "" : ThemeStore.AccentHex(AccentOf(profile));
        public Theme ThemeFor(Profile profile)
        {
            string key = profile?.Id ?? "";
            if (!looks.TryGetValue(key, out var look)) looks[key] = look = Theme.From(ThemeDefFor(profile), AccentHexFor(profile));
            return look;
        }
        public Task<AdBlocker> BlockerTask { get; set; }
        public AdBlocker Blocker => BlockerTask != null && BlockerTask.Status == TaskStatus.RanToCompletion ? BlockerTask.Result : null;
        public IReadOnlyList<MainForm> Windows => windows;
        public Bridge Bridge { get; private set; }       // lets a Claude app read pages, for the profiles that have it switched on
        public MemoryCare Memory { get; }                // hands memory that isn't being used back to Windows

        public event Action<string> ThemeChanged;   // the profile whose look changed; null: every profile's
        public event Action SettingsChanged;

        // Set by "--restore-tabs" (a copy that takes the place of one that restarted itself): the windows it opens
        // at its start bring their tabs back, also when tabs are not kept between starts.
        public static bool RestoreOnce;
        readonly string[] alsoProfiles;   // more profiles whose windows were open in that copy

        public BrowserApp(string[] urls, string profileId, string[] alsoProfiles = null)
        {
            this.alsoProfiles = alsoProfiles ?? new string[0];
            BlockerTask = Task.Run(() => AdBlocker.Load());
            Memory = new MemoryCare(this);
            Start(urls, profileId);
        }

        // Everything that was saved (settings, the language, profiles, the theme, the first profile's bookmarks) is
        // read on another thread. Reading it needs no window, and this thread has to stay free meanwhile: the
        // engine, which was asked for its first views a moment ago (EarlyViews), hands them over through this
        // thread's messages, and would wait for it. The window is built when the reading is done.
        async void Start(string[] urls, string profileId)
        {
            try
            {
                await Task.Run(() =>
                {
                    Settings = Settings.Load();
                    L.Use(Settings.Language);   // the interface's language, before anything is put into words
                    Profiles = ProfileStore.Load();
                    Profiles.DeletePendingProfiles();
                    if (Profiles.TakeSetupColor(Settings, AppPaths.AppDir)) { Profiles.Save(); Settings.Save(); }   // the color picked in setup, once
                    ThemeFor(Profiles.Find(profileId) ?? LastProfile);   // the themes read and the first window's look worked out, off the window's thread
                    BookmarksFor(Profiles.Find(profileId) ?? Profiles.Find(Profiles.LastUsed) ?? Profiles.Default);
                    WindowIcon = AppIcons.WindowIcon(Settings.AppIcon);   // the app icon chosen in Settings (or the program's own)
                    StartupTrace.Mark("saved things read");
                });
                Anim.Enabled = Settings.Animations;
                Bridge = new Bridge(this);
                // An app icon chosen in Settings goes back onto shortcuts that setup made again (an update) and onto
                // taskbar pins made since: a few seconds after the start, off this thread.
                if (Settings.AppIcon.Length > 0) AppIcons.ApplyToShortcutsInTurn(Settings.AppIcon, 5000);
                // With more than one profile, IGYO asks which one to open (unless it is told not to and has a default
                // one). Not when it was started for a link or for a profile, or to take the place of a restart.
                bool plainStart = Profiles.Find(profileId) == null && (urls == null || urls.Length == 0) && !RestoreOnce && alsoProfiles.Length == 0;
                if (plainStart && Profiles.AsksAtStart())
                {
                    AskForProfile();
                    OneCopy.Serve(this);
                    Bridge.Refresh();
                    return;
                }
                var profile = Profiles.Find(profileId) ?? (plainStart ? Profiles.OpensWithoutAsking() : null) ?? Profiles.Find(Profiles.LastUsed) ?? Profiles.Default;
                OpenWindow(profile, false, urls, true);
                foreach (var id in alsoProfiles)
                {
                    var other = Profiles.Find(id);
                    if (other != null && other.Id != profile.Id) OpenWindow(other, false, null, true);
                }
                RestoreOnce = false;   // (each window has taken note of it by now)
                OneCopy.Serve(this);   // links handed over by copies started meanwhile, and later, open from now on
                Bridge.Refresh();   // opens its pipe only if some profile has the switch on
                StartupTrace.Mark("window built and asked to show");

                // Following Windows between light and dark is not needed to start: hooked up a little later.
                await Task.Delay(2000);
                SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                if (windows.Count == 0)
                {
                    try { MessageBox.Show(InstallInfo.AppName + " couldn't start: " + ex.Message + "\n\n" + ErrorLog.FilePath, InstallInfo.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                    ExitThread();
                }
            }
        }

        ProfilePicker picker;   // up at start until a profile is chosen (with more than one profile)

        void AskForProfile()
        {
            picker = new ProfilePicker(this);
            var shown = picker;
            shown.Picked += p =>
            {
                picker = null;
                OpenWindow(p, false, null, true);
            };
            shown.FormClosed += (s, e) =>
            {
                if (picker == shown) picker = null;
                if (windows.Count == 0 && picker == null) ExitThread();   // closed without a choice: IGYO ends
            };
            shown.Show();
            StartupTrace.Mark("profile picker shown");
            _ = HookSystemPreferenceLater();
        }

        async Task HookSystemPreferenceLater()
        {
            await Task.Delay(2000);
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        }

        // restoreSession: the profile's first window brings back its windows as they were left (the others first,
        // behind it). adopt and place: a window made for a tab dragged out of another window; torn: that tab is still
        // being dragged (see MainForm.Tear).
        public MainForm OpenWindow(Profile profile, bool isPrivate, string[] urls = null, bool restoreSession = false, BrowserTab adopt = null, System.Drawing.Rectangle? place = null, bool torn = false)
        {
            BookmarkStore.Ui = System.Threading.SynchronizationContext.Current ?? BookmarkStore.Ui;   // where changes made on disk are applied
            bool firstOfProfile = !isPrivate && !windows.Any(w => w.Profile.Id == profile.Id && !w.IsPrivate);
            var saved = restoreSession && firstOfProfile && adopt == null ? Session.Load(profile.Id) : null;
            if (saved?.Others != null && (Settings.RestoreTabs || RestoreOnce))
                foreach (var other in saved.Others.Where(o => o?.Tabs != null && o.Tabs.Count > 0))
                    OpenOne(profile, false, null, other, null, null);
            return OpenOne(profile, isPrivate, urls, saved, adopt, place, torn);
        }

        // The window whose tab strip a tab dragged at this point of the screen goes into: another window of the same
        // profile, private or not alike (a tab can't change either), with its strip there, and on top there (a window
        // covered by another program's is not reached through it). from: the window the tab is dragged with.
        public MainForm WindowToJoin(MainForm from, System.Drawing.Point screen)
        {
            var here = windows.Where(w => w != from && w.Profile.Id == from.Profile.Id && w.IsPrivate == from.IsPrivate && w.TabStripContains(screen)).ToList();
#if DEBUG
            // Test builds: why a strip was or was not taken (checks cannot see the screen).
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-join.txt"),
                    $"{DateTime.UtcNow:HH:mm:ss.fff} at {screen.X},{screen.Y} from {from.Handle} strips here: {string.Join(" ", here.Select(w => w.Handle))}; " +
                    $"others: {string.Join(" ", windows.Where(w => w != from).Select(w => w.Handle + "=" + w.StripDebug(screen)))}; top: {ScreenWindows.Describe(ScreenWindows.TopAt(screen, from.IsHandleCreated ? from.Handle : IntPtr.Zero))}\r\n");
            }
            catch { }
#endif
            if (here.Count == 0) return null;
            // The one on top there (the margin around a row reaches a little past the window's edges: what covers the
            // window is looked for just inside them then).
            var skip = from.IsHandleCreated ? from.Handle : IntPtr.Zero;
            foreach (var w in here.OrderByDescending(w => w.LastActive))
            {
                var b = w.ScreenBounds;
                if (b.IsEmpty) continue;
                var probe = new System.Drawing.Point(Math.Max(b.Left, Math.Min(b.Right - 1, screen.X)), Math.Max(b.Top, Math.Min(b.Bottom - 1, screen.Y)));
                if (ScreenWindows.TopAt(probe, skip) == w.Handle) return w;
            }
            return null;
        }

        // Windows of a profile closed while another of its windows stayed open, with when (see Session.Together).
        readonly Dictionary<string, List<(DateTime at, Session window)>> closedLately = new Dictionary<string, List<(DateTime, Session)>>();

        // A window that isn't private is closing, with its tabs and place: kept for a moment if the profile has other
        // windows open, written down with the ones closed shortly before it if it is the profile's last.
        public void WindowClosing(MainForm window, Session state)
        {
            if (window.IsPrivate) return;
            string id = window.Profile.Id;
            if (!closedLately.TryGetValue(id, out var list)) closedLately[id] = list = new List<(DateTime, Session)>();
            var now = DateTime.UtcNow;
            if (!IsLastWindowOf(window))
            {
                list.RemoveAll(c => now - c.at > Session.TogetherWithin);
                list.Add((now, state));
                return;
            }
            Session.Together(state, list, now).Save(id);
            list.Clear();
        }

        MainForm OpenOne(Profile profile, bool isPrivate, string[] urls, Session restore, BrowserTab adopt, System.Drawing.Rectangle? place, bool torn = false)
        {
            var window = new MainForm(this, profile, isPrivate, urls, restore, adopt, place, torn);
            windows.Add(window);
            window.FormClosed += (s, e) =>
            {
                windows.Remove(window);
                EndPreviewIfEditorGone();
                if (windows.Count == 0) ExitThread();
            };
            window.Show();
            if (!isPrivate && Profiles.LastUsed != profile.Id)
            {
                Profiles.LastUsed = profile.Id;
                Profiles.Save();
            }
            return window;
        }

        // Another copy of IGYO was started while this one is open (by a link another program opened, or the shortcut
        // clicked again) and handed over what it was started with (see OneCopy). Its links open as new tabs at the end
        // of the window used last, which comes to the front; without links, a window opens, as when IGYO starts.
        // False when IGYO is closing: the other copy then waits for it to end and starts by itself.
        public bool TakeOver(OneCopy.Request request)
        {
            var asked = Profiles.Find(request.Profile);
            if (windows.Count == 0 && picker != null)
            {
                // Still asking which profile to open. Started again without anything: the question comes forward.
                // A link, or a profile named, settles it: that window opens and the question goes.
                var handed = request.Links ?? new List<string>();
                if (handed.Count == 0 && asked == null && !request.Restore) { picker.ComeForward(); return true; }
                var settled = picker;
                picker = null;
                var window = OpenWindow(asked ?? Profiles.Find(Profiles.LastUsed) ?? Profiles.Default, false, handed.ToArray(), true);
                settled.Dismiss();
                window.ComeForward();
                return true;
            }
            if (windows.Count == 0) return false;
            if (request.Restore)
            {
                // A copy meant to take the place of one that restarted itself found this one open instead: the
                // windows of its profiles come back here (with their tabs, if they weren't open).
                RestoreOnce = true;
                try
                {
                    foreach (var id in new[] { request.Profile }.Concat(request.AlsoProfiles ?? new List<string>()))
                    {
                        var p = Profiles.Find(id);
                        if (p != null) OpenOrFocus(p);
                    }
                }
                finally { RestoreOnce = false; }
                return true;
            }
            var links = request.Links ?? new List<string>();
            var target = links.Count == 0 ? null :
                windows.Where(w => !w.IsPrivate && (asked == null || w.Profile.Id == asked.Id)).OrderByDescending(w => w.LastActive).FirstOrDefault();
            if (target == null) target = OpenWindow(asked ?? Profiles.Find(Profiles.LastUsed) ?? Profiles.Default, false, links.ToArray(), true);
            else target.OpenFromOutside(links);
            target.ComeForward();
            return true;
        }

        public void OpenOrFocus(Profile profile)
        {
            var existing = windows.FirstOrDefault(w => w.Profile.Id == profile.Id && !w.IsPrivate);
            if (existing == null)
            {
                OpenWindow(profile, false, null, true);
                return;
            }
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Activate();
        }

        public bool IsLastWindowOf(MainForm window) =>
            !window.IsPrivate && windows.Count(w => w.Profile.Id == window.Profile.Id && !w.IsPrivate) <= 1;

        public void ExitAll()
        {
            foreach (var w in windows.ToList()) w.Close();
        }

        // Closes IGYO and starts it again, for something that only holds from the engine's next start on. Every
        // window that is not private comes back with its tabs (also when tabs are not kept between starts); the
        // new copy waits until this one and its engine have ended before it asks for the engine.
        public void Restart(MainForm from)
        {
            try
            {
                var profiles = windows.Where(w => !w.IsPrivate).Select(w => w.Profile.Id).Distinct().ToList();
                string first = from != null && !from.IsPrivate ? from.Profile.Id : profiles.FirstOrDefault();
                var args = new List<string> { "--restore-tabs", "--wait-for=" + System.Diagnostics.Process.GetCurrentProcess().Id + "," + EngineWindows.EngineProcessId() };
                if (first != null) args.Add("--profile=" + first);
                var others = profiles.Where(p => p != first).ToList();
                if (others.Count > 0) args.Add("--also-profiles=" + string.Join(",", others));
                var again = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args.Select(BrowserEngine.Quoted))) { UseShellExecute = false });
#if DEBUG
                // Test builds: which process carries on, for the checks (they stop test copies by their process id).
                try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "debug-started-again.txt"), again.Id.ToString()); } catch { }
#endif
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                return;
            }
            OneCopy.Closing();   // a copy started by a link meanwhile waits for the new one (see OneCopy)
            ExitAll();
        }

        public BookmarkStore BookmarksFor(Profile profile)
        {
            if (!bookmarkStores.TryGetValue(profile.Id, out var store))
                bookmarkStores[profile.Id] = store = BookmarkStore.Load(Path.Combine(ProfileStore.DirFor(profile.Id), "bookmarks.json"));
            return store;
        }

        // A look changed: that profile's windows (all windows when null) take it again.
        public void ApplyTheme(string profileId = null)
        {
            if (profileId == null) looks.Clear(); else looks.Remove(profileId);
            looks.Remove("");   // (no profile: the picker's, worked out again too)
            ThemeStamp++;
            ThemeChanged?.Invoke(profileId);
        }

        // Shows a theme that isn't saved yet in the windows of one profile (the theme editor in one of them); null goes
        // back to the chosen theme.
        public void PreviewTheme(ThemeDef def, Profile profile)
        {
            string was = previewingFor;
            previewing = def;
            previewingFor = def == null ? null : profile?.Id;
            if (was != null && !string.Equals(was, previewingFor, StringComparison.OrdinalIgnoreCase)) ApplyTheme(was);
            ApplyTheme(profile?.Id ?? was);
        }

        // A profile's theme being tried out ends without its look being sent to its windows: what the caller does next
        // sends the look once. (Each look sent is a new stylesheet for every window's interface; two at once left an
        // earlier theme's sheet behind, 17 Nov 2026.) Another profile's preview is left as it is.
        public void EndPreviewQuietly(Profile profile)
        {
            if (!PreviewingFor(profile)) return;
            previewing = null;
            previewingFor = null;
        }

        // A theme being tried out never outlives the editor: once no Settings tab is open in that profile's windows,
        // its chosen theme comes back.
        public void EndPreviewIfEditorGone()
        {
            if (previewing == null) return;
            if (windows.Any(w => string.Equals(w.Profile.Id, previewingFor, StringComparison.OrdinalIgnoreCase) && w.Tabs.Any(t => SettingsPage.IsUrl(t.Url)))) return;
            PreviewTheme(null, Profiles.Find(previewingFor));
        }

        // A profile chooses a look of its own (Settings > Appearance in one of its windows): its windows take it.
        // apply false: only kept (the caller sends the look itself, once). Settings pages that are open show the choice.
        public void ChooseTheme(Profile profile, string theme = null, string accent = null, bool apply = true)
        {
            if (profile == null) return;
            if (theme != null) profile.Theme = theme;
            if (accent != null) profile.Accent = accent;
            Profiles.Save();
            if (apply) ApplyTheme(profile.Id);
            foreach (var window in windows) window.RefreshSettingsPages();
        }

        // Another language for IGYO's own menus and pages: they are loaded again, in the new language.
        public void SetLanguage(string chosen)
        {
            Settings.Language = L.IsChoice(chosen) ? chosen : L.Auto;
            L.Use(Settings.Language);
            SaveSettings();
            foreach (var window in windows.ToList()) window.ReloadOwnPages();
        }

        // The icon IGYO wears (Settings > Appearance > App icon): every window shows it at once, and IGYO's shortcuts
        // are given it (off this thread). One for the whole program, whatever the profile.
        public void ChooseAppIcon(string id)
        {
            Settings.AppIcon = AppIcons.IsChoice(id) && id != AppIcons.Default ? id : "";
            WindowIcon = AppIcons.WindowIcon(Settings.AppIcon);
            if (WindowIcon != null)
            {
                foreach (var window in windows) try { window.Icon = WindowIcon; } catch { }
                if (picker != null) try { picker.Icon = WindowIcon; } catch { }
            }
            SaveSettings();
            AppIcons.ApplyToShortcutsInTurn(Settings.AppIcon);
        }

        public void SaveSettings()
        {
            Anim.Enabled = Settings.Animations;
            Settings.Save();
            SettingsChanged?.Invoke();
        }

        void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // (Every profile on Automatic, or the picker's look, follows Windows: all windows look again.)
            if (e.Category == UserPreferenceCategory.General &&
                (ThemeStore.Normalize(Settings.Theme) == ThemeStore.Auto || Profiles.Profiles.Any(p => ThemeStore.Normalize(ThemeOf(p)) == ThemeStore.Auto)))
                ApplyTheme();
        }

        protected override void ExitThreadCore()
        {
            OneCopy.Closing();   // a copy started from now on waits for this one to end, and starts by itself
            // The last window is closed. A window the engine opened by itself would keep the engine's processes
            // running after this: it is closed too, so that nothing of IGYO stays behind.
            EngineWindows.CloseAll("end");
            try { Bridge?.Shutdown(); } catch { }
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            base.ExitThreadCore();
        }
    }
}
