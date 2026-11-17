using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IGYO
{
    // A profile has its own cookies, sign-ins, history, saved passwords, bookmarks and tabs.
    sealed class Profile
    {
        public string Id { get; set; }          // also the engine's profile name
        public string Name { get; set; }
        public string Color { get; set; }
        public string EnginePath { get; set; }  // where the engine keeps this profile's data
        public bool ClaudeBridge { get; set; }  // "Let Claude use this profile": off unless the person switches it on
        public bool ClaudeAct { get; set; }     // "Let Claude act in this profile" (click, type, open its own tabs): off unless switched on
        // This profile's look: a theme's id or "auto", and its accent ("" for the theme's own). Null until the profile
        // chooses one: it then has the look kept in settings.json, the one every profile shared before each had its own.
        public string Theme { get; set; }
        public string Accent { get; set; }
    }

    sealed class ProfileStore
    {
        public List<Profile> Profiles { get; set; } = new List<Profile>();
        public List<string> PendingDelete { get; set; } = new List<string>();
        public string LastUsed { get; set; } = "Default";
        // At start, with more than one profile, IGYO asks which one to open (ProfilePicker), unless AskAtStart is off
        // AND a default profile (StartWith) is chosen: that one then opens straight away. "" when none is chosen.
        public bool AskAtStart { get; set; } = true;
        public string StartWith { get; set; } = "";

        // The profile opened at start without asking, or null when IGYO asks (or there is only one profile).
        // (methods, not properties: every property of this class is written into profiles.json)
        public Profile OpensWithoutAsking() => !AskAtStart ? Find(StartWith) : null;
        public bool AsksAtStart() => Profiles.Count > 1 && OpensWithoutAsking() == null;

        public static readonly string[] Colors = { "#2563EB", "#7C3AED", "#16A34A", "#D97706", "#E11D48", "#0D9488", "#DB2777", "#475569",
                                                   "#8AA4FF", "#C5F135", "#FFD500", "#FE58C9", "#FF99C5", "#D9C6A2" };

        // The color picked in setup's window (install.json in the install folder, with an id of that pick) is taken once:
        // the id is kept in settings, so a later choice in Settings is never undone by it. It becomes IGYO's own accent
        // (the one every profile that never chose its own has, a new profile starts with, and the profile picker shows),
        // and the first profile's accent and the color of its circle. Setup run again with the color it gave before (it
        // starts on that one) leaves the first profile as it is. Only these objects change; the caller saves them when
        // this says so.
        public bool TakeSetupColor(Settings settings, string installDir)
        {
            var marker = InstallInfo.ReadMarker(installDir);
            if (marker == null || !IsColor(marker.Color) || string.IsNullOrEmpty(marker.ColorId) || marker.ColorId == settings.SetupColorApplied) return false;
            var color = marker.Color.ToUpperInvariant();
            settings.Accent = color;
            if (!string.Equals(color, settings.SetupColor, StringComparison.OrdinalIgnoreCase))
            {
                Default.Accent = color;
                Default.Color = color;
            }
            settings.SetupColor = color;
            settings.SetupColorApplied = marker.ColorId;
            return true;
        }

        // "#RRGGBB" only: what the circle of a profile without a picture is painted with.
        public static bool IsColor(string text) =>
            text != null && text.Length == 7 && text[0] == '#' && text.Skip(1).All(c => Uri.IsHexDigit(c));

        public static string ColorOf(Profile p) => IsColor(p?.Color) ? p.Color.ToUpperInvariant() : Colors[0];

        static string FilePath => Path.Combine(AppPaths.DataDir, "profiles.json");

        public static string DirFor(string id)
        {
            var dir = Path.Combine(AppPaths.DataDir, "Profiles", id);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public Profile Default => Profiles[0];

        // A profile's own picture: any image you choose, cut to a square and kept as a 256-pixel PNG.
        public static string PicturePath(string id) => Path.Combine(DirFor(id), "avatar.png");

        public static bool SetPicture(Profile profile, string file)
        {
            try
            {
                var path = PicturePath(profile.Id);
                var temp = path + ".tmp";
                using (var square = Pictures.Square(file, 256)) square.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void RemovePicture(Profile profile)
        {
            try { File.Delete(PicturePath(profile.Id)); } catch { }
        }

        // Which profile the first window will open with, read straight from the file. (Nothing else is built
        // here: this runs at the very start, before the language is known, to ask the engine for that profile's
        // first view as early as possible.) The same choice as BrowserApp makes: the one asked for, else the one
        // used last, else the first.
        public static string StartId(string asked)
        {
            try
            {
                if (!File.Exists(FilePath)) return "Default";
                // The file is small and written by this program, so the ids are looked for in its text. (Parsing it
                // here cost 45 ms at every start, before the engine had even been asked.) If the text is not as
                // expected, the answer is "Default", and a wrong answer only means the early view goes unused.
                var text = File.ReadAllText(FilePath);
                string Stored(string id)
                {
                    if (string.IsNullOrEmpty(id)) return null;
                    foreach (char c in id) if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return null;
                    int at = text.IndexOf("\"Id\":\"" + id + "\"", StringComparison.OrdinalIgnoreCase);
                    return at < 0 ? null : text.Substring(at + 6, id.Length);
                }
                string Value(string name)
                {
                    int mark = text.IndexOf("\"" + name + "\":\"", StringComparison.Ordinal);
                    if (mark < 0) return null;
                    int from = mark + name.Length + 4, to = text.IndexOf('"', from);
                    return to > from ? text.Substring(from, to - from) : null;
                }
                // (the default profile, when IGYO opens it without asking; else the one used last, which is also
                // the likeliest pick when it asks)
                string start = text.Contains("\"AskAtStart\":false") ? Stored(Value("StartWith")) : null;
                return Stored(asked) ?? start ?? Stored(Value("LastUsed")) ?? "Default";
            }
            catch { return "Default"; }
        }

        // Whether some profile lets Claude act, read straight from the file as the engine starts (see
        // BrowserEngine.DrawCovered): the same quick look into the file's text as StartId.
        public static bool AnyLetsClaudeAct()
        {
            try { return File.Exists(FilePath) && File.ReadAllText(FilePath).Contains("\"ClaudeAct\":true"); }
            catch { return false; }
        }

        public static ProfileStore Load()
        {
            ProfileStore store = null;
            try
            {
                if (File.Exists(FilePath)) store = Json.Deserialize<ProfileStore>(File.ReadAllText(FilePath));
            }
            catch { }
            store = store ?? new ProfileStore();
            if (store.Profiles == null) store.Profiles = new List<Profile>();
            if (store.PendingDelete == null) store.PendingDelete = new List<string>();
            store.Profiles.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id));
            if (!store.Profiles.Any(p => p.Id == "Default"))
                store.Profiles.Insert(0, new Profile { Id = "Default", Name = L.T("Personal"), Color = Colors[0] });
            return store;
        }

        public void Save()
        {
            try { FileUtil.WriteAtomic(FilePath, Json.Write(this)); } catch { }
        }

        public Profile Find(string id) =>
            string.IsNullOrEmpty(id) ? null : Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        // The color a new profile's circle has unless another is picked when it is added.
        public string NextColor() => Colors[Profiles.Count % Colors.Length];

        public Profile Add(string name, string color = null)
        {
            int n = 1;
            while (Profiles.Any(p => p.Id == "Profile" + n)) n++;
            var profile = new Profile
            {
                Id = "Profile" + n,
                Name = string.IsNullOrWhiteSpace(name) ? L.T("Profile {0}", n) : name.Trim(),
                Color = IsColor(color) ? color.ToUpperInvariant() : NextColor(),
            };
            Profiles.Add(profile);
            Save();
            return profile;
        }

        // The engine keeps a profile's files open while it runs, so they are removed on the next start.
        public void Delete(Profile profile)
        {
            if (profile == null || profile.Id == "Default") return;
            Profiles.Remove(profile);
            PendingDelete.Add(profile.Id + "|" + (profile.EnginePath ?? ""));
            if (LastUsed == profile.Id) LastUsed = "Default";
            if (StartWith == profile.Id) StartWith = "";
            Save();
        }

        public void DeletePendingProfiles()
        {
            if (PendingDelete.Count == 0) return;
            foreach (var entry in PendingDelete)
            {
                var parts = entry.Split('|');
                if (parts[0].Length > 0 && parts[0] != "Default") TryDelete(Path.Combine(AppPaths.DataDir, "Profiles", parts[0]));
                var enginePath = parts.Length > 1 ? parts[1] : "";
                if (enginePath.Length > 0 && enginePath.StartsWith(AppPaths.EngineDir + "\\", StringComparison.OrdinalIgnoreCase))
                    TryDelete(enginePath);
            }
            PendingDelete.Clear();
            Save();
        }

        static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    // The tabs and window position a profile had when its last window closed.
    // A profile's windows as they were left: the one closed last (the fields below) and, in Others, the windows
    // closed together with it (each a Session of its own, with no Others). Builds before Others read only the first.
    sealed class Session
    {
        public List<string> Tabs { get; set; } = new List<string>();
        public List<string> Titles { get; set; } = new List<string>();
        public int Active { get; set; }
        public int[] Bounds { get; set; }
        public bool Maximized { get; set; }
        public List<Session> Others { get; set; }

        static string FileFor(string profileId) => Path.Combine(ProfileStore.DirFor(profileId), "session.json");

        // Closing one window after another to leave IGYO keeps them all: the windows of a profile that were closed
        // within this long before its last one come back with it at the next start. A window closed long before,
        // while others stayed open, stays closed (as in other browsers).
        public static readonly TimeSpan TogetherWithin = TimeSpan.FromSeconds(60);

        // The last window's session, with the windows closed shortly before it (oldest first) that had tabs.
        public static Session Together(Session last, IEnumerable<(DateTime at, Session window)> closedBefore, DateTime now)
        {
            var others = closedBefore.Where(c => now - c.at <= TogetherWithin && c.window?.Tabs != null && c.window.Tabs.Count > 0)
                .OrderBy(c => c.at).Select(c => c.window).ToList();
            foreach (var other in others) other.Others = null;
            last.Others = others.Count > 0 ? others : null;
            return last;
        }

        public static Session Load(string profileId)
        {
            try
            {
                var file = FileFor(profileId);
                if (!File.Exists(file)) return null;
                var session = Json.Deserialize<Session>(File.ReadAllText(file));
                if (session == null) return null;
                foreach (var window in new[] { session }.Concat(session.Others ?? new List<Session>()))
                    if (window?.Tabs != null)
                        for (int i = 0; i < window.Tabs.Count; i++) window.Tabs[i] = InternalPages.FromEarlierName(window.Tabs[i]);
                return session;
            }
            catch { }
            return null;
        }

        public void Save(string profileId)
        {
            try { FileUtil.WriteAtomic(FileFor(profileId), Json.Write(this)); }
            catch (Exception ex) { ErrorLog.WriteOnce("tabs", ex); }
        }
    }
}
