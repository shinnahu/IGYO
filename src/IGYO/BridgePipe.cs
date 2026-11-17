using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IGYO
{
    // What the browser and its relay (IGYO.exe --mcp) share: the name of the pipe between them and how lines
    // travel on it. Nothing here creates, moves or reads the data folder: the relay has to leave it alone.
    static class BridgePipe
    {
        public const string Prefix = "IGYO.Bridge.";
        public const int MaxRequestBytes = 1 << 20;   // one line a program may send the browser
        public const int MaxReplyBytes = 16 << 20;    // one line the browser answers with (it can hold a picture)

        // Where this copy keeps its data, worked out without touching it (AppPaths would create it and move old data in).
        public static string DataDirPath()
        {
            var dir = Environment.GetEnvironmentVariable("NSB_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstallInfo.AppName);
            return dir;
        }

        // Copies of IGYO that use the same data folder share this part of the pipe's name; a test copy with its own
        // folder gets another one, so it can never be reached by a relay meant for the real browser (or the other way round).
        public static string KeyFor(string dataDir)
        {
            string full;
            try { full = Path.GetFullPath(dataDir ?? ""); } catch { full = dataDir ?? ""; }
            full = full.TrimEnd('\\', '/').ToUpperInvariant();
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(full)), 0, 6).Replace("-", "").ToLowerInvariant();
        }

        public static string NameFor(string dataDir, int pid) => Prefix + KeyFor(dataDir) + "." + pid;

        // Only the Windows user who runs the browser can open the pipe, and never from another computer.
        public static PipeSecurity OnlyThisUser()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint createdLow, createdHigh, accessedLow, accessedHigh, writtenLow, writtenHigh;   // three FILETIMEs
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFileW(string name, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextFileW(IntPtr find, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr find);

        // The process ids of the running copies of IGYO that use this data folder and have their pipe open.
        // Looks at the list of pipe names only: no pipe is opened by looking.
        public static List<int> Find(string dataDir)
        {
            var found = new List<int>();
            string start = Prefix + KeyFor(dataDir) + ".";
            IntPtr find = FindFirstFileW(@"\\.\pipe\*", out var data);
            if (find == new IntPtr(-1)) return found;
            try
            {
                do
                {
                    var name = data.cFileName ?? "";
                    if (name.StartsWith(start, StringComparison.OrdinalIgnoreCase) && int.TryParse(name.Substring(start.Length), out int pid) && pid > 0 && !found.Contains(pid))
                        found.Add(pid);
                }
                while (FindNextFileW(find, out data));
            }
            finally { FindClose(find); }
            return found;
        }
    }

    // Reads lines (one message each) from a stream of UTF-8 text, never holding more than the limit for one line.
    sealed class LineReader
    {
        readonly Stream stream;
        readonly int max;
        byte[] buffer = new byte[16384];
        int start, end, scanned;   // unread bytes are buffer[start..end); up to scanned there is no line end

        public LineReader(Stream stream, int maxLineBytes)
        {
            this.stream = stream;
            max = maxLineBytes;
        }

        // The next line without its line end, or null when the stream has ended.
        public async Task<string> ReadAsync(CancellationToken cancel = default(CancellationToken))
        {
            while (true)
            {
                if (TryTake(out var line)) return line;
                MakeRoom();
                int read = await stream.ReadAsync(buffer, end, buffer.Length - end, cancel);
                if (read <= 0) return Rest();
                end += read;
            }
        }

        // The same, for a stream that can only be read by waiting (the relay's standard input).
        public string Read()
        {
            while (true)
            {
                if (TryTake(out var line)) return line;
                MakeRoom();
                int read = stream.Read(buffer, end, buffer.Length - end);
                if (read <= 0) return Rest();
                end += read;
            }
        }

        bool TryTake(out string line)
        {
            line = null;
            int at = scanned < end ? Array.IndexOf(buffer, (byte)'\n', scanned, end - scanned) : -1;
            if (at < 0)
            {
                scanned = end;
                if (end - start > max) throw new InvalidDataException("A line was longer than allowed.");
                return false;
            }
            int length = at - start;
            if (length > 0 && buffer[at - 1] == (byte)'\r') length--;
            line = Encoding.UTF8.GetString(buffer, start, length);
            start = scanned = at + 1;
            return true;
        }

        // What is left when the stream ends without a last line end.
        string Rest()
        {
            if (end <= start) return null;
            var line = Encoding.UTF8.GetString(buffer, start, end - start).TrimEnd('\r');
            start = end = scanned = 0;
            return line;
        }

        void MakeRoom()
        {
            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                scanned -= start;
                start = 0;
            }
            if (end == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
        }
    }

    // What Windows says about a process: which program it is, when it started and which process started it.
    static class ProcessFacts
    {
        const uint QueryLimited = 0x1000;   // PROCESS_QUERY_LIMITED_INFORMATION

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        public static int ClientOf(PipeStream pipe)
        {
            try { return GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? (int)pid : 0; } catch { return 0; }
        }

        public static int ServerOf(PipeStream pipe)
        {
            try { return GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? (int)pid : 0; } catch { return 0; }
        }

        // The full path of the program a process runs; null when Windows won't say (the process is gone, or isn't ours to ask about).
        public static string PathOf(int pid)
        {
            if (pid <= 0) return null;
            IntPtr process = OpenProcess(QueryLimited, false, (uint)pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var name = new StringBuilder(1024);
                uint size = (uint)name.Capacity;
                return QueryFullProcessImageNameW(process, 0, name, ref size) ? name.ToString(0, (int)size) : null;
            }
            finally { CloseHandle(process); }
        }

        // When a process started (Windows' own count); 0 when unknown.
        public static long StartOf(int pid)
        {
            if (pid <= 0) return 0;
            IntPtr process = OpenProcess(QueryLimited, false, (uint)pid);
            if (process == IntPtr.Zero) return 0;
            try { return GetProcessTimes(process, out long creation, out _, out _, out _) ? creation : 0; }
            finally { CloseHandle(process); }
        }

        // Names one run of one program: a process id alone is given to another process once the first has ended.
        public static string KeyOf(int pid) => pid + "@" + StartOf(pid);

        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        public static bool IsRunning(int pid)
        {
            if (pid <= 0) return false;
            IntPtr process = OpenProcess(0x00100000 /* SYNCHRONIZE */, false, (uint)pid);
            if (process == IntPtr.Zero) return false;
            try { return WaitForSingleObject(process, 0) == 0x102; }   // WAIT_TIMEOUT: it has not ended
            finally { CloseHandle(process); }
        }

        // The process that started this one; 0 when it can't be told, has ended, or its id now belongs to a younger process.
        public static int ParentOf(int pid)
        {
            if (pid <= 0) return 0;
            int parent = 0;
            IntPtr snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snapshot == new IntPtr(-1)) return 0;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (entry.th32ProcessID == (uint)pid) { parent = (int)entry.th32ParentProcessID; break; }
                    }
                    while (Process32NextW(snapshot, ref entry));
                }
            }
            finally { CloseHandle(snapshot); }
            if (parent <= 0) return 0;
            long born = StartOf(pid), parentBorn = StartOf(parent);
            return parentBorn > 0 && born > 0 && parentBorn <= born ? parent : 0;
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
