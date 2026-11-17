using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IGYO
{
    // The page's zoom. Ctrl and the mouse wheel change it inside the engine (which, Microsoft's documentation says,
    // remembers one for each site); the zoom keys, the menus and the zoom box change it here, for the tab. Whatever
    // changed it, the address bar shows a button while it is not 100% (NavState's "zoom"), and its popup (the figure,
    // - and +, Reset: IGYO.zoom in overlays.js) comes up by itself for a moment when the person changed it, as in
    // other browsers.
    sealed partial class MainForm
    {
        static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };

        bool zoomPopupQueued;   // the interface is told to show the popup right after it is sent the new zoom (Flush)

        // One step in or out, or back to 100% (direction 0). Pressed in the popup itself, the popup only follows;
        // from a key or a menu, it comes up by itself.
        void Zoom(int direction, bool fromPopup = false)
        {
            var tab = current;
            var view = tab?.View;
            if (view == null) return;
            double z = tab.ZoomFactor;
            if (direction == 0) z = 1;
            else if (direction > 0) z = ZoomSteps.FirstOrDefault(s => s > z + 0.001) is double up && up > 0 ? up : ZoomSteps[ZoomSteps.Length - 1];
            else z = ZoomSteps.LastOrDefault(s => s < z - 0.001) is double down && down > 0 ? down : ZoomSteps[0];
            try { view.ZoomFactor = z; } catch { return; }   // (the engine reports nothing for a zoom set from here)
            if (fromPopup) MarkDirty(nav: true);
            else ShowZoomPopup();
        }

        // The engine changed a tab's zoom by itself (BrowserTab.ZoomChanged). The button follows in any case.
        void OnTabZoomChanged(BrowserTab tab)
        {
            if (tab != current || IsDisposed) return;
            if (ZoomedByHand(tab)) ShowZoomPopup();
            else MarkDirty(nav: true);
        }

        // Whether the person has just changed the zoom in this window: Ctrl is held (Ctrl and the wheel, or a zoom key
        // the engine took itself), and this is the window they are at (it has the keyboard, or the mouse is over it).
        // Not when a page has just begun loading: the engine then gives it the zoom its site was last shown at. And not
        // in another window that shows the same site and follows the change.
        bool ZoomedByHand(BrowserTab tab)
        {
            if (tab.NewDocumentJustNow) return false;
#if DEBUG
            // Test builds only: a window on a desktop that is never shown has no keyboard or mouse to look at.
            if (Environment.GetEnvironmentVariable("NSB_TEST_ZOOM_BY_HAND") == "1") return true;
#endif
            if (!MiddleClick.CtrlDown) return false;
            if (ContainsFocus) return true;
            var under = WindowFromPoint(Cursor.Position);
            return under != IntPtr.Zero && GetAncestor(under, GA_ROOT) == Handle;
        }

        void ShowZoomPopup()
        {
            if (ui != null && ui.Ready) zoomPopupQueued = true;
            MarkDirty(nav: true);
        }

        const uint GA_ROOT = 2;
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    }
}
