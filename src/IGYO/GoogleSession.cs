using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // Whether a profile is signed in on Google's sites, read from that profile's own cookies on this PC.
    // Nothing is asked of Google, and no cookie's value is kept, shown or written anywhere: only whether it is there.
    static class GoogleSession
    {
        public const string SignInUrl = "https://accounts.google.com/";
        public const string AccountUrl = "https://myaccount.google.com/";

        // Google's own page "How Google uses cookies" says the cookies called SID and HSID hold the signed-in
        // account (its ID and the latest sign-in time). Either one, with something in it, counts as signed in.
        static readonly string[] SessionCookies = { "SID", "HSID" };

        // cookies: name and value of each cookie the profile would send to Google's sign-in address.
        public static bool LooksSignedIn(IEnumerable<KeyValuePair<string, string>> cookies) =>
            cookies != null && cookies.Any(c => !string.IsNullOrEmpty(c.Value) && SessionCookies.Contains(c.Key, StringComparer.Ordinal));

        // Null when it could not be read (the view is gone, the engine is busy closing).
        public static async Task<bool?> ReadAsync(CoreWebView2 core)
        {
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(SignInUrl);
                return LooksSignedIn(cookies.Select(c => new KeyValuePair<string, string>(c.Name, c.Value)));
            }
            catch { return null; }
        }

        // Pages where signing in or out happens, or shows: after one of them loads, the answer is read again.
        public static bool IsGoogleSite(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "google.com" || host.EndsWith(".google.com") || host == "youtube.com" || host.EndsWith(".youtube.com");
        }
    }
}
