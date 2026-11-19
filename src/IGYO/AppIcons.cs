using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IGYO
{
    // The icons IGYO can wear (Settings > Appearance > App icon): his star on tiles of other colors, listed in
    // Assets\AppIcons\icons.json and drawn by tools\make-app-icons.ps1. The default one is also the program's own
    // icon (Assets\app.ico). The one chosen is every window's icon (Alt+Tab, the window's picture on the taskbar) and
    // the icon of IGYO's shortcuts: the ones setup made (the install folder's install.json lists them) and the
    // taskbar's pins that start this IGYO. One for the whole program, whatever the profile.
    static class AppIcons
    {
        public sealed class Choice
        {
            public string Id;
            public string Name;   // in English; shown through L.T (tools\lang.js takes the names from icons.json)
        }

        static List<Choice> all;
        static string defaultId = "";

        public static string Dir => Path.Combine(AppPaths.AppDir, "Assets", "AppIcons");

        public static IReadOnlyList<Choice> All { get { Load(); return all; } }
        public static string Default { get { Load(); return defaultId; } }

        static void Load()
        {
            if (all != null) return;
            var list = new List<Choice>();
            string wanted = "";
            try
            {
                var root = Json.Obj(Json.Parse(File.ReadAllText(Path.Combine(Dir, "icons.json"))));
                foreach (var item in Json.Arr(Json.Get(root, "icons")).Select(Json.Obj))
                {
                    string id = Json.Str(item, "id") ?? "", name = Json.Str(item, "name") ?? id;
                    if (IsId(id) && File.Exists(IcoFile(id)) && list.All(c => c.Id != id)) list.Add(new Choice { Id = id, Name = name });
                }
                wanted = Json.Str(root, "default") ?? "";
            }
            catch (Exception ex) { ErrorLog.Write(ex); }   // (read once a run)
            defaultId = list.Any(c => c.Id == wanted) ? wanted : list.FirstOrDefault()?.Id ?? "";
            all = list;
        }

        static bool IsId(string id) => id.Length > 0 && id.Length <= 40 && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');

        public static bool IsChoice(string id) => !string.IsNullOrEmpty(id) && All.Any(c => c.Id == id);

        // The icon worn: the one chosen while it is on the list, otherwise ("" too) the default one.
        public static string Resolve(string chosen) => IsChoice(chosen) ? chosen : Default;

        public static string IcoFile(string id) => Path.Combine(Dir, id + ".ico");

        static readonly Dictionary<string, Icon> windowIcons = new Dictionary<string, Icon>();

        // An icon for windows, with every size its file has: Windows takes the one it needs for each place. The
        // program's own icon when the file can't be read.
        public static Icon WindowIcon(string chosen)
        {
            string id = Resolve(chosen);
            lock (windowIcons)
            {
                if (windowIcons.TryGetValue(id, out var known)) return known;
                Icon icon = null;
                try
                {
                    if (id.Length > 0)
                        using (var stream = File.OpenRead(IcoFile(id))) icon = new Icon(stream);
                }
                catch (Exception ex) { ErrorLog.Write(ex); icon = null; }   // (each icon is read once a run)
                if (icon == null) try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                windowIcons[id] = icon;
                return icon;
            }
        }

        // ----- shortcuts -----

        // Gives IGYO's shortcuts the icon worn (the default one: the program's own, as setup made them). Only shortcuts
        // that start this IGYO are touched, and nothing of them but the icon. Returns how many were changed. Any thread.
        public static int ApplyToShortcuts(string chosen)
        {
            string id = Resolve(chosen), exe = Application.ExecutablePath;
            string iconFile = id == Default || !File.Exists(IcoFile(id)) ? exe : IcoFile(id);
            int changed = 0;
            foreach (var file in ShortcutFiles())
            {
                try
                {
                    if (!SetIcon(file, exe, iconFile)) continue;
                    changed++;
                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, file, IntPtr.Zero);
                }
                catch (Exception ex) { ErrorLog.WriteOnce("shortcut icon", ex); }
            }
            // Windows keeps the pictures it has drawn of icons; this makes it look at them again (the taskbar's pins too).
            if (changed > 0) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero);
            return changed;
        }

        static Task shortcutWork = Task.CompletedTask;
        static readonly object workLock = new object();

        // The same, off the caller's thread, each in turn in the order asked (a quick second choice never loses to the
        // first), after a wait when one is given.
        public static void ApplyToShortcutsInTurn(string chosen, int waitMs = 0)
        {
            lock (workLock)
                shortcutWork = shortcutWork.ContinueWith(async _ =>
                {
                    if (waitMs > 0) await Task.Delay(waitMs);
                    try { ApplyToShortcuts(chosen); } catch (Exception ex) { ErrorLog.WriteOnce("shortcut icon", ex); }
                }, TaskScheduler.Default).Unwrap();
        }

        // The shortcuts setup made, as the install folder's install.json lists them, and the taskbar's pins.
        static IEnumerable<string> ShortcutFiles()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var made = InstallInfo.ReadMarker(AppPaths.AppDir)?.Shortcuts ?? new List<string>();
            foreach (var link in made)
                if (!string.IsNullOrEmpty(link) && File.Exists(link) && seen.Add(link)) yield return link;
            string pins = PinsFolder;
            if (pins != null && Directory.Exists(pins))
                foreach (var link in Directory.GetFiles(pins, "*.lnk"))
                    if (seen.Add(link)) yield return link;
        }

        // Where Windows keeps the taskbar's pins. A test build looks only where a check says (NSB_TEST_PINS), so that a
        // test copy never comes near the pins of whoever is at the PC.
        static string PinsFolder
        {
            get
            {
#if DEBUG
                var test = Environment.GetEnvironmentVariable("NSB_TEST_PINS");
                return string.IsNullOrWhiteSpace(test) ? null : test;
#else
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
#endif
            }
        }

        // One shortcut: when it starts this IGYO (exe) and shows another icon than iconFile's first one, it is given
        // that one. False when it was left as it was.
        internal static bool SetIcon(string file, string exe, string iconFile)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(file, 0 /* STGM_READ: only read, unless it is IGYO's and changes */);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                if (!SamePath(target.ToString(), exe)) return false;
                var shown = new StringBuilder(1024);
                link.GetIconLocation(shown, shown.Capacity, out int index);
                string now = Environment.ExpandEnvironmentVariables(shown.ToString());
                if (now.Length == 0) { now = exe; index = 0; }   // (a shortcut that names no icon shows its program's)
                if (index == 0 && SamePath(now, iconFile)) return false;
                link.SetIconLocation(iconFile, 0);
                ((IPersistFile)link).Save(file, true);
                return true;
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        // A shortcut's icon, as "file,index" (for the checks).
        internal static string IconOf(string file)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(file, 0 /* STGM_READ */);
                var shown = new StringBuilder(1024);
                link.GetIconLocation(shown, shown.Capacity, out int index);
                return shown + "," + index;
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        static bool SamePath(string a, string b)
        {
            try { return a.Length > 0 && b.Length > 0 && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        const int SHCNE_UPDATEITEM = 0x00002000, SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000, SHCNF_PATHW = 0x0005, SHCNF_FLUSHNOWAIT = 0x3000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
