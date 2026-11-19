using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace IGYO
{
    // One engine instance (browser process) shared by every tab.
    static class BrowserEngine
    {
        static Task<CoreWebView2Environment> environment;

        public static Task<CoreWebView2Environment> GetAsync() => environment ?? (environment = CreateAsync());

        public static CoreWebView2Environment Current =>
            environment != null && environment.Status == TaskStatus.RanToCompletion ? environment.Result : null;

        // Whether this engine was started with its extension support: only when the extensions folder held something
        // at that moment (see Extensions.cs). It cannot be changed while the engine runs.
        public static bool ExtensionsOn { get; private set; }

        // Said on the command line by a copy of IGYO that had to start itself again (see StartAgainTheOtherWay):
        // "on" or "off" instead of what the extensions folder says.
        public static bool? ExtensionsGiven { get; set; }

        // The one extension that may record tabs without a click on its button, as its id in the engine; "" when
        // there is none. In a browser, a click on an extension's button lets it record that tab; the engine has no
        // such button in an app like this one, and refuses every recording. It has one way around that: an id
        // named when it starts ("--allowlisted-extension-id") is let through, for every tab and at any time, and
        // is also let past the engine's other lists of extensions that may use a feature. So it is only named when
        // the person chose the extension for it (Settings > Extensions), and like the extension support itself it
        // cannot be changed while the engine runs.
        public static string Recording { get; private set; } = "";
        public static string RecordingGiven { get; set; }   // from "--engine-recording=" of a copy that started itself again ("none": none)

        // Whether the engine draws a window that other windows cover. It normally stops (so a covered window costs
        // nothing), but then nothing in it can be pictured: while some profile lets Claude act, it keeps drawing, so
        // Claude can picture the window it works in while the person uses another one. Like the extension support,
        // it is decided as the engine starts.
        public static bool DrawCovered { get; private set; }
        public static bool? DrawCoveredGiven { get; set; }   // from "--engine-covered=" of a copy that started itself again

        static string WantedFile => System.IO.Path.Combine(AppPaths.DataDir, "engine-recording.txt");   // what the next start asks for
        static string StartedFile => System.IO.Path.Combine(AppPaths.DataDir, "engine-started.txt");    // how the running engine was asked for

        // Read before the settings are (the engine is asked for first of all), so it has a small file of its own.
        public static string WantedRecording()
        {
            try
            {
                if (!System.IO.File.Exists(WantedFile)) return "";
                string id = System.IO.File.ReadAllText(WantedFile).Trim();
                return ExtensionStore.IsId(id) ? id : "";
            }
            catch { return ""; }
        }

        public static void SetWantedRecording(string id)
        {
            try
            {
                if (ExtensionStore.IsId(id)) { if (WantedRecording() != id) System.IO.File.WriteAllText(WantedFile, id); }
                else if (System.IO.File.Exists(WantedFile)) System.IO.File.Delete(WantedFile);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        // The engine gave this copy a view, so it runs the way this copy asked for it: written down for a copy
        // that starts later and has to ask the same way (StartAgainTheOtherWay).
        static bool confirmed;
        public static void Confirmed()
        {
            if (confirmed) return;
            confirmed = true;
            try { System.IO.File.WriteAllText(StartedFile, (ExtensionsOn ? "on" : "off") + "|" + Recording + "|" + (DrawCovered ? "covered" : "")); } catch { }
            // A window the engine opened by itself in an earlier run, which has kept the engine running since
            // (this copy then joined that engine instead of starting one): closed now.
            EngineWindows.CloseAll("start");
        }

        const int NotInTheRightState = unchecked((int)0x8007139F);
        static bool startingAgain;

        // The engine is one program for every copy of IGYO that is open on the same data. A copy that starts while
        // another one is open has to ask for the engine the way the open one did, or the engine refuses it a view
        // (with this error). That happens when the extensions folder got its first extension, or lost its last one,
        // while IGYO was open, or the extension that may record tabs was changed, and IGYO is then started once
        // more, by a link from another program for instance. This copy then starts itself again, asking the way
        // the open one wrote down (or, when it wrote nothing, the other way about extensions), and ends. Returns
        // false when that was already tried, or the error is another one.
        public static bool StartAgainTheOtherWay(Exception error)
        {
            if (!(error is System.Runtime.InteropServices.COMException) || error.HResult != NotInTheRightState) return false;
            if (ExtensionsGiven.HasValue) return false;   // already the second try: the error is shown as it is
            if (startingAgain) return true;
            startingAgain = true;
            try
            {
                string extensions = ExtensionsOn ? "off" : "on", recording = Recording.Length > 0 ? Recording : "none", covered = DrawCovered ? "on" : "off";
                try
                {
                    var running = System.IO.File.Exists(StartedFile) ? System.IO.File.ReadAllText(StartedFile).Trim().Split('|') : new string[0];
                    if (running.Length >= 2 && (running[0] == "on" || running[0] == "off") && (running[1].Length == 0 || ExtensionStore.IsId(running[1])))
                    {
                        bool coveredThere = running.Length >= 3 && running[2] == "covered";
                        if (running[0] != (ExtensionsOn ? "on" : "off") || running[1] != Recording || coveredThere != DrawCovered)
                        {
                            extensions = running[0];
                            recording = running[1].Length > 0 ? running[1] : "none";
                            covered = coveredThere ? "on" : "off";
                        }
                    }
                }
                catch { }
                // The same links in the same form (see CommandLine). The new copy waits for this one to end, so that it
                // finds IGYO's place free when it starts (see OneCopy).
                var switches = CommandLine.Switches.Where(a => !a.StartsWith("--engine-extensions=") && !a.StartsWith("--engine-recording=") && !a.StartsWith("--engine-covered=") && !a.StartsWith("--wait-for="))
                    .Concat(new[] { "--engine-extensions=" + extensions, "--engine-recording=" + recording, "--engine-covered=" + covered, "--wait-for=" + System.Diagnostics.Process.GetCurrentProcess().Id });
                var again = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, CommandLine.Again(switches)) { UseShellExecute = false });
#if DEBUG
                // Test builds: which process carries on, for the checks (they stop test copies by their process id).
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-started-again.txt"), again.Id.ToString()); } catch { }
#endif
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                return false;
            }
            Environment.Exit(0);
            return true;
        }

        // One word of a command line, written so that Windows hands it to the program exactly as it is (a backslash
        // only needs doubling where a quotation mark follows it).
        public static string Quoted(string word)
        {
            var text = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in word ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') text.Append('\\', slashes * 2 + 1);
                else text.Append('\\', slashes);
                text.Append(c);
                slashes = 0;
            }
            return text.Append('\\', slashes * 2).Append('"').ToString();
        }

        // What the engine is started with, besides its options ("" when nothing).
        // The engine's reports to Windows Task Manager stay on: with them, Task Manager names the engine's processes by
        // what they do and by their pages ("WebView2 Manager", "WebView2: <page>"). Seen 19 Nov 2026 on Windows 11
        // 23H2: whatever IGYO did, Task Manager showed the engine as a group of its own, apart from IGYO. With the
        // reports off (msWindowsTaskManager) it was the same group, called "Utility", its processes named only by kind;
        // with an empty view kept open so that the engine names IGYO as their owner again after every closed view
        // (the engine takes that back each time any view closes), it was still "WebView2 Manager". Task Manager knows
        // the engine by its "--embedded-browser-webview=1" switch, which the WebView2 loader always adds.
        internal static string Arguments(string recording, bool drawCovered)
        {
            var all = new List<string>();
            if (!string.IsNullOrEmpty(recording)) all.Add("--allowlisted-extension-id=" + recording);
            if (drawCovered) all.Add("--disable-features=CalculateNativeWinOcclusion");
            return string.Join(" ", all);
        }

        static Task<CoreWebView2Environment> CreateAsync()
        {
            ExtensionFolder.RemovePending();   // what the engine would not let go of last time (rarely anything)
            ExtensionsOn = ExtensionsGiven ?? ExtensionFolder.Any();
            Recording = RecordingGiven ?? (ExtensionsOn ? WantedRecording() : "");
            if (!ExtensionsOn || !ExtensionStore.IsId(Recording)) Recording = "";
            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = ExtensionsOn,
                EnableTrackingPrevention = true,
                ScrollBarStyle = CoreWebView2ScrollbarStyle.FluentOverlay,   // thin modern scrollbars
            };
            DrawCovered = DrawCoveredGiven ?? ProfileStore.AnyLetsClaudeAct();
            var extra = Arguments(Recording, DrawCovered);
            if (extra.Length > 0) options.AdditionalBrowserArguments = extra;
            StartupTrace.Mark("engine: asked for");
            var task = CoreWebView2Environment.CreateAsync(AppPaths.RuntimeFolder(), AppPaths.EngineDir, options);
            if (StartupTrace.On) task.ContinueWith(_ => StartupTrace.Mark("engine: ready"));
            return task;
        }
    }

    // Starting up, the slowest single thing is the engine handing out its first views: about a third of a second,
    // most of it the engine starting its own processes. The rest of the program needs about as long to read its
    // settings and build its window. So the two views every start needs (the first tab's, and the interface's) are
    // asked for at the very beginning, before the window exists, and the engine works while the program gets
    // ready. The window then takes them over instead of asking for new ones.
    static class EarlyViews
    {
        public sealed class Early
        {
            public WebView View;
            public Task Ready;   // done when the engine has attached to the view
        }

        public const string InterfaceProfile = "IGYOUi";

        static Early tab, ui;
        static string tabProfile;

        // The interface's view first: it needs nothing looked up, and the first request is what starts the engine.
        public static void Start(Func<string> startProfile)
        {
            try
            {
                ui = Ask(new WebView { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent }, InterfaceProfile, "interface");
                tabProfile = startProfile();
                tab = Ask(new WebView { Dock = DockStyle.Fill, Visible = false }, tabProfile, "tab");
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        static Early Ask(WebView view, string profile, string what)
        {
            _ = view.Handle;   // a window of its own for now (WinForms keeps it on its hidden parking window)
            StartupTrace.Mark(what + ": view asked for early");
            return new Early { View = view, Ready = AttachAsync(view, profile) };
        }

        static async Task AttachAsync(WebView view, string profile)
        {
            var environment = await BrowserEngine.GetAsync();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = profile;
            try
            {
                await view.EnsureCoreWebView2Async(environment, options);
                BrowserEngine.Confirmed();
            }
            catch (Exception ex) when (BrowserEngine.StartAgainTheOtherWay(ex))
            {
                // (not reached: this copy has ended, and the one it started carries on)
            }
        }

        // The first tab of the profile it was asked for takes the early view; anything else gets none.
        public static Early TakeTab(string profileId, bool isPrivate)
        {
            var early = tab;
            if (early == null || isPrivate || profileId != tabProfile || early.Ready.IsFaulted || early.Ready.IsCanceled) return null;
            tab = null;
            return early;
        }

        public static Early TakeInterface()
        {
            var early = ui;
            ui = null;
            return early == null || early.Ready.IsFaulted || early.Ready.IsCanceled ? null : early;
        }

        // Whatever was not taken (a private window first, another profile, a failure) is let go.
        public static void DropUnused()
        {
            foreach (var early in new[] { tab, ui })
                if (early != null) try { early.View.Dispose(); } catch { }
            tab = ui = null;
        }
    }
}
