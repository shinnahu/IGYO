using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // Browser extensions. IGYO has no store and installs nothing by itself: an extension is used when it stands in
    // the extensions folder (in the data folder), as its unpacked folder or as a .zip or .crx file, which is
    // unpacked there. They are listed under Tools > Extensions, where each can be switched off.
    //
    // The engine has its extension support switched on only when that folder holds something as IGYO starts
    // (it cannot be switched while the engine runs), so with an empty folder nothing of it is running at all.
    // What the engine does not offer in an app like this one: a toolbar button and badge for an extension, and
    // its popup on a click; IGYO opens an extension's popup and options pages as ordinary tabs instead.

    // One entry of the extensions folder, and what the engine made of it in one profile.
    sealed class ExtensionInfo
    {
        public string Entry;            // what stands in the extensions folder: a folder, or the .zip or .crx it was unpacked from
        public string Key = "";         // its name in that folder (the unpacked folder's name): what its pin and its settings go by
        public string Folder;           // where its manifest.json is
        public string Name = "";        // from its manifest, until the engine gives the name it shows
        public string Popup, Options;   // its own pages, as paths inside it
        public string Version = "", Description = "";
        public string Title = "";       // what its button says (its manifest's own words for it), "" when it has none
        public string IconFile;         // a picture of it among its own files (the full path); null when it brings none
        public List<string> Permissions = new List<string>();   // what its manifest asks the browser for
        public List<string> Sites = new List<string>();         // the websites its manifest names: where it may read and change pages
        public bool WantsRecording;     // it asks to record tabs ("tabCapture")
        public string StoreId;          // its id in a web store, when it came as a store's package (the package carries it)
        public string KeyId;            // the id the engine gives it because of the key in its manifest; null when it has no key
        public string Store;            // that store's name, when its manifest says where it is updated from
        public string Error;            // why it could not be used
        public string Id;               // given by the engine once it is loaded
        public bool Enabled;
        public bool Fresh;              // loaded for the first time just now: pages already open have to be loaded again
        public bool Unpacked;           // unpacked from its file just now (the first time, or because the file is newer)
        public CoreWebView2BrowserExtension Handle;
    }

    static class ExtensionFolder
    {
        public static string Dir => Path.Combine(AppPaths.DataDir, "Extensions");

        const string SetAside = "metadata (from the store, set aside)";
        const long MostBytes = 400L * 1024 * 1024;
        const int MostFiles = 20000;

        // Whether there is anything in the folder at all: asked once, as the engine starts.
        public static bool Any(string dir = null)
        {
            try
            {
                dir = dir ?? Dir;
                return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(e => Directory.Exists(e) ? !Hidden(e) : IsPacked(e));
            }
            catch { return false; }
        }

        // An extension the engine would not let go of while it ran keeps its files until IGYO next starts; they are
        // moved to the Recycle Bin then, before the engine starts (which then takes it out: see ProfileExtensions).
        static string PendingFile => Path.Combine(AppPaths.DataDir, "extensions-to-remove.txt");

        public static void RemoveAtNextStart(IEnumerable<string> paths)
        {
            try
            {
                var all = File.Exists(PendingFile) ? File.ReadAllLines(PendingFile).ToList() : new List<string>();
                all.AddRange(paths);
                FileUtil.WriteAtomic(PendingFile, string.Join("\r\n", all.Where(p => p.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        public static void RemovePending()
        {
            if (!File.Exists(PendingFile)) return;
            try
            {
                string root = Path.GetFullPath(Dir).TrimEnd('\\') + "\\";
                foreach (var line in File.ReadAllLines(PendingFile))
                {
                    string path;
                    try { path = Path.GetFullPath(line.Trim()); } catch { continue; }
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length <= root.Length) continue;   // only ever what stands in the extensions folder
                    if (Directory.Exists(path) || File.Exists(path)) FileUtil.Recycle(path);
                }
                File.Delete(PendingFile);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        static bool Hidden(string path) => Path.GetFileName(path).StartsWith(".");
        static bool IsPacked(string file) => file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".crx", StringComparison.OrdinalIgnoreCase);

        // Everything in the folder, ready to be handed to the engine: packed files are unpacked first (once, and
        // again when the file is newer than what was unpacked from it), and each folder is looked into for its
        // manifest. Something that cannot be used is listed too, with the reason.
        public static List<ExtensionInfo> Find(string dir = null)
        {
            var list = new List<ExtensionInfo>();
            dir = dir ?? Dir;
            if (!Directory.Exists(dir)) return list;
            var unpacked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // folder -> the file it came from
            var justNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(dir).Where(IsPacked).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string target = Path.Combine(dir, Path.GetFileNameWithoutExtension(file));
                try
                {
                    if (!Directory.Exists(target) || Directory.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(file))
                    {
                        Unpack(file, target);
                        justNow.Add(target);
                    }
                    unpacked[target] = file;
                }
                catch (Exception ex)
                {
                    list.Add(new ExtensionInfo { Entry = file, Name = Path.GetFileName(file), Error = L.T("The file could not be unpacked: ") + ex.Message });
                    unpacked[target] = null;   // whatever is left of it is not looked at
                }
            }
            foreach (var folder in Directory.GetDirectories(dir).Where(d => !Hidden(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (unpacked.TryGetValue(folder, out var from) && from == null) continue;
                var info = new ExtensionInfo { Entry = from ?? folder, Key = Path.GetFileName(folder), Name = Path.GetFileName(folder), Unpacked = justNow.Contains(folder) };
                list.Add(info);
                try
                {
                    if (from != null && from.EndsWith(".crx", StringComparison.OrdinalIgnoreCase)) info.StoreId = ExtensionStore.IdOfFile(from);
                    info.Folder = RootOf(folder);
                    if (info.Folder == null) { info.Error = L.T("There is no manifest.json in it, so it is not an unpacked extension."); continue; }
                    // A store's installed copy carries a folder that the engine refuses in an unpacked extension.
                    string metadata = Path.Combine(info.Folder, "_metadata");
                    if (Directory.Exists(metadata))
                    {
                        string aside = Path.Combine(info.Folder, SetAside);
                        if (Directory.Exists(aside)) Directory.Delete(metadata, true);   // an updated copy brought it again; the first one was kept
                        else Directory.Move(metadata, aside);
                    }
                    string manifestFile = Path.Combine(info.Folder, "manifest.json");
                    var manifest = Json.Obj(Json.Parse(File.ReadAllText(manifestFile)));
                    if (manifest == null) { info.Error = L.T("Its manifest.json could not be read."); continue; }
                    // An extension that came as a store's package has its id from the key the package was signed with,
                    // and its own code counts on that id (it asks for its files by it). Unpacked, it would get
                    // another id, made from where its folder is. So the key is written into its manifest, which is
                    // what a browser does when it installs one, and the engine then gives it the id it has in the store.
                    if (info.StoreId != null && Json.Str(manifest, "key") == null && GiveKey(manifestFile, from))
                    {
                        manifest = Json.Obj(Json.Parse(File.ReadAllText(manifestFile))) ?? manifest;
                        info.Unpacked = true;   // the engine has to read it again
                    }
                    Describe(info, manifest);
                }
                catch (Exception ex) { info.Error = ex.Message; }
            }
            return list;
        }

        // Writes the key of the package the folder was unpacked from into the manifest, as its first line. The file
        // is left as it was when the key is not found, or when the manifest does not read back with it.
        static bool GiveKey(string manifestFile, string package)
        {
            try
            {
                string key = ExtensionStore.KeyInside(ExtensionStore.HeaderOf(package));
                if (key == null) return false;
                string text = File.ReadAllText(manifestFile);
                int open = text.IndexOf('{');
                if (open < 0) return false;
                string with = text.Substring(0, open + 1) + "\n  \"key\": \"" + key + "\"," + text.Substring(open + 1);
                if (Json.Str(Json.Obj(Json.Parse(with)), "key") != key) return false;
                FileUtil.WriteAtomic(manifestFile, with);
                return true;
            }
            catch { return false; }
        }

        // What the manifest says about the extension: its name and words, its own pages, its picture, and what it
        // asks for. (Words written as __MSG_name__ are looked up in the extension's own language files.)
        public static void Describe(ExtensionInfo info, Dictionary<string, object> manifest)
        {
            var words = new Lazy<Dictionary<string, string>>(() => Words(info.Folder, Json.Str(manifest, "default_locale")));
            string say(string text)
            {
                text = (text ?? "").Trim();
                if (!text.StartsWith("__MSG_") || !text.EndsWith("__") || text.Length < 9) return text;
                return words.Value.TryGetValue(text.Substring(6, text.Length - 8).ToLowerInvariant(), out var found) ? found.Trim() : "";
            }
            var action = Json.Obj(Json.Get(manifest, "action")) ?? Json.Obj(Json.Get(manifest, "browser_action")) ?? Json.Obj(Json.Get(manifest, "page_action"));
            string name = say(Json.Str(manifest, "name"));
            if (name.Length > 0) info.Name = name;
            info.Version = (Json.Str(manifest, "version_name") ?? Json.Str(manifest, "version") ?? "").Trim();
            info.Description = say(Json.Str(manifest, "description"));
            info.Title = say(Json.Str(action, "default_title"));
            info.Popup = InsidePath(Json.Str(action, "default_popup"));
            info.Options = InsidePath(Json.Str(Json.Obj(Json.Get(manifest, "options_ui")), "page") ?? Json.Str(manifest, "options_page"));
            info.IconFile = Picture(info.Folder, Json.Get(action, "default_icon")) ?? Picture(info.Folder, Json.Get(manifest, "icons"));

            // What it asks for. An older manifest writes websites among its permissions; a newer one lists them apart.
            bool isSite(string p) => p.Contains("://") || p == "<all_urls>" || p.StartsWith("*.");
            var asked = Strings(Json.Get(manifest, "permissions"));
            info.Permissions = asked.Where(p => !isSite(p)).Distinct().ToList();
            var sites = asked.Where(isSite).Concat(Strings(Json.Get(manifest, "host_permissions")));
            foreach (var script in Json.Arr(Json.Get(manifest, "content_scripts")).Select(Json.Obj).Where(s => s != null))
                sites = sites.Concat(Strings(Json.Get(script, "matches")));
            info.Sites = sites.Distinct().ToList();
            info.WantsRecording = info.Permissions.Contains("tabCapture");
            info.KeyId = ExtensionStore.IdOfKey(Json.Str(manifest, "key"));
            string update = Json.Str(manifest, "update_url") ?? "";
            info.Store = update.Contains("google.com") ? "Chrome Web Store" : update.Contains("microsoft.com") ? "Microsoft Edge Add-ons" : null;
        }

        static List<string> Strings(object value) =>
            Json.Arr(value).OfType<string>().Select(s => s.Trim()).Where(s => s.Length > 0 && s.Length < 300).ToList();

        // The extension's picture: the one closest above 32 pixels of those it names (or the only one), when it is a
        // file of its own.
        static string Picture(string folder, object named)
        {
            var candidates = new List<KeyValuePair<int, string>>();
            if (named is string one) candidates.Add(new KeyValuePair<int, string>(32, one));
            else if (Json.Obj(named) is Dictionary<string, object> sizes)
                foreach (var pair in sizes)
                    if (pair.Value is string file && int.TryParse(pair.Key, out int size)) candidates.Add(new KeyValuePair<int, string>(size, file));
            foreach (var c in candidates.OrderBy(c => c.Key >= 32 ? 0 : 1).ThenBy(c => c.Key >= 32 ? c.Key : -c.Key))
            {
                string inside = InsidePath(c.Value);
                if (inside == null) continue;
                string full = Path.Combine(folder, inside.Replace('/', '\\'));
                if (File.Exists(full) && new FileInfo(full).Length < 4 * 1024 * 1024) return full;
            }
            return null;
        }

        // The extension's own words in the interface's language, else in the language it calls its default, else in
        // English: the names in lower case, each with its text.
        static Dictionary<string, string> Words(string folder, string defaultLocale)
        {
            var words = new Dictionary<string, string>();
            foreach (var locale in new[] { "en", (defaultLocale ?? "").Trim(), L.Id }.Where(l => !string.IsNullOrEmpty(l) && l.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')).Distinct())
            {
                try
                {
                    string file = Path.Combine(folder, "_locales", locale.Replace('-', '_'), "messages.json");
                    if (!File.Exists(file) || new FileInfo(file).Length > 4 * 1024 * 1024) continue;
                    foreach (var pair in Json.Obj(Json.Parse(File.ReadAllText(file))) ?? new Dictionary<string, object>())
                        if (Json.Str(Json.Obj(pair.Value), "message") is string message) words[pair.Key.ToLowerInvariant()] = message;
                }
                catch { }
            }
            return words;
        }

        // A page of the extension as it is written in its manifest, when that is a plain path inside the extension.
        static string InsidePath(string path)
        {
            path = (path ?? "").Trim().TrimStart('/');
            return path.Length == 0 || path.Contains("://") || path.Contains("..") || path.Contains("\\") ? null : path;
        }

        // Where the manifest is: in the folder itself, or one level down (a browser keeps an installed extension as
        // <id>\<version>\…; of several versions the newest is taken).
        static string RootOf(string folder)
        {
            if (File.Exists(Path.Combine(folder, "manifest.json"))) return folder;
            return Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)).ThenByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        // Unpacks a .zip, or a .crx (the same, behind a header that says how long the header is). Nothing is written
        // outside the folder, whatever the names inside the file say, and a file that would unpack to something
        // huge is refused.
        public static void Unpack(string file, string target)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int start = 0;
            if (bytes.Length > 16 && bytes[0] == 'C' && bytes[1] == 'r' && bytes[2] == '2' && bytes[3] == '4')
            {
                uint version = BitConverter.ToUInt32(bytes, 4);
                long skip = version == 2 ? 16L + BitConverter.ToUInt32(bytes, 8) + BitConverter.ToUInt32(bytes, 12)
                    : version == 3 ? 12L + BitConverter.ToUInt32(bytes, 8)
                    : throw new InvalidDataException("an unknown kind of .crx file");
                if (skip >= bytes.Length) throw new InvalidDataException("the .crx file is cut short");
                start = (int)skip;
            }
            string staging = target + ".unpacking";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            try
            {
                string root = Path.GetFullPath(staging).TrimEnd('\\') + "\\";
                using (var stream = new MemoryStream(bytes, start, bytes.Length - start, false))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count > MostFiles || zip.Entries.Sum(e => e.Length) > MostBytes) throw new InvalidDataException("it is too large");
                    foreach (var entry in zip.Entries)
                    {
                        string path = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', '\\')));
                        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("it names a file outside its own folder");
                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(path); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var from = entry.Open())
                        using (var to = File.Create(path)) from.CopyTo(to);
                    }
                }
                if (Directory.Exists(target)) Directory.Delete(target, true);   // what was unpacked from the older file
                Directory.Move(staging, target);
                Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            }
            catch
            {
                try { Directory.Delete(staging, true); } catch { }
                throw;
            }
        }
    }

    // Getting an extension from its page in a web store. A store's own "Add" button needs parts of a browser that
    // the engine does not have in an app like this one, so IGYO fetches the extension's package itself, from the
    // address the store's own browser gets it from, and puts it into the extensions folder like any other. Nobody
    // documents those addresses for other programs: they are the ones in common use, and may stop working.
    // Only asked for when the person chooses "Add the extension on this page"; the request carries the
    // extension's id and the engine's version, nothing else.
    static class ExtensionStore
    {
        public sealed class StorePage
        {
            public string Store;    // the store's name, as it is shown
            public string Id;       // the extension's id: 32 letters from a to p
            public string Name;     // the extension's name as the address writes it ("typing-sounds"), "" when it has none
            public bool Google;
        }

        public static bool IsId(string id) => id != null && id.Length == 32 && id.All(c => c >= 'a' && c <= 'p');

        // https://chromewebstore.google.com/detail/<name>/<id>, https://chrome.google.com/webstore/detail/<name>/<id>,
        // https://microsoftedge.microsoft.com/addons/detail/<name>/<id>   (the name may be missing, more may follow)
        public static StorePage FromAddress(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) || u.Scheme != "https") return null;
            var parts = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int detail = Array.IndexOf(parts, "detail");
            if (detail < 0) return null;
            string id = parts.Skip(detail + 1).Take(2).FirstOrDefault(IsId);
            if (id == null) return null;
            string name = parts.Length > detail + 1 && parts[detail + 1] != id ? Uri.UnescapeDataString(parts[detail + 1]) : "";
            string host = u.Host.ToLowerInvariant();
            if ((host == "chromewebstore.google.com" && detail == 0) || (host == "chrome.google.com" && detail == 1 && parts[0] == "webstore"))
                return new StorePage { Store = "Chrome Web Store", Id = id, Name = name, Google = true };
            if (host == "microsoftedge.microsoft.com" && detail == 1 && parts[0] == "addons")
                return new StorePage { Store = "Microsoft Edge Add-ons", Id = id, Name = name };
            return null;
        }

        // ----- the store's own "Add" and "Remove" buttons -----
        // A store's page asks the browser to install an extension through a function the engine gives pages at the
        // store's address (chrome.webstorePrivate.beginInstallWithManifest3). In an app like this one the engine
        // takes the call, shows a question of its own, and nothing ever comes of it. So a page at a store's address
        // is given a few lines of script before its own run (PageScript): the function then asks IGYO instead,
        // by a request to an address on the store's own name that never leaves the PC (the tab answers it itself,
        // see BrowserTab), and IGYO does what "Add the extension on this page" does. When IGYO cannot be asked
        // that way, the engine's own function is called as before.
        // The same button reads "Remove" once the extension is there, and then calls chrome.management.uninstall
        // (seen on the real Chrome Web Store page): the engine answers that with a question in its own words
        // ("Remove … from Microsoft Edge?"), which knows nothing of IGYO's folder. That call is passed to IGYO
        // the same way, and IGYO does what "Remove from IGYO" does. A no is told to the page as the engine
        // tells it: through chrome.runtime.lastError, in the engine's words.
        public const string AddPath = "/.igyo/add-extension";
        public const string RemovePath = "/.igyo/remove-extension";
        public static readonly string[] Hosts = { "chromewebstore.google.com", "chrome.google.com", "microsoftedge.microsoft.com" };

        public static StorePage ForHost(string host, string id)
        {
            host = (host ?? "").ToLowerInvariant();
            if (!IsId(id) || Array.IndexOf(Hosts, host) < 0) return null;
            return host == "microsoftedge.microsoft.com" ? new StorePage { Store = "Microsoft Edge Add-ons", Id = id, Name = "" } : new StorePage { Store = "Chrome Web Store", Id = id, Name = "", Google = true };
        }

        // Whether a request is one PageScript makes, which button it stands for ("add" or "remove"), and for which
        // extension (with the name the store's page gave, when adding).
        public static bool IsButtonRequest(string uri, out string host, out string what, out string id, out string name)
        {
            host = what = id = name = null;
            if (!Uri.TryCreate(uri ?? "", UriKind.Absolute, out var u) || u.Scheme != "https") return false;
            what = u.AbsolutePath == AddPath ? "add" : u.AbsolutePath == RemovePath ? "remove" : null;
            if (what == null) return false;
            host = u.Host.ToLowerInvariant();
            if (Array.IndexOf(Hosts, host) < 0) return false;
            var query = InternalAssets.Query(uri);
            query.TryGetValue("id", out id);
            query.TryGetValue("name", out name);
            name = Ui.Trim(new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim(), 60);
            return true;
        }

        public static bool IsAddRequest(string uri, out string host, out string id, out string name)
        {
            bool one = IsButtonRequest(uri, out host, out string what, out id, out name);
            return one && what == "add";
        }

        public static bool IsRemoveRequest(string uri, out string host, out string id)
        {
            bool one = IsButtonRequest(uri, out host, out string what, out id, out _);
            return one && what == "remove";
        }

        public const string PageScript = @"(() => {
  try {
    const host = location.hostname;
    if (window.top !== window || (host !== 'chromewebstore.google.com' && host !== 'chrome.google.com' && host !== 'microsoftedge.microsoft.com')) return;
    const p = window.chrome && window.chrome.webstorePrivate;
    if (!p || typeof p.beginInstallWithManifest3 !== 'function') return;
    const begin = p.beginInstallWithManifest3.bind(p);
    const added = new Set();
    const ask = (id, name) => fetch('" + AddPath + @"?id=' + encodeURIComponent(id || '') + '&name=' + encodeURIComponent(name || ''), { method: 'POST', cache: 'no-store', credentials: 'omit' })
      .then((r) => (r.ok ? r.text() : Promise.reject(new Error('not answered'))))
      .then((said) => { if (said === 'added') { added.add(id); return ''; } if (said === 'cancelled') return 'user_cancelled'; if (said === 'failed') return 'install_error'; throw new Error('not answered'); });
    p.beginInstallWithManifest3 = function (details, callback) {
      const d = details || {};
      const result = ask(d.id, d.localizedName).catch(() => new Promise((done, failed) => { try { begin(details, done); } catch (e) { failed(e); } }));
      if (typeof callback === 'function') { result.then((v) => callback(v), () => callback('unknown_error')); return undefined; }
      return result;
    };
    for (const name of ['completeInstall', 'completeInstallWithCV']) {
      if (typeof p[name] !== 'function') continue;
      const original = p[name].bind(p);
      p[name] = function (id, ...rest) {
        if (!added.has(id)) return original(id, ...rest);   // not one IGYO added: the engine's own way
        const callback = typeof rest[rest.length - 1] === 'function' ? rest[rest.length - 1] : null;
        if (callback) { Promise.resolve().then(() => callback()); return undefined; }
        return Promise.resolve();
      };
    }
    // The same button, once the extension is there: 'Remove'. IGYO is asked; an extension that is not one of
    // IGYO's, or a request that cannot be made, goes the engine's own way with what the page passed.
    const m = window.chrome.management;
    if (m && typeof m.uninstall === 'function') {
      const uninstall = m.uninstall.bind(m);
      const runtime = window.chrome.runtime;
      // The engine tells a page that something went wrong through chrome.runtime.lastError, which is there
      // only while the page's function runs: the same is done here.
      const tell = (callback, message) => {
        let made = false;
        if (message && runtime) { try { Object.defineProperty(runtime, 'lastError', { configurable: true, enumerable: true, value: { message } }); made = true; } catch (e) {} }
        try { callback(); } finally { if (made) { try { delete runtime.lastError; } catch (e) {} } }
      };
      m.uninstall = function (id, ...rest) {
        const callback = typeof rest[rest.length - 1] === 'function' ? rest[rest.length - 1] : null;
        const asked = fetch('" + RemovePath + @"?id=' + encodeURIComponent(id || ''), { method: 'POST', cache: 'no-store', credentials: 'omit' })
          .then((r) => (r.ok ? r.text() : 'unknown'), () => 'unknown');
        const error = (said) => (said === 'removed' ? '' : 'Extension ' + id + (said === 'cancelled' ? ' uninstall canceled by user.' : ' could not be removed.'));
        if (callback) {
          asked.then((said) => { if (said === 'removed' || said === 'cancelled' || said === 'failed') tell(callback, error(said)); else uninstall(id, ...rest); });
          return undefined;
        }
        return asked.then((said) => {
          if (said === 'removed') return undefined;
          if (said === 'cancelled' || said === 'failed') throw new Error(error(said));
          return uninstall(id, ...rest);
        });
      };
    }
  } catch (e) {}
})();";

        // What to call the extension on a store page before anything of it has been fetched: the page's title without
        // the store's own name behind it ("Typing sounds - Chrome Web Store"); when the title is not of that kind
        // (a page that is still loading, or did not load, is titled with the site's name), the name the address
        // writes; failing that, whatever title there is that is not an address; and last the id.
        public static string NameFor(string title, StorePage page)
        {
            title = (title ?? "").Trim();
            int cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
            bool store = cut > 0 && (title.IndexOf("Web Store", cut, StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("Edge Add", cut, StringComparison.OrdinalIgnoreCase) >= 0);
            if (store) return Ui.Trim(title.Substring(0, cut).Trim(), 60);
            string name = (page.Name ?? "").Replace('-', ' ').Replace('_', ' ').Trim();
            if (name.Length > 0) return Ui.Trim(char.ToUpperInvariant(name[0]) + name.Substring(1), 60);
            bool address = title.Contains("://") || (title.IndexOf(' ') < 0 && title.IndexOf('.') > 0);
            return title.Length > 0 && !address ? Ui.Trim(title, 60) : page.Id;
        }

        // Where the store hands out the package of an extension (it answers with the way to the file itself).
        public static string PackageAddress(StorePage page, string engineVersion)
        {
#if DEBUG
            var test = Environment.GetEnvironmentVariable("NSB_EXTENSION_STORE_URL");   // test builds: a stand-in for the stores, on this PC
            if (!string.IsNullOrEmpty(test)) return test.Replace("{id}", page.Id).Replace("{store}", page.Google ? "google" : "microsoft");
#endif
            string asked = Uri.EscapeDataString("id=" + page.Id + "&installsource=ondemand&uc");
            return page.Google
                ? "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=" + Uri.EscapeDataString(string.IsNullOrEmpty(engineVersion) ? "140.0.0.0" : engineVersion) + "&acceptformat=crx2,crx3&x=" + asked
                : "https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&prod=chromiumcrx&prodchannel=&x=" + asked;
        }

        const int MostBytes = 150 * 1024 * 1024;

        public static async Task<byte[]> DownloadAsync(StorePage page, string engineVersion)
        {
            var handler = new System.Net.Http.HttpClientHandler { UseCookies = false };
            using (var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" +
                    (string.IsNullOrEmpty(engineVersion) ? "140.0.0.0" : engineVersion.Split('.')[0] + ".0.0.0") + " Safari/537.36");
                using (var response = await http.GetAsync(PackageAddress(page, engineVersion), System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 204) throw new InvalidDataException(L.T("The store has no package of this extension for this browser."));
                    if (!response.IsSuccessStatusCode) throw new InvalidDataException(L.T("The store answered with an error ({0}).", (int)response.StatusCode));
                    if (response.Content.Headers.ContentLength > MostBytes) throw new InvalidDataException(L.T("The package is too large."));
                    using (var from = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var to = new MemoryStream())
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            to.Write(buffer, 0, read);
                            if (to.Length > MostBytes) throw new InvalidDataException(L.T("The package is too large."));
                        }
                        var bytes = to.ToArray();
                        if (!IsPackage(bytes)) throw new InvalidDataException(L.T("What the store sent is not an extension's package."));
                        return bytes;
                    }
                }
            }
        }

        static bool IsCrx(byte[] b) => b != null && b.Length > 16 && b[0] == 'C' && b[1] == 'r' && b[2] == '2' && b[3] == '4';
        static bool IsZip(byte[] b) => b != null && b.Length > 30 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4;
        public static bool IsPackage(byte[] bytes) => IsCrx(bytes) || IsZip(bytes);

        // The id a package carries in its own header (packages in today's format do), as the 32 letters; null when
        // none is found there. Used to refuse a package that says it is another extension than the one asked for.
        public static string IdInside(byte[] crx)
        {
            try
            {
                if (!IsCrx(crx) || BitConverter.ToUInt32(crx, 4) != 3) return null;
                int end = 12 + (int)BitConverter.ToUInt32(crx, 8);
                if (end > crx.Length) return null;
                var signed = Field(crx, 12, end, 10000);            // the part of the header that is signed…
                if (signed == null) return null;
                var id = Field(crx, signed.Item1, signed.Item2, 1);   // …holds the id, as 16 bytes
                if (id == null || id.Item2 - id.Item1 != 16) return null;
                var text = new System.Text.StringBuilder();
                for (int i = id.Item1; i < id.Item2; i++) text.Append((char)('a' + (crx[i] >> 4))).Append((char)('a' + (crx[i] & 15)));
                return text.ToString();
            }
            catch { return null; }
        }

        // The key the extension's maker signed the package with, as a browser writes it into the manifest of an
        // extension it installs ("key"): the extension's id is made from it. A package carries more than one key
        // (the store signs it too); the maker's is the one the id in the header was made from. Null when not found.
        public static string KeyInside(byte[] crx)
        {
            try
            {
                if (!IsCrx(crx) || BitConverter.ToUInt32(crx, 4) != 3) return null;
                int end = 12 + (int)BitConverter.ToUInt32(crx, 8);
                if (end > crx.Length) return null;
                var signed = Field(crx, 12, end, 10000);
                var id = signed == null ? null : Field(crx, signed.Item1, signed.Item2, 1);
                if (id == null || id.Item2 - id.Item1 != 16) return null;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    foreach (int kind in new[] { 2, 3 })   // what was signed with RSA keys, then with elliptic-curve ones
                        foreach (var proof in Fields(crx, 12, end, kind))
                        {
                            var key = Field(crx, proof.Item1, proof.Item2, 1);
                            if (key == null) continue;
                            var hash = sha.ComputeHash(crx, key.Item1, key.Item2 - key.Item1);
                            bool same = true;
                            for (int i = 0; i < 16 && same; i++) same = hash[i] == crx[id.Item1 + i];
                            if (same) return Convert.ToBase64String(crx, key.Item1, key.Item2 - key.Item1);
                        }
                return null;
            }
            catch { return null; }
        }

        // How the engine makes an extension's id: from the key in its manifest when it has one, and otherwise, for
        // an unpacked extension, from the path of its folder. Either way the first 16 bytes of a SHA-256, written
        // with the letters a to p. (Both were compared with the ids the engine itself gave.)
        static string IdOfBytes(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var text = new System.Text.StringBuilder();
                for (int i = 0; i < 16; i++) text.Append((char)('a' + (hash[i] >> 4))).Append((char)('a' + (hash[i] & 15)));
                return text.ToString();
            }
        }

        public static string IdOfKey(string key)
        {
            try { return string.IsNullOrWhiteSpace(key) ? null : IdOfBytes(Convert.FromBase64String(key.Trim())); }
            catch { return null; }
        }

        public static string IdOfPath(string folder)
        {
            folder = (folder ?? "").TrimEnd('\\');
            if (folder.Length > 1 && folder[1] == ':') folder = char.ToUpperInvariant(folder[0]) + folder.Substring(1);
            return folder.Length == 0 ? null : IdOfBytes(System.Text.Encoding.Unicode.GetBytes(folder));
        }

        // The start of a package file: its header, which is all that IdInside and KeyInside read. Null for a file
        // that is not a package in today's format.
        public static byte[] HeaderOf(string file)
        {
            try
            {
                using (var stream = File.OpenRead(file))
                {
                    var start = new byte[12];
                    if (stream.Read(start, 0, 12) != 12 || !(start[0] == 'C' && start[1] == 'r' && start[2] == '2' && start[3] == '4') || BitConverter.ToUInt32(start, 4) != 3) return null;
                    long length = BitConverter.ToUInt32(start, 8);
                    if (length <= 0 || length > 1024 * 1024 || 12 + length > stream.Length) return null;
                    var all = new byte[12 + length + 8];   // (a few bytes more: what reads it takes a package to be longer than its header)
                    Array.Copy(start, all, 12);
                    int got = 0;
                    while (got < length) { int n = stream.Read(all, 12 + got, (int)length - got); if (n <= 0) return null; got += n; }
                    return all;
                }
            }
            catch { return null; }
        }

        // The id a package file carries (asked again only when the file changed).
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> idsOfFiles = new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.OrdinalIgnoreCase);

        public static string IdOfFile(string file)
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                lock (idsOfFiles)
                    if (idsOfFiles.TryGetValue(file, out var known) && known.Key == written) return known.Value;
                string id = IdInside(HeaderOf(file));
                lock (idsOfFiles) idsOfFiles[file] = new KeyValuePair<DateTime, string>(written, id);
                return id;
            }
            catch { return null; }
        }

        // Where the bytes of a field are, in a message written the way these headers are (each part: a number that
        // says which field and of what kind, then the part itself). Null when the field is not there.
        static Tuple<int, int> Field(byte[] b, int at, int end, int wanted) => Fields(b, at, end, wanted, true).FirstOrDefault();

        // Every place the field stands at (a header can hold one field several times over).
        static List<Tuple<int, int>> Fields(byte[] b, int at, int end, int wanted, bool firstOnly = false)
        {
            var found = new List<Tuple<int, int>>();
            while (at < end)
            {
                long key = Number(b, ref at, end);
                int field = (int)(key >> 3), kind = (int)(key & 7);
                if (kind == 0) Number(b, ref at, end);
                else if (kind == 1) at += 8;
                else if (kind == 5) at += 4;
                else if (kind == 2)
                {
                    long length = Number(b, ref at, end);
                    if (length < 0 || at + length > end) break;
                    if (field == wanted) { found.Add(Tuple.Create(at, at + (int)length)); if (firstOnly) break; }
                    at += (int)length;
                }
                else break;
            }
            return found;
        }

        static long Number(byte[] b, ref int at, int end)
        {
            long value = 0;
            for (int shift = 0; shift < 64 && at < end; shift += 7)
            {
                byte one = b[at++];
                value |= (long)(one & 0x7F) << shift;
                if ((one & 0x80) == 0) return value;
            }
            throw new InvalidDataException();
        }

        // Puts the package into the extensions folder under the extension's id (replacing an older one of that
        // name, which is how a newer version of the same extension is taken).
        public static string Save(StorePage page, byte[] bytes, string dir = null)
        {
            dir = dir ?? ExtensionFolder.Dir;
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, page.Id + (IsCrx(bytes) ? ".crx" : ".zip"));
            string temp = file + ".part";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            return file;
        }
    }

    // The extensions of one profile: the engine keeps them per profile, and remembers the ones it was given.
    sealed class ProfileExtensions
    {
        static readonly Dictionary<string, ProfileExtensions> all = new Dictionary<string, ProfileExtensions>();

        public static ProfileExtensions For(string profileId)
        {
            if (!all.TryGetValue(profileId, out var set)) all[profileId] = set = new ProfileExtensions(profileId);
            return set;
        }

        readonly string profileId;
        Task loading;
        bool stale;   // the folder changed in another profile's window: looked at again the next time it is asked for

        ProfileExtensions(string profileId) { this.profileId = profileId; }

        // An extension was taken out in one profile's window. Its files served every profile, so every other profile
        // looks at the folder again the next time it is asked (then takes it out of the engine too).
        public static void LookAgainExcept(string profileId)
        {
            foreach (var pair in all)
                if (!string.Equals(pair.Key, profileId, StringComparison.OrdinalIgnoreCase)) pair.Value.stale = true;
        }

        public List<ExtensionInfo> List { get; private set; } = new List<ExtensionInfo>();

        // Which extensions this profile was given from the folder (the engine's id of each, and its folder), so that
        // one whose folder is gone can be taken out of the profile again, and nothing else ever is.
        string RecordFile => Path.Combine(ProfileStore.DirFor(profileId), "extensions.json");

        Dictionary<string, string> ReadRecord()
        {
            var record = new Dictionary<string, string>();
            moved.Clear();
            try
            {
                if (File.Exists(RecordFile))
                    foreach (var pair in Json.Obj(Json.Parse(File.ReadAllText(RecordFile))) ?? new Dictionary<string, object>())
                        if (pair.Value is string folder)
                        {
                            var now = MovedTo(folder);
                            if (now != null) moved.Add(pair.Key);
                            record[pair.Key] = now ?? folder;
                        }
            }
            catch { }
            return record;
        }

        // The ids of the extensions whose folder ReadRecord found at a new place.
        readonly HashSet<string> moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The extensions folder moves with the data folder (the first start after the browser's rename moves it from
        // the earlier name's place, see AppPaths). A recorded folder that is no longer where it was, but stands in the
        // present extensions folder under the same name, is that folder. Null when the folder has not moved.
        internal static string MovedTo(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || Directory.Exists(folder)) return null;
                string root = Path.GetFullPath(ExtensionFolder.Dir).TrimEnd('\\');
                if (folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                int at = folder.LastIndexOf("\\Extensions\\", StringComparison.OrdinalIgnoreCase);
                if (at < 0) return null;
                string candidate = root + folder.Substring(at + "\\Extensions".Length);
                return Directory.Exists(candidate) ? candidate.TrimEnd('\\') : null;
            }
            catch { return null; }
        }

        static bool Same(string a, string b) => string.Equals((a ?? "").TrimEnd('\\'), (b ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // Once for a profile after a start (again: when asked to look for new ones).
        public Task EnsureLoadedAsync(CoreWebView2Profile profile, bool again = false)
        {
            if (profile == null && BrowserEngine.ExtensionsOn) return loading ?? Task.CompletedTask;   // no view of this profile yet: asked again later
            if (loading == null || ((again || stale) && loading.IsCompleted))
            {
                stale = false;
                loading = LoadAsync(profile, loading == null);
            }
            return loading;
        }

        async Task LoadAsync(CoreWebView2Profile profile, bool firstLook)
        {
            try
            {
                var found = await Task.Run(() => ExtensionFolder.Find());
                if (!BrowserEngine.ExtensionsOn || profile == null)
                {
                    List = found;   // only named: the engine was started without its extension support
                    return;
                }
                var record = ReadRecord();
                var installed = (await profile.GetBrowserExtensionsAsync()).ToList();
                var takenOut = new HashSet<string>();
                bool changed = moved.Count > 0;   // (folders found at their new place are written down at their new place)
                foreach (var pair in record.ToList())
                {
                    if (found.Any(x => x.Error == null && Same(x.Folder, pair.Value))) continue;
                    var gone = installed.FirstOrDefault(h => h.Id == pair.Key);
                    if (gone != null)
                    {
                        // Its folder is gone. It stays in the record until the engine has let it go, so that it is
                        // tried again at the next look (dropped from the record, the engine would keep it for good).
                        try { await TakeOut(gone); takenOut.Add(gone.Id); }
                        catch (Exception ex)
                        {
                            // At the profile's first look in this run the engine loaded the profile after the folder
                            // was gone, so it could not load this one and it is not running: the engine then refuses
                            // to take it out, and it is dropped from the record all the same. Later on it may still be
                            // running (taken away while IGYO ran): kept, and tried again at the next start.
                            if (!firstLook) { ErrorLog.Write(ex); continue; }
                        }
                    }
                    record.Remove(pair.Key);
                    changed = true;
                }
                foreach (var x in found.Where(x => x.Error == null))
                {
                    string id = record.FirstOrDefault(p => Same(p.Value, x.Folder)).Key;
                    var handle = id == null ? null : installed.FirstOrDefault(h => h.Id == id);
                    if (id != null && x.KeyId != null && id != x.KeyId && id == ExtensionStore.IdOfPath(x.Folder))
                    {
                        // The profile was given this extension before its manifest had its key (see ExtensionFolder),
                        // under the id made from its folder's path. It is taken out and added anew below, under its
                        // own id; what it had stored under the old one goes with it.
                        if (handle != null) try { await TakeOut(handle); } catch { }
                        record.Remove(id);
                        changed = true;
                        handle = installed.FirstOrDefault(h => h.Id == x.KeyId);   // (the engine may have taken it in under the new id by itself)
                        id = handle?.Id;
                        if (handle != null) record[handle.Id] = x.Folder;
                    }
                    if (handle != null && moved.Contains(handle.Id))
                    {
                        // Its folder moved with the data folder: the engine still looks for the files at the old place.
                        // Given the folder at its new place, it takes the extension in from there under the same id,
                        // with everything the extension had stored.
                        try
                        {
                            handle = await profile.AddBrowserExtensionAsync(x.Folder);
                            record.Remove(id);
                            record[handle.Id] = x.Folder;
                            changed = true;
                            x.Fresh = true;
                        }
                        catch (Exception ex) { ErrorLog.Write(ex); }
                    }
                    else if (handle != null && x.Unpacked && handle.IsEnabled)
                    {
                        // A newer package took the place of the files the engine is running. Switched off and on, the
                        // engine reads them again, and the extension keeps what it had stored (taking it out of the
                        // profile and putting it back would lose that).
                        try
                        {
                            await handle.EnableAsync(false);
                            await handle.EnableAsync(true);
                            x.Fresh = true;
                        }
                        catch { }
                    }
                    if (handle == null)
                    {
                        try
                        {
                            handle = await profile.AddBrowserExtensionAsync(x.Folder);
                            if (id != null) record.Remove(id);
                            record[handle.Id] = x.Folder;
                            changed = true;
                            x.Fresh = true;
                        }
                        catch (Exception ex)
                        {
                            x.Error = L.T("The engine did not take it: ") + ex.Message;
                            continue;
                        }
                    }
                    x.Handle = handle;
                    x.Id = handle.Id;
                    x.Enabled = handle.IsEnabled;
                    if (!string.IsNullOrWhiteSpace(handle.Name)) x.Name = handle.Name;
                }
                // What the engine still has in this profile that IGYO lists nowhere: an extension whose record was
                // dropped while the engine kept it. Up to 1.0.5 that happened when taking one out failed; its files
                // were moved away all the same, and its scripts went on running in pages without them (Ambient light
                // for YouTube then said "Failed to load a resource" on every YouTube page). Every extension in the
                // engine was given to it by IGYO from the extensions folder, so one that is in neither is taken out.
                foreach (var h in installed)
                {
                    string id;
                    try { id = h.Id; } catch { continue; }
                    if (takenOut.Contains(id) || record.ContainsKey(id) || refusedOrphans.Contains(profileId + "|" + id)) continue;
                    if (found.Any(x => x.Id == id || x.KeyId == id || (x.Folder != null && ExtensionStore.IdOfPath(x.Folder) == id))) continue;
                    try
                    {
                        await TakeOut(h);
                        takenOut.Add(id);
#if DEBUG
                        // Test builds: what was taken out this way, for the checks.
                        try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug-extensions.txt"), "orphan taken out: " + profileId + " " + id + "\r\n"); } catch { }
#endif
                    }
                    catch
                    {
                        // The engine refuses to take out one it could not load (its folder was already gone when it loaded the
                        // profile): such a one does not run at all. Not asked again in this run, and not logged.
                        refusedOrphans.Add(profileId + "|" + id);
                    }
                }
                if (changed) try { FileUtil.WriteAtomic(RecordFile, Json.Write(record)); } catch { }
                List = found;
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }

        // Takes an extension out of the engine's profile. An extension can name a page to be opened when it is
        // removed (a "why did you leave" page), and the engine opens that page in a browser window of its own,
        // which stands apart from IGYO and keeps the engine running after IGYO is closed: that window is closed
        // as it appears (see EngineWindows), so no such page is shown.
        static Task TakeOut(CoreWebView2BrowserExtension extension) => EngineWindows.Quietly(() => extension.RemoveAsync());

        static readonly HashSet<string> refusedOrphans = new HashSet<string>();   // "profile|id": not tried again in this run

        // The engine's handle on one of this profile's extensions, asked for anew, through a view of the profile
        // that is open now. A handle kept from when the extensions were loaded can stop working: it came from the
        // first tab of a window, and once that tab is closed every call on it fails ("cannot be accessed after the
        // WebView2 control is disposed"). 1.0.5 took extensions out and switched them on and off through such kept
        // handles, and those calls were seen failing in use (also as 0x8000FFFF, "Class not registered").
        static async Task<CoreWebView2BrowserExtension> FreshAsync(CoreWebView2Profile live, string id)
        {
            if (live == null || string.IsNullOrEmpty(id)) throw new InvalidOperationException("No open view of the profile.");
            return (await live.GetBrowserExtensionsAsync()).FirstOrDefault(h => h.Id == id);
        }

        // Takes an extension out of the engine (what it stored goes with it), tried a second time a moment later.
        // True once it is out, also when the engine did not have it at all.
        static async Task<bool> TakeOutAsync(CoreWebView2Profile live, string id)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var handle = await FreshAsync(live, id);
                    if (handle == null) return true;
                    await TakeOut(handle);
                    return true;
                }
                catch (Exception ex) { ErrorLog.Write(ex); }
                await Task.Delay(400);
            }
            return false;
        }

        // What came of taking an extension out.
        public sealed class Removal
        {
            public string Left;        // what could not be moved to the Recycle Bin
            public bool SwitchedOff;   // the engine would not let it go: it is switched off, and taken out at the next start
            public bool Untouched;     // the engine would not even switch it off: its files stay, all of it goes at the next start
        }

        // Takes an extension out of the profile (what it stored goes with it) and its files out of the extensions
        // folder, to the Recycle Bin. The files only go once the engine has stopped running it: moved away while it
        // runs, its scripts would go on in pages without them. Should the engine refuse, it stays in the record (and,
        // when not even switched off, its files are marked), and the next start of IGYO finishes the job.
        public async Task<Removal> RemoveAsync(ExtensionInfo x, CoreWebView2Profile live)
        {
            var result = new Removal();
            if (x == null) return result;
            bool outOfEngine = x.Handle == null || await TakeOutAsync(live, x.Id);
            if (!outOfEngine)
            {
                result.SwitchedOff = await SwitchAsync(x, false, live);
                if (!result.SwitchedOff)
                {
                    result.Untouched = true;
                    ExtensionFolder.RemoveAtNextStart(PathsOf(x));
                    List = List.Where(e => e != x).ToList();
                    return result;
                }
            }
            if (outOfEngine)
            {
                try
                {
                    var record = ReadRecord();
                    bool changed = false;
                    foreach (var pair in record.ToList())
                        if (pair.Key == x.Id || Same(pair.Value, x.Folder)) changed |= record.Remove(pair.Key);
                    if (changed) FileUtil.WriteAtomic(RecordFile, Json.Write(record));
                }
                catch (Exception ex) { ErrorLog.Write(ex); }
            }
            foreach (var path in PathsOf(x))
                if (!await Task.Run(() => FileUtil.Recycle(path))) result.Left = path;
            List = List.Where(e => e != x).ToList();
            return result;
        }

        // What stands in the extensions folder for it: the folder, and the .zip or .crx it was unpacked from.
        static IEnumerable<string> PathsOf(ExtensionInfo x)
        {
            string root = Path.GetFullPath(ExtensionFolder.Dir).TrimEnd('\\') + "\\";
            return new[] { x.Entry, string.IsNullOrEmpty(x.Key) ? null : Path.Combine(ExtensionFolder.Dir, x.Key) }
                .Where(p => !string.IsNullOrEmpty(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && p.Length > root.Length)   // only ever what stands in the extensions folder
                .ToList();
        }

        public Task<bool> SetEnabledAsync(ExtensionInfo x, bool on, CoreWebView2Profile live) => SwitchAsync(x, on, live);

        // Switches it on or off through a fresh handle (see FreshAsync); the kept one is the fallback. True when done.
        static async Task<bool> SwitchAsync(ExtensionInfo x, bool on, CoreWebView2Profile live)
        {
            if (x?.Handle == null) return false;
            CoreWebView2BrowserExtension handle = null;
            try { handle = await FreshAsync(live, x.Id); } catch (Exception ex) { ErrorLog.Write(ex); }
            foreach (var h in new[] { handle, x.Handle }.Where(h => h != null).Distinct())
            {
                try
                {
                    await h.EnableAsync(on);
                    x.Handle = h;
                    x.Enabled = h.IsEnabled;
                    return true;
                }
                catch (Exception ex) { ErrorLog.Write(ex); }
            }
            return false;
        }
    }
}
