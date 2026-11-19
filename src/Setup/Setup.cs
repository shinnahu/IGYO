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
        public string Dir = DefaultDir, ShortcutDir, RegKey = InstallInfo.DefaultRegKey, EarlierRoot;

        public static string ProgramsRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

        public static string DefaultDir => Path.Combine(ProgramsRoot, InstallInfo.AppName);

        // /S  /D=<folder>  /NOSHORTCUTS  /NOLAUNCH  (and, for testing: /SHORTCUTDIR=<folder>  /REGKEY=<name>,
        // /EARLIERROOT=<folder>: look for an install under the browser's earlier names there instead of in Programs, and
        // /COLOR=#RRGGBB: a silent install that carries a color, as if it had been picked in the window)
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
            if (InstallInfo.IsColor(o.Color)) { marker.Color = o.Color.ToUpperInvariant(); marker.ColorId = Guid.NewGuid().ToString("N"); }
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

    // The setup window: the icon itself (the white star on its periwinkle tile) on dark slate, and a color to pick for
    // whatever can be pressed, periwinkle until another is picked. The pick is also what the browser
    // starts with: the theme's accent and the color of the profile's circle. Three choices, one big button, smooth progress.
    sealed class SetupForm : Form
    {
        static readonly Color Slate = Color.FromArgb(0x24, 0x26, 0x2C),   // the header
            Deep = Color.FromArgb(0x1C, 0x1E, 0x23),                       // the rest of the window
            Well = Color.FromArgb(0x15, 0x16, 0x1A),                       // the box with the folder in it
            Bone = Color.FromArgb(0xEE, 0xEF, 0xEB),                       // text
            // The icon as the program wears it by default (Assets\AppIcons\icons.json, "periwinkle"; tools\make-app-icons.ps1):
            TileTop = Color.FromArgb(0x9D, 0xB1, 0xFF),                    // its tile, periwinkle running down
            TileBottom = Color.FromArgb(0x4F, 0x5F, 0xE0),                 //   to deep blue
            Star = Color.White,                                            // its star
            Muted = Color.FromArgb(0x9A, 0xA1, 0xA9), Faint = Color.FromArgb(0x6C, 0x72, 0x7B),
            Line = Color.FromArgb(0x34, 0x37, 0x3F), Off = Color.FromArgb(0x3A, 0x3D, 0x46), Bad = Color.FromArgb(0xFF, 0x8A, 0x80);

        // The colors to pick from, round the color wheel and beige last; periwinkle unless another is picked. (The browser
        // offers the same as accents, in the same order.) A menu shows the picked one with its name; it opens a list of all.
        static readonly (string Name, Color Value)[] Swatches =
        {
            ("Rose", Hex(0xE11D48)), ("Amber", Hex(0xD97706)), ("Yellow", Hex(0xFFD500)), ("Acid", Hex(0xC5F135)), ("Green", Hex(0x16A34A)), ("Teal", Hex(0x0D9488)),
            ("Blue", Hex(0x2563EB)), ("Periwinkle", Hex(0x8AA4FF)), ("Violet", Hex(0x7C3AED)), ("Hot pink", Hex(0xFE58C9)), ("Sakura", Hex(0xFF99C5)), ("Beige", Hex(0xD9C6A2)),
        };
        int picked = Array.FindIndex(Swatches, c => c.Name == "Periwinkle"), hoverSwatch = -1;
        bool menuOpen;
        Rectangle menuBox;
        Color Accent => Swatches[picked].Value;                        // buttons, switches, progress
        Color AccentLit => Mix(Accent, Color.White, 0.2f);             // the same under the pointer
        Color AccentInk => Ink(Accent);                                // what stands on it
        Rectangle[] swatchRects = new Rectangle[Swatches.Length];

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
        readonly Font wordmark = Pick(24f, "Bahnschrift SemiBold", "Segoe UI Semibold");
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
            // Run again over an install, the window starts on the color picked then, so that installing again without
            // touching it changes nothing the browser was given before (see ProfileStore.TakeSetupColor).
            var before = InstallInfo.ReadMarker(o.Dir)?.Color;
            int earlier = InstallInfo.IsColor(before) ? Array.FindIndex(Swatches, c => string.Equals(HexOf(c.Value), before, StringComparison.OrdinalIgnoreCase)) : -1;
            if (earlier >= 0) picked = earlier;
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

        // A font by the first of these names that Windows has.
        static Font Pick(float size, params string[] families)
        {
            foreach (var family in families)
            {
                var font = new Font(family, size);
                if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase)) return font;
                font.Dispose();
            }
            return new Font("Segoe UI", size);
        }

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

            // Header: the star large and faint behind, in the picked color, and the icon itself beside the name: the white
            // star on its periwinkle tile.
            var header = new Rectangle(0, 0, w, S(150));
            using (var back = new SolidBrush(Slate)) g.FillRectangle(back, header);
            var clip = g.Clip;
            g.SetClip(header);
            using (var big = Mark.Shape(new RectangleF(w - S(206), -S(16), S(182), S(182))))   // most of it in the header
            using (var faint = new SolidBrush(Color.FromArgb(22, Accent)))
                g.FillPath(faint, big);
            g.Clip = clip;
            using (var hairline = new Pen(Line)) g.DrawLine(hairline, 0, header.Bottom - 1, w, header.Bottom - 1);
            var icon = new RectangleF(pad - S(2), S(39), S(72), S(72));
            using (var tile = Rounded(Rectangle.Round(icon), (int)Math.Round(icon.Width * 44.48 / 256)))   // the icon's corner: 44.48 of its 256
            using (var tint = new LinearGradientBrush(new PointF(0, icon.Top - 1), new PointF(0, icon.Bottom + 1), TileTop, TileBottom))
                g.FillPath(tint, tile);
            using (var mark = Mark.OnTile(icon))
            using (var star = new SolidBrush(Star))
                g.FillPath(star, mark);
            int nameX = pad + S(94);
            DrawTracked(g, InstallInfo.AppName, wordmark, new Point(nameX, S(40)), Bone, Slate, S(6));
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
                using (var brush = new SolidBrush(Accent))
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
            using (var dot = new SolidBrush(Accent)) g.FillEllipse(dot, new Rectangle(menuBox.X + S(12), menuBox.Y + (menuBox.Height - S(16)) / 2, S(16), S(16)));
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
                using (var b = new SolidBrush(Swatches[i].Value)) g.FillEllipse(b, dot);
                TextRenderer.DrawText(g, Words.T(Swatches[i].Name), Font, new Rectangle(r.X + S(34), r.Y, r.Width - S(36), r.Height), i == picked ? Bone : Muted, back,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
        }

        // Letters set a little apart, as in the logo's name; returns where the last one ends.
        static int DrawTracked(Graphics g, string text, Font font, Point at, Color color, Color back, int spacing)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int x = at.X;
            for (int i = 0; i < text.Length; i++)
            {
                string letter = text[i].ToString();
                TextRenderer.DrawText(g, letter, font, new Point(x, at.Y), color, back, flags);
                x += TextRenderer.MeasureText(g, letter, font, Size.Empty, flags).Width + (i < text.Length - 1 ? spacing : 0);
            }
            return x;
        }

        // In the picked color: under the pointer, it lights up; while setup works, it steps back.
        void DrawButton(Graphics g, string text, bool enabled)
        {
            int w = S(170), h = S(44);
            installButton = new Rectangle(ClientSize.Width - S(36) - w, ClientSize.Height - S(28) - h, w, h);
            var fill = !enabled ? Off : hoverInstall ? AccentLit : Accent;
            using (var path = Rounded(installButton, S(10)))
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);
            using (var font = new Font("Segoe UI Semibold", 10.5f))
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
            using (var brush = new SolidBrush(on ? Accent : Off))
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

    // IGYO's mark, drawn as the app's icon draws it (tools\make-icon.ps1, IGYOSpark: change both together): since 17 Nov
    // 2026 night his own drawing, a ten-point star with softly round tips, as his SVG's cubic curves on his 256 x 256
    // icon box, where the star stands centred (199 x 203 of it): a start, then three points a curve.
    static class Mark
    {
        static readonly float[] P =
        {
            226.25f, 173.44f, 220.21f, 165.6f, 210.02f, 159.52f, 202.34f, 153.46f, 197.24f, 149.44f, 192.03f, 145.55f,
            186.97f, 141.47f, 184.44f, 139.43f, 181.94f, 137.35f, 179.51f, 135.19f, 176.03f, 132.1f, 171.99f, 129.41f,
            175.16f, 124.4f, 177.25f, 121.1f, 181.3f, 119.3f, 184.17f, 116.96f, 193.31f, 109.51f, 202.68f, 102.36f,
            211.87f, 94.98f, 215.66f, 91.94f, 219.47f, 88.9f, 223.07f, 85.64f, 223.91f, 84.88f, 227.7f, 82.16f,
            227.39f, 80.72f, 226.44f, 79.31f, 225.54f, 79.2f, 223.95f, 79.16f, 221.19f, 79.09f, 218.27f, 80.35f,
            215.74f, 81.3f, 207.18f, 84.53f, 198.96f, 88.65f, 190.63f, 92.43f, 186.74f, 94.2f, 182.84f, 95.95f,
            178.84f, 97.46f, 176.79f, 98.23f, 174.74f, 99.05f, 172.57f, 99.43f, 171.42f, 99.63f, 169.16f, 100.06f,
            168.17f, 99.12f, 166.67f, 97.69f, 168.69f, 94.66f, 169.31f, 93.23f, 170.21f, 91.14f, 171.23f, 89.1f,
            172.27f, 87.07f, 173.78f, 84.12f, 175.35f, 81.21f, 176.89f, 78.28f, 177.89f, 76.39f, 179.05f, 74.47f,
            179.73f, 72.44f, 180.05f, 70.98f, 178.61f, 70.17f, 177.37f, 70.06f, 175.75f, 69.92f, 173.94f, 70.84f,
            172.51f, 71.47f, 169.99f, 72.57f, 167.56f, 73.86f, 165.14f, 75.16f, 160.89f, 77.44f, 156.7f, 79.83f,
            152.42f, 82.04f, 150.63f, 82.96f, 148.82f, 83.87f, 146.96f, 84.64f, 145.8f, 85.12f, 143.27f, 86.38f,
            142.09f, 85.69f, 141.68f, 85.45f, 141.37f, 85.06f, 141.08f, 84.68f, 139.55f, 82.65f, 139.41f, 79.62f,
            138.94f, 77.16f, 138.28f, 73.72f, 137.61f, 70.29f, 136.95f, 66.85f, 135.34f, 58.52f, 133.73f, 50.19f,
            132.13f, 41.86f, 131.29f, 37.53f, 130.46f, 33.2f, 129.62f, 28.87f, 129.27f, 27.86f, 128.72f, 27.14f,
            128f, 26.67f, 127.29f, 27.14f, 126.73f, 27.86f, 126.38f, 28.87f, 125.54f, 33.2f, 124.71f, 37.53f,
            123.87f, 41.86f, 122.26f, 50.19f, 120.65f, 58.52f, 119.05f, 66.85f, 118.39f, 70.29f, 117.72f, 73.72f,
            117.06f, 77.16f, 116.59f, 79.62f, 116.45f, 82.65f, 114.92f, 84.68f, 114.63f, 85.06f, 114.33f, 85.45f,
            113.91f, 85.69f, 112.73f, 86.38f, 110.2f, 85.12f, 109.04f, 84.64f, 107.18f, 83.87f, 105.37f, 82.96f,
            103.58f, 82.04f, 99.29f, 79.83f, 95.11f, 77.44f, 90.86f, 75.16f, 88.44f, 73.86f, 86.01f, 72.57f,
            83.49f, 71.47f, 82.06f, 70.84f, 80.25f, 69.92f, 78.63f, 70.06f, 77.38f, 70.17f, 75.95f, 70.98f,
            76.27f, 72.44f, 76.95f, 74.47f, 78.12f, 76.39f, 79.11f, 78.28f, 80.65f, 81.21f, 82.23f, 84.12f,
            83.73f, 87.07f, 84.77f, 89.1f, 85.78f, 91.14f, 86.69f, 93.23f, 87.31f, 94.67f, 89.33f, 97.69f,
            87.83f, 99.12f, 86.85f, 100.06f, 84.59f, 99.63f, 83.43f, 99.43f, 81.27f, 99.06f, 79.21f, 98.24f,
            77.16f, 97.46f, 73.16f, 95.95f, 69.26f, 94.2f, 65.37f, 92.43f, 57.04f, 88.65f, 48.82f, 84.53f,
            40.26f, 81.3f, 37.73f, 80.35f, 34.81f, 79.08f, 32.05f, 79.16f, 30.46f, 79.2f, 29.56f, 79.31f,
            28.61f, 80.72f, 28.3f, 82.16f, 32.09f, 84.88f, 32.93f, 85.64f, 36.53f, 88.9f, 40.34f, 91.94f,
            44.13f, 94.98f, 53.32f, 102.36f, 62.7f, 109.51f, 71.83f, 116.96f, 74.71f, 119.31f, 78.76f, 121.1f,
            80.84f, 124.4f, 84.01f, 129.41f, 79.97f, 132.1f, 76.49f, 135.19f, 74.06f, 137.35f, 71.56f, 139.43f,
            69.03f, 141.47f, 63.97f, 145.54f, 58.76f, 149.44f, 53.66f, 153.46f, 45.97f, 159.52f, 35.78f, 165.6f,
            29.75f, 173.44f, 29.56f, 173.69f, 28.32f, 174.85f, 28.61f, 175.28f, 29.56f, 176.69f, 30.46f, 176.8f,
            32.05f, 176.84f, 35.91f, 176.94f, 40.13f, 174.8f, 43.6f, 173.37f, 49.35f, 171f, 54.98f, 168.34f,
            60.62f, 165.74f, 65.57f, 163.46f, 70.53f, 161.15f, 75.6f, 159.14f, 78.15f, 158.13f, 84.64f, 154.89f,
            87.25f, 157.1f, 87.97f, 157.71f, 88.13f, 158.77f, 87.98f, 159.69f, 87.24f, 164.17f, 84.17f, 168.14f,
            82.1f, 172.06f, 80.86f, 174.41f, 79.6f, 176.75f, 78.37f, 179.1f, 77.66f, 180.46f, 76.59f, 182.03f,
            76.26f, 183.55f, 75.94f, 185.01f, 77.38f, 185.82f, 78.62f, 185.93f, 79.69f, 186.02f, 80.82f, 185.58f,
            81.8f, 185.21f, 83.44f, 184.6f, 85.02f, 183.83f, 86.59f, 183.05f, 90.63f, 181.03f, 94.56f, 178.8f,
            98.53f, 176.64f, 102.11f, 174.69f, 105.72f, 172.66f, 109.52f, 171.15f, 110.5f, 170.76f, 111.47f, 170.3f,
            112.51f, 170.12f, 113.97f, 169.88f, 114.67f, 170.8f, 115.41f, 171.97f, 116.27f, 173.32f, 116.35f, 175.15f,
            116.65f, 176.69f, 117.23f, 179.71f, 117.81f, 182.72f, 118.4f, 185.74f, 119.16f, 189.67f, 119.92f, 193.59f,
            120.67f, 197.52f, 121.46f, 201.59f, 122.24f, 205.66f, 123.03f, 209.73f, 123.7f, 213.18f, 124.36f, 216.63f,
            125.03f, 220.08f, 125.43f, 222.15f, 125.83f, 224.21f, 126.23f, 226.28f, 126.28f, 226.56f, 126.34f, 226.83f,
            126.39f, 227.11f, 126.74f, 228.12f, 127.29f, 228.84f, 128.01f, 229.31f, 128.72f, 228.84f, 129.28f, 228.12f,
            129.63f, 227.11f, 129.68f, 226.83f, 129.74f, 226.56f, 129.79f, 226.28f, 130.19f, 224.21f, 130.59f, 222.15f,
            130.99f, 220.08f, 131.66f, 216.63f, 132.32f, 213.18f, 132.99f, 209.73f, 133.78f, 205.66f, 134.56f, 201.59f,
            135.35f, 197.52f, 136.11f, 193.59f, 136.87f, 189.67f, 137.62f, 185.74f, 138.2f, 182.72f, 138.78f, 179.71f,
            139.37f, 176.69f, 139.67f, 175.15f, 139.75f, 173.32f, 140.61f, 171.97f, 141.35f, 170.8f, 142.05f, 169.88f,
            143.51f, 170.12f, 144.56f, 170.29f, 145.53f, 170.76f, 146.5f, 171.15f, 150.3f, 172.66f, 153.9f, 174.68f,
            157.49f, 176.64f, 161.46f, 178.8f, 165.39f, 181.03f, 169.43f, 183.05f, 170.99f, 183.83f, 172.58f, 184.6f,
            174.22f, 185.21f, 175.2f, 185.58f, 176.33f, 186.02f, 177.4f, 185.93f, 178.65f, 185.82f, 180.08f, 185.01f,
            179.76f, 183.55f, 179.43f, 182.03f, 178.35f, 180.46f, 177.65f, 179.1f, 176.43f, 176.74f, 175.16f, 174.41f,
            173.92f, 172.06f, 171.85f, 168.13f, 168.78f, 164.17f, 168.04f, 159.69f, 167.89f, 158.76f, 168.05f, 157.7f,
            168.77f, 157.1f, 171.38f, 154.89f, 177.86f, 158.13f, 180.42f, 159.14f, 185.49f, 161.15f, 190.45f, 163.46f,
            195.4f, 165.74f, 201.05f, 168.34f, 206.68f, 171f, 212.42f, 173.37f, 215.89f, 174.8f, 220.11f, 176.94f,
            223.97f, 176.84f, 225.56f, 176.8f, 226.46f, 176.69f, 227.41f, 175.28f, 227.7f, 174.85f, 226.46f, 173.69f,
            226.27f, 173.44f, 226.26f, 173.44f, 226.26f, 173.44f, 226.25f, 173.44f
        };

        static GraphicsPath Canvas(float scale, float ox, float oy)
        {
            var pts = new PointF[P.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(P[2 * i] * scale + ox, P[2 * i + 1] * scale + oy);
            var path = new GraphicsPath(FillMode.Winding);
            path.AddBeziers(pts);
            path.CloseFigure();
            return path;
        }

        // The star where the icon puts it on its tile.
        public static GraphicsPath OnTile(RectangleF tile) => Canvas(tile.Width / 256f, tile.X, tile.Y);

        // The star as large as fits in the box, centred in it. (Its own box on the 256 one: 28.57, 26.67, 198.89 x 202.64.)
        public static GraphicsPath Shape(RectangleF box)
        {
            const float x0 = 28.57f, y0 = 26.67f, w = 198.89f, h = 202.64f;
            float k = Math.Min(box.Width / w, box.Height / h);
            return Canvas(k, box.X + (box.Width - w * k) / 2 - x0 * k, box.Y + (box.Height - h * k) / 2 - y0 * k);
        }
    }
}
