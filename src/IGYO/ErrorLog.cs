using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace IGYO
{
    // If something unexpected goes wrong in a window, IGYO writes it to errors.log in its data folder and keeps
    // going, instead of stopping on .NET's "Unhandled exception" box. The first time per run, it says so in a small notice.
    static class ErrorLog
    {
        static bool told;

        public static string FilePath => Path.Combine(AppPaths.DataDir, "errors.log");

        public static void Write(Exception ex)
        {
            if (ex == null) return;
            try
            {
                var file = FilePath;
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);   // stays small
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {InstallInfo.AppName} {Application.ProductVersion}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }

        // For something that is tried again and again (saving the settings, saving a window's tabs): written down
        // the first time it fails in a run, under what it was, and not again.
        static readonly System.Collections.Generic.HashSet<string> once = new System.Collections.Generic.HashSet<string>();
        public static void WriteOnce(string what, Exception ex)
        {
            lock (once) { if (!once.Add(what)) return; }
            Write(new Exception("Could not save: " + what, ex));
        }

        public static void Report(Exception ex, BrowserApp app)
        {
            Write(ex);
            if (told || app == null) return;
            told = true;
            try
            {
                var window = app.Windows.FirstOrDefault(w => w.ContainsFocus) ?? app.Windows.LastOrDefault();
                window?.ShowProblem();
            }
            catch { }
        }
    }
}
