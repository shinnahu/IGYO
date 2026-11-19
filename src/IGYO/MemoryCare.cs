using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace IGYO
{
    // Memory that is not being used is let go of early, instead of being held until Windows has to take it:
    //   - a tab that has been in the background for half a minute (its page keeps running: nothing is paused,
    //     and whatever it touches again comes back by itself);
    //   - all of IGYO, once no IGYO window has been the one in use for a minute (and nothing is playing, floating in
    //     picture in picture or in a call);
    //   - once after starting, what only starting up needed.
    // What goes down is the "memory in use" Task Manager shows for each process (its private working set).
    // What this does not do: the memory stays reserved for the process (committed) until the tab is unloaded,
    // and Windows keeps the pages on its own lists. Measured on a PC with memory to spare, nearly all of them
    // stayed in RAM on the "modified" list, where Windows must first save them to the paging file before another
    // program can have them, and it saved none in five minutes (docs\performance.md). So sleep and unload are
    // what really shrinks a browser; this only makes what is idle easy for Windows to take.
    // Settings > Memory saver has the switch (ReleaseMemory).
    sealed class MemoryCare
    {
        public const int HiddenSeconds = 30, AwaySeconds = 60, StartSeconds = 12;

        readonly BrowserApp app;
        DateTime firstPage = DateTime.MaxValue, lastInUse = DateTime.UtcNow, lastTick = DateTime.MinValue;
        bool startDone, awayDone, busy;

        public MemoryCare(BrowserApp app) { this.app = app; }

        // The first page of this start has loaded: from here the start-up release is counted.
        public void PageLoaded()
        {
            if (firstPage == DateTime.MaxValue) firstPage = DateTime.UtcNow;
        }

        // Called from every window's housekeeping (each 5 seconds); does its work at most once per round.
        public async void Tick()
        {
            var now = DateTime.UtcNow;
            if (busy || (now - lastTick).TotalSeconds < 4) return;
            lastTick = now;
            if (app.Settings == null || !app.Settings.ReleaseMemory) return;
            busy = true;
            try
            {
                bool inUse = app.Windows.Any(w => w.IsInUse);
                bool playing = app.Windows.Any(w => w.Tabs.Any(t => t.IsPlayingAudio || t.InPictureInPicture || (t.InCall && !w.IsShowing(t))));
                if (inUse || playing)
                {
                    lastInUse = now;
                    awayDone = false;
                }
                if (!startDone && firstPage != DateTime.MaxValue && (now - firstPage).TotalSeconds >= StartSeconds)
                {
                    startDone = true;
                    ReleaseEverything();
                }
                else if (!awayDone && (now - lastInUse).TotalSeconds >= AwaySeconds)
                {
                    // A call on show counts as in use, whatever program is in front (its window may be the one being
                    // shared): the tabs on show are asked, as tabs in the background were when they were left.
                    if (await CallOnShowAsync()) lastInUse = now;
                    else
                    {
                        awayDone = true;   // once per time away: doing it again and again would only make pages stutter
                        ReleaseEverything();
                    }
                }
                else await ReleaseHiddenTabsAsync();
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
            finally { busy = false; }
        }

        async Task<bool> CallOnShowAsync()
        {
            foreach (var window in app.Windows.ToList())
                foreach (var tab in window.Tabs.Where(window.IsShowing).ToList())
                    if (tab.State == TabState.Live && await tab.CheckInUseAsync() && tab.InCall) return true;
            return false;
        }

        void ReleaseEverything()
        {
            var environment = BrowserEngine.Current;
            if (environment != null)
                foreach (var info in environment.GetProcessInfos()) ProcessMemory.Release(info.ProcessId);
            foreach (var window in app.Windows)
                foreach (var tab in window.Tabs)
                    if (!window.IsShowing(tab)) tab.MemoryReleased = true;
            MemoryTrim.Trim();   // this program's own
        }

        // The page processes that only serve tabs nobody is looking at. A process can serve several tabs (pages of
        // one site share it) and frames inside other tabs, so each is checked against everything that is in use.
        async Task ReleaseHiddenTabsAsync()
        {
            var environment = BrowserEngine.Current;
            if (environment == null) return;
            var due = new List<BrowserTab>();
            var keep = new HashSet<uint>();   // the frames whose processes are left alone
            foreach (var window in app.Windows)
            {
                uint own = window.InterfaceFrameId;
                if (own != 0) keep.Add(own);
                foreach (var tab in window.Tabs)
                {
                    if (tab.Core == null) continue;
                    uint frame;
                    try { frame = tab.Core.FrameId; } catch { continue; }
                    bool hidden = !window.IsShowing(tab) && !tab.IsPlayingAudio && !tab.InPictureInPicture && !tab.InCall && !tab.OnAwakeList;
                    if (!hidden) keep.Add(frame);
                    else if (tab.MemoryReleased) continue;
                    else if (tab.State == TabState.Live && (DateTime.Now - tab.HiddenSince).TotalSeconds >= HiddenSeconds) due.Add(tab);
                    else keep.Add(frame);   // only just hidden: not yet
                }
            }
            if (due.Count == 0) return;
            foreach (var info in await environment.GetProcessExtendedInfosAsync())
            {
                if (info.ProcessInfo.Kind != CoreWebView2ProcessKind.Renderer) continue;
                var roots = info.AssociatedFrameInfos.Select(Root).ToList();
                if (roots.Count == 0 || roots.Any(keep.Contains)) continue;
                ProcessMemory.Release(info.ProcessInfo.ProcessId);
            }
            foreach (var tab in due) tab.MemoryReleased = true;
        }

        internal static uint Root(CoreWebView2FrameInfo frame)
        {
            for (int guard = 0; frame.ParentFrameInfo != null && guard < 64; guard++) frame = frame.ParentFrameInfo;
            return frame.FrameId;
        }
    }

    // Other processes' memory: handing it back to Windows, and reading how much is in use.
    static class ProcessMemory
    {
        const uint QueryLimited = 0x1000, SetQuota = 0x0100, VmRead = 0x0010;

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
        [DllImport("psapi.dll", SetLastError = true)] static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

        // PROCESS_MEMORY_COUNTERS_EX2: the last two fields exist from Windows 10 22H2 on.
        [StructLayout(LayoutKind.Sequential)]
        struct Counters
        {
            public uint Size, PageFaults;
            public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged, QuotaPeakNonPaged, QuotaNonPaged, Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
            public ulong SharedCommit;
        }

        // Windows takes the process's memory out of its working set; the process gets back whatever it touches.
        public static bool Release(int pid)
        {
            var handle = OpenProcess(QueryLimited | SetQuota, false, pid);
            if (handle == IntPtr.Zero) return false;
            try { return EmptyWorkingSet(handle); }
            catch { return false; }
            finally { CloseHandle(handle); }
        }

        // Memory in use as Task Manager shows it: the private working set (bytes). The whole working set, which also
        // counts memory shared with other processes, where Windows cannot tell the private part.
        public static long InUse(int pid)
        {
            var handle = OpenProcess(QueryLimited | VmRead, false, pid);
            if (handle == IntPtr.Zero) return 0;
            try { return InUse(handle); }
            finally { CloseHandle(handle); }
        }

        public static long InUseByThisProcess() => InUse(GetCurrentProcess());

        // In use (as InUse) and reserved: the private memory the process has committed, which stays the same when
        // memory is let go of (MemoryCare), so it says what the process really holds. Zeros when it can't be read.
        public static (long inUse, long reserved) Usage(int pid)
        {
            var handle = OpenProcess(QueryLimited | VmRead, false, pid);
            if (handle == IntPtr.Zero) return (0, 0);
            try
            {
                var counters = new Counters();
                uint whole = (uint)Marshal.SizeOf(typeof(Counters));
                counters.Size = whole;
                if (GetProcessMemoryInfo(handle, ref counters, whole))
                    return ((long)counters.PrivateWorkingSet.ToUInt64(), (long)counters.PrivateUsage.ToUInt64());
                counters = new Counters();
                uint older = whole - (uint)UIntPtr.Size - 8;   // without the last two fields
                counters.Size = older;
                return GetProcessMemoryInfo(handle, ref counters, older) ? ((long)counters.WorkingSet.ToUInt64(), (long)counters.PrivateUsage.ToUInt64()) : (0, 0);
            }
            catch { return (0, 0); }
            finally { CloseHandle(handle); }
        }

        static long InUse(IntPtr handle)
        {
            try
            {
                var counters = new Counters();
                uint whole = (uint)Marshal.SizeOf(typeof(Counters));
                counters.Size = whole;
                if (GetProcessMemoryInfo(handle, ref counters, whole)) return (long)counters.PrivateWorkingSet.ToUInt64();
                counters = new Counters();
                uint older = whole - (uint)UIntPtr.Size - 8;   // without the last two fields
                counters.Size = older;
                return GetProcessMemoryInfo(handle, ref counters, older) ? (long)counters.WorkingSet.ToUInt64() : 0;
            }
            catch { return 0; }
        }
    }
}
