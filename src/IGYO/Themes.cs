using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IGYO
{
    // A theme is data: colors, fonts, numbers and options, plus the name of a "kit" (a stylesheet in
    // Assets\Themes\kits that draws the shapes, decorations and animations) and optional extra CSS.
    // Built-in themes ship in Assets\Themes; yours live in the data folder and can be exported as one .json file.
    sealed class ThemeDef
    {
        public const string Format = "igyo-theme/1";

        public string format { get; set; } = Format;
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string author { get; set; } = "";
        public string description { get; set; } = "";
        public string inspiredBy { get; set; } = "";
        public string kit { get; set; } = "igyo";
        public string scheme { get; set; } = "dark";
        public Dictionary<string, string> colors { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> fonts { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> shape { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> options { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> fx { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> vars { get; set; } = new Dictionary<string, string>();
        public string wallpaper { get; set; } = "";
        public string css { get; set; } = "";

        [System.Web.Script.Serialization.ScriptIgnore] public bool BuiltIn;

        public bool IsDark => !string.Equals(scheme, "light", StringComparison.OrdinalIgnoreCase);

        // A theme color as a drawing color, for the few things Windows draws (window edge, DevTools header).
        public Color Color(string key, Color fallback)
        {
            if (colors != null && colors.TryGetValue(key, out var value) && ThemeStore.TryParseColor(value, out var color)) return color;
            return fallback;
        }
    }

    // The handful of colors the Windows side of the app needs, taken from the theme.
    sealed class Theme
    {
        public string Name;
        public bool IsDark;
        public Color Frame, Toolbar, Field, Page, Text, Muted, Border, Hover, Pressed, Accent, AccentText;
        public bool SquareCorners;

        public static Theme From(ThemeDef def, string accentOverride)
        {
            bool dark = def.IsDark;
            var t = new Theme
            {
                Name = def.name,
                IsDark = dark,
                Frame = def.Color("frame", dark ? Rgb(0x17181C) : Rgb(0xE4E7EE)),
                Toolbar = def.Color("surface", dark ? Rgb(0x24262C) : Rgb(0xFFFFFF)),
                Field = def.Color("field", dark ? Rgb(0x17181C) : Rgb(0xEEF0F5)),
                Page = def.Color("page", dark ? Rgb(0x1A1B20) : Rgb(0xF4F5F9)),
                Text = def.Color("ink", dark ? Rgb(0xECEEF3) : Rgb(0x1B1F27)),
                Muted = def.Color("ink2", dark ? Rgb(0xA5ABB8) : Rgb(0x5C6473)),
                Border = def.Color("line", dark ? Rgb(0x35373F) : Rgb(0xDFE3EA)),
                Accent = def.Color("accent", dark ? Rgb(0x8AA4FF) : Rgb(0x3D5AFE)),
            };
            if (ThemeStore.TryParseColor(accentOverride, out var accent)) t.Accent = accent;
            t.AccentText = def.Color("accentInk", Luminance(t.Accent) > 0.55 ? System.Drawing.Color.FromArgb(16, 24, 40) : System.Drawing.Color.White);
            t.Hover = Ui.Blend(t.Toolbar, t.Text, 0.09);
            t.Pressed = Ui.Blend(t.Toolbar, t.Text, 0.16);
            t.SquareCorners = def.options != null && def.options.TryGetValue("window", out var w) && w == "square";
            return t;
        }

        static Color Rgb(int v) => System.Drawing.Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Tells Windows how to draw what it still draws: the window's edge, its corners and its system menu.
        public static void ApplyWindow(Form form, Theme t)
        {
            if (form == null || t == null || !form.IsHandleCreated) return;
            try
            {
                int dark = t.IsDark ? 1 : 0;
                DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
                int caption = ColorRef(t.Frame);
                DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);  // DWMWA_CAPTION_COLOR
                int border = ColorRef(Ui.Blend(t.Frame, t.Text, t.IsDark ? 0.14 : 0.22));
                DwmSetWindowAttribute(form.Handle, 34, ref border, 4);   // DWMWA_BORDER_COLOR
                int corners = t.SquareCorners ? 1 : 2;                   // DWMWCP_DONOTROUND / DWMWCP_ROUND
                DwmSetWindowAttribute(form.Handle, 33, ref corners, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE
            }
            catch { }
        }

        static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
    }

    static class ThemeStore
    {
        public const string Auto = "auto";
        const int MaxFileBytes = 6 * 1024 * 1024, MaxCss = 200 * 1024, MaxWallpaper = 5 * 1024 * 1024;

        static readonly Regex IdRule = new Regex("^[a-z0-9][a-z0-9-]{0,47}$", RegexOptions.Compiled);
        static readonly Regex VarRule = new Regex("^--[a-zA-Z0-9-]{1,60}$", RegexOptions.Compiled);
        static readonly Regex KeyRule = new Regex("^[a-zA-Z][a-zA-Z0-9]{0,31}$", RegexOptions.Compiled);
        static readonly Regex Unsafe = new Regex(@"[;{}<>\\]|url\(|@import|expression\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Imports = new Regex(@"@import[^;]*;", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex OutsideUrls = new Regex(@"url\(\s*(['""]?)(?!data:)[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Camel = new Regex("([a-z])([A-Z0-9])", RegexOptions.Compiled);

        static List<ThemeDef> cache;
        static readonly Dictionary<string, string> kitCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static int version = 1;

        public static int Version => version;
        public static string BuiltInDir => Path.Combine(AppPaths.AppDir, "Assets", "Themes");
        public static string UserDir => Path.Combine(AppPaths.DataDir, "Themes");
        static string KitFile(string kit) => Path.Combine(BuiltInDir, "kits", kit + ".css");

        // The look before themes were files, and the two themes that carried the browser's earlier name (Tacit Dark
        // and Tacit Light): those names still open the same look. A look that was taken out opens the default one.
        static readonly Dictionary<string, string> Legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["System"] = Auto, ["Light"] = "igyo-light", ["Dark"] = "igyo-dark", ["Midnight"] = "midnight",
            ["Graphite"] = "igyo-dark", ["Ocean"] = "ocean", ["Forest"] = "forest", ["Rose"] = "rose",
            ["tacit-dark"] = "igyo-dark", ["tacit-light"] = "igyo-light",
            ["guild"] = Auto,   // a look taken out on 17 Nov 2026 (its side rail of tabs upset the other looks when switching)
        };

        public static readonly Dictionary<string, string> LegacyAccents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // round the color wheel, beige last (setup offers the same, in the same order)
            ["Theme default"] = "", ["Rose"] = "#E11D48", ["Amber"] = "#D97706", ["Yellow"] = "#FFD500", ["Acid"] = "#C5F135", ["Green"] = "#16A34A",
            ["Teal"] = "#0D9488", ["Blue"] = "#2563EB", ["Periwinkle"] = "#8AA4FF", ["Violet"] = "#7C3AED", ["Hot pink"] = "#FE58C9", ["Sakura"] = "#FF99C5",
            ["Beige"] = "#D9C6A2",
        };

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }

        // ----- the list -----

        public static IReadOnlyList<ThemeDef> All()
        {
            if (cache != null) return cache;
            var list = new List<ThemeDef>();
            foreach (var (dir, builtIn) in new[] { (BuiltInDir, true), (UserDir, false) })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var def = Parse(File.ReadAllText(file));
                        if (def == null) continue;
                        def.BuiltIn = builtIn;
                        if (!builtIn) def.id = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();   // the file's name is its id
                        if (!IdRule.IsMatch(def.id) || list.Any(t => t.id == def.id)) continue;
                        list.Add(def);
                    }
                    catch { }
                }
            }
            if (list.Count == 0) list.Add(new ThemeDef { id = "igyo-dark", name = "IGYO Dark", BuiltIn = true });
            return cache = list;
        }

        static void Changed()
        {
            cache = null;
            version++;
        }

        public static ThemeDef Find(string id) => All().FirstOrDefault(t => string.Equals(t.id, id, StringComparison.OrdinalIgnoreCase));

        // Settings store a theme id, "auto", or a name from before themes were files.
        public static string Normalize(string setting)
        {
            if (string.IsNullOrWhiteSpace(setting)) return Auto;
            if (Legacy.TryGetValue(setting, out var mapped)) return mapped;
            return setting;
        }

        // A profile's theme: its own once it has chosen one, until then the one in settings (every profile shared
        // that one before each had its own).
        public static string ChosenFor(Profile profile, Settings settings) =>
            !string.IsNullOrWhiteSpace(profile?.Theme) ? profile.Theme : settings?.Theme ?? Auto;

        // A theme was deleted: every profile that had it, and the shared one in settings, go back to Automatic.
        // True when something changed.
        public static bool Forget(string id, IEnumerable<Profile> profiles, Settings settings)
        {
            bool changed = false;
            foreach (var p in profiles ?? Enumerable.Empty<Profile>())
                if (p.Theme != null && Normalize(p.Theme) == id) { p.Theme = Auto; changed = true; }
            if (settings != null && Normalize(settings.Theme) == id) { settings.Theme = Auto; changed = true; }
            return changed;
        }

        public static ThemeDef Resolve(string setting)
        {
            var id = Normalize(setting);
            if (id == Auto) id = SystemPrefersDark() ? "igyo-dark" : "igyo-light";
            return Find(id) ?? Find(SystemPrefersDark() ? "igyo-dark" : "igyo-light") ?? All()[0];
        }

        public static string AccentHex(string setting)
        {
            if (string.IsNullOrWhiteSpace(setting)) return "";
            if (LegacyAccents.TryGetValue(setting, out var hex)) return hex;
            return TryParseColor(setting, out var c) ? Hex(c) : "";
        }

        // ----- reading and checking -----

        public static ThemeDef Parse(string json)
        {
            var def = Json.Deserialize<ThemeDef>(json);
            return def == null ? null : Clean(def);
        }

        // Keeps what a theme may contain and drops the rest, so a file from anywhere is safe to load.
        static ThemeDef Clean(ThemeDef def)
        {
            def.format = ThemeDef.Format;
            def.id = (def.id ?? "").Trim().ToLowerInvariant();
            def.name = Limit(def.name, 60);
            if (def.name.Length == 0) def.name = def.id.Length > 0 ? def.id : L.T("Untitled");
            def.author = Limit(def.author, 60);
            def.description = Limit(def.description, 240);
            def.inspiredBy = Limit(def.inspiredBy, 80);
            def.kit = (def.kit ?? "").Trim().ToLowerInvariant();
            if (!IdRule.IsMatch(def.kit) || !File.Exists(KitFile(def.kit))) def.kit = "igyo";
            def.scheme = string.Equals(def.scheme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            def.colors = CleanMap(def.colors, KeyRule, 64);
            def.fonts = CleanMap(def.fonts, KeyRule, 16);
            def.options = CleanMap(def.options, KeyRule, 48, 40);
            def.fx = CleanMap(def.fx, KeyRule, 32, 40);
            def.vars = CleanMap(def.vars, VarRule, 96);
            def.shape = def.shape ?? new Dictionary<string, object>();
            def.css = def.css ?? "";
            if (def.css.Length > MaxCss) def.css = def.css.Substring(0, MaxCss);
            def.wallpaper = def.wallpaper ?? "";
            if (def.wallpaper.Length > MaxWallpaper || !def.wallpaper.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || def.wallpaper.IndexOfAny(new[] { '"', '\'', ')', '\\', ' ' }) >= 0)
                def.wallpaper = "";
            return def;
        }

        static string Limit(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        static Dictionary<string, string> CleanMap(Dictionary<string, string> map, Regex keyRule, int maxEntries, int maxValue = 400)
        {
            var clean = new Dictionary<string, string>();
            if (map == null) return clean;
            foreach (var pair in map)
            {
                if (clean.Count >= maxEntries) break;
                var value = (pair.Value ?? "").Trim();
                if (pair.Key == null || !keyRule.IsMatch(pair.Key) || value.Length == 0 || value.Length > maxValue || Unsafe.IsMatch(value)) continue;
                clean[pair.Key] = value;
            }
            return clean;
        }

        // ----- writing -----

        public static string ToJson(ThemeDef def) => Json.Write(def);

        static string NewId(string name)
        {
            var slug = new string((name ?? "").ToLowerInvariant().Select(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' ? c : '-').ToArray()).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            if (slug.Length == 0) slug = "theme";
            if (slug.Length > 36) slug = slug.Substring(0, 36).Trim('-');
            var id = slug;
            for (int n = 2; Find(id) != null; n++) id = slug + "-" + n;
            return id;
        }

        // Saves one of your themes. A built-in theme is never changed: saving over one makes your own copy.
        public static ThemeDef Save(ThemeDef def)
        {
            def = Clean(def);
            var existing = Find(def.id);
            if (existing == null || existing.BuiltIn || !IdRule.IsMatch(def.id)) def.id = NewId(def.name);
            def.BuiltIn = false;
            Directory.CreateDirectory(UserDir);
            FileUtil.WriteAtomic(Path.Combine(UserDir, def.id + ".json"), ToJson(def));
            Changed();
            return Find(def.id) ?? def;
        }

        public static ThemeDef Duplicate(string id)
        {
            var source = Find(id);
            if (source == null) return null;
            var copy = Parse(ToJson(source));
            copy.id = "";
            copy.name = Limit(L.T("{0} copy", source.name), 60);
            copy.author = "";
            return Save(copy);
        }

        public static bool Delete(string id)
        {
            var def = Find(id);
            if (def == null || def.BuiltIn) return false;
            try { File.Delete(Path.Combine(UserDir, def.id + ".json")); } catch { return false; }
            Changed();
            return true;
        }

        public static ThemeDef Import(string file)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) throw new InvalidDataException(L.T("That file is empty or too large to be a theme."));
            ThemeDef def;
            try { def = Parse(File.ReadAllText(file)); }
            catch { throw new InvalidDataException(L.T("That file isn't an IGYO theme.")); }
            if (def == null || def.colors.Count == 0 && def.css.Length == 0 && def.vars.Count == 0) throw new InvalidDataException(L.T("That file isn't an IGYO theme."));
            def.id = "";   // always becomes one of your themes, under a free id
            return Save(def);
        }

        public static void Export(string id, string file)
        {
            var def = Find(id) ?? throw new InvalidOperationException(L.T("That theme no longer exists."));
            File.WriteAllText(file, Pretty(ToJson(def)), new UTF8Encoding(false));
        }

        // Indents JSON so an exported theme can be read and edited by hand.
        static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{': case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        sb.Append(c).Append('\n').Append(' ', ++depth * 2); break;
                    case '}': case ']': sb.Append('\n').Append(' ', --depth * 2).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('\n').ToString();
        }

        // ----- what pages load: /theme.css and /theme.js (keep in step with tools\serve.js) -----

        static string Kebab(string key) => Camel.Replace(key, "$1-$2").ToLowerInvariant();

        static string KitCss(string kit)
        {
            if (kitCache.TryGetValue(kit, out var css)) return css;
            try { css = File.ReadAllText(KitFile(kit)); } catch { css = ""; }
#if !DEBUG
            kitCache[kit] = css;   // test builds re-read the file, so a kit can be edited while the browser runs
#endif
            return css;
        }

        public static string BuildCss(ThemeDef def, string accentHex = null)
        {
            var sb = new StringBuilder();
            sb.Append("@layer base, kit, user;\n@layer kit {\n").Append(KitCss(def.kit)).Append("\n}\n:root {\n");
            void Put(string name, string value)
            {
                if (!string.IsNullOrEmpty(value) && !Unsafe.IsMatch(value)) sb.Append("  ").Append(name).Append(": ").Append(value).Append(";\n");
            }
            foreach (var pair in def.colors) Put("--c-" + Kebab(pair.Key), pair.Value);
            foreach (var pair in def.fonts) Put("--f-" + Kebab(pair.Key), pair.Value);
            if (TryNumber(def.shape, "radius", out var radius)) Put("--radius", Math.Max(0, Math.Min(28, radius)).ToString("0.##", CultureInfo.InvariantCulture));
            if (TryNumber(def.shape, "border", out var border)) Put("--bw", Math.Max(0, Math.Min(4, border)).ToString("0.##", CultureInfo.InvariantCulture) + "px");
            foreach (var pair in def.vars) Put(pair.Key, pair.Value);
            if (!string.IsNullOrEmpty(def.wallpaper)) sb.Append("  --wallpaper: url(\"").Append(def.wallpaper).Append("\");\n");
            if (!string.IsNullOrEmpty(accentHex))
            {
                Put("--c-accent", accentHex);
                if (TryParseColor(accentHex, out var accent)) Put("--c-accent-ink", Theme.Luminance(accent) > 0.55 ? "#101828" : "#ffffff");
            }
            sb.Append("}\n@layer user {\n").Append(OutsideUrls.Replace(Imports.Replace(def.css ?? "", ""), "none")).Append("\n}\n");
            return sb.ToString();
        }

        public static Dictionary<string, object> Meta(ThemeDef def) => new Dictionary<string, object>
        {
            ["id"] = def.id, ["name"] = def.name, ["kit"] = def.kit, ["scheme"] = def.scheme,
            ["options"] = def.options, ["fx"] = def.fx, ["v"] = version,
        };

        public static string BuildJs(ThemeDef def) =>
            "(function(t){var r=document.documentElement;r.dataset.kit=t.kit;r.dataset.scheme=t.scheme;r.dataset.theme=t.id;" +
            "for(var k in t.options)r.setAttribute('data-o-'+k,t.options[k]);window.IGYO_THEME=t;})(" + Json.Write(Meta(def)) + ");\n";

        // What the theme list shows for each theme.
        public static Dictionary<string, object> Summary(ThemeDef def) => new Dictionary<string, object>
        {
            ["id"] = def.id, ["name"] = def.name, ["author"] = def.author, ["description"] = def.description,
            ["inspiredBy"] = def.inspiredBy, ["kit"] = def.kit, ["scheme"] = def.scheme, ["builtIn"] = def.BuiltIn,
            ["colors"] = def.colors,
        };

        static bool TryNumber(Dictionary<string, object> map, string key, out double value)
        {
            value = 0;
            if (map == null || !map.TryGetValue(key, out var raw) || raw == null) return false;
            try { value = Convert.ToDouble(raw, CultureInfo.InvariantCulture); return !double.IsNaN(value) && !double.IsInfinity(value); }
            catch { return false; }
        }

        public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static bool TryParseColor(string text, out Color color)
        {
            color = System.Drawing.Color.Empty;
            text = (text ?? "").Trim();
            if (text.Length < 4 || text[0] != '#') return false;
            var hex = text.Substring(1);
            if (hex.Length == 3 || hex.Length == 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length != 6 && hex.Length != 8) return false;
            if (!int.TryParse(hex.Substring(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
            color = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return true;
        }
    }
}
