using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace IGYO
{
    // Whether Claude may read a page is decided per site: its scheme, name and port ("https://mail.example.com").
    static class BridgeSites
    {
        public const string Ask = "ask", Read = "read", Never = "never";
        public const string Act = "act";   // read, and act on it too (click, type, run scripts): asked for apart from reading

        public static bool MayRead(string mode) => mode == Read || mode == Act;

        // The site of a page Claude could be allowed to read; null for everything else (IGYO's own pages,
        // local files, blank and special pages).
        public static string OriginOf(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            if (u.Scheme != "https" && u.Scheme != "http") return null;
            string host;
            try { host = (u.IdnHost ?? "").ToLowerInvariant(); } catch { return null; }
            if (host.Length == 0 || host == "igyo" || host.EndsWith(".igyo")) return null;   // the interface, New Tab, Settings
            return u.Scheme + "://" + host + (u.IsDefaultPort ? "" : ":" + u.Port.ToString(CultureInfo.InvariantCulture));
        }

        // How a site is named to a person: without "https://", which is what nearly every site has.
        public static string Name(string origin) =>
            string.IsNullOrEmpty(origin) ? "" : origin.StartsWith("https://", StringComparison.Ordinal) ? origin.Substring(8) : origin;

        // A page's address without a user name or password that may be written into it.
        public static string WithoutSignIn(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.UserInfo)) return url ?? "";
            int at = url.IndexOf(u.UserInfo + "@", StringComparison.Ordinal);
            return at < 0 ? url : url.Remove(at, u.UserInfo.Length + 1);
        }
    }

    sealed class BridgeProgram
    {
        public string Path { get; set; } = "";
        public string Added { get; set; } = "";
    }

    // One thing Claude did or asked for. Never any of a page's content, title or full address: the site only.
    sealed class BridgeActivity
    {
        public string Time { get; set; } = "";
        public string Profile { get; set; } = "";
        public string Tool { get; set; } = "";
        public string Site { get; set; } = "";
        public string Note { get; set; } = "";
        public bool Ok { get; set; }
    }

    sealed class BridgeProgramsFile { public List<BridgeProgram> Programs { get; set; } = new List<BridgeProgram>(); }
    sealed class BridgeSitesFile { public Dictionary<string, string> Sites { get; set; } = new Dictionary<string, string>(); }
    sealed class BridgeActivityFile { public List<BridgeActivity> Entries { get; set; } = new List<BridgeActivity>(); }

    // What the Claude bridge remembers on this PC, all in IGYO's data folder:
    //   bridge.json                 the programs allowed to connect
    //   Profiles\<id>\bridge.json   the sites set to "always" or "never" for that profile
    //   bridge-activity.json        the last 500 things Claude did
    sealed class BridgeStore
    {
        public const int MaxActivity = 500;

        List<BridgeProgram> programs;
        List<BridgeActivity> activity;
        readonly Dictionary<string, Dictionary<string, string>> sites = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        System.Windows.Forms.Timer saveTimer;
        bool activityDirty;

        static string ProgramsPath => Path.Combine(AppPaths.DataDir, "bridge.json");
        static string ActivityPath => Path.Combine(AppPaths.DataDir, "bridge-activity.json");
        static string SitesPath(string profileId) => Path.Combine(ProfileStore.DirFor(profileId), "bridge.json");

        static T Load<T>(string file) where T : class, new()
        {
            try { if (File.Exists(file)) return Json.Deserialize<T>(File.ReadAllText(file)) ?? new T(); } catch { }
            return new T();
        }

        // ----- programs -----

        public IReadOnlyList<BridgeProgram> Programs
        {
            get
            {
                if (programs == null)
                {
                    programs = (Load<BridgeProgramsFile>(ProgramsPath).Programs ?? new List<BridgeProgram>())
                        .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Path)).ToList();
                }
                return programs;
            }
        }

        public bool IsProgramAllowed(string path) => !string.IsNullOrEmpty(path) && Programs.Any(p => ProcessFacts.SamePath(p.Path, path));

        public void AllowProgram(string path)
        {
            if (string.IsNullOrEmpty(path) || IsProgramAllowed(path)) return;
            programs.Add(new BridgeProgram { Path = path, Added = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            SavePrograms();
        }

        public bool RemoveProgram(string path)
        {
            _ = Programs;
            if (programs.RemoveAll(p => ProcessFacts.SamePath(p.Path, path)) == 0) return false;
            SavePrograms();
            return true;
        }

        void SavePrograms()
        {
            try { FileUtil.WriteAtomic(ProgramsPath, Json.Write(new BridgeProgramsFile { Programs = programs })); } catch { }
        }

        // ----- sites, per profile -----

        Dictionary<string, string> SitesOf(string profileId)
        {
            if (!sites.TryGetValue(profileId, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in Load<BridgeSitesFile>(SitesPath(profileId)).Sites ?? new Dictionary<string, string>())
                    if (BridgeSites.OriginOf(pair.Key) == pair.Key && (pair.Value == BridgeSites.Read || pair.Value == BridgeSites.Act || pair.Value == BridgeSites.Never)) map[pair.Key] = pair.Value;
                sites[profileId] = map;
            }
            return map;
        }

        // "read" (reading always allowed), "act" (reading and acting always allowed), "never", or "ask" (what every
        // site is until the person says otherwise).
        public string ModeFor(string profileId, string origin) =>
            origin != null && SitesOf(profileId).TryGetValue(origin, out var mode) ? mode : BridgeSites.Ask;

        public bool SetSite(string profileId, string origin, string mode)
        {
            if (origin == null || BridgeSites.OriginOf(origin) != origin) return false;
            var map = SitesOf(profileId);
            if (mode == BridgeSites.Read || mode == BridgeSites.Act || mode == BridgeSites.Never) map[origin] = mode;
            else if (mode == BridgeSites.Ask) map.Remove(origin);
            else return false;
            try { FileUtil.WriteAtomic(SitesPath(profileId), Json.Write(new BridgeSitesFile { Sites = map })); } catch { }
            return true;
        }

        public List<KeyValuePair<string, string>> SiteList(string profileId) =>
            SitesOf(profileId).OrderBy(p => BridgeSites.Name(p.Key), StringComparer.OrdinalIgnoreCase).ToList();

        // ----- activity -----

        public IReadOnlyList<BridgeActivity> Activity
        {
            get
            {
                if (activity == null)
                {
                    activity = (Load<BridgeActivityFile>(ActivityPath).Entries ?? new List<BridgeActivity>()).Where(e => e != null).ToList();
                    if (activity.Count > MaxActivity) activity.RemoveRange(0, activity.Count - MaxActivity);
                }
                return activity;
            }
        }

        public void Log(string profile, string tool, string site, string note, bool ok)
        {
            _ = Activity;
            activity.Add(new BridgeActivity
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Profile = profile ?? "", Tool = tool ?? "", Site = site ?? "", Note = note ?? "", Ok = ok,
            });
            if (activity.Count > MaxActivity) activity.RemoveRange(0, activity.Count - MaxActivity);
            activityDirty = true;
            // Written a few seconds later, so a run of requests is one write. (Without a window's message loop the
            // timer never fires: Flush is then called by whoever is done.)
            try
            {
                if (saveTimer == null)
                {
                    saveTimer = new System.Windows.Forms.Timer { Interval = 3000 };
                    saveTimer.Tick += (s, e) => Flush();
                }
                saveTimer.Start();
            }
            catch { }
        }

        public void ClearActivity()
        {
            _ = Activity;
            activity.Clear();
            activityDirty = true;
            Flush();
        }

        public void Flush()
        {
            saveTimer?.Stop();
            if (!activityDirty || activity == null) return;
            activityDirty = false;
            try { FileUtil.WriteAtomic(ActivityPath, Json.Write(new BridgeActivityFile { Entries = activity })); } catch { }
        }
    }

    // What a tool answers with, in the shape Claude's side expects: pieces of text and pictures.
    static class BridgeReply
    {
        public static Dictionary<string, object> Text(string text, bool isError = false) => new Dictionary<string, object>
        {
            ["content"] = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = text ?? "" } },
            ["isError"] = isError,
        };

        // Something that kept the tool from doing its work, said so that Claude can pass it on to the person.
        public static Dictionary<string, object> Problem(string text) => Text("[IGYO] " + text, true);

        public static Dictionary<string, object> Picture(string text, byte[] jpeg) => new Dictionary<string, object>
        {
            ["content"] = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = text ?? "" },
                new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(jpeg), ["mimeType"] = "image/jpeg" },
            },
            ["isError"] = false,
        };
    }
}
