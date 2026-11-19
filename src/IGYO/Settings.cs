using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IGYO
{
    // Settings shared by all profiles. Each profile's tabs and bookmarks live in its own folder.
    sealed class Settings
    {
        // Appearance
        public string Language { get; set; } = "auto";         // a language's id ("es", "ja"…), "en", or "auto": the one Windows is shown in
        public string Theme { get; set; } = "auto";            // a theme's id, or "auto": IGYO Light or IGYO Dark with Windows
        public string Accent { get; set; } = "";               // "" keeps the theme's own accent; otherwise a color like #7C3AED
        public string AppIcon { get; set; } = "";              // the icon IGYO wears (an id in Assets\AppIcons\icons.json); "" is the default one
        public bool Compact { get; set; }
        public bool Animations { get; set; } = true;
        public bool NewTabPage { get; set; } = true;
        public bool SearchSuggestions { get; set; } = true;   // only Google's predictions, never history or bookmarks
        public bool ShowBookmarksBar { get; set; } = true;

        // Toolbar buttons. The tools button opens the group that holds ad block, dark mode for websites, video
        // enhancement, the color picker, the volume, picture in picture and the screenshot. Any of those tools can
        // also be pinned: it then has a button of its own on the toolbar, in the order they are listed here.
        public bool ShowToolsButton { get; set; } = true;
        public List<string> ToolbarTools { get; set; }        // the pinned tools, left to right; null in settings saved before tools could be pinned
        public bool ShowAdBlockButton { get; set; } = true;   // the three buttons earlier versions had, as they saved them
        public bool ShowPipButton { get; set; } = true;       //   (kept in step with ToolbarTools, so going back to an
        public bool ShowScreenshotButton { get; set; } = true; //    earlier version finds its toolbar as it is now)
        public bool ShowDownloadsButton { get; set; } = true;

        // Tools
        public bool ForceDark { get; set; }                   // websites that have no dark look of their own are shown dark
        public bool EnhanceVideos { get; set; } = true;       // videos are sharpened as they play (switches at once)
        public string EnhanceLevel { get; set; } = "medium";  // how much: soft, medium or strong

        // Extensions (named as they stand in the extensions folder)
        public List<string> ExtensionsSeen { get; set; } = new List<string>();   // the ones whose button was put on the toolbar once: each starts there
        public string RecordingExtension { get; set; } = "";  // the one extension that may record tabs without a click on its button (see BrowserEngine)

        // Translating a selection
        public string TranslateTo { get; set; } = "";         // the language text is put into; "" is the interface's own
        public string TranslateSecond { get; set; } = "";     // where text that is already in that language goes; "" leaves it
        public List<string> TranslateRecent { get; set; } = new List<string>();   // languages picked in the translation panel, newest first

        // Search and startup
        public string SearchEngine { get; set; } = "Google";
        public string CustomSearchUrl { get; set; } = "";
        public bool RestoreTabs { get; set; } = true;
        public bool SwitchToNewTabs { get; set; }             // go to a tab at once when a link is opened in one with the middle button or with Ctrl; off: it opens behind the tab on show
        public bool FirstRunDone { get; set; }
        public string SetupColorApplied { get; set; } = "";  // the id of the color picked in setup that the first profile took (ProfileStore.TakeSetupColor)
        public string SetupColor { get; set; } = "";         // and that color: setup run again with the same one leaves the first profile as it is

        // Files
        public string DownloadFolder { get; set; } = "";
        public string ScreenshotFolder { get; set; } = "";

        // Privacy and blocking
        public bool AdBlock { get; set; } = true;
        public bool SavePasswords { get; set; } = true;
        public bool IdentifyAsEdge { get; set; } = true;

        // Memory saver
        public double SleepAfterMinutes { get; set; } = 5;
        public double UnloadAfterMinutes { get; set; } = 30;
        public bool SleepWhenMinimized { get; set; } = true;
        public bool ReleaseMemory { get; set; } = true;        // hand memory that isn't being used back to Windows early (MemoryCare)
        public double MinimizedSleepSeconds { get; set; } = 30;

        public static readonly Dictionary<string, string> SearchEngines = new Dictionary<string, string>
        {
            ["Google"] = "https://www.google.com/search?q=%s",
            ["DuckDuckGo"] = "https://duckduckgo.com/?q=%s",
            ["Bing"] = "https://www.bing.com/search?q=%s",
            ["Brave Search"] = "https://search.brave.com/search?q=%s",
            ["Ecosia"] = "https://www.ecosia.org/search?q=%s",
            ["Startpage"] = "https://www.startpage.com/do/search?q=%s",
            ["Custom"] = "",
        };

        // The search address with %s where the words go (a method, so it isn't written into settings.json).
        public string SearchTemplate()
        {
            string template = SearchEngine == "Custom" ? CustomSearchUrl : null;
            if (string.IsNullOrWhiteSpace(template) || !template.Contains("%s"))
                template = SearchEngines.TryGetValue(SearchEngine ?? "", out var t) && t.Length > 0 ? t : SearchEngines["Google"];
            return template;
        }

        public string SearchUrl(string query) => SearchTemplate().Replace("%s", Uri.EscapeDataString(query));

        public string EffectiveScreenshotFolder => string.IsNullOrWhiteSpace(ScreenshotFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), InstallInfo.AppName)
            : ScreenshotFolder;

        public static Settings Load()
        {
            Settings settings = null;
            try
            {
                if (File.Exists(AppPaths.SettingsFile)) settings = Json.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile));
            }
            catch { }
            settings = settings ?? new Settings();
            settings.BringUpToDate();
            return settings;
        }

        // Settings as an earlier version saved them (or brand new ones): the tools that had a toolbar button of
        // their own keep it, in the order they stood. Whatever was edited by hand is put right.
        public void BringUpToDate()
        {
            if (ToolbarTools == null)
            {
                ToolbarTools = new List<string>();
                if (ShowAdBlockButton) ToolbarTools.Add(Tools.AdBlock);
                if (ShowPipButton) ToolbarTools.Add(Tools.Pip);
                if (ShowScreenshotButton) ToolbarTools.Add(Tools.Shot);
                ToolbarTools.Add(Tools.Translate);   // new with the pins, and asked for on the toolbar: it starts there
            }
            var seen = new HashSet<string>();
            ToolbarTools.RemoveAll(id => !Tools.IsPinnable(id) || !seen.Add(id));
            ExtensionsSeen = ExtensionsSeen ?? new List<string>();
            seen.Clear();
            ExtensionsSeen.RemoveAll(key => !Tools.IsExtension(Tools.ForExtension(key ?? "")) || !seen.Add(key));
            if (!Tools.IsExtension(Tools.ForExtension(RecordingExtension ?? ""))) RecordingExtension = "";
            seen.Clear();
            if (!Tools.IsLevel(EnhanceLevel)) EnhanceLevel = "medium";
            AppIcon = AppIcon ?? "";
            if (!TranslateLang.IsKnown(TranslateTo)) TranslateTo = "";
            if (!TranslateLang.IsKnown(TranslateSecond)) TranslateSecond = "";
            TranslateRecent = TranslateRecent ?? new List<string>();
            seen.Clear();
            TranslateRecent.RemoveAll(id => !TranslateLang.IsKnown(id) || id.Length == 0 || !seen.Add(id));
            SyncToolButtons();
        }

        public bool IsPinned(string tool) => ToolbarTools.Contains(tool);

        // Pins a tool to the toolbar (at the end) or takes it off.
        public void Pin(string tool, bool on)
        {
            if (!Tools.IsPinnable(tool) || on == IsPinned(tool)) return;
            if (on) ToolbarTools.Add(tool); else ToolbarTools.Remove(tool);
            SyncToolButtons();
        }

        // Keeps the toolbar in step with the extensions folder: an extension that is new gets its button on the
        // toolbar (once: taken off, it stays off), and what belonged to one that is gone is dropped. Says whether
        // anything changed.
        public bool FollowExtensions(IEnumerable<string> keys)
        {
            var now = new HashSet<string>(keys.Where(k => Tools.IsExtension(Tools.ForExtension(k ?? ""))));
            bool changed = false;
            foreach (var key in now)
            {
                if (ExtensionsSeen.Contains(key)) continue;
                ExtensionsSeen.Add(key);
                if (!IsPinned(Tools.ForExtension(key))) ToolbarTools.Add(Tools.ForExtension(key));
                changed = true;
            }
            changed |= ExtensionsSeen.RemoveAll(key => !now.Contains(key)) > 0;
            changed |= ToolbarTools.RemoveAll(id => Tools.IsExtension(id) && !now.Contains(Tools.ExtensionKey(id))) > 0;
            if (RecordingExtension.Length > 0 && !now.Contains(RecordingExtension)) { RecordingExtension = ""; changed = true; }
            return changed;
        }

        // Moves a pinned tool to another place among the pinned ones.
        public void MoveTool(string tool, int index)
        {
            if (!ToolbarTools.Remove(tool)) return;
            ToolbarTools.Insert(Math.Max(0, Math.Min(ToolbarTools.Count, index)), tool);
        }

        void SyncToolButtons()
        {
            ShowAdBlockButton = IsPinned(Tools.AdBlock);
            ShowPipButton = IsPinned(Tools.Pip);
            ShowScreenshotButton = IsPinned(Tools.Shot);
        }

        public void Save()
        {
            try { FileUtil.WriteAtomic(AppPaths.SettingsFile, Json.Write(this)); }
            catch (Exception ex) { ErrorLog.WriteOnce("settings", ex); }   // (not shown, but no longer lost without a trace)
        }
    }
}
