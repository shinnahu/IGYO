using System;
using System.Collections.Generic;
using System.Linq;

namespace IGYO
{
    // Sites whose tabs are never put to sleep or unloaded, for pages that must keep running in the background: a chat
    // that should ring, a page that records, a call that IGYO can't tell is one (Settings > Memory saver, and the main
    // menu's Memory saver for the site on show). A site is a host name ("meet.google.com"), and covers the names
    // under it too ("google.com" covers "meet.google.com"); a leading "www." is left off.
    static class AwakeSites
    {
        // The site for what the person typed (a name, or a whole address) or for a page's address: a host name in
        // lower case, or null when it is none (IGYO's own pages, anything that isn't a website, a name without a dot).
        public static string SiteOf(string text)
        {
            var t = (text ?? "").Trim();
            if (t.StartsWith("[*.]")) t = t.Substring(4);
            else if (t.StartsWith("*.")) t = t.Substring(2);
            if (t.Length == 0) return null;
            if (!t.Contains("://")) t = "https://" + t;
            if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
            if (InternalPages.IsInternal(uri.AbsoluteUri)) return null;
            string host;
            try { host = uri.IdnHost.ToLowerInvariant().TrimEnd('.'); } catch { return null; }
            if (host.StartsWith("www.") && host.IndexOf('.', 4) > 0) host = host.Substring(4);
            bool name = host.Contains('.') || host == "localhost" || uri.HostNameType == UriHostNameType.IPv6;
            return name && host.Length > 0 ? host : null;
        }

        // Whether a page's address is on one of the sites.
        public static bool Covers(IEnumerable<string> sites, string url) => EntryFor(sites, url) != null;

        // The site on the list that a page's address is on ("google.com" for a page of meet.google.com), or null.
        public static string EntryFor(IEnumerable<string> sites, string url)
        {
            if (sites == null || !Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
            string host;
            try { host = uri.IdnHost.ToLowerInvariant().TrimEnd('.'); } catch { return null; }
            foreach (var site in sites)
                if (!string.IsNullOrEmpty(site) && (host == site || host.EndsWith("." + site, StringComparison.Ordinal))) return site;
            return null;
        }

        // The list as it is kept: each site once, in the order they were added, and nothing that isn't a site.
        public static List<string> Clean(IEnumerable<string> sites) =>
            (sites ?? Enumerable.Empty<string>()).Select(SiteOf).Where(s => s != null).Distinct().ToList();
    }
}
