using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IGYO
{
    // Settings > Default browser: whether Windows opens links from other programs in IGYO, and the way to Windows'
    // own page where that is chosen. Windows lets no program make itself the default browser; the person presses
    // "Set default" on IGYO's page there. Setup lists IGYO with Windows (see BrowserRegistration); if that list
    // entry is missing or points elsewhere, it is written again before Windows' page is opened.
    static class DefaultBrowser
    {
        [ComImport, Guid("4e530b0a-e611-4c77-a3ac-9031d022281b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationAssociationRegistration
        {
            // (Only the first of the interface's methods is used, and declared.)
            [PreserveSig]
            int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string query, int type, int level, [MarshalAs(UnmanagedType.LPWStr)] out string association);
        }

        [ComImport, Guid("591209c7-767b-42b2-9fba-44ee4615f2c7")]
        class ApplicationAssociationRegistration { }

        const int ForProtocol = 1;   // AT_URLPROTOCOL
        const int Effective = 1;     // AL_EFFECTIVE: what Windows really uses

        // The kind of document (ProgID) Windows opens links of this kind with; null when it can't be told.
        static string CurrentFor(string protocol)
        {
            object registration = null;
            try
            {
                registration = new ApplicationAssociationRegistration();
                return ((IApplicationAssociationRegistration)registration).QueryCurrentDefault(protocol, ForProtocol, Effective, out string progId) == 0 ? progId : null;
            }
            catch { return null; }
            finally { if (registration != null) try { Marshal.ReleaseComObject(registration); } catch { } }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int what, string association, string verb, StringBuilder output, ref uint size);

        // The name of the program Windows opens links of this kind with ("Google Chrome", say); null when unknown.
        static string ProgramFor(string protocol)
        {
            try
            {
                var name = new StringBuilder(260);
                uint size = (uint)name.Capacity;
                return AssocQueryString(0x1000 /* ASSOCF_IS_PROTOCOL */, 4 /* ASSOCSTR_FRIENDLYAPPNAME */, protocol, "open", name, ref size) == 0 && name.Length > 0 ? name.ToString() : null;
            }
            catch { return null; }
        }

        static InstallInfo.Marker Installed() => InstallInfo.ReadMarker(AppPaths.AppDir);

        public static Dictionary<string, object> State()
        {
            var marker = Installed();
            string progId = BrowserRegistration.ProgId(marker?.RegKey ?? InstallInfo.DefaultRegKey);
            string http = CurrentFor("http"), https = CurrentFor("https");
            bool ours(string current) => string.Equals(current, progId, StringComparison.OrdinalIgnoreCase);
            bool isDefault = marker != null && ours(http) && ours(https);
            return new Dictionary<string, object>
            {
                ["installed"] = marker != null,   // only a copy installed by setup can be offered to Windows
                ["isDefault"] = isDefault,
                // Where links go instead (the kind that isn't IGYO's, when only one of the two is).
                ["current"] = isDefault ? "" : ProgramFor(ours(https) ? "http" : "https") ?? "",
            };
        }

        // Opens Windows' page for IGYO among the default apps. Null when it was opened; otherwise what went wrong.
        public static string Choose()
        {
            var marker = Installed();
            if (marker == null) return L.T("Only a copy installed with {0}'s setup can be the default browser.", InstallInfo.AppName);
            try
            {
                if (!BrowserRegistration.IsRegistered(Registry.CurrentUser, marker.RegKey, Application.ExecutablePath))
                {
                    BrowserRegistration.Register(Registry.CurrentUser, marker.RegKey, Application.ExecutablePath);
                    BrowserRegistration.TellWindows();
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                return L.T("{0} couldn't be listed with Windows: {1}", InstallInfo.AppName, ex.Message);
            }
            // IGYO's own page in Settings > Apps > Default apps: Microsoft's address for a program installed for one user.
            var page = "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(BrowserRegistration.AppKey(marker.RegKey));
#if DEBUG
            // Test builds: the page Windows would be asked for is written down instead of opened, so a check never
            // puts Windows' settings in front of whoever is at the PC.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_DEFAULT_APPS")))
            {
                try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "debug-default-apps.txt"), page); } catch { }
                return null;
            }
#endif
            try { Process.Start(page); }
            catch (Exception ex) { return L.T("Windows' settings couldn't be opened: {0}", ex.Message); }
            return null;
        }
    }
}
