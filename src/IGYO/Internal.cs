using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // What IGYO's own pages share: the browser UI (https://ui.igyo), the New Tab page (https://newtab.igyo)
    // and Settings (https://settings.igyo). None of these addresses exist on the internet: every request to
    // them is answered here from the app's own folder, and only for the app's own views.
    static class InternalAssets
    {
        public const string Common = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nCross-Origin-Resource-Policy: same-origin";

        public static string AssetsDir => Path.Combine(AppPaths.AppDir, "Assets");

        public static void Respond(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, byte[] body, string type, string headers = Common) =>
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(body), 200, "OK", "Content-Type: " + type + "\r\n" + headers);

        public static void RespondText(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string text, string type, string headers = Common) =>
            Respond(core, e, Encoding.UTF8.GetBytes(text), type, headers);

        public static void RespondJson(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, object value) =>
            RespondText(core, e, Json.Write(value), "application/json; charset=utf-8");

        public static void Refuse(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, int status = 404) =>
            e.Response = core.Environment.CreateWebResourceResponse(null, status, status == 404 ? "Not Found" : "Refused", Common);

        public static string TypeOf(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                case ".avif": return "image/avif";
                case ".woff2": return "font/woff2";
                case ".woff": return "font/woff";
                case ".ttf": return "font/ttf";
                default: return "application/octet-stream";
            }
        }

        // A file name made only of letters, digits, dots and dashes: nothing that could leave the folder.
        public static bool SafeName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= 80 && !name.Contains("..") && name[0] != '.' &&
            name.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_');

        // Serves Assets\<folder>\<name> or one folder deeper (Assets\<folder>\<sub>\<name>). False when there is no such file.
        public static bool TryServeFile(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string folder, string relative)
        {
            var parts = (relative ?? "").Split('/');
            if (parts.Length == 0 || parts.Length > 2 || !parts.All(SafeName)) return false;
            var file = Path.Combine(new[] { AssetsDir, folder }.Concat(parts).ToArray());
            if (!File.Exists(file)) return false;
            Respond(core, e, File.ReadAllBytes(file), TypeOf(file));
            return true;
        }

        // Paths every IGYO page has: /shared/* (the design system) and the current theme.
        public static bool TryServeCommon(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string path)
        {
            if (path == "/theme.css")
            {
                RespondText(core, e, ThemeStore.BuildCss(owner.ThemeDef, owner.AccentHex), "text/css; charset=utf-8");
                return true;
            }
            if (path == "/theme.js")
            {
                RespondText(core, e, ThemeStore.BuildJs(owner.ThemeDef), "text/javascript; charset=utf-8");
                return true;
            }
            if (path == "/lang.js")   // the interface's language: its words, and tr('…') to look them up
            {
                RespondText(core, e, L.Script(), "text/javascript; charset=utf-8");
                return true;
            }
            if (path.StartsWith("/shared/"))
            {
                if (!TryServeFile(core, e, "Shared", path.Substring("/shared/".Length))) Refuse(core, e);
                return true;
            }
            if (path.StartsWith("/app-icons/"))   // the app icons' pictures (Settings > Appearance > App icon)
            {
                var name = path.Substring("/app-icons/".Length);
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !TryServeFile(core, e, "AppIcons", name)) Refuse(core, e);
                return true;
            }
            if (path == "/logo.png")
            {
                var logo = Path.Combine(AssetsDir, "logo.png");
                if (File.Exists(logo)) Respond(core, e, File.ReadAllBytes(logo), "image/png");
                else Refuse(core, e);
                return true;
            }
            if (path.StartsWith("/icon/site/"))
            {
                var file = Favicons.FileForHost(Uri.UnescapeDataString(path.Substring("/icon/site/".Length)));
                if (file != null) Respond(core, e, File.ReadAllBytes(file), "image/png");
                else Refuse(core, e);
                return true;
            }
            if (path.StartsWith("/icon/ext/"))   // an extension's own picture, by the extension's name in the extensions folder
            {
                var file = owner.ExtensionIconFile(Uri.UnescapeDataString(path.Substring("/icon/ext/".Length)));
                string kind = file == null ? null : TypeOf(file);
                if (kind != null && kind.StartsWith("image/") && File.Exists(file)) Respond(core, e, File.ReadAllBytes(file), kind);
                else Refuse(core, e);
                return true;
            }
            return false;
        }

        // The words after the "?" of an address.
        public static Dictionary<string, string> Query(string uri)
        {
            var result = new Dictionary<string, string>();
            int q = uri.IndexOf('?');
            if (q < 0) return result;
            foreach (var part in uri.Substring(q + 1).Split('#')[0].Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                result[Decode(eq < 0 ? part : part.Substring(0, eq))] = eq < 0 ? "" : Decode(part.Substring(eq + 1));
            }
            return result;
        }

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        // What a page sent with a POST, as text (null when there was nothing or it was too large).
        public static string Body(CoreWebView2WebResourceRequestedEventArgs e, int maxBytes = 8 * 1024 * 1024)
        {
            try
            {
                var stream = e.Request.Content;
                if (stream == null) return null;
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[16384];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        memory.Write(buffer, 0, read);
                        if (memory.Length > maxBytes) return null;
                    }
                    return Encoding.UTF8.GetString(memory.ToArray());
                }
            }
            catch { return null; }
        }

        public static bool SameAddress(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(a.Split('#')[0].Split('?')[0], b.Split('#')[0].Split('?')[0], StringComparison.OrdinalIgnoreCase);

        static readonly Dictionary<string, (DateTime stamp, byte[] png)> avatars = new Dictionary<string, (DateTime, byte[])>();

        // A profile's round picture as a PNG (drawn once, redrawn when the picture or name changes).
        public static byte[] AvatarPng(Profile profile, bool isPrivate)
        {
            var key = (isPrivate ? "private|" : "") + profile.Id + "|" + profile.Name + "|" + profile.Color;
            DateTime stamp = DateTime.MinValue;
            try { var file = ProfileStore.PicturePath(profile.Id); if (File.Exists(file)) stamp = File.GetLastWriteTimeUtc(file); } catch { }
            if (avatars.TryGetValue(key, out var cached) && cached.stamp == stamp) return cached.png;
            using (var bitmap = Ui.Avatar(profile, 96, isPrivate))
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                var png = memory.ToArray();
                avatars[key] = (stamp, png);
                return png;
            }
        }
    }

    // Requests from a tab to one of IGYO's own pages.
    static class InternalPages
    {
        public static readonly string[] Filters = { NewTabPage.Filter, SettingsPage.Filter };

        public static bool IsInternal(string url) => NewTabPage.IsUrl(url) || SettingsPage.IsUrl(url);

        // Under the browser's earlier name its own pages were at https://newtab.tacit and https://settings.tacit:
        // a tab saved then opens the same page at its present address.
        public static string FromEarlierName(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            foreach (var (old, now) in new[] { ("https://newtab.tacit", NewTabPage.Origin), ("https://settings.tacit", SettingsPage.Origin) })
                if (url.Equals(old, StringComparison.OrdinalIgnoreCase) || url.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase))
                    return now + url.Substring(old.Length);
            return url;
        }

        public static bool TryServe(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string topLevelRequest) =>
            NewTabPage.TryServe(owner, core, e, topLevelRequest) || SettingsPage.TryServe(owner, core, e, topLevelRequest);
    }
}
