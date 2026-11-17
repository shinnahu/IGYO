using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IGYO
{
    // The languages a selection can be translated between, by the names the interface can put into words
    // (it asks the engine for a language's name in the language IGYO is shown in).
    static class TranslateLang
    {
        public static readonly string[] All =
        {
            "af", "ar", "bg", "bn", "ca", "cs", "cy", "da", "de", "el", "en", "es", "et", "eu", "fa", "fi", "fr", "ga", "gl", "he",
            "hi", "hr", "hu", "id", "is", "it", "ja", "ko", "lt", "lv", "ms", "nb", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv",
            "sw", "ta", "te", "th", "tr", "uk", "ur", "vi", "zh-Hans", "zh-Hant",
        };

        // "" stands for "not chosen" wherever a language is kept.
        public static bool IsKnown(string id) => string.IsNullOrEmpty(id) || Array.IndexOf(All, id) >= 0;

        // A language as the translation service names it, and back.
        public static string ToService(string id) => id == "zh-Hans" ? "zh-CN" : id == "zh-Hant" ? "zh-TW" : id;

        public static string FromService(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "";
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return name.IndexOf("TW", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("HK", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ? "zh-Hant" : "zh-Hans";
            var first = name.Split('-', '_')[0].ToLowerInvariant();
            if (first == "no") first = "nb";
            if (first == "iw") first = "he";
            return Array.IndexOf(All, first) >= 0 ? first : name;
        }

        // What the writing itself says about the language, when it says anything: Japanese has letters no other
        // language uses, and so have Korean, Thai, Greek, Hebrew and Hindi. Text in the Latin, Cyrillic or Arabic
        // alphabets, and text in Chinese characters alone, could be several languages, and is left to the service
        // ("" is returned), except that Chinese characters alone count as Japanese for someone who reads Japanese
        // and not Chinese (japaneseReader).
        public static string FromWriting(string text, bool japaneseReader = false)
        {
            int kana = 0, han = 0, hangul = 0, thai = 0, greek = 0, hebrew = 0, devanagari = 0, letters = 0;
            foreach (char c in text ?? "")
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0xFF66 && c <= 0xFF9D)) kana++;
                else if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF)) han++;
                else if ((c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F)) hangul++;
                else if (c >= 0x0E00 && c <= 0x0E7F) thai++;
                else if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF)) greek++;
                else if (c >= 0x0590 && c <= 0x05FF) hebrew++;
                else if (c >= 0x0900 && c <= 0x097F) devanagari++;
            }
            if (letters == 0) return "";
            if (kana > 0 && (kana + han) * 4 >= letters) return "ja";
            if (hangul * 2 >= letters) return "ko";
            if (thai * 2 >= letters) return "th";
            if (greek * 2 >= letters) return "el";
            if (hebrew * 2 >= letters) return "he";
            if (devanagari * 2 >= letters) return "hi";
            if (japaneseReader && han * 2 >= letters) return "ja";
            return "";
        }
    }

    sealed class Translation
    {
        public string Text = "";      // the translation
        public string From = "";      // the language it was translated from (found out, or given)
        public string To = "";        // the language it was put into (the second language, when the text was already in the first)
        public bool Detected;         // From was found out, not given
        public bool Same;             // the text is already in the wanted language, and there was no second one to put it into
        public bool Partial;          // the selection was too long: only its beginning was translated
        public string Error;          // null, or what went wrong: "quota", "network" or "service"
    }

    // Translates a piece of selected text with MyMemory (mymemory.translated.net), a public translation service that
    // needs no account. The text is sent there, in pieces of at most 500 bytes (the service's limit for one
    // request), without cookies and without anything that says who is asking. The service allows 5,000 characters
    // a day from one network address; it says so itself when that is used up.
    static class Translator
    {
        public const string ServiceName = "MyMemory";
        const int PieceBytes = 450, MostBytes = 6000;

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(InstallInfo.AppName);
            return client;
        }

        static string Endpoint
        {
            get
            {
#if DEBUG
                var test = Environment.GetEnvironmentVariable("NSB_TRANSLATE_URL");   // test builds: a local stand-in for the service
                if (!string.IsNullOrEmpty(test)) return test;
#endif
                return "https://api.mymemory.translated.net/get";
            }
        }

        // from: a language, or "" to find it out. to: the wanted language. second: where text already in the wanted
        // language goes ("" for nowhere).
        public static async Task<Translation> TranslateAsync(string text, string from, string to, string second, bool japaneseReader, CancellationToken cancel)
        {
            var result = new Translation { To = to };
            try
            {
                text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                var pieces = Split(text, out bool partial);
                result.Partial = partial;
                if (pieces.Count == 0) { result.Error = "service"; return result; }

                string source = string.IsNullOrEmpty(from) ? TranslateLang.FromWriting(text, japaneseReader) : from;
                result.Detected = string.IsNullOrEmpty(from);
                if (source == to)
                {
                    // Already in the wanted language (the writing says so, or it was given as such).
                    if (string.IsNullOrEmpty(second) || second == to) { result.Same = true; result.From = source; return result; }
                    to = result.To = second;
                }

                var first = await AskAsync(pieces[0], source, to, cancel).ConfigureAwait(false);
                if (first.Same)
                {
                    // The service found the text to be in the wanted language itself.
                    if (string.IsNullOrEmpty(second) || second == to) { result.Same = true; result.From = to; return result; }
                    source = to;
                    to = result.To = second;
                    first = await AskAsync(pieces[0], source, to, cancel).ConfigureAwait(false);
                    if (first.Same) { result.Same = true; result.From = source; return result; }
                }
                if (first.Error != null) { result.Error = first.Error; return result; }
                if (string.IsNullOrEmpty(source)) source = TranslateLang.FromService(first.Detected);
                result.From = source;

                // The rest, a few at a time, in the language the first piece turned out to be in.
                var texts = new string[pieces.Count];
                texts[0] = first.Text;
                for (int i = 1; i < pieces.Count; i += 3)
                {
                    var batch = Enumerable.Range(i, Math.Min(3, pieces.Count - i)).ToList();
                    var answers = await Task.WhenAll(batch.Select(n => string.IsNullOrWhiteSpace(pieces[n])
                        ? Task.FromResult(new Answer { Text = pieces[n] })
                        : AskAsync(pieces[n], source, to, cancel))).ConfigureAwait(false);
                    for (int k = 0; k < batch.Count; k++)
                    {
                        if (answers[k].Error != null) { result.Error = answers[k].Error; return result; }
                        texts[batch[k]] = answers[k].Same ? pieces[batch[k]] : answers[k].Text;
                    }
                }
                // Pieces cut from text written without spaces (Japanese, Chinese) come back as sentences that need one
                // between them, unless the language they were put into is written without spaces too.
                bool spaced = to != "ja" && to != "zh-Hans" && to != "zh-Hant" && to != "th";
                var joined = new StringBuilder();
                for (int i = 0; i < texts.Length; i++)
                {
                    string piece = texts[i] ?? "";
                    if (spaced && joined.Length > 0 && piece.Length > 0 && !char.IsWhiteSpace(joined[joined.Length - 1]) && !char.IsWhiteSpace(piece[0])) joined.Append(' ');
                    joined.Append(piece);
                }
                result.Text = joined.ToString().Trim();
                return result;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception) { result.Error = "network"; return result; }
        }

        sealed class Answer
        {
            public string Text = "", Detected = "", Error;
            public bool Same;
        }

        static async Task<Answer> AskAsync(string piece, string from, string to, CancellationToken cancel)
        {
            // Line breaks and spaces around a piece are kept as they are; only what is between them is sent.
            string lead = piece.Substring(0, piece.Length - piece.TrimStart().Length);
            string tail = piece.Substring(piece.TrimEnd().Length);
            string core = piece.Trim();
            if (core.Length == 0) return new Answer { Text = piece };
            string pair = (string.IsNullOrEmpty(from) ? "Autodetect" : TranslateLang.ToService(from)) + "|" + TranslateLang.ToService(to);
            string url = Endpoint + "?q=" + Uri.EscapeDataString(core) + "&langpair=" + Uri.EscapeDataString(pair);
            string body;
            using (var response = await http.GetAsync(url, cancel).ConfigureAwait(false))
            {
                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if ((int)response.StatusCode == 429) return new Answer { Error = "quota" };
                if (!response.IsSuccessStatusCode && body.TrimStart().StartsWith("{") == false) return new Answer { Error = "service" };
            }
            var answer = Parse(body);
            if (answer.Error == null && !answer.Same) answer.Text = lead + answer.Text + tail;
            return answer;
        }

        // { "responseData": { "translatedText": "…", "detectedLanguage": "ja" }, "quotaFinished": false,
        //   "responseStatus": 200, "responseDetails": "" }   (the status is sometimes a number, sometimes a text)
        static Answer Parse(string json)
        {
            var answer = new Answer();
            Dictionary<string, object> root;
            try { root = Json.Obj(Json.Parse(json)); } catch { root = null; }
            if (root == null) { answer.Error = "service"; return answer; }
            string status = Convert.ToString(Json.Get(root, "responseStatus") ?? "", System.Globalization.CultureInfo.InvariantCulture);
            string details = Json.Str(root, "responseDetails") ?? "";
            var data = Json.Obj(Json.Get(root, "responseData"));
            string text = Json.Str(data, "translatedText") ?? "";
            bool quota = Json.Get(root, "quotaFinished") is bool finished && finished;
            if (details.IndexOf("DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("TWO DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                answer.Same = true;
                return answer;
            }
            if (status == "429" || text.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) || (quota && text.Length == 0))
            {
                answer.Error = "quota";
                return answer;
            }
            if (status != "200" || text.Length == 0) { answer.Error = "service"; return answer; }
            answer.Text = WebUtility.HtmlDecode(text);
            answer.Detected = Json.Get(data, "detectedLanguage") as string ?? "";
            return answer;
        }

        // The text in pieces the service takes (each at most PieceBytes bytes as UTF-8), cut where a sentence ends
        // when there is such a place, and joined again they give the text back. A very long selection is cut off.
        public static List<string> Split(string text, out bool partial)
        {
            partial = false;
            var pieces = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return pieces;
            if (Encoding.UTF8.GetByteCount(text) > MostBytes)
            {
                partial = true;
                text = Cut(text, MostBytes, out _);
            }
            while (text.Length > 0)
            {
                if (Encoding.UTF8.GetByteCount(text) <= PieceBytes) { pieces.Add(text); break; }
                string head = Cut(text, PieceBytes, out int used);
                pieces.Add(head);
                text = text.Substring(used);
            }
            return pieces;
        }

        // The longest beginning of the text that fits in the bytes, ending after a sentence if one ends in the
        // second half of it, else after a comma or a space, else wherever it has to.
        static string Cut(string text, int bytes, out int used)
        {
            int fit = 0, count = 0;
            while (fit < text.Length)
            {
                int step = char.IsHighSurrogate(text[fit]) && fit + 1 < text.Length ? 2 : 1;
                int size = Encoding.UTF8.GetByteCount(text.ToCharArray(fit, step));
                if (count + size > bytes) break;
                count += size;
                fit += step;
            }
            if (fit >= text.Length) { used = text.Length; return text; }
            int at = -1;
            for (int i = fit - 1; i >= fit / 2 && at < 0; i--) if (text[i] == '\n' || "。．！？!?…".IndexOf(text[i]) >= 0 || (text[i] == '.' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))) at = i + 1;
            for (int i = fit - 1; i >= fit / 2 && at < 0; i--) if ("、，,;；:：".IndexOf(text[i]) >= 0 || char.IsWhiteSpace(text[i])) at = i + 1;
            if (at <= 0) at = fit;
            // spaces after the cut stay with the piece before it, so that no piece starts with them
            while (at < text.Length && at < fit && char.IsWhiteSpace(text[at])) at++;
            used = at;
            return text.Substring(0, at);
        }

        // A language as Google Translate's website names it.
        public static string SiteLanguage(string id) => id == "zh-Hans" ? "zh-CN" : id == "zh-Hant" ? "zh-TW" : id == "nb" ? "no" : id;

        // Where the whole selection can be read in translation on a website instead (Google Translate).
        public static string SiteUrl(string text, string from, string to)
        {
            if (text.Length > 4000) text = text.Substring(0, char.IsHighSurrogate(text[3999]) ? 3999 : 4000);
            return "https://translate.google.com/?sl=" + (string.IsNullOrEmpty(from) ? "auto" : Uri.EscapeDataString(SiteLanguage(from))) +
                "&tl=" + Uri.EscapeDataString(SiteLanguage(to)) + "&text=" + Uri.EscapeDataString(text) + "&op=translate";
        }
    }

    // Translating a whole page. The engine cannot do that by itself in an app like this one, and the service used
    // for selections allows far too little text for pages, so the page is opened through Google Translate's
    // website: Google is given the page's address, fetches the page itself and shows it translated, under an
    // address of its own (the site's name with dashes, ending in ".translate.goog"). So it only works for pages
    // Google can reach: not for pages on this PC or network, and without the sign-ins the site has in IGYO.
    // "Show the original page" goes back to the page's own address, which is worked out from Google's.
    static class PageTranslation
    {
        const string Suffix = ".translate.goog";

        // Whether Google could fetch this page at all.
        public static bool CanTranslate(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
            if (u.HostNameType != UriHostNameType.Dns || u.Host.IndexOf('.') < 0 || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;   // this PC, a PC by its bare name or number
            return !IsTranslated(url);
        }

        // The address that shows the page translated into the language (hl: the language Google's own bar is shown in).
        public static string Address(string url, string to, string hl) =>
            "https://translate.google.com/translate?sl=auto&tl=" + Uri.EscapeDataString(Translator.SiteLanguage(to)) +
            "&hl=" + Uri.EscapeDataString(Translator.SiteLanguage(hl)) + "&u=" + Uri.EscapeDataString(url);

        public static bool IsTranslated(string url) => Original(url) != null;

        // The page's own address, from the address Google shows it under; null when this is not such an address.
        //   https://translate.google.com/translate?…&u=<the address>          (on the way there)
        //   https://en-m-wikipedia-org.translate.goog/wiki/X?_x_tr_sl=…       (there: dots are dashes, a dash is two;
        //                                                                      "_x_tr_sch=http" for a page not on https)
        public static string Original(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return null;
            var query = new List<KeyValuePair<string, string>>();
            foreach (var part in u.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                query.Add(new KeyValuePair<string, string>(eq < 0 ? part : part.Substring(0, eq), eq < 0 ? null : part.Substring(eq + 1)));
            }
            if (u.Host.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.TrimEnd('/') == "/translate")
            {
                var given = query.FirstOrDefault(p => p.Key == "u").Value;
                if (string.IsNullOrEmpty(given)) return null;
                given = Uri.UnescapeDataString(given.Replace("+", " "));
                return Uri.TryCreate(given, UriKind.Absolute, out var own) && (own.Scheme == "http" || own.Scheme == "https") ? given : null;
            }
            if (!u.Host.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) || u.Host.Length == Suffix.Length) return null;
            string name = u.Host.Substring(0, u.Host.Length - Suffix.Length);
            var host = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] != '-') host.Append(name[i]);
                else if (i + 1 < name.Length && name[i + 1] == '-') { host.Append('-'); i++; }
                else host.Append('.');
            }
            string scheme = query.Any(p => p.Key == "_x_tr_sch" && p.Value == "http") ? "http" : "https";
            var rest = query.Where(p => !p.Key.StartsWith("_x_tr_")).Select(p => p.Value == null ? p.Key : p.Key + "=" + p.Value).ToList();
            return scheme + "://" + host + u.AbsolutePath + (rest.Count > 0 ? "?" + string.Join("&", rest) : "") + u.Fragment;
        }
    }
}
