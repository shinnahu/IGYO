using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IGYO
{
    sealed class Suggestion
    {
        public string Text;    // what goes into the address bar when chosen
        public string Title;   // a site's page title (sites only)
        public bool IsSite;    // a website Google predicts ("navigational" suggestion)
    }

    // Google's predictions for what you're typing. Nothing comes from your history, bookmarks or most visited sites.
    // On unless turned off in Settings (Search and new tabs), and never used in private windows.
    static class GoogleSuggest
    {
        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            // No cookies: Google gets the words, not who you are.
            var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36");
            return client;
        }

        static string Template
        {
            get
            {
#if DEBUG
                var test = Environment.GetEnvironmentVariable("NSB_SUGGEST_URL");   // test builds: a local stand-in for Google
                if (!string.IsNullOrEmpty(test)) return test;
#endif
                return "https://www.google.com/complete/search?client=chrome&ie=UTF-8&oe=UTF-8&hl=" +
                    Uri.EscapeDataString(L.Chosen == L.Auto ? CultureInfo.CurrentUICulture.Name : L.Id) + "&q=%s";
            }
        }

        // Only plain words are sent: nothing that looks like an address with a scheme, a local or network path, or a long paste.
        public static bool ShouldAsk(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Length > 80) return false;
            var lower = text.ToLowerInvariant();
            if (lower.Contains("://") || lower.StartsWith("file:") || lower.StartsWith("about:") || lower.StartsWith("data:") ||
                lower.StartsWith("view-source:") || lower.StartsWith("javascript:") || lower.StartsWith("mailto:")) return false;
            if (lower.StartsWith("localhost") || lower.StartsWith("127.") || lower.StartsWith("192.168.") || lower.StartsWith("10.") ||
                lower.StartsWith("\\\\") || (lower.Length > 2 && lower[1] == ':' && (lower[2] == '\\' || lower[2] == '/'))) return false;
            return true;
        }

        public static async Task<List<Suggestion>> FetchAsync(string text, CancellationToken cancel)
        {
            var url = Template.Replace("%s", Uri.EscapeDataString(text.Trim()));
            using (var response = await http.GetAsync(url, cancel).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return new List<Suggestion>();
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Parse(body, text);
            }
        }

        // ["words", ["prediction", ...], ["title", ...], [], {"google:suggesttype": ["QUERY" | "NAVIGATION" | ...]}]
        public static List<Suggestion> Parse(string json, string typed)
        {
            var list = new List<Suggestion>();
            try
            {
                json = (json ?? "").Trim();
                if (json.StartsWith(")]}'")) json = json.Substring(json.IndexOf('\n') + 1);
                var root = Json.Arr(Json.Deserialize<object>(json));
                if (root.Length < 2) return list;
                var texts = Json.Arr(root[1]);
                var titles = root.Length > 2 ? Json.Arr(root[2]) : new object[0];
                var extra = root.Length > 4 ? Json.Obj(root[4]) : null;
                var types = extra != null && extra.TryGetValue("google:suggesttype", out var t) ? Json.Arr(t) : new object[0];
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { (typed ?? "").Trim() };
                for (int i = 0; i < texts.Length && list.Count < 7; i++)
                {
                    var s = ((texts[i] as string) ?? "").Trim();
                    var type = i < types.Length ? types[i] as string : "QUERY";
                    if (s.Length == 0 || s.Length > 300 || type == "CALCULATOR" || !seen.Add(s)) continue;
                    bool site = type == "NAVIGATION" && (s.StartsWith("http://") || s.StartsWith("https://"));
                    if (type == "NAVIGATION" && !site) continue;
                    list.Add(new Suggestion { Text = s, IsSite = site, Title = site && i < titles.Length ? (titles[i] as string)?.Trim() : null });
                }
            }
            catch { list.Clear(); }
            return list;
        }

        // How a site suggestion reads: "facebook.com" rather than "https://www.facebook.com/".
        public static string DisplayUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
            var host = u.Host.StartsWith("www.") ? u.Host.Substring(4) : u.Host;
            var rest = u.PathAndQuery == "/" ? "" : u.PathAndQuery;
            return host + rest;
        }
    }
}
