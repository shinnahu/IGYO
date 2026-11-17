using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IGYO
{
    // The fonts that come with IGYO: WOFF2 files in Assets\Shared\fonts, offered to the pages by Assets\Shared\fonts.css.
    // They are never installed in Windows; only IGYO's own pages load them. README.txt in that folder lists where each
    // file came from, and each family's license text is beside it.
    static class Fonts
    {
        public static string Dir => Path.Combine(InternalAssets.AssetsDir, "Shared", "fonts");
        public static string Stylesheet => Path.Combine(InternalAssets.AssetsDir, "Shared", "fonts.css");

        static readonly Regex FileRule = new Regex(@"url\(""fonts/([^""]+)""\)", RegexOptions.Compiled);
        static readonly Regex FamilyRule = new Regex(@"font-family:\s*""([^""]+)""", RegexOptions.Compiled);
        static readonly Regex OwnerRule = new Regex(@"^(.+?)-(latin-ext|latin|japanese)-", RegexOptions.Compiled);

        static string ReadStylesheet()
        {
            try { return File.ReadAllText(Stylesheet); } catch { return ""; }
        }

        // The file names the stylesheet asks for.
        public static List<string> DeclaredFiles() =>
            FileRule.Matches(ReadStylesheet()).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();

        // The family names the stylesheet offers to themes.
        public static List<string> DeclaredFamilies() =>
            FamilyRule.Matches(ReadStylesheet()).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();

        // A whole WOFF2 file starts with "wOF2" and is exactly as long as its own header says.
        public static bool IsWholeFontFile(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length < 48) return false;
                var head = new byte[12];
                using (var stream = info.OpenRead())
                    if (stream.Read(head, 0, 12) != 12) return false;
                long length = ((long)head[8] << 24) | ((long)head[9] << 16) | ((long)head[10] << 8) | head[11];
                return head[0] == (byte)'w' && head[1] == (byte)'O' && head[2] == (byte)'F' && head[3] == (byte)'2' && length == info.Length;
            }
            catch { return false; }
        }

        // "saira-latin-wght-normal.woff2" belongs to the family whose license is LICENSE-saira.txt.
        public static string LicenseFileFor(string fontFile)
        {
            var m = OwnerRule.Match(fontFile ?? "");
            return m.Success ? "LICENSE-" + m.Groups[1].Value + ".txt" : null;
        }
    }
}
