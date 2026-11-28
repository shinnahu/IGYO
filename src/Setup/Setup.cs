using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using IGYO;

namespace IGYOSetup
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var options = SetupOptions.Parse(args);
            if (options.Silent)
            {
                try
                {
                    var exe = Installer.Install(options, null);
                    if (options.Launch) Process.Start(exe);
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "IGYOSetup.log"), ex.ToString()); } catch { }
                    return 1;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(options));
            return 0;
        }
    }

    // The setup window is shown in the language Windows is shown in, for the languages the browser itself comes in.
    // (The browser's own language files are inside the package and not unpacked yet when this window opens.)
    static class Words
    {
        // English text -> { Spanish, Japanese }
        static readonly System.Collections.Generic.Dictionary<string, string[]> Table = new System.Collections.Generic.Dictionary<string, string[]>
        {
            ["{0} Setup"] = new[] { "Instalación de {0}", "{0} セットアップ" },
            ["Web browser"] = new[] { "Navegador web", "ウェブブラウザー" },
            ["Install location"] = new[] { "Ubicación de instalación", "インストール先" },
            ["Change…"] = new[] { "Cambiar…", "変更…" },
            ["Desktop shortcut"] = new[] { "Acceso directo en el escritorio", "デスクトップにショートカットを作成" },
            ["Start menu shortcut"] = new[] { "Acceso directo en el menú Inicio", "スタート メニューにショートカットを作成" },
            ["Open when it's done"] = new[] { "Abrir al terminar", "完了したら開く" },
            ["Install"] = new[] { "Instalar", "インストール" },
            ["Installing…"] = new[] { "Instalando…", "インストール中…" },
            ["Copying files…"] = new[] { "Copiando archivos…", "ファイルをコピー中…" },
            ["Copying files… {0}%"] = new[] { "Copiando archivos… {0}%", "ファイルをコピー中… {0}%" },
            ["Creating shortcuts…"] = new[] { "Creando accesos directos…", "ショートカットを作成中…" },
            ["{0} is ready"] = new[] { "{0} está listo", "{0} の準備ができました" },
            ["Find it on your desktop and in the Start menu."] = new[] { "Lo encuentras en el escritorio y en el menú Inicio.", "デスクトップとスタート メニューから開けます。" },
            ["Open {0}"] = new[] { "Abrir {0}", "{0} を開く" },
            ["Close"] = new[] { "Cerrar", "閉じる" },
            ["Setup didn't finish: {0}"] = new[] { "La instalación no terminó: {0}", "セットアップを完了できませんでした: {0}" },
            ["{0} is open. Close it and run setup again."] = new[] { "{0} está abierto. Ciérralo y vuelve a ejecutar la instalación.", "{0} が開いています。閉じてから、もう一度セットアップを実行してください。" },
            ["Waiting for {0} to close…"] = new[] { "Esperando a que {0} se cierre…", "{0} が閉じるのを待っています…" },
            ["Something is still using {0}'s files. Close it, or restart the PC, and run setup again."] = new[] { "Algo sigue usando los archivos de {0}. Ciérralo, o reinicia el PC, y vuelve a ejecutar la instalación.", "{0} のファイルがまだ使用されています。使用中のプログラムを閉じるか PC を再起動してから、もう一度セットアップを実行してください。" },
            ["Close {0} first. {1} replaces it and keeps your profiles and bookmarks."] = new[] { "Cierra primero {0}. {1} lo reemplaza y conserva tus perfiles y marcadores.", "先に {0} を閉じてください。{1} がそれを置き換え、プロファイルとブックマークは保持されます。" },
            ["Includes Microsoft's WebView2 engine. Installing means accepting Microsoft's licence terms for it."] = new[] { "Incluye el motor WebView2 de Microsoft. Al instalar aceptas los términos de licencia de Microsoft para ese motor.", "Microsoft の WebView2 エンジンが含まれます。インストールすると、そのライセンス条項に同意したことになります。" },
            ["Read the terms"] = new[] { "Leer los términos", "条項を読む" },
            ["Color"] = new[] { "Color", "カラー" },
            ["Subculture"] = new[] { "Subcultura", "サブカル" },
            ["Periwinkle"] = new[] { "Pervinca", "ペリウィンクル" },
            ["Acid"] = new[] { "Ácido", "アシッド" },
            ["Yellow"] = new[] { "Amarillo", "イエロー" },
            ["Hot pink"] = new[] { "Rosa intenso", "ホットピンク" },
            ["Sakura"] = new[] { "Sakura", "桜色" },
            ["Beige"] = new[] { "Beige", "ベージュ" },
            ["Blue"] = new[] { "Azul", "ブルー" },
            ["Violet"] = new[] { "Violeta", "バイオレット" },
            ["Green"] = new[] { "Verde", "グリーン" },
            ["Amber"] = new[] { "Ámbar", "アンバー" },
            ["Rose"] = new[] { "Rosa", "ローズ" },
            ["Teal"] = new[] { "Verde azulado", "ティール" },
        };

        static readonly int Column = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? 0
            : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? 1 : -1;

        public static string T(string text, params object[] args)
        {
            string s = Column >= 0 && Table.TryGetValue(text, out var row) ? row[Column] : text;
            for (int i = 0; i < args.Length; i++) s = s.Replace("{" + i + "}", Convert.ToString(args[i]));
            return s;
        }
    }

    sealed class SetupOptions
    {
        public bool Silent, Desktop = true, StartMenu = true, Launch = true;
        public string Color;   // the color picked in the window (#RRGGBB): the theme's accent and the profile's circle; null leaves them
        public string Theme;   // with it, the theme it stands for ("subculture"): the browser starts in it, with its own accent
        public string Dir = DefaultDir, ShortcutDir, RegKey = InstallInfo.DefaultRegKey, EarlierRoot;

        public static string ProgramsRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

        public static string DefaultDir => Path.Combine(ProgramsRoot, InstallInfo.AppName);

        // /S  /D=<folder>  /NOSHORTCUTS  /NOLAUNCH  (and, for testing: /SHORTCUTDIR=<folder>  /REGKEY=<name>,
        // /EARLIERROOT=<folder>: look for an install under the browser's earlier names there instead of in Programs, and
        // /COLOR=#RRGGBB: a silent install that carries a color, as if it had been picked in the window, and with it
        // /THEME=subculture: the Subculture pick)
        public static SetupOptions Parse(string[] args)
        {
            var o = new SetupOptions();
            foreach (var a in args)
            {
                var u = a.ToUpperInvariant();
                if (u == "/S" || u == "/SILENT") o.Silent = true;
                else if (u.StartsWith("/D=")) o.Dir = a.Substring(3).Trim('"');
                else if (u.StartsWith("/SHORTCUTDIR=")) o.ShortcutDir = a.Substring(13).Trim('"');
                else if (u.StartsWith("/REGKEY=")) o.RegKey = a.Substring(8).Trim('"');
                else if (u.StartsWith("/EARLIERROOT=")) o.EarlierRoot = a.Substring(13).Trim('"');
                else if (u.StartsWith("/COLOR=")) o.Color = a.Substring(7).Trim('"');
                else if (u.StartsWith("/THEME=")) o.Theme = a.Substring(7).Trim('"').ToLowerInvariant();
                else if (u == "/NOSHORTCUTS") o.Desktop = o.StartMenu = false;
                else if (u == "/NOLAUNCH") o.Launch = false;
            }
            return o;
        }
    }

    static class Installer
    {
        static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        // Nothing of a copy that is already installed is touched until all of the new one is on the disk beside it
        // and everything of the old one is known to be free: the new copy is unpacked into a folder of its own
        // inside the install folder, then each thing of the old copy is moved aside and the new one moved into its
        // place (the engine's folder first: a folder with a file in use cannot be moved, and nothing has changed
        // then). If anything fails on the way, what was moved is moved back. Profiles and bookmarks live elsewhere
        // and are not touched at all.
        public static string Install(SetupOptions o, IProgress<int> progress, Action<string> say = null)
        {
            var dir = Path.GetFullPath(o.Dir).TrimEnd('\\');
            var exe = Path.Combine(dir, InstallInfo.ExeName);
            if (IsRunning(InstallInfo.ExeName, dir)) throw new InvalidOperationException(Words.T("{0} is open. Close it and run setup again.", InstallInfo.AppName));
            // A test install (its own registry key) leaves real installs alone, unless it is given a folder of its own to look in.
            if (o.EarlierRoot != null) RemoveEarlierInstalls(o.EarlierRoot, false, dir);
            else if (o.RegKey == InstallInfo.DefaultRegKey) RemoveEarlierInstalls(SetupOptions.ProgramsRoot, true, dir);
            Directory.CreateDirectory(dir);
            RemoveWorkFolders(dir);   // of a run that was cut short

            string id = Guid.NewGuid().ToString("N").Substring(0, 6);
            string fresh = Path.Combine(dir, NewPrefix + id), old = Path.Combine(dir, OldPrefix + id);
            bool swapped = false;
            try
            {
                Unpack(fresh, progress);

                // The engine of a copy that was closed a moment ago may still be ending; one that something of its
                // own keeps running never ends by itself. Its browser is closed, so it is waited for and then ended.
                if (InstallInfo.RunningFrom(Path.Combine(dir, "runtime")).Count > 0)
                {
                    say?.Invoke(Words.T("Waiting for {0} to close…", InstallInfo.AppName));
                    InstallInfo.EndEngineLeftovers(dir, 3000);
                    say?.Invoke(null);
                }
                if (IsRunning(InstallInfo.ExeName, dir)) throw new InvalidOperationException(Words.T("{0} is open. Close it and run setup again.", InstallInfo.AppName));
                Swap(dir, fresh, old);
                swapped = true;
            }
            finally
            {
                TryRemove(fresh);
                // The copy that was installed before is only thrown away once the new one stands in its place.
                // (Had something of it not found its way back after a failure, it is left where it is.)
                if (swapped) TryRemove(old);
                else try { if (Directory.Exists(old) && !Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old); } catch { }
            }

            if (IsWindows10()) GrantEngineAccess(Path.Combine(dir, "runtime"));

            var marker = new InstallInfo.Marker { RegKey = o.RegKey, Version = Version };
            if (InstallInfo.IsColor(o.Color))
            {
                marker.Color = o.Color.ToUpperInvariant();
                marker.ColorId = Guid.NewGuid().ToString("N");
                if (InstallInfo.IsThemeId(o.Theme)) marker.Theme = o.Theme;
            }
            var desktop = o.ShortcutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenu = o.ShortcutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (o.Desktop)
            {
                var link = Path.Combine(desktop, InstallInfo.AppName + ".lnk");
                InstallInfo.CreateShortcut(link, exe, InstallInfo.Tagline);
                marker.Shortcuts.Add(link);
            }
            if (o.StartMenu)
            {
                var link = Path.Combine(startMenu, InstallInfo.AppName + (o.ShortcutDir != null && o.Desktop ? " (Start menu)" : "") + ".lnk");
                InstallInfo.CreateShortcut(link, exe, InstallInfo.Tagline);
                marker.Shortcuts.Add(link);
            }
            InstallInfo.WriteMarker(dir, marker);
            WriteUninstallEntry(o.RegKey, dir, exe);
            OfferAsBrowser(o.RegKey, exe);
            progress?.Report(100);
            return exe;
        }

        // Windows lists IGYO among the browsers that can be made the default (Settings > Apps > Default apps); the
        // choice itself stays the person's. IGYO is installed and works without this, and its Settings page can put
        // it back, so a failure here is written down instead of stopping the setup.
        static void OfferAsBrowser(string regKey, string exe)
        {
            try
            {
                BrowserRegistration.Register(Registry.CurrentUser, regKey, exe);
                BrowserRegistration.TellWindows();
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "IGYOSetup.log"), "Not listed as a browser: " + ex); } catch { }
            }
        }

        const string NewPrefix = ".new-", OldPrefix = ".old-";

        static void Unpack(string into, IProgress<int> progress)
        {
            Directory.CreateDirectory(into);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
                var root = into + "\\";
                foreach (var entry in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(into, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected path in the package: " + entry.FullName);
                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                    done += entry.Length;
                    progress?.Report((int)(done * 92 / total));
                }
            }
        }

        // Puts the unpacked copy in the place of the installed one, thing by thing at the top of the folder, and
        // puts everything back when a thing cannot be moved.
        static void Swap(string dir, string fresh, string old)
        {
            var names = Directory.GetFileSystemEntries(fresh).Select(Path.GetFileName)
                .OrderBy(n => string.Equals(n, "runtime", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Directory.CreateDirectory(old);
            var aside = new System.Collections.Generic.List<string>();
            var placed = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var name in names)
                {
                    string at = Path.Combine(dir, name);
                    if (!Directory.Exists(at) && !File.Exists(at)) continue;
                    try { Move(at, Path.Combine(old, name), 3000); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException(Words.T("Something is still using {0}'s files. Close it, or restart the PC, and run setup again.", InstallInfo.AppName), ex);
                    }
                    aside.Add(name);
                }
                foreach (var name in names)
                {
                    Move(Path.Combine(fresh, name), Path.Combine(dir, name), 1500);
                    placed.Add(name);
                }
            }
            catch
            {
                foreach (var name in placed) try { Move(Path.Combine(dir, name), Path.Combine(fresh, name), 1500); } catch { }
                foreach (var name in aside) try { Move(Path.Combine(old, name), Path.Combine(dir, name), 1500); } catch { }
                throw;
            }
        }

        // A file or a folder to another name on the same disk. Tried again for a little while when it is refused:
        // a program that has just ended lets go of its files a moment later.
        static void Move(string from, string to, int patienceMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(patienceMs);
            for (;;)
            {
                try
                {
                    if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && DateTime.UtcNow < until)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }

        static void TryRemove(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
        }

        static void RemoveWorkFolders(string dir)
        {
            try
            {
                foreach (var folder in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(folder);
                    if (name.StartsWith(NewPrefix, StringComparison.Ordinal) || name.StartsWith(OldPrefix, StringComparison.Ordinal)) TryRemove(folder);
                }
            }
            catch { }
        }

        // The browser used to be called Tacit, and "No Suggestions Browser" before that: a copy installed under one of
        // those names is removed, with its shortcuts, its entry in Installed apps and its listing as a browser (its
        // data moves to the new name on first start, see AppPaths). Only a folder that setup wrote (it has the
        // marker) is touched. With `inRegistry`, an install in a folder of the person's choosing is found through its
        // entry in Installed apps too.
        static void RemoveEarlierInstalls(string programsRoot, bool inRegistry, string installingTo)
        {
            foreach (var earlier in InstallInfo.EarlierNames)
            {
                var places = new System.Collections.Generic.List<string> { Path.Combine(programsRoot, earlier.AppName) };
                if (inRegistry && earlier.RegKey != null)
                {
                    try
                    {
                        using (var key = Registry.CurrentUser.OpenSubKey(InstallInfo.UninstallKeyPath(earlier.RegKey)))
                            if (key?.GetValue("InstallLocation") is string at && at.Trim().Length > 0) places.Add(at.Trim());
                    }
                    catch { }
                }
                foreach (var place in places)
                {
                    string dir;
                    try { dir = Path.GetFullPath(place).TrimEnd('\\'); } catch { continue; }
                    RemoveEarlierInstall(earlier, dir, installingTo);
                }
            }
        }

        static void RemoveEarlierInstall(InstallInfo.EarlierName earlier, string dir, string installingTo)
        {
            // Never the folder being installed to, nor one that holds this browser under its present name.
            if (string.Equals(dir, installingTo, StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(dir, InstallInfo.ExeName))) return;
            var marker = InstallInfo.ReadMarker(dir);
            if (marker == null) return;
            if (IsRunning(earlier.ExeName, dir))
                throw new InvalidOperationException(Words.T("Close {0} first. {1} replaces it and keeps your profiles and bookmarks.", earlier.AppName, InstallInfo.AppName));
            foreach (var link in marker.Shortcuts ?? new System.Collections.Generic.List<string>())
                try { if (File.Exists(link)) File.Delete(link); } catch { }
            // Its own registry names only: a marker without a key of its own would otherwise name this browser's.
            string regKey = string.IsNullOrWhiteSpace(marker.RegKey) ? earlier.RegKey : marker.RegKey;
            if (regKey != null && !string.Equals(BrowserRegistration.AppKey(regKey), InstallInfo.DefaultRegKey, StringComparison.OrdinalIgnoreCase))
            {
                try { BrowserRegistration.Unregister(Registry.CurrentUser, regKey); BrowserRegistration.TellWindows(); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.UninstallKeyPath(regKey), false); } catch { }
            }
            InstallInfo.EndEngineLeftovers(dir, 3000);   // its engine, should it still be running, holds its files
            // A program that has just ended lets go of its files a moment later: tried again for a few seconds.
            for (var until = DateTime.UtcNow.AddSeconds(5); ; System.Threading.Thread.Sleep(250))
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); break; }
                catch (Exception) when (DateTime.UtcNow < until) { }
                catch { break; }
            }
        }

        static void WriteUninstallEntry(string regKey, string dir, string exe)
        {
            long size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            using (var key = Registry.CurrentUser.CreateSubKey(InstallInfo.UninstallKeyPath(regKey)))
            {
                key.SetValue("DisplayName", InstallInfo.AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", InstallInfo.AppName);
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size / 1024), RegistryValueKind.DWord);
            }
        }

        static bool IsRunning(string exeName, string dir)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
            {
                try { if ((InstallInfo.PathOf(p.Id) ?? "").StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        static bool IsWindows10()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var build) && build < 22000;
            }
            catch { return false; }
        }

        // Microsoft's instructions for an engine shipped with an app on Windows 10 (not needed on Windows 11).
        static void GrantEngineAccess(string runtimeDir)
        {
            foreach (var sid in new[] { "*S-1-15-2-2", "*S-1-15-2-1" })
                using (var p = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{runtimeDir}\" /grant {sid}:(OI)(CI)(RX)") { CreateNoWindow = true, UseShellExecute = false }))
                    p?.WaitForExit();
        }
    }

    // The setup window: the icon itself (the 異 seal, white on its black stamp) on dark slate, and a color to pick for
    // whatever can be pressed: Subculture (white, as the icon's 異) until another is picked. The pick is also what the
    // browser starts with: Subculture starts it in the Subculture theme, with that theme's own colors, and any other is
    // the theme's accent; either is the color of the profile's circle. Three choices, one big button, smooth progress.
    sealed class SetupForm : Form
    {
        static readonly Color Slate = Color.FromArgb(0x24, 0x26, 0x2C),   // the header
            Deep = Color.FromArgb(0x1C, 0x1E, 0x23),                       // the rest of the window
            Well = Color.FromArgb(0x15, 0x16, 0x1A),                       // the box with the folder in it
            Bone = Color.FromArgb(0xEE, 0xEF, 0xEB),                       // text
            Kanji = Color.White, Stamp = Color.FromArgb(0x0B, 0x0B, 0x0B),   // the icon: its 異, white on its black stamp
            Muted = Color.FromArgb(0x9A, 0xA1, 0xA9), Faint = Color.FromArgb(0x6C, 0x72, 0x7B),
            Line = Color.FromArgb(0x34, 0x37, 0x3F), Off = Color.FromArgb(0x3A, 0x3D, 0x46), Bad = Color.FromArgb(0xFF, 0x8A, 0x80);

        // The icon as the program wears it by default (Assets\AppIcons\icons.json, "ink"; tools\make-app-icons.ps1): the
        // 異 seal, 異 in white carved out of a black stamp (Kanji and Stamp above).

        // What to pick from: Subculture first (the theme, in its own white: the buttons, the switches, the progress and
        // the circle's color), then the colors round the color wheel and beige last. (The browser offers the same colors
        // as accents, in the same order.) Subculture unless another is picked (his words, 27 Nov 2026: "My bad setup
        // button should be white I think", and of the Subculture theme: "make it default"). A menu shows the picked one
        // with its name; it opens a list of all.
        const int ThemePick = 0;
        const string PickedTheme = "subculture";
        static readonly (string Name, Color Value)[] Swatches =
        {
            ("Subculture", Hex(0xF2F2F2)),
            ("Rose", Hex(0xE11D48)), ("Amber", Hex(0xD97706)), ("Yellow", Hex(0xFFD500)), ("Acid", Hex(0xC5F135)), ("Green", Hex(0x16A34A)), ("Teal", Hex(0x0D9488)),
            ("Blue", Hex(0x2563EB)), ("Periwinkle", Hex(0x8AA4FF)), ("Violet", Hex(0x7C3AED)), ("Hot pink", Hex(0xFE58C9)), ("Sakura", Hex(0xFF99C5)), ("Beige", Hex(0xD9C6A2)),
        };
        int picked = ThemePick, hoverSwatch = -1;
        bool menuOpen;
        Rectangle menuBox;
        Color Accent => Swatches[picked].Value;                        // buttons, switches, progress
        Color AccentLit => Lit(Accent);                                // the same under the pointer
        Color AccentInk => Ink(Accent);                                // what stands on it
        Rectangle[] swatchRects = new Rectangle[Swatches.Length];

        // Under the pointer a color lights up; white can't, so it greys a little instead.
        static Color Lit(Color c) => c.R + c.G + c.B >= 3 * 0xE6 ? Mix(c, Color.Black, 0.12f) : Mix(c, Color.White, 0.2f);

        // What a pick paints with: its own color, lit under the pointer; alpha: how much of it shows.
        static Brush PaintFor(int pick, RectangleF box, bool lit = false, int alpha = 255)
        {
            var c = Swatches[pick].Value;
            return new SolidBrush(Color.FromArgb(alpha, lit ? Lit(c) : c));
        }
        Brush AccentPaint(RectangleF box, bool lit = false, int alpha = 255) => PaintFor(picked, box, lit, alpha);

        static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        static string HexOf(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        static Color Mix(Color a, Color b, float t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // Dark text on a light color, white on a dark one: whichever stands out more against it (WCAG's contrast).
        static Color Ink(Color c)
        {
            double Lin(int v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            double Lum(Color k) => 0.2126 * Lin(k.R) + 0.7152 * Lin(k.G) + 0.0722 * Lin(k.B);
            var dark = Color.FromArgb(0x16, 0x17, 0x1B);
            double l = Lum(c);
            return (l + 0.05) / (Lum(dark) + 0.05) >= 1.05 / (l + 0.05) ? dark : Color.White;
        }

        readonly SetupOptions options;
        readonly Timer animation = new Timer { Interval = 15 };
        // Microsoft's licence terms for the engine that is inside the package (the WebView2 Runtime).
        const string EngineTerms = "https://developer.microsoft.com/microsoft-edge/api/eula/webview2";
        Rectangle installButton, browseLink, closeLink, termsLink;
        bool hoverInstall, desktop = true, startMenu = true, launch = true, installing, finished;
        string status = "", error;
        float shown, target;   // progress 0..100, drawn smoothly
        Rectangle[] toggles = new Rectangle[3];

        public SetupForm(SetupOptions o)
        {
            options = o;
            // The window starts on Subculture, also over an install that had another color picked: left on it, the
            // update brings the Subculture look (see ProfileStore.TakeSetupColor); the color picked then can be picked
            // again.
            Text = Words.T("{0} Setup", InstallInfo.AppName);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(560, 580);
            KeyPreview = true;   // Esc closes the colors' list
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Deep;
            ForeColor = Bone;
            Font = new Font("Segoe UI", 9.75f);
            DoubleBuffered = true;
            animation.Tick += (s, e) =>
            {
                shown += (target - shown) * 0.18f;
                if (Math.Abs(target - shown) < 0.3f) shown = target;
                Invalidate();
            };
            animation.Start();
        }

        int S(int v) => LogicalToDeviceUnits(v);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        // The title bar goes dark like the window (Windows 10 2004 and later), and takes the logo's slate where
        // Windows 11 lets it have a color of its own. Elsewhere it stays as Windows draws it.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1, caption = Slate.R | Slate.G << 8 | Slate.B << 16;
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, pad = S(36);

            // Header: 異 large and faint behind, in the picked color, and the icon itself beside the name: the 異 seal,
            // white on its black stamp.
            var header = new Rectangle(0, 0, w, S(150));
            using (var back = new SolidBrush(Slate)) g.FillRectangle(back, header);
            var clip = g.Clip;
            g.SetClip(header);
            var bigBox = new RectangleF(w - S(206), -S(16), S(182), S(182));   // most of it in the header
            using (var big = Mark.Shape(bigBox))
            using (var faint = AccentPaint(bigBox, alpha: 22))
                g.FillPath(faint, big);
            g.Clip = clip;
            using (var hairline = new Pen(Line)) g.DrawLine(hairline, 0, header.Bottom - 1, w, header.Bottom - 1);
            var icon = new RectangleF(pad - S(2), S(39), S(72), S(72));
            using (var stamp = Rounded(Rectangle.Round(icon), (int)Math.Round(icon.Width * Mark.Corner / 256)))   // the stamp's corner: 36.57 of its 256
            using (var black = new SolidBrush(Stamp))
                g.FillPath(black, stamp);
            using (var mark = Mark.OnTile(icon))
            using (var white = new SolidBrush(Kanji))
                g.FillPath(white, mark);
            int nameX = pad + S(94);
            using (var name = Wordmark.Shape(nameX, S(70), S(24)))   // the logo book's name: its baseline, and its capitals' height
            using (var bone = new SolidBrush(Bone))
                g.FillPath(bone, name);
            TextRenderer.DrawText(g, Words.T(InstallInfo.Tagline), Font, new Point(nameX, S(86)), Muted, Slate);

            int y = header.Bottom + S(26);
            if (finished)
            {
                using (var big = new Font("Segoe UI Semibold", 15f)) TextRenderer.DrawText(g, Words.T("{0} is ready", InstallInfo.AppName), big, new Point(pad, y), Bone, Deep);
                TextRenderer.DrawText(g, Words.T("Find it on your desktop and in the Start menu."), Font, new Point(pad, y + S(36)), Muted, Deep);
                DrawButton(g, Words.T("Open {0}", InstallInfo.AppName), true);
                closeLink = DrawLink(g, Words.T("Close"), new Point(pad, installButton.Top + S(12)), Deep);
                return;
            }

            using (var label = new Font("Segoe UI Semibold", 9.75f)) TextRenderer.DrawText(g, Words.T("Install location"), label, new Point(pad, y), Bone, Deep);
            var box = new Rectangle(pad, y + S(24), w - pad * 2, S(38));
            using (var path = Rounded(box, S(8)))
            {
                using (var fill = new SolidBrush(Well)) g.FillPath(fill, path);
                using (var pen = new Pen(Line)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, options.Dir, Font, new Rectangle(box.X + S(12), box.Y, box.Width - S(100), box.Height), Bone, Well,
                TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);
            browseLink = installing ? Rectangle.Empty : DrawLink(g, Words.T("Change…"), new Point(box.Right - S(74), box.Y + S(10)), Well);

            // The color: a menu with the picked one and its name; the buttons and switches take a pick at once.
            y = box.Bottom + S(18);
            Array.Clear(swatchRects, 0, swatchRects.Length);
            using (var label = new Font("Segoe UI Semibold", 9.75f)) TextRenderer.DrawText(g, Words.T("Color"), label, new Point(pad, y), Bone, Deep);
            DrawMenuBox(g, pad, y + S(26));
            y += S(80);

            var labels = new[] { Words.T("Desktop shortcut"), Words.T("Start menu shortcut"), Words.T("Open when it's done") };
            var values = new[] { desktop, startMenu, launch };
            for (int i = 0; i < 3; i++)   // one per line, so long labels never collide
            {
                var r = new Rectangle(pad, y + i * S(34), S(40), S(22));
                var labelWidth = TextRenderer.MeasureText(labels[i], Font).Width;
                toggles[i] = new Rectangle(r.X, r.Y - S(4), S(50) + labelWidth + S(8), S(30));
                DrawToggle(g, r, values[i]);
                TextRenderer.DrawText(g, labels[i], Font, new Point(r.Right + S(10), r.Y + S(1)), values[i] ? Bone : Muted, Deep);
            }
            y += 3 * S(34) - S(32);

            if (installing || shown > 0)
            {
                var bar = new Rectangle(pad, y + S(48), w - pad * 2, S(6));
                using (var path = Rounded(bar, bar.Height / 2)) using (var back = new SolidBrush(Line)) g.FillPath(back, path);
                var fillRect = new Rectangle(bar.X, bar.Y, Math.Max(bar.Height, (int)(bar.Width * shown / 100f)), bar.Height);
                using (var path = Rounded(fillRect, bar.Height / 2))
                using (var brush = AccentPaint(bar))
                    g.FillPath(brush, path);
            }
            int said = installing || shown > 0 ? y + S(64) : y + S(46);   // under the progress bar; higher when there is none (a message after a failed run)
            TextRenderer.DrawText(g, error ?? status, Font, new Rectangle(pad, said, w - pad * 2, S(40)), error != null ? Bad : Muted, Deep,
                TextFormatFlags.WordBreak);
            DrawButton(g, installing ? Words.T("Installing…") : Words.T("Install"), !installing);

            // Beside the button: whose engine comes with it, and under which terms.
            using (var small = new Font("Segoe UI", 8.25f))
            using (var smallLink = new Font("Segoe UI", 8.25f, FontStyle.Underline))
            {
                // as tall as its words need (three lines in Spanish), the link under it
                var noteText = Words.T("Includes Microsoft's WebView2 engine. Installing means accepting Microsoft's licence terms for it.");
                int noteWidth = installButton.Left - pad - S(18);
                var note = new Rectangle(pad, installButton.Top - S(2), noteWidth,
                    TextRenderer.MeasureText(g, noteText, small, new Size(noteWidth, int.MaxValue), TextFormatFlags.WordBreak).Height);
                TextRenderer.DrawText(g, noteText, small, note, Muted, Deep, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
                var at = new Point(pad, note.Bottom + S(1));
                var text = Words.T("Read the terms");
                TextRenderer.DrawText(g, text, smallLink, at, Bone, Deep);
                termsLink = new Rectangle(at, TextRenderer.MeasureText(text, smallLink));
            }
            if (menuOpen && !installing) DrawMenuList(g);   // over everything else
        }

        // ----- the color -----

        // The menu: a box with the picked color and its name; it opens a list of all of them.
        void DrawMenuBox(Graphics g, int x0, int top)
        {
            menuBox = new Rectangle(x0, top, S(230), S(36));
            var back = menuOpen ? Line : Well;
            using (var path = Rounded(menuBox, S(8)))
            {
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
                using (var pen = new Pen(menuOpen ? Faint : Line)) g.DrawPath(pen, path);
            }
            var dotBox = new Rectangle(menuBox.X + S(12), menuBox.Y + (menuBox.Height - S(16)) / 2, S(16), S(16));
            using (var dot = AccentPaint(dotBox)) g.FillEllipse(dot, dotBox);
            TextRenderer.DrawText(g, Words.T(Swatches[picked].Name), Font, new Rectangle(menuBox.X + S(38), menuBox.Y, menuBox.Width - S(70), menuBox.Height), Bone, back,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            int cx = menuBox.Right - S(18), cy = menuBox.Y + menuBox.Height / 2;
            using (var pen = new Pen(Muted, Math.Max(1.5f, S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, menuOpen ? new[] { new Point(cx - S(5), cy + S(2)), new Point(cx, cy - S(3)), new Point(cx + S(5), cy + S(2)) }
                                          : new[] { new Point(cx - S(5), cy - S(2)), new Point(cx, cy + S(3)), new Point(cx + S(5), cy - S(2)) });
        }

        void DrawMenuList(Graphics g)
        {
            int cols = 3, rows = (Swatches.Length + cols - 1) / cols, itemW = S(134), itemH = S(32), inner = S(6);   // shallow enough to end above the button
            var list = new Rectangle(menuBox.X, menuBox.Bottom + S(6), inner * 2 + itemW * cols, inner * 2 + itemH * rows);
            var shadow = list; shadow.Offset(0, S(4));
            using (var path = Rounded(Rectangle.Inflate(shadow, S(2), S(2)), S(12))) using (var b = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillPath(b, path);
            using (var path = Rounded(list, S(10)))
            {
                using (var b = new SolidBrush(Well)) g.FillPath(b, path);
                using (var pen = new Pen(Line)) g.DrawPath(pen, path);
            }
            for (int i = 0; i < Swatches.Length; i++)
            {
                int row = i / cols, col = i % cols;
                var r = new Rectangle(list.X + inner + col * itemW, list.Y + inner + row * itemH, itemW, itemH);
                swatchRects[i] = r;
                var back = i == hoverSwatch ? Line : Well;
                if (i == hoverSwatch) using (var path = Rounded(Rectangle.Inflate(r, -S(2), -S(2)), S(6))) using (var b = new SolidBrush(back)) g.FillPath(b, path);
                var dot = new Rectangle(r.X + S(10), r.Y + (r.Height - S(14)) / 2, S(14), S(14));
                if (i == picked) using (var ring = new Pen(Bone, Math.Max(1.5f, S(2)))) g.DrawEllipse(ring, Rectangle.Inflate(dot, S(3), S(3)));
                using (var b = PaintFor(i, dot)) g.FillEllipse(b, dot);
                TextRenderer.DrawText(g, Words.T(Swatches[i].Name), Font, new Rectangle(r.X + S(34), r.Y, r.Width - S(36), r.Height), i == picked ? Bone : Muted, back,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }

        // In the picked color: under the pointer, it lights up; while setup works, it steps back.
        void DrawButton(Graphics g, string text, bool enabled)
        {
            int w = S(170), h = S(44);
            installButton = new Rectangle(ClientSize.Width - S(36) - w, ClientSize.Height - S(28) - h, w, h);
            var fill = !enabled ? Off : hoverInstall ? AccentLit : Accent;
            using (var path = Rounded(installButton, S(10)))
            using (var brush = enabled ? AccentPaint(installButton, hoverInstall) : new SolidBrush(Off))
                g.FillPath(brush, path);
            using (var font = new Font("Segoe UI", 10.5f, FontStyle.Bold))   // (bold: his word, 19 Nov 2026)
                TextRenderer.DrawText(g, text, font, installButton, enabled ? AccentInk : Muted, fill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        Rectangle DrawLink(Graphics g, string text, Point at, Color back)
        {
            using (var font = new Font(Font, FontStyle.Underline))
            {
                var size = TextRenderer.MeasureText(text, font);
                TextRenderer.DrawText(g, text, font, at, Bone, back);
                return new Rectangle(at, size);
            }
        }

        void DrawToggle(Graphics g, Rectangle r, bool on)
        {
            using (var path = Rounded(r, r.Height / 2))
            using (var brush = on ? AccentPaint(r) : new SolidBrush(Off))
                g.FillPath(brush, path);
            int d = r.Height - S(6);
            var knob = new Rectangle(on ? r.Right - d - S(3) : r.X + S(3), r.Y + S(3), d, d);
            using (var brush = new SolidBrush(on ? AccentInk : Muted)) g.FillEllipse(brush, knob);
        }

        // Microsoft publishes these terms as data for its own download page, not as a page of their own. They are
        // fetched from Microsoft when asked for (never copied into this program), written out as a page and opened
        // in the PC's browser. If that fails, Microsoft's address is opened as it is.
        static void ShowEngineTerms()
        {
            try
            {
                string json;
                using (var web = new System.Net.WebClient { Encoding = System.Text.Encoding.UTF8 }) json = web.DownloadString(EngineTerms);
                var data = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }
                    .Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                var html = data != null && data.TryGetValue("evergreenHtml", out var value) ? value as string : null;
                if (string.IsNullOrWhiteSpace(html)) throw new InvalidDataException();
                var file = Path.Combine(Path.GetTempPath(), "Microsoft Edge WebView2 Runtime licence terms.html");
                File.WriteAllText(file, "<!doctype html><meta charset=\"utf-8\"><title>Microsoft Edge WebView2 Runtime: licence terms</title>" +
                    "<body style=\"font: 15px/1.5 'Segoe UI', sans-serif; max-width: 820px; margin: 32px auto; padding: 0 20px\">" + html, System.Text.Encoding.UTF8);
                Process.Start(file);
            }
            catch
            {
                try { Process.Start(EngineTerms); } catch { }
            }
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && menuOpen) { menuOpen = false; hoverSwatch = -1; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = installButton.Contains(e.Location);
            int swatch = Array.FindIndex(swatchRects, r => r.Contains(e.Location));
            bool clickable = over || browseLink.Contains(e.Location) || closeLink.Contains(e.Location) || (!finished && termsLink.Contains(e.Location))
                || (!installing && !finished && (toggles.Any(t => t.Contains(e.Location)) || swatch >= 0 || menuBox.Contains(e.Location)));
            Cursor = clickable ? Cursors.Hand : Cursors.Default;
            if (over != hoverInstall || swatch != hoverSwatch) { hoverInstall = over; hoverSwatch = swatch; Invalidate(); }
        }

        protected override async void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (finished)
            {
                if (installButton.Contains(e.Location)) { try { Process.Start(Path.Combine(Path.GetFullPath(options.Dir), InstallInfo.ExeName)); } catch { } Close(); }
                else if (closeLink.Contains(e.Location)) Close();
                return;
            }
            if (termsLink.Contains(e.Location))
            {
                await Task.Run(() => ShowEngineTerms());
                return;
            }
            if (installing) return;
            if (menuOpen || menuBox.Contains(e.Location))
            {
                // the menu: a click on its box opens it; once open, any click closes it (on a color, after picking it: the
                // window takes the color at once)
                if (menuOpen) { int i = Array.FindIndex(swatchRects, r => r.Contains(e.Location)); if (i >= 0) picked = i; }
                menuOpen = !menuOpen;
                hoverSwatch = -1;
                Invalidate();
                return;
            }
            for (int i = 0; i < 3; i++)
                if (toggles[i].Contains(e.Location))
                {
                    if (i == 0) desktop = !desktop; else if (i == 1) startMenu = !startMenu; else launch = !launch;
                    Invalidate();
                    return;
                }
            if (browseLink.Contains(e.Location))
            {
                using (var d = new FolderBrowserDialog { SelectedPath = options.Dir })
                    if (d.ShowDialog(this) == DialogResult.OK) options.Dir = Path.Combine(d.SelectedPath, InstallInfo.AppName);
                Invalidate();
                return;
            }
            if (!installButton.Contains(e.Location)) return;
            installing = true;
            error = null;
            status = Words.T("Copying files…");
            options.Desktop = desktop;
            options.StartMenu = startMenu;
            options.Color = HexOf(Accent);
            options.Theme = picked == ThemePick ? PickedTheme : null;
            bool waiting = false;   // while the engine of a copy that was just closed is waited for, the line says so
            var progress = new Progress<int>(p => { target = p; if (!waiting) status = p < 92 ? Words.T("Copying files… {0}%", p) : Words.T("Creating shortcuts…"); });
            try
            {
                var exe = await Task.Run(() => Installer.Install(options, progress, text => { waiting = text != null; if (text != null) status = text; }));
                target = 100;
                await Task.Delay(450);
                if (launch) { Process.Start(exe); Close(); return; }
                finished = true;
            }
            catch (Exception ex)
            {
                error = Words.T("Setup didn't finish: {0}", ex.Message);
                target = shown = 0;
            }
            finally
            {
                installing = false;
                Invalidate();
            }
        }
    }

    // IGYO's mark, drawn as the app's icon draws it (tools\make-icon.ps1, IGYOSeal: change both together): since 27 Nov
    // 2026 the 異 seal, 異 (the first kanji of 異形, IGYO, in Dela Gothic One) carved out of a square stamp. These are
    // the kanji's contours on the stamp's own 256 x 256 box (the stamp fills it, its corners round by Corner), each a
    // start point and then three points a curve; its counters are contours of their own, which the alternate fill
    // leaves the stamp's color.
    static class Mark
    {
        public const float Corner = 36.57f;   // the stamp's corner, of its 256
        static readonly float[][] Glyph =
        {
            new[] { 168.78f, 168.35f, 176.46f, 170.1f, 184.5f, 171.52f, 192.91f, 172.62f, 201.29f, 173.67f, 212.15f, 174.81f, 225.52f, 176.03f, 220.93f, 180.5f, 215.67f, 186.66f, 209.74f, 194.5f, 205.55f, 199.78f, 202.77f, 203.15f, 201.39f, 204.62f, 179.12f, 202.06f, 154.66f, 195.92f, 128f, 186.21f, 102.89f, 195.8f, 78.43f, 201.94f, 54.61f, 204.62f, 53.27f, 203.15f, 50.51f, 199.78f, 46.32f, 194.5f, 40.35f, 186.66f, 35.07f, 180.5f, 30.48f, 176.03f, 43.89f, 174.81f, 54.78f, 173.67f, 63.15f, 172.62f, 71.52f, 171.52f, 79.56f, 170.1f, 87.28f, 168.35f, 68.96f, 168.35f, 50.63f, 168.35f, 32.3f, 168.35f, 32.3f, 160.16f, 32.3f, 151.97f, 32.3f, 143.79f, 46.36f, 143.79f, 60.42f, 143.79f, 74.48f, 143.79f, 74.48f, 142.51f, 74.48f, 141.23f, 74.48f, 139.95f, 61.7f, 139.95f, 48.92f, 139.95f, 36.14f, 139.95f, 36.14f, 132.45f, 36.14f, 124.95f, 36.14f, 117.46f, 48.92f, 117.46f, 61.7f, 117.46f, 74.48f, 117.46f, 74.48f, 116.09f, 74.48f, 114.73f, 74.48f, 113.37f, 62.39f, 113.37f, 50.31f, 113.37f, 38.22f, 113.37f, 38.22f, 92.69f, 38.22f, 72.01f, 38.22f, 51.32f, 98.09f, 51.32f, 157.97f, 51.32f, 217.84f, 51.32f, 217.84f, 72.01f, 217.84f, 92.69f, 217.84f, 113.37f, 205.73f, 113.37f, 193.63f, 113.37f, 181.52f, 113.37f, 181.52f, 114.73f, 181.52f, 116.09f, 181.52f, 117.46f, 194.3f, 117.46f, 207.08f, 117.46f, 219.86f, 117.46f, 219.86f, 124.95f, 219.86f, 132.45f, 219.86f, 139.95f, 207.08f, 139.95f, 194.3f, 139.95f, 181.52f, 139.95f, 181.52f, 141.23f, 181.52f, 142.51f, 181.52f, 143.79f, 195.58f, 143.79f, 209.64f, 143.79f, 223.7f, 143.79f, 223.7f, 151.97f, 223.7f, 160.16f, 223.7f, 168.35f, 205.39f, 168.35f, 187.08f, 168.35f, 168.78f, 168.35f },
            new[] { 128f, 185.6f, 133.69f, 179.85f, 139.38f, 174.1f, 145.07f, 168.35f, 133.71f, 168.35f, 122.35f, 168.35f, 110.99f, 168.35f, 116.66f, 174.1f, 122.33f, 179.85f, 128f, 185.6f },
            new[] { 109.59f, 89.9f, 100.79f, 89.9f, 92f, 89.9f, 83.2f, 89.9f, 83.2f, 91.18f, 83.2f, 92.46f, 83.2f, 93.74f, 92f, 93.74f, 100.79f, 93.74f, 109.59f, 93.74f, 109.59f, 92.46f, 109.59f, 91.18f, 109.59f, 89.9f },
            new[] { 172.8f, 89.9f, 164.02f, 89.9f, 155.25f, 89.9f, 146.47f, 89.9f, 146.47f, 91.18f, 146.47f, 92.46f, 146.47f, 93.74f, 155.25f, 93.74f, 164.02f, 93.74f, 172.8f, 93.74f, 172.8f, 92.46f, 172.8f, 91.18f, 172.8f, 89.9f },
            new[] { 146.47f, 74.67f, 155.25f, 74.67f, 164.02f, 74.67f, 172.8f, 74.67f, 172.8f, 73.45f, 172.8f, 72.23f, 172.8f, 71.01f, 164.02f, 71.01f, 155.25f, 71.01f, 146.47f, 71.01f, 146.47f, 72.23f, 146.47f, 73.45f, 146.47f, 74.67f },
            new[] { 109.59f, 74.67f, 109.59f, 73.45f, 109.59f, 72.23f, 109.59f, 71.01f, 100.79f, 71.01f, 92f, 71.01f, 83.2f, 71.01f, 83.2f, 72.23f, 83.2f, 73.45f, 83.2f, 74.67f, 92f, 74.67f, 100.79f, 74.67f, 109.59f, 74.67f },
            new[] { 139.4f, 113.37f, 131.82f, 113.37f, 124.24f, 113.37f, 116.66f, 113.37f, 116.66f, 114.73f, 116.66f, 116.09f, 116.66f, 117.46f, 124.24f, 117.46f, 131.82f, 117.46f, 139.4f, 117.46f, 139.4f, 116.09f, 139.4f, 114.73f, 139.4f, 113.37f },
            new[] { 139.4f, 139.95f, 131.82f, 139.95f, 124.24f, 139.95f, 116.66f, 139.95f, 116.66f, 141.23f, 116.66f, 142.51f, 116.66f, 143.79f, 124.24f, 143.79f, 131.82f, 143.79f, 139.4f, 143.79f, 139.4f, 142.51f, 139.4f, 141.23f, 139.4f, 139.95f },
        };

        static GraphicsPath Canvas(float scale, float ox, float oy)
        {
            var path = new GraphicsPath(FillMode.Alternate);
            foreach (var c in Glyph)
            {
                var pts = new PointF[c.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(c[2 * i] * scale + ox, c[2 * i + 1] * scale + oy);
                path.StartFigure();
                path.AddBeziers(pts);
                path.CloseFigure();
            }
            return path;
        }

        // The kanji where the icon puts it on its stamp.
        public static GraphicsPath OnTile(RectangleF tile) => Canvas(tile.Width / 256f, tile.X, tile.Y);

        // The kanji alone, as large as fits in the box, centred in it. (Its own box on the 256 one: 30.48, 51.32,
        // 195.04 x 153.3.)
        public static GraphicsPath Shape(RectangleF box)
        {
            const float x0 = 30.48f, y0 = 51.32f, w = 195.04f, h = 153.3f;
            float k = Math.Min(box.Width / w, box.Height / h);
            return Canvas(k, box.X + (box.Width - w * k) / 2 - x0 * k, box.Y + (box.Height - h * k) / 2 - y0 * k);
        }
    }

    // IGYO's name as the logo book sets it (the make-logo skill, with the 異 seal): Dela Gothic One (SIL Open Font
    // License 1.1), tracked 20 thousandths of the em, outlined, so setup needs no font for it. Its contours in the
    // font's units, y down, the baseline at 0 and the capitals 747 tall, the ink starting at x 0; each a start point
    // and then three points a curve (an O's inside is a contour of its own, left open by the alternate fill).
    static class Wordmark
    {
        const float Cap = 747f;
        static readonly float[][] Contours =
        {
            new[] { 2523f, -369f, 2523f, -487.7f, 2558.8f, -580.3f, 2630.5f, -647f, 2702.2f, -713.7f, 2814.7f, -747f, 2968f, -747f, 3122f, -747f, 3234.8f, -713.7f, 3306.5f, -647f, 3378.2f, -580.3f, 3414f, -487.7f, 3414f, -369f, 3414f, -248.3f, 3378.3f, -155f, 3307f, -89f, 3235.7f, -23f, 3122.7f, 10f, 2968f, 10f, 2815.3f, 10f, 2703f, -23.2f, 2631f, -89.5f, 2559f, -155.8f, 2523f, -249f, 2523f, -369f },
            new[] { 1093f, -414f, 1235.3f, -414f, 1377.7f, -414f, 1520f, -414f, 1520f, -276f, 1520f, -138f, 1520f, 0f, 1461.3f, 0f, 1402.7f, 0f, 1344f, 0f, 1337f, -59f, 1330f, -118f, 1323f, -177f, 1303f, -113.7f, 1267.3f, -66.7f, 1216f, -36f, 1164.7f, -5.3f, 1102f, 10f, 1028f, 10f, 960.7f, 10f, 900.8f, -5f, 848.5f, -35f, 796.2f, -65f, 755.2f, -108f, 725.5f, -164f, 695.8f, -220f, 681f, -285.3f, 681f, -360f, 681f, -479.3f, 716.7f, -573.7f, 788f, -643f, 859.3f, -712.3f, 970.3f, -747f, 1121f, -747f, 1207f, -747f, 1282.8f, -733f, 1348.5f, -705f, 1414.2f, -677f, 1470f, -633.7f, 1516f, -575f, 1486f, -559.7f, 1427.3f, -529f, 1340f, -483f, 1331.7f, -478.3f, 1323.3f, -473.7f, 1315f, -469f, 1289f, -495f, 1258.7f, -514.5f, 1224f, -527.5f, 1189.3f, -540.5f, 1152.7f, -547f, 1114f, -547f, 1050f, -547f, 1002.3f, -530.2f, 971f, -496.5f, 939.7f, -462.8f, 924f, -421.3f, 924f, -372f, 924f, -318.7f, 940.8f, -275.7f, 974.5f, -243f, 1008.2f, -210.3f, 1059.7f, -194f, 1129f, -194f, 1159f, -194f, 1188.2f, -198.3f, 1216.5f, -207f, 1244.8f, -215.7f, 1268.7f, -230.7f, 1288f, -252f, 1223f, -252f, 1158f, -252f, 1093f, -252f, 1093f, -306f, 1093f, -360f, 1093f, -414f },
            new[] { 581f, -527f, 501.7f, -531f, 443f, -533.3f, 405f, -534f, 405f, -420f, 405f, -306f, 405f, -192f, 443.7f, -192.7f, 502.3f, -195f, 581f, -199f, 581f, -132.7f, 581f, -66.3f, 581f, 0f, 387.3f, 0f, 193.7f, 0f, 0f, 0f, 0f, -66.3f, 0f, -132.7f, 0f, -199f, 76.7f, -195f, 135f, -192.7f, 175f, -192f, 175f, -306f, 175f, -420f, 175f, -534f, 135f, -533.3f, 76.7f, -531f, 0f, -527f, 0f, -593.3f, 0f, -659.7f, 0f, -726f, 193.7f, -726f, 387.3f, -726f, 581f, -726f, 581f, -659.7f, 581f, -593.3f, 581f, -527f },
            new[] { 2510f, -726f, 2503.7f, -716.3f, 2497.3f, -706.7f, 2491f, -697f, 2419.7f, -586.3f, 2358.5f, -488.5f, 2307.5f, -403.5f, 2256.5f, -318.5f, 2214f, -240.3f, 2180f, -169f, 2180f, -112.7f, 2180f, -56.3f, 2180f, 0f, 2093f, 0f, 2006f, 0f, 1919f, 0f, 1919f, -62f, 1919f, -124f, 1919f, -186f, 1894.3f, -236f, 1865.3f, -287.5f, 1832f, -340.5f, 1798.7f, -393.5f, 1751.3f, -466.3f, 1690f, -559f, 1642f, -629.7f, 1605.3f, -685.3f, 1580f, -726f, 1673.3f, -726f, 1766.7f, -726f, 1860f, -726f, 1880f, -685.3f, 1912.7f, -625f, 1958f, -545f, 2003.3f, -463.7f, 2034.3f, -406.3f, 2051f, -373f, 2074.3f, -421.7f, 2105.7f, -481.3f, 2145f, -552f, 2191f, -634.7f, 2222f, -692.7f, 2238f, -726f, 2328.7f, -726f, 2419.3f, -726f, 2510f, -726f },
            new[] { 3177f, -369f, 3177f, -427.7f, 3160.7f, -471.3f, 3128f, -500f, 3095.3f, -528.7f, 3042f, -543f, 2968f, -543f, 2894.7f, -543f, 2841.7f, -528.7f, 2809f, -500f, 2776.3f, -471.3f, 2760f, -427.7f, 2760f, -369f, 2760f, -309.7f, 2776.3f, -265.7f, 2809f, -237f, 2841.7f, -208.3f, 2894.7f, -194f, 2968f, -194f, 3042f, -194f, 3095.3f, -208.2f, 3128f, -236.5f, 3160.7f, -264.8f, 3177f, -309f, 3177f, -369f },
        };

        // The name with its left edge at x, its baseline at baseline, its capitals cap pixels tall.
        public static GraphicsPath Shape(float x, float baseline, float cap)
        {
            float k = cap / Cap;
            var path = new GraphicsPath(FillMode.Alternate);
            foreach (var c in Contours)
            {
                var pts = new PointF[c.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(x + c[2 * i] * k, baseline + c[2 * i + 1] * k);
                path.StartFigure();
                path.AddBeziers(pts);
                path.CloseFigure();
            }
            return path;
        }
    }
}
