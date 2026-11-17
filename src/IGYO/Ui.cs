using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IGYO
{
    static class Ui
    {
        public static readonly string IconFont =
            FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        public const string Back = "", Forward = "", Reload = "", Stop = "", Star = "", StarFilled = "",
            Shield = "", Pip = "", Camera = "", Download = "", More = "", Add = "", Close = "",
            Lock = "", Info = "", Search = "", Page = "", Folder = "", Volume = "", Moon = "";

        public const string Globe = "";

        static readonly Dictionary<float, Font> glyphFonts = new Dictionary<float, Font>();
        static readonly Dictionary<string, Bitmap> glyphImages = new Dictionary<string, Bitmap>();

        public static Font GlyphFont(float size)
        {
            if (!glyphFonts.TryGetValue(size, out var font)) glyphFonts[size] = font = new Font(IconFont, size);
            return font;
        }

        // The button's text is its name (what screen readers and UI Automation read); the icon is drawn separately.
        public static GlyphButton MakeButton(string glyph, string name, ToolTip tips, string tip, float size = 11f)
        {
            var b = new GlyphButton { Glyph = glyph, GlyphSize = size, Text = name, AccessibleName = name, Margin = Padding.Empty, Padding = Padding.Empty };
            tips.SetToolTip(b, tip);
            return b;
        }

        public static void Style(GlyphButton b, Color back, Theme t)
        {
            b.BackColor = back;
            b.ForeColor = t.Text;
            b.DisabledColor = Blend(t.Muted, back, 0.35);
            b.HoverBack = t.Hover;
            b.PressedBack = t.Pressed;
            b.Invalidate();
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static GraphicsPath TopRounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            path.CloseFigure();
            return path;
        }

        public static Bitmap GlyphImage(string glyph, Color color, int size)
        {
            var key = glyph + "|" + color.ToArgb() + "|" + size;
            if (glyphImages.TryGetValue(key, out var bmp)) return bmp;
            bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font(IconFont, size * 0.62f, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
            }
            glyphImages[key] = bmp;
            return bmp;
        }

        static readonly Dictionary<int, Bitmap> logos = new Dictionary<int, Bitmap>();

        // The app's logo at a pixel size (Assets\logo.png, or the program's own icon).
        public static Bitmap Logo(int size)
        {
            size = Math.Max(1, size);
            if (logos.TryGetValue(size, out var bmp)) return bmp;
            bmp = new Bitmap(size, size);
            try
            {
                var file = System.IO.Path.Combine(AppPaths.AppDir, "Assets", "logo.png");
                using (var source = System.IO.File.Exists(file)
                    ? Image.FromStream(new System.IO.MemoryStream(System.IO.File.ReadAllBytes(file)))
                    : Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap())
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(source, 0, 0, size, size);
                }
            }
            catch { }
            logos[size] = bmp;
            return bmp;
        }

        static readonly Dictionary<string, (DateTime stamp, Image image)> profilePictures = new Dictionary<string, (DateTime, Image)>();

        // A profile's round picture: the one you chose, or its initial on its color.
        public static Bitmap Avatar(Profile profile, int size, bool isPrivate = false)
        {
            size = Math.Max(1, size);
            if (!isPrivate && profile != null && ProfilePicture(profile.Id) is Image picture)
                try { return Pictures.Round(picture, size); } catch { }
            return Avatar(profile?.Color, profile?.Name, size, isPrivate);
        }

        static Image ProfilePicture(string id)
        {
            try
            {
                var file = ProfileStore.PicturePath(id);
                if (!System.IO.File.Exists(file))
                {
                    if (profilePictures.TryGetValue(id, out var gone)) { gone.image?.Dispose(); profilePictures.Remove(id); }
                    return null;
                }
                var stamp = System.IO.File.GetLastWriteTimeUtc(file);
                if (profilePictures.TryGetValue(id, out var cached) && cached.stamp == stamp) return cached.image;
                cached.image?.Dispose();
                Image image;
                using (var stream = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(file)))
                using (var loaded = Image.FromStream(stream))
                    image = new Bitmap(loaded);
                profilePictures[id] = (stamp, image);
                return image;
            }
            catch { return null; }
        }

        public static Bitmap Avatar(string hexColor, string name, int size, bool isPrivate = false)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var color = isPrivate ? Color.FromArgb(60, 60, 66) : ParseHex(hexColor, Color.SteelBlue);
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, size, size), Blend(color, Color.White, 0.18), Blend(color, Color.Black, 0.12), 45f))
                    g.FillEllipse(brush, 0, 0, size - 1, size - 1);
                using (var font = new Font("Segoe UI Semibold", size * 0.5f, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(Initial(name, isPrivate), font, Brushes.White, new RectangleF(0, 0, size, size + 1), format);
            }
            return bmp;
        }

        public static string Initial(string name, bool isPrivate) =>
            isPrivate ? "P" : string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();

        public static string Trim(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        public static Color Blend(Color a, Color b, double amount) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));

        public static Color ParseHex(string hex, Color fallback)
        {
            try { return string.IsNullOrWhiteSpace(hex) ? fallback : ColorTranslator.FromHtml(hex); } catch { return fallback; }
        }

    }

    sealed class GlyphButton : Button
    {
        string glyph = "";
        bool hover, pressed;

        public float GlyphSize = 11f;
        public Color HoverBack = SystemColors.ControlLight, PressedBack = SystemColors.ControlDark, DisabledColor = SystemColors.GrayText;

        public GlyphButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            UseMnemonic = false;
        }

        public string Glyph
        {
            get => glyph;
            set { glyph = value ?? ""; Invalidate(); }
        }

        double hoverAmount;   // 0..1, fades in and out

        void FadeHover(double to, int ms)
        {
            double from = hoverAmount;
            Anim.Run(this, ms, t => { hoverAmount = from + (to - from) * t; Invalidate(); });
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; FadeHover(1, 110); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; FadeHover(0, 180); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Enabled && (hoverAmount > 0.01 || pressed))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var fill = pressed ? PressedBack : Ui.Blend(BackColor, HoverBack, Math.Min(1, hoverAmount));
                using (var path = Ui.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(8)))
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, glyph, Ui.GlyphFont(GlyphSize), ClientRectangle, Enabled ? ForeColor : DisabledColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    static class MemoryTrim
    {
        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr process);

        // Gives this window's own memory back to Windows (the engine's processes are handled by sleep and unload).
        public static void Trim()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                using (var self = Process.GetCurrentProcess()) EmptyWorkingSet(self.Handle);
            }
            catch { }
        }
    }
}
