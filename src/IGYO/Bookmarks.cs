using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IGYO
{
    sealed class Bookmark
    {
        static int nextId;
        public readonly int Id = ++nextId;  // how the interface names this bookmark while the browser runs (never saved)
        public string Title = "";
        public string Url;                  // null for a folder
        public List<Bookmark> Children;     // null for a link
        public long Added;                  // Unix seconds
        public bool IsToolbar;              // only used while reading an HTML file

        public bool IsFolder => Children != null;
        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static Bookmark Folder(string title) => new Bookmark { Title = title ?? "", Children = new List<Bookmark>(), Added = Now };

        public static Bookmark Link(string title, string url) =>
            new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title.Trim(), Url = url, Added = Now };

        public int CountLinks() => IsFolder ? Children.Sum(c => c.CountLinks()) : 1;

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["title"] = Title, ["added"] = Added };
            if (IsFolder) d["children"] = Children.Select(c => (object)c.ToJson()).ToArray();
            else d["url"] = Url;
            return d;
        }

        public static Bookmark FromJson(object value)
        {
            var d = Json.Obj(value);
            if (d == null) return null;
            var b = new Bookmark { Title = Json.Str(d, "title") ?? "", Url = Json.Str(d, "url") };
            try { b.Added = Convert.ToInt64(Json.Get(d, "added") ?? 0L); } catch { }
            if (b.Url == null) b.Children = Json.Arr(Json.Get(d, "children")).Select(FromJson).Where(x => x != null).ToList();
            return b;
        }
    }

    sealed class BookmarkStore
    {
        string path;
        string known;   // what the file held when this store last read or wrote it
        FileSystemWatcher watcher;
        System.Threading.Timer settle;
        public Bookmark Bar = Bookmark.Folder("Bookmarks bar");
        public Bookmark Other = Bookmark.Folder("Other bookmarks");
        public event Action Changed;

        // The window thread, where a change made to the file from outside IGYO is applied (set by BrowserApp).
        public static System.Threading.SynchronizationContext Ui;

        public static BookmarkStore Load(string file)
        {
            var store = new BookmarkStore { path = file };
            if (file == null) return store;
            if (File.Exists(file))
            {
                string text = null;
                try
                {
                    text = File.ReadAllText(file);
                    if (!Parse(text, out var bar, out var other)) throw new InvalidDataException();
                    store.Bar = bar;
                    store.Other = other;
                    store.known = text;
                }
                catch
                {
                    // Keep the unreadable file instead of overwriting it later.
                    try { File.Copy(file, file + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
                }
            }
            store.Watch();
            return store;
        }

        static bool Parse(string text, out Bookmark bar, out Bookmark other)
        {
            bar = other = null;
            var root = Json.Obj(Json.Parse(text));
            if (root == null) return false;
            bar = Bookmark.FromJson(Json.Get(root, "bar"));
            other = Bookmark.FromJson(Json.Get(root, "other"));
            if (bar == null || !bar.IsFolder) bar = Bookmark.Folder("Bookmarks bar");
            if (other == null || !other.IsFolder) other = Bookmark.Folder("Other bookmarks");
            return true;
        }

        public void Save()
        {
            if (path == null) return;
            var text = Json.Write(new Dictionary<string, object> { ["version"] = 1, ["bar"] = Bar.ToJson(), ["other"] = Other.ToJson() });
            known = text;
            FileUtil.WriteAtomic(path, text);
        }

        // The file can be changed while IGYO runs, by another program or by hand (while this profile's windows are
        // open or not). IGYO reads it again then, so the bar and the bookmarks page show it at once, and IGYO's next
        // change does not write over it. What IGYO wrote itself is told apart by its content.
        void Watch()
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler changed = (s, e) => Later();
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Renamed += (s, e) => Later();
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { ErrorLog.WriteOnce("bookmarks-watch", ex); }
        }

        // Writes come in several steps: the file is looked at once it has been quiet for a moment.
        void Later()
        {
            lock (this)
            {
                if (settle == null) settle = new System.Threading.Timer(_ => ReadAgain(), null, 400, System.Threading.Timeout.Infinite);
                else settle.Change(400, System.Threading.Timeout.Infinite);
            }
        }

        void ReadAgain(int attempt = 0)
        {
            string text;
            try
            {
                if (!File.Exists(path)) return;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                if (attempt < 5) { System.Threading.Thread.Sleep(200); ReadAgain(attempt + 1); }
                return;
            }
            catch { return; }
            var ui = Ui;
            if (ui == null) return;
            ui.Post(_ =>
            {
                if (text == known) return;   // what IGYO itself wrote (or already read)
                Bookmark bar, other;
                try { if (!Parse(text, out bar, out other)) return; }
                catch { return; }   // half written, or not bookmarks: the next change is looked at again
                Bar = bar;
                Other = other;
                known = text;
                try { Changed?.Invoke(); } catch (Exception ex) { ErrorLog.Write(ex); }
            }, null);
        }

        public void NotifyChanged()
        {
            try { Save(); } catch (Exception ex) { ErrorLog.WriteOnce("bookmarks", ex); }
            Changed?.Invoke();
        }

        public bool IsEmpty => Bar.Children.Count == 0 && Other.Children.Count == 0;
        public IEnumerable<Bookmark> Roots { get { yield return Bar; yield return Other; } }

        public static IEnumerable<Bookmark> Flatten(Bookmark node)
        {
            yield return node;
            if (!node.IsFolder) yield break;
            foreach (var child in node.Children)
                foreach (var x in Flatten(child)) yield return x;
        }

        public Bookmark FindByUrl(string url) => string.IsNullOrEmpty(url) ? null :
            Roots.SelectMany(Flatten).FirstOrDefault(b => !b.IsFolder && string.Equals(b.Url, url, StringComparison.Ordinal));

        // The two top folders are named by IGYO, so their names follow its language; every other name is the person's own.
        public string TitleOf(Bookmark node) => node == Bar ? L.T("Bookmarks bar") : node == Other ? L.T("Other bookmarks") : node?.Title ?? "";

        public Bookmark ParentOf(Bookmark node) => Roots.SelectMany(Flatten).FirstOrDefault(f => f.IsFolder && f.Children.Contains(node));

        public void Remove(Bookmark node) => ParentOf(node)?.Children.Remove(node);

        public bool Move(Bookmark node, Bookmark folder, int index = -1)
        {
            if (node == null || folder == null || !folder.IsFolder || Flatten(node).Contains(folder) || Roots.Contains(node)) return false;
            var parent = ParentOf(node);
            if (parent == null) return false;
            int old = parent.Children.IndexOf(node);
            parent.Children.RemoveAt(old);
            if (parent == folder && index > old) index--;
            if (index < 0 || index > folder.Children.Count) folder.Children.Add(node);
            else folder.Children.Insert(index, node);
            return true;
        }

        public IEnumerable<(Bookmark folder, int depth)> Folders()
        {
            foreach (var root in Roots)
                foreach (var x in FoldersOf(root, 0)) yield return x;
        }

        static IEnumerable<(Bookmark, int)> FoldersOf(Bookmark folder, int depth)
        {
            yield return (folder, depth);
            foreach (var child in folder.Children.Where(c => c.IsFolder))
                foreach (var x in FoldersOf(child, depth + 1)) yield return x;
        }

        // An empty store takes imported bookmarks as they are; otherwise they go into their own folder.
        public int Import(Bookmark bar, Bookmark other, string source)
        {
            int count = (bar?.CountLinks() ?? 0) + (other?.CountLinks() ?? 0);
            if (count == 0) return 0;
            if (IsEmpty)
            {
                if (bar != null) Bar.Children.AddRange(bar.Children);
                if (other != null) Other.Children.AddRange(other.Children);
            }
            else
            {
                var folder = Bookmark.Folder(L.T("Imported from {0}", source));
                if (bar != null && bar.Children.Count > 0)
                {
                    var copy = Bookmark.Folder(bar.Title);
                    copy.Children.AddRange(bar.Children);
                    folder.Children.Add(copy);
                }
                if (other != null) folder.Children.AddRange(other.Children);
                Other.Children.Add(folder);
            }
            NotifyChanged();
            return count;
        }
    }

    // The standard bookmarks file (Netscape HTML) that Chrome, Edge and Firefox import and export.
    static class BookmarkHtml
    {
        static readonly Regex Token = new Regex(
            @"<h3(?<ha>[^>]*)>(?<ht>.*?)</h3>|<a\s(?<aa>[^>]*)>(?<at>.*?)</a>|(?<open><dl[^>]*>)|(?<close></dl\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex Attribute = new Regex(@"([\w-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);
        static readonly Regex Tags = new Regex("<[^>]*>", RegexOptions.Compiled);

        public static void Parse(string html, out Bookmark bar, out Bookmark other)
        {
            var root = Bookmark.Folder("root");
            var stack = new Stack<Bookmark>();
            stack.Push(root);
            Bookmark pending = null;
            foreach (Match m in Token.Matches(html ?? ""))
            {
                if (m.Groups["ht"].Success)
                {
                    var attrs = Attributes(m.Groups["ha"].Value);
                    var folder = Bookmark.Folder(Text(m.Groups["ht"].Value));
                    folder.Added = Number(attrs, "add_date") ?? folder.Added;
                    folder.IsToolbar = Get(attrs, "personal_toolbar_folder") == "true";
                    stack.Peek().Children.Add(folder);
                    pending = folder;
                }
                else if (m.Groups["at"].Success)
                {
                    var attrs = Attributes(m.Groups["aa"].Value);
                    var href = Get(attrs, "href");
                    if (string.IsNullOrWhiteSpace(href) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                    var link = Bookmark.Link(Text(m.Groups["at"].Value), WebUtility.HtmlDecode(href));
                    link.Added = Number(attrs, "add_date") ?? link.Added;
                    stack.Peek().Children.Add(link);
                }
                else if (m.Groups["open"].Success)
                {
                    stack.Push(pending ?? stack.Peek());
                    pending = null;
                }
                else if (m.Groups["close"].Success && stack.Count > 1)
                {
                    stack.Pop();
                }
            }
            var toolbar = root.Children.FirstOrDefault(c => c.IsFolder && c.IsToolbar);
            bar = Bookmark.Folder("Bookmarks bar");
            other = Bookmark.Folder("Other bookmarks");
            if (toolbar != null) bar.Children.AddRange(toolbar.Children);
            foreach (var child in root.Children)
                if (child != toolbar) other.Children.Add(child);
        }

        public static string Write(Bookmark bar, Bookmark other)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
            sb.AppendLine("<!-- This is an automatically generated file.");
            sb.AppendLine("     It will be read and overwritten.");
            sb.AppendLine("     DO NOT EDIT! -->");
            sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
            sb.AppendLine("<TITLE>Bookmarks</TITLE>");
            sb.AppendLine("<H1>Bookmarks</H1>");
            sb.AppendLine("<DL><p>");
            sb.AppendLine($"    <DT><H3 ADD_DATE=\"{bar.Added}\" LAST_MODIFIED=\"{Bookmark.Now}\" PERSONAL_TOOLBAR_FOLDER=\"true\">{Encode(bar.Title)}</H3>");
            WriteList(sb, bar.Children, 1);
            foreach (var child in other.Children) WriteNode(sb, child, 1);
            sb.AppendLine("</DL><p>");
            return sb.ToString();
        }

        static void WriteList(StringBuilder sb, List<Bookmark> items, int depth)
        {
            var pad = new string(' ', depth * 4);
            sb.AppendLine(pad + "<DL><p>");
            foreach (var item in items) WriteNode(sb, item, depth + 1);
            sb.AppendLine(pad + "</DL><p>");
        }

        static void WriteNode(StringBuilder sb, Bookmark b, int depth)
        {
            var pad = new string(' ', depth * 4);
            if (b.IsFolder)
            {
                sb.AppendLine($"{pad}<DT><H3 ADD_DATE=\"{b.Added}\" LAST_MODIFIED=\"{b.Added}\">{Encode(b.Title)}</H3>");
                WriteList(sb, b.Children, depth);
            }
            else
            {
                sb.AppendLine($"{pad}<DT><A HREF=\"{Encode(b.Url)}\" ADD_DATE=\"{b.Added}\">{Encode(b.Title)}</A>");
            }
        }

        static string Encode(string s) => WebUtility.HtmlEncode(s ?? "");
        static string Text(string html) => WebUtility.HtmlDecode(Tags.Replace(html ?? "", "")).Trim();

        static Dictionary<string, string> Attributes(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Attribute.Matches(text ?? ""))
                d[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            return d;
        }

        static string Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v.ToLowerInvariant() == "true" ? "true" : v : null;
        static long? Number(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : (long?)null;
    }

    // Reads bookmarks straight from Google Chrome's profile folders (read-only).
    static class ChromeImport
    {
        public sealed class ChromeProfile
        {
            public string Dir;
            public string Name;
        }

        public static string UserDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");

        public static List<ChromeProfile> Profiles(string userDataDir = null)
        {
            userDataDir = userDataDir ?? UserDataDir;
            var list = new List<ChromeProfile>();
            if (!Directory.Exists(userDataDir)) return list;
            Dictionary<string, object> names = null;
            try
            {
                var state = Json.Obj(Json.Parse(ReadShared(Path.Combine(userDataDir, "Local State"))));
                names = Json.Obj(Json.Get(Json.Obj(Json.Get(state, "profile")), "info_cache"));
            }
            catch { }
            foreach (var dir in Directory.GetDirectories(userDataDir))
            {
                if (!File.Exists(Path.Combine(dir, "Bookmarks")) && !File.Exists(Path.Combine(dir, "AccountBookmarks"))) continue;
                var id = Path.GetFileName(dir);
                var name = Json.Str(Json.Obj(Json.Get(names, id)), "name");
                list.Add(new ChromeProfile { Dir = dir, Name = string.IsNullOrWhiteSpace(name) ? id : $"{name} ({id})" });
            }
            return list.OrderBy(p => Path.GetFileName(p.Dir) == "Default" ? 0 : 1).ThenBy(p => p.Name).ToList();
        }

        public static void Read(string profileDir, out Bookmark bar, out Bookmark other)
        {
            bar = Bookmark.Folder("Bookmarks bar");
            other = Bookmark.Folder("Other bookmarks");
            foreach (var file in new[] { "Bookmarks", "AccountBookmarks" })
            {
                var path = Path.Combine(profileDir, file);
                if (File.Exists(path)) ReadJson(ReadShared(path), bar, other);
            }
        }

        public static void ReadJson(string json, Bookmark bar, Bookmark other)
        {
            var roots = Json.Obj(Json.Get(Json.Obj(Json.Parse(json)), "roots"));
            AddChildren(Json.Obj(Json.Get(roots, "bookmark_bar")), bar);
            AddChildren(Json.Obj(Json.Get(roots, "other")), other);
            var synced = Json.Obj(Json.Get(roots, "synced"));
            var mobile = Bookmark.Folder(Json.Str(synced, "name") ?? L.T("Mobile bookmarks"));
            AddChildren(synced, mobile);
            if (mobile.Children.Count > 0) other.Children.Add(mobile);
        }

        static void AddChildren(Dictionary<string, object> node, Bookmark target)
        {
            foreach (var child in Json.Arr(Json.Get(node, "children")).Select(Json.Obj).Where(c => c != null))
            {
                var name = Json.Str(child, "name") ?? "";
                var added = ChromeTime(Json.Str(child, "date_added"));
                if (Json.Str(child, "type") == "folder")
                {
                    var folder = Bookmark.Folder(name);
                    folder.Added = added;
                    AddChildren(child, folder);
                    target.Children.Add(folder);
                }
                else
                {
                    var url = Json.Str(child, "url");
                    if (string.IsNullOrEmpty(url)) continue;
                    var link = Bookmark.Link(name, url);
                    link.Added = added;
                    target.Children.Add(link);
                }
            }
        }

        // Chrome stores microseconds since 1601-01-01.
        static long ChromeTime(string value) =>
            long.TryParse(value, out var us) && us > 0 ? us / 1000000 - 11644473600L : Bookmark.Now;

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}
