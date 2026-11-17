using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace IGYO
{
    // The interface is a page and animates itself. This drives the few animations left on the Windows side
    // (the buttons on the DevTools drawer). With animations off, everything jumps to its end state.
    static class Anim
    {
        public static bool Enabled = true;

        sealed class Item
        {
            public DateTime Start;
            public double Ms;
            public Action<double> Step;
            public Action Done;
            public object Key;
        }

        static readonly List<Item> items = new List<Item>();
        static Timer timer;

        // Calls step(t) with t easing from 0 to 1. A new animation with the same key replaces the running one.
        public static void Run(object key, int ms, Action<double> step, Action done = null)
        {
            if (key != null) items.RemoveAll(i => Equals(i.Key, key));
            if (!Enabled || ms <= 0)
            {
                step(1);
                done?.Invoke();
                return;
            }
            if (timer == null)
            {
                timer = new Timer { Interval = 15 };
                timer.Tick += Tick;
            }
            items.Add(new Item { Start = DateTime.UtcNow, Ms = ms, Step = step, Done = done, Key = key });
            if (!timer.Enabled) timer.Start();
        }

        static void Tick(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            foreach (var item in items.ToArray())
            {
                double t = Math.Min(1, (now - item.Start).TotalMilliseconds / item.Ms);
                double eased = 1 - Math.Pow(1 - t, 3);
                try { item.Step(eased); } catch { }
                if (t < 1) continue;
                items.Remove(item);
                try { item.Done?.Invoke(); } catch { }
            }
            if (items.Count == 0) timer.Stop();
        }
    }

    // Repaints completely when resized and never flickers.
    class SmoothPanel : Panel
    {
        public SmoothPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }
    }
}
