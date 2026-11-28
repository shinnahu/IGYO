using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace IGYO
{
    // Shared by the browser (uninstall) and the setup program (install).
    static class InstallInfo
    {
        public const string AppName = "IGYO";
        public const string Tagline = "Web browser";   // what it is, in plain words (shortcuts, Windows' list of browsers, setup)
        public const string ExeName = "IGYO.exe";
        public const string DefaultRegKey = "IGYO";
        public const string MarkerName = "install.json";

        // The browser's earlier names, the most recent first: it was called Tacit until November 2026, and No
        // Suggestions Browser before that. Setup removes an install under one of them, and the browser moves that
        // name's data folder on its first start.
        public sealed class EarlierName
        {
            public string AppName;       // also the install folder's and the data folder's name
            public string ExeName;
            public string RegKey;        // the registry key its setup used by default (null: not known)
            public string DataFolder;
        }

        public static readonly EarlierName[] EarlierNames =
        {
            new EarlierName { AppName = "Tacit", ExeName = "Tacit.exe", RegKey = "Tacit", DataFolder = "Tacit" },
            new EarlierName { AppName = "No Suggestions Browser", ExeName = "NoSuggestionsBrowser.exe", RegKey = null, DataFolder = "NoSuggestionsBrowser" },
        };

        // Written by setup into the install folder; the uninstaller only removes folders that have it.
        public sealed class Marker
        {
            public string RegKey { get; set; } = DefaultRegKey;
            public List<string> Shortcuts { get; set; } = new List<string>();
            public string Version { get; set; } = "";
            // The color picked in setup's window (#RRGGBB, "" when none was), and an id of that pick: the browser makes
            // it the first profile's accent and the color of its circle once (it keeps the id), so that a later choice
            // in Settings stays.
            public string Color { get; set; } = "";
            public string ColorId { get; set; } = "";
            // A theme picked with that color ("subculture": Subculture, with its own accent and the color for the
            // circle), "" when the pick was a color alone.
            public string Theme { get; set; } = "";
        }

        // A theme's id as setup writes it: letters, digits and dashes.
        public static bool IsThemeId(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 40) return false;
            foreach (var c in text) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
            return true;
        }

        // "#RRGGBB" only.
        public static bool IsColor(string text)
        {
            if (text == null || text.Length != 7 || text[0] != '#') return false;
            for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(text[i])) return false;
            return true;
        }

        public static string UninstallKeyPath(string regKey) =>
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + (string.IsNullOrWhiteSpace(regKey) ? DefaultRegKey : regKey);

        public static Marker ReadMarker(string dir)
        {
            try
            {
                var file = Path.Combine(dir, MarkerName);
                return File.Exists(file) ? new JavaScriptSerializer().Deserialize<Marker>(File.ReadAllText(file)) : null;
            }
            catch
            {
                return null;
            }
        }

        public static void WriteMarker(string dir, Marker marker) =>
            File.WriteAllText(Path.Combine(dir, MarkerName), new JavaScriptSerializer().Serialize(marker));

        // ----- programs running from a folder -----
        // The browser's engine runs as programs of its own, from the "runtime" folder beside the browser. They end
        // a moment after the browser does, and stay when something of the engine's own keeps them (a window the
        // engine opened by itself). Files of a running program cannot be replaced or removed, so whoever replaces
        // or removes the folder looks for them first.

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        // Where a running program's file is; null when that cannot be told (another user's program, or one that has ended).
        public static string PathOf(int processId)
        {
            IntPtr handle = OpenProcess(0x1000 /* only what anyone may ask about a program */, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var name = new System.Text.StringBuilder(1024);
                int size = name.Capacity;
                return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally { CloseHandle(handle); }
        }

        // The ids of the programs that run from inside a folder (this program itself is left out).
        public static List<int> RunningFrom(string dir)
        {
            var found = new List<int>();
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me || p.Id <= 4) continue;
                    string path = PathOf(p.Id);
                    if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) found.Add(p.Id);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
        }

        // Waits for the engine's programs under an install folder to end by themselves, then ends the ones that
        // have not (the browser they belonged to is closed: nothing shows them, and nothing else will end them).
        // Only ever programs that run from that folder's "runtime". True when none is left.
        public static bool EndEngineLeftovers(string installDir, int waitMs)
        {
            string engine = Path.Combine(Path.GetFullPath(installDir).TrimEnd('\\'), "runtime");
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (RunningFrom(engine).Count > 0 && DateTime.UtcNow < until) System.Threading.Thread.Sleep(250);
            foreach (int id in RunningFrom(engine))
            {
                try
                {
                    using (var p = System.Diagnostics.Process.GetProcessById(id))
                    {
                        p.Kill();
                        p.WaitForExit(4000);
                    }
                }
                catch { }   // ended by itself meanwhile (the others end when the engine's main program does)
            }
            until = DateTime.UtcNow.AddSeconds(5);
            while (RunningFrom(engine).Count > 0 && DateTime.UtcNow < until) System.Threading.Thread.Sleep(200);
            return RunningFrom(engine).Count == 0;
        }

        public static void CreateShortcut(string linkPath, string target, string description)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = target;
                link.WorkingDirectory = Path.GetDirectoryName(target);
                link.Description = description;
                link.IconLocation = target + ",0";
                link.Save();
                Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
