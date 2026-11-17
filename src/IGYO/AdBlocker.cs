using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IGYO
{
    // Blocks requests to ad servers listed in EasyList (domain rules such as "||ads.example.com^").
    sealed class AdBlocker
    {
        public const string ListUrl = "https://easylist.to/easylist/easylist.txt";

        static readonly Regex DomainRule = new Regex(@"^(@@)?\|\|([a-z0-9][a-z0-9.\-]*[a-z0-9])\^(?:\$(.+))?$", RegexOptions.Compiled);
        static readonly HashSet<string> TypeOptions = new HashSet<string>
        {
            "script", "image", "stylesheet", "subdocument", "xmlhttprequest", "media", "object", "other",
            "ping", "websocket", "font", "popup", "xhr", "css", "frame",
        };
        static readonly HashSet<string> SecondLevel = new HashSet<string> { "co", "com", "net", "org", "gov", "edu", "ac", "gob", "or", "ne", "go" };

        readonly HashSet<string> blocked = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> blockedThirdParty = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> allowed = new HashSet<string>(StringComparer.Ordinal);

        public string Version { get; private set; } = "";
        public int RuleCount => blocked.Count + blockedThirdParty.Count;

        public static AdBlocker Load()
        {
            var blocker = new AdBlocker();
            var file = File.Exists(AppPaths.UpdatedAdListFile) ? AppPaths.UpdatedAdListFile : AppPaths.BundledAdListFile;
            try { if (File.Exists(file)) blocker.Parse(File.ReadLines(file)); } catch { }
            StartupTrace.Mark("ad block list: loaded");
            return blocker;
        }

        public void Parse(IEnumerable<string> lines)
        {
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '!')
                {
                    if (line.StartsWith("! Version:", StringComparison.OrdinalIgnoreCase)) Version = line.Substring(10).Trim();
                    continue;
                }
                var m = DomainRule.Match(line.ToLowerInvariant());
                if (!m.Success) continue;
                var domain = m.Groups[2].Value;
                var options = m.Groups[3].Success ? m.Groups[3].Value.Split(',') : new string[0];
                if (m.Groups[1].Success)
                {
                    if (options.Length == 0) allowed.Add(domain);   // site-wide exception
                    continue;
                }
                bool thirdPartyOnly = false, usable = true;
                foreach (var option in options)
                {
                    if (option == "third-party" || option == "3p") thirdPartyOnly = true;
                    else if (!TypeOptions.Contains(option)) { usable = false; break; }   // site-specific rules are skipped
                }
                if (usable) (thirdPartyOnly ? blockedThirdParty : blocked).Add(domain);
            }
        }

        public bool ShouldBlock(string requestHost, string pageHost)
        {
            if (string.IsNullOrEmpty(requestHost)) return false;
            requestHost = requestHost.ToLowerInvariant();
            for (var h = requestHost; h != null; h = Parent(h))
                if (allowed.Contains(h)) return false;
            for (var h = requestHost; h != null; h = Parent(h))
            {
                if (blocked.Contains(h)) return true;
                if (blockedThirdParty.Contains(h) && !SameSite(requestHost, pageHost)) return true;
            }
            return false;
        }

        static string Parent(string host)
        {
            int dot = host.IndexOf('.');
            return dot < 0 || dot == host.Length - 1 ? null : host.Substring(dot + 1);
        }

        static bool SameSite(string a, string b) => !string.IsNullOrEmpty(b) && Site(a) == Site(b.ToLowerInvariant());

        static string Site(string host)
        {
            if (IPAddress.TryParse(host, out _)) return host;
            var parts = host.Split('.');
            int n = parts.Length;
            if (n <= 2) return host;
            int take = parts[n - 1].Length == 2 && SecondLevel.Contains(parts[n - 2]) ? 3 : 2;
            return string.Join(".", parts, n - take, take);
        }

        public static async Task<AdBlocker> DownloadLatestAsync()
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                var text = await http.GetStringAsync(ListUrl);
                if (!text.StartsWith("[Adblock", StringComparison.Ordinal)) throw new InvalidDataException(L.T("The downloaded file is not an ad block list."));
                FileUtil.WriteAtomic(AppPaths.UpdatedAdListFile, text);
            }
            return await Task.Run(() => Load());
        }
    }
}
