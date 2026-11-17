using System;
using System.IO;
using System.Linq;
using System.Text;

namespace IGYO
{
    static class AppPaths
    {
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string DataDir = InitDataDir();

        // Having this (even empty) makes .NET work out the data folder exactly when it is first asked for, never
        // earlier: the self-test and the relay point it elsewhere, or stay away from it, before that.
        static AppPaths() { }

        static string InitDataDir()
        {
            // NSB_DATA_DIR lets tests use a throwaway profile instead of yours.
            var dir = Environment.GetEnvironmentVariable("NSB_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dir))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                dir = BringOverEarlierData(local, Path.Combine(local, InstallInfo.AppName));
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        // First start after a rename: the data folder of the browser's earlier name (Tacit, and No Suggestions
        // Browser before that) moves to the current name, with every profile, bookmark, sign-in and setting in it.
        // Returns the folder to use. When an earlier folder cannot be moved yet (a program still has one of its files
        // open), it is used where it stands this time, and the move is tried again at the next start.
        internal static string BringOverEarlierData(string parent, string dir)
        {
            foreach (var earlier in InstallInfo.EarlierNames)
                BringOverLegacyData(Path.Combine(parent, earlier.DataFolder), dir);
            if (Directory.Exists(dir)) return dir;
            foreach (var earlier in InstallInfo.EarlierNames)
            {
                var old = Path.Combine(parent, earlier.DataFolder);
                if (Directory.Exists(old)) return old;
            }
            return dir;
        }

        // One earlier folder: moved in when there is no folder under the current name yet. If there is one but it was
        // used less recently than the earlier one (left by a test, say), it is put aside under a dated name, never
        // deleted. Whichever folder isn't used stays on disk as it was.
        internal static void BringOverLegacyData(string legacy, string dir)
        {
            if (!Directory.Exists(legacy)) return;
            if (!Directory.Exists(dir)) { TryMove(legacy, dir); return; }
            if (LastUsed(legacy) <= LastUsed(dir)) return;
            var aside = dir + " (set aside " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ")";
            if (TryMove(dir, aside) && !TryMove(legacy, dir)) TryMove(aside, dir);   // put it back if the old folder can't move
        }

        static bool TryMove(string from, string to)
        {
            try { Directory.Move(from, to); return true; } catch { return false; }
        }

        // When a data folder was last used: its settings, profile list, or a window's saved tabs.
        static DateTime LastUsed(string folder)
        {
            try
            {
                var files = new[] { "settings.json", "profiles.json" }.Select(f => Path.Combine(folder, f)).Where(File.Exists).ToList();
                var profiles = Path.Combine(folder, "Profiles");
                if (Directory.Exists(profiles)) files.AddRange(Directory.EnumerateFiles(profiles, "session.json", SearchOption.AllDirectories));
                return files.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            }
            catch { return DateTime.MinValue; }
        }

        public static string EngineDir => Path.Combine(DataDir, "Engine");
        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
        public static string UpdatedAdListFile => Path.Combine(DataDir, "easylist.txt");
        public static string BundledAdListFile => Path.Combine(AppDir, "Assets", "easylist.txt");

        // The engine copy that ships with the browser: runtime\<version>\msedgewebview2.exe.
        // Null means "use Windows' shared WebView2 Runtime" (only during development).
        public static string RuntimeFolder()
        {
#if DEBUG
            var dev = Environment.GetEnvironmentVariable("NSB_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(dev)) return Path.GetFullPath(dev);
#endif
            var root = Path.Combine(AppDir, "runtime");
            if (!Directory.Exists(root)) return null;
            if (File.Exists(Path.Combine(root, "msedgewebview2.exe"))) return root;
            return Directory.GetDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "msedgewebview2.exe")));
        }
    }

    static class FileUtil
    {
        // Writes through a temporary file so a crash never leaves a half-written file behind.
        public static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The file could not be swapped for the new one (something has it open, say): written over as it
                // stands instead. Whatever happens, the temporary file is not left lying about.
                try { File.Copy(temp, path, true); }
                finally { try { File.Delete(temp); } catch { } }
            }
        }

        // Moves a file or a folder to the Recycle Bin, with nothing shown. False when it is still there afterwards.
        public static bool Recycle(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return true;
            try
            {
#if DEBUG
                // Test builds: the checks work in folders of their own and leave the Recycle Bin of whoever is at the PC alone.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_NO_RECYCLE")))
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
                    return true;
                }
#endif
                var op = new SHFILEOPSTRUCT { wFunc = 3 /* delete */, pFrom = path + "\0\0", fFlags = 0x0040 /* to the Recycle Bin */ | 0x0010 /* no question */ | 0x0004 /* no progress window */ | 0x0400 /* no error window */ };
                SHFileOperation(ref op);
            }
            catch { }
            return !File.Exists(path) && !Directory.Exists(path);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    }
}
