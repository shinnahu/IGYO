using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // By default the engine tells every site it is "Microsoft Edge WebView2", an engine embedded in an app,
    // and Google refuses sign-in from those. This makes the browser present itself as Microsoft Edge (the
    // engine it runs on), with the engine's own version numbers, and hides the app-only window.chrome.webview.
    static class Identity
    {
        static string overrideJson;
        static Task<string> building;

        // Whether what to present is already at hand (from this run, or kept from an earlier one), so that applying
        // it needs nothing found out first.
        public static bool IsKnown(CoreWebView2 core)
        {
            if (overrideJson == null) overrideJson = Kept(core);
            return overrideJson != null;
        }

        public static async Task ApplyAsync(CoreWebView2 core, MainForm owner)
        {
            try
            {
                var hidden = core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "try { if (window.chrome && 'webview' in window.chrome) delete window.chrome.webview; } catch (e) {}");
                if (overrideJson == null) overrideJson = Kept(core);
                if (overrideJson == null)
                {
                    building = building ?? BuildWithHelperAsync(owner);
                    overrideJson = await building;
                    if (overrideJson != null) Keep(core, overrideJson);
                }
                var result = overrideJson == null ? "no override" : await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", overrideJson);
                bool cover = overrideJson != null;
#if DEBUG
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_NO_COVER"))) cover = false;   // checks: as before
#endif
                if (cover) await CoverWorkersAsync(core, overrideJson);
                await hidden;
                Log("override=" + overrideJson + "\nresult=" + result);
            }
            catch (Exception ex)
            {
                Log("error: " + ex);
            }
        }

        // Workers a page starts (above all a service worker, which runs a site such as Gmail in the background) and
        // frames from other sites, which the engine runs apart, are targets of their own: the page's override does not
        // reach them, and a service worker went on telling sites it is "Microsoft Edge WebView2" (seen 17 Nov 2026, a
        // test page's service worker listed that brand while the page did not). Each is held as it starts, given the
        // same identity, and let go, so that even its first requests carry it.
        static async Task CoverWorkersAsync(CoreWebView2 core, string identity)
        {
            core.GetDevToolsProtocolEventReceiver("Target.attachedToTarget").DevToolsProtocolEventReceived += async (s, e) =>
            {
                string session = null;
                try
                {
                    var p = Json.Obj(Json.Parse(e.ParameterObjectAsJson));
                    session = Json.Str(p, "sessionId");
                    var type = Json.Str(Json.Obj(Json.Get(p, "targetInfo")), "type") ?? "";
                    Log("attached " + type);
                    if (string.IsNullOrEmpty(session)) return;
                    if (type.EndsWith("worker", StringComparison.Ordinal))
                        await core.CallDevToolsProtocolMethodForSessionAsync(session, "Network.setUserAgentOverride", identity);
                    else if (type == "iframe")
                    {
                        await core.CallDevToolsProtocolMethodForSessionAsync(session, "Emulation.setUserAgentOverride", identity);
                        await core.CallDevToolsProtocolMethodForSessionAsync(session, "Target.setAutoAttach", AutoAttach);   // its own workers too
                    }
                    Log("covered " + type);
                }
                catch (Exception ex) { Log("cover: " + ex.Message); }
                finally
                {
                    // never left waiting, whatever happened above
                    if (!string.IsNullOrEmpty(session))
                        try { await core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.runIfWaitingForDebugger", "{}"); } catch { }
                }
            };
            await core.CallDevToolsProtocolMethodAsync("Target.setAutoAttach", AutoAttach);
        }

        const string AutoAttach = "{\"autoAttach\":true,\"waitForDebuggerOnStart\":false,\"flatten\":true}";

        // What the helper found only depends on the engine and on Windows, so it is kept between starts: building it
        // takes a hidden view and a page load, about 0.15 s that the first page of every start would wait for.
        // It is built again when the engine's or Windows' version is another one.
        static string KeptFile => System.IO.Path.Combine(AppPaths.DataDir, "identity.json");

        static string VersionsOf(CoreWebView2 core)
        {
            string windows = "";
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    windows = key?.GetValue("CurrentBuildNumber") + "." + key?.GetValue("UBR");
            }
            catch { }
            return core.Environment.BrowserVersionString + " on Windows " + windows;
        }

        static string Kept(CoreWebView2 core)
        {
            try
            {
                if (!System.IO.File.Exists(KeptFile)) return null;
                var kept = Json.Obj(Json.Parse(System.IO.File.ReadAllText(KeptFile)));
                var value = Json.Str(kept, "identity");
                return Json.Str(kept, "for") == VersionsOf(core) && !string.IsNullOrEmpty(value) ? value : null;
            }
            catch { return null; }
        }

        static void Keep(CoreWebView2 core, string value)
        {
            try { FileUtil.WriteAtomic(KeptFile, Json.Write(new Dictionary<string, object> { ["for"] = VersionsOf(core), ["identity"] = value })); }
            catch { }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        static void Log(string text)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "debug-identity.txt"), text + "\n"); } catch { }
        }

        // A page is needed to read the engine's own version details, so a hidden private view loads a blank
        // page once and is thrown away. Tabs keep a clean back history.
        static async Task<string> BuildWithHelperAsync(MainForm owner)
        {
            var helper = new Microsoft.Web.WebView2.WinForms.WebView2 { Visible = false, Size = new System.Drawing.Size(2, 2) };
            owner.ContentHost.Controls.Add(helper);
            try
            {
                _ = helper.Handle;
                var environment = await BrowserEngine.GetAsync();
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = owner.Profile.Id;
                options.IsInPrivateModeEnabled = true;   // leaves nothing behind
                await helper.EnsureCoreWebView2Async(environment, options);
                // The version details are only given to secure pages; a local file counts, about:blank doesn't.
                var blank = System.IO.Path.Combine(AppPaths.DataDir, "blank.html");
                System.IO.File.WriteAllText(blank, "<!doctype html><title></title>");
                var loaded = new TaskCompletionSource<bool>();
                helper.CoreWebView2.NavigationCompleted += (s, e) => loaded.TrySetResult(true);
                helper.CoreWebView2.Navigate(new Uri(blank).AbsoluteUri);
                await Task.WhenAny(loaded.Task, Task.Delay(5000));
                return await BuildAsync(helper.CoreWebView2);
            }
            catch (Exception ex)
            {
                Log("helper error: " + ex);
                building = null;   // try again with the next tab
                return null;
            }
            finally
            {
                owner.ContentHost.Controls.Remove(helper);
                helper.Dispose();
            }
        }

        static async Task<string> BuildAsync(CoreWebView2 core)
        {
            var request = Json.Write(new Dictionary<string, object>
            {
                ["expression"] = "navigator.userAgentData.getHighEntropyValues(['architecture','bitness','model','platformVersion','fullVersionList','wow64'])" +
                                 ".then(v => JSON.stringify({ ua: navigator.userAgent, v: v }))",
                ["awaitPromise"] = true,
                ["returnByValue"] = true,
            });
            var response = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request)));
            var text = Json.Str(Json.Obj(Json.Get(response, "result")), "value");
            if (string.IsNullOrEmpty(text)) return null;
            var data = Json.Obj(Json.Parse(text));
            var v = Json.Obj(Json.Get(data, "v"));
            object[] WithoutWebView(string key) => Json.Arr(Json.Get(v, key)).Select(Json.Obj)
                .Where(b => b != null && (Json.Str(b, "brand") ?? "").IndexOf("WebView", StringComparison.OrdinalIgnoreCase) < 0)
                .Select(b => (object)new Dictionary<string, object> { ["brand"] = Json.Str(b, "brand"), ["version"] = Json.Str(b, "version") })
                .ToArray();
            var metadata = new Dictionary<string, object>
            {
                ["brands"] = WithoutWebView("brands"),
                ["fullVersionList"] = WithoutWebView("fullVersionList"),
                ["platform"] = Json.Str(v, "platform") ?? "Windows",
                ["platformVersion"] = Json.Str(v, "platformVersion") ?? "",
                ["architecture"] = Json.Str(v, "architecture") ?? "x86",
                ["model"] = Json.Str(v, "model") ?? "",
                ["mobile"] = false,
                ["bitness"] = Json.Str(v, "bitness") ?? "64",
                ["wow64"] = false,
            };
            return Json.Write(new Dictionary<string, object> { ["userAgent"] = Json.Str(data, "ua"), ["userAgentMetadata"] = metadata });
        }
    }
}
