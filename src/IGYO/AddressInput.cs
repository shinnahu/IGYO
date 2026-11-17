using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace IGYO
{
    // Turns what was typed in the address bar into an address or a search. Nothing is ever suggested.
    static class AddressInput
    {
        static readonly Regex SchemePrefix = new Regex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:", RegexOptions.Compiled);
        // ("edge": the engine's own pages, such as edge://history; "chrome" is its other name for them)
        static readonly string[] KnownSchemes = { "http", "https", "file", "about", "data", "view-source", "ftp", "mailto", "edge", "chrome" };

        public static string ToUrl(string input, Func<string, string> searchUrl)
        {
            var text = (input ?? "").Trim();
            if (text.Length == 0) return null;

            if (SchemePrefix.IsMatch(text) && Uri.TryCreate(text, UriKind.Absolute, out var absolute) && KnownSchemes.Contains(absolute.Scheme))
                return absolute.Scheme == "about" || absolute.Scheme == "view-source" ? text : absolute.AbsoluteUri;

            if (text.IndexOf(' ') < 0 && LooksLikeHost(text, out var local) &&
                Uri.TryCreate((local ? "http://" : "https://") + text, UriKind.Absolute, out var guessed) && guessed.Host.Length > 0)
                return guessed.AbsoluteUri;

            return searchUrl(text);
        }

        static bool LooksLikeHost(string text, out bool local)
        {
            local = false;
            var host = text.Split(new[] { '/', '?', '#' }, 2)[0];
            int colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                var port = host.Substring(colon + 1);
                if (port.Length == 0 || !port.All(char.IsDigit)) return false;
                host = host.Substring(0, colon);
            }
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                local = true;
                return true;
            }
            if (host.Count(c => c == '.') == 3 && IPAddress.TryParse(host, out _))
            {
                local = true;
                return true;
            }
            var labels = host.Split('.');
            if (labels.Length < 2 || labels.Any(l => l.Length == 0)) return false;
            var tld = labels[labels.Length - 1];
            return tld.Length >= 2 && tld.All(char.IsLetter) && labels.All(l => l.All(c => char.IsLetterOrDigit(c) || c == '-'));
        }
    }
}
