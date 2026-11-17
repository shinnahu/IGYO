# Starts a program on its own Windows desktop ("IGYOTest"), which is never shown. Its windows exist there only,
# so they cannot appear on the desktop in use or take its keyboard. Environment variables set in this PowerShell
# are passed on. Returns the process id.
param([Parameter(Mandatory = $true)][string]$Exe, [string]$Arguments = '', [string]$Desktop = 'IGYOTest')
if (-not ('HiddenDesk' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Threading;
public static class HiddenDesk {
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static int Start(string desktop, string exe, string args) {
        IntPtr d = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);   // GENERIC_ALL
        if (d == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO)); si.lpDesktop = desktop;
        PROCESS_INFORMATION pi;
        var cmd = new StringBuilder("\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
        if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out pi)) { CloseDesktop(d); throw new System.ComponentModel.Win32Exception(); }
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        Thread.Sleep(3000);      // the new process attaches to the desktop while this handle keeps it alive
        CloseDesktop(d);
        return pi.dwProcessId;
    }
}
"@
}
[HiddenDesk]::Start($Desktop, $Exe, $Arguments)
