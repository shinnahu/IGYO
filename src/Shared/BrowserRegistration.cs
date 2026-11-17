using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IGYO
{
    // How Windows learns that IGYO is a browser: what its "Default apps" page lists, and what it runs for a link or a
    // page file once IGYO is chosen there. Setup writes it for the Windows user who installs (no administrator is
    // needed) and the uninstaller removes it. Windows lets no program make itself the default: the person chooses it
    // in Windows' settings, and IGYO can only open that page for them.
    //
    // Shared by the setup program (install), the browser (uninstall, and putting it back from Settings) and the
    // self-test, which writes it under a throwaway key instead of the real place: every path below is relative to
    // the key it is given.
    static class BrowserRegistration
    {
        // What Windows runs is "IGYO.exe --single-argument <the link>". Everything after the switch is one address,
        // however it is written (see CommandLine), so a link can never hand IGYO a switch of its own.
        public const string SingleArgument = "--single-argument";

        // The kinds of address and the kinds of file IGYO offers to open.
        public static readonly string[] Protocols = { "http", "https" };
        public static readonly string[] FileTypes = { ".htm", ".html", ".shtml", ".xht", ".xhtml" };

        const string RegisteredApps = @"Software\RegisteredApplications";

        // Every name comes from the install's registry key, so a test install under another key has registrations
        // of its own and never touches the real one.
        public static string AppKey(string regKey)
        {
            var name = new string((regKey ?? "").Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_').ToArray());
            return name.Length > 0 ? name : InstallInfo.DefaultRegKey;
        }

        public static string ProgId(string regKey) => AppKey(regKey) + "HTML";
        static string ClientPath(string regKey) => @"Software\Clients\StartMenuInternet\" + AppKey(regKey);
        public static string CapabilitiesPath(string regKey) => ClientPath(regKey) + @"\Capabilities";
        static string ClassPath(string name) => @"Software\Classes\" + name;

        public static string OpenCommand(string exe) => "\"" + exe + "\" " + SingleArgument + " %1";

        public static void Register(RegistryKey root, string regKey, string exe)
        {
            string app = AppKey(regKey), progId = ProgId(regKey), icon = exe + ",0";

            // The kind of document IGYO opens: what Windows runs for a link or a page file once IGYO is chosen for it.
            using (var key = root.CreateSubKey(ClassPath(progId))) key.SetValue("", InstallInfo.AppName + " HTML Document");
            using (var key = root.CreateSubKey(ClassPath(progId) + @"\DefaultIcon")) key.SetValue("", icon);
            using (var key = root.CreateSubKey(ClassPath(progId) + @"\shell\open\command")) key.SetValue("", OpenCommand(exe));
            using (var key = root.CreateSubKey(ClassPath(progId) + @"\Application"))
            {
                key.SetValue("ApplicationName", InstallInfo.AppName);
                key.SetValue("ApplicationIcon", icon);
                key.SetValue("ApplicationDescription", InstallInfo.Tagline);
                key.SetValue("ApplicationCompany", InstallInfo.AppName);
            }

            // The browser, and what it can open.
            using (var key = root.CreateSubKey(ClientPath(regKey))) key.SetValue("", InstallInfo.AppName);
            using (var key = root.CreateSubKey(ClientPath(regKey) + @"\DefaultIcon")) key.SetValue("", icon);
            using (var key = root.CreateSubKey(ClientPath(regKey) + @"\shell\open\command")) key.SetValue("", "\"" + exe + "\"");
            using (var key = root.CreateSubKey(CapabilitiesPath(regKey)))
            {
                key.SetValue("ApplicationName", InstallInfo.AppName);
                key.SetValue("ApplicationDescription", InstallInfo.Tagline);
                key.SetValue("ApplicationIcon", icon);
            }
            using (var key = root.CreateSubKey(CapabilitiesPath(regKey) + @"\StartMenu")) key.SetValue("StartMenuInternet", app);
            using (var key = root.CreateSubKey(CapabilitiesPath(regKey) + @"\URLAssociations"))
                foreach (var protocol in Protocols) key.SetValue(protocol, progId);
            using (var key = root.CreateSubKey(CapabilitiesPath(regKey) + @"\FileAssociations"))
                foreach (var type in FileTypes) key.SetValue(type, progId);

            // Offered in "Open with" for page files.
            foreach (var type in FileTypes)
                using (var key = root.CreateSubKey(ClassPath(type) + @"\OpenWithProgids")) key.SetValue(progId, "");

            // Windows' list of programs that can be chosen as the default for something. Written last: from here on
            // Windows lists IGYO, and everything it points to is there.
            using (var key = root.CreateSubKey(RegisteredApps)) key.SetValue(app, CapabilitiesPath(regKey));
        }

        // Removes what Register wrote, and only that: a value or key someone else put beside it stays.
        public static void Unregister(RegistryKey root, string regKey)
        {
            string app = AppKey(regKey), progId = ProgId(regKey);
            try
            {
                using (var key = root.OpenSubKey(RegisteredApps, true))
                    if (key != null && string.Equals(key.GetValue(app) as string, CapabilitiesPath(regKey), StringComparison.OrdinalIgnoreCase)) key.DeleteValue(app, false);
            }
            catch { }
            try { root.DeleteSubKeyTree(ClientPath(regKey), false); } catch { }
            try { root.DeleteSubKeyTree(ClassPath(progId), false); } catch { }
            foreach (var type in FileTypes)
            {
                try
                {
                    using (var key = root.OpenSubKey(ClassPath(type) + @"\OpenWithProgids", true)) key?.DeleteValue(progId, false);
                    DeleteIfEmpty(root, ClassPath(type) + @"\OpenWithProgids");
                    DeleteIfEmpty(root, ClassPath(type));
                }
                catch { }
            }
        }

        static void DeleteIfEmpty(RegistryKey root, string path)
        {
            using (var key = root.OpenSubKey(path))
                if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            root.DeleteSubKey(path, false);
        }

        // Whether Windows lists this install as a browser, and runs this program for its links.
        public static bool IsRegistered(RegistryKey root, string regKey, string exe)
        {
            try
            {
                using (var apps = root.OpenSubKey(RegisteredApps))
                    if (!string.Equals(apps?.GetValue(AppKey(regKey)) as string, CapabilitiesPath(regKey), StringComparison.OrdinalIgnoreCase)) return false;
                using (var urls = root.OpenSubKey(CapabilitiesPath(regKey) + @"\URLAssociations"))
                    if (urls == null || Protocols.Any(p => !string.Equals(urls.GetValue(p) as string, ProgId(regKey), StringComparison.OrdinalIgnoreCase))) return false;
                using (var command = root.OpenSubKey(ClassPath(ProgId(regKey)) + @"\shell\open\command"))
                    return string.Equals(command?.GetValue("") as string, OpenCommand(exe), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        // Windows reads these lists again (without this, open windows of Explorer and Settings may keep the old ones).
        public static void TellWindows()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST, as that event requires */, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }
}
