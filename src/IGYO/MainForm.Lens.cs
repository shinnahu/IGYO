using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // "Search image with Google Lens" in a picture's right-click menu, as in Chrome: the picture goes to Google Lens,
    // and Lens's page opens in a new tab beside the one on show. What goes is the picture as the page already has it,
    // taken from the engine (not downloaded again, so a picture behind a sign-in goes too), made smaller first when it
    // is large, sent the way Chrome sends it (a form to Lens's upload address). When the picture's own data can't be
    // had, Lens is given the picture's address to fetch it itself. Nothing is sent before the line is chosen.
    sealed partial class MainForm
    {
#if DEBUG
        // Test builds: NSB_LENS_URL puts a stand-in on the PC in Lens's place (checks never reach Google).
        static string LensSite => Environment.GetEnvironmentVariable("NSB_LENS_URL") is string s && s.Length > 0 ? s.TrimEnd('/') : "https://lens.google.com";
#else
        const string LensSite = "https://lens.google.com";
#endif
        const int LensSide = 1000;              // a larger picture goes at most this many pixels on its long side
        const int LensMaxBytes = 20 << 20;      // and one Windows can't make smaller goes only up to this size

        // Below the engine's own lines about the picture (Chrome has it there too).
        void AddLens(BrowserTab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            try
            {
                var target = e.ContextMenuTarget;
                if (target.Kind != CoreWebView2ContextMenuTargetKind.Image || InternalPages.IsInternal(tab.Url)) return;
                string address = "";
                try { address = target.SourceUri ?? ""; } catch { }   // (the address decides: see AddOpenImage)
                if (!OpensAsPicture(address)) return;
                // The frame the picture is in, by its address (the engine's own "main frame or not" answer was false for
                // pictures in the page itself, 18 Nov 2026).
                string frame = "";
                try { frame = target.FrameUri ?? ""; } catch { }
                var item = core.Environment.CreateContextMenuItem(L.T("Search image with Google Lens"), null, CoreWebView2ContextMenuItemKind.Command);
                Action search = () => { if (!IsDisposed) _ = SearchWithLensAsync(tab, address, frame); };
                item.CustomItemSelected += (s, a) => BeginInvoke(search);
#if DEBUG
                // Test builds, when asked: the line is taken as pressed (a check could not press a line of the engine's menu).
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_LENS"))) BeginInvoke(search);
#endif
                int index = -1;
                for (int i = 0; i < e.MenuItems.Count; i++)
                    if ((e.MenuItems[i].Name ?? "").IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0 || e.MenuItems[i].Label == L.T("Open image in new tab")) index = i;
                e.MenuItems.Insert(index + 1, item);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        async Task SearchWithLensAsync(BrowserTab tab, string address, string frameUri)
        {
            byte[] picture = null;
            try { picture = await PictureOfAsync(tab, address, frameUri); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            if (IsDisposed) return;
            string query = "ep=ccm&re=df&st=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "&hl=" + Uri.EscapeDataString(L.Id);
            if (picture != null && picture.Length > 0)
            {
                string type = Pictures.TypeOf(picture);
                var smaller = Pictures.ForSearch(picture, LensSide, out int width, out int height, out bool asIs);
                if (smaller == null && !asIs && picture.Length > LensMaxBytes) picture = null;   // too large to send as it is
                else
                {
                    byte[] sent = smaller ?? picture;
                    string sentType = smaller != null ? "image/jpeg" : type.Length > 0 ? type : "application/octet-stream";
                    double k = smaller != null && width > 0 ? Math.Min(1.0, (double)LensSide / Math.Max(width, height)) : 1;
                    var fields = new List<(string, string)>();
                    if (width > 0)
                    {
                        fields.Add(("original_width", width.ToString(CultureInfo.InvariantCulture)));
                        fields.Add(("original_height", height.ToString(CultureInfo.InvariantCulture)));
                        fields.Add(("processed_image_dimensions", Math.Max(1, (int)Math.Round(width * k)).ToString(CultureInfo.InvariantCulture) + "," + Math.Max(1, (int)Math.Round(height * k)).ToString(CultureInfo.InvariantCulture)));
                    }
                    string boundary = "----IGYOLens" + Guid.NewGuid().ToString("N");
                    var body = Form(boundary, "encoded_image", "image" + Extension(sentType), sentType, sent, fields);
                    OpenPosting(LensSite + "/v3/upload?" + query, body, "multipart/form-data; boundary=" + boundary);
                    return;
                }
            }
            // The picture's own data could not be had: Lens fetches it from its address, which only works for one on the web.
            if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                NewTab(LensSite + "/uploadbyurl?url=" + Uri.EscapeDataString(address) + "&" + query, true, false);
            else ShowNotice(L.T("This picture can't be sent to Google Lens."));
        }

        // A tab beside the one on show, on show itself (as Chrome opens Lens), that asks for its address with this form.
        void OpenPosting(string url, byte[] body, string contentType)
        {
            var tab = CreateTab(url);
            tab.PostWhenReady(body, contentType);
            int index = current == null || !tabs.Contains(current) ? tabs.Count : tabs.IndexOf(current) + 1;
            tabs.Insert(index, tab);
            Select(tab);
        }

        // The picture's bytes: written out in its address (data:), else the engine's own copy of it (what the page
        // shows, with nothing fetched again), else read by the page itself, else a picture file on this PC. Null when
        // none of these gives it.
        async Task<byte[]> PictureOfAsync(BrowserTab tab, string address, string frameUri)
        {
            if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = address.IndexOf(',');
                if (comma < 0) return null;
                string head = address.Substring(5, comma - 5), data = address.Substring(comma + 1);
                return head.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(Uri.UnescapeDataString(data))
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));   // (an SVG written out as text)
            }
            var core = tab.Core;
            if (core == null) return null;
            string frameId = await FrameIdAsync(core, frameUri);
            byte[] bytes = frameId == null ? null : await FromEngineAsync(core, frameId, address);
#if DEBUG
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_LENS_NO_ENGINE"))) bytes = null;   // checks: as if the engine had no copy
#endif
            if (bytes != null) return bytes;
            // Read by the page itself, in the frame the picture is in: one the page made (blob:), which only it can read,
            // or one the engine had no copy of (the same address fetched again by a script takes the picture's place
            // there). A picture from another site the page may not read: Lens is then given its address.
            bool web = address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (frameId != null && (web || address.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)))
            {
                bytes = await FromPageAsync(core, frameId, address);
                Trace((bytes == null ? "the page could not read " : "the page read ") + address);
                if (bytes != null) return bytes;
            }
            if (address.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var file = new Uri(address).LocalPath;
                if (File.Exists(file) && new FileInfo(file).Length <= LensMaxBytes) return File.ReadAllBytes(file);
            }
            return null;
        }

        // The engine's own copy of a picture the page has loaded (its debugging protocol hands it over from the page's
        // memory; nothing goes out to fetch it again).
        static async Task<byte[]> FromEngineAsync(CoreWebView2 core, string frameId, string address)
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                try
                {
                    var content = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getResourceContent",
                        Json.Write(new Dictionary<string, object> { ["frameId"] = frameId, ["url"] = address }))));
                    string text = Json.Str(content, "content");
                    Trace("engine copy of " + address + ": " + (string.IsNullOrEmpty(text) ? "empty" : text.Length + " characters"));
                    if (string.IsNullOrEmpty(text)) return null;
                    return Json.Get(content, "base64Encoded") is bool b64 && b64 ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text);
                }
                finally
                {
                    try { await core.CallDevToolsProtocolMethodAsync("Page.disable", "{}"); } catch { }
                }
            }
            catch (Exception ex)   // not in the page's memory (or the frame is gone): the next way is tried
            {
                Trace("no engine copy of " + address + ": " + ex.Message);
                return null;
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        static void Trace(string text)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-lens.txt"), DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text + "\r\n"); } catch { }
        }

        // The page fetches the picture (in a world of IGYO's own, so the page's scripts can't step in; with the page's
        // sign-in, as the picture itself was asked for).
        static async Task<byte[]> FromPageAsync(CoreWebView2 core, string frameId, string address)
        {
            try
            {
                string script = "(async () => { const r = await fetch(" + Json.Write(address) + ", { credentials: 'include' }); if (!r.ok) return ''; const b = await r.blob(); if (b.size > " +
                    LensMaxBytes.ToString(CultureInfo.InvariantCulture) + ") return ''; const a = new Uint8Array(await b.arrayBuffer()); let s = ''; " +
                    "for (let i = 0; i < a.length; i += 32768) s += String.fromCharCode.apply(null, a.subarray(i, i + 32768)); return btoa(s); })()";
                var world = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                    Json.Write(new Dictionary<string, object> { ["frameId"] = frameId, ["worldName"] = "IGYOLens" }))));
                var request = new Dictionary<string, object> { ["expression"] = script, ["awaitPromise"] = true, ["returnByValue"] = true };
                if (Json.Get(world, "executionContextId") != null) request["contextId"] = Json.Get(world, "executionContextId");
                var answer = Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", Json.Write(request))));
                var text = Json.Str(Json.Obj(Json.Get(answer, "result")), "value");
                return string.IsNullOrEmpty(text) ? null : Convert.FromBase64String(text);
            }
            catch { return null; }
        }

        // The engine's id of the frame at this address: the page itself when the page has that address, or when no frame
        // has it (null only when the engine can't say).
        static async Task<string> FrameIdAsync(CoreWebView2 core, string frameUri)
        {
            try
            {
                var tree = Json.Obj(Json.Get(Json.Obj(Json.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"))), "frameTree"));
                return FrameWith(tree, frameUri) ?? Json.Str(Json.Obj(Json.Get(tree, "frame")), "id");
            }
            catch { return null; }
        }

        // The id of the frame at this address in the engine's tree of frames, the page itself first.
        static string FrameWith(Dictionary<string, object> node, string frameUri)
        {
            if (node == null || string.IsNullOrEmpty(frameUri)) return null;
            var frame = Json.Obj(Json.Get(node, "frame"));
            if (string.Equals(Json.Str(frame, "url"), frameUri, StringComparison.Ordinal)) return Json.Str(frame, "id");
            foreach (var child in Json.Arr(Json.Get(node, "childFrames")))
            {
                var found = FrameWith(Json.Obj(child), frameUri);
                if (found != null) return found;
            }
            return null;
        }

        static string Extension(string type)
        {
            switch (type)
            {
                case "image/jpeg": return ".jpg";
                case "image/png": return ".png";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "image/avif": return ".avif";
                case "image/bmp": return ".bmp";
                case "image/svg+xml": return ".svg";
                case "image/x-icon": return ".ico";
                default: return "";
            }
        }

        // A form as a browser sends one with a file in it (multipart/form-data).
        static byte[] Form(string boundary, string fileField, string fileName, string fileType, byte[] file, IEnumerable<(string name, string value)> fields)
        {
            var output = new MemoryStream();
            void Write(string text) { var b = Encoding.UTF8.GetBytes(text); output.Write(b, 0, b.Length); }
            Write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + fileField + "\"; filename=\"" + fileName + "\"\r\nContent-Type: " + fileType + "\r\n\r\n");
            output.Write(file, 0, file.Length);
            Write("\r\n");
            foreach (var (name, value) in fields)
                Write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n");
            Write("--" + boundary + "--\r\n");
            return output.ToArray();
        }
    }
}
