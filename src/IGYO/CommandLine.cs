using System.Collections.Generic;
using System.Linq;

namespace IGYO
{
    // What IGYO was started with: switches ("--profile=…", "--restore-tabs" and the like) and links (addresses, or
    // page files, to open).
    //
    // Once IGYO is the default browser, Windows starts it as "IGYO.exe --single-argument <link>" for each link another
    // program opens (see BrowserRegistration). Everything after that switch is the one link, exactly as written, spaces
    // and quotation marks included: no part of it is ever read as a switch. Otherwise a link written to look like
    // switches (" --uninstall --remove-data", say) could make IGYO do something other than open it. Switches before
    // it are read as switches; only IGYO puts any there, when it starts itself again with the same link.
    static class CommandLine
    {
        public static string[] Switches { get; private set; } = new string[0];
        public static string[] Links { get; private set; } = new string[0];
        public static string HandedOver { get; private set; }   // the text after --single-argument; null when there was none

        public static void Read(string raw, string[] args)
        {
            var before = new List<string>();
            var link = AfterSingleArgument(raw, before);
            HandedOver = link;
            if (link != null)
            {
                Switches = before.ToArray();
                Links = link.Length > 0 ? new[] { link } : new string[0];
            }
            else
            {
                Switches = (args ?? new string[0]).Where(a => a.StartsWith("--")).ToArray();
                Links = (args ?? new string[0]).Where(a => !a.StartsWith("--")).ToArray();
            }
        }

        // For "<program> [--switch …] --single-argument <rest>": the rest as written (only the spaces around it, and
        // one pair of quotation marks around all of it, are taken off), with the switches before it added to "before".
        // Null when the command line has no such switch in that place ("before" is then left as it was).
        internal static string AfterSingleArgument(string raw, List<string> before)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var seen = new List<string>();
            int i = 0, n = raw.Length;
            // The program's own name: up to its closing quotation mark, or else up to the first space.
            if (raw[0] == '"')
            {
                int close = raw.IndexOf('"', 1);
                i = close < 0 ? n : close + 1;
            }
            else while (i < n && !IsSpace(raw[i])) i++;
            while (true)
            {
                while (i < n && IsSpace(raw[i])) i++;
                if (i >= n) return null;
                int end = i;
                while (end < n && !IsSpace(raw[end])) end++;
                var word = raw.Substring(i, end - i);
                if (word == BrowserRegistration.SingleArgument)
                {
                    var rest = raw.Substring(end).Trim(' ', '\t');
                    if (rest.Length >= 2 && rest[0] == '"' && rest[rest.Length - 1] == '"') rest = rest.Substring(1, rest.Length - 2).Trim(' ', '\t');
                    before?.AddRange(seen);
                    return rest;
                }
                if (!word.StartsWith("--") || word.IndexOf('"') >= 0) return null;
                seen.Add(word);
                i = end;
            }
        }

        static bool IsSpace(char c) => c == ' ' || c == '\t';

        // The command line for a copy of IGYO that starts in this one's place: these switches, and the same links
        // in the same form (a link Windows handed over stays after --single-argument).
        public static string Again(IEnumerable<string> switches)
        {
            if (HandedOver == null) return string.Join(" ", switches.Concat(Links).Select(BrowserEngine.Quoted));
            // Here the switches go in without quotation marks, so each must be one plain word (IGYO's own always are).
            var words = switches.Where(s => s.StartsWith("--") && s.All(c => !IsSpace(c) && c != '"')).ToList();
            words.Add(BrowserRegistration.SingleArgument);
            if (HandedOver.Length > 0) words.Add(HandedOver);
            return string.Join(" ", words);
        }
    }
}
