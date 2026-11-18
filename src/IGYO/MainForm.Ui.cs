using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IGYO
{
    // One line of a menu the interface draws. The interface gets the words; the action stays here.
    sealed class MenuEntry
    {
        public string Label, Keys, Icon, Image;
        public string Ext;          // the line of an extension (its name in the extensions folder): a right click on it opens that extension's own menu
        public bool Checked, Disabled, Separator, Header;
        public Action Run;
        public List<MenuEntry> Children;
        public MenuSlider Slider;   // a line that is a slider: moving it sends its command with the new value, and the menu stays open
        public bool? Switch;        // a line that switches something on or off: it keeps its icon, shows a switch, and the menu stays open
        public MenuPin Pin;         // a pin at the end of the line: whether this tool also stands on the toolbar

        public static MenuEntry Line() => new MenuEntry { Separator = true };
        public static MenuEntry Note(string text) => new MenuEntry { Label = text, Disabled = true };
        public static MenuEntry Head(string text) => new MenuEntry { Label = text, Header = true };
    }

    sealed class MenuSlider
    {
        public string Command, Unit;
        public double Value, Normal;   // Normal: the value a double click goes back to
        public double[] Stops;         // the values the slider can stand at, from left to right (they need not be evenly apart)
    }

    sealed class MenuPin
    {
        public bool On;
        public Action Run;   // pins or unpins; the menu stays open
    }

    // What the interface asks the window to do, and what the window asks the interface to show.
    sealed partial class MainForm
    {
        readonly Dictionary<int, Action> menuActions = new Dictionary<int, Action>();
        readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> dialogs = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        int menuSeq, dialogSeq;

        void OnUiMessage(Dictionary<string, object> m)
        {
            switch (Json.Str(m, "t"))
            {
                case "ready":
                    // The interface (re)loaded: anything it was asked is gone, so nobody keeps waiting for an answer.
                    foreach (var waiting in dialogs.Values.ToList()) waiting.TrySetResult(null);
                    dialogs.Clear();
                    uiModal = false;
                    uiOverlays.Clear();
                    SendEverything();
                    ShowWaitingNotice();
                    uiReady.TrySetResult(true);
                    break;
                case "layout":
                    OnUiLayout(m);
                    break;
                case "cmd":
                    RunCommand(Json.Str(m, "name") ?? "", m);
                    break;
                case "menu":
                    OpenMenu(Json.Str(m, "name") ?? "", m);
                    break;
                case "menuItem":
                    if (menuActions.TryGetValue(Int(m, "id"), out var action)) action();
                    break;
                case "suggest":
                    addressFocused = true;
                    ScheduleSuggestions(Json.Str(m, "text"));
                    break;
                case "dialog":
                    if (dialogs.TryGetValue(Int(m, "id"), out var answer))
                    {
                        dialogs.Remove(Int(m, "id"));
                        answer.TrySetResult(m);
                    }
                    break;
                case "notice":
                    if (noticeActions.TryGetValue(Int(m, "id"), out var run))
                    {
                        noticeActions.Remove(Int(m, "id"));
                        run();
                    }
                    break;
                case "ask":
                    OnAskAnswer(Int(m, "id"), Json.Str(m, "choice"));
                    break;
            }
        }

        void RunCommand(string name, Dictionary<string, object> m)
        {
            int id = Int(m, "id");
            var tab = TabById(id);
            switch (name)
            {
                // tabs
                case "newTab": NewTab(HomeUrl, true, true); break;
                case "selectTab": if (tab != null) Select(tab); break;
                case "closeTab": if (tab != null) CloseTab(tab); break;
                case "moveTab": if (tab != null) MoveTab(tab, Int(m, "index")); break;
                // Dragged off the strip: it gets a window of its own now (MainForm.Tear). In the interface's pixels: x, y
                // the mouse; gx, gy the mouse from the tab's top left; sx, sy where the first tab of a strip starts.
                case "tearTab":
                    if (tab != null)
                        TearOff(tab, ui.PointToScreen(new System.Drawing.Point(Int(m, "x"), Int(m, "y"))),
                            new System.Drawing.Point(Int(m, "sx") + Int(m, "gx"), Int(m, "sy") + Int(m, "gy")), new System.Drawing.Point(Int(m, "gx"), Int(m, "gy")));
                    break;
                case "tearMove": TearMove(ui.PointToScreen(new System.Drawing.Point(Int(m, "x"), Int(m, "y")))); break;
                case "tearEnd":   // let go there (without x: where the mouse is); cancel: Esc
                    TearEnd(Json.Get(m, "x") != null ? ui.PointToScreen(new System.Drawing.Point(Int(m, "x"), Int(m, "y"))) : Cursor.Position, Flag(m, "cancel"));
                    break;
                case "dragWindow":   // the window's only tab is being dragged (x, y: the mouse in the interface; gx, gy: on the tab)
                    DragWindow(new System.Drawing.Point(Int(m, "x"), Int(m, "y")), new System.Drawing.Point(Int(m, "gx"), Int(m, "gy")));
                    break;
#if DEBUG
                case "devSplitDrag":   // checks: the DevTools splitter dragged up in steps (see DevToolsDrawer.RehearseSplit)
                    try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "debug-split.txt"), devtools.RehearseSplit(new[] { 20, 40, 60, 80, 120 })); } catch { }
                    break;
                case "devPointer":   // checks: where the mouse is (screen pixels) and whether its button is down; no x: Windows' own again
                    Script(Json.Get(m, "x") != null ? new System.Drawing.Point(Int(m, "x"), Int(m, "y")) : (System.Drawing.Point?)null, Flag(m, "down"));
                    break;
#endif
                case "tabCard": if (tab != null) SendTabCard(tab); break;       // the card over a tab the mouse rests on
                case "stripHover": current?.TakePreview(); break;            // the mouse came to the strip: a picture for the card
                case "duplicateTab":
                    if (tab != null && !string.IsNullOrEmpty(tab.Url))
                    {
                        var copy = CreateTab(tab.Url);
                        tabs.Insert(tabs.IndexOf(tab) + 1, copy);
                        Select(copy);
                    }
                    break;
                case "reloadTab":
                    if (tab?.Core != null) tab.Core.Reload();
                    else if (tab != null) Select(tab);
                    break;
                case "closeOtherTabs": if (tab != null) CloseTabs(tabs.Where(t => t != tab)); break;
                case "closeTabsToRight": if (tab != null) CloseTabs(tabs.Skip(tabs.IndexOf(tab) + 1)); break;
                case "reopenTab": ReopenClosedTab(); break;

                // toolbar and address bar
                case "back": current?.Core?.GoBack(); break;
                case "forward": current?.Core?.GoForward(); break;
                case "reload": ReloadOrStop(); break;
                case "go": NavigateFromAddress(Json.Str(m, "text"), Flag(m, "newTab")); break;
                case "addressFocus":
                    addressFocused = Flag(m, "on");
                    if (!addressFocused) { suggestTimer.Stop(); suggestCancel?.Cancel(); }
                    break;
                case "paste":
                {
                    string text = "";
                    try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
                    if (Flag(m, "go")) { if (text.Trim().Length > 0) NavigateFromAddress(text, false); }
                    else Post("paste", new Dictionary<string, object> { ["text"] = text });
                    break;
                }
                case "focusPage": FocusPage(); break;
                case "pip": TogglePictureInPicture(); break;
                case "shot": TakeScreenshot(); break;
                // tools
                case "dark": ToggleForceDark(); break;
                case "enhance": ToggleEnhanceVideos(); break;
                case "pickColor": PickColor(); break;
                case "translatePage": TranslatePage(); break;
                case "volume":
                    if (current != null) current.SetVolume(Math.Round(Convert.ToDouble(Json.Get(m, "value") ?? 100.0)) / 100);
                    break;
                case "pickedColor": OnColorPicked(Json.Str(m, "hex")); break;
                case "pinTool": SetPinned(Json.Str(m, "tool"), Flag(m, "on")); break;
                // extensions: a click on one's button (with the button's place), and a click elsewhere in the interface
                case "extensionClick": OnExtensionClick(m); break;
                case "popupDismiss": CloseExtensionPopup(); break;
                case "moveTool":
                    // Dragged along the toolbar: to the place it was let go at, among the pinned tools.
                    if (Settings.IsPinned(Json.Str(m, "tool") ?? ""))
                    {
                        Settings.MoveTool(Json.Str(m, "tool"), Int(m, "index"));
                        app.SaveSettings();   // every window's toolbar follows
                    }
                    break;
                // translating a selection (the panel's own buttons)
                case "translateAgain": TranslateAgain(Json.Str(m, "from"), Json.Str(m, "to")); break;
                case "translateCopy": CopyTranslation(); break;
                case "translateOpen": OpenTranslationSite(); break;
                case "translateClose": CloseTranslation(false); break;
                case "downloads": ToggleDownloads(); break;
                case "zoom": Zoom(Int(m, "dir"), Json.Str(m, "from") == "popup"); break;

                // bookmarks
                case "star": EditBookmarkForCurrentPage(); break;
                case "openBookmark": OpenBookmark(BookmarkById(id), Flag(m, "newTab"), Flag(m, "behind")); break;
                case "editBookmark": EditBookmark(BookmarkById(id), L.T("Edit bookmark")); break;
                case "deleteBookmark": DeleteBookmark(BookmarkById(id)); break;
                case "newBookmarkFolder": NewBookmarkFolder(BookmarkById(id)); break;
                case "moveBookmark":
                {
                    // Dragged on the bookmarks bar: to a place on the bar (folder 0), or into a folder.
                    var moved = BookmarkById(id);
                    var folder = Int(m, "folder") == 0 ? bookmarks.Bar : BookmarkById(Int(m, "folder"));
                    int to = Json.Get(m, "index") == null ? -1 : Int(m, "index");
                    if (moved != null && folder != null && folder.IsFolder)
                    {
                        var parent = bookmarks.ParentOf(moved);
                        // The interface says where it ends up; Move counts places before it is taken out.
                        if (parent == folder && to >= 0 && to > parent.Children.IndexOf(moved)) to++;
                        if (bookmarks.Move(moved, folder, to)) bookmarks.NotifyChanged();
                    }
                    MarkDirty(bookmarks: true);   // the bar is sent again as it really is, moved or not
                    break;
                }
                case "openAllBookmarks":
                {
                    var folder = BookmarkById(id);
                    if (folder != null && folder.IsFolder)
                        foreach (var link in folder.Children.Where(c => !c.IsFolder).ToList()) NewTab(link.Url, false, true);
                    break;
                }
                case "toggleBookmarksBar": ToggleBookmarksBar(); break;

                // window
                case "exitFullScreen": LeaveFullScreen(); break;   // the ✕ that comes down at the top in full screen
                case "win":
                    switch (Json.Str(m, "action"))
                    {
                        case "min": WindowState = FormWindowState.Minimized; break;
                        case "max": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                        case "close": Close(); break;
                    }
                    break;
                case "settings": OpenSettings(Json.Str(m, "section")); break;
                case "history": OpenHistory(); break;
#if DEBUG
                // Test builds only: a shortcut key as if it had been pressed (a check cannot press keys in a window that is never shown).
                case "devKey": if (Enum.TryParse(Json.Str(m, "keys"), out Keys devKeys)) HandleShortcut(devKeys); break;
                // Test builds only: lets an automated check switch themes without going through Settings.
                case "devTheme":
                    app.ChooseTheme(Profile, Json.Str(m, "theme") ?? ThemeStore.Auto);   // (this window's profile's)
                    break;
                // Test builds only: what "Translate selection" in the page's right-click menu does.
                case "devTranslate": TranslateSelectionForChecks(); break;
                // Test builds only: sets the page's zoom straight in the engine. A figure out of the engine's range is put
                // right by the engine, which then reports the change by itself, as it does for Ctrl and the wheel (which a
                // check cannot turn): that is how the checks reach MainForm.OnTabZoomChanged.
                case "devZoomFactor":
                    try { if (current?.View != null) current.View.ZoomFactor = Convert.ToDouble(Json.Get(m, "value") ?? 1.0); } catch { }
                    break;
                // Test builds only: IGYO restarts itself, as it does after a change that needs the engine started anew.
                case "devRestart": app.Restart(this); break;
                // Test builds only: Claude's sign in the page's corner (a window that is never shown cannot be clicked).
                // devActing: as if the program named had just acted here. devAsk: a question as the bridge puts one
                // (its answer is written to debug-sign-answers.txt). devClaudeNotice: a notice about Claude.
                // devSign: a press on a card's button (button "" is its ✕), or with hover: the pointer on the card.
                case "devActing": app.Bridge.ActingForCheck(this, Json.Str(m, "program") ?? "check.exe"); break;
                case "devAsk":
                {
                    var asked = AskAsync(Json.Str(m, "text") ?? "", Json.Str(m, "tip") ?? "",
                        Json.Arr(Json.Get(m, "buttons")).Select(b => Json.Obj(b)).Where(b => b != null).Select(b => (Json.Str(b, "id") ?? "", Json.Str(b, "label") ?? "")).ToArray(),
                        null, Math.Max(5, Int(m, "seconds")));
                    var label = Json.Str(m, "text") ?? "";
                    _ = asked.ContinueWith(t => { try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-sign-answers.txt"), label + " => " + (t.Result ?? "(none)") + "\r\n"); } catch { } });
                    break;
                }
                case "devClaudeNotice":
                    ShowClaudeNotice(Json.Str(m, "text") ?? "", Json.Str(m, "action"),
                        Json.Str(m, "action") == null ? (Action)null : () => { try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-sign-answers.txt"), "notice action\r\n"); } catch { } }, Int(m, "ms"));
                    break;
                case "devSign":
                    if (sign != null)
                    {
                        if (Flag(m, "hover")) sign.PointerForCheck(Json.Str(m, "card") ?? "", Json.Str(m, "button"));
                        else sign.PressForCheck(Json.Str(m, "card") ?? "", Json.Str(m, "button") ?? "");
                    }
                    break;
#endif
            }
        }

        // Deleting from the bar is one click, so it can be taken back.
        void DeleteBookmark(Bookmark b)
        {
            var parent = b == null ? null : bookmarks.ParentOf(b);
            if (parent == null) return;
            int index = parent.Children.IndexOf(b);
            parent.Children.RemoveAt(index);
            bookmarks.NotifyChanged();
            ShowNotice(b.IsFolder ? L.T("Deleted the folder \"{0}\".", Ui.Trim(b.Title, 40)) : L.T("Deleted \"{0}\".", Ui.Trim(b.Title, 40)), L.T("Undo"), () =>
            {
                if (bookmarks.ParentOf(parent) == null && !bookmarks.Roots.Contains(parent)) return;   // its folder is gone too
                parent.Children.Insert(Math.Min(index, parent.Children.Count), b);
                bookmarks.NotifyChanged();
            });
        }

        // A new folder: inside the folder that was right-clicked, beside the bookmark that was, or on the bar.
        async void NewBookmarkFolder(Bookmark near)
        {
            var parent = near == null ? bookmarks.Bar : near.IsFolder ? near : bookmarks.ParentOf(near) ?? bookmarks.Bar;
            var name = await PromptAsync(L.T("New folder"),
                parent == bookmarks.Bar ? L.T("Name for the new folder on the bookmarks bar:") : L.T("Name for the new folder in \"{0}\":", Ui.Trim(parent.Title, 40)), "", L.T("Create"));
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!bookmarks.Roots.Contains(parent) && bookmarks.ParentOf(parent) == null) parent = bookmarks.Bar;   // its folder was deleted meanwhile
            parent.Children.Add(Bookmark.Folder(name.Length > 80 ? name.Substring(0, 80) : name));
            bookmarks.NotifyChanged();
        }

        // ----- menus -----

        static MenuEntry Item(string text, string keys, Action action, bool enabled = true, bool isChecked = false, string icon = null) =>
            new MenuEntry { Label = text, Keys = keys, Run = action, Disabled = !enabled, Checked = isChecked, Icon = icon };

        static MenuEntry Sub(string text, IEnumerable<MenuEntry> children, string icon = null) =>
            new MenuEntry { Label = text, Children = children.ToList(), Icon = icon };

        async void OpenMenu(string name, Dictionary<string, object> request)
        {
            if (name == "profile")
            {
                await RefreshGoogleSessionAsync();   // so its Google line says what is true at this moment
                if (IsDisposed) return;
            }
            CloseExtensionPopup();
            if ((name == "tools" || name == "extensions" || name == "main") && ExtensionSet != null)
            {
                // What stands in the extensions folder right now. With the engine's extension support running, the
                // list is the one made when the profile's extensions were loaded; without it, the folder is only read.
                try
                {
                    var core = tabs.Select(t => t.Core).FirstOrDefault(c => c != null);
                    var looking = ExtensionSet.EnsureLoadedAsync(core?.Profile, !BrowserEngine.ExtensionsOn);
                    await Task.WhenAny(looking, Task.Delay(600));   // never keeps a menu waiting
                    if (!IsDisposed && looking.IsCompleted) ExtensionsLookedAt();   // one that is new in the folder gets its button
                }
                catch { }
                if (IsDisposed) return;
            }
            IEnumerable<MenuEntry> items;
            switch (name)
            {
                case "main": items = MainMenuItems(); break;
                case "profile": items = ProfileItems(); break;
                case "adblock": items = AdBlockItems(); break;
                case "tools": items = ToolsItems(); break;
                case "volume": items = VolumeItems(); break;
                case "extensions": items = ExtensionItems(); break;
                case "extension": items = ExtensionContextItems(Json.Str(request, "key")); break;
                case "claude": items = ClaudeItems(); break;
                default: return;
            }
            menuActions.Clear();
            Post("menu", new Dictionary<string, object>
            {
                ["name"] = name,
                ["anchor"] = Json.Get(request, "anchor"),
                ["items"] = items.Select(MenuJson).ToList(),
            });
        }

        Dictionary<string, object> MenuJson(MenuEntry e)
        {
            var d = new Dictionary<string, object>();
            if (e.Separator) { d["sep"] = true; return d; }
            d["label"] = e.Label ?? "";
            if (e.Header) { d["head"] = true; return d; }
            if (!string.IsNullOrEmpty(e.Keys)) d["keys"] = e.Keys;
            if (!string.IsNullOrEmpty(e.Icon)) d["icon"] = e.Icon;
            if (!string.IsNullOrEmpty(e.Image)) d["image"] = e.Image;
            if (!string.IsNullOrEmpty(e.Ext)) d["ext"] = e.Ext;
            if (e.Checked) d["checked"] = true;
            if (e.Disabled) d["disabled"] = true;
            if (e.Switch.HasValue) d["switch"] = e.Switch.Value;
            if (e.Pin != null)
            {
                int pin = ++menuSeq;
                menuActions[pin] = e.Pin.Run;
                d["pin"] = new Dictionary<string, object> { ["on"] = e.Pin.On, ["id"] = pin };
            }
            if (e.Slider != null)
            {
                d["slider"] = new Dictionary<string, object>
                {
                    ["cmd"] = e.Slider.Command, ["value"] = e.Slider.Value, ["stops"] = e.Slider.Stops,
                    ["normal"] = e.Slider.Normal, ["unit"] = e.Slider.Unit ?? "",
                };
                return d;
            }
            if (e.Children != null) d["children"] = e.Children.Select(MenuJson).ToList();
            else if (e.Run != null)
            {
                int id = ++menuSeq;
                menuActions[id] = e.Run;
                d["id"] = id;
            }
            return d;
        }

        IEnumerable<MenuEntry> MainMenuItems()
        {
            yield return Item(L.T("New tab"), "Ctrl+T", () => NewTab(HomeUrl, true, true), icon: "plus");
            yield return Item(L.T("New window"), "Ctrl+N", () => app.OpenWindow(Profile, false), icon: "window");
            yield return Item(L.T("New private window"), "Ctrl+Shift+N", () => app.OpenWindow(Profile, true), icon: "private");
            yield return Item(L.T("Reopen closed tab"), "Ctrl+Shift+T", ReopenClosedTab, closedTabs.Count > 0, icon: "undo");
            yield return MenuEntry.Line();
            yield return Sub(L.T("Bookmarks"), BookmarkMenuItems(), "star");
            yield return Item(L.T("History"), "Ctrl+H", OpenHistory, icon: "history");
            yield return Item(L.T("Downloads"), "Ctrl+J", ToggleDownloads, icon: "download");
            yield return MenuEntry.Line();
            yield return Item(L.T("Picture in picture"), null, TogglePictureInPicture, icon: "pip");
            yield return Item(L.T("Full-page screenshot"), "Ctrl+Shift+S", TakeScreenshot, icon: "camera");
            yield return Item(L.T("Print…"), "Ctrl+P", () => current?.Core?.ShowPrintUI(), icon: "print");
            int zoom = (int)Math.Round((current?.View?.ZoomFactor ?? 1) * 100);
            yield return Sub(L.T("Zoom ({0}%)", zoom), new[]
            {
                Item(L.T("Zoom in"), "Ctrl +", () => Zoom(1)),
                Item(L.T("Zoom out"), "Ctrl −", () => Zoom(-1)),
                Item(L.T("Reset to 100%"), "Ctrl+0", () => Zoom(0)),
            }, "zoom");
            yield return Item(L.T("Full screen"), "F11", ToggleWindowFullScreen, true, windowFullScreen, "fullscreen");
            yield return Item(L.T("Developer tools"), "F12", OpenDevTools, icon: "code");
            yield return MenuEntry.Line();
            yield return Sub(L.T("Tools"), ToolsItems(), "puzzle");
            yield return Sub(L.T("Memory saver"), MemoryItems(), "leaf");
            yield return Item(L.T("Themes"), null, () => OpenSettings("themes"), icon: "palette");
            yield return Item(L.T("Settings"), "Ctrl+,", () => OpenSettings(), icon: "settings");
            yield return MenuEntry.Line();
            yield return Item(L.T("About {0}", InstallInfo.AppName), null, () => OpenSettings("about"), icon: "info");
            yield return Item(L.T("Exit"), null, () => app.ExitAll(), icon: "exit");
        }

        IEnumerable<MenuEntry> BookmarkMenuItems()
        {
            bool canBookmark = current != null && !current.IsBlank && !SettingsPage.IsUrl(current.Url);
            yield return Item(IsCurrentBookmarked ? L.T("Edit bookmark…") : L.T("Bookmark this page"), "Ctrl+D", EditBookmarkForCurrentPage, canBookmark);
            yield return Item(L.T("Show bookmarks bar"), "Ctrl+Shift+B", ToggleBookmarksBar, true, Settings.ShowBookmarksBar);
            yield return Item(L.T("Manage bookmarks…"), "Ctrl+Shift+O", () => OpenSettings("bookmarks"));
            yield return MenuEntry.Line();
            yield return Item(L.T("Import from Google Chrome…"), null, ImportFromChrome);
            yield return Item(L.T("Import from file (HTML)…"), null, ImportFromFile);
            yield return Item(L.T("Export to file (HTML)…"), null, ExportToFile);
        }

        IEnumerable<MenuEntry> AdBlockItems()
        {
            var b = Blocker;
            yield return Item(L.T("Block ads"), null, () => SetAdBlock(!Settings.AdBlock), true, Settings.AdBlock);
            yield return Item(L.T("Update the block list now"), null, UpdateAdList);
            yield return MenuEntry.Line();
            yield return MenuEntry.Note(b == null ? L.T("Loading the block list…") : L.T("EasyList {0} · {1} ad domains", b.Version, b.RuleCount.ToString("N0")));
            if (current != null && Settings.AdBlock) yield return MenuEntry.Note(L.T("{0} blocked on this page", current.Blocked));
        }

        // The tools group (the button at the right of the toolbar, and "Tools" in the main menu): what acts on the
        // page on show, in one place. Every line keeps its icon and ends in a pin: pinned, the tool also has a
        // button of its own on the toolbar. The two that are switched on and off show a switch, and switching
        // leaves the menu open.
        IEnumerable<MenuEntry> ToolsItems()
        {
            var tab = current;
            bool website = tab?.Core != null && !tab.IsBlank && !InternalPages.IsInternal(tab.Url);
            MenuPin pin(string tool) => new MenuPin { On = Settings.IsPinned(tool), Run = () => SetPinned(tool, !Settings.IsPinned(tool)) };

            var adBlock = Sub(Settings.AdBlock ? L.T("Ad block ({0} blocked)", tab?.Blocked ?? 0) : L.T("Ad block (off)"), AdBlockItems(), "shield");
            adBlock.Pin = pin(Tools.AdBlock);
            yield return adBlock;
            yield return new MenuEntry { Label = L.T("Dark mode for websites"), Icon = "moon", Switch = Settings.ForceDark, Run = ToggleForceDark, Pin = pin(Tools.Dark) };
            yield return new MenuEntry { Label = L.T("Enhance videos"), Icon = "enhance", Switch = Settings.EnhanceVideos, Run = ToggleEnhanceVideos, Pin = pin(Tools.Enhance) };
            yield return new MenuEntry { Label = L.T("Pick a color from the page"), Icon = "dropper", Run = PickColor, Disabled = !website, Pin = pin(Tools.Picker) };
            yield return new MenuEntry { Label = PageIsTranslated ? L.T("Show the original page") : L.T("Translate this page"), Icon = "translate", Run = TranslatePage, Disabled = !website, Pin = pin(Tools.Translate) };
            yield return MenuEntry.Line();
            var volume = VolumeLine(tab, website);
            volume.Pin = pin(Tools.Volume);
            yield return volume;
            yield return MenuEntry.Line();
            yield return new MenuEntry { Label = L.T("Picture in picture"), Icon = "pip", Run = TogglePictureInPicture, Disabled = !website, Pin = pin(Tools.Pip) };
            yield return new MenuEntry { Label = L.T("Full-page screenshot"), Keys = "Ctrl+Shift+S", Icon = "camera", Run = TakeScreenshot, Disabled = !website, Pin = pin(Tools.Shot) };
            yield return MenuEntry.Line();
            foreach (var line in ExtensionLines()) yield return line;
        }

        // ----- extensions (see Extensions.cs, and MainForm.Extensions.cs for what their buttons do) -----

        ProfileExtensions ExtensionSet => IsPrivate ? null : ProfileExtensions.For(Profile.Id);

        // The extensions in the tools menu, under their own heading: one line each, with the extension's own picture
        // and its pin. A click opens its popup, a right click its own menu (switch, options, manage, remove).
        IEnumerable<MenuEntry> ExtensionLines()
        {
            yield return MenuEntry.Head(L.T("Extensions"));
            var set = ExtensionSet;
            if (set == null)
            {
                yield return MenuEntry.Note(L.T("Extensions are not used in private windows."));
                yield break;
            }
            foreach (var x in set.List)
            {
                var one = x;
                string name = Ui.Trim(one.Name, 44);
                string pin = Tools.ForExtension(one.Key);
                yield return new MenuEntry
                {
                    Label = one.Error != null ? L.T("{0} (not used)", name) : one.Handle != null && !one.Enabled ? L.T("{0} (off)", name) : name,
                    Image = ExtensionIconAddress(one), Icon = "plug", Ext = one.Key,
                    Run = () => AskWhereForPopup(one),
                    Pin = new MenuPin { On = Settings.IsPinned(pin), Run = () => SetPinned(pin, !Settings.IsPinned(pin)) },
                };
            }
            if (set.List.Count == 0) yield return MenuEntry.Note(L.T("None yet: put one into the folder"));
            else if (!BrowserEngine.ExtensionsOn) yield return MenuEntry.Note(L.T("IGYO loads them the next time it starts"));
            // On an extension's page in a web store: the way to get that extension into the folder.
            if (ExtensionStore.FromAddress(current?.Url) != null)
                yield return Item(L.T("Add the extension on this page…"), null, AddExtensionFromPage, icon: "plus");
            yield return Item(L.T("Manage extensions"), null, () => OpenSettings("extensions"), icon: "settings");
        }

        // What the extensions' own toolbar button opens: the same lines, and the folder they come from.
        IEnumerable<MenuEntry> ExtensionItems()
        {
            foreach (var line in ExtensionLines()) yield return line;
            if (ExtensionSet == null) yield break;
            yield return MenuEntry.Line();
            yield return Item(L.T("Open the extensions folder"), null, OpenExtensionsFolder, icon: "folder");
            if (BrowserEngine.ExtensionsOn) yield return Item(L.T("Look for new extensions"), null, () => LoadExtensions(true), icon: "reload");
        }

        // Makes sure this profile's extensions are known: loaded into the engine once after a start (again: when
        // asked to look for new ones), or only named when the engine runs without its extension support.
        async void LoadExtensions(bool again, bool quiet = false)
        {
            var set = ExtensionSet;
            var core = tabs.Select(t => t.Core).FirstOrDefault(c => c != null);
            if (set == null || core == null) return;
            try
            {
                await set.EnsureLoadedAsync(core.Profile, again);
                if (IsDisposed) return;
                ExtensionsLookedAt();   // their buttons, for the toolbar
                var fresh = set.List.Where(x => x.Fresh).ToList();
                foreach (var x in fresh) x.Fresh = false;
                if (fresh.Count > 0)
                    ShowNotice(L.T("Loaded: {0}. Pages that are already open have to be loaded again before it acts on them.", string.Join(", ", fresh.Select(x => Ui.Trim(x.Name, 40)))),
                        L.T("Reload this page"), () => current?.Core?.Reload(), 12000);
                else if (again && !quiet) ShowNotice(set.List.Count == 0 ? L.T("There is nothing in the extensions folder.") : L.T("Nothing new in the extensions folder."));
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        // "Add the extension on this page": asked first, then the store's package is fetched into the extensions
        // folder and loaded like anything else that is put there (see ExtensionStore).
        async void AddExtensionFromPage()
        {
            var tab = current;
            var page = ExtensionStore.FromAddress(tab?.Url);
            if (page == null || IsPrivate) return;
            await AddExtensionAsync(page, ExtensionStore.NameFor(tab.Title, page));
        }

        // The store's own "Add" button was pressed in a tab (see ExtensionStore.PageScript): the same thing, for the
        // extension the store's page named. The page is told "added", "cancelled" or "failed".
        public async Task<string> AddFromStoreButtonAsync(BrowserTab tab, string host, string id, string name)
        {
            if (IsDisposed || IsPrivate || tab == null || !tabs.Contains(tab)) return "failed";
            // Only a page that really is at a store's address is listened to, and it is the tab on show.
            if (!Uri.TryCreate(tab.Url ?? "", UriKind.Absolute, out var at) || at.Scheme != "https" || !string.Equals(at.Host, host, StringComparison.OrdinalIgnoreCase)) return "failed";
            var page = ExtensionStore.FromAddress(tab.Url);
            if (page == null || page.Id != id) page = ExtensionStore.ForHost(host, id);
            if (page == null) return "failed";
            if (tab != current) Select(tab);
            return await AddExtensionAsync(page, string.IsNullOrWhiteSpace(name) ? ExtensionStore.NameFor(tab.Title, page) : name);
        }

        async Task<string> AddExtensionAsync(ExtensionStore.StorePage page, string name)
        {
            bool yes = await ConfirmAsync(L.T("Add \"{0}\"?", name),
                L.T("IGYO downloads it from {0} and keeps it in the extensions folder. An extension can read and change the pages you open.", page.Store), L.T("Add"));
            if (!yes || IsDisposed) return "cancelled";
            ShowNotice(L.T("Downloading \"{0}\"…", name), null, null, 60000);
            try
            {
                var bytes = await ExtensionStore.DownloadAsync(page, BrowserEngine.Current?.BrowserVersionString);
                string inside = ExtensionStore.IdInside(bytes);
                if (inside != null && inside != page.Id) throw new InvalidDataException(L.T("The store sent another extension than the one on this page."));
                await Task.Run(() => ExtensionStore.Save(page, bytes));
            }
            catch (Exception ex)
            {
                if (!IsDisposed) ShowNotice(L.T("\"{0}\" could not be added: ", name) + (ex is TaskCanceledException ? L.T("the store did not answer in time.") : ex.Message), null, null, 12000);
                return "failed";
            }
            if (IsDisposed) return "added";
            if (BrowserEngine.ExtensionsOn) LoadExtensions(true);
            else ShowNotice(L.T("\"{0}\" is in the extensions folder now. IGYO uses extensions from its next start on: close it and start it again.", name), null, null, 20000);
            return "added";
        }

        async void SetExtension(ExtensionInfo x, bool on)
        {
            var set = ExtensionSet;
            if (set == null) return;
            if (!on && extensionPopup != null && extensionPopup.Extension == x) CloseExtensionPopup();
            bool done = await set.SetEnabledAsync(x, on, LiveEngineProfile());
            if (IsDisposed) return;
            ExtensionsLookedAt();
            RefreshSettingsPages();
            if (!done)
                ShowNotice(on ? L.T("The engine did not switch \"{0}\" on. Restarting IGYO usually helps.", Ui.Trim(x.Name, 44))
                    : L.T("The engine did not switch \"{0}\" off. Restarting IGYO usually helps.", Ui.Trim(x.Name, 44)), L.T("Restart now"), () => app.Restart(this), 12000);
        }

        // The engine's side of this window's profile, through a view that is open now (the tab on show if it has
        // one, else any other tab of the profile's windows).
        Microsoft.Web.WebView2.Core.CoreWebView2Profile LiveEngineProfile()
        {
            var core = current?.Core ?? tabs.Select(t => t.Core).FirstOrDefault(c => c != null)
                ?? app.Windows.Where(w => w.Profile.Id == Profile.Id && !w.IsPrivate).SelectMany(w => w.Tabs).Select(t => t.Core).FirstOrDefault(c => c != null);
            try { return core?.Profile; } catch { return null; }
        }

        // An extension was taken out in another profile's window: this profile looks at the folder again now, and
        // takes it out of the engine too (the files served every profile).
        public void ExtensionsChangedElsewhere()
        {
            if (IsDisposed || IsPrivate) return;
            LoadExtensions(true, true);
        }

        void OpenExtensionPage(ExtensionInfo x, string page)
        {
            if (string.IsNullOrEmpty(x.Id)) return;
            if (!x.Enabled) { ShowNotice(L.T("Switch the extension on first.")); return; }
            NewTab("chrome-extension://" + x.Id + "/" + page, true, false);
        }

        void OpenExtensionsFolder()
        {
            try
            {
                Directory.CreateDirectory(ExtensionFolder.Dir);
                Process.Start("explorer.exe", $"\"{ExtensionFolder.Dir}\"");
            }
            catch (Exception ex) { ShowNotice(ex.Message); }
        }

        static void ShowInExplorer(string path)
        {
            try { if (File.Exists(path) || Directory.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }

        static MenuEntry VolumeLine(BrowserTab tab, bool website) => new MenuEntry
        {
            Label = L.T("Volume"), Icon = "volume", Disabled = !website,
            Slider = new MenuSlider { Command = "volume", Value = Math.Round((tab?.Volume ?? 1) * 100), Stops = VolumeStops, Normal = 100, Unit = "%" },
        };

        // What the volume's own toolbar button opens: the slider, and the way back to the page's own volume.
        IEnumerable<MenuEntry> VolumeItems()
        {
            var tab = current;
            bool website = tab?.Core != null && !tab.IsBlank && !InternalPages.IsInternal(tab.Url);
            yield return VolumeLine(tab, website);
            yield return Item(L.T("Back to 100%"), null, () => current?.SetVolume(1), website && Math.Abs((tab?.Volume ?? 1) - 1) > 0.0005, icon: "undo");
        }

        // This tab's volume in percent. 100 (the page's own volume) is the middle of the slider: quieter in steps of
        // 5 to its left, louder in steps of 25 up to 600 to its right.
        static readonly double[] VolumeStops = Enumerable.Range(0, 21).Select(i => i * 5.0)
            .Concat(Enumerable.Range(1, (int)((BrowserTab.MaxVolume * 100 - 100) / 25)).Select(i => 100 + i * 25.0)).ToArray();

        // Pins a tool to the toolbar or takes it off. Every window's toolbar follows (OnSettingsChanged).
        void SetPinned(string tool, bool on)
        {
            if (!Tools.IsPinnable(tool ?? "")) return;
            if (Tools.IsExtension(tool) && ExtensionByKey(Tools.ExtensionKey(tool)) == null) return;
            Settings.Pin(tool, on);
            app.SaveSettings();
        }

        void ToggleForceDark()
        {
            Settings.ForceDark = !Settings.ForceDark;
            app.SaveSettings();   // every window then darkens or undarkens its tabs (OnSettingsChanged)
        }

        // Switches at once: every tab is told, and the videos playing right now change as they play.
        void ToggleEnhanceVideos()
        {
            Settings.EnhanceVideos = !Settings.EnhanceVideos;
            app.SaveSettings();
        }

        // A picture of the page is laid over the page in the interface, where a color is picked from it with a
        // magnifier. (The engine's own color picker does not open in an app like this one.)
        async void PickColor()
        {
            var tab = current;
            if (tab?.Core == null || tab.IsBlank) return;
            try
            {
                var data = await PageTools.CaptureViewAsync(tab.Core);
                if (tab != current || IsDisposed) return;
                ui.FocusView();   // Esc reaches the picker
                Post("pickColor", new Dictionary<string, object> { ["image"] = "data:image/png;base64," + data });
            }
            catch (Exception ex)
            {
                ShowNotice(L.T("The color picker couldn't start: ") + ex.Message);
            }
        }

        void OnColorPicked(string hex)
        {
            if (string.IsNullOrEmpty(hex) || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$")) return;
            hex = hex.ToUpperInvariant();
            int r = Convert.ToInt32(hex.Substring(1, 2), 16), g = Convert.ToInt32(hex.Substring(3, 2), 16), b = Convert.ToInt32(hex.Substring(5, 2), 16);
            CopyText(hex);
            ShowNotice(L.T("{0} copied. As RGB: {1}, {2}, {3}.", hex, r, g, b), L.T("Copy as rgb()"), () => CopyText($"rgb({r}, {g}, {b})"), 8000);
        }

        static void CopyText(string text)
        {
#if DEBUG
            // Test builds: checks run beside whoever is using the PC, and must leave their clipboard alone.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_NO_CLIPBOARD"))) return;
#endif
            try { Clipboard.SetText(text); } catch { }
        }

        IEnumerable<MenuEntry> MemoryItems()
        {
            var sleep = new List<MenuEntry>();
            foreach (var (label, minutes) in new[] { (L.T("Never"), 0.0), (L.T("1 minute"), 1.0), (L.T("5 minutes"), 5.0), (L.T("15 minutes"), 15.0), (L.T("1 hour"), 60.0) })
                sleep.Add(Item(label, null, () => { Settings.SleepAfterMinutes = minutes; app.SaveSettings(); }, true, Math.Abs(Settings.SleepAfterMinutes - minutes) < 0.001));
            var unload = new List<MenuEntry>();
            foreach (var (label, minutes) in new[] { (L.T("Never"), 0.0), (L.T("15 minutes"), 15.0), (L.T("30 minutes"), 30.0), (L.T("1 hour"), 60.0), (L.T("4 hours"), 240.0) })
                unload.Add(Item(label, null, () => { Settings.UnloadAfterMinutes = minutes; app.SaveSettings(); }, true, Math.Abs(Settings.UnloadAfterMinutes - minutes) < 0.001));
            yield return Sub(L.T("Put background tabs to sleep after"), sleep);
            yield return Sub(L.T("Unload sleeping tabs after"), unload);
            yield return Item(L.T("Sleep when the window is minimized"), null, () => { Settings.SleepWhenMinimized = !Settings.SleepWhenMinimized; app.SaveSettings(); }, true, Settings.SleepWhenMinimized);
            yield return Item(L.T("Give memory back to Windows"), null, () => { Settings.ReleaseMemory = !Settings.ReleaseMemory; app.SaveSettings(); }, true, Settings.ReleaseMemory);
            yield return MenuEntry.Line();
            yield return Item(L.T("Memory in use…"), null, () => OpenSettings("memory"));
        }

        IEnumerable<MenuEntry> ProfileItems()
        {
            yield return MenuEntry.Head(IsPrivate ? L.T("Private window · {0}", Profile.Name) : Profile.Name);
            foreach (var p in app.Profiles.Profiles.ToList())
            {
                var profile = p;
                var item = Item(p.Name, null, () => app.OpenOrFocus(profile), true, p.Id == Profile.Id && !IsPrivate);
                item.Image = "/avatar/" + Uri.EscapeDataString(p.Id) + "?v=" + DateTime.UtcNow.Ticks;
                yield return item;
            }
            yield return MenuEntry.Line();
            yield return Item(L.T("Add profile…"), null, AddProfile, icon: "plus");
            yield return Item(L.T("Manage profiles…"), null, () => OpenSettings("profiles"), icon: "people");
            yield return Item(L.T("New private window"), "Ctrl+Shift+N", () => app.OpenWindow(Profile, true), icon: "private");
            yield return MenuEntry.Line();
            // Signed in already (this profile's own cookies say so): the line opens the account instead of offering to sign in.
            yield return GoogleSignedIn
                ? Item(L.T("Google account"), L.T("Signed in"), () => NewTab(GoogleSession.AccountUrl, true, false), icon: "key")
                : Item(L.T("Sign in to Google…"), null, () => NewTab(GoogleSession.SignInUrl, true, false), icon: "key");
        }

        // ----- questions the interface asks for the window -----

        // Shows a dialog in the interface and waits for the answer. Null means it was closed without one.
        async Task<Dictionary<string, object>> AskAsync(Dictionary<string, object> spec)
        {
            await uiReady.Task;
            if (IsDisposed) return null;
            int id = ++dialogSeq;
            var waiting = new TaskCompletionSource<Dictionary<string, object>>();
            dialogs[id] = waiting;
            spec["id"] = id;
            ui.FocusView();
            Post("dialog", spec);
            return await waiting.Task;
        }

        public async Task<bool> ConfirmAsync(string title, string message, string ok = null, string cancel = null, bool danger = false)
        {
            var answer = await AskAsync(new Dictionary<string, object>
            {
                ["kind"] = "confirm", ["title"] = title, ["message"] = message ?? "", ["ok"] = ok ?? L.T("OK"), ["cancel"] = cancel ?? L.T("Cancel"), ["danger"] = danger,
            });
            return answer != null && Json.Str(answer, "action") == "ok";
        }

        public async Task<string> PromptAsync(string title, string message, string value, string ok = null)
        {
            var answer = await AskAsync(new Dictionary<string, object>
            {
                ["kind"] = "prompt", ["title"] = title, ["message"] = message ?? "", ["value"] = value ?? "", ["ok"] = ok ?? L.T("OK"),
            });
            return answer != null && Json.Str(answer, "action") == "ok" ? (Json.Str(answer, "value") ?? "").Trim() : null;
        }
    }
}
