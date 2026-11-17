using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IGYO
{
    // Runs as "IGYO.exe --uninstall" from Windows' Installed apps list.
    static class Uninstaller
    {
        public static int Run(string[] args)
        {
            L.Use(L.Auto);   // in the language Windows is shown in (the settings file is not opened here)
            bool quiet = args.Contains("--quiet");
            bool removeData = args.Contains("--remove-data");
            var dir = AppPaths.AppDir;
            var marker = InstallInfo.ReadMarker(dir);
            if (marker == null)
            {
                if (!quiet) MessageBox.Show(L.T("This copy wasn't installed by setup, so there is nothing to uninstall here."), InstallInfo.AppName);
                return 2;
            }
            if (!quiet)
            {
                if (MessageBox.Show(L.T("Remove {0} from this PC?", InstallInfo.AppName), InstallInfo.AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                removeData = MessageBox.Show(L.T("Also delete your profiles, bookmarks, settings and browsing data?") + "\n\n" + AppPaths.DataDir,
                    InstallInfo.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            if (OtherCopiesRunning(dir))
            {
                if (!quiet) MessageBox.Show(L.T("Close all {0} windows first, then try again.", InstallInfo.AppName), InstallInfo.AppName);
                return 3;
            }

            // The engine of a copy that was closed a moment ago may still be ending, and one that something of its
            // own keeps running never ends by itself: files it holds could not be removed. Waited for, then ended.
            InstallInfo.EndEngineLeftovers(dir, 3000);

            foreach (var link in marker.Shortcuts ?? new List<string>())
                try { if (File.Exists(link)) File.Delete(link); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.UninstallKeyPath(marker.RegKey), false); } catch { }
            // No longer offered to Windows as a browser.
            BrowserRegistration.Unregister(Registry.CurrentUser, marker.RegKey);
            BrowserRegistration.TellWindows();

            // A running program can't delete its own folder, so a short-lived command does it right after this exits.
            var command = $"/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{dir}\" & ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"";
            if (removeData) command += $" & rmdir /s /q \"{AppPaths.DataDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (!quiet) MessageBox.Show(L.T("{0} was removed.", InstallInfo.AppName), InstallInfo.AppName);
            return 0;
        }

        static bool OtherCopiesRunning(string dir)
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstallInfo.ExeName)))
            {
                try
                {
                    if (p.Id != me && string.Equals(Path.GetDirectoryName(p.MainModule.FileName), dir, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }
    }
}
