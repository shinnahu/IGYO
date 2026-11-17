using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace IGYO
{
    // Remembers each site's icon (one small PNG per host) so bookmarks and reopened tabs can show it.
    static class Favicons
    {
        static readonly Dictionary<string, Image> cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        public static event Action<string> Changed;   // host: its icon is new or different

        static string Dir => Path.Combine(AppPaths.DataDir, "Favicons");

        public static string HostOf(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && u.Host.Length > 0
                ? StripWww(u.Host.ToLowerInvariant())
                : null;

        static string StripWww(string host) => host.StartsWith("www.") ? host.Substring(4) : host;

        static string FileFor(string host) =>
            Path.Combine(Dir, new string(host.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray()) + ".png");

        static HashSet<string> known;   // hosts with a saved icon, so lists of bookmarks don't touch the disk for each one

        public static bool Has(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (known == null)
            {
                known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (Directory.Exists(Dir))
                        foreach (var file in Directory.EnumerateFiles(Dir, "*.png")) known.Add(Path.GetFileNameWithoutExtension(file));
                }
                catch { }
            }
            return known.Contains(Path.GetFileNameWithoutExtension(FileFor(StripWww(host.Trim().ToLowerInvariant()))));
        }

        // The saved icon file for a host name, or null when there is none.
        public static string FileForHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return null;
            var file = FileFor(StripWww(host.Trim().ToLowerInvariant()));
            return File.Exists(file) ? file : null;
        }

        public static void Save(string pageUrl, byte[] png)
        {
            var host = HostOf(pageUrl);
            if (host == null || png == null || png.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var file = FileFor(host);
                if (File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(png)) return;   // same icon as before
                File.WriteAllBytes(file, png);
                known?.Add(Path.GetFileNameWithoutExtension(file));
                // The old picture may still be on a bookmark button or in a menu, so it is left to the garbage
                // collector rather than disposed here (a disposed picture crashes the toolbar's next layout).
                cache.Remove(host);
                Changed?.Invoke(host);
            }
            catch { }
        }

        public static Image Get(string url)
        {
            var host = HostOf(url);
            if (host == null) return null;
            if (cache.TryGetValue(host, out var image)) return image;
            image = null;
            var file = FileFor(host);
            if (File.Exists(file))
            {
                try
                {
                    using (var stream = new MemoryStream(File.ReadAllBytes(file)))
                    using (var source = Image.FromStream(stream))
                        image = new Bitmap(source);
                }
                catch { image = null; }
            }
            cache[host] = image;
            return image;
        }
    }
}
