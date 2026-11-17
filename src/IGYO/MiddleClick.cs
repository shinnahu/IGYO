using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IGYO
{
    // Whether a tab a page asks for was asked for with the middle mouse button. The engine says that a page wants
    // a new tab, not how the link was clicked, and what the mouse does over a page never passes through IGYO.
    // So, while an IGYO window is the active one, Windows is asked to tell IGYO of the mouse's buttons as well
    // ("raw input"), and the moment the middle button was last pressed or let go is kept. Nothing else is read
    // from what Windows tells, and nothing is asked for while no IGYO window is active.
    static class MiddleClick
    {
        sealed class Sink : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_INPUT) Note(m.LParam);
                base.WndProc(ref m);   // (Windows clears the message's data away itself)
            }
        }

        static Sink sink;
        static bool watching, broken, seen;
        static int last;                                        // Environment.TickCount at the button's last press or release
        static readonly byte[] data = new byte[64];             // one mouse report is 40 or 48 bytes
        static readonly int Header = 8 + 2 * IntPtr.Size;       // what stands before the mouse's part in it

        // Asked for while an IGYO window is active (and tabs opened that way are to stay behind), given up otherwise.
        public static void Watch(bool on)
        {
            if (broken || on == watching) return;
            try
            {
                if (on && sink == null)
                {
                    sink = new Sink();
                    sink.CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // a window for messages only, never shown
                }
                var device = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = on ? RIDEV_INPUTSINK : RIDEV_REMOVE, hwndTarget = on ? sink.Handle : IntPtr.Zero };
                if (RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) watching = on;
                else if (on) broken = true;
            }
            catch { broken = true; }
        }

        static void Note(IntPtr input)
        {
            if (broken) return;
            try
            {
                uint size = (uint)data.Length;
                uint got = GetRawInputData(input, RID_INPUT, data, ref size, (uint)Header);
                if (got == uint.MaxValue || got < Header + 8 || BitConverter.ToInt32(data, 0) != RIM_TYPEMOUSE) return;
                int buttons = BitConverter.ToUInt16(data, Header + 4);
#if DEBUG
                Reports++;
                Sizes |= got == 48 ? 1 : got == 40 ? 2 : 4;
                Buttons |= buttons;
#endif
                if ((buttons & (MIDDLE_DOWN | MIDDLE_UP)) == 0) return;
                last = Environment.TickCount;
                seen = true;
            }
            catch { broken = true; }
        }

#if DEBUG
        // Test builds: how many of the mouse's reports were read, and whether reading them is still in order.
        // (Of the buttons, only which kinds of press and release were ever seen is kept, as one number; of the
        //  reports' lengths, only which of the two expected ones came, or another.)
        public static int Reports, Buttons, Sizes;
        public static string State => (watching ? "asked for" : "not asked for") + (broken ? ", given up" : "") + ", " + Reports + " reports read" +
            (Reports > 0 ? " (lengths seen: " + ((Sizes & 1) != 0 ? "48 " : "") + ((Sizes & 2) != 0 ? "40 " : "") + ((Sizes & 4) != 0 ? "another " : "") + "; button changes seen: 0x" + Buttons.ToString("X4") + ")" : "") +
            (seen ? ", the middle button among them" : "");
#endif

        // True when the middle button was used a moment ago. Asked once per click: the answer is used up.
        public static bool Take(int withinMs = 1500)
        {
#if DEBUG
            // Test builds, when asked: taken as used (what Windows tells of the mouse cannot be made up on a desktop that is never shown).
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NSB_TEST_MIDDLE"))) return true;
#endif
            if (broken || sink == null) return false;
            try
            {
                // (What Windows has told and this program has not yet looked at is looked at first: a request from
                //  the engine is handed over before messages about the mouse are.)
                while (PeekMessage(out MSG m, sink.Handle, WM_INPUT, WM_INPUT, PM_REMOVE))
                {
                    Note(m.lParam);
                    DefWindowProc(m.hwnd, m.message, m.wParam, m.lParam);
                }
            }
            catch { }
            if (!seen) return false;
            seen = false;
            int ago = unchecked(Environment.TickCount - last);
            return ago >= 0 && ago <= withinMs;
        }

        // Whether Ctrl, or Shift, is held down right now (asked of Windows itself: the keyboard is with the page).
        public static bool CtrlDown => GetAsyncKeyState(0x11) < 0;
        public static bool ShiftDown => GetAsyncKeyState(0x10) < 0;

        const int WM_INPUT = 0x00FF, RIM_TYPEMOUSE = 0, MIDDLE_DOWN = 0x0010, MIDDLE_UP = 0x0020;
        const uint RID_INPUT = 0x10000003, RIDEV_REMOVE = 0x00000001, RIDEV_INPUTSINK = 0x00000100, PM_REMOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(IntPtr input, uint command, [Out] byte[] data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG message, IntPtr window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    }
}
