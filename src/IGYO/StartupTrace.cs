using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace IGYO
{
    // Where the time goes between starting the program and the first page being on show. Off unless the
    // environment variable NSB_STARTUP_TRACE names a file: then each step is written to it with the
    // milliseconds since the process was created (tools\bench reads it).
    static class StartupTrace
    {
        static readonly string file = Environment.GetEnvironmentVariable("NSB_STARTUP_TRACE");
        static readonly DateTime started = StartOfProcess();
        static readonly StringBuilder lines = new StringBuilder();
        static readonly object gate = new object();
        static bool closed;

        public static bool On => !string.IsNullOrEmpty(file);

        static DateTime StartOfProcess()
        {
            try { using (var me = Process.GetCurrentProcess()) return me.StartTime.ToUniversalTime(); }
            catch { return DateTime.UtcNow; }
        }

        public static void Mark(string step)
        {
            if (!On) return;
            lock (gate)
            {
                if (closed) return;
                lines.Append(((DateTime.UtcNow - started).TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture)).Append('\t').Append(step).Append('\n');
            }
        }

        // The last step: everything is written out at once, so that writing never sits inside what is being timed.
        public static void Done(string step)
        {
            if (!On) return;
            Mark(step);
            lock (gate)
            {
                if (closed) return;
                closed = true;
                try { File.WriteAllText(file, lines.ToString()); } catch { }
            }
        }
    }
}
