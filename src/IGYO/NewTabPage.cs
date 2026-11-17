using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    sealed class NewTabShortcut
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }

    // What a profile's New Tab page shows. The app keeps it in the profile's folder (not as the page's site data),
    // so clearing cookies and site data leaves your shortcuts and pictures alone.
    sealed class NewTabData
    {
        public List<NewTabShortcut> Shortcuts { get; set; }
        public bool ShowClock { get; set; } = true;
        public bool ShowShortcuts { get; set; } = true;
        public List<string> Pictures { get; set; } = new List<string>();   // file names in the profile's NewTab folder
        public string Picture { get; set; } = "";                          // "" = IGYO's own look
        public bool Rotate { get; set; }                                   // a different picture with every new tab
        public int ClockSize { get; set; } = 100;                          // the time's size, in percent of the look's own
        public int Blur { get; set; } = -1;                                // how out of focus the picture is, 0 to 100; -1: as the look has it
    }

    // Serves the New Tab page at https://newtab.igyo/ from Assets\NewTab. Nothing goes to the network:
    // every request to that address is answered here, and only for this app's own tabs showing that page.
    static class NewTabPage
    {
        public const string Origin = "https://newtab.igyo";
        public const string Url = Origin + "/";
        public const string Filter = Origin + "/*";

        const int MaxShortcuts = 24, MaxPictures = 24, MaxSide = 3840, ThumbSide = 400;
        static readonly string[] PictureTypes = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".avif" };
        static readonly Dictionary<string, NewTabData> loaded = new Dictionary<string, NewTabData>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> rotation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        const string PageHeaders = InternalAssets.Common +
            "\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: data: blob:; font-src 'self' data:; " +
            "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'" +
            "\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer";

        public static bool IsUrl(string url) =>
            !string.IsNullOrEmpty(url) &&
            (url.Equals(Origin, StringComparison.OrdinalIgnoreCase) || url.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase));

        static string AssetsDir => Path.Combine(AppPaths.AppDir, "Assets", "NewTab");

        static string FolderFor(string profileId)
        {
            var dir = Path.Combine(ProfileStore.DirFor(profileId), "NewTab");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ----- stored data -----

        public static NewTabData Load(string profileId)
        {
            if (loaded.TryGetValue(profileId, out var cached)) return cached;
            NewTabData data = null;
            var folder = FolderFor(profileId);
            try
            {
                var file = Path.Combine(folder, "newtab.json");
                if (File.Exists(file)) data = Json.Deserialize<NewTabData>(File.ReadAllText(file));
            }
            catch { }
            data = data ?? new NewTabData();
            if (data.Shortcuts == null) data.Shortcuts = DefaultShortcuts();
            data.Shortcuts = data.Shortcuts.Where(s => s != null && CleanUrl(s.Url) != null).Take(MaxShortcuts).ToList();
            foreach (var s in data.Shortcuts) s.Name = string.IsNullOrWhiteSpace(s.Name) ? Favicons.HostOf(s.Url) ?? s.Url : s.Name.Trim();
            if (data.Pictures == null) data.Pictures = new List<string>();
            data.Pictures.RemoveAll(p => !ValidName(p) || !File.Exists(Path.Combine(folder, p)));
            if (!data.Pictures.Contains(data.Picture ?? "")) data.Picture = "";
            data.ClockSize = ClockSizeFrom(data.ClockSize);
            data.Blur = BlurFrom(data.Blur);
            loaded[profileId] = data;
            return data;
        }

        static void Save(string profileId, NewTabData data)
        {
            try { FileUtil.WriteAtomic(Path.Combine(FolderFor(profileId), "newtab.json"), Json.Write(data)); } catch { }
        }

        static List<NewTabShortcut> DefaultShortcuts() => new List<NewTabShortcut>
        {
            new NewTabShortcut { Name = "Gmail", Url = "https://mail.google.com/" },
            new NewTabShortcut { Name = "Google Images", Url = "https://images.google.com/" },
            new NewTabShortcut { Name = "Drive", Url = "https://drive.google.com/" },
        };

        static bool ValidName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= 64 && name.All(c => char.IsLetterOrDigit(c) || c == '.') &&
            !name.Contains("..") && PictureTypes.Contains(Path.GetExtension(name).ToLowerInvariant());

        // The time's size as the page may ask for it: 60 to 140 percent of the look's own; anything else is 100.
        public const int MinClockSize = 60, MaxClockSize = 140;
        public static int ClockSizeFrom(object value)
        {
            double percent;
            if (value is int i) percent = i;
            else if (value is long l) percent = l;
            else if (value is double d) percent = d;
            else if (value is decimal m) percent = (double)m;
            else return 100;
            if (double.IsNaN(percent) || percent < 1) return 100;
            return (int)Math.Round(Math.Max(MinClockSize, Math.Min(MaxClockSize, percent)));
        }

        // How out of focus the background picture is, as the page may ask for it: 0 (sharp) to 100, where 50 is the
        // soft copy's blur and 100 twice that. Below 0, or anything that is no amount, is -1: the look decides (a look
        // that lays your picture behind glass starts it at 50, the others show it sharp).
        public const int MaxBlur = 100;
        public static int BlurFrom(object value)
        {
            double amount;
            if (value is int i) amount = i;
            else if (value is long l) amount = l;
            else if (value is double d) amount = d;
            else if (value is decimal m) amount = (double)m;
            else return -1;
            if (double.IsNaN(amount) || amount < 0) return -1;
            return (int)Math.Round(Math.Min(MaxBlur, amount));
        }

        static string ThumbName(string picture) => Path.GetFileNameWithoutExtension(picture) + ".thumb.jpg";

        // Beside each picture: a small copy of it out of focus, for looks that lay your picture behind glass.
        public static string SoftName(string picture) => Path.GetFileNameWithoutExtension(picture) + ".soft.jpg";

        // ----- requests -----

        // True when the request was for the New Tab page's address (answered or refused here).
        public static bool TryServe(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string topLevelRequest)
        {
            var uri = e.Request.Uri;
            if (!IsUrl(uri) || core == null) return false;
            try
            {
                var target = new Uri(uri);
                var path = target.AbsolutePath;
                if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    // Only as the page of a tab: never inside another site's frame.
                    if ((path == "/" || path == "/index.html") && InternalAssets.SameAddress(uri, topLevelRequest))
                        Respond(core, e, File.ReadAllBytes(Path.Combine(AssetsDir, "index.html")), "text/html; charset=utf-8", PageHeaders);
                    else Refuse(core, e);
                    return true;
                }
                // Everything else is only for the New Tab page itself, so other sites can't peek at it.
                if (!IsUrl(core.Source)) { Refuse(core, e, 403); return true; }
                if (e.Request.Method == "POST") HandleCommand(owner, core, e, path, InternalAssets.Query(uri));
                else if (e.Request.Method == "GET") HandleFile(owner, core, e, path);
                else Refuse(core, e, 405);
            }
            catch
            {
                try { Refuse(core, e, 500); } catch { }
            }
            return true;
        }

        static void HandleFile(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string path)
        {
            if (InternalAssets.TryServeCommon(owner, core, e, path)) return;   // the design system, the theme, site icons
            if (path == "/config.js")
            {
                Respond(core, e, Encoding.UTF8.GetBytes(Config(owner)), "text/javascript; charset=utf-8");
                return;
            }
            if (path.StartsWith("/favicon/"))
            {
                var file = Favicons.FileForHost(Uri.UnescapeDataString(path.Substring("/favicon/".Length)));
                if (file != null) Respond(core, e, File.ReadAllBytes(file), "image/png");
                else Refuse(core, e);
                return;
            }
            if (path.StartsWith("/picture/") || path.StartsWith("/thumb/"))
            {
                bool thumb = path.StartsWith("/thumb/");
                var name = path.Substring(thumb ? "/thumb/".Length : "/picture/".Length);
                var data = Load(owner.Profile.Id);
                if (!data.Pictures.Contains(name)) { Refuse(core, e); return; }
                var folder = FolderFor(owner.Profile.Id);
                var file = Path.Combine(folder, name);
                if (thumb && File.Exists(Path.Combine(folder, ThumbName(name)))) file = Path.Combine(folder, ThumbName(name));
                if (File.Exists(file)) Respond(core, e, File.ReadAllBytes(file), TypeOf(file));
                else Refuse(core, e);
                return;
            }
            if (path.StartsWith("/soft/"))
            {
                // The out-of-focus copy. A picture added before there were such copies gets its own now (not from
                // a private window, which changes nothing). Where there is none (Windows can't read WebP or AVIF)
                // the answer is "not found", and the page softens the picture itself.
                var name = path.Substring("/soft/".Length);
                var data = Load(owner.Profile.Id);
                if (!data.Pictures.Contains(name)) { Refuse(core, e); return; }
                var folder = FolderFor(owner.Profile.Id);
                var soft = Path.Combine(folder, SoftName(name));
                if (!File.Exists(soft) && !owner.IsPrivate) TryMakeSoft(Path.Combine(folder, name), soft);
                if (File.Exists(soft)) Respond(core, e, File.ReadAllBytes(soft), "image/jpeg");
                else Refuse(core, e);
                return;
            }
            // The page's own files (index.html, newtab.css, newtab.js).
            if (!InternalAssets.TryServeFile(core, e, "NewTab", path.TrimStart('/'))) Refuse(core, e);
        }

        static void HandleCommand(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string path, Dictionary<string, string> args)
        {
            if (path == "/api/open-themes")
            {
                // Customize's "Themes": Settings at Themes, in this window (the same as Menu > Themes; it changes nothing,
                // so a private window may ask too). After this answer, not inside the engine's request.
                owner.BeginInvoke((Action)(() => { if (!owner.IsDisposed) owner.OpenSettings("themes"); }));
                RespondJson(core, e, new Dictionary<string, object> { ["ok"] = true });
                return;
            }
            if (owner.IsPrivate) { Refuse(core, e, 403); return; }   // private windows change nothing
            var profileId = owner.Profile.Id;
            var data = Load(profileId);
            switch (path)
            {
                case "/api/save":
                    if (args.TryGetValue("data", out var json)) Apply(data, json);
                    Save(profileId, data);
                    RespondJson(core, e, State(data));
                    break;
                case "/api/remove-picture":
                    if (args.TryGetValue("id", out var id) && data.Pictures.Contains(id))
                    {
                        data.Pictures.Remove(id);
                        if (data.Picture == id) data.Picture = "";
                        var folder = FolderFor(profileId);
                        TryDelete(Path.Combine(folder, id));
                        TryDelete(Path.Combine(folder, ThumbName(id)));
                        TryDelete(Path.Combine(folder, SoftName(id)));
                        Save(profileId, data);
                    }
                    RespondJson(core, e, State(data));
                    break;
                case "/api/add-pictures":
                    AddPictures(owner, core, e, data, profileId);
                    break;
                default:
                    Refuse(core, e);
                    break;
            }
        }

        static string Config(MainForm owner)
        {
            var s = owner.AppSettings;
            var data = Load(owner.Profile.Id);
            string shown = data.Picture ?? "";
            if (data.Rotate && data.Pictures.Count > 1)
            {
                if (!rotation.TryGetValue(owner.Profile.Id, out int n)) n = Math.Max(0, data.Pictures.IndexOf(shown));
                shown = data.Pictures[n % data.Pictures.Count];
                rotation[owner.Profile.Id] = n + 1;
            }
            var theme = owner.CurrentTheme;
            var config = new Dictionary<string, object>
            {
                ["engine"] = s.SearchTemplate(),
                ["engineName"] = s.SearchEngine == "Custom" || !Settings.SearchEngines.ContainsKey(s.SearchEngine ?? "") ? L.T("the web") : s.SearchEngine,
                ["anim"] = s.Animations,
                ["accent"] = Hex(theme.Accent),
                ["page"] = Hex(theme.Toolbar),
                ["isPrivate"] = owner.IsPrivate,
                ["clock"] = data.ShowClock,
                ["clockSize"] = data.ClockSize,
                ["blur"] = data.Blur,
                ["showShortcuts"] = data.ShowShortcuts,
                ["shortcuts"] = data.Shortcuts.Select(x => new Dictionary<string, object> { ["name"] = x.Name, ["url"] = x.Url }).ToList(),
                ["pictures"] = PictureList(data),
                ["picture"] = data.Picture ?? "",
                ["shown"] = shown,
                ["rotate"] = data.Rotate,
            };
            return "window.IGYO_NEWTAB = " + Json.Write(config) + ";\n";
        }

        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        static Dictionary<string, object> State(NewTabData data) => new Dictionary<string, object>
        {
            ["pictures"] = PictureList(data),
            ["picture"] = data.Picture ?? "",
        };

        static List<Dictionary<string, object>> PictureList(NewTabData data) =>
            data.Pictures.Select(p => new Dictionary<string, object> { ["id"] = p, ["full"] = "/picture/" + p, ["thumb"] = "/thumb/" + p, ["soft"] = "/soft/" + p }).ToList();

        // Takes what the page sends, keeping only sensible values.
        static void Apply(NewTabData data, string json)
        {
            var o = Json.Obj(Json.Deserialize<object>(json));
            if (o == null) return;
            if (o.TryGetValue("clock", out var clock) && clock is bool showClock) data.ShowClock = showClock;
            if (o.TryGetValue("clockSize", out var size)) data.ClockSize = ClockSizeFrom(size);
            if (o.TryGetValue("blur", out var blur)) data.Blur = BlurFrom(blur);
            if (o.TryGetValue("showShortcuts", out var tiles) && tiles is bool showTiles) data.ShowShortcuts = showTiles;
            if (o.TryGetValue("rotate", out var rotate) && rotate is bool rotateOn) data.Rotate = rotateOn;
            if (o.TryGetValue("picture", out var picture) && picture is string chosen && (chosen.Length == 0 || data.Pictures.Contains(chosen)))
                data.Picture = chosen;
            if (o.TryGetValue("shortcuts", out var list))
            {
                var shortcuts = new List<NewTabShortcut>();
                foreach (var item in Json.Arr(list))
                {
                    var x = Json.Obj(item);
                    if (x == null) continue;
                    var url = CleanUrl(x.TryGetValue("url", out var u) ? u as string : null);
                    if (url == null) continue;
                    var name = ((x.TryGetValue("name", out var n) ? n as string : null) ?? "").Trim();
                    if (name.Length == 0) name = Favicons.HostOf(url) ?? url;
                    if (name.Length > 60) name = name.Substring(0, 60);
                    shortcuts.Add(new NewTabShortcut { Name = name, Url = url });
                    if (shortcuts.Count >= MaxShortcuts) break;
                }
                data.Shortcuts = shortcuts;
            }
        }

        static string CleanUrl(string url)
        {
            url = url?.Trim();
            if (string.IsNullOrEmpty(url) || url.Length > 2048) return null;
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0
                ? u.AbsoluteUri : null;
        }

        // ----- pictures -----

        // Opens Windows' file picker for the page; the page waits for the answer.
        static void AddPictures(MainForm owner, CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, NewTabData data, string profileId)
        {
            var deferral = e.GetDeferral();
            Log("add-pictures: request");
            owner.BeginInvoke(new Action(() =>
            {
                int added = 0, skipped = 0;
                Log("add-pictures: opening the picker");
                try
                {
                    using (var dialog = new OpenFileDialog
                    {
                        Title = L.T("Choose pictures for the New Tab page"),
                        Filter = L.T("Pictures") + "|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp;*.avif|" + L.T("All files") + "|*.*",
                        Multiselect = true,
                    })
                    {
                        if (dialog.ShowDialog(owner) == DialogResult.OK)
                        {
                            var folder = FolderFor(profileId);
                            string first = null;
                            Cursor.Current = Cursors.WaitCursor;
                            foreach (var file in dialog.FileNames)
                            {
                                var name = data.Pictures.Count < MaxPictures ? Import(file, folder) : null;
                                if (name == null) { skipped++; continue; }
                                data.Pictures.Add(name);
                                first = first ?? name;
                                added++;
                            }
                            Cursor.Current = Cursors.Default;
                            if (first != null)
                            {
                                data.Picture = first;
                                Save(profileId, data);
                            }
                        }
                    }
                    var state = State(data);
                    state["added"] = added;
                    state["skipped"] = skipped;
                    RespondJson(core, e, state);
                    Log($"add-pictures: added {added}, skipped {skipped}");
                }
                catch (Exception ex)
                {
                    Log("add-pictures: " + ex);
                    try { Refuse(core, e, 500); } catch { }
                }
                finally
                {
                    deferral.Complete();
                }
            }));
        }

        [System.Diagnostics.Conditional("DEBUG")]
        static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-newtab.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + text + "\n"); } catch { }
        }

        // Copies a picture into the profile. Very large ones are scaled down to 4K so new tabs open fast.
        static string Import(string source, string folder)
        {
            try
            {
                var info = new FileInfo(source);
                var ext = info.Extension.ToLowerInvariant();
                if (!info.Exists || info.Length == 0 || info.Length > 60L * 1024 * 1024 || !PictureTypes.Contains(ext)) return null;
                var id = Guid.NewGuid().ToString("N").Substring(0, 16);
                if (ext == ".webp" || ext == ".avif")
                {
                    // The engine shows these, but Windows' picture library can't read them, so they're kept as they are.
                    if (info.Length > 25L * 1024 * 1024 || !HasSignature(source, ext)) return null;
                    var copy = id + ext;
                    File.Copy(source, Path.Combine(folder, copy));
                    return copy;
                }
                using (var stream = new MemoryStream(File.ReadAllBytes(source)))
                using (var image = Image.FromStream(stream, true, true))
                {
                    bool animated = ext == ".gif" && image.FrameDimensionsList.Contains(FrameDimension.Time.Guid) && image.GetFrameCount(FrameDimension.Time) > 1;
                    int orientation = animated ? 1 : Pictures.Orientation(image);
                    if (orientation > 1) image.RotateFlip(Pictures.FlipFor(orientation));
                    bool keep = animated || (orientation <= 1 && ext != ".bmp" && Math.Max(image.Width, image.Height) <= MaxSide && info.Length <= 12L * 1024 * 1024);
                    string name;
                    if (keep)
                    {
                        name = id + (ext == ".jpeg" ? ".jpg" : ext);
                        File.Copy(source, Path.Combine(folder, name));
                    }
                    else
                    {
                        name = id + ".jpg";
                        using (var large = Pictures.Scale(image, MaxSide)) Pictures.SaveJpeg(large, Path.Combine(folder, name), 90L);
                    }
                    using (var small = Pictures.Scale(image, ThumbSide)) Pictures.SaveJpeg(small, Path.Combine(folder, ThumbName(name)), 85L);
                    SaveSoft(image, Path.Combine(folder, SoftName(name)));
                    return name;
                }
            }
            catch
            {
                return null;
            }
        }

        // Makes a picture file's out-of-focus copy. False when it can't be made: Windows' picture library doesn't
        // read WebP or AVIF, and the page then softens such a picture itself.
        public static bool TryMakeSoft(string picture, string soft)
        {
            try
            {
                using (var stream = new MemoryStream(File.ReadAllBytes(picture)))
                using (var image = Image.FromStream(stream, true, true))
                {
                    int orientation = Pictures.Orientation(image);
                    if (orientation > 1) image.RotateFlip(Pictures.FlipFor(orientation));
                    return SaveSoft(image, soft);
                }
            }
            catch
            {
                return false;
            }
        }

        // (Written beside its place first, so a half-written file is never taken for the copy.)
        static bool SaveSoft(Image upright, string soft)
        {
            var draft = soft + ".tmp";
            try
            {
                using (var softened = Pictures.Soften(upright)) Pictures.SaveJpeg(softened, draft, 90L);
                TryDelete(soft);
                File.Move(draft, soft);
                return true;
            }
            catch
            {
                TryDelete(draft);
                return false;
            }
        }

        static bool HasSignature(string file, string ext)
        {
            var head = new byte[12];
            using (var f = File.OpenRead(file))
                if (f.Read(head, 0, head.Length) < head.Length) return false;
            return ext == ".webp"
                ? Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "WEBP"
                : Encoding.ASCII.GetString(head, 4, 4) == "ftyp";
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        // ----- responses -----

        static void Respond(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, byte[] body, string type, string headers = InternalAssets.Common) =>
            InternalAssets.Respond(core, e, body, type, headers);

        static void RespondJson(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, object value) => InternalAssets.RespondJson(core, e, value);

        static void Refuse(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, int status = 404) => InternalAssets.Refuse(core, e, status);

        static string TypeOf(string file) => InternalAssets.TypeOf(file);
    }
}
