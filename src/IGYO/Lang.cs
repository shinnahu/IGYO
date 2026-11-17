using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace IGYO
{
    // The language of IGYO's own interface. The program is written in English; every other language is one
    // file, Assets\Lang\<id>.json, that maps each English text to its translation:
    //   { "name": "Español", "words": { "New tab": "Nueva pestaña", "Deleted \"{0}\".": "Se eliminó «{0}»." } }
    // A text is put into words with L.T("…") here and tr('…') in the pages' scripts. One that has no translation
    // is shown in English, so a missing line is never an empty label. (tools\lang.js checks the files against
    // the source.) Only IGYO's own menus and pages follow this: websites are not told anything by it.
    static class L
    {
        public const string Auto = "auto", English = "en";

        static Dictionary<string, string> words = new Dictionary<string, string>();

        public static string Id { get; private set; } = English;   // the language in use
        public static string Chosen { get; private set; } = Auto;  // what Settings says: "auto" (as Windows) or a language's id
        public static int Stamp { get; private set; }               // changes when the language does, so pages fetch their words again

        // Beside the program. (Worked out here, not through AppPaths: the uninstaller uses this too, and must not go near the data folder.)
        static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Lang");

        // A language file as it is on disk; null when there is none or it can't be read.
        static Dictionary<string, object> Read(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.All(c => char.IsLetterOrDigit(c) || c == '-') || id.Length > 12) return null;
            try
            {
                var file = Path.Combine(Dir, id + ".json");
                return File.Exists(file) ? Json.Obj(Json.Parse(File.ReadAllText(file))) : null;
            }
            catch { return null; }
        }

        // The languages IGYO comes with, each under its own name: English first, then one per file.
        public static List<KeyValuePair<string, string>> Available()
        {
            var list = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(English, "English") };
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var file in Directory.GetFiles(Dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        var name = Json.Str(Read(id), "name");
                        if (!string.IsNullOrWhiteSpace(name) && id != English) list.Add(new KeyValuePair<string, string>(id, name));
                    }
            }
            catch { }
            return list;
        }

        // Which of IGYO's languages Windows itself is shown in ("es-ES" is looked up as "es-ES", then "es").
        public static string FromWindows(CultureInfo culture = null)
        {
            var known = Available().Select(l => l.Key).ToList();
            for (var c = culture ?? CultureInfo.CurrentUICulture; c != null && !string.IsNullOrEmpty(c.Name); c = c.Parent)
            {
                var match = known.FirstOrDefault(id => string.Equals(id, c.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return English;
        }

        public static bool IsChoice(string chosen) => chosen == Auto || Available().Any(l => l.Key == chosen);

        public static void Use(string chosen)
        {
            Chosen = string.IsNullOrWhiteSpace(chosen) || !IsChoice(chosen) ? Auto : chosen;
            string id = Chosen == Auto ? FromWindows() : Chosen;
            var loaded = new Dictionary<string, string>();
            foreach (var pair in Json.Obj(Json.Get(Read(id), "words")) ?? new Dictionary<string, object>())
                if (pair.Value is string text && text.Length > 0) loaded[pair.Key] = text;
            words = loaded;
            Id = id;
            Stamp++;
        }

        // The text in the language in use, with {0}, {1}… replaced by what is given.
        public static string T(string text, params object[] args)
        {
            string s = text != null && words.TryGetValue(text, out var translated) ? translated : text ?? "";
            if (args == null) return s;
            for (int i = 0; i < args.Length; i++) s = s.Replace("{" + i.ToString(CultureInfo.InvariantCulture) + "}", Convert.ToString(args[i], CultureInfo.CurrentCulture) ?? "");
            return s;
        }

        // Marks a text for translation where it is only kept (in a table, say); L.T turns it into words where it is shown.
        public static string Key(string text) => text;

        // What the pages load as /lang.js: the words, and tr('…') to look them up.
        public static string Script() =>
            "(() => {\n" +
            "  const words = " + Json.Write(words) + ";\n" +
            "  const id = " + Json.Write(Id) + ";\n" +
            "  const has = (text) => Object.prototype.hasOwnProperty.call(words, text);\n" +
            "  const tr = (text, ...args) => {\n" +
            "    const s = has(text) ? words[text] : String(text == null ? '' : text);\n" +
            "    return args.length ? s.replace(/\\{(\\d+)\\}/g, (all, n) => (Number(n) < args.length ? String(args[n]) : all)) : s;\n" +
            "  };\n" +
            "  // Texts written into a page's own HTML: tooltips, labels, hints, and elements marked data-t.\n" +
            "  const translatePage = (root) => {\n" +
            "    const from = root || document;\n" +
            "    for (const name of ['title', 'aria-label', 'placeholder', 'alt'])\n" +
            "      for (const node of from.querySelectorAll('[' + name + ']')) { const value = node.getAttribute(name); if (has(value)) node.setAttribute(name, words[value]); }\n" +
            "    for (const node of from.querySelectorAll('[data-t]'))\n" +
            "      for (const child of node.childNodes) { if (child.nodeType !== 3) continue; const value = child.nodeValue.trim(); if (has(value)) child.nodeValue = words[value]; }\n" +
            "  };\n" +
            "  window.IGYO = window.IGYO || {};\n" +
            "  Object.assign(window.IGYO, { tr, translatePage, lang: id });\n" +
            "  document.documentElement.lang = id;\n" +
            "})();\n";
    }
}
